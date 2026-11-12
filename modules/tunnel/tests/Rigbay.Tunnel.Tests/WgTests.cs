using System.Net;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel.Tests;

public class TunnelNameTests
{
    [Theory]
    [InlineData("example-tunnel")]
    [InlineData("a")]
    [InlineData("Home_1.x+y=z")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]   // 32 characters
    public void WireGuards_own_names_are_accepted(string name) => Assert.True(TunnelNames.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]  // 33 characters
    [InlineData("two words")]
    [InlineData("a;b")]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("\"quoted\"")]
    [InlineData("..\\evil")]
    [InlineData("C:\\x")]
    [InlineData("a/b")]
    [InlineData("name\n")]
    [InlineData("-/installtunnelservice")]
    [InlineData("tên-việt")]
    public void Anything_else_never_reaches_a_command_line(string? name) => Assert.False(TunnelNames.IsValid(name));

    [Fact]
    public void The_service_name_is_the_one_wireguard_uses() => Assert.Equal("WireGuardTunnel$example-tunnel", TunnelNames.ServiceName("example-tunnel"));

    [Theory]
    [InlineData("example-tunnel")]
    [InlineData("Home_1.x+y=z")]
    [InlineData("A")]
    public void A_slug_is_a_valid_tray_hotkey_and_metric_id(string name)
    {
        var slug = TunnelNames.Slug(name);
        Assert.Matches("^[a-z0-9-]+$", slug);
        Assert.True(Rigbay.Abstractions.MetricId.IsValid("tunnel." + slug + ".handshake_age"));
    }

    [Fact]
    public void Names_that_differ_in_more_than_case_get_different_slugs()
    {
        Assert.NotEqual(TunnelNames.Slug("a.b"), TunnelNames.Slug("a-b"));
        Assert.NotEqual(TunnelNames.Slug("a_b"), TunnelNames.Slug("a.b"));
        Assert.Equal("example-tunnel", TunnelNames.Slug("example-tunnel"));
        Assert.Equal(TunnelNames.Slug("Home"), TunnelNames.Slug("home"));   // Windows treats them as one name
    }
}

public class WgParserTests
{
    [Fact]
    public void The_newest_handshake_of_any_peer_wins_and_zero_means_never()
    {
        var text = World.PeerKey + "\t1000\n" + World.PeerKey + "\t2000\n" + World.PeerKey + "\t0\n";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000), WgParsers.LatestHandshake(text));
        Assert.Null(WgParsers.LatestHandshake(World.PeerKey + "\t0\n"));
        Assert.Null(WgParsers.LatestHandshake(""));
        Assert.Null(WgParsers.LatestHandshake(null));
    }

    [Fact]
    public void Transfer_is_summed_over_the_peers_and_garbage_is_skipped()
    {
        var text = World.PeerKey + "\t100\t10\n" + "garbage line\n" + World.PeerKey + "\t5\t7\n" + World.PeerKey + "\tx\t1\n";
        Assert.Equal((105L, 17L), WgParsers.Transfer(text));
        Assert.Null(WgParsers.Transfer("nothing useful"));
    }

    [Fact]
    public void An_endpoint_is_the_first_real_one()
    {
        Assert.Equal("198.51.100.1:51820", WgParsers.Endpoint(World.PeerKey + "\t(none)\n" + World.PeerKey + "\t198.51.100.1:51820\n"));
        Assert.Null(WgParsers.Endpoint(World.PeerKey + "\t(none)\n"));
    }

    [Fact]
    public void Windows_line_endings_are_fine()
        => Assert.Equal((1L, 2L), WgParsers.Transfer(World.PeerKey + "\t1\t2\r\n"));
}

internal sealed class FakeRunner : IProcessRunner
{
    public readonly List<(string Exe, string[] Args, bool KeepOutput)> Runs = new();
    public Func<string, string[], ProcessResult>? Answer;

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, bool keepOutput, CancellationToken ct)
    {
        var a = args.ToArray();
        Runs.Add((exe, a, keepOutput));
        return Task.FromResult(Answer?.Invoke(exe, a) ?? new ProcessResult(0, "", false, false));
    }
}

internal sealed class FakeSignature : ISignatureCheck
{
    public readonly HashSet<string> Unsigned = new(StringComparer.OrdinalIgnoreCase);
    public int Checks;
    public bool IsSignedByWireGuard(string path) { Checks++; return !Unsigned.Contains(Path.GetFileName(path)); }
}

