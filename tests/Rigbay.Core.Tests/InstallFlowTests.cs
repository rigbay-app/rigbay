using System.Net;
using Rigbay.Core.Install;
using Xunit;

namespace Rigbay.Core.Tests;

public class InstallerTests : IDisposable
{
    private readonly InstallEnv _env = new();
    private static readonly TestModule M = new();

    public void Dispose() => _env.Dispose();

    private async Task<InstallException> Fails(Task task, InstallErrorCode code)
    {
        var ex = await Assert.ThrowsAsync<InstallException>(() => task);
        Assert.Equal(code, ex.Code);
        AssertNothingLeftBehind();
        return ex;
    }

    /// <summary>After a refusal or failure: no pending change, no staged module, no half download, no new module folder.</summary>
    private void AssertNothingLeftBehind()
    {
        Assert.Empty(new PendingStore(_env.Paths, _env.Log).Read());
        Assert.Empty(Directory.Exists(_env.Paths.Ready) ? Directory.GetFileSystemEntries(_env.Paths.Ready) : Array.Empty<string>());
        Assert.Empty(Directory.Exists(_env.Paths.Downloads) ? Directory.GetFileSystemEntries(_env.Paths.Downloads) : Array.Empty<string>());
        Assert.Empty(Directory.Exists(_env.Paths.Staging) ? Directory.GetFileSystemEntries(_env.Paths.Staging) : Array.Empty<string>());
    }

    private Task Install(RegistryModule entry, int version = 0, CancellationToken ct = default) =>
        _env.NewInstaller().InstallAsync(entry, entry.Versions[version], null, ct);

    [Fact]
    public async Task A_good_release_is_verified_and_queued_and_the_modules_folder_is_not_touched()
    {
        var zip = M.Zip();
        _env.ServeZip(M, zip);
        var entry = _env.Entry(M, zip);

        var steps = new List<InstallPhase>();
        await _env.NewInstaller().InstallAsync(entry, entry.Versions[0], new SyncProgress(p => { if (steps.Count == 0 || steps[^1] != p.Phase) steps.Add(p.Phase); }), default);

        var op = Assert.Single(new PendingStore(_env.Paths, _env.Log).Read());
        Assert.Equal((PendingKinds.Install, "demo-tool", "1.0.0"), (op.Op, op.Id, op.Version));
        Assert.Equal(ZipKit.Sha256(zip), op.Sha256);
        Assert.Equal(FileOps.TreeHash(_env.Paths.ReadyFolder("demo-tool")), op.TreeHash);
        Assert.True(File.Exists(Path.Combine(_env.Paths.ReadyFolder("demo-tool"), "module.json")));
        Assert.Empty(Directory.GetFileSystemEntries(_env.ModulesRoot));
        Assert.Empty(Directory.GetFileSystemEntries(_env.Paths.Downloads));
        Assert.Empty(Directory.GetFileSystemEntries(_env.Paths.Staging));
        Assert.Equal(new[] { InstallPhase.Preparing, InstallPhase.Downloading, InstallPhase.Verifying, InstallPhase.Unpacking, InstallPhase.Checking, InstallPhase.Finishing }, steps);
        Assert.Equal(RegistryKit.ZipUrl(), Assert.Single(_env.Http.Requests).Uri.ToString());
    }

