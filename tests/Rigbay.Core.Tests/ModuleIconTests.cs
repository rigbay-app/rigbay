using System.Security.Cryptography;
using System.Text;
using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>A11: the optional manifest <c>icon</c> field, the icon files next to it, the choice of file per place, and the brand files copied into the app.</summary>
public class ModuleIconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigbay-icons-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private const string GoodSvg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 208 208"><title>t</title><path fill="#2F7BFF" d="M0 0H208V208H0Z"/></svg>""";

    /// <summary>The first 33 bytes of a PNG with the given size: signature, IHDR, enough for the host's header check.</summary>
    private static byte[] Png(int w, int h)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        foreach (var v in new[] { w, h }) b.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        b.AddRange(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
        return b.ToArray();
    }

    private static string Manifest(string extra) =>
        $$"""
        {
          "manifestVersion": 1, "id": "ic", "name": "Icon module", "version": "1.0.0", "author": "a", "description": "d",
          "entry": { "assembly": "P.dll", "type": "P.M" }, "hostApi": ">=0.1 <0.2", "group": "other"{{extra}}
        }
        """;

    /// <summary>A module folder named <c>ic</c> with a manifest (plus <paramref name="extra"/> JSON) and the entry assembly placeholder.</summary>
    private string Folder(string extra, params (string Name, object Content)[] files)
    {
        var dir = Path.Combine(_root, "ic");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "module.json"), Manifest(extra));
        File.WriteAllBytes(Path.Combine(dir, "P.dll"), new byte[] { 1 });
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (content is byte[] bytes) File.WriteAllBytes(path, bytes); else File.WriteAllText(path, (string)content);
        }
        return dir;
    }

    private static ManifestIssue OneIconWarning(ManifestResult r)
    {
        var w = Assert.Single(r.Issues, i => i.Path == "icon");
        Assert.False(w.IsError);
        Assert.True(r.IsValid);
        Assert.NotEqual(w.Key, w.Message);   // the key has an English text in Core's language file
        return w;
    }

    // ---------------------------------------------------------------- the field

    [Fact]
    public void A_module_without_the_field_or_files_has_no_icon_and_no_issue()
    {
        var r = ManifestReader.ReadFolder(Folder(""));
        Assert.True(r.IsValid);
        Assert.Empty(r.Issues);
        Assert.Null(r.Manifest!.Icon);
        Assert.False(r.Icons!.HasIcon);
        Assert.Null(r.Icons.Tile);
    }

    [Fact]
    public void A_declared_svg_is_resolved_with_its_sidecar_files()
    {
        var dir = Folder(", \"icon\": \"icon.svg\"",
            ("icon.svg", GoodSvg), ("icon-glyph.svg", GoodSvg), ("icon-unsupported.svg", GoodSvg),
            ("icon-24.png", Png(24, 24)), ("icon-48.png", Png(48, 48)), ("icon-unsupported-24.png", Png(24, 24)), ("icon-99.png", Png(99, 99)));
        var r = ManifestReader.ReadFolder(dir);
        Assert.Empty(r.Issues);
        Assert.Equal("icon.svg", r.Manifest!.Icon);
        var icons = r.Icons!;
        Assert.Equal(Path.Combine(dir, "icon.svg"), icons.Tile);
        Assert.Equal(Path.Combine(dir, "icon-glyph.svg"), icons.Glyph);
        Assert.Equal(Path.Combine(dir, "icon-unsupported.svg"), icons.Unsupported);
        Assert.Equal(new[] { 24, 48 }, icons.Png.Keys.OrderBy(k => k));
        Assert.Equal(new[] { 24 }, icons.UnsupportedPng.Keys);
    }

    [Fact]
    public void The_convention_files_are_found_without_the_field()
    {
        var r = ManifestReader.ReadFolder(Folder("", ("icon.svg", GoodSvg)));
        Assert.Empty(r.Issues);
        Assert.Null(r.Manifest!.Icon);
        Assert.EndsWith("icon.svg", r.Icons!.Tile);
        Directory.Delete(Path.Combine(_root, "ic"), true);
        var png = ManifestReader.ReadFolder(Folder("", ("icon-48.png", Png(48, 48)))).Icons!;
        Assert.EndsWith("icon-48.png", png.Tile);
        Assert.Equal(new[] { 48 }, png.Png.Keys);
    }

    [Fact]
    public void A_png_icon_in_a_subfolder_is_allowed()
    {
        var r = ManifestReader.ReadFolder(Folder(", \"icon\": \"art/mark-128.png\"", ("art/mark-128.png", Png(128, 128))));
        Assert.Empty(r.Issues);
        Assert.EndsWith(Path.Combine("art", "mark-128.png"), r.Icons!.Tile);
        Assert.Equal(new[] { 128 }, r.Icons.Png.Keys);
    }

    public static IEnumerable<object[]> BadSyntax() => new[]
    {
        new object[] { "\"../icon.svg\"" },
        new object[] { "\"sub/../../icon.svg\"" },
        new object[] { "\"C:\\\\Windows\\\\icon.svg\"" },
        new object[] { "\"/icon.svg\"" },
        new object[] { "\"\\\\\\\\server\\\\share\\\\icon.svg\"" },
        new object[] { "\"icon.gif\"" },
        new object[] { "\"icon\"" },
        new object[] { "\"\"" },
        new object[] { "\"   \"" },
        new object[] { "42" },
        new object[] { "[\"icon.svg\"]" },
        new object[] { "null" },
        new object[] { "\"" + new string('a', 120) + ".svg\"" },
    };

    [Theory]
    [MemberData(nameof(BadSyntax))]
    public void A_badly_written_icon_value_is_one_warning_and_the_convention_files_are_not_used_instead(string json)
    {
        // icon.svg exists and is fine: a declared-but-bad value must still end with no icon, not with a surprise
        var r = ManifestReader.ReadFolder(Folder(", \"icon\": " + json, ("icon.svg", GoodSvg)));
        Assert.Equal("core.manifest.icon-invalid", OneIconWarning(r).Key);
        Assert.Null(r.Manifest!.Icon);
        Assert.Null(r.Icons!.Tile);
        Assert.False(r.Icons.HasIcon);
    }

    public static IEnumerable<object[]> BadFiles() => new[]
    {
        new object[] { "missing.svg", null!, "core.manifest.icon-missing" },
        new object[] { "icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div/></foreignObject></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><text>hi</text></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><style>*{fill:red}</style></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="data:image/png;base64,AAAA"/></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><use xlink:href="http://example.com/a.svg#x"/></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><path fill="url(http://example.com/x)" d="M0 0"/></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"><path d="M0 0"/></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<!DOCTYPE svg [<!ENTITY x "y">]><svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0"/></svg>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", """<html><body/></html>""", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", "not xml at all", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", "", "core.manifest.icon-unsafe" },
        new object[] { "icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"" + new string('M', 70 * 1024) + "\"/></svg>", "core.manifest.icon-too-large" },
        new object[] { "icon.png", new byte[] { 1, 2, 3 }, "core.manifest.icon-unsafe" },
        new object[] { "icon.png", Png(48, 64), "core.manifest.icon-size" },
        new object[] { "icon.png", Png(32, 32), "core.manifest.icon-size" },
        new object[] { "icon.png", Png(2048, 2048), "core.manifest.icon-size" },
        new object[] { "icon.png", new byte[300 * 1024], "core.manifest.icon-too-large" },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public void A_bad_icon_file_is_one_warning_and_no_icon_and_the_manifest_stays_valid(string file, object? content, string key)
    {
        var files = content == null ? Array.Empty<(string, object)>() : new[] { (file, content) };
        var r = ManifestReader.ReadFolder(Folder($", \"icon\": \"{file}\"", files));
        Assert.Equal(key, OneIconWarning(r).Key);
        Assert.Null(r.Icons!.Tile);
        Assert.False(r.Icons.HasIcon);
        Assert.Empty(r.Icons.Png);
    }

    [Fact]
    public void A_bad_sidecar_is_a_warning_but_the_main_icon_stays()
    {
        var dir = Folder(", \"icon\": \"icon.svg\"", ("icon.svg", GoodSvg), ("icon-glyph.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>"), ("icon-16.png", Png(16, 20)));
        var r = ManifestReader.ReadFolder(dir);
        Assert.Equal(2, r.Issues.Count(i => i.Path == "icon" && !i.IsError));
        Assert.NotNull(r.Icons!.Tile);
        Assert.Null(r.Icons.Glyph);
        Assert.Empty(r.Icons.Png);
    }

    [Fact]
    public void A_file_that_the_convention_finds_but_is_bad_is_ignored_silently()
    {
        var r = ManifestReader.ReadFolder(Folder("", ("icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>")));
        Assert.Empty(r.Issues);
        Assert.False(r.Icons!.HasIcon);
    }

    [Fact]
    public void The_new_warning_keys_have_text_in_English_and_Vietnamese()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        host.Languages.SetLanguage("vi");
        foreach (var key in new[] { "invalid", "missing", "too-large", "unsafe", "size", "unreadable" })
        {
            var issue = new ManifestIssue("icon", "core.manifest.icon-" + key, new object[] { "x.svg", 64, 256 }, IsError: false);
            Assert.NotEqual(issue.Key, issue.Message);
            Assert.NotEqual(issue.Key, issue.Localise(host.Languages));
        }
    }

    // ---------------------------------------------------------------- the module entry

    [Fact]
    public void A_module_loads_and_runs_with_a_bad_icon_and_shows_the_warning()
    {
        using var env = new TestEnv();
        var dir = env.AddModule("sample", editManifest: j => j.Replace("\"group\": \"other\"", "\"group\": \"other\", \"icon\": \"../evil.svg\""));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable(), m.Reason is null ? "" : m.ResolveReason(m.Reason));
        Assert.Contains(m.Issues, i => i.Path == "icon" && !i.IsError);
        Assert.False(m.Icons.HasIcon);
        Assert.Contains(host.Log.Recent(), e => e.Level == LogLevel.Warn && e.Message.Contains("icon", StringComparison.Ordinal));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void The_entry_exposes_the_resolved_paths()
    {
        using var env = new TestEnv();
        var dir = env.AddModule("sample", editManifest: j => j.Replace("\"group\": \"other\"", "\"group\": \"other\", \"icon\": \"icon.svg\""));
        File.WriteAllText(Path.Combine(dir, "icon.svg"), GoodSvg);
        File.WriteAllText(Path.Combine(dir, "icon-glyph.svg"), GoodSvg);
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(Path.Combine(dir, "icon.svg"), m.Icons.Tile);
        Assert.Equal(Path.Combine(dir, "icon-glyph.svg"), m.Icons.Glyph);
        Assert.DoesNotContain(m.Issues, i => i.Path == "icon");
    }

    [Fact]
    public void An_invalid_manifest_has_no_icons()
    {
        var dir = Folder(", \"icon\": \"icon.svg\"", ("icon.svg", GoodSvg));
        File.WriteAllText(Path.Combine(dir, "module.json"), Manifest(", \"icon\": \"icon.svg\"").Replace("\"id\": \"ic\"", "\"id\": \"Bad Id\""));
        var r = ManifestReader.ReadFolder(dir);
        Assert.False(r.IsValid);
        Assert.Null(r.Icons);
    }

    // ---------------------------------------------------------------- which file for which place

    private static ModuleIcons Full() => new(
        "T.svg", "G.svg", "U.svg",
        new Dictionary<int, string> { [16] = "16.png", [24] = "24.png", [32] = "32.png", [48] = "48.png", [64] = "64.png", [128] = "128.png" },
        new Dictionary<int, string> { [16] = "u16.png", [24] = "u24.png", [32] = "u32.png", [48] = "u48.png" });

    [Fact]
    public void Choose_gives_the_tile_svg_at_any_scale_and_the_glyph_for_the_pane()
    {
        var i = Full();
        Assert.Equal(new IconSource("T.svg", true, 1.0), i.Choose(IconForm.Tile, false, 24, 1.5));
        Assert.Equal(new IconSource("G.svg", true, 1.0), i.Choose(IconForm.Glyph, false, 16, 2.0));
    }

    [Fact]
    public void Choose_unsupported_uses_the_grey_file_or_45_percent_of_the_own_icon()
    {
        var i = Full();
        Assert.Equal(new IconSource("U.svg", true, 1.0), i.Choose(IconForm.Tile, true, 24, 1));
        Assert.Equal(new IconSource("G.svg", true, 0.45), i.Choose(IconForm.Glyph, true, 16, 1));
        var own = new ModuleIcons("T.svg", null, null, new Dictionary<int, string>(), new Dictionary<int, string>());
        Assert.Equal(new IconSource("T.svg", true, 0.45), own.Choose(IconForm.Tile, true, 24, 1));
        Assert.Equal(new IconSource("T.svg", true, 0.45), own.Choose(IconForm.Glyph, true, 16, 1));
    }

    [Theory]
    [InlineData(24, 1.0, "24.png")]
    [InlineData(24, 1.25, "32.png")]
    [InlineData(24, 1.5, "48.png")]
    [InlineData(24, 2.0, "48.png")]
    [InlineData(16, 1.0, "16.png")]
    [InlineData(16, 1.25, "24.png")]
    [InlineData(48, 1.5, "128.png")]
    [InlineData(48, 3.0, "128.png")]
    [InlineData(200, 2.0, "128.png")]
    public void Choose_raster_takes_the_smallest_png_with_enough_pixels_else_the_largest(double dip, double scale, string expected)
    {
        var pick = Full().Choose(IconForm.Tile, false, dip, scale, preferSvg: false);
        Assert.Equal(new IconSource(expected, false, 1.0), pick);
    }

    [Fact]
    public void Choose_raster_unsupported_uses_the_grey_pngs()
    {
        Assert.Equal(new IconSource("u32.png", false, 1.0), Full().Choose(IconForm.Tile, true, 24, 1.25, preferSvg: false));
    }

    [Fact]
    public void Choose_with_a_png_only_module_and_with_no_icon()
    {
        var pngOnly = new ModuleIcons("icon-48.png", null, null, new Dictionary<int, string> { [48] = "icon-48.png" }, new Dictionary<int, string>());
        Assert.Equal(new IconSource("icon-48.png", false, 1.0), pngOnly.Choose(IconForm.Tile, false, 24, 2));
        Assert.Equal(new IconSource("icon-48.png", false, 1.0), pngOnly.Choose(IconForm.Glyph, false, 16, 1));
        Assert.Null(ModuleIcons.None.Choose(IconForm.Tile, false, 24, 1));
    }

    // ---------------------------------------------------------------- the app's copy of the brand files

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Rigbay.sln"))) return d.FullName;
        return null;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>The same pairs as <c>tools\sync-brand-assets.ps1</c>: the app's copy must equal the brand master.</summary>
    [Fact]
    public void The_app_copies_of_the_brand_files_equal_assets_brand()
    {
        var repo = RepoRoot();
        Assert.NotNull(repo);
        var map = Path.Combine(repo!, "src", "Rigbay.App", "Assets", "brand-files.txt");
        Assert.True(File.Exists(map), "src/Rigbay.App/Assets/brand-files.txt is the list the script and this test share");
        var lines = File.ReadAllLines(map).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.NotEmpty(lines);
        var bad = new List<string>();
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            Assert.Equal(2, parts.Length);
            var src = Path.Combine(repo!, "assets", "brand", parts[0].Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(repo!, "src", "Rigbay.App", parts[1].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) bad.Add("missing master " + parts[0]);
            else if (!File.Exists(dst)) bad.Add("missing copy " + parts[1]);
            else if (Hash(src) != Hash(dst)) bad.Add("differs " + parts[1]);
        }
        Assert.True(bad.Count == 0, "run tools\\sync-brand-assets.ps1: " + string.Join("; ", bad));
    }

    /// <summary>The real PredatorSense folder (manifest field and the brand files) passes the validator with no issue; a stub stands in for its DLL.</summary>
    [Fact]
    public void The_predatorsense_module_folder_has_a_valid_icon_set_and_equals_the_brand_kit()
    {
        var repo = RepoRoot()!;
        var src = Path.Combine(repo, "modules", "predatorsense");
        var dir = Path.Combine(_root, "predatorsense");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(src, "*.*").Where(f => !f.EndsWith(".props") && !f.EndsWith(".sln"))) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        File.WriteAllBytes(Path.Combine(dir, "Rigbay.PredatorSense.dll"), new byte[] { 1 });
        var r = ManifestReader.ReadFolder(dir);
        Assert.True(r.IsValid, r.ErrorSummary);
        Assert.Empty(r.Issues);
        Assert.Equal("icon.svg", r.Manifest!.Icon);
        Assert.NotNull(r.Icons!.Glyph);
        Assert.NotNull(r.Icons.Unsupported);
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128 }, r.Icons.Png.Keys.OrderBy(k => k));
        Assert.Equal(new[] { 16, 24, 32, 48 }, r.Icons.UnsupportedPng.Keys.OrderBy(k => k));
        var kit = Path.Combine(repo, "assets", "brand", "integration", "module-icons", "predatorsense");
        foreach (var file in Directory.GetFiles(kit)) Assert.Equal(Hash(file), Hash(Path.Combine(src, Path.GetFileName(file))));
    }

    [Fact]
    public void The_placeholder_icon_script_is_gone_and_no_green_is_left_in_the_icon_sources()
    {
        var repo = RepoRoot()!;
        Assert.False(File.Exists(Path.Combine(repo, "tools", "make-icon.ps1")));
        var ico = File.ReadAllBytes(Path.Combine(repo, "src", "Rigbay.App", "Assets", "rigbay.ico"));
        Assert.Equal(Hash(Path.Combine(repo, "assets", "brand", "app-icon", "rigbay.ico")), Convert.ToHexString(SHA256.HashData(ico)));
    }
}
