using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>Navigation group of a module; a fixed list, a module cannot add one.</summary>
public enum ModuleGroup { Power, Cooling, Network, Display, Monitoring, Other }

/// <summary>What a module says it uses. Informational, shown before the first enable; not a sandbox.</summary>
public sealed record ModuleRequires(
    bool Admin,
    IReadOnlyList<string> Network,
    IReadOnlyList<string> Registry,
    IReadOnlyList<string> Services)
{
    public static ModuleRequires None { get; } = new(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>A validated <c>module.json</c>.</summary>
public sealed record ModuleManifest(
    int ManifestVersion,
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string EntryAssembly,
    string EntryType,
    ApiVersionRange HostApi,
    ModuleGroup Group,
    ModuleRequires Requires,
    string LanguagesFolder,
    string? Homepage);

/// <summary>A problem found in a manifest. <see cref="Path"/> is the JSON path (<c>entry.type</c>).</summary>
public sealed record ManifestIssue(string Path, string Message, bool IsError = true)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>Outcome of reading a manifest: the manifest when there is no error, and every issue found.</summary>
public sealed record ManifestResult(ModuleManifest? Manifest, IReadOnlyList<ManifestIssue> Issues)
{
    public bool IsValid => Manifest is not null;

    public string ErrorSummary => string.Join("; ", Issues.Where(i => i.IsError).Select(i => i.ToString()));
}

/// <summary>Reads and validates <c>module.json</c>. Reports all problems at once and never throws for bad input.</summary>
public static class ManifestReader
{
    public const string FileName = "module.json";
    public const int MaxBytes = 64 * 1024;

    private static readonly string[] Reserved = { "rigbay", "host", "help", "list", "version" };

    private static readonly HashSet<string> KnownTop = new(StringComparer.Ordinal)
    {
        "manifestVersion", "id", "name", "version", "author", "description", "entry", "hostApi", "group", "requires", "languages", "homepage",
    };

    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private static readonly Regex SemVer = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TypeName = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*(\+[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant);

    /// <summary>Reads <c>module.json</c> from a module folder and also checks that the entry assembly file exists.</summary>
    public static ManifestResult ReadFolder(string folder)
    {
        var path = Path.Combine(folder, FileName);
        string json;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Fail("module.json", "the file does not exist");
            if (info.Length > MaxBytes) return Fail("module.json", $"the file is larger than {MaxBytes / 1024} KiB");
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("module.json", "cannot read the file: " + ex.Message);
        }

        var result = Parse(json, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
        if (result.Manifest is { } m)
        {
            var issues = result.Issues.ToList();
            if (!File.Exists(Path.Combine(folder, m.EntryAssembly)))
                issues.Add(new ManifestIssue("entry.assembly", $"'{m.EntryAssembly}' is not in the module folder"));
            if (issues.Any(i => i.IsError)) return new ManifestResult(null, issues);
            return new ManifestResult(m, issues);
        }
        return result;
    }

    /// <summary>Validates manifest text. <paramref name="folderName"/> must equal the id (pass null to skip that check).</summary>
    public static ManifestResult Parse(string json, string? folderName)
    {
        var issues = new List<ManifestIssue>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return Fail("module.json", "not valid JSON: " + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("module.json", "the top level must be an object");

            foreach (var p in root.EnumerateObject())
                if (!KnownTop.Contains(p.Name)) issues.Add(new ManifestIssue(p.Name, "unknown field (ignored)", IsError: false));

            int version = 0;
            if (!root.TryGetProperty("manifestVersion", out var mv) || mv.ValueKind != JsonValueKind.Number || !mv.TryGetInt32(out version))
                issues.Add(new ManifestIssue("manifestVersion", "required, an integer"));
            else if (version > 1)
                issues.Add(new ManifestIssue("manifestVersion", $"{version} is a newer manifest format than this host reads (1)"));
            else if (version < 1)
                issues.Add(new ManifestIssue("manifestVersion", "must be 1"));

            var id = Text(root, "id", issues, 2, 40);
            if (id is not null)
            {
                if (!IdPattern.IsMatch(id)) issues.Add(new ManifestIssue("id", "use lowercase letters, digits and single dashes"));
                else if (Reserved.Contains(id)) issues.Add(new ManifestIssue("id", $"'{id}' is reserved"));
                else if (folderName is not null && !string.Equals(folderName, id, StringComparison.Ordinal))
                    issues.Add(new ManifestIssue("id", $"must equal the folder name '{folderName}'"));
            }

            var name = Text(root, "name", issues, 1, 60);
            var ver = Text(root, "version", issues, 1, 64);
            if (ver is not null && !SemVer.IsMatch(ver)) issues.Add(new ManifestIssue("version", "must be a semantic version like 1.2.3"));
            var author = Text(root, "author", issues, 1, 100);
            var description = Text(root, "description", issues, 1, 200);

            string? entryAssembly = null, entryType = null;
            if (!root.TryGetProperty("entry", out var entry) || entry.ValueKind != JsonValueKind.Object)
                issues.Add(new ManifestIssue("entry", "required, an object with 'assembly' and 'type'"));
            else
            {
                entryAssembly = Text(entry, "assembly", issues, 1, 200, "entry.");
                if (entryAssembly is not null)
                {
                    if (entryAssembly.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || entryAssembly == "." || entryAssembly.Contains(".."))
                        issues.Add(new ManifestIssue("entry.assembly", "must be a file name without a folder"));
                    else if (!entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        issues.Add(new ManifestIssue("entry.assembly", "must end with .dll"));
                    else if (entryAssembly.Equals("Rigbay.Abstractions.dll", StringComparison.OrdinalIgnoreCase))
                        issues.Add(new ManifestIssue("entry.assembly", "cannot be Rigbay.Abstractions.dll"));
                }
                entryType = Text(entry, "type", issues, 1, 300, "entry.");
                if (entryType is not null && !TypeName.IsMatch(entryType)) issues.Add(new ManifestIssue("entry.type", "must be a full type name like My.Module.MainModule"));
            }

            ApiVersionRange? range = null;
            var hostApi = Text(root, "hostApi", issues, 1, 100);
            if (hostApi is not null && !ApiVersionRange.TryParse(hostApi, out range, out var rangeError))
                issues.Add(new ManifestIssue("hostApi", rangeError));

            ModuleGroup group = ModuleGroup.Other;
            var groupText = Text(root, "group", issues, 1, 20);
            if (groupText is not null)
            {
                ModuleGroup? found = null;
                foreach (var g in Enum.GetValues<ModuleGroup>())
                    if (g.ToString().ToLowerInvariant() == groupText) found = g;
                if (found is null) issues.Add(new ManifestIssue("group", "must be one of power, cooling, network, display, monitoring, other"));
                else group = found.Value;
            }

            var requires = ReadRequires(root, issues);

            var languages = "lang";
            if (root.TryGetProperty("languages", out var lang))
            {
                if (lang.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(lang.GetString())) issues.Add(new ManifestIssue("languages", "must be a folder name"));
                else
                {
                    languages = lang.GetString()!.Trim();
                    if (Path.IsPathRooted(languages) || languages.Split('/', '\\').Any(p => p == ".." || p == ".") || languages.Contains(':'))
                        issues.Add(new ManifestIssue("languages", "must be a relative folder inside the module folder"));
                }
            }

            string? homepage = null;
            if (root.TryGetProperty("homepage", out var hp))
            {
                if (hp.ValueKind != JsonValueKind.String || !Uri.TryCreate(hp.GetString(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    issues.Add(new ManifestIssue("homepage", "must be an http or https address"));
                else homepage = hp.GetString();
            }

            if (issues.Any(i => i.IsError)) return new ManifestResult(null, issues);

            var manifest = new ModuleManifest(version, id!, name!, ver!, author!, description!, entryAssembly!, entryType!, range!, group, requires, languages, homepage);
            return new ManifestResult(manifest, issues);
        }
    }

    private static ModuleRequires ReadRequires(JsonElement root, List<ManifestIssue> issues)
    {
        if (!root.TryGetProperty("requires", out var req)) return ModuleRequires.None;
        if (req.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ManifestIssue("requires", "must be an object"));
            return ModuleRequires.None;
        }
        bool admin = false;
        if (req.TryGetProperty("admin", out var a))
        {
            if (a.ValueKind is JsonValueKind.True or JsonValueKind.False) admin = a.GetBoolean();
            else issues.Add(new ManifestIssue("requires.admin", "must be true or false"));
        }
        return new ModuleRequires(admin, Strings(req, "network", issues), Strings(req, "registry", issues), Strings(req, "services", issues));
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name, List<ManifestIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var arr)) return Array.Empty<string>();
        if (arr.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new ManifestIssue("requires." + name, "must be an array of strings"));
            return Array.Empty<string>();
        }
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(s) || s.Length > 200) issues.Add(new ManifestIssue("requires." + name, "every entry must be a string of 1 to 200 characters"));
            else list.Add(s);
        }
        return list;
    }

    private static string? Text(JsonElement parent, string name, List<ManifestIssue> issues, int min, int max, string prefix = "")
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            issues.Add(new ManifestIssue(prefix + name, "required, a string"));
            return null;
        }
        var s = el.GetString()!.Trim();
        if (s.Length < min || s.Length > max)
        {
            issues.Add(new ManifestIssue(prefix + name, $"must be {min} to {max} characters"));
            return null;
        }
        return s;
    }

    private static ManifestResult Fail(string path, string message) => new(null, new[] { new ManifestIssue(path, message) });
}
