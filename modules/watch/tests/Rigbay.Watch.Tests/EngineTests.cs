using Rigbay.Abstractions;

namespace Rigbay.Watch.Tests;

/// <summary>The rules against readings and a fake clock: threshold, duration, hysteresis, one alert per episode, the quiet period, no data, the condition, pause.</summary>
public class EngineTests
{
    private readonly FakeClock _clock = new();
    private readonly RuleEngine _engine;

    public EngineTests() => _engine = new RuleEngine(_clock.Func);

    private static WatchRule Rule(Action<WatchRule>? change = null) => Rig.CpuRule(change);

    private void Put(string id, double value, MetricUnit unit = MetricUnit.Celsius) =>
        _engine.Record(new MetricSample(new MetricId(id), value, unit, _clock.Now, "test"));

    /// <summary>One step of the timer: time passes, the publisher sends a fresh reading, the rules are checked.</summary>
    private IReadOnlyList<Alert> Step(double value, double seconds = 10, string id = "cpu.temp", bool paused = false)
    {
        _clock.Advance(seconds);
        Put(id, value);
        return _engine.Evaluate(paused);
    }

    private int Steps(double value, int count, double seconds = 10, string id = "cpu.temp") =>
        Enumerable.Range(0, count).Sum(_ => Step(value, seconds, id).Count);

    private RuleView View(string id = "cpu1") => _engine.View(id)!;

    // ------------------------------------------------------------------ threshold and duration

    [Fact]
    public void A_reading_under_the_limit_is_quiet_and_never_alerts()
    {
        _engine.SetRules(new[] { Rule() });
        Assert.Equal(0, Steps(60, 20));
        Assert.Equal(RuleState.Quiet, View().State);
        Assert.Equal(60, View().Value);
    }

    [Fact]
    public void A_rule_that_has_seen_nothing_yet_is_waiting_not_quiet()
    {
        _engine.SetRules(new[] { Rule() });
        Assert.Equal(RuleState.Waiting, View().State);
        Assert.Equal("cpu.temp", View().WaitingFor);
    }

    [Fact]
    public void It_fires_only_after_the_reading_stayed_over_the_limit_for_the_whole_time()
    {
        _engine.SetRules(new[] { Rule() });
        Assert.Empty(Step(96));                     // t0: the breach is seen, 0 s of 30 s
        Assert.Equal(RuleState.Pending, View().State);
        Assert.Empty(Step(96));                     // 10 s
        Assert.Empty(Step(96));                     // 20 s
        var alerts = Step(96);                      // 30 s
        var alert = Assert.Single(alerts);
        Assert.Equal(96, alert.Value);
        Assert.Equal("cpu1", alert.Rule.Id);
        Assert.Equal(RuleState.Firing, View().State);
        Assert.Equal(_clock.Now, View().FiringSince);
        Assert.Equal(_clock.Now, View().LastAlert);
    }

    [Fact]
    public void A_dip_below_the_limit_starts_the_time_again()
    {
        _engine.SetRules(new[] { Rule(r => r.ClearAt = null) });
        Steps(96, 3);                               // 20 s over
        Step(80);                                   // back under: the count is dropped
        Assert.Equal(RuleState.Quiet, View().State);
        Assert.Equal(0, Steps(96, 3));              // 20 s again, still not 30
        Assert.Single(Step(96));
    }

    [Fact]
    public void A_reading_exactly_at_the_limit_counts_for_above_and_for_below()
    {
        _engine.SetRules(new[] { Rule(r => r.Seconds = 0), Rule(r => { r.Id = "low"; r.Metric = "cpu.fan"; r.Kind = RuleKind.Below; r.Threshold = 0; r.ClearAt = null; r.Seconds = 0; }) });
        Assert.Single(Step(95));
        _clock.Advance(10); Put("cpu.fan", 0, MetricUnit.Rpm);
        Assert.Single(_engine.Evaluate(false));
    }

