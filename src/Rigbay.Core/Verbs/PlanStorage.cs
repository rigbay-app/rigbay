using System.Text.Json;
using Rigbay.Abstractions;
using Rigbay.Core.Install;

namespace Rigbay.Core;

/// <summary>A command line stored in the journal.</summary>
public sealed class StepRef
{
    public string Module { get; set; } = "";
    public string Verb { get; set; } = "";
    public List<string> Args { get; set; } = new();
}

/// <summary>What the owner approved: the hash of the exact plan and the target versions it was approved with.</summary>
public sealed class ApprovalRecord
{
    public string Owner { get; set; } = "";
    public string PlanId { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTimeOffset ApprovedAt { get; set; }
    public bool Revoked { get; set; }
    public Dictionary<string, string> Versions { get; set; } = new();
    public List<string> Lines { get; set; } = new();
}

/// <summary>One step of an open run as it was run.</summary>
public sealed class RunStepRecord
{
    public StepRef Step { get; set; } = new();
    public StepRef? FixedUndo { get; set; }
    public bool RestoreRecorded { get; set; }
    public bool Done { get; set; }
    public StepRef? RecordedUndo { get; set; }
}

/// <summary>A run that was started and not yet put back.</summary>
public sealed class RunRecord
{
    public string RunId { get; set; } = "";
    public string Owner { get; set; } = "";
    public string PlanId { get; set; } = "";
    public string PlanName { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public bool UndoFailed { get; set; }
    public List<RunStepRecord> Steps { get; set; } = new();
}

/// <summary>
/// The two files that let the elevated host write by itself: <c>approved-plans.json</c> and <c>plan-runs.json</c>. They live in a folder only Administrators and
/// SYSTEM can write (<see cref="IFolderSecurity"/>); a folder that exists is verified and never repaired here. If the folder is not trustworthy,
/// <see cref="Trusted"/> is false: nothing counts as approved and nothing is saved. Rule: anything that grants the elevated host an automatic write must not
/// live in a user-writable place.
/// </summary>
internal sealed class PlanStorage
{
    private sealed class ApprovalFile { public int Version { get; set; } = 1; public List<ApprovalRecord> Plans { get; set; } = new(); }
    private sealed class RunFile { public int Version { get; set; } = 1; public List<RunRecord> Runs { get; set; } = new(); }

    private readonly object _gate = new();
    private readonly string _root;
    private readonly IReadOnlyList<string> _chain;
    private readonly IFolderSecurity _security;
    private readonly ILog _log;
    private readonly List<ApprovalRecord> _approvals = new();
    private readonly List<RunRecord> _runs = new();
    private readonly HashSet<string> _interrupted = new(StringComparer.Ordinal);
    private bool _warned;

    /// <param name="root">The folder that holds the two files.</param>
    /// <param name="chain">Folders to check from the outside in, ending with <paramref name="root"/> (created admin-only when missing, otherwise verified).</param>
    /// <param name="security">Makes and verifies admin-only folders.</param>
    /// <param name="log">The host log.</param>
    public PlanStorage(string root, IReadOnlyList<string> chain, IFolderSecurity security, ILog log)
    {
        _root = root;
        _chain = chain;
        _security = security;
        _log = log;
        Load();
    }

    public string Root => _root;
    public bool Trusted { get; private set; }
    public string UntrustedReason { get; private set; } = "";

    private string ApprovalsPath => Path.Combine(_root, "approved-plans.json");
    private string RunsPath => Path.Combine(_root, "plan-runs.json");

    /// <summary>
    /// Verifies the folders. A missing folder is created admin-only only when <paramref name="create"/> is true (something is about to be saved), so a run that never
    /// approves a plan leaves nothing behind; a missing folder otherwise means "nothing stored", which is trustworthy. Sets <see cref="Trusted"/>.
    /// </summary>
    private bool Check(bool create = false)
    {
        string? problem = null;
        try
        {
            foreach (var folder in _chain)
            {
                if (!Directory.Exists(folder))
                {
                    if (!create) break;
                    _security.EnsureAdminOnly(folder);
                }
                problem = _security.Verify(folder);
                if (problem is not null) { problem = $"'{folder}': {problem}"; break; }
            }
        }
        catch (Exception ex) when (ex is InstallException or IOException or UnauthorizedAccessException)
        {
            problem = $"'{_root}' could not be made admin-only: {ex.Message}";
        }
        if (problem is null) { Trusted = true; UntrustedReason = ""; _warned = false; return true; }
        Trusted = false;
        UntrustedReason = problem;
        if (!_warned) { _log.Warn("approved plans are off: " + problem + ". Nothing counts as approved until the folder is admin-only again; it is not repaired automatically."); _warned = true; }
        return false;
    }

