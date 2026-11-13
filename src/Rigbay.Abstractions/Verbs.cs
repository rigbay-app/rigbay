namespace Rigbay.Abstractions;

// Host API 0.1.4 (H1): cross-module verbs and approved plans. Everything here is additive. See docs/ABSTRACTIONS.md, section 9.1.

/// <summary>What a verb does to the machine, as its own module declares it.</summary>
public enum VerbAccess
{
    /// <summary>The module did not say (it registered the verb the old way). The host treats it as a write.</summary>
    Unknown = 0,
    /// <summary>The verb only reads. Another module may call it with <see cref="IVerbService.InvokeAsync"/>.</summary>
    ReadOnly = 1,
    /// <summary>The verb changes the machine or a setting. It runs for another module only inside a plan the owner approved.</summary>
    Writes = 2,
}

/// <summary>
/// A verb with what the host and the owner need to know about it. Register it with
/// <see cref="ICommandRegistry.Register(CommandInfo, CommandHandler)"/>. Without it a verb is <see cref="VerbAccess.Unknown"/>.
/// </summary>
/// <param name="Verb">Lowercase, digits and dashes, starting with a letter.</param>
/// <param name="DescriptionKey">Module-relative language key of the one-line help.</param>
public sealed record CommandInfo(string Verb, string DescriptionKey)
{
    /// <summary>What the verb does to the machine. Default <see cref="VerbAccess.Unknown"/>.</summary>
    public VerbAccess Access { get; init; } = VerbAccess.Unknown;

    /// <summary>Module-relative key (or <see cref="Text.Literal"/>) of the usage line, for example <c>apply &lt;profile&gt;</c>.</summary>
    public string? UsageKey { get; init; }

    /// <summary>
    /// What the owner is told a run of this verb does. Required when <see cref="Access"/> is <see cref="VerbAccess.Writes"/>.
    /// Its <see cref="HardwareEffect.Kind"/> is also the only kind of question the host answers for the verb inside an approved plan.
    /// </summary>
    public HardwareEffect? Effect { get; init; }

    /// <summary>
    /// The exact change these arguments would make, for the one approval dialog ("Gaming: PL1 45 W, PL2 65 W, 28 s"), or null for none.
    /// <b>Rule:</b> a pure function of the arguments and the module's own configuration, never of live state (the value in force now
    /// differs between runs and would make the host refuse a valid plan). Read-only and quick (the host waits 2 s); it runs in the
    /// module's fault boundary. The host stores the key and arguments in the plan's hash, so a changed profile voids the approval.
    /// </summary>
    public Func<IReadOnlyList<string>, LocalisedText?>? Preview { get; init; }

    /// <summary>
    /// A verb of the same module that puts back what this one changed, given the arguments this verb reports in
    /// <see cref="CommandResult.UndoArgs"/> (for <c>apply</c>: <c>apply</c> with the profile that was active before).
    /// </summary>
    public string? UndoVerb { get; init; }
}

/// <summary>Result of running a verb of another module.</summary>
public enum VerbOutcome
{
    /// <summary>The verb ran and answered exit code 0.</summary>
    Done,
    /// <summary>The verb ran and answered exit code 1.</summary>
    Failed,
    /// <summary>The verb ran and answered exit code 2 (wrong arguments).</summary>
    Usage,
    /// <summary>No such module or verb (or the module is the caller itself).</summary>
    NotFound,
    /// <summary>The module is not running.</summary>
    NotRunning,
    /// <summary>The verb is not declared read-only: it runs only inside an approved plan. Nothing ran.</summary>
    NeedsPlan,
    /// <summary>The verb did not answer in time. It is not stopped by force; the module is not faulted.</summary>
    TimedOut,
    /// <summary>The caller (or the callee) stopped, or the caller cancelled.</summary>
    Cancelled,
    /// <summary>The verb threw or hung and the host disabled its module.</summary>
    CalleeFaulted,
    /// <summary>This host has no cross-module verbs.</summary>
    Unavailable,
    /// <summary>Not allowed here: the arguments break a limit, the call comes from inside another call, or the caller targets itself.</summary>
    Refused,
}

