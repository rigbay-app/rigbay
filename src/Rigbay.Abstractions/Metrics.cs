using System.Text.RegularExpressions;

namespace Rigbay.Abstractions;

/// <summary>
/// Id of a metric: lowercase dotted segments of <c>[a-z0-9_-]</c>, starting with a letter, at most 64 characters
/// (<c>cpu.temp</c>, <c>gpu.fan.rpm</c>). Ids are a shared vocabulary so a second source of the same reading can
/// publish the same id; module-specific ids start with the module id.
/// </summary>
public readonly partial record struct MetricId
{
    private static readonly Regex Pattern = IdPattern();

    /// <summary>Creates an id and checks its form.</summary>
    /// <param name="value">The id text.</param>
    /// <exception cref="ArgumentException">The text is not a valid id.</exception>
    public MetricId(string value)
    {
        if (!IsValid(value)) throw new ArgumentException($"'{value}' is not a valid metric id (lowercase dotted segments of a-z 0-9 _ -, at most 64 characters).", nameof(value));
        _value = value;
    }

    private readonly string? _value;

    /// <summary>The id text; empty for <c>default(MetricId)</c>.</summary>
    public string Value => _value ?? "";

    /// <summary>True when <paramref name="value"/> has the form of an id.</summary>
    /// <param name="value">The text to test.</param>
    public static bool IsValid(string? value) => value is { Length: > 0 and <= 64 } && Pattern.IsMatch(value);

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z][a-z0-9_-]*(\.[a-z0-9_-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}

/// <summary>The unit of a metric. The list is closed so that every consumer can format every unit.</summary>
public enum MetricUnit
{
    /// <summary>A plain number.</summary>
    None,
    /// <summary>Degrees Celsius.</summary>
    Celsius,
    /// <summary>Watts.</summary>
    Watt,
    /// <summary>Revolutions per minute.</summary>
    Rpm,
    /// <summary>Percent, 0 to 100.</summary>
    Percent,
    /// <summary>Megahertz.</summary>
    Megahertz,
    /// <summary>Volts.</summary>
    Volt,
    /// <summary>Amperes.</summary>
    Ampere,
    /// <summary>Bytes.</summary>
    Bytes,
    /// <summary>Bytes per second.</summary>
    BytesPerSecond,
    /// <summary>Milliseconds.</summary>
    Milliseconds,
    /// <summary>Seconds.</summary>
    Seconds,
    /// <summary>A count of things.</summary>
    Count,
}

/// <summary>Display helpers for <see cref="MetricUnit"/>.</summary>
public static class MetricUnits
{
    /// <summary>The short symbol shown after a number (<c>°C</c>, <c>W</c>, <c>rpm</c>). Symbols are not localised.</summary>
    /// <param name="unit">The unit.</param>
    public static string Symbol(this MetricUnit unit) => unit switch
    {
        MetricUnit.Celsius => "°C",
        MetricUnit.Watt => "W",
        MetricUnit.Rpm => "rpm",
        MetricUnit.Percent => "%",
        MetricUnit.Megahertz => "MHz",
        MetricUnit.Volt => "V",
        MetricUnit.Ampere => "A",
        MetricUnit.Bytes => "B",
        MetricUnit.BytesPerSecond => "B/s",
        MetricUnit.Milliseconds => "ms",
        MetricUnit.Seconds => "s",
        _ => "",
    };
}

/// <summary>One reading. The host sets <see cref="Timestamp"/> and <see cref="SourceModule"/>; a module cannot choose them.</summary>
/// <param name="Id">What was measured.</param>
/// <param name="Value">The number (never NaN or infinite).</param>
/// <param name="Unit">Its unit.</param>
/// <param name="Timestamp">When it was published.</param>
/// <param name="SourceModule">Id of the module that published it (<c>host</c> for the host itself).</param>
public sealed record MetricSample(MetricId Id, double Value, MetricUnit Unit, DateTimeOffset Timestamp, string SourceModule);

/// <summary>
/// The bus on which modules publish readings and subscribe to those of others. It caches the latest sample of
/// every id. Delivery is synchronous on the publisher's thread; each subscriber runs in its own module's fault
/// boundary. NaN and infinite values are dropped and logged. If two modules publish the same id the newest sample
/// wins and <see cref="MetricSample.SourceModule"/> says who.
/// </summary>
public interface IMetricsBus
{
    /// <summary>Publishes a reading from this module.</summary>
    /// <param name="id">What was measured.</param>
    /// <param name="value">The number.</param>
    /// <param name="unit">Its unit.</param>
    void Publish(MetricId id, double value, MetricUnit unit);

    /// <summary>Receives every sample. Dispose the result to stop.</summary>
    /// <param name="handler">Called for each sample; keep it short.</param>
    IDisposable Subscribe(Action<MetricSample> handler);

    /// <summary>Receives the samples of one id. Dispose the result to stop.</summary>
    /// <param name="id">The id to follow.</param>
    /// <param name="handler">Called for each sample; keep it short.</param>
    IDisposable Subscribe(MetricId id, Action<MetricSample> handler);

    /// <summary>Reads the latest cached sample of an id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="sample">The sample when the result is true.</param>
    bool TryGetLatest(MetricId id, out MetricSample sample);

    /// <summary>The latest sample of every id seen so far.</summary>
    IReadOnlyList<MetricSample> GetLatest();
}

/// <summary>Ids agreed across modules, so a recorder or the dashboard works with any source.</summary>
public static class WellKnownMetrics
{
    /// <summary>CPU package temperature, °C.</summary>
    public static readonly MetricId CpuTemp = new("cpu.temp");
    /// <summary>CPU package power, W.</summary>
    public static readonly MetricId CpuPower = new("cpu.power");
    /// <summary>CPU usage, %.</summary>
    public static readonly MetricId CpuUsage = new("cpu.usage");
    /// <summary>CPU clock, MHz.</summary>
    public static readonly MetricId CpuClock = new("cpu.clock");
    /// <summary>CPU fan speed, rpm.</summary>
    public static readonly MetricId CpuFan = new("cpu.fan");
    /// <summary>Sustained power limit (PL1), W.</summary>
    public static readonly MetricId CpuPl1 = new("cpu.power.pl1");
    /// <summary>Burst power limit (PL2), W.</summary>
    public static readonly MetricId CpuPl2 = new("cpu.power.pl2");
    /// <summary>GPU temperature, °C.</summary>
    public static readonly MetricId GpuTemp = new("gpu.temp");
    /// <summary>GPU board power, W.</summary>
    public static readonly MetricId GpuPower = new("gpu.power");
    /// <summary>GPU usage, %.</summary>
    public static readonly MetricId GpuUsage = new("gpu.usage");
    /// <summary>GPU clock, MHz.</summary>
    public static readonly MetricId GpuClock = new("gpu.clock");
    /// <summary>GPU fan speed, rpm.</summary>
    public static readonly MetricId GpuFan = new("gpu.fan");
    /// <summary>Memory in use, %.</summary>
    public static readonly MetricId MemoryUsage = new("memory.usage");
    /// <summary>Battery charge level, %.</summary>
    public static readonly MetricId BatteryLevel = new("battery.level");
    /// <summary>Battery charge (positive) or discharge (negative) rate, W.</summary>
    public static readonly MetricId BatteryRate = new("battery.rate");
}
