using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>One line of the plan dialog: a step with what it does and how it is undone. Texts are already in the current language.</summary>
public sealed record PlanApprovalLine(string ModuleName, string CommandLine, string? EffectText, EffectKind Kind, string? PreviewText, string UndoText);

/// <summary>
/// Everything about approved plans and cross-module verbs that is not specific to one calling module: the approval store, the journal, the checks, the
/// rate limit and the run. One per host (<see cref="RigbayHost.Plans"/>). See <c>docs/plan/H1-DESIGN.md</c> and ABSTRACTIONS.md 9.1.
/// </summary>
public sealed class PlanEngine
{
    internal const int MaxSteps = 8, MaxArgs = 16, MaxArgLength = 256, MaxOpenRuns = 4;
    private static readonly Regex IdPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex VerbPattern = new("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);
    private static readonly TimeSpan DeclinedMemory = TimeSpan.FromMinutes(1), RateWindow = TimeSpan.FromHours(1);

    private readonly RigbayHost _host;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly PlanStorage _storage;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Hash, DateTimeOffset Until)> _declined = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _starts = new();
    private readonly List<DateTimeOffset> _allStarts = new();
    private readonly HashSet<string> _notified = new();

    internal PlanEngine(RigbayHost host, PlanStorage storage, TimeProvider time)
    {
        _host = host;
        _log = host.Log.ForSource("plans");
        _storage = storage;
        _time = time;
    }

    /// <summary>Raised after an approval was given or revoked, or a run was opened or closed.</summary>
    public event Action? Changed;

    /// <summary>True when the approval store can be trusted (its folder is admin-only). False means nothing counts as approved.</summary>
    public bool Trusted => _storage.Recheck();

    /// <summary>Why the store cannot be trusted; empty when it can.</summary>
    public string UntrustedReason => _storage.UntrustedReason;

    /// <summary>The folder of the two files.</summary>
    public string StorageRoot => _storage.Root;

