using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;
using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu.Tests;

/// <summary>What the code can and cannot say (read only, enforced), the manifest, the icons and the language files.</summary>
public class HygieneTests
{
    private static string SourceFolder => Path.Combine(AppContext.BaseDirectory, "source");
    private static IEnumerable<string> SourceFiles => Directory.GetFiles(SourceFolder, "*.cs", SearchOption.AllDirectories);

    /// <summary>The code of a file without comment lines.</summary>
    private static string Code(string path) => string.Join("\n", File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>An NVML entry point is spelled "nvml" and a capital letter.</summary>
    private static readonly Regex EntryPoint = new(@"nvml[A-Z][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

    private static readonly string[] ExpectedNames =
    {
        "nvmlInit_v2", "nvmlShutdown", "nvmlDeviceGetCount_v2", "nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetName", "nvmlDeviceGetUtilizationRates",
        "nvmlDeviceGetPowerUsage", "nvmlDeviceGetEnforcedPowerLimit", "nvmlDeviceGetClockInfo", "nvmlDeviceGetMemoryInfo", "nvmlDeviceGetTemperature",
        "nvmlDeviceGetPerformanceState", "nvmlDeviceGetCurrentClocksEventReasons", "nvmlDeviceGetCurrentClocksThrottleReasons", "nvmlErrorString",
    };

    // ------------------------------------------------------------------ read only, enforced

    [Fact]
    public void The_source_is_there_to_be_scanned()
    {
        Assert.True(SourceFiles.Count() >= 10);
        Assert.Contains(SourceFiles, f => f.EndsWith("NvmlAllowList.cs", StringComparison.Ordinal));
        Assert.Contains(SourceFiles, f => f.EndsWith("NvmlApi.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_allow_list_is_exactly_the_agreed_read_functions_and_the_manifest_counts_them()
    {
        Assert.Equal(ExpectedNames.OrderBy(x => x, StringComparer.Ordinal), NvmlAllowList.Names.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(ExpectedNames.Length, NvmlAllowList.Names.Distinct().Count());
        Assert.All(NvmlAllowList.Names, n => Assert.True(NvmlAllowList.IsAllowed(n)));
        var requires = Manifest().GetProperty("requires").GetProperty("services").EnumerateArray().Single().GetString()!;
        Assert.Contains(ExpectedNames.Length + " read-only NVML calls", requires);
    }

    [Theory]
    [InlineData("nvmlDeviceSetPowerManagementLimit")]
    [InlineData("nvmlDeviceSetFanSpeed_v2")]
    [InlineData("nvmlDeviceSetGpuLockedClocks")]
    [InlineData("nvmlDeviceResetGpuLockedClocks")]
    [InlineData("nvmlDeviceSetComputeMode")]
    [InlineData("nvmlDeviceSetPersistenceMode")]
    [InlineData("nvmlDeviceClearEccErrorCounts")]
    [InlineData("nvmlDeviceSetApplicationsClocks")]
    [InlineData("nvmlSystemGetDriverVersion")]   // a read, but not one the module declared
    [InlineData("nvmlInit")]
    public void Nothing_that_sets_resets_or_is_merely_not_declared_is_on_the_list(string name)
    {
        Assert.False(NvmlAllowList.IsAllowed(name));
        Assert.Throws<InvalidOperationException>(() => NvmlAllowList.Require(name));
    }

    [Fact]
    public void Every_nvml_word_in_the_source_is_on_the_list_and_each_is_spelled_in_one_file_only()
    {
        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in SourceFiles)
            foreach (Match m in EntryPoint.Matches(Code(file)))
                (found.TryGetValue(m.Value, out var set) ? set : found[m.Value] = new HashSet<string>()).Add(Path.GetFileName(file));

        Assert.NotEmpty(found);
        foreach (var (word, files) in found)
        {
            Assert.True(NvmlAllowList.IsAllowed(word), $"'{word}' is in {string.Join(", ", files)} and is not on the NVML allow list");
            Assert.Equal(new[] { "NvmlAllowList.cs" }, files.ToArray());
        }
        Assert.Equal(ExpectedNames.OrderBy(x => x, StringComparer.Ordinal), found.Keys.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_nvml_word_in_the_compiled_assembly_is_on_the_list()
    {
        // The compiled module: names of methods and types (UTF-8 in the metadata) and string literals (UTF-16 in the user-string heap).
        var bytes = File.ReadAllBytes(typeof(GpuModule).Assembly.Location);
        var texts = new[]
        {
            Encoding.Latin1.GetString(bytes),
            Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1),
            Encoding.Unicode.GetString(bytes, 1, (bytes.Length - 1) & ~1),
        };
        var words = texts.SelectMany(t => EntryPoint.Matches(t).Select(m => m.Value)).ToHashSet(StringComparer.Ordinal);

        Assert.True(words.Count >= ExpectedNames.Length, "the scan must see the strings, or it proves nothing");
        foreach (var name in ExpectedNames) Assert.Contains(name, words);
        foreach (var word in words) Assert.True(NvmlAllowList.IsAllowed(word), $"the assembly contains '{word}', which is not on the NVML allow list");
    }

    [Fact]
    public void The_interface_the_module_uses_has_no_method_that_changes_the_gpu()
    {
        var names = typeof(INvml).GetMethods().Select(m => m.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                "ErrorString", "GetClock", "GetClockEventReasons", "GetCount", "GetEnforcedPowerLimit", "GetHandle", "GetMemory", "GetName",
                "GetPerformanceState", "GetPowerUsage", "GetTemperature", "GetUtilization", "Init", "Shutdown",
            },
            names);
        Assert.DoesNotContain(names, n => n.StartsWith("Set", StringComparison.Ordinal) || n.StartsWith("Reset", StringComparison.Ordinal) || n.StartsWith("Clear", StringComparison.Ordinal));
    }

    [Fact]
    public void The_library_is_loaded_from_the_windows_system_folder_by_full_path_and_nothing_is_bound_at_build_time()
    {
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            Assert.DoesNotContain("[DllImport", code);
            Assert.DoesNotContain("[LibraryImport", code);
            Assert.DoesNotContain("LoadLibrary", code);
            Assert.DoesNotContain("SetDllDirectory", code);
            Assert.DoesNotContain("AddDllDirectory", code);
        }
        var api = Code(SourceFiles.Single(f => f.EndsWith("NvmlApi.cs", StringComparison.Ordinal)));
        Assert.Single(Regex.Matches(api, @"""nvml\.dll"""));
        Assert.Contains("Path.Combine(Environment.SystemDirectory, \"nvml.dll\")", api);
        Assert.Contains("NativeLibrary.TryLoad(path, typeof(NvmlApi).Assembly, DllImportSearchPath.System32, out var handle)", api);
        Assert.Single(Regex.Matches(string.Join("\n", SourceFiles.Select(Code)), @"NativeLibrary\.TryLoad"));
        Assert.DoesNotContain("NativeLibrary.Load(", string.Join("\n", SourceFiles.Select(Code)));
    }

    [Fact]
    public void The_module_starts_no_program_opens_no_file_and_touches_no_registry_wmi_or_service()
    {
        var forbidden = new[]
        {
            "Process.Start", "ProcessStartInfo", "Registry", "ManagementObject", "ManagementClass", "ServiceController", "ControlService", "StartService",
            "File.Write", "File.Open", "File.Delete", "File.Copy", "File.Move", "File.Append", "FileStream", "Directory.Delete", "Environment.Exit", "Assembly.Load",
            "HttpClient", "Socket", "WebRequest",
        };
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            foreach (var word in forbidden)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} uses {word}");
        }
    }

    [Fact]
    public void Only_the_serial_wrapper_and_the_api_implement_the_nvml_interface_and_the_module_wraps_what_it_is_given()
    {
        var implementers = typeof(GpuModule).Assembly.GetTypes().Where(t => typeof(INvml).IsAssignableFrom(t) && t.IsClass).Select(t => t.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { nameof(NvmlApi), nameof(SerialNvml) }, implementers);
        var module = string.Join("\n", SourceFiles.Where(f => Path.GetFileName(f).StartsWith("GpuModule", StringComparison.Ordinal)).Select(Code));
        Assert.Equal(2, Regex.Matches(module, @"new SerialNvml\(").Count);   // Probe and Initialize: every NVML call of the module goes through the lock
    }

    // ------------------------------------------------------------------ manifest

    private static JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module", "module.json"))).RootElement.Clone();

    [Fact]
    public void The_manifest_is_valid_and_within_the_hosts_limits()
    {
        var m = Manifest();
        Assert.Equal(1, m.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("gpu", m.GetProperty("id").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.GetProperty("version").GetString());
        Assert.Equal("monitoring", m.GetProperty("group").GetString());
        Assert.Equal(">=0.1.4 <0.2", m.GetProperty("hostApi").GetString());   // MetricDemand and CommandInfo (host API revision 4)
        Assert.Equal("GPU", m.GetProperty("name").GetString());
        Assert.InRange(m.GetProperty("description").GetString()!.Length, 1, 200);
        Assert.Equal(typeof(GpuModule).FullName, m.GetProperty("entry").GetProperty("type").GetString());
        Assert.Equal(typeof(GpuModule).Assembly.GetName().Name + ".dll", m.GetProperty("entry").GetProperty("assembly").GetString());
        Assert.Equal("icon.svg", m.GetProperty("icon").GetString());
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "module", "icon.svg")));
        foreach (var field in new[] { "network", "registry", "services" })
            foreach (var entry in m.GetProperty("requires").GetProperty(field).EnumerateArray())
                Assert.InRange(entry.GetString()!.Length, 1, 200);
    }

