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

/// <summary>
/// A problem found in a manifest. <see cref="Path"/> is the JSON path (<c>entry.type</c>); the text is a host language key
/// (<c>core.manifest.*</c>) with arguments, so the host can show it in the user's language
/// (<see cref="Localise(LanguageCatalog)"/>). <see cref="Message"/> is the English text, for logs and tests.
/// </summary>
/// <param name="Path">JSON path of the field.</param>
/// <param name="Key">Host language key of the problem text.</param>
/// <param name="Args">Values for <c>{0}</c>, <c>{1}</c> in the text.</param>
/// <param name="IsError">False for a warning (the manifest is still usable).</param>
public sealed record ManifestIssue(string Path, string Key, object[]? Args = null, bool IsError = true)
{
    /// <summary>The problem in English (from Core's own language file).</summary>
    public string Message => CoreEnglish.Format(Key, Args);

    /// <summary>The problem in the current language of <paramref name="languages"/>, without the path.</summary>
    public string Localise(LanguageCatalog languages) => languages.F(Key, Args ?? Array.Empty<object>());

    /// <summary>The path and the problem in the current language: <c>entry.type: must be a full type name ...</c>.</summary>
    public string LocaliseLine(LanguageCatalog languages) => languages.F("core.manifest.issue", Path, Localise(languages));

    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>Outcome of reading a manifest: the manifest when there is no error, and every issue found.</summary>
public sealed record ManifestResult(ModuleManifest? Manifest, IReadOnlyList<ManifestIssue> Issues)
{
    public bool IsValid => Manifest is not null;

    /// <summary>All errors in English, joined by "; " (logs, tests).</summary>
    public string ErrorSummary => string.Join("; ", Issues.Where(i => i.IsError).Select(i => i.ToString()));

    /// <summary>All errors in the current language, joined by "; ".</summary>
    public string LocaliseErrors(LanguageCatalog languages) => ManifestIssues.Summary(Issues, languages);
}

/// <summary>Helpers for lists of <see cref="ManifestIssue"/>.</summary>
public static class ManifestIssues
{
    /// <summary>The errors among <paramref name="issues"/> in the current language, joined by "; ".</summary>
    public static string Summary(IEnumerable<ManifestIssue> issues, LanguageCatalog languages) =>
        string.Join("; ", issues.Where(i => i.IsError).Select(i => i.LocaliseLine(languages)));
}

/// <summary>Core's own English texts, for the places that need text without a catalog (logs, <see cref="ManifestIssue.Message"/>).</summary>
internal static class CoreEnglish
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Texts = new(() =>
    {
        var assembly = typeof(CoreEnglish).Assembly;
        using var stream = assembly.GetManifestResourceStream("Rigbay.Core.lang.en.lang");
        if (stream is null) return new Dictionary<string, string>();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return LanguageFile.Parse(reader, "en.lang").Text;
    });

