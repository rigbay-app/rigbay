using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rigbay.Abstractions;
using Rigbay.Core;
using Windows.Foundation;
using Windows.System;

namespace Rigbay.App;

/// <summary>
/// The trend chart of a module page. It draws the reading of the selected tile (the tiles are its tabs, as in Task Manager's Performance page) over
/// the last 1, 5 or 10 minutes, from the host's history: one 2 px line in the reading's own hue (the tile's), no area, the live end marked, the limits
/// of its group (<see cref="ChartRole.Reference"/>) as 1 px dashed lines labelled at the line. "Compare" adds the other readings of the same group
/// (same unit, one axis, never two) in the documented categorical order, with a legend that carries their latest values. Until the data covers the
/// chosen range the time axis is the data's own span (at least a minute), so a reading that just started is not a sliver in a corner; its labels
/// (<see cref="TimeAxis"/>) are in one unit, never two alike, and none is older than the data. The y axis is
/// in the current language's number format, scaled for a byte rate (KB/s), and its top tick names the unit. A crosshair with a tooltip (time and every
/// line's value) follows the pointer or the arrow keys. The chart is not drawn at all until one of its lines has two points.
/// Under high contrast the lines of a comparison are told apart by dashes.
/// </summary>
sealed class TrendChart : Grid
{
    static readonly int[] Ranges = { 60, 300, 600 };
    static readonly Dictionary<string, (int Range, bool Compare)> Remembered = new();

    const double Right = 12, Top = 8, Bottom = 22, MinLeft = 34;

