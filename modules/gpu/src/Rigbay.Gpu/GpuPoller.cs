using Rigbay.Abstractions;
using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu;

/// <summary>State of the readings as the rest of the module sees it.</summary>
internal enum GpuState
{
    /// <summary>No poll has finished yet.</summary>
    Starting,
    /// <summary>The last poll got at least one answer.</summary>
    Available,
    /// <summary>The last poll or the last few got no answer at all (fewer than <see cref="GpuPoller.FailureLimit"/> in a row).</summary>
    Degraded,
    /// <summary>NVML could not be read <see cref="GpuPoller.FailureLimit"/> times in a row. The page says so.</summary>
    Unavailable,
}

/// <summary>The result of one poll. A null is a reading the GPU did not give.</summary>
internal sealed record GpuSnapshot(
    DateTimeOffset At,
    double? Load,
    double? MemoryController,
    double? PowerWatts,
    double? PowerLimitWatts,
    double? GraphicsMegahertz,
    double? MemoryMegahertz,
    double? MemoryUsedBytes,
    double? MemoryTotalBytes,
    double? TemperatureCelsius,
    int? PerformanceState,
    ulong? ClockReasons,
    bool AnyAnswer)
{
    /// <summary>Nothing read yet.</summary>
    public static GpuSnapshot Empty { get; } = new(DateTimeOffset.MinValue, null, null, null, null, null, null, null, null, null, null, null, false);
}

/// <summary>
/// Reads the GPU through <see cref="INvml"/> and publishes the readings on the metrics bus. An answer NVML gives, whatever the code, is a result and
/// never an exception (an exception would be a bug, and the caller's fault boundary or the scheduler deals with it). A reading that the GPU says it
/// does not have (<see cref="NvmlReturn.NotSupported"/>) is withdrawn at once and stays withdrawn; one that fails any other way is withdrawn after
/// <see cref="FailureLimit"/> polls in a row, so the page and the Dashboard say "not available" and never keep or invent a number. A poll in which no
/// call answered counts as failed; after <see cref="FailureLimit"/> of them the state is <see cref="GpuState.Unavailable"/> and NVML is opened again
/// (a driver reset or update ends the old session). Polls are serialised here, and every NVML call is serialised again by <see cref="SerialNvml"/>.
/// </summary>
internal sealed class GpuPoller
{
    /// <summary>Failed polls (or failures of one reading) in a row after which the readings count as not available.</summary>
    public const int FailureLimit = 3;

    /// <summary>NVML's value for "performance state unknown".</summary>
    private const int UnknownPerformanceState = 32;

    private readonly INvml _nvml;
    private readonly IMetricsBus _bus;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _pollLock = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<GpuMetric, int> _misses = new();
    private readonly HashSet<GpuMetric> _withdrawn = new();
    private nint _device;
    private bool _open;
    private int _failedPolls;
    private GpuSnapshot _latest = GpuSnapshot.Empty;
    private GpuState _state = GpuState.Starting;
    private string _deviceName = "";

