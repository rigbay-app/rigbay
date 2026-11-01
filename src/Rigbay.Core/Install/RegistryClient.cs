using System.Text.Json;
using Rigbay.Abstractions;

namespace Rigbay.Core.Install;

public enum RegistryStatus
{
    /// <summary>Just fetched (or confirmed unchanged by the server).</summary>
    Fresh,
    /// <summary>Served from the cache on disk. <see cref="RegistrySnapshot.Problem"/> says why the network was not used, or is null when no check was tried yet.</summary>
    Cached,
    /// <summary>Nothing to show: no network answer and no cache.</summary>
    Unavailable,
}

public sealed record RegistrySnapshot(RegistryDocument? Document, RegistryStatus Status, DateTimeOffset? FetchedAt, InstallErrorCode? Problem, string? ProblemDetail)
{
    public static RegistrySnapshot None { get; } = new(null, RegistryStatus.Unavailable, null, null, null);
}

/// <summary>
/// Fetches <c>registry.json</c> from the registry repository, keeps the last good copy and its ETag on disk, and falls back to that copy when
/// the network is down. The cache lives in the admin-only install folder (a copy that a non-elevated process could edit would be a way to
/// plant a module). Nothing identifying is sent: no version, no machine name, no cookies.
/// </summary>
public sealed class RegistryClient
{
    private readonly SafeDownloader _downloader;
    private readonly InstallPolicy _policy;
    private readonly InstallPaths _paths;
    private readonly IFolderSecurity _security;
    private readonly Uri _url;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILog _log;

    public RegistryClient(SafeDownloader downloader, InstallPolicy policy, InstallPaths paths, IFolderSecurity security, Uri url, ILog log, Func<DateTimeOffset>? clock = null)
    {
        _downloader = downloader;
        _policy = policy;
        _paths = paths;
        _security = security;
        _url = url;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private string CacheFile => Path.Combine(_paths.RegistryCache, "registry.json");
    private string MetaFile => Path.Combine(_paths.RegistryCache, "registry.meta.json");

    private sealed class Meta
    {
        public string Url { get; set; } = "";
        public string? ETag { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
    }

    /// <summary>The cached registry, if there is a valid one for this URL. Never touches the network.</summary>
    public RegistrySnapshot LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile) || !File.Exists(MetaFile)) return RegistrySnapshot.None;
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaFile), JsonSettingsStore.Options);
            if (meta is null || !string.Equals(meta.Url, _url.ToString(), StringComparison.Ordinal)) return RegistrySnapshot.None;
            var parsed = RegistryReader.Parse(File.ReadAllText(CacheFile), _policy);
            if (!parsed.IsValid)
            {
                _log.Warn("the cached registry is not valid and was ignored: " + parsed.Errors.FirstOrDefault());
                return RegistrySnapshot.None;
            }
            return new RegistrySnapshot(parsed.Document, RegistryStatus.Cached, meta.FetchedAt, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn("could not read the registry cache: " + ex.Message);
            return RegistrySnapshot.None;
        }
    }

    /// <summary>Asks the server (conditionally). On failure returns the cache with the reason, or nothing with the reason. Cancellation throws.</summary>
    public async Task<RegistrySnapshot> RefreshAsync(CancellationToken ct)
    {
        var cached = LoadCache();
        string? etag = null;
        try
        {
            if (cached.Document is not null && File.Exists(MetaFile))
                etag = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaFile), JsonSettingsStore.Options)?.ETag;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        try
        {
            var result = await _downloader.GetTextAsync(_url, etag, ct).ConfigureAwait(false);
            var now = _clock();
            if (result.NotModified && cached.Document is not null)
            {
                WriteMeta(new Meta { Url = _url.ToString(), ETag = etag, FetchedAt = now });
                _log.Info("registry: not modified");
                return new RegistrySnapshot(cached.Document, RegistryStatus.Fresh, now, null, null);
            }
            if (result.Body is null) throw new InstallException(InstallErrorCode.RegistryUnreachable, "the server answered 'not modified' but there is no cached copy");

            var parsed = RegistryReader.Parse(result.Body, _policy);
            if (!parsed.IsValid)
                throw new InstallException(InstallErrorCode.RegistryInvalid, "the registry is not valid: " + string.Join("; ", parsed.Errors.Take(3)) + (parsed.Errors.Count > 3 ? $" (+{parsed.Errors.Count - 3} more)" : ""));

            _paths.Prepare(_security);
            FileOps.WriteAtomic(CacheFile, result.Body);
            WriteMeta(new Meta { Url = _url.ToString(), ETag = result.ETag, FetchedAt = now });
            _log.Info($"registry: {parsed.Document!.Modules.Count} module(s) fetched");
            return new RegistrySnapshot(parsed.Document, RegistryStatus.Fresh, now, null, null);
        }
        catch (InstallException ex)
        {
            _log.Warn($"registry: {ex.Code}: {ex.Detail}");
            var code = ex.Code is InstallErrorCode.Offline or InstallErrorCode.RegistryInvalid or InstallErrorCode.UrlNotAllowed or InstallErrorCode.FolderSecurity ? ex.Code : InstallErrorCode.RegistryUnreachable;
            return cached.Document is not null
                ? cached with { Problem = code, ProblemDetail = ex.Detail }
                : new RegistrySnapshot(null, RegistryStatus.Unavailable, null, code, ex.Detail);
        }
    }

    private void WriteMeta(Meta meta)
    {
        try { FileOps.WriteAtomic(MetaFile, JsonSerializer.Serialize(meta, JsonSettingsStore.Options)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn("could not write the registry cache: " + ex.Message); }
    }
}