public sealed class WireGuardToolsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rigbay-tunnel-tests-" + Guid.NewGuid().ToString("N")[..10]);

    public WireGuardToolsTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private void Install(bool wireguard = true, bool wg = true)
    {
        if (wireguard) File.WriteAllText(Path.Combine(_folder, "wireguard.exe"), "x");
        if (wg) File.WriteAllText(Path.Combine(_folder, "wg.exe"), "x");
    }

    [Fact]
    public async Task Missing_programs_make_the_module_unsupported_with_the_download_hint()
    {
        var tools = new WireGuardTools(_folder, new FakeRunner(), new FakeSignature());
        Assert.Equal(new ToolProbe(false, "probe.no-wireguard"), tools.Probe());
        Install(wireguard: true, wg: false);
        Assert.Equal("probe.no-wireguard", tools.Probe().ReasonKey);
        Assert.Contains("https://www.wireguard.com/install/", FakeLocalisation.Load("en")["module.tunnel.probe.no-wireguard"]);
    }

    [Theory]
    [InlineData("wireguard.exe")]
    [InlineData("wg.exe")]
    public async Task A_program_not_signed_by_WireGuard_LLC_is_never_used(string unsignedFile)
    {
        Install();
        var signature = new FakeSignature();
        signature.Unsigned.Add(unsignedFile);
        var runner = new FakeRunner();
        var tools = new WireGuardTools(_folder, runner, signature);
        Assert.Equal(new ToolProbe(false, "probe.not-signed"), tools.Probe());

        // even if the probe was skipped, nothing is run
        var install = (await tools.InstallTunnelServiceAsync(@"C:\Store\example-tunnel.conf.dpapi", default));
        var uninstall = (await tools.UninstallTunnelServiceAsync("example-tunnel", default));
        var show = (await tools.ShowAsync("example-tunnel", WgField.Transfer, default));
        if (unsignedFile == "wireguard.exe") { Assert.False(install.Ok); Assert.False(uninstall.Ok); }
        else Assert.Null(show);
        Assert.DoesNotContain(runner.Runs, r => Path.GetFileName(r.Exe) == unsignedFile);
    }

    [Fact]
    public async Task Both_signed_is_usable_and_the_signature_is_not_rechecked_for_an_unchanged_file()
    {
        Install();
        var signature = new FakeSignature();
        var tools = new WireGuardTools(_folder, new FakeRunner(), signature);
        Assert.True(tools.Probe().Usable);
        int after = signature.Checks;
        Assert.True(tools.Probe().Usable);
        _ = (await tools.ShowAsync("example-tunnel", WgField.Transfer, default));
        Assert.Equal(after, signature.Checks);   // cached by size and time

        File.WriteAllText(Path.Combine(_folder, "wg.exe"), "changed contents");   // swapped program: checked again
        _ = (await tools.ShowAsync("example-tunnel", WgField.Transfer, default));
        Assert.True(signature.Checks > after);
    }

    [Fact]
    public async Task The_arguments_are_exactly_the_ones_the_official_app_uses()
    {
        Install();
        var runner = new FakeRunner();
        var tools = new WireGuardTools(_folder, runner, new FakeSignature());
        _ = (await tools.InstallTunnelServiceAsync(@"C:\Store\example-tunnel.conf.dpapi", default));
        _ = (await tools.UninstallTunnelServiceAsync("example-tunnel", default));

        Assert.Equal(2, runner.Runs.Count);
        Assert.Equal(new[] { "/installtunnelservice", @"C:\Store\example-tunnel.conf.dpapi" }, runner.Runs[0].Args);
        Assert.Equal(new[] { "/uninstalltunnelservice", "example-tunnel" }, runner.Runs[1].Args);
        Assert.All(runner.Runs, r => Assert.False(r.KeepOutput));   // the output of these can quote a configuration: never kept
        Assert.All(runner.Runs, r => Assert.Equal(Path.Combine(_folder, "wireguard.exe"), r.Exe));
    }

    [Fact]
    public async Task Show_has_exactly_three_forms_and_each_names_a_field()
    {
        Assert.Equal(3, Enum.GetValues<WgField>().Length);
        Assert.Equal(new[] { "show", "example-tunnel", "latest-handshakes" }, WireGuardTools.ShowArguments("example-tunnel", WgField.LatestHandshakes));
        Assert.Equal(new[] { "show", "example-tunnel", "transfer" }, WireGuardTools.ShowArguments("example-tunnel", WgField.Transfer));
        Assert.Equal(new[] { "show", "example-tunnel", "endpoints" }, WireGuardTools.ShowArguments("example-tunnel", WgField.Endpoints));
        Assert.Throws<ArgumentOutOfRangeException>(() => WireGuardTools.ShowArguments("example-tunnel", (WgField)99));
    }

    [Fact]
    public async Task Show_runs_wg_with_a_field_keeps_the_output_only_for_it_and_never_runs_for_a_bad_name()
    {
        Install();
        var runner = new FakeRunner { Answer = (_, _) => new ProcessResult(0, "text", false, false) };
        var tools = new WireGuardTools(_folder, runner, new FakeSignature());
        Assert.Equal("text", (await tools.ShowAsync("example-tunnel", WgField.Endpoints, default)));
        Assert.Equal(Path.Combine(_folder, "wg.exe"), runner.Runs.Single().Exe);
        Assert.True(runner.Runs.Single().KeepOutput);

        Assert.Null((await tools.ShowAsync("a b;c", WgField.Endpoints, default)));
        Assert.False((await tools.UninstallTunnelServiceAsync("a b;c", default)).Ok);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task A_failed_or_timed_out_command_gives_no_text_and_no_success()
    {
        Install();
        var runner = new FakeRunner { Answer = (_, _) => new ProcessResult(1, "text", false, false) };
        var tools = new WireGuardTools(_folder, runner, new FakeSignature());
        Assert.Null((await tools.ShowAsync("example-tunnel", WgField.Transfer, default)));
        Assert.False((await tools.InstallTunnelServiceAsync(@"C:\Store\example-tunnel.conf", default)).Ok);
        runner.Answer = (_, _) => new ProcessResult(-1, "", true, false);
        Assert.True((await tools.InstallTunnelServiceAsync(@"C:\Store\example-tunnel.conf", default)).TimedOut);
    }

    [Fact]
    public async Task A_relative_config_path_is_refused()
    {
        Install();
        var runner = new FakeRunner();
        var tools = new WireGuardTools(_folder, runner, new FakeSignature());
        Assert.False((await tools.InstallTunnelServiceAsync("example-tunnel.conf", default)).Ok);
        Assert.Empty(runner.Runs);
    }
}

