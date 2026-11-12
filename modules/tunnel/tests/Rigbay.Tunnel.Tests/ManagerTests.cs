using System.Net;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel.Tests;

public class ManagerTests
{
    private const string Name = "example-tunnel";

    private static World WorldWith(params string[] names)
    {
        var w = new World();
        foreach (var n in names) w.Store.Add(n);
        return w;
    }

    [Fact]
    public async Task Turning_on_installs_the_existing_store_file_and_reads_the_service_back()
    {
        var w = WorldWith(Name);
        var manager = w.Manager();

        var result = await manager.ChangeAsync(Name, true, CancellationToken.None);

        Assert.Equal(ChangeStatus.Done, result.Status);
        Assert.Equal(new[] { @"C:\Store\example-tunnel.conf.dpapi" }, w.Tools.Installed);
        Assert.Empty(w.Tools.Uninstalled);
        Assert.True(w.Services.Of(Name).IsRunning);
        Assert.True(manager.Find(Name)!.IsOn);
    }

    [Fact]
    public async Task A_plain_conf_in_the_store_is_passed_as_it_is()
    {
        var w = new World();
        w.Store.Add(Name, ".conf");
        var result = await w.Manager().ChangeAsync(Name, true, CancellationToken.None);
        Assert.Equal(ChangeStatus.Done, result.Status);
        Assert.Equal(new[] { @"C:\Store\example-tunnel.conf" }, w.Tools.Installed);
    }

    [Fact]
    public async Task Turning_off_uninstalls_by_name_and_waits_until_the_service_is_gone()
    {
        var w = WorldWith(Name);
        w.Services.Set(Name, ServiceState.Running);
        var result = await w.Manager().ChangeAsync(Name, false, CancellationToken.None);
        Assert.Equal(ChangeStatus.Done, result.Status);
        Assert.Equal(new[] { Name }, w.Tools.Uninstalled);
        Assert.False(w.Services.Of(Name).Exists);
    }

    [Fact]
    public async Task Already_there_changes_nothing()
    {
        var w = WorldWith(Name);
        Assert.Equal(ChangeStatus.AlreadyThere, (await w.Manager().ChangeAsync(Name, false, CancellationToken.None)).Status);   // no service: already off
        w.Services.Set(Name, ServiceState.Running);
        Assert.Equal(ChangeStatus.AlreadyThere, (await w.Manager().ChangeAsync(Name, true, CancellationToken.None)).Status);    // running: already on
        Assert.Empty(w.Tools.Calls);
    }

    [Fact]
    public async Task A_stale_stopped_service_is_removed_before_the_install()
    {
        var w = WorldWith(Name);
        w.Services.Set(Name, ServiceState.Stopped, win32: 1);
        var result = await w.Manager().ChangeAsync(Name, true, CancellationToken.None);
        Assert.Equal(ChangeStatus.Done, result.Status);
        Assert.Equal(new[] { "uninstall", "install" }, w.Tools.Calls);
    }

    [Fact]
    public async Task An_unknown_name_is_refused_and_nothing_is_run()
    {
        var w = WorldWith(Name);
        var manager = w.Manager();
        Assert.Equal(ChangeStatus.UnknownTunnel, (await manager.ChangeAsync("other", true, CancellationToken.None)).Status);
        Assert.Equal(ChangeStatus.UnknownTunnel, (await manager.ChangeAsync("bad name; calc", true, CancellationToken.None)).Status);
        Assert.Equal(ChangeStatus.UnknownTunnel, (await manager.ChangeAsync("bad name; calc", false, CancellationToken.None)).Status);
        Assert.Empty(w.Tools.Calls);
    }

    [Fact]
    public async Task A_missing_configuration_says_so_in_plain_words()
    {
        var w = new World();   // empty store
        var result = await w.Manager().ChangeAsync(Name, true, CancellationToken.None);
        Assert.Equal(ChangeStatus.UnknownTunnel, result.Status);
        Assert.Equal("err.config-missing", result.Problem!.Key);
        Assert.Empty(w.Tools.Calls);
    }

    [Fact]
    public async Task WireGuard_missing_or_not_genuine_runs_nothing()
    {
        var w = WorldWith(Name);
        w.Tools.ProbeResult = new ToolProbe(false, "probe.not-signed");
        var result = await w.Manager().ChangeAsync(Name, true, CancellationToken.None);
        Assert.Equal(ChangeStatus.ToolsUnusable, result.Status);
        Assert.Equal("probe.not-signed", result.Problem!.Key);
        Assert.Empty(w.Tools.Calls);
    }