/// <summary>What a cross-module call returned.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ExitCode">The verb's exit code (0, 1, 2) when it ran; -1 otherwise.</param>
/// <param name="Output">The verb's text, or an English explanation when it did not run.</param>
public sealed record VerbResult(VerbOutcome Outcome, int ExitCode, string Output)
{
    /// <summary>True when the verb ran and answered 0.</summary>
    public bool Succeeded => Outcome == VerbOutcome.Done;
}

/// <summary>A verb of another module, as <see cref="IVerbService.List"/> reports it. Texts are in the current language, resolved through the owner module.</summary>
/// <param name="ModuleId">The module that owns the verb.</param>
/// <param name="ModuleName">Its display name.</param>
/// <param name="Verb">The verb.</param>
/// <param name="Description">The one-line help.</param>
/// <param name="Usage">The usage line, or null.</param>
/// <param name="Access">What the module says it does.</param>
/// <param name="EffectText">What it changes (set for <see cref="VerbAccess.Writes"/>), or null.</param>
/// <param name="EffectKind">The kind of change when there is an effect; otherwise <see cref="EffectKind.Hardware"/>.</param>
/// <param name="UndoVerb">The verb of the same module that puts it back, or null.</param>
/// <param name="HasPreview">True when the verb can describe the exact change its arguments make.</param>
public sealed record VerbInfo(string ModuleId, string ModuleName, string Verb, string Description, string? Usage, VerbAccess Access,
    string? EffectText, EffectKind EffectKind, string? UndoVerb, bool HasPreview);

/// <summary>One step of a plan: a verb of another module with its exact arguments, and how it is undone.</summary>
/// <param name="ModuleId">The module that owns the verb. It must not be the plan's owner.</param>
/// <param name="Verb">The verb.</param>
/// <param name="Args">The exact arguments.</param>
public sealed record PlanStep(string ModuleId, string Verb, IReadOnlyList<string> Args)
{
    /// <summary>A step run in the undo phase exactly as written (<c>steam-bypass off</c>). Its own undo fields are ignored.</summary>
    public PlanStep? FixedUndo { get; init; }

    /// <summary>
    /// Undo with what the verb reports: the verb returns <see cref="CommandResult.UndoArgs"/> and declares <see cref="CommandInfo.UndoVerb"/>;
    /// the host records both and runs them at the end. The caller cannot supply or change them.
    /// </summary>
    public bool RestoreRecorded { get; init; }
}

/// <summary>
/// What the owner approves, once: an ordered list of steps. At most 8 steps, 16 arguments of 256 characters each. The host approves, stores and
/// compares it by a hash of every field, so any change means a new approval.
/// </summary>
/// <param name="Id">Lowercase letters, digits and dashes; unique inside the module that owns the plan.</param>
/// <param name="Name">What the dialog and the notifications call it (owner-module text).</param>
/// <param name="Steps">The steps, in order.</param>
public sealed record VerbPlan(string Id, LocalisedText Name, IReadOnlyList<PlanStep> Steps);

/// <summary>Where a plan stands with the owner.</summary>
public enum PlanApprovalState
{
    /// <summary>Never approved.</summary>
    NotApproved,
    /// <summary>Approved exactly as it is now.</summary>
    Approved,
    /// <summary>Approved before, but something differs now (an argument, a step, a target module's version, a preview). Needs a new approval.</summary>
    Changed,
    /// <summary>The owner revoked it.</summary>
    Revoked,
    /// <summary>The owner said no (a repeat of the question within a minute is answered without asking again).</summary>
    Declined,
    /// <summary>The approval store cannot be trusted (its folder is not admin-only); nothing counts as approved.</summary>
    Untrusted,
    /// <summary>A module of the plan is not running or the plan is not valid, so it cannot be judged now.</summary>
    Unavailable,
}

