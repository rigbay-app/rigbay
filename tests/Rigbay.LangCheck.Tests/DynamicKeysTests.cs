using System.Diagnostics;
using Rigbay.Core;
using Rigbay.LangCheckTool;
using Xunit;

namespace Rigbay.LangCheck.Tests;

public sealed class DynamicKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigbay-langcheck-" + Guid.NewGuid().ToString("N"));
    public DynamicKeysTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string text) { var p = Path.Combine(_dir, name); File.WriteAllText(p, text); return p; }

    private (LangCheckReport Report, List<string> Problems) Run(string json, string? prefix, params string[] keys)
    {
        Write("en.lang", "@code = en\n@name = English\n" + string.Join("\n", keys.Select(k => k + " = text")) + "\n");
        if (json is not null) Write(DynamicKeys.FileName, json);
        var files = Rigbay.Core.LangCheck.LoadDirectory(_dir);
        var report = Rigbay.Core.LangCheck.Check(files, new LangCheckOptions(prefix, new[] { "var x = \"nothing.here\";" }));
        var (entries, problems) = DynamicKeys.Load(_dir);
        report.Problems.AddRange(problems);
        DynamicKeys.Apply(report, entries, files.First().Text.Keys, prefix);
        return (report, problems);
    }

    [Fact]
    public void Without_a_file_nothing_changes()
    {
        var (r, _) = Run(null!, null, "a.one", "a.two");
        Assert.Equal(new[] { "a.one", "a.two" }, r.Unused);
        Assert.True(r.HasWarnings);
    }

    [Fact]
    public void A_family_silences_its_keys_only()
    {
        var (r, _) = Run("{\"dynamic\":[\"phase.*\"]}", null, "phase.a", "phase.b", "other.c");
        Assert.Equal(new[] { "other.c" }, r.Unused); // strict stays strong: no family, no use, still reported
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void An_exact_key_does_not_cover_its_neighbours()
    {
        var (r, _) = Run("{\"dynamic\":[\"one.key\"]}", null, "one.key", "one.key2", "one.key.sub");
        Assert.Equal(new[] { "one.key.sub", "one.key2" }, r.Unused);
    }

    [Fact]
    public void A_module_family_may_omit_the_module_prefix()
    {
        var (r, _) = Run("{\"dynamic\":[\"mode.*\"]}", "module.demo.", "module.demo.mode.a", "module.demo.mode.b", "module.demo.x");
        Assert.Equal(new[] { "module.demo.x" }, r.Unused);
    }

    [Fact]
    public void A_module_family_may_use_the_full_key()
    {
        var (r, _) = Run("{\"dynamic\":[\"module.demo.mode.*\"]}", "module.demo.", "module.demo.mode.a");
        Assert.Empty(r.Unused);
    }

    [Fact]
    public void A_stale_declaration_is_an_error()
    {
        var (r, _) = Run("{\"dynamic\":[\"gone.*\", \"gone.key\"]}", null, "a.b");
        Assert.True(r.HasErrors);
        Assert.Equal(2, r.Problems.Count(p => p.Contains("matches no English key")));
    }

    [Theory]
    [InlineData("{\"dynamic\":[\"*\"]}")]
    [InlineData("{\"dynamic\":[\"a*\"]}")]
    [InlineData("{\"dynamic\":[\"a.*.b\"]}")]
    [InlineData("{\"dynamic\":[\"a b\"]}")]
    [InlineData("{\"dynamic\":[\"\"]}")]
    [InlineData("{\"dynamic\":[5]}")]
    [InlineData("{\"dynamic\":\"a.*\"}")]
    [InlineData("{\"dynamics\":[\"a.*\"]}")]
    [InlineData("[\"a.*\"]")]
    [InlineData("{ not json")]
    public void A_malformed_file_is_an_error(string json)
    {
        var (r, problems) = Run(json, null, "a.b");
        Assert.NotEmpty(problems);
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var (r, problems) = Run("{ // why\n \"dynamic\": [ \"a.*\", ] }", null, "a.b");
        Assert.Empty(problems);
        Assert.Empty(r.Unused);
    }

    [Fact]
    public void A_declaration_never_hides_a_missing_translation_or_an_unknown_key()
    {
        Write("en.lang", "@code = en\n@name = English\nfam.a = x\n");
        Write("vi.lang", "@code = vi\n@name = Vi\nfam.typo = y\n");
        Write(DynamicKeys.FileName, "{\"dynamic\":[\"fam.*\"]}");
        var files = Rigbay.Core.LangCheck.LoadDirectory(_dir);
        var report = Rigbay.Core.LangCheck.Check(files, new LangCheckOptions(null, new[] { "" }));
        var (entries, _) = DynamicKeys.Load(_dir);
        DynamicKeys.Apply(report, entries, files.First(f => f.Code == "en").Text.Keys, null);
        Assert.True(report.HasErrors);                       // fam.typo is unknown in vi
        Assert.Contains("fam.a", report.Languages.First(l => l.Code == "vi").Missing);
    }

    [Fact]
    public void The_tool_exit_code_follows_strict()
    {
        Write("en.lang", "@code = en\n@name = English\nphase.a = x\nplain.b = y\n");
        var src = Path.Combine(_dir, "src"); Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.cs"), "class A { string s = \"plain.b\"; }");
        var lang = _dir;
        Assert.Equal(1, RunTool(lang, src, "--strict"));
        Write(DynamicKeys.FileName, "{\"dynamic\":[\"phase.*\"]}");
        Assert.Equal(0, RunTool(lang, src, "--strict"));
        Write(DynamicKeys.FileName, "{\"dynamic\":[\"phase.*\",\"ghost.*\"]}");
        Assert.Equal(1, RunTool(lang, src));                  // a stale entry is an error even without --strict
    }

    private static int RunTool(string lang, string src, params string[] extra)
    {
        var dll = typeof(DynamicKeys).Assembly.Location;
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll); psi.ArgumentList.Add(lang); psi.ArgumentList.Add("--source"); psi.ArgumentList.Add(src);
        foreach (var e in extra) psi.ArgumentList.Add(e);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
