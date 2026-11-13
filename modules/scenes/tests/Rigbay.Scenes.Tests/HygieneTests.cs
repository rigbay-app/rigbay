using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Scenes.Tests;

/// <summary>What the code can and cannot call, the manifest, the icons and the language files.</summary>
public class HygieneTests
{
    private static string SourceFolder => Path.Combine(AppContext.BaseDirectory, "source");
    private static IEnumerable<string> SourceFiles => Directory.GetFiles(SourceFolder, "*.cs", SearchOption.AllDirectories);
    private static string Name(string path) => Path.GetFileName(path);

    /// <summary>The code of a file without comment lines.</summary>
    private static string Code(string path) => string.Join("\n", File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    private static string AllCode => string.Join("\n", SourceFiles.Select(Code));

    [Fact]
    public void The_source_is_there_to_be_scanned()
    {
        Assert.True(SourceFiles.Count() >= 8);
        Assert.Contains(SourceFiles, f => Name(f) == "WindowsProcesses.cs");
    }

    // ------------------------------------------------------------------ the module only reads the machine

    [Fact]
    public void The_module_touches_no_registry_file_service_network_or_native_function_and_starts_or_stops_no_process()
    {
        var forbidden = new[]
        {
            "Registry", "Process.Start", "ProcessStartInfo", ".Kill(", "PriorityClass", "ProcessorAffinity", "File.", "Directory.", "FileStream", "StreamWriter",
            "ControlService", "StartService", "Environment.Exit", "HttpClient", "Socket", "WebClient", "DllImport", "LibraryImport", "NativeLibrary", "Marshal.",
            "System.Management", "WMI", "powercfg", "Assembly.Load", "Activator.", "Reflection",
        };
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            foreach (var word in forbidden)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{Name(file)} mentions '{word}'");
        }
    }

    [Fact]
    public void Processes_are_only_listed_in_one_file_and_only_by_name_or_for_the_picker()
    {
        foreach (var file in SourceFiles.Where(f => Name(f) != "WindowsProcesses.cs"))
            Assert.DoesNotContain("System.Diagnostics", Code(file));
        var code = Code(SourceFiles.Single(f => Name(f) == "WindowsProcesses.cs"));
        Assert.Single(Regex.Matches(code, @"Process\.GetProcessesByName\("));
        Assert.Single(Regex.Matches(code, @"Process\.GetProcesses\("));
        Assert.Contains("MainWindowHandle", code);
        // nothing is done to a process it found except reading its name, id, window and path
        foreach (var word in new[] { "Kill", "Close", "Suspend", "Modules", "Threads", "Handle", "SetWindow" })
            if (word != "MainWindowHandle") Assert.DoesNotMatch(new Regex(@"\b" + word + @"\b"), code.Replace("MainWindowHandle", ""));
    }

    // ------------------------------------------------------------------ the module never runs another module's verb on its own

    [Fact]
    public void Only_the_hosts_plan_calls_are_used_and_never_the_free_call_of_a_verb()
    {
        var code = AllCode;
        Assert.DoesNotContain("InvokeAsync(", code.Replace("InvokeAsync(CancellationToken", ""));   // the page's button delegate type, not Verbs
        Assert.DoesNotMatch(new Regex(@"Verbs\.InvokeAsync"), code);
        foreach (var call in Regex.Matches(code, @"Verbs\.(\w+)").Select(m => m.Groups[1].Value).Distinct())
            Assert.Contains(call, new[] { "List", "Changed", "GetApproval", "RequestApprovalAsync", "Revoke", "StartAsync", "EndAsync", "ActiveRuns" });
    }

    [Fact]
    public void A_plan_is_started_and_ended_in_one_place_each()
    {
        var runtime = Code(SourceFiles.Single(f => Name(f) == "Runtime.cs"));
        Assert.Single(Regex.Matches(runtime, @"Verbs\.StartAsync\("));
        Assert.Equal(2, Regex.Matches(runtime, @"Verbs\.EndAsync\(").Count);                       // EndSceneAsync, and the end of a run that belongs to no scene
        foreach (var file in SourceFiles.Where(f => Name(f) != "Runtime.cs"))
        {
            Assert.DoesNotContain("Verbs.StartAsync", Code(file));
            Assert.DoesNotContain("Verbs.EndAsync", Code(file));
        }
    }