    private void Load()
    {
        lock (_gate)
        {
            if (!Check()) return;
            Read(ApprovalsPath, (ApprovalFile f) => { _approvals.Clear(); _approvals.AddRange(f.Plans); });
            Read(RunsPath, (RunFile f) =>
            {
                _runs.Clear();
                _runs.AddRange(f.Runs);
                foreach (var r in f.Runs) _interrupted.Add(r.RunId);   // open when the previous process ended
            });
        }
    }

    private void Read<T>(string path, Action<T> take) where T : class, new()
    {
        if (!File.Exists(path)) return;
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonSettingsStore.Options) ?? throw new JsonException("empty");
            take(value);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // never "everything approved": a file that cannot be read means nothing is approved
            _log.Warn($"{Path.GetFileName(path)} could not be read, treated as empty: {ex.Message}");
            try { File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch (Exception) { /* leave it */ }
        }
    }

    private bool Write<T>(string path, T value)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonSettingsStore.Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"could not write {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- approvals

    /// <summary>Re-verifies the folder (cheap) and returns whether the stored approvals can be believed.</summary>
    public bool Recheck() { lock (_gate) return Check(); }

    public ApprovalRecord? FindApproval(string owner, string planId)
    {
        lock (_gate)
        {
            if (!Check()) return null;
            return _approvals.FirstOrDefault(a => a.Owner == owner && a.PlanId == planId);
        }
    }

    public IReadOnlyList<ApprovalRecord> Approvals(string? owner = null)
    {
        lock (_gate)
        {
            if (!Check()) return Array.Empty<ApprovalRecord>();
            return _approvals.Where(a => owner is null || a.Owner == owner).OrderBy(a => a.Owner, StringComparer.Ordinal).ThenBy(a => a.PlanId, StringComparer.Ordinal).ToArray();
        }
    }

    public bool SaveApproval(ApprovalRecord record)
    {
        lock (_gate)
        {
            if (!Check(create: true)) return false;
            _approvals.RemoveAll(a => a.Owner == record.Owner && a.PlanId == record.PlanId);
            _approvals.Add(record);
            return Write(ApprovalsPath, new ApprovalFile { Plans = _approvals.ToList() });
        }
    }

    /// <summary>Marks the approval revoked (kept so the state reads "revoked"). False when there was none.</summary>
    public bool Revoke(string owner, string planId)
    {
        lock (_gate)
        {
            if (!Check()) return false;
            var rec = _approvals.FirstOrDefault(a => a.Owner == owner && a.PlanId == planId);
            if (rec is null || rec.Revoked) return false;
            rec.Revoked = true;
            return Write(ApprovalsPath, new ApprovalFile { Plans = _approvals.ToList() });
        }
    }

    /// <summary>Drops the approvals of owners that are not installed any more.</summary>
    public int DropOwnersNotIn(ISet<string> installed)
    {
        lock (_gate)
        {
            if (!Check()) return 0;
            int n = _approvals.RemoveAll(a => !installed.Contains(a.Owner));
            if (n > 0) Write(ApprovalsPath, new ApprovalFile { Plans = _approvals.ToList() });
            return n;
        }
    }

    // ---------------------------------------------------------------- runs

    public IReadOnlyList<RunRecord> Runs(string? owner = null)
    {
        lock (_gate) return _runs.Where(r => owner is null || r.Owner == owner).OrderBy(r => r.StartedAt).ToArray();
    }

    public RunRecord? FindRun(string runId) { lock (_gate) return _runs.FirstOrDefault(r => r.RunId == runId); }

    public bool IsInterrupted(string runId) { lock (_gate) return _interrupted.Contains(runId); }

    /// <summary>Writes the run to the journal. False (and nothing may run) when the folder cannot be trusted or the file cannot be written.</summary>
    public bool SaveRun(RunRecord run)
    {
        lock (_gate)
        {
            if (!Check(create: true)) return false;
            if (!_runs.Contains(run)) _runs.Add(run);
            return Write(RunsPath, new RunFile { Runs = _runs.ToList() });
        }
    }

    public void RemoveRun(string runId)
    {
        lock (_gate)
        {
            _runs.RemoveAll(r => r.RunId == runId);
            _interrupted.Remove(runId);
            if (Check()) Write(RunsPath, new RunFile { Runs = _runs.ToList() });
        }
    }
}
