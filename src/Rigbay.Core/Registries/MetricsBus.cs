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
    private readonly Dictionary<MetricId, (string Source, MetricUnit Unit)> _known = new();
    private long _catalogVersion;
    private readonly List<Subscription> _subscriptions = new();
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _clock;

    public MetricsBus(ILog log, Func<DateTimeOffset>? clock = null, MetricHistory? history = null)
    {
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        History = history ?? new MetricHistory(_clock);
    }

    /// <summary>
    /// What the bus remembers of every metric (a bounded ring per id, see <see cref="MetricHistory"/>): fed by <see cref="Publish(MetricSample)"/>,
    /// cleared by a withdraw. It is not a subscriber: it does not count in <see cref="SubscriberCount(MetricId)"/>, so it never keeps a module polling.
    /// </summary>
    public MetricHistory History { get; }

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
        bool catalogChanged;
        lock (_gate)
        {
            _latest[sample.Id] = sample;
            catalogChanged = _withdrawn.Remove(sample.Id);
            if (!_known.TryGetValue(sample.Id, out var known) || known.Source != sample.SourceModule || known.Unit != sample.Unit)
            {
                _known[sample.Id] = (sample.SourceModule, sample.Unit);
                catalogChanged = true;
            }
            if (catalogChanged) _catalogVersion++;
            targets = Targets(sample.Id);
        }
        History.Record(sample);
        if (catalogChanged) RaiseCatalogChanged();
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
            _catalogVersion++;
            targets = Targets(id);
        }
        History.Clear(id);   // what was drawn is no longer true
        RaiseCatalogChanged();
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

    private Subscription Subscribe(Action<MetricSample> handler, MetricId? filter, string? owner, TimeSpan? background = null)
    {
        var sub = new Subscription(this, handler, filter, owner, background);
        lock (_gate) _subscriptions.Add(sub);
        RaiseSubscribersChanged(filter, owner);
        return sub;
    }

    /// <summary>The latest sample of an id. False when none was published or the id is withdrawn.</summary>
    public bool TryGetLatest(MetricId id, out MetricSample sample)
    {
        lock (_gate) return _latest.TryGetValue(id, out sample!);
    }

    /// <summary>The unit of an id: from its descriptor, else from the last sample that was published, else <see cref="MetricUnit.None"/>. Cheap; no text is resolved.</summary>
    public MetricUnit UnitOf(MetricId id)
    {
        lock (_gate)
        {
            if (_descriptors.TryGetValue(id, out var d) && d.Descriptor.Unit != MetricUnit.None) return d.Descriptor.Unit;
            return _known.TryGetValue(id, out var k) ? k.Unit : MetricUnit.None;
        }
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
            return _subscriptions.Count(s => s.Background is null && (s.Filter is null || s.Filter.Value == id)
                && (excludeOwner is null || !string.Equals(s.Owner, excludeOwner, StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- background demand (host API 0.1.4)

    /// <summary>The shortest interval anybody asks for <paramref name="id"/> in the background (see <see cref="IMetricsBus.SubscribeBackground"/>), or null.</summary>
    public TimeSpan? BackgroundInterval(MetricId id) => BackgroundInterval(id, excludeOwner: null);

    internal TimeSpan? BackgroundInterval(MetricId id, string? excludeOwner)
    {
        lock (_gate)
        {
            TimeSpan? best = null;
            foreach (var s in _subscriptions)
            {
                if (s.Background is not { } b || s.Filter != id) continue;
                if (excludeOwner is not null && string.Equals(s.Owner, excludeOwner, StringComparison.Ordinal)) continue;
                if (best is null || b < best) best = b;
            }
            return best;
        }
    }

    /// <summary>Shortest and longest background interval; asked values outside are moved inside.</summary>
    public static readonly TimeSpan MinBackgroundInterval = TimeSpan.FromSeconds(5), MaxBackgroundInterval = TimeSpan.FromHours(1);

    /// <summary>Most background subscriptions one module may hold.</summary>
    public const int MaxBackgroundPerModule = 64;

    /// <summary>For host code and tests: a background subscription that belongs to no module.</summary>
    public IDisposable SubscribeBackground(MetricId id, TimeSpan interval, Action<MetricSample> handler) =>
        Subscribe(handler, id, owner: null, Clamp(interval));

    private static TimeSpan Clamp(TimeSpan interval) => interval < MinBackgroundInterval ? MinBackgroundInterval : interval > MaxBackgroundInterval ? MaxBackgroundInterval : interval;

    private IDisposable SubscribeBackgroundFor(string moduleId, MetricId id, TimeSpan interval, Action<MetricSample> guarded)
    {
        if (id.Value.Length == 0) throw new ArgumentException("a background demand must name a metric id", nameof(id));
        var clamped = Clamp(interval);
        if (clamped != interval) _log.Debug($"{moduleId}: background interval {interval.TotalSeconds:0.#} s for {id} moved to {clamped.TotalSeconds:0.#} s");
        lock (_gate)
        {
            if (_subscriptions.Count(s => s.Background is not null && string.Equals(s.Owner, moduleId, StringComparison.Ordinal)) >= MaxBackgroundPerModule)
                throw new InvalidOperationException($"a module may hold at most {MaxBackgroundPerModule} background subscriptions");
        }
        return Subscribe(guarded, id, moduleId, clamped);
    }

    /// <summary>Every background demand: metric, interval and the module that asked (null for the host), ordered by id.</summary>
    public IReadOnlyList<(MetricId Id, TimeSpan Interval, string? Owner)> GetBackgroundDemands()
    {
        lock (_gate)
            return _subscriptions.Where(s => s.Background is not null).Select(s => (s.Filter!.Value, s.Background!.Value, s.Owner))
                .OrderBy(d => d.Item1.Value, StringComparer.Ordinal).ThenBy(d => d.Item2).ToArray();
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
            if (_descriptors.TryGetValue(descriptor.Id, out var previous) && previous.Descriptor == descriptor) return; // nothing new
            _descriptors[descriptor.Id] = (owner, descriptor);
            _catalogVersion++;
        }
        RaiseCatalogChanged();
    }

    // ---------------------------------------------------------------- catalogue (what other modules may read)

    /// <summary>Raised after the catalogue changed (see <see cref="IMetricsBus.CatalogChanged"/>); synchronous, no lock held, a handler that throws is logged and skipped.</summary>
    public event Action? CatalogChanged;

    /// <summary>Grows with every change that raises <see cref="CatalogChanged"/>.</summary>
    public long CatalogVersion { get { lock (_gate) return _catalogVersion; } }

    /// <summary>
    /// How the bus turns a language key into text: <c>(ownerModuleId or null for the host, key)</c> returns the text in the
    /// current language, or null when there is none. Set by the host; without it labels fall back to the raw id.
    /// </summary>
    public Func<string?, string, string?>? TextResolver { get; set; }

    /// <summary>Tells readers the catalogue changed without a bus change (the host calls it when the UI language changes).</summary>
    public void NotifyCatalogChanged()
    {
        lock (_gate) _catalogVersion++;
        RaiseCatalogChanged();
    }

    private void RaiseCatalogChanged()
    {
        try { CatalogChanged?.Invoke(); }
        catch (Exception ex) { _log.Warn("a CatalogChanged handler threw: " + ex.Message); }
    }

    /// <summary>Info about one metric in the current language; null when the id is unknown. See <see cref="IMetricsBus.GetMetricInfo"/>.</summary>
    public MetricInfo? GetMetricInfo(MetricId id)
    {
        Raw? raw;
        lock (_gate) raw = Snapshot(id);
        return raw is null ? null : Materialize(raw);
    }

    /// <summary>Info about every known metric, ordered by owner module id then metric id (ordinal).</summary>
    public IReadOnlyList<MetricInfo> GetAllMetricInfo()
    {
        Raw[] raws;
        lock (_gate)
        {
            var ids = new HashSet<MetricId>(_known.Keys);
            ids.UnionWith(_descriptors.Keys);
            ids.UnionWith(_withdrawn);
            raws = ids.Select(Snapshot).OfType<Raw>().ToArray();
        }
        return raws.Select(Materialize)
            .OrderBy(i => i.OwnerModuleId, StringComparer.Ordinal).ThenBy(i => i.Id.Value, StringComparer.Ordinal).ToArray();
    }

    private sealed record Raw(MetricId Id, MetricDescriptor? Descriptor, string Owner, MetricUnit Unit, bool Available);

    private MetricInfo Materialize(Raw raw)
    {
        var (id, descriptor, owner, unit, available) = raw;
        string label = descriptor is null ? "" : Resolve(owner, descriptor.LabelKey);
        if (string.IsNullOrWhiteSpace(label)) label = Resolve(null, "metric." + id.Value);
        if (string.IsNullOrWhiteSpace(label)) label = id.Value;
        string description = descriptor?.DescriptionKey is { Length: > 0 } dk ? Resolve(owner, dk) : "";
        return new MetricInfo(id, label, description, unit, descriptor?.FormatHint, owner, available);
    }

    private Raw? Snapshot(MetricId id)
    {
        bool hasDescriptor = _descriptors.TryGetValue(id, out var entry);
        bool hasKnown = _known.TryGetValue(id, out var known);
        if (!hasDescriptor && !hasKnown && !_withdrawn.Contains(id)) return null;

        var descriptor = hasDescriptor ? entry.Descriptor : null;
        string owner = hasDescriptor ? entry.Owner : hasKnown ? known.Source : "host";
        var unit = descriptor is not null ? descriptor.Unit : hasKnown ? known.Unit : MetricUnit.None;
        return new Raw(id, descriptor, owner, unit, !_withdrawn.Contains(id));
    }

    private string Resolve(string? owner, string key)
    {
        if (Text.IsLiteral(key)) return Text.Unwrap(key);
        try { return TextResolver?.Invoke(owner, key) ?? ""; }
        catch (Exception ex) { _log.Warn($"resolving '{key}' for {owner ?? "host"} failed: {ex.Message}"); return ""; }
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

    private sealed class Subscription(MetricsBus bus, Action<MetricSample> handler, MetricId? filter, string? owner, TimeSpan? background = null) : IDisposable
    {
        private int _disposed;

        /// <summary>Set for a background subscription: it is not counted in <c>SubscriberCount</c> and publishers read it as a demand.</summary>
        public TimeSpan? Background { get; } = background;

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
        private readonly List<(Action Original, Action Hook)> _catalogHooks = new();

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

        public MetricInfo? GetMetricInfo(MetricId id) => bus.GetMetricInfo(id);

        public IReadOnlyList<MetricInfo> GetAllMetricInfo() => bus.GetAllMetricInfo();

        public long CatalogVersion => bus.CatalogVersion;

        public event Action? CatalogChanged
        {
            add
            {
                if (value is null) return;
                var guarded = guardAction(value);
                lock (_eventGate) _catalogHooks.Add((value, guarded));
                bus.CatalogChanged += guarded;
                track(new Remover(() => RemoveCatalog(value)));
            }
            remove { if (value is not null) RemoveCatalog(value); }
        }

        private void RemoveCatalog(Action original)
        {
            Action? hook = null;
            lock (_eventGate)
            {
                var index = _catalogHooks.FindIndex(h => h.Original == original);
                if (index < 0) return;
                hook = _catalogHooks[index].Hook;
                _catalogHooks.RemoveAt(index);
            }
            bus.CatalogChanged -= hook;
        }

        public int SubscriberCount(MetricId id) => bus.SubscriberCount(id, excludeOwner: moduleId);

        public IDisposable SubscribeBackground(MetricId id, TimeSpan interval, Action<MetricSample> handler)
        {
            var sub = bus.SubscribeBackgroundFor(moduleId, id, interval, guard(handler));
            track(sub);
            return sub;
        }

        public TimeSpan? BackgroundInterval(MetricId id) => bus.BackgroundInterval(id, excludeOwner: moduleId);

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
