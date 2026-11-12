using System.Runtime.Loader;
using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Core.Tests;

public class LoadingTests
{
    [Fact]
    public void The_sample_module_is_loaded_from_its_folder_probed_enabled_and_shut_down()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        using var host = env.NewHost();
        host.Discover();

        var m = Assert.Single(host.Modules);
        Assert.Equal("sample", m.Id);
        Assert.Equal(ModuleLifecycle.Off, m.Lifecycle);
        Assert.Equal(BayState.Off, m.Bay.State);
        Assert.True(m.HasLoadedCode);
        Assert.Equal("Sample module", m.DisplayName);

        Assert.True(m.Enable());
        Assert.Equal(ModuleLifecycle.Running, m.Lifecycle);
        Assert.Equal(BayState.Ready, m.Bay.State);
        Assert.NotNull(m.Page);
        Assert.True(host.IsEnabledByUser("sample"));

        Assert.True(m.Disable());
        Assert.Equal(ModuleLifecycle.Off, m.Lifecycle);
        Assert.Null(m.Page);
        Assert.False(host.IsEnabledByUser("sample"));
    }

    [Fact]
    public void The_module_runs_in_its_own_load_context_with_the_hosts_copy_of_Abstractions()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        using var host = env.NewHost();
        host.Discover();

        var module = host.Modules.Single().LoadContext!;
        Assert.NotSame(AssemblyLoadContext.Default, module);
        var moduleAssembly = module.Assemblies.Single(a => a.GetName().Name == "Rigbay.SampleModule");
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IModule).Assembly));
        // the module's reference to Abstractions was bound to the default context's copy: no second copy exists
        Assert.DoesNotContain(module.Assemblies, a => a.GetName().Name == "Rigbay.Abstractions");
        Assert.Contains(moduleAssembly.GetTypes(), t => typeof(IModule).IsAssignableFrom(t));
    }

    [Fact]
    public void A_copy_of_Abstractions_in_the_module_folder_is_ignored()
    {
        using var env = new TestEnv();
        var folder = env.AddModule("sample");
        File.Copy(typeof(IModule).Assembly.Location, Path.Combine(folder, "Rigbay.Abstractions.dll"));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Off, m.Lifecycle);
        Assert.True(m.Enable());
    }

    [Fact]
    public void A_dependency_next_to_the_module_dll_is_found()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", behavior: "uses-dependency");
        Assert.True(File.Exists(Path.Combine(env.ModulesRoot, "faulty", "Rigbay.TestDependency.dll")));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable(), m.Reason is null ? "" : m.ResolveReason(m.Reason));
        var row = m.Page!.EnumerateRows().OfType<ValueRow>().Single();
        Assert.Equal("hello from the dependency", row.Value.Value);
        // the dependency lives in the module's context, not in the host's
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "Rigbay.TestDependency");
    }

    [Fact]
    public void Two_modules_do_not_share_a_load_context()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        env.AddModule("faulty");
        using var host = env.NewHost();
        host.Discover();
        Assert.Equal(2, host.Modules.Count(m => m.Lifecycle == ModuleLifecycle.Off));
        Assert.NotSame(host.Find("sample")!.LoadContext, host.Find("faulty")!.LoadContext);
        Assert.Equal("module:sample", host.Find("sample")!.LoadContext!.Name);
    }

    [Fact]
    public void A_module_whose_host_api_range_does_not_match_is_refused_before_any_of_its_code_loads()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", behavior: "ctor-throws",
            editManifest: json => json.Replace(">=0.1 <0.2", ">=0.2 <0.3"));
        using var host = env.NewHost();
        host.Discover();

        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Incompatible, m.Lifecycle);
        Assert.False(m.HasLoadedCode); // no load context, so not even the constructor (which would throw) ran
        Assert.Equal(BayState.Unsupported, m.Bay.State);
        var reason = m.ResolveReason(m.Bay.Reason!);
        Assert.Contains(">=0.2 <0.3", reason);
        Assert.Contains("0.1", reason);
        Assert.False(m.Enable());
        Assert.Equal(ModuleLifecycle.Incompatible, m.Lifecycle);
    }

    [Fact]
    public void A_module_that_needs_a_newer_revision_of_this_minor_is_refused_with_the_revision_named()
    {
        var needed = $"0.1.{HostApi.Revision + 1}";
        using var env = new TestEnv();
        env.AddModule("faulty", behavior: "ctor-throws",
            editManifest: json => json.Replace(">=0.1 <0.2", $">={needed} <0.2"));
        using var host = env.NewHost();
        host.Discover();

        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Incompatible, m.Lifecycle);
        Assert.False(m.HasLoadedCode);
        Assert.Equal(BayState.Unsupported, m.Bay.State);
        var reason = m.ResolveReason(m.Bay.Reason!);
        Assert.Equal($"Needs host API {needed} or newer, this host is API {HostApi.Current}", reason);
        host.Languages.SetLanguage("vi");
        Assert.Equal($"Cần API máy chủ {needed} trở lên, bản này là API {HostApi.Current}", m.ResolveReason(m.Bay.Reason!));
        Assert.False(m.Enable());
    }

    [Fact]
    public void A_module_that_needs_exactly_this_revision_still_loads()
    {
        using var env = new TestEnv();
        env.AddModule("sample", editManifest: json => System.Text.RegularExpressions.Regex.Replace(json, @"""hostApi"":\s*""[^""]*""", $"\"hostApi\": \">=0.1.{HostApi.Revision} <0.2\""));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Off, m.Lifecycle);
        Assert.Equal($">=0.1.{HostApi.Revision} <0.2", m.Manifest!.HostApi.Text);
    }

    [Fact]
    public void An_invalid_manifest_shows_every_problem_and_loads_nothing()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", editManifest: json => json.Replace("\"version\": \"0.1.0\"", "\"version\": \"nope\"").Replace("\"group\": \"other\"", "\"group\": \"toys\""));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Invalid, m.Lifecycle);
        Assert.False(m.HasLoadedCode);
        Assert.Equal(BayState.Failed, m.Bay.State);
        var text = m.ResolveReason(m.Bay.Reason!);
        Assert.Contains("version", text);
        Assert.Contains("group", text);
        Assert.Equal("faulty", m.Id); // falls back to the folder name
    }

    [Fact]
    public void A_folder_without_a_manifest_or_with_a_missing_dll_is_listed_as_failed()
    {
        using var env = new TestEnv();
        Directory.CreateDirectory(Path.Combine(env.ModulesRoot, "empty"));
        var noDll = env.AddModule("sample");
        File.Delete(Path.Combine(noDll, "Rigbay.SampleModule.dll"));
        using var host = env.NewHost();
        host.Discover();
        Assert.Equal(2, host.Modules.Count);
        Assert.All(host.Modules, m => Assert.Equal(BayState.Failed, m.Bay.State));
    }

    [Fact]
    public void A_wrong_entry_type_is_a_load_failure_with_the_reason()
    {
        using var env = new TestEnv();
        env.AddModule("sample", editManifest: json => json.Replace("Rigbay.SampleModule.SampleModule", "Rigbay.SampleModule.DoesNotExist"));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        Assert.Contains("DoesNotExist", m.ResolveReason(m.Reason!));
    }

    [Fact]
    public void A_module_that_probes_unsupported_is_listed_with_its_reason_and_cannot_be_enabled()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", behavior: "probe-unsupported");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Unsupported, m.Lifecycle);
        Assert.Equal(BayState.Unsupported, m.Bay.State);
        Assert.Equal(new ModuleReason("probe.nope", Array.Empty<object>(), "faulty"), m.Bay.Reason);
        Assert.Equal("module.faulty.probe.nope", m.ResolveReason(m.Bay.Reason!)); // no text in any language file: the key shows
        Assert.False(m.Enable());
    }

    [Fact]
    public void Module_language_files_are_loaded_before_any_code_and_localise_the_manifest_text()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        using var host = env.NewHost();
        host.Discover();
        host.SetLanguage("vi");
        var m = host.Modules.Single();
        Assert.Equal("Module mẫu", m.DisplayName);
        Assert.Contains("Không thay đổi", m.DisplayDescription);
        host.SetLanguage("en");
        Assert.Equal("Sample module", m.DisplayName);
    }

    [Fact]
    public void Enabled_modules_start_again_on_the_next_host_start_and_disabled_ones_do_not()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        env.AddModule("faulty");
        using (var first = env.NewHost())
        {
            first.Discover();
            first.Find("sample")!.Enable();
            first.Find("faulty")!.Enable();
            first.Find("faulty")!.Disable();
            first.Shutdown(); // exit: keeps the choice
        }
        using var second = env.NewHost();
        second.Discover();
        second.StartEnabled();
        Assert.Equal(ModuleLifecycle.Running, second.Find("sample")!.Lifecycle);
        Assert.Equal(ModuleLifecycle.Off, second.Find("faulty")!.Lifecycle);
    }

    [Fact]
    public void Updates_and_restarts_show_in_the_bay_state_without_hiding_failures()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        m.SetPending(PendingChange.UpdateAvailable, "0.2.0");
        Assert.Equal(BayState.UpdateAvailable, m.Bay.State);
        Assert.Contains("0.2.0", m.ResolveReason(m.Bay.Reason!));
        m.SetPending(PendingChange.RestartRequired);
        Assert.Equal(BayState.RestartRequired, m.Bay.State);
        m.Enable();
        Assert.Equal(BayState.RestartRequired, m.Bay.State);
        m.FaultNow("test", new InvalidOperationException("x"));
        Assert.Equal(BayState.Failed, m.Bay.State);
    }

    [Fact]
    public void A_missing_modules_folder_is_not_an_error()
    {
        using var env = new TestEnv();
        Directory.Delete(env.ModulesRoot);
        using var host = env.NewHost();
        host.Discover();
        Assert.Empty(host.Modules);
    }

    [Fact]
    public void The_first_start_creates_a_missing_modules_folder_and_logs_no_warning()
    {
        using var env = new TestEnv();
        Directory.Delete(env.ModulesRoot);
        using var host = env.NewHost();
        host.Discover();
        Assert.True(Directory.Exists(env.ModulesRoot));
        var entries = host.Log.Recent();
        Assert.DoesNotContain(entries, e => e.Level >= LogLevel.Warn);
        Assert.Single(entries, e => e.Message.Contains("modules folder", StringComparison.Ordinal));
    }

    [Fact]
    public void The_missing_modules_folder_is_made_through_the_admin_only_security_and_a_refusal_leaves_no_folder_behind()
    {
        using var env = new TestEnv();
        Directory.Delete(env.ModulesRoot);
        var security = new RecordingSecurity();
        using (var host = new RigbayHost(new RigbayHostOptions { ModulesRoot = env.ModulesRoot, DataRoot = env.DataRoot, FolderSecurity = security }))
            host.Discover();
        Assert.Equal(new[] { env.ModulesRoot }, security.Paths);
        Assert.True(Directory.Exists(env.ModulesRoot));

        Directory.Delete(env.ModulesRoot);
        security.Refuse = true;
        using var refused = new RigbayHost(new RigbayHostOptions { ModulesRoot = env.ModulesRoot, DataRoot = env.DataRoot, FolderSecurity = security });
        refused.Discover();
        Assert.False(Directory.Exists(env.ModulesRoot));   // not left behind: the host would load modules from a folder nobody secured
        Assert.DoesNotContain(refused.Log.Recent(), e => e.Level >= LogLevel.Warn);
    }

    sealed class RecordingSecurity : Rigbay.Core.Install.IFolderSecurity
    {
        public List<string> Paths { get; } = new();
        public bool Refuse { get; set; }
        public void EnsureAdminOnly(string path)
        {
            Paths.Add(path);
            Directory.CreateDirectory(path);
            if (Refuse) throw new Rigbay.Core.Install.InstallException(Rigbay.Core.Install.InstallErrorCode.FolderSecurity, "not owned by Administrators");
        }
    }

    [Fact]
    public void A_modules_folder_that_cannot_be_created_is_one_info_line_with_what_to_do_and_no_warning()
    {
        using var env = new TestEnv();
        Directory.Delete(env.ModulesRoot);
        // a file where the folder's parent should be: CreateDirectory cannot succeed whoever runs the test
        var blocker = Path.Combine(env.Root, "blocker");
        File.WriteAllText(blocker, "x");
        var impossible = Path.Combine(blocker, "modules");
        using var host = new RigbayHost(new RigbayHostOptions { ModulesRoot = impossible, DataRoot = env.DataRoot, FolderSecurity = new Rigbay.Core.Install.NoFolderSecurity() });
        host.Discover();
        Assert.Empty(host.Modules);
        var entries = host.Log.Recent();
        Assert.DoesNotContain(entries, e => e.Level >= LogLevel.Warn);
        var line = Assert.Single(entries, e => e.Message.Contains("could not be created", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Info, line.Level);
        Assert.Contains("create it", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sample_page_is_valid_and_has_one_row_of_every_kind()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var page = host.Find("sample")!.Page!;
        Assert.Empty(PageValidator.Validate(page));
        var kinds = page.EnumerateRows().Select(r => r.GetType()).Distinct().ToHashSet();
        foreach (var kind in new[] { typeof(ToggleRow), typeof(ChoiceRow), typeof(NumberRow), typeof(TextRow), typeof(ShortcutRow),
                     typeof(ButtonRow), typeof(ValueRow), typeof(ReadingRow), typeof(ExpanderRow), typeof(InfoRow) })
            Assert.Contains(kind, kinds);
        Assert.Equal(2, page.Groups.Count(g => g.Rows.Count > 0) - 1);
        Assert.NotNull(page.Summary);
        host.Shutdown();
    }

    [Fact]
    public void The_sample_registers_a_hotkey_a_tray_item_commands_and_publishes_metrics()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        Assert.Single(host.Hotkeys.Entries);
        Assert.Equal("Ctrl+Alt+F11", host.Hotkeys.Entries[0].Gesture.Value.ToString());
        Assert.Equal("sample", host.Tray.Items.Single().ModuleId);
        Assert.Equal(new[] { "echo", "status" }, host.Commands.VerbsOf("sample").Select(v => v.Verb).ToArray());

        var deadline = DateTime.UtcNow.AddSeconds(30);
        MetricSample sample = default!;
        while (!host.Metrics.TryGetLatest(new MetricId("sample.ticks"), out sample) && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.Equal("sample", sample.SourceModule);
        Assert.Equal(MetricUnit.Count, sample.Unit);

        // disabling removes everything the module registered
        host.Find("sample")!.Disable();
        Assert.Empty(host.Hotkeys.Entries);
        Assert.Empty(host.Tray.Items);
        Assert.Empty(host.Commands.Entries);
    }

    [Fact]
    public void Enabling_again_after_disabling_works_on_the_same_instance()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var m = host.Find("sample")!;
        for (int i = 0; i < 3; i++)
        {
            Assert.True(m.Disable());
            Assert.True(m.Enable());
            Assert.Equal(ModuleLifecycle.Running, m.Lifecycle);
            Assert.Single(host.Hotkeys.Entries);
        }
        host.Shutdown();
    }

    [Fact]
    public void Settings_of_the_sample_are_stored_under_the_modules_data_folder()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var toggle = host.Find("sample")!.Page!.EnumerateRows().OfType<ToggleRow>().First();
        toggle.Value.Value = true;
        var file = Path.Combine(env.DataRoot, "modules", "sample", "settings.json");
        Assert.True(File.Exists(file));
        Assert.Contains("\"echo\": true", File.ReadAllText(file));
        host.Shutdown();
    }
}

