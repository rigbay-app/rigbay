namespace Rigbay.Gpu.Nvml;

/// <summary>NVML's return codes (<c>nvmlReturn_t</c>). Any other number is possible; it is carried as is.</summary>
internal enum NvmlReturn
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    AlreadyInitialized = 5,
    NotFound = 6,
    InsufficientSize = 7,
    InsufficientPower = 8,
    DriverNotLoaded = 9,
    Timeout = 10,
    IrqIssue = 11,
    LibraryNotFound = 12,
    FunctionNotFound = 13,
    CorruptedInfoRom = 14,
    GpuIsLost = 15,
    ResetRequired = 16,
    OperatingSystem = 17,
    LibRmVersionMismatch = 18,
    InUse = 19,
    Memory = 20,
    NoData = 21,
    Unknown = 999,
}

/// <summary>The two clocks this module reads (<c>nvmlClockType_t</c>: graphics 0, memory 2).</summary>
internal enum NvmlClock
{
    Graphics = 0,
    Memory = 2,
}

/// <summary>
/// What the module can ask of NVML: one method per function on <see cref="NvmlAllowList"/>, all of them reads. There is no method that
/// sets or resets anything, so nothing that uses this interface can change the GPU. A device is an opaque handle from <see cref="GetHandle"/>.
/// </summary>
internal interface INvml
{
    /// <summary>Initialises NVML. NVML counts initialisations: every successful call needs one <see cref="Shutdown"/>.</summary>
    NvmlReturn Init();

    /// <summary>Shuts NVML down (one level).</summary>
    NvmlReturn Shutdown();

    /// <summary>The number of NVIDIA GPUs.</summary>
    NvmlReturn GetCount(out uint count);

    /// <summary>The handle of the GPU at <paramref name="index"/>.</summary>
    NvmlReturn GetHandle(uint index, out nint device);

    /// <summary>The product name of the GPU.</summary>
    NvmlReturn GetName(nint device, out string name);

    /// <summary>The GPU's and the memory controller's busy time in percent over the last sample period.</summary>
    NvmlReturn GetUtilization(nint device, out uint gpuPercent, out uint memoryControllerPercent);

    /// <summary>Board power in milliwatts.</summary>
    NvmlReturn GetPowerUsage(nint device, out uint milliwatts);

    /// <summary>The power limit in force, in milliwatts.</summary>
    NvmlReturn GetEnforcedPowerLimit(nint device, out uint milliwatts);

    /// <summary>A clock in MHz.</summary>
    NvmlReturn GetClock(nint device, NvmlClock clock, out uint megahertz);

    /// <summary>Total and used device memory in bytes.</summary>
    NvmlReturn GetMemory(nint device, out ulong totalBytes, out ulong usedBytes);

    /// <summary>The GPU core temperature in degrees Celsius.</summary>
    NvmlReturn GetTemperature(nint device, out uint celsius);

    /// <summary>The performance state: 0 (fastest) to 15; 32 means unknown.</summary>
    NvmlReturn GetPerformanceState(nint device, out int pstate);

    /// <summary>
    /// Why the clocks are held down, as a bit mask: the call with the current name, or the older one when the driver does not export the new
    /// one. <see cref="NvmlReturn.NotSupported"/> when the driver exports neither.
    /// </summary>
    NvmlReturn GetClockEventReasons(nint device, out ulong reasons);

    /// <summary>NVML's own words for a return code (never throws; a plain fallback text when the function is missing).</summary>
    string ErrorString(NvmlReturn code);
}