    [Fact]
    public void A_time_of_zero_fires_at_the_first_check_that_sees_the_breach()
    {
        _engine.SetRules(new[] { Rule(r => r.Seconds = 0) });
        Assert.Single(Step(99));
    }

    [Fact]
    public void A_below_rule_fires_when_the_reading_stays_at_or_under_the_limit()
    {
        _engine.SetRules(new[] { Rule(r => { r.Metric = "fan.cpu.rpm"; r.Kind = RuleKind.Below; r.Threshold = 500; r.ClearAt = null; r.Seconds = 20; }) });
        Assert.Empty(Step(100, id: "fan.cpu.rpm"));
        Assert.Empty(Step(100, id: "fan.cpu.rpm"));
        Assert.Single(Step(100, id: "fan.cpu.rpm"));
        Step(900, id: "fan.cpu.rpm");
        Assert.Equal(RuleState.Quiet, View().State);
    }

    // ------------------------------------------------------------------ hysteresis, one alert per episode

    [Fact]
    public void An_open_episode_alerts_once_however_long_it_lasts()
    {
        _engine.SetRules(new[] { Rule() });
        Assert.Equal(1, Steps(97, 40));             // 400 s over the limit
        Assert.Equal(RuleState.Firing, View().State);
    }

    [Fact]
    public void A_reading_between_the_two_limits_does_not_end_the_episode_and_does_not_start_one()
    {
        _engine.SetRules(new[] { Rule(r => r.QuietMinutes = 0) });
        Assert.Equal(1, Steps(97, 4));              // fires
        Step(93);                                   // inside the band 90..95
        Assert.Equal(RuleState.Firing, View().State);
        Step(96);                                   // over again: still the same episode
        Assert.Equal(RuleState.Firing, View().State);
        Step(89);                                   // at or under 90: over
        Assert.Equal(RuleState.Quiet, View().State);
        Assert.Null(View().FiringSince);
    }

    [Fact]
    public void Inside_the_band_a_new_episode_does_not_begin()
    {
        _engine.SetRules(new[] { Rule(r => r.QuietMinutes = 0) });
        Assert.Equal(0, Steps(93, 20));             // 93 is under the limit 95: never pending
        Assert.Equal(RuleState.Quiet, View().State);
    }

    [Fact]
    public void A_clearing_limit_equal_to_the_limit_ends_the_episode_as_soon_as_the_reading_drops_under_it()
    {
        _engine.SetRules(new[] { Rule(r => { r.ClearAt = null; r.QuietMinutes = 0; }) });
        Assert.Equal(1, Steps(96, 4));
        Step(94.9);
        Assert.Equal(RuleState.Quiet, View().State);
    }

    [Fact]
    public void A_new_episode_after_the_old_one_ended_needs_the_whole_time_again_and_alerts_again()
    {
        _engine.SetRules(new[] { Rule(r => r.QuietMinutes = 0) });
        Assert.Equal(1, Steps(97, 4));
        Step(80);
        Assert.Equal(0, Steps(97, 3));
        Assert.Equal(1, Steps(97, 1));
    }

    // ------------------------------------------------------------------ the quiet period

    [Fact]
    public void After_an_alert_the_quiet_period_holds_back_the_next_episodes_alert_until_it_is_over()
    {
        _engine.SetRules(new[] { Rule() });         // quiet 15 min
        Assert.Equal(1, Steps(97, 4));              // alert at t = 40 s
        Step(80);
        Assert.Equal(0, Steps(97, 4));              // a second episode starts and is Firing, but nobody is told
        Assert.Equal(RuleState.Firing, View().State);
        Assert.NotNull(View().NextAlertAfter);
        Assert.Equal(1, Steps(97, 90));             // 900 s later the episode is still open: told once, now
        Assert.Null(View().NextAlertAfter);
    }