public class FaultBoundaryTests
{
    private static string Reason(ModuleEntry m) => m.ResolveReason(m.Reason!);

    [Theory]
    [InlineData("ctor-throws", "constructor blew up")]
    [InlineData("probe-throws", "probe blew up")]
    [InlineData("probe-null", "returned null")]
    public void A_module_that_throws_while_loading_or_probing_is_failed_and_the_host_survives(string behavior, string text)
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        env.AddModule("faulty", behavior);
        using var host = env.NewHost();
        var faults = new List<string>();
        host.ModuleFaulted += m => faults.Add(m.Id);

        host.Discover();

        var faulty = host.Find("faulty")!;
        Assert.Equal(ModuleLifecycle.Faulted, faulty.Lifecycle);
        Assert.Equal(BayState.Failed, faulty.Bay.State);
        Assert.Contains(text, Reason(faulty) + faulty.Fault?.Message);
        Assert.False(faulty.Enable());
        // the other module is untouched and works
        Assert.True(host.Find("sample")!.Enable());
        host.Shutdown();
    }

    [Fact]
    public void A_module_that_throws_in_Initialize_is_disabled_logged_and_cleaned_up()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "init-throws");
        using var host = env.NewHost();
        var faulted = new List<ModuleEntry>();
        host.ModuleFaulted += faulted.Add;
        host.Discover();
        var m = host.Modules.Single();

        Assert.False(m.Enable());

        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        Assert.Contains("Initialize", Reason(m));
        Assert.Contains("initialize blew up", Reason(m));
        Assert.IsType<InvalidOperationException>(m.Fault);
        Assert.Same(m, Assert.Single(faulted));
        Assert.Null(m.Page);
        Assert.Contains(host.Log.Recent(), e => e.Level == LogLevel.Error && e.Source == "faulty" && e.Exception!.Contains("initialize blew up"));
        Assert.Empty(host.Tray.Items);
        Assert.False(host.IsEnabledByUser("faulty"));
    }

    [Fact]
    public void A_module_that_throws_in_GetPageModel_is_disabled()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "page-throws");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.False(m.Enable());
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        Assert.Contains("GetPageModel", Reason(m));
        Assert.Empty(host.Hotkeys.Entries);
        Assert.Empty(host.Commands.Entries); // what Initialize had registered is removed
    }

    [Fact]
    public void A_structurally_invalid_page_is_a_fault_with_the_reason()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "bad-page");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.False(m.Enable());
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        Assert.Contains("Minimum is above Maximum", Reason(m));
    }

    [Fact]
    public async Task A_module_that_throws_in_a_value_callback_is_disabled_and_the_host_survives()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        env.AddModule("faulty", "toggle-throws");
        var host = env.StartHost();
        var faulty = host.Find("faulty")!;
        var toggle = faulty.Page!.EnumerateRows().OfType<ToggleRow>().Single();

        var result = await RowEditors.For(faulty, toggle).SubmitAsync(true);

        Assert.Equal(EditOutcome.Faulted, result.Outcome);
        Assert.Equal(ModuleLifecycle.Faulted, faulty.Lifecycle);
        Assert.Contains("toggle handler blew up", Reason(faulty));
        Assert.Equal(ModuleLifecycle.Running, host.Find("sample")!.Lifecycle);
        // later UI calls into the faulted module do nothing and do not throw
        Assert.False(faulty.Invoke("again", () => throw new Exception("must not run")));
        host.Shutdown();
    }

    [Fact]
    public async Task A_module_that_throws_in_a_button_is_disabled_and_the_user_gets_a_failed_result()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "button-throws");
        var host = env.StartHost();
        var m = host.Modules.Single();
        var button = m.Page!.EnumerateRows().OfType<ButtonRow>().Single();

        var result = await RowEditors.InvokeButtonAsync(m, button);

        Assert.False(result.Succeeded);
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        Assert.Contains("button blew up", Reason(m));
    }

    [Fact]
    public void A_module_that_throws_in_a_hotkey_is_disabled_and_the_press_does_not_throw()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "hotkey-throws");
        var host = env.StartHost();
        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Running, m.Lifecycle);

        var ran = host.Hotkeys.Press(HotkeyGesture.Parse("Ctrl+Alt+F9"));

        Assert.Equal(1, ran);
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        Assert.Empty(host.Hotkeys.Entries);
    }

    [Fact]
    public async Task A_module_that_throws_in_a_command_gives_exit_code_1_and_is_disabled()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "command-throws");
        var host = env.StartHost();
        var m = host.Modules.Single();

        var outcome = await host.ExecuteCommandAsync(new[] { "faulty", "boom" });

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("command blew up", outcome.Output);
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        var again = await host.ExecuteCommandAsync(new[] { "faulty", "ok" });
        Assert.Equal(2, again.ExitCode); // not running any more
    }

    [Fact]
    public void A_module_whose_metrics_subscriber_throws_is_disabled_and_publishers_are_not_affected()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        env.AddModule("faulty", "metric-handler-throws");
        var host = env.StartHost();
        var received = new List<MetricSample>();
        host.Metrics.Subscribe(received.Add);

        host.Metrics.Publish("test", new MetricId("cpu.temp"), 50, MetricUnit.Celsius);

        Assert.Equal(ModuleLifecycle.Faulted, host.Find("faulty")!.Lifecycle);
        Assert.Contains(received, s => s.Value == 50);
        Assert.Equal(ModuleLifecycle.Running, host.Find("sample")!.Lifecycle);
        host.Shutdown();
    }

    [Fact]
    public void A_hanging_Initialize_times_out_and_the_host_goes_on()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "init-hangs");
        using var host = env.NewHost(timeout: TimeSpan.FromMilliseconds(300), probeTimeout: TimeSpan.FromSeconds(30));   // only Initialize is short
        host.Discover();
        var m = host.Modules.Single();

        var started = DateTime.UtcNow;
        Assert.False(m.Enable());

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        Assert.IsType<TimeoutException>(m.Fault);
        Assert.Contains("Initialize", Reason(m));
        Assert.Contains("did not answer", Reason(m));
    }

    [Fact]
    public void A_hanging_Probe_times_out()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "probe-hangs");
        using var host = env.NewHost(timeout: TimeSpan.FromMilliseconds(300));
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        Assert.IsType<TimeoutException>(m.Fault);
    }

    [Fact]
    public void Disabling_calls_Shutdown_and_a_fault_after_Initialize_also_tries_it_once()
    {
        using var env = new TestEnv();
        var folder = env.AddModule("faulty", "none");
        var host = env.StartHost();
        host.Modules.Single().Disable();
        Assert.True(File.Exists(Path.Combine(folder, "shutdown.marker")));
        File.Delete(Path.Combine(folder, "shutdown.marker"));

        var m = host.Modules.Single();
        m.Enable();
        m.FaultNow("test", new InvalidOperationException("later failure"));
        SpinWait.SpinUntil(() => File.Exists(Path.Combine(folder, "shutdown.marker")), TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(Path.Combine(folder, "shutdown.marker")));
    }

    [Fact]
    public void A_fault_is_idempotent_and_keeps_the_first_cause()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var m = host.Find("sample")!;
        int events = 0;
        host.ModuleFaulted += _ => events++;
        m.FaultNow("first", new InvalidOperationException("one"));
        m.FaultNow("second", new InvalidOperationException("two"));
        Assert.Equal(1, events);
        Assert.Equal("one", m.Fault!.Message);
    }

    [Fact]
    public async Task Cancellation_because_the_module_was_stopped_is_not_a_fault()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var m = host.Find("sample")!;

        // work that is still running when the user disables the module, and ends with the cancellation the stop token asked for
        var release = new TaskCompletionSource();
        var pending = m.InvokeAsync<int>("slow work", async () =>
        {
            await release.Task;
            m.StopToken.ThrowIfCancellationRequested();
            return 1;
        });
        Assert.True(m.Disable());
        release.SetResult();

        var (completed, _) = await pending;
        Assert.False(completed);
        Assert.Equal(ModuleLifecycle.Off, m.Lifecycle);
        Assert.Null(m.Fault);
    }

    [Fact]
    public void A_stray_cancellation_while_running_is_an_ordinary_bug_and_faults()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var m = host.Find("sample")!;
        Assert.False(m.Invoke("stray", () => throw new OperationCanceledException()));
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        host.Shutdown();
    }
}

