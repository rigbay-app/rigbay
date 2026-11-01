using System.Text.Json;
using Rigbay.Core.Install;
using Xunit;

namespace Rigbay.Core.Tests;

public sealed class SimulatedCrash(string step) : Exception("simulated crash at " + step);

/// <summary>Everything that calls <see cref="PendingRunner.Run"/> shares this collection: the step hook is static.</summary>
[CollectionDefinition("pending-runner", DisableParallelization = true)]
public class PendingRunnerCollection { }

[Collection("pending-runner")]
public class PendingRunnerTests : IDisposable
{
    private readonly InstallEnv _env = new();
    private static readonly TestModule Old = new(Version: "1.0.0");
    private static readonly TestModule New = new(Version: "1.1.0");

    public void Dispose() => _env.Dispose();

    // ---------------------------------------------------------------- arrangement

    private string HostJson => Path.Combine(_env.DataRoot, "host.json");
    private string AppJson => Path.Combine(_env.DataRoot, "app.json");

    private void WriteSettings(bool enabled, bool acknowledged)
    {
        File.WriteAllText(HostJson, JsonSerializer.Serialize(new { version = 1, data = new { enabledModules = enabled ? new[] { "demo-tool", "other" } : new[] { "other" }, language = "en" } }));
        File.WriteAllText(AppJson, JsonSerializer.Serialize(new { version = 1, data = new { theme = "dark", logLevel = "info", acknowledged = acknowledged ? new[] { "demo-tool", "other" } : new[] { "other" } } }));
    }

    private bool Enabled => JsonDocument.Parse(File.ReadAllText(HostJson)).RootElement.GetProperty("data").GetProperty("enabledModules").EnumerateArray().Any(e => e.GetString() == "demo-tool");
    private bool Acknowledged => JsonDocument.Parse(File.ReadAllText(AppJson)).RootElement.GetProperty("data").GetProperty("acknowledged").EnumerateArray().Any(e => e.GetString() == "demo-tool");

    /// <summary>A verified module waiting in <c>ready</c> with its change queued.</summary>
    private void Stage(TestModule module, string kind, string? fromVersion = null)
    {
        _env.Paths.Prepare(_env.Security);
        var ready = _env.Paths.ReadyFolder(module.Id);
        Directory.CreateDirectory(ready);
        File.WriteAllText(Path.Combine(ready, "module.json"), module.ManifestJson());
        File.WriteAllBytes(Path.Combine(ready, "Demo.dll"), new byte[] { 0x4D, 0x5A, 9, 9 });
        new PendingStore(_env.Paths, _env.Log).Upsert(new PendingOp { Op = kind, Id = module.Id, Version = module.Version, FromVersion = fromVersion, TreeHash = FileOps.TreeHash(ready), Queued = DateTimeOffset.Now });
    }