    [Fact]
    public void The_manifest_declares_what_the_module_really_touches_and_no_more()
    {
        var m = Manifest();
        var requires = m.GetProperty("requires");
        Assert.False(requires.GetProperty("admin").GetBoolean());          // NVML reads need no administrator
        Assert.Empty(requires.GetProperty("network").EnumerateArray());
        Assert.Empty(requires.GetProperty("registry").EnumerateArray());
        var services = requires.GetProperty("services").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Single(services);
        Assert.Contains("nvml.dll", services[0]);
        Assert.Contains("read-only", services[0]);
        Assert.False(m.TryGetProperty("probePaths", out _));              // nvml.dll is a system file, loaded by full path; no assembly is read from another folder
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
    public void The_name_and_description_in_the_english_file_match_the_manifest()
    {
        var m = Manifest();
        var en = FakeLocalisation.Load("en");
        Assert.Equal(m.GetProperty("name").GetString(), en["module.gpu.name"]);
        Assert.Equal(m.GetProperty("description").GetString(), en["module.gpu.description"]);
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
            Assert.StartsWith("module.gpu.", key);
            var a = Regex.Matches(text, @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            var b = Regex.Matches(vi[key], @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            Assert.True(a.SequenceEqual(b), $"placeholders of {key} differ");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void Every_key_the_page_the_metrics_and_the_texts_use_exists(string language)
    {
        var rig = new Rig(language).Start();
        rig.Module.PollNow();
        var page = rig.Page();
        var loc = rig.Host.Loc;
        var keys = new List<string>();
        foreach (var group in page.Groups) keys.Add(group.HeaderKey);
        foreach (var row in page.EnumerateRows())
        {
            keys.Add(row.HeaderKey);
            if (row.DescriptionKey is { } d) keys.Add(d);
            if (row.HelpKey is { } h) keys.Add(h);
            if (row is ReadingRow { ChartGroupKey: { Length: > 0 } c }) keys.Add(c);
        }
        foreach (var d in rig.Host.MetricsBus.Descriptors) { keys.Add(d.LabelKey); if (d.DescriptionKey is { } k) keys.Add(k); }
        foreach (var verb in rig.Host.CommandRegistry.Verbs.Values) keys.Add(verb.DescriptionKey);
        foreach (var reason in new ulong[] { 0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x40, 0x80, 0x100 }) keys.AddRange(ClockReasons.Keys(reason));
        keys.AddRange(new[] { "state.starting", "state.reading", "state.degraded", "state.unavailable", "row.limit.none", "cmd.status-usage",
            "probe.no-dll", "probe.dll-not-loaded", "probe.init-failed", "probe.no-device", "probe.check-failed" });

        foreach (var key in keys.Where(k => !Text.IsLiteral(k)).Distinct())
            Assert.True(loc.Has(key), $"missing key {key}");
        Assert.NotEmpty(keys);
        Assert.False(string.IsNullOrEmpty(loc.T("row.poll.header")));
    }

    [Fact]
    public void Every_clock_reason_has_a_short_lower_case_word_in_both_languages()
    {
        foreach (var language in new[] { "en", "vi" })
        {
            var lang = FakeLocalisation.Load(language);
            foreach (var key in lang.Keys.Where(k => k.StartsWith("module.gpu.reason.", StringComparison.Ordinal)))
            {
                Assert.InRange(lang[key].Length, 3, 30);
                Assert.Equal(char.ToLowerInvariant(lang[key][0]), lang[key][0]);   // they follow "P0 · 72 W · "
            }
        }
        // each bit the module names has a key, and an unknown bit names nothing
        Assert.Empty(ClockReasons.Keys(0));
        Assert.Empty(ClockReasons.Keys(0x200));
        Assert.Equal(new[] { "reason.idle" }, ClockReasons.Keys(ClockReasons.GpuIdle));
    }

    [Fact]
    public void The_words_on_the_page_follow_the_writing_rules()
    {
        var en = FakeLocalisation.Load("en");
        foreach (var (key, text) in en.Where(kv => kv.Key.Contains(".row.") && kv.Key.EndsWith(".header", StringComparison.Ordinal)))
            Assert.True(text.Split(' ').Length <= 4, key);                        // a header is at most 4 words
        foreach (var (key, text) in en.Where(kv => kv.Key.EndsWith(".description", StringComparison.Ordinal) && kv.Key.Contains(".row.")))
            Assert.True(text.Length <= 70, key);                                  // a description is one line of at most 70 characters
        foreach (var (key, text) in en)
        {
            Assert.DoesNotContain("!", text);
            foreach (var banned in new[] { "simply", "easily", "powerful", "hotkey", "plugin" })
                Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