/// <summary>The answer to "is this plan approved".</summary>
/// <param name="State">Where it stands.</param>
/// <param name="Reason">An English explanation for <see cref="PlanApprovalState.Changed"/>, <see cref="PlanApprovalState.Unavailable"/> and <see cref="PlanApprovalState.Untrusted"/>; empty otherwise.</param>
public sealed record PlanApproval(PlanApprovalState State, string Reason = "")
{
    /// <summary>True for <see cref="PlanApprovalState.Approved"/>.</summary>
    public bool IsApproved => State == PlanApprovalState.Approved;
}

/// <summary>How a plan run (or its undo) ended.</summary>
public enum PlanOutcome
{
    /// <summary>Every step ran and answered 0.</summary>
    Done,
    /// <summary>A step failed; the steps already done were put back (see the step results).</summary>
    Failed,
    /// <summary>Not approved exactly as it is; nothing ran.</summary>
    NeedsApproval,
    /// <summary>Too many starts of this plan (or of all plans) in the last hour; nothing ran.</summary>
    RateLimited,
    /// <summary>This plan already has an open run.</summary>
    AlreadyRunning,
    /// <summary>Four runs are open already.</summary>
    TooManyRuns,
    /// <summary>The plan is not valid, or a target module or verb is not there.</summary>
    Invalid,
    /// <summary>The run was cancelled.</summary>
    Cancelled,
    /// <summary>The undo ran but a step of it failed; the run stays open until the owner acts.</summary>
    UndoFailed,
    /// <summary>No such run.</summary>
    NotFound,
    /// <summary>This host has no plans.</summary>
    Unavailable,
}

/// <summary>The result of one step.</summary>
/// <param name="Step">The step as run (for an undo step, the undo).</param>
/// <param name="IsUndo">True for a step of the undo phase.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="ExitCode">The verb's exit code, or -1.</param>
/// <param name="Output">The verb's text, or an explanation.</param>
public sealed record PlanStepResult(PlanStep Step, bool IsUndo, VerbOutcome Outcome, int ExitCode, string Output);

/// <summary>The result of <see cref="IVerbService.StartAsync"/> or <see cref="IVerbService.EndAsync"/>.</summary>
/// <param name="RunId">The run's id (empty when nothing started).</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Steps">Every step that ran, forward and undo, in the order they ran.</param>
/// <param name="Message">An English explanation when the outcome is not <see cref="PlanOutcome.Done"/>.</param>
public sealed record PlanRun(string RunId, PlanOutcome Outcome, IReadOnlyList<PlanStepResult> Steps, string Message = "")
{
    /// <summary>True for <see cref="PlanOutcome.Done"/>.</summary>
    public bool Succeeded => Outcome == PlanOutcome.Done;
}

/// <summary>A run that is open: its steps were applied and not yet put back. Includes runs left by a Rigbay that ended.</summary>
/// <param name="RunId">The id for <see cref="IVerbService.EndAsync"/>.</param>
/// <param name="PlanId">The plan.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="Interrupted">True when it was found in the journal at start: Rigbay ended while it was open.</param>
/// <param name="UndoFailed">True when an undo was tried and a step of it failed.</param>
/// <param name="Steps">The command lines of the steps that were applied.</param>
public sealed record ActiveRun(string RunId, string PlanId, DateTimeOffset StartedAt, bool Interrupted, bool UndoFailed, IReadOnlyList<string> Steps);

/// <summary>An approval the owner gave.</summary>
/// <param name="PlanId">The plan.</param>
/// <param name="Hash">The hash of the approved plan (hex).</param>
/// <param name="ApprovedAt">When.</param>
/// <param name="Lines">What was approved, one command line per step.</param>
public sealed record ApprovedPlanInfo(string PlanId, string Hash, DateTimeOffset ApprovedAt, IReadOnlyList<string> Lines);

