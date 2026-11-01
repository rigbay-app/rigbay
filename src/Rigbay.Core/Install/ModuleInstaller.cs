using Rigbay.Abstractions;

namespace Rigbay.Core.Install;

public enum InstallPhase { Preparing, Downloading, Verifying, Unpacking, Checking, Finishing }

public readonly record struct InstallProgress(InstallPhase Phase, long Done, long? Total);

/// <summary>
/// Downloads, verifies and stages a release and queues the change for the next start; queues removals and roll-backs. Nothing under the
/// modules folder is touched here (the loaded DLLs are in use): the swap is <see cref="PendingRunner"/>'s job at the next start.
/// One change at a time. Cancelling or failing at any step leaves the modules folder and the pending file exactly as they were.
/// </summary>
public sealed class ModuleInstaller
{
    private readonly InstallPaths _paths;
    private readonly InstallPolicy _policy;
    private readonly SafeDownloader _downloader;
    private readonly IFolderSecurity _security;
    private readonly Version _hostApi;
    private readonly ILog _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public ModuleInstaller(InstallPaths paths, InstallPolicy policy, SafeDownloader downloader, IFolderSecurity security, Version hostApi, ILog log)
    {
        _paths = paths;
        _policy = policy;
        _downloader = downloader;
        _security = security;
        _hostApi = hostApi;
        _log = log;
        Pending = new PendingStore(paths, log);
    }

    public PendingStore Pending { get; }

