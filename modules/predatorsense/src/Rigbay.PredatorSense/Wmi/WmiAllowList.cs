namespace Rigbay.PredatorSense.Wmi;

/// <summary>
/// The hard-coded list of what this module may send to the firmware. Everything else is refused in code, whatever the
/// caller asks, so a bug above this class cannot reach the hardware with another value. The two writes are the ones
/// decoded in docs/predatorsense/FINDINGS.md 11.2 for this machine's fan layout (1 CPU fan, 2 GPU fans); Auto was run
/// once on the real machine (task B4), Maximum has never been run. The two operation-mode reads (<see cref="MiscReadMethod"/>
/// with <see cref="MiscCurrentMode"/> and <see cref="MiscSupportedModes"/>) were run once each in task B10 (FINDINGS 15.9).
/// There is no operation-mode write: a <c>Set*</c> other than <see cref="FanMethod"/> cannot be named here.
/// </summary>
public static class WmiAllowList
{
    /// <summary>The WMI namespace.</summary>
    public const string Namespace = @"root\WMI";

    /// <summary>The class that carries the sensor read and the fan write.</summary>
    public const string GamingClass = "AcerGamingFunction";

    /// <summary>The only read method.</summary>
    public const string ReadMethod = "GetGamingSysInfo";

    /// <summary>The method of the two operation-mode reads (never a write; the <c>Set</c> twin is not on any list).</summary>
    public const string MiscReadMethod = "GetGamingMiscSetting";

    /// <summary>Input of <see cref="MiscReadMethod"/> that asks for the current operation mode (W3-R1): status in the low byte, mode id in bits 8 to 15.</summary>
    public const uint MiscCurrentMode = 0x0B;

    /// <summary>Input of <see cref="MiscReadMethod"/> that asks which operation modes exist (W3-R2): status in the low byte, a bit mask by mode id in bits 8 to 15.</summary>
    public const uint MiscSupportedModes = 0x0A;

    /// <summary>The only write method.</summary>
    public const string FanMethod = "SetGamingFanBehavior";

    /// <summary>Fan mode Auto for every fan (CPU, GPU 1, GPU 2): the firmware controls the fans.</summary>
    public const ulong FanAuto = 0x01410019;

    /// <summary>Fan mode Maximum for every fan (CPU, GPU 1, GPU 2): full speed, loud.</summary>
    public const ulong FanMax = 0x02820019;

    /// <summary>The approved sensor indexes: 1 CPU temp, 2 CPU fan, 3 system temp, 6 GPU fan 1, 9 GPU fan 2, 10 GPU temp.</summary>
    public static readonly IReadOnlyList<int> ReadIndexes = new[] { 1, 2, 3, 6, 9, 10 };

    /// <summary>True when <paramref name="index"/> is an approved sensor index.</summary>
    public static bool IsAllowedRead(int index) => ReadIndexes.Contains(index);

    /// <summary>True when <paramref name="input"/> is one of the two approved <see cref="MiscReadMethod"/> inputs, 0x0A or 0x0B, and no other.</summary>
    public static bool IsAllowedMiscRead(ulong input) => input == MiscCurrentMode || input == MiscSupportedModes;

    /// <summary>True when <paramref name="value"/> is one of the two approved fan-mode values.</summary>
    public static bool IsAllowedFanWrite(ulong value) => value == FanAuto || value == FanMax;

    /// <summary>The input of a sensor read: <c>1 | (index &lt;&lt; 8)</c>.</summary>
    public static uint ReadInput(int index) => 1u | ((uint)index << 8);

    /// <summary>Throws <see cref="AcerWmiException"/> (Refused) unless the read is approved.</summary>
    public static void RequireRead(int index)
    {
        if (!IsAllowedRead(index))
            throw new AcerWmiException(AcerWmiFailure.Refused, $"Sensor index {index} is not on the module's read list.");
    }

    /// <summary>Throws <see cref="AcerWmiException"/> (Refused) unless the operation-mode read input is approved.</summary>
    public static void RequireMiscRead(ulong input)
    {
        if (!IsAllowedMiscRead(input))
            throw new AcerWmiException(AcerWmiFailure.Refused, $"Input 0x{input:X} of {MiscReadMethod} is not on the module's read list.");
    }

    /// <summary>Throws <see cref="AcerWmiException"/> (Refused) unless the write is approved.</summary>
    public static void RequireFanWrite(ulong value)
    {
        if (!IsAllowedFanWrite(value))
            throw new AcerWmiException(AcerWmiFailure.Refused, $"Fan value 0x{value:X} is not on the module's write list.");
    }
}
