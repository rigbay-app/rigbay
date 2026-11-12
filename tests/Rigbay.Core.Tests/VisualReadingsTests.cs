using Xunit;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>V1: the history behind the charts (bounded ring per metric, fake clock), the scale rules and the plan of tiles and charts.</summary>
public class VisualReadingsTests
{
    private static readonly MetricId Temp = new("cpu.temp");
    private static readonly MetricId Power = new("cpu.power");
    private static readonly MetricId Pl1 = new("cpu.power.pl1");
    private static readonly MetricId Usage = new("cpu.usage");
    private static readonly MetricId Clock = new("cpu.clock");
    private static readonly MetricId Flag = new("xtu.throttle");

    private sealed class FakeClock
    {
        public DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Get() => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    private static (MetricsBus Bus, FakeClock Clock) NewBus(int maxPoints = 300, int maxMetrics = 256)
    {
        var clock = new FakeClock();
        var history = new MetricHistory(clock.Get, maxPoints: maxPoints, maxMetrics: maxMetrics);
        return (new MetricsBus(new ListLog(), clock.Get, history), clock);
    }

    // ------------------------------------------------------------ history

    [Fact]
    public void Published_samples_reach_the_history_in_order_with_the_bus_clock()
    {
        var (bus, clock) = NewBus();
        for (int i = 0; i < 5; i++) { bus.Publish("m", Temp, 50 + i, MetricUnit.Celsius); clock.Advance(2); }
        var points = bus.History.Get(Temp);
        Assert.Equal(new[] { 50.0, 51, 52, 53, 54 }, points.Select(p => p.Value));
        Assert.True(points.Zip(points.Skip(1), (a, b) => a.Time < b.Time).All(x => x));
    }

    [Fact]
    public void Samples_in_one_two_second_slot_are_averaged_into_one_point()
    {
        var (bus, clock) = NewBus();
        clock.Now = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);   // a slot boundary: 600 s / 300 = 2 s
        bus.Publish("m", Power, 10, MetricUnit.Watt);
        clock.Advance(0.5); bus.Publish("m", Power, 20, MetricUnit.Watt);
        clock.Advance(0.5); bus.Publish("m", Power, 30, MetricUnit.Watt);
        clock.Advance(1.5); bus.Publish("m", Power, 100, MetricUnit.Watt);   // next slot
        var points = bus.History.Get(Power);
        Assert.Equal(2, points.Count);
        Assert.Equal(20, points[0].Value);
        Assert.Equal(100, points[1].Value);
    }

    [Fact]
    public void A_fast_source_costs_no_more_than_a_slow_one_and_the_count_never_passes_the_cap()
    {
        var (bus, clock) = NewBus();
        for (int i = 0; i < 6000; i++) { bus.Publish("m", Temp, 40 + i % 7, MetricUnit.Celsius); clock.Advance(0.2); }   // 20 minutes at 5 Hz
        Assert.InRange(bus.History.PointCount(Temp), 290, bus.History.MaxPoints + 1);
        var points = bus.History.Get(Temp);
        Assert.True(points[^1].Time - points[0].Time <= MetricHistory.DefaultWindow);
    }

    [Fact]
    public void Points_older_than_the_window_are_gone_even_when_nothing_new_arrives()
    {
        var (bus, clock) = NewBus();
        bus.Publish("m", Temp, 60, MetricUnit.Celsius);
        Assert.Single(bus.History.Get(Temp));
        clock.Advance(601);
        Assert.Empty(bus.History.Get(Temp));
    }