    private void Previous(TestModule module)
    {
        var folder = _env.Paths.PreviousFolder(module.Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "module.json"), module.ManifestJson());
        File.WriteAllBytes(Path.Combine(folder, "Demo.dll"), new byte[] { 1 });
    }

    private PendingRunReport Run() => PendingRunner.Run(_env.Paths, _env.Security, _env.DataRoot, _ => { });

    private IReadOnlyList<PendingOp> Pending => new PendingStore(_env.Paths, _env.Log).Read();

    private string? VersionIn(string folder) => Directory.Exists(folder) ? ManifestReader.ReadFolder(folder).Manifest?.Version : null;

    private string? Installed => VersionIn(_env.Paths.ModuleFolder("demo-tool"));
    private string? Kept => VersionIn(_env.Paths.PreviousFolder("demo-tool"));

    // ---------------------------------------------------------------- the four changes

    [Fact]
    public void Install_puts_the_module_in_place_turned_off_and_unapproved()
    {
        WriteSettings(enabled: true, acknowledged: true);   // left over from an earlier install of the same id
        Stage(New, PendingKinds.Install);

        var report = Run();

        Assert.Equal("1.1.0", Installed);
        Assert.False(Directory.Exists(_env.Paths.ReadyFolder("demo-tool")));
        Assert.Empty(Pending);
        Assert.False(Enabled);
        Assert.False(Acknowledged);
        var outcome = Assert.Single(report.Outcomes);
        Assert.True(outcome.Applied);
        Assert.True(outcome.NeedsApproval);
        Assert.Equal("1.1.0", PendingRunner.ReadLastRun(_env.Paths)!.Outcomes[0].ToVersion);
    }

    [Fact]
    public void Install_leaves_other_modules_settings_alone()
    {
        WriteSettings(enabled: true, acknowledged: true);
        Stage(New, PendingKinds.Install);
        Run();
        Assert.Contains("other", File.ReadAllText(HostJson));
        Assert.Contains("other", File.ReadAllText(AppJson));
        Assert.Contains("\"theme\"", File.ReadAllText(AppJson));
        Assert.Contains("\"language\"", File.ReadAllText(HostJson));
    }

    [Fact]
    public void Update_swaps_the_folders_keeps_the_old_one_and_keeps_the_module_enabled()
    {
        WriteSettings(enabled: true, acknowledged: true);
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");

        var report = Run();

        Assert.Equal("1.1.0", Installed);
        Assert.Equal("1.0.0", Kept);
        Assert.Empty(Pending);
        Assert.True(Enabled);
        Assert.True(Acknowledged);
        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal(("1.0.0", "1.1.0", false), (outcome.FromVersion, outcome.ToVersion, outcome.NeedsApproval));
    }

    [Fact]
    public void An_update_that_asks_for_more_is_turned_off_until_the_user_accepts_again()
    {
        WriteSettings(enabled: true, acknowledged: true);
        _env.Install(Old);
        Stage(New with { Admin = true, Network = new[] { "tcp:5000" } }, PendingKinds.Update, "1.0.0");

        var report = Run();

        Assert.Equal("1.1.0", Installed);
        Assert.False(Enabled);
        Assert.False(Acknowledged);
        Assert.True(Assert.Single(report.Outcomes).NeedsApproval);
    }

    [Fact]
    public void An_update_that_asks_for_less_stays_enabled()
    {
        WriteSettings(enabled: true, acknowledged: true);
        _env.Install(Old with { Admin = true, Network = new[] { "tcp:5000", "tcp:5001" } });
        Stage(New with { Admin = true, Network = new[] { "tcp:5000" } }, PendingKinds.Update, "1.0.0");
        Run();
        Assert.True(Enabled);
        Assert.True(Acknowledged);
    }

    [Fact]
    public void Remove_moves_the_module_aside_and_forgets_the_choice_but_not_the_data()
    {
        WriteSettings(enabled: true, acknowledged: true);
        _env.Install(Old);
        var data = Path.Combine(_env.DataRoot, "modules", "demo-tool");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
        new PendingStore(_env.Paths, _env.Log).Upsert(new PendingOp { Op = PendingKinds.Remove, Id = "demo-tool", FromVersion = "1.0.0" });
        _env.Paths.Prepare(_env.Security);

        Run();

        Assert.Null(Installed);
        Assert.Equal("1.0.0", Kept);
        Assert.False(Enabled);
        Assert.False(Acknowledged);
        Assert.True(File.Exists(Path.Combine(data, "settings.json")));
    }

    [Fact]
    public void Rollback_brings_back_the_kept_version()
    {
        WriteSettings(enabled: true, acknowledged: true);
        _env.Install(New);
        Previous(Old);
        new PendingStore(_env.Paths, _env.Log).Upsert(new PendingOp { Op = PendingKinds.Rollback, Id = "demo-tool", FromVersion = "1.1.0", Version = "1.0.0" });
        _env.Paths.Prepare(_env.Security);

        Run();

        Assert.Equal("1.0.0", Installed);
        Assert.Null(Kept);
        Assert.Empty(Pending);
        Assert.False(Enabled);   // the user turns the old version on again
        Assert.True(Acknowledged);
    }

    // ---------------------------------------------------------------- the kept copy

    [Fact]
    public void The_previous_version_is_deleted_only_when_the_new_one_started_healthy()
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");
        Run();
        Assert.Equal("1.0.0", Kept);

        var kept = PendingRunner.CommitHealthy(_env.Paths, id => false, _ => { });
        Assert.Equal(new[] { "demo-tool" }, kept);
        Assert.Equal("1.0.0", Kept);   // still there: the new version did not load

        PendingRunner.CommitHealthy(_env.Paths, id => true, _ => { });
        Assert.Null(Kept);
    }

    // ---------------------------------------------------------------- refusals at apply time

    [Fact]
    public void Files_that_changed_after_they_were_verified_are_not_swapped_in()
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");
        File.WriteAllBytes(Path.Combine(_env.Paths.ReadyFolder("demo-tool"), "Demo.dll"), new byte[] { 0x4D, 0x5A, 6, 6, 6 });   // somebody edited the staged file

        var report = Run();

        Assert.Equal("1.0.0", Installed);
        Assert.False(Assert.Single(report.Outcomes).Applied);
        Assert.Empty(Pending);
        Assert.False(Directory.Exists(_env.Paths.ReadyFolder("demo-tool")));
    }

    [Fact]
    public void A_file_added_to_the_staged_folder_is_noticed_too()
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");
        File.WriteAllText(Path.Combine(_env.Paths.ReadyFolder("demo-tool"), "extra.dll"), "x");
        Run();
        Assert.Equal("1.0.0", Installed);
    }

    [Fact]
    public void An_install_over_an_existing_folder_is_dropped_and_the_existing_module_is_kept()
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Install);
        var report = Run();
        Assert.Equal("1.0.0", Installed);
        Assert.False(Assert.Single(report.Outcomes).Applied);
        Assert.Empty(Pending);
    }

    [Fact]
    public void A_module_folder_that_is_a_link_is_not_touched()
    {
        var elsewhere = Path.Combine(_env.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "module.json"), Old.ManifestJson());
        File.WriteAllBytes(Path.Combine(elsewhere, "Demo.dll"), new byte[] { 1 });
        if (!SafeZipTests.TryMakeJunction(_env.Paths.ModuleFolder("demo-tool"), elsewhere)) return;
        Stage(New, PendingKinds.Update, "1.0.0");

        var report = Run();

        Assert.False(Assert.Single(report.Outcomes).Applied);
        Assert.True(File.Exists(Path.Combine(elsewhere, "module.json")));
        Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(elsewhere, "module.json")));
        FileOps.SafeDelete(_env.Paths.ModuleFolder("demo-tool"));
    }

    [Fact]
    public void A_file_held_open_postpones_the_change_to_the_next_start()
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");
        using var held = new FileStream(Path.Combine(_env.Paths.ModuleFolder("demo-tool"), "Demo.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var report = Run();

        var outcome = Assert.Single(report.Outcomes);
        Assert.True(outcome.WillRetry);
        Assert.False(outcome.Applied);
        Assert.Equal("1.0.0", Installed);
        Assert.Single(Pending);                                              // kept for the next start
        Assert.True(Directory.Exists(_env.Paths.ReadyFolder("demo-tool")));  // and its files too

        held.Dispose();
        Assert.True(Assert.Single(Run().Outcomes).Applied);
        Assert.Equal("1.1.0", Installed);
    }

    [Fact]
    public void A_module_whose_code_is_loaded_in_this_process_is_not_touched()
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");
        var context = new System.Runtime.Loader.AssemblyLoadContext("module:demo-tool", isCollectible: true);   // named like the loader names the context of a module
        try
        {
            var outcome = Assert.Single(Run().Outcomes);
            Assert.True(outcome.WillRetry);
            Assert.False(outcome.Applied);
            Assert.Equal("1.0.0", Installed);
            Assert.Single(Pending);
        }
        finally { context.Unload(); }
        Assert.True(Assert.Single(Run().Outcomes).Applied);   // once nothing is loaded (the next start) it goes through
        Assert.Equal("1.1.0", Installed);
    }

    [Fact]
    public void When_the_folders_cannot_be_made_safe_nothing_is_applied()
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");
        _env.Security = new RefusingSecurity();
        var report = Run();
        Assert.True(Assert.Single(report.Outcomes).WillRetry);
        Assert.Equal("1.0.0", Installed);
        Assert.Single(Pending);
    }

    private sealed class RefusingSecurity : IFolderSecurity
    {
        public void EnsureAdminOnly(string path) => throw new InstallException(InstallErrorCode.FolderSecurity, "refused for the test");
    }

    // ---------------------------------------------------------------- housekeeping

    [Fact]
    public void Leftovers_of_an_interrupted_download_or_unpack_are_cleaned_at_start()
    {
        _env.Paths.Prepare(_env.Security);
        File.WriteAllText(Path.Combine(_env.Paths.Downloads, "abc.zip"), "half");
        Directory.CreateDirectory(Path.Combine(_env.Paths.Staging, "abc", "demo-tool"));
        Directory.CreateDirectory(Path.Combine(_env.Paths.Ready, "orphan-module"));
        Directory.CreateDirectory(Path.Combine(_env.Paths.Trash, "x"));
        Run();
        Assert.Empty(Directory.GetFileSystemEntries(_env.Paths.Downloads));
        Assert.Empty(Directory.GetFileSystemEntries(_env.Paths.Staging));
        Assert.Empty(Directory.GetFileSystemEntries(_env.Paths.Ready));
        Assert.Empty(Directory.GetFileSystemEntries(_env.Paths.Trash));
    }

    [Fact]
    public void A_pending_file_that_cannot_be_read_is_set_aside_not_trusted()
    {
        _env.Paths.Prepare(_env.Security);
        File.WriteAllText(_env.Paths.PendingFile, "{ this is not json");
        var report = Run();
        Assert.Empty(report.Outcomes);
        Assert.False(File.Exists(_env.Paths.PendingFile));
        Assert.Single(Directory.GetFiles(_env.Paths.Root, "pending.json.corrupt-*"));
    }

    [Fact]
    public void A_pending_file_with_a_bad_id_or_kind_is_ignored()
    {
        _env.Paths.Prepare(_env.Security);
        File.WriteAllText(_env.Paths.PendingFile, JsonSerializer.Serialize(new { version = 1, ops = new object[]
        {
            new { op = "install", id = "..\\..\\windows" },
            new { op = "format-disk", id = "demo-tool" },
        } }));
        Assert.Empty(Run().Outcomes);
    }

    [Fact]
    public void Nothing_happens_when_the_installer_never_ran()
    {
        Assert.False(Directory.Exists(_env.Paths.Root));
        Assert.Empty(Run().Outcomes);
        Assert.False(Directory.Exists(_env.Paths.Root));
    }

    // ---------------------------------------------------------------- a crash at every step

    public static IEnumerable<object[]> CrashPoints()
    {
        foreach (var step in new[] { "install:before-disable", "install:disabled", "install:moved", "op:applied-before-forget", "op:forgotten" })
            yield return new object[] { PendingKinds.Install, step };
        foreach (var step in new[] { "update:disabled", "update:old-moved", "update:new-moved", "op:applied-before-forget", "op:forgotten" })
            yield return new object[] { PendingKinds.Update, step };
        foreach (var step in new[] { "remove:disabled", "remove:moved", "op:applied-before-forget", "op:forgotten" })
            yield return new object[] { PendingKinds.Remove, step };
        foreach (var step in new[] { "rollback:disabled", "rollback:old-trashed", "rollback:previous-restored", "op:applied-before-forget", "op:forgotten" })
            yield return new object[] { PendingKinds.Rollback, step };
    }

    private void Arrange(string kind)
    {
        WriteSettings(enabled: true, acknowledged: true);
        switch (kind)
        {
            case PendingKinds.Install:
                Stage(New, PendingKinds.Install);
                break;
            case PendingKinds.Update:
                _env.Install(Old);
                Stage(New with { Admin = true, Network = new[] { "tcp:1" } }, PendingKinds.Update, "1.0.0");   // widened, so the settings edit is exercised too
                break;
            case PendingKinds.Remove:
                _env.Install(Old);
                _env.Paths.Prepare(_env.Security);
                new PendingStore(_env.Paths, _env.Log).Upsert(new PendingOp { Op = PendingKinds.Remove, Id = "demo-tool", FromVersion = "1.0.0" });
                break;
            case PendingKinds.Rollback:
                _env.Install(New);
                Previous(Old);
                _env.Paths.Prepare(_env.Security);
                new PendingStore(_env.Paths, _env.Log).Upsert(new PendingOp { Op = PendingKinds.Rollback, Id = "demo-tool", FromVersion = "1.1.0", Version = "1.0.0" });
                break;
        }
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void A_crash_at_any_step_is_finished_by_the_next_start(string kind, string crashAt)
    {
        Arrange(kind);

        bool reached = false;
        PendingRunner.StepHook = step => { if (step == crashAt) { reached = true; throw new SimulatedCrash(step); } };
        Assert.Throws<SimulatedCrash>(() => Run());
        Assert.True(reached);
        PendingRunner.StepHook = null;

        // "the process died here": the next start runs everything again
        Run();
        Run();   // and a third time changes nothing

        Assert.Empty(Pending);
        Assert.False(Directory.Exists(_env.Paths.ReadyFolder("demo-tool")));
        Assert.Empty(Directory.GetFileSystemEntries(_env.Paths.Trash));
        switch (kind)
        {
            case PendingKinds.Install:
                Assert.Equal("1.1.0", Installed);
                Assert.False(Enabled);
                Assert.False(Acknowledged);
                break;
            case PendingKinds.Update:
                Assert.Equal("1.1.0", Installed);
                Assert.Equal("1.0.0", Kept);
                Assert.False(Enabled);      // it asks for more than 1.0.0 did
                Assert.False(Acknowledged);
                break;
            case PendingKinds.Remove:
                Assert.Null(Installed);
                Assert.Equal("1.0.0", Kept);
                Assert.False(Enabled);
                Assert.False(Acknowledged);
                break;
            case PendingKinds.Rollback:
                Assert.Equal("1.0.0", Installed);
                Assert.Null(Kept);
                Assert.False(Enabled);
                break;
        }
    }

    [Theory]
    [InlineData("update:disabled")]
    [InlineData("update:old-moved")]
    [InlineData("update:new-moved")]
    public void A_crashed_update_never_leaves_the_module_missing_or_half_there(string crashAt)
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");
        PendingRunner.StepHook = step => { if (step == crashAt) throw new SimulatedCrash(step); };
        Assert.Throws<SimulatedCrash>(() => Run());
        PendingRunner.StepHook = null;

        // between the two renames the old copy sits in previous and the new one in ready: both complete
        var folders = new[] { _env.Paths.ModuleFolder("demo-tool"), _env.Paths.PreviousFolder("demo-tool"), _env.Paths.ReadyFolder("demo-tool") }.Where(Directory.Exists).ToList();
        Assert.All(folders, f => Assert.NotNull(ManifestReader.ReadFolder(f).Manifest));
        Assert.Contains(folders, f => f == _env.Paths.ModuleFolder("demo-tool") || f == _env.Paths.ReadyFolder("demo-tool"));
    }

    [Fact]
    public void A_crash_loop_cannot_lose_the_pending_change()
    {
        _env.Install(Old);
        Stage(New, PendingKinds.Update, "1.0.0");
        int calls = 0;
        PendingRunner.StepHook = step => { if (++calls <= 3) throw new SimulatedCrash(step); };   // crash on the first three steps seen, over three starts
        for (int i = 0; i < 3; i++) Assert.Throws<SimulatedCrash>(() => Run());
        PendingRunner.StepHook = null;
        Run();
        Assert.Equal("1.1.0", Installed);
        Assert.Equal("1.0.0", Kept);
    }
}

