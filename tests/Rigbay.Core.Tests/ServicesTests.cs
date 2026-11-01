using System.Text.Json;
using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Core.Tests;

public class SettingsTests
{
    public sealed class Sample
    {
        public string Name { get; set; } = "default";
        public int Count { get; set; } = 3;
        public ModuleGroup Group { get; set; } = ModuleGroup.Other;
        public List<string> Items { get; set; } = new();
    }

    private static (JsonSettingsStore Store, string Path, ListLog Log, TestEnv Env) Make()
    {
        var env = new TestEnv();
        var path = Path.Combine(env.Root, "modules", "m", "settings.json");
        var log = new ListLog();
        return (new JsonSettingsStore(path, log), path, log, env);
    }

    [Fact]
    public void No_file_gives_defaults()
    {
        var (store, _, _, env) = Make();
        using (env)
        {
            var s = store.Load<Sample>(out var version);
            Assert.Equal("default", s.Name);
            Assert.Equal(3, s.Count);
            Assert.Equal(0, version);
        }
    }

    [Fact]
    public void Values_round_trip_with_their_version()
    {
        var (store, path, _, env) = Make();
        using (env)
        {
            store.Save(new Sample { Name = "x", Count = 9, Group = ModuleGroup.Power, Items = { "a", "b" } }, version: 4);
            var s = store.Load<Sample>(out var version);
            Assert.Equal("x", s.Name);
            Assert.Equal(9, s.Count);
            Assert.Equal(ModuleGroup.Power, s.Group);
            Assert.Equal(new[] { "a", "b" }, s.Items);
            Assert.Equal(4, version);
            var text = File.ReadAllText(path);
            Assert.Contains("\"version\": 4", text);
            Assert.Contains("\"power\"", text); // enums as camelCase strings
            Assert.Contains("\"name\"", text);  // camelCase names
        }
    }

    [Fact]
    public void Missing_properties_keep_their_defaults_and_unknown_ones_are_ignored()
    {
        var (store, path, _, env) = Make();
        using (env)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "version": 2, "data": { "count": 11, "somethingNew": true } }""");
            var s = store.Load<Sample>(out var version);
            Assert.Equal(11, s.Count);
            Assert.Equal("default", s.Name);
            Assert.Equal(2, version);
        }
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("""{ "version": 1, "data": [] }""")]
    [InlineData("""{ "version": "one", "data": {} }""")]
    [InlineData("""{ "version": 1, "data": { "count": "many" } }""")]
    [InlineData("""{ "version": 1, "data": { "group": "nonsense" } }""")]
    [InlineData("{ \"version\": 1, \"data\": { \"count\": ")]
    public void A_corrupt_file_is_backed_up_and_defaults_are_returned(string content)
    {
        var (store, path, log, env) = Make();
        using (env)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);

            var s = store.Load<Sample>(out var version);

            Assert.Equal("default", s.Name);
            Assert.Equal(0, version);
            Assert.False(File.Exists(path));
            var backup = Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.corrupt-*").Single();
            Assert.Equal(content, File.ReadAllText(backup));
            Assert.Contains(log.Lines, l => l.StartsWith("Warn") && l.Contains("corrupt"));

            // and the module can save again afterwards
            store.Save(new Sample { Name = "fresh" });
            Assert.Equal("fresh", store.Load<Sample>().Name);
        }
    }

    [Fact]
    public void A_locked_file_is_not_mistaken_for_corruption()
    {
        var (store, path, log, env) = Make();
        using (env)
        {
            store.Save(new Sample { Name = "kept" });
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var s = store.Load<Sample>();
                Assert.Equal("default", s.Name);
            }
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.corrupt-*"));
            Assert.Equal("kept", store.Load<Sample>().Name);
        }
    }

    [Fact]
    public void Saving_leaves_no_temporary_files_and_replaces_the_old_content()
    {
        var (store, path, _, env) = Make();
        using (env)
        {
            for (int i = 0; i < 5; i++) store.Save(new Sample { Count = i });
            Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).ToArray());
            Assert.Equal(4, store.Load<Sample>().Count);
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); // always whole, valid JSON
        }
    }

    [Fact]
    public async Task Concurrent_saves_never_leave_a_half_written_file()
    {
        var (store, path, _, env) = Make();
        using (env)
        {
            var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < 25; i++) store.Save(new Sample { Name = new string('x', 2000), Count = t * 100 + i });
            })).ToArray();
            await Task.WhenAll(tasks);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2000, store.Load<Sample>().Name.Length);
        }
    }
}

public class LanguageTests
{
    private static LanguageFile Parse(string text, string source = "x.lang") => LanguageFile.Parse(new StringReader(text), source);

    [Fact]
    public void The_file_format_matches_xtu_profile_switcher()
    {
        var f = Parse("""
            # comment
            @code = VI
            @name = Tiếng Việt