    [Fact]
    public async Task The_release_download_may_be_redirected_to_the_asset_hosts()
    {
        var zip = M.Zip();
        _env.Http.Respond = r => r.RequestUri!.Host switch
        {
            "github.com" => FakeHttp.Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/1?sig=abc"),
            "release-assets.githubusercontent.com" => FakeHttp.Bytes(zip),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        await Install(_env.Entry(M, zip));
        Assert.Equal(2, _env.Http.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_not_followed()
    {
        var zip = M.Zip();
        _env.Http.Respond = _ => FakeHttp.Redirect("https://evil.example.com/demo.zip");
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.UrlNotAllowed);
        Assert.Single(_env.Http.Requests);   // evil.example.com was never asked
    }

    [Fact]
    public async Task A_redirect_to_plain_http_is_not_followed()
    {
        var zip = M.Zip();
        _env.Http.Respond = _ => FakeHttp.Redirect("http://objects.githubusercontent.com/x");
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.UrlNotAllowed);
        Assert.Single(_env.Http.Requests);
    }

    [Fact]
    public async Task Three_redirects_are_followed_and_a_fourth_is_not()
    {
        var zip = M.Zip();
        int hops = 0;
        _env.Http.Respond = r => hops++ < 3 ? FakeHttp.Redirect($"https://objects.githubusercontent.com/hop{hops}") : FakeHttp.Bytes(zip);
        await Install(_env.Entry(M, zip));
        Assert.Equal(4, _env.Http.Requests.Count);

        Dispose();
        var env2 = new InstallEnv();
        try
        {
            env2.Http.Respond = _ => FakeHttp.Redirect("https://objects.githubusercontent.com/again");
            var entry = env2.Entry(M, zip);
            var ex = await Assert.ThrowsAsync<InstallException>(() => env2.NewInstaller().InstallAsync(entry, entry.Versions[0], null, default));
            Assert.Equal(InstallErrorCode.UrlNotAllowed, ex.Code);
            Assert.Equal(4, env2.Http.Requests.Count);
        }
        finally { env2.Dispose(); }
    }

    [Fact]
    public async Task A_wrong_hash_is_refused_before_anything_is_unpacked()
    {
        var zip = M.Zip();
        _env.ServeZip(M, zip);
        var entry = _env.Entry(M, zip, v => RegistryEdit.Set(v, "sha256", new string('0', 64)));
        var ex = await Fails(Install(entry), InstallErrorCode.HashMismatch);
        Assert.Contains("SHA-256", ex.Detail);
        Assert.False(Directory.Exists(Path.Combine(_env.Paths.Staging, "demo-tool")));
    }

    [Fact]
    public async Task One_flipped_byte_in_the_zip_is_a_hash_mismatch()
    {
        var zip = M.Zip();
        var entry = _env.Entry(M, zip);
        var tampered = (byte[])zip.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        _env.ServeZip(M, tampered);
        await Fails(Install(entry), InstallErrorCode.HashMismatch);
    }

    [Fact]
    public async Task A_server_that_announces_another_size_than_the_registry_is_refused()
    {
        var zip = M.Zip();
        _env.ServeZip(M, zip);
        var entry = _env.Entry(M, zip, v => RegistryEdit.Set(v, "size", zip.Length + 10));
        await Fails(Install(entry), InstallErrorCode.SizeMismatch);
    }

    [Fact]
    public async Task A_download_larger_than_the_cap_is_refused_from_its_header()
    {
        var zip = M.Zip();
        _env.Policy = new InstallPolicy { MaxZipBytes = zip.Length + 5 };
        _env.Http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent(new byte[10], zip.Length + 1000, stall: false) };
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.TooLarge);
    }

    [Fact]
    public async Task A_partial_download_is_incomplete_and_leaves_no_file()
    {
        var zip = M.Zip();
        _env.Http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent(zip[..(zip.Length / 2)], zip.Length, stall: false) };
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.Incomplete);
    }

    [Fact]
    public async Task A_download_that_stalls_ends_as_incomplete()
    {
        var zip = M.Zip();
        _env.Policy = new InstallPolicy { StallTimeout = TimeSpan.FromMilliseconds(300) };
        _env.Http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent(zip[..10], zip.Length, stall: true) };
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.Incomplete);
    }

    [Fact]
    public async Task Cancelling_during_the_download_changes_nothing()
    {
        var zip = M.Zip();
        _env.Http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent(zip[..10], zip.Length, stall: true) };
        using var cts = new CancellationTokenSource();
        var entry = _env.Entry(M, zip);
        var task = Install(entry, ct: cts.Token);
        await Task.Delay(300);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        AssertNothingLeftBehind();
        Assert.Empty(Directory.GetFileSystemEntries(_env.ModulesRoot));
    }

    [Fact]
    public async Task A_missing_file_is_a_failed_download_not_a_crash()
    {
        var zip = M.Zip();
        var ex = await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.DownloadFailed);
        Assert.Contains("404", ex.Detail);
    }

    [Fact]
    public async Task No_network_is_reported_as_offline()
    {
        var zip = M.Zip();
        _env.Http.Respond = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host");
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.Offline);
    }

    [Fact]
    public async Task A_zip_with_a_path_that_escapes_is_refused_even_when_its_hash_is_right()
    {
        var zip = M.Zip(z => ZipKit.Add(z, "../../evil.dll", "x"));
        _env.ServeZip(M, zip);
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.UnsafeZip);
        Assert.False(File.Exists(Path.Combine(_env.Root, "evil.dll")));
    }

    [Fact]
    public async Task A_zip_with_entries_that_collide_by_case_is_refused()
    {
        var zip = M.Zip(z => { ZipKit.Add(z, "Lib/a.dll", "1"); ZipKit.Add(z, "lib/A.DLL", "2"); });
        _env.ServeZip(M, zip);
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.UnsafeZip);
    }

    [Fact]
    public async Task A_zip_over_the_unpacked_limit_is_refused()
    {
        var zip = M.Zip(z => ZipKit.Add(z, "big.bin", new byte[2 * 1024 * 1024]));
        _env.Policy = new InstallPolicy { MaxUnpackedBytes = 1024 * 1024 };
        _env.ServeZip(M, zip);
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.TooLarge);
    }

    [Fact]
    public async Task A_manifest_with_another_id_than_the_registry_entry_is_refused()
    {
        var evil = M with { ManifestIdOverride = "something-else" };
        var zip = evil.Zip();
        _env.ServeZip(M, zip);
        var ex = await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.ManifestMismatch);
        Assert.Contains("something-else", ex.Detail);
    }

    [Fact]
    public async Task A_manifest_with_another_version_than_the_registry_entry_is_refused()
    {
        var zip = (M with { ManifestVersionOverride = "9.9.9" }).Zip();
        _env.ServeZip(M, zip);
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.ManifestMismatch);
    }

    [Fact]
    public async Task A_manifest_whose_host_api_range_excludes_this_host_is_refused()
    {
        var zip = (M with { HostApi = ">=0.2 <0.3" }).Zip();
        _env.ServeZip(M, zip);
        // the registry still claims ">=0.1 <0.2"
        var entry = _env.Entry(M, zip, v => RegistryEdit.Set(v, "sha256", ZipKit.Sha256(zip)));
        await Fails(Install(entry), InstallErrorCode.IncompatibleHostApi);
    }

    [Fact]
    public async Task A_registry_release_for_another_host_api_is_refused_before_any_download()
    {
        var other = M with { HostApi = ">=0.2 <0.3" };
        var zip = other.Zip();
        var entry = _env.Entry(other, zip);
        await Fails(Install(entry), InstallErrorCode.IncompatibleHostApi);
        Assert.Empty(_env.Http.Requests);
    }

    [Fact]
    public async Task A_manifest_that_declares_more_than_the_registry_says_is_refused()
    {
        var greedy = M with { Admin = true, Network = new[] { "tcp:9999 (secret channel)" } };
        var zip = greedy.Zip();
        _env.ServeZip(M, zip);
        var entry = _env.Entry(M, zip, v => RegistryEdit.Set(v, "sha256", ZipKit.Sha256(zip)));   // registry declares nothing
        var ex = await Fails(Install(entry), InstallErrorCode.DeclaresWider);
        Assert.Contains("administrator", ex.Detail);
        Assert.Contains("tcp:9999", ex.Detail);
    }

    [Fact]
    public async Task A_manifest_that_declares_less_than_the_registry_says_is_fine()
    {
        var wide = M with { Admin = true, Network = new[] { "tcp:1", "tcp:2" } };
        var zip = M.Zip();   // the zip asks for nothing
        _env.ServeZip(M, zip);
        var entry = _env.Entry(M, zip, v => RegistryEdit.Set(v, "declares", new { admin = true, network = new[] { "tcp:1", "tcp:2" } }));
        await Install(entry);
        Assert.Single(new PendingStore(_env.Paths, _env.Log).Read());
        _ = wide;
    }

    [Fact]
    public async Task A_module_that_is_already_installed_is_not_installed_again()
    {
        _env.Install(M);
        var zip = M.Zip();
        _env.ServeZip(M, zip);
        await Fails(Install(_env.Entry(M, zip)), InstallErrorCode.AlreadyInstalled);
        Assert.Empty(_env.Http.Requests);
    }

    [Fact]
    public async Task A_second_change_to_the_same_module_waits_for_the_restart()
    {
        var zip = M.Zip();
        _env.ServeZip(M, zip);
        var entry = _env.Entry(M, zip);
        await Install(entry);
        var ex = await Assert.ThrowsAsync<InstallException>(() => Install(entry));
        Assert.Equal(InstallErrorCode.AlreadyPending, ex.Code);
        Assert.Single(new PendingStore(_env.Paths, _env.Log).Read());
    }

    [Fact]
    public async Task Only_one_change_runs_at_a_time()
    {
        var zip = M.Zip();
        _env.Http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent(zip[..10], zip.Length, stall: true) };
        var installer = _env.NewInstaller();
        var entry = _env.Entry(M, zip);
        using var cts = new CancellationTokenSource();
        var first = installer.InstallAsync(entry, entry.Versions[0], null, cts.Token);
        await Task.Delay(200);
        var ex = await Assert.ThrowsAsync<InstallException>(() => installer.InstallAsync(entry, entry.Versions[0], null, default));
        Assert.Equal(InstallErrorCode.Busy, ex.Code);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task An_update_needs_a_newer_release_than_the_installed_one()
    {
        _env.Install(M);
        var newer = M with { Version = "1.1.0" };
        var zip = newer.Zip();
        _env.Http.Respond = r => r.RequestUri!.ToString() == RegistryKit.ZipUrl("1.1.0") ? FakeHttp.Bytes(zip) : new HttpResponseMessage(HttpStatusCode.NotFound);
        var entry = _env.Entry(newer, zip);

        await _env.NewInstaller().UpdateAsync(entry, entry.Versions[0], null, default);
        var op = Assert.Single(new PendingStore(_env.Paths, _env.Log).Read());
        Assert.Equal((PendingKinds.Update, "1.0.0", "1.1.0"), (op.Op, op.FromVersion, op.Version));
        Assert.Equal("1.0.0", _env.Version("demo-tool"));   // the running version's files are untouched

        // the same version again, or an older one, is not an update
        new PendingStore(_env.Paths, _env.Log).Remove("demo-tool");
        var older = M with { Version = "0.9.0" };
        var olderZip = older.Zip();
        var olderEntry = _env.Entry(older, olderZip);
        var ex = await Assert.ThrowsAsync<InstallException>(() => _env.NewInstaller().UpdateAsync(olderEntry, olderEntry.Versions[0], null, default));
        Assert.Equal(InstallErrorCode.AlreadyInstalled, ex.Code);
    }

    [Fact]
    public async Task Updating_a_module_that_is_not_installed_is_refused()
    {
        var zip = M.Zip();
        var entry = _env.Entry(M, zip);
        var ex = await Assert.ThrowsAsync<InstallException>(() => _env.NewInstaller().UpdateAsync(entry, entry.Versions[0], null, default));
        Assert.Equal(InstallErrorCode.NotInstalled, ex.Code);
    }

    [Fact]
    public void Removal_is_queued_and_the_files_stay_until_the_restart()
    {
        _env.Install(M);
        var installer = _env.NewInstaller();
        installer.QueueRemove("demo-tool");
        var op = Assert.Single(installer.Pending.Read());
        Assert.Equal((PendingKinds.Remove, "1.0.0"), (op.Op, op.FromVersion));
        Assert.True(File.Exists(Path.Combine(_env.ModulesRoot, "demo-tool", "module.json")));
        Assert.Equal(InstallErrorCode.AlreadyPending, Assert.Throws<InstallException>(() => installer.QueueRemove("demo-tool")).Code);
        Assert.Equal(InstallErrorCode.NotInstalled, Assert.Throws<InstallException>(() => installer.QueueRemove("nothing-here")).Code);
    }

    [Fact]
    public void Rollback_needs_a_kept_previous_version()
    {
        _env.Install(M);
        var installer = _env.NewInstaller();
        Assert.Equal(InstallErrorCode.NothingToRollBack, Assert.Throws<InstallException>(() => installer.QueueRollback("demo-tool")).Code);
        Directory.CreateDirectory(_env.Paths.Previous);
        var previous = Path.Combine(_env.Paths.Previous, "demo-tool");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "module.json"), (M with { Version = "0.9.0" }).ManifestJson());
        File.WriteAllBytes(Path.Combine(previous, "Demo.dll"), new byte[] { 1 });
        Assert.Equal("0.9.0", installer.PreviousVersion("demo-tool"));
        installer.QueueRollback("demo-tool");
        Assert.Equal(PendingKinds.Rollback, Assert.Single(installer.Pending.Read()).Op);
    }

    [Fact]
    public async Task A_queued_change_can_be_taken_back_before_the_restart()
    {
        var zip = M.Zip();
        _env.ServeZip(M, zip);
        var installer = _env.NewInstaller();
        var entry = _env.Entry(M, zip);
        await installer.InstallAsync(entry, entry.Versions[0], null, default);
        Assert.True(installer.CancelPending("demo-tool"));
        AssertNothingLeftBehind();
        Assert.False(installer.CancelPending("demo-tool"));
    }

    private sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}