    readonly ModuleEntry _e;
    // three layers so that what does not change every time (grid and labels) is not rebuilt: the grid, the lines, the crosshair with its tooltip
    readonly Grid _plot = new() { Height = 190, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsTabStop = true, UseSystemFocusVisuals = true };
    readonly Canvas _gridLayer = new() { IsHitTestVisible = false };
    readonly Canvas _dataLayer = new() { IsHitTestVisible = false };
    readonly Canvas _hoverLayer = new() { IsHitTestVisible = false };
    string _gridKey = "";
    readonly FlowPanel _legend = new() { HorizontalGap = 18 };
    readonly SelectorBar _rangeBar = new();
    readonly ToggleButton _compare = new() { Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _title;
    ChartPlan? _plan;
    ReadingRow? _focus;
    int _range = 300;
    bool _compareOn;
    double? _hoverX;
    int _seriesDrawn;
    int _referencesDrawn;
    bool _hasData;
    double _left = 42;
    double _shownSeconds;
    IReadOnlyList<string> _tickLabels = Array.Empty<string>();
    IReadOnlyList<string> _timeLabels = Array.Empty<string>();
    TimeAxisPlan? _timeAxis;
    readonly List<string> _referenceLabels = new();

    public int RangeSeconds => _range;
    public int SeriesDrawn => _seriesDrawn;
    /// <summary>How many reference lines (limits, <see cref="ChartRole.Reference"/>) the last draw put on the plot.</summary>
    public int ReferencesDrawn => _referencesDrawn;
    /// <summary>The labels of the reference lines of the last draw ("PL1 limit 45 W").</summary>
    public IReadOnlyList<string> ReferenceLabels => _referenceLabels;
    /// <summary>The y-axis labels of the last draw, bottom to top (the top one carries the unit).</summary>
    public IReadOnlyList<string> TickLabels => _tickLabels;
    /// <summary>The time the x axis spans in the last draw: the data's own span (at least 60 s) until the data covers the chosen range.</summary>
    public double ShownSeconds => _shownSeconds;
    /// <summary>The time-axis labels of the last draw, now first (never two alike, none older than the data).</summary>
    public IReadOnlyList<string> TimeLabels => _timeLabels;
    /// <summary>The time axis of the last draw (<see cref="TimeAxis"/>).</summary>
    public TimeAxisPlan? TimeAxisShown => _timeAxis;
    public bool HasData => _hasData;
    public Grid Plot => _plot;
    public ChartPlan? PlanShown => _plan;
    /// <summary>The reading the chart draws (the selected tile's), or null when it draws its whole group.</summary>
    public ReadingRow? FocusRow => _focus;
    /// <summary>True while the other readings of the group are drawn too.</summary>
    public bool Comparing => _compareOn || _focus == null;
    public ToggleButton CompareButton => _compare;

    /// <summary>Raised when a script or test asks for another tile (<see cref="SelectGroup"/>): the block selects that tile.</summary>
    public event Action<int>? TileRequested;
    /// <summary>Raised when the chart goes from nothing to draw to a line, or back (the block hides its card when it is empty).</summary>
    public event Action? HasDataChanged;

    public TrendChart(ModuleEntry e, PageScope scope)
    {
        _e = e;
        RowSpacing = 6;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Visibility = Visibility.Collapsed;   // until a line has two points

        if (Remembered.TryGetValue(e.Id, out var mem))
        {
            if (Ranges.Contains(mem.Range)) _range = mem.Range;
            _compareOn = mem.Compare;
        }

        // header: what is drawn, Compare (when the group has more than one reading), the time range
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title = Ui.Text("", weight: FontWeights.SemiBold, trim: true);
        head.Children.Add(_title);
        _compare.Content = Host.T("chart.compare");
        _compare.IsChecked = _compareOn;
        AutomationProperties.SetName(_compare, Host.T("chart.compare"));
        _compare.Click += (_, _) => { _compareOn = _compare.IsChecked == true; Remember(); Redraw(); };
        Grid.SetColumn(_compare, 1);
        head.Children.Add(_compare);
        foreach (var r in Ranges) _rangeBar.Items.Add(new SelectorBarItem { Text = Host.F("chart.range-min", r / 60), Tag = r });
        _rangeBar.SelectedItem = _rangeBar.Items.OfType<SelectorBarItem>().First(i => (int)i.Tag == _range);
        _rangeBar.SelectionChanged += (_, _) => { if (_rangeBar.SelectedItem is SelectorBarItem it) { _range = (int)it.Tag; Remember(); Redraw(); } };
        AutomationProperties.SetName(_rangeBar, Host.T("chart.range"));
        Grid.SetColumn(_rangeBar, 2);
        head.Children.Add(_rangeBar);
        // a narrow window (640 px): the range goes under the title
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.SizeChanged += (_, a) =>
        {
            bool narrow = a.NewSize.Width < 460;
            Grid.SetRow(_rangeBar, narrow ? 1 : 0);
            Grid.SetColumn(_rangeBar, narrow ? 0 : 2);
            Grid.SetColumnSpan(_rangeBar, narrow ? 3 : 1);
            _rangeBar.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
        Children.Add(head);

        Grid.SetRow(_legend, 1);
        Children.Add(_legend);

        Grid.SetRow(_plot, 2);
        Children.Add(_plot);

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
    }

    /// <summary>Draws <paramref name="focus"/> (a series of <paramref name="plan"/>, the selected tile's reading) or, with null, every series of the plan.</summary>
    public void Show(ChartPlan plan, ReadingRow? focus)
    {
        _plan = plan;
        _focus = focus != null && plan.Series.Contains(focus) ? focus : null;
        _hoverX = null;
        _gridKey = "";
        Redraw();
    }

    /// <summary>Draws nothing (the selected reading is kept out of every chart): the chart is hidden.</summary>
    public void Clear()
    {
        _plan = null;
        _focus = null;
        bool had = _hasData;
        _hasData = false;
        Visibility = Visibility.Collapsed;
        if (had) HasDataChanged?.Invoke();
    }

    /// <summary>Test and script hook: asks the block for tile <paramref name="index"/>, as a click on it would.</summary>
    public void SelectGroup(int index) => TileRequested?.Invoke(index);

    /// <summary>Test hook: puts the crosshair where the pointer would be (null takes it away) and draws; true when a tooltip is on the plot afterwards.</summary>
    public bool HoverAt(double? x)
    {
        _hoverX = x;
        Redraw();
        return _hoverLayer.Children.OfType<Border>().Any();
    }

    /// <summary>Test hook: picks the time range (1, 5 or 10 minutes) as a click on its tab would.</summary>
    public void SelectRange(int minutes)
    {
        var item = _rangeBar.Items.OfType<SelectorBarItem>().FirstOrDefault(i => (int)i.Tag == minutes * 60);
        if (item != null) _rangeBar.SelectedItem = item;
    }

    /// <summary>Test hook: Compare on or off, as a click on the button would.</summary>
    public void SetCompare(bool on)
    {
        _compare.IsChecked = on;
        _compareOn = on;
        Remember();
        Redraw();
    }

    string GroupName(ChartPlan plan) =>
        plan.TitleKey is { Length: > 0 } k ? _e.Localisation.T(k)
        // a group named only by its unit ("Count") takes its reading's own name when it has one
        : plan.Series.Count == 1 ? _e.Localisation.T(plan.Series[0].HeaderKey)
        : Host.T("chart.group." + plan.Unit.ToString().ToLowerInvariant());

    void Remember() => Remembered[_e.Id] = (_range, _compareOn);

    void OnKey(object sender, KeyRoutedEventArgs a)
    {
        if (Nudge(a.Key)) a.Handled = true;
    }

    /// <summary>The keyboard way to the crosshair: Left and Right move it a sixtieth of the plot, Home and End send it to the oldest and the newest end. True when the key was one of them.</summary>
    public bool Nudge(VirtualKey key)
    {
        if (key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End)) return false;
        double w = _plot.ActualWidth - _left - Right;
        if (w <= 0) return false;
        double step = w / 60;
        double x = _hoverX ?? _plot.ActualWidth - Right;
        x = key switch { VirtualKey.Left => x - step, VirtualKey.Right => x + step, VirtualKey.Home => _left, _ => _plot.ActualWidth - Right };
        _hoverX = Math.Clamp(x, _left, _plot.ActualWidth - Right);
        Redraw();
        return true;
    }

    /// <summary>Where the crosshair is (null: nowhere).</summary>
    public double? HoverX => _hoverX;

    sealed record Series(ReadingRow Row, string Name, IReadOnlyList<HistoryPoint> Points, string Hue, MetricSample? Latest);

    /// <summary>Draws the chart from the history now. UI thread.</summary>
    public void Redraw()
    {
        if (_plan is not { } plan) return;
        var bus = Host.Core.Metrics;
        var history = bus.History;
        var now = history.Now;
        var range = TimeSpan.FromSeconds(_range);
        var unit = bus.UnitOf(plan.Series[0].Metric) is var u && u != MetricUnit.None ? u : plan.Unit;
        var culture = ReadingFormat.Culture(Host.Core.Languages);
        bool compare = Comparing && plan.Series.Count > 1;

        // what is drawn: the selected reading in its own hue (the tile's), or the group in the categorical order; the colour follows the reading,
        // never its rank, so a reading keeps its hue whichever tile is selected
        var series = new List<Series>();
        for (int i = 0; i < plan.Series.Count; i++)
        {
            var row = plan.Series[i];
            if (!compare && _focus != null && row != _focus) continue;
            string hue = compare ? ChartKit.SeriesHue(i, plan.Series.Count, "") : ChartKit.Hue(row.Color, unit);
            series.Add(new Series(row, _e.Localisation.T(row.HeaderKey), history.Get(row.Metric, range), hue, bus.Latest(row.Metric)));
        }
        if (compare && _focus != null)
        {
            // the selected reading on top
            int at = series.FindIndex(s => s.Row == _focus);
            if (at >= 0) { var f = series[at]; series.RemoveAt(at); series.Add(f); }
        }
        var references = plan.References.Select(r => new Series(r, _e.Localisation.T(r.HeaderKey), history.Get(r.Metric, range), "Neutral", bus.Latest(r.Metric))).ToList();

        _compare.Visibility = plan.Series.Count > 1 && _focus != null ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = !compare && _focus != null ? _e.Localisation.T(_focus.HeaderKey) : GroupName(plan);

        bool had = _hasData;
        _hasData = series.Any(s => s.Points.Count >= 2);
        Visibility = _hasData ? Visibility.Visible : Visibility.Collapsed;
        if (had != _hasData) HasDataChanged?.Invoke();

        // legend: only when several lines are compared (a single line is named by the title); each swatch is its line's colour, the value is in ink
        _legend.Children.Clear();
        _legend.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        if (compare)
            foreach (var s in plan.Series.Select(r => series.First(x => x.Row == r)))
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                item.Children.Add(new Rectangle { Width = 14, Height = 3, Style = ChartKit.Key(s.Hue), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(Ui.Text(s.Name, "RbSecondaryCaption"));
                item.Children.Add(Ui.Text(ReadingFormat.IsAvailable(s.Latest) ? ReadingFormat.Format(Host.Core.Languages, s.Latest, s.Row.Decimals) : Host.T("core.reading.unavailable"), "RbPrimaryText").With(t => { t.FontSize = 12; t.FontWeight = FontWeights.SemiBold; }));
                _legend.Children.Add(item);
            }

        _dataLayer.Children.Clear();
        _hoverLayer.Children.Clear();
        double w = _plot.ActualWidth, h = _plot.ActualHeight;
        _seriesDrawn = 0;
        _referencesDrawn = 0;
        _referenceLabels.Clear();
        if (!_hasData || w < MinLeft + Right + 40 || h < Top + Bottom + 40) { AutomationProperties.SetName(_plot, Describe(plan, series, references)); return; }

        // the time axis (TimeAxis): the data's own span (at least a minute) until the data covers the chosen range, within one sample's spacing (the
        // history hands over what is inside the range, so its oldest point is up to one sample younger than the range); labels only where there is data
        var oldest = series.Where(s => s.Points.Count > 0).Select(s => s.Points[0].Time).DefaultIfEmpty(now).Min();
        double age = (now - oldest).TotalSeconds;
        double tolerance = Math.Max(history.SlotWidth.TotalSeconds,
            series.Where(s => s.Points.Count > 0).Select(s => MetricHistory.GapFor(s.Points).TotalSeconds / 3).DefaultIfEmpty(0).Max());
        double shown = TimeAxis.Plan(age, _range, double.PositiveInfinity, tolerance).ShownSeconds;
        _shownSeconds = shown;
        var span = TimeSpan.FromSeconds(shown);

        // the value axis: the data and its limits, scaled for a byte rate, in round steps; the top tick names the unit
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in series) foreach (var p in s.Points) if (now - p.Time <= span) { min = Math.Min(min, p.Value); max = Math.Max(max, p.Value); }
        if (min <= max) foreach (var s in references) foreach (var p in s.Points) if (now - p.Time <= span) { min = Math.Min(min, p.Value); max = Math.Max(max, p.Value); }
        if (min > max) { min = 0; max = 0; }
        var (divisor, symbol) = ReadingFormat.Scale(unit, Math.Max(Math.Abs(min), Math.Abs(max)));
        var first = plan.Series[0];
        var (lo, hi, ticks) = ReadingScale.Axis(min / divisor, max / divisor, unit, first.RangeMin / divisor, first.RangeMax / divisor);
        _tickLabels = ReadingScale.TickLabels(ticks, symbol, culture);

        // the y labels take the room they need at the left (a unit on the top one), the plot takes the rest
        double labelWidth = 0;
        foreach (var t in _tickLabels)
        {
            var probe = Ui.Text(t, "RbSecondaryCaption");
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            labelWidth = Math.Max(labelWidth, probe.DesiredSize.Width);
        }
        _left = Math.Max(MinLeft, Math.Ceiling(labelWidth) + 8);
        double left = _left;
        double pw = w - left - Right, ph = h - Top - Bottom;
        var axis = TimeAxis.Plan(age, _range, pw, tolerance);   // the same span, with the labels spaced for this width
        _timeAxis = axis;
        _timeLabels = axis.Ticks.Select(ChartKit.TickText).ToList();
        double Y(double v) => Top + ph - ReadingScale.Fraction(v / divisor, lo, hi) * ph;
        double X(DateTimeOffset t) => left + pw - Math.Clamp((now - t).TotalSeconds / span.TotalSeconds, 0, 1) * pw;

        // grid and the y axis: faint solid hairlines, labels in secondary ink; the x axis is the time before now. Rebuilt only when the axis, the span or the size changed.
        string key = $"{w:0}x{h:0}|{lo}|{hi}|{string.Join(",", _tickLabels)}|{shown:0}|{left}|{string.Join(",", _timeLabels)}";
        if (key != _gridKey)
        {
            _gridKey = key;
            _gridLayer.Children.Clear();
            for (int i = 0; i < ticks.Count; i++)
            {
                double y = Top + ph - ReadingScale.Fraction(ticks[i], lo, hi) * ph;
                _gridLayer.Children.Add(new Line { X1 = left, X2 = left + pw, Y1 = y, Y2 = y, Style = Ui.Style("RbChartGrid"), IsHitTestVisible = false });
                var lab = Ui.Text(_tickLabels[i], "RbSecondaryCaption");
                lab.Width = left - 8; lab.TextAlignment = TextAlignment.Right; lab.IsHitTestVisible = false;
                Canvas.SetLeft(lab, 0); Canvas.SetTop(lab, y - 9);
                _gridLayer.Children.Add(lab);
            }
            for (int i = 0; i < axis.Ticks.Count; i++)
            {
                double x = left + pw - axis.Ticks[i].SecondsAgo / shown * pw;
                var lab = Ui.Text(_timeLabels[i], "RbSecondaryCaption");
                lab.IsHitTestVisible = false;
                lab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lw = lab.DesiredSize.Width;
                Canvas.SetLeft(lab, Math.Clamp(x - lw / 2, left - 4, w - lw));
                Canvas.SetTop(lab, Top + ph + 4);
                _gridLayer.Children.Add(lab);
            }
        }

        // the lines (breaks where the module did not publish), no area, the dot at the live end
        bool hc = ChartKit.HighContrast;
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.Points.Count == 0) continue;
            var gap = MetricHistory.GapFor(s.Points);
            foreach (var run in MetricHistory.Segments(s.Points, gap))
            {
                if (run.Count < 2) continue;
                var pts = run.Where(p => now - p.Time <= span).Select(p => new Point(X(p.Time), Y(p.Value))).ToList();
                if (pts.Count < 2) continue;
                var line = new Polyline { Points = ChartKit.Points(pts), Style = ChartKit.Line(s.Hue), IsHitTestVisible = false };
                if (hc && compare && ChartKit.Dashes(plan.Series.ToList().IndexOf(s.Row)) is { } dashes) line.StrokeDashArray = dashes;
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
        double? lastLabelY = null;
        foreach (var s in references)
        {
            if (s.Points.Count == 0) continue;
            var gap = MetricHistory.GapFor(s.Points);
            foreach (var run in MetricHistory.Segments(s.Points, gap))
            {
                // one point is still a level: draw it across to now
                var pts = run.Where(p => now - p.Time <= span).Select(p => new Point(X(p.Time), Y(p.Value))).ToList();
                if (pts.Count == 0) continue;
                if (pts.Count == 1) pts.Add(new Point(left + pw, pts[0].Y));
                _dataLayer.Children.Add(new Polyline { Points = ChartKit.Points(pts), Style = Ui.Style("RbChartReference"), StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false });
            }
            var lastRef = s.Points[^1];
            var text = s.Name + " " + FormatValue(lastRef.Value, s);
            var lab = Ui.Text(text, "RbSecondaryCaption");
            lab.IsHitTestVisible = false;
            lab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double ly = Y(lastRef.Value) - lab.DesiredSize.Height - 1;
            if (ly < 0) ly = Y(lastRef.Value) + 2;
            // two limits close together (PL1 55 W, PL2 60 W): the second label goes to the other end of its line
            bool atLeft = lastLabelY is { } prev && Math.Abs(prev - ly) < lab.DesiredSize.Height;
            Canvas.SetLeft(lab, atLeft ? left + 6 : left + pw - lab.DesiredSize.Width - 6);
            Canvas.SetTop(lab, ly);
            _dataLayer.Children.Add(lab);
            lastLabelY = ly;
            _referenceLabels.Add(text);
            _referencesDrawn++;
        }

        if (_hoverX is { } hx) DrawHover(series, hx, X, Y, now, span, pw, w, ph, left);

        AutomationProperties.SetName(_plot, Describe(plan, series, references));
    }

    void DrawHover(List<Series> series, double hoverX, Func<DateTimeOffset, double> X, Func<double, double> Y, DateTimeOffset now, TimeSpan span, double pw, double w, double ph, double left)
    {
        // snap to the nearest point of any line
        DateTimeOffset target = now - TimeSpan.FromSeconds((left + pw - Math.Clamp(hoverX, left, left + pw)) / pw * span.TotalSeconds);
        DateTimeOffset? snapped = null; double best = double.MaxValue;
        foreach (var s in series)
            foreach (var p in s.Points)
            {
                if (now - p.Time > span) continue;
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

    string FormatValue(double v, Series s) =>
        ReadingFormat.Parts(v, Host.Core.Metrics.UnitOf(s.Row.Metric), s.Row.Decimals, ReadingFormat.Culture(Host.Core.Languages)).ToString();

    string Describe(ChartPlan plan, List<Series> series, List<Series> references)
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
        return _title.Text + ", " + span + ". " + string.Join("; ", parts);
    }
}
