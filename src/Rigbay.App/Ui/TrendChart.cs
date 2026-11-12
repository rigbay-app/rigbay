using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rigbay.Abstractions;
using Rigbay.Core;
using Windows.Foundation;
using Windows.System;

namespace Rigbay.App;

/// <summary>
/// The trend chart of a module page: the readings of one group on one axis over the last 1, 5 or 10 minutes, drawn from the host's history.
/// Lines are 2 px, the grid is a faint solid hairline, there is never a second axis (the plan only groups readings of one unit), a legend with the
/// latest values appears for two or more lines, and a crosshair with a tooltip (time and every line's value) follows the pointer or the arrow keys.
/// The values are also the page's reading rows and tiles, so nothing is only in the tooltip. Under high contrast the lines are told apart by dashes.
/// </summary>
sealed class TrendChart : Grid
{
    static readonly int[] Ranges = { 60, 300, 600 };
    static readonly Dictionary<string, (string Group, int Range)> Remembered = new();

    const double Left = 42, Right = 12, Top = 8, Bottom = 22;

    readonly ModuleEntry _e;
    readonly IReadOnlyList<ChartPlan> _plans;
    // three layers so that what does not change every time (grid and labels) is not rebuilt: the grid, the lines, the crosshair with its tooltip
    readonly Grid _plot = new() { Height = 190, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsTabStop = true, UseSystemFocusVisuals = true };
    readonly Canvas _gridLayer = new() { IsHitTestVisible = false };
    readonly Canvas _dataLayer = new() { IsHitTestVisible = false };
    readonly Canvas _hoverLayer = new() { IsHitTestVisible = false };
    string _gridKey = "";
    readonly TextBlock _empty;
    readonly FlowPanel _legend = new() { HorizontalGap = 18 };
    readonly SelectorBar _groupBar = new();
    readonly SelectorBar _rangeBar = new();
    readonly TextBlock _title;
    int _group;
    int _range = 300;
    double? _hoverX;
    int _seriesDrawn;
    int _referencesDrawn;
    bool _hasData;

    public int GroupIndex => _group;
    public int RangeSeconds => _range;
    public int SeriesDrawn => _seriesDrawn;
    /// <summary>How many reference lines (limits, <see cref="ChartRole.Reference"/>) the last draw put on the plot.</summary>
    public int ReferencesDrawn => _referencesDrawn;
    /// <summary>The labels of the reference lines of the last draw ("PL1 limit 45 W").</summary>
    public IReadOnlyList<string> ReferenceLabels => _referenceLabels;
    readonly List<string> _referenceLabels = new();
    public bool HasData => _hasData;
    public Grid Plot => _plot;
    public int GroupCount => _plans.Count;

