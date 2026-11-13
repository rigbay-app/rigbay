using Rigbay.Abstractions;

namespace Rigbay.Watch.Tests;

/// <summary>The page as data: it is valid for the host, the picker lists what the machine publishes, edits reach the rule, the suggestions are offered and added by a click.</summary>
public class PageTests
{
    private static void Catalogue(Rig rig, bool all = true)
    {
        rig.Bus.Describe("predatorsense", "cpu.temp", "CPU temperature");
        rig.Bus.Describe("predatorsense", "fan.cpu.rpm", "CPU fan speed", MetricUnit.Rpm);
        if (!all) return;
        rig.Bus.Describe("steam-bypass", "steam.proxy.errors", "Steam proxy errors", MetricUnit.Count);
        rig.Bus.Describe("tunnel", "tunnel.home.handshake_age", "Tunnel home: handshake age", MetricUnit.Seconds);
        rig.Bus.Describe("tunnel", "tunnel.work.handshake_age", "Tunnel work: handshake age", MetricUnit.Seconds);
        rig.Bus.Describe("gpu", "gpu.throttle", "GPU clock limit reasons", MetricUnit.None);
        rig.Bus.Describe("gpu", "gpu.nvml.temp", "GPU core temperature");
        rig.Bus.Describe("xtu", "cpu.power", "CPU package power", MetricUnit.Watt);
    }

    /// <summary>The host's own checks of a page (PageValidator), the ones a module can break.</summary>
    internal static void AssertValid(PageModel page)
    {
        Assert.NotNull(page.Groups);
        foreach (var group in page.Groups) Assert.False(string.IsNullOrWhiteSpace(group.HeaderKey));
        if (page.Primary is { } primary) Assert.IsNotType<ExpanderRow>(primary);
        foreach (var row in page.EnumerateRows())
        {
            Assert.False(string.IsNullOrWhiteSpace(row.HeaderKey), row.GetType().Name);
            Assert.NotNull(row.Enabled);
            switch (row)
            {
                case ChoiceRow c:
                    Assert.NotNull(c.Value);
                    Assert.NotEmpty(c.Options);
                    Assert.Equal(c.Options.Count, c.Options.Select(o => o.Id).Distinct().Count());
                    break;
                case NumberRow n:
                    Assert.True(n.Minimum <= n.Maximum, n.HeaderKey);
                    Assert.True(n.Step > 0, n.HeaderKey);
                    break;
                case ButtonRow b:
                    Assert.NotNull(b.InvokeAsync);
                    break;
                case ExpanderRow { HeaderRow: { } head }:
                    Assert.True(head is ToggleRow or ChoiceRow or ButtonRow or ValueRow or ReadingRow);
                    break;
            }
        }
    }

    private static ExpanderRow RuleRowOf(Rig rig) => rig.Rows().OfType<ExpanderRow>().Single();
    private static T Child<T>(ExpanderRow e, string header) where T : Row => e.Children.OfType<T>().Single(r => r.HeaderKey == header);

    // ------------------------------------------------------------------ the page itself

    [Fact]
    public void An_empty_module_on_an_empty_bus_has_a_valid_page_without_a_picker_that_has_nothing_to_pick()
    {
        var rig = new Rig().Start();
        var page = rig.Page();
        AssertValid(page);
        Assert.Contains(page.EnumerateRows(), r => r.HeaderKey == "info.no-rules");
        Assert.Contains(page.EnumerateRows(), r => r.HeaderKey == "info.no-readings");
        Assert.Contains(page.EnumerateRows(), r => r.HeaderKey == "info.suggest-none");
        Assert.DoesNotContain(page.EnumerateRows(), r => r.HeaderKey == "row.new.header");
        Assert.Equal("row.alerts.header", page.Primary!.HeaderKey);
    }

    [Fact]
    public void The_picker_lists_what_the_machine_publishes_by_module_with_the_value_it_has_now()
    {
        var rig = new Rig().Start();
        Catalogue(rig);
        rig.Bus.Put("cpu.temp", 61);
        var picker = rig.Row<ChoiceRow>("row.new.header");
        var labels = picker.Options.Select(o => rig.Host.Loc.T(o.LabelKey)).ToList();
        Assert.Contains("predatorsense · CPU temperature (61 °C)", labels);
        Assert.Contains("predatorsense · CPU fan speed", labels);          // no value yet: no brackets
        Assert.Equal(labels.Count, picker.Options.Count);
        AssertValid(rig.Page());
    }

