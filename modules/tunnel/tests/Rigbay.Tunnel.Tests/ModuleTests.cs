using System.Text.Json;
using Rigbay.Abstractions;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel.Tests;

/// <summary>One module on a fake machine and a fake host.</summary>
internal sealed class Rig
{
    public const string Name = "example-tunnel";
    public const string Endpoint = "198.51.100.1:51820";
    public const string PublicAddress = "https://remote.example.test/";

    public readonly World W = new();
    public readonly FakeHost Host;
    public readonly TunnelModule Module;

    public Rig(string language = "en", params string[] names)
    {
        foreach (var n in names.Length > 0 ? names : new[] { Name }) W.Store.Add(n);
        Host = new FakeHost(language);
        Module = new TunnelModule(W.Dependencies);
        // what a connected tunnel reports
        W.Tools.ShowOutput = (_, f) => f switch
        {
            WgField.LatestHandshakes => World.Handshakes(W.Clock.Now.AddSeconds(-1)),
            WgField.Transfer => World.Transfer(1000 + 100 * W.Clock.Delays, 500),
            _ => World.Endpoint(Endpoint),
        };
    }

    public Rig Running(string name = Name, StartKind start = StartKind.Automatic) { W.Services.Set(name, ServiceState.Running, start); return this; }

    public Rig Start()
    {
        Assert.True(Module.Probe().Supported);
        Module.Initialize(Host);
        return this;
    }

    public PageModel Page() => Module.GetPageModel(Host)!;

    public T Row<T>(string header, string tunnel = Name) where T : Row
    {
        var expander = (ExpanderRow)Page().Groups.Single(g => g.HeaderKey == "group.tunnels").Rows.Single(r => r.HeaderKey == Text.Literal(tunnel));
        return (T)expander.Children.Single(c => c.HeaderKey == header);
    }

    public string Words(HardwareEffect effect) => Host.Loc.T(effect.DescriptionKey);
    public string Words(LocalisedText? text) => text is { } t ? Host.Loc.Resolve(t) : "";
    public string[] Toasts() => Host.ToastService.All().Select(t => Host.Loc.Resolve(t.Body)).ToArray();
    public Task Idle() => Module.WhenIdleAsync();
}

public class ModuleTests
{
    // ------------------------------------------------------------------ probe, start

    [Fact]
    public void Without_WireGuard_the_module_is_unsupported_and_says_where_to_get_it()
    {
        var rig = new Rig();
        rig.W.Tools.ProbeResult = new ToolProbe(false, "probe.no-wireguard");
        var probe = rig.Module.Probe();
        Assert.False(probe.Supported);
        Assert.Equal("probe.no-wireguard", probe.ReasonKey);
        Assert.Contains("wireguard.com/install", rig.Host.Loc.T(probe.ReasonKey));

        rig.W.Tools.ProbeResult = new ToolProbe(false, "probe.not-signed");
        Assert.Contains("WireGuard LLC", rig.Host.Loc.T(rig.Module.Probe().ReasonKey));
    }

    [Fact]
    public void Starting_the_module_changes_nothing_on_the_machine()
    {
        var rig = new Rig(names: new[] { "one", "two", "three" });
        rig.Running("two");
        rig.W.Services.Set("three", ServiceState.Stopped);
        rig.Start();

        Assert.DoesNotContain(rig.W.Tools.Calls, c => c is "install" or "uninstall");
        Assert.Empty(rig.W.Services.StartChanges);
        Assert.Equal(0, rig.Host.ApprovalService.Count);
        Assert.Empty(rig.Host.ToastService.All());
        Assert.Equal(0, rig.Host.SettingsStore.Saves);
        Assert.Empty(rig.W.Tools.Shows);   // not even a status read while nobody looks
    }

