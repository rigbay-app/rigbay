using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Rigbay.Core.Install;
using Xunit;

namespace Rigbay.Core.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("1.0.0+build5", "1.0.0", 0)]
    [InlineData("2.0.0", "1.99.99", 1)]
    public void Versions_are_ordered_by_the_semver_rules(string a, string b, int expected)
    {
        Assert.True(SemVer.TryParse(a, out var x));
        Assert.True(SemVer.TryParse(b, out var y));
        Assert.Equal(expected, Math.Sign(x.CompareTo(y)));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("")]
    [InlineData("1.0.0-")]
    public void Things_that_are_not_versions_are_refused(string text) => Assert.False(SemVer.TryParse(text, out _));
}

public class InstallPolicyTests
{
    private const string Repo = "https://github.com/rigbay-app/module-demo";
    private static readonly InstallPolicy P = InstallPolicy.Production;

    [Theory]
    [InlineData("https://github.com/rigbay-app/module-demo/releases/download/v1.0.0/demo.zip", true)]
    [InlineData("https://GitHub.com/Rigbay-App/Module-Demo/releases/download/v1.0.0/demo.zip", true)]
    [InlineData("http://github.com/rigbay-app/module-demo/releases/download/v1.0.0/demo.zip", false)]
    [InlineData("https://github.com/rigbay-app/other/releases/download/v1.0.0/demo.zip", false)]
    [InlineData("https://github.com/evil/module-demo/releases/download/v1.0.0/demo.zip", false)]
    [InlineData("https://github.com.evil.com/rigbay-app/module-demo/releases/download/v1.0.0/demo.zip", false)]
    [InlineData("https://evil.com/rigbay-app/module-demo/releases/download/v1.0.0/demo.zip", false)]
    [InlineData("https://github.com/rigbay-app/module-demo/archive/main.zip", false)]
    [InlineData("https://github.com/rigbay-app/module-demo/releases/download/v1.0.0/demo.exe", false)]
    [InlineData("https://github.com/rigbay-app/module-demo/releases/download/v1.0.0/demo.zip?x=1", false)]
    [InlineData("https://github.com/rigbay-app/module-demo/releases/download/v1.0.0/../x.zip", false)]
    [InlineData("https://github.com/rigbay-app/module-demo/releases/download/v1.0.0/%2e%2e/x.zip", false)]
    [InlineData("https://user@github.com/rigbay-app/module-demo/releases/download/v1.0.0/demo.zip", false)]
    [InlineData("https://github.com:444/rigbay-app/module-demo/releases/download/v1.0.0/demo.zip", false)]
    [InlineData("https://github.com/rigbay-app/module-demo/releases/download/v1.0.0/sub/demo.zip", false)]
    public void A_zip_address_must_be_a_release_download_of_the_same_repo(string zip, bool ok) => Assert.Equal(ok, P.IsZipUrlOfRepo(zip, Repo));

    [Theory]
    [InlineData("https://github.com/rigbay-app/module-demo", true)]
    [InlineData("https://github.com/rigbay-app/module-demo/", false)]
    [InlineData("https://github.com/rigbay-app", false)]
    [InlineData("https://github.com/rigbay-app/module-demo/tree/main", false)]
    [InlineData("http://github.com/rigbay-app/module-demo", false)]
    [InlineData("https://gitlab.com/rigbay-app/module-demo", false)]
    [InlineData("https://github.com/rigbay-app/module-demo#x", false)]
    public void A_repo_is_owner_and_name_on_github(string repo, bool ok) => Assert.Equal(ok, P.TryParseRepo(repo, out _, out _));

    [Fact]
    public void Redirects_may_only_lead_to_the_three_release_hosts_over_https()
    {
        var hosts = InstallPolicy.ReleaseHosts;
        Assert.True(P.IsAllowedRequest(new Uri("https://github.com/x"), hosts));
        Assert.True(P.IsAllowedRequest(new Uri("https://objects.githubusercontent.com/x"), hosts));
        Assert.True(P.IsAllowedRequest(new Uri("https://release-assets.githubusercontent.com/x"), hosts));
        Assert.False(P.IsAllowedRequest(new Uri("https://raw.githubusercontent.com/x"), hosts));
        Assert.False(P.IsAllowedRequest(new Uri("http://github.com/x"), hosts));
        Assert.False(P.IsAllowedRequest(new Uri("https://evilgithub.com/x"), hosts));
        Assert.False(P.IsAllowedRequest(new Uri("https://github.com.evil.com/x"), hosts));
        Assert.False(P.IsAllowedRequest(new Uri("file:///C:/x.zip"), hosts));
        Assert.False(P.IsAllowedRequest(new Uri("http://127.0.0.1/x"), hosts));
        Assert.True(InstallPolicy.ForLocalTesting().IsAllowedRequest(new Uri("http://127.0.0.1:8080/x"), hosts));
    }
}

