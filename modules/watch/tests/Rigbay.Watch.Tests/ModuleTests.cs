using Rigbay.Abstractions;

namespace Rigbay.Watch.Tests;

/// <summary>The module on a fake host: what it asks the bus for, what it shows, what it sends, the verbs, the pause, the page and its edits.</summary>
public class ModuleTests
{
    private static void Catalogue(Rig rig)
    {
        rig.Bus.Describe("predatorsense", "cpu.temp", "CPU temperature");
        rig.Bus.Describe("predatorsense", "fan.cpu.rpm", "CPU fan speed", MetricUnit.Rpm);
        rig.Bus.Describe("steam-bypass", "steam.proxy.errors", "Steam proxy errors", MetricUnit.Count);
        rig.Bus.Describe("xtu", "cpu.power", "CPU package power", MetricUnit.Watt);
    }

    // ------------------------------------------------------------------ what it asks for

    [Fact]
    public void It_asks_for_readings_in_the_background_only_never_as_a_reader()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        Assert.Equal(0, rig.Bus.PlainSubscribes);                        // a Subscribe would hold the publisher at its fast rate all day
        Assert.Equal(1, rig.Bus.AskedCount("cpu.temp"));
        Assert.Equal(TimeSpan.FromSeconds(10), rig.Module.Subscribed["cpu.temp"]);
        Assert.Equal(0, rig.Bus.Publishes);
        Assert.Equal(0, rig.Host.ApprovalService.Asked);
    }

    [Fact]
    public void The_interval_is_the_rules_own_and_a_shared_reading_gets_the_shortest()
    {
        var rig = Rig.With(Rig.CpuRule(r => r.IntervalSeconds = 30), Rig.CpuRule(r => { r.Id = "b"; r.IntervalSeconds = 15; r.Metric = "cpu.power"; r.ConditionMetric = "cpu.temp"; r.ConditionThreshold = 70; })).Start();
        Assert.Equal(TimeSpan.FromSeconds(15), rig.Module.Subscribed["cpu.temp"]);
        Assert.Equal(TimeSpan.FromSeconds(15), rig.Module.Subscribed["cpu.power"]);
        Assert.Equal(2, rig.Module.Subscribed.Count);
    }

    [Fact]
    public void A_switched_off_rule_asks_for_nothing()
    {
        var rig = Rig.With(Rig.CpuRule(r => r.Enabled = false)).Start();
        Assert.Empty(rig.Module.Subscribed);
        Assert.Equal(0, rig.Bus.EverAsked);
    }

    [Fact]
    public void Adding_removing_and_switching_a_rule_changes_what_is_asked_for_and_leaves_nothing_behind()
    {
        var rig = new Rig().Start();
        Assert.Empty(rig.Module.Subscribed);
        var rule = rig.Module.AddRule(Rig.CpuRule())!;
        Assert.Equal(1, rig.Bus.AskedCount("cpu.temp"));
        rig.Module.UpdateRule(rule.Id, r => r.Enabled = false);
        Assert.Equal(0, rig.Bus.AskedCount("cpu.temp"));
        rig.Module.UpdateRule(rule.Id, r => r.Enabled = true);
        rig.Module.UpdateRule(rule.Id, r => r.IntervalSeconds = 60);
        Assert.Equal(1, rig.Bus.AskedCount("cpu.temp"));                 // the 10 s ask was dropped when the interval changed
        Assert.Equal(TimeSpan.FromSeconds(60), rig.Module.Subscribed["cpu.temp"]);
        Assert.True(rig.Module.RemoveRule(rule.Id));
        Assert.Empty(rig.Bus.Asked);
        Assert.False(rig.Module.RemoveRule(rule.Id));
    }

    [Fact]
    public void The_last_sample_on_the_bus_is_used_at_once_but_only_if_it_is_fresh()
    {
        var rig = new Rig();
        rig.Bus.Put("cpu.temp", 61);
        rig.Clock.Advance(5);
        rig.Host.SettingsStore.Stored = new WatchSettings { Rules = { Rig.CpuRule() } };
        rig.Start();
        Assert.Contains("now 61", rig.Line("cpu1"));                     // seeded: no waiting for the next sample

        var old = new Rig();
        old.Bus.Put("cpu.temp", 61);
        old.Clock.Advance(300);
        old.Host.SettingsStore.Stored = new WatchSettings { Rules = { Rig.CpuRule() } };
        old.Start();
        Assert.Contains("Waiting for cpu.temp", old.Line("cpu1"));       // too old to be a value
    }

    [Fact]
    public void Shutdown_drops_every_ask_and_every_timer_and_later_samples_change_nothing()
    {
        var rig = Rig.With(Rig.CpuRule(r => r.Seconds = 0)).Start();
        rig.Module.Shutdown();
        Assert.Empty(rig.Bus.Asked);
        rig.Bus.Put("cpu.temp", 99);
        rig.Module.Tick();
        Assert.Equal(0, rig.Host.ToastService.Count);
        rig.Host.PageVisibleValue.Value = true;                          // the handler is gone
        Assert.Equal(0, rig.Host.PageRefreshRequests);
        Assert.False(rig.Module.TimerArmed);
    }

    [Fact]
    public void It_can_be_turned_on_again_after_it_was_turned_off()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        rig.Module.Shutdown();
        rig.Host.CommandRegistry.Verbs.Clear();                           // the host removes a module's verbs and tray items when it stops
        rig.Host.TrayService.Items.Clear();
        rig.Module.Initialize(rig.Host);
        Assert.Equal(1, rig.Bus.AskedCount("cpu.temp"));
        Assert.Single(rig.Module.Rules);
    }

    [Fact]
    public void The_timer_runs_only_while_a_rule_is_on()
    {
        var rig = new Rig(autoTick: true).Start();
        Assert.False(rig.Module.TimerArmed);                             // nothing to check: nothing wakes
        var rule = rig.Module.AddRule(Rig.CpuRule())!;
        Assert.True(rig.Module.TimerArmed);
        rig.Module.UpdateRule(rule.Id, r => r.Enabled = false);
        Assert.False(rig.Module.TimerArmed);
        rig.Module.Shutdown();
        Assert.False(rig.Module.TimerArmed);
    }

    // ------------------------------------------------------------------ the alert

    [Fact]
    public void A_rule_that_fires_shows_one_Warning_notification_from_the_background_with_the_numbers()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        rig.Bus.Describe("predatorsense", "cpu.temp", "CPU temperature");
        rig.Run("cpu.temp", 97, 120);
        var toast = Assert.Single(rig.Host.ToastService.Shown);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Equal(ToastOrigin.Background, toast.Origin);
        Assert.Equal("CPU temperature", rig.Text(toast.Title));
        Assert.Equal("At or above 95 °C for 30 s. Now 97 °C.", rig.Text(toast.Body));
        Assert.Contains("alert: cpu.temp", rig.Host.LogSink.All());
    }

    [Fact]
    public void The_alert_text_follows_the_language_and_the_condition_is_in_it()
    {
        var rig = Rig.With(new WatchRule
        {
            Id = "fan", Metric = "fan.cpu.rpm", Kind = RuleKind.Below, Threshold = 0, Seconds = 20, QuietMinutes = 0,
            ConditionMetric = "cpu.temp", ConditionKind = RuleKind.Above, ConditionThreshold = 80,
        }).Start();
        Catalogue(rig);
        for (int i = 0; i < 3; i++) { rig.Clock.Advance(10); rig.Bus.Put("cpu.temp", 85); rig.Bus.Put("fan.cpu.rpm", 0, MetricUnit.Rpm); rig.Module.Tick(); }
        var toast = Assert.Single(rig.Host.ToastService.Shown);
        Assert.Equal("CPU fan speed", rig.Text(toast.Title));
        Assert.Equal("At or below 0 rpm for 20 s, only while CPU temperature is at or above 80 °C. Now 0 rpm.", rig.Text(toast.Body));

        rig.Host.Loc.Switch("vi");
        Assert.Contains("chỉ khi CPU temperature từ 80 °C trở lên", rig.Line("fan"));
    }

    [Fact]
    public void Nothing_is_shown_for_a_reading_that_stays_under_its_limit_or_for_a_stale_one()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        rig.Run("cpu.temp", 70, 300);
        rig.Clock.Advance(600);
        rig.Module.Tick();
        Assert.Equal(0, rig.Host.ToastService.Count);
        Assert.Contains("Waiting for cpu.temp", rig.Line("cpu1"));
    }

    [Fact]
    public void A_notification_that_throws_is_logged_and_the_other_alerts_of_the_check_still_go_out()
    {
        var rig = Rig.With(Rig.CpuRule(r => r.Seconds = 0), Rig.CpuRule(r => { r.Id = "two"; r.Metric = "cpu.power"; r.Seconds = 0; })).Start();
        rig.Host.ToastService.ThrowOnShow = true;
        rig.Bus.Put("cpu.temp", 99);
        rig.Bus.Put("cpu.power", 99, MetricUnit.Watt);
        rig.Module.Tick();                                                // does not throw
        Assert.Equal(2, rig.Host.LogSink.Count(LogLevel.Error));
        Assert.Equal(2, rig.Host.ToastService.Attempts);
    }

    [Fact]
    public void The_test_alert_is_a_Warning_from_the_background_like_a_real_one()
    {
        var rig = new Rig().Start();
        rig.Module.SendTestAlert();
        var toast = Assert.Single(rig.Host.ToastService.Shown);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Equal(ToastOrigin.Background, toast.Origin);
        Assert.Equal("Watch", rig.Text(toast.Title));
    }

    // ------------------------------------------------------------------ pause

    [Fact]
    public void Pausing_holds_the_alert_back_and_it_is_sent_when_the_pause_is_over_if_the_episode_is_still_open()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        rig.Module.Pause(TimeSpan.FromMinutes(30));
        Assert.False(((ToggleRow)rig.Page().Primary!).Value.Value);
        Assert.True(rig.Module.Tray!.Checked.Value);
        rig.Run("cpu.temp", 97, 600);                                     // 10 minutes over the limit
        Assert.Equal(0, rig.Host.ToastService.Count);
        Assert.Contains("Firing since", rig.Line("cpu1"));
        rig.Run("cpu.temp", 97, 1300);                                    // the pause ends at 30 minutes
        Assert.Equal(1, rig.Host.ToastService.Count);
        Assert.True(((ToggleRow)rig.Page().Primary!).Value.Value);
        Assert.False(rig.Module.Tray!.Checked.Value);
        Assert.Null(rig.Module.PausedUntil);
    }

    [Fact]
    public void The_toggle_pauses_for_an_hour_and_turning_it_back_on_resumes()
    {
        var rig = new Rig().Start();
        var toggle = (ToggleRow)rig.Page().Primary!;
        Assert.True(toggle.Value.Value);
        toggle.Value.Value = false;
        Assert.Equal(rig.Clock.Now + TimeSpan.FromHours(1), rig.Module.PausedUntil);
        Assert.Contains("paused until", rig.Text(toggle.Line!.Value));
        toggle.Value.Value = true;
        Assert.Null(rig.Module.PausedUntil);
        Assert.Equal("", rig.Text(toggle.Line!.Value));
    }

    [Fact]
    public void The_tray_item_toggles_the_pause_and_shows_only_when_the_owner_asked_for_it()
    {
        var rig = new Rig().Start();
        var item = Assert.Single(rig.Host.TrayService.Items);
        Assert.Equal("tray.pause", item.TextKey);
        Assert.True(item.Checkable);
        Assert.False(item.Visible.Value);                                 // off by default: the owner turns it on
        rig.Row<ToggleRow>("row.tray.header").Value.Value = true;
        Assert.True(item.Visible.Value);
        Assert.True(((WatchSettings)rig.Host.SettingsStore.Stored!).TrayItem);
        item.Invoke();
        Assert.True(item.Checked.Value);
        Assert.NotNull(rig.Module.PausedUntil);
        item.Invoke();
        Assert.False(item.Checked.Value);
        Assert.Null(rig.Module.PausedUntil);
    }

    // ------------------------------------------------------------------ verbs

    [Fact]
    public void The_verbs_are_declared_read_only_with_a_usage_line_because_they_change_only_the_modules_own_state()
    {
        var rig = new Rig().Start();
        Assert.Equal(new[] { "pause", "resume", "status" }, rig.Host.CommandRegistry.Verbs.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (info, _) in rig.Host.CommandRegistry.Verbs.Values)
        {
            Assert.Equal(VerbAccess.ReadOnly, info.Access);
            Assert.NotNull(info.UsageKey);
            Assert.Null(info.Effect);
            Assert.True(rig.Host.Loc.Has(info.DescriptionKey));
            Assert.True(rig.Host.Loc.Has(info.UsageKey!));
        }
    }

    [Fact]
    public async Task Status_lists_each_rule_with_its_state_and_says_whether_alerts_are_on()
    {
        var rig = Rig.With(Rig.CpuRule(), Rig.CpuRule(r => { r.Id = "off"; r.Metric = "cpu.power"; r.Enabled = false; })).Start();
        rig.Run("cpu.temp", 61, 10);
        var result = await rig.Host.CommandRegistry.Run("status");
        Assert.Equal(0, result.ExitCode);
        var lines = result.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("Alerts are on.", lines[0]);
        Assert.Equal("cpu.temp: At or above 95 °C for 30 s · Quiet · now 61 °C", lines[1]);
        Assert.Equal("cpu.power: At or above 95 for 30 s · Off", lines[2]);
        Assert.Equal(2, (await rig.Host.CommandRegistry.Run("status", "x")).ExitCode);
    }

    [Fact]
    public async Task Status_with_no_rules_says_so()
    {
        var rig = new Rig().Start();
        var result = await rig.Host.CommandRegistry.Run("status");
        Assert.Contains("No rules yet", result.Output);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1441")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("")]
    public async Task Pause_wants_one_whole_number_of_minutes_from_1_to_1440(string argument)
    {
        var rig = new Rig().Start();
        var result = await rig.Host.CommandRegistry.Run("pause", argument.Length == 0 ? Array.Empty<string>() : new[] { argument });
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("rigbay watch pause", result.Output);
        Assert.Null(rig.Module.PausedUntil);
        Assert.Equal(2, (await rig.Host.CommandRegistry.Run("pause", "5", "6")).ExitCode);
    }

    [Fact]
    public async Task Pause_and_resume_change_only_the_alerts_state()
    {
        var rig = new Rig().Start();
        var result = await rig.Host.CommandRegistry.Run("pause", "90");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(rig.Clock.Now + TimeSpan.FromMinutes(90), rig.Module.PausedUntil);
        Assert.Contains("paused until", result.Output);
        var status = await rig.Host.CommandRegistry.Run("status");
        Assert.StartsWith("Alerts paused until", status.Output);
        Assert.Equal(0, (await rig.Host.CommandRegistry.Run("resume")).ExitCode);
        Assert.Null(rig.Module.PausedUntil);
        Assert.Equal(2, (await rig.Host.CommandRegistry.Run("resume", "now")).ExitCode);
        Assert.Equal(0, rig.Host.ApprovalService.Asked);
        Assert.Equal(0, rig.Bus.Publishes);
    }

    // ------------------------------------------------------------------ the saved settings

    [Fact]
    public void Rules_are_saved_when_they_change_and_come_back_when_the_module_starts_again()
    {
        var rig = new Rig().Start();
        var added = rig.Module.AddRule(Rig.CpuRule())!;
        var saved = Assert.IsType<WatchSettings>(rig.Host.SettingsStore.Stored);
        Assert.Equal("cpu.temp", Assert.Single(saved.Rules).Metric);
        Assert.NotEqual("cpu1", added.Id);                               // the module gives its own id

        var again = new Rig(saved: saved).Start();
        Assert.Equal(added.Id, Assert.Single(again.Module.Rules).Id);
        Assert.Equal(95, again.Module.Rules[0].Threshold);
    }

    [Fact]
    public void A_settings_file_with_nonsense_in_it_loads_as_what_can_be_kept()
    {
        var rig = new Rig(saved: new WatchSettings { Rules = { new WatchRule { Id = "x", Metric = "BAD" }, Rig.CpuRule(r => r.IntervalSeconds = 0) } }).Start();
        var rule = Assert.Single(rig.Module.Rules);
        Assert.Equal(WatchRule.MinIntervalSeconds, rule.IntervalSeconds);
    }

    [Fact]
    public void The_list_stops_at_the_limit()
    {
        var rig = new Rig().Start();
        for (int i = 0; i < WatchRule.MaxRules; i++) Assert.NotNull(rig.Module.AddRule(Rig.CpuRule()));
        Assert.Null(rig.Module.AddRule(Rig.CpuRule()));
        Assert.Equal(WatchRule.MaxRules, rig.Module.Rules.Count);
        Assert.True(rig.Module.Subscribed.Count <= 64);
    }

    [Fact]
    public void An_edit_that_makes_the_rule_unusable_is_refused_and_the_old_rule_stays()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        Assert.False(rig.Module.UpdateRule("cpu1", r => r.Metric = "NOT VALID"));
        Assert.Equal("cpu.temp", rig.Module.Rules[0].Metric);
        Assert.False(rig.Module.UpdateRule("nope", r => r.Threshold = 1));
    }

    // ------------------------------------------------------------------ the state line and the language

    [Fact]
    public void The_state_line_counts_rules_that_are_on_and_firing_and_says_when_alerts_are_paused()
    {
        var rig = new Rig().Start();
        Assert.Equal("No rules yet", rig.Text(rig.Page().Summary!.StateLine!.Value));
        rig.Module.AddRule(Rig.CpuRule(r => r.Seconds = 0));
        Assert.Equal("Rules on: 1", rig.Text(rig.Page().Summary!.StateLine!.Value));
        rig.Run("cpu.temp", 99, 10);
        Assert.Equal("Firing: 1 of 1", rig.Text(rig.Page().Summary!.StateLine!.Value));
        rig.Module.UpdateRule(rig.Module.Rules[0].Id, r => r.Enabled = false);
        Assert.Equal("All rules off", rig.Text(rig.Page().Summary!.StateLine!.Value));
    }

    [Fact]
    public void Switching_the_language_rewrites_the_lines_of_the_rules()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        rig.Run("cpu.temp", 61, 10);
        Assert.Contains("Quiet", rig.Line("cpu1"));
        rig.Host.Loc.Switch("vi");
        Assert.Contains("Yên", rig.Line("cpu1"));
        Assert.Contains("Từ 95 °C trở lên trong 30 giây", rig.Line("cpu1"));
    }
}