            a.b = Xin chào
            multi = line one\nline two
            slash = a\\b
            spaced   =   keeps inner  spaces
            """);
        Assert.Equal("vi", f.Code);
        Assert.Equal("Tiếng Việt", f.Name);
        Assert.Equal("Xin chào", f.Text["a.b"]);
        Assert.Equal("line one\nline two", f.Text["multi"]);
        Assert.Equal("a\\b", f.Text["slash"]);
        Assert.Equal("keeps inner  spaces", f.Text["spaced"]);
        Assert.Empty(f.Problems);
    }

    [Fact]
    public void Lines_without_an_equals_sign_are_problems_and_a_missing_header_uses_the_file_name()
    {
        var f = Parse("just text\nk = v", "fr.lang");
        Assert.Equal("fr", f.Code);
        Assert.Single(f.Problems);
        Assert.Equal("v", f.Text["k"]);
    }

    [Fact]
    public void Lookup_falls_back_from_the_current_language_to_English_to_the_key()
    {
        var catalog = new LanguageCatalog(new ListLog());
        catalog.Add(Parse("@code = en\n@name = English\nhello = Hello\nonly.en = English only"));
        catalog.Add(Parse("@code = vi\n@name = Tiếng Việt\nhello = Xin chào"));

        Assert.Equal("Hello", catalog.T("hello"));
        Assert.True(catalog.SetLanguage("vi"));
        Assert.Equal("Xin chào", catalog.T("hello"));
        Assert.Equal("English only", catalog.T("only.en"));
        Assert.Equal("nowhere.key", catalog.T("nowhere.key"));
        Assert.False(catalog.SetLanguage("zz"));
        Assert.Equal("vi", catalog.CurrentCode);
        Assert.Equal("en", catalog.Languages[0].Code); // English first
    }

    [Fact]
    public void A_broken_placeholder_in_a_translation_falls_back_to_the_English_text()
    {
        var catalog = new LanguageCatalog(new ListLog());
        catalog.Add(Parse("@code = en\nmsg = {0} of {1}"));
        catalog.Add(Parse("@code = vi\nmsg = {0} trên {2} {"));
        catalog.SetLanguage("vi");
        Assert.Equal("3 of 4", catalog.F("msg", 3, 4));
    }

    [Fact]
    public void Module_files_may_only_define_keys_under_their_own_prefix()
    {
        var log = new ListLog();
        var catalog = new LanguageCatalog(log);
        catalog.Add(Parse("@code = en\napp.title = Rigbay"));
        var accepted = catalog.Add(Parse("@code = en\nmodule.sample.ok = fine\napp.title = HIJACKED\nmodule.other.x = nope"), "module.sample.");
        Assert.Equal(1, accepted);
        Assert.Equal("Rigbay", catalog.T("app.title"));
        Assert.Equal("module.other.x", catalog.T("module.other.x"));
        Assert.Equal(2, log.Lines.Count(l => l.Contains("outside 'module.sample.'")));
    }

    [Fact]
    public void A_module_uses_short_keys_and_the_prefix_is_added()
    {
        var catalog = new LanguageCatalog(new ListLog());
        catalog.Add(Parse("@code = en\nmodule.sample.name = Sample\nmodule.sample.count = {0} items"), "module.sample.");
        var loc = new ModuleLocalisation(catalog, "sample");
        Assert.Equal("Sample", loc.T("name"));
        Assert.Equal("2 items", loc.F("count", 2));
        Assert.Equal("2 items", loc.Resolve(LocalisedText.Of("count", 2)));
        Assert.Equal("module.sample.missing", loc.T("missing")); // the full key is shown, so a typo is visible
        Assert.False(loc.TryGet("missing", out _));
    }

    [Fact]
    public void A_user_file_overrides_single_keys_of_a_built_in_language()
    {
        using var env = new TestEnv();
        var userDir = Path.Combine(env.DataRoot, "lang");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "en.lang"), "@code = en\ncore.reason.restart = Please restart\n");
        using var host = env.NewHost();
        Assert.Equal("Please restart", host.Languages.T("core.reason.restart"));
        Assert.Equal("Needs host API a, this host is API b", host.Languages.F("core.reason.api-mismatch", "a", "b")); // others untouched
    }

    [Fact]
    public void The_host_ships_English_and_Vietnamese_with_the_same_keys()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        Assert.True(host.Languages.Exists("en"));
        Assert.True(host.Languages.Exists("vi"));
        var en = host.Languages.Languages.Single(l => l.Code == "en");
        var vi = host.Languages.Languages.Single(l => l.Code == "vi");
        Assert.Empty(en.Text.Keys.Except(vi.Text.Keys));
        Assert.Empty(vi.Text.Keys.Except(en.Text.Keys));
        foreach (var (key, text) in en.Text)
            Assert.Equal(LanguageFile.Placeholders(text), LanguageFile.Placeholders(vi.Text[key]));
    }
}

public class LangCheckTests
{
    private static LanguageFile F(string text, string source = "f.lang") => LanguageFile.Parse(new StringReader(text), source);

    [Fact]
    public void A_matching_pair_is_clean()
    {
        var report = LangCheck.Check(new[] { F("@code = en\na = A {0}\nb = B"), F("@code = vi\na = Ă {0}\nb = Bê") });
        Assert.False(report.HasErrors);
        Assert.False(report.HasWarnings);
        Assert.Equal(2, report.EnglishKeys);
        Assert.Equal(2, report.Languages.Single(l => l.Code == "vi").Translated);
    }

    [Fact]
    public void Missing_keys_are_a_warning()
    {
        var report = LangCheck.Check(new[] { F("@code = en\na = A\nb = B"), F("@code = vi\na = Ă") });
        var vi = report.Languages.Single(l => l.Code == "vi");
        Assert.Equal(new[] { "b" }, vi.Missing);
        Assert.True(report.HasWarnings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Unknown_keys_are_an_error()
    {
        var report = LangCheck.Check(new[] { F("@code = en\na = A"), F("@code = vi\na = Ă\nb.typo = ?") });
        Assert.Equal(new[] { "b.typo" }, report.Languages.Single(l => l.Code == "vi").Unknown);
        Assert.True(report.HasErrors);
    }

    [Theory]
    [InlineData("A {0} and {1}", "Ă {0}")]
    [InlineData("A {0}", "Ă {0} {1}")]
    [InlineData("A {0}", "Ă")]
    [InlineData("A", "Ă {0}")]
    public void Placeholder_mismatches_are_an_error(string english, string other)
    {
        var report = LangCheck.Check(new[] { F($"@code = en\na = {english}"), F($"@code = vi\na = {other}") });
        Assert.Equal(new[] { "a" }, report.Languages.Single(l => l.Code == "vi").PlaceholderMismatch);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Reordered_placeholders_are_fine()
    {
        var report = LangCheck.Check(new[] { F("@code = en\na = {0} of {1}"), F("@code = vi\na = {1} trên {0}") });
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Keys_outside_the_module_prefix_are_an_error()
    {
        var report = LangCheck.Check(
            new[] { F("@code = en\nmodule.sample.a = A\napp.title = hijack") },
            new LangCheckOptions("module.sample."));
        Assert.Equal(new[] { "app.title" }, report.Languages.Single().BadPrefix);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Unused_keys_are_found_by_scanning_source_literals()
    {
        var source = """
            var a = host.Localisation.T("row.used.header");
            var b = host.Localisation.T("module.sample.full.key");
            var c = Prefix + "family.";   // a literal ending in '.' marks a family of dynamic keys
            """;
        var report = LangCheck.Check(
            new[] { F("@code = en\nmodule.sample.name = N\nmodule.sample.description = D\nmodule.sample.row.used.header = U\nmodule.sample.full.key = K\nmodule.sample.family.one = 1\nmodule.sample.orphan = O") },
            new LangCheckOptions("module.sample.", new[] { source }));
        Assert.Equal(new[] { "module.sample.orphan" }, report.Unused); // name/description are read by the host, family.* by prefix
        Assert.True(report.HasWarnings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Without_source_texts_the_unused_check_is_skipped()
    {
        var report = LangCheck.Check(new[] { F("@code = en\na = A") });
        Assert.Empty(report.Unused);
    }

    [Fact]
    public void A_missing_English_file_and_duplicate_keys_are_errors()
    {
        var noEnglish = LangCheck.Check(new[] { F("@code = vi\na = Ă") });
        Assert.True(noEnglish.HasErrors);
        Assert.Contains(noEnglish.Problems, p => p.Contains("English"));

        var dup = LangCheck.Check(new[] { F("@code = en\na = 1\na = 2", "dup.lang") });
        Assert.Contains(dup.Problems, p => p.Contains("duplicate") && p.Contains("a"));
    }

    [Fact]
    public void The_report_text_lists_every_finding()
    {
        var report = LangCheck.Check(new[] { F("@code = en\na = A {0}\nb = B"), F("@code = vi\na = Ă\nz = ?") });
        var text = report.ToText();
        Assert.Contains("English keys: 2", text);
        Assert.Contains("missing (shown in English): b", text);
        Assert.Contains("unknown keys", text);
        Assert.Contains("placeholders differ", text);
        Assert.Contains("result: errors", text);
    }

    [Fact]
    public void A_folder_is_loaded_and_files_with_the_same_code_are_merged()
    {
        using var env = new TestEnv();
        var dir = Path.Combine(env.Root, "lang");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "en.lang"), "@code = en\na = A\n");
        File.WriteAllText(Path.Combine(dir, "en-extra.lang"), "@code = en\nb = B\n");
        var files = LangCheck.LoadDirectory(dir);
        Assert.Single(files);
        Assert.Equal(2, files[0].Text.Count);
    }

    [Fact]
    public void The_sample_modules_language_files_are_clean_even_in_strict_mode()
    {
        using var env = new TestEnv();
        var langDir = Path.Combine(env.AddModule("sample"), "lang");
        var files = LangCheck.LoadDirectory(langDir);
        var sourceDir = FindRepoFile("samples", "Rigbay.SampleModule");
        var sources = Directory.GetFiles(sourceDir, "*.cs").Select(File.ReadAllText).ToArray();
        var report = LangCheck.Check(files, new LangCheckOptions("module.sample.", sources));
        Assert.True(!report.HasErrors && !report.HasWarnings, report.ToText());
    }

    internal static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rigbay.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}