public class RegistryReaderTests
{
    private static readonly TestModule M = new();
    private static readonly byte[] Zip = M.Zip();

    private static RegistryParseResult Parse(string json) => RegistryReader.Parse(json, InstallPolicy.Production);

    [Fact]
    public void A_valid_registry_is_read_with_everything_the_page_needs()
    {
        var r = Parse(RegistryKit.One(new TestModule(Admin: true, Network: new[] { "tcp:18081 (local proxy)" }), Zip));
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        var m = Assert.Single(r.Document!.Modules);
        var v = Assert.Single(m.Versions);
        Assert.Equal("demo-tool", m.Id);
        Assert.Equal(Zip.LongLength, v.Size);
        Assert.True(v.Declares.Admin);
        Assert.Equal("tcp:18081 (local proxy)", Assert.Single(v.Declares.Network));
        Assert.Equal(new DateOnly(2026, 11, 1), v.Released);
    }

    [Fact]
    public void An_empty_registry_is_valid()
    {
        var r = Parse("{\"schemaVersion\":1,\"modules\":[]}");
        Assert.True(r.IsValid);
        Assert.Empty(r.Document!.Modules);
    }

    [Fact]
    public void Unknown_fields_are_ignored_so_a_newer_registry_still_reads()
    {
        var json = RegistryKit.One(M, Zip).Replace("\"modules\":", "\"note\":\"hi\",\"modules\":");
        Assert.True(Parse(json).IsValid);
    }