    [Fact]
    public void An_episode_that_began_and_ended_inside_the_quiet_period_is_never_announced()
    {
        _engine.SetRules(new[] { Rule() });
        Assert.Equal(1, Steps(97, 4));
        Step(80);
        Assert.Equal(0, Steps(97, 4));
        Step(80);                                   // it ended
        Assert.Equal(0, Steps(60, 120));            // and the quiet period runs out with nothing open
    }

    [Fact]
    public void A_quiet_period_of_zero_never_holds_an_alert_back()
    {
        _engine.SetRules(new[] { Rule(r => r.QuietMinutes = 0) });
        for (int i = 0; i < 3; i++) { Assert.Equal(1, Steps(97, 4)); Step(80); }
    }

    // ------------------------------------------------------------------ no data

    [Fact]
    public void A_reading_older_than_twice_the_interval_is_no_data_and_the_rule_waits_for_its_module()
    {
        _engine.SetRules(new[] { Rule() });         // interval 10 s: 20 s is the most
        Steps(60, 1);
        Assert.Equal(RuleState.Quiet, View().State);
        _clock.Advance(20); _engine.Evaluate(false);
        Assert.Equal(RuleState.Quiet, View().State);   // exactly 20 s old: still data
        _clock.Advance(1); _engine.Evaluate(false);
        Assert.Equal(RuleState.Waiting, View().State);
        Assert.Equal("cpu.temp", View().WaitingFor);
        Assert.Null(View().Value);
    }

    [Fact]
    public void Old_data_does_not_fire_a_rule_however_hot_the_last_value_was()
    {
        _engine.SetRules(new[] { Rule() });
        Put("cpu.temp", 99);
        _clock.Advance(300);                        // the publisher never sent another
        Assert.Empty(_engine.Evaluate(false));
        Assert.Equal(RuleState.Waiting, View().State);
    }

    [Fact]
    public void A_gap_in_the_data_drops_the_time_already_counted()
    {
        _engine.SetRules(new[] { Rule() });
        Steps(97, 3);                               // 20 s over
        _clock.Advance(60); Assert.Empty(_engine.Evaluate(false));   // nothing arrived for a minute: waiting
        Assert.Equal(RuleState.Waiting, View().State);
        Assert.Equal(0, Steps(97, 3));              // data is back: the 30 s count starts from zero
        Assert.Equal(1, Steps(97, 1));
    }

    [Fact]
    public void No_data_neither_fires_nor_clears_an_open_episode()
    {
        _engine.SetRules(new[] { Rule(r => r.QuietMinutes = 0) });
        Assert.Equal(1, Steps(97, 4));
        _clock.Advance(120); Assert.Empty(_engine.Evaluate(false));
        var view = View();
        Assert.Equal(RuleState.Waiting, view.State);
        Assert.True(view.InEpisode);                // the episode is still open: it could not be told it ended
        Assert.Equal(0, Steps(97, 3));              // and it was not announced a second time
        Step(80);
        Assert.Equal(RuleState.Quiet, View().State);
    }

    [Fact]
    public void A_withdrawn_reading_is_no_data()
    {
        _engine.SetRules(new[] { Rule() });
        Steps(60, 1);
        _engine.Record(new MetricSample(new MetricId("cpu.temp"), double.NaN, MetricUnit.None, _clock.Now, "test") { IsAvailable = false });
        _engine.Evaluate(false);
        Assert.Equal(RuleState.Waiting, View().State);
    }

    [Fact]
    public void An_episode_that_began_without_data_is_not_announced_while_the_reading_is_missing()
    {
        _engine.SetRules(new[] { Rule(r => r.Seconds = 0) });
        Assert.Single(Step(99, paused: false));
        // paused and then without data: when the pause is over there is nothing fresh to announce on
        var engine2 = new RuleEngine(_clock.Func);
        engine2.SetRules(new[] { Rule(r => r.Seconds = 0) });
        engine2.Record(new MetricSample(new MetricId("cpu.temp"), 99, MetricUnit.Celsius, _clock.Now, "test"));
        Assert.Empty(engine2.Evaluate(paused: true));
        _clock.Advance(120);
        Assert.Empty(engine2.Evaluate(paused: false));
        Assert.Equal(RuleState.Waiting, engine2.View("cpu1")!.State);
    }

