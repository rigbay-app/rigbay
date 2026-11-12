using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Module.Xtu.Tests;

/// <summary>
/// Every hardware change, refusal and failure tells the user (docs/APP.md, "Notifications"). The module picks the content, the kind
/// and the origin; the host picks the place from them (tested in Rigbay.Core.Tests). A fake toast service records what it was given.
/// The key parity of en.lang and vi.lang is checked by LanguageTests.
/// </summary>
public class NotificationTests
{
    private static List<ToastMessage> Shown(Rig rig) { lock (rig.Host.FakeToast.Shown) return rig.Host.FakeToast.Shown.ToList(); }

    private static void Forget(Rig rig) { lock (rig.Host.FakeToast.Shown) rig.Host.FakeToast.Shown.Clear(); }

    private static async Task<List<ToastMessage>> Settled(Rig rig, int count)
    {
        Assert.True(await Rig.WaitFor(() => Shown(rig).Count >= count), $"expected {count} message(s), got {Shown(rig).Count}");
        await Task.Delay(100);   // a second message that should not come has had time to
        return Shown(rig);
    }

    private static HotkeyRegistration Hotkey(Rig rig, string profile) => rig.Host.FakeHotkeys.Registered.First(r => r.NameKey == "=" + profile);

    // ---- the page: the user clicked, so the origin is Page and the host decides between window and toast

    [Fact]
    public async Task A_profile_chosen_on_the_page_is_reported_once_with_the_page_origin()
    {
        using var rig = new Rig();
        rig.Row<ChoiceRow>("row.profile.header").Value.Value = "p:PL35W";

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Success, ToastOrigin.Page, "toast.applied"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Equal("PL35W", t.Body.Args![0]);
    }