    [Fact]
    public void Initialize_registers_a_tray_item_a_hotkey_per_tunnel_and_the_four_verbs()
    {
        var rig = new Rig(names: new[] { "example-tunnel", "Other_2" }).Running().Start();

        Assert.Equal(new[] { "toggle-example-tunnel", "toggle-other-2-" + TunnelNames.Slug("Other_2")[^4..] }, rig.Host.TrayService.Items.Select(i => i.Id).ToArray());
        Assert.True(rig.Host.TrayService.Item("toggle-example-tunnel").Checkable);
        Assert.True(rig.Host.TrayService.Item("toggle-example-tunnel").Checked.Value);
        Assert.Equal(2, rig.Host.HotkeyService.Registered.Count);
        Assert.All(rig.Host.HotkeyService.Registered, r => Assert.Null(r.Default));   // unbound until the user chooses
        Assert.Equal(new[] { "list", "off", "on", "status" }, rig.Host.CommandRegistry.Verbs.Keys.OrderBy(k => k).ToArray());
    }

    // ------------------------------------------------------------------ the tray and the hotkey ask, and tell

    [Fact]
    public async Task A_declined_question_changes_nothing_and_shows_nothing()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = false;

        rig.Host.TrayService.Item("toggle-example-tunnel").Invoke();
        await rig.Idle();

