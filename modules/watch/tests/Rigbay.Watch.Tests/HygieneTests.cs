using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Watch.Tests;

/// <summary>What the code can and cannot say (it reads the bus and nothing else), the manifest, the icons and the language files.</summary>
public class HygieneTests
{
    private static string SourceFolder => Path.Combine(AppContext.BaseDirectory, "source");
    private static IEnumerable<string> SourceFiles => Directory.GetFiles(SourceFolder, "*.cs", SearchOption.AllDirectories);

    /// <summary>The code of a file without comment lines.</summary>
    private static string Code(string path) => string.Join("\n", File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string AllCode => string.Join("\n", SourceFiles.Select(Code));

    // ------------------------------------------------------------------ read only, enforced

    [Fact]
    public void The_source_is_there_to_be_scanned()
    {
        Assert.True(SourceFiles.Count() >= 8);
        Assert.Contains(SourceFiles, f => f.EndsWith("RuleEngine.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void It_asks_for_readings_only_in_the_background_and_never_publishes_one()
    {
        var code = AllCode;
        Assert.Contains(".SubscribeBackground(", code);
        Assert.DoesNotContain(".Subscribe(", code);          // a Subscribe holds the publishers at their fast rate all day
        Assert.DoesNotContain(".Publish(", code);
        Assert.DoesNotContain(".Withdraw(", code);
        Assert.DoesNotContain(".Describe(", code);
    }

    [Fact]
    public void It_asks_for_no_approval_runs_no_verb_registers_no_shortcut_and_marks_no_row_as_a_hardware_change()
    {
        var code = AllCode;
        foreach (var word in new[] { "Approval", "ConfirmAsync", "HardwareEffect", "Verbs.", "VerbPlan", "RequestApprovalAsync", "StartAsync", "Hotkeys", "ShortcutRow", "RevertAsync" })
            Assert.DoesNotContain(word, code);
        Assert.DoesNotContain(".Effect", code);
    }

    [Fact]
    public void The_module_starts_no_program_opens_no_file_and_touches_no_registry_wmi_service_or_network()
    {
        var forbidden = new[]
        {
            "Process.Start", "ProcessStartInfo", "Registry", "ManagementObject", "ManagementClass", "ServiceController", "DllImport", "LibraryImport", "NativeLibrary",
            "File.", "Directory.", "FileStream", "StreamWriter", "Environment.Exit", "Assembly.Load", "HttpClient", "Socket", "WebRequest", "Marshal.",
        };
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            foreach (var word in forbidden)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} uses {word}");
        }
    }

    [Fact]
    public void Probe_is_always_yes_because_the_bus_is_the_same_on_every_machine()
    {
        Assert.True(new WatchModule().Probe().Supported);
    }

    [Fact]
    public void The_rig_proves_the_module_published_nothing_and_asked_for_nothing_across_a_whole_alert()
    {
        var rig = Rig.With(Rig.CpuRule()).Start();
        rig.Bus.Describe("predatorsense", "cpu.temp", "CPU temperature");
        rig.Run("cpu.temp", 97, 120);
        rig.Module.Pause(TimeSpan.FromMinutes(5));
        rig.Module.Resume();
        rig.Module.SendTestAlert();
        Assert.Equal(0, rig.Bus.Publishes);
        Assert.Equal(0, rig.Bus.PlainSubscribes);
        Assert.Equal(0, rig.Host.ApprovalService.Asked);
        Assert.Equal(0, rig.Host.HotkeyService.Registered);
    }

    // ------------------------------------------------------------------ manifest

    private static JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module", "module.json"))).RootElement.Clone();

    [Fact]
    public void The_manifest_is_valid_and_within_the_hosts_limits()
    {
        var m = Manifest();
        Assert.Equal(1, m.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("watch", m.GetProperty("id").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.GetProperty("version").GetString());
        Assert.Equal("monitoring", m.GetProperty("group").GetString());
        Assert.Equal(">=0.1.4 <0.2", m.GetProperty("hostApi").GetString());   // SubscribeBackground and CommandInfo are revision 4
        Assert.Equal("Watch", m.GetProperty("name").GetString());
        Assert.InRange(m.GetProperty("description").GetString()!.Length, 1, 200);
        Assert.Equal(typeof(WatchModule).FullName, m.GetProperty("entry").GetProperty("type").GetString());
        Assert.Equal(typeof(WatchModule).Assembly.GetName().Name + ".dll", m.GetProperty("entry").GetProperty("assembly").GetString());
        Assert.Equal("icon.svg", m.GetProperty("icon").GetString());
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "module", "icon.svg")));
        foreach (var field in new[] { "network", "registry", "services" })
            foreach (var entry in m.GetProperty("requires").GetProperty(field).EnumerateArray())
                Assert.InRange(entry.GetString()!.Length, 1, 200);
    }