    public static IEnumerable<object[]> BadRegistries()
    {
        yield return new object[] { "not json", "not valid JSON" };
        yield return new object[] { "[]", "top level" };
        yield return new object[] { "{\"modules\":[]}", "schemaVersion" };
        yield return new object[] { "{\"schemaVersion\":2,\"modules\":[]}", "not supported" };
        yield return new object[] { "{\"schemaVersion\":1}", "modules" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "sha256", ZipKit.Sha256(Zip).ToUpperInvariant())), "sha256" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "sha256", "abc")), "sha256" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "zip", "http://github.com/rigbay-app/module-demo/releases/download/v1.0.0/demo.zip")), "zip" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "zip", "https://github.com/someone-else/module-demo/releases/download/v1.0.0/demo.zip")), "zip" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "zip", "https://example.com/demo.zip")), "zip" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "version", "1.0")), "version" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "hostApi", "newest")), "hostApi" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "size", 0)), "size" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "size", 65L * 1024 * 1024)), "size" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "released", "yesterday")), "released" };
        yield return new object[] { RegistryKit.One(M, Zip, v => Edit(v, "declares", "none")), "declares" };
        yield return new object[] { RegistryKit.Json(RegistryKit.Module("Bad_Id", new[] { RegistryKit.Version(M, Zip) })), "id" };
        yield return new object[] { RegistryKit.Json(RegistryKit.Module("list", new[] { RegistryKit.Version(M, Zip) })), "reserved" };
        yield return new object[] { RegistryKit.Json(RegistryKit.Module("demo-tool", new[] { RegistryKit.Version(M, Zip) }, repo: "https://gitlab.com/a/b")), "repo" };
        yield return new object[] { RegistryKit.Json(RegistryKit.Module("demo-tool", Array.Empty<object>())), "versions" };
        // duplicate ids
        yield return new object[] { RegistryKit.Json(RegistryKit.Module("demo-tool", new[] { RegistryKit.Version(M, Zip) }), RegistryKit.Module("demo-tool", new[] { RegistryKit.Version(M, Zip) })), "more than once" };
        // versions must ascend
        yield return new object[]
        {
            RegistryKit.Json(RegistryKit.Module("demo-tool", new[]
            {
                RegistryKit.Version(new TestModule(Version: "1.1.0"), Zip),
                RegistryKit.Version(new TestModule(Version: "1.0.0"), Zip),
            })),
            "ascending",
        };
    }

    private static object Edit(object version, string field, object value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(version, RegistryKit.Web))!.AsObject();
        node[field] = JsonSerializer.SerializeToNode(value, RegistryKit.Web);
        return node;
    }

    [Theory]
    [MemberData(nameof(BadRegistries))]
    public void A_registry_with_any_problem_is_refused_whole(string json, string mention)
    {
        var r = Parse(json);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains(mention, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Latest_skips_releases_that_need_another_host_and_prefers_stable()
    {
        var json = RegistryKit.Json(RegistryKit.Module("demo-tool", new[]
        {
            RegistryKit.Version(new TestModule(Version: "1.0.0"), Zip),
            RegistryKit.Version(new TestModule(Version: "1.1.0-beta.1"), Zip),
            RegistryKit.Version(new TestModule(Version: "2.0.0", HostApi: ">=0.2 <0.3"), Zip),
        }));
        var module = Parse(json).Document!.Modules[0];
        Assert.Equal("1.0.0", module.Latest(new Version(0, 1))!.Version);
        Assert.Equal("2.0.0", module.Latest(new Version(0, 2))!.Version);
        Assert.Null(module.Latest(new Version(0, 3)));
        Assert.Equal("2.0.0", module.Newest!.Version);
    }

    [Fact]
    public void Latest_honours_the_revision_a_release_names()
    {
        var json = RegistryKit.Json(RegistryKit.Module("demo-tool", new[]
        {
            RegistryKit.Version(new TestModule(Version: "1.0.0"), Zip),
            RegistryKit.Version(new TestModule(Version: "1.1.0", HostApi: ">=0.1.2 <0.2"), Zip),
            RegistryKit.Version(new TestModule(Version: "1.2.0", HostApi: ">=0.1.5 <0.2"), Zip),
        }));
        var module = Parse(json).Document!.Modules[0];
        Assert.True(Parse(json).IsValid); // the reader takes three-number versions in a range
        Assert.Equal("1.0.0", module.Latest(new Version(0, 1))!.Version);       // a host that names no revision is revision 0
        Assert.Equal("1.0.0", module.Latest(new Version(0, 1, 1))!.Version);
        Assert.Equal("1.1.0", module.Latest(new Version(0, 1, 2))!.Version);
        Assert.Equal("1.1.0", module.Latest(new Version(0, 1, 4))!.Version);
        Assert.Equal("1.2.0", module.Latest(new Version(0, 1, 5))!.Version);
        Assert.Equal("1.2.0", module.Newest!.Version);
        Assert.Equal(new Version(0, 1, 5), module.Newest!.HostApiRange.NeededRevision(new Version(0, 1, 2)));
    }

    [Fact]
    public void Declares_cover_the_same_or_fewer_things_and_never_more()
    {
        var registry = new RegistryDeclares(true, new[] { "tcp:1", "tcp:2" }, new[] { "HKCU\\X" }, Array.Empty<string>(), Array.Empty<string>());
        Assert.True(registry.Covers(new RegistryDeclares(true, new[] { "tcp:1", "tcp:2" }, new[] { "HKCU\\X" }, Array.Empty<string>(), Array.Empty<string>())));
        Assert.True(registry.Covers(new RegistryDeclares(false, new[] { "tcp:2" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())));
        Assert.False(registry.Covers(new RegistryDeclares(true, new[] { "tcp:3" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())));
        Assert.False(RegistryDeclares.None.Covers(new RegistryDeclares(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())));
        Assert.False(RegistryDeclares.None.Covers(new RegistryDeclares(false, Array.Empty<string>(), Array.Empty<string>(), new[] { "Spooler" }, Array.Empty<string>())));
        Assert.False(RegistryDeclares.None.Covers(new RegistryDeclares(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new[] { "C:\\Vendor" })));
    }
}

public class SafeZipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigbay-zip-" + Guid.NewGuid().ToString("N")[..10]);
    private static readonly InstallPolicy Policy = InstallPolicy.Production;

    public SafeZipTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { FileOps.SafeDelete(_root); } catch (Exception) { } }

    private string Extract(byte[] zip, InstallPolicy? policy = null, string? into = null)
    {
        var zipPath = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6] + ".zip");
        File.WriteAllBytes(zipPath, zip);
        var target = into ?? Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N")[..6]);
        SafeZip.Extract(zipPath, target, policy ?? Policy);
        return target;
    }

    private InstallException Refused(byte[] zip, InstallPolicy? policy = null) =>
        Assert.Throws<InstallException>(() => Extract(zip, policy));

    [Fact]
    public void A_good_zip_is_unpacked_with_its_folders()
    {
        var target = Extract(new TestModule().Zip());
        Assert.True(File.Exists(Path.Combine(target, "module.json")));
        Assert.True(File.Exists(Path.Combine(target, "Demo.dll")));
        Assert.True(File.Exists(Path.Combine(target, "lang", "en.lang")));
    }

    [Fact]
    public void Backslash_names_from_old_tools_are_taken_as_folders()
    {
        var target = Extract(ZipKit.Build(z => { ZipKit.Add(z, "module.json", "{}"); ZipKit.Add(z, "lang\\en.lang", "x"); }));
        Assert.True(File.Exists(Path.Combine(target, "lang", "en.lang")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    [InlineData("a/b/../../../evil.txt")]
    [InlineData("./a.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("/abs.txt")]
    [InlineData("\\abs.txt")]
    [InlineData("C:/abs.txt")]
    [InlineData("C:\\abs.txt")]
    [InlineData("C:abs.txt")]
    [InlineData("\\\\server\\share\\x.txt")]
    [InlineData("a.txt:stream")]
    [InlineData("dir/a.dll:Zone.Identifier")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("dir/NUL.dll")]
    [InlineData("com1")]
    [InlineData("LPT9.log")]
    [InlineData("AUX .txt")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("dir./file.txt")]
    [InlineData("a<b.txt")]
    [InlineData("a|b.txt")]
    [InlineData("a?.txt")]
    [InlineData("a*.txt")]
    [InlineData("PROGRA~1/x.dll")]
    [InlineData("MODULE~1.JSO")]
    [InlineData("a//b.txt")]
    public void Names_that_could_escape_or_confuse_windows_are_refused(string name)
    {
        var zip = ZipKit.Build(z => ZipKit.Add(z, name, "x"));
        var ex = Refused(zip);
        Assert.Equal(InstallErrorCode.UnsafeZip, ex.Code);
        // nothing was written outside, and nothing at all next to the staging folder
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.txt")));
    }

    [Fact]
    public void Nothing_is_written_when_a_later_entry_is_bad()
    {
        var zip = ZipKit.Build(z => { ZipKit.Add(z, "module.json", "{}"); ZipKit.Add(z, "../evil.txt", "x"); });
        var target = Path.Combine(_root, "out-partial");
        var zipPath = Path.Combine(_root, "p.zip");
        File.WriteAllBytes(zipPath, zip);
        Assert.Throws<InstallException>(() => SafeZip.Extract(zipPath, target, Policy));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));   // names are checked in a first pass, before any file is written
    }

    [Fact]
    public void Two_entries_that_differ_only_by_case_are_refused()
    {
        var ex = Refused(ZipKit.Build(z => { ZipKit.Add(z, "Demo.dll", "a"); ZipKit.Add(z, "demo.DLL", "b"); }));
        Assert.Equal(InstallErrorCode.UnsafeZip, ex.Code);
        Assert.Equal(InstallErrorCode.UnsafeZip, Refused(ZipKit.Build(z => { ZipKit.Add(z, "lib/a.dll", "a"); ZipKit.Add(z, "LIB/A.dll", "b"); })).Code);
    }

    [Fact]
    public void The_same_name_twice_is_refused()
    {
        Assert.Equal(InstallErrorCode.UnsafeZip, Refused(ZipKit.Build(z => { ZipKit.Add(z, "a.txt", "1"); ZipKit.Add(z, "a.txt", "2"); })).Code);
    }

    [Fact]
    public void A_file_and_a_folder_with_one_name_are_refused()
    {
        Assert.Equal(InstallErrorCode.UnsafeZip, Refused(ZipKit.Build(z => { ZipKit.Add(z, "lib", "file"); ZipKit.Add(z, "lib/inner.dll", "x"); })).Code);
        Assert.Equal(InstallErrorCode.UnsafeZip, Refused(ZipKit.Build(z => { ZipKit.Add(z, "lib/inner.dll", "x"); ZipKit.Add(z, "lib", "file"); })).Code);
        Assert.Equal(InstallErrorCode.UnsafeZip, Refused(ZipKit.Build(z => { ZipKit.Add(z, "LIB", "file"); ZipKit.Add(z, "lib/inner.dll", "x"); })).Code);
    }

    [Fact]
    public void A_symbolic_link_entry_is_refused()
    {
        var zip = ZipKit.Build(z =>
        {
            var e = ZipKit.Add(z, "link", "C:\\Windows");
            e.ExternalAttributes = (0xA000 | 0x1ED) << 16;   // unix mode: symbolic link, 0755
        });
        var ex = Refused(zip);
        Assert.Contains("symbolic link", ex.Detail);
    }

    [Fact]
    public void A_windows_reparse_point_attribute_in_the_zip_is_refused()
    {
        var zip = ZipKit.Build(z => ZipKit.Add(z, "link", "x").ExternalAttributes = (int)FileAttributes.ReparsePoint);
        Assert.Equal(InstallErrorCode.UnsafeZip, Refused(zip).Code);
    }

    [Fact]
    public void A_staging_folder_that_is_a_junction_is_refused()
    {
        var real = Path.Combine(_root, "real");
        var junction = Path.Combine(_root, "junction");
        Directory.CreateDirectory(real);
        if (!TryMakeJunction(junction, real)) return;   // mklink /J did not work on this machine; nothing to test
        var ex = Assert.Throws<InstallException>(() => Extract(new TestModule().Zip(), into: junction));
        Assert.Equal(InstallErrorCode.UnsafeZip, ex.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public void Too_many_entries_are_refused()
    {
        var zip = ZipKit.Build(z => { for (int i = 0; i < 30; i++) ZipKit.Add(z, $"f{i}.txt", "x"); });
        var ex = Refused(zip, new InstallPolicy { MaxEntries = 10 });
        Assert.Equal(InstallErrorCode.TooLarge, ex.Code);
    }

    [Fact]
    public void A_zip_that_unpacks_to_too_much_is_refused_from_its_headers_before_writing()
    {
        var zip = ZipKit.Build(z => ZipKit.Add(z, "big.bin", new byte[3 * 1024 * 1024]));
        var policy = new InstallPolicy { MaxUnpackedBytes = 1024 * 1024 };
        Assert.True(zip.Length < 100 * 1024, "the zip itself is tiny: that is the point of a bomb");
        var target = Path.Combine(_root, "bomb");
        var zipPath = Path.Combine(_root, "bomb.zip");
        File.WriteAllBytes(zipPath, zip);
        var ex = Assert.Throws<InstallException>(() => SafeZip.Extract(zipPath, target, policy));
        Assert.Equal(InstallErrorCode.TooLarge, ex.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void A_header_that_understates_the_size_cannot_make_Rigbay_write_more_than_the_limit()
    {
        // an entry whose central directory says "10 bytes" but whose data inflates to 3 MB: the archive reader stops at the declared
        // length or throws, and our own counter of written bytes is the second guard; either way nothing past the limit reaches the disk
        var zip = ZipKit.Build(z => ZipKit.Add(z, "big.bin", new byte[3 * 1024 * 1024]));
        LieAboutSize(zip, 10);
        var policy = new InstallPolicy { MaxUnpackedBytes = 1024 * 1024 };
        var zipPath = Path.Combine(_root, "liar.zip");
        File.WriteAllBytes(zipPath, zip);
        var target = Path.Combine(_root, "liar");
        try { SafeZip.Extract(zipPath, target, policy); }
        catch (Exception ex) when (ex is InstallException or InvalidDataException) { }
        var written = Directory.Exists(target) ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        Assert.True(written <= policy.MaxUnpackedBytes, $"{written} bytes were written");
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_a_bad_zip()
    {
        var zipPath = Path.Combine(_root, "text.zip");
        File.WriteAllText(zipPath, "this is not a zip file at all");
        var ex = Assert.Throws<InstallException>(() => SafeZip.Extract(zipPath, Path.Combine(_root, "t"), Policy));
        Assert.Equal(InstallErrorCode.BadZip, ex.Code);
    }

    [Fact]
    public void A_non_empty_staging_folder_is_not_used()
    {
        var target = Path.Combine(_root, "full");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old.txt"), "x");
        Assert.Throws<InvalidOperationException>(() => Extract(new TestModule().Zip(), into: target));
    }

    // central directory entries start with PK\1\2; the uncompressed size is at offset 24 of the record
    private static void LieAboutSize(byte[] zip, int size)
    {
        for (int i = 0; i + 28 < zip.Length; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
            {
                BitConverter.GetBytes(size).CopyTo(zip, i + 24);
                return;
            }
        }
        throw new InvalidOperationException("no central directory");
    }

    internal static bool TryMakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception) { return false; }
    }
}

public class FileOpsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigbay-fo-" + Guid.NewGuid().ToString("N")[..10]);

    public FileOpsTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { FileOps.SafeDelete(_root); } catch (Exception) { } }

    [Fact]
    public void Deleting_a_junction_removes_the_link_and_never_what_it_points_to()
    {
        var precious = Path.Combine(_root, "precious");
        Directory.CreateDirectory(precious);
        File.WriteAllText(Path.Combine(precious, "keep.txt"), "x");
        var tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "nested"));
        var link = Path.Combine(tree, "nested", "link");
        if (!SafeZipTests.TryMakeJunction(link, precious)) return;

        FileOps.SafeDelete(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(precious, "keep.txt")));
    }

    [Fact]
    public void Read_only_files_are_deleted_too()
    {
        var dir = Path.Combine(_root, "ro");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        FileOps.SafeDelete(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void The_tree_hash_changes_with_content_name_and_extra_files_and_not_with_order()
    {
        string Make(string name, Action<string> change)
        {
            var d = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(d, "lang"));
            File.WriteAllText(Path.Combine(d, "a.txt"), "1");
            File.WriteAllText(Path.Combine(d, "lang", "b.txt"), "2");
            change(d);
            return FileOps.TreeHash(d);
        }
        var h1 = Make("h1", _ => { });
        Assert.Equal(h1, Make("h2", _ => { }));
        Assert.NotEqual(h1, Make("h3", d => File.WriteAllText(Path.Combine(d, "a.txt"), "changed")));
        Assert.NotEqual(h1, Make("h4", d => File.WriteAllText(Path.Combine(d, "extra.txt"), "x")));
        Assert.NotEqual(h1, Make("h5", d => File.Move(Path.Combine(d, "a.txt"), Path.Combine(d, "c.txt"))));
    }

    [Fact]
    public void Atomic_write_leaves_no_temporary_file()
    {
        var file = Path.Combine(_root, "w", "x.json");
        FileOps.WriteAtomic(file, "one");
        FileOps.WriteAtomic(file, "two");
        Assert.Equal("two", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!));
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class AdminFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigbay-acl-" + Guid.NewGuid().ToString("N")[..10]);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    public AdminFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // the folders under test have no entry for this user any more; the owner can always write the DACL, so give the user back full control
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var info = new DirectoryInfo(dir);
                var s = info.GetAccessControl();
                s.SetAccessRuleProtection(false, false);
                s.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(s);
            }
            Directory.Delete(_root, true);
        }
        catch (Exception) { }
    }

    private static bool Elevated
    {
        get { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    [Fact]
    public void The_folder_ends_up_with_exactly_administrators_and_system_and_no_inheritance()
    {
        var path = Path.Combine(_root, "modules");
        // an unelevated test run creates a folder it owns itself, which the real class rightly distrusts; the DACL logic is the same
        new AdminFolderSecurity(requireAdminOwner: Elevated).EnsureAdminOnly(path);

        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected, "inheritance from the parent must be off");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights);
            Assert.False(r.IsInherited);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags);
        });
        Assert.Contains(rules, r => r.IdentityReference.Equals(Admins));
        Assert.Contains(rules, r => r.IdentityReference.Equals(SystemSid));
        Assert.Null(AdminFolderSecurity.Audit(path));
    }

    [Fact]
    public void The_audit_names_an_account_that_can_write()
    {
        var path = Path.Combine(_root, "loose");
        Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        var s = info.GetAccessControl();
        s.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
        info.SetAccessControl(s);
        var problem = AdminFolderSecurity.Audit(path);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Securing_twice_gives_the_same_result()
    {
        var path = Path.Combine(_root, "twice");
        var security = new AdminFolderSecurity(requireAdminOwner: Elevated);
        security.EnsureAdminOnly(path);
        security.EnsureAdminOnly(path);
        Assert.Null(AdminFolderSecurity.Audit(path));
    }

    [Fact]
    public void A_link_is_not_accepted_as_the_modules_folder()
    {
        var real = Path.Combine(_root, "real");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(real);
        if (!SafeZipTests.TryMakeJunction(link, real)) return;
        var ex = Assert.Throws<InstallException>(() => new AdminFolderSecurity(requireAdminOwner: false).EnsureAdminOnly(link));
        Assert.Equal(InstallErrorCode.FolderSecurity, ex.Code);
        FileOps.SafeDelete(link);
    }
}