    [Fact]
    public void The_plan_the_module_starts_is_always_built_from_the_scene_as_stored()
    {
        var runtime = Code(SourceFiles.Single(f => Name(f) == "Runtime.cs"));
        Assert.Contains("Verbs.StartAsync(ScenePlans.Build(scene), _host.StopToken)", runtime);
        Assert.Contains("Verbs.RequestApprovalAsync(ScenePlans.Build(scene), _host.StopToken)", runtime);
        // the three places that give the host a plan (the read of its approval, the question, the start) all build it from the scene, nowhere else
        Assert.Equal(3, Regex.Matches(runtime, @"ScenePlans\.Build\(").Count);
        foreach (var file in SourceFiles.Where(f => Name(f) is not ("Runtime.cs" or "Model.cs")))
            Assert.DoesNotContain("ScenePlans.Build(", Code(file));
    }

    [Fact]
    public void The_module_registers_only_two_read_only_verbs_and_no_hotkey_tray_item_or_metric()
    {
        var code = AllCode;
        Assert.Equal(2, Regex.Matches(code, @"Commands\.Register\(").Count);
        Assert.Equal(2, Regex.Matches(code, @"Access = VerbAccess\.ReadOnly").Count);
        Assert.DoesNotContain("Access = VerbAccess.Writes", code);      // it declares no write of its own (it only reads other modules' declarations)
        Assert.DoesNotContain("Hotkeys.", code);
        Assert.DoesNotContain("Tray.", code);
        Assert.DoesNotContain("Metrics.", code);
        Assert.DoesNotContain("Approval.ConfirmAsync", code);      // the host's plan dialog is the only question
        Assert.DoesNotContain("HardwareEffect(", code);
    }

    [Fact]
    public void The_poll_is_5_seconds_and_the_grace_10()
    {
        var ports = Code(SourceFiles.Single(f => Name(f) == "Ports.cs"));
        Assert.Contains("Poll { get; init; } = TimeSpan.FromSeconds(5)", ports);
        Assert.Contains("Grace { get; init; } = TimeSpan.FromSeconds(10)", ports);
    }

    // ------------------------------------------------------------------ manifest and icons

    private static JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module", "module.json"))).RootElement.Clone();