    // ------------------------------------------------------------------ the condition

    private static WatchRule FanRule(Action<WatchRule>? change = null)
    {
        var rule = new WatchRule
        {
            Id = "fan", Metric = "fan.cpu.rpm", Kind = RuleKind.Below, Threshold = 0, Seconds = 20, QuietMinutes = 0, IntervalSeconds = 10,
            ConditionMetric = "cpu.temp", ConditionKind = RuleKind.Above, ConditionThreshold = 80,
        };
        change?.Invoke(rule);
        return rule;
    }

    private IReadOnlyList<Alert> Both(double rpm, double temp, double seconds = 10)
    {
        _clock.Advance(seconds);
        Put("fan.cpu.rpm", rpm, MetricUnit.Rpm);
        Put("cpu.temp", temp);
        return _engine.Evaluate(false);
    }

    [Fact]
    public void A_stopped_fan_alerts_only_while_the_cpu_is_hot()
    {
        _engine.SetRules(new[] { FanRule() });
        for (int i = 0; i < 10; i++) Assert.Empty(Both(0, 60));     // fan at 0 rpm, CPU cool: nothing
        Assert.Equal(RuleState.Quiet, View("fan").State);
        Assert.Empty(Both(0, 85));                                  // hot: the 20 s start now
        Assert.Empty(Both(0, 85));
        Assert.Single(Both(0, 85));
    }

    [Fact]
    public void The_episode_ends_when_the_condition_stops_holding()
    {
        _engine.SetRules(new[] { FanRule() });
        Both(0, 85); Both(0, 85); Assert.Single(Both(0, 85));
        Both(0, 70);                                                // CPU cooled: no longer a problem
        Assert.Equal(RuleState.Quiet, View("fan").State);
    }

    [Fact]
    public void A_running_fan_never_alerts_however_hot_the_cpu()
    {
        _engine.SetRules(new[] { FanRule() });
        for (int i = 0; i < 10; i++) Assert.Empty(Both(3000, 99));
    }

    [Fact]
    public void Without_a_fresh_condition_reading_the_rule_waits_for_that_module()
    {
        _engine.SetRules(new[] { FanRule() });
        Put("fan.cpu.rpm", 0, MetricUnit.Rpm);                      // the fan is read, the temperature never arrives
        _engine.Evaluate(false);
        Assert.Equal(RuleState.Waiting, View("fan").State);
        Assert.Equal("cpu.temp", View("fan").WaitingFor);
    }

    // ------------------------------------------------------------------ pause

    [Fact]
    public void While_alerts_are_paused_an_episode_still_opens_but_nothing_is_sent_until_the_pause_ends()
    {
        _engine.SetRules(new[] { Rule() });
        for (int i = 0; i < 6; i++) Assert.Empty(Step(97, paused: true));
        Assert.Equal(RuleState.Firing, View().State);
        Assert.Null(View().LastAlert);
        Assert.Single(Step(97, paused: false));                     // the pause is over and the episode is still open
        Assert.Empty(Step(97, paused: false));
    }

    [Fact]
    public void An_episode_that_ends_during_a_pause_is_never_announced()
    {
        _engine.SetRules(new[] { Rule() });
        for (int i = 0; i < 6; i++) Step(97, paused: true);
        Step(80, paused: true);
        Assert.Equal(0, Steps(80, 5));
        Assert.Null(View().LastAlert);
    }

    // ------------------------------------------------------------------ a rise, a mask

    private static WatchRule ErrorsRule(Action<WatchRule>? change = null)
    {
        var rule = new WatchRule { Id = "err", Metric = "steam.proxy.errors", Kind = RuleKind.Rises, Threshold = 5, Seconds = 60, QuietMinutes = 0, IntervalSeconds = 10 };
        change?.Invoke(rule);
        return rule;
    }

