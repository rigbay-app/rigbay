using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Core.Tests;

public class ManifestTests
{
    private const string Good = """
    {
      "manifestVersion": 1, "id": "steam-access", "name": "Steam access", "version": "0.3.1-beta.1",
      "author": "A. Person", "description": "Workaround for networks that block Steam.",
      "entry": { "assembly": "SteamAccess.dll", "type": "SteamAccess.SteamModule" },
      "hostApi": ">=0.1 <0.2", "group": "network",
      "requires": { "admin": true, "network": ["tcp:18081"], "registry": [], "services": ["Dnscache"] },
      "languages": "lang", "homepage": "https://github.com/rigbay-app/module-steam-access"
    }
    """;

    [Fact]
    public void A_complete_manifest_is_read_in_full()
    {
        var result = ManifestReader.Parse(Good, "steam-access");
        Assert.True(result.IsValid, result.ErrorSummary);
        var m = result.Manifest!;
        Assert.Equal("steam-access", m.Id);
        Assert.Equal(ModuleGroup.Network, m.Group);
        Assert.Equal("SteamAccess.dll", m.EntryAssembly);
        Assert.True(m.Requires.Admin);
        Assert.Equal(new[] { "tcp:18081" }, m.Requires.Network);
        Assert.Equal(new[] { "Dnscache" }, m.Requires.Services);
        Assert.True(m.HostApi.Contains(new Version(0, 1)));
        Assert.False(m.HostApi.Contains(new Version(0, 2)));
    }

    [Fact]
    public void Optional_fields_have_defaults()
    {
        var json = """
        { "manifestVersion": 1, "id": "tiny", "name": "T", "version": "1.0.0", "author": "a", "description": "d",
          "entry": { "assembly": "Tiny.dll", "type": "Tiny.Module" }, "hostApi": "=0.1", "group": "other" }
        """;
        var m = ManifestReader.Parse(json, "tiny").Manifest!;
        Assert.Equal("lang", m.LanguagesFolder);
        Assert.False(m.Requires.Admin);
        Assert.Empty(m.Requires.Network);
        Assert.Null(m.Homepage);
    }

    [Theory]
    [InlineData("\"id\": \"steam-access\"", "\"id\": \"Steam Access\"", "id")]
    [InlineData("\"id\": \"steam-access\"", "\"id\": \"steam--access\"", "id")]
    [InlineData("\"id\": \"steam-access\"", "\"id\": \"x\"", "id")]
    [InlineData("\"id\": \"steam-access\"", "\"id\": \"host\"", "id")]
    [InlineData("\"version\": \"0.3.1-beta.1\"", "\"version\": \"1.0\"", "version")]
    [InlineData("\"version\": \"0.3.1-beta.1\"", "\"version\": \"01.0.0\"", "version")]
    [InlineData("\"group\": \"network\"", "\"group\": \"Network\"", "group")]
    [InlineData("\"group\": \"network\"", "\"group\": \"gaming\"", "group")]
    [InlineData("\"hostApi\": \">=0.1 <0.2\"", "\"hostApi\": \"latest\"", "hostApi")]
    [InlineData("\"hostApi\": \">=0.1 <0.2\"", "\"hostApi\": \"\"", "hostApi")]
    [InlineData("\"assembly\": \"SteamAccess.dll\"", "\"assembly\": \"..\\\\evil.dll\"", "entry.assembly")]
    [InlineData("\"assembly\": \"SteamAccess.dll\"", "\"assembly\": \"sub/x.dll\"", "entry.assembly")]
    [InlineData("\"assembly\": \"SteamAccess.dll\"", "\"assembly\": \"SteamAccess.exe\"", "entry.assembly")]
    [InlineData("\"assembly\": \"SteamAccess.dll\"", "\"assembly\": \"Rigbay.Abstractions.dll\"", "entry.assembly")]
    [InlineData("\"type\": \"SteamAccess.SteamModule\"", "\"type\": \"not a type\"", "entry.type")]
    [InlineData("\"manifestVersion\": 1", "\"manifestVersion\": 2", "manifestVersion")]
    [InlineData("\"languages\": \"lang\"", "\"languages\": \"..\\\\other\"", "languages")]
    [InlineData("\"homepage\": \"https://github.com/rigbay-app/module-steam-access\"", "\"homepage\": \"ftp://x\"", "homepage")]
    [InlineData("\"admin\": true", "\"admin\": \"yes\"", "requires.admin")]
    public void A_bad_field_is_reported_by_its_path(string from, string to, string path)
    {
        var json = Good.Replace(from, to);
        Assert.NotEqual(Good, json);
        var result = ManifestReader.Parse(json, "steam-access");
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.IsError && i.Path == path);
    }

    [Fact]
    public void The_id_must_equal_the_folder_name()
    {
        var result = ManifestReader.Parse(Good, "some-other-folder");
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path == "id" && i.Message.Contains("folder"));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var result = ManifestReader.Parse("""{ "manifestVersion": 1, "id": "BAD", "name": "", "version": "x" }""", null);
        Assert.False(result.IsValid);
        var paths = result.Issues.Select(i => i.Path).ToHashSet();
        Assert.Superset(new HashSet<string> { "id", "name", "version", "author", "description", "entry", "hostApi", "group" }, paths);
    }

    [Fact]
    public void Unknown_fields_are_a_warning_not_an_error()
    {
        var result = ManifestReader.Parse(Good.Replace("\"languages\"", "\"futureThing\": 1, \"languages\""), "steam-access");
        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => !i.IsError && i.Path == "futureThing");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("{ \"id\": ")]
    public void Garbage_never_throws(string text)
    {
        var result = ManifestReader.Parse(text, null);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void A_missing_file_or_entry_assembly_is_reported()
    {
        using var env = new TestEnv();
        var none = ManifestReader.ReadFolder(Path.Combine(env.Root, "nothing"));
        Assert.False(none.IsValid);

        var folder = Path.Combine(env.ModulesRoot, "steam-access");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "module.json"), Good);
        var noDll = ManifestReader.ReadFolder(folder);
        Assert.False(noDll.IsValid);
        Assert.Contains(noDll.Issues, i => i.Path == "entry.assembly" && i.Message.Contains("not in the module folder"));
    }

    [Fact]
    public void The_sample_modules_manifest_is_valid()
    {
        using var env = new TestEnv();
        var folder = env.AddModule("sample");
        var result = ManifestReader.ReadFolder(folder);
        Assert.True(result.IsValid, result.ErrorSummary);
        Assert.Equal(ModuleGroup.Other, result.Manifest!.Group);
    }
}