public class CommandTests
{
    [Fact]
    public async Task Exit_codes_are_0_for_ok_1_for_failed_and_2_for_not_found_or_bad_usage()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        env.AddModule("faulty", "none");
        var host = env.StartHost();

        var ok = await host.ExecuteCommandAsync(new[] { "faulty", "ok" });
        Assert.Equal((0, "fine"), (ok.ExitCode, ok.Output));

        var failed = await host.ExecuteCommandAsync(new[] { "faulty", "fail" });
        Assert.Equal((1, "expected failure"), (failed.ExitCode, failed.Output));
        Assert.Equal(ModuleLifecycle.Running, host.Find("faulty")!.Lifecycle); // an expected failure is not a fault

        var usage = await host.ExecuteCommandAsync(new[] { "faulty", "usage" });
        Assert.Equal(2, usage.ExitCode);

        Assert.Equal(2, (await host.ExecuteCommandAsync(new[] { "nosuchmodule", "x" })).ExitCode);
        Assert.Equal(2, (await host.ExecuteCommandAsync(new[] { "faulty", "nosuchverb" })).ExitCode);
        Assert.Equal(2, (await host.ExecuteCommandAsync(new[] { "faulty" })).ExitCode);
        Assert.Equal(2, (await host.ExecuteCommandAsync(Array.Empty<string>())).ExitCode);
        host.Shutdown();
    }

    [Fact]
    public async Task Arguments_reach_the_handler_and_help_and_list_work()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");

        var echo = await host.ExecuteCommandAsync(new[] { "sample", "echo", "hello", "big world" });
        Assert.Equal((0, "hello big world"), (echo.ExitCode, echo.Output));

        var noArgs = await host.ExecuteCommandAsync(new[] { "sample", "echo" });
        Assert.Equal(2, noArgs.ExitCode);
        Assert.Contains("Usage: rigbay sample echo", noArgs.Output);

        var status = await host.ExecuteCommandAsync(new[] { "sample", "status" });
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("mode=normal", status.Output);

        var help = await host.ExecuteCommandAsync(new[] { "help" });
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("sample", help.Output);
        var list = await host.ExecuteCommandAsync(new[] { "list" });
        Assert.Contains("sample  Ready", list.Output);

        var verbs = await host.ExecuteCommandAsync(new[] { "sample" });
        Assert.Equal(2, verbs.ExitCode);
        Assert.Contains("sample echo  Print the arguments", verbs.Output);
        host.Shutdown();
    }

    [Fact]
    public async Task A_module_that_is_not_running_cannot_be_called()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        using var host = env.NewHost();
        host.Discover();
        var outcome = await host.ExecuteCommandAsync(new[] { "sample", "status" });
        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("not running", outcome.Output);
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("1abc")]
    [InlineData("a b")]
    [InlineData("")]
    public void Verbs_have_a_fixed_form(string verb)
    {
        var registry = new CommandRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register("m", verb, "k", _ => Task.FromResult(CommandResult.Ok())));
    }

    [Fact]
    public async Task Handler_exit_codes_outside_0_to_2_become_1_and_cancellation_is_passed_on()
    {
        var registry = new CommandRegistry();
        CancellationToken seen = default;
        registry.Register("m", "weird", "k", _ => Task.FromResult(new CommandResult(7, "odd")));
        registry.Register("m", "token", "k", ctx => { seen = ctx.CancellationToken; return Task.FromResult(CommandResult.Ok()); });
        using var cts = new CancellationTokenSource();
        Assert.Equal(1, (await CommandRegistry.RunAsync(registry.Find("m", "weird")!, Array.Empty<string>(), default)).ExitCode);
        await CommandRegistry.RunAsync(registry.Find("m", "token")!, Array.Empty<string>(), cts.Token);
        Assert.Equal(cts.Token, seen);
    }
}

