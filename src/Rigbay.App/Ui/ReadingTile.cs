using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// One reading as a tile: its name, the number big with the unit small beside it, a sparkline in its hue, and, for a temperature or a percent
/// (or any reading whose module gave a range), a thin meter of where it sits in its range, with a tick at the warning value. The number wears the
/// text colour, the hue is on the marks. At the warning value (the module's <see cref="ReadingRow.Warn"/>, else 90 °C for a temperature) the tile
/// says "High" in words with an icon, so the state never depends on a colour. A second figure (<see cref="ReadingRow.Secondary"/>, a temperature's
/// fan) sits small beside the number.
/// <para>The tiles are the chart's tabs: a tile is focusable and selectable (a click, Space or Enter, the arrow keys in its <see cref="TileStrip"/>),
/// the selected one wears a 1.5 px accent border and the chart below draws its reading. Numbers are in the current language's format and a byte
/// rate is scaled (<c>17.6 KB/s</c>).</para>
/// <para><b>Why a tile and not the round gauge Acer's own app shows.</b> A gauge spends about four times the area of a tile on one number and shows
/// nothing about where it is going; at idle a CPU sits at 40 to 60 °C, where the needle looks the same all day. The tile gives the same number
/// at the same size, the trend beside it and the meter (with the warning tick) for the "how close to the limit" question.</para>
/// </summary>
sealed class ReadingTile : UserControl
{
    /// <summary>A temperature at or above this many °C is called High when the module gave no <see cref="ReadingRow.Warn"/>.</summary>
    public const double HighCelsius = 90;

    /// <summary>Below this width the second figure goes under the number instead of beside it.</summary>
    const double InlineSecondaryWidth = 230;

