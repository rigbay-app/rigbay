using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Core.Install;

/// <summary>What a release's manifest declares (<c>requires</c> and <c>probePaths</c>), written down in the registry so it can be shown before anything is downloaded.</summary>
public sealed record RegistryDeclares(
    bool Admin,
    IReadOnlyList<string> Network,
    IReadOnlyList<string> Registry,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> OutsideFiles)
{
    public static RegistryDeclares None { get; } = new(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public bool IsEmpty => !Admin && Network.Count + Registry.Count + Services.Count + OutsideFiles.Count == 0;

    /// <summary>The declarations of a manifest in the same shape.</summary>
    public static RegistryDeclares Of(ModuleManifest m) => new(m.Requires.Admin, m.Requires.Network, m.Requires.Registry, m.Requires.Services, m.DeclaredProbePaths);

    /// <summary>
    /// True when <paramref name="actual"/> asks for nothing that this one does not list: admin only if this says admin, and every
    /// network, registry, service and outside-file entry of <paramref name="actual"/> is listed here (compared as written).
    /// </summary>
    public bool Covers(RegistryDeclares actual) =>
        (!actual.Admin || Admin)
        && Subset(actual.Network, Network) && Subset(actual.Registry, Registry)
        && Subset(actual.Services, Services) && Subset(actual.OutsideFiles, OutsideFiles);

    private static bool Subset(IReadOnlyList<string> inner, IReadOnlyList<string> outer) => inner.All(i => outer.Contains(i, StringComparer.Ordinal));

    /// <summary>A short English description of what <paramref name="actual"/> asks for beyond this declaration (for the log).</summary>
    public string DescribeExcess(RegistryDeclares actual)
    {
        var parts = new List<string>();
        if (actual.Admin && !Admin) parts.Add("administrator");
        void Add(string label, IReadOnlyList<string> inner, IReadOnlyList<string> outer)
        {
            foreach (var i in inner.Where(i => !outer.Contains(i, StringComparer.Ordinal))) parts.Add($"{label} '{i}'");
        }
        Add("network", actual.Network, Network);
        Add("registry", actual.Registry, Registry);
        Add("service", actual.Services, Services);
        Add("outside file", actual.OutsideFiles, OutsideFiles);
        return string.Join(", ", parts);
    }
}

public sealed record RegistryVersion(
    string Version,
    string HostApi,
    ApiVersionRange HostApiRange,
    string Zip,
    string Sha256,
    long Size,
    RegistryDeclares Declares,
    DateOnly Released)
{
    public SemVer Semver => SemVer.TryParse(Version, out var v) ? v : throw new InvalidOperationException("registry version was validated");

    /// <summary>True when this release supports the host API <paramref name="current"/>.</summary>
    public bool SupportsHost(Version current) => HostApiRange.Contains(current);
}

public sealed record RegistryModule(
    string Id,
    string Name,
    string Description,
    string Repo,
    string? Icon,
    IReadOnlyList<RegistryVersion> Versions)
{
    /// <summary>The newest release that supports <paramref name="host"/>; prereleases only when the module has nothing else for this host.</summary>
    public RegistryVersion? Latest(Version host)
    {
        var fits = Versions.Where(v => v.SupportsHost(host)).ToList();
        var stable = fits.Where(v => !v.Semver.IsPrerelease).ToList();
        var pool = stable.Count > 0 ? stable : fits;
        return pool.Count == 0 ? null : pool.MaxBy(v => v.Semver);
    }

    /// <summary>The newest release at all, whatever host it needs.</summary>
    public RegistryVersion? Newest => Versions.Count == 0 ? null : Versions.MaxBy(v => v.Semver);
}

public sealed record RegistryDocument(int SchemaVersion, IReadOnlyList<RegistryModule> Modules)
{
    public RegistryModule? Find(string id) => Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
}

public sealed record RegistryParseResult(RegistryDocument? Document, IReadOnlyList<string> Errors)
{
    public bool IsValid => Document is not null;
}

/// <summary>
/// Reads <c>registry.json</c> strictly: any problem makes the whole document invalid (the registry is the trust anchor, so the
/// host fails closed). Unknown fields are ignored so a newer registry with extra information still reads. The same rules are in
/// <c>registry.schema.json</c> and the validator script of the registry repository.
/// </summary>
public static class RegistryReader
{
    public const int SchemaVersion = 1;

    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly string[] Reserved = { "rigbay", "host", "help", "list", "version" };

    public static RegistryParseResult Parse(string json, InstallPolicy policy)
    {
        var errors = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            return new(null, new[] { "not valid JSON: " + ex.Message });
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, new[] { "the top level must be an object" });
            if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out var schema))
                return new(null, new[] { "schemaVersion: an integer is required" });
            if (schema != SchemaVersion) return new(null, new[] { $"schemaVersion {schema} is not supported (this Rigbay reads {SchemaVersion})" });
            if (!root.TryGetProperty("modules", out var modulesEl) || modulesEl.ValueKind != JsonValueKind.Array)
                return new(null, new[] { "modules: an array is required" });

            var modules = new List<RegistryModule>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (var el in modulesEl.EnumerateArray())
            {
                var path = $"modules[{index++}]";
                var module = ReadModule(el, path, policy, errors);
                if (module is null) continue;
                if (!seen.Add(module.Id)) errors.Add($"{path}.id: '{module.Id}' appears more than once");
                modules.Add(module);
                if (modules.Count > 500) { errors.Add("modules: more than 500 entries"); break; }
            }
            return errors.Count > 0 ? new(null, errors) : new(new RegistryDocument(schema, modules), errors);
        }
    }

    private static RegistryModule? ReadModule(JsonElement el, string path, InstallPolicy policy, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"{path}: must be an object"); return null; }
        int before = errors.Count;

        var id = Str(el, "id", path, errors, 2, 40);
        if (id is not null)
        {
            if (!IdPattern.IsMatch(id)) errors.Add($"{path}.id: lowercase words joined by '-'");
            else if (Reserved.Contains(id)) errors.Add($"{path}.id: '{id}' is reserved");
        }
        var name = Str(el, "name", path, errors, 1, 60);
        var description = Str(el, "description", path, errors, 1, 200);
        var repo = Str(el, "repo", path, errors, 1, 200);
        if (repo is not null && !policy.TryParseRepo(repo, out _, out _)) errors.Add($"{path}.repo: must be https://github.com/<owner>/<repo>");

        string? icon = null;
        if (el.TryGetProperty("icon", out var iconEl))
        {
            icon = iconEl.ValueKind == JsonValueKind.String ? iconEl.GetString() : null;
            if (icon is null || !IsIconUrl(icon, policy)) { errors.Add($"{path}.icon: must be an https URL on raw.githubusercontent.com or github.com ending in .svg or .png"); icon = null; }
        }

        var versions = new List<RegistryVersion>();
        if (!el.TryGetProperty("versions", out var vEl) || vEl.ValueKind != JsonValueKind.Array || vEl.GetArrayLength() == 0)
            errors.Add($"{path}.versions: a non-empty array is required");
        else if (vEl.GetArrayLength() > 200)
            errors.Add($"{path}.versions: more than 200 entries");
        else
        {
            int vi = 0;
            SemVer? previous = null;
            foreach (var v in vEl.EnumerateArray())
            {
                var vp = $"{path}.versions[{vi++}]";
                var version = ReadVersion(v, vp, repo, policy, errors);
                if (version is null) continue;
                var sv = version.Semver;
                if (previous is not null && sv <= previous) errors.Add($"{vp}.version: versions must be strictly ascending ('{version.Version}' after '{previous}')");
                previous = sv;
                versions.Add(version);
            }
        }

        if (errors.Count > before || id is null || name is null || description is null || repo is null) return null;
        return new RegistryModule(id, name, description, repo, icon, versions);
    }

    private static RegistryVersion? ReadVersion(JsonElement el, string path, string? repo, InstallPolicy policy, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Object) { errors.Add($"{path}: must be an object"); return null; }
        int before = errors.Count;

        var version = Str(el, "version", path, errors, 1, 64);
        if (version is not null && !SemVer.TryParse(version, out _)) { errors.Add($"{path}.version: not a semantic version"); version = null; }

        var hostApi = Str(el, "hostApi", path, errors, 1, 100);
        ApiVersionRange? range = null;
        if (hostApi is not null && !ApiVersionRange.TryParse(hostApi, out range, out _)) errors.Add($"{path}.hostApi: not a range like '>=0.1 <0.2'");

        var zip = Str(el, "zip", path, errors, 1, 400);
        if (zip is not null && repo is not null && !policy.IsZipUrlOfRepo(zip, repo))
            errors.Add($"{path}.zip: must be https://github.com/<owner>/<repo>/releases/download/<tag>/<file>.zip of the repo above");

        var sha = Str(el, "sha256", path, errors, 64, 64);
        if (sha is not null && !Sha256Pattern.IsMatch(sha)) errors.Add($"{path}.sha256: 64 lowercase hex digits");

        long size = 0;
        if (!el.TryGetProperty("size", out var sEl) || sEl.ValueKind != JsonValueKind.Number || !sEl.TryGetInt64(out size) || size < 1 || size > policy.MaxZipBytes)
            errors.Add($"{path}.size: an integer from 1 to {policy.MaxZipBytes} (bytes of the zip)");

        DateOnly released = default;
        var date = Str(el, "released", path, errors, 10, 10);
        if (date is not null && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out released))
            errors.Add($"{path}.released: a date like 2026-11-01");

        var declares = ReadDeclares(el, path, errors);

        if (errors.Count > before || version is null || hostApi is null || range is null || zip is null || sha is null || declares is null) return null;
        return new RegistryVersion(version, hostApi, range, zip, sha, size, declares, released);
    }

    private static RegistryDeclares? ReadDeclares(JsonElement parent, string path, List<string> errors)
    {
        if (!parent.TryGetProperty("declares", out var el) || el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.declares: an object is required");
            return null;
        }
        int before = errors.Count;
        bool admin = false;
        if (!el.TryGetProperty("admin", out var a) || a.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors.Add($"{path}.declares.admin: true or false is required");
        else admin = a.GetBoolean();
        var network = Strings(el, "network", path + ".declares", errors);
        var registry = Strings(el, "registry", path + ".declares", errors);
        var services = Strings(el, "services", path + ".declares", errors);
        var outside = Strings(el, "outsideFiles", path + ".declares", errors);
        return errors.Count > before ? null : new RegistryDeclares(admin, network, registry, services, outside);
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var arr)) return Array.Empty<string>();
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > 32) { errors.Add($"{path}.{name}: an array of at most 32 strings"); return Array.Empty<string>(); }
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(s) || s.Length > 260) { errors.Add($"{path}.{name}: entries are strings of 1 to 260 characters"); continue; }
            list.Add(s);
        }
        return list;
    }

    private static string? Str(JsonElement parent, string name, string path, List<string> errors, int min, int max)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}.{name}: a string is required");
            return null;
        }
        var s = el.GetString()!;
        if (s.Length < min || s.Length > max || s != s.Trim() || s.Any(char.IsControl))
        {
            errors.Add($"{path}.{name}: {min} to {max} characters, no control characters, no surrounding spaces");
            return null;
        }
        return s;
    }

    private static bool IsIconUrl(string icon, InstallPolicy policy)
    {
        if (icon.Length > 300 || !Uri.TryCreate(icon, UriKind.Absolute, out var uri)) return false;
        if (policy.AllowLocalSources && uri.Scheme == Uri.UriSchemeFile) return true;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (!uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }
}