        Assert.Equal(1, rig.Host.ApprovalService.Count);
        Assert.DoesNotContain(rig.W.Tools.Calls, c => c is "install" or "uninstall");
        Assert.Empty(rig.Host.ToastService.All());
        Assert.False(rig.W.Services.Of(Rig.Name).Exists);
    }

    [Fact]
    public async Task The_tray_turns_a_tunnel_on_after_the_exact_question_and_a_toast_says_it_is_connected()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;

        rig.Host.TrayService.Item("toggle-example-tunnel").Invoke();
        await rig.Idle();

        var (effect, detail) = rig.Host.ApprovalService.Asked.Single();
        Assert.Equal("Turn on tunnel example-tunnel", rig.Words(effect));
        Assert.Equal(EffectKind.SystemSetting, effect.Kind);
        Assert.Contains("Windows service", rig.Words(detail));
        Assert.Equal(new[] { @"C:\Store\example-tunnel.conf.dpapi" }, rig.W.Tools.Installed);
        Assert.True(rig.W.Services.Of(Rig.Name).IsRunning);
        Assert.True(rig.Host.TrayService.Item("toggle-example-tunnel").Checked.Value);
        Assert.Equal(new[] { "Tunnel example-tunnel is on and connected." }, rig.Toasts());
        Assert.Equal(ToastKind.Success, rig.Host.ToastService.All().Single().Kind);
    }

    [Fact]
    public async Task A_hotkey_turns_it_off_after_asking_and_a_toast_says_so()
    {
        var rig = new Rig().Running().Start();
        rig.Host.ApprovalService.Answer = true;

        rig.Host.HotkeyService.Press("toggle-example-tunnel");
        await rig.Idle();

        Assert.Equal("Turn off tunnel example-tunnel", rig.Words(rig.Host.ApprovalService.Asked.Single().Effect));
        Assert.Equal(new[] { Rig.Name }, rig.W.Tools.Uninstalled);
        Assert.False(rig.W.Services.Of(Rig.Name).Exists);
        Assert.False(rig.Host.TrayService.Item("toggle-example-tunnel").Checked.Value);
        Assert.Equal(new[] { "Tunnel example-tunnel is off." }, rig.Toasts());
    }

    [Fact]
    public async Task No_handshake_in_thirty_seconds_gets_a_warning_in_plain_words()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;
        rig.W.Tools.ShowOutput = (_, f) => f == WgField.LatestHandshakes ? World.Handshakes(null) : null;

        rig.Host.TrayService.Item("toggle-example-tunnel").Invoke();
        await rig.Idle();

        var toast = rig.Host.ToastService.All().Single();
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Equal("Tunnel example-tunnel is on, but the server is not answering. Check the internet connection or the server.", rig.Words(toast.Body));
        Assert.True(rig.W.Services.Of(Rig.Name).IsRunning);   // it stays on: the owner decides
    }

    [Fact]
    public async Task A_failure_is_shown_whatever_the_origin_and_the_row_names_the_problem()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;
        rig.W.Tools.OnInstall = name => { rig.W.Services.Set(name, ServiceState.Stopped, win32: 1053); return new ToolRun(1); };

        rig.Host.TrayService.Item("toggle-example-tunnel").Invoke();
        await rig.Idle();

        var toast = rig.Host.ToastService.All().Single();
        Assert.Equal(ToastKind.Error, toast.Kind);
        Assert.Contains("did not start", rig.Words(toast.Body));
        Assert.Contains("did not start", rig.Row<ValueRow>("row.problem.header").Value.Value);
        Assert.False(rig.Host.TrayService.Item("toggle-example-tunnel").Checked.Value);
    }

    // ------------------------------------------------------------------ the page

    [Fact]
    public async Task The_page_switch_is_marked_as_a_system_change_so_the_host_asks_and_the_module_does_not_ask_twice()
    {
        var rig = new Rig().Start();
        var row = rig.Row<ToggleRow>("row.on.header");
        Assert.Equal(EffectKind.SystemSetting, row.HardwareEffect!.Kind);
        Assert.False(row.HardwareEffect.SafeRevert);   // a revert is asked about too
        Assert.Contains("example-tunnel", rig.Words(row.HardwareEffect));

        row.Value.Value = true;   // the host asked and the user said yes: it sets the value
        await rig.Idle();

        Assert.Equal(0, rig.Host.ApprovalService.Count);
        Assert.True(rig.W.Services.Of(Rig.Name).IsRunning);
        Assert.True(row.Value.Value);
        Assert.Empty(rig.Host.ToastService.All());   // the switch itself shows it (success from the page is quiet)

        row.Value.Value = false;
        await rig.Idle();
        Assert.False(rig.W.Services.Of(Rig.Name).Exists);
        Assert.False(row.Value.Value);
    }

    [Fact]
    public async Task A_page_switch_that_fails_goes_back_to_what_is_real()
    {
        var rig = new Rig().Start();
        rig.W.Tools.OnInstall = _ => new ToolRun(5);
        var row = rig.Row<ToggleRow>("row.on.header");

        row.Value.Value = true;
        await rig.Idle();

        Assert.False(row.Value.Value);
        Assert.Equal(ToastKind.Error, rig.Host.ToastService.All().Single().Kind);
        Assert.Contains("exit code 5", rig.Row<ValueRow>("row.problem.header").Value.Value);
    }

    [Fact]
    public void The_page_lists_each_tunnel_with_status_options_and_a_hotkey_row()
    {
        var rig = new Rig().Running().Start();
        var page = rig.Page();
        var headers = ((ExpanderRow)page.Groups.Single(g => g.HeaderKey == "group.tunnels").Rows.Single()).Children.Select(c => c.HeaderKey).ToArray();
        Assert.Contains("row.on.header", headers);
        Assert.Contains("row.handshake.header", headers);
        Assert.Contains("row.start.header", headers);
        Assert.Contains("row.autooff.header", headers);
        Assert.Contains("row.port.header", headers);
        Assert.Contains("row.public.header", headers);
        Assert.Contains("row.hotkey.header", headers);
        Assert.Equal("1 of 1 on", rig.Host.Loc.Resolve(page.Summary!.StateLine!.Value));
        Assert.Contains(page.Links, l => l.Url == "https://www.wireguard.com/install/");
    }

    [Fact]
    public async Task No_tunnels_and_an_unreadable_store_show_a_message_not_an_empty_page()
    {
        var rig = new Rig();
        rig.W.Store.Tunnels.Clear();
        rig.Start();
        var page = rig.Page();
        Assert.DoesNotContain(page.Groups, g => g.HeaderKey == "group.tunnels");
        var info = page.Groups.Single(g => g.HeaderKey == "group.info").Rows.OfType<InfoRow>().ToDictionary(r => r.HeaderKey);
        Assert.True(info["info.no-tunnels.header"].IsOpen.Value);
        Assert.False(info["info.store-unreadable.header"].IsOpen.Value);

        rig.W.Store.Readable = false;
        await rig.Module.PollOnceAsync();
        Assert.True(info["info.store-unreadable.header"].IsOpen.Value);
        Assert.False(info["info.no-tunnels.header"].IsOpen.Value);
    }

    [Fact]
    public async Task A_tunnel_that_appears_or_goes_gets_a_new_page_a_tray_item_and_nothing_else_asks_for_one()
    {
        var rig = new Rig().Start();
        await rig.Module.PollOnceAsync();
        Assert.Equal(0, rig.Host.PageRefreshRequests);   // nothing changed

        rig.W.Store.Add("second");
        await rig.Module.PollOnceAsync();
        Assert.Equal(1, rig.Host.PageRefreshRequests);
        Assert.Contains(rig.Host.TrayService.Items, i => i.Id == "toggle-second");

        rig.W.Store.Tunnels.RemoveAll(t => t.Name == "second");
        await rig.Module.PollOnceAsync();
        Assert.Equal(2, rig.Host.PageRefreshRequests);
        Assert.False(rig.Host.TrayService.Item("toggle-second").Visible.Value);
        Assert.DoesNotContain(rig.Page().Groups.Single(g => g.HeaderKey == "group.tunnels").Rows, r => r.HeaderKey == Text.Literal("second"));
    }

    [Fact]
    public async Task A_tunnel_turned_on_in_the_WireGuard_app_shows_up_on_the_page()
    {
        var rig = new Rig().Start();
        var on = rig.Row<ToggleRow>("row.on.header");
        Assert.False(on.Value.Value);
        rig.Running();   // the official app activated it
        await rig.Module.PollOnceAsync();
        Assert.True(on.Value.Value);
        Assert.Equal(0, rig.Host.ApprovalService.Count);
        Assert.DoesNotContain(rig.W.Tools.Calls, c => c is "install" or "uninstall");   // seeing it never changes it
    }

    // ------------------------------------------------------------------ status, metrics, rates of polling

    [Fact]
    public async Task While_nobody_looks_wg_is_not_asked_and_when_the_page_is_open_it_is()
    {
        var rig = new Rig().Running().Start();
        await rig.Module.PollOnceAsync();
        Assert.Empty(rig.W.Tools.Shows);
        Assert.Empty(rig.Host.MetricsBus.Published);

        rig.Host.PageVisibleValue.Value = true;
        await rig.Module.PollOnceAsync();
        Assert.Equal(new[] { WgField.LatestHandshakes, WgField.Transfer, WgField.Endpoints }, rig.W.Tools.Shows.Select(s => s.Field).ToArray());
        Assert.Equal("On, connected (handshake 1 s ago)", rig.Row<ValueRow>("row.status.header").Value.Value);
        Assert.Equal(Rig.Endpoint, rig.Row<ValueRow>("row.endpoint.header").Value.Value);
        Assert.Equal("Received 1000 B, sent 500 B", rig.Row<ValueRow>("row.transfer.header").Value.Value);
    }

    [Fact]
    public async Task A_reader_of_the_metrics_is_enough_to_poll_without_the_page()
    {
        var rig = new Rig().Running().Start();
        rig.Host.MetricsBus.Subscribed.Add("tunnel.example-tunnel.rx_rate");   // the recorder, say
        await rig.Module.PollOnceAsync();
        Assert.NotEmpty(rig.W.Tools.Shows);
        Assert.True(rig.Host.MetricsBus.Has("tunnel.example-tunnel.handshake_age"));
    }

    [Fact]
    public async Task Metrics_are_described_published_and_withdrawn_when_the_tunnel_goes_off()
    {
        var rig = new Rig().Running().Start();
        rig.Host.PageVisibleValue.Value = true;

        await rig.Module.PollOnceAsync();
        rig.W.Clock.Now = rig.W.Clock.Now.AddSeconds(2);
        await rig.Module.PollOnceAsync();

        var bus = rig.Host.MetricsBus;
        Assert.Equal(new[] { "tunnel.example-tunnel.rx_rate", "tunnel.example-tunnel.tx_rate", "tunnel.example-tunnel.handshake_age" },
            bus.Descriptors.Select(d => d.Id.Value).ToArray());
        Assert.Equal(MetricUnit.BytesPerSecond, bus.Descriptors[0].Unit);
        Assert.Equal(MetricUnit.Seconds, bus.Descriptors[2].Unit);
        Assert.Equal("example-tunnel: received", rig.Host.Loc.T(bus.Descriptors[0].LabelKey));
        Assert.True(bus.Has("tunnel.example-tunnel.rx_rate"));
        Assert.Equal(MetricUnit.BytesPerSecond, bus.Latest["tunnel.example-tunnel.rx_rate"].Unit);
        Assert.InRange(bus.Latest["tunnel.example-tunnel.handshake_age"].Value, 0, 5);

        rig.W.Services.Remove(Rig.Name);   // turned off elsewhere
        await rig.Module.PollOnceAsync();
        Assert.False(bus.Has("tunnel.example-tunnel.rx_rate"));
        Assert.False(bus.Has("tunnel.example-tunnel.handshake_age"));
        Assert.Equal("Off", rig.Row<ValueRow>("row.status.header").Value.Value);
    }

    [Fact]
    public async Task A_reading_that_fails_withdraws_the_metrics_instead_of_leaving_the_last_value()
    {
        var rig = new Rig().Running().Start();
        rig.Host.PageVisibleValue.Value = true;
        await rig.Module.PollOnceAsync();
        Assert.True(rig.Host.MetricsBus.Has("tunnel.example-tunnel.handshake_age"));

        rig.W.Tools.ShowOutput = (_, _) => null;
        await rig.Module.PollOnceAsync();
        Assert.False(rig.Host.MetricsBus.Has("tunnel.example-tunnel.handshake_age"));
    }

    [Fact]
    public async Task A_service_check_that_throws_is_survived_by_the_poll()
    {
        var rig = new Rig().Running().Start();
        rig.W.Services.QueryThrows = new InvalidOperationException("boom");
        await rig.Module.PollOnceAsync();   // no exception out of the poll
        Assert.Contains("tunnel list failed", rig.Host.LogSink.All());
    }

    // ------------------------------------------------------------------ the options

    [Fact]
    public async Task Auto_off_turns_the_tunnel_off_when_the_time_is_up_without_asking_again_and_says_so()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;
        rig.Row<NumberRow>("row.autooff.header").Value.Value = 2;   // the host asked when the option was set
        rig.Host.TrayService.Item("toggle-example-tunnel").Invoke();
        await rig.Idle();
        int asked = rig.Host.ApprovalService.Count;
        Assert.True(rig.W.Services.Of(Rig.Name).IsRunning);

        rig.W.Clock.Now = rig.W.Clock.Now.AddMinutes(119);
        await rig.Module.PollOnceAsync();
        Assert.True(rig.W.Services.Of(Rig.Name).IsRunning);

        rig.W.Clock.Now = rig.W.Clock.Now.AddMinutes(2);
        await rig.Module.PollOnceAsync();
        await rig.Idle();

        Assert.False(rig.W.Services.Of(Rig.Name).Exists);
        Assert.Equal(asked, rig.Host.ApprovalService.Count);
        Assert.Equal("Tunnel example-tunnel was turned off automatically after 2 hours.", rig.Toasts().Last());
    }

    [Fact]
    public async Task Auto_off_is_off_by_default_and_never_acts_on_a_tunnel_it_was_not_asked_about()
    {
        var rig = new Rig().Running().Start();
        rig.W.Clock.Now = rig.W.Clock.Now.AddDays(30);
        await rig.Module.PollOnceAsync();
        Assert.True(rig.W.Services.Of(Rig.Name).IsRunning);
        Assert.Equal(0, rig.Row<NumberRow>("row.autooff.header").Value.Value);
        Assert.DoesNotContain(rig.W.Tools.Calls, c => c == "uninstall");
    }

    [Fact]
    public async Task Auto_off_counts_from_the_moment_it_is_set_for_a_tunnel_that_is_already_on()
    {
        var rig = new Rig().Running().Start();
        rig.Row<NumberRow>("row.autooff.header").Value.Value = 1;
        rig.W.Clock.Now = rig.W.Clock.Now.AddMinutes(61);
        await rig.Module.PollOnceAsync();
        await rig.Idle();
        Assert.False(rig.W.Services.Of(Rig.Name).Exists);
    }

    [Fact]
    public void The_hours_are_kept_within_zero_and_a_week()
    {
        var rig = new Rig().Start();
        var row = rig.Row<NumberRow>("row.autooff.header");
        Assert.Equal(0, row.Minimum);
        Assert.Equal(168, row.Maximum);
        row.Value.Value = 500;
        Assert.Equal(168, row.Value.Value);
        row.Value.Value = -3;
        Assert.Equal(0, row.Value.Value);
        Assert.NotNull(row.Validate!(1.5));
        Assert.Null(row.Validate(2));
        Assert.Equal(EffectKind.SystemSetting, row.HardwareEffect!.Kind);   // setting it is asked about: it lets Rigbay turn the tunnel off by itself
    }

    [Fact]
    public async Task Start_with_Windows_is_the_start_type_of_the_service_and_follows_it()
    {
        var rig = new Rig().Running(start: StartKind.Automatic).Start();
        var row = rig.Row<ToggleRow>("row.start.header");
        Assert.True(row.Value.Value);
        Assert.True(row.Enabled.Value);
        Assert.Equal(EffectKind.SystemSetting, row.HardwareEffect!.Kind);

        row.Value.Value = false;   // the host asked
        await rig.Idle();
        Assert.Equal(("WireGuardTunnel$example-tunnel", StartKind.Manual), rig.W.Services.StartChanges.Single());
        Assert.False(row.Value.Value);

        rig.W.Services.SetStartResult = 5;   // access denied
        row.Value.Value = true;
        await rig.Idle();
        Assert.False(row.Value.Value);   // put back to what Windows says
        Assert.Equal(ToastKind.Error, rig.Host.ToastService.All().Last().Kind);
    }

    [Fact]
    public void Start_with_Windows_cannot_be_changed_while_the_tunnel_is_off()
    {
        var rig = new Rig().Start();
        Assert.False(rig.Row<ToggleRow>("row.start.header").Enabled.Value);
    }

    [Fact]
    public async Task The_local_service_check_reports_listening_on_the_tunnel_address()
    {
        var rig = new Rig().Running().Start();
        rig.Host.PageVisibleValue.Value = true;
        rig.W.Network.Adapters[Rig.Name] = new[] { System.Net.IPAddress.Parse("10.9.0.2") };
        rig.Row<NumberRow>("row.port.header").Value.Value = 3389;
        await rig.Idle();
        Assert.Equal("Port 3389: nothing is listening", rig.Row<ValueRow>("row.local.header").Value.Value);

        rig.W.Network.Listening.Add(3389);
        await rig.Module.PollOnceAsync();
        Assert.Equal("Port 3389: something is listening", rig.Row<ValueRow>("row.local.header").Value.Value);

        rig.W.Services.Remove(Rig.Name);
        await rig.Module.PollOnceAsync();
        Assert.Equal("The tunnel is off", rig.Row<ValueRow>("row.local.header").Value.Value);
    }

    [Fact]
    public void The_public_address_is_stored_in_the_settings_and_shown_as_a_link_only_when_it_is_a_web_address()
    {
        var rig = new Rig().Start();
        var row = rig.Row<TextRow>("row.public.header");
        row.Value.Value = "  " + Rig.PublicAddress + " ";
        Assert.Equal(Rig.PublicAddress, row.Value.Value);   // trimmed
        Assert.Equal(1, rig.Host.PageRefreshRequests);
        Assert.Contains(rig.Page().Links, l => l.Url == Rig.PublicAddress);

        row.Value.Value = "remote.example.test:3389";   // free text is fine, but it is not a link
        Assert.DoesNotContain(rig.Page().Links, l => l.Url.Contains("example.test"));
        var saved = (TunnelSettings)rig.Host.SettingsStore.Stored!;
        Assert.Equal("remote.example.test:3389", saved.Tunnels["example-tunnel"].PublicAddress);
        Assert.Equal(200, row.MaxLength);
    }

    [Fact]
    public async Task Settings_come_back_after_a_restart()
    {
        var rig = new Rig().Start();
        rig.Row<NumberRow>("row.port.header").Value.Value = 8080;
        rig.Row<TextRow>("row.public.header").Value.Value = Rig.PublicAddress;
        await rig.Idle();
        rig.Module.Shutdown();

        var again = new TunnelModule(rig.W.Dependencies);
        var host2 = new FakeHost { };
        host2.SettingsStore.Stored = rig.Host.SettingsStore.Stored;
        again.Initialize(host2);
        var page = again.GetPageModel(host2)!;
        var expander = (ExpanderRow)page.Groups.Single(g => g.HeaderKey == "group.tunnels").Rows.Single();
        Assert.Equal(8080, ((NumberRow)expander.Children.Single(c => c.HeaderKey == "row.port.header")).Value.Value);
        Assert.Equal(Rig.PublicAddress, ((TextRow)expander.Children.Single(c => c.HeaderKey == "row.public.header")).Value.Value);
    }

    // ------------------------------------------------------------------ verbs

    [Fact]
    public async Task The_list_and_status_verbs_read_and_never_change()
    {
        var rig = new Rig(names: new[] { "example-tunnel", "second" }).Running().Start();

        var list = await rig.Host.CommandRegistry.Run("list");
        Assert.Equal(0, list.ExitCode);
        Assert.Equal("example-tunnel: on\nsecond: off".Replace("\n", Environment.NewLine), list.Output);

        var status = await rig.Host.CommandRegistry.Run("status", "example-tunnel");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("example-tunnel: On, connected (handshake 1 s ago)", status.Output);
        Assert.Contains("server " + Rig.Endpoint, status.Output);
        Assert.Contains("received 1000 B, sent 500 B", status.Output);

        Assert.Equal(1, (await rig.Host.CommandRegistry.Run("status", "nope")).ExitCode);
        Assert.Equal(2, (await rig.Host.CommandRegistry.Run("status", "a", "b")).ExitCode);
        Assert.DoesNotContain(rig.W.Tools.Calls, c => c is "install" or "uninstall");
        Assert.Equal(0, rig.Host.ApprovalService.Count);
    }

    [Fact]
    public async Task The_on_verb_asks_then_waits_for_the_handshake_and_reports()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;

        var result = await rig.Host.CommandRegistry.Run("on", Rig.Name);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Tunnel example-tunnel is on and connected.", result.Output);
        Assert.Equal("Turn on tunnel example-tunnel", rig.Words(rig.Host.ApprovalService.Asked.Single().Effect));
        Assert.Single(rig.Host.ToastService.All());   // a verb also shows the toast (N1's rule: tray, hotkey, verb, auto-off)
    }

    [Fact]
    public async Task The_on_verb_without_a_window_to_ask_in_changes_nothing()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = false;   // what the host answers when it cannot ask
        var result = await rig.Host.CommandRegistry.Run("on", Rig.Name);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Nothing was changed", result.Output);
        Assert.DoesNotContain(rig.W.Tools.Calls, c => c is "install" or "uninstall");
    }

    [Fact]
    public async Task The_on_verb_says_when_the_server_does_not_answer()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;
        rig.W.Tools.ShowOutput = (_, f) => f == WgField.LatestHandshakes ? World.Handshakes(null) : null;
        var result = await rig.Host.CommandRegistry.Run("on", Rig.Name);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Tunnel example-tunnel is on, but the server is not answering. Check the internet connection or the server.", result.Output);
    }

    [Fact]
    public async Task The_off_verb_and_the_edge_cases()
    {
        var rig = new Rig().Running().Start();
        rig.Host.ApprovalService.Answer = true;

        Assert.Equal(2, (await rig.Host.CommandRegistry.Run("off")).ExitCode);
        Assert.Equal(1, (await rig.Host.CommandRegistry.Run("off", "nope")).ExitCode);
        Assert.Equal(1, (await rig.Host.CommandRegistry.Run("off", "bad name; calc")).ExitCode);
        Assert.Equal(0, rig.Host.ApprovalService.Count);   // nobody is asked about a tunnel that does not exist

        var off = await rig.Host.CommandRegistry.Run("off", Rig.Name);
        Assert.Equal((0, "Tunnel example-tunnel is off."), (off.ExitCode, off.Output));
        Assert.Equal("Tunnel example-tunnel is already off. Nothing was changed.", (await rig.Host.CommandRegistry.Run("off", Rig.Name)).Output);
        Assert.Equal(1, rig.Host.ApprovalService.Count);   // the second call did not ask
    }

    [Fact]
    public async Task Verbs_look_the_name_up_ignoring_case_and_use_the_stored_spelling()
    {
        var rig = new Rig().Running().Start();
        rig.Host.ApprovalService.Answer = true;
        await rig.Host.CommandRegistry.Run("off", "EXAMPLE-TUNNEL");
        Assert.Equal(new[] { Rig.Name }, rig.W.Tools.Uninstalled);
    }

    [Fact]
    public async Task A_change_that_is_already_running_is_refused_not_queued_and_nobody_is_asked()
    {
        var rig = new Rig(names: new[] { "example-tunnel", "second" }).Start();
        rig.Host.ApprovalService.Answer = true;
        Task<CommandResult>? inner = null;
        rig.W.Tools.BeforeInstall = () => inner = rig.Host.CommandRegistry.Run("on", "second");   // arrives while the first install runs

        var first = await rig.Host.CommandRegistry.Run("on", Rig.Name);
        var second = await inner!;

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(1, second.ExitCode);
        Assert.Contains("in progress", second.Output);
        Assert.Single(rig.W.Tools.Installed);
        Assert.Equal(1, rig.Host.ApprovalService.Count);
    }

    // ------------------------------------------------------------------ stop, language, privacy

    [Fact]
    public async Task Shutdown_leaves_the_tunnel_as_it_is_and_the_module_can_start_again()
    {
        var rig = new Rig().Running().Start();
        rig.Module.Shutdown();
        Assert.DoesNotContain(rig.W.Tools.Calls, c => c is "install" or "uninstall");
        Assert.True(rig.W.Services.Of(Rig.Name).IsRunning);

        rig.Host.TrayService.Items.Clear();       // the real host removes them at Shutdown
        rig.Host.HotkeyService.Registered.Clear();
        rig.Host.CommandRegistry.Verbs.Clear();
        rig.Module.Initialize(rig.Host);
        await rig.Module.PollOnceAsync();
        Assert.Single(rig.Host.TrayService.Items);
        Assert.True(rig.Host.TrayService.Items[0].Checked.Value);
        Assert.Equal(4, rig.Host.CommandRegistry.Verbs.Count);
    }

    [Fact]
    public async Task Changing_the_language_asks_for_a_new_page_and_the_texts_follow()
    {
        var rig = new Rig().Running().Start();
        await rig.Module.PollOnceAsync();
        int before = rig.Host.PageRefreshRequests;
        rig.Host.Loc.Switch("vi");
        await rig.Idle();
        Assert.True(rig.Host.PageRefreshRequests > before);
        Assert.Equal("Bật", rig.Row<ValueRow>("row.status.header").Value.Value);
    }

    [Fact]
    public async Task Nothing_of_the_owners_network_reaches_the_log_or_the_settings()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;
        rig.Host.PageVisibleValue.Value = true;
        rig.Row<TextRow>("row.public.header").Value.Value = Rig.PublicAddress;
        rig.Row<NumberRow>("row.port.header").Value.Value = 3389;
        rig.Host.TrayService.Item("toggle-example-tunnel").Invoke();
        await rig.Idle();
        await rig.Module.PollOnceAsync();
        await rig.Host.CommandRegistry.Run("status");
        rig.W.Tools.OnInstall = _ => new ToolRun(7);
        rig.W.Services.Remove(Rig.Name);
        rig.Host.TrayService.Item("toggle-example-tunnel").Invoke();
        await rig.Idle();

        var log = rig.Host.LogSink.All();
        foreach (var secret in new[] { Rig.Endpoint, "198.51.100.1", World.PeerKey, "remote.example.test", "10.9.0.2" })
            Assert.DoesNotContain(secret, log);
        Assert.NotEmpty(log);

        var settings = JsonSerializer.Serialize(rig.Host.SettingsStore.Stored);
        foreach (var secret in new[] { Rig.Endpoint, World.PeerKey, "PrivateKey", "10.9.0.2" })
            Assert.DoesNotContain(secret, settings);
    }
}