    [Fact]
    public async Task A_service_that_fails_to_start_reports_its_last_error_and_is_cleaned_up()
    {
        var w = WorldWith(Name);
        w.Tools.OnInstall = name => { w.Services.Set(name, ServiceState.Stopped, win32: 1053); return new ToolRun(1); };   // 1053: did not respond in time
        var result = await w.Manager().ChangeAsync(Name, true, CancellationToken.None);

        Assert.Equal(ChangeStatus.Failed, result.Status);
        Assert.Equal("err.service-failed", result.Problem!.Key);
        Assert.Contains("1053", (string)result.Problem.Args[1]);
        Assert.Equal(new[] { Name }, w.Tools.Uninstalled);   // no half-installed service left behind
        Assert.False(w.Services.Of(Name).Exists);
        Assert.Contains("Log tab", FakeLocalisation.Load("en")["module.tunnel.err.service-failed"]);   // WireGuard's own log is where to look
    }

    [Fact]
    public async Task An_install_that_fails_without_a_service_gives_its_exit_code()
    {
        var w = WorldWith(Name);
        w.Tools.OnInstall = _ => new ToolRun(5);
        var result = await w.Manager().ChangeAsync(Name, true, CancellationToken.None);
        Assert.Equal(ChangeStatus.Failed, result.Status);
        Assert.Equal("err.install-failed", result.Problem!.Key);
        Assert.Equal(5, result.Problem.Args[1]);
    }

    [Fact]
    public async Task A_command_that_could_not_start_or_timed_out_is_a_failure_with_its_own_text()
    {
        var w = WorldWith(Name);
        w.Tools.OnInstall = _ => new ToolRun(-1, StartFailed: true);
        Assert.Equal("err.install-not-run", (await w.Manager().ChangeAsync(Name, true, CancellationToken.None)).Problem!.Key);
        w.Tools.OnInstall = _ => new ToolRun(-1, TimedOut: true);
        Assert.Equal("err.timeout", (await w.Manager().ChangeAsync(Name, true, CancellationToken.None)).Problem!.Key);
    }

    [Fact]
    public async Task A_service_that_does_not_go_away_is_a_failed_turn_off()
    {
        var w = WorldWith(Name);
        w.Services.Set(Name, ServiceState.Running);
        w.Tools.OnUninstall = _ => new ToolRun(1);   // the service stays
        var result = await w.Manager().ChangeAsync(Name, false, CancellationToken.None);
        Assert.Equal(ChangeStatus.Failed, result.Status);
        Assert.Equal("err.uninstall-failed", result.Problem!.Key);
    }

    [Fact]
    public async Task Only_one_change_runs_at_a_time()
    {
        var w = WorldWith(Name, "second");
        var manager = w.Manager();
        ChangeResult? inner = null;
        w.Tools.BeforeInstall = () => inner = manager.ChangeAsync("second", true, CancellationToken.None).GetAwaiter().GetResult();   // while the first install runs
        var first = await manager.ChangeAsync(Name, true, CancellationToken.None);

        Assert.Equal(ChangeStatus.Done, first.Status);
        Assert.Equal(ChangeStatus.Busy, inner!.Status);
        Assert.Single(w.Tools.Installed);
        Assert.False(manager.Busy);   // the slot is given back
    }

