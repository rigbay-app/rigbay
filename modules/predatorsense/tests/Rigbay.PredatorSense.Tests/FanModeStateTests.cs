using Rigbay.Abstractions;
using Rigbay.PredatorSense.Wmi;
using Xunit;

namespace Rigbay.PredatorSense.Tests;

/// <summary>
/// The Fan mode choice must not pretend to know the mode (E2E-10). The firmware has no read of the mode, so until Rigbay has set one
/// in this run the row says "Not set"; choosing "Not set" writes nothing; a Revert of the first change has nothing to go back to.
/// Every write still comes from a fake firmware and only with an allow-listed value.
/// </summary>
public class FanModeStateTests
{
    private static Task<bool> Eventually(Func<bool> c) => Wait.UntilAsync(c);

    private static string[] OptionIds(ChoiceRow row) => row.Options.Select(o => o.Id).ToArray();

    // ---- state: not set

    [Fact]
    public void Before_anything_is_set_the_row_says_not_set_and_nothing_was_written()
    {
        using var rig = new ModuleRig();
        var row = rig.FanRow;
        Assert.Equal(new[] { "auto", "max" }, OptionIds(row));
        Assert.Equal("Not set", row.Value.Value);
        Assert.Equal("Nothing yet", rig.LastSet);
        Assert.True(row.ChangesHardware);   // still guarded by the host's approval question
        Assert.Equal(0, rig.Wmi.WriteCount);
        Assert.Equal(0, rig.Host.PageRefreshRequests);
    }

