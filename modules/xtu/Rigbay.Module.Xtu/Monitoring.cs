using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

/// <summary>XTU monitor ids (from the contract's monitor id table). Short identifiers are facts; the table is our own.</summary>
internal static class Mon
{
    public const int Cores = 8;
    public const uint CpuThrottle = 3, ActiveCores = 4, GfxFreq = 6, CoreTemp0 = 7, RefClock = 15, MemSpeed = 17, Uncore = 20,
        PowerThrottle = 22, CurrentThrottle = 23, PkgPower = 1000, CorePower = 1001, GfxPower = 1002, VrThrottle = 1003,
        MaxPFreq = 1010, CoreFreq0 = 2000, CoreVolt0 = 3000, CoreUtil0 = 4000;
    public const uint PkgTemp = 2701131777u, SaVolt = 2717909008u, GfxVolt = 2768240652u, GfxTemp = 2768240653u, CacheVolt = 2768240654u;
}

/// <summary>The metric ids this module publishes (source module is stamped by the host: <c>xtu</c>).</summary>
internal static class XtuMetrics
{
    // Shared vocabulary (WellKnownMetrics), so any recorder or dashboard works with another sensor source too.
    public static readonly MetricId CpuTemp = WellKnownMetrics.CpuTemp;
    public static readonly MetricId CpuPower = WellKnownMetrics.CpuPower;
    public static readonly MetricId CpuUsage = WellKnownMetrics.CpuUsage;
    public static readonly MetricId CpuClock = WellKnownMetrics.CpuClock;
    public static readonly MetricId Pl1 = WellKnownMetrics.CpuPl1;
    public static readonly MetricId Pl2 = WellKnownMetrics.CpuPl2;

    // Module-specific ids start with the module id.
    public static readonly MetricId CoreTempMax = new("xtu.cpu.temp.core-max");
    public static readonly MetricId CorePower = new("xtu.cpu.power.cores");
    public static readonly MetricId GfxPower = new("xtu.igpu.power");
    public static readonly MetricId Uncore = new("xtu.cpu.uncore");
    public static readonly MetricId ActiveCores = new("xtu.cpu.active-cores");
    public static readonly MetricId RefClock = new("xtu.cpu.ref-clock");
    public static readonly MetricId MemSpeed = new("xtu.memory.speed");
    public static readonly MetricId GfxFreq = new("xtu.igpu.clock");
    public static readonly MetricId GfxTemp = new("xtu.igpu.temp");
    public static readonly MetricId GfxVolt = new("xtu.igpu.volt");
    public static readonly MetricId CacheVolt = new("xtu.cpu.volt.cache");
    public static readonly MetricId SaVolt = new("xtu.cpu.volt.system-agent");
    public static readonly MetricId ThrottleThermal = new("xtu.throttle.thermal");
    public static readonly MetricId ThrottlePower = new("xtu.throttle.power");
    public static readonly MetricId ThrottleCurrent = new("xtu.throttle.current");
    public static readonly MetricId ThrottleVr = new("xtu.throttle.vr");

    public static MetricId CoreTemp(int core) => new($"xtu.core{core}.temp");
    public static MetricId CoreFreq(int core) => new($"xtu.core{core}.clock");
    public static MetricId CoreVolt(int core) => new($"xtu.core{core}.volt");
    public static MetricId CoreUtil(int core) => new($"xtu.core{core}.usage");
}

/// <summary>
/// One monitor sample, turned into bus readings. The sampler thread calls <see cref="Tick"/> about once a second.
/// Right after (re)connecting, XTU returns what it measured the last time; those samples are skipped until a probe value
/// moves or four seconds pass (the standalone app's warm-up rule), so stale numbers never reach the bus.
/// </summary>
internal sealed class MonitorSampler
{
    private static readonly uint[] Probe = { Mon.PkgPower, Mon.CorePower, Mon.PkgTemp, Mon.MaxPFreq, Mon.CoreFreq0 };
    public static readonly TimeSpan WarmupLimit = TimeSpan.FromSeconds(4);

    private readonly IXtuClient _client;
    private readonly IMetricsBus _bus;
    private readonly Func<DateTime> _now;
    private bool _warming;
    private DateTime _warmSince;
    private Dictionary<uint, decimal>? _warmFirst;