    [Fact]
    public void The_manifest_is_valid_and_within_the_hosts_limits()
    {
        var m = Manifest();
        Assert.Equal(1, m.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("scenes", m.GetProperty("id").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.GetProperty("version").GetString());
        Assert.Equal("power", m.GetProperty("group").GetString());
        Assert.Equal(">=0.1.4 <0.2", m.GetProperty("hostApi").GetString());   // Verbs, VerbPlan and CommandInfo are revision 0.1.4
        Assert.True(HostApi.IsSupported(m.GetProperty("hostApi").GetString()));
        Assert.InRange(m.GetProperty("name").GetString()!.Length, 1, 60);
        Assert.InRange(m.GetProperty("description").GetString()!.Length, 1, 200);
        Assert.Equal(typeof(ScenesModule).FullName, m.GetProperty("entry").GetProperty("type").GetString());
        Assert.Equal(typeof(ScenesModule).Assembly.GetName().Name + ".dll", m.GetProperty("entry").GetProperty("assembly").GetString());
        Assert.Equal("icon.svg", m.GetProperty("icon").GetString());
        Assert.Equal(SceneRules.OwnModule, m.GetProperty("id").GetString());
    }

    [Fact]
    public void The_manifest_declares_what_the_module_really_touches()
    {
        var requires = Manifest().GetProperty("requires");
        Assert.False(requires.GetProperty("admin").GetBoolean());
        Assert.Empty(requires.GetProperty("network").EnumerateArray());
        Assert.Empty(requires.GetProperty("registry").EnumerateArray());
        Assert.Empty(requires.GetProperty("services").EnumerateArray());
        Assert.Empty(Manifest().GetProperty("probePaths").EnumerateArray());
    }

    [Theory]
    [InlineData("icon.svg")]
    [InlineData("icon-glyph.svg")]
    [InlineData("icon-unsupported.svg")]
    public void The_icons_are_plain_drawings_the_host_accepts(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "module", file);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length <= 64 * 1024);
        var text = File.ReadAllText(path);
        var doc = System.Xml.Linq.XDocument.Parse(text);
        Assert.Equal("svg", doc.Root!.Name.LocalName);
        var forbidden = new[] { "script", "foreignObject", "image", "text", "tspan", "style", "iframe", "object", "embed", "link" };
        Assert.DoesNotContain(doc.Descendants(), e => forbidden.Contains(e.Name.LocalName));
        Assert.DoesNotMatch(new Regex(@"\son\w+\s*=", RegexOptions.IgnoreCase), text);
        Assert.DoesNotContain("href", text);
        Assert.DoesNotContain("url(", text);
        if (file != "icon-glyph.svg") Assert.Equal("0 0 208 208", doc.Root.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void The_name_and_description_in_the_language_files_match_the_manifest()
    {
        var m = Manifest();
        var en = FakeLocalisation.Load("en");
        Assert.Equal(m.GetProperty("name").GetString(), en["module.scenes.name"]);
        Assert.Equal(m.GetProperty("description").GetString(), en["module.scenes.description"]);
    }

    // ------------------------------------------------------------------ language

    [Fact]
    public void Both_languages_have_the_same_keys_and_the_same_placeholders()
    {
        var en = FakeLocalisation.Load("en");
        var vi = FakeLocalisation.Load("vi");
        Assert.Empty(en.Keys.Except(vi.Keys));
        Assert.Empty(vi.Keys.Except(en.Keys));
        foreach (var (key, text) in en)
        {
            Assert.StartsWith("module.scenes.", key);
            var a = Regex.Matches(text, @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            var b = Regex.Matches(vi[key], @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            Assert.True(a.SequenceEqual(b), $"placeholders of {key} differ");
        }
    }

    [Fact]
    public void Every_key_written_in_the_source_exists_in_the_reference_language_and_every_key_is_used()
    {
        var en = FakeLocalisation.Load("en");
        var found = new SortedSet<string>();
        foreach (Match m in Regex.Matches(AllCode, @"""((?:row|group|info|line|attention|problem|block|toast|last|verb|err|msg|step|opt|button|confirm|summary|cmd|plan|probe|duration|default)[.\-][a-z0-9.\-]+|default-name)"""))
            found.Add(m.Groups[1].Value);
        Assert.True(found.Count > 60, "found " + found.Count);
        foreach (var key in found) Assert.True(en.ContainsKey("module.scenes." + key), $"missing key {key}");
        // the keys built in code: problem.<name> from SceneProblem, block.<name> from BlockKind
        var dynamicKeys = Enum.GetValues<SceneProblem>().Where(p => p != SceneProblem.None).Select(SceneRules.ProblemKey)
            .Concat(Enum.GetValues<BlockKind>().Where(k => k != BlockKind.None).Select(k => "block." + k.ToString().ToLowerInvariant())).ToHashSet();
        foreach (var key in en.Keys.Select(k => k["module.scenes.".Length..]).Where(k => k is not ("name" or "description")))
            Assert.True(found.Contains(key) || dynamicKeys.Contains(key), $"unused key {key}");
    }

    [Fact]
    public void Row_headers_are_at_most_four_words_and_row_descriptions_at_most_70_characters()
    {
        var en = FakeLocalisation.Load("en");
        foreach (var key in en.Keys.Where(k => k.StartsWith("module.scenes.row.", StringComparison.Ordinal) && k.EndsWith(".header", StringComparison.Ordinal)))
            Assert.True(en[key].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4, $"{key}: {en[key]}");
        foreach (var key in en.Keys.Where(k => k.EndsWith(".description", StringComparison.Ordinal) && k.StartsWith("module.scenes.", StringComparison.Ordinal) && k != "module.scenes.description"))
            Assert.True(en[key].Length <= 70, $"{key}: {en[key].Length}");
    }
}
