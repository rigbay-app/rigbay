using Rigbay.Abstractions;

namespace Rigbay.Watch.Demo;

/// <summary>
/// Two made-up readings for trying Watch: <c>watch-demo.level</c> (°C: calm at 40, hot at 100 from 10 s to 70 s after the module was turned on, calm
/// again at 30) and <c>watch-demo.errors</c> (a counter that goes up by 3 per reading between 30 s and 60 s). It publishes only while something asks, with
/// <see cref="MetricDemand"/>, so it also shows what a publisher built for host API 0.1.4 does with Watch's background request (every 5 s here).
/// </summary>
public sealed class DemoFeedModule : IModule
{
    private static readonly MetricId Level = new("watch-demo.level");
    private static readonly MetricId Errors = new("watch-demo.errors");
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Look = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private IHostServices? _host;
    private Timer? _timer;
    private DateTimeOffset _start;
    private double _errors;
    private bool _stopped;

    public ProbeResult Probe() => ProbeResult.Yes();

    public void Initialize(IHostServices host)
    {
        _host = host;
        _stopped = false;
        _start = DateTimeOffset.UtcNow;
        _errors = 0;
        host.Metrics.Describe(new MetricDescriptor(Level, Text.Literal("Demo level"), MetricUnit.Celsius, "0"));
        host.Metrics.Describe(new MetricDescriptor(Errors, Text.Literal("Demo errors"), MetricUnit.Count, "0"));
        host.Metrics.SubscribersChanged += OnDemandChanged;
        lock (_gate) _timer = new Timer(OnTimer, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
        }
        if (_host is { } host)
        {
            host.Metrics.SubscribersChanged -= OnDemandChanged;
            host.Metrics.Withdraw(Level);
            host.Metrics.Withdraw(Errors);
        }
    }

    private void OnDemandChanged()
    {
        lock (_gate) if (!_stopped) _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        var host = _host;
        if (host is null) return;
        var next = Look;
        try
        {
            var demand = host.Evaluate(new[] { Level, Errors }, Fast, Timeout.InfiniteTimeSpan);
            if (demand.Kind != DemandKind.Idle)
            {
                double t = (DateTimeOffset.UtcNow - _start).TotalSeconds;
                if (t is >= 30 and < 60) _errors += 3;
                host.Metrics.Publish(Level, t is >= 10 and < 70 ? 100 : t < 10 ? 40 : 30, MetricUnit.Celsius);
                host.Metrics.Publish(Errors, _errors, MetricUnit.Count);
                host.Log.Debug($"demo feed: published at t={t:0}s ({demand.Kind}, every {demand.Interval.TotalSeconds:0}s)");
                next = demand.Interval;
            }
        }
        catch (Exception ex)
        {
            host.Log.Error("demo feed failed", ex);
        }
        finally
        {
            lock (_gate) if (!_stopped) _timer?.Change(next, Timeout.InfiniteTimeSpan);
        }
    }
}