internal static class RegistryEdit
{
    public static object Set(object version, string field, object value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(version, RegistryKit.Web))!.AsObject();
        node[field] = System.Text.Json.JsonSerializer.SerializeToNode(value, RegistryKit.Web);
        return node;
    }
}

public class RegistryClientTests : IDisposable
{
    private readonly InstallEnv _env = new();
    private static readonly TestModule M = new();

    public void Dispose() => _env.Dispose();

    private string Json => RegistryKit.One(M, M.Zip());

    [Fact]
    public async Task A_fetch_is_cached_with_its_etag_and_the_next_one_is_conditional()
    {
        _env.Http.Respond = r => r.Headers.TryGetValues("If-None-Match", out var v) && v.First() == "\"abc\"" ? new HttpResponseMessage(HttpStatusCode.NotModified) : FakeHttp.Text(Json, "\"abc\"");
        var client = _env.NewRegistryClient();

        var first = await client.RefreshAsync(default);
        Assert.Equal(RegistryStatus.Fresh, first.Status);
        Assert.Equal("demo-tool", Assert.Single(first.Document!.Modules).Id);
        Assert.Null(_env.Http.Requests[0].IfNoneMatch);

        var second = await client.RefreshAsync(default);
        Assert.Equal(RegistryStatus.Fresh, second.Status);
        Assert.Equal("\"abc\"", _env.Http.Requests[1].IfNoneMatch);
        Assert.Equal("demo-tool", Assert.Single(second.Document!.Modules).Id);
        Assert.Equal(InstallPolicy.DefaultRegistryUrl, _env.Http.Requests[0].Uri);
    }