public sealed class TunnelStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rigbay-tunnel-store-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly List<FileStream> _locks = new();

    public TunnelStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        foreach (var l in _locks) l.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string Make(string file, bool lockIt = false)
    {
        var path = Path.Combine(_folder, file);
        File.WriteAllText(path, "placeholder, not a configuration");
        if (lockIt) _locks.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));   // nothing else can open it
        return path;
    }

    [Fact]
    public void Names_come_from_both_kinds_of_file_and_are_listed_once()
    {
        Make("beta.conf.dpapi");
        Make("alpha.conf");
        Make("both.conf");
        Make("both.conf.dpapi");
        Make("notes.txt");
        Make("bad name.conf");
        Make("toolong-toolong-toolong-toolong-toolong.conf");
        var list = new TunnelStore(_folder).List();

        Assert.True(list.Readable);
        Assert.Equal(new[] { "alpha", "beta", "both" }, list.Tunnels.Select(t => t.Name).ToArray());
        Assert.EndsWith("alpha.conf", list.Tunnels[0].Path);              // the file that exists is what is passed on
        Assert.EndsWith("beta.conf.dpapi", list.Tunnels[1].Path);
        Assert.EndsWith("both.conf.dpapi", list.Tunnels[2].Path);         // both present: the encrypted one
    }

    [Fact]
    public void The_content_of_a_configuration_is_never_opened()
    {
        // The files are opened exclusively by the test: a listing that tried to read one would throw. It must not.
        Make("locked-one.conf", lockIt: true);
        Make("locked-two.conf.dpapi", lockIt: true);
        var list = new TunnelStore(_folder).List();
        Assert.Equal(new[] { "locked-one", "locked-two" }, list.Tunnels.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_is_reported_not_thrown()
    {
        var list = new TunnelStore(Path.Combine(_folder, "missing")).List();
        Assert.False(list.Readable);
        Assert.Empty(list.Tunnels);
    }
}

public class NetworkProbeTests
{
    [Fact]
    public void Real_adapter_lookup_and_listener_check_do_not_throw()
    {
        var net = new NetworkProbe();
        Assert.Empty(net.AddressesOf("no-such-adapter-example"));
        Assert.False(net.IsListening(new[] { IPAddress.Parse("10.9.0.2") }, 1));   // port 1 is unused on any normal machine
    }
}