    readonly ModuleEntry _e;
    readonly ReadingRow _row;
    readonly Grid _valueRow = new() { ColumnSpacing = 12 };
    readonly StackPanel _value = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly TextBlock _number = new() { Style = Ui.Style("RbTileValue"), TextTrimming = TextTrimming.None };
    readonly TextBlock _unit = new() { Style = Ui.Style("RbSecondaryText"), FontSize = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock _secondary = new() { Style = Ui.Style("RbSecondaryText"), FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 7), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
    readonly TextBlock _unavailable;
    readonly StackPanel _high;
    readonly Sparkline _spark;
    readonly Grid? _meter;
    readonly Rectangle? _meterFill;
    readonly Rectangle? _warnTick;
    readonly string _name;
    readonly double? _warn;
    readonly Border _frame;
    readonly Border _hover;
    readonly Border _tint;
    bool _selected;
    MetricSample? _latest;
    (double Min, double Max) _meterRange;

    public Sparkline Spark => _spark;
    public ReadingRow Row => _row;
    public string ValueText => _unavailable.Visibility == Visibility.Visible ? _unavailable.Text : _number.Text + (_unit.Text.Length > 0 ? " " + _unit.Text : "");
    public bool ShowsHigh => _high.Visibility == Visibility.Visible;
    public double MeterShare => _meterFill is { } f && _meter is { ActualWidth: > 0 } m ? f.Width / m.ActualWidth : 0;
    public bool HasMeter => _meter != null;
    /// <summary>The warning value this tile uses (the module's, else 90 for a temperature), or null.</summary>
    public double? WarnValue => _warn;
    /// <summary>Where the warning tick sits on the meter (0 to 1), or null when there is none.</summary>
    public double? WarnShare => _warnTick != null && _warn is { } w ? ReadingScale.Fraction(w, _meterRange.Min, _meterRange.Max) : null;
    /// <summary>The second figure as shown ("fan 5,200 rpm"), or "" when there is none.</summary>
    public string SecondaryText => _secondary.Visibility == Visibility.Visible ? _secondary.Text : "";

    /// <summary>Whether this tile is the one the chart draws (set by its <see cref="TileStrip"/>).</summary>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            _frame.Style = Ui.Style(value ? "RbTileFrameSelected" : "RbTileFrame");
            _tint.Opacity = value ? 0.1 : 0;   // a faint accent tint under the selected tile
        }
    }

    public ReadingTile(ModuleEntry e, ReadingRow row, MetricUnit unit, PageScope scope)
    {
        _e = e; _row = row;
        _name = e.Localisation.T(row.HeaderKey);
        _warn = row.Warn ?? (unit == MetricUnit.Celsius ? HighCelsius : null);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        _frame = new Border { Style = Ui.Style("RbTileFrame"), Padding = new Thickness(12, 10, 12, 10), MinHeight = 108 };
        var frame = _frame;

        var grid = new Grid { RowSpacing = 2 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 28 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { ColumnSpacing = 6 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Ui.Text(_name, "RbSecondaryCaption", trim: true);
        label.TextWrapping = TextWrapping.NoWrap;
        head.Children.Add(label);
        _high = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        _high.Children.Add(new FontIcon { Glyph = "", FontSize = 11 });
        _high.Children.Add(Ui.Text(Host.T("tile.high"), "RbSecondaryCaption"));
        Grid.SetColumn(_high, 1);
        head.Children.Add(_high);
        grid.Children.Add(head);

        _value.Children.Add(_number);
        _value.Children.Add(_unit);
        _unavailable = Ui.Text(Host.T("core.reading.unavailable"), "RbSecondaryText");
        _unavailable.FontSize = 16;
        _unavailable.Visibility = Visibility.Collapsed;
        // the number, and beside it (under it on a narrow tile) the second figure
        _valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _valueRow.ColumnDefinitions.Add(new ColumnDefinition());
        _valueRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _valueRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _valueRow.Children.Add(_value);
        Grid.SetColumn(_secondary, 1);
        _valueRow.Children.Add(_secondary);
        Ui.Tabular(_secondary);
        var valueHost = new Grid { MinHeight = 40 };
        valueHost.Children.Add(_valueRow);
        valueHost.Children.Add(_unavailable);
        Grid.SetRow(valueHost, 1);
        grid.Children.Add(valueHost);
        SizeChanged += (_, a) => PlaceSecondary(a.NewSize.Width);

        _spark = new Sparkline(row.Metric, row.Color, 0, 36);
        _spark.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetRow(_spark, 2);
        grid.Children.Add(_spark);

        var natural = row.RangeMin is { } a && row.RangeMax is { } b ? (a, b) : ReadingScale.NaturalRange(unit);
        if (natural is { } range)
        {
            // a 3 px track and fill, and a 1 px tick across it at the warning value
            _meter = new Grid { Height = 9, Margin = new Thickness(0, 5, 0, 0) };
            _meter.Children.Add(new Rectangle { Style = Ui.Style("RbMeterTrack"), Height = 3, VerticalAlignment = VerticalAlignment.Center });
            _meterFill = new Rectangle { Style = ChartKit.Key(ChartKit.Hue(row.Color, unit)), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Height = 3, VerticalAlignment = VerticalAlignment.Center };
            _meter.Children.Add(_meterFill);
            _meterRange = range;
            if (_warn is { } w && w > range.Item1 && w < range.Item2)
            {
                _warnTick = new Rectangle { Style = Ui.Style("RbMeterTick"), Width = 1, Height = 9, HorizontalAlignment = HorizontalAlignment.Left };
                _meter.Children.Add(_warnTick);
            }
            _meter.SizeChanged += (_, _) => ApplyMeter();
            Grid.SetRow(_meter, 3);
            grid.Children.Add(_meter);
        }
        frame.Child = grid;
        // a hover layer and the selection tint sit under the content, inside the frame's rounded corners
        _hover = new Border { Style = Ui.Style("RbTileHover"), Opacity = 0, CornerRadius = new CornerRadius(4), IsHitTestVisible = false };
        _tint = new Border { Style = Ui.Style("RbTileTint"), Opacity = 0, CornerRadius = new CornerRadius(4), IsHitTestVisible = false };
        var stack = new Grid();
        stack.Children.Add(_tint);
        stack.Children.Add(_hover);
        stack.Children.Add(frame);
        Content = stack;
        PointerEntered += (_, _) => _hover.Opacity = 1;
        PointerExited += (_, _) => _hover.Opacity = 0;
        PointerCanceled += (_, _) => _hover.Opacity = 0;
        Tapped += (_, _) => SelectMe(focus: false);
        KeyDown += (_, a) => { if (a.Key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter) { SelectMe(focus: true); a.Handled = true; } };

        AutomationProperties.SetName(this, _name);

        Show(Host.Core.Metrics.TryGetLatest(row.Metric, out var latest) ? latest : null);
        scope.Live(
            () => Host.Core.Metrics.Subscribe(s => Ui.Post(e, () => { if (!scope.Disposed && scope.Active) Show(s); }), row.Metric),
            () => Show(Host.Core.Metrics.TryGetLatest(row.Metric, out var l) ? l : null));
        if (row.Secondary is { Metrics.Count: > 0 } second)
        {
            // the second figure follows its own metrics; it is redrawn with the tile's other parts once a second
            ShowSecondary();
            foreach (var id in second.Metrics.Take(2))
                scope.Live(() => Host.Core.Metrics.Subscribe(_ => Ui.Post(e, () => { if (!scope.Disposed && scope.Active) ShowSecondary(); }), id), ShowSecondary);
        }
        scope.Tick(_spark.Redraw);
    }

    void SelectMe(bool focus)
    {
        if (Parent is TileStrip strip) strip.Select(strip.Children.IndexOf(this), focus);
    }

    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ReadingTilePeer(this);

    void PlaceSecondary(double width)
    {
        bool inline = width >= InlineSecondaryWidth;
        Grid.SetRow(_secondary, inline ? 0 : 1);
        Grid.SetColumn(_secondary, inline ? 1 : 0);
        Grid.SetColumnSpan(_secondary, inline ? 1 : 2);
        _secondary.Margin = inline ? new Thickness(0, 0, 0, 7) : new Thickness(0, -2, 0, 0);
    }

    /// <summary>"fan 5,200 rpm" or "fans 4,350 · 4,340 rpm": the label, the available figures, the unit once.</summary>
    void ShowSecondary()
    {
        if (_row.Secondary is not { } second) return;
        var parts = new List<string>();
        string symbol = "";
        var culture = ReadingFormat.Culture(Host.Core.Languages);
        foreach (var id in second.Metrics.Take(2))
            if (Host.Core.Metrics.TryGetLatest(id, out var s) && ReadingFormat.IsAvailable(s))
            {
                var p = ReadingFormat.Parts(s.Value, s.Unit, 0, culture);
                parts.Add(p.Number);
                symbol = p.Unit;
            }
        string label = second.LabelKey is { Length: > 0 } k ? _e.Localisation.T(k) : "";
        var text = parts.Count == 0 ? "" : (label.Length > 0 ? label + " " : "") + string.Join(" · ", parts) + (symbol.Length > 0 ? " " + symbol : "");
        _secondary.Text = text;
        _secondary.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Describe();
    }

    void Show(MetricSample? s)
    {
        _latest = s;
        bool ok = ReadingFormat.IsAvailable(s);
        _value.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        _secondary.Opacity = ok ? 1 : 0;
        _unavailable.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (ok)
        {
            // the current language's number format, thousands grouped; a byte rate scaled to KB/s, MB/s
            var text = ReadingFormat.Parts(s!.Value, s.Unit, _row.Decimals, ReadingFormat.Culture(Host.Core.Languages));
            _number.Text = text.Number;
            _unit.Text = text.Unit;
        }
        _high.Visibility = ok && _warn is { } w && s!.Value >= w ? Visibility.Visible : Visibility.Collapsed;
        ApplyMeter();
        Describe();
    }

    /// <summary>The accessible name and the tooltip: "CPU temperature, 82 °C, High, warning at 95 °C, fan 5,200 rpm" and the meter's scale.</summary>
    void Describe()
    {
        bool ok = ReadingFormat.IsAvailable(_latest);
        var parts = new List<string> { _name, ok ? _number.Text + (_unit.Text.Length > 0 ? " " + _unit.Text : "") : _unavailable.Text };
        if (ShowsHigh) parts.Add(Host.T("tile.high"));
        string unit = _unit.Text.Length > 0 ? " " + _unit.Text : "";
        var culture = ReadingFormat.Culture(Host.Core.Languages);
        if (_warn is { } w) parts.Add(Host.F("tile.warn-at", w.ToString("0.##", culture) + unit));
        if (SecondaryText.Length > 0) parts.Add(SecondaryText);
        AutomationProperties.SetName(this, string.Join(", ", parts));
        var tip = string.Join(" · ", parts.Skip(1));
        if (_meter != null) tip += " · " + Host.F("tile.scale", _meterRange.Min.ToString("0.##", culture), _meterRange.Max.ToString("0.##", culture) + unit);
        ToolTipService.SetToolTip(this, _name + ": " + tip);
    }

    void ApplyMeter()
    {
        if (_meter is null || _meterFill is null || _meter.ActualWidth <= 0) return;
        double width = _meter.ActualWidth;
        _meterFill.Width = ReadingFormat.IsAvailable(_latest)
            ? Math.Round(ReadingScale.Fraction(_latest!.Value, _meterRange.Min, _meterRange.Max) * width)
            : 0;
        if (_warnTick != null && _warn is { } w)
            _warnTick.Margin = new Thickness(Math.Round(ReadingScale.Fraction(w, _meterRange.Min, _meterRange.Max) * width) - 0.5, 0, 0, 0);
    }
}