    private IReadOnlyList<Alert> Errors(double total)
    {
        _clock.Advance(10);
        Put("steam.proxy.errors", total, MetricUnit.Count);
        return _engine.Evaluate(false);
    }

    [Fact]
    public void A_counter_that_rises_by_the_limit_within_the_window_fires_at_once()
    {
        _engine.SetRules(new[] { ErrorsRule() });
        Assert.Empty(Errors(100));
        Assert.Empty(Errors(100));
        Assert.Empty(Errors(103));                                  // +3 in the window
        Assert.Single(Errors(106));                                 // +6
    }

    [Fact]
    public void A_counter_that_is_high_but_flat_does_not_fire_and_an_old_rise_ages_out_of_the_window()
    {
        _engine.SetRules(new[] { ErrorsRule() });
        Errors(500);
        Assert.Single(Errors(510));                                 // +10 at once
        for (int i = 0; i < 7; i++) Errors(510);                    // flat for 70 s: the rise left the window
        Assert.Equal(RuleState.Quiet, View("err").State);
        Assert.Empty(Errors(510));
    }

    [Fact]
    public void A_counter_that_starts_over_does_not_look_like_a_rise()
    {
        _engine.SetRules(new[] { ErrorsRule() });
        Errors(1000); Errors(1000);
        Assert.Empty(Errors(2));                                    // the module restarted
        Assert.Empty(Errors(3));
        Assert.Equal(RuleState.Quiet, View("err").State);
    }

    [Fact]
    public void A_status_mask_fires_while_any_of_the_bits_is_set()
    {
        _engine.SetRules(new[] { new WatchRule { Id = "hot", Metric = "gpu.throttle", Kind = RuleKind.Bits, Threshold = 0x60, Seconds = 20, QuietMinutes = 0, IntervalSeconds = 10 } });
        Assert.Empty(Step(0x04, id: "gpu.throttle"));               // power cap: not a thermal bit
        Assert.Empty(Step(0x20 | 0x04, id: "gpu.throttle"));
        Assert.Empty(Step(0x20, id: "gpu.throttle"));
        Assert.Single(Step(0x40, id: "gpu.throttle"));
        Step(0x04, id: "gpu.throttle");
        Assert.Equal(RuleState.Quiet, View("hot").State);
    }

    // ------------------------------------------------------------------ the rule set

    [Fact]
    public void A_switched_off_rule_does_nothing_and_asks_for_nothing()
    {
        _engine.SetRules(new[] { Rule(r => r.Enabled = false) });
        Assert.Equal(0, Steps(99, 10));
        Assert.Equal(RuleState.Off, View().State);
        Assert.Empty(_engine.Wanted());
        Assert.Null(_engine.CheckEvery());
    }

    [Fact]
    public void Each_metric_is_wanted_at_the_shortest_interval_of_the_rules_that_use_it_and_the_condition_counts()
    {
        _engine.SetRules(new[]
        {
            Rule(r => r.IntervalSeconds = 30),
            FanRule(r => { r.Id = "fan"; r.IntervalSeconds = 15; }),
            ErrorsRule(r => { r.IntervalSeconds = 5; r.Enabled = false; }),
        });
        var wanted = _engine.Wanted();
        Assert.Equal(TimeSpan.FromSeconds(15), wanted["cpu.temp"]);             // the cpu rule asks 30 s, the fan rule's condition 15 s
        Assert.Equal(TimeSpan.FromSeconds(15), wanted["fan.cpu.rpm"]);
        Assert.DoesNotContain("steam.proxy.errors", wanted.Keys);
        Assert.Equal(TimeSpan.FromSeconds(15), _engine.CheckEvery());
    }

