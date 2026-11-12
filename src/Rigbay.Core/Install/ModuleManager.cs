using Rigbay.Abstractions;

namespace Rigbay.Core.Install;

/// <summary>A module the host found in its modules folder (valid or not).</summary>
public sealed record InstalledModule(string Id, string? Version, ModuleLifecycle Lifecycle);

/// <summary>The manager's view of the host. A fake in tests, <see cref="HostInstalledModules"/> in the app.</summary>
public interface IInstalledModules
{
    IReadOnlyList<InstalledModule> Current { get; }

    /// <summary>Shows "update available" or "restart required" on the module (<see cref="PendingChange.None"/> clears it).</summary>
    void MarkPending(string id, PendingChange change, string? version);
}

/// <summary>Reads the installed modules from a <see cref="RigbayHost"/>.</summary>
public sealed class HostInstalledModules : IInstalledModules
{
    private readonly RigbayHost _host;

    public HostInstalledModules(RigbayHost host) => _host = host;

    public IReadOnlyList<InstalledModule> Current => _host.Modules.Select(m => new InstalledModule(m.Id, m.Manifest?.Version, m.Lifecycle)).ToList();

    public void MarkPending(string id, PendingChange change, string? version)
    {
        var entry = _host.Find(id);
        if (entry is null || (entry.Pending == change && entry.PendingVersion == version)) return;
        entry.SetPending(change, version);
    }
}

public enum OperationKind { Install, Update, Remove, Rollback, Undo }

public sealed record OperationState(string Id, OperationKind Kind, InstallPhase Phase, long Done, long? Total);

public sealed record OperationFailure(string Id, OperationKind Kind, InstallErrorCode Code, string Detail);

public enum AvailableState
{
    /// <summary>Not installed, a release fits this host: the Install button.</summary>
    Installable,
    /// <summary>Already in the modules folder.</summary>
    Installed,
    /// <summary>Queued: it appears after the next start.</summary>
    Queued,
    /// <summary>No release supports this host's API.</summary>
    Incompatible,
}

public sealed record AvailableRow(RegistryModule Module, RegistryVersion? Version, AvailableState State, string? InstalledVersion, PendingOp? Pending);

public sealed record InstalledRow(InstalledModule Module, RegistryVersion? Update, PendingOp? Pending, string? PreviousVersion);

/// <summary>
/// Everything the Modules page needs, without any UI type: the registry snapshot, the installed modules next to the registry's entries,
/// the one running operation with its progress and cancel, the last failure per module, and what waits for a restart. It also puts
/// "update available" and "restart required" on the installed modules.
/// </summary>
public sealed class ModuleManager
{
    private readonly object _gate = new();
    private readonly ModuleInstaller _installer;
    private readonly RegistryClient _registry;
    private readonly IInstalledModules _installed;
    private readonly Version _hostApi;
    private readonly ILog _log;
    private readonly Dictionary<string, OperationFailure> _failures = new();
    private RegistrySnapshot _snapshot = RegistrySnapshot.None;
    private OperationState? _operation;
    private CancellationTokenSource? _cts;
    private bool _checking;
    private long _lastRaiseTicks;

    public ModuleManager(ModuleInstaller installer, RegistryClient registry, IInstalledModules installed, Version hostApi, ILog log)
    {
        _installer = installer;
        _registry = registry;
        _installed = installed;
        _hostApi = hostApi;
        _log = log;
    }

    /// <summary>Raised on whatever thread changed something; the UI marshals.</summary>
    public event Action? Changed;

    public RegistrySnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool IsChecking { get { lock (_gate) return _checking; } }
    public OperationState? Operation { get { lock (_gate) return _operation; } }
    public IReadOnlyList<PendingOp> PendingOps => _installer.Pending.Read();
    public bool RestartRequired => PendingOps.Count > 0;

    /// <summary>
    /// The name the registry gives a module, for a module the host no longer knows (a removed one: its folder is gone, so only the id is left).
    /// Null when there is no list or the id is not in it.
    /// </summary>
    public string? RegistryNameOf(string id) => Snapshot.Document?.Find(id)?.Name;

    public OperationFailure? FailureOf(string id)
    {
        lock (_gate) return _failures.TryGetValue(id, out var f) ? f : null;
    }

    public void DismissFailure(string id)
    {
        lock (_gate) _failures.Remove(id);
        Raise(force: true);
    }

    /// <summary>Takes the registry copy from disk (no network). Call once at start.</summary>
    public void LoadCache()
    {
        var cached = _registry.LoadCache();
        lock (_gate) _snapshot = cached;
        RefreshMarks();
        Raise(force: true);
    }

