using System.Text;
using Xunit;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>Task F1: mojibake detection in language files, and the EffectKind of a HardwareEffect.</summary>
public sealed class F1Tests
{
    // ---------------------------------------------------------------- mojibake

    /// <summary>What a tool that reads UTF-8 bytes as Windows-1252 makes of a text.</summary>
    static F1Tests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string Garble(string text) => Encoding.GetEncoding(1252).GetString(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("phải là tên tệp hoặc đường dẫn")]
    [InlineData("Thay đổi phần cứng của bạn?")]
    [InlineData("Hoàn tác")]
    [InlineData("Ứng dụng đã cập nhật – “xong”")]
    [InlineData("Ünïcode café, naïve façade, señor, Åre, Çelik, Ångström")]
    [InlineData("Changes your hardware. Rigbay asks before it applies.")]
    public void Good_text_is_not_flagged(string text) => Assert.False(LangCheck.LooksGarbled(text));

    [Theory]
    [InlineData("phải là tên tệp hoặc đường dẫn")]
    [InlineData("Thay đổi phần cứng của bạn?")]
    [InlineData("Hoàn tác thay đổi")]
    [InlineData("Ứng dụng – “xong”")]
    [InlineData("café naïve")]
    [InlineData("Ångström")]
    public void The_typical_Latin1_reading_of_UTF8_is_flagged(string original) => Assert.True(LangCheck.LooksGarbled(Garble(original)), Garble(original));

