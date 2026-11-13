namespace Rigbay.Gpu.Nvml;

/// <summary>
/// Puts every NVML call behind one lock, so the library is never entered by two threads at once (the poll thread, the probe, a command, the
/// shutdown). The module only ever holds NVML through this wrapper.
/// </summary>
internal sealed class SerialNvml : INvml
{
    private readonly INvml _inner;
    private readonly object _gate = new();

    public SerialNvml(INvml inner) => _inner = inner;

    public NvmlReturn Init() { lock (_gate) return _inner.Init(); }
    public NvmlReturn Shutdown() { lock (_gate) return _inner.Shutdown(); }
    public NvmlReturn GetCount(out uint count) { lock (_gate) return _inner.GetCount(out count); }
    public NvmlReturn GetHandle(uint index, out nint device) { lock (_gate) return _inner.GetHandle(index, out device); }
    public NvmlReturn GetName(nint device, out string name) { lock (_gate) return _inner.GetName(device, out name); }
    public NvmlReturn GetUtilization(nint device, out uint gpuPercent, out uint memoryControllerPercent) { lock (_gate) return _inner.GetUtilization(device, out gpuPercent, out memoryControllerPercent); }
    public NvmlReturn GetPowerUsage(nint device, out uint milliwatts) { lock (_gate) return _inner.GetPowerUsage(device, out milliwatts); }
    public NvmlReturn GetEnforcedPowerLimit(nint device, out uint milliwatts) { lock (_gate) return _inner.GetEnforcedPowerLimit(device, out milliwatts); }
    public NvmlReturn GetClock(nint device, NvmlClock clock, out uint megahertz) { lock (_gate) return _inner.GetClock(device, clock, out megahertz); }
    public NvmlReturn GetMemory(nint device, out ulong totalBytes, out ulong usedBytes) { lock (_gate) return _inner.GetMemory(device, out totalBytes, out usedBytes); }
    public NvmlReturn GetTemperature(nint device, out uint celsius) { lock (_gate) return _inner.GetTemperature(device, out celsius); }
    public NvmlReturn GetPerformanceState(nint device, out int pstate) { lock (_gate) return _inner.GetPerformanceState(device, out pstate); }
    public NvmlReturn GetClockEventReasons(nint device, out ulong reasons) { lock (_gate) return _inner.GetClockEventReasons(device, out reasons); }
    public string ErrorString(NvmlReturn code) { lock (_gate) return _inner.ErrorString(code); }
}
