using Rigbay.Abstractions;
using Rigbay.PredatorSense.Wmi;
using Xunit;

namespace Rigbay.PredatorSense.Tests;

/// <summary>
/// Every fan change, refusal and failure tells the user, once (docs/APP.md, "Notifications"). The module picks the content, the
/// kind and the origin; the host picks the place from them (tested in Rigbay.Core.Tests). A fake toast service records what it got.
/// </summary>
public class NotificationTests
{
    private static List<ToastMessage> Shown(ModuleRig rig) { lock (rig.Host.ToastService.Shown) return rig.Host.ToastService.Shown.ToList(); }

    private static async Task<List<ToastMessage>> Settled(ModuleRig rig, int count)
    {
        Assert.True(await Wait.UntilAsync(() => Shown(rig).Count >= count), $"expected {count} message(s), got {Shown(rig).Count}");
        await Task.Delay(100);   // a second message that should not come has had time to
        return Shown(rig);
    }

    // ---- the page row: the user chose and the host confirmed, so the origin is Page

    [Fact]
    public async Task Choosing_max_on_the_page_reports_once_with_the_speed_check_in_it()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "max";

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Success, ToastOrigin.Page, "toast.fan-set"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Equal("Fans set to Maximum. GPU fan 2 sped up: 2663 to 5900 rpm", rig.Host.Loc.Resolve(t.Body));
    }

    [Fact]
    public async Task Choosing_auto_on_the_page_reports_too()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "max";
        await Settled(rig, 1);
        lock (rig.Host.ToastService.Shown) rig.Host.ToastService.Shown.Clear();

        rig.FanRow.Value.Value = "auto";   // also what Revert does

        var t = Assert.Single(await Settled(rig, 1));
        Assert.Equal((ToastKind.Success, ToastOrigin.Page), (t.Kind, t.Origin));
        Assert.StartsWith("Fans set to Auto.", rig.Host.Loc.Resolve(t.Body));
    }

    [Fact]
    public async Task A_write_the_laptop_refuses_is_a_warning_from_the_page()
    {
        using var rig = new ModuleRig();
        rig.Wmi.WriteAnswer = 0x05;
        rig.FanRow.Value.Value = "max";

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Warning, ToastOrigin.Page, "toast.fan-failed"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Equal("Not changed. Refused by the laptop (status 5)", rig.Host.Loc.Resolve(t.Body));
    }

    [Fact]
    public async Task A_write_that_stalls_says_it_is_not_confirmed()
    {
        using var rig = new ModuleRig();
        rig.Wmi.WriteThrows = new AcerWmiException(AcerWmiFailure.Timeout, "stalled");
        rig.FanRow.Value.Value = "max";

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Warning, ToastOrigin.Page, "toast.fan-unconfirmed"), (t.Kind, t.Origin, t.Body.Key));
        Assert.StartsWith("Not confirmed whether the fans are on Maximum.", rig.Host.Loc.Resolve(t.Body));
    }

    [Fact]
    public async Task An_accepted_write_the_fans_did_not_follow_is_a_warning_even_from_the_page()
    {
        using var rig = new ModuleRig();
        rig.Wmi.OnWrite = null;   // the fans stay where they are
        rig.FanRow.Value.Value = "max";

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Warning, ToastOrigin.Page, "toast.fan-set"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Equal("Fans set to Maximum. No fan sped up within 10 s", rig.Host.Loc.Resolve(t.Body));
    }

    [Fact]
    public async Task A_page_choice_with_the_sensors_gone_is_refused_out_loud()
    {
        using var rig = new ModuleRig();
        rig.Wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "gone");
        await rig.Host.CommandRegistry.Run("status");   // a fresh read: the readings are degraded, so writes are off
        rig.FanRow.Value.Value = "max";

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Warning, ToastOrigin.Page, "toast.fan-failed"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task Putting_back_nothing_is_not_announced_because_nothing_happened()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "Not set";   // a state, not a value
        await Task.Delay(150);

        Assert.Empty(Shown(rig));
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    // ---- hotkey and verb: no window involved, so Background (always a toast)

    [Fact]
    public async Task A_hotkey_result_is_one_background_message()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Host.HotkeyService.Press("toggle-fan");

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Success, ToastOrigin.Background, "toast.fan-set"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Contains("sped up", rig.Host.Loc.Resolve(t.Body));
    }

    [Fact]
    public async Task A_hotkey_the_user_declines_is_not_announced()
    {
        using var rig = new ModuleRig(approve: false);
        rig.Host.HotkeyService.Press("toggle-fan");
        Assert.True(await Wait.UntilAsync(() => rig.Host.ApprovalService.Asked.Count == 1));
        await Task.Delay(150);

        Assert.Empty(Shown(rig));
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task A_hotkey_with_the_sensors_gone_is_refused_out_loud_and_never_asks()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "gone");
        await rig.Host.CommandRegistry.Run("status");
        rig.Host.HotkeyService.Press("toggle-fan");

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Warning, ToastOrigin.Background, "check.refused-readings"), (t.Kind, t.Origin, t.Body.Key));
        Assert.Empty(rig.Host.ApprovalService.Asked);
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task A_hotkey_write_the_laptop_refuses_is_a_background_warning()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Wmi.WriteAnswer = 0x03;
        rig.Host.HotkeyService.Press("toggle-fan");

        var t = Assert.Single(await Settled(rig, 1));

        Assert.Equal((ToastKind.Warning, ToastOrigin.Background, "toast.fan-failed"), (t.Kind, t.Origin, t.Body.Key));
    }

    [Fact]
    public async Task The_command_line_verb_raises_a_toast_too_and_still_prints_its_result()
    {
        using var rig = new ModuleRig(approve: true);

        var r = await rig.Host.CommandRegistry.Run("fan", "max");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Fan mode set to Maximum.", r.Output);
        var t = Assert.Single(Shown(rig));
        Assert.Equal((ToastKind.Success, ToastOrigin.Background, "toast.fan-set"), (t.Kind, t.Origin, t.Body.Key));
    }

    [Fact]
    public async Task A_verb_the_user_declines_raises_no_toast()
    {
        using var rig = new ModuleRig(approve: false);

        var r = await rig.Host.CommandRegistry.Run("fan", "max");

        Assert.Equal(1, r.ExitCode);
        Assert.Empty(Shown(rig));
    }

    // ---- quiet: nothing for the periodic reading

    [Fact]
    public async Task Reading_the_sensors_says_nothing()
    {
        using var rig = new ModuleRig();
        await rig.Host.CommandRegistry.Run("status");
        rig.Host.PageVisibleValue.Value = true;
        await Task.Delay(150);
        rig.Wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "gone");
        await rig.Host.CommandRegistry.Run("status");

        Assert.Empty(Shown(rig));
    }
}