public class EditorTests
{
    private static (RigbayHost Host, ModuleEntry Module) Start(TestEnv env)
    {
        var host = env.StartHost("sample");
        return (host, host.Find("sample")!);
    }

    [Fact]
    public async Task An_immediate_toggle_is_applied_at_once_and_can_be_reverted()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var row = m.Page!.EnumerateRows().OfType<ToggleRow>().First();
        var editor = RowEditors.For(m, row);

        var result = await editor.SubmitAsync(true);

        Assert.Equal(EditOutcome.Applied, result.Outcome);
        Assert.True(row.Value.Value);
        Assert.True(editor.CanRevert);
        Assert.Equal(EditOutcome.Applied, editor.Revert().Outcome);
        Assert.False(row.Value.Value);
        Assert.False(editor.CanRevert);
        Assert.Equal(EditOutcome.NothingToDo, (await editor.SubmitAsync(false)).Outcome);
        host.Shutdown();
    }

    [Fact]
    public async Task A_choice_must_be_one_of_the_options()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var row = m.Page!.EnumerateRows().OfType<ChoiceRow>().Single(r => r.HeaderKey == "row.mode.header");
        var editor = RowEditors.For(m, row);
        Assert.Equal(EditOutcome.Invalid, (await editor.SubmitAsync("warp")).Outcome);
        Assert.Equal("normal", row.Value.Value);
        Assert.Equal(EditOutcome.Applied, (await editor.SubmitAsync("quiet")).Outcome);
        Assert.Equal("quiet", row.Value.Value);
        host.Shutdown();
    }

    [Fact]
    public async Task A_text_row_in_apply_revert_mode_changes_nothing_until_applied_and_uses_the_modules_validation()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var row = m.Page!.EnumerateRows().OfType<TextRow>().Single();
        var editor = RowEditors.For(m, row);
        Assert.Equal(ApplyMode.ApplyRevert, editor.Mode);

        editor.SetDraft("kitchen");
        Assert.True(editor.IsDirty);
        Assert.Equal("", row.Value.Value); // the module has not seen it
        editor.Discard();
        Assert.False(editor.IsDirty);
        Assert.Equal("", editor.Draft);

        editor.SetDraft("RESERVED");
        var invalid = await editor.ApplyAsync();
        Assert.Equal(EditOutcome.Invalid, invalid.Outcome);
        Assert.Equal("'reserved' is not allowed", invalid.Message);
        Assert.Equal("", row.Value.Value);

        editor.SetDraft(new string('x', 21));
        var tooLong = await editor.ApplyAsync();
        Assert.Equal(EditOutcome.Invalid, tooLong.Outcome);
        Assert.Contains("20", tooLong.Message);

        editor.SetDraft("kitchen");
        Assert.Equal(EditOutcome.Applied, (await editor.ApplyAsync()).Outcome);
        Assert.Equal("kitchen", row.Value.Value);
        Assert.False(editor.IsDirty);
        Assert.True(editor.CanRevert);

        Assert.Equal(EditOutcome.Applied, editor.Revert().Outcome);
        Assert.Equal("", row.Value.Value);
        host.Shutdown();
    }

    [Fact]
    public async Task A_hardware_number_needs_a_confirmation_and_stays_a_draft_when_it_is_refused()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var prompt = new FakeApproval { Answer = false };
        host.ApprovalPrompt = prompt;
        var row = m.Page!.EnumerateRows().OfType<NumberRow>().Single();
        Assert.True(row.ChangesHardware);
        var editor = RowEditors.For(m, row);

        editor.SetDraft(55);
        var refused = await editor.ApplyAsync();

        Assert.Equal(EditOutcome.Declined, refused.Outcome);
        Assert.Equal(45, row.Value.Value);
        Assert.True(editor.IsDirty);
        Assert.Equal(55, editor.Draft);
        var request = Assert.Single(prompt.Requests);
        Assert.Equal("sample", request.ModuleId);
        Assert.Equal("Pretends to set a power limit. Nothing on this machine changes.", request.EffectText);
        Assert.True(request.Revertible);

        prompt.Answer = true;
        Assert.Equal(EditOutcome.Applied, (await editor.ApplyAsync()).Outcome);
        Assert.Equal(55, row.Value.Value);
        Assert.Equal(2, prompt.Requests.Count);

        // a revert is a hardware write too: the synchronous one cannot ask, so it refuses; RevertAsync asks (see HostGapTests)
        Assert.Equal(EditOutcome.Declined, editor.Revert().Outcome);
        Assert.Equal(55, row.Value.Value);
        Assert.Equal(EditOutcome.Applied, (await editor.RevertAsync()).Outcome);
        Assert.Equal(45, row.Value.Value);
        Assert.Equal(3, prompt.Requests.Count);
        Assert.True(prompt.Requests[2].IsRevert);
        Assert.Equal("Limit: 55 → 45", prompt.Requests[2].Detail);
        host.Shutdown();
    }

    [Fact]
    public async Task Without_a_way_to_ask_a_hardware_change_is_refused()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var row = m.Page!.EnumerateRows().OfType<NumberRow>().Single();
        var result = await RowEditors.For(m, row).SubmitAsync(50);
        Assert.Equal(EditOutcome.Declined, result.Outcome);
        Assert.Contains("cannot ask for confirmation", result.Message);
        Assert.Equal(45, row.Value.Value);
        host.Shutdown();
    }

    [Theory]
    [InlineData(14)]
    [InlineData(66)]
    [InlineData(double.NaN)]
    public async Task A_number_outside_the_rows_range_is_refused_before_anything_is_asked(double value)
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var prompt = new FakeApproval();
        host.ApprovalPrompt = prompt;
        var row = m.Page!.EnumerateRows().OfType<NumberRow>().Single();
        var result = await RowEditors.For(m, row).SubmitAsync(value);
        Assert.Equal(EditOutcome.Invalid, result.Outcome);
        Assert.Contains("15", result.Message);
        Assert.Empty(prompt.Requests);
        host.Shutdown();
    }

    [Fact]
    public async Task The_draft_follows_the_module_until_the_user_edits_it()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var row = m.Page!.EnumerateRows().OfType<TextRow>().Single();
        var editor = RowEditors.For(m, row);

        row.Value.Value = "from the module";
        Assert.Equal("from the module", editor.Draft);
        Assert.False(editor.IsDirty);

        editor.SetDraft("user typing");
        row.Value.Value = "module changed again";
        Assert.Equal("user typing", editor.Draft);
        Assert.True(editor.IsDirty);
        await Task.CompletedTask;
        host.Shutdown();
    }

    [Fact]
    public async Task A_button_runs_in_the_boundary_and_resets_the_sample()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var toasts = new List<(string Module, string Title, string Body)>();
        host.ToastSink = new ToastRecorder(toasts);
        var rows = m.Page!.EnumerateRows();
        rows.OfType<ToggleRow>().First().Value.Value = true;

        var result = await RowEditors.InvokeButtonAsync(m, rows.OfType<ButtonRow>().Single(b => b.HeaderKey == "row.reset.header"));

        Assert.True(result.Succeeded);
        Assert.False(rows.OfType<ToggleRow>().First().Value.Value);
        Assert.Equal(("sample", "Sample module", "Settings were reset"), Assert.Single(toasts));
        host.Shutdown();
    }

    [Fact]
    public async Task RunOnUi_uses_the_hosts_dispatcher_and_the_actions_run_inside_the_boundary()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        using var host = env.NewHost();
        var queued = new List<Action>();
        host.UiDispatcher = queued.Add;
        host.Discover();
        var m = host.Modules.Single();
        m.Enable();

        // the services object is internal to the module; reach RunOnUi through the guard the host gives every callback
        var guarded = m.Guard("ui action", () => throw new InvalidOperationException("raw dispatcher callbacks would kill the process"));
        host.UiDispatcher!(guarded);
        foreach (var a in queued.ToArray()) a(); // the UI thread runs it: no exception escapes
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
        await Task.CompletedTask;
    }

    private sealed class ToastRecorder(List<(string, string, string)> list) : IToastSink
    {
        public void Show(string moduleId, string title, string body, ToastKind kind) => list.Add((moduleId, title, body));
    }
}

