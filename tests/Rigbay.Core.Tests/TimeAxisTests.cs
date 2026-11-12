using Xunit;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>
/// UF (polish before 0.1.2): the trend chart's time axis. With about 1.5 minutes of history in the 5-minute range it read
/// "-1 min, -1 min, -30 s, now": ticks every 30 s, and 60 s and 90 s were both rounded to "-1 min".
/// </summary>
public class TimeAxisTests
{
    static string Label(TimeTick t) => t.Unit switch
    {
        TimeTickUnit.Now => "now",
        TimeTickUnit.Seconds => $"-{t.Value} s",
        _ => $"-{t.Value} min",
    };

    static string[] Labels(TimeAxisPlan p) => p.Ticks.Select(Label).ToArray();

    // the history hands the chart the points inside the range, so data older than the range shows up as an oldest point one sample younger than it
    static double AgeSeen(double dataSeconds, int rangeSeconds) => Math.Min(dataSeconds, rangeSeconds - 1);

    [Fact]
    public void Ninety_seconds_in_the_five_minute_range_no_longer_says_one_minute_twice()
    {
        var p = TimeAxis.Plan(90, 300, plotWidth: 1000);
        Assert.Equal(new[] { "now", "-30 s", "-60 s", "-90 s" }, Labels(p));
        Assert.Equal(90, p.ShownSeconds);
        Assert.False(p.CoversRange);
    }

    [Theory]
    // range, data, plot width, expected labels (now first)
    [InlineData(60, 20, 1000, "now|-5 s|-10 s|-15 s|-20 s")]
    [InlineData(60, 90, 1000, "now|-15 s|-30 s|-45 s|-60 s")]
    [InlineData(60, 240, 1000, "now|-15 s|-30 s|-45 s|-60 s")]
    [InlineData(60, 720, 1000, "now|-15 s|-30 s|-45 s|-60 s")]
    [InlineData(300, 20, 1000, "now|-5 s|-10 s|-15 s|-20 s")]
    [InlineData(300, 90, 1000, "now|-30 s|-60 s|-90 s")]
    [InlineData(300, 240, 1000, "now|-1 min|-2 min|-3 min|-4 min")]
    [InlineData(300, 720, 1000, "now|-1 min|-2 min|-3 min|-4 min|-5 min")]
    [InlineData(600, 20, 1000, "now|-5 s|-10 s|-15 s|-20 s")]
    [InlineData(600, 90, 1000, "now|-30 s|-60 s|-90 s")]
    [InlineData(600, 240, 1000, "now|-1 min|-2 min|-3 min|-4 min")]
    [InlineData(600, 720, 1000, "now|-2 min|-4 min|-6 min|-8 min|-10 min")]
    // a narrow window (640 px): fewer, wider steps, still distinct
    [InlineData(60, 20, 450, "now|-15 s")]
    [InlineData(300, 90, 450, "now|-30 s|-60 s|-90 s")]
    [InlineData(600, 720, 450, "now|-2 min|-4 min|-6 min|-8 min|-10 min")]
    [InlineData(600, 720, 200, "now|-5 min|-10 min")]
    public void Every_range_and_data_age_gets_distinct_honest_labels(int range, double data, double width, string expected)
    {
        var p = TimeAxis.Plan(AgeSeen(data, range), range, width);
        Assert.Equal(expected.Split('|'), Labels(p));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    [InlineData(600)]
    public void No_label_repeats_none_lies_beyond_the_data_or_the_axis_and_labels_keep_their_distance(int range)
    {
        foreach (var width in new double[] { 160, 300, 450, 700, 1000, 1600 })
            for (double data = 0; data <= 800; data += 0.5)
            {
                double age = AgeSeen(data, range);
                var p = TimeAxis.Plan(age, range, width);
                var labels = Labels(p);
                string at = $"range {range}, data {data}, width {width}: {string.Join(", ", labels)}";
                Assert.True(labels.Distinct().Count() == labels.Length, at);
                Assert.True(p.Ticks.Count >= 1 && p.Ticks[0].Unit == TimeTickUnit.Now, at);
                Assert.True(p.Ticks.All(t => t.SecondsAgo <= p.ShownSeconds + 1e-6), at);
                Assert.True(p.Ticks.All(t => t.SecondsAgo <= age + 2 + 1e-6), at);
                // until the data covers the range no label lies beyond its oldest point at all
                Assert.True(p.CoversRange || p.Ticks.All(t => t.SecondsAgo <= age + 1e-6), at);
                Assert.True(p.StepSeconds / p.ShownSeconds * width >= TimeAxis.MinTickGap - 1e-6 || p.Ticks.Count == 1, at);
                Assert.True(p.Ticks.Count <= TimeAxis.MaxIntervals + 1, at);
                // one unit on the whole axis, and the value is what the position says
                Assert.True(p.Ticks.Skip(1).Select(t => t.Unit).Distinct().Count() <= 1, at);
                Assert.True(p.Ticks.All(t => t.Unit != TimeTickUnit.Minutes || t.Value * 60 == t.SecondsAgo), at);
                Assert.True(p.Ticks.All(t => t.Unit != TimeTickUnit.Seconds || t.Value == t.SecondsAgo), at);
                Assert.InRange(p.ShownSeconds, Math.Min(TimeAxis.MinSpan, range), range);
            }
    }

    [Fact]
    public void The_axis_spans_the_data_until_it_covers_the_range_but_never_less_than_a_minute()
    {
        Assert.Equal(60, TimeAxis.Plan(0, 300).ShownSeconds);
        Assert.Equal(60, TimeAxis.Plan(20, 300).ShownSeconds);
        Assert.Equal(150, TimeAxis.Plan(150, 300).ShownSeconds);
        // one sample short of the range is the whole range (the history keeps what is inside it)
        var full = TimeAxis.Plan(298.5, 300, toleranceSeconds: 2);
        Assert.True(full.CoversRange);
        Assert.Equal(300, full.ShownSeconds);
        Assert.Equal("-5 min", Label(full.Ticks[^1]));
        Assert.False(TimeAxis.Plan(290, 300, toleranceSeconds: 2).CoversRange);
    }

    [Fact]
    public void A_sample_spacing_does_not_stretch_the_labels_past_the_data()
    {
        // seen in a render: points 12 s apart, the oldest 26 s old, and a "-30 s" label left of where the line starts
        var p = TimeAxis.Plan(26, 60, plotWidth: 800, toleranceSeconds: 12);
        Assert.Equal(new[] { "now", "-10 s", "-20 s" }, Labels(p));
        Assert.False(p.CoversRange);
        // the tolerance still lets a full range count as covered
        Assert.True(TimeAxis.Plan(50, 60, plotWidth: 800, toleranceSeconds: 12).CoversRange);
    }

    [Fact]
    public void A_reading_that_just_started_is_labelled_now_only()
    {
        var p = TimeAxis.Plan(1, 300, plotWidth: 1000);
        Assert.Equal(new[] { "now" }, Labels(p));
        Assert.Equal(60, p.ShownSeconds);
    }

    [Fact]
    public void Nonsense_input_still_gives_an_axis()
    {
        Assert.Equal(new[] { "now" }, Labels(TimeAxis.Plan(double.NaN, 300)));
        Assert.Equal(new[] { "now" }, Labels(TimeAxis.Plan(-5, 300)));
        Assert.Equal(60, TimeAxis.Plan(double.PositiveInfinity, 60, double.NaN).ShownSeconds);
    }
}