    public TrendChart(ModuleEntry e, IReadOnlyList<ChartPlan> plans, PageScope scope)
    {
        _e = e; _plans = plans;
        RowSpacing = 6;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (Remembered.TryGetValue(e.Id, out var mem))
        {
            int at = plans.ToList().FindIndex(p => p.Id == mem.Group);
            if (at >= 0) _group = at;
            if (Ranges.Contains(mem.Range)) _range = mem.Range;
        }

        // header: the group (tabs when there are several, a title when one) and the time range
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title = Ui.Text("", weight: FontWeights.SemiBold, trim: true);
        if (plans.Count > 1)
        {
            for (int i = 0; i < plans.Count; i++) _groupBar.Items.Add(new SelectorBarItem { Text = GroupName(plans[i]), Tag = i });
            _groupBar.SelectedItem = _groupBar.Items[_group];
            _groupBar.SelectionChanged += (_, _) => { if (_groupBar.SelectedItem is SelectorBarItem it) { _group = (int)it.Tag; Remember(); Redraw(); } };
            AutomationProperties.SetName(_groupBar, Host.T("chart.groups"));
            head.Children.Add(_groupBar);
        }
        else head.Children.Add(_title);
        foreach (var r in Ranges) _rangeBar.Items.Add(new SelectorBarItem { Text = Host.F("chart.range-min", r / 60), Tag = r });
        _rangeBar.SelectedItem = _rangeBar.Items.OfType<SelectorBarItem>().First(i => (int)i.Tag == _range);
        _rangeBar.SelectionChanged += (_, _) => { if (_rangeBar.SelectedItem is SelectorBarItem it) { _range = (int)it.Tag; Remember(); Redraw(); } };
        AutomationProperties.SetName(_rangeBar, Host.T("chart.range"));
        Grid.SetColumn(_rangeBar, 1);
        head.Children.Add(_rangeBar);
        // a narrow window (640 px): five tabs and the range do not fit in one row, so the range goes under the tabs
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.SizeChanged += (_, a) =>
        {
            bool narrow = plans.Count > 1 && a.NewSize.Width < 620;
            Grid.SetRow(_rangeBar, narrow ? 1 : 0);
            Grid.SetColumn(_rangeBar, narrow ? 0 : 1);
            Grid.SetColumnSpan(_rangeBar, narrow ? 2 : 1);
            _rangeBar.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
        Children.Add(head);

        Grid.SetRow(_legend, 1);
        Children.Add(_legend);

        _empty = Ui.Text(Host.T("chart.collecting"), "RbSecondaryText");
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.IsHitTestVisible = false;
        var plotHost = new Grid();
        plotHost.Children.Add(_plot);
        plotHost.Children.Add(_empty);
        Grid.SetRow(plotHost, 2);
        Children.Add(plotHost);

        _plot.Children.Add(_gridLayer);
        _plot.Children.Add(_dataLayer);
        _plot.Children.Add(_hoverLayer);
        _plot.SizeChanged += (_, _) => Redraw();
        _plot.PointerMoved += (_, a) => { _hoverX = a.GetCurrentPoint(_plot).Position.X; Redraw(); };
        _plot.PointerExited += (_, _) => { _hoverX = null; Redraw(); };
        _plot.GotFocus += (_, _) => { if (_hoverX == null) { _hoverX = _plot.ActualWidth - Right; Redraw(); } };
        _plot.LostFocus += (_, _) => { _hoverX = null; Redraw(); };
        _plot.KeyDown += OnKey;
        _plot.ActualThemeChanged += (_, _) => { _gridKey = ""; Redraw(); };

        scope.Tick(Redraw);
        Redraw();
    }

    /// <summary>Test hook: puts the crosshair where the pointer would be (null takes it away) and draws; true when a tooltip is on the plot afterwards.</summary>
    public bool HoverAt(double? x)
    {
        _hoverX = x;
        Redraw();
        return _hoverLayer.Children.OfType<Border>().Any();
    }

    /// <summary>Test hook: picks chart group <paramref name="index"/> as a click on its tab would.</summary>
    public void SelectGroup(int index)
    {
        if (index < 0 || index >= _plans.Count || _groupBar.Items.Count == 0) return;
        _groupBar.SelectedItem = _groupBar.Items[index];
    }

    /// <summary>Test hook: picks the time range (1, 5 or 10 minutes) as a click on its tab would.</summary>
    public void SelectRange(int minutes)
    {
        var item = _rangeBar.Items.OfType<SelectorBarItem>().FirstOrDefault(i => (int)i.Tag == minutes * 60);
        if (item != null) _rangeBar.SelectedItem = item;
    }

    string GroupName(ChartPlan plan) => plan.TitleKey is { Length: > 0 } k ? _e.Localisation.T(k) : Host.T("chart.group." + plan.Unit.ToString().ToLowerInvariant());

    void Remember() => Remembered[_e.Id] = (_plans[Math.Clamp(_group, 0, _plans.Count - 1)].Id, _range);

    void OnKey(object sender, KeyRoutedEventArgs a)
    {
        if (Nudge(a.Key)) a.Handled = true;
    }

    /// <summary>The keyboard way to the crosshair: Left and Right move it a sixtieth of the plot, Home and End send it to the oldest and the newest end. True when the key was one of them.</summary>
    public bool Nudge(VirtualKey key)
    {
        if (key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End)) return false;
        double w = _plot.ActualWidth - Left - Right;
        if (w <= 0) return false;
        double step = w / 60;
        double x = _hoverX ?? _plot.ActualWidth - Right;
        x = key switch { VirtualKey.Left => x - step, VirtualKey.Right => x + step, VirtualKey.Home => Left, _ => _plot.ActualWidth - Right };
        _hoverX = Math.Clamp(x, Left, _plot.ActualWidth - Right);
        Redraw();
        return true;
    }