    /// <summary>Installs a module that is not there yet.</summary>
    public Task InstallAsync(RegistryModule module, RegistryVersion version, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        StageAsync(module, version, update: false, progress, ct);

    /// <summary>Replaces the installed version by a newer one at the next start.</summary>
    public Task UpdateAsync(RegistryModule module, RegistryVersion version, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        StageAsync(module, version, update: true, progress, ct);

    private async Task StageAsync(RegistryModule module, RegistryVersion version, bool update, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (!_busy.Wait(0)) throw new InstallException(InstallErrorCode.Busy, "another change is running");
        string opId = Guid.NewGuid().ToString("N")[..10];
        string zipPath = Path.Combine(_paths.Downloads, opId + ".zip");
        string stagingRoot = Path.Combine(_paths.Staging, opId);
        try
        {
            ct.ThrowIfCancellationRequested();
            Report(progress, InstallPhase.Preparing, 0, null);

            // --- what is allowed at all
            var installedDir = _paths.ModuleFolder(module.Id);
            bool installed = Directory.Exists(installedDir);
            string? installedVersion = installed ? ManifestReader.ReadFolder(installedDir).Manifest?.Version : null;
            if (!update && installed) throw new InstallException(InstallErrorCode.AlreadyInstalled, $"'{module.Id}' is already installed");
            if (update)
            {
                if (!installed) throw new InstallException(InstallErrorCode.NotInstalled, $"'{module.Id}' is not installed");
                if (installedVersion is not null && SemVer.TryParse(installedVersion, out var have) && version.Semver <= have)
                    throw new InstallException(InstallErrorCode.AlreadyInstalled, $"version {installedVersion} is installed, {version.Version} is not newer");
            }
            if (Pending.Read().Any(o => o.Id == module.Id)) throw new InstallException(InstallErrorCode.AlreadyPending, $"a change to '{module.Id}' is already waiting for a restart");
            if (!version.SupportsHost(_hostApi)) throw new InstallException(InstallErrorCode.IncompatibleHostApi, $"version {version.Version} needs host API {version.HostApi}, this is {_hostApi.Major}.{_hostApi.Minor}");
            if (!_policy.IsZipUrlOfRepo(version.Zip, module.Repo)) throw new InstallException(InstallErrorCode.UrlNotAllowed, "the zip address is not a release download of the module's repository");
            if (!Uri.TryCreate(version.Zip, UriKind.Absolute, out var zipUri)) throw new InstallException(InstallErrorCode.UrlNotAllowed, "the zip address is not valid");

            _paths.Prepare(_security);

            // --- download, counted and hashed while it is written
            _log.Info($"{(update ? "update" : "install")} {module.Id} {version.Version}: downloading {version.Zip}");
            var downloadProgress = progress is null ? null : new InlineProgress<DownloadProgress>(p => Report(progress, InstallPhase.Downloading, p.Done, p.Total));
            var result = await _downloader.DownloadFileAsync(zipUri, zipPath, version.Size, downloadProgress, ct).ConfigureAwait(false);

            // --- the hash comes before anything is unpacked
            Report(progress, InstallPhase.Verifying, result.Bytes, result.Bytes);
            if (!string.Equals(result.Sha256, version.Sha256, StringComparison.Ordinal))
                throw new InstallException(InstallErrorCode.HashMismatch, $"SHA-256 of the download is {result.Sha256[..16]}…, the registry says {version.Sha256[..16]}…");

            // --- unpack into a fresh folder named like the module (the manifest check wants folder name == id)
            Report(progress, InstallPhase.Unpacking, 0, null);
            var staged = Path.Combine(stagingRoot, module.Id);
            await Task.Run(() => SafeZip.Extract(zipPath, staged, _policy, ct), ct).ConfigureAwait(false);

            // --- the module.json inside must be what the registry promised
            Report(progress, InstallPhase.Checking, 0, null);
            CheckManifest(module, version, staged);

            // --- park it where the next start picks it up
            Report(progress, InstallPhase.Finishing, 0, null);
            var ready = _paths.ReadyFolder(module.Id);
            if (Directory.Exists(ready)) FileOps.SafeDelete(ready);
            FileOps.MoveDirectory(staged, ready);
            var treeHash = FileOps.TreeHash(ready);
            Pending.Upsert(new PendingOp
            {
                Op = update ? PendingKinds.Update : PendingKinds.Install,
                Id = module.Id,
                Version = version.Version,
                FromVersion = installedVersion,
                TreeHash = treeHash,
                Sha256 = version.Sha256,
                Source = version.Zip,
                Queued = DateTimeOffset.Now,
            });
            _log.Info($"{(update ? "update" : "install")} {module.Id} {version.Version}: verified and queued, waiting for a restart");
        }
        finally
        {
            Cleanup(zipPath);
            Cleanup(stagingRoot);
            _busy.Release();
        }
    }

    /// <summary>Compares the staged module.json with the registry entry. Throws <see cref="InstallException"/> (ManifestMismatch or DeclaresWider).</summary>
    internal void CheckManifest(RegistryModule module, RegistryVersion version, string stagedFolder)
    {
        // say "this is another module" before the folder-bound validation can say "invalid"
        var manifestFile = Path.Combine(stagedFolder, ManifestReader.FileName);
        if (File.Exists(manifestFile) && new FileInfo(manifestFile).Length <= ManifestReader.MaxBytes
            && ManifestReader.Parse(File.ReadAllText(manifestFile), null).Manifest is { } peek)
        {
            if (!string.Equals(peek.Id, module.Id, StringComparison.Ordinal))
                throw new InstallException(InstallErrorCode.ManifestMismatch, $"the zip holds module '{peek.Id}', the registry entry is '{module.Id}'");
            if (!string.Equals(peek.Version, version.Version, StringComparison.Ordinal))
                throw new InstallException(InstallErrorCode.ManifestMismatch, $"the zip holds version {peek.Version}, the registry entry is {version.Version}");
        }
        var result = ManifestReader.ReadFolder(stagedFolder);
        if (result.Manifest is not { } m)
            throw new InstallException(InstallErrorCode.ManifestMismatch, "the module.json in the zip is not valid: " + result.ErrorSummary);
        if (!string.Equals(m.Id, module.Id, StringComparison.Ordinal))
            throw new InstallException(InstallErrorCode.ManifestMismatch, $"the zip holds module '{m.Id}', the registry entry is '{module.Id}'");
        if (!string.Equals(m.Version, version.Version, StringComparison.Ordinal))
            throw new InstallException(InstallErrorCode.ManifestMismatch, $"the zip holds version {m.Version}, the registry entry is {version.Version}");
        if (!m.HostApi.Contains(_hostApi))
            throw new InstallException(InstallErrorCode.IncompatibleHostApi, $"the module.json needs host API {m.HostApi.Text}, this is {_hostApi.Major}.{_hostApi.Minor}");
        var actual = RegistryDeclares.Of(m);
        if (!version.Declares.Covers(actual))
            throw new InstallException(InstallErrorCode.DeclaresWider, "the module.json declares more than the registry says: " + version.Declares.DescribeExcess(actual));
    }

    /// <summary>Queues the removal of an installed module for the next start.</summary>
    public void QueueRemove(string id)
    {
        if (!_busy.Wait(0)) throw new InstallException(InstallErrorCode.Busy, "another change is running");
        try
        {
            var folder = _paths.ModuleFolder(id);
            if (!Directory.Exists(folder)) throw new InstallException(InstallErrorCode.NotInstalled, $"'{id}' is not installed");
            if (Pending.Read().Any(o => o.Id == id)) throw new InstallException(InstallErrorCode.AlreadyPending, $"a change to '{id}' is already waiting for a restart");
            _paths.Prepare(_security);
            Pending.Upsert(new PendingOp { Op = PendingKinds.Remove, Id = id, FromVersion = ManifestReader.ReadFolder(folder).Manifest?.Version, Queued = DateTimeOffset.Now });
            _log.Info($"remove {id}: queued, waiting for a restart");
        }
        finally { _busy.Release(); }
    }

    /// <summary>Queues going back to the version that was replaced (still kept in <c>previous</c>).</summary>
    public void QueueRollback(string id)
    {
        if (!_busy.Wait(0)) throw new InstallException(InstallErrorCode.Busy, "another change is running");
        try
        {
            if (!Directory.Exists(_paths.PreviousFolder(id))) throw new InstallException(InstallErrorCode.NothingToRollBack, $"no earlier version of '{id}' is kept");
            if (Pending.Read().Any(o => o.Id == id)) throw new InstallException(InstallErrorCode.AlreadyPending, $"a change to '{id}' is already waiting for a restart");
            _paths.Prepare(_security);
            Pending.Upsert(new PendingOp
            {
                Op = PendingKinds.Rollback,
                Id = id,
                FromVersion = ManifestReader.ReadFolder(_paths.ModuleFolder(id)).Manifest?.Version,
                Version = PreviousVersion(id),
                Queued = DateTimeOffset.Now,
            });
            _log.Info($"rollback {id}: queued, waiting for a restart");
        }
        finally { _busy.Release(); }
    }

    /// <summary>Takes a queued change back (before the restart): the staged files are deleted, nothing else changed.</summary>
    public bool CancelPending(string id)
    {
        if (!_busy.Wait(0)) throw new InstallException(InstallErrorCode.Busy, "another change is running");
        try
        {
            var op = Pending.Remove(id);
            if (op is null) return false;
            Cleanup(_paths.ReadyFolder(id));
            _log.Info($"{op.Op} {id}: taken back before the restart");
            return true;
        }
        finally { _busy.Release(); }
    }

    /// <summary>The version of the kept previous copy of a module, or null.</summary>
    public string? PreviousVersion(string id)
    {
        var folder = _paths.PreviousFolder(id);
        return Directory.Exists(folder) ? ManifestReader.ReadFolder(folder).Manifest?.Version : null;
    }

    private static void Report(IProgress<InstallProgress>? progress, InstallPhase phase, long done, long? total) => progress?.Report(new InstallProgress(phase, done, total));

    private void Cleanup(string path)
    {
        try { if (Directory.Exists(path) || File.Exists(path)) FileOps.SafeDelete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn($"could not delete {path}: {ex.Message}"); }
    }
}

/// <summary>Like <see cref="Progress{T}"/> but calls the handler on the reporting thread, in order (no post to another thread).</summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
