namespace Rigbay.Gpu.Nvml;

/// <summary>
/// The hard-coded list of NVML functions this module may call. It is the ONLY place in the source that spells an NVML entry point:
/// <see cref="NvmlApi"/> resolves a function by one of these constants and refuses any other name before it asks the DLL for it, so no
/// <c>nvmlDeviceSet*</c>, <c>nvmlDeviceReset*</c> or anything else that changes the GPU can be reached from this module. The hygiene tests
/// scan the source and the compiled assembly for every "nvml" + capital letter word and fail on one that is not on this list.
/// Every function here reads. The signatures are the ones in NVIDIA's NVML header; the module is written from them.
/// </summary>
internal static class NvmlAllowList
{
    public const string Init = "nvmlInit_v2";
    public const string Shutdown = "nvmlShutdown";
    public const string DeviceCount = "nvmlDeviceGetCount_v2";
    public const string HandleByIndex = "nvmlDeviceGetHandleByIndex_v2";
    public const string DeviceName = "nvmlDeviceGetName";
    public const string Utilization = "nvmlDeviceGetUtilizationRates";
    public const string PowerUsage = "nvmlDeviceGetPowerUsage";
    public const string EnforcedPowerLimit = "nvmlDeviceGetEnforcedPowerLimit";
    public const string ClockInfo = "nvmlDeviceGetClockInfo";
    public const string MemoryInfo = "nvmlDeviceGetMemoryInfo";
    public const string Temperature = "nvmlDeviceGetTemperature";
    public const string PerformanceState = "nvmlDeviceGetPerformanceState";

    /// <summary>The current name (driver 535 and newer) of the call that reports why the clocks are held down.</summary>
    public const string ClocksEventReasons = "nvmlDeviceGetCurrentClocksEventReasons";

    /// <summary>The older name of <see cref="ClocksEventReasons"/>, used only when the newer one is not exported.</summary>
    public const string ClocksThrottleReasons = "nvmlDeviceGetCurrentClocksThrottleReasons";

    public const string ErrorString = "nvmlErrorString";

    /// <summary>Every name the module may resolve.</summary>
    public static readonly IReadOnlyList<string> Names = new[]
    {
        Init, Shutdown, DeviceCount, HandleByIndex, DeviceName, Utilization, PowerUsage, EnforcedPowerLimit,
        ClockInfo, MemoryInfo, Temperature, PerformanceState, ClocksEventReasons, ClocksThrottleReasons, ErrorString,
    };

    /// <summary>True when <paramref name="name"/> is on the list (ordinal, case sensitive).</summary>
    public static bool IsAllowed(string name) => Names.Contains(name, StringComparer.Ordinal);

    /// <summary>Throws unless <paramref name="name"/> is on the list.</summary>
    public static void Require(string name)
    {
        if (!IsAllowed(name)) throw new InvalidOperationException("'" + name + "' is not on the module's NVML read list.");
    }
}
