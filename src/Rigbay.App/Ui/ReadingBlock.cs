using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// What sits at the top of a module page when the page has readings: a strip of up to four tiles and one trend chart under them. The tiles are
/// the chart's tabs (Task Manager's Performance page): the selected tile's reading is the chart's line, so each reading is shown once as a number
/// and once over time, never a third time as a row. It is worked out from the page model by <see cref="ReadingPlan"/> (the unit of each metric, the
/// module's optional hints) and rebuilt when the plan changes: a tile appears once its reading has a value and goes when the module takes it back
/// (a feature that is off shows no "Not available" tiles above the switch that would turn it on). Every live part is registered with the page's
/// <see cref="PageScope"/>, so nothing runs while the page is not on screen.
/// </summary>
sealed class ReadingBlock : UserControl
{
    static readonly Dictionary<string, MetricId> RememberedTile = new();

    readonly ModuleEntry _e;
    readonly PageModel _page;
    readonly PageScope _scope;
    PageScope _inner;
    string _signature = "";
    ReadingPlan _plan = ReadingPlan.None;
    bool _choosing;   // true while the block itself picks the tile (a rebuild): only the user's pick is remembered

    public TileStrip? Tiles { get; private set; }
    public TrendChart? Chart { get; private set; }
    public IReadOnlyList<ReadingTile> TileList { get; private set; } = Array.Empty<ReadingTile>();
    /// <summary>The plan the block was last built from (live tiles only).</summary>
    public ReadingPlan Plan => _plan;

    /// <summary>True when the block shows something: a tile, or a chart with a line.</summary>
    public bool HasContent => TileList.Count > 0 || Chart?.HasData == true;
    /// <summary>Raised when <see cref="HasContent"/> may have changed (the page hides an empty card).</summary>
    public event Action? ContentChanged;

    /// <summary>The block for a page, or null when the page has no reading that could ever be drawn.</summary>
    public static ReadingBlock? For(ModuleEntry e, PageModel page, PageScope scope)
    {
        var plan = ReadingPlan.Build(page, id => Host.Core.Metrics.UnitOf(id));
        return plan.IsEmpty ? null : new ReadingBlock(e, page, scope);
    }

    /// <summary>The plan of the page now: tiles only for readings that have a value.</summary>
    static ReadingPlan LivePlan(PageModel page) =>
        ReadingPlan.Build(page, id => Host.Core.Metrics.UnitOf(id), id => Host.Core.Metrics.TryGetLatest(id, out _));

    static string Signature(ReadingPlan plan) =>
        string.Join(",", plan.Tiles.Select(t => t.Row.Metric.Value + ":" + t.Unit)) + "|" +
        string.Join(",", plan.Charts.Select(c => c.Id + ":" + c.Unit + "=" + string.Join("+", c.Series.Select(s => s.Metric.Value)) + "/" + string.Join("+", c.References.Select(s => s.Metric.Value))));

    ReadingBlock(ModuleEntry e, PageModel page, PageScope scope)
    {
        _e = e; _page = page; _scope = scope; _inner = scope.CreateChild();
        Margin = new Thickness(0, 12, 0, 8);
        Rebuild();
        ChartKit.FadeIn(this);

        // a reading that comes, goes (withdrawn) or tells its unit changes the catalogue: rebuild when the plan says something changed
        void Changed() => Ui.Post(null, () => { if (!_scope.Disposed && Signature(LivePlan(_page)) != _signature) Rebuild(); });
        Host.Core.Metrics.CatalogChanged += Changed;
        scope.Add(() => Host.Core.Metrics.CatalogChanged -= Changed);
    }

    void Rebuild()
    {
        var plan = LivePlan(_page);
        _plan = plan;
        _signature = Signature(plan);
        _inner.Dispose();
        _inner = _scope.CreateChild();   // the old tiles and chart let go of their subscriptions and timer
        var stack = new StackPanel { Spacing = 14 };
        var tiles = new List<ReadingTile>();
        Tiles = null; Chart = null;

        var chart = plan.Charts.Count > 0 ? new TrendChart(_e, _inner) : null;
        if (plan.Tiles.Count > 0)
        {
            var strip = new TileStrip();
            foreach (var t in plan.Tiles)
            {
                var tile = new ReadingTile(_e, t.Row, t.Unit, _inner);
                tiles.Add(tile);
                strip.Children.Add(tile);
            }
            Tiles = strip;
            stack.Children.Add(strip);
            strip.SelectionChanged += i =>
            {
                if (strip.SelectedTile is not { } tile) return;
                if (!_choosing) RememberedTile[_e.Id] = tile.Row.Metric;
                ShowChartFor(tile.Row);
            };
        }
        if (chart != null)
        {
            Chart = chart;
            chart.TileRequested += i => Tiles?.Select(i);
            chart.HasDataChanged += () => ContentChanged?.Invoke();
            stack.Children.Add(chart);
        }
        TileList = tiles;
        Content = stack;

        if (Tiles is { } s)
        {
            // the tile the user picked last on this module's page, else the first
            int at = RememberedTile.TryGetValue(_e.Id, out var m) ? tiles.FindIndex(t => t.Row.Metric == m) : -1;
            _choosing = true;
            try { s.Select(at < 0 ? 0 : at); }
            finally { _choosing = false; }
        }
        else if (chart != null) chart.Show(plan.Charts[0], null);   // no tile yet (or none wanted): the first group, every line
        ContentChanged?.Invoke();
    }

    /// <summary>The chart draws the reading of <paramref name="row"/> with the limits of its group; a reading kept out of every chart hides it.</summary>
    void ShowChartFor(ReadingRow row)
    {
        if (Chart is not { } chart) return;
        var plan = _plan.Charts.FirstOrDefault(c => c.Series.Contains(row));
        if (plan == null) chart.Clear();
        else chart.Show(plan, row);
    }
}
