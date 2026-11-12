using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>A reading that becomes a tile at the top of the page.</summary>
/// <param name="Row">The page row it comes from (header, decimals, colour, range).</param>
/// <param name="Unit">Its unit at planning time.</param>
public sealed record TilePlan(ReadingRow Row, MetricUnit Unit);

/// <summary>One trend chart: the readings drawn together on one axis. All series share <see cref="Unit"/>; there is never a second axis.</summary>
/// <param name="Id">Stable id of the group (<c>unit:Celsius</c> or <c>key:chart.power</c>), used to remember the selected chart.</param>
/// <param name="TitleKey">Module-relative key of a title the module gave (<see cref="ReadingRow.ChartGroupKey"/>); null for a group made from the unit.</param>
/// <param name="Unit">The unit of every series.</param>
/// <param name="Series">The readings, in page order (at most <see cref="ReadingPlan.MaxSeries"/>).</param>
public sealed record ChartPlan(string Id, string? TitleKey, MetricUnit Unit, IReadOnlyList<ReadingRow> Series);

/// <summary>
/// What the host draws above the rows of a module page, worked out from the page model and the units of its metrics: up to four tiles
/// and the trend charts. Without hints on the rows (<see cref="ReadingRow.Tile"/>, <see cref="ReadingRow.ChartGroupKey"/>) the choice
/// comes from the unit alone: temperatures together, power together, one tile per kind first. Pure, so it is tested without a window.
/// </summary>
public sealed record ReadingPlan(IReadOnlyList<TilePlan> Tiles, IReadOnlyList<ChartPlan> Charts)
{
    /// <summary>Most tiles on a page.</summary>
    public const int MaxTiles = 4;
    /// <summary>Most series in one chart (the colour order has six places that stay apart).</summary>
    public const int MaxSeries = 6;

    /// <summary>True when there is nothing to draw above the rows.</summary>
    public bool IsEmpty => Tiles.Count == 0 && Charts.Count == 0;

    /// <summary>The order in which kinds of reading are offered: the ones people look at first.</summary>
    public static int UnitRank(MetricUnit unit) => unit switch
    {
        MetricUnit.Celsius => 0,
        MetricUnit.Watt => 1,
        MetricUnit.Percent => 2,
        MetricUnit.Megahertz => 3,
        MetricUnit.Rpm => 4,
        MetricUnit.Volt => 5,
        MetricUnit.Ampere => 6,
        MetricUnit.BytesPerSecond => 7,
        MetricUnit.Bytes => 8,
        MetricUnit.Milliseconds => 9,
        MetricUnit.Seconds => 10,
        MetricUnit.Count => 20,
        _ => 21,
    };

    /// <summary>Units with no kind of their own (a plain number, a count): drawn only when the page has nothing better, or when a module asks.</summary>
    public static bool IsGeneric(MetricUnit unit) => unit is MetricUnit.None or MetricUnit.Count;

    /// <summary>The colour meaning of a reading: the row's own, else the one that goes with the unit (temperature warm, power blue, speed teal, load amber).</summary>
    public static SemanticColor ColorOf(SemanticColor color, MetricUnit unit)
    {
        if (color != SemanticColor.Auto) return color;
        return unit switch
        {
            MetricUnit.Celsius => SemanticColor.Temperature,
            MetricUnit.Watt or MetricUnit.Volt or MetricUnit.Ampere => SemanticColor.Power,
            MetricUnit.Rpm or MetricUnit.Megahertz => SemanticColor.Speed,
            MetricUnit.Percent => SemanticColor.Load,
            _ => SemanticColor.Neutral,
        };
    }

    /// <summary>The empty plan.</summary>
    public static ReadingPlan None { get; } = new(Array.Empty<TilePlan>(), Array.Empty<ChartPlan>());

    /// <param name="page">The module's page.</param>
    /// <param name="unitOf">The unit of a metric (the bus knows it from the descriptor or the last sample); <see cref="MetricUnit.None"/> when unknown.</param>
    public static ReadingPlan Build(PageModel page, Func<MetricId, MetricUnit> unitOf)
    {
        var rows = new List<(ReadingRow Row, MetricUnit Unit)>();
        var seen = new HashSet<MetricId>();
        foreach (var r in page.EnumerateRows().OfType<ReadingRow>())
            if (seen.Add(r.Metric)) rows.Add((r, unitOf(r.Metric)));
        if (rows.Count == 0) return None;

        bool anyKind = rows.Any(x => !IsGeneric(x.Unit));
        return new ReadingPlan(PickTiles(page, rows, anyKind), PickCharts(rows, anyKind));
    }

    private static IReadOnlyList<TilePlan> PickTiles(PageModel page, List<(ReadingRow Row, MetricUnit Unit)> rows, bool anyKind)
    {
        var chosen = new List<TilePlan>();
        void Take((ReadingRow Row, MetricUnit Unit) x)
        {
            if (chosen.Count < MaxTiles && chosen.All(c => c.Row.Metric != x.Row.Metric)) chosen.Add(new TilePlan(x.Row, x.Unit));
        }

        foreach (var x in rows.Where(x => x.Row.Tile == TileHint.Always)) Take(x);
        // what the module put on its dashboard bay is what it thinks matters most
        if (page.Summary?.Readings is { } summary)
            foreach (var id in summary)
                if (rows.FirstOrDefault(x => x.Row.Metric == id) is { Row: not null } hit && hit.Row.Tile != TileHint.Never && (!IsGeneric(hit.Unit) || !anyKind)) Take(hit);

        var candidates = rows.Where(x => x.Row.Tile == TileHint.Auto && (!IsGeneric(x.Unit) || !anyKind)).ToList();
        // first one per kind (temperature, power, load, clock ...), so four tiles are four different things; then the rest
        foreach (var x in candidates.GroupBy(c => c.Unit).OrderBy(g => UnitRank(g.Key)).Select(g => g.First())) Take(x);
        foreach (var x in candidates) Take(x);
        return chosen;
    }

    private static IReadOnlyList<ChartPlan> PickCharts(List<(ReadingRow Row, MetricUnit Unit)> rows, bool anyKind)
    {
        var groups = new List<(string Id, string? Title, MetricUnit Unit, List<ReadingRow> Series, int Rank)>();
        foreach (var (row, unit) in rows)
        {
            if (row.ChartGroupKey is { Length: 0 }) continue;
            string id; string? title;
            if (row.ChartGroupKey is { } key) { id = "key:" + key; title = key; }
            else
            {
                if (IsGeneric(unit) && anyKind) continue;
                id = "unit:" + unit; title = null;
            }
            int index = groups.FindIndex(g => g.Id == id);
            if (index < 0) { groups.Add((id, title, unit, new List<ReadingRow>(), title is null ? 100 + UnitRank(unit) : 0)); index = groups.Count - 1; }
            // one axis: a series in another unit than the group's first is left out of the chart (it keeps its row and its sparkline)
            if (groups[index].Unit == unit && groups[index].Series.Count < MaxSeries) groups[index].Series.Add(row);
        }
        return groups
            .Select((g, i) => (g, i))
            .OrderBy(x => x.g.Rank).ThenBy(x => x.i)
            .Select(x => new ChartPlan(x.g.Id, x.g.Title, x.g.Unit, x.g.Series))
            .ToList();
    }
}