    /// <summary>Where the crosshair is (null: nowhere).</summary>
    public double? HoverX => _hoverX;

    sealed record Series(ReadingRow Row, string Name, IReadOnlyList<HistoryPoint> Points, string Hue, MetricSample? Latest);

    /// <summary>Draws the chart from the history now. UI thread.</summary>
    public void Redraw()
    {
        var plan = _plans[Math.Clamp(_group, 0, _plans.Count - 1)];
        var bus = Host.Core.Metrics;
        var history = bus.History;
        var now = history.Now;
        var span = TimeSpan.FromSeconds(_range);
        var unit = bus.UnitOf(plan.Series[0].Metric) is var u && u != MetricUnit.None ? u : plan.Unit;
        var kindHue = ChartKit.Hue(plan.Series[0].Color, unit);

        var series = new List<Series>();
        for (int i = 0; i < plan.Series.Count; i++)
        {
            var row = plan.Series[i];
            series.Add(new Series(row, _e.Localisation.T(row.HeaderKey), history.Get(row.Metric, span),
                ChartKit.SeriesHue(i, plan.Series.Count, kindHue), bus.Latest(row.Metric)));
        }
        // the limits this chart is read against (PL1 for package power): dashed lines, not series
        var references = plan.References.Select(r => new Series(r, _e.Localisation.T(r.HeaderKey), history.Get(r.Metric, span), "Neutral", bus.Latest(r.Metric))).ToList();
        _title.Text = $"{GroupName(plan)}  ·  {unit.Symbol()}".TrimEnd(' ', '·');

        // legend: one entry per line when there are two or more (the title names a single line)
        _legend.Children.Clear();
        _legend.Visibility = series.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (series.Count > 1)
            for (int i = 0; i < series.Count; i++)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                item.Children.Add(new Rectangle { Width = 14, Height = 3, Style = ChartKit.Key(series[i].Hue), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(Ui.Text(series[i].Name, "RbSecondaryCaption"));
                item.Children.Add(Ui.Text(ReadingFormat.IsAvailable(series[i].Latest) ? ReadingFormat.Format(Host.Core.Languages, series[i].Latest, series[i].Row.Decimals) : Host.T("core.reading.unavailable"), "RbPrimaryText").With(t => { t.FontSize = 12; t.FontWeight = FontWeights.SemiBold; }));
                _legend.Children.Add(item);
            }

        _dataLayer.Children.Clear();
        _hoverLayer.Children.Clear();
        double w = _plot.ActualWidth, h = _plot.ActualHeight;
        _seriesDrawn = 0;
        _hasData = series.Any(s => s.Points.Count >= 2);
        _empty.Visibility = _hasData ? Visibility.Collapsed : Visibility.Visible;
        if (w < Left + Right + 40 || h < Top + Bottom + 40) return;

        double pw = w - Left - Right, ph = h - Top - Bottom;
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in series) foreach (var p in s.Points) { min = Math.Min(min, p.Value); max = Math.Max(max, p.Value); }
        // a limit stays on the plot even when the data is far below it: "how close to PL1" is the question the line answers
        if (min <= max) foreach (var s in references) foreach (var p in s.Points) { min = Math.Min(min, p.Value); max = Math.Max(max, p.Value); }
        if (min > max) { min = 0; max = 0; }
        var first = plan.Series[0];
        var (lo, hi, ticks) = ReadingScale.Axis(min, max, unit, first.RangeMin, first.RangeMax);
        double step = ticks.Count > 1 ? ticks[1] - ticks[0] : 1;
        double Y(double v) => Top + ph - ReadingScale.Fraction(v, lo, hi) * ph;
        double X(DateTimeOffset t) => Left + pw - Math.Clamp((now - t).TotalSeconds / span.TotalSeconds, 0, 1) * pw;

        // grid and the y axis: faint solid hairlines, labels in secondary ink; the x axis is the time before now. Rebuilt only when the axis, the range or the size changed.
        string key = $"{w:0}x{h:0}|{lo}|{hi}|{step}|{_range}|{unit}";
        if (key != _gridKey)
        {
            _gridKey = key;
            _gridLayer.Children.Clear();
            foreach (var t in ticks)
            {
                double y = Y(t);
                _gridLayer.Children.Add(new Line { X1 = Left, X2 = Left + pw, Y1 = y, Y2 = y, Style = Ui.Style("RbChartGrid"), IsHitTestVisible = false });
                var lab = Ui.Text(ReadingScale.FormatTick(t, step), "RbSecondaryCaption");
                lab.Width = Left - 8; lab.TextAlignment = TextAlignment.Right; lab.IsHitTestVisible = false;
                Canvas.SetLeft(lab, 0); Canvas.SetTop(lab, y - 9);
                _gridLayer.Children.Add(lab);
            }
            int tickEvery = _range switch { <= 60 => 15, <= 300 => 60, _ => 120 };
            for (int k = 0; k <= _range; k += tickEvery)
            {
                double x = Left + pw - (double)k / _range * pw;
                var lab = Ui.Text(ChartKit.AgoText(k), "RbSecondaryCaption");
                lab.IsHitTestVisible = false;
                lab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lw = lab.DesiredSize.Width;
                Canvas.SetLeft(lab, Math.Clamp(x - lw / 2, Left - 4, w - lw));
                Canvas.SetTop(lab, Top + ph + 4);
                _gridLayer.Children.Add(lab);
            }
        }

        // the lines (breaks where the module did not publish), the area when there is only one, the dot at the live end
        bool hc = ChartKit.HighContrast;
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.Points.Count == 0) continue;
            var gap = MetricHistory.GapFor(s.Points);
            foreach (var run in MetricHistory.Segments(s.Points, gap))
            {
                if (run.Count < 2) continue;
                var pts = run.Select(p => new Point(X(p.Time), Y(p.Value))).ToList();
                if (series.Count == 1) _dataLayer.Children.Add(ChartKit.Area(pts, Top + ph, s.Hue));
                var line = new Polyline { Points = ChartKit.Points(pts), Style = ChartKit.Line(s.Hue), IsHitTestVisible = false };
                if (hc && ChartKit.Dashes(i) is { } dashes) line.StrokeDashArray = dashes;
                _dataLayer.Children.Add(line);
            }
            var last = s.Points[^1];
            if (now - last.Time <= gap)
            {
                var dot = new Ellipse { Width = 10, Height = 10, Style = ChartKit.Dot(s.Hue), IsHitTestVisible = false };
                Canvas.SetLeft(dot, X(last.Time) - 5); Canvas.SetTop(dot, Y(last.Value) - 5);
                _dataLayer.Children.Add(dot);
            }
            _seriesDrawn++;
        }

        // reference lines: 1 px dashed in secondary ink, the name and the value written at the line (never in the legend)
        _referencesDrawn = 0;
        _referenceLabels.Clear();
        double? lastLabelY = null;
        if (_hasData)
            foreach (var s in references)
            {
                if (s.Points.Count == 0) continue;
                var gap = MetricHistory.GapFor(s.Points);
                foreach (var run in MetricHistory.Segments(s.Points, gap))
                {
                    // one point is still a level: draw it across to now
                    var pts = run.Select(p => new Point(X(p.Time), Y(p.Value))).ToList();
                    if (pts.Count == 1) pts.Add(new Point(Left + pw, pts[0].Y));
                    _dataLayer.Children.Add(new Polyline { Points = ChartKit.Points(pts), Style = Ui.Style("RbChartReference"), StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false });
                }
                var last = s.Points[^1];
                var text = s.Name + " " + FormatValue(last.Value, s);
                var lab = Ui.Text(text, "RbSecondaryCaption");
                lab.IsHitTestVisible = false;
                lab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double ly = Y(last.Value) - lab.DesiredSize.Height - 1;
                if (ly < 0) ly = Y(last.Value) + 2;
                // two limits close together (PL1 55 W, PL2 60 W): the second label goes to the other end of its line
                bool left = lastLabelY is { } prev && Math.Abs(prev - ly) < lab.DesiredSize.Height;
                Canvas.SetLeft(lab, left ? Left + 6 : Left + pw - lab.DesiredSize.Width - 6);
                Canvas.SetTop(lab, ly);
                _dataLayer.Children.Add(lab);
                lastLabelY = ly;
                _referenceLabels.Add(text);
                _referencesDrawn++;
            }

        if (_hoverX is { } hx && _hasData) DrawHover(series, hx, X, Y, now, span, pw, w, ph);

        AutomationProperties.SetName(_plot, Describe(plan, series, unit, references));
    }

    void DrawHover(List<Series> series, double hoverX, Func<DateTimeOffset, double> X, Func<double, double> Y, DateTimeOffset now, TimeSpan span, double pw, double w, double ph)
    {
        // snap to the nearest point of any line
        DateTimeOffset target = now - TimeSpan.FromSeconds((Left + pw - Math.Clamp(hoverX, Left, Left + pw)) / pw * span.TotalSeconds);
        DateTimeOffset? snapped = null; double best = double.MaxValue;
        foreach (var s in series)
            foreach (var p in s.Points)
            {
                double d = Math.Abs((p.Time - target).TotalSeconds);
                if (d < best) { best = d; snapped = p.Time; }
            }
        if (snapped is not { } at) return;
        double x = X(at);

        var cursor = new Line { X1 = x, X2 = x, Y1 = Top, Y2 = Top + ph, Style = Ui.Style("RbChartCursor"), IsHitTestVisible = false };
        _hoverLayer.Children.Add(cursor);

        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(Ui.Text(at.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), "RbSecondaryCaption"));
        foreach (var s in series)
        {
            // the point of this line nearest to the cursor, if it is close enough to be the same moment
            HistoryPoint? near = null; double nd = double.MaxValue;
            foreach (var p in s.Points) { double d = Math.Abs((p.Time - at).TotalSeconds); if (d < nd) { nd = d; near = p; } }
            bool has = near is { } && nd <= Math.Max(4, MetricHistory.GapFor(s.Points).TotalSeconds / 3);
            if (has)
            {
                var dot = new Ellipse { Width = 10, Height = 10, Style = ChartKit.Dot(s.Hue), IsHitTestVisible = false };
                Canvas.SetLeft(dot, x - 5); Canvas.SetTop(dot, Y(near!.Value.Value) - 5);
                _hoverLayer.Children.Add(dot);
            }
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            rowPanel.Children.Add(new Rectangle { Width = 10, Height = 3, Style = ChartKit.Key(s.Hue), VerticalAlignment = VerticalAlignment.Center });
            rowPanel.Children.Add(Ui.Text(has ? FormatValue(near!.Value.Value, s) : "—", "RbPrimaryText").With(t => t.FontWeight = FontWeights.SemiBold));
            if (series.Count > 1) rowPanel.Children.Add(Ui.Text(s.Name, "RbSecondaryCaption"));
            body.Children.Add(rowPanel);
        }
        var tip = new Border { Style = Ui.Style("RbChartTip"), Child = body, IsHitTestVisible = false };
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = tip.DesiredSize.Width;
        double tx = x + 12 + tw > w ? x - 12 - tw : x + 12;
        Canvas.SetLeft(tip, Math.Max(0, tx)); Canvas.SetTop(tip, Top + 4);
        _hoverLayer.Children.Add(tip);
    }

    string FormatValue(double v, Series s)
    {
        var symbol = Host.Core.Metrics.UnitOf(s.Row.Metric).Symbol();
        return v.ToString("F" + Math.Max(0, s.Row.Decimals), System.Globalization.CultureInfo.InvariantCulture) + (symbol.Length > 0 ? " " + symbol : "");
    }

    string Describe(ChartPlan plan, List<Series> series, MetricUnit unit, List<Series> references)
    {
        string lowest = Host.T("chart.min-word"), highest = Host.T("chart.max-word"), unavailable = Host.T("core.reading.unavailable");
        var parts = new List<string>();
        foreach (var s in series)
        {
            if (s.Points.Count == 0) { parts.Add(s.Name + ": " + unavailable); continue; }
            double lo = s.Points.Min(p => p.Value), hi = s.Points.Max(p => p.Value);
            parts.Add(s.Name + ": " + FormatValue(s.Points[^1].Value, s) + ", " + lowest + " " + FormatValue(lo, s) + ", " + highest + " " + FormatValue(hi, s));
        }
        foreach (var s in references)
            if (s.Points.Count > 0) parts.Add(Host.F("chart.reference", s.Name, FormatValue(s.Points[^1].Value, s)));
        string span = Host.F("chart.last", Host.F("chart.range-min", _range / 60));
        return GroupName(plan) + ", " + span + ". " + string.Join("; ", parts);
    }
}
