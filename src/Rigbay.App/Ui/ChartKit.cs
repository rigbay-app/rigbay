using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rigbay.Abstractions;
using Rigbay.Core;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rigbay.App;

/// <summary>Small helpers shared by the reading visuals (sparkline, tile, trend chart): hue names, system settings, point lists.</summary>
static class ChartKit
{
    /// <summary>The hue family of a reading (a suffix of the <c>RbLine*</c>, <c>RbFill*</c>, <c>RbDot*</c> and <c>RbKey*</c> styles).</summary>
    public static string Hue(SemanticColor color, MetricUnit unit) => ReadingPlan.ColorOf(color, unit) switch
    {
        SemanticColor.Temperature => "Temperature",
        SemanticColor.Power => "Power",
        SemanticColor.Speed => "Speed",
        SemanticColor.Load => "Load",
        _ => "Neutral",
    };

    /// <summary>The hue of series number <paramref name="index"/> (0-based) in a chart with <paramref name="count"/> lines: one line wears its kind's hue (as its tile does), several lines wear the validated categorical order.</summary>
    public static string SeriesHue(int index, int count, string kindHue) => count <= 1 ? kindHue : "S" + (Math.Min(index, 5) + 1);

    public static Style Line(string hue) => Ui.Style("RbLine" + hue);
    public static Style Fill(string hue) => Ui.Style("RbFill" + hue);
    public static Style Dot(string hue) => Ui.Style("RbDot" + hue);
    public static Style Key(string hue) => Ui.Style("RbKey" + hue);

    static Windows.UI.ViewManagement.AccessibilitySettings? _access;
    static Windows.UI.ViewManagement.UISettings? _ui;

    /// <summary>True under a Windows high contrast theme, where every hue is the same text colour and lines are told apart by their dashes.</summary>
    public static bool HighContrast
    {
        get { try { return (_access ??= new Windows.UI.ViewManagement.AccessibilitySettings()).HighContrast; } catch { return false; } }
    }

    /// <summary>The Windows "Animation effects" setting; when it is off nothing here moves.</summary>
    public static bool AnimationsEnabled
    {
        get { try { return (_ui ??= new Windows.UI.ViewManagement.UISettings()).AnimationsEnabled; } catch { return true; } }
    }

    /// <summary>Dash pattern for series <paramref name="index"/> under high contrast; null for a solid line (the first).</summary>
    public static DoubleCollection? Dashes(int index) => index switch
    {
        1 => new DoubleCollection { 5, 3 },
        2 => new DoubleCollection { 1.5, 3 },
        3 => new DoubleCollection { 6, 3, 1.5, 3 },
        4 => new DoubleCollection { 3, 3 },
        5 => new DoubleCollection { 8, 4 },
        _ => null,
    };

    public static PointCollection Points(IEnumerable<Point> points)
    {
        var c = new PointCollection();
        foreach (var p in points) c.Add(p);
        return c;
    }

    /// <summary>A closed shape under a line, down to <paramref name="baseline"/>.</summary>
    public static Path Area(IReadOnlyList<Point> line, double baseline, string hue)
    {
        var fig = new PathFigure { StartPoint = new Point(line[0].X, baseline), IsClosed = true };
        foreach (var p in line) fig.Segments.Add(new LineSegment { Point = p });
        fig.Segments.Add(new LineSegment { Point = new Point(line[^1].X, baseline) });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return new Path { Data = geo, Style = Fill(hue), IsHitTestVisible = false };
    }

    /// <summary>A short text such as "−45 s" or "−4 min" for a tick that is <paramref name="seconds"/> before now; "now" for 0.</summary>
    public static string AgoText(int seconds)
    {
        if (seconds <= 0) return Host.T("chart.now");
        return seconds < 60 ? Host.F("chart.sec", seconds) : Host.F("chart.min", seconds / 60);
    }

    /// <summary>The fade the page's visuals come in with, unless the user turned animation effects off.</summary>
    public static void FadeIn(UIElement element, int ms = 160)
    {
        if (!AnimationsEnabled) return;
        element.Opacity = 0;
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) => element.Opacity = 1;
        sb.Begin();
    }
}

/// <summary>Lays children out left to right and starts a new row where the next one no longer fits (a legend that wraps on a narrow window).</summary>
sealed class FlowPanel : Panel
{
    public double HorizontalGap { get; set; } = 16;
    public double VerticalGap { get; set; } = 2;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, rowH = 0, total = 0, maxW = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > available.Width) { total += rowH + VerticalGap; maxW = Math.Max(maxW, x - HorizontalGap); x = 0; rowH = 0; }
            x += d.Width + HorizontalGap; rowH = Math.Max(rowH, d.Height);
        }
        if (Children.Count > 0) { total += rowH; maxW = Math.Max(maxW, x - HorizontalGap); }
        return new Size(double.IsInfinity(available.Width) ? maxW : Math.Min(maxW, available.Width), total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, rowH = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > final.Width) { y += rowH + VerticalGap; x = 0; rowH = 0; }
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width + HorizontalGap; rowH = Math.Max(rowH, d.Height);
        }
        return final;
    }
}

/// <summary>Equal-width tiles in rows: as many columns as fit (at least <see cref="MinTileWidth"/> each), but never a lone tile on the last row (four tiles are 4, 2 or 1 wide).</summary>
sealed class TileStrip : Panel
{
    public const double MinTileWidth = 156;
    public double Gap { get; set; } = 8;

    /// <summary>How many columns a strip of <paramref name="count"/> tiles gets in <paramref name="width"/>.</summary>
    public static int Columns(int count, double width, double gap = 8)
    {
        if (count <= 1) return 1;
        int fit = double.IsInfinity(width) ? count : (int)Math.Floor((width + gap) / (MinTileWidth + gap));
        int cols = Math.Clamp(fit, 1, count);
        while (cols > 1 && count % cols != 0) cols--;
        return cols;
    }

    protected override Size MeasureOverride(Size available)
    {
        int n = Children.Count;
        if (n == 0) return new Size(0, 0);
        int cols = Columns(n, available.Width, Gap);
        double w = double.IsInfinity(available.Width) ? MinTileWidth : (available.Width - Gap * (cols - 1)) / cols;
        double rowH = 0, total = 0;
        for (int i = 0; i < n; i++)
        {
            Children[i].Measure(new Size(w, double.PositiveInfinity));
            rowH = Math.Max(rowH, Children[i].DesiredSize.Height);
            if ((i + 1) % cols == 0 || i == n - 1) { total += rowH + (i == n - 1 ? 0 : Gap); rowH = 0; }
        }
        return new Size(double.IsInfinity(available.Width) ? w * cols + Gap * (cols - 1) : available.Width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int n = Children.Count;
        if (n == 0) return final;
        int cols = Columns(n, final.Width, Gap);
        double w = (final.Width - Gap * (cols - 1)) / cols;
        // every row is as tall as its tallest tile, so a row of tiles lines up
        double y = 0;
        for (int start = 0; start < n; start += cols)
        {
            double rowH = 0;
            for (int i = start; i < Math.Min(n, start + cols); i++) rowH = Math.Max(rowH, Children[i].DesiredSize.Height);
            for (int i = start; i < Math.Min(n, start + cols); i++)
                Children[i].Arrange(new Rect((i - start) * (w + Gap), y, w, rowH));
            y += rowH + Gap;
        }
        return final;
    }
}