    public GpuPoller(INvml nvml, IMetricsBus bus, ILog log, Func<DateTimeOffset>? now = null)
    {
        _nvml = nvml;
        _bus = bus;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Raised after a poll when the state changed (on the polling thread).</summary>
    public event Action<GpuState>? StateChanged;

    /// <summary>Raised after every poll (on the polling thread).</summary>
    public event Action<GpuSnapshot>? Polled;

    public GpuSnapshot Latest { get { lock (_stateLock) return _latest; } }

    public GpuState State { get { lock (_stateLock) return _state; } }

    public int FailedPolls { get { lock (_stateLock) return _failedPolls; } }

    /// <summary>The product name of the GPU read when NVML was opened; empty until then.</summary>
    public string DeviceName { get { lock (_stateLock) return _deviceName; } }

    /// <summary>True between a successful <see cref="Open"/> and <see cref="Close"/>.</summary>
    public bool IsOpen { get { lock (_pollLock) return _open; } }

    /// <summary>
    /// Initialises NVML and takes the handle and name of the first GPU. On success the session stays open until <see cref="Close"/>; on a failure
    /// NVML is left shut down and the code says why.
    /// </summary>
    public NvmlReturn Open()
    {
        lock (_pollLock) return OpenLocked();
    }

    private NvmlReturn OpenLocked()
    {
        if (_open) return NvmlReturn.Success;
        var code = _nvml.Init();
        if (code != NvmlReturn.Success) return code;
        code = _nvml.GetCount(out var count);
        if (code == NvmlReturn.Success && count == 0) code = NvmlReturn.NotFound;
        nint device = 0;
        string name = "";
        if (code == NvmlReturn.Success) code = _nvml.GetHandle(0, out device);
        if (code == NvmlReturn.Success) code = _nvml.GetName(device, out name);
        if (code != NvmlReturn.Success)
        {
            _nvml.Shutdown();
            return code;
        }
        _device = device;
        _open = true;
        lock (_stateLock) _deviceName = name;
        return NvmlReturn.Success;
    }

    /// <summary>Shuts NVML down (once). A poll that is running finishes first; later polls do nothing.</summary>
    public void Close()
    {
        lock (_pollLock)
        {
            if (!_open) return;
            _open = false;
            _nvml.Shutdown();
        }
    }

    /// <summary>Reads every reading once, publishes the values, updates the state. Safe to call from any thread.</summary>
    public GpuSnapshot PollOnce()
    {
        GpuSnapshot snapshot;
        GpuState? changed = null;
        lock (_pollLock)
        {
            if (!_open) return Latest;
            var dev = _device;
            bool any = false;

            double? load = null, memctl = null, power = null, limit = null, clock = null, memClock = null, used = null, total = null, temp = null;
            int? pstate = null;
            ulong? reasons = null;

            var rc = _nvml.GetUtilization(dev, out var gpuPct, out var memPct);
            any |= Take(GpuMetrics.Load, rc, gpuPct, out load);
            any |= Take(GpuMetrics.MemoryController, rc, memPct, out memctl);

            rc = _nvml.GetPowerUsage(dev, out var mw);
            any |= Take(GpuMetrics.Power, rc, mw / 1000.0, out power);
            rc = _nvml.GetEnforcedPowerLimit(dev, out var limitMw);
            any |= Take(GpuMetrics.PowerLimit, rc, limitMw / 1000.0, out limit);

            rc = _nvml.GetClock(dev, NvmlClock.Graphics, out var gr);
            any |= Take(GpuMetrics.Clock, rc, gr, out clock);
            rc = _nvml.GetClock(dev, NvmlClock.Memory, out var mem);
            any |= Take(GpuMetrics.MemoryClock, rc, mem, out memClock);

            rc = _nvml.GetMemory(dev, out var totalBytes, out var usedBytes);
            any |= Take(GpuMetrics.MemoryUsed, rc, usedBytes, out used);
            any |= Take(GpuMetrics.MemoryTotal, rc, totalBytes, out total);

            rc = _nvml.GetTemperature(dev, out var celsius);
            any |= Take(GpuMetrics.Temperature, rc, celsius, out temp);

            rc = _nvml.GetPerformanceState(dev, out var p);
            // 32 is NVML's "unknown": not a number to show.
            if (rc == NvmlReturn.Success && p is < 0 or > 15) rc = NvmlReturn.NotSupported;
            any |= Take(GpuMetrics.PerformanceState, rc, p, out var pst);
            pstate = pst is { } pv ? (int)pv : null;

            rc = _nvml.GetClockEventReasons(dev, out var mask);
            any |= Take(GpuMetrics.ClockLimits, rc, mask, out var rs);
            reasons = rs is { } rv ? (ulong)rv : null;

            lock (_stateLock)
            {
                snapshot = new GpuSnapshot(_now(), load, memctl, power, limit, clock, memClock, used, total, temp, pstate, reasons, any);
                _latest = snapshot;
                if (any) _failedPolls = 0;
                else _failedPolls++;
                var next = any ? GpuState.Available : _failedPolls >= FailureLimit ? GpuState.Unavailable : GpuState.Degraded;
                if (next != _state) { _state = next; changed = next; }
            }

            // After a run of polls with no answer at all, the session may be dead (driver reset or update): open it again, at first and then now and then.
            int failed = FailedPolls;
            if (!any && (failed == FailureLimit || (failed > FailureLimit && failed % 10 == 0))) TryReopen();
        }

        if (changed is { } state)
        {
            _log.Info($"GPU readings are now {state} (failed polls in a row: {FailedPolls}).");
            Invoke(() => StateChanged?.Invoke(state));
        }
        Invoke(() => Polled?.Invoke(snapshot));
        return snapshot;
    }

    /// <summary>
    /// Turns one NVML answer into a published value, a withdrawal or a count of misses. Returns true when it was a value.
    /// A reading the GPU says it does not have is withdrawn at once; any other failure after <see cref="FailureLimit"/> polls in a row.
    /// </summary>
    private bool Take(GpuMetric metric, NvmlReturn code, double value, out double? taken)
    {
        taken = null;
        if (code == NvmlReturn.Success)
        {
            lock (_stateLock) { _misses[metric] = 0; _withdrawn.Remove(metric); }
            taken = value;
            try { _bus.Publish(metric.Id, value, metric.Unit); }
            catch (Exception ex) { _log.Warn("Publishing " + metric.Id + " failed: " + ex.Message); }
            return true;
        }

        int misses;
        bool withdraw;
        lock (_stateLock)
        {
            misses = _misses.TryGetValue(metric, out var m) ? m + 1 : 1;
            _misses[metric] = misses;
            withdraw = (code == NvmlReturn.NotSupported || misses >= FailureLimit) && _withdrawn.Add(metric);
        }
        if (misses == 1) _log.Warn($"GPU reading {metric.Id} is not available: {SafeErrorString(code)} ({(int)code}).");
        if (!withdraw) return false;
        try { _bus.Withdraw(metric.Id); }
        catch (Exception ex) { _log.Warn("Withdrawing " + metric.Id + " failed: " + ex.Message); }
        return false;
    }

    private void TryReopen()
    {
        _log.Info("GPU readings: opening NVML again.");
        if (_open) { _open = false; _nvml.Shutdown(); }
        var code = OpenLocked();
        if (code != NvmlReturn.Success) _log.Warn($"GPU readings: NVML did not open again: {SafeErrorString(code)} ({(int)code}).");
    }

    private string SafeErrorString(NvmlReturn code)
    {
        try { return _nvml.ErrorString(code); }
        catch (Exception) { return "NVML error " + (int)code; }
    }

    private void Invoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { _log.Warn("A GPU readings handler threw: " + ex.Message); }
    }
}
