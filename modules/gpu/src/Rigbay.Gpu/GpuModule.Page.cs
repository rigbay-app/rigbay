using Rigbay.Abstractions;

namespace Rigbay.Gpu;

public sealed partial class GpuModule
{
    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host)
    {
        // Four tiles (load, power, temperature, memory in use); the rest are in the charts and in "All readings". A limit is a dashed reference line.
        var readings = new List<Row>
        {
            Reading(GpuMetrics.Load, TileHint.Always, "chart.load", min: 0, max: 100),
            Reading(GpuMetrics.MemoryController, TileHint.Never, "chart.load", min: 0, max: 100),
            Reading(GpuMetrics.Power, TileHint.Always, "chart.power", min: 0),
            Reading(GpuMetrics.PowerLimit, TileHint.Never, "chart.power", min: 0, role: ChartRole.Reference),
            Reading(GpuMetrics.Temperature, TileHint.Always, "chart.temperature", min: 0, max: 100),
            Reading(GpuMetrics.Clock, TileHint.Never, "chart.clocks", min: 0),
            Reading(GpuMetrics.MemoryClock, TileHint.Never, "chart.clocks", min: 0),
            Reading(GpuMetrics.MemoryUsed, TileHint.Always, "chart.memory", min: 0),
            Reading(GpuMetrics.MemoryTotal, TileHint.Never, "chart.memory", min: 0, role: ChartRole.Reference),
        };

        var state = new List<Row>
        {
            new ValueRow("row.device.header", _deviceText),
            new ValueRow("row.limit.header", _limitText) { HelpKey = "row.limit.help" },
        };

        var settings = new List<Row>
        {
            new NumberRow("row.poll.header", _pollSeconds, GpuSettings.MinPollSeconds, GpuSettings.MaxPollSeconds, 1)
            {
                DescriptionKey = "row.poll.description",
                HelpKey = "row.poll.help",
                Unit = MetricUnit.Seconds,
            },
        };

        return new PageModel
        {
            Groups = new[]
            {
                new RowGroup("group.readings", readings),
                new RowGroup("group.state", state),
                new RowGroup("group.settings", settings),
            },
            Summary = new PageSummary(_stateLine, new[] { GpuMetrics.Load.Id, GpuMetrics.Power.Id }),
        };
    }

    private static ReadingRow Reading(GpuMetric metric, TileHint tile, string chartKey, double? min = null, double? max = null, ChartRole role = ChartRole.Series) =>
        new(metric.LabelKey, metric.Id)
        {
            Decimals = 0,
            Tile = tile,
            ChartGroupKey = chartKey,
            RangeMin = min,
            RangeMax = max,
            ChartRole = role,
        };
}