public class PageValidatorTests
{
    private static PageModel Page(params Row[] rows) => new() { Groups = new[] { new RowGroup("g", rows) } };

    [Fact]
    public void A_good_page_has_no_problems()
    {
        Assert.Empty(PageValidator.Validate(Page(
            new ToggleRow("t", new ObservableValue<bool>(false)),
            new ChoiceRow("c", new[] { new ChoiceOption("a", "a") }, new ObservableValue<string>("a")),
            new NumberRow("n", new ObservableValue<double>(1), 0, 2),
            new ReadingRow("r", new MetricId("cpu.temp")),
            new CustomRow("graph"))));
    }

    [Fact]
    public void Impossible_rows_are_found()
    {
        Assert.Contains(PageValidator.Validate(Page(new ChoiceRow("c", Array.Empty<ChoiceOption>(), new ObservableValue<string>("")))), p => p.Contains("no options"));
        Assert.Contains(PageValidator.Validate(Page(new ChoiceRow("c", new[] { new ChoiceOption("a", "x"), new ChoiceOption("a", "y") }, new ObservableValue<string>("a")))), p => p.Contains("duplicate"));
        Assert.Contains(PageValidator.Validate(Page(new NumberRow("n", new ObservableValue<double>(1), 5, 1))), p => p.Contains("Minimum"));
        Assert.Contains(PageValidator.Validate(Page(new NumberRow("n", new ObservableValue<double>(1), 0, 1, Step: 0))), p => p.Contains("Step"));
        Assert.Contains(PageValidator.Validate(Page(new ToggleRow("", new ObservableValue<bool>(false)))), p => p.Contains("HeaderKey"));
        Assert.Contains(PageValidator.Validate(Page(new ToggleRow("t", null!))), p => p.Contains("Value is null"));
        Assert.Contains(PageValidator.Validate(Page(new CustomRow(""))), p => p.Contains("ControlId"));
        Assert.Contains(PageValidator.Validate(Page(new ReadingRow("r", default))), p => p.Contains("no metric"));
        Assert.Contains(PageValidator.Validate(Page(new ToggleRow("t", new ObservableValue<bool>(false)) { HardwareEffect = new HardwareEffect("") })), p => p.Contains("DescriptionKey"));
        Assert.Contains(PageValidator.Validate(Page(new ExpanderRow("e", new Row[] { new NumberRow("n", new ObservableValue<double>(1), 5, 1) }))), p => p.Contains("Minimum"));
        Assert.Contains(PageValidator.Validate(new PageModel { Links = new[] { new PageLink("l", "javascript:alert(1)") } }), p => p.Contains("http"));
    }

