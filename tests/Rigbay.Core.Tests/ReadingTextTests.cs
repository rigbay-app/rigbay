using System.Globalization;
using Xunit;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>
/// UI review package B, part 2: readings in the current language's number format (VR-8), byte rates scaled to KB/s and MB/s with the unit on the
/// axis (VR-3), and only readings with a value as tiles (VR-2).
/// </summary>
public class ReadingTextTests
{
    private static readonly CultureInfo En = ReadingFormat.CultureFor("en");
    private static readonly CultureInfo Vi = ReadingFormat.CultureFor("vi");

    [Theory]
    [InlineData(3448, MetricUnit.Rpm, 0, "3,448", "rpm")]
    [InlineData(4072, MetricUnit.Megahertz, 0, "4,072", "MHz")]
    [InlineData(21.24, MetricUnit.Watt, 1, "21.2", "W")]
    [InlineData(82, MetricUnit.Celsius, 0, "82", "°C")]
    [InlineData(7, MetricUnit.None, 0, "7", "")]
    public void English_groups_thousands_and_keeps_the_point(double value, MetricUnit unit, int decimals, string number, string symbol)
    {
        Assert.Equal(new ReadingText(number, symbol), ReadingFormat.Parts(value, unit, decimals, En));
    }

    [Fact]
    public void Vietnamese_writes_a_decimal_comma_and_a_dot_between_thousands()
    {
        Assert.Equal("21,2 W", ReadingFormat.Parts(21.24, MetricUnit.Watt, 1, Vi).ToString());
        Assert.Equal("3.448 rpm", ReadingFormat.Parts(3448, MetricUnit.Rpm, 0, Vi).ToString());
        Assert.Equal("17,6 KB/s", ReadingFormat.Parts(17978, MetricUnit.BytesPerSecond, 0, Vi).ToString());
    }

    [Theory]
    [InlineData(980, "980", "B/s")]
    [InlineData(2497, "2.4", "KB/s")]
    [InlineData(17978, "17.6", "KB/s")]
    [InlineData(180_000, "176", "KB/s")]
    [InlineData(1_258_291, "1.2", "MB/s")]
    [InlineData(3_221_225_472.0, "3.0", "GB/s")]
    public void A_byte_rate_scales_with_one_decimal_under_a_hundred(double value, string number, string symbol)
    {
        Assert.Equal(new ReadingText(number, symbol), ReadingFormat.Parts(value, MetricUnit.BytesPerSecond, 3, En));
    }

    [Fact]
    public void Bytes_scale_too_and_a_plain_number_of_bytes_has_no_decimals()
    {
        Assert.Equal("42.0 KB", ReadingFormat.Parts(43_008, MetricUnit.Bytes, 0, En).ToString());
        Assert.Equal("512 B", ReadingFormat.Parts(512, MetricUnit.Bytes, 2, En).ToString());
        Assert.Equal((1024 * 1024.0, "MB/s"), ReadingFormat.Scale(MetricUnit.BytesPerSecond, 5_000_000));
        Assert.Equal((1.0, "°C"), ReadingFormat.Scale(MetricUnit.Celsius, 5_000_000));
    }

    [Fact]
    public void Format_uses_the_language_of_the_catalog()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        host.Metrics.Publish("m", new MetricId("cpu.power"), 21.24, MetricUnit.Watt);
        var row = new ReadingRow("p", new MetricId("cpu.power")) { Decimals = 1 };
        Assert.Equal("21.2 W", ReadingFormat.Format(host.Languages, row, host.Metrics.Latest(new MetricId("cpu.power"))));
        host.Languages.SetLanguage("vi");
        Assert.Equal("21,2 W", ReadingFormat.Format(host.Languages, row, host.Metrics.Latest(new MetricId("cpu.power"))));
    }

    [Fact]
    public void An_unknown_language_falls_back_to_the_invariant_format()
    {
        Assert.Same(CultureInfo.InvariantCulture, ReadingFormat.CultureFor("xx-nope"));
        Assert.Same(CultureInfo.InvariantCulture, ReadingFormat.CultureFor(null));
        Assert.Same(CultureInfo.InvariantCulture, ReadingFormat.CultureFor("en"));
    }

    [Fact]
    public void Axis_labels_are_grouped_in_the_language_and_the_top_one_names_the_unit()
    {
        Assert.Equal(new[] { "0", "5", "10", "15", "20 KB/s" }, ReadingScale.TickLabels(new double[] { 0, 5, 10, 15, 20 }, "KB/s", En));
        Assert.Equal(new[] { "3,000", "3,500", "4,000 MHz" }, ReadingScale.TickLabels(new double[] { 3000, 3500, 4000 }, "MHz", En));
        Assert.Equal(new[] { "0,5", "1,0", "1,5 V" }, ReadingScale.TickLabels(new double[] { 0.5, 1.0, 1.5 }, "V", Vi));
        Assert.Equal(new[] { "1", "2" }, ReadingScale.TickLabels(new double[] { 1, 2 }, "", En));
        Assert.Empty(ReadingScale.TickLabels(Array.Empty<double>(), "W", En));
    }

    [Fact]
    public void Only_readings_with_a_value_become_tiles_but_every_chart_is_planned()
    {
        var temp = new MetricId("cpu.temp");
        var conn = new MetricId("steam.proxy.active");
        var page = new PageModel { Groups = new[] { new RowGroup("g", new Row[] { new ReadingRow("t", temp), new ReadingRow("c", conn) }) } };
        MetricUnit Unit(MetricId id) => id == temp ? MetricUnit.Celsius : MetricUnit.Count;
        var plan = ReadingPlan.Build(page, Unit, id => id == temp);
        Assert.Equal(new[] { temp }, plan.Tiles.Select(t => t.Row.Metric));
        Assert.Contains(plan.Charts, c => c.Series.Any(s => s.Metric == temp));
        var none = ReadingPlan.Build(page, Unit, _ => false);
        Assert.Empty(none.Tiles);
        Assert.NotEmpty(none.Charts);
    }
}
