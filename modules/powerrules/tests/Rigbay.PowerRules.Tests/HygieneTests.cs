using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.PowerRules.Tests;

/// <summary>What the code can and cannot call, the manifest, the icons and the language files.</summary>
public class HygieneTests
{
    private static string SourceFolder => Path.Combine(AppContext.BaseDirectory, "source");
    private static IEnumerable<string> SourceFiles => Directory.GetFiles(SourceFolder, "*.cs", SearchOption.AllDirectories);
    private static string Name(string path) => Path.GetFileName(path);

    /// <summary>The code of a file without comment lines.</summary>
    private static string Code(string path) => string.Join("\n", File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string AllCode => string.Join("\n", SourceFiles.Select(Code));

    [Fact]
    public void The_source_is_there_to_be_scanned()
    {
        Assert.True(SourceFiles.Count() >= 9);
        Assert.Contains(SourceFiles, f => Name(f) == "WindowsPanel.cs");
    }

    // ------------------------------------------------------------------ the native allow-list

    private static readonly string[] AllowedImports =
    {
        // power source (read) and the window that listens
        "GetSystemPowerStatus", "GetModuleHandleW", "RegisterClassExW", "UnregisterClassW", "CreateWindowExW", "DestroyWindow", "DefWindowProcW",
        "GetMessageW", "TranslateMessage", "DispatchMessageW", "PostMessageW", "PostQuitMessage", "RegisterPowerSettingNotification", "UnregisterPowerSettingNotification",
        // the internal panel: four reads and the one write
        "GetDisplayConfigBufferSizes", "QueryDisplayConfig", "DisplayConfigGetDeviceInfo", "EnumDisplaySettingsExW", "ChangeDisplaySettingsExW",
    };

    [Fact]
    public void The_module_imports_only_the_listed_functions_from_kernel32_and_user32()
    {
        var imports = new List<(string Dll, string Function)>();
        foreach (var file in SourceFiles)
            foreach (Match m in Regex.Matches(Code(file), @"\[DllImport\(""([^""]+)""[^\]]*\)\]\s*(?:private|internal|public)?\s*static extern [\w<>\[\]\.?]+\s+(\w+)\s*\(", RegexOptions.Singleline))
                imports.Add((m.Groups[1].Value, m.Groups[2].Value));
        Assert.NotEmpty(imports);
        Assert.All(imports, i => Assert.Contains(i.Dll, new[] { "kernel32.dll", "user32.dll" }));
        Assert.All(imports, i => Assert.Contains(i.Function, AllowedImports));
        // and nothing is imported that the scan did not see
        Assert.Equal(imports.Count, SourceFiles.Sum(f => Regex.Matches(Code(f), @"\[DllImport").Count));
    }

    [Fact]
    public void The_two_undocumented_exports_are_the_only_names_bound_by_string_and_come_from_system32()
    {
        var code = Code(SourceFiles.Single(f => Name(f) == "WindowsPowerMode.cs"));
        var names = Regex.Matches(code, @"TryGetExport\([^,]+,\s*""([^""]+)""").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "PowerGetEffectiveOverlayScheme", "PowerSetActiveOverlayScheme" }, names);
        Assert.Contains("Environment.SystemDirectory", code);
        Assert.DoesNotContain("LoadLibrary", AllCode);
        foreach (var file in SourceFiles.Where(f => Name(f) != "WindowsPowerMode.cs"))
        {
            Assert.DoesNotContain("TryGetExport", Code(file));
            Assert.DoesNotContain("GetProcAddress", Code(file));
        }
    }

    [Fact]
    public void The_display_change_is_called_in_one_place_with_nothing_but_the_test_flag_or_no_flag()
    {
        var panel = Code(SourceFiles.Single(f => Name(f) == "WindowsPanel.cs"));
        Assert.Equal(1, Regex.Matches(panel, @"ChangeDisplaySettingsExW\(").Count - 1);          // the import itself is the other match
        Assert.Contains("const uint ChangeNow = 0, ChangeTest = 0x00000002;", panel);
        Assert.Single(Regex.Matches(panel, @"Change\(panel, hz, ChangeTest\)"));
        Assert.Single(Regex.Matches(panel, @"Change\(panel, hz, ChangeNow\)"));
        foreach (var file in SourceFiles.Where(f => Name(f) != "WindowsPanel.cs"))
            Assert.DoesNotContain("ChangeDisplaySettings", Code(file));
        foreach (var word in new[] { "CDS_UPDATEREGISTRY", "CDS_GLOBAL", "CDS_FULLSCREEN", "CDS_SET_PRIMARY", "CDS_RESET", "CDS_NORESET", "CDS_VIDEOPARAMETERS", "CDS_ENABLE_UNSAFE_MODES" })
            Assert.DoesNotContain(word, AllCode);
        // the panel class re-checks the display and the rate itself
        Assert.Contains("fresh.Panel.Lists(hz)", panel);
        Assert.Contains("found.Device", panel);
    }

    [Fact]
    public void The_power_mode_is_set_from_the_three_allow_listed_guids_never_from_a_value_passed_in()
    {
        var mode = Code(SourceFiles.Single(f => Name(f) == "WindowsPowerMode.cs"));
        Assert.Contains("PowerModes.Guids.TryGetValue(mode, out var guid)", mode);
        Assert.DoesNotContain("new Guid(", mode);
        Assert.Equal(3, PowerModes.Guids.Count);
        Assert.Equal(PowerModes.All.OrderBy(m => m), PowerModes.Guids.Keys.OrderBy(m => m));
        Assert.DoesNotContain(PowerMode.Unknown, PowerModes.Guids.Keys);
        foreach (var file in SourceFiles.Where(f => Name(f) is not ("WindowsPowerMode.cs")))
            Assert.DoesNotContain("PowerSetActiveOverlayScheme", Code(file));
    }

    [Fact]
    public void Each_machine_change_is_made_in_one_place_in_the_runtime()
    {
        var runtime = Code(SourceFiles.Single(f => Name(f) == "Runtime.cs"));
        Assert.Single(Regex.Matches(runtime, @"_deps\.Panel\.Apply\("));
        Assert.Single(Regex.Matches(runtime, @"_deps\.Panel\.Test\("));
        Assert.Single(Regex.Matches(runtime, @"_deps\.Mode\.Set\("));
        Assert.True(runtime.IndexOf("_deps.Panel.Test(", StringComparison.Ordinal) < runtime.IndexOf("_deps.Panel.Apply(", StringComparison.Ordinal), "the test comes before the real call");
        foreach (var file in SourceFiles.Where(f => Name(f) != "Runtime.cs"))
        {
            var code = Code(file);
            Assert.DoesNotContain("Panel.Apply(", code.Replace("IPanelPort", ""));
            Assert.DoesNotContain("Mode.Set(", code);
        }
    }

    [Fact]
    public void The_module_touches_no_registry_file_process_service_or_power_plan()
    {
        var forbidden = new[]
        {
            "Registry", "Process.Start", "ProcessStartInfo", "powercfg", "PowerSetActiveScheme", "PowerWriteACValueIndex", "PowerWriteDCValueIndex",
            "File.", "Directory.", "FileStream", "ControlService", "StartService", "Environment.Exit", "SetThreadExecutionState", "ExitWindows", "SetSuspendState",
            "HttpClient", "Socket", "WebClient",
        };
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            foreach (var word in forbidden)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{Name(file)} mentions '{word}'");
        }
    }

    // ------------------------------------------------------------------ manifest and icons

    private static JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module", "module.json"))).RootElement.Clone();

    [Fact]
    public void The_manifest_is_valid_and_within_the_hosts_limits()
    {
        var m = Manifest();
        Assert.Equal(1, m.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("power-rules", m.GetProperty("id").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.GetProperty("version").GetString());
        Assert.Equal("power", m.GetProperty("group").GetString());
        Assert.Equal(">=0.1.3 <0.2", m.GetProperty("hostApi").GetString());   // Primary, Row.Line and Row.HelpKey are revision 0.1.3
        Assert.InRange(m.GetProperty("name").GetString()!.Length, 1, 60);
        Assert.InRange(m.GetProperty("description").GetString()!.Length, 1, 200);
        Assert.Equal(typeof(PowerRulesModule).FullName, m.GetProperty("entry").GetProperty("type").GetString());
        Assert.Equal(typeof(PowerRulesModule).Assembly.GetName().Name + ".dll", m.GetProperty("entry").GetProperty("assembly").GetString());
        Assert.Equal("icon.svg", m.GetProperty("icon").GetString());
    }

    [Fact]
    public void The_manifest_declares_what_the_module_really_touches()
    {
        var requires = Manifest().GetProperty("requires");
        Assert.False(requires.GetProperty("admin").GetBoolean());                              // display and power-mode calls need no administrator
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
        Assert.Equal(m.GetProperty("name").GetString(), en["module.power-rules.name"]);
        Assert.Equal(m.GetProperty("description").GetString(), en["module.power-rules.description"]);
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
            Assert.StartsWith("module.power-rules.", key);
            var a = Regex.Matches(text, @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            var b = Regex.Matches(vi[key], @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            Assert.True(a.SequenceEqual(b), $"placeholders of {key} differ");
        }
    }

    [Fact]
    public void Every_key_written_in_the_source_exists_in_the_reference_language()
    {
        var en = FakeLocalisation.Load("en");
        var found = new SortedSet<string>();
        foreach (Match m in Regex.Matches(AllCode, @"""((?:row|group|info|line|source|panel|mode|opt|effect|confirm|button|err|toast|part|command|status|probe|rule)\.[a-z0-9.\-]+)"""))
            found.Add(m.Groups[1].Value);
        Assert.True(found.Count > 40);
        foreach (var key in found) Assert.True(en.ContainsKey("module.power-rules." + key), $"missing key {key}");
        // and the other way round: every key is used (the dynamic mode.* labels come from PowerModes.LabelKey)
        var dynamicKeys = PowerModes.All.Select(PowerModes.LabelKey).ToHashSet();
        foreach (var key in en.Keys.Select(k => k["module.power-rules.".Length..]).Where(k => k is not ("name" or "description")))
            Assert.True(found.Contains(key) || dynamicKeys.Contains(key), $"unused key {key}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public async Task Every_key_the_page_uses_exists_and_the_rules_read_in_that_language(string language)
    {
        var rig = new Rig(language).Start();
        var page = rig.Page();
        var loc = rig.Host.Loc;
        var keys = new List<string>();
        foreach (var group in page.Groups) keys.Add(group.HeaderKey);
        foreach (var row in page.EnumerateRows())
        {
            keys.Add(row.HeaderKey);
            if (row.DescriptionKey is { } d) keys.Add(d);
            if (row.HelpKey is { } h) keys.Add(h);
            if (row.HardwareEffect is { } e) { keys.Add(e.DescriptionKey); if (e.RevertDescriptionKey is { } r) keys.Add(r); }
            if (row is ChoiceRow c) keys.AddRange(c.Options.Select(o => o.LabelKey));
            if (row is ButtonRow b) keys.Add(b.ButtonKey);
            if (row.Line is { } line && !line.Value.IsEmpty) keys.Add(line.Value.Key);
        }
        foreach (var verb in rig.Host.CommandRegistry.Verbs.Values) keys.Add(verb.DescriptionKey);
        foreach (var key in keys.Where(k => !Text.IsLiteral(k)).Distinct())
            Assert.True(loc.Has(key), $"missing key {key}");
        Assert.NotEmpty(keys);

        // the approval question, in words
        await rig.Approve(answer: false);
        var detail = rig.Words(rig.Host.ApprovalService.Asked.Single().Detail);
        Assert.Contains("60 Hz", detail);
        Assert.DoesNotContain("module.power-rules.", detail);
    }

    [Fact]
    public void Row_headers_are_at_most_four_words_and_descriptions_at_most_70_characters()
    {
        var en = FakeLocalisation.Load("en");
        foreach (var key in en.Keys.Where(k => k.StartsWith("module.power-rules.row.", StringComparison.Ordinal) && k.EndsWith(".header", StringComparison.Ordinal)))
            Assert.True(en[key].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4, $"{key}: {en[key]}");
        foreach (var key in en.Keys.Where(k => k.EndsWith(".description", StringComparison.Ordinal) && k.StartsWith("module.power-rules.row.", StringComparison.Ordinal)))
            Assert.True(en[key].Length <= 70, key);
    }
}