    [Fact]
    public async Task A_request_carries_no_identifier_beyond_a_generic_agent_name()
    {
        var headers = new List<string>();
        _env.Http.Respond = r => { headers.AddRange(r.Headers.Select(h => h.Key + "=" + string.Join(",", h.Value))); return FakeHttp.Text(Json); };
        await _env.NewRegistryClient().RefreshAsync(default);
        var line = Assert.Single(headers);
        Assert.Equal("User-Agent=Rigbay-Installer", line);
        Assert.DoesNotContain(Environment.MachineName, line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Offline_with_a_cache_shows_the_cache_and_says_it_is_offline()
    {
        _env.Http.Respond = _ => FakeHttp.Text(Json);
        await _env.NewRegistryClient().RefreshAsync(default);

        _env.Http.Respond = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host");
        var s = await _env.NewRegistryClient().RefreshAsync(default);
        Assert.Equal(RegistryStatus.Cached, s.Status);
        Assert.Equal(InstallErrorCode.Offline, s.Problem);
        Assert.NotNull(s.Document);
        Assert.NotNull(s.FetchedAt);
    }

    [Fact]
    public async Task Offline_without_a_cache_has_nothing_to_show()
    {
        _env.Http.Respond = _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
        var s = await _env.NewRegistryClient().RefreshAsync(default);
        Assert.Equal(RegistryStatus.Unavailable, s.Status);
        Assert.Equal(InstallErrorCode.Offline, s.Problem);
        Assert.Null(s.Document);
    }

    [Fact]
    public async Task A_server_error_is_unreachable_not_offline_and_the_cache_still_serves()
    {
        _env.Http.Respond = _ => FakeHttp.Text(Json);
        await _env.NewRegistryClient().RefreshAsync(default);
        _env.Http.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var s = await _env.NewRegistryClient().RefreshAsync(default);
        Assert.Equal((RegistryStatus.Cached, InstallErrorCode.RegistryUnreachable), (s.Status, s.Problem));
        Assert.NotNull(s.Document);
    }

    [Fact]
    public async Task An_invalid_registry_is_not_used_and_does_not_replace_the_good_copy()
    {
        _env.Http.Respond = _ => FakeHttp.Text(Json);
        await _env.NewRegistryClient().RefreshAsync(default);
        _env.Http.Respond = _ => FakeHttp.Text("{\"schemaVersion\":1,\"modules\":[{\"id\":\"BAD\"}]}");
        var s = await _env.NewRegistryClient().RefreshAsync(default);
        Assert.Equal((RegistryStatus.Cached, InstallErrorCode.RegistryInvalid), (s.Status, s.Problem));
        Assert.Equal("demo-tool", Assert.Single(_env.NewRegistryClient().LoadCache().Document!.Modules).Id);
    }

    [Fact]
    public async Task An_invalid_registry_without_a_cache_is_reported_as_invalid()
    {
        _env.Http.Respond = _ => FakeHttp.Text("this is not json");
        var s = await _env.NewRegistryClient().RefreshAsync(default);
        Assert.Equal((RegistryStatus.Unavailable, InstallErrorCode.RegistryInvalid), (s.Status, s.Problem));
        Assert.False(File.Exists(Path.Combine(_env.Paths.RegistryCache, "registry.json")));
    }

    [Fact]
    public async Task The_registry_may_not_redirect_to_another_host()
    {
        _env.Http.Respond = _ => FakeHttp.Redirect("https://evil.example.com/registry.json");
        var s = await _env.NewRegistryClient().RefreshAsync(default);
        Assert.Equal(InstallErrorCode.UrlNotAllowed, s.Problem);
        Assert.Single(_env.Http.Requests);
    }

    [Fact]
    public void A_cache_that_was_edited_into_something_invalid_is_ignored()
    {
        Directory.CreateDirectory(_env.Paths.RegistryCache);
        File.WriteAllText(Path.Combine(_env.Paths.RegistryCache, "registry.json"), "{\"schemaVersion\":1,\"modules\":[{\"id\":\"x\"}]}");
        File.WriteAllText(Path.Combine(_env.Paths.RegistryCache, "registry.meta.json"), "{\"data\":1}");
        Assert.Equal(RegistryStatus.Unavailable, _env.NewRegistryClient().LoadCache().Status);
    }

    [Fact]
    public async Task A_cache_for_another_address_is_not_used()
    {
        _env.Http.Respond = _ => FakeHttp.Text(Json);
        await _env.NewRegistryClient().RefreshAsync(default);
        var other = new RegistryClient(_env.Downloader, _env.Policy, _env.Paths, _env.Security, new Uri("https://raw.githubusercontent.com/someone/registry/main/registry.json"), _env.Log);
        Assert.Equal(RegistryStatus.Unavailable, other.LoadCache().Status);
    }

    [Fact]
    public async Task A_huge_registry_file_is_refused()
    {
        _env.Policy = new InstallPolicy { MaxRegistryBytes = 200 };
        _env.Http.Respond = _ => FakeHttp.Text(new string('x', 5000));
        var s = await _env.NewRegistryClient().RefreshAsync(default);
        Assert.Equal(InstallErrorCode.RegistryInvalid, s.Problem);
    }
}

public class ModuleManagerTests : IDisposable
{
    private readonly InstallEnv _env = new();
    private static readonly TestModule M = new();
    private readonly FakeInstalled _installed = new();

    public void Dispose() => _env.Dispose();

    private sealed class FakeInstalled : IInstalledModules
    {
        public List<InstalledModule> List { get; } = new();
        public Dictionary<string, (PendingChange Change, string? Version)> Marks { get; } = new();
        public IReadOnlyList<InstalledModule> Current => List.ToList();
        public void MarkPending(string id, PendingChange change, string? version) => Marks[id] = (change, version);
    }

    private ModuleManager NewManager() => new(_env.NewInstaller(), _env.NewRegistryClient(), _installed, _env.HostApi, _env.Log);

    private void ServeRegistryAndZip(params (TestModule Module, byte[] Zip)[] releases)
    {
        var byModule = releases.GroupBy(r => r.Module.Id).Select(g => RegistryKit.Module(g.Key, g.Select(r => RegistryKit.Version(r.Module, r.Zip)).ToArray())).ToArray();
        var json = RegistryKit.Json(byModule);
        _env.Http.Respond = r =>
        {
            var url = r.RequestUri!.ToString();
            if (url == InstallPolicy.DefaultRegistryUrl.ToString()) return FakeHttp.Text(json);
            foreach (var (module, zip) in releases) if (url == RegistryKit.ZipUrlOf(module)) return FakeHttp.Bytes(zip);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    [Fact]
    public async Task The_available_list_shows_each_honest_state()
    {
        var newer = new TestModule(Id: "newer-one", HostApi: ">=0.2 <0.3");
        var installedMod = new TestModule(Id: "have-it");
        var queued = new TestModule(Id: "queued-one");
        var free = new TestModule(Id: "free-one");
        ServeRegistryAndZip((free, free.Zip()), (newer, newer.Zip()), (installedMod, installedMod.Zip()), (queued, queued.Zip()));
        _env.Install(installedMod);
        _installed.List.Add(new InstalledModule("have-it", "1.0.0", ModuleLifecycle.Off));
        var manager = NewManager();
        await manager.CheckAsync();
        Assert.True(await manager.InstallAsync("queued-one"));

        var rows = manager.AvailableRows().ToDictionary(r => r.Module.Id);
        Assert.Equal(AvailableState.Installable, rows["free-one"].State);
        Assert.Equal(AvailableState.Incompatible, rows["newer-one"].State);
        Assert.Equal(AvailableState.Installed, rows["have-it"].State);
        Assert.Equal("1.0.0", rows["have-it"].InstalledVersion);
        Assert.Equal(AvailableState.Queued, rows["queued-one"].State);
        Assert.True(manager.RestartRequired);
    }

    [Fact]
    public async Task A_module_the_host_no_longer_knows_is_named_by_the_registry_not_by_its_id()
    {
        // E2E1: after a removal the start-up report said "recorder was removed" (the id) because the folder, and with it the manifest name, was gone.
        ServeRegistryAndZip((M, M.Zip()));
        var manager = NewManager();
        Assert.Null(manager.RegistryNameOf(M.Id));   // no list yet
        await manager.CheckAsync();
        Assert.Equal("Demo " + M.Id, manager.RegistryNameOf(M.Id));   // the name RegistryKit gives every entry
        Assert.Null(manager.RegistryNameOf("not-in-the-list"));

        _env.Http.Respond = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no host");
        var later = NewManager();
        later.LoadCache();   // what the app does at start, before the page is built: the name survives a restart and needs no network
        Assert.Equal(manager.RegistryNameOf(M.Id), later.RegistryNameOf(M.Id));
    }

    [Fact]
    public async Task Offline_the_list_comes_from_the_cache_and_says_so()
    {
        ServeRegistryAndZip((M, M.Zip()));
        await NewManager().CheckAsync();

        _env.Http.Respond = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no host");
        var manager = NewManager();
        manager.LoadCache();
        Assert.Equal((RegistryStatus.Cached, null), (manager.Snapshot.Status, manager.Snapshot.Problem));
        await manager.CheckAsync();
        Assert.Equal((RegistryStatus.Cached, (InstallErrorCode?)InstallErrorCode.Offline), (manager.Snapshot.Status, manager.Snapshot.Problem));
        Assert.Single(manager.AvailableRows());
    }

    [Fact]
    public async Task An_installed_module_with_a_newer_release_is_marked_update_available()
    {
        var newer = M with { Version = "1.2.0" };
        ServeRegistryAndZip((newer, newer.Zip()));
        _env.Install(M);
        _installed.List.Add(new InstalledModule("demo-tool", "1.0.0", ModuleLifecycle.Running));
        var manager = NewManager();
        await manager.CheckAsync();

        Assert.Equal((PendingChange.UpdateAvailable, "1.2.0"), _installed.Marks["demo-tool"]);
        Assert.Equal("1.2.0", manager.InstalledRowOf(_installed.List[0]).Update!.Version);
    }

    [Fact]
    public async Task An_installed_module_that_is_up_to_date_has_no_mark()
    {
        ServeRegistryAndZip((M, M.Zip()));
        _env.Install(M);
        _installed.List.Add(new InstalledModule("demo-tool", "1.0.0", ModuleLifecycle.Running));
        var manager = NewManager();
        await manager.CheckAsync();
        Assert.Equal(PendingChange.None, _installed.Marks["demo-tool"].Change);
    }

    [Fact]
    public async Task A_failed_install_is_remembered_with_its_reason_and_changes_nothing()
    {
        var zip = M.Zip();
        var tampered = (byte[])zip.Clone();
        tampered[10] ^= 0xFF;
        ServeRegistryAndZip((M, tampered));
        // the registry says the sha of the good zip, the server sends the tampered one
        var json = RegistryKit.Json(RegistryKit.Module(M.Id, new[] { RegistryKit.Version(M, zip) }));
        _env.Http.Respond = r => r.RequestUri!.ToString() == InstallPolicy.DefaultRegistryUrl.ToString() ? FakeHttp.Text(json) : FakeHttp.Bytes(tampered);
        var manager = NewManager();
        await manager.CheckAsync();

        Assert.False(await manager.InstallAsync("demo-tool"));
        var failure = manager.FailureOf("demo-tool")!;
        Assert.Equal((InstallErrorCode.HashMismatch, OperationKind.Install), (failure.Code, failure.Kind));
        Assert.False(manager.RestartRequired);
        Assert.Null(manager.Operation);
        manager.DismissFailure("demo-tool");
        Assert.Null(manager.FailureOf("demo-tool"));
    }

    [Fact]
    public async Task Installing_something_that_is_not_in_the_list_says_so()
    {
        ServeRegistryAndZip((M, M.Zip()));
        var manager = NewManager();
        await manager.CheckAsync();
        Assert.False(await manager.InstallAsync("no-such-module"));
        Assert.Equal(InstallErrorCode.NotInRegistry, manager.FailureOf("no-such-module")!.Code);
    }

    [Fact]
    public async Task Installing_a_module_for_another_host_api_says_so()
    {
        var future = new TestModule(Id: "from-the-future", HostApi: ">=0.2 <0.3");
        ServeRegistryAndZip((future, future.Zip()));
        var manager = NewManager();
        await manager.CheckAsync();
        Assert.False(await manager.InstallAsync("from-the-future"));
        Assert.Equal(InstallErrorCode.IncompatibleHostApi, manager.FailureOf("from-the-future")!.Code);
    }

    [Fact]
    public async Task Cancel_stops_the_running_download_and_is_not_a_failure()
    {
        var zip = M.Zip();
        var json = RegistryKit.One(M, zip);
        _env.Http.Respond = r => r.RequestUri!.ToString() == InstallPolicy.DefaultRegistryUrl.ToString()
            ? FakeHttp.Text(json)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent(zip[..10], zip.Length, stall: true) };
        var manager = NewManager();
        await manager.CheckAsync();

        var task = manager.InstallAsync("demo-tool");
        await WaitFor(() => manager.Operation is { Phase: InstallPhase.Downloading });
        Assert.Equal("demo-tool", manager.Operation!.Id);
        manager.CancelOperation();
        Assert.False(await task);
        Assert.Null(manager.Operation);
        Assert.Null(manager.FailureOf("demo-tool"));
        Assert.False(manager.RestartRequired);
    }

    [Fact]
    public async Task A_second_change_while_one_runs_is_told_to_wait()
    {
        var zip = M.Zip();
        var other = new TestModule(Id: "other-one");
        var json = RegistryKit.Json(RegistryKit.Module(M.Id, new[] { RegistryKit.Version(M, zip) }), RegistryKit.Module(other.Id, new[] { RegistryKit.Version(other, other.Zip()) }));
        _env.Http.Respond = r => r.RequestUri!.ToString() == InstallPolicy.DefaultRegistryUrl.ToString()
            ? FakeHttp.Text(json)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent(zip[..10], zip.Length, stall: true) };
        var manager = NewManager();
        await manager.CheckAsync();
        var first = manager.InstallAsync("demo-tool");
        await WaitFor(() => manager.Operation is not null);
        Assert.False(await manager.InstallAsync("other-one"));
        Assert.Equal(InstallErrorCode.Busy, manager.FailureOf("other-one")!.Code);
        manager.CancelOperation();
        await first;
    }

    [Fact]
    public async Task Remove_marks_the_module_restart_required_and_undo_takes_it_back()
    {
        ServeRegistryAndZip((M, M.Zip()));
        _env.Install(M);
        _installed.List.Add(new InstalledModule("demo-tool", "1.0.0", ModuleLifecycle.Off));
        var manager = NewManager();
        Assert.True(await manager.RemoveAsync("demo-tool"));
        Assert.Equal(PendingChange.RestartRequired, _installed.Marks["demo-tool"].Change);
        Assert.True(manager.RestartRequired);
        Assert.Equal(PendingKinds.Remove, manager.InstalledRowOf(_installed.List[0]).Pending!.Op);

        Assert.True(await manager.UndoAsync("demo-tool"));
        Assert.False(manager.RestartRequired);
        Assert.Equal(PendingChange.None, _installed.Marks["demo-tool"].Change);
    }

    [Fact]
    public async Task Changes_are_reported_through_the_changed_event()
    {
        ServeRegistryAndZip((M, M.Zip()));
        var manager = NewManager();
        int raised = 0;
        manager.Changed += () => Interlocked.Increment(ref raised);
        await manager.CheckAsync();
        Assert.True(raised >= 2);   // checking started, checking ended
        Assert.False(manager.IsChecking);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "timed out waiting");
    }
}