    private DateTimeOffset Now => _time.GetUtcNow();

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { _log.Warn("a Plans.Changed handler threw: " + ex.Message); }
    }

    // ---------------------------------------------------------------- resolving a plan against the live modules

    private sealed record ResolvedStep(PlanStep Step, ModuleEntry Target, CommandEntry Command, string PreviewKey, string? PreviewText, ModuleEntry? UndoTarget, CommandEntry? UndoCommand);

    private sealed record Resolved(ModuleEntry Owner, VerbPlan Plan, List<ResolvedStep> Steps, string Hash, Dictionary<string, string> Versions);

    private static string CommandLine(string module, string verb, IEnumerable<string> args) =>
        string.Join(' ', new[] { module, verb }.Concat(args.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a)));

    private static string CommandLine(PlanStep s) => CommandLine(s.ModuleId, s.Verb, s.Args);

    private static StepRef ToRef(PlanStep s) => new() { Module = s.ModuleId, Verb = s.Verb, Args = s.Args.ToList() };

    private string? ValidateShape(ModuleEntry owner, VerbPlan plan)
    {
        if (plan is null) return "no plan";
        if (!IdPattern.IsMatch(plan.Id ?? "") || plan.Id!.Length > 48) return "the plan id must be lowercase letters, digits and dashes (at most 48 characters)";
        if (plan.Steps is null || plan.Steps.Count == 0) return "the plan has no steps";
        if (plan.Steps.Count > MaxSteps) return $"a plan may have at most {MaxSteps} steps";
        foreach (var step in plan.Steps)
        {
            var problem = ValidateRef(owner, step.ModuleId, step.Verb, step.Args);
            if (problem is not null) return problem;
            if (step.FixedUndo is { } undo)
            {
                problem = ValidateRef(owner, undo.ModuleId, undo.Verb, undo.Args);
                if (problem is not null) return "undo step: " + problem;
                if (step.RestoreRecorded) return $"{step.ModuleId} {step.Verb}: choose a fixed undo or the recorded one, not both";
            }
        }
        return null;
    }

    private static string? ValidateRef(ModuleEntry owner, string module, string verb, IReadOnlyList<string>? args)
    {
        if (string.IsNullOrEmpty(module) || string.IsNullOrEmpty(verb)) return "a step needs a module and a verb";
        if (!VerbPattern.IsMatch(verb)) return $"'{verb}' is not a valid verb";
        if (string.Equals(module, owner.Id, StringComparison.Ordinal)) return "a step may not target the module that owns the plan";
        if (args is null) return $"{module} {verb}: no arguments list";
        if (args.Count > MaxArgs) return $"{module} {verb}: at most {MaxArgs} arguments";
        if (args.Any(a => a is null || a.Length > MaxArgLength)) return $"{module} {verb}: an argument is longer than {MaxArgLength} characters";
        return null;
    }

    /// <summary>Looks at every module and verb of the plan, asks the verbs for their previews and computes the hash. Null plus an English reason when it cannot.</summary>
    private async Task<(Resolved? Plan, string Error)> ResolveAsync(ModuleEntry owner, VerbPlan plan, CancellationToken ct)
    {
        var shape = ValidateShape(owner, plan);
        if (shape is not null) return (null, shape);

        var steps = new List<ResolvedStep>();
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var hash = new StringBuilder();
        static void F(StringBuilder b, string? s) { s ??= ""; b.Append(s.Length).Append(':').Append(s).Append(';'); }
        F(hash, owner.Id);
        F(hash, plan.Id);
        F(hash, plan.Steps.Count.ToString());

        foreach (var step in plan.Steps)
        {
            var (target, cmd, error) = Find(step.ModuleId, step.Verb);
            if (error is not null) return (null, error);
            versions[target!.Id] = target.Manifest?.Version ?? "";
            F(hash, target.Id); F(hash, versions[target.Id]); F(hash, step.Verb);
            F(hash, step.Args.Count.ToString());
            foreach (var a in step.Args) F(hash, a);
            F(hash, cmd!.Access.ToString());
            F(hash, cmd.Info?.Effect is { } e ? $"{e.Kind}/{e.DescriptionKey}" : "-");

            ModuleEntry? undoTarget = null; CommandEntry? undoCmd = null;
            if (step.FixedUndo is { } undo)
            {
                var (ut, uc, uerr) = Find(undo.ModuleId, undo.Verb);
                if (uerr is not null) return (null, "undo step: " + uerr);
                undoTarget = ut; undoCmd = uc;
                versions[ut!.Id] = ut.Manifest?.Version ?? "";
                F(hash, "fixed"); F(hash, ut.Id); F(hash, versions[ut.Id]); F(hash, undo.Verb);
                F(hash, undo.Args.Count.ToString());
                foreach (var a in undo.Args) F(hash, a);
            }
            else if (step.RestoreRecorded)
            {
                var undoVerb = cmd.Info?.UndoVerb;
                if (string.IsNullOrEmpty(undoVerb)) return (null, $"{target.Id} {step.Verb} does not say how it is undone (no UndoVerb), so it cannot use the recorded undo");
                var (ut, uc, uerr) = Find(target.Id, undoVerb);
                if (uerr is not null) return (null, "undo verb: " + uerr);
                undoTarget = ut; undoCmd = uc;
                F(hash, "recorded"); F(hash, undoVerb);
            }
            else F(hash, "none");

            var (previewKey, previewText) = await PreviewAsync(target, cmd, step.Args, ct).ConfigureAwait(false);
            F(hash, previewKey);
            steps.Add(new ResolvedStep(step, target, cmd, previewKey, previewText, undoTarget, undoCmd));
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString()))).ToLowerInvariant();
        return (new Resolved(owner, plan, steps, digest, versions), "");
    }

    private (ModuleEntry? Target, CommandEntry? Command, string? Error) Find(string moduleId, string verb)
    {
        var target = _host.Find(moduleId);
        if (target is null) return (null, null, $"module '{moduleId}' is not installed");
        if (target.Lifecycle != ModuleLifecycle.Running) return (null, null, $"module '{moduleId}' is not running");
        var cmd = _host.Commands.Find(moduleId, verb);
        if (cmd is null) return (null, null, $"module '{moduleId}' has no verb '{verb}'");
        return (target, cmd, null);
    }

    /// <summary>The preview of a step in the target's fault boundary, with a time limit. The key part (key and arguments, not the resolved text) is what the hash uses.</summary>
    private async Task<(string Key, string? Text)> PreviewAsync(ModuleEntry target, CommandEntry cmd, IReadOnlyList<string> args, CancellationToken ct)
    {
        var preview = cmd.Info?.Preview;
        if (preview is null) return ("-", null);
        LocalisedText? text = null;
        var work = Task.Run(() => target.Invoke("Preview " + cmd.Verb, () => text = preview(args)), CancellationToken.None);
        var finished = await Task.WhenAny(work, Task.Delay(_host.Options.PreviewTimeout, _time, ct)).ConfigureAwait(false);
        if (finished != work || !work.IsCompletedSuccessfully || text is not { IsEmpty: false } t) return ("-", null);
        var key = t.Key + "|" + string.Join("|", (t.Args ?? Array.Empty<object>()).Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)));
        return (key, target.Localisation.Resolve(t));
    }

    // ---------------------------------------------------------------- approval

    internal async Task<PlanApproval> GetApprovalAsync(ModuleEntry owner, VerbPlan plan, CancellationToken ct = default)
    {
        if (!_storage.Recheck()) return new PlanApproval(PlanApprovalState.Untrusted, _storage.UntrustedReason);
        var (resolved, error) = await ResolveAsync(owner, plan, ct).ConfigureAwait(false);
        if (resolved is null) return new PlanApproval(PlanApprovalState.Unavailable, error);
        return Judge(resolved);
    }

    private PlanApproval Judge(Resolved r)
    {
        var rec = _storage.FindApproval(r.Owner.Id, r.Plan.Id);
        if (rec is null) return IsDeclined(r) ? new PlanApproval(PlanApprovalState.Declined) : new PlanApproval(PlanApprovalState.NotApproved);
        if (rec.Revoked) return new PlanApproval(PlanApprovalState.Revoked);
        if (string.Equals(rec.Hash, r.Hash, StringComparison.Ordinal)) return new PlanApproval(PlanApprovalState.Approved);
        foreach (var (module, version) in r.Versions.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (rec.Versions.TryGetValue(module, out var was) && !string.Equals(was, version, StringComparison.Ordinal))
                return new PlanApproval(PlanApprovalState.Changed, _host.Languages.F("core.plan.changed-version", _host.Find(module)?.DisplayName ?? module, was, version));
        }
        return new PlanApproval(PlanApprovalState.Changed, _host.Languages.T("core.plan.changed-steps"));
    }

    private bool IsDeclined(Resolved r)
    {
        lock (_gate)
            return _declined.TryGetValue(r.Owner.Id + "/" + r.Plan.Id, out var d) && d.Hash == r.Hash && d.Until > Now;
    }

    internal async Task<PlanApproval> RequestApprovalAsync(ModuleEntry owner, VerbPlan plan, CancellationToken ct)
    {
        if (!_storage.Recheck()) return new PlanApproval(PlanApprovalState.Untrusted, _storage.UntrustedReason);
        var (resolved, error) = await ResolveAsync(owner, plan, ct).ConfigureAwait(false);
        if (resolved is null) return new PlanApproval(PlanApprovalState.Unavailable, error);
        if (IsDeclined(resolved)) return new PlanApproval(PlanApprovalState.Declined);

        var prompt = _host.ApprovalPrompt;
        if (prompt is null)
        {
            owner.Log.Warn("plan approval refused: the host has no way to ask for confirmation");
            return new PlanApproval(PlanApprovalState.Declined);
        }

        var lines = resolved.Steps.Select(s => LineOf(s)).ToList();
        var request = new ApprovalRequest(owner.Id, owner.DisplayName, _host.Languages.F("core.plan.dialog-effect", owner.Localisation.Resolve(plan.Name)), null, Revertible: false)
        {
            Plan = lines,
            PlanName = owner.Localisation.Resolve(plan.Name),
        };
        bool yes;
        try { yes = await prompt.ConfirmAsync(request, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new PlanApproval(PlanApprovalState.Declined); }
        catch (Exception ex) { owner.Log.Warn("plan approval prompt failed, treated as no: " + ex.Message); return new PlanApproval(PlanApprovalState.Declined); }

        if (!yes)
        {
            lock (_gate) _declined[owner.Id + "/" + plan.Id] = (resolved.Hash, Now + DeclinedMemory);
            _log.Info($"{owner.Id}/{plan.Id}: the owner declined the plan");
            return new PlanApproval(PlanApprovalState.Declined);
        }

        var record = new ApprovalRecord
        {
            Owner = owner.Id, PlanId = plan.Id, Hash = resolved.Hash, ApprovedAt = Now, Versions = resolved.Versions,
            Lines = resolved.Steps.Select(s => CommandLine(s.Step) + (s.Step.FixedUndo is { } u ? " / undo: " + CommandLine(u) : s.Step.RestoreRecorded ? " / undo: recorded" : "")).ToList(),
        };
        if (!_storage.SaveApproval(record)) return new PlanApproval(PlanApprovalState.Untrusted, _storage.UntrustedReason.Length > 0 ? _storage.UntrustedReason : "the approval could not be saved");
        lock (_gate) { _declined.Remove(owner.Id + "/" + plan.Id); _notified.RemoveWhere(k => k.StartsWith(owner.Id + "/" + plan.Id + "|", StringComparison.Ordinal)); }
        _log.Info($"{owner.Id}/{plan.Id}: approved by the owner, hash {record.Hash[..12]}, {record.Lines.Count} step(s): {string.Join("; ", record.Lines)}");
        RaiseChanged();
        return new PlanApproval(PlanApprovalState.Approved);
    }

    private PlanApprovalLine LineOf(ResolvedStep s)
    {
        string undo = s.Step.FixedUndo is { } u ? _host.Languages.F("core.plan.undo-fixed", CommandLine(u))
            : s.Step.RestoreRecorded ? _host.Languages.F("core.plan.undo-recorded", s.Target.DisplayName)
            : _host.Languages.T("core.plan.undo-none");
        string? effect = s.Command.Info?.Effect is { } e ? s.Target.Localisation.T(e.DescriptionKey)
            : s.Command.Access == VerbAccess.Unknown ? _host.Languages.T("core.plan.unknown-effect") : null;
        return new PlanApprovalLine(s.Target.DisplayName, CommandLine(s.Step), effect, s.Command.Info?.Effect?.Kind ?? EffectKind.Hardware,
            s.PreviewText ?? (s.Command.Access == VerbAccess.Writes ? _host.Languages.T("core.plan.no-preview") : null), undo);
    }

    internal void Revoke(string owner, string planId)
    {
        if (_storage.Revoke(owner, planId))
        {
            _log.Info($"{owner}/{planId}: approval revoked");
            RaiseChanged();
        }
    }

    internal IReadOnlyList<ApprovedPlanInfo> ApprovedPlans(string owner) =>
        _storage.Approvals(owner).Where(a => !a.Revoked).Select(a => new ApprovedPlanInfo(a.PlanId, a.Hash, a.ApprovedAt, a.Lines)).ToArray();

    /// <summary>Every approval, revoked ones included (host UI, <c>rigbay plans list</c>).</summary>
    public IReadOnlyList<ApprovalRecord> AllApprovals() => _storage.Approvals();

    /// <summary>Every open run (host UI, <c>rigbay plans list</c>).</summary>
    public IReadOnlyList<RunRecord> OpenRuns() => _storage.Runs();

    /// <summary>Revokes an approval by owner and plan id (host UI, <c>rigbay plans revoke</c>). Returns true when there was one.</summary>
    public bool RevokeAny(string owner, string planId)
    {
        bool had = _storage.FindApproval(owner, planId) is { Revoked: false };
        Revoke(owner, planId);
        return had;
    }

    /// <summary>Drops the approvals of modules that are not installed (called after discovery).</summary>
    internal void DropMissingOwners(IEnumerable<string> installed)
    {
        int n = _storage.DropOwnersNotIn(new HashSet<string>(installed, StringComparer.Ordinal));
        if (n > 0) { _log.Info($"{n} approval(s) of modules that are not installed any more were removed"); RaiseChanged(); }
    }

    // ---------------------------------------------------------------- running

    internal IReadOnlyList<ActiveRun> ActiveRuns(string owner) =>
        _storage.Runs(owner).Select(r => new ActiveRun(r.RunId, r.PlanId, r.StartedAt, _storage.IsInterrupted(r.RunId), r.UndoFailed,
            r.Steps.Where(s => s.Done).Select(s => CommandLine(s.Step.Module, s.Step.Verb, s.Step.Args)).ToArray())).ToArray();

    private void Notify(ModuleEntry? owner, string title, string body, ToastKind kind)
    {
        _log.Info($"notice [{kind}] {title} / {body}");
        var sink = _host.ToastSink;
        if (sink is null) { _log.Info("notification not shown: the host has no toast sink"); return; }
        try { sink.Show(owner?.Id ?? "host", title, body, kind); }
        catch (Exception ex) { _log.Warn("toast sink failed: " + ex.Message); }
    }

    private bool TakeRateSlot(string key, out string why)
    {
        why = "";
        var now = Now;
        lock (_gate)
        {
            _allStarts.RemoveAll(t => now - t >= RateWindow);
            if (!_starts.TryGetValue(key, out var list)) _starts[key] = list = new List<DateTimeOffset>();
            list.RemoveAll(t => now - t >= RateWindow);
            if (list.Count >= _host.Options.PlanStartsPerHour) { why = $"{list.Count} starts of this plan in the last hour (the limit is {_host.Options.PlanStartsPerHour})"; return false; }
            if (_allStarts.Count >= _host.Options.PlanStartsPerHourTotal) { why = $"{_allStarts.Count} plan starts in the last hour (the limit is {_host.Options.PlanStartsPerHourTotal})"; return false; }
            list.Add(now);
            _allStarts.Add(now);
            return true;
        }
    }

    private bool NotifyOnce(string key)
    {
        lock (_gate) return _notified.Add(key);
    }

    internal async Task<PlanRun> StartAsync(ModuleEntry owner, VerbPlan plan, CancellationToken ct)
    {
        if (owner.Lifecycle != ModuleLifecycle.Running) return new PlanRun("", PlanOutcome.Cancelled, [], "the module is not running");
        if (CallScope.InsideOpenCall) return new PlanRun("", PlanOutcome.Invalid, [], "a plan cannot be started from inside a verb that the host started for another module");

        using var runCts = new CancellationTokenSource(_host.Options.PlanRunTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runCts.Token, owner.StopToken);
        try { await _runLock.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new PlanRun("", PlanOutcome.Cancelled, [], "cancelled before the run started"); }
        try { return await StartLockedAsync(owner, plan, linked.Token, runCts.Token).ConfigureAwait(false); }
        finally { _runLock.Release(); }
    }

    private async Task<PlanRun> StartLockedAsync(ModuleEntry owner, VerbPlan plan, CancellationToken ct, CancellationToken timeoutToken)
    {
        if (!_storage.Recheck()) return new PlanRun("", PlanOutcome.NeedsApproval, [], "approved plans are off: " + _storage.UntrustedReason);
        var (resolved, error) = await ResolveAsync(owner, plan, ct).ConfigureAwait(false);
        if (resolved is null) return new PlanRun("", PlanOutcome.Invalid, [], error);

        var approval = Judge(resolved);
        string title = owner.Localisation.Resolve(plan.Name);
        if (!approval.IsApproved)
        {
            _log.Warn($"{owner.Id}/{plan.Id}: refused, {approval.State}{(approval.Reason.Length > 0 ? " (" + approval.Reason + ")" : "")}");
            if (approval.State == PlanApprovalState.Changed && NotifyOnce($"{owner.Id}/{plan.Id}|changed"))
                Notify(owner, title, _host.Languages.F("core.plan.changed-notify", title, approval.Reason), ToastKind.Warning);
            return new PlanRun("", PlanOutcome.NeedsApproval, [], $"not approved ({approval.State}) {approval.Reason}".Trim());
        }

        var open = _storage.Runs();
        if (open.Any(r => r.Owner == owner.Id && r.PlanId == plan.Id)) return new PlanRun("", PlanOutcome.AlreadyRunning, [], "this plan already has an open run");
        if (open.Count >= MaxOpenRuns) return new PlanRun("", PlanOutcome.TooManyRuns, [], $"{open.Count} runs are open already");
        if (!TakeRateSlot(owner.Id + "/" + plan.Id, out var why))
        {
            _log.Warn($"{owner.Id}/{plan.Id}: paused, {why}");
            if (NotifyOnce($"{owner.Id}/{plan.Id}|rate")) Notify(owner, title, _host.Languages.F("core.plan.rate-limited", title), ToastKind.Warning);
            return new PlanRun("", PlanOutcome.RateLimited, [], why);
        }
        lock (_gate) _notified.Remove($"{owner.Id}/{plan.Id}|rate");

        var record = new RunRecord
        {
            RunId = Guid.NewGuid().ToString("N")[..12], Owner = owner.Id, PlanId = plan.Id, PlanName = title, Hash = resolved.Hash, StartedAt = Now,
            Steps = resolved.Steps.Select(s => new RunStepRecord { Step = ToRef(s.Step), FixedUndo = s.Step.FixedUndo is { } u ? ToRef(u) : null, RestoreRecorded = s.Step.RestoreRecorded }).ToList(),
        };
        if (!_storage.SaveRun(record)) return new PlanRun("", PlanOutcome.NeedsApproval, [], "the journal could not be written, so nothing was run");
        RaiseChanged();
        _log.Info($"{owner.Id}/{plan.Id}: run {record.RunId} started, {record.Steps.Count} step(s)");

        var results = new List<PlanStepResult>();
        var notes = new List<string>();
        for (int i = 0; i < resolved.Steps.Count; i++)
        {
            var rs = resolved.Steps[i];
            var run = await RunStepAsync(owner, record.RunId, ToRef(rs.Step), isUndo: false, rs.Target, rs.Command, ct, timeoutToken).ConfigureAwait(false);
            results.Add(new PlanStepResult(rs.Step, false, run.Outcome, run.ExitCode, run.Output));
            notes.AddRange(run.Notes);
            if (run.Outcome == VerbOutcome.Done)
            {
                record.Steps[i].Done = true;
                if (rs.Step.RestoreRecorded && rs.Command.Info?.UndoVerb is { } undoVerb && run.UndoArgs is { } undoArgs)
                {
                    if (undoArgs.Count <= MaxArgs && undoArgs.All(a => a is { Length: <= MaxArgLength }))
                        record.Steps[i].RecordedUndo = new StepRef { Module = rs.Target.Id, Verb = undoVerb, Args = undoArgs.ToList() };
                    else _log.Warn($"{rs.Target.Id} {rs.Step.Verb}: the undo it reported breaks the limits and was ignored");
                }
                _storage.SaveRun(record);
                continue;
            }

            // the first failure stops the run and puts back what was done
            _storage.SaveRun(record);
            var back = await UndoStepsAsync(owner, record, results, CancellationToken.None).ConfigureAwait(false);
            if (back.AllUndone) { _storage.RemoveRun(record.RunId); }
            else { record.UndoFailed = true; _storage.SaveRun(record); }
            RaiseChanged();
            string failed = $"{CommandLine(rs.Step)}: {run.Outcome}{(run.Output.Length > 0 ? " (" + FirstLine(run.Output) + ")" : "")}";
            Notify(owner, title, _host.Languages.F("core.plan.failed", title, i + 1, failed, back.Count), ToastKind.Warning);
            return new PlanRun(record.RunId, back.AllUndone ? PlanOutcome.Failed : PlanOutcome.UndoFailed, results, failed);
        }

        string summary = string.Join(", ", resolved.Steps.Select(s => CommandLine(s.Step)));
        _log.Info($"{owner.Id}/{plan.Id}: run {record.RunId} done: {summary}" + (notes.Count > 0 ? " | the modules asked: " + string.Join(" ; ", notes) : ""));
        Notify(owner, title, _host.Languages.F("core.plan.ran", resolved.Steps.Count, summary), ToastKind.Info);
        return new PlanRun(record.RunId, PlanOutcome.Done, results);
    }

    private static string FirstLine(string text) => text.ReplaceLineEndings("\n").Split('\n')[0];

    private sealed record StepRun(VerbOutcome Outcome, int ExitCode, string Output, IReadOnlyList<string>? UndoArgs, IReadOnlyList<string> Notes);

    private async Task<StepRun> RunStepAsync(ModuleEntry? owner, string runId, StepRef step, bool isUndo, ModuleEntry? target, CommandEntry? command, CancellationToken ct, CancellationToken timeoutToken)
    {
        if (target is null || command is null)
        {
            var (t, c, error) = Find(step.Module, step.Verb);
            if (error is not null) return new StepRun(error.Contains("not running") ? VerbOutcome.NotRunning : VerbOutcome.NotFound, -1, error, null, []);
            target = t; command = c;
        }
        var scope = new CallScope(target!.Id, isPlanStep: true, command!.Info?.Effect?.Kind);
        var result = await VerbInvoker.RunAsync(_host, owner, target, command, step.Args, scope, _host.Options.VerbTimeout, ct).ConfigureAwait(false);
        _log.Info($"run {runId}: {(isUndo ? "undo " : "")}{CommandLine(step.Module, step.Verb, step.Args)} -> {result.Outcome}{(result.Output.Length > 0 ? " (" + FirstLine(result.Output) + ")" : "")}");
        return new StepRun(result.Outcome, result.ExitCode, result.Output, result.UndoArgs, scope.Notes);
    }

    private sealed record UndoSummary(bool AllUndone, int Count);

    /// <summary>Puts back the steps of a run that were done, last first. Every undo is tried; the record keeps what could not be put back.</summary>
    private async Task<UndoSummary> UndoStepsAsync(ModuleEntry? owner, RunRecord record, List<PlanStepResult> results, CancellationToken ct)
    {
        int count = 0;
        bool all = true;
        for (int i = record.Steps.Count - 1; i >= 0; i--)
        {
            var s = record.Steps[i];
            if (!s.Done) continue;
            var undo = s.FixedUndo ?? s.RecordedUndo;
            if (undo is null) continue;   // nothing to put back (the owner saw "not undone")
            var run = await RunStepAsync(owner, record.RunId, undo, isUndo: true, null, null, ct, CancellationToken.None).ConfigureAwait(false);
            results.Add(new PlanStepResult(new PlanStep(undo.Module, undo.Verb, undo.Args), true, run.Outcome, run.ExitCode, run.Output));
            if (run.Outcome == VerbOutcome.Done) { count++; s.Done = false; }
            else all = false;
        }
        return new UndoSummary(all, count);
    }

    internal Task<PlanRun> EndAsync(ModuleEntry owner, string runId, CancellationToken ct)
    {
        var record = _storage.FindRun(runId);
        if (record is null || record.Owner != owner.Id) return Task.FromResult(new PlanRun(runId, PlanOutcome.NotFound, [], "this module has no such open run"));
        return EndCoreAsync(owner, record, ct);
    }

    /// <summary>
    /// Puts back an open run without its owner module (<c>rigbay plans undo</c>). Allowed even if the plan was revoked since: it only undoes what approved steps did.
    /// </summary>
    public Task<PlanRun> UndoRunAsync(string runId, CancellationToken ct = default)
    {
        var record = _storage.FindRun(runId);
        if (record is null) return Task.FromResult(new PlanRun(runId, PlanOutcome.NotFound, [], "there is no open run " + runId));
        return EndCoreAsync(_host.Find(record.Owner), record, ct);
    }

    /// <summary>Drops a run from the journal without undoing anything (<c>rigbay plans forget</c>).</summary>
    public bool ForgetRun(string runId)
    {
        if (_storage.FindRun(runId) is null) return false;
        _storage.RemoveRun(runId);
        _log.Warn($"run {runId} was dropped from the journal without being undone");
        RaiseChanged();
        return true;
    }

    private async Task<PlanRun> EndCoreAsync(ModuleEntry? owner, RunRecord record, CancellationToken ct)
    {
        if (CallScope.InsideOpenCall) return new PlanRun(record.RunId, PlanOutcome.Invalid, [], "an undo cannot be started from inside a verb that the host started for another module");
        using var runCts = new CancellationTokenSource(_host.Options.PlanRunTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runCts.Token);
        try { await _runLock.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new PlanRun(record.RunId, PlanOutcome.Cancelled, [], "cancelled before the undo started"); }
        try
        {
            if (_storage.FindRun(record.RunId) is null) return new PlanRun(record.RunId, PlanOutcome.NotFound, [], "the run is closed already");
            var results = new List<PlanStepResult>();
            var back = await UndoStepsAsync(owner, record, results, linked.Token).ConfigureAwait(false);
            string title = record.PlanName.Length > 0 ? record.PlanName : record.PlanId;
            if (back.AllUndone)
            {
                _storage.RemoveRun(record.RunId);
                RaiseChanged();
                Notify(owner, title, _host.Languages.F("core.plan.undone", back.Count), ToastKind.Info);
                _log.Info($"run {record.RunId} ended, {back.Count} step(s) put back");
                return new PlanRun(record.RunId, PlanOutcome.Done, results);
            }
            record.UndoFailed = true;
            _storage.SaveRun(record);
            RaiseChanged();
            var bad = string.Join("; ", results.Where(r => r.IsUndo && r.Outcome != VerbOutcome.Done).Select(r => CommandLine(r.Step) + ": " + r.Outcome));
            Notify(owner, title, _host.Languages.F("core.plan.undo-failed", title, bad), ToastKind.Warning);
            return new PlanRun(record.RunId, PlanOutcome.UndoFailed, results, bad);
        }
        finally { _runLock.Release(); }
    }

    /// <summary>One Warning for runs that were open when Rigbay ended. Called once after the modules started. The host never undoes by itself.</summary>
    internal void AnnounceInterrupted()
    {
        var runs = _storage.Runs().Where(r => _storage.IsInterrupted(r.RunId)).ToList();
        if (runs.Count == 0) return;
        var names = string.Join(", ", runs.Select(r => r.PlanName.Length > 0 ? r.PlanName : r.PlanId));
        _log.Warn($"{runs.Count} plan run(s) were open when Rigbay ended: {string.Join(", ", runs.Select(r => r.RunId + " " + r.Owner + "/" + r.PlanId))}. Nothing is undone automatically (rigbay plans undo <run>).");
        Notify(null, _host.Languages.T("core.plan.interrupted-title"), _host.Languages.F("core.plan.interrupted", names), ToastKind.Warning);
    }

    // ---------------------------------------------------------------- the host verbs: rigbay plans ...

    internal async Task<CommandOutcome> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var languages = _host.Languages;
        string usage = languages.T("core.plan.usage");
        if (args.Count == 0) return new CommandOutcome(CommandOutcome.UsageOrNotFound, usage);
        switch (args[0])
        {
            case "list":
            {
                var sb = new StringBuilder();
                if (!_storage.Recheck()) sb.AppendLine(languages.F("core.plan.store-off", _storage.UntrustedReason));
                foreach (var a in _storage.Approvals())
                    sb.AppendLine($"{a.Owner}/{a.PlanId}  {(a.Revoked ? "revoked" : "approved")}  {a.ApprovedAt:yyyy-MM-dd HH:mm}  {string.Join(" ; ", a.Lines)}");
                foreach (var r in _storage.Runs())
                    sb.AppendLine($"run {r.RunId}  {r.Owner}/{r.PlanId}  {(r.UndoFailed ? "undo failed" : _storage.IsInterrupted(r.RunId) ? "interrupted" : "open")}  {r.StartedAt:yyyy-MM-dd HH:mm}");
                var text = sb.ToString().TrimEnd();
                return new CommandOutcome(CommandOutcome.Ok, text.Length == 0 ? languages.T("core.plan.none") : text);
            }
            case "revoke" when args.Count == 2:
            {
                var parts = args[1].Split('/', 2);
                if (parts.Length != 2) return new CommandOutcome(CommandOutcome.UsageOrNotFound, usage);
                return RevokeAny(parts[0], parts[1])
                    ? new CommandOutcome(CommandOutcome.Ok, languages.F("core.plan.revoked", args[1]))
                    : new CommandOutcome(CommandOutcome.UsageOrNotFound, languages.F("core.plan.not-found", args[1]));
            }
            case "undo" when args.Count == 2:
            {
                var run = await UndoRunAsync(args[1], ct).ConfigureAwait(false);
                return run.Outcome == PlanOutcome.Done ? new CommandOutcome(CommandOutcome.Ok, languages.F("core.plan.undone", run.Steps.Count(s => s.IsUndo)))
                    : run.Outcome == PlanOutcome.NotFound ? new CommandOutcome(CommandOutcome.UsageOrNotFound, languages.F("core.plan.not-found", args[1]))
                    : new CommandOutcome(CommandOutcome.Failed, run.Message);
            }
            case "forget" when args.Count == 2:
                return ForgetRun(args[1])
                    ? new CommandOutcome(CommandOutcome.Ok, languages.F("core.plan.forgotten", args[1]))
                    : new CommandOutcome(CommandOutcome.UsageOrNotFound, languages.F("core.plan.not-found", args[1]));
            default:
                return new CommandOutcome(CommandOutcome.UsageOrNotFound, usage);
        }
    }
}
