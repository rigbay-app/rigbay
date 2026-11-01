using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>
/// The host's metrics bus: latest-value cache plus synchronous delivery. A handler that throws is logged and skipped;
/// the other subscribers still get the sample. Module access goes through <see cref="ForModule"/>, which stamps the
/// source and runs module handlers in the module's fault boundary.
/// <para>Besides samples the bus knows three more things: which ids are <i>withdrawn</i> (<see cref="Withdraw(string, MetricId)"/>), the
/// <see cref="MetricDescriptor"/> each module registered (<see cref="Describe"/>, <see cref="TryGetDescriptor"/>), and how
/// many subscribers each id has (<see cref="SubscriberCount(MetricId)"/>, <see cref="SubscribersChanged"/>).</para>
/// </summary>
public sealed class MetricsBus
{
    private readonly object _gate = new();
    private readonly Dictionary<MetricId, MetricSample> _latest = new();
    private readonly HashSet<MetricId> _withdrawn = new();
    private readonly Dictionary<MetricId, (string Owner, MetricDescriptor Descriptor)> _descriptors = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _clock;

    public MetricsBus(ILog log, Func<DateTimeOffset>? clock = null)
    {
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Raised after any subscription was added or disposed (host or module); the argument is the id the subscription was
    /// filtered to, or null for an all-samples subscription (it concerns every id). Synchronous on the thread that
    /// subscribed or disposed, with no lock held; a handler that throws is logged and skipped.
    /// </summary>
    public event Action<MetricId?>? SubscribersChanged;

    /// <summary>Same as <see cref="SubscribersChanged"/> with the module that owns the subscription (null for the host). Modules use it to ignore their own.</summary>
    internal event Action<MetricId?, string?>? SubscriptionChanged;

    /// <summary>Publishes as <paramref name="source"/>. NaN and infinite values are dropped and logged.</summary>
    public void Publish(string source, MetricId id, double value, MetricUnit unit)
    {
        if (id.Value.Length == 0) { _log.Warn($"metric from {source}: empty id dropped"); return; }
        if (!double.IsFinite(value)) { _log.Warn($"metric {id} from {source}: non-finite value dropped"); return; }
        Publish(new MetricSample(id, value, unit, _clock(), source));
    }

    /// <summary>
    /// Publishes a complete sample (host and tests). A sample with <see cref="MetricSample.IsAvailable"/> false withdraws
    /// the id like <see cref="Withdraw(string, MetricId)"/> does, whoever the source is.
    /// </summary>
    public void Publish(MetricSample sample)
    {
        if (!sample.IsAvailable)
        {
            WithdrawCore(sample.Id, null, sample.Unit, sample.SourceModule);
            return;
        }
        Subscription[] targets;
        lock (_gate)
        {
            _latest[sample.Id] = sample;
            _withdrawn.Remove(sample.Id);
            targets = Targets(sample.Id);
        }
        Deliver(targets, sample);
    }

    /// <summary>
    /// Marks an id unavailable on behalf of <paramref name="source"/>. Only the module whose sample is the current one
    /// can withdraw it (a newer sample from another module is kept). Subscribers get one sample with
    /// <see cref="MetricSample.IsAvailable"/> false and a NaN value. Returns true when the id was withdrawn now.
    /// </summary>
    public bool Withdraw(string source, MetricId id) => WithdrawCore(id, source, null, null);

    /// <summary>Withdraws an id whoever published it (host code, tests).</summary>
    public bool Withdraw(MetricId id) => WithdrawCore(id, null, null, null);

    private bool WithdrawCore(MetricId id, string? onlyIfSource, MetricUnit? unit, string? stampSource)
    {
        Subscription[] targets;
        MetricSample notice;
        lock (_gate)
        {
            if (!_latest.TryGetValue(id, out var current)) return false; // never published, or withdrawn already
            if (onlyIfSource is not null && !string.Equals(current.SourceModule, onlyIfSource, StringComparison.Ordinal)) return false;
            _latest.Remove(id);
            _withdrawn.Add(id);
            notice = new MetricSample(id, double.NaN, unit ?? current.Unit, _clock(), stampSource ?? current.SourceModule) { IsAvailable = false };
            targets = Targets(id);
        }
        Deliver(targets, notice);
        return true;
    }

    /// <summary>Withdraws every id whose current sample came from <paramref name="source"/> (not called automatically; for host code that wants stale readings gone).</summary>
    public void WithdrawAll(string source)
    {
        MetricId[] ids;
        lock (_gate) ids = _latest.Values.Where(s => string.Equals(s.SourceModule, source, StringComparison.Ordinal)).Select(s => s.Id).ToArray();
        foreach (var id in ids) Withdraw(source, id);
    }

    /// <summary>True when the id had a sample that was withdrawn and nothing was published since.</summary>
    public bool IsWithdrawn(MetricId id)
    {
        lock (_gate) return _withdrawn.Contains(id);
    }

    private Subscription[] Targets(MetricId id) => _subscriptions.Where(s => s.Filter is null || s.Filter.Value == id).ToArray();

    private void Deliver(Subscription[] targets, MetricSample sample)
    {
        foreach (var s in targets)
        {
            try { s.Handler(sample); }
            catch (Exception ex) { _log.Warn($"metric subscriber failed for {sample.Id}", ex); }
        }
    }

    /// <summary>For host code (UI, recorder host): not tied to a module.</summary>
    public IDisposable Subscribe(Action<MetricSample> handler, MetricId? filter = null) => Subscribe(handler, filter, owner: null);

    private Subscription Subscribe(Action<MetricSample> handler, MetricId? filter, string? owner)
    {
        var sub = new Subscription(this, handler, filter, owner);
        lock (_gate) _subscriptions.Add(sub);
        RaiseSubscribersChanged(filter, owner);
        return sub;
    }

    /// <summary>The latest sample of an id. False when none was published or the id is withdrawn.</summary>
    public bool TryGetLatest(MetricId id, out MetricSample sample)
    {
        lock (_gate) return _latest.TryGetValue(id, out sample!);
    }

    /// <summary>The latest sample of an id, or null when none was published or the id is withdrawn (the UI shows "not available").</summary>
    public MetricSample? Latest(MetricId id) => TryGetLatest(id, out var sample) ? sample : null;

    /// <summary>Same as <see cref="Latest(MetricId)"/>; null for text that is not a valid id.</summary>
    public MetricSample? Latest(string id) => MetricId.IsValid(id) ? Latest(new MetricId(id)) : null;

    /// <summary>The latest sample of every available id, ordered by id.</summary>
    public IReadOnlyList<MetricSample> GetLatest()
    {
        lock (_gate) return _latest.Values.OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToArray();
    }

    // ---------------------------------------------------------------- subscribers

    /// <summary>How many subscriptions receive samples of <paramref name="id"/>: those filtered to it and the all-samples ones.</summary>
    public int SubscriberCount(MetricId id) => SubscriberCount(id, excludeOwner: null);

    /// <summary>Same as <see cref="SubscriberCount(MetricId)"/>; 0 for text that is not a valid id.</summary>
    public int SubscriberCount(string id) => MetricId.IsValid(id) ? SubscriberCount(new MetricId(id), excludeOwner: null) : 0;

    internal int SubscriberCount(MetricId id, string? excludeOwner)
    {
        lock (_gate)
            return _subscriptions.Count(s => (s.Filter is null || s.Filter.Value == id)
                && (excludeOwner is null || !string.Equals(s.Owner, excludeOwner, StringComparison.Ordinal)));
    }

    private void RaiseSubscribersChanged(MetricId? filter, string? owner)
    {
        try { SubscribersChanged?.Invoke(filter); }
        catch (Exception ex) { _log.Warn("a SubscribersChanged handler threw: " + ex.Message); }
        try { SubscriptionChanged?.Invoke(filter, owner); }
        catch (Exception ex) { _log.Warn("a SubscribersChanged handler threw: " + ex.Message); }
    }

    // ---------------------------------------------------------------- descriptors

    /// <summary>
    /// Registers what a module says about a metric. The same module again replaces its entry; if another module described
    /// the id first, that entry is kept. A descriptor without an id or label is dropped and logged.
    /// </summary>
    public void Describe(string owner, MetricDescriptor descriptor)
    {
        if (descriptor is null || descriptor.Id.Value.Length == 0 || string.IsNullOrWhiteSpace(descriptor.LabelKey))
        {
            _log.Warn($"metric descriptor from {owner} dropped: it needs an id and a label key");
            return;
        }
        lock (_gate)
        {
            if (_descriptors.TryGetValue(descriptor.Id, out var existing) && !string.Equals(existing.Owner, owner, StringComparison.Ordinal))
            {
                _log.Debug($"metric {descriptor.Id}: described by {existing.Owner} already, the description from {owner} is ignored");
                return;
            }
            _descriptors[descriptor.Id] = (owner, descriptor);
        }
    }

    /// <summary>The description of an id and the id of the module that registered it.</summary>
    public bool TryGetDescriptor(MetricId id, out MetricDescriptor descriptor, out string owner)
    {
        lock (_gate)
        {
            if (_descriptors.TryGetValue(id, out var entry)) { descriptor = entry.Descriptor; owner = entry.Owner; return true; }
        }
        descriptor = null!;
        owner = "";
        return false;
    }

    /// <summary>All descriptors with their owners, ordered by id.</summary>
    public IReadOnlyList<(string Owner, MetricDescriptor Descriptor)> GetDescriptors()
    {
        lock (_gate) return _descriptors.Values.OrderBy(d => d.Descriptor.Id.Value, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The <see cref="IMetricsBus"/> of one module. <paramref name="guard"/> wraps each module handler.</summary>
    internal IMetricsBus ForModule(string moduleId, Func<Action<MetricSample>, Action<MetricSample>> guard, Func<Action, Action> guardAction, Action<IDisposable> track)
        => new ModuleBus(this, moduleId, guard, guardAction, track);

    private sealed class Subscription(MetricsBus bus, Action<MetricSample> handler, MetricId? filter, string? owner) : IDisposable
    {
        private int _disposed;

        public Action<MetricSample> Handler { get; } = handler;
        public MetricId? Filter { get; } = filter;
        public string? Owner { get; } = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            bool removed;
            lock (bus._gate) removed = bus._subscriptions.Remove(this);
            if (removed) bus.RaiseSubscribersChanged(Filter, Owner);
        }
    }

    private sealed class ModuleBus(MetricsBus bus, string moduleId, Func<Action<MetricSample>, Action<MetricSample>> guard,
        Func<Action, Action> guardAction, Action<IDisposable> track) : IMetricsBus
    {
        private readonly object _eventGate = new();
        private readonly List<(Action Original, Action<MetricId?, string?> Hook)> _hooks = new();

        public void Publish(MetricId id, double value, MetricUnit unit) => bus.Publish(moduleId, id, value, unit);

        public IDisposable Subscribe(Action<MetricSample> handler)
        {
            var sub = bus.Subscribe(guard(handler), null, moduleId);
            track(sub);
            return sub;
        }

        public IDisposable Subscribe(MetricId id, Action<MetricSample> handler)
        {
            var sub = bus.Subscribe(guard(handler), id, moduleId);
            track(sub);
            return sub;
        }

        public bool TryGetLatest(MetricId id, out MetricSample sample) => bus.TryGetLatest(id, out sample);

        public IReadOnlyList<MetricSample> GetLatest() => bus.GetLatest();

        public MetricSample? Latest(MetricId id) => bus.Latest(id);

        public void Withdraw(MetricId id) => bus.Withdraw(moduleId, id);

        public void Describe(MetricDescriptor descriptor) => bus.Describe(moduleId, descriptor);

        public int SubscriberCount(MetricId id) => bus.SubscriberCount(id, excludeOwner: moduleId);

        public event Action? SubscribersChanged
        {
            add
            {
                if (value is null) return;
                var guarded = guardAction(value);
                Action<MetricId?, string?> hook = (_, owner) =>
                {
                    if (!string.Equals(owner, moduleId, StringComparison.Ordinal)) guarded();
                };
                lock (_eventGate) _hooks.Add((value, hook));
                bus.SubscriptionChanged += hook;
                // the host removes the hook when the module stops, so a stopped module is never called
                track(new Remover(() => Remove(value)));
            }
            remove { if (value is not null) Remove(value); }
        }

        private void Remove(Action original)
        {
            Action<MetricId?, string?>? hook = null;
            lock (_eventGate)
            {
                var index = _hooks.FindIndex(h => h.Original == original);
                if (index < 0) return;
                hook = _hooks[index].Hook;
                _hooks.RemoveAt(index);
            }
            bus.SubscriptionChanged -= hook;
        }

        private sealed class Remover(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }
}
