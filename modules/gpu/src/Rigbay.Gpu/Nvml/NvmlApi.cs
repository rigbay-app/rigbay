using System.Runtime.InteropServices;
using System.Text;

namespace Rigbay.Gpu.Nvml;

/// <summary>Finds an exported function of the NVML library by name (0 when it is not exported). Tests give it a table of managed functions.</summary>
internal interface INvmlExports
{
    /// <summary>The address of the exported function, or 0.</summary>
    nint Find(string name);
}

/// <summary>The exports of a library loaded by <see cref="NvmlApi.TryOpenSystemLibrary"/>.</summary>
internal sealed class LibraryExports : INvmlExports
{
    private readonly nint _library;

    public LibraryExports(nint library) => _library = library;

    public nint Find(string name) => NativeLibrary.TryGetExport(_library, name, out var address) ? address : 0;
}

/// <summary>
/// NVML through function pointers. Nothing is bound at compile time (no DllImport): a function is looked up by name at the first call, and only
/// after <see cref="NvmlAllowList.Require"/> has accepted the name, so the list in <see cref="NvmlAllowList"/> is the whole reach of the module
/// into the driver. The struct layouts and signatures follow NVIDIA's NVML header (Windows x64: one calling convention).
/// </summary>
internal sealed class NvmlApi : INvml
{
    // ------------------------------------------------------------------ native shapes

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeUtilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMemory { public ulong Total; public ulong Free; public ulong Used; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int NoArguments();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CountFn(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int HandleFn(uint index, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int NameFn(nint device, byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int UtilizationFn(nint device, out NativeUtilization utilization);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int UIntFn(nint device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ClockFn(nint device, int clockType, out uint megahertz);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MemoryFn(nint device, out NativeMemory memory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int TemperatureFn(nint device, int sensor, out uint celsius);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int PStateFn(nint device, out int pstate);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ULongFn(nint device, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate nint ErrorStringFn(int code);

    /// <summary>The size NVML documents for a device name buffer.</summary>
    private const int NameBuffer = 96;

    /// <summary><c>NVML_TEMPERATURE_GPU</c>.</summary>
    private const int TemperatureGpu = 0;

    // ------------------------------------------------------------------ loading

    private static readonly object LibraryGate = new();
    private static nint _library;

    /// <summary>
    /// Loads <c>nvml.dll</c> from the Windows system folder by its full path, and from there only (no search of the current folder, the
    /// application folder or PATH). Returns null with a language key (<c>probe.no-dll</c>, <c>probe.dll-not-loaded</c>) when it cannot.
    /// The library stays loaded for the life of the process; NVML itself is initialised and shut down by <see cref="Init"/> and <see cref="Shutdown"/>.
    /// </summary>
    public static NvmlApi? TryOpenSystemLibrary(out string? reasonKey)
    {
        reasonKey = null;
        lock (LibraryGate)
        {
            if (_library == 0)
            {
                var path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
                if (!File.Exists(path)) { reasonKey = "probe.no-dll"; return null; }
                if (!NativeLibrary.TryLoad(path, typeof(NvmlApi).Assembly, DllImportSearchPath.System32, out var handle) || handle == 0)
                {
                    reasonKey = "probe.dll-not-loaded";
                    return null;
                }
                _library = handle;
            }
            return new NvmlApi(new LibraryExports(_library));
        }
    }

    private readonly INvmlExports _exports;
    private readonly Dictionary<string, Delegate?> _resolved = new(StringComparer.Ordinal);

    /// <summary>Creates the API over a set of exports (tests pass a table of managed functions).</summary>
    public NvmlApi(INvmlExports exports) => _exports = exports;

    /// <summary>
    /// The function called <paramref name="name"/>, or null when the library does not export it. The name must be on the allow list (an exception
    /// otherwise, before the library is asked). The answer, also a null, is remembered.
    /// </summary>
    private T? Resolve<T>(string name) where T : Delegate
    {
        NvmlAllowList.Require(name);
        lock (_resolved)
        {
            if (_resolved.TryGetValue(name, out var known)) return (T?)known;
            var address = _exports.Find(name);
            var function = address == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
            _resolved[name] = function;
            return function;
        }
    }

    private static NvmlReturn Missing => NvmlReturn.FunctionNotFound;

    // ------------------------------------------------------------------ INvml

    public NvmlReturn Init() => Resolve<NoArguments>(NvmlAllowList.Init) is { } f ? (NvmlReturn)f() : Missing;

    public NvmlReturn Shutdown() => Resolve<NoArguments>(NvmlAllowList.Shutdown) is { } f ? (NvmlReturn)f() : Missing;

    public NvmlReturn GetCount(out uint count)
    {
        count = 0;
        return Resolve<CountFn>(NvmlAllowList.DeviceCount) is { } f ? (NvmlReturn)f(out count) : Missing;
    }

    public NvmlReturn GetHandle(uint index, out nint device)
    {
        device = 0;
        return Resolve<HandleFn>(NvmlAllowList.HandleByIndex) is { } f ? (NvmlReturn)f(index, out device) : Missing;
    }

    public NvmlReturn GetName(nint device, out string name)
    {
        name = "";
        if (Resolve<NameFn>(NvmlAllowList.DeviceName) is not { } f) return Missing;
        var buffer = new byte[NameBuffer];
        var code = (NvmlReturn)f(device, buffer, (uint)buffer.Length);
        if (code != NvmlReturn.Success) return code;
        int end = Array.IndexOf(buffer, (byte)0);
        name = Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
        return code;
    }

    public NvmlReturn GetUtilization(nint device, out uint gpuPercent, out uint memoryControllerPercent)
    {
        gpuPercent = 0;
        memoryControllerPercent = 0;
        if (Resolve<UtilizationFn>(NvmlAllowList.Utilization) is not { } f) return Missing;
        var code = (NvmlReturn)f(device, out var u);
        if (code == NvmlReturn.Success) { gpuPercent = u.Gpu; memoryControllerPercent = u.Memory; }
        return code;
    }

    public NvmlReturn GetPowerUsage(nint device, out uint milliwatts)
    {
        milliwatts = 0;
        return Resolve<UIntFn>(NvmlAllowList.PowerUsage) is { } f ? (NvmlReturn)f(device, out milliwatts) : Missing;
    }

    public NvmlReturn GetEnforcedPowerLimit(nint device, out uint milliwatts)
    {
        milliwatts = 0;
        return Resolve<UIntFn>(NvmlAllowList.EnforcedPowerLimit) is { } f ? (NvmlReturn)f(device, out milliwatts) : Missing;
    }

    public NvmlReturn GetClock(nint device, NvmlClock clock, out uint megahertz)
    {
        megahertz = 0;
        if (clock is not (NvmlClock.Graphics or NvmlClock.Memory)) return NvmlReturn.InvalidArgument;
        return Resolve<ClockFn>(NvmlAllowList.ClockInfo) is { } f ? (NvmlReturn)f(device, (int)clock, out megahertz) : Missing;
    }

    public NvmlReturn GetMemory(nint device, out ulong totalBytes, out ulong usedBytes)
    {
        totalBytes = 0;
        usedBytes = 0;
        if (Resolve<MemoryFn>(NvmlAllowList.MemoryInfo) is not { } f) return Missing;
        var code = (NvmlReturn)f(device, out var m);
        if (code == NvmlReturn.Success) { totalBytes = m.Total; usedBytes = m.Used; }
        return code;
    }

    public NvmlReturn GetTemperature(nint device, out uint celsius)
    {
        celsius = 0;
        return Resolve<TemperatureFn>(NvmlAllowList.Temperature) is { } f ? (NvmlReturn)f(device, TemperatureGpu, out celsius) : Missing;
    }

    public NvmlReturn GetPerformanceState(nint device, out int pstate)
    {
        pstate = 0;
        return Resolve<PStateFn>(NvmlAllowList.PerformanceState) is { } f ? (NvmlReturn)f(device, out pstate) : Missing;
    }

    public NvmlReturn GetClockEventReasons(nint device, out ulong reasons)
    {
        reasons = 0;
        // The driver from 535 on exports the new name; older ones only the old name. Same signature, same bits.
        var f = Resolve<ULongFn>(NvmlAllowList.ClocksEventReasons) ?? Resolve<ULongFn>(NvmlAllowList.ClocksThrottleReasons);
        return f is null ? NvmlReturn.NotSupported : (NvmlReturn)f(device, out reasons);
    }

    public string ErrorString(NvmlReturn code)
    {
        try
        {
            if (Resolve<ErrorStringFn>(NvmlAllowList.ErrorString) is { } f && Marshal.PtrToStringAnsi(f((int)code)) is { Length: > 0 } text) return text;
        }
        catch (Exception)
        {
            // fall through to the plain text
        }
        return "NVML error " + (int)code;
    }
}
