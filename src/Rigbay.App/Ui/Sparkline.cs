using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Rigbay.Abstractions;
using Rigbay.Core;
using Windows.Foundation;

namespace Rigbay.App;

/// <summary>
/// A hairline trend of one metric from the host's history: no axes, no numbers, the metric's hue, the newest reading marked with a dot.
/// The scale follows the data but never fills the height with noise (<see cref="ReadingScale.Fit"/>), and the time runs from the oldest point
/// the history has (at least 30 s) to now, up to <c>MaxSpan</c>, so a line that was just born does not hide in a corner. A hole in the data (a module that
/// slept) is a gap in the line. Draws nothing without two points, and never while its page is inactive (the page's timer is off).
/// </summary>
sealed class Sparkline : Canvas
{
    readonly MetricId _metric;
    readonly SemanticColor _color;
    readonly bool _area;
    readonly double? _rangeMin, _rangeMax;
    string _hue = "";
    int _points;
    bool _live;

    /// <summary>The longest stretch of history shown.</summary>
    public TimeSpan MaxSpan { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many points the last draw used (the self-test reads it).</summary>
    public int PointsDrawn => _points;

    /// <summary>True when the last draw had a live end (a reading newer than a gap).</summary>
    public bool Live => _live;

    public MetricId Metric => _metric;

    public Sparkline(MetricId metric, SemanticColor color, double width, double height, bool area = false, double? rangeMin = null, double? rangeMax = null)
    {
        _metric = metric; _color = color; _area = area; _rangeMin = rangeMin; _rangeMax = rangeMax;
        Height = height;
        if (width > 0) Width = width; else HorizontalAlignment = HorizontalAlignment.Stretch;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>At most one point per 1.5 px: a line cannot show more, and the interop cost of a point is paid on every redraw.</summary>
    static List<Point> Thin(List<Point> pts)
    {
        if (pts.Count < 4) return pts;
        var result = new List<Point>(pts.Count);
        double bucket = double.NaN, sum = 0, x = 0; int n = 0;
        foreach (var p in pts)
        {
            double b = Math.Floor(p.X / 1.5);
            if (b != bucket && n > 0) { result.Add(new Point(x / n, sum / n)); sum = 0; x = 0; n = 0; }
            bucket = b; sum += p.Y; x += p.X; n++;
        }
        if (n > 0) result.Add(new Point(x / n, sum / n));
        return result;
    }

    /// <summary>Draws the history now. Cheap: at most 300 points.</summary>
    public void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        Children.Clear();
        _points = 0; _live = false;
        if (w < 8 || h < 6) return;
        var bus = Host.Core.Metrics;
        var history = bus.History;
        var unit = bus.UnitOf(_metric);
        var now = history.Now;
        var all = history.Get(_metric, MaxSpan);
        if (all.Count < 2) return;

        var span = TimeSpan.FromSeconds(Math.Clamp((now - all[0].Time).TotalSeconds, 30, MaxSpan.TotalSeconds));
        var hue = ChartKit.Hue(_color, unit);
        _hue = hue;

        double min = double.MaxValue, max = double.MinValue;
        foreach (var p in all) { min = Math.Min(min, p.Value); max = Math.Max(max, p.Value); }
        var (lo, hi) = _rangeMin is { } a && _rangeMax is { } b ? (a, b) : ReadingScale.Fit(min, max, unit);
        if (hi <= lo) hi = lo + 1;

        const double padX = 3, padY = 3;
        double pw = w - 2 * padX, ph = h - 2 * padY;
        Point Map(HistoryPoint p) => new(
            padX + pw - Math.Clamp((now - p.Time).TotalSeconds / span.TotalSeconds, 0, 1) * pw,
            padY + ph - ReadingScale.Fraction(p.Value, lo, hi) * ph);

        var gap = MetricHistory.GapFor(all);
        foreach (var run in MetricHistory.Segments(all, gap))
        {
            if (run.Count < 2) continue;
            var pts = Thin(run.Select(Map).ToList());
            if (_area) Children.Add(ChartKit.Area(pts, h - padY + 1, hue));
            var line = new Polyline { Points = ChartKit.Points(pts), Style = ChartKit.Line(hue), StrokeThickness = 1.5, IsHitTestVisible = false };
            Children.Add(line);
        }

        var last = all[^1];
        _live = now - last.Time <= gap;
        if (_live)
        {
            var m = Map(last);
            var dot = new Ellipse { Width = 6, Height = 6, Style = ChartKit.Dot(hue), StrokeThickness = 1, IsHitTestVisible = false };
            SetLeft(dot, m.X - 3); SetTop(dot, m.Y - 3);
            Children.Add(dot);
        }
        _points = all.Count;
    }
}
