namespace Rigbay.Core.Install;

/// <summary>
/// The rules a download and a registry entry must satisfy. <see cref="Production"/> is what the app uses: HTTPS only, GitHub only.
/// <see cref="ForLocalTesting"/> additionally accepts <c>file:</c> and loopback <c>http:</c> sources and skips the "same repo" rule for
/// the zip URL; the app only builds it for a scratch modules folder (see docs/INSTALL.md).
/// </summary>
public sealed class InstallPolicy
{
    public static readonly Uri DefaultRegistryUrl = new("https://raw.githubusercontent.com/rigbay-app/registry/main/registry.json");

    /// <summary>Hosts a release download may be served from after the first request (github.com answers with a redirect to one of the other two).</summary>
    public static readonly IReadOnlyList<string> ReleaseHosts = new[] { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    public static readonly IReadOnlyList<string> RegistryHosts = new[] { "raw.githubusercontent.com" };

    public static InstallPolicy Production { get; } = new();

    public static InstallPolicy ForLocalTesting(TimeSpan? readDelay = null) => new() { AllowLocalSources = true, LocalReadDelay = readDelay ?? TimeSpan.Zero };

    public long MaxZipBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxUnpackedBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxEntries { get; init; } = 5000;
    public int MaxRedirects { get; init; } = 3;
    public long MaxRegistryBytes { get; init; } = 2L * 1024 * 1024;

    /// <summary>No data for this long on an open download ends it.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>With <see cref="AllowLocalSources"/>: wait this long before each read of a local file, so a download is slow enough to see and cancel. Tests and screenshots only.</summary>
    public TimeSpan LocalReadDelay { get; init; }

    /// <summary>Accept <c>file:</c> and loopback <c>http:</c> sources and any zip URL. Tests and local rendering only.</summary>
    public bool AllowLocalSources { get; init; }

    /// <summary>Checks one URL a request is about to be sent to (the first one or a redirect target).</summary>
    public bool IsAllowedRequest(Uri uri, IReadOnlyList<string> hosts)
    {
        if (!uri.IsAbsoluteUri) return false;
        if (AllowLocalSources && (uri.Scheme == Uri.UriSchemeFile || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))) return true;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
        return hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary><c>https://github.com/&lt;owner&gt;/&lt;repo&gt;</c> exactly (no query, fragment, port or extra path).</summary>
    public bool TryParseRepo(string? repo, out string owner, out string name)
    {
        owner = name = "";
        if (string.IsNullOrEmpty(repo) || repo.Length > 200) return false;
        if (!Uri.TryCreate(repo, UriKind.Absolute, out var uri)) return false;
        if (AllowLocalSources && uri.Scheme == Uri.UriSchemeFile) { owner = "local"; name = "local"; return true; }
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.AbsolutePath.EndsWith('/') || uri.AbsolutePath.Contains("//", StringComparison.Ordinal)) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || !IsNamePart(parts[0]) || !IsNamePart(parts[1])) return false;
        owner = parts[0];
        name = parts[1];
        return true;
    }

    /// <summary>
    /// A zip URL is acceptable when it is <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;file&gt;.zip</c> and
    /// owner and repo are those of <paramref name="repo"/>.
    /// </summary>
    public bool IsZipUrlOfRepo(string? zip, string repo)
    {
        if (AllowLocalSources && Uri.TryCreate(zip, UriKind.Absolute, out var local) && (local.Scheme == Uri.UriSchemeFile || (local.Scheme == Uri.UriSchemeHttp && local.IsLoopback))) return true;
        if (!TryParseRepo(repo, out var owner, out var name)) return false;
        if (string.IsNullOrEmpty(zip) || zip.Length > 400 || !Uri.TryCreate(zip, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (zip.Contains('\\') || zip.Contains("%2e", StringComparison.OrdinalIgnoreCase) || zip.Contains("%2f", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.None);
        // "", owner, repo, "releases", "download", tag, file
        if (parts.Length != 7 || parts[0].Length != 0) return false;
        if (!string.Equals(parts[1], owner, StringComparison.OrdinalIgnoreCase) || !string.Equals(parts[2], name, StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[3] != "releases" || parts[4] != "download") return false;
        if (parts[5].Length == 0 || parts[5] is "." or ".." || parts[6].Length == 0 || parts[6] is "." or "..") return false;
        return parts[6].EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNamePart(string s) =>
        s.Length is >= 1 and <= 100 && s is not ("." or "..") && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