    [Fact]
    public async Task The_slot_is_given_back_when_the_work_throws()
    {
        var w = WorldWith(Name);
        w.Services.QueryThrows = new InvalidOperationException("boom");
        var manager = w.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ChangeAsync(Name, true, CancellationToken.None));
        Assert.False(manager.Busy);
    }

    // ------------------------------------------------------------------ handshake

    [Fact]
    public async Task A_handshake_that_arrives_within_the_wait_is_connected()
    {
        var w = WorldWith(Name);
        w.Services.Set(Name, ServiceState.Running);
        w.Tools.ShowOutput = (_, f) => f == WgField.LatestHandshakes
            ? World.Handshakes(w.Clock.Delays >= 5 ? w.Clock.Now.AddSeconds(-2) : null)   // the fifth wait brings the first handshake
            : null;

        var outcome = await w.Manager().WaitForHandshakeAsync(Name, TunnelManager.HandshakeWait, CancellationToken.None);

        Assert.Equal(HandshakeOutcome.Connected, outcome);
        Assert.Equal(5, w.Clock.Delays);
    }

    [Fact]
    public async Task No_handshake_in_thirty_seconds_is_reported_as_such()
    {
        var w = WorldWith(Name);
        w.Services.Set(Name, ServiceState.Running);
        w.Tools.ShowOutput = (_, f) => f == WgField.LatestHandshakes ? World.Handshakes(null) : null;

        var outcome = await w.Manager().WaitForHandshakeAsync(Name, TunnelManager.HandshakeWait, CancellationToken.None);

        Assert.Equal(HandshakeOutcome.NoHandshake, outcome);
        Assert.Equal(30, w.Clock.Delays);   // one look a second for 30 s, no real waiting
        Assert.Equal(TimeSpan.FromSeconds(30), TunnelManager.HandshakeWait);
    }

    [Fact]
    public async Task An_old_handshake_does_not_count_as_connected()
    {
        var w = WorldWith(Name);
        w.Services.Set(Name, ServiceState.Running);
        w.Tools.ShowOutput = (_, _) => World.Handshakes(w.Clock.Now.AddMinutes(-10));
        Assert.Equal(HandshakeOutcome.NoHandshake, await w.Manager().WaitForHandshakeAsync(Name, TimeSpan.FromSeconds(3), CancellationToken.None));
        w.Tools.ShowOutput = (_, _) => World.Handshakes(w.Clock.Now.AddMinutes(-2.9));
        Assert.Equal(HandshakeOutcome.Connected, await w.Manager().WaitForHandshakeAsync(Name, TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    [Fact]
    public async Task A_tunnel_turned_off_meanwhile_and_an_unreadable_wg_are_told_apart()
    {
        var w = WorldWith(Name);
        w.Services.Set(Name, ServiceState.Running);
        w.Tools.ShowOutput = (_, _) => World.Handshakes(null);
        w.Clock.OnDelay = n => { if (n == 3) w.Services.Remove(Name); };
        Assert.Equal(HandshakeOutcome.TurnedOff, await w.Manager().WaitForHandshakeAsync(Name, TunnelManager.HandshakeWait, CancellationToken.None));

        w.Services.Set(Name, ServiceState.Running);
        w.Clock.OnDelay = null;
        w.Tools.ShowOutput = (_, _) => null;   // wg.exe fails every time
        Assert.Equal(HandshakeOutcome.Unreadable, await w.Manager().WaitForHandshakeAsync(Name, TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    // ------------------------------------------------------------------ reading, port check, start type

    [Fact]
    public async Task A_reading_uses_only_the_three_fields()
    {
        var w = WorldWith(Name);
        w.Tools.ShowOutput = (_, f) => f switch
        {
            WgField.LatestHandshakes => World.Handshakes(w.Clock.Now.AddSeconds(-5)),
            WgField.Transfer => World.Transfer(1000, 200),
            _ => World.Endpoint("198.51.100.1:51820"),
        };
        var reading = await w.Manager().ReadAsync(Name, CancellationToken.None);

        Assert.Equal(w.Clock.Now.AddSeconds(-5).ToUnixTimeSeconds(), reading!.LatestHandshake!.Value.ToUnixTimeSeconds());
        Assert.Equal(1000, reading.RxBytes);
        Assert.Equal(200, reading.TxBytes);
        Assert.Equal("198.51.100.1:51820", reading.Endpoint);
        Assert.Equal(new[] { WgField.LatestHandshakes, WgField.Transfer, WgField.Endpoints }, w.Tools.Shows.Select(s => s.Field).ToArray());

        w.Tools.ShowOutput = (_, _) => null;
        Assert.Null(await w.Manager().ReadAsync(Name, CancellationToken.None));
    }

    [Fact]
    public void The_local_service_check_looks_at_the_tunnels_own_address()
    {
        var w = WorldWith(Name);
        var manager = w.Manager();
        Assert.Equal(LocalCheck.TunnelOff, manager.CheckPort(Name, 3389, tunnelOn: false));
        Assert.Equal(LocalCheck.NoAddress, manager.CheckPort(Name, 3389, tunnelOn: true));
        w.Network.Adapters[Name] = new[] { IPAddress.Parse("10.9.0.2") };
        Assert.Equal(LocalCheck.NotListening, manager.CheckPort(Name, 3389, tunnelOn: true));
        w.Network.Listening.Add(3389);
        Assert.Equal(LocalCheck.Listening, manager.CheckPort(Name, 3389, tunnelOn: true));
    }

    [Fact]
    public void The_start_type_is_set_on_the_tunnels_own_service_and_only_for_valid_names()
    {
        var w = WorldWith(Name);
        w.Services.Set(Name, ServiceState.Running, StartKind.Automatic);
        var manager = w.Manager();
        Assert.Equal(0, manager.SetStart(Name, automatic: false));
        Assert.Equal(("WireGuardTunnel$example-tunnel", StartKind.Manual), w.Services.StartChanges.Single());
        Assert.Equal(StartKind.Manual, w.Services.Of(Name).Start);
        Assert.NotEqual(0, manager.SetStart("bad name", automatic: true));
        Assert.Single(w.Services.StartChanges);
    }
}
