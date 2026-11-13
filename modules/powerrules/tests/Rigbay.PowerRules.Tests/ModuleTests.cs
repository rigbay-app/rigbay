using System.Text.Json;
using Rigbay.Abstractions;

namespace Rigbay.PowerRules.Tests;

/// <summary>The module on a fake machine: probe, start, the approval of the rules, the switch, every way a step can fail.</summary>
public class ModuleTests
{
    private static string SettingsJson(Action<PowerRulesSettings>? edit = null)
    {
        var s = new PowerRulesSettings
        {
            Initialised = true, RatesDefaulted = true,
            BatteryHz = 60, BatteryMode = "efficiency", PluggedHz = 165, PluggedMode = "balanced",
            KnownRates = new[] { 60, 120, 165 },
        };
        edit?.Invoke(s);
        return JsonSerializer.Serialize(s);
    }

    private static string ApprovedHash(int batteryHz = 60, PowerMode battery = PowerMode.BestEfficiency, int pluggedHz = 165, PowerMode plugged = PowerMode.Balanced) =>
        new RuleSet(new Rule(batteryHz, battery), new Rule(pluggedHz, plugged)).Hash();

    /// <summary>Started, approved, the first check done (plugged in at 165 Hz, Balanced: nothing to change).</summary>
    private static async Task<Rig> Approved()
    {
        var rig = new Rig().Start();
        await rig.Approve();
        await rig.Fire();
        return rig;
    }

    // ------------------------------------------------------------------ probe, start

    [Fact]
    public void A_PC_without_a_battery_is_unsupported_and_says_why()
    {
        var rig = new Rig();
        rig.Source.Battery = false;
        var probe = rig.Module.Probe();
        Assert.False(probe.Supported);
        Assert.Equal("probe.no-battery", probe.ReasonKey);
        Assert.Contains("no battery", rig.Host.Loc.T(probe.ReasonKey));
    }

    [Fact]
    public void Start_reads_and_listens_but_writes_nothing_and_arms_nothing_until_the_rules_are_approved()
    {
        var rig = new Rig().Start();
        Assert.True(rig.Source.Started);
        Assert.Empty(rig.Panel.Tests);
        Assert.Empty(rig.Panel.Applies);
        Assert.Empty(rig.Mode.Sets);
        Assert.False(rig.Scheduler.Pending);
        Assert.Empty(rig.Host.ToastService.All());
        Assert.Contains("status", rig.Host.CommandRegistry.Verbs.Keys);
        Assert.False(rig.Auto.Value.Value);
        Assert.Equal("Off", rig.State);
        Assert.Equal("", rig.Line(rig.Auto));   // the row itself says nothing: the header already has the state
    }

    [Fact]
    public void The_first_run_proposes_60_Hz_and_best_efficiency_on_battery_and_the_top_rate_and_balanced_plugged_in()
    {
        var rig = new Rig().Start();
        var saved = rig.Saved;
        Assert.Equal(60, saved.BatteryHz);
        Assert.Equal("efficiency", saved.BatteryMode);
        Assert.Equal(165, saved.PluggedHz);
        Assert.Equal("balanced", saved.PluggedMode);
        Assert.False(saved.AutoApproved);
        Assert.Equal("efficiency", rig.Choice("row.bat-mode.header").Value.Value);
        Assert.Equal("60", rig.Choice("row.bat-rate.header").Value.Value);
        Assert.Equal("165", rig.Choice("row.plug-rate.header").Value.Value);
    }

    [Fact]
    public void Without_an_internal_panel_the_first_run_leaves_the_rates_alone_until_one_is_seen()
    {
        var rig = new Rig();
        rig.Panel.State = PanelState.NotActive;
        rig.Start();
        Assert.Equal(0, rig.Saved.BatteryHz);
        Assert.Equal(0, rig.Saved.PluggedHz);
        Assert.Equal("leave", rig.Choice("row.bat-rate.header").Value.Value);

        // later the panel is in use: Initialize again on the same module fills them, once
        rig.Module.Shutdown();
        rig.Host.CommandRegistry.Verbs.Clear();   // the host removes a module's commands when it stops
        rig.Panel.State = PanelState.Active;
        rig.Module.Initialize(rig.Host);
        Assert.Equal(60, rig.Saved.BatteryHz);
        Assert.Equal(165, rig.Saved.PluggedHz);
    }

