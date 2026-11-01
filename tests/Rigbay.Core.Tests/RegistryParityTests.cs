using System.Text.Json;
using Rigbay.Core.Install;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>
/// <c>registry-fixtures</c> is a copy of <c>scripts/fixtures</c> of the registry repository (rigbay-app/registry): one registry.json per rule, and
/// <c>expectations.json</c> saying whether the pull request check (scripts/validate.mjs) accepts it and what its first error mentions. The host must
/// reach the same verdict on every file, so a pull request the check lets through is a file the app reads, and the other way round.
/// One difference is on purpose: the app ignores unknown fields (a newer registry may add some), the check refuses them (a typo in a pull request).
/// </summary>
public class RegistryParityTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "registry-fixtures");

    public static IEnumerable<object[]> Fixtures()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "expectations.json")));
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            bool valid = p.Value.GetProperty("valid").GetBoolean();
            string mention = p.Value.GetProperty("mention").ValueKind == JsonValueKind.String ? p.Value.GetProperty("mention").GetString()! : "";
            bool hostAccepts = p.Value.TryGetProperty("hostAccepts", out var h) && h.GetBoolean();
            yield return new object[] { p.Name, valid || hostAccepts, mention, valid };
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_host_and_the_pull_request_check_agree_on_every_fixture(string file, bool hostAccepts, string mention, bool checkAccepts)
    {
        var result = RegistryReader.Parse(File.ReadAllText(Path.Combine(Dir, file)), InstallPolicy.Production);
        Assert.True(hostAccepts == result.IsValid, $"{file}: the host says {(result.IsValid ? "valid" : "invalid: " + string.Join("; ", result.Errors))}");
        if (!checkAccepts && !hostAccepts)
            Assert.Contains(result.Errors, e => e.Contains(mention, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_fixture_file_has_an_expectation()
    {
        var files = Directory.GetFiles(Dir, "*.json").Select(f => Path.GetFileName(f)!).Where(f => f != "expectations.json").Order().ToList();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "expectations.json")));
        Assert.Equal(files, doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList());
        Assert.True(files.Count >= 30);
    }
}
