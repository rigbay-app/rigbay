using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>
/// The host's metrics bus: latest-value cache plus synchronous delivery. A handler that throws is logged and skipped;
/// the other subscribers still get the sample. Module access goes through <see cref="ForModule"/>, which stamps the
/// source and runs module handlers in the module's fault boundary.
/// </summary>
public sealed class MetricsBus
{
    private readonly object _gate = new();
    private readonly Dictionary<MetricId, MetricSample> _latest = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _clock;

    public MetricsBus(ILog log, Func<DateTimeOffset>? clock = null)
    {
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Publishes as <paramref name="source"/>. NaN and infinite values are dropped and logged.</summary>
    public void Publish(string source, MetricId id, double value, MetricUnit unit)
    {
        if (id.Value.Length == 0) { _log.Warn($"metric from {source}: empty id dropped"); return; }
        if (!double.IsFinite(value)) { _log.Warn($"metric {id} from {source}: non-finite value dropped"); return; }
        Publish(new MetricSample(id, value, unit, _clock(), source));
    }

    /// <summary>Publishes a complete sample (host and tests).</summary>
    public void Publish(MetricSample sample)
    {
        Subscription[] targets;
        lock (_gate)
        {
            _latest[sample.Id] = sample;
            targets = _subscriptions.Where(s => s.Filter is null || s.Filter.Value == sample.Id).ToArray();
        }
        foreach (var s in targets)
        {
            try { s.Handler(sample); }
            catch (Exception ex) { _log.Warn($"metric subscriber failed for {sample.Id}", ex); }
        }
    }

    /// <summary>For host code (UI, recorder host): not tied to a module.</summary>
    public IDisposable Subscribe(Action<MetricSample> handler, MetricId? filter = null)
    {
        var sub = new Subscription(this, handler, filter);
        lock (_gate) _subscriptions.Add(sub);
        return sub;
    }

    public bool TryGetLatest(MetricId id, out MetricSample sample)
    {
        lock (_gate) return _latest.TryGetValue(id, out sample!);
    }

    public IReadOnlyList<MetricSample> GetLatest()
    {
        lock (_gate) return _latest.Values.OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The <see cref="IMetricsBus"/> of one module. <paramref name="guard"/> wraps each module handler.</summary>
    internal IMetricsBus ForModule(string moduleId, Func<Action<MetricSample>, Action<MetricSample>> guard, Action<IDisposable> track)
        => new ModuleBus(this, moduleId, guard, track);

    private sealed class Subscription(MetricsBus bus, Action<MetricSample> handler, MetricId? filter) : IDisposable
    {
        public Action<MetricSample> Handler { get; } = handler;
        public MetricId? Filter { get; } = filter;

        public void Dispose()
        {
            lock (bus._gate) bus._subscriptions.Remove(this);
        }
    }

    private sealed class ModuleBus(MetricsBus bus, string moduleId, Func<Action<MetricSample>, Action<MetricSample>> guard, Action<IDisposable> track) : IMetricsBus
    {
        public void Publish(MetricId id, double value, MetricUnit unit) => bus.Publish(moduleId, id, value, unit);

        public IDisposable Subscribe(Action<MetricSample> handler)
        {
            var sub = bus.Subscribe(guard(handler));
            track(sub);
            return sub;
        }

        public IDisposable Subscribe(MetricId id, Action<MetricSample> handler)
        {
            var sub = bus.Subscribe(guard(handler), id);
            track(sub);
            return sub;
        }

        public bool TryGetLatest(MetricId id, out MetricSample sample) => bus.TryGetLatest(id, out sample);

        public IReadOnlyList<MetricSample> GetLatest() => bus.GetLatest();
    }
}