    [Fact]
    public void Get_can_ask_for_a_shorter_span()
    {
        var (bus, clock) = NewBus();
        for (int i = 0; i < 300; i++) { bus.Publish("m", Temp, i, MetricUnit.Celsius); clock.Advance(2); }
        var last60 = bus.History.Get(Temp, TimeSpan.FromSeconds(60));
        Assert.InRange(last60.Count, 29, 31);
        Assert.True(last60[0].Time >= clock.Now - TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Withdrawing_a_metric_clears_its_history_and_publishing_again_starts_a_new_one()
    {
        var (bus, clock) = NewBus();
        for (int i = 0; i < 10; i++) { bus.Publish("m", Temp, 50, MetricUnit.Celsius); clock.Advance(2); }
        Assert.True(bus.Withdraw("m", Temp));
        Assert.Empty(bus.History.Get(Temp));
        bus.Publish("m", Temp, 70, MetricUnit.Celsius);
        Assert.Equal(new[] { 70.0 }, bus.History.Get(Temp).Select(p => p.Value));
    }

    [Fact]
    public void An_unavailable_sample_clears_too_and_a_withdraw_by_another_module_does_not()
    {
        var (bus, _) = NewBus();
        bus.Publish("a", Temp, 50, MetricUnit.Celsius);
        Assert.False(bus.Withdraw("b", Temp));            // not b's sample
        Assert.Single(bus.History.Get(Temp));
        bus.Publish(new MetricSample(Temp, double.NaN, MetricUnit.Celsius, DateTimeOffset.Now, "a") { IsAvailable = false });
        Assert.Empty(bus.History.Get(Temp));
    }

    [Fact]
    public void Non_finite_values_never_get_in()
    {
        var (bus, _) = NewBus();
        bus.Publish("m", Temp, double.NaN, MetricUnit.Celsius);
        bus.Publish("m", Temp, double.PositiveInfinity, MetricUnit.Celsius);
        Assert.Empty(bus.History.Get(Temp));
    }

    [Fact]
    public void The_history_is_not_a_subscriber_so_it_never_keeps_a_module_polling()
    {
        var (bus, _) = NewBus();
        bus.Publish("m", Temp, 50, MetricUnit.Celsius);
        Assert.Equal(0, bus.SubscriberCount(Temp));
        using var sub = bus.Subscribe(_ => { }, Temp);
        Assert.Equal(1, bus.SubscriberCount(Temp));
    }

    [Fact]
    public void The_number_of_tracked_metrics_is_capped_and_the_worst_case_is_small()
    {
        var (bus, _) = NewBus(maxMetrics: 3);
        foreach (var id in new[] { "a.one", "a.two", "a.three", "a.four" }) bus.Publish("m", new MetricId(id), 1, MetricUnit.None);
        Assert.Equal(3, bus.History.TrackedCount);
        Assert.Empty(bus.History.Get(new MetricId("a.four")));
        Assert.True(new MetricHistory().WorstCaseBytes < 2_500_000, $"worst case {new MetricHistory().WorstCaseBytes} bytes");
    }

    [Fact]
    public void An_older_sample_from_another_thread_is_dropped_not_reordered()
    {
        var (bus, clock) = NewBus();
        clock.Advance(100);
        bus.Publish("m", Temp, 60, MetricUnit.Celsius);
        bus.Publish(new MetricSample(Temp, 10, MetricUnit.Celsius, clock.Now - TimeSpan.FromSeconds(50), "m"));
        Assert.Equal(new[] { 60.0 }, bus.History.Get(Temp).Select(p => p.Value));
    }

    [Fact]
    public void Version_grows_with_every_change_and_per_metric()
    {
        var (bus, clock) = NewBus();
        long v0 = bus.History.Version;
        bus.Publish("m", Temp, 50, MetricUnit.Celsius);
        Assert.True(bus.History.Version > v0);
        Assert.Equal(0, bus.History.VersionOf(Power));
        long t1 = bus.History.VersionOf(Temp);
        clock.Advance(3); bus.Publish("m", Temp, 51, MetricUnit.Celsius);
        Assert.True(bus.History.VersionOf(Temp) > t1);
    }

    [Fact]
    public void A_gap_splits_a_line_into_runs()
    {
        var t0 = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        var pts = new List<HistoryPoint>();
        for (int i = 0; i < 10; i++) pts.Add(new HistoryPoint(t0 + TimeSpan.FromSeconds(i * 2), 50));
        for (int i = 0; i < 5; i++) pts.Add(new HistoryPoint(t0 + TimeSpan.FromSeconds(120 + i * 2), 55));   // the module slept for 100 s
        var gap = MetricHistory.GapFor(pts);
        Assert.Equal(8, gap.TotalSeconds);
        var runs = MetricHistory.Segments(pts, gap);
        Assert.Equal(new[] { 10, 5 }, runs.Select(r => r.Count));
        Assert.Single(MetricHistory.Segments(pts.Take(10).ToList(), gap));
        Assert.Empty(MetricHistory.Segments(Array.Empty<HistoryPoint>(), gap));
    }

    [Fact]
    public void A_slow_source_gets_a_wider_gap_so_its_line_is_not_cut()
    {
        var t0 = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        var pts = Enumerable.Range(0, 10).Select(i => new HistoryPoint(t0 + TimeSpan.FromSeconds(i * 10), 5)).ToList();   // one sample every 10 s
        Assert.Equal(30, MetricHistory.GapFor(pts).TotalSeconds);
        Assert.Single(MetricHistory.Segments(pts, MetricHistory.GapFor(pts)));
    }

    // ------------------------------------------------------------ scale

    [Fact]
    public void One_degree_of_noise_is_not_blown_up_to_a_full_height_line()
    {
        var (lo, hi) = ReadingScale.Fit(58, 59, MetricUnit.Celsius);
        Assert.True(hi - lo >= 10, $"span {hi - lo}");
        Assert.InRange(58.5, lo, hi);
        // 1 W of noise on a 12 W package reading: the line uses a tenth of the height at most (1 of 10+ W)
        var (plo, phi) = ReadingScale.Fit(12, 13, MetricUnit.Watt);
        Assert.True((13 - 12) / (phi - plo) <= 0.1);
    }

    [Fact]
    public void A_real_swing_uses_the_height_with_a_little_room()
    {
        var (lo, hi) = ReadingScale.Fit(40, 90, MetricUnit.Celsius);
        Assert.True(lo < 40 && hi > 90 && hi - lo < 60);
    }

    [Fact]
    public void A_percent_scale_slides_inside_0_to_100_and_a_watt_scale_never_goes_below_zero()
    {
        var (lo, hi) = ReadingScale.Fit(0, 2, MetricUnit.Percent);
        Assert.Equal(0, lo); Assert.True(hi >= 20);
        var (lo2, hi2) = ReadingScale.Fit(98, 100, MetricUnit.Percent);
        Assert.Equal(100, hi2); Assert.True(lo2 <= 80);
        var (lo3, _) = ReadingScale.Fit(0, 0.5, MetricUnit.Watt);
        Assert.Equal(0, lo3);
        Assert.Equal((0, 1), ReadingScale.Fit(double.NaN, 1, MetricUnit.Watt));
    }

    [Fact]
    public void The_axis_ends_on_round_numbers_with_three_to_six_ticks()
    {
        var (lo, hi, ticks) = ReadingScale.Axis(47, 83, MetricUnit.Celsius);
        Assert.True(lo <= 47 && hi >= 83);
        Assert.InRange(ticks.Count, 3, 7);
        Assert.Equal(lo, ticks[0], 6); Assert.Equal(hi, ticks[^1], 6);
        double step = ticks[1] - ticks[0];
        Assert.Contains(step, new[] { 5.0, 10, 20, 25, 50 });
        Assert.All(ticks, t => Assert.Equal(0, t % step, 6));
    }

    [Fact]
    public void A_fixed_range_from_the_module_is_kept_and_widens_to_hold_the_data()
    {
        var (lo, hi, _) = ReadingScale.Axis(40, 60, MetricUnit.Celsius, 0, 100);
        Assert.Equal((0, 100), (lo, hi));
        var (_, hi2, _) = ReadingScale.Axis(40, 120, MetricUnit.Celsius, 0, 100);
        Assert.True(hi2 >= 120);
    }

    [Theory]
    [InlineData(0.07, 0.1)] [InlineData(1.0, 1)] [InlineData(1.1, 2)] [InlineData(3, 5)] [InlineData(7, 10)] [InlineData(42, 50)] [InlineData(0, 1)]
    public void NiceStep_is_1_2_or_5_times_a_power_of_ten(double raw, double expected) => Assert.Equal(expected, ReadingScale.NiceStep(raw), 9);

    [Fact]
    public void Tick_labels_have_the_decimals_their_step_needs()
    {
        Assert.Equal("40", ReadingScale.FormatTick(40, 10));
        Assert.Equal("0.5", ReadingScale.FormatTick(0.5, 0.5));
        Assert.Equal("0.25", ReadingScale.FormatTick(0.25, 0.05));
    }

    [Fact]
    public void Fraction_clamps_and_survives_an_empty_range()
    {
        Assert.Equal(0.5, ReadingScale.Fraction(50, 0, 100));
        Assert.Equal(1, ReadingScale.Fraction(150, 0, 100));
        Assert.Equal(0, ReadingScale.Fraction(5, 3, 3));
        Assert.Equal(0, ReadingScale.Fraction(double.NaN, 0, 100));
    }

    // ------------------------------------------------------------ plan

    private static MetricUnit Unit(MetricId id) =>
        id == Temp ? MetricUnit.Celsius : id == Power || id == Pl1 ? MetricUnit.Watt : id == Usage ? MetricUnit.Percent : id == Clock ? MetricUnit.Megahertz : id == Flag ? MetricUnit.Count : MetricUnit.None;

    private static PageModel PageOf(params Row[] rows) => new() { Groups = new[] { new RowGroup("g", rows) } };

    [Fact]
    public void Without_hints_an_xtu_like_page_gets_four_tiles_one_per_kind_and_a_chart_per_unit()
    {
        var page = PageOf(
            new ReadingRow("t", Temp), new ReadingRow("p", Power), new ReadingRow("u", Usage), new ReadingRow("c", Clock),
            new ReadingRow("l1", Pl1),
            new ExpanderRow("x", new Row[] { new ReadingRow("f", Flag) }));
        var plan = ReadingPlan.Build(page, Unit);
        Assert.Equal(new[] { Temp, Power, Usage, Clock }, plan.Tiles.Select(t => t.Row.Metric));
        Assert.Equal(new[] { "unit:Celsius", "unit:Watt", "unit:Percent", "unit:Megahertz" }, plan.Charts.Select(c => c.Id));
        Assert.Equal(new[] { Power, Pl1 }, plan.Charts[1].Series.Select(s => s.Metric));   // power together
        Assert.DoesNotContain(plan.Charts, c => c.Unit == MetricUnit.Count);               // a 0/1 flag is not charted next to real readings
    }

    [Fact]
    public void The_summary_readings_come_first_among_the_tiles()
    {
        var page = new PageModel
        {
            Groups = new[] { new RowGroup("g", new Row[] { new ReadingRow("t", Temp), new ReadingRow("p", Power), new ReadingRow("u", Usage), new ReadingRow("c", Clock) }) },
            Summary = new PageSummary(null, new[] { Clock, Usage }),
        };
        var plan = ReadingPlan.Build(page, Unit);
        Assert.Equal(new[] { Clock, Usage, Temp, Power }, plan.Tiles.Select(t => t.Row.Metric));
    }

    [Fact]
    public void Hints_choose_the_tiles_and_never_beats_always()
    {
        var page = PageOf(new ReadingRow("t", Temp) { Tile = TileHint.Never }, new ReadingRow("p", Power), new ReadingRow("l1", Pl1) { Tile = TileHint.Always });
        var plan = ReadingPlan.Build(page, Unit);
        Assert.Equal(new[] { Pl1, Power }, plan.Tiles.Select(t => t.Row.Metric));
    }

    [Fact]
    public void A_page_with_only_counts_still_gets_a_tile_and_a_chart()
    {
        var page = PageOf(new ReadingRow("a", Flag), new ReadingRow("b", new MetricId("x.errors")));
        var plan = ReadingPlan.Build(page, id => MetricUnit.Count);
        Assert.Equal(2, plan.Tiles.Count);
        Assert.Single(plan.Charts);
        Assert.Equal(2, plan.Charts[0].Series.Count);
    }

    [Fact]
    public void A_module_can_group_a_chart_and_keep_a_reading_out_of_every_chart()
    {
        var page = PageOf(
            new ReadingRow("t", Temp) { ChartGroupKey = "chart.heat" }, new ReadingRow("p", Power) { ChartGroupKey = "chart.heat" },   // two units in one group
            new ReadingRow("u", Usage) { ChartGroupKey = "" },
            new ReadingRow("c", Clock));
        var plan = ReadingPlan.Build(page, Unit);
        Assert.Equal(new[] { "key:chart.heat", "unit:Megahertz" }, plan.Charts.Select(c => c.Id));
        Assert.Equal("chart.heat", plan.Charts[0].TitleKey);
        Assert.Equal(new[] { Temp }, plan.Charts[0].Series.Select(s => s.Metric));   // one axis: the watt series stays out of a celsius chart
    }

    [Fact]
    public void A_chart_never_has_more_than_six_series_and_a_page_without_readings_has_nothing()
    {
        var rows = Enumerable.Range(0, 9).Select(i => (Row)new ReadingRow("r" + i, new MetricId("cpu.core" + i))).ToArray();
        var plan = ReadingPlan.Build(PageOf(rows), _ => MetricUnit.Celsius);
        Assert.Equal(6, plan.Charts[0].Series.Count);
        Assert.Equal(4, plan.Tiles.Count);
        Assert.True(ReadingPlan.Build(PageOf(new ToggleRow("x", new ObservableValue<bool>(true))), Unit).IsEmpty);
    }

    [Fact]
    public void The_hue_follows_the_unit_unless_the_row_says_otherwise()
    {
        Assert.Equal(SemanticColor.Temperature, ReadingPlan.ColorOf(SemanticColor.Auto, MetricUnit.Celsius));
        Assert.Equal(SemanticColor.Power, ReadingPlan.ColorOf(SemanticColor.Auto, MetricUnit.Watt));
        Assert.Equal(SemanticColor.Load, ReadingPlan.ColorOf(SemanticColor.Auto, MetricUnit.Percent));
        Assert.Equal(SemanticColor.Speed, ReadingPlan.ColorOf(SemanticColor.Auto, MetricUnit.Megahertz));
        Assert.Equal(SemanticColor.Neutral, ReadingPlan.ColorOf(SemanticColor.Auto, MetricUnit.Count));
        Assert.Equal(SemanticColor.Ok, ReadingPlan.ColorOf(SemanticColor.Ok, MetricUnit.Celsius));
    }

    [Fact]
    public void The_page_validator_refuses_an_empty_range()
    {
        var bad = PageOf(new ReadingRow("t", Temp) { RangeMin = 100, RangeMax = 0 });
        Assert.Contains(Rigbay.Core.PageValidator.Validate(bad), p => p.Contains("RangeMin"));
        var good = PageOf(new ReadingRow("t", Temp) { RangeMin = 0, RangeMax = 100 });
        Assert.DoesNotContain(Rigbay.Core.PageValidator.Validate(good), p => p.Contains("RangeMin"));
    }
}
