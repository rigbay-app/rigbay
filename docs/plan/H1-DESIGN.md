# H1 design: background metric demand (Watch) and cross-module verbs under an approved plan (Game scenes)

Task f67fe8b5, job rigbay-13, branch `feat/host-scenes-watch`. **Design only; no code yet.** Written 2026-11-13 from `WORKER-RULES.md`, `DESIGN.md`, `ABSTRACTIONS.md`,
`APP.md`, `MODULE-IDEAS.md` 2 and 3, `POWERTOYS-2026-11.md` section 1, and the code of `Rigbay.Abstractions`, `Rigbay.Core` (`MetricsBus`, `CommandRegistry`,
`ModuleEntry`, `ModuleHostServices`, `RigbayHost`), `Rigbay.App/Ui/Approval.cs`, and the poll code of the XTU, tunnel, Steam, PredatorSense, recorder, sample and demo modules.

Result in one paragraph: the host API grows by **one revision, 0.1.3 to 0.1.4**, all additive (default interface members and new types). Part A adds
`IMetricsBus.SubscribeBackground(id, interval, handler)` plus a one-line helper `MetricDemand.PollInterval(...)` for publishers. Part B adds `IHostServices.Verbs`
(list, free-call of read-only verbs, plans), optional verb metadata on `ICommandRegistry`, and a host-owned approved plan: one dialog, a stored hash,
a per-run notification, a journal for undo and restarts, and a host-enforced rate limit. Nothing in it writes by itself; no module has to change for the
host to stay as it is.

---

## 0. What exists today (facts read from the code)

**How a module picks its poll rate.** There is no host timer. Each module owns its timers and decides with two signals:

| Signal | Source | Meaning |
| --- | --- | --- |
| `IHostServices.PageVisible` | `ModuleEntry.SetPageVisible`, called by `MainWindow` when the module's page is shown or left (`MainWindow.xaml.cs:262`) | The page is on screen. False while the module is off, and false for every module while the window is in the tray (the app sets the current page invisible). |
| `IMetricsBus.SubscriberCount(id)` | `MetricsBus.SubscriberCount(id, excludeOwner)`: subscriptions filtered to `id` plus all-samples ones, from the host (dashboard strip, recorder host code) and other modules; the asking module's own are excluded | Someone wants the reading now. |
| `IMetricsBus.SubscribersChanged` | raised on every subscribe/dispose by another party, no id | Re-evaluate. |

Every module follows the same pattern, `wanted = PageVisible.Value || SubscriberCount(id) > 0`, then fast or slow:

| Module | Fast | Not wanted |
| --- | --- | --- |
| Steam bypass | 1 s | 10 s (`SlowPoll`) |
| Tunnel | 2 s | 15 s |
| PredatorSense (`PollScheduler`) | `PollSeconds` setting | `IdlePollSeconds`; the operation-mode read uses `Timeout.InfiniteTimeSpan` (no poll at all while nobody looks) |
| XTU monitor thread | its sampling period | **not read at all** (`MonitorWanted()` false: the thread sleeps on a wake event); the 5 s status timer is separate |
| Sample module | 1 s | 10 s |
| Recorder | (it is a consumer; it subscribes, which makes the publishers go fast) | |

So what runs while the window is hidden is: each module's slow timer (or nothing), and anything with a subscriber (a running recorder) at the fast rate. A
Watch that used `Subscribe` would be a permanent subscriber of every watched id and would hold those modules at the fast rate all day. That is the
cost `POWERTOYS-2026-11.md` section 1 warns about (idle CPU), and the reason for Part A.

**How a verb is registered and run.** `ICommandRegistry.Register(verb, descriptionKey, handler)`; `CommandRegistry` stores `CommandEntry(ModuleId, Verb,
DescriptionKey, Handler)`. `ModuleHostServices.CommandService` wraps the handler in the module's fault boundary (`ModuleEntry.InvokeAsync`): an exception
faults the callee, the caller gets `CommandResult.Failed`. There is **no metadata**: the host cannot tell which verbs write, what they change, or what
arguments they take, and `InvokeAsync` has no timeout. The write verbs (`xtu apply`, `steam-bypass on|off`, `tunnel on|off`, `predatorsense fan`) each
ask `host.Approval.ConfirmAsync(effect, detail, ct)` inside the handler, so a verb run with nobody at the window opens a dialog (`ApprovalPrompt` shows the
window from the tray) or fails.

