using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;
using Rigbay.SteamBypass.Check;

namespace Rigbay.SteamBypass.Tests;

public class ModuleLifecycleTests
{
    [Fact]
    public void The_public_constructor_does_nothing_and_exists()
    {
        var module = new SteamBypassModule();   // the one the host calls: it must not touch the registry, a socket or a file
        Assert.NotNull(module);
        Assert.True(module.Probe().Supported);
    }

    [Fact]
    public void Initialize_on_a_clean_machine_writes_nothing_and_opens_no_port()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = "http://corp.example/wpad.pac");
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Empty(rig.Listeners.Requested);
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
        Assert.False(rig.Switch.Value.Value);
        Assert.Equal("Bypass off", rig.StateLine);
        Assert.Empty(rig.Host.MetricsBus.Snapshot());   // nothing is published while it is off
    }

    [Fact]
    public void Initialize_registers_the_four_verbs_and_describes_the_two_metrics()
    {
        using var rig = new ModuleRig();
        Assert.Equal(new[] { "off", "on", "status", "test" }, rig.Host.CommandRegistry.Verbs.Keys.OrderBy(k => k).ToArray());
        var d = rig.Host.MetricsBus.Descriptors.ToDictionary(x => x.Id.Value);
        Assert.Equal(MetricUnit.Count, d["steam.proxy.active"].Unit);
        Assert.Equal(MetricUnit.Count, d["steam.proxy.errors"].Unit);
        Assert.Equal("metric.active", d["steam.proxy.active"].LabelKey);
        Assert.Equal("Steam proxy connections", rig.Host.Loc.T(d["steam.proxy.active"].LabelKey));
    }

    [Fact]
    public void The_page_has_the_planned_shape()
    {
        using var rig = new ModuleRig();
        var page = rig.Page;
        Assert.Equal(new[] { "group.bypass", "group.status", "group.check", "group.settings" }, page.Groups.Select(g => g.HeaderKey).ToArray());

        // Only the two rows that change the Windows setting are marked, and the switch's effect text names the exact change.
        var marked = page.EnumerateRows().Where(r => r.ChangesHardware).Select(r => r.HeaderKey).ToArray();
        Assert.Equal(new[] { "row.bypass.header", "row.auto-on.header" }, marked);
        var effect = rig.Host.Loc.T(rig.Switch.HardwareEffect!.DescriptionKey);
        Assert.Contains("AutoConfigURL", effect);
        Assert.Contains("http://127.0.0.1:18081/proxy.pac", effect);
        Assert.Contains("18081 and 80", effect);
        Assert.Contains("previous", effect + "previous");
        Assert.False(rig.Switch.HardwareEffect!.SafeRevert);     // a revert is a change of the Windows setting: it is asked about
        Assert.True(rig.Switch.HardwareEffect!.Revertible);
        Assert.NotNull(rig.Switch.HardwareEffect!.RevertDescriptionKey);
        Assert.Contains("AutoConfigURL", rig.Host.Loc.T(rig.AutoOn.HardwareEffect!.DescriptionKey));
        // a Windows proxy setting is not hardware: the host words its questions as a system setting
        Assert.Equal(EffectKind.SystemSetting, rig.Switch.HardwareEffect!.Kind);
        Assert.Equal(EffectKind.SystemSetting, rig.AutoOn.HardwareEffect!.Kind);

        Assert.Equal(new[] { "steam.proxy.active", "steam.proxy.errors" }, page.EnumerateRows().OfType<ReadingRow>().Select(r => r.Metric.Value).ToArray());
        Assert.Equal(new[] { "steam.proxy.active", "steam.proxy.errors" }, page.Summary!.Readings!.Select(m => m.Value).ToArray());
        Assert.Equal(new[] { "check.store.title", "check.community.title", "check.static.title", "check.downloads.title" },
            page.Groups[2].Rows.OfType<ValueRow>().Select(r => r.HeaderKey).ToArray());
        Assert.Single(page.Groups[2].Rows.OfType<ButtonRow>());
        Assert.Empty(page.Links);
    }

    [Fact]
    public void The_module_requires_are_honest_in_the_manifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "module", "module.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal("steam-bypass", root.GetProperty("id").GetString());
        Assert.Equal("network", root.GetProperty("group").GetString());
        Assert.Equal("icon.svg", root.GetProperty("icon").GetString());   // F1: the key tile
        foreach (var file in new[] { "icon.svg", "icon-glyph.svg", "icon-unsupported.svg", "icon-16.png", "icon-32.png", "icon-128.png" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "module", file)), file + " is copied next to module.json");
        var requires = root.GetProperty("requires");
        var network = string.Join(" | ", requires.GetProperty("network").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("127.0.0.1:18081", network);
        Assert.Contains("127.0.0.1:80", network);
        Assert.Contains("loopback only", network);
        Assert.Contains("DNS over HTTPS", network);
        var registry = string.Join(" | ", requires.GetProperty("registry").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("HKCU", registry);
        Assert.Contains("AutoConfigURL", registry);
        Assert.False(requires.GetProperty("admin").GetBoolean());
        Assert.False(root.TryGetProperty("probePaths", out _));

        // The host refuses a manifest whose entries break these limits (found by loading the module in the real app).
        foreach (var list in new[] { "network", "registry", "services" })
            foreach (var entry in requires.GetProperty(list).EnumerateArray())
                Assert.InRange(entry.GetString()!.Length, 1, 200);
        Assert.InRange(root.GetProperty("description").GetString()!.Length, 1, 200);
        Assert.InRange(root.GetProperty("name").GetString()!.Length, 1, 60);
    }

    [Fact]
    public void Enabling_again_after_disabling_works_on_the_same_instance()
    {
        using var rig = new ModuleRig(approve: true);
        rig.FlipSwitch(true);
        rig.Module.Shutdown();
        Assert.Null(rig.Proxy.Value);
        rig.Host.CommandRegistry.Verbs.Clear();   // the real host removes a module's verbs after Shutdown
        rig.Module.Initialize(rig.Host);
        Assert.False(rig.Switch.Value.Value);
        rig.FlipSwitch(true);
        Assert.True(rig.IsOn);
    }
}

