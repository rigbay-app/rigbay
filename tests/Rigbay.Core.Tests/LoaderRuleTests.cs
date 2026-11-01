using System.Text.Json;
using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>A6: the module load rule (host, then module folder, then probe paths) and the <c>probePaths</c> manifest field.</summary>
public class LoaderRuleTests
{
    private const string SystemDepDll = "System.Rigbay.OnlyHere.dll";

    private static string Manifest(string probePathsJson) =>
        $$"""
        {
          "manifestVersion": 1, "id": "pp", "name": "P", "version": "1.0.0", "author": "a", "description": "d",
          "entry": { "assembly": "P.dll", "type": "P.M" }, "hostApi": ">=0.1 <0.2", "group": "other"{{probePathsJson}}
        }
        """;

    private static string Row(ModuleEntry m, string key) =>
        m.Page!.EnumerateRows().OfType<ValueRow>().Single(r => r.HeaderKey == key).Value.Value?.ToString() ?? "";

    private static bool LoggedAtDebug(RigbayHost host, string text) =>
        host.Log.Recent().Any(e => e.Level == LogLevel.Debug && e.Message.Contains(text, StringComparison.Ordinal));

    // ---------------------------------------------------------------- 1. the rule

    [Fact]
    public void A_system_assembly_the_host_lacks_is_loaded_from_the_module_folder()
    {
        using var env = new TestEnv();
        var folder = env.AddModule("faulty", behavior: "uses-system-dependency");
        Assert.True(File.Exists(Path.Combine(folder, SystemDepDll)));
        Assert.DoesNotContain(System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "System.Rigbay.OnlyHere");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable(), m.Reason is null ? "" : m.ResolveReason(m.Reason));
        Assert.Equal("hello from a System.* assembly the host does not have", Row(m, "row.sysdep"));
        Assert.DoesNotContain(System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "System.Rigbay.OnlyHere");
        Assert.True(LoggedAtDebug(host, "System.Rigbay.OnlyHere"), "the winner should be logged at debug level");
        Assert.True(LoggedAtDebug(host, "module copy"));
    }

    [Fact]
    public void A_module_copy_of_an_assembly_the_host_has_is_ignored()
    {
        using var env = new TestEnv();
        var folder = env.AddModule("faulty", behavior: "json-context");
        File.Copy(typeof(JsonSerializer).Assembly.Location, Path.Combine(folder, "System.Text.Json.dll"), overwrite: true);
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable(), m.Reason is null ? "" : m.ResolveReason(m.Reason));
        Assert.Equal("Default", Row(m, "row.json"));
        Assert.True(LoggedAtDebug(host, "System.Text.Json"));
        Assert.True(LoggedAtDebug(host, "host copy"));
    }

    [Fact]
    public void Without_the_dll_anywhere_the_load_fails_and_the_module_faults()
    {
        using var env = new TestEnv();
        var folder = env.AddModule("faulty", behavior: "uses-system-dependency");
        File.Delete(Path.Combine(folder, SystemDepDll));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.False(m.Enable());
        Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
    }

    // ---------------------------------------------------------------- 2. probePaths

    [Fact]
    public void A_probe_path_supplies_an_assembly_that_is_neither_in_the_host_nor_in_the_module_folder()
    {
        using var env = new TestEnv();
        var outside = Path.Combine(env.Root, "outside");
        Directory.CreateDirectory(outside);
        Environment.SetEnvironmentVariable("RIGBAY_TEST_PROBE", outside);
        var folder = env.AddModule("faulty", behavior: "uses-system-dependency",
            editManifest: j => j.Replace("\"group\": \"other\"", "\"group\": \"other\", \"probePaths\": [\"%RIGBAY_TEST_PROBE%\"]"));
        File.Move(Path.Combine(folder, SystemDepDll), Path.Combine(outside, SystemDepDll));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(new[] { "%RIGBAY_TEST_PROBE%" }, m.Manifest!.DeclaredProbePaths);
        Assert.Equal(new[] { outside }, m.Manifest.ResolvedProbePaths);
        Assert.True(m.Manifest.ReadsOutsideItsFolder);
        Assert.True(m.Enable(), m.Reason is null ? "" : m.ResolveReason(m.Reason));
        Assert.Equal("hello from a System.* assembly the host does not have", Row(m, "row.sysdep"));
        Assert.True(LoggedAtDebug(host, "probe path copy"));
    }

    [Fact]
    public void A_probe_path_does_not_beat_the_host_or_the_module_folder()
    {
        using var env = new TestEnv();
        var outside = Path.Combine(env.Root, "outside2");
        Directory.CreateDirectory(outside);
        var folder = env.AddModule("faulty", behavior: "json-context",
            editManifest: j => j.Replace("\"group\": \"other\"", "\"group\": \"other\", \"probePaths\": [" + JsonSerializer.Serialize(outside) + "]"));
        File.Copy(typeof(JsonSerializer).Assembly.Location, Path.Combine(outside, "System.Text.Json.dll"));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable());
        Assert.Equal("Default", Row(m, "row.json"));
        Assert.False(LoggedAtDebug(host, "probe path copy"));
    }

    [Fact]
    public void Probe_paths_are_validated()
    {
        var ok = ManifestReader.Parse(Manifest(", \"probePaths\": [\"C:/Program Files/Intel\", \"%ProgramFiles%/Intel\"]"), "pp");
        Assert.True(ok.IsValid, ok.ErrorSummary);
        Assert.Equal(2, ok.Manifest!.DeclaredProbePaths.Count);
        Assert.False(ManifestReader.Parse(Manifest(""), "pp").Manifest!.ReadsOutsideItsFolder);

        foreach (var bad in new[]
        {
            "\"relative/dir\"", "\"C:/a/../b\"", "\"//server/share\"", "\"%RIGBAY_NO_SUCH_VARIABLE%/x\"", "\"\"", "5", "\"C:/a*\"",
        })
        {
            var r = ManifestReader.Parse(Manifest(", \"probePaths\": [" + bad + "]"), "pp");
            Assert.False(r.IsValid, bad);
            Assert.Contains(r.Issues, i => i.Path == "probePaths" && i.IsError);
        }

        var notArray = ManifestReader.Parse(Manifest(", \"probePaths\": \"C:/x\""), "pp");
        Assert.False(notArray.IsValid);

        var many = string.Join(",", Enumerable.Range(0, 9).Select(i => $"\"C:/d{i}\""));
        Assert.False(ManifestReader.Parse(Manifest(", \"probePaths\": [" + many + "]"), "pp").IsValid);
    }

    [Fact]
    public void Probe_path_problems_and_the_modules_page_fact_have_text_in_Core_s_language_files()
    {
        var bad = ManifestReader.Parse(Manifest(", \"probePaths\": [\"relative\"]"), "pp");
        Assert.Contains("must be an absolute local path", bad.ErrorSummary);
        Assert.Contains("'relative'", bad.ErrorSummary);
        var catalog = new LanguageCatalog(new ListLog());
        catalog.LoadEmbedded(typeof(RigbayHost).Assembly, "Rigbay.Core.lang.");
        Assert.Equal("Reads files outside its folder", catalog.T("core.requires.outside-files"));
        Assert.True(catalog.SetLanguage("vi"));
        Assert.NotEqual("Reads files outside its folder", catalog.T("core.requires.outside-files"));
    }
}
