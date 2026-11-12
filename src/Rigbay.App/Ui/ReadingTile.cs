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
/// (or any reading whose module gave a range), a thin meter of where it sits in 0 to 100. The number wears the text colour, the hue is on the marks.
/// A temperature at 90 °C or more says "High" in words with an icon, so the state never depends on a colour.
/// <para><b>Why a tile and not the round gauge Acer's own app shows.</b> A gauge spends about four times the area of a tile on one number and shows
/// nothing about where it is going; at idle a CPU sits at 40 to 60 °C, where the needle looks the same all day. The tile gives the same number
/// at the same size, the trend beside it and the meter for the "how close to the limit" question.</para>
/// </summary>
sealed class ReadingTile : UserControl
{
    /// <summary>A temperature at or above this many °C is called High (the label is a word and an icon, not just a colour).</summary>
    public const double HighCelsius = 90;

    readonly ModuleEntry _e;
    readonly ReadingRow _row;
    readonly StackPanel _value = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly TextBlock _number = new() { Style = Ui.Style("RbTileValue"), TextTrimming = TextTrimming.None };
    readonly TextBlock _unit = new() { Style = Ui.Style("RbSecondaryText"), FontSize = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock _unavailable;
    readonly StackPanel _high;
    readonly Sparkline _spark;
    readonly Grid? _meter;
    readonly Rectangle? _meterFill;
    readonly string _name;
    MetricSample? _latest;

    public Sparkline Spark => _spark;
    public string ValueText => _unavailable.Visibility == Visibility.Visible ? _unavailable.Text : _number.Text + (_unit.Text.Length > 0 ? " " + _unit.Text : "");
    public bool ShowsHigh => _high.Visibility == Visibility.Visible;
    public double MeterShare => _meterFill is { } f && _meter is { ActualWidth: > 0 } m ? f.Width / m.ActualWidth : 0;
    public bool HasMeter => _meter != null;

    public ReadingTile(ModuleEntry e, ReadingRow row, MetricUnit unit, PageScope scope)
    {
        _e = e; _row = row;
        _name = e.Localisation.T(row.HeaderKey);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var frame = new Border { Style = Ui.Style("RbTileFrame"), Padding = new Thickness(12, 10, 12, 10), MinHeight = 108 };

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
        _high.Children.Add(new FontIcon { Glyph = "", FontSize = 11 });
        _high.Children.Add(Ui.Text(Host.T("tile.high"), "RbSecondaryCaption"));
        Grid.SetColumn(_high, 1);
        head.Children.Add(_high);
        grid.Children.Add(head);

        _value.Children.Add(_number);
        _value.Children.Add(_unit);
        _unavailable = Ui.Text(Host.T("core.reading.unavailable"), "RbSecondaryText");
        _unavailable.FontSize = 16;
        _unavailable.Visibility = Visibility.Collapsed;
        var valueHost = new Grid { MinHeight = 40 };
        valueHost.Children.Add(_value);
        valueHost.Children.Add(_unavailable);
        Grid.SetRow(valueHost, 1);
        grid.Children.Add(valueHost);

        _spark = new Sparkline(row.Metric, row.Color, 0, 36, area: true);
        _spark.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetRow(_spark, 2);
        grid.Children.Add(_spark);

        var natural = row.RangeMin is { } a && row.RangeMax is { } b ? (a, b) : ReadingScale.NaturalRange(unit);
        if (natural is { } range)
        {
            _meter = new Grid { Height = 3, Margin = new Thickness(0, 8, 0, 0) };
            _meter.Children.Add(new Rectangle { Style = Ui.Style("RbMeterTrack") });
            _meterFill = new Rectangle { Style = ChartKit.Key(ChartKit.Hue(row.Color, unit)), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _meter.Children.Add(_meterFill);
            _meter.SizeChanged += (_, _) => ApplyMeter();
            _meterRange = range;
            Grid.SetRow(_meter, 3);
            grid.Children.Add(_meter);
        }
        frame.Child = grid;
        Content = frame;

        AutomationProperties.SetName(this, _name);

        Show(Host.Core.Metrics.TryGetLatest(row.Metric, out var latest) ? latest : null);
        scope.Live(
            () => Host.Core.Metrics.Subscribe(s => Ui.Post(e, () => { if (!scope.Disposed && scope.Active) Show(s); }), row.Metric),
            () => Show(Host.Core.Metrics.TryGetLatest(row.Metric, out var l) ? l : null));
        scope.Tick(_spark.Redraw);
    }

    (double Min, double Max) _meterRange;

    void Show(MetricSample? s)
    {
        _latest = s;
        bool ok = ReadingFormat.IsAvailable(s);
        _value.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        _unavailable.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (ok)
        {
            _number.Text = s!.Value.ToString("F" + Math.Max(0, _row.Decimals), CultureInfo.InvariantCulture);
            var symbol = s.Unit.Symbol();
            _unit.Text = symbol;
        }
        _high.Visibility = ok && s!.Unit == MetricUnit.Celsius && s.Value >= HighCelsius ? Visibility.Visible : Visibility.Collapsed;
        ApplyMeter();
        AutomationProperties.SetName(this, ok ? $"{_name}, {_number.Text} {_unit.Text}{(_high.Visibility == Visibility.Visible ? ", " + Host.T("tile.high") : "")}" : $"{_name}, {_unavailable.Text}");
    }

    void ApplyMeter()
    {
        if (_meter is null || _meterFill is null || _meter.ActualWidth <= 0) return;
        _meterFill.Width = ReadingFormat.IsAvailable(_latest)
            ? Math.Round(ReadingScale.Fraction(_latest!.Value, _meterRange.Min, _meterRange.Max) * _meter.ActualWidth)
            : 0;
    }
}
