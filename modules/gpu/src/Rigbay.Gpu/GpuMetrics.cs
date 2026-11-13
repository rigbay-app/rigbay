using Rigbay.Abstractions;

namespace Rigbay.Gpu;

/// <summary>One metric the module publishes: its id, the label and description keys, the unit and the number format.</summary>
internal sealed record GpuMetric(MetricId Id, string LabelKey, MetricUnit Unit, string Format, string? DescriptionKey = null);

/// <summary>
/// The metrics of the module. The load, power and clock use the shared ids of <see cref="WellKnownMetrics"/> (so a recorder or the Dashboard works with
/// any GPU source); the rest start with <c>gpu.</c> and are this module's. The temperature is deliberately NOT <c>gpu.temp</c>: PredatorSense publishes
/// that one from the laptop's firmware, and two sources on one id would alternate in the chart and the recorder (the newest sample wins), so this
/// module's reading has its own id and its own label ("GPU core temperature").
/// </summary>
internal static class GpuMetrics
{
    public static readonly GpuMetric Load = new(WellKnownMetrics.GpuUsage, "metric.usage", MetricUnit.Percent, "0");
    public static readonly GpuMetric MemoryController = new(new MetricId("gpu.memctl.usage"), "metric.memctl", MetricUnit.Percent, "0", "metric.memctl.description");
    public static readonly GpuMetric Power = new(WellKnownMetrics.GpuPower, "metric.power", MetricUnit.Watt, "0.0");
    public static readonly GpuMetric PowerLimit = new(new MetricId("gpu.power.limit"), "metric.power-limit", MetricUnit.Watt, "0");
    public static readonly GpuMetric Clock = new(WellKnownMetrics.GpuClock, "metric.clock", MetricUnit.Megahertz, "0");
    public static readonly GpuMetric MemoryClock = new(new MetricId("gpu.mem.clock"), "metric.mem-clock", MetricUnit.Megahertz, "0");
    public static readonly GpuMetric MemoryUsed = new(new MetricId("gpu.mem.used"), "metric.mem-used", MetricUnit.Bytes, "0");
    public static readonly GpuMetric MemoryTotal = new(new MetricId("gpu.mem.total"), "metric.mem-total", MetricUnit.Bytes, "0");
    public static readonly GpuMetric Temperature = new(new MetricId("gpu.nvml.temp"), "metric.temp", MetricUnit.Celsius, "0", "metric.temp.description");
    public static readonly GpuMetric PerformanceState = new(new MetricId("gpu.pstate"), "metric.pstate", MetricUnit.None, "0", "metric.pstate.description");
    public static readonly GpuMetric ClockLimits = new(new MetricId("gpu.throttle"), "metric.throttle", MetricUnit.None, "0", "metric.throttle.description");

    /// <summary>Every metric, in the order the module describes and polls them.</summary>
    public static readonly IReadOnlyList<GpuMetric> All = new[]
    {
        Load, MemoryController, Power, PowerLimit, Clock, MemoryClock, MemoryUsed, MemoryTotal, Temperature, PerformanceState, ClockLimits,
    };
}