[Collection("pending-runner")]
public class InstallEndToEndTests : IDisposable
{
    private const string Id = "sample-installed";
    private readonly InstallEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task A_real_module_goes_from_a_release_zip_to_a_module_the_loader_finds_turned_off()
    {
        // the sample module the tests already build, zipped as a release would be
        var built = Path.Combine(AppContext.BaseDirectory, "module-templates", "sample");
        var zipBytes = ZipKit.Build(z =>
        {
            foreach (var file in Directory.EnumerateFiles(built, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(built, file).Replace('\\', '/');
                // another id than the sample: other tests load "sample" in this process, and a loaded module is (rightly) never swapped
                if (name == "module.json") ZipKit.Add(z, name, File.ReadAllText(file).Replace("\"id\": \"sample\"", "\"id\": \"" + Id + "\""));
                else ZipKit.Add(z, name, File.ReadAllBytes(file));
            }
        });
        var manifest = ManifestReader.ReadFolder(built).Manifest! with { Id = Id };
        var declares = RegistryDeclares.Of(manifest);
        var json = RegistryKit.Json(RegistryKit.Module(manifest.Id, new object[]
        {
            new
            {
                version = manifest.Version, hostApi = manifest.HostApi.Text, zip = RegistryKit.ZipUrl(manifest.Version), sha256 = ZipKit.Sha256(zipBytes), size = zipBytes.LongLength,
                released = "2026-11-01", declares = new { admin = declares.Admin, network = declares.Network, registry = declares.Registry, services = declares.Services, outsideFiles = declares.OutsideFiles },
            },
        }));
        _env.Http.Respond = r => r.RequestUri!.ToString() == InstallPolicy.DefaultRegistryUrl.ToString() ? FakeHttp.Text(json) : FakeHttp.Bytes(zipBytes);

        var installed = new HostlessInstalled();
        var manager = new ModuleManager(_env.NewInstaller(), _env.NewRegistryClient(), installed, _env.HostApi, _env.Log);
        await manager.CheckAsync();
        Assert.True(await manager.InstallAsync(manifest.Id));
        Assert.True(manager.RestartRequired);
        Assert.Empty(Directory.GetFileSystemEntries(_env.ModulesRoot));   // nothing moved while "running"

        // next start: the runner first, then the host as the app does it
        PendingRunner.Run(_env.Paths, _env.Security, _env.DataRoot, _ => { });
        using var host = new RigbayHost(new RigbayHostOptions { ModulesRoot = _env.ModulesRoot, DataRoot = _env.DataRoot });
        host.Discover();
        host.StartEnabled();
        var entry = Assert.Single(host.Modules);
        Assert.Equal(manifest.Id, entry.Id);
        Assert.Equal(ModuleLifecycle.Off, entry.Lifecycle);   // installed turned off; the user decides
        Assert.False(host.IsEnabledByUser(manifest.Id));
        Assert.False(manager.RestartRequired);
    }

    private sealed class HostlessInstalled : IInstalledModules
    {
        public IReadOnlyList<InstalledModule> Current => Array.Empty<InstalledModule>();
        public void MarkPending(string id, PendingChange change, string? version) { }
    }
}
