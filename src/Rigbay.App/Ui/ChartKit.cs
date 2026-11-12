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

    /// <summary>The text of a time-axis label (<see cref="TimeAxis"/>): "now", "-45 s" or "-4 min" in the current language.</summary>
    public static string TickText(TimeTick tick) => tick.Unit switch
    {
        TimeTickUnit.Now => Host.T("chart.now"),
        TimeTickUnit.Seconds => Host.F("chart.sec", tick.Value),
        _ => Host.F("chart.min", tick.Value),
    };

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

/// <summary>
/// The tiles of a module page, which are also the chart's tabs (Task Manager's Performance page): equal-width tiles in rows, as many columns as fit
/// (at least <see cref="MinTileWidth"/> each) but never a lone tile on the last row (four tiles are 4, 2 or 1 wide). One tile is selected; the chart
/// below draws its reading. It is one tab stop: Tab lands on the selected tile, the arrow keys, Home and End move the selection. For a screen reader
/// it is a list with one selected item, and each tile's name carries its value and unit.
/// </summary>
sealed class TileStrip : Panel
{
    public const double MinTileWidth = 156;
    public double Gap { get; set; } = 8;

    int _selected = -1;

    /// <summary>The selected tile's index, or -1.</summary>
    public int SelectedIndex => _selected;
    public ReadingTile? SelectedTile => _selected >= 0 && _selected < Children.Count ? Children[_selected] as ReadingTile : null;
    /// <summary>Raised after the selection changed (a click, a key, or <see cref="Select"/>), with the new index.</summary>
    public event Action<int>? SelectionChanged;

    public TileStrip()
    {
        TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Once;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, Host.T("chart.tiles"));
        KeyDown += OnKey;
        GettingFocus += (_, a) =>
        {
            // coming in from outside (Tab, Shift+Tab): land on the selected tile, not on the first or the last
            if (SelectedTile is { } sel && a.NewFocusedElement is ReadingTile t && t != sel && !IsInside(a.OldFocusedElement))
                a.TrySetNewFocusedElement(sel);
        };
    }

    bool IsInside(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d)) if (ReferenceEquals(d, this)) return true;
        return false;
    }

    /// <summary>Selects tile <paramref name="index"/>; <paramref name="focus"/> also moves the keyboard focus to it.</summary>
    public void Select(int index, bool focus = false)
    {
        if (index < 0 || index >= Children.Count) return;
        bool changed = index != _selected;
        _selected = index;
        for (int i = 0; i < Children.Count; i++) if (Children[i] is ReadingTile t) t.IsSelected = i == index;
        if (focus && SelectedTile is { } tile) tile.Focus(FocusState.Keyboard);
        if (changed)
        {
            if (SelectedTile is { } sel && Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(sel) is { } peer)
                peer.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.SelectionItemPatternOnElementSelected);
            SelectionChanged?.Invoke(index);
        }
    }

    void OnKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs a)
    {
        int n = Children.Count;
        if (n == 0) return;
        int at = _selected < 0 ? 0 : _selected;
        int next = a.Key switch
        {
            Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Up => Math.Max(0, at - 1),
            Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Down => Math.Min(n - 1, at + 1),
            Windows.System.VirtualKey.Home => 0,
            Windows.System.VirtualKey.End => n - 1,
            _ => -1,
        };
        if (next < 0) return;
        Select(next, focus: true);
        a.Handled = true;
    }

    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TileStripPeer(this);

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

/// <summary>The tile strip for UI Automation: a list with a single, required selection (AX-4).</summary>
sealed class TileStripPeer : Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer, Microsoft.UI.Xaml.Automation.Provider.ISelectionProvider
{
    readonly TileStrip _strip;
    public TileStripPeer(TileStrip owner) : base(owner) => _strip = owner;
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.List;
    protected override string GetClassNameCore() => nameof(TileStrip);
    protected override object GetPatternCore(Microsoft.UI.Xaml.Automation.Peers.PatternInterface pattern) =>
        pattern == Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Selection ? this : base.GetPatternCore(pattern);
    public bool CanSelectMultiple => false;
    public bool IsSelectionRequired => true;
    public Microsoft.UI.Xaml.Automation.Provider.IRawElementProviderSimple[] GetSelection() =>
        _strip.SelectedTile is { } t && FromElement(t) is { } p ? new[] { ProviderFromPeer(p) } : Array.Empty<Microsoft.UI.Xaml.Automation.Provider.IRawElementProviderSimple>();
}

/// <summary>A reading tile for UI Automation: a list item that can be selected (selecting it picks the chart's reading).</summary>
sealed class ReadingTilePeer : Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer, Microsoft.UI.Xaml.Automation.Provider.ISelectionItemProvider
{
    readonly ReadingTile _tile;
    public ReadingTilePeer(ReadingTile owner) : base(owner) => _tile = owner;
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.ListItem;
    protected override string GetClassNameCore() => nameof(ReadingTile);
    protected override bool IsKeyboardFocusableCore() => true;
    protected override object GetPatternCore(Microsoft.UI.Xaml.Automation.Peers.PatternInterface pattern) =>
        pattern == Microsoft.UI.Xaml.Automation.Peers.PatternInterface.SelectionItem ? this : base.GetPatternCore(pattern);
    public bool IsSelected => _tile.IsSelected;
    public Microsoft.UI.Xaml.Automation.Provider.IRawElementProviderSimple SelectionContainer =>
        _tile.Parent is TileStrip s && FromElement(s) is { } p ? ProviderFromPeer(p) : null!;
    public void AddToSelection() => Select();
    public void RemoveFromSelection() { }
    public void Select() { if (_tile.Parent is TileStrip s) s.Select(s.Children.IndexOf(_tile)); }
}