    [Fact]
    public async Task Choosing_not_set_while_nothing_is_set_writes_nothing()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "Not set";   // already the value: no change
        rig.FanRow.Value.Value = "auto";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1));
        Assert.Equal("auto", rig.FanRow.Value.Value);
        Assert.Equal(new[] { WmiAllowList.FanAuto }, rig.WrittenValues.ToArray());
    }

    [Fact]
    public async Task Auto_chosen_while_nothing_is_set_is_a_real_request_and_sends_auto_once()
    {
        // Before 0.1.1 the row already showed Auto, so choosing Auto did nothing. Now it is the way to get the laptop into a known state.
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "auto";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("unchanged")));
        Assert.Equal(new[] { 0x01410019UL }, rig.WrittenValues.ToArray());
        Assert.Equal("Auto at 18:42", rig.LastSet);
        Assert.Equal("auto", rig.FanRow.Value.Value);
    }

    // ---- state: set

    [Fact]
    public async Task After_the_first_set_the_choice_shows_it_and_the_page_needs_no_rebuild()
    {
        using var rig = new ModuleRig();
        var before = rig.FanRow;
        rig.FanRow.Value.Value = "auto";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("unchanged")));
        var row = rig.FanRow;
        Assert.Equal(OptionIds(before), OptionIds(row));   // the same two options before and after: no "Not set" item to take away (host API 0.1.3)
        Assert.Equal("auto", row.Value.Value);
        Assert.True(row.ChangesHardware);
        Assert.Equal(0, rig.Host.PageRefreshRequests);
        Assert.Equal("Set to Auto at 18:42 · Speeds unchanged, as expected", rig.FanLine);
    }

    [Fact]
    public async Task Set_max_shows_max_without_the_not_set_option_and_the_status_line_keeps_last_set()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("sped up")));
        Assert.Equal(new[] { "auto", "max" }, OptionIds(rig.FanRow));
        Assert.Equal("max", rig.FanRow.Value.Value);
        Assert.Equal("Maximum at 18:42", rig.LastSet);
        Assert.Equal(new[] { WmiAllowList.FanMax }, rig.WrittenValues.ToArray());
    }

    [Fact]
    public async Task A_write_that_did_not_answer_still_counts_as_set_because_it_may_have_landed()
    {
        using var rig = new ModuleRig();
        rig.Wmi.WriteThrows = new AcerWmiException(AcerWmiFailure.Timeout, "stalled");
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.LastSet.Contains("not confirmed")));
        Assert.Equal(new[] { "auto", "max" }, OptionIds(rig.FanRow));
        Assert.Equal("max", rig.FanRow.Value.Value);
    }

    [Fact]
    public async Task A_refused_first_write_leaves_the_row_on_not_set()
    {
        using var rig = new ModuleRig();
        rig.Wmi.WriteAnswer = 0x05;
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.Check.Contains("Refused by the laptop")));
        Assert.True(await Eventually(() => rig.FanRow.Value.Value == "Not set"));
        Assert.Equal(new[] { "auto", "max" }, OptionIds(rig.FanRow));
        Assert.Equal(0, rig.Host.PageRefreshRequests);
    }

    // ---- revert

    [Fact]
    public async Task Reverting_the_first_change_has_no_earlier_mode_and_writes_nothing()
    {
        using var rig = new ModuleRig();
        var row = rig.FanRow;   // the page the user still has open
        row.Value.Value = "max";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("sped up")));

        // The host's Revert puts the value from before the change back, and that value is "Not set".
        row.Value.Value = "Not set";
        Assert.True(await Eventually(() => rig.Check.Contains("No earlier mode")));
        Assert.Equal("max", row.Value.Value);   // the row goes back to what Rigbay really set
        await Task.Delay(100);
        Assert.Equal(1, rig.Wmi.WriteCount);    // nothing was sent for it
        Assert.Equal("Maximum at 18:42", rig.LastSet);
    }

    [Fact]
    public async Task Reverting_a_later_change_goes_back_to_the_known_previous_mode()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "auto";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("unchanged")));
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 2 && rig.Check.Contains("sped up")));

        rig.FanRow.Value.Value = "auto";   // what the host's Revert does after "Auto -> Maximum", once the user approved it
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 3 && rig.Check.Contains("slowed")));
        Assert.Equal(new[] { WmiAllowList.FanAuto, WmiAllowList.FanMax, WmiAllowList.FanAuto }, rig.WrittenValues.ToArray());
    }

    [Fact]
    public async Task A_new_set_after_a_revert_notice_clears_the_notice()
    {
        using var rig = new ModuleRig();
        var row = rig.FanRow;
        row.Value.Value = "max";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("sped up")));
        row.Value.Value = "Not set";
        Assert.True(await Eventually(() => rig.Check.Contains("No earlier mode")));
        row.Value.Value = "auto";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 2 && rig.Check.Contains("slowed")));
    }

    // ---- restart of the module

    [Fact]
    public void Restarting_the_module_before_anything_was_set_still_says_not_set()
    {
        using var rig = new ModuleRig();
        rig.Module.Shutdown();
        rig.Host.HotkeyService.Registered.Clear();
        rig.Host.CommandRegistry.Verbs.Clear();
        rig.Module.Initialize(rig.Host);
        Assert.Equal("Not set", rig.FanRow.Value.Value);
        Assert.Equal(new[] { "auto", "max" }, OptionIds(rig.FanRow));
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task Restarting_the_module_after_a_set_keeps_what_rigbay_set_without_the_not_set_option()
    {
        using var rig = new ModuleRig();
        rig.Rows.OfType<ToggleRow>().Single().Value.Value = false;   // do not restore on stop, so the carried state is Maximum
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("sped up")));
        rig.Module.Shutdown();
        rig.Host.HotkeyService.Registered.Clear();
        rig.Host.CommandRegistry.Verbs.Clear();
        rig.Module.Initialize(rig.Host);
        Assert.Equal("max", rig.FanRow.Value.Value);
        Assert.Equal(new[] { "auto", "max" }, OptionIds(rig.FanRow));
        Assert.Equal("Maximum at 18:42", rig.LastSet);
        Assert.Equal(1, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task A_new_module_instance_a_new_run_starts_at_not_set_again()
    {
        using (var first = new ModuleRig())
        {
            first.FanRow.Value.Value = "auto";
            Assert.True(await Eventually(() => first.Wmi.WriteCount == 1 && first.Check.Contains("unchanged")));
        }
        using var second = new ModuleRig();   // nothing is carried over a process: the firmware may have been changed meanwhile
        Assert.Equal("Not set", second.FanRow.Value.Value);
        Assert.Equal("Nothing yet", second.LastSet);
    }

    // ---- language switch

    [Fact]
    public void A_language_switch_keeps_the_state_and_translates_the_label()
    {
        using var rig = new ModuleRig();
        var row = rig.FanRow;
        rig.Host.Loc.Switch("vi");
        Assert.Equal("Chưa đặt", row.Value.Value);   // the not-set value is the words, in the current language
        Assert.Equal("Chưa đặt", rig.LastSet);
        rig.Host.Loc.Switch("en");
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task A_language_switch_after_a_set_keeps_the_mode_and_translates_the_last_set_line()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("sped up")));
        rig.Host.Loc.Switch("vi");
        Assert.Equal("max", rig.FanRow.Value.Value);
        Assert.StartsWith("Tối đa", rig.LastSet);
        Assert.Equal(1, rig.Wmi.WriteCount);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void The_new_texts_exist_in_both_languages(string lang)
    {
        var table = FakeLocalisation.Load(lang);
        Assert.False(string.IsNullOrWhiteSpace(table["module.predatorsense.line.not-set"]));
        Assert.False(string.IsNullOrWhiteSpace(table["module.predatorsense.check.no-previous"]));
    }

    // ---- other ways in keep working from the unknown state

    [Fact]
    public async Task The_hotkey_from_not_set_asks_to_set_maximum_and_then_the_row_follows()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Host.HotkeyService.Press("toggle-fan");
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.Check.Contains("sped up")));
        Assert.Equal("Set fan mode to Maximum", rig.Host.Loc.Resolve(rig.Host.ApprovalService.Asked[0].Detail!.Value));
        Assert.Equal("max", rig.FanRow.Value.Value);
        Assert.Equal(new[] { "auto", "max" }, OptionIds(rig.FanRow));
    }
}
