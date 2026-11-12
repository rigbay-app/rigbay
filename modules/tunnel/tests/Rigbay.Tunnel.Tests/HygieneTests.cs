using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Tunnel.Tests;

/// <summary>What the code can and cannot say, the manifest, and the language files.</summary>
public class HygieneTests
{
    private static string SourceFolder => Path.Combine(AppContext.BaseDirectory, "source");
    private static IEnumerable<string> SourceFiles => Directory.GetFiles(SourceFolder, "*.cs", SearchOption.AllDirectories);

    /// <summary>The code of a file without comment lines.</summary>
    private static string Code(string path) => string.Join("\n", File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void The_source_is_there_to_be_scanned()
    {
        Assert.True(SourceFiles.Count() >= 8);
        Assert.Contains(SourceFiles, f => f.EndsWith("WireGuardTools.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Wg_show_can_only_be_called_with_one_of_three_fields_never_bare_never_dump()
    {
        // One place builds the argument list of "show", and it always adds a field; the words that print a key or a configuration appear nowhere.
        int showLiterals = SourceFiles.Sum(f => Regex.Matches(Code(f), "\"show\"").Count);
        Assert.Equal(1, showLiterals);

        var forbidden = new[] { "dump", "showconf", "setconf", "syncconf", "addconf", "genkey", "genpsk", "pubkey", "private-key", "privatekey", "preshared", "/dumplog" };
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            foreach (var word in forbidden)
                Assert.False(code.Contains(word, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} mentions '{word}'");
        }
        Assert.Equal(new[] { "LatestHandshakes", "Transfer", "Endpoints" }, Enum.GetNames<Wg.WgField>());
    }

    [Fact]
    public void The_module_opens_no_file_so_it_cannot_read_a_configuration()
    {
        var forbidden = new[] { "File.ReadAll", "File.Open", "File.ReadLines", "OpenRead", "OpenText", "FileStream", "StreamReader", "File.Copy", "File.Move", "File.Write", "File.Append", "File.Delete", "CryptUnprotect", "ProtectedData" };
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            foreach (var word in forbidden)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} uses {word}");
        }
    }

    [Fact]
    public void Only_the_process_runner_starts_programs_and_never_through_a_shell()
    {
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            bool starts = code.Contains("Process.Start", StringComparison.Ordinal) || code.Contains("ProcessStartInfo", StringComparison.Ordinal);
            if (Path.GetFileName(file) == "WireGuardTools.cs")
            {
                Assert.Contains("UseShellExecute = false", code);
                Assert.Contains("ArgumentList.Add", code);
                Assert.DoesNotContain("psi.Arguments", code);
                Assert.DoesNotContain("cmd.exe", code);
                Assert.DoesNotContain("powershell", code, StringComparison.OrdinalIgnoreCase);
            }
            else Assert.False(starts, Path.GetFileName(file) + " starts a process");
        }
    }

    [Fact]
    public void The_module_never_starts_or_stops_a_service_itself_and_never_touches_the_registry()
    {
        foreach (var file in SourceFiles)
        {
            var code = Code(file);
            Assert.DoesNotContain("ControlService", code);
            Assert.DoesNotContain("StartService", code);
            Assert.DoesNotContain("DeleteService", code);
            Assert.DoesNotContain("CreateService", code);
            Assert.DoesNotContain("Registry", code);
            Assert.DoesNotContain("Environment.Exit", code);
        }
    }

    [Fact]
    public void Every_switch_that_changes_a_service_goes_through_the_manager_which_holds_the_one_change_slot()
    {
        // The only callers of the two WireGuard install commands are in TunnelManager.ChangeAsync.
        foreach (var file in SourceFiles.Where(f => Path.GetFileName(f) is not ("TunnelManager.cs" or "WireGuardTools.cs" or "Contracts.cs")))
        {
            var code = Code(file);
            Assert.DoesNotContain("InstallTunnelServiceAsync", code);
            Assert.DoesNotContain("UninstallTunnelServiceAsync", code);
        }
    }

    // ------------------------------------------------------------------ manifest

    private static JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module", "module.json"))).RootElement.Clone();

    [Fact]
    public void The_manifest_is_valid_and_within_the_hosts_limits()
    {
        var m = Manifest();
        Assert.Equal(1, m.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("tunnel", m.GetProperty("id").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.GetProperty("version").GetString());
        Assert.Equal("network", m.GetProperty("group").GetString());
        Assert.Equal(">=0.1.3 <0.2", m.GetProperty("hostApi").GetString());   // the page uses ExpanderRow.HeaderRow, Row.Line, Row.HelpKey
        Assert.InRange(m.GetProperty("name").GetString()!.Length, 1, 60);
        Assert.InRange(m.GetProperty("description").GetString()!.Length, 1, 200);
        Assert.Equal(typeof(TunnelModule).FullName, m.GetProperty("entry").GetProperty("type").GetString());
        Assert.Equal(typeof(TunnelModule).Assembly.GetName().Name + ".dll", m.GetProperty("entry").GetProperty("assembly").GetString());
        Assert.Equal("icon.svg", m.GetProperty("icon").GetString());
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "module", "icon.svg")));
        foreach (var field in new[] { "network", "registry", "services" })
            foreach (var entry in m.GetProperty("requires").GetProperty(field).EnumerateArray())
                Assert.InRange(entry.GetString()!.Length, 1, 200);
    }

    [Fact]
    public void The_manifest_declares_what_the_module_really_touches()
    {
        var m = Manifest();
        Assert.True(m.GetProperty("requires").GetProperty("admin").GetBoolean());
        Assert.Empty(m.GetProperty("requires").GetProperty("registry").EnumerateArray());   // the module never touches the registry itself
        var services = m.GetProperty("requires").GetProperty("services").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(services, s => s.StartsWith("WireGuardTunnel$", StringComparison.Ordinal));
        var probe = m.GetProperty("probePaths").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { @"%ProgramFiles%\WireGuard" }, probe);
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
        var doc = System.Xml.Linq.XDocument.Parse(text);   // well-formed
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
        Assert.Equal(m.GetProperty("name").GetString(), en["module.tunnel.name"]);
        Assert.Equal(m.GetProperty("description").GetString(), en["module.tunnel.description"]);
    }

    [Fact]
    public void The_manifest_and_the_repository_text_name_no_real_tunnel_or_address()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module", "module.json"));
        Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", text);
        foreach (var file in SourceFiles.Concat(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "lang"))))
            Assert.DoesNotMatch(@"\b(?!198\.51\.100\.|10\.9\.0\.)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", File.ReadAllText(file));
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
            Assert.StartsWith("module.tunnel.", key);
            var a = Regex.Matches(text, @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            var b = Regex.Matches(vi[key], @"\{\d+\}").Select(x => x.Value).OrderBy(x => x).ToArray();
            Assert.True(a.SequenceEqual(b), $"placeholders of {key} differ");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void Every_key_the_page_uses_exists(string language)
    {
        var rig = new Rig(language).Running();
        rig.Start();
        rig.Row<TextRow>("row.public.header").Value.Value = Rig.PublicAddress;
        var page = rig.Page();
        var loc = rig.Host.Loc;
        var keys = new List<string>();
        foreach (var group in page.Groups)
        {
            keys.Add(group.HeaderKey);
            foreach (var row in page.EnumerateRows())
            {
                keys.Add(row.HeaderKey);
                if (row.DescriptionKey is { } d) keys.Add(d);
                if (row is TextRow { PlaceholderKey: { } p }) keys.Add(p);
                if (row.HardwareEffect is { } e) { keys.Add(e.DescriptionKey); if (e.RevertDescriptionKey is { } r) keys.Add(r); }
            }
        }
        foreach (var link in page.Links) keys.Add(link.TextKey);
        foreach (var verb in rig.Host.CommandRegistry.Verbs.Values) keys.Add(verb.DescriptionKey);
        foreach (var key in keys.Where(k => !Text.IsLiteral(k)).Distinct())
            Assert.True(rig.Host.Loc.Has(key), $"missing key {key}");
        Assert.NotEmpty(keys);
        Assert.False(string.IsNullOrEmpty(loc.T("row.on.header")));
    }

    [Fact]
    public void The_two_exact_approval_texts_are_what_the_task_names()
    {
        var en = FakeLocalisation.Load("en");
        Assert.Equal("Turn on tunnel {0}", en["module.tunnel.effect.turn-on"]);
        Assert.Equal("Turn off tunnel {0}", en["module.tunnel.effect.turn-off"]);
    }

    [Fact]
    public void Every_problem_text_has_a_plain_sentence_in_both_languages()
    {
        foreach (var language in new[] { "en", "vi" })
        {
            var lang = FakeLocalisation.Load(language);
            foreach (var key in lang.Keys.Where(k => k.StartsWith("module.tunnel.err.", StringComparison.Ordinal)))
                Assert.True(lang[key].Length > 20, key);
        }
    }
}
