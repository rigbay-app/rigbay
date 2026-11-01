using System.Text.Json;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Rigbay.Abstractions;

namespace Rigbay.Core.Install;

public static class PendingKinds
{
    public const string Install = "install";
    public const string Update = "update";
    public const string Remove = "remove";
    public const string Rollback = "rollback";
}

/// <summary>One change waiting for the next start. The new files (install, update) are already verified and sit in <c>ready\&lt;id&gt;</c>.</summary>
public sealed class PendingOp
{
    public string Op { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Version { get; set; }
    public string? FromVersion { get; set; }
    /// <summary>Hash of the staged folder, checked again right before the swap.</summary>
    public string? TreeHash { get; set; }
    public string? Sha256 { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset Queued { get; set; }
}

public sealed class PendingFile
{
    public int Version { get; set; } = 1;
    public List<PendingOp> Ops { get; set; } = new();
}

/// <summary>The small file the next start reads before the loader runs.</summary>
public sealed class PendingStore
{
    private static readonly object Gate = new();
    private readonly InstallPaths _paths;
    private readonly ILog _log;

    public PendingStore(InstallPaths paths, ILog log)
    {
        _paths = paths;
        _log = log;
    }

    public IReadOnlyList<PendingOp> Read()
    {
        lock (Gate) return ReadLocked();
    }

    private List<PendingOp> ReadLocked()
    {
        try
        {
            if (!File.Exists(_paths.PendingFile)) return new();
            var file = JsonSerializer.Deserialize<PendingFile>(File.ReadAllText(_paths.PendingFile), JsonSettingsStore.Options);
            if (file is null || file.Version != 1) throw new JsonException("unknown format");
            return file.Ops.Where(o => o.Op is PendingKinds.Install or PendingKinds.Update or PendingKinds.Remove or PendingKinds.Rollback && IsId(o.Id)).ToList();
        }
        catch (JsonException ex)
        {
            var aside = _paths.PendingFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _log.Warn($"pending.json is not readable ({ex.Message}); moved to {Path.GetFileName(aside)}");
            try { File.Move(_paths.PendingFile, aside, overwrite: true); } catch (Exception) { }
            return new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("could not read pending.json: " + ex.Message);
            return new();
        }
    }

    private static bool IsId(string id) => id.Length is >= 2 and <= 40 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private void WriteLocked(List<PendingOp> ops)
    {
        if (ops.Count == 0) { if (File.Exists(_paths.PendingFile)) File.Delete(_paths.PendingFile); return; }
        FileOps.WriteAtomic(_paths.PendingFile, JsonSerializer.Serialize(new PendingFile { Ops = ops }, JsonSettingsStore.Options));
    }

    /// <summary>Adds an op, replacing an older one for the same id.</summary>
    public void Upsert(PendingOp op)
    {
        lock (Gate)
        {
            var ops = ReadLocked();
            ops.RemoveAll(o => o.Id == op.Id);
            ops.Add(op);
            WriteLocked(ops);
        }
    }

    public PendingOp? Remove(string id)
    {
        lock (Gate)
        {
            var ops = ReadLocked();
            var found = ops.FirstOrDefault(o => o.Id == id);
            if (found is null) return null;
            ops.Remove(found);
            WriteLocked(ops);
            return found;
        }
    }
}

/// <summary>What happened to one pending change at the start.</summary>
public sealed record PendingOutcome(string Op, string Id, string? FromVersion, string? ToVersion, bool Applied, bool WillRetry, string? Message, bool NeedsApproval);

public sealed record PendingRunReport(DateTimeOffset Time, IReadOnlyList<PendingOutcome> Outcomes);

/// <summary>
/// Applies the pending changes before the loader runs (nothing is loaded yet, so no module file is in use). It decides from what is on disk, not
/// from a journal, so it can be started again after a crash at any step and finish: each change is a few renames, and every rename is checked
/// first ("is the source still there, is the target already there"). A change that fails for a lock (a scanner, a second process) is kept for the
/// next start; a change that is wrong (the staged folder changed, the id is taken) is dropped and logged.
/// </summary>
public static class PendingRunner
{
    /// <summary>Tests only: called with a step name ("update:old-moved") before each step that changes the disk; an exception thrown from it stands for the process dying there.</summary>
    public static Action<string>? StepHook { get; set; }

    private static void Step(string name) => StepHook?.Invoke(name);

    public static PendingRunReport Run(InstallPaths paths, IFolderSecurity security, string? dataRoot, Action<string> log)
    {
        var outcomes = new List<PendingOutcome>();
        if (!Directory.Exists(paths.Root)) return new PendingRunReport(DateTimeOffset.Now, outcomes);

        var store = new PendingStore(paths, new ActionLog(log));
        var ops = store.Read();
        try
        {
            paths.Prepare(security);
        }
        catch (InstallException ex)
        {
            log("install folders are not safe, pending changes are not applied: " + ex.Detail);
            foreach (var op in ops) outcomes.Add(new PendingOutcome(op.Op, op.Id, op.FromVersion, op.Version, false, true, ex.Detail, false));
            return new PendingRunReport(DateTimeOffset.Now, outcomes);
        }

        foreach (var dir in new[] { paths.Downloads, paths.Staging, paths.Trash })
        {
            var problem = FileOps.TryClearFolder(dir);
            if (problem is not null) log($"could not empty {dir}: {problem}");
        }

        var settings = new SettingsFiles(new RigbayPaths(dataRoot));
        foreach (var op in ops)
        {
            PendingOutcome outcome;
            try
            {
                outcome = Apply(op, paths, settings, log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"{op.Op} {op.Id}: not done now, will try again at the next start: {ex.Message}");
                outcomes.Add(new PendingOutcome(op.Op, op.Id, op.FromVersion, op.Version, false, true, ex.Message, false));
                continue;
            }
            catch (InstallException ex)
            {
                log($"{op.Op} {op.Id}: dropped: {ex.Detail}");
                TryDelete(paths.ReadyFolder(op.Id), log);
                store.Remove(op.Id);
                outcomes.Add(new PendingOutcome(op.Op, op.Id, op.FromVersion, op.Version, false, false, ex.Detail, false));
                continue;
            }
            Step("op:applied-before-forget");

            store.Remove(op.Id);

            Step("op:forgotten");
            outcomes.Add(outcome);
            log($"{op.Op} {op.Id}: {(outcome.Applied ? "applied" : "nothing to do")}{(outcome.ToVersion is null ? "" : " (" + (outcome.FromVersion ?? "-") + " -> " + outcome.ToVersion + ")")}");
        }

        // staged folders that no remaining change refers to are leftovers
        var wanted = store.Read().Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (Directory.Exists(paths.Ready))
            foreach (var dir in Directory.GetDirectories(paths.Ready))
                if (!wanted.Contains(Path.GetFileName(dir))) TryDelete(dir, log);

        var report = new PendingRunReport(DateTimeOffset.Now, outcomes);
        if (outcomes.Count > 0)
        {
            try { FileOps.WriteAtomic(paths.LastRunFile, JsonSerializer.Serialize(report, JsonSettingsStore.Options)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("could not write last-run.json: " + ex.Message); }
        }
        return report;
    }

    /// <summary>The report of the last start that applied something, or null.</summary>
    public static PendingRunReport? ReadLastRun(InstallPaths paths)
    {
        try
        {
            return File.Exists(paths.LastRunFile) ? JsonSerializer.Deserialize<PendingRunReport>(File.ReadAllText(paths.LastRunFile), JsonSettingsStore.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static PendingOutcome Apply(PendingOp op, InstallPaths paths, SettingsFiles settings, Action<string> log)
    {
        var target = paths.ModuleFolder(op.Id);
        var ready = paths.ReadyFolder(op.Id);
        var previous = paths.PreviousFolder(op.Id);
        if (FileOps.IsReparsePoint(target)) throw new InstallException(InstallErrorCode.ApplyFailed, $"'{target}' is a link");
        // A folder can be renamed while the DLLs in it are loaded (Windows allows it), so this is the guard: a module whose code is loaded in this
        // process is never touched. At a real start nothing is loaded yet; this only matters if somebody calls Run later.
        if (IsLoaded(op.Id)) throw new IOException($"module '{op.Id}' is loaded in this process; its folder is changed at the next start");

        switch (op.Op)
        {
            case PendingKinds.Install:
            {
                if (!Directory.Exists(ready)) return new PendingOutcome(op.Op, op.Id, null, op.Version, Directory.Exists(target), false, null, false);   // done before a crash, or gone
                if (Directory.Exists(target)) throw new InstallException(InstallErrorCode.AlreadyInstalled, $"a module folder '{op.Id}' is already there");
                VerifyReady(op, ready);
                Step("install:before-disable");
                settings.Disable(op.Id, forgetApproval: true);      // first: a crash after this leaves a module that is simply off
                Step("install:disabled");
                FileOps.MoveDirectory(ready, target);
                Step("install:moved");
                return new PendingOutcome(op.Op, op.Id, null, op.Version, true, false, null, true);
            }
            case PendingKinds.Update:
            {
                if (!Directory.Exists(ready))
                {
                    if (!Directory.Exists(target) && Directory.Exists(previous))
                    {
                        FileOps.MoveDirectory(previous, target);   // lost the new files after moving the old ones away: put the old back
                        throw new InstallException(InstallErrorCode.ApplyFailed, "the staged files were missing; the previous version was put back");
                    }
                    return new PendingOutcome(op.Op, op.Id, op.FromVersion, op.Version, Directory.Exists(target), false, null, false);   // done before a crash
                }
                VerifyReady(op, ready);
                var oldFolder = Directory.Exists(target) ? target : Directory.Exists(previous) ? previous : null;
                var before = oldFolder is null ? null : ManifestReader.ReadFolder(oldFolder).Manifest;
                var after = ManifestReader.ReadFolder(ready).Manifest ?? throw new InstallException(InstallErrorCode.ManifestMismatch, "the staged module.json is no longer valid");
                bool widened = before is null || !RegistryDeclares.Of(before).Covers(RegistryDeclares.Of(after));
                if (widened) settings.Disable(op.Id, forgetApproval: true);   // an update that asks for more is never started before the user has accepted that
                Step("update:disabled");
                if (Directory.Exists(target))
                {
                    TryDeleteOrThrow(previous);
                    FileOps.MoveDirectory(target, previous);
                    Step("update:old-moved");
                }
                FileOps.MoveDirectory(ready, target);
                Step("update:new-moved");
                return new PendingOutcome(op.Op, op.Id, before?.Version ?? op.FromVersion, after.Version, true, false, null, widened);
            }
            case PendingKinds.Remove:
            {
                if (!Directory.Exists(target)) return new PendingOutcome(op.Op, op.Id, op.FromVersion, null, false, false, null, false);
                settings.Disable(op.Id, forgetApproval: true);
                Step("remove:disabled");
                TryDeleteOrThrow(previous);
                FileOps.MoveDirectory(target, previous);
                Step("remove:moved");
                return new PendingOutcome(op.Op, op.Id, op.FromVersion, null, true, false, null, false);
            }
            case PendingKinds.Rollback:
            {
                if (!Directory.Exists(previous)) return new PendingOutcome(op.Op, op.Id, null, null, false, false, "no previous version", false);
                var oldVersion = ManifestReader.ReadFolder(previous).Manifest?.Version;
                settings.Disable(op.Id, forgetApproval: false);
                Step("rollback:disabled");
                string? trash = null;
                if (Directory.Exists(target))
                {
                    trash = Path.Combine(paths.Trash, op.Id + "-" + Guid.NewGuid().ToString("N")[..8]);
                    FileOps.MoveDirectory(target, trash);
                }
                Step("rollback:old-trashed");
                FileOps.MoveDirectory(previous, target);
                Step("rollback:previous-restored");
                if (trash is not null) TryDelete(trash, log);
                return new PendingOutcome(op.Op, op.Id, op.FromVersion, oldVersion, true, false, null, false);
            }
            default:
                throw new InstallException(InstallErrorCode.ApplyFailed, "unknown change '" + op.Op + "'");
        }
    }

    /// <summary>True when a module load context named like the loader names it (<c>module:&lt;id&gt;</c>) exists in this process.</summary>
    public static bool IsLoaded(string id) => AssemblyLoadContext.All.Any(c => c.Name == "module:" + id);

    private static void VerifyReady(PendingOp op, string ready)
    {
        if (op.TreeHash is null) throw new InstallException(InstallErrorCode.ApplyFailed, "the change has no hash of its files");
        string actual;
        try { actual = FileOps.TreeHash(ready); }
        catch (IOException ex) when (ex.Message.StartsWith("a link", StringComparison.Ordinal)) { throw new InstallException(InstallErrorCode.UnsafeZip, ex.Message); }
        if (!string.Equals(actual, op.TreeHash, StringComparison.Ordinal))
            throw new InstallException(InstallErrorCode.ApplyFailed, "the staged files changed after they were verified");
    }

    private static void TryDeleteOrThrow(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) FileOps.SafeDelete(path);
    }

    private static void TryDelete(string path, Action<string> log)
    {
        try { FileOps.SafeDelete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log($"could not delete {path}: {ex.Message}"); }
    }

    /// <summary>
    /// After a start: a kept previous version is deleted when its module is healthy again (or gone, after a removal). Returns the ids kept.
    /// <paramref name="isHealthy"/> gets the module id and says whether the module that is installed now loaded fine.
    /// </summary>
    public static IReadOnlyList<string> CommitHealthy(InstallPaths paths, Func<string, bool> isHealthy, Action<string> log)
    {
        var kept = new List<string>();
        if (!Directory.Exists(paths.Previous)) return kept;
        foreach (var dir in Directory.GetDirectories(paths.Previous))
        {
            var id = Path.GetFileName(dir);
            if (!isHealthy(id)) { kept.Add(id); log($"previous version of {id} kept: the installed one is not healthy"); continue; }
            TryDelete(dir, log);
        }
        return kept;
    }

    private sealed class ActionLog(Action<string> write) : Abstractions.ILog
    {
        public void Write(Abstractions.LogLevel level, string message, Exception? exception = null) => write((level == Abstractions.LogLevel.Info ? "" : level + ": ") + message);
    }

    /// <summary><c>host.json</c> (enabled modules) and <c>app.json</c> (modules whose declarations were accepted), edited without knowing their other fields.</summary>
    private sealed class SettingsFiles(RigbayPaths paths)
    {
        public void Disable(string id, bool forgetApproval, bool disable = true)
        {
            if (disable) RemoveFromList(paths.HostSettingsFile, "enabledModules", id);
            if (forgetApproval) RemoveFromList(Path.Combine(paths.DataRoot, "app.json"), "acknowledged", id);
        }

        private static void RemoveFromList(string file, string listName, string id)
        {
            if (!File.Exists(file)) return;
            JsonObject? root;
            try { root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
            catch (JsonException) { return; }
            if (root?["data"] is not JsonObject data) return;
            foreach (var property in data.ToList())
            {
                if (!string.Equals(property.Key, listName, StringComparison.OrdinalIgnoreCase) || property.Value is not JsonArray list) continue;
                var keep = list.Where(n => !(n is JsonValue v && v.TryGetValue<string>(out var s) && s == id)).Select(n => n?.DeepClone()).ToList();
                if (keep.Count == list.Count) return;
                data[property.Key] = new JsonArray(keep.ToArray());
                FileOps.WriteAtomic(file, root.ToJsonString(JsonSettingsStore.Options));
                return;
            }
        }
    }
}