/// <summary>
/// Verbs of other modules: list them, call the read-only ones, and run plans the owner approved. <see cref="IHostServices.Verbs"/>.
/// <para>Every call runs inside the fault boundary of both modules, with a timeout (30 s per step, 120 s per run). A verb that was started by
/// this service cannot start another call. A module cannot call itself.</para>
/// </summary>
public interface IVerbService
{
    /// <summary>The verbs of the other modules that are running, ordered by module id and verb, texts in the current language. Never null.</summary>
    IReadOnlyList<VerbInfo> List();

    /// <summary>Raised when the list can have changed: a module started, stopped or faulted, or the language changed. Call <see cref="List"/> again. Runs in your fault boundary.</summary>
    event Action? Changed;

    /// <summary>Runs one verb of another module. Only a verb declared <see cref="VerbAccess.ReadOnly"/> runs this way.</summary>
    /// <param name="moduleId">The module.</param>
    /// <param name="verb">The verb.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<VerbResult> InvokeAsync(string moduleId, string verb, IReadOnlyList<string> args, CancellationToken cancellationToken = default);

    /// <summary>Whether the owner approved this plan exactly as it is now.</summary>
    /// <param name="plan">The plan.</param>
    PlanApproval GetApproval(VerbPlan plan);

    /// <summary>
    /// Shows the owner ONE dialog with every step and asks. On yes the host stores the hash. A refusal is remembered for a minute. Use it from a
    /// button the owner pressed.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<PlanApproval> RequestApprovalAsync(VerbPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Withdraws the approval of a plan of this module. Runs already open can still be undone.</summary>
    /// <param name="planId">The plan id.</param>
    void Revoke(string planId);

    /// <summary>The approvals of this module's plans that are in force.</summary>
    IReadOnlyList<ApprovedPlanInfo> ApprovedPlans();

    /// <summary>
    /// Runs the steps of an approved plan in order. It checks the approval against the plan passed now (any difference refuses), the rate limit and the
    /// open runs, writes the journal, runs the steps (a failure puts back the ones done) and raises one notification. Returns the run.
    /// </summary>
    /// <param name="plan">The plan, exactly as approved.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<PlanRun> StartAsync(VerbPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Puts back what an open run of this module changed (every undo is tried), raises one notification and closes the run.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="cancellationToken">Cancels the undo.</param>
    Task<PlanRun> EndAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>This module's open runs, oldest first, including ones left by a Rigbay that ended.</summary>
    IReadOnlyList<ActiveRun> ActiveRuns();
}

/// <summary>The <see cref="IHostServices.Verbs"/> of a host without cross-module verbs: nothing is listed and nothing runs.</summary>
internal sealed class UnavailableVerbService : IVerbService
{
    public static readonly UnavailableVerbService Instance = new();

    public IReadOnlyList<VerbInfo> List() => [];
    public event Action? Changed { add { } remove { } }
    public Task<VerbResult> InvokeAsync(string moduleId, string verb, IReadOnlyList<string> args, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VerbResult(VerbOutcome.Unavailable, -1, "this host has no cross-module verbs"));
    public PlanApproval GetApproval(VerbPlan plan) => new(PlanApprovalState.Unavailable, "this host has no plans");
    public Task<PlanApproval> RequestApprovalAsync(VerbPlan plan, CancellationToken cancellationToken = default) => Task.FromResult(GetApproval(plan));
    public void Revoke(string planId) { }
    public IReadOnlyList<ApprovedPlanInfo> ApprovedPlans() => [];
    public Task<PlanRun> StartAsync(VerbPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PlanRun("", PlanOutcome.Unavailable, [], "this host has no plans"));
    public Task<PlanRun> EndAsync(string runId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PlanRun(runId, PlanOutcome.Unavailable, [], "this host has no plans"));
    public IReadOnlyList<ActiveRun> ActiveRuns() => [];
}