    [Fact]
    public void The_pure_ISO_8859_1_reading_is_flagged_too() => Assert.True(LangCheck.LooksGarbled(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("Thay đổi phần cứng"))));

    [Fact]
    public void A_replacement_character_is_flagged() => Assert.True(LangCheck.LooksGarbled("cài đ�t"));

    [Fact]
    public void LangCheck_reports_garbled_text_as_an_error_with_the_key()
    {
        var en = LanguageFile.Parse(new StringReader("# code: en\nk.a = Hello\nk.b = Back\n"), "en.lang");
        en.Code = "en";
        var vi = LanguageFile.Parse(new StringReader("# code: vi\nk.a = " + Garble("Xin chào") + "\nk.b = Quay lại\n"), "vi.lang");
        vi.Code = "vi";
        var report = LangCheck.Check(new[] { en, vi });
        Assert.True(report.HasErrors);
        Assert.Equal(new[] { "k.a" }, report.Languages.Single(l => l.Code == "vi").Mojibake);
        Assert.Contains("garbled", report.ToText());
    }

    [Fact]
    public void A_backslash_that_is_not_an_escape_is_a_problem_and_a_doubled_one_is_a_backslash()
    {
        var bad = LanguageFile.Parse(new StringReader("k.a = The namespace root\\WMI is missing\n"), "en.lang");
        Assert.Single(bad.Problems);
        Assert.Contains("'W'", bad.Problems[0]);
        Assert.Equal("The namespace rootWMI is missing", bad.Text["k.a"]);   // what the user used to see

        var good = LanguageFile.Parse(new StringReader("k.a = The namespace root\\\\WMI is missing\\nsecond line\\ttab\n"), "en.lang");
        Assert.Empty(good.Problems);
        Assert.Equal("The namespace root\\WMI is missing\nsecond line\ttab", good.Text["k.a"]);
    }

    /// <summary>Every language file in the repo that the build ships must be free of mojibake (Core, App, sample, modules, demo modules).</summary>
    [Fact]
    public void No_language_file_in_the_repo_has_mojibake()
    {
        var root = FindRepoRoot();
        var bad = new List<string>();
        int files = 0;
        // IgnoreInaccessible: the install tests and E2E runs leave admin-only scratch folders under tools\out, which a normal shell cannot list (E2E1)
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(root, "*.lang", walk))
        {
            var rel = Path.GetRelativePath(root, path);
            if (rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || rel.StartsWith(".claude") || rel.StartsWith("spike")
                || rel.StartsWith("tools" + Path.DirectorySeparatorChar + "out")) continue;   // tools\out is build output and scratch copies, not source
            files++;
            var file = LanguageFile.ParseFile(path);
            bad.AddRange(file.Problems);   // includes a backslash that is not an escape
            foreach (var (key, value) in file.Text)
                if (LangCheck.LooksGarbled(value)) bad.Add($"{rel}: {key}");
        }
        Assert.True(files >= 10, "found only " + files + " language files");
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }

    // ---------------------------------------------------------------- module icons

    [Theory]
    [InlineData("modules/xtu/Rigbay.Module.Xtu")]
    [InlineData("modules/steambypass")]
    [InlineData("modules/recorder")]
    [InlineData("modules/predatorsense")]
    public void Every_shipped_module_folder_has_a_valid_complete_icon_set(string folder)
    {
        var path = Path.Combine(FindRepoRoot(), folder.Replace('/', Path.DirectorySeparatorChar));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "module.json")));
        var declared = doc.RootElement.GetProperty("icon").GetString();
        var issues = new List<ManifestIssue>();

        var icons = ModuleIconResolver.Resolve(path, declared, issues);

        Assert.Empty(issues);
        Assert.NotNull(icons.Tile);
        Assert.NotNull(icons.Glyph);
        Assert.NotNull(icons.Unsupported);
        Assert.Contains(16, icons.Png.Keys);
        Assert.Contains(128, icons.Png.Keys);
        Assert.Contains(16, icons.UnsupportedPng.Keys);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rigbay.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Rigbay.sln not found above " + AppContext.BaseDirectory);
    }

    // ---------------------------------------------------------------- EffectKind

    private static (RigbayHost Host, ModuleEntry Module, FakeApproval Prompt) StartSample(TestEnv env)
    {
        var host = env.StartHost("sample");
        var prompt = new FakeApproval();
        host.ApprovalPrompt = prompt;
        return (host, host.Find("sample")!, prompt);
    }

    [Fact]
    public void The_default_kind_is_Hardware_so_existing_modules_keep_their_wording()
    {
        Assert.Equal(EffectKind.Hardware, new HardwareEffect("effect.x").Kind);
        Assert.Equal(EffectKind.SystemSetting, new HardwareEffect("effect.x") { Kind = EffectKind.SystemSetting }.Kind);
    }

    [Fact]
    public async Task The_kind_reaches_the_approval_request_for_apply_and_revert()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = m.Page!.EnumerateRows().OfType<ChoiceRow>().Single(r => r.HeaderKey == "row.profile.header");
        var setting = row with { HardwareEffect = row.HardwareEffect! with { Kind = EffectKind.SystemSetting } };
        var editor = RowEditors.For(m, setting);

        await editor.SubmitAsync("max");
        await editor.RevertAsync();

        Assert.Equal(2, prompt.Requests.Count);
        Assert.All(prompt.Requests, r => Assert.Equal(EffectKind.SystemSetting, r.Kind));

        var hardware = RowEditors.For(m, row);
        await hardware.SubmitAsync("max");
        Assert.Equal(EffectKind.Hardware, prompt.Requests[^1].Kind);
        host.Shutdown();
    }

    [Fact]
    public async Task A_module_started_approval_carries_the_kind()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        await host.RequestApprovalAsync(m, new HardwareEffect("effect.profile") { Kind = EffectKind.SystemSetting }, null, CancellationToken.None);
        Assert.Equal(EffectKind.SystemSetting, prompt.Requests.Single().Kind);
        host.Shutdown();
    }

    [Fact]
    public void The_neutral_texts_exist_in_both_languages_for_Core()
    {
        var assembly = typeof(RigbayHost).Assembly;
        foreach (var code in new[] { "en", "vi" })
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream($"Rigbay.Core.lang.{code}.lang")!);
            var file = LanguageFile.Parse(reader, code);
            Assert.True(file.Text.ContainsKey("core.revert.needs-approval-setting"), code);
            Assert.DoesNotContain("hardware", file.Text["core.revert.needs-approval-setting"], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("phần cứng", file.Text["core.revert.needs-approval-setting"], StringComparison.OrdinalIgnoreCase);
        }
    }
}