    public MonitorSampler(IXtuClient client, IMetricsBus bus, Func<DateTime>? now = null)
    {
        _client = client;
        _bus = bus;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Publish per-core temperature, clock, voltage and usage too (32 more ids). Off by default.</summary>
    public bool PerCore { get; set; }

    public bool Warming => _warming;

    /// <summary>One read. Returns the number of readings published (0 while warming up). Throws <see cref="XtuException"/> when XTU cannot be read.</summary>
    public int Tick()
    {
        var values = _client.ReadMonitor(out var justOpened);
        var now = _now();

        if (justOpened)
        {
            _warming = true;
            _warmSince = now;
            _warmFirst = null;
        }

        if (_warming)
        {
            if (_warmFirst is null)
            {
                _warmFirst = new Dictionary<uint, decimal>();
                foreach (var id in Probe)
                    if (values.TryGetValue(id, out var v)) _warmFirst[id] = v;
                return 0;
            }
            var moved = Probe.Any(id => values.TryGetValue(id, out var v) && _warmFirst.TryGetValue(id, out var w) && v != w);
            if (moved || now - _warmSince >= WarmupLimit) _warming = false;
            else return 0;
        }
        return Publish(values);
    }

    /// <summary>XTU gave up or the connection broke: the next read starts a new warm-up.</summary>
    public void Reset()
    {
        _warming = false;
        _warmFirst = null;
        _client.ResetMonitor();
    }

    private int Publish(IReadOnlyDictionary<uint, decimal> v)
    {
        int n = 0;
        void Put(MetricId id, double value, MetricUnit unit)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            _bus.Publish(id, value, unit);
            n++;
        }
        double X(uint id) => v.TryGetValue(id, out var d) ? (double)d : double.NaN;

        Put(XtuMetrics.CpuTemp, X(Mon.PkgTemp), MetricUnit.Celsius);
        Put(XtuMetrics.CpuPower, X(Mon.PkgPower), MetricUnit.Watt);
        Put(XtuMetrics.CpuClock, X(Mon.MaxPFreq) * 1000.0, MetricUnit.Megahertz);   // XTU reports GHz
        Put(XtuMetrics.CpuUsage, MeanCoreUsage(v), MetricUnit.Percent);
        Put(XtuMetrics.CoreTempMax, MaxCoreTemp(v), MetricUnit.Celsius);
        Put(XtuMetrics.CorePower, X(Mon.CorePower), MetricUnit.Watt);
        Put(XtuMetrics.GfxPower, X(Mon.GfxPower), MetricUnit.Watt);
        Put(XtuMetrics.Uncore, X(Mon.Uncore) * 1000.0, MetricUnit.Megahertz);       // GHz
        Put(XtuMetrics.ActiveCores, X(Mon.ActiveCores), MetricUnit.Count);
        Put(XtuMetrics.RefClock, X(Mon.RefClock), MetricUnit.Megahertz);
        Put(XtuMetrics.MemSpeed, X(Mon.MemSpeed), MetricUnit.None);                  // MT/s has no unit of its own
        Put(XtuMetrics.GfxFreq, X(Mon.GfxFreq), MetricUnit.Megahertz);
        Put(XtuMetrics.GfxTemp, X(Mon.GfxTemp), MetricUnit.Celsius);
        Put(XtuMetrics.GfxVolt, X(Mon.GfxVolt), MetricUnit.Volt);
        Put(XtuMetrics.CacheVolt, X(Mon.CacheVolt), MetricUnit.Volt);
        Put(XtuMetrics.SaVolt, X(Mon.SaVolt), MetricUnit.Volt);
        Put(XtuMetrics.ThrottleThermal, X(Mon.CpuThrottle), MetricUnit.Percent);
        Put(XtuMetrics.ThrottlePower, X(Mon.PowerThrottle), MetricUnit.Percent);
        Put(XtuMetrics.ThrottleCurrent, X(Mon.CurrentThrottle), MetricUnit.Percent);
        Put(XtuMetrics.ThrottleVr, X(Mon.VrThrottle), MetricUnit.Percent);

        if (PerCore)
        {
            for (int i = 0; i < Mon.Cores; i++)
            {
                Put(XtuMetrics.CoreTemp(i), X(Mon.CoreTemp0 + (uint)i), MetricUnit.Celsius);
                Put(XtuMetrics.CoreFreq(i), X(Mon.CoreFreq0 + (uint)i) * 1000.0, MetricUnit.Megahertz);
                Put(XtuMetrics.CoreVolt(i), X(Mon.CoreVolt0 + (uint)i), MetricUnit.Volt);
                Put(XtuMetrics.CoreUtil(i), X(Mon.CoreUtil0 + (uint)i), MetricUnit.Percent);
            }
        }
        return n;
    }

    /// <summary>Mean of the per-core utilisation XTU reports (ids 4000 + core), or NaN when none is present.</summary>
    internal static double MeanCoreUsage(IReadOnlyDictionary<uint, decimal> v)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < Mon.Cores; i++)
            if (v.TryGetValue(Mon.CoreUtil0 + (uint)i, out var u)) { sum += (double)u; count++; }
        return count == 0 ? double.NaN : Math.Clamp(sum / count, 0, 100);
    }

    internal static double MaxCoreTemp(IReadOnlyDictionary<uint, decimal> v)
    {
        double max = double.NaN;
        for (int i = 0; i < Mon.Cores; i++)
            if (v.TryGetValue(Mon.CoreTemp0 + (uint)i, out var t) && (double.IsNaN(max) || (double)t > max)) max = (double)t;
        return max;
    }
}