public class ModuleSwitchTests
{
    [Fact]
    public void Flipping_the_switch_on_listens_then_points_windows_at_the_pac_and_remembers_the_old_value()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = "http://corp.example/wpad.pac");
        rig.FlipSwitch(true);
        Assert.Equal(ModuleRig.Pac, rig.Proxy.Value);
        Assert.Equal(new[] { 18081, 80 }, rig.Listeners.Requested);
        Assert.Equal("http://corp.example/wpad.pac", rig.State.State!.Previous);
        Assert.Equal("Bypass on · 0 open", rig.StateLine);
        Assert.Equal("127.0.0.1:18081 (proxy), 127.0.0.1:80 (downloads)", rig.ValueOf("row.listening.header"));
    }

    [Fact]
    public void Flipping_it_off_puts_the_saved_value_back_closes_the_listeners_and_withdraws_the_metrics()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = "http://corp.example/wpad.pac");
        rig.FlipSwitch(true);
        Assert.Contains(rig.Host.MetricsBus.Snapshot(), p => p.Id.Value == "steam.proxy.active");
        rig.FlipSwitch(false);
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
        Assert.Null(rig.State.State);
        Assert.Equal(2, rig.Listeners.Disposed);
        Assert.Equal("Bypass off", rig.StateLine);
        Assert.Equal("Not listening", rig.ValueOf("row.listening.header"));
        Assert.Contains(new MetricId("steam.proxy.active"), rig.Host.MetricsBus.Withdrawn);
        Assert.Contains(new MetricId("steam.proxy.errors"), rig.Host.MetricsBus.Withdrawn);
    }

    [Fact]
    public void The_metrics_are_published_with_their_units_while_it_is_on()
    {
        using var rig = new ModuleRig();
        rig.FlipSwitch(true);
        rig.Module.Tick();
        var published = rig.Host.MetricsBus.Snapshot();
        Assert.Contains(published, p => p.Id.Value == "steam.proxy.active" && p.Unit == MetricUnit.Count && p.Value == 0);
        Assert.Contains(published, p => p.Id.Value == "steam.proxy.errors" && p.Unit == MetricUnit.Count && p.Value == 0);
    }

    [Fact]
    public void Port_18081_in_use_sends_the_switch_back_to_off_says_why_and_touches_nothing()
    {
        using var rig = new ModuleRig(arrange: r => r.Listeners.Taken.Add(18081));
        rig.Proxy.Value = "http://corp.example/wpad.pac";
        rig.FlipSwitch(true);
        Assert.False(rig.Switch.Value.Value);
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Contains("Port 18081 is already in use", rig.ValueOf("row.problem.header"));
        Assert.Equal("Could not turn on", rig.StateLine);
        Assert.Contains(rig.Host.ToastService.Shown, t => t.Kind == ToastKind.Error);
    }

    [Fact]
    public void A_registry_that_cannot_be_written_sends_the_switch_back_and_stops_the_proxy()
    {
        using var rig = new ModuleRig();
        rig.Proxy.WriteThrows = new UnauthorizedAccessException("denied by policy");
        rig.Switch.Value.Value = true;
        Assert.True(Wait.Until(() => rig.Host.ToastService.Shown.Count > 0));
        Assert.True(Wait.Until(() => !rig.Switch.Value.Value));
        Assert.Contains("denied by policy", rig.ValueOf("row.problem.header"));
        Assert.True(Wait.Until(() => rig.Listeners.Disposed == 2));
    }

    [Fact]
    public void Port_80_busy_turns_on_with_a_warning_and_the_info_row_opens()
    {
        using var rig = new ModuleRig(arrange: r => r.Listeners.Taken.Add(80));
        var infoBefore = rig.Rows.OfType<InfoRow>().Single(r => r.HeaderKey == "info.port80.header");
        Assert.False(infoBefore.IsOpen.Value);
        rig.FlipSwitch(true);
        Assert.True(Wait.Until(() => rig.Rows.OfType<InfoRow>().Single(r => r.HeaderKey == "info.port80.header").IsOpen.Value));
        Assert.Equal(Severity.Warning, infoBefore.Severity);
        Assert.Equal("On, no game downloads · 0 open", rig.StateLine);
        Assert.Contains("port 80 is not available", rig.ValueOf("row.listening.header"));
        Assert.Contains(rig.Host.ToastService.Shown, t => t.Kind == ToastKind.Warning);
    }

    [Fact]
    public void Setting_the_switch_to_what_it_already_is_does_nothing()
    {
        using var rig = new ModuleRig();
        rig.Switch.Value.Value = false;
        rig.WaitUntilSwitchWorkersAreDone();   // no change, so no worker was started; if one was, wait for it and then look
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Empty(rig.Listeners.Requested);
    }

    [Fact]
    public void Connections_and_the_dashboard_line_follow_the_proxy_counters()
    {
        using var rig = new ModuleRig();
        rig.FlipSwitch(true);
        var engine = ReflectEngine(rig.Module);
        Assert.NotNull(engine);
        rig.Module.Tick();
        Assert.Equal("Bypass on · 0 open", rig.StateLine);
        Assert.Equal(new[] { "steam.proxy.active", "steam.proxy.errors" }, rig.Page.Summary!.Readings!.Select(m => m.Value).ToArray());
    }

    private static object? ReflectEngine(SteamBypassModule m) =>
        typeof(SteamBypassModule).GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(m);
}

