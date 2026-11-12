using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// What sits above the rows of a module page when the page has readings: a strip of up to four tiles and one trend chart. It is worked out from
/// the page model by <see cref="ReadingPlan"/> (the unit of each metric, the module's optional hints) and rebuilt when the plan changes, for example
/// when the first sample of a metric tells its unit. Every live part is registered with the page's <see cref="PageScope"/>, so nothing runs while the
/// page is not on screen.
/// </summary>
sealed class ReadingBlock : UserControl
{
    readonly ModuleEntry _e;
    readonly PageModel _page;
    readonly PageScope _scope;
    PageScope _inner;
    string _signature = "";

    public TileStrip? Tiles { get; private set; }
    public TrendChart? Chart { get; private set; }
    public IReadOnlyList<ReadingTile> TileList { get; private set; } = Array.Empty<ReadingTile>();

    /// <summary>The block for a page, or null when the page has nothing to draw.</summary>
    public static ReadingBlock? For(ModuleEntry e, PageModel page, PageScope scope)
    {
        var plan = Plan(page);
        return plan.IsEmpty ? null : new ReadingBlock(e, page, scope);
    }

    static ReadingPlan Plan(PageModel page) => ReadingPlan.Build(page, id => Host.Core.Metrics.UnitOf(id));

    static string Signature(ReadingPlan plan) =>
        string.Join(",", plan.Tiles.Select(t => t.Row.Metric.Value + ":" + t.Unit)) + "|" +
        string.Join(",", plan.Charts.Select(c => c.Id + ":" + c.Unit + "=" + string.Join("+", c.Series.Select(s => s.Metric.Value))));

    ReadingBlock(ModuleEntry e, PageModel page, PageScope scope)
    {
        _e = e; _page = page; _scope = scope; _inner = scope.CreateChild();
        Margin = new Thickness(0, 12, 0, 8);
        Rebuild();
        ChartKit.FadeIn(this);

        // the unit of a metric is only known after its first sample when the module did not describe it: rebuild when the catalogue says something changed
        void Changed() => Ui.Post(null, () => { if (!_scope.Disposed && Signature(Plan(_page)) != _signature) Rebuild(); });
        Host.Core.Metrics.CatalogChanged += Changed;
        scope.Add(() => Host.Core.Metrics.CatalogChanged -= Changed);
    }

    void Rebuild()
    {
        var plan = Plan(_page);
        _signature = Signature(plan);
        _inner.Dispose();
        _inner = _scope.CreateChild();   // the old tiles and chart let go of their subscriptions and timer
        var stack = new StackPanel { Spacing = 14 };
        var tiles = new List<ReadingTile>();
        Tiles = null; Chart = null;
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
        }
        if (plan.Charts.Count > 0)
        {
            Chart = new TrendChart(_e, plan.Charts, _inner);
            stack.Children.Add(Chart);
        }
        TileList = tiles;
        Content = stack;
    }
}
