using Rigbay.Abstractions;

namespace Rigbay.PredatorSense.Sensors;

/// <summary>The sensors this module reads.</summary>
public enum Sensor
{
    /// <summary>CPU temperature (the Acer firmware sensor, not the Windows thermal zone).</summary>
    CpuTemp,
    /// <summary>System (board) temperature.</summary>
    SystemTemp,
    /// <summary>GPU temperature.</summary>
    GpuTemp,
    /// <summary>CPU fan speed.</summary>
    CpuFan,
    /// <summary>First GPU fan speed.</summary>
    GpuFan1,
    /// <summary>Second GPU fan speed.</summary>
    GpuFan2,
}

/// <summary>What is fixed about one sensor: its <c>GetGamingSysInfo</c> index, its metric id and unit.</summary>
/// <param name="Sensor">The sensor.</param>
/// <param name="Index">Index for <c>1 | (index &lt;&lt; 8)</c>.</param>
/// <param name="Metric">Id on the metrics bus.</param>
/// <param name="Unit">Unit on the metrics bus.</param>
/// <param name="IsFan">True for a fan speed.</param>
public sealed record SensorInfo(Sensor Sensor, int Index, MetricId Metric, MetricUnit Unit, bool IsFan)
{
    /// <summary>All sensors, in the order they are read.</summary>
    public static readonly IReadOnlyList<SensorInfo> All = new[]
    {
        new SensorInfo(Sensor.CpuTemp, 1, new MetricId("cpu.temp"), MetricUnit.Celsius, false),
        new SensorInfo(Sensor.SystemTemp, 3, new MetricId("system.temp"), MetricUnit.Celsius, false),
        new SensorInfo(Sensor.GpuTemp, 10, new MetricId("gpu.temp"), MetricUnit.Celsius, false),
        new SensorInfo(Sensor.CpuFan, 2, new MetricId("fan.cpu.rpm"), MetricUnit.Rpm, true),
        new SensorInfo(Sensor.GpuFan1, 6, new MetricId("fan.gpu1.rpm"), MetricUnit.Rpm, true),
        new SensorInfo(Sensor.GpuFan2, 9, new MetricId("fan.gpu2.rpm"), MetricUnit.Rpm, true),
    };

    /// <summary>The fan sensors.</summary>
    public static IEnumerable<SensorInfo> Fans => All.Where(s => s.IsFan);

    /// <summary>Looks one up.</summary>
    public static SensorInfo Of(Sensor sensor) => All.First(s => s.Sensor == sensor);
}

/// <summary>Why a sensor has no value.</summary>
public enum Unavailable
{
    /// <summary>It has a value.</summary>
    None,
    /// <summary>The call failed (error, timeout, refused).</summary>
    CallFailed,
    /// <summary>The firmware answered a non-zero status.</summary>
    Status,
    /// <summary>Status 0 and value 0 on a part that has never reported anything: the machine does not have it.</summary>
    Absent,
    /// <summary>A value outside anything physical (a temperature of 200, a fan at 60000).</summary>
    OutOfRange,
}

/// <summary>One decoded sensor answer.</summary>
/// <param name="Value">The number, or null when <see cref="Why"/> is not <see cref="Unavailable.None"/>.</param>
/// <param name="Why">Reason for "not available".</param>
/// <param name="Status">The status byte of the answer, or -1 when the call failed.</param>
public readonly record struct Decoded(double? Value, Unavailable Why, int Status)
{
    /// <summary>True when there is a value.</summary>
    public bool HasValue => Value.HasValue;

    /// <summary>True when the firmware answered with status 0 (even if the part is absent).</summary>
    public bool AnswerOk => Status == 0;
}

/// <summary>Turns the raw 64-bit answer of <c>GetGamingSysInfo</c> into a value or "not available".</summary>
public static class SensorDecoder
{
    /// <summary>Highest temperature that counts as a reading, °C. Above this the answer is not a temperature.</summary>
    public const int MaxTemperature = 130;

    /// <summary>Highest fan speed that counts as a reading, rpm.</summary>
    public const int MaxFanRpm = 20000;

    /// <summary>
    /// Status is bits 0 to 7 (0 = ok); the value is <c>(raw &gt;&gt; 8) &amp; 0xFFFF</c>. A non-zero status is "not available".
    /// A value of 0 is "not available" too, because absent hardware answers status 0 with value 0 (FINDINGS 12.2) - except for a fan
    /// that has already reported a non-zero speed in this session (<paramref name="hasReported"/>): its 0 is a real reading
    /// (the fan stopped). A temperature of 0 is never a reading. Never throws.
    /// </summary>
    /// <param name="info">The sensor.</param>
    /// <param name="raw">The raw answer.</param>
    /// <param name="hasReported">True when this sensor has already given a non-zero value.</param>
    public static Decoded Decode(SensorInfo info, ulong raw, bool hasReported)
    {
        int status = (int)(raw & 0xFF);
        if (status != 0) return new Decoded(null, Unavailable.Status, status);
        int value = (int)((raw >> 8) & 0xFFFF);
        // A temperature of 0 is never real. A fan at 0 is real only if that fan has turned before.
        if (value == 0 && (!info.IsFan || !hasReported)) return new Decoded(null, Unavailable.Absent, 0);
        int max = info.IsFan ? MaxFanRpm : MaxTemperature;
        if (value > max) return new Decoded(null, Unavailable.OutOfRange, 0);
        return new Decoded(value, Unavailable.None, 0);
    }
}