    [Fact]
    public void The_manifest_declares_what_the_module_really_touches_and_no_more()
    {
        var requires = Manifest().GetProperty("requires");
        Assert.False(requires.GetProperty("admin").GetBoolean());
        Assert.Empty(requires.GetProperty("network").EnumerateArray());
        Assert.Empty(requires.GetProperty("registry").EnumerateArray());
        var services = requires.GetProperty("services").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Single(services);
        Assert.Contains("metrics bus", services[0]);
        Assert.Contains("read only", services[0]);
        Assert.False(Manifest().TryGetProperty("probePaths", out _));
    }

    [Fact]
    public void The_most_rules_fit_the_hosts_limit_of_background_subscriptions()
    {
        Assert.True(WatchRule.MaxRules * 2 <= 64);
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
        Assert.Equal(m.GetProperty("name").GetString(), en["module.watch.name"]);
        Assert.Equal(m.GetProperty("description").GetString(), en["module.watch.description"]);
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
            Assert.StartsWith("module.watch.", key);
            var a = Regex.Matches(text, @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            var b = Regex.Matches(vi[key], @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            Assert.True(a.SequenceEqual(b), $"placeholders of {key} differ");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void Every_key_a_full_page_the_verbs_and_the_alert_use_exists_in_the_language(string language)
    {
        var rig = new Rig(language, saved: new WatchSettings
        {
            Rules =
            {
                Rig.CpuRule(),
                Rig.CpuRule(r => { r.Id = "fan"; r.Metric = "fan.cpu.rpm"; r.Kind = RuleKind.Below; r.ConditionMetric = "cpu.temp"; r.ConditionThreshold = 80; r.Enabled = false; }),
                Rig.CpuRule(r => { r.Id = "err"; r.Metric = "steam.proxy.errors"; r.Kind = RuleKind.Rises; r.Threshold = 5; r.Seconds = 60; }),
                Rig.CpuRule(r => { r.Id = "bits"; r.Metric = "gpu.throttle"; r.Kind = RuleKind.Bits; r.Threshold = 96; }),
                Rig.CpuRule(r => { r.Id = "gone"; r.Metric = "nobody.publishes.this"; }),
            },
        }).Start();
        rig.Bus.Describe("predatorsense", "cpu.temp", "CPU temperature");
        rig.Bus.Describe("predatorsense", "fan.cpu.rpm", "CPU fan speed", MetricUnit.Rpm);
        rig.Bus.Describe("steam-bypass", "steam.proxy.errors", "Steam proxy errors", MetricUnit.Count);
        rig.Bus.Describe("tunnel", "tunnel.home.handshake_age", "Tunnel home: handshake age", MetricUnit.Seconds);
        rig.Bus.Describe("gpu", "gpu.throttle", "GPU clock limit reasons", MetricUnit.None);
        rig.Bus.Describe("gpu", "gpu.nvml.temp", "GPU core temperature");
        rig.Bus.Put("cpu.temp", 61);
        rig.Module.Tick();

        var page = rig.Page();
        PageTests.AssertValid(page);
        var loc = rig.Host.Loc;
        var keys = new List<string>();
        foreach (var group in page.Groups) keys.Add(group.HeaderKey);
        foreach (var row in page.EnumerateRows())
        {
            keys.Add(row.HeaderKey);
            if (row.DescriptionKey is { } d) keys.Add(d);
            if (row.HelpKey is { } h) keys.Add(h);
            switch (row)
            {
                case ButtonRow b: keys.Add(b.ButtonKey); if (b.ConfirmKey is { } c) keys.Add(c); break;
                case ChoiceRow choice: keys.AddRange(choice.Options.Select(o => o.LabelKey)); break;
            }
        }
        keys.AddRange(rig.Host.CommandRegistry.Verbs.Values.SelectMany(v => new[] { v.Info.DescriptionKey, v.Info.UsageKey! }));
        keys.AddRange(rig.Host.TrayService.Items.Select(i => i.TextKey));
        keys.AddRange(new[]
        {
            "state.starting", "state.no-rules", "state.all-off", "state.firing-count", "state.paused", "state.watching", "pause.until", "status.alerts-on",
            "toast.body", "toast.test.title", "toast.test.body", "picker.absent", "unit.seconds", "unit.minutes", "unit.hours",
            "rule.above", "rule.above.for", "rule.below", "rule.below.for", "rule.rises", "rule.bits", "rule.bits.for", "rule.while.above", "rule.while.below",
            "state.off", "state.waiting", "state.was-firing", "state.pending", "state.firing", "state.next", "state.quiet", "state.now", "state.last",
            "remove.gone", "remove.done", "add.pick", "add.full", "add.done", "test.done",
        });

        foreach (var key in keys.Where(k => !Text.IsLiteral(k)).Distinct())
            Assert.True(loc.Has(key), $"missing key {key}");
        Assert.NotEmpty(keys);
    }

    [Fact]
    public void Every_line_a_rule_can_show_resolves_to_words_and_not_to_a_key()
    {
        foreach (var language in new[] { "en", "vi" })
        {
            var rig = new Rig(language, saved: new WatchSettings { Rules = { Rig.CpuRule(), Rig.CpuRule(r => { r.Id = "off"; r.Enabled = false; }) } }).Start();
            rig.Run("cpu.temp", 96, 20);                                          // pending
            Assert.DoesNotContain("module.watch.", rig.Line("cpu1"));
            rig.Run("cpu.temp", 96, 40);                                          // firing
            Assert.DoesNotContain("module.watch.", rig.Line("cpu1"));
            rig.Clock.Advance(300); rig.Module.Tick();                            // waiting
            Assert.DoesNotContain("module.watch.", rig.Line("cpu1"));
            Assert.DoesNotContain("module.watch.", rig.Line("off"));
        }
    }

    [Fact]
    public void The_words_on_the_page_follow_the_writing_rules()
    {
        var en = FakeLocalisation.Load("en");
        foreach (var (key, text) in en.Where(kv => kv.Key.Contains(".row.") && kv.Key.EndsWith(".header", StringComparison.Ordinal)))
            Assert.True(text.Split(' ').Length <= 4, key);                        // a header is at most 4 words
        foreach (var (key, text) in en.Where(kv => kv.Key.EndsWith(".description", StringComparison.Ordinal) && kv.Key.Contains(".row.")))
            Assert.True(text.Length <= 70, key);                                  // a description is one line of at most 70 characters
        foreach (var key in new[] { "kind.above", "kind.below", "kind.rises", "kind.bits" })
            Assert.True(en["module.watch." + key].Length <= 16, key);              // options of a segmented control
        foreach (var language in new[] { "en", "vi" })
            foreach (var (key, text) in FakeLocalisation.Load(language))
            {
                Assert.DoesNotContain("!", text);
                foreach (var banned in new[] { "simply", "easily", "powerful", "hotkey", "plugin" })
                    Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
            }
    }
}
