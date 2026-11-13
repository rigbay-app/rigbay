namespace Rigbay.Gpu;

/// <summary>
/// The bits NVML reports for "why the clocks are held down" (the clocks event reasons, called throttle reasons before driver 535), and the words
/// for them. A bit this module does not know is ignored; none of this is written to the GPU.
/// </summary>
internal static class ClockReasons
{
    public const ulong GpuIdle = 0x1;
    public const ulong ApplicationClocks = 0x2;
    public const ulong SoftwarePowerCap = 0x4;
    public const ulong HardwareSlowdown = 0x8;
    public const ulong SyncBoost = 0x10;
    public const ulong SoftwareThermal = 0x20;
    public const ulong HardwareThermal = 0x40;
    public const ulong HardwarePowerBrake = 0x80;
    public const ulong DisplayClocks = 0x100;

    /// <summary>
    /// The language keys of the reasons in <paramref name="mask"/>, most serious first. The two thermal bits are one reason. "Idle" is the normal
    /// state of an unused GPU, so it is named only when it is the one reason. An empty list: nothing holds the clocks down.
    /// </summary>
    public static IReadOnlyList<string> Keys(ulong mask)
    {
        var keys = new List<string>();
        if ((mask & HardwarePowerBrake) != 0) keys.Add("reason.power-brake");
        if ((mask & HardwareSlowdown) != 0) keys.Add("reason.hw-slowdown");
        if ((mask & (HardwareThermal | SoftwareThermal)) != 0) keys.Add("reason.thermal");
        if ((mask & SoftwarePowerCap) != 0) keys.Add("reason.power-cap");
        if ((mask & SyncBoost) != 0) keys.Add("reason.sync-boost");
        if ((mask & ApplicationClocks) != 0) keys.Add("reason.app-clocks");
        if ((mask & DisplayClocks) != 0) keys.Add("reason.display-clocks");
        if (keys.Count == 0 && (mask & GpuIdle) != 0) keys.Add("reason.idle");
        return keys;
    }
}