    public static string Format(string key, object[]? args)
    {
        if (!Texts.Value.TryGetValue(key, out var text)) return key;
        try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args ?? Array.Empty<object>()); }
        catch (FormatException) { return text; }
    }
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
            if (!info.Exists) return Fail("module.json", "core.manifest.file-missing");
            if (info.Length > MaxBytes) return Fail("module.json", "core.manifest.file-too-large", MaxBytes / 1024);
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("module.json", "core.manifest.file-unreadable", ex.Message);
        }

        var result = Parse(json, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
        if (result.Manifest is { } m)
        {
            var issues = result.Issues.ToList();
            if (!File.Exists(Path.Combine(folder, m.EntryAssembly)))
                issues.Add(Issue("entry.assembly", "core.manifest.assembly-missing", m.EntryAssembly));
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
            return Fail("module.json", "core.manifest.not-json", ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("module.json", "core.manifest.not-object");

            foreach (var p in root.EnumerateObject())
                if (!KnownTop.Contains(p.Name)) issues.Add(new ManifestIssue(p.Name, "core.manifest.unknown-field", IsError: false));

            int version = 0;
            if (!root.TryGetProperty("manifestVersion", out var mv) || mv.ValueKind != JsonValueKind.Number || !mv.TryGetInt32(out version))
                issues.Add(Issue("manifestVersion", "core.manifest.required-integer"));
            else if (version > 1)
                issues.Add(Issue("manifestVersion", "core.manifest.newer-format", version));
            else if (version < 1)
                issues.Add(Issue("manifestVersion", "core.manifest.must-be-one"));

            var id = Text(root, "id", issues, 2, 40);
            if (id is not null)
            {
                if (!IdPattern.IsMatch(id)) issues.Add(Issue("id", "core.manifest.id-pattern"));
                else if (Reserved.Contains(id)) issues.Add(Issue("id", "core.manifest.id-reserved", id));
                else if (folderName is not null && !string.Equals(folderName, id, StringComparison.Ordinal))
                    issues.Add(Issue("id", "core.manifest.id-folder", folderName));
            }

            var name = Text(root, "name", issues, 1, 60);
            var ver = Text(root, "version", issues, 1, 64);
            if (ver is not null && !SemVer.IsMatch(ver)) issues.Add(Issue("version", "core.manifest.semver"));
            var author = Text(root, "author", issues, 1, 100);
            var description = Text(root, "description", issues, 1, 200);

            string? entryAssembly = null, entryType = null;
            if (!root.TryGetProperty("entry", out var entry) || entry.ValueKind != JsonValueKind.Object)
                issues.Add(Issue("entry", "core.manifest.entry-required"));
            else
            {
                entryAssembly = Text(entry, "assembly", issues, 1, 200, "entry.");
                if (entryAssembly is not null)
                {
                    if (entryAssembly.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || entryAssembly == "." || entryAssembly.Contains(".."))
                        issues.Add(Issue("entry.assembly", "core.manifest.assembly-no-folder"));
                    else if (!entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        issues.Add(Issue("entry.assembly", "core.manifest.assembly-dll"));
                    else if (entryAssembly.Equals("Rigbay.Abstractions.dll", StringComparison.OrdinalIgnoreCase))
                        issues.Add(Issue("entry.assembly", "core.manifest.assembly-abstractions"));
                }
                entryType = Text(entry, "type", issues, 1, 300, "entry.");
                if (entryType is not null && !TypeName.IsMatch(entryType)) issues.Add(Issue("entry.type", "core.manifest.type-name"));
            }

            ApiVersionRange? range = null;
            var hostApi = Text(root, "hostApi", issues, 1, 100);
            if (hostApi is not null && !ApiVersionRange.TryParse(hostApi, out range, out var rangeError))
                issues.Add(Issue("hostApi", "core.manifest.host-api-invalid", hostApi));

            ModuleGroup group = ModuleGroup.Other;
            var groupText = Text(root, "group", issues, 1, 20);
            if (groupText is not null)
            {
                ModuleGroup? found = null;
                foreach (var g in Enum.GetValues<ModuleGroup>())
                    if (g.ToString().ToLowerInvariant() == groupText) found = g;
                if (found is null) issues.Add(Issue("group", "core.manifest.group-values"));
                else group = found.Value;
            }

            var requires = ReadRequires(root, issues);

            var languages = "lang";
            if (root.TryGetProperty("languages", out var lang))
            {
                if (lang.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(lang.GetString())) issues.Add(Issue("languages", "core.manifest.languages-name"));
                else
                {
                    languages = lang.GetString()!.Trim();
                    if (Path.IsPathRooted(languages) || languages.Split('/', '\\').Any(p => p == ".." || p == ".") || languages.Contains(':'))
                        issues.Add(Issue("languages", "core.manifest.languages-relative"));
                }
            }

            string? homepage = null;
            if (root.TryGetProperty("homepage", out var hp))
            {
                if (hp.ValueKind != JsonValueKind.String || !Uri.TryCreate(hp.GetString(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    issues.Add(Issue("homepage", "core.manifest.homepage-url"));
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
            issues.Add(Issue("requires", "core.manifest.must-be-object"));
            return ModuleRequires.None;
        }
        bool admin = false;
        if (req.TryGetProperty("admin", out var a))
        {
            if (a.ValueKind is JsonValueKind.True or JsonValueKind.False) admin = a.GetBoolean();
            else issues.Add(Issue("requires.admin", "core.manifest.must-be-bool"));
        }
        return new ModuleRequires(admin, Strings(req, "network", issues), Strings(req, "registry", issues), Strings(req, "services", issues));
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name, List<ManifestIssue> issues)
    {
        if (!parent.TryGetProperty(name, out var arr)) return Array.Empty<string>();
        if (arr.ValueKind != JsonValueKind.Array)
        {
            issues.Add(Issue("requires." + name, "core.manifest.must-be-string-array"));
            return Array.Empty<string>();
        }
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(s) || s.Length > 200) issues.Add(Issue("requires." + name, "core.manifest.string-entries"));
            else list.Add(s);
        }
        return list;
    }

    private static string? Text(JsonElement parent, string name, List<ManifestIssue> issues, int min, int max, string prefix = "")
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            issues.Add(Issue(prefix + name, "core.manifest.required-string"));
            return null;
        }
        var s = el.GetString()!.Trim();
        if (s.Length < min || s.Length > max)
        {
            issues.Add(Issue(prefix + name, "core.manifest.length-range", min, max));
            return null;
        }
        return s;
    }

    private static ManifestIssue Issue(string path, string key, params object[] args) => new(path, key, args);

    private static ManifestResult Fail(string path, string key, params object[] args) => new(null, new[] { Issue(path, key, args) });
}