    [Fact]
    public void A_rule_whose_reading_is_not_published_now_still_shows_it_in_its_picker()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        Catalogue(rig, all: false);
        rig.Bus.ClearCatalogue();
        rig.Bus.Describe("xtu", "cpu.power", "CPU package power", MetricUnit.Watt);
        var picker = Child<ChoiceRow>(RuleRowOf(rig), "row.metric.header");
        Assert.Contains(picker.Options, o => o.Id == "cpu.temp" && rig.Host.Loc.T(o.LabelKey) == "cpu.temp (not published now)");
        AssertValid(rig.Page());
    }

    [Fact]
    public void A_rule_is_an_expander_with_its_name_its_switch_its_live_line_and_the_editing_rows()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        Catalogue(rig);
        rig.Run("cpu.temp", 61, 10);
        var e = RuleRowOf(rig);
        Assert.Equal("CPU temperature", rig.Host.Loc.T(e.HeaderKey));
        var head = Assert.IsType<ToggleRow>(e.HeaderRow);
        Assert.True(head.Value.Value);
        Assert.Contains("Quiet · now 61 °C", rig.Text(e.Line!.Value));
        Assert.Equal(
            new[] { "row.metric.header", "row.kind.header", "row.limit.header", "row.clear.header", "row.for.header", "row.quiet.header", "row.interval.header",
                    "row.condition.header", "row.condition.metric.header", "row.condition.kind.header", "row.condition.limit.header", "row.remove.header" },
            e.Children.Select(c => c.HeaderKey));
        var limit = Child<NumberRow>(e, "row.limit.header");
        Assert.Equal(95, limit.Value.Value);
        Assert.Equal(MetricUnit.Celsius, limit.Unit);
        Assert.Equal(90, Child<NumberRow>(e, "row.clear.header").Value.Value);
        Assert.Equal(30, Child<NumberRow>(e, "row.for.header").Value.Value);
        Assert.Equal(15, Child<NumberRow>(e, "row.quiet.header").Value.Value);
        Assert.Equal(10, Child<NumberRow>(e, "row.interval.header").Value.Value);
        Assert.False(Child<ChoiceRow>(e, "row.condition.metric.header").Enabled.Value);   // greyed out until the condition is on
        Assert.False(Child<NumberRow>(e, "row.condition.limit.header").Enabled.Value);
        AssertValid(rig.Page());
    }

    // ------------------------------------------------------------------ edits

    [Fact]
    public void Editing_a_row_changes_the_saved_rule_and_what_is_checked()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        Catalogue(rig);
        var e = RuleRowOf(rig);
        Child<NumberRow>(e, "row.limit.header").Value.Value = 100;
        Child<NumberRow>(e, "row.for.header").Value.Value = 5;
        Child<NumberRow>(e, "row.quiet.header").Value.Value = 0;
        Child<NumberRow>(e, "row.interval.header").Value.Value = 20;
        var rule = rig.Module.Rules.Single();
        Assert.Equal(100, rule.Threshold);
        Assert.Equal(5, rule.Seconds);
        Assert.Equal(0, rule.QuietMinutes);
        Assert.Equal(20, rule.IntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(20), rig.Module.Subscribed["cpu.temp"]);
        Assert.Equal(100, ((WatchSettings)rig.Host.SettingsStore.Stored!).Rules.Single().Threshold);
        Assert.Contains("At or above 100 °C for 5 s", rig.Text(e.Line!.Value));
        Assert.Equal(0, rig.Host.PageRefreshRequests);                    // a number changes the line, not the page
    }

    [Fact]
    public void A_clearing_limit_on_the_wrong_side_is_refused_and_the_row_goes_back()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        var clear = Child<NumberRow>(RuleRowOf(rig), "row.clear.header");
        clear.Value.Value = 99;                                           // above the limit 95: the alert could never end
        Assert.Null(rig.Module.Rules.Single().ClearAt);
        Assert.Equal(95, clear.Value.Value);
        clear.Value.Value = 80;
        Assert.Equal(80, rig.Module.Rules.Single().ClearAt);
        clear.Value.Value = 95;                                           // the same as the limit: no band
        Assert.Null(rig.Module.Rules.Single().ClearAt);
    }

    [Fact]
    public void Moving_the_limit_below_the_clearing_limit_drops_the_clearing_limit_and_the_row_follows()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        var e = RuleRowOf(rig);
        Child<NumberRow>(e, "row.limit.header").Value.Value = 85;         // the clearing limit 90 is now above it
        Assert.Null(rig.Module.Rules.Single().ClearAt);
        Assert.Equal(85, Child<NumberRow>(e, "row.clear.header").Value.Value);
    }

    [Fact]
    public void The_kind_can_change_and_a_rise_needs_a_window_of_at_least_a_second()
    {
        var rig = Rig.With(Rig.CpuRule(r => r.Seconds = 0)).Start();
        var e = RuleRowOf(rig);
        Child<ChoiceRow>(e, "row.kind.header").Value.Value = "rises";
        Assert.Equal(RuleKind.Rises, rig.Module.Rules.Single().Kind);
        Assert.Equal(1, rig.Module.Rules.Single().Seconds);
        Assert.Equal(1, Child<NumberRow>(e, "row.for.header").Value.Value);
        Child<ChoiceRow>(e, "row.kind.header").Value.Value = "bits";
        Assert.Equal(RuleKind.Bits, rig.Module.Rules.Single().Kind);
        Assert.Null(rig.Module.Rules.Single().ClearAt);
    }

    [Fact]
    public void The_switch_in_the_header_turns_the_rule_off_and_on_and_stops_asking_for_its_reading()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        var head = (ToggleRow)RuleRowOf(rig).HeaderRow!;
        head.Value.Value = false;
        Assert.False(rig.Module.Rules.Single().Enabled);
        Assert.Empty(rig.Module.Subscribed);
        Assert.Contains("Off", rig.Text(RuleRowOf(rig).Line!.Value));
        head.Value.Value = true;
        Assert.Single(rig.Module.Subscribed);
    }

    [Fact]
    public void Another_reading_is_chosen_with_the_picker_and_the_page_is_rebuilt_with_that_rule_open()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        Catalogue(rig);
        Child<ChoiceRow>(RuleRowOf(rig), "row.metric.header").Value.Value = "cpu.power";
        Assert.Equal("cpu.power", rig.Module.Rules.Single().Metric);
        Assert.Equal(new[] { "cpu.power" }, rig.Module.Subscribed.Keys);
        Assert.Equal(1, rig.Host.PageRefreshRequests);
        var rebuilt = RuleRowOf(rig);
        Assert.True(rebuilt.IsExpanded);                                   // the rule the owner is editing stays open
        Assert.Equal("CPU package power", rig.Host.Loc.T(rebuilt.HeaderKey));
        Assert.Equal(MetricUnit.Watt, Child<NumberRow>(rebuilt, "row.limit.header").Unit);
    }

    [Fact]
    public void An_only_while_reading_is_turned_on_with_a_default_and_edited_like_the_rest()
    {
        var rig = Rig.With(Rig.CpuRule(r => { r.Metric = "fan.cpu.rpm"; r.Kind = RuleKind.Below; r.Threshold = 0; r.ClearAt = null; })).Start();
        Catalogue(rig);
        var e = RuleRowOf(rig);
        var on = Child<ToggleRow>(e, "row.condition.header");
        on.Value.Value = true;
        var rule = rig.Module.Rules.Single();
        Assert.True(rule.HasCondition);
        Assert.Equal("cpu.temp", rule.ConditionMetric);
        Assert.Equal(1, rig.Host.PageRefreshRequests);
        Assert.Equal(2, rig.Module.Subscribed.Count);
        Assert.True(Child<ChoiceRow>(e, "row.condition.metric.header").Enabled.Value);   // the same observable as the switch

        Child<NumberRow>(e, "row.condition.limit.header").Value.Value = 80;
        Child<ChoiceRow>(e, "row.condition.kind.header").Value.Value = "below";
        rule = rig.Module.Rules.Single();
        Assert.Equal(80, rule.ConditionThreshold);
        Assert.Equal(RuleKind.Below, rule.ConditionKind);

        on.Value.Value = false;
        Assert.False(rig.Module.Rules.Single().HasCondition);
        Assert.Single(rig.Module.Subscribed);
    }

    [Fact]
    public void With_nothing_to_pick_the_condition_switch_goes_back_off()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        var on = Child<ToggleRow>(RuleRowOf(rig), "row.condition.header");
        on.Value.Value = true;
        Assert.False(rig.Module.Rules.Single().HasCondition);
        Assert.False(on.Value.Value);
    }

    [Fact]
    public async Task Remove_asks_first_and_then_takes_the_rule_and_its_reading_away()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        var button = Child<ButtonRow>(RuleRowOf(rig), "row.remove.header");
        Assert.Equal("confirm.remove", button.ConfirmKey);
        var result = await button.InvokeAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Empty(rig.Module.Rules);
        Assert.Empty(rig.Bus.Asked);
        Assert.Equal(1, rig.Host.PageRefreshRequests);
        Assert.False((await button.InvokeAsync(CancellationToken.None)).Succeeded);   // already gone: a failed result, not an exception
    }

    // ------------------------------------------------------------------ add a rule

    [Fact]
    public async Task Adding_without_a_pick_says_so_and_adding_a_temperature_starts_from_90()
    {
        var rig = new Rig().Start();
        Catalogue(rig);
        Assert.False((await rig.Row<ButtonRow>("row.add.header").InvokeAsync(CancellationToken.None)).Succeeded);
        Assert.Empty(rig.Module.Rules);

        var page = rig.Page();                                            // one page: the picker and the button belong together
        page.EnumerateRows().OfType<ChoiceRow>().Single(r => r.HeaderKey == "row.new.header").Value.Value = "cpu.temp";
        var result = await page.EnumerateRows().OfType<ButtonRow>().Single(r => r.HeaderKey == "row.add.header").InvokeAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        var rule = Assert.Single(rig.Module.Rules);
        Assert.Equal("cpu.temp", rule.Metric);
        Assert.Equal(RuleKind.Above, rule.Kind);
        Assert.Equal(90, rule.Threshold);
        Assert.Equal(WatchRule.DefaultSeconds, rule.Seconds);
        Assert.Equal(WatchRule.DefaultQuietMinutes, rule.QuietMinutes);
        Assert.Equal(WatchRule.DefaultIntervalSeconds, rule.IntervalSeconds);
        Assert.Equal(1, rig.Host.PageRefreshRequests);
        Assert.True(RuleRowOf(rig).IsExpanded);                           // opened for editing
        Assert.Equal("cpu.temp", rig.Row<ChoiceRow>("row.new.header").Value.Value);                   // the pick is remembered across the rebuild
    }

    [Fact]
    public async Task A_reading_without_a_natural_scale_starts_a_little_above_its_value_now()
    {
        var rig = new Rig().Start();
        Catalogue(rig);
        rig.Bus.Put("cpu.power", 40, MetricUnit.Watt);
        rig.Row<ChoiceRow>("row.new.header").Value.Value = "cpu.power";
        await rig.Row<ButtonRow>("row.add.header").InvokeAsync(CancellationToken.None);
        Assert.Equal(44, rig.Module.Rules.Single().Threshold);
    }

    [Fact]
    public async Task The_twenty_fifth_rule_is_refused_with_a_sentence()
    {
        var rig = new Rig().Start();
        Catalogue(rig);
        for (int i = 0; i < WatchRule.MaxRules; i++) rig.Module.AddRule(Rig.CpuRule());
        rig.Row<ChoiceRow>("row.new.header").Value.Value = "cpu.temp";
        var result = await rig.Row<ButtonRow>("row.add.header").InvokeAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("at most 24", rig.Text(result.Message));
    }

    // ------------------------------------------------------------------ suggestions

    private static IEnumerable<ButtonRow> Offered(Rig rig) => rig.Rows().OfType<ButtonRow>().Where(b => b.ButtonKey == "row.suggest.add");

    [Fact]
    public void Nothing_is_a_rule_and_nothing_is_asked_for_until_the_owner_adds_a_suggestion()
    {
        var rig = new Rig().Start();
        Catalogue(rig);
        Assert.NotEmpty(Offered(rig));
        Assert.Empty(rig.Module.Rules);
        Assert.Empty(rig.Module.Subscribed);
        Assert.Equal(0, rig.Bus.EverAsked);
    }

    [Fact]
    public void A_suggestion_is_offered_only_when_a_module_publishes_its_reading()
    {
        var rig = new Rig().Start();
        Assert.Empty(Offered(rig));
        rig.Bus.Describe("predatorsense", "cpu.temp", "CPU temperature");
        Assert.Equal(new[] { "suggest.cpu-temp" }, Offered(rig).Select(b => b.HeaderKey));
        rig.Bus.Describe("predatorsense", "fan.cpu.rpm", "CPU fan speed", MetricUnit.Rpm);
        Assert.Contains(Offered(rig), b => b.HeaderKey == "suggest.fan-stopped");
        rig.Bus.Describe("tunnel", "tunnel.home.handshake_age", "Tunnel home: handshake age", MetricUnit.Seconds);
        rig.Bus.Describe("tunnel", "tunnel.work.handshake_age", "Tunnel work: handshake age", MetricUnit.Seconds);
        var tunnels = Offered(rig).Where(b => Text.IsLiteral(b.HeaderKey)).Select(b => Text.Unwrap(b.HeaderKey)).ToList();
        Assert.Equal(new[] { "Tunnel home: handshake age", "Tunnel work: handshake age" }, tunnels);   // one per tunnel, named by the tunnel module
        rig.Bus.Describe("gpu", "gpu.nvml.temp", "GPU core temperature");
        rig.Bus.Describe("steam-bypass", "steam.proxy.errors", "Steam proxy errors", MetricUnit.Count);
        rig.Bus.Describe("gpu", "gpu.throttle", "GPU clock limit reasons");
        Assert.Equal(7, Offered(rig).Count());
        AssertValid(rig.Page());
    }

    [Fact]
    public void Each_suggestion_makes_the_rule_the_plan_names()
    {
        var rig = new Rig().Start();
        Catalogue(rig);
        var byMetric = Suggestions.For(rig.Bus.GetAllMetricInfo()).ToDictionary(s => s.Rule.Metric, s => s.Rule);

        var cpu = byMetric["cpu.temp"];
        Assert.Equal((RuleKind.Above, 95.0, 30), (cpu.Kind, cpu.Threshold, cpu.Seconds));
        var gpu = byMetric["gpu.nvml.temp"];
        Assert.Equal((RuleKind.Above, 87.0, 30), (gpu.Kind, gpu.Threshold, gpu.Seconds));
        var tunnel = byMetric["tunnel.home.handshake_age"];
        Assert.Equal((RuleKind.Above, 180.0), (tunnel.Kind, tunnel.Threshold));
        var steam = byMetric["steam.proxy.errors"];
        Assert.Equal(RuleKind.Rises, steam.Kind);
        var thermal = byMetric["gpu.throttle"];
        Assert.Equal((RuleKind.Bits, 0x60), (thermal.Kind, (int)thermal.Threshold));        // software and hardware thermal bits
        var fan = byMetric["fan.cpu.rpm"];
        Assert.Equal((RuleKind.Below, 0.0, "cpu.temp", 80.0), (fan.Kind, fan.Threshold, fan.ConditionMetric, fan.ConditionThreshold));
        Assert.All(byMetric.Values, r => Assert.Equal(WatchRule.DefaultIntervalSeconds, r.IntervalSeconds));
        Assert.All(byMetric.Values, r => Assert.False(string.IsNullOrEmpty(r.Suggestion)));
        Assert.Equal(7, Suggestions.For(rig.Bus.GetAllMetricInfo()).Count);
    }

    [Fact]
    public async Task One_click_adds_the_rule_opens_it_and_stops_offering_it()
    {
        var rig = new Rig().Start();
        Catalogue(rig);
        var before = Offered(rig).Count();
        var button = Offered(rig).First(b => b.HeaderKey == "suggest.cpu-temp");
        Assert.Contains("At or above 95 °C for 30 s", Text.Unwrap(button.DescriptionKey!));
        var result = await button.InvokeAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        var rule = Assert.Single(rig.Module.Rules);
        Assert.Equal("cpu-temp", rule.Suggestion);
        Assert.True(rule.Enabled);
        Assert.Equal(1, rig.Bus.AskedCount("cpu.temp"));
        Assert.Equal(before - 1, Offered(rig).Count());
        Assert.DoesNotContain(Offered(rig), b => b.HeaderKey == "suggest.cpu-temp");
        Assert.Equal(1, rig.Host.PageRefreshRequests);
        // the same suggestion cannot be added twice
        Assert.DoesNotContain(Suggestions.For(rig.Bus.GetAllMetricInfo()).Where(s => rig.Module.Rules.All(r => r.Suggestion != s.Key)), s => s.Key == "cpu-temp");
    }

    [Fact]
    public async Task When_every_suggestion_is_added_the_page_says_so()
    {
        var rig = new Rig().Start();
        rig.Bus.Describe("predatorsense", "cpu.temp", "CPU temperature");
        await Offered(rig).Single().InvokeAsync(CancellationToken.None);
        Assert.Empty(Offered(rig));
        Assert.Contains(rig.Rows(), r => r.HeaderKey == "info.suggest-done");
    }

    // ------------------------------------------------------------------ the page follows the bus

    [Fact]
    public void Opening_the_page_asks_for_a_rebuild_so_the_picker_has_current_values_and_closing_does_not()
    {
        var rig = new Rig().Start();
        rig.Host.PageVisibleValue.Value = true;
        Assert.Equal(1, rig.Host.PageRefreshRequests);
        rig.Host.PageVisibleValue.Value = false;
        Assert.Equal(1, rig.Host.PageRefreshRequests);
    }

    [Fact]
    public void A_catalogue_change_while_the_page_is_closed_rebuilds_nothing()
    {
        var rig = new Rig().Start();
        rig.Bus.RaiseCatalogChanged();
        Assert.Equal(0, rig.Host.PageRefreshRequests);
    }
}
