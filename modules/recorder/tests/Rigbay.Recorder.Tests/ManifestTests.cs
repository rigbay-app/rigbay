using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rigbay.Recorder.Tests;

/// <summary>The manifest is read by the host before any code runs; a typo there makes the module "invalid" and nothing else would notice.</summary>
public class ManifestTests
{
    private static string ModuleFolder => Path.Combine(AppContext.BaseDirectory, "module");

    private static JsonElement Manifest()
    {
        var text = File.ReadAllText(Path.Combine(ModuleFolder, "module.json"));
        return JsonDocument.Parse(text).RootElement.Clone();   // throws when it is not valid JSON, which is the first thing the host does
    }

    [Fact]
    public void The_manifest_is_valid_json_with_the_fields_the_host_requires()
    {
        var m = Manifest();
        Assert.Equal(1, m.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("recorder", m.GetProperty("id").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", m.GetProperty("version").GetString());
        Assert.Equal("monitoring", m.GetProperty("group").GetString());
        Assert.Equal(">=0.1 <0.2", m.GetProperty("hostApi").GetString());
        Assert.InRange(m.GetProperty("description").GetString()!.Length, 1, 200);
        Assert.InRange(m.GetProperty("name").GetString()!.Length, 1, 60);
        var entry = m.GetProperty("entry");
        Assert.Equal(typeof(RecorderModule).FullName, entry.GetProperty("type").GetString());
        Assert.Equal(typeof(RecorderModule).Assembly.GetName().Name + ".dll", entry.GetProperty("assembly").GetString());
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, entry.GetProperty("assembly").GetString()!)));
        Assert.Equal("lang", m.GetProperty("languages").GetString());
    }

    [Fact]
    public void The_registry_reads_it_declares_are_the_ones_the_code_makes()
    {
        var registry = Manifest().GetProperty("requires").GetProperty("registry").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(registry, r => r.StartsWith(@"HKLM\HARDWARE\DESCRIPTION\System\BIOS"));
        Assert.Contains(registry, r => r.StartsWith(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0"));
        Assert.Empty(Manifest().GetProperty("requires").GetProperty("services").EnumerateArray());
        Assert.Empty(Manifest().GetProperty("requires").GetProperty("network").EnumerateArray());
    }

    [Fact]
    public void The_name_and_description_in_the_language_file_match_the_manifest_text()
    {
        var m = Manifest();
        var en = Rigbay.Recorder.Tests.FakeLocalisation.Load("en");
        Assert.Equal(m.GetProperty("name").GetString(), en["module.recorder.name"]);
        Assert.Equal(m.GetProperty("description").GetString(), en["module.recorder.description"]);
    }

    [Theory]
    [InlineData("icon.svg")]
    [InlineData("icon-glyph.svg")]
    public void The_icons_are_plain_drawings_the_host_accepts(string file)
    {
        var path = Path.Combine(ModuleFolder, file);
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
        if (file == "icon.svg") Assert.Equal("0 0 208 208", doc.Root.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void The_manifest_names_the_icon_that_ships()
    {
        Assert.Equal("icon.svg", Manifest().GetProperty("icon").GetString());
        Assert.True(File.Exists(Path.Combine(ModuleFolder, "icon.svg")));
    }
}

public class LanguageFileTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void A_backslash_in_a_language_file_is_written_twice_because_the_host_drops_a_single_one(string language)
    {
        var raw = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "lang", language + ".lang")).Where(l => !l.TrimStart().StartsWith('#'));
        Assert.Empty(raw.Where(HasLoneBackslash).ToList());   // "D:\Logs" would show as "D:Logs"; every backslash must be doubled, or be the start of \n or \t
    }

    /// <summary>True when a backslash is followed by something other than another backslash, n or t.</summary>
    private static bool HasLoneBackslash(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '\\') continue;
            if (i + 1 >= line.Length || line[i + 1] is not ('\\' or 'n' or 't')) return true;
            i++;   // skip the second character of the pair
        }
        return false;
    }

    [Fact]
    public void The_folder_texts_show_real_paths_after_the_host_unescapes_them()
    {
        var en = FakeLocalisation.Load("en");
        Assert.Equal(@"Documents\Rigbay\logs (default)", en["module.recorder.row.folder.placeholder"]);
        Assert.Contains(@"D:\Logs", en["module.recorder.error.folder-absolute"]);
        Assert.Contains(@"Documents\Rigbay\logs", FakeLocalisation.Load("vi")["module.recorder.row.folder.placeholder"]);
    }
}
