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

    /// <summary>The metrics the monitor sampler publishes without the per-core option (PL1 and PL2 come from the state refresh).</summary>
    public static IReadOnlyList<MetricId> Monitored { get; } = new[]
    {
        CpuTemp, CpuPower, CpuClock, CpuUsage, CoreTempMax, CorePower, GfxPower, Uncore, ActiveCores, RefClock, MemSpeed, GfxFreq,
        GfxTemp, GfxVolt, CacheVolt, SaVolt, ThrottleThermal, ThrottlePower, ThrottleCurrent, ThrottleVr,
    };

    /// <summary>The 32 per-core metrics (published only when the "Publish per-core readings" option is on).</summary>
    public static IReadOnlyList<MetricId> PerCore { get; } = Enumerable.Range(0, Mon.Cores)
        .SelectMany(i => new[] { CoreTemp(i), CoreFreq(i), CoreVolt(i), CoreUtil(i) }).ToArray();

    /// <summary>Everything the module publishes, per-core ids included.</summary>
    public static IReadOnlyList<MetricId> All { get; } = Monitored.Concat(PerCore).Append(Pl1).Append(Pl2).ToArray();

    // Written out (not built from the core number) so the language checker sees every key used in the source.
    // They come before Descriptors: static initialisers run in this order.
    private static readonly string[] CoreTempKeys = { "metric.core0.temp", "metric.core1.temp", "metric.core2.temp", "metric.core3.temp", "metric.core4.temp", "metric.core5.temp", "metric.core6.temp", "metric.core7.temp" };
    private static readonly string[] CoreClockKeys = { "metric.core0.clock", "metric.core1.clock", "metric.core2.clock", "metric.core3.clock", "metric.core4.clock", "metric.core5.clock", "metric.core6.clock", "metric.core7.clock" };
    private static readonly string[] CoreVoltKeys = { "metric.core0.volt", "metric.core1.volt", "metric.core2.volt", "metric.core3.volt", "metric.core4.volt", "metric.core5.volt", "metric.core6.volt", "metric.core7.volt" };
    private static readonly string[] CoreUsageKeys = { "metric.core0.usage", "metric.core1.usage", "metric.core2.usage", "metric.core3.usage", "metric.core4.usage", "metric.core5.usage", "metric.core6.usage", "metric.core7.usage" };

    /// <summary>Label (module-relative language key), unit and format of every metric, for <see cref="IMetricsBus.Describe"/>.</summary>
    public static IReadOnlyList<MetricDescriptor> Descriptors { get; } = BuildDescriptors();

    private static IReadOnlyList<MetricDescriptor> BuildDescriptors()
    {
        var list = new List<MetricDescriptor>
        {
            new(CpuTemp, "metric.cpu-temp", MetricUnit.Celsius, "0"),
            new(CpuPower, "metric.cpu-power", MetricUnit.Watt, "0.0"),
            new(CpuUsage, "metric.cpu-usage", MetricUnit.Percent, "0"),
            new(CpuClock, "metric.cpu-clock", MetricUnit.Megahertz, "0"),
            new(Pl1, "metric.cpu-pl1", MetricUnit.Watt, "0"),
            new(Pl2, "metric.cpu-pl2", MetricUnit.Watt, "0"),
            new(CoreTempMax, "metric.core-temp-max", MetricUnit.Celsius, "0"),
            new(CorePower, "metric.core-power", MetricUnit.Watt, "0.0"),
            new(GfxPower, "metric.igpu-power", MetricUnit.Watt, "0.0"),
            new(Uncore, "metric.uncore", MetricUnit.Megahertz, "0"),
            new(ActiveCores, "metric.active-cores", MetricUnit.Count, "0"),
            new(RefClock, "metric.ref-clock", MetricUnit.Megahertz, "0"),
            new(MemSpeed, "metric.mem-speed", MetricUnit.None, "0"),
            new(GfxFreq, "metric.igpu-clock", MetricUnit.Megahertz, "0"),
            new(GfxTemp, "metric.igpu-temp", MetricUnit.Celsius, "0"),
            new(GfxVolt, "metric.igpu-volt", MetricUnit.Volt, "0.000"),
            new(CacheVolt, "metric.cache-volt", MetricUnit.Volt, "0.000"),
            new(SaVolt, "metric.sa-volt", MetricUnit.Volt, "0.000"),
            new(ThrottleThermal, "metric.throttle-thermal", MetricUnit.Percent, "0"),
            new(ThrottlePower, "metric.throttle-power", MetricUnit.Percent, "0"),
            new(ThrottleCurrent, "metric.throttle-current", MetricUnit.Percent, "0"),
            new(ThrottleVr, "metric.throttle-vr", MetricUnit.Percent, "0"),
        };
        for (int i = 0; i < Mon.Cores; i++)
        {
            list.Add(new(CoreTemp(i), CoreTempKeys[i], MetricUnit.Celsius, "0"));
            list.Add(new(CoreFreq(i), CoreClockKeys[i], MetricUnit.Megahertz, "0"));
            list.Add(new(CoreVolt(i), CoreVoltKeys[i], MetricUnit.Volt, "0.000"));
            list.Add(new(CoreUtil(i), CoreUsageKeys[i], MetricUnit.Percent, "0"));
        }
        return list;
    }
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

    /// <summary>
    /// Takes every monitor reading off the bus (<see cref="IMetricsBus.Withdraw"/>) because XTU cannot be read: the page and the
    /// dashboard then say "not available" instead of showing the last number. The next published sample makes an id available again.
    /// </summary>
    public void WithdrawAll()
    {
        foreach (var id in XtuMetrics.Monitored.Concat(XtuMetrics.PerCore))
        {
            try { _bus.Withdraw(id); }
            catch (Exception) { /* a bus that refuses has nothing more to say; the loop must go on */ }
        }
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