    [Fact]
    public void Setting_the_same_rules_again_keeps_the_state_and_changing_one_starts_it_over_but_keeps_its_last_alert()
    {
        _engine.SetRules(new[] { Rule() });
        Assert.Equal(1, Steps(97, 4));
        _engine.SetRules(new[] { Rule() });
        Assert.Equal(RuleState.Firing, View().State);               // the same meaning: nothing was lost
        _engine.SetRules(new[] { Rule(r => r.Threshold = 98) });
        Assert.Equal(RuleState.Waiting, View().State);              // a new limit: a new rule
        Assert.NotNull(View().LastAlert);                           // but the page still says when it last alerted
        Step(97);
        Assert.Equal(RuleState.Quiet, View().State);
    }

    [Fact]
    public void A_rule_that_is_removed_is_forgotten()
    {
        _engine.SetRules(new[] { Rule() });
        _engine.SetRules(Array.Empty<WatchRule>());
        Assert.Null(_engine.View("cpu1"));
        Assert.Empty(_engine.Evaluate(false));
    }

    [Fact]
    public void A_reading_that_is_not_a_number_is_ignored()
    {
        _engine.SetRules(new[] { Rule() });
        Put("cpu.temp", double.NaN);
        Assert.Equal(RuleState.Waiting, View().State);
        _engine.Evaluate(false);
        Assert.Equal(RuleState.Waiting, View().State);
    }

    // ------------------------------------------------------------------ the saved rules

    [Fact]
    public void Saved_rules_are_repaired_to_their_ranges_and_the_unusable_ones_dropped()
    {
        var saved = new WatchSettings
        {
            Rules =
            {
                new WatchRule { Id = "a", Metric = "cpu.temp", Threshold = double.NaN, Seconds = -5, QuietMinutes = 99999, IntervalSeconds = 1 },
                new WatchRule { Id = "b", Metric = "Not A Metric" },
                new WatchRule { Id = "a", Metric = "gpu.nvml.temp", Threshold = 87, ClearAt = 99 },        // a clearing limit above the limit never ends: dropped
                new WatchRule { Id = "", Metric = "cpu.temp", Kind = (RuleKind)42, ConditionMetric = "bad id", ConditionKind = RuleKind.Rises },
                new WatchRule { Id = "c", Metric = "steam.proxy.errors", Kind = RuleKind.Rises, Threshold = 5, Seconds = 0 },
            },
        }.Clamped();

        Assert.Equal(4, saved.Rules.Count);
        var first = saved.Rules[0];
        Assert.Equal(0, first.Threshold);
        Assert.Equal(0, first.Seconds);
        Assert.Equal(WatchRule.MaxQuietMinutes, first.QuietMinutes);
        Assert.Equal(WatchRule.MinIntervalSeconds, first.IntervalSeconds);
        Assert.Null(saved.Rules[1].ClearAt);
        Assert.Equal(RuleKind.Above, saved.Rules[2].Kind);
        Assert.False(saved.Rules[2].HasCondition);
        Assert.Equal(RuleKind.Above, saved.Rules[2].ConditionKind);
        Assert.Equal(1, saved.Rules[3].Seconds);                                                        // a window is at least a second
        Assert.Equal(saved.Rules.Count, saved.Rules.Select(r => r.Id).Distinct().Count());
        Assert.All(saved.Rules, r => Assert.False(string.IsNullOrEmpty(r.Id)));
    }

    [Fact]
    public void At_most_the_allowed_number_of_rules_survive()
    {
        var saved = new WatchSettings { Rules = Enumerable.Range(0, 40).Select(i => new WatchRule { Id = "r" + i, Metric = "cpu.temp" }).ToList() }.Clamped();
        Assert.Equal(WatchRule.MaxRules, saved.Rules.Count);
        Assert.True(WatchRule.MaxRules * 2 <= 64, "two background subscriptions per rule at most must fit the host's limit of 64");
    }

    [Fact]
    public void A_mask_is_a_whole_number_and_has_no_clearing_limit()
    {
        var rule = new WatchRule { Metric = "gpu.throttle", Kind = RuleKind.Bits, Threshold = 96.7, ClearAt = 3 }.Normalised()!;
        Assert.Equal(96, rule.Threshold);
        Assert.Null(rule.ClearAt);
    }
}