    [Fact]
    public void Rows_are_enumerated_depth_first_including_expander_children()
    {
        var inner = new ValueRow("inner", new ObservableValue<string>(""));
        var page = Page(new ToggleRow("a", new ObservableValue<bool>(false)), new ExpanderRow("e", new Row[] { inner }), new CustomRow("z"));
        Assert.Equal(new[] { "a", "e", "inner", "" }, page.EnumerateRows().Select(r => r.HeaderKey).ToArray());
    }

    [Fact]
    public void A_row_that_changes_hardware_says_so()
    {
        var plain = new ToggleRow("t", new ObservableValue<bool>(false));
        var risky = plain with { HardwareEffect = new HardwareEffect("effect.x") };
        Assert.False(plain.ChangesHardware);
        Assert.True(risky.ChangesHardware);
    }

    [Fact]
    public async Task Observable_values_raise_Changed_only_for_a_different_value_on_the_setters_thread()
    {
        var v = new ObservableValue<int>(1);
        int calls = 0;
        int? thread = null;
        v.Changed += () => { calls++; thread = Environment.CurrentManagedThreadId; };
        v.Value = 1;
        Assert.Equal(0, calls);
        int setter = -1;
        await Task.Run(() => { setter = Environment.CurrentManagedThreadId; v.Value = 2; });
        Assert.Equal(1, calls);
        Assert.Equal(setter, thread); // the thread rule from the contract
        Assert.Equal(2, v.Value);
    }
}