    [Fact]
    public async Task A_profile_that_XTU_refuses_is_an_error_message_from_the_page()
    {
        using var rig = new Rig();
        rig.Client.ApplyError = new XtuException("err.rejected", "InvalidValue");
        rig.Row<ChoiceRow>("row.profile.header").Value.Value = "p:PL35W";

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Error, ToastOrigin.Page, "toast.title-failed"), (t.Kind, t.Origin, t.Title.Key));
    }

    [Fact]
    public async Task Values_that_are_already_in_force_are_said_so_instead_of_staying_silent()
    {
        using var rig = new Rig();
        rig.Row<ChoiceRow>("row.profile.header").Value.Value = "p:PL35W";
        await Settled(rig, 1);
        Forget(rig);

        Hotkey(rig, "PL35W").Invoke();   // the hardware already has PL35W

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Info, ToastOrigin.Background, "msg.already-named"), (t.Kind, t.Origin, t.Body.Key));
    }

    // ---- hotkey, tray item: no window involved, so a toast (Background)

    [Fact]
    public async Task A_hotkey_result_is_a_background_message()
    {
        using var rig = new Rig();
        Hotkey(rig, "PL35W").Invoke();

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Success, ToastOrigin.Background, "toast.applied"), (t.Kind, t.Origin, t.Body.Key));
    }

    [Fact]
    public async Task A_tray_item_result_is_a_background_message()
    {
        using var rig = new Rig();
        rig.Host.FakeTray.Items.Values.First(i => i.TextKey == "=PL35W").Invoke();

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Success, ToastOrigin.Background), (t.Kind, t.Origin));
    }

    [Fact]
    public async Task A_hotkey_failure_is_an_error()
    {
        using var rig = new Rig();
        rig.Client.ApplyError = new XtuException("err.unavailable", "pipe closed");
        Hotkey(rig, "PL35W").Invoke();

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Error, ToastOrigin.Background, "toast.title-failed"), (t.Kind, t.Origin, t.Title.Key));
    }

    [Fact]
    public async Task A_no_from_the_user_is_not_announced()
    {
        using var rig = new Rig();
        rig.Host.FakeApproval.Answer = false;
        Hotkey(rig, "PL35W").Invoke();

        Assert.True(await Rig.WaitFor(() => rig.Host.FakeApproval.Asked.Count == 1));
        await Task.Delay(150);

        Assert.Empty(Shown(rig));
        Assert.Empty(rig.Client.Applied);
    }

    [Fact]
    public async Task A_hotkey_pressed_while_another_change_runs_is_refused_out_loud()
    {
        using var rig = new Rig();
        rig.Client.ApplyGate = new ManualResetEventSlim(false);
        try
        {
            Hotkey(rig, "PL35W").Invoke();
            Assert.True(await Rig.WaitFor(() => rig.Runtime.Apply.IsBusy));

            rig.Host.FakeHotkeys.Registered.First(r => r.NameKey != "=PL35W").Invoke();

            var t = Assert.Single(await Settled(rig, 1));
            Assert.Equal((ToastKind.Warning, ToastOrigin.Background, "msg.busy"), (t.Kind, t.Origin, t.Body.Key));
        }
        finally { rig.Client.ApplyGate.Set(); }
    }

    // ---- auto-switch: nobody clicked, so a change must always reach the user

    [Fact]
    public async Task Auto_switch_applying_on_a_power_change_always_raises_a_background_message()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL35W"; s.BatteryProfile = "PL55-60W"; });
        rig.Runtime.AutoCheck(false);                  // first look: on AC
        Assert.Single(await Settled(rig, 1));
        Forget(rig);

        rig.Power.Line = PowerLine.Offline;
        rig.Power.RaiseChanged();

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Success, ToastOrigin.Background, "toast.auto-applied"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Equal(new object[] { "PL55-60W", "On battery" }, t.Body.Args!);
    }

    [Fact]
    public async Task Auto_switch_applying_after_waking_from_sleep_raises_a_background_message()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL35W"; });
        rig.Runtime.AutoCheck(false);
        Assert.Single(await Settled(rig, 1));
        Forget(rig);
        rig.Client.State = rig.Client.State with { Pl1 = 90, Pl2 = 115, Window = 56 };   // the firmware put the limits back

        rig.Power.RaiseResumed();

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Success, ToastOrigin.Background, "toast.auto-applied"), (t.Kind, t.Origin, t.Body.Key));
    }

    [Fact]
    public async Task Auto_switch_that_fails_raises_an_error()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL35W"; });
        rig.Client.ApplyError = new XtuException("err.unavailable", "pipe closed");

        rig.Runtime.AutoCheck(false);

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Error, ToastOrigin.Background), (t.Kind, t.Origin));
    }

    [Fact]
    public async Task Auto_switch_that_finds_the_values_already_in_force_stays_quiet()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL55-60W"; });   // what the hardware has

        rig.Runtime.AutoCheck(false);
        await Task.Delay(200);

        Assert.Empty(Shown(rig));
    }

    [Fact]
    public async Task A_profile_chosen_for_auto_switch_that_no_longer_exists_is_a_warning()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "Gone"; });

        rig.Runtime.AutoCheck(false);

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Warning, ToastOrigin.Background, "msg.auto-missing"), (t.Kind, t.Origin, t.Body.Key));
    }

    // ---- auto-switch settings changed on the page: the user clicked

    [Fact]
    public async Task Turning_auto_switch_on_from_the_page_reports_what_it_applied_as_a_page_change()
    {
        using var rig = new Rig(settings: s => { s.AcProfile = "PL35W"; });

        rig.Row<ToggleRow>("row.auto.header").Value.Value = true;

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Success, ToastOrigin.Page, "toast.applied"), (t.Kind, t.Origin, t.Body.Key));
    }

    [Fact]
    public async Task Turning_auto_switch_on_with_nothing_chosen_says_nothing_was_applied()
    {
        using var rig = new Rig();

        rig.Row<ToggleRow>("row.auto.header").Value.Value = true;

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Info, ToastOrigin.Page, "toast.auto-nothing"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Empty(rig.Client.Applied);
    }

    [Fact]
    public async Task Turning_auto_switch_off_from_the_page_says_so_and_writes_nothing()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL35W"; });

        rig.Row<ToggleRow>("row.auto.header").Value.Value = false;

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Info, ToastOrigin.Page, "toast.auto-off"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Empty(rig.Client.Applied);
    }

    [Fact]
    public async Task Choosing_the_profile_for_the_power_source_in_use_applies_it_and_reports_it_as_a_page_change()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; });

        rig.Row<ChoiceRow>("row.ac.header").Value.Value = "p:PL35W";   // on AC in this rig

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Success, ToastOrigin.Page, "toast.applied"), (t.Kind, t.Origin, t.Body.Key));
    }

    // ---- quiet: nothing for the periodic reading

    [Fact]
    public async Task The_periodic_state_refresh_and_the_monitor_say_nothing()
    {
        using var rig = new Rig();
        rig.Client.Monitor.Enqueue((new Dictionary<uint, decimal> { [Mon.PkgTemp] = 70, [Mon.PkgPower] = 30 }, true));

        rig.Runtime.Refresh();
        rig.Runtime.MonitorOnce();
        rig.Client.ReadError = new XtuException("err.unavailable", "pipe closed");
        rig.Runtime.Refresh();
        await Task.Delay(100);

        Assert.Empty(Shown(rig));
    }
}