**How XTU's auto-switch is approved today.** The consent is the user's confirmation of the page row that turns auto-switch on or picks the profile
for a power source (the row carries a `HardwareEffect`, the host asks, `Settings.cs` and `Apply.cs`, `ApplyOrigin.Auto`). After that the module applies
the profile itself on a power change or resume, **without asking again** (`ApplyService.RunAsync` only calls `ConfirmAsync` for Hotkey, Command and Tray),
announces it with a Background toast, and keeps its own checks (`Rules.Validate`, write latch). It is a standing consent given to the module, enforced by
the module's own code, and the host cannot see it or check it. That is fine when module and writer are the same code. It does not fit Scenes: the writer is
a different module from the one that decides, so the host is the only party that can enforce "only these steps". I reuse the shape (approve the rule once,
exact values on screen, a Background notification per automatic change, the callee's own limits stay) and put the enforcement in the host.
(Not checked: whether XTU asks again when a profile's numbers are edited after the consent; the plan below closes that for plan steps, section B5.)

**Host notification.** `host.ToastSink` is the tray balloon in the elevated app (`APP.md` "Notifications"); `ToastService` in `ModuleHostServices` shows how a
module's `ToastMessage` reaches it. Core can call the sink directly.

---

## A. Background metric demand

### A1. API (`Rigbay.Abstractions`, default interface members on `IMetricsBus`)

```csharp
public interface IMetricsBus
{
    // ... existing members unchanged ...

    /// Receives the samples of one id like Subscribe(id, handler), and tells publishers "I want this id at least every `interval`,
    /// in the background". It does NOT count in SubscriberCount, so it never makes a publisher poll at its fast rate.
    /// `interval` is clamped to 5 s .. 1 h. At most 64 background subscriptions per module (A2). Dispose to stop.
    /// The default implementation (a host or test double without it) returns an inert disposable and delivers nothing (A3).
    IDisposable SubscribeBackground(MetricId id, TimeSpan interval, Action<MetricSample> handler) => NoSubscription.Instance;

    /// The shortest interval any OTHER module asks for this id in the background, or null when nobody does. Own demands are excluded
    /// (like SubscriberCount). Cheap.
    TimeSpan? BackgroundInterval(MetricId id) => null;
}
```

`SubscribersChanged` also fires when a background subscription is added or disposed (documentation change only; same event, no new member). A publisher
already re-evaluates on that event, so it needs no second event.

The helper that makes the publisher side a one-liner (new static class, `Rigbay.Abstractions/MetricDemand.cs`):

```csharp
public static class MetricDemand
{
    /// How often this module should read the metrics it publishes right now.
    ///   page visible, or any subscriber on one of `ids`   -> fast
    ///   else a background demand of N on one of `ids`      -> min(idle, max(fast, N))   (never faster than fast, never slower than idle)
    ///   else                                               -> idle (may be Timeout.InfiniteTimeSpan: "do not poll")
    public static TimeSpan PollInterval(this IHostServices host, IEnumerable<MetricId> ids, TimeSpan fast, TimeSpan idle);

    /// The same decision as three reasons, for a module with its own scheduler: Fast, Background(interval), Idle.
    public static DemandState Evaluate(this IHostServices host, IEnumerable<MetricId> ids, TimeSpan fast, TimeSpan idle);
}
```

Existing subscriptions keep meaning "fast". Nothing about `SubscriberCount`, `PageVisible` or `Subscribe` changes.

### A2. Host implementation (`Rigbay.Core/Registries/MetricsBus.cs`)

* `Subscription` gets `TimeSpan? Background`. `SubscriberCount` counts only `Background is null`. `BackgroundInterval(id, excludeOwner)` is the minimum over
  background subscriptions filtered to `id` whose owner is not `excludeOwner`. Same `lock (_gate)`, same `RaiseSubscribersChanged` path, so every existing
  `SubscribersChanged` hook (they filter by owner) works unchanged.
* `ModuleBus.SubscribeBackground` guards the handler like `Subscribe` (`guard(handler)`) and tracks the disposable, so a stopped or faulted module loses its
  demands (`Cleanup()` disposes tracked subscriptions, as for every subscription today).
* Host-side overload for the app and tests: `MetricsBus.SubscribeBackground(handler, id, interval)` and `GetBackgroundDemands()` (id, interval, owner), for a
  diagnostics line and tests. No UI change needed.
* Validation: id must be valid (no all-samples background subscription: a demand must name what it wants); interval below 5 s is raised to 5 s, above 1 h
  lowered to 1 h (logged once); a module over 64 background subscriptions gets `InvalidOperationException` (the module's own bug; the fault boundary logs it).
  Delivery is the same as `Subscribe`: every sample of the id, synchronous on the publisher's thread, handler in the module's boundary. No throttle: Watch keeps
  a cheap handler (store value and time). (Open question Q2.)
* Cost of a demand: one list entry. The host starts no timer and polls nothing.

### A3. What an old publisher does

A publisher built before this revision (every module today) never reads `BackgroundInterval`. With only a background demand on its id:
`SubscriberCount` is 0 and the page is hidden, so it stays at its idle rate (Steam 10 s, Tunnel 15 s, PredatorSense `IdlePollSeconds`) or, if it does not poll
when unwatched (XTU monitor, PredatorSense operation mode), it **does not publish**. Nothing breaks and nothing speeds up. For Watch that means: a rule reads
the sample's `Timestamp`; a value older than twice its interval is "no data", not a value, and the rule neither fires nor clears. Watch's page can say "waiting
for XTU" per rule. Those modules adopt `MetricDemand.PollInterval` in their own tasks (a few lines each; not part of H1, I may not touch `modules/`). I will list
that in the result mail as the follow-up.

A host or test double that does not implement the new members gets "nobody demands" (`BackgroundInterval` null) and `SubscribeBackground` returns an inert
disposable (nothing delivered). A module that calls it on such a host must have declared `>=0.1.4` and is refused before it loads (section 11 rule); a module that
guards the call with `HostApi.RevisionOf(HostApi.Current) >= 4` gets no samples, and its UI should say "needs Rigbay 0.1.4".

### A4. Tests (fake clock, fake modules; `tests/Rigbay.Core.Tests/BackgroundDemandTests.cs`)

`MetricsBus` with a fake `MetricsClock`, fake modules built on `ForModule`, and `MetricDemand.PollInterval` over a stub `IHostServices`:

1. demand does not change `SubscriberCount`; a normal subscriber does; both together report each separately;
2. `BackgroundInterval` is the minimum of two demands, excludes the asking module's own, and goes back to null after dispose;
3. `PollInterval` table: nothing -> idle; page visible -> fast; subscriber -> fast; background 30 s with idle 60 s -> 30 s; background 3 s (clamped 5 s) with fast 2 s ->
   5 s; background 120 s with idle 60 s -> 60 s; background smaller than fast -> fast; idle infinite with a demand -> the demand interval; subscriber beats demand;
4. `SubscribersChanged` fires for a demand added, changed (dispose + add) and removed, and is not raised on the owner's own demand;
5. delivery: a sample reaches the background handler inside the module's guard; a throwing handler faults only its module; withdrawn notice delivered;
6. a stopped or faulted module loses its demands (publisher sees null again);
7. old publisher (a module that only uses `SubscriberCount`) is not woken to the fast path by a demand; its slow rate is unchanged (counts timer ticks);
8. clamps, the 64 limit, invalid id, interval <= 0.

---

## B. Cross-module verbs and the approved plan

### B1. Verb metadata (so a verb can be listed, understood and gated)

New in `Rigbay.Abstractions`, optional, default-implemented:

```csharp
public enum VerbAccess { Unknown, ReadOnly, Writes }          // Unknown = an old module that gave no metadata: treated as Writes

public sealed record CommandInfo(string Verb, string DescriptionKey)
{
    public VerbAccess Access { get; init; } = VerbAccess.Unknown;
    public string? UsageKey { get; init; }                     // "xtu apply <profile>"; module-relative key or Text.Literal
    public HardwareEffect? Effect { get; init; }               // required when Access == Writes (what the owner is told; Kind says hardware or setting)
    public Func<IReadOnlyList<string>, LocalisedText?>? Preview { get; init; }   // read-only: the exact change these args would make ("Gaming: PL1 45 W, PL2 65 W, 28 s")
    public string? UndoVerb { get; init; }                     // a verb of the same module that puts back what this one changed, fed by CommandResult.UndoArgs
}

public interface ICommandRegistry
{
    void Register(string verb, string descriptionKey, CommandHandler handler);                         // unchanged
    void Register(CommandInfo info, CommandHandler handler) => Register(info.Verb, info.DescriptionKey, handler);   // new; the default loses the metadata
}

public sealed record CommandResult(int ExitCode, string Output)
{
    public IReadOnlyList<string>? UndoArgs { get; init; }       // new: the args of CommandInfo.UndoVerb that restore the value this run replaced ("Balanced")
    ...
}
```

Rules: `Writes` without `Effect` throws `ArgumentException` at `Register`. `Preview` and the registration run in the module's boundary; a `Preview` that throws
faults its module, and one that takes more than 2 s counts as "no preview". The metadata lives in `CommandEntry` (one new optional member). `rigbay <module> <verb>` from
a terminal is unchanged and ignores all of this.

### B2. The service a module sees: `IHostServices.Verbs`

New default member `IVerbService Verbs => UnavailableVerbService.Instance` (list empty, every call returns `Unavailable`). Core implements it as `VerbBroker`, one
instance per calling module.

```csharp
public interface IVerbService
{
    /// Verbs of the OTHER modules that are running, ordered by module id then verb, texts already in the current language
    /// (resolved through the owner module's localisation, like MetricInfo). Verbs of modules that are off are not listed.
    IReadOnlyList<VerbInfo> List();
    event Action? Changed;                         // a module started/stopped/faulted or the language changed; call List() again

    /// Runs ONE verb of another module. Only a verb declared ReadOnly runs this way; Writes and Unknown verbs return NeedsPlan without running.
    Task<VerbResult> InvokeAsync(string moduleId, string verb, IReadOnlyList<string> args, CancellationToken cancellationToken = default);

    // ---- approved plans (B3 to B6)
    PlanApproval GetApproval(VerbPlan plan);                                           // Approved | NotApproved | Changed | Revoked, with the reason as text
    Task<PlanApproval> RequestApprovalAsync(VerbPlan plan, CancellationToken ct = default);   // THE dialog
    void Revoke(string planId);
    IReadOnlyList<ApprovedPlanInfo> ApprovedPlans();                                   // this module's
    Task<PlanRun> StartAsync(VerbPlan plan, CancellationToken ct = default);           // run the steps
    Task<PlanRun> EndAsync(string runId, CancellationToken ct = default);              // undo them
    IReadOnlyList<ActiveRun> ActiveRuns();                                             // this module's, including ones left by a restart
}

public sealed record VerbInfo(string ModuleId, string ModuleName, string Verb, string Description, string? Usage,
    VerbAccess Access, string? EffectText, EffectKind EffectKind, string? UndoVerb, bool HasPreview);

public enum VerbOutcome { Done, Failed, Usage, NotFound, NotRunning, NeedsPlan, TimedOut, Cancelled, CalleeFaulted, Unavailable, RateLimited }
public sealed record VerbResult(VerbOutcome Outcome, int ExitCode, string Output)        // Done carries the callee's exit code 0; Failed/Usage carry 1/2
```

**Fault boundaries and timeout.** `InvokeAsync` (and every plan step) checks the caller is `Running`, finds the callee `CommandEntry` and runs it with a token linked to
the caller's `StopToken`, the callee's `StopToken`, the caller's own token and a timeout (host option `VerbTimeout`, default 30 s; plan steps the same; a whole run at most 120 s).
The callee side is the boundary it already has (`ModuleEntry.InvokeAsync` in `CommandService`): an exception faults the callee, the broker then sees a non-running
callee and returns `CalleeFaulted`. A timeout cancels the token and returns `TimedOut` and does **not** fault the callee (a verb may be legitimately slow, and the
CLI has no timeout today). Caller side: if the caller stops or faults during the call the token is cancelled and the result is dropped. The broker's own code never
throws into the caller. A module cannot call its own verbs through the broker (`NotFound`), and a verb that is running because of the broker cannot start another
call (depth 1, an `AsyncLocal` flag; the call returns `NotFound`), so there are no loops.

### B3. The approved plan

```csharp
public sealed record PlanStep(string ModuleId, string Verb, IReadOnlyList<string> Args)
{
    public PlanStep? FixedUndo { get; init; }        // run in the undo phase as written ("steam-bypass off")
    public bool RestoreRecorded { get; init; }       // use the undo the verb reported in CommandResult.UndoArgs (B6)
}

public sealed record VerbPlan(string Id, LocalisedText Name, IReadOnlyList<PlanStep> Steps);   // Id: [a-z0-9-], unique inside the owner module
```

* A **plan** is what the owner approves: an owner-module id, a plan id, a name, an ordered list of steps. At most 8 steps, 16 args per step, 256 characters per arg.
  A step names a module, a verb and its exact arguments. Its undo is either another exact step (`FixedUndo`) or "the undo this verb reports" (`RestoreRecorded`) or
  nothing (shown to the owner as "not undone").
* A **target** module may not be the owner. A step whose verb is `ReadOnly` is allowed but pointless to approve; plans are for writes. Steps on `Unknown` verbs are allowed
  in a plan (that is the point of asking) but the dialog says "this module did not say what the verb changes".
* **The hash.** SHA-256 of a canonical text: owner module id, plan id, for each step in order the target module id **and its manifest version**, verb, each arg, the fixed
  undo (same fields), the restore flag, and for each step the `Preview` text that was shown (B4). If any of these changes the hash changes.

### B4. The one dialog

`RequestApprovalAsync(plan)`:

1. Validates the plan (shape, limits, every target module exists; targets need not be running to be approved, only to run).
2. For each step, reads the verb's metadata and, when it has one, calls `Preview(args)` (2 s, read-only, in the target's boundary) to get the exact change text.
3. Builds an `ApprovalRequest` with a new optional `Plan` member (list of `PlanApprovalLine`: module name, command line `xtu apply Gaming`, effect text and kind, preview
   text, undo line) and shows **one dialog** through the existing `IApprovalPrompt`. The App's `ApprovalPrompt` draws it: title "Let *<scene>* change your machine
   on its own?", one line per step, a footer "Each run shows a notification. You can revoke this any time.", primary button "Approve 3 steps", **focus on Cancel**.
4. On approve: stores `{ownerModule, planId, hash, approvedAt, lines}` in `approved-plans.json` in the host data folder (not in the module's data folder; written atomically
   like `JsonSettingsStore`). A decline is remembered for 60 s (same hash): a module that asks again in a loop gets `Declined` without a dialog.

### B5. A run: what the host checks, in order

`StartAsync(plan)` and `EndAsync(runId)` are serialised by one host-wide lock (`SemaphoreSlim`), so steps of two plans never interleave.

1. Owner module is running; plan valid; the stored approval for `(owner, planId)` exists, is not revoked, and **its hash equals the hash of the plan passed now**.
   Otherwise `NeedsApproval` / `Changed` and nothing runs. (Exact match: a changed argument, a reordered step, a different target version, a new undo: refused.)
2. Rate limit: at most 6 starts per plan per hour and 20 per hour in total (clock-driven, tests use a fake clock). Over: `RateLimited`, a Warning notification
   ("Scene X was asked to run too often and was paused"), logged. This is the guard against a trigger that flaps (a game that crashes and restarts in a loop would
   otherwise write hardware every time).
3. Opens the run in the journal (B6) **before** the first step.
4. For each step: the target must be running and the verb registered; its metadata must still say what the owner saw (the verb's `Access`/`Effect`/`UndoVerb` are in the
   hash through the preview text and target version; I also re-run `Preview(args)` now and compare it to the approved text: **different text means the values changed
   since approval, the step and the run are refused with `Changed`**). Then the step runs inside a `PlanScope` (below).
5. First failure (exit code not 0, timeout, target not running) stops the run, then **rolls back the steps already done in reverse** (their undo, best effort, each
   logged) and returns `Failed` with the per-step results. The run is closed.
6. One notification per run (B7).

**How the callee's own approval question is answered.** The write verbs call `host.Approval.ConfirmAsync` inside their handler. `PlanScope` is an `AsyncLocal` set by the
broker around one step: `{runId, targetModuleId, remaining = 3, closed = false}`. In `RigbayHost.RequestApprovalTextAsync(entry, ...)` (the single place all approvals pass through):
if a scope is current, `scope.TargetModuleId == entry.Id`, it is not closed and `remaining > 0`, the question is answered **yes without a dialog**, `remaining--`, and the
effect and detail text are written to the log and added to the run result (so the notification and the audit show what was asked). Anything else (another module, an
exhausted or closed scope, an approval raised by a timer the verb started earlier, a revert from a page) is asked normally. The scope is closed when the step ends, so a
task the verb left running cannot use it later. The callee's own checks (limits, allow-lists, write latch) are untouched: the plan removes the dialog, not the validation.

### B6. Undo, the journal, restart, overlap

* **Value before.** The host cannot know it. The verb does: it reads the current value to do its job anyway. A verb that can restore declares `CommandInfo.UndoVerb`
  and returns `CommandResult.UndoArgs` (for `xtu apply Gaming`: `UndoVerb = "apply"`, `UndoArgs = ["Balanced"]`, the profile that was active). A step with
  `RestoreRecorded` has that pair recorded in the journal; at undo time the host runs `(same module, UndoVerb, UndoArgs)`. The recorded undo is accepted only if the module
  and verb equal the ones the metadata declared for that step's verb, and args are within limits; **the caller cannot supply or change them**. A verb that reports no undo
  args (nothing changed, "already active") records nothing. Steps with a `FixedUndo` (`steam-bypass on` / `steam-bypass off`, `recorder start` / `recorder stop`) need nothing recorded.
  The undo steps are part of the approved hash (fixed ones as written; recorded ones through "the verb named X restores"), and they run under the same scope and checks.
* **Journal.** `plan-runs.json` next to `approved-plans.json`, rewritten atomically at: run opened, each step done (with its recorded undo), run closed. It holds run id, owner,
  plan id, **the plan hash and the plan's steps as run** (so an undo does not depend on the module still holding the plan), start time, step states.
* **Undo after a revoke.** Revoking stops new runs. Undoing a run that is already in the journal is still allowed: it only puts back what the approved steps changed and
  every undo is one the owner saw (fixed) or one the verb itself recorded. Leaving a machine in a changed state because the owner clicked Revoke would be worse. (Q3.)
* **Rigbay restarts mid-scene.** At host start the journal's open runs are loaded as `ActiveRun { Interrupted = true }`. The host never undoes by itself. It (1) logs them, (2) shows one
  notification "Scene X was running when Rigbay closed; its changes may still be in place", (3) shows them to the owner module through `ActiveRuns()`: Scenes sees its run, adopts
  it if the game is still running, or calls `EndAsync(runId)` if not. (4) A host verb `rigbay plans undo <runId>` runs the undo without Scenes, so the owner is never stuck.
* **Overlapping scenes.** The host allows up to 4 open runs and serialises their steps. It does not decide which scene wins; that is Scenes' rule ("last started wins, undo in
  reverse order" as `MODULE-IDEAS.md` says), and `ActiveRuns()` returns them in start order so Scenes can do it. A second `StartAsync` for a plan that already has an open run is refused
  (`AlreadyRunning`), so a trigger that fires twice does not stack the same writes.
* **Failure in `EndAsync`.** Every undo step is tried even if an earlier one failed; the run closes with the failures listed, one Warning notification, and the log has the exact command lines so
  the owner can run them by hand. A run with a failed undo stays in the journal as `UndoFailed` until the owner acts (`rigbay plans undo` again) or discards it (`rigbay plans forget <runId>`).

### B7. Notification per run

The **host** raises it (not the module, which could skip it) through `ToastSink` (tray balloon in the elevated app), kind Info, origin Background: title = plan name resolved
through the owner module; body = "Ran 3 steps: xtu apply Gaming, steam-bypass on, recorder start." (command lines; the callee's own `ConfirmAsync` texts as recorded in the scope).
`EndAsync` raises its own ("Put back 3 steps ..."). A failed or rolled-back run, a rate-limit pause and an interrupted run are Warnings. New host language keys `core.plan.*` in `en.lang`
and `vi.lang`. If there is no sink (tests, command line) it is logged. (Q4: one notification at the start and one at the end, or only one per run?)

### B8. Revoke in one click, and where it lives

* `IVerbService.Revoke(planId)` (the module's own page row, a button "Revoke approval") and `rigbay plans list | revoke <planId> | undo <runId> | forget <runId>` as **host**
  verbs answered by `RigbayHost.ExecuteCommandAsync` next to `list` and `version` (so they exist whatever the modules do). Revoking is immediate and logged.
* A host-level list with a Revoke button for every approved plan belongs on the Settings page or the Modules page. I may not touch `Ui/SettingsPage.cs` or `TrayHost.cs`
  (UT is changing them): I will expose `RigbayHost.Plans.Approved` (a `PlanStore` with `Changed`) and mail the coordinator the one-line hook UT needs. (Q5.)
* A plan is also revoked when the owner module is uninstalled, and its approval is void when any target module's version changes (hash contains it).

### B9. Safety argument

What can go wrong and what stops it:

| Risk | Control |
| --- | --- |
| A module writes the machine through another module without the owner knowing | `Verbs.InvokeAsync` refuses everything that is not declared ReadOnly. A write only runs inside `StartAsync`/`EndAsync` and only if the owner approved the exact plan in a dialog the host drew. |
| Scenes (or its settings file) is edited or buggy and runs something else | The hash is checked at every run against the plan passed now; any difference is refused. The module cannot add a step. |
| An argument is changed | Part of the hash. |
| The target module is updated and the verb now does something else | The target module's version is in the hash; an update voids the approval. |
| The same command line now means different values (an edited XTU profile) | `Preview(args)` text is shown at approval, stored in the hash and compared at run time; a different text refuses the step. A verb with no `Preview` is shown to the owner as "the module decides the values" and the dialog says so. |
| A verb asks for more than one write | The scope allows 3 auto-answered questions per step, all logged and put in the notification; the 4th gets a normal dialog. |
| The auto-yes leaks to later code | The scope is an `AsyncLocal` object that is closed when the step ends and only answers for its target module. |
| A trigger flaps and writes in a loop | 6 starts per plan and 20 per hour in total, then a paused notice; one open run per plan. |
| The run dies halfway | Journal written before the first step; rollback on failure; restart detection; `rigbay plans undo`. |
| A write is slow or hangs | 30 s per step, 120 s per run; timeout is a failure and rolls back; the callee is not killed but its scope is closed. |
| Modules calling each other in circles | A verb run by the broker cannot call the broker (depth 1); a module cannot target itself. |
| Silent change | One notification per run, from the host, and a log line per step with the exact command line. |
| The owner can not stop it | Revoke on the module page, `rigbay plans revoke`, disable the module; undo stays possible. |

What it does **not** protect against: a malicious module. Modules run in-process as administrator and could write the hardware directly; the controls are against mistakes, a
corrupted settings file and a trigger that misfires. This is the same trust level `DESIGN.md` already states for modules, and the approval file is not signed.

### B10. What old hosts and modules do

* Old module on the new host: unchanged. Its verbs are `Unknown` access: listed as such, usable in a plan (with the "did not say what it changes" line), never by free `InvokeAsync`.
* New module (uses `Verbs`, `CommandInfo`, `SubscribeBackground`) on an old host: it declares `>=0.1.4 <0.2`; hosts that honour revisions refuse to load it with "Needs host API 0.1.4 or newer".
  0.1.0 and 0.1.1 hosts ignore the revision (section 11); that is the existing caveat, and the module should not be released to the registry before the host that has the members.
* `ICommandRegistry.Register(CommandInfo, handler)`, `IVerbService`, `Verbs`, `SubscribeBackground` and `BackgroundInterval` are default members, so test doubles that implement the interfaces still compile.

### B11. Host-side pieces (Core and App)

| Where | What |
| --- | --- |
| `Rigbay.Abstractions` | `CommandInfo`, `VerbAccess`, `IVerbService`, `VerbInfo`, `VerbResult`, `VerbOutcome`, `VerbPlan`, `PlanStep`, `PlanApproval`, `PlanRun`, `ActiveRun`, `ApprovedPlanInfo`, `MetricDemand`, new default members on `ICommandRegistry`, `IMetricsBus`, `IHostServices`; `CommandResult.UndoArgs`; `HostApi.Revision = 4` |
| `Rigbay.Core/Verbs/` (new) | `VerbBroker` (list, invoke, link tokens, timeout), `PlanStore` (hash, `approved-plans.json`, revoke), `PlanRunner` (checks, scope, rate limit, rollback, notification), `PlanJournal` (`plan-runs.json`), `PlanScope` |
| `Rigbay.Core` changed | `CommandRegistry`/`CommandEntry` (metadata), `ModuleHostServices` (`Verbs`, `SubscribeBackground` through `ModuleBus`), `RigbayHost.RequestApprovalTextAsync` (scope check), `RigbayHost.ExecuteCommandAsync` (`plans` verbs), `ApprovalRequest.Plan`, `lang/en.lang` + `vi.lang` (`core.plan.*`) |
| `Rigbay.App` | `Ui/Approval.cs` + `lang/` (the plan dialog and keys), and wiring in `Program`/host setup of the data folder; **not** `TrayHost.cs`, `SettingsPage.cs` |
| `docs/ABSTRACTIONS.md` | section 6 (background demand), new 9.1 (verb metadata, cross-module verbs, approved plans), the 11 revision row, 12 (one rule for authors), 16 (Core objects), 17 (changelog); `docs/APP.md` a short "Approved plans" paragraph |

### B12. Tests (`tests/Rigbay.Core.Tests/VerbBrokerTests.cs`, `PlanTests.cs`, fake `TimeProvider`, fake modules via `ModuleTests` helpers)

1. Listing: other modules only, running only, sorted, texts through the owner's localisation, metadata and `Unknown` for an old registration; `Changed` fires on start/stop/fault/language.
2. `InvokeAsync`: ReadOnly runs; Writes and Unknown return `NeedsPlan` and run nothing; unknown module `NotFound`; stopped module `NotRunning`; own module refused; args limits.
3. Fault boundaries: callee throws -> callee `Faulted`, caller keeps running, result `CalleeFaulted`; timeout (fake time) -> `TimedOut`, callee still running; caller stops during the call -> cancelled; a verb run by the broker calling `Verbs` is refused.
4. Approved plan: no approval -> nothing runs; approve -> runs in order; **exact match**: change an arg, reorder, add a step, change a fixed undo, bump the target's manifest version, change the `Preview` text -> each refused with `Changed` and nothing ran; revoke -> refused; revoked then undo of an open run still works; a declined dialog stores nothing and a re-ask within 60 s gets `Declined` without a dialog.
5. Scope: the callee's `ConfirmAsync` is answered yes inside the step (and logged), a 4th question and a question from another module or after the step ends go to the prompt; the prompt is never shown for a plan step.
6. Notification: one per start, one per end, Warning for failure/rate limit/interrupted; text lists the steps; none from a declined approval.
7. Failure: step 2 fails -> step 1 rolled back with its undo, run closed, result lists both; undo failure listed, run stays `UndoFailed`.
8. Undo: `RestoreRecorded` uses the verb's `UndoArgs`; a verb reporting a different module/verb or too many args is ignored and logged; "already active" records none.
9. Journal and restart: a new `RigbayHost` on the same data folder sees the open run as `Interrupted`, raises the one notification, does not undo; `rigbay plans undo` does; `forget` drops it.
10. Overlap: second start of the same plan refused; two different plans serialise (steps never interleave, checked with a gate in the fake verb); more than 4 open runs refused; rate limit with the fake clock (6th start in an hour refused, 7th after an hour allowed).
11. Persistence: `approved-plans.json` round trip, a corrupt file is renamed and means "nothing approved" (never "everything approved"), uninstall of the owner removes the entry.
12. Part A tests (A4). `ApiVersionTests` pinned to 0.1.4.
13. No test calls a real hardware verb: all fake modules change an in-memory value.

**Self-test (`tools\selftest.ps1 -AllowMulti`, `SelfTest.cs`).** Two new small demo modules in `tools/demo-modules` (new files, existing ones untouched): `demo-plan-target` (verbs
`get` ReadOnly; `set <n>` Writes with `Effect`, `Preview`, `UndoVerb`; it only changes a number in memory) and `demo-plan-owner` (page with a button that approves and runs a
three-step plan, one fixed undo, one recorded). Checks: the approval dialog is built with 3 lines and Cancel focus (the test harness answers via `ApprovalPrompt.TestAnswer`, `Asked`
counts exactly 1); the run changes the demo value and raises exactly one balloon (counted through the toast sink); a changed argument is refused; revoke refuses; end restores the
value; `rigbay plans list` output; the background demand is visible in `GetBackgroundDemands` and a demo publisher slows down to its background interval. No hardware, no real write.

### B13. Risks

1. **Scope of the auto-yes** is the sharp edge. It is narrow (target module, 3 questions, one step, closed after) and logged, but it is a bypass of the dialog by design. If you prefer, the
   alternative is for target modules to expose a second handler for plan use, which means changing every module; I recommend the scope.
2. **`Preview` is voluntary.** Verbs without it are approved on the command line alone. The follow-up module tasks (XTU, Steam, Tunnel, Recorder, PredatorSense) should add metadata,
   `Preview` and `UndoVerb`; until then Scenes can use those modules but the dialog says the values are decided by the module.
3. **`RestoreRecorded` needs module changes** (XTU `apply` must report the profile before). `FixedUndo` works with today's `on/off` and `start/stop` pairs once they declare `Access`/`Effect`.
4. **`HostApi.Revision` merge conflict.** Another branch that raises the revision conflicts on the one line in `HostApi.cs` and the table row; the coordinator picks the order. I will not touch `Directory.Build.props`, `CHANGELOG.md` or the version lines.
5. **Memory/idle cost**: one lock, a few lists, one small JSON file read at start; no timers. The journal is written only on a run.
6. **UI**: the dialog is drawn by code I can only check with the self-test's `Asked`/`LastTitle` and a window render, never by clicking; same limit as the existing approval dialog.
7. **Not verified** until built: everything above is design. Real Scenes behaviour with game processes is the module's task.

### Open questions for the coordinator (defaults in brackets, I build these unless you say otherwise)

* **Q1.** Is the XTU/Steam/Tunnel/Recorder metadata (`Access`, `Effect`, `Preview`, `UndoVerb`) a separate task for those modules' owners [yes, I list the exact additions in my result mail]?
* **Q2.** Background subscribers get every sample, unthrottled [yes], or at most one per `interval`?
* **Q3.** Undo of an already open run stays allowed after Revoke [yes].
* **Q4.** One host notification at start and one at end of a scene [yes], or a single one per run?
* **Q5.** Revoke button in the host UI: I expose `RigbayHost.Plans` and mail the hook for UT (Settings page), and ship `rigbay plans ...` plus the module's own button [yes].
* **Q6.** Rate limit 6 per plan and 20 per hour in total, 8 steps per plan, 3 auto-answers per step, timeouts 30 s / 120 s [yes, constants in `RigbayHostOptions`].
* **Q7.** Two small demo modules in `tools/demo-modules` as new files (that folder is not listed in my paths) [yes].
* **Q8.** `module.json` `uses` flag "runs other modules' verbs" shown at install time: not in H1 [skip], the approval dialog already tells the owner.

---

## Coordinator's changes (2026-11-13, mail m-0eb53a6aee94): all four adopted

1. **Approval store location (security).** `approved-plans.json` and `plan-runs.json` live under `%ProgramData%\Rigbay\plans\<user SID>\` with an explicit admin-only
   DACL (SYSTEM + Administrators full control, protected, no user write), made by the same `IFolderSecurity` as the modules folder. A folder that already exists is
   **verified, not repaired**: owner Administrators/SYSTEM and no write ACE for anyone else (`AdminFolderSecurity.Audit`/`FindWriters`). If it is wrong or cannot
   be verified the store is `Untrusted`: nothing counts as approved, no approval can be saved, a Warning is logged once, and nothing is repaired silently. The check
   is repeated before every approval check and every journal write. `RigbayHostOptions.PlansRoot` overrides the folder (tests and the self-test use a scratch folder made
   admin-only the same way; the self-test removes it with `toolsemove-elevated.ps1`). Rule for `DESIGN.md`/`ABSTRACTIONS.md`: **anything that grants the elevated host an
   automatic write must not live in a user-writable place.** The earlier text of B4/B6 that says "host data folder" is replaced by this.
2. **Preview rule.** `Preview(args)` describes only the target values the args select, as a pure function of the args and the module's own configuration, never of live state
   (Steam's "Off -> On" would differ between runs). The hash uses the preview's **key and arguments** (not the resolved text, so a language change does not void an approval).
   Tested with a demo verb whose live state changes between approval and run.
3. **Scope hardening.** Inside a `PlanScope` a `ConfirmAsync` is answered yes only when its `HardwareEffect.Kind` equals the declared `Effect.Kind` of the step verb. A verb with
   unknown access (no declared effect) is answered for any kind and the log says so. A mismatch goes to the normal prompt.
4. **Re-approval visibility.** The approval record keeps the manifest version of each target. When one differs, `GetApproval` returns `Changed` with the reason
   "<module> was updated from x to y" and the first refused run per plan raises one Warning notification (not one per trigger; re-armed by a new approval).

Out of scope, for the result mail: XTU's auto-switch consent and profiles live in a user-writable folder (same class of issue, bounded by XTU's `Rules.Validate`).