public class ModuleCleanupTests
{
    [Fact]
    public void Shutdown_while_on_restores_the_setting_and_closes_everything()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = "http://corp.example/wpad.pac");
        rig.FlipSwitch(true);
        rig.Module.Shutdown();
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
        Assert.Null(rig.State.State);
        Assert.Equal(2, rig.Listeners.Disposed);
        Assert.Contains(new MetricId("steam.proxy.active"), rig.Host.MetricsBus.Withdrawn);
    }

    [Fact]
    public void Shutdown_when_it_was_never_on_changes_nothing()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = "http://corp.example/wpad.pac");
        rig.Module.Shutdown();
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
    }

    [Fact]
    public void Shutdown_twice_is_fine()
    {
        var rig = new ModuleRig();
        rig.FlipSwitch(true);
        rig.Module.Shutdown();
        rig.Module.Shutdown();
        Assert.Null(rig.Proxy.Value);
    }

    [Fact]
    public void Crash_recovery_at_start_puts_the_saved_value_back_before_anything_else()
    {
        using var rig = new ModuleRig(arrange: r =>
        {
            r.Proxy.Value = ModuleRig.Pac;   // the previous run died with the bypass on
            r.State.State = new ProxyState(ModuleRig.Pac, "http://corp.example/wpad.pac");
        });
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
        Assert.Null(rig.State.State);
        Assert.Empty(rig.Listeners.Requested);
        Assert.False(rig.Switch.Value.Value);
        Assert.Contains(rig.Host.LogSink.Entries, e => e.Message.Contains("restored"));
    }

    [Fact]
    public void Crash_recovery_leaves_a_running_standalone_app_alone()
    {
        using var rig = new ModuleRig(arrange: r =>
        {
            r.Proxy.Value = ModuleRig.Pac;
            r.PortAnswers = true;   // something else answers on 18081
        });
        Assert.Equal(ModuleRig.Pac, rig.Proxy.Value);
        Assert.Equal(0, rig.Proxy.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_standalone_app_that_owns_the_address_is_not_disturbed_by_start_stop_or_exit(bool itIsAnswering)
    {
        // This is the situation on the author's machine: the old Steam Bypass app is running and its address is in Windows.
        var rig = new ModuleRig(arrange: r => { r.Proxy.Value = ModuleRig.Pac; r.PortAnswers = itIsAnswering; });
        Assert.Equal(ModuleRig.Pac, rig.Proxy.Value);
        rig.Module.Shutdown();
        Assert.Equal(ModuleRig.Pac, rig.Proxy.Value);
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Equal(0, rig.Proxy.Notifications);
    }

    [Fact]
    public void A_restore_that_fails_at_shutdown_is_logged_and_does_not_throw()
    {
        var rig = new ModuleRig();
        rig.FlipSwitch(true);
        rig.Proxy.DeleteThrows = new IOException("busy");   // nothing was set before, so the restore is a delete
        rig.Module.Shutdown();   // must not throw: the host would fault the module
        Assert.Contains(rig.Host.LogSink.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("could not be restored"));
        Assert.Equal(2, rig.Listeners.Disposed);   // the proxy is stopped anyway
        Assert.NotNull(rig.State.State);            // and the saved value is kept for the next start
    }
}

public class ModuleCommandTests
{
    [Fact]
    public async Task On_is_declined_nothing_is_changed()
    {
        using var rig = new ModuleRig(approve: false);
        rig.Proxy.Value = "http://corp.example/wpad.pac";
        var result = await rig.Run("on");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Not confirmed", result.Output);
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Empty(rig.Listeners.Requested);
        Assert.Null(rig.State.State);
        Assert.False(rig.Switch.Value.Value);
        var asked = Assert.Single(rig.Host.ApprovalService.Asked);
        Assert.Equal("effect.bypass", asked.Effect.DescriptionKey);
        Assert.Equal("Steam bypass: Off → On", rig.Host.Loc.Resolve(asked.Detail!.Value));
    }

    [Fact]
    public async Task On_is_approved_the_bypass_turns_on()
    {
        using var rig = new ModuleRig(approve: true);
        var result = await rig.Run("on");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("The bypass is on.", result.Output);
        Assert.True(rig.IsOn);
        Assert.True(Wait.Until(() => rig.Switch.Value.Value));
    }

    [Fact]
    public async Task Off_is_declined_the_bypass_stays_on_and_the_setting_stays_ours()
    {
        using var rig = new ModuleRig(approve: true);
        await rig.Run("on");
        rig.Host.ApprovalService.Answer = false;
        var result = await rig.Run("off");
        Assert.Equal(1, result.ExitCode);
        Assert.True(rig.IsOn);
        Assert.Equal(0, rig.Listeners.Disposed);
        Assert.Equal("Steam bypass: On → Off", rig.Host.Loc.Resolve(rig.Host.ApprovalService.Asked.Last().Detail!.Value));
    }

    [Fact]
    public async Task Off_is_approved_works_with_the_page_never_opened()
    {
        using var rig = new ModuleRig(approve: true, arrange: r => r.Proxy.Value = "http://corp.example/wpad.pac");
        Assert.False(rig.Host.PageVisibleValue.Value);   // the page is closed all the time
        await rig.Run("on");
        var result = await rig.Run("off");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("back", result.Output);
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
        Assert.False(rig.Switch.Value.Value);
    }

    [Fact]
    public async Task On_when_already_on_and_off_when_already_off_ask_nothing_and_change_nothing()
    {
        using var rig = new ModuleRig(approve: true);
        var off = await rig.Run("off");
        Assert.Equal(0, off.ExitCode);
        Assert.Equal("The bypass is already off.", off.Output);
        await rig.Run("on");
        int asked = rig.Host.ApprovalService.Asked.Count;
        var on = await rig.Run("on");
        Assert.Equal("The bypass is already on.", on.Output);
        Assert.Equal(asked, rig.Host.ApprovalService.Asked.Count);
    }

    [Fact]
    public async Task On_reports_why_it_failed()
    {
        using var rig = new ModuleRig(approve: true, arrange: r => r.Listeners.Taken.Add(18081));
        var result = await rig.Run("on");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Port 18081 is already in use", result.Output);
        Assert.Equal(0, rig.Proxy.Writes);
    }

    [Fact]
    public async Task Off_after_a_failed_restore_asks_again_and_retries()
    {
        using var rig = new ModuleRig(approve: true, arrange: r => r.Proxy.Value = "http://corp.example/wpad.pac");
        await rig.Run("on");
        rig.Proxy.WriteThrows = new IOException("busy");
        var failed = await rig.Run("off");
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("could not be put back", failed.Output);
        rig.Proxy.WriteThrows = null;
        var retry = await rig.Run("off");
        Assert.Equal(0, retry.ExitCode);
        Assert.Equal("http://corp.example/wpad.pac", rig.Proxy.Value);
    }

    [Theory]
    [InlineData("on", "extra")]
    [InlineData("off", "now")]
    [InlineData("status", "all")]
    [InlineData("test", "fast")]
    public async Task Extra_arguments_are_a_usage_error_and_change_nothing(string verb, string arg)
    {
        using var rig = new ModuleRig(approve: true);
        var result = await rig.Run(verb, arg);
        if (verb == "status") { Assert.Equal(0, result.ExitCode); return; }   // status ignores arguments
        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("Usage: rigbay steam-bypass", result.Output);
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Empty(rig.Host.ApprovalService.Asked);
    }

    [Fact]
    public async Task Status_says_everything_in_five_lines_and_reads_only()
    {
        using var rig = new ModuleRig(approve: true);
        var off = await rig.Run("status");
        Assert.Equal(0, off.ExitCode);
        var lines = off.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal("Bypass: off", lines[0]);
        Assert.Equal("Listening: Not listening", lines[1]);
        Assert.Equal("Connections: 0 open, 0 in total, 0 failed, 0 refused", lines[2]);
        Assert.Equal("Steam: Steam is not running", lines[3]);
        Assert.Equal("Windows proxy setting: not set by the bypass", lines[4]);

        await rig.Run("on");
        rig.Steam.Running = new() { new SteamProcess(10, ModuleRig.T0.AddMinutes(-5)) };
        rig.Now = ModuleRig.T0.AddMinutes(5);
        var on = await rig.Run("status");
        Assert.StartsWith("Bypass: on since 18:42", on.Output);
        Assert.Contains("Listening: 127.0.0.1:18081 (proxy), 127.0.0.1:80 (downloads)", on.Output);
        Assert.Contains("Steam: Steam was opened before the bypass: restart it", on.Output);
        Assert.Contains("Windows proxy setting: points at the bypass", on.Output);
        Assert.Equal(0, rig.Proxy.Writes - 1);   // the only write is the one from "on"
    }

    [Fact]
    public async Task The_test_verb_runs_the_four_checks_and_exits_0_only_if_all_pass()
    {
        using var rig = new ModuleRig();
        var ok = await rig.Run("test");
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains("Store: Reachable · HTTP 200 · 42 ms", ok.Output);
        Assert.EndsWith("All 4 checks passed", ok.Output);
        Assert.Equal(4, rig.Probe.Calls.Count);
        Assert.All(rig.Probe.Calls, c => Assert.Null(c.ProxyPort));   // the bypass is off: direct requests

        rig.Probe.Failing.Add("community");
        var bad = await rig.Run("test");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("Community: Blocked (connection reset)", bad.Output);
        Assert.EndsWith("1 of 4 checks failed", bad.Output);
    }

    [Fact]
    public async Task With_the_bypass_on_the_checks_go_through_the_proxy_port()
    {
        using var rig = new ModuleRig(approve: true);
        await rig.Run("on");
        await rig.Run("test");
        Assert.All(rig.Probe.Calls, c => Assert.Equal(18081, c.ProxyPort));
        Assert.Equal(new[] { "community", "downloads", "static", "store" }, rig.Probe.Calls.Select(c => c.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void The_four_checks_are_the_ones_of_the_old_app()
    {
        Assert.Equal(new[] { "store", "community", "static", "downloads" }, CheckSpec.All.Select(s => s.Id).ToArray());
        Assert.Equal("https://store.steampowered.com/", CheckSpec.All[0].Url.ToString());
        Assert.Equal("https://steamcommunity.com/", CheckSpec.All[1].Url.ToString());
        Assert.Equal("https://store.cloudflare.steamstatic.com/", CheckSpec.All[2].Url.ToString());
        Assert.Equal("http://127.0.0.1/", CheckSpec.All[3].Url.ToString());
        Assert.Equal("cache8-hkg1.steamcontent.com", CheckSpec.All[3].HostHeader);
        Assert.Equal(new[] { true, true, true, false }, CheckSpec.All.Select(s => s.UseProxy).ToArray());
    }

    [Fact]
    public async Task The_run_test_button_shows_each_result_on_its_row_and_reports_the_total()
    {
        using var rig = new ModuleRig();
        rig.Probe.Failing.Add("downloads");
        var result = await rig.TestButton.InvokeAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("1 of 4 checks failed", rig.Host.Loc.Resolve(result.Message!.Value));
        Assert.Equal("Reachable · HTTP 200 · 42 ms", rig.ValueOf("check.store.title"));
        Assert.Equal("Blocked (connection reset)", rig.ValueOf("check.downloads.title"));

        rig.Probe.Failing.Clear();
        var ok = await rig.TestButton.InvokeAsync(CancellationToken.None);
        Assert.True(ok.Succeeded);
        Assert.Equal("All 4 checks passed", rig.Host.Loc.Resolve(ok.Message!.Value));
    }

    [Fact]
    public async Task A_second_test_while_one_runs_is_refused_not_stacked()
    {
        using var rig = new ModuleRig();
        var gate = new TaskCompletionSource();
        rig.Probe.Gate = gate.Task;
        var first = rig.Run("test");
        Assert.True(Wait.Until(() => rig.Probe.Calls.Count == 4));
        Assert.Equal("Testing…", rig.ValueOf("check.store.title"));
        var second = await rig.Run("test");
        Assert.Equal(1, second.ExitCode);
        Assert.Equal("A test is already running", second.Output);
        gate.SetResult();
        Assert.Equal(0, (await first).ExitCode);
        Assert.Equal(4, rig.Probe.Calls.Count);
    }
}

public class ModuleSettingsTests
{
    [Fact]
    public void Auto_on_turns_the_bypass_on_at_start()
    {
        using var rig = new ModuleRig(initialize: false, arrange: r => r.Host.SettingsStore.Stored = new SteamBypassSettings { AutoOn = true });
        rig.Module.Initialize(rig.Host);
        Assert.True(Wait.Until(() => rig.IsOn));
        Assert.True(Wait.Until(() => rig.Switch.Value.Value));
        Assert.True(rig.AutoOn.Value.Value);
    }

    [Fact]
    public void Without_auto_on_nothing_is_changed_at_start()
    {
        using var rig = new ModuleRig();
        rig.WaitUntilSwitchWorkersAreDone();   // nothing was asked for, so there is nothing to wait for; a stray worker would be waited for
        Assert.False(rig.IsOn);
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.False(rig.AutoOn.Value.Value);
    }

    [Fact]
    public void Auto_on_with_a_busy_port_fails_visibly_and_changes_nothing()
    {
        using var rig = new ModuleRig(initialize: false, arrange: r =>
        {
            r.Host.SettingsStore.Stored = new SteamBypassSettings { AutoOn = true };
            r.Listeners.Taken.Add(18081);
        });
        rig.Module.Initialize(rig.Host);
        Assert.True(Wait.Until(() => rig.Host.ToastService.Shown.Count > 0));
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.False(rig.Switch.Value.Value);
    }

    [Fact]
    public void The_auto_on_setting_is_saved_when_it_changes()
    {
        using var rig = new ModuleRig();
        rig.AutoOn.Value.Value = true;
        Assert.Equal(1, rig.Host.SettingsStore.Saves);
        Assert.True(((SteamBypassSettings)rig.Host.SettingsStore.Stored!).AutoOn);
        rig.AutoOn.Value.Value = false;
        Assert.False(((SteamBypassSettings)rig.Host.SettingsStore.Stored!).AutoOn);
    }

    [Fact]
    public void The_domain_list_comes_from_the_data_folder_first_then_the_module_folder_then_the_built_in_list()
    {
        using var rig = new ModuleRig(initialize: false);
        Directory.CreateDirectory(rig.Host.DataDirectory);
        File.WriteAllText(Path.Combine(rig.Host.DataDirectory, "steam-domains.txt"), "# mine\nmy-steam.example\n");
        rig.Module.Initialize(rig.Host);
        Assert.Equal("1 names, from steam-domains.txt", rig.ValueOf("row.domains.header"));

        using var plain = new ModuleRig();
        Assert.Equal("15 names, from steam-domains.txt", plain.ValueOf("row.domains.header"));
    }
}

public class ModuleSteamRowTests
{
    private static string Steam(ModuleRig rig) => rig.ValueOf("row.steam.header");

    [Fact]
    public void The_steam_row_follows_the_five_states()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Host.PageVisibleValue.Value = true;   // the row is only worked out while somebody looks at it

        rig.Module.Tick();
        Assert.Equal("Steam is not running", Steam(rig));

        rig.Steam.Running = new() { new SteamProcess(1, ModuleRig.T0.AddHours(-1)) };
        rig.Module.Tick();
        Assert.Equal("Steam is running; the bypass is off", Steam(rig));

        rig.FlipSwitch(true);
        rig.Now = ModuleRig.T0.AddSeconds(10);
        rig.Module.Tick();
        Assert.Equal("Waiting for Steam to connect", Steam(rig));
        Assert.False(rig.Rows.OfType<InfoRow>().Single(r => r.HeaderKey == "info.steam-restart.header").IsOpen.Value);

        rig.Now = ModuleRig.T0.AddMinutes(2);
        rig.Module.Tick();
        Assert.Equal("Steam was opened before the bypass: restart it", Steam(rig));
        Assert.True(rig.Rows.OfType<InfoRow>().Single(r => r.HeaderKey == "info.steam-restart.header").IsOpen.Value);

        rig.Steam.Connected = true;
        rig.Module.Tick();
        Assert.Equal("Steam is using the bypass", Steam(rig));
        Assert.False(rig.Rows.OfType<InfoRow>().Single(r => r.HeaderKey == "info.steam-restart.header").IsOpen.Value);
    }

    [Theory]
    [InlineData(false, false, 0, 0, "NotRunning")]
    [InlineData(true, false, 0, 0, "NotRunning")]
    [InlineData(false, false, 600, 600, "RunningBypassOff")]
    [InlineData(true, true, 600, 600, "Using")]
    [InlineData(true, true, 5, 5, "Using")]
    [InlineData(true, false, 10, 600, "Starting")]       // the bypass was turned on 10 s ago
    [InlineData(true, false, 600, 10, "Starting")]       // Steam itself started 10 s ago
    [InlineData(true, false, 44, 600, "Starting")]
    [InlineData(true, false, 46, 46, "NeedsRestart")]
    [InlineData(true, false, 3600, 3600, "NeedsRestart")]
    public void Steam_state_rules(bool on, bool uses, int onForSeconds, int steamForSeconds, string expected)
    {
        var now = ModuleRig.T0;
        var procs = steamForSeconds == 0 && expected == "NotRunning" ? new List<SteamProcess>() : new List<SteamProcess> { new(7, now.AddSeconds(-steamForSeconds)) };
        Assert.Equal(expected, SteamLink.Evaluate(on, on ? now.AddSeconds(-onForSeconds) : null, procs, uses, now).ToString());
    }

    [Fact]
    public void The_steam_row_is_not_worked_out_while_nobody_looks()
    {
        using var rig = new ModuleRig();
        rig.Steam.Running = new() { new SteamProcess(1, ModuleRig.T0.AddHours(-1)) };
        rig.Module.Tick();   // page not visible
        Assert.Equal("Steam is not running", Steam(rig));
    }
}

public class ModulePollingTests
{
    [Fact]
    public void The_rate_is_fast_only_while_the_page_is_open_or_somebody_reads_a_metric()
    {
        using var rig = new ModuleRig();
        Assert.Equal(TimeSpan.FromSeconds(10), rig.Module.CurrentPollInterval);
        rig.Host.PageVisibleValue.Value = true;
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Module.CurrentPollInterval);
        rig.Host.PageVisibleValue.Value = false;
        Assert.Equal(TimeSpan.FromSeconds(10), rig.Module.CurrentPollInterval);
        rig.Host.MetricsBus.SetSubscribers(SteamBypassModule.ActiveMetric, 1);   // the dashboard bay
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Module.CurrentPollInterval);
        rig.Host.MetricsBus.SetSubscribers(SteamBypassModule.ActiveMetric, 0);
        Assert.Equal(TimeSpan.FromSeconds(10), rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void The_module_unsubscribes_at_shutdown()
    {
        var rig = new ModuleRig();
        rig.Module.Shutdown();
        rig.Host.PageVisibleValue.Value = true;   // a late event must not reach a stopped module
        rig.Host.MetricsBus.SetSubscribers(SteamBypassModule.ActiveMetric, 1);
        Assert.Equal(0, rig.Proxy.Writes);
    }
}

public class ModuleLanguageTests
{
    [Fact]
    public void Vietnamese_renders_the_page_without_falling_back_to_keys()
    {
        using var rig = new ModuleRig("vi");
        Assert.Equal("Bypass đang tắt", rig.StateLine);
        foreach (var row in rig.Rows)
        {
            Assert.DoesNotContain("module.steam-bypass.", rig.Host.Loc.T(row.HeaderKey));
            if (row.DescriptionKey is not null) Assert.DoesNotContain("module.steam-bypass.", rig.Host.Loc.T(row.DescriptionKey));
        }
        Assert.Contains("AutoConfigURL", rig.Host.Loc.T("effect.bypass"));
    }

    [Fact]
    public void Switching_the_language_refreshes_the_texts()
    {
        using var rig = new ModuleRig();
        Assert.Equal("Not listening", rig.ValueOf("row.listening.header"));
        rig.Host.Loc.Switch("vi");
        Assert.Equal("Không lắng nghe", rig.ValueOf("row.listening.header"));
        Assert.Equal("Bypass đang tắt", rig.StateLine);
    }

    [Fact]
    public void Every_language_key_used_in_the_source_exists_in_both_files()
    {
        var en = FakeLocalisation.Load("en");
        var vi = FakeLocalisation.Load("vi");
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(DohTests.SourceRoot(), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"((?:row|group|state|effect|detail|info|metric|listen|steam|problem|domains|check|button|test|toast|cmd|status)\\.[a-z0-9.-]+)\""))
                if (!m.Groups[1].Value.StartsWith("steam.proxy.", StringComparison.Ordinal)) used.Add(m.Groups[1].Value);   // the two metric ids are not language keys
        Assert.True(used.Count > 60);
        foreach (var key in used)
        {
            Assert.True(en.ContainsKey("module.steam-bypass." + key), "missing in en.lang: " + key);
            Assert.True(vi.ContainsKey("module.steam-bypass." + key), "missing in vi.lang: " + key);
        }
        Assert.Equal(en.Keys.OrderBy(k => k), vi.Keys.OrderBy(k => k));
    }
}

public class LogThrottleTests
{
    [Fact]
    public void Only_the_burst_gets_through_per_window_and_the_rest_is_counted()
    {
        var now = ModuleRig.T0;
        var t = new LogThrottle(3, TimeSpan.FromMinutes(1), () => now);
        int allowed = 0;
        for (int i = 0; i < 10; i++) if (t.Allow(out int left)) { allowed++; Assert.Equal(0, left); }
        Assert.Equal(3, allowed);

        now = now.AddSeconds(59);
        Assert.False(t.Allow(out _));
        now = now.AddSeconds(2);   // a new window: the first line reports what was left out in the last one
        Assert.True(t.Allow(out int suppressed));
        Assert.Equal(8, suppressed);
        Assert.True(t.Allow(out int none));
        Assert.Equal(0, none);
    }

    [Fact]
    public async Task A_flood_of_failures_writes_few_log_lines_but_counts_every_error()
    {
        using var rig = new ModuleRig(approve: true);
        rig.FlipSwitch(true);
        var engine = (Proxy.ProxyEngine)typeof(SteamBypassModule).GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(rig.Module)!;
        rig.Dialer.Throws = new IOException("cannot connect");
        // The listeners are fakes, so connections are fed to the handler directly through a real loopback pair.
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        int before = rig.Host.LogSink.Entries.Count(e => e.Level == LogLevel.Warn);
        for (int i = 0; i < 60; i++)
        {
            using var client = new System.Net.Sockets.TcpClient();
            var accept = server.AcceptTcpClientAsync();
            client.Connect(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)server.LocalEndpoint).Port);
            using var accepted = await accept;
            client.GetStream().Write(System.Text.Encoding.ASCII.GetBytes("CONNECT store.steampowered.com:443 HTTP/1.1\r\n\r\n"));
            await engine.HandleProxyAsync(accepted, CancellationToken.None);
        }
        Assert.Equal(60, engine.Counters.Failed);
        int warnings = rig.Host.LogSink.Entries.Count(e => e.Level == LogLevel.Warn) - before;
        Assert.InRange(warnings, 1, 20);
    }
}