    /// <summary>"Check for updates": asks the server and falls back to the copy on disk. Never throws (cancellation aside).</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_checking) return;
            _checking = true;
        }
        Raise(force: true);
        try
        {
            var snapshot = await Task.Run(() => _registry.RefreshAsync(ct), ct).ConfigureAwait(false);
            lock (_gate) _snapshot = snapshot;
        }
        finally
        {
            lock (_gate) _checking = false;
            RefreshMarks();
            Raise(force: true);
        }
    }

    // ---------------------------------------------------------------- rows

    public IReadOnlyList<AvailableRow> AvailableRows()
    {
        var doc = Snapshot.Document;
        if (doc is null) return Array.Empty<AvailableRow>();
        var installed = _installed.Current.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var pending = PendingOps.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var rows = new List<AvailableRow>();
        foreach (var module in doc.Modules)
        {
            var latest = module.Latest(_hostApi);
            pending.TryGetValue(module.Id, out var op);
            if (installed.TryGetValue(module.Id, out var have)) rows.Add(new AvailableRow(module, latest, AvailableState.Installed, have.Version, op));
            else if (op is { Op: PendingKinds.Install }) rows.Add(new AvailableRow(module, latest, AvailableState.Queued, null, op));
            else if (latest is null) rows.Add(new AvailableRow(module, module.Newest, AvailableState.Incompatible, null, null));
            else rows.Add(new AvailableRow(module, latest, AvailableState.Installable, null, null));
        }
        return rows;
    }

    public InstalledRow InstalledRowOf(InstalledModule m)
    {
        var op = PendingOps.FirstOrDefault(p => p.Id == m.Id);
        return new InstalledRow(m, UpdateFor(m), op, _installer.PreviousVersion(m.Id));
    }

    /// <summary>The release an installed module could be updated to, or null.</summary>
    public RegistryVersion? UpdateFor(InstalledModule m)
    {
        if (m.Version is null || !SemVer.TryParse(m.Version, out var have)) return null;
        var latest = Snapshot.Document?.Find(m.Id)?.Latest(_hostApi);
        return latest is not null && latest.Semver > have ? latest : null;
    }

    private void RefreshMarks()
    {
        var pending = PendingOps.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var m in _installed.Current)
        {
            if (pending.ContainsKey(m.Id)) { _installed.MarkPending(m.Id, PendingChange.RestartRequired, null); continue; }
            var update = UpdateFor(m);
            if (update is not null) _installed.MarkPending(m.Id, PendingChange.UpdateAvailable, update.Version);
            else _installed.MarkPending(m.Id, PendingChange.None, null);
        }
    }

    // ---------------------------------------------------------------- operations

    public Task<bool> InstallAsync(string id) => RunAsync(id, OperationKind.Install, (module, version, progress, ct) =>
        _installer.InstallAsync(module!, version!, progress, ct), needsRelease: true);

    public Task<bool> UpdateAsync(string id) => RunAsync(id, OperationKind.Update, (module, version, progress, ct) =>
        _installer.UpdateAsync(module!, version!, progress, ct), needsRelease: true);

    public Task<bool> RemoveAsync(string id) => RunAsync(id, OperationKind.Remove, (_, _, _, _) => { _installer.QueueRemove(id); return Task.CompletedTask; }, needsRelease: false);

    public Task<bool> RollbackAsync(string id) => RunAsync(id, OperationKind.Rollback, (_, _, _, _) => { _installer.QueueRollback(id); return Task.CompletedTask; }, needsRelease: false);

    /// <summary>Takes a queued change back before the restart.</summary>
    public Task<bool> UndoAsync(string id) => RunAsync(id, OperationKind.Undo, (_, _, _, _) =>
    {
        if (!_installer.CancelPending(id)) throw new InstallException(InstallErrorCode.NotInstalled, "there is nothing queued for '" + id + "'");
        return Task.CompletedTask;
    }, needsRelease: false);

    /// <summary>Asks the running download to stop. The state afterwards is exactly the one before.</summary>
    public void CancelOperation()
    {
        lock (_gate) _cts?.Cancel();
    }

    private async Task<bool> RunAsync(string id, OperationKind kind, Func<RegistryModule?, RegistryVersion?, IProgress<InstallProgress>, CancellationToken, Task> work, bool needsRelease)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_operation is not null)
            {
                _failures[id] = new OperationFailure(id, kind, InstallErrorCode.Busy, "another change is running");
                Raise(force: true);
                return false;
            }
            cts = _cts = new CancellationTokenSource();
            _operation = new OperationState(id, kind, InstallPhase.Preparing, 0, null);
            _failures.Remove(id);
        }
        Raise(force: true);

        bool ok = false;
        try
        {
            RegistryModule? module = null;
            RegistryVersion? version = null;
            if (needsRelease)
            {
                (module, version) = ChooseRelease(id, kind);
            }
            var progress = new Progress<InstallProgress>(p => OnProgress(id, kind, p));
            await Task.Run(() => work(module, version, progress, cts.Token), cts.Token).ConfigureAwait(false);
            ok = true;
        }
        catch (OperationCanceledException)
        {
            _log.Info($"{kind} {id}: cancelled");
        }
        catch (InstallException ex)
        {
            _log.Warn($"{kind} {id} failed: {ex.Code}: {ex.Detail}");
            lock (_gate) _failures[id] = new OperationFailure(id, kind, ex.Code, ex.Detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{kind} {id} failed: {ex.Message}", ex);
            lock (_gate) _failures[id] = new OperationFailure(id, kind, InstallErrorCode.ApplyFailed, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _operation = null;
                _cts = null;
            }
            cts.Dispose();
            RefreshMarks();
            Raise(force: true);
        }
        return ok;
    }

    private (RegistryModule, RegistryVersion) ChooseRelease(string id, OperationKind kind)
    {
        var module = Snapshot.Document?.Find(id) ?? throw new InstallException(InstallErrorCode.NotInRegistry, $"'{id}' is not in the registry copy Rigbay has");
        var version = module.Latest(_hostApi);
        if (version is null)
        {
            var newest = module.Newest;
            throw new InstallException(newest is null ? InstallErrorCode.NoVersion : InstallErrorCode.IncompatibleHostApi,
                newest is null ? "the registry lists no release" : $"no release supports this Rigbay (latest {newest.Version} {HostApiText.Needs(newest.HostApiRange, _hostApi)})");
        }
        return (module, version);
    }

    private void OnProgress(string id, OperationKind kind, InstallProgress p)
    {
        lock (_gate)
        {
            if (_operation is null || _operation.Id != id) return;
            _operation = _operation with { Phase = p.Phase, Done = p.Done, Total = p.Total };
        }
        Raise(force: false);
    }

    private void Raise(bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaiseTicks) < 120) return;
        Interlocked.Exchange(ref _lastRaiseTicks, now);
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.Warn("a Changed handler threw: " + ex.Message); }
    }
}