public class ApiVersionTests
{
    [Theory]
    [InlineData(">=0.1 <0.2", "0.1", true)]
    [InlineData(">=0.1 <0.2", "0.2", false)]
    [InlineData(">=0.1 <0.2", "0.0", false)]
    [InlineData(">=0.1", "3.7", true)]
    [InlineData(">0.1", "0.1", false)]
    [InlineData(">0.1", "0.2", true)]
    [InlineData("<=0.1", "0.1", true)]
    [InlineData("<=0.1", "0.2", false)]
    [InlineData("=0.1", "0.1", true)]
    [InlineData("0.1", "0.1", true)]
    [InlineData("0.1", "0.2", false)]
    [InlineData(">=1.0 <2.0", "1.9", true)]
    [InlineData(">=1.0 <2.0", "2.0", false)]
    [InlineData(">=0.1.0   <0.2.0", "0.1", true)]
    public void Ranges_are_checked(string range, string version, bool expected)
    {
        Assert.True(ApiVersionRange.TryParse(range, out var parsed, out var error), error);
        Assert.Equal(expected, parsed.Contains(Version.Parse(version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(">=")]
    [InlineData(">=1")]
    [InlineData(">=a.b")]
    [InlineData("~0.1")]
    [InlineData(">=0.1.2.3")]
    [InlineData("0.1 || 0.2")]
    public void Bad_ranges_are_refused_with_a_reason(string range)
    {
        Assert.False(ApiVersionRange.TryParse(range, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrEmpty(error));
    }

    // The host's third number is a revision; a range that does not write one means any revision.
    [Theory]
    [InlineData(">=0.1 <0.2", "0.1.0", true)]
    [InlineData(">=0.1 <0.2", "0.1.7", true)]
    [InlineData(">=0.1 <0.2", "0.1", true)]
    [InlineData(">=0.1.0 <0.2", "0.1.0", true)]
    [InlineData(">=0.1.2 <0.2", "0.1.1", false)]
    [InlineData(">=0.1.2 <0.2", "0.1.2", true)]
    [InlineData(">=0.1.2 <0.2", "0.1.9", true)]
    [InlineData(">=0.1.2 <0.2", "0.1", false)]
    [InlineData(">=0.1.2 <0.2", "0.2.0", false)]
    [InlineData(">=0.1.2 <0.2", "0.2.5", false)]
    [InlineData(">=0.1.2", "0.2.0", true)]
    [InlineData(">=0.1.2", "1.0.0", true)]
    [InlineData("=0.1", "0.1.5", true)]
    [InlineData("=0.1.2", "0.1.2", true)]
    [InlineData("=0.1.2", "0.1.3", false)]
    [InlineData("=0.1.2", "0.1.1", false)]
    [InlineData(">0.1", "0.1.5", false)]
    [InlineData(">0.1.2", "0.1.2", false)]
    [InlineData(">0.1.2", "0.1.3", true)]
    [InlineData(">0.1.2", "0.2.0", true)]
    [InlineData("<=0.1", "0.1.9", true)]
    [InlineData("<=0.1.2", "0.1.2", true)]
    [InlineData("<=0.1.2", "0.1.3", false)]
    [InlineData("<0.1.2", "0.1.1", true)]
    [InlineData("<0.1.2", "0.1.2", false)]
    [InlineData("<0.1.2", "0.0.9", true)]
    [InlineData(">=0.1.1 <=0.1.3", "0.1.2", true)]
    [InlineData(">=0.1.1 <=0.1.3", "0.1.4", false)]
    [InlineData(">=0.1.02 <0.2", "0.1.2", true)]
    [InlineData("0.1.2", "0.1.2", true)]
    public void Revisions_count_only_when_the_range_writes_one(string range, string host, bool expected)
    {
        Assert.True(ApiVersionRange.TryParse(range, out var parsed, out var error), error);
        Assert.Equal(expected, parsed.Contains(Version.Parse(host)));
    }

    [Theory]
    [InlineData(">=0.1.3 <0.2", "0.1.2", "0.1.3")]
    [InlineData(">=0.1.3 <0.2", "0.1", "0.1.3")]
    [InlineData(">=0.1.2 >=0.1.5 <0.2", "0.1.1", "0.1.5")]
    [InlineData(">0.1.2 <0.2", "0.1.2", "0.1.3")]
    [InlineData("=0.1.4", "0.1.2", "0.1.4")]
    [InlineData(">=0.1.3 <0.2", "0.1.3", null)] // accepted: nothing needed
    [InlineData("=0.1.1", "0.1.2", null)] // too new, not too old
    [InlineData(">=0.2 <0.3", "0.1.2", null)] // another minor, not a revision problem
    [InlineData(">=0.1.3 <0.1.5", "0.1.7", null)] // too new
    [InlineData(">=0.1.3 <0.1.5", "0.2.0", null)]
    [InlineData(">=0.2.1 <0.3", "0.1.2", null)] // a revision of another minor does not count
    public void A_revision_that_is_too_low_is_named(string range, string host, string? expected)
    {
        Assert.True(ApiVersionRange.TryParse(range, out var parsed, out var error), error);
        var needed = parsed.NeededRevision(Version.Parse(host));
        Assert.Equal(expected is null ? null : Version.Parse(expected), needed);
    }

    [Theory]
    [InlineData(">=0.1.")]
    [InlineData(">=0..2")]
    [InlineData(">=0.1.x")]
    [InlineData(">=0.1.-1")]
    [InlineData(">=0.1.2.0")]
    [InlineData(">=0.1.99999999999")]
    public void Malformed_revisions_are_refused(string range)
    {
        Assert.False(ApiVersionRange.TryParse(range, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void The_range_text_keeps_the_revision_as_written()
    {
        Assert.True(ApiVersionRange.TryParse("  >=0.1.2    <0.2 ", out var parsed, out _));
        Assert.Equal(">=0.1.2 <0.2", parsed.Text);
    }

    [Fact]
    public void The_current_version_is_0_1_with_the_revision_of_this_contract()
    {
        Assert.Equal(new Version(0, 1, HostApi.Revision), HostApi.Current);
        Assert.Equal(2, HostApi.Revision); // 0.1.0 = released hosts 0.1.0 and 0.1.1, 1 = N1 (ToastMessage.Origin), 2 = V1 (ReadingRow hints)
        Assert.Equal(HostApi.Revision, HostApi.RevisionOf(HostApi.Current));
        Assert.Equal(0, HostApi.RevisionOf(new Version(0, 1)));
        Assert.True(HostApi.IsSupported(">=0.1 <0.2"));
        Assert.True(HostApi.IsSupported($">=0.1.{HostApi.Revision} <0.2"));
        Assert.False(HostApi.IsSupported($">=0.1.{HostApi.Revision + 1} <0.2"));
        Assert.False(HostApi.IsSupported(">=0.2"));
        Assert.False(HostApi.IsSupported("nonsense"));
        Assert.False(HostApi.IsSupported(null));
    }
}