    [Fact]
    public void A_reader_that_throws_gives_unknown_not_a_fault()
    {
        var rig = new Rig();
        rig.Panel.FindThrows = new InvalidOperationException("display driver");
        rig.Start();
        var page = rig.Page();
        Assert.Equal("Cannot be read", rig.Host.Loc.T("panel.unreadable"));
        Assert.False(rig.Row<ChoiceRow>("row.rate.header").Enabled.Value);
        Assert.True(rig.Row<InfoRow>("info.no-panel.header").IsOpen.Value);
        Assert.Contains(rig.Host.LogSink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("panel"));
        Assert.NotNull(page);
    }

    // ------------------------------------------------------------------ the approval of the rule set

    [Fact]
    public async Task Turning_it_on_asks_once_about_exactly_the_two_rules_and_a_no_leaves_it_off()
    {
        var rig = new Rig().Start();
        await rig.Approve(answer: false);
        Assert.Equal(1, rig.Host.ApprovalService.Count);
        var (effect, detail) = rig.Host.ApprovalService.Asked[0];
        Assert.Equal(EffectKind.SystemSetting, effect.Kind);
        var text = rig.Words(detail);
        Assert.Contains("On battery: 60 Hz, Best efficiency", text);
        Assert.Contains("Plugged in: 165 Hz, Balanced", text);
        Assert.Contains("puts back the values from before", text);
        Assert.False(rig.Auto.Value.Value);
        Assert.False(rig.Saved.AutoApproved);
        Assert.False(rig.Scheduler.Pending);
        Assert.Empty(rig.Panel.Applies);
    }

    [Fact]
    public async Task A_yes_stores_the_hash_of_these_rules_and_runs_the_rule_for_the_source_now_once()
    {
        var rig = new Rig().Start();
        await rig.Approve();
        Assert.True(rig.Saved.AutoApproved);
        Assert.Equal(ApprovedHash(), rig.Saved.ApprovedHash);
        Assert.True(rig.Auto.Value.Value);
        Assert.Equal("Rules on · Plugged in", rig.State);
        Assert.True(rig.Scheduler.Pending);
        Assert.Equal(TimeSpan.Zero, rig.Scheduler.LastDelay);
        await rig.Fire();
        // plugged in at 165 Hz and Balanced already: the rule changes nothing and says nothing
        Assert.Empty(rig.Panel.Applies);
        Assert.Empty(rig.Mode.Sets);
        Assert.Empty(rig.Host.ToastService.All());
    }

    [Fact]
    public async Task Approving_on_battery_applies_the_battery_rule_straight_away()
    {
        var rig = new Rig();
        rig.Source.Now = PowerSource.Battery;
        rig.Start();
        await rig.Approve();
        await rig.Fire();
        Assert.Equal(new[] { 60 }, rig.Panel.Applies);
        Assert.Equal(new[] { PowerMode.BestEfficiency }, rig.Mode.Sets);
    }

    [Fact]
    public async Task Turning_it_on_with_nothing_to_switch_asks_nothing()
    {
        var rig = new Rig().Start();
        await rig.Rules(0, null, 0, null);
        await rig.Approve();
        Assert.Equal(0, rig.Host.ApprovalService.Count);
        Assert.False(rig.Auto.Value.Value);
        Assert.Contains("Choose at least one value", rig.Toasts().Single());
    }

    [Fact]
    public async Task Rules_edited_while_the_question_is_open_do_not_get_the_answer_for_the_old_ones()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.OnAsk = () => rig.Choice("row.bat-rate.header").Value.Value = "120";
        await rig.Approve();
        Assert.False(rig.Saved.AutoApproved);
        Assert.False(rig.Auto.Value.Value);
        Assert.Contains("rules changed", rig.Toasts().Single());
    }

    [Fact]
    public async Task Changing_a_rule_ends_the_approval_and_nothing_switches_until_the_owner_approves_the_new_rules()
    {
        var rig = await Approved();
        rig.Choice("row.bat-rate.header").Value.Value = "120";
        Assert.False(rig.Saved.AutoApproved);
        Assert.False(rig.Auto.Value.Value);
        Assert.Equal("The rules changed. Turn this on to approve them.", rig.Line(rig.Auto));
        Assert.Equal("The rules changed. Turn this on to approve them.", rig.State);

        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Empty(rig.Panel.Applies);
        Assert.Empty(rig.Mode.Sets);

        rig.Host.ApprovalService.Answer = true;
        rig.Auto.Value.Value = true;
        await rig.Idle();
        Assert.Equal(2, rig.Host.ApprovalService.Count);
        Assert.Contains("On battery: 120 Hz, Best efficiency", rig.Words(rig.Host.ApprovalService.Asked[1].Detail));
        Assert.Equal(ApprovedHash(batteryHz: 120), rig.Saved.ApprovedHash);
    }

    [Fact]
    public async Task A_settings_file_edited_by_hand_does_not_carry_the_approval_to_other_rules()
    {
        var rig = new Rig();
        rig.Host.SettingsStore.Json = SettingsJson(s => { s.AutoApproved = true; s.ApprovedHash = ApprovedHash(); s.BatteryHz = 120; });
        rig.Source.Now = PowerSource.Battery;
        rig.Start();
        Assert.False(rig.Scheduler.Pending);   // not approved for these rules: not even a timer
        Assert.False(rig.Auto.Value.Value);
        rig.Source.Switch(PowerSource.Plugged);
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Empty(rig.Panel.Applies);
        Assert.Empty(rig.Mode.Sets);
    }

    [Fact]
    public void An_approval_for_the_rules_in_the_file_is_honoured_after_a_restart_and_runs_after_the_settle_delay_not_in_Initialize()
    {
        var rig = new Rig();
        rig.Host.SettingsStore.Json = SettingsJson(s => { s.AutoApproved = true; s.ApprovedHash = ApprovedHash(); });
        rig.Source.Now = PowerSource.Battery;
        rig.Start();
        Assert.Empty(rig.Panel.Applies);     // nothing written inside Initialize
        Assert.True(rig.Scheduler.Pending);
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Scheduler.LastDelay);
        Assert.True(rig.Auto.Value.Value);
    }

    // ------------------------------------------------------------------ the switch

    [Fact]
    public async Task On_battery_the_panel_is_tested_then_set_to_60_Hz_and_the_mode_to_best_efficiency_with_one_notification_and_the_values_before_remembered()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Battery);
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Scheduler.LastDelay);
        Assert.Empty(rig.Panel.Applies);     // after the settle delay, not at once
        await rig.Fire();

        Assert.Equal(new[] { "test 60", "apply 60" }, rig.Panel.Order);
        Assert.Equal(60, rig.Panel.Current);
        Assert.Equal(new[] { PowerMode.BestEfficiency }, rig.Mode.Sets);
        var toast = Assert.Single(rig.Host.ToastService.All());
        Assert.Equal("On battery: refresh rate 60 Hz, power mode Best efficiency.", rig.Host.Loc.Resolve(toast.Body));
        Assert.Equal(ToastOrigin.Background, toast.Origin);
        Assert.Equal(ToastKind.Info, toast.Kind);
        Assert.Equal(165, rig.Saved.BeforeHz);
        Assert.Equal("balanced", rig.Saved.BeforeMode);
        Assert.Equal("Rules on · On battery", rig.State);
    }

    [Fact]
    public async Task Plugging_in_applies_the_plugged_column_and_the_values_before_are_forgotten_because_they_are_back()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        rig.Source.Switch(PowerSource.Plugged);
        await rig.Fire();

        Assert.Equal(165, rig.Panel.Current);
        Assert.Equal(PowerMode.Balanced, rig.Mode.Current);
        Assert.Equal(0, rig.Saved.BeforeHz);
        Assert.Equal("", rig.Saved.BeforeMode);
        var toasts = rig.Toasts();
        Assert.Equal(2, toasts.Length);
        Assert.Equal("Plugged in: refresh rate 165 Hz, power mode Balanced.", toasts[1]);
    }

    [Fact]
    public async Task The_value_before_is_the_one_before_the_first_automatic_change_not_before_each()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        await rig.Rules(60, PowerMode.BestEfficiency, 0, null);   // plugged in: leave both
        await rig.Approve();
        rig.Choice("row.bat-rate.header").Value.Value = "60";
        rig.Source.Switch(PowerSource.Plugged);
        await rig.Fire();                                          // leaves everything: still 60 Hz
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Equal(165, rig.Saved.BeforeHz);
        Assert.Equal("balanced", rig.Saved.BeforeMode);
    }

    [Fact]
    public async Task The_same_source_reported_again_changes_nothing()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        int applies = rig.Panel.Applies.Count, tests = rig.Panel.Tests.Count, toasts = rig.Host.ToastService.All().Length;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Equal(applies, rig.Panel.Applies.Count);
        Assert.Equal(tests, rig.Panel.Tests.Count);
        Assert.Equal(toasts, rig.Host.ToastService.All().Length);
    }

    [Fact]
    public async Task A_burst_of_changes_runs_the_rules_once_for_the_last_state()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Battery);
        rig.Source.Switch(PowerSource.Plugged);
        rig.Source.Switch(PowerSource.Battery);
        Assert.True(rig.Scheduler.Cancels >= 3);
        await rig.Fire();
        Assert.False(rig.Scheduler.Fire());
        Assert.Equal(new[] { 60 }, rig.Panel.Applies);
        Assert.Single(rig.Host.ToastService.All());
    }

    [Fact]
    public async Task After_resume_the_rules_run_again_even_though_the_source_did_not_change_and_after_XTUs_own_check()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        rig.Panel.Current = 165;          // the driver brought the panel back at its own rate
        rig.Mode.Current = PowerMode.Balanced;
        rig.Source.Resume();
        Assert.Equal(TimeSpan.FromSeconds(8), rig.Scheduler.LastDelay);   // XTU forces its check 4 s after resume
        await rig.Fire();
        Assert.Equal(new[] { 60, 60 }, rig.Panel.Applies);
        Assert.Equal(new[] { PowerMode.BestEfficiency, PowerMode.BestEfficiency }, rig.Mode.Sets);
    }

    [Fact]
    public void The_delays_put_the_module_after_XTUs_auto_switch_which_acts_at_once_and_on_a_5_s_timer_and_4_s_after_resume()
    {
        var deps = new Dependencies { Source = new FakeSource(), Panel = new FakePanel(), Mode = new FakeMode() };
        Assert.True(deps.SettleDelay >= TimeSpan.FromSeconds(5));
        Assert.True(deps.ResumeDelay > TimeSpan.FromSeconds(4) + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task An_unknown_source_such_as_a_UPS_switches_nothing()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Unknown);
        await rig.Fire();
        Assert.Empty(rig.Panel.Applies);
        Assert.Empty(rig.Mode.Sets);
    }

    [Fact]
    public async Task When_Windows_does_not_accept_the_notification_the_source_is_read_on_a_slow_timer_instead()
    {
        var rig = new Rig();
        rig.Source.Listening = false;
        rig.Start();
        await rig.Approve();
        await rig.Fire();                                      // the approval's own check
        Assert.Contains(rig.Host.LogSink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("notification"));
        Assert.True(rig.Scheduler.Pending);      // the slow poll is armed

        rig.Source.Now = PowerSource.Battery;                  // no event: only the poll can notice
        await rig.Fire();
        Assert.Equal(new[] { 60 }, rig.Panel.Applies);
        Assert.True(rig.Scheduler.Pending, "the poll arms itself again");
        Assert.Equal(TimeSpan.FromSeconds(30), rig.Scheduler.LastDelay);
        rig.Module.Shutdown();
        Assert.False(rig.Scheduler.Pending);
    }

    [Fact]
    public void With_the_notification_there_is_no_polling_at_all()
    {
        var rig = new Rig().Start();
        Assert.False(rig.Scheduler.Pending);
        Assert.Equal(0, rig.Scheduler.Scheduled);
    }

    // ------------------------------------------------------------------ the refresh rate: every way it can go wrong

    [Fact]
    public async Task When_the_test_call_refuses_the_rate_the_real_call_is_never_made_and_nothing_is_remembered_for_it()
    {
        var rig = await Approved();
        rig.Panel.TestCode = -2;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();

        Assert.Equal(new[] { 60 }, rig.Panel.Tests);
        Assert.Empty(rig.Panel.Applies);
        Assert.Equal(165, rig.Panel.Current);
        Assert.Equal(0, rig.Saved.BeforeHz);
        Assert.Equal("balanced", rig.Saved.BeforeMode);   // the power mode is a separate step and did change
        var toast = Assert.Single(rig.Host.ToastService.All());
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Contains("the panel refused 60 Hz, nothing changed", rig.Host.Loc.Resolve(toast.Body));
    }

    [Fact]
    public async Task A_rate_the_panel_does_not_list_is_never_sent()
    {
        var rig = new Rig().Start();
        await rig.Rules(120, null, 165, null);
        await rig.Approve();
        await rig.Fire();
        rig.Panel.Rates = new[] { 60, 165 };       // another panel, say after a dock change
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Empty(rig.Panel.Tests);
        Assert.Empty(rig.Panel.Applies);
        Assert.Contains("120 Hz is not a rate the panel lists", rig.Toasts().Single());
    }

    [Fact]
    public async Task A_change_that_Windows_accepts_but_the_display_does_not_keep_is_reported_and_not_remembered()
    {
        var rig = await Approved();
        rig.Panel.ApplyTakesEffect = false;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Equal(new[] { 60 }, rig.Panel.Applies);
        Assert.Equal(165, rig.Panel.Current);
        Assert.Equal(0, rig.Saved.BeforeHz);
        Assert.Contains("refresh rate stayed 165 Hz, 60 Hz did not take effect", rig.Toasts().Single());
    }

    [Fact]
    public async Task A_failing_real_call_is_a_failed_step_not_a_fault()
    {
        var rig = await Approved();
        rig.Panel.ApplyCode = -1;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Equal(0, rig.Saved.BeforeHz);
        Assert.Contains("did not take effect", rig.Toasts().Single());
    }

    [Fact]
    public async Task Without_an_active_internal_panel_the_rate_step_is_skipped_silently_and_the_mode_still_switches()
    {
        var rig = await Approved();
        rig.Panel.State = PanelState.NotActive;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Empty(rig.Panel.Tests);
        Assert.Empty(rig.Panel.Applies);
        Assert.Equal(new[] { PowerMode.BestEfficiency }, rig.Mode.Sets);
        Assert.Equal("On battery: power mode Best efficiency.", rig.Toasts().Single());
    }

    [Fact]
    public async Task With_a_rule_about_the_rate_only_and_no_panel_there_is_no_notification_at_all()
    {
        var rig = new Rig().Start();
        await rig.Rules(60, null, 165, null);
        await rig.Approve();
        await rig.Fire();
        rig.Panel.State = PanelState.NotActive;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Empty(rig.Panel.Applies);
        Assert.Empty(rig.Host.ToastService.All());
        Assert.True(rig.Row<InfoRow>("info.no-panel.header").IsOpen.Value);
    }

    // ------------------------------------------------------------------ the power mode: every way it can go wrong

    [Fact]
    public async Task When_the_undocumented_export_is_missing_the_mode_is_never_set_and_the_page_says_so_while_the_rate_still_switches()
    {
        var rig = await Approved();
        rig.Mode.Available = false;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Empty(rig.Mode.Sets);
        Assert.Equal(60, rig.Panel.Current);
        Assert.Contains("the power mode cannot be changed on this Windows", rig.Toasts().Single());
        Assert.True(rig.Row<InfoRow>("info.no-mode.header").IsOpen.Value);
        Assert.False(rig.Row<ChoiceRow>("row.pmode.header").Enabled.Value);
        Assert.Equal("Not available on this Windows", rig.Row<ValueRow>("row.mode.header").Value.Value);
    }

    [Fact]
    public async Task A_mode_call_that_fails_or_does_not_hold_is_reported_and_not_remembered()
    {
        var rig = await Approved();
        rig.Mode.SetTakesEffect = false;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Equal("", rig.Saved.BeforeMode);
        Assert.Contains("power mode Best efficiency did not take effect", rig.Toasts().Single());
    }

    [Fact]
    public async Task A_mode_that_is_none_of_the_three_is_shown_as_unknown_and_is_not_remembered_as_before()
    {
        var rig = new Rig();
        rig.Mode.Current = PowerMode.Unknown;
        rig.Source.Now = PowerSource.Battery;
        rig.Start();
        Assert.Equal("Unknown", rig.Row<ValueRow>("row.mode.header").Value.Value);
        Assert.Equal("", rig.Choice("row.pmode.header").Value.Value);
        await rig.Approve();
        await rig.Fire();
        Assert.Equal(new[] { PowerMode.BestEfficiency }, rig.Mode.Sets);
        Assert.Equal("", rig.Saved.BeforeMode);   // nothing to put back to
        Assert.Equal(165, rig.Saved.BeforeHz);    // the refresh rate was known, so that one is remembered
    }

    // ------------------------------------------------------------------ turning off, closing, putting back

    [Fact]
    public async Task Turning_it_off_ends_the_approval_and_puts_the_values_before_back()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        rig.Auto.Value.Value = false;
        await rig.Idle();

        Assert.False(rig.Saved.AutoApproved);
        Assert.Equal("", rig.Saved.ApprovedHash);
        Assert.Equal(165, rig.Panel.Current);
        Assert.Equal(PowerMode.Balanced, rig.Mode.Current);
        Assert.Equal(0, rig.Saved.BeforeHz);
        var last = rig.Host.ToastService.All().Last();
        Assert.Equal("Put back: refresh rate 165 Hz, power mode Balanced.", rig.Host.Loc.Resolve(last.Body));
        Assert.Equal(ToastOrigin.Page, last.Origin);
        // and the source changing afterwards does nothing
        int applies = rig.Panel.Applies.Count;
        rig.Source.Switch(PowerSource.Plugged);
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Equal(applies, rig.Panel.Applies.Count);
    }

    [Fact]
    public async Task Turning_the_module_off_while_approved_puts_the_values_before_back_and_stops_listening()
    {
        var rig = await Approved();
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        rig.Module.Shutdown();
        Assert.Equal(165, rig.Panel.Current);
        Assert.Equal(PowerMode.Balanced, rig.Mode.Current);
        Assert.True(rig.Source.Disposed);
        Assert.False(rig.Source.HasSubscribers);
        Assert.Equal(0, rig.Saved.BeforeHz);
    }

    [Fact]
    public void Turning_the_module_off_without_an_approval_changes_nothing_even_when_a_value_is_remembered()
    {
        var rig = new Rig();
        rig.Panel.Current = 60;
        rig.Host.SettingsStore.Json = SettingsJson(s => s.BeforeHz = 165);
        rig.Start();
        rig.Module.Shutdown();
        Assert.Empty(rig.Panel.Applies);
        Assert.Equal(60, rig.Panel.Current);
    }

    [Fact]
    public async Task The_page_offers_the_values_before_as_one_approved_row_that_puts_them_back()
    {
        var rig = await Approved();
        int refreshes = rig.Host.PageRefreshRequests;
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.True(rig.Host.PageRefreshRequests > refreshes, "the page must be rebuilt to show the row");

        var group = rig.Page().Groups.Single(g => g.HeaderKey == "group.before");
        var button = Assert.IsType<ButtonRow>(group.Rows.Single());
        Assert.NotNull(button.HardwareEffect);
        Assert.Equal(EffectKind.SystemSetting, button.HardwareEffect!.Kind);
        Assert.Equal("Puts back 165 Hz, Balanced, as before the first automatic change.", Text.Unwrap(button.HardwareEffect.DescriptionKey));

        // Windows has it at 60 Hz and Balanced... the owner presses it
        var result = await button.InvokeAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(165, rig.Panel.Current);
        Assert.DoesNotContain(rig.Page().Groups, g => g.HeaderKey == "group.before");
    }

    // ------------------------------------------------------------------ manual changes from the page

    [Fact]
    public async Task A_rate_chosen_on_the_page_is_tested_set_and_read_back_and_reported_as_the_result_of_what_the_owner_confirmed()
    {
        var rig = new Rig().Start();
        var row = rig.Choice("row.rate.header");
        Assert.Equal(new[] { "60", "120", "165" }, row.Options.Select(o => o.Id));
        Assert.Equal("165", row.Value.Value);
        Assert.NotNull(row.HardwareEffect);
        Assert.Equal(EffectKind.SystemSetting, row.HardwareEffect!.Kind);

        row.Value.Value = "60";
        await rig.Idle();
        Assert.Equal(new[] { "test 60", "apply 60" }, rig.Panel.Order);
        Assert.Equal("60", rig.Choice("row.rate.header").Value.Value);
        var toast = Assert.Single(rig.Host.ToastService.All());
        Assert.Equal(ToastOrigin.Page, toast.Origin);
        Assert.Equal("refresh rate 60 Hz.", rig.Host.Loc.Resolve(toast.Body));
        Assert.Equal(0, rig.Saved.BeforeHz);   // only automatic changes are remembered; a manual one has the host's Revert
    }

    [Fact]
    public async Task A_manual_rate_the_panel_refuses_puts_the_row_back_to_what_the_display_has()
    {
        var rig = new Rig().Start();
        rig.Panel.TestCode = -2;
        var row = rig.Choice("row.rate.header");
        row.Value.Value = "120";
        await rig.Idle();
        Assert.Empty(rig.Panel.Applies);
        Assert.Equal("165", row.Value.Value);
        Assert.Equal(ToastKind.Warning, rig.Host.ToastService.All().Single().Kind);
    }

    [Fact]
    public async Task A_power_mode_chosen_on_the_page_is_set_and_read_back()
    {
        var rig = new Rig().Start();
        var row = rig.Choice("row.pmode.header");
        Assert.Equal(new[] { "efficiency", "balanced", "performance" }, row.Options.Select(o => o.Id));
        row.Value.Value = "performance";
        await rig.Idle();
        Assert.Equal(new[] { PowerMode.BestPerformance }, rig.Mode.Sets);
        Assert.Equal("performance", row.Value.Value);
        Assert.Equal("Best performance", rig.Row<ValueRow>("row.mode.header").Value.Value);
    }

    [Fact]
    public void The_rows_that_change_the_machine_carry_a_system_setting_effect_in_one_sentence_of_at_most_90_characters()
    {
        var rig = new Rig().Start();
        foreach (var row in rig.Page().EnumerateRows().Where(r => r.HardwareEffect is not null))
        {
            Assert.Equal(EffectKind.SystemSetting, row.HardwareEffect!.Kind);
            foreach (var language in new[] { "en", "vi" })
            {
                rig.Host.Loc.Switch(language);
                var text = rig.Words(row.HardwareEffect);
                Assert.InRange(text.Length, 10, 90);
            }
        }
        rig.Host.Loc.Switch("en");
        // the rule rows change nothing by themselves: the approval of the rule set does
        Assert.All(new[] { "row.bat-rate.header", "row.bat-mode.header", "row.plug-rate.header", "row.plug-mode.header" }, h => Assert.Null(rig.Choice(h).HardwareEffect));
    }

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public async Task The_module_can_be_turned_off_and_on_again_without_listening_twice()
    {
        var rig = await Approved();
        rig.Module.Shutdown();
        rig.Host.CommandRegistry.Verbs.Clear();
        Assert.False(rig.Source.HasSubscribers);
        rig.Source.Started = false;
        rig.Module.Initialize(rig.Host);
        Assert.True(rig.Source.Started);
        rig.Source.Switch(PowerSource.Battery);
        await rig.Fire();
        Assert.Single(rig.Panel.Applies, h => h == 60);
    }

    [Fact]
    public async Task Opening_the_page_reads_the_machine_again()
    {
        var rig = new Rig().Start();
        rig.Panel.Current = 120;
        rig.Host.PageVisibleValue.Value = true;
        await rig.Idle();
        Assert.Equal("120", rig.Choice("row.rate.header").Value.Value);
        Assert.Equal("2560 × 1600 · 120 Hz", rig.Row<ValueRow>("row.panel.header").Value.Value);
    }

    [Fact]
    public async Task The_status_command_prints_the_source_the_panel_the_mode_and_the_rules_and_changes_nothing()
    {
        var rig = new Rig().Start();
        var result = await rig.Host.CommandRegistry.Run("status");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Power source: Plugged in", result.Output);
        Assert.Contains("Internal panel: 2560 × 1600 · 165 Hz", result.Output);
        Assert.Contains("Power mode: Balanced", result.Output);
        Assert.Contains("On battery: 60 Hz, Best efficiency", result.Output);
        Assert.Contains("Automatic switching: off", result.Output);
        Assert.Empty(rig.Panel.Applies);
        Assert.Empty(rig.Panel.Tests);
        Assert.Empty(rig.Mode.Sets);
    }
}
