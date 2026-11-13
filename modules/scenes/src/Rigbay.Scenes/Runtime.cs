using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Scenes;

/// <summary>Why a scene did not run, or does not run again until its game has ended and started again.</summary>
internal enum BlockKind { None, NeedsApproval, Changed, RateLimited, TooManyRuns, Invalid, Failed, Stopped, Cancelled }

/// <summary>What the module knows about one scene right now (not stored).</summary>
internal sealed class SceneState
{
    /// <summary>What the host said the last time it was asked about the plan.</summary>
    public PlanApproval Approval { get; set; } = new(PlanApprovalState.NotApproved);

    /// <summary>The run this scene has open, or null.</summary>
    public string? RunId { get; set; }
    public DateTimeOffset RunStarted { get; set; }
    public string RunGame { get; set; } = "";
    public bool Manual { get; set; }

    /// <summary>Since when no program of the scene runs (the grace starts here), or null while one does.</summary>
    public DateTimeOffset? Missing { get; set; }

    /// <summary>The undo of the open run failed once; it is not retried by itself (the owner presses Stop).</summary>
    public bool EndFailed { get; set; }

    /// <summary>The scene whose run this one waits for before it is put back, or empty.</summary>
    public string WaitingFor { get; set; } = "";

    public BlockKind Block { get; set; }
    public string BlockDetail { get; set; } = "";

    /// <summary>The question for the owner is open.</summary>
    public bool Approving { get; set; }

    /// <summary>The last answer of the host to a request for approval, for the page.</summary>
    public PlanApproval? LastAnswer { get; set; }

    /// <summary>The words of the last thing the owner typed that was refused (a program name), for the page.</summary>
    public string BadInput { get; set; } = "";
}

/// <summary>
/// The module's working part: the scenes, their approvals, the poll of the process list and the runs. <c>Page.cs</c> is the page, <c>Commands.cs</c> the verbs.
/// Everything that talks to the host about a run goes through <c>_work</c> (one operation at a time); the question to the owner does not, so an unanswered dialog never
/// holds up the end of a game.
/// </summary>
internal sealed partial class ScenesRuntime
{
    private readonly IHostServices _host;
    private readonly Dependencies _deps;
    private readonly object _state = new();                 // settings and scene states
    private readonly SemaphoreSlim _work = new(1, 1);       // one start, end or tick at a time
    private readonly Dictionary<string, SceneState> _states = new();
    private readonly List<Task> _tasks = new();
    private ScenesSettings _s = new();
    private IDisposable? _ticker;
    private volatile bool _stopping;
    private volatile bool _started;
    private bool _hooked;
    private int _tickBusy;

    [ThreadStatic] private static bool _own;

    private IVerbService Verbs => _host.Verbs;

    public ScenesRuntime(IHostServices host, Dependencies deps)
    {
        _host = host;
        _deps = deps;
        CreateRows();
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        _s = _host.Settings.Load<ScenesSettings>();
        Sanitise();
        lock (_state) foreach (var scene in _s.Scenes) EnsureView(scene);
        LoadRows();
        RegisterCommands();
        _host.Localisation.LanguageChanged += OnLanguageChanged;
        _host.PageVisible.Changed += OnPageVisible;
        Verbs.Changed += OnVerbsChanged;
        _hooked = true;
        RefreshTexts();
        Track(StartupAsync, "start");
    }

    /// <summary>Reads, never writes: the approvals, then the runs the last Rigbay left, then the poll (only if something needs it).</summary>
    private async Task StartupAsync()
    {
        await Task.Run(RefreshApprovals).ConfigureAwait(false);
        await ReconcileAsync(initial: true).ConfigureAwait(false);
        _started = true;
        RefreshTexts();
        UpdatePolling();
    }

    public void Stop()
    {
        _stopping = true;
        if (_hooked)
        {
            _host.Localisation.LanguageChanged -= OnLanguageChanged;
            _host.PageVisible.Changed -= OnPageVisible;
            Verbs.Changed -= OnVerbsChanged;
            _hooked = false;
        }
        lock (_state) { _ticker?.Dispose(); _ticker = null; }

        // Turning the module off (or closing Rigbay) puts back what an open scene changed: nothing would manage it afterwards. If that cannot be done in the time
        // the host gives Shutdown, the host's journal keeps the run and the next start adopts or ends it (and `rigbay plans undo` works without this module).
        try { Task.Run(EndOpenRunsAtStopAsync).Wait(TimeSpan.FromSeconds(3.5)); }
        catch (Exception ex) { _host.Log.Warn("scenes: putting back the open scenes at shutdown did not finish: " + ex.Message); }
    }

    private async Task EndOpenRunsAtStopAsync()
    {
        if (!await _work.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false)) return;
        try
        {
            List<string> open;
            lock (_state) open = _s.Scenes.Where(s => State(s.Id).RunId is not null && !State(s.Id).EndFailed).OrderByDescending(s => State(s.Id).RunStarted).Select(s => s.Id).ToList();
            foreach (var id in open) await EndSceneAsync(id, "the module was turned off").ConfigureAwait(false);
        }
        finally { _work.Release(); }
    }

    public async Task WhenIdleAsync()
    {
        for (; ; )
        {
            Task[] pending;
            lock (_tasks) pending = _tasks.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private Task Track(Func<Task> work, string what)
    {
        Task task = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _host.Log.Error("scenes: " + what + " failed", ex); }
        });
        lock (_tasks) _tasks.Add(task);
        task.ContinueWith(t => { lock (_tasks) _tasks.Remove(t); }, TaskScheduler.Default);
        return task;
    }

    private Task Track(Action work, string what) => Track(() => { work(); return Task.CompletedTask; }, what);

    // ------------------------------------------------------------------ settings

    /// <summary>Repairs what a hand edit can break: ids that are not valid or not unique, lists that are null. A scene that was repaired has to be approved again (its id is its plan).</summary>
    private void Sanitise()
    {
        _s.Scenes ??= new();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in _s.Scenes)
        {
            scene.Games ??= new();
            scene.Steps ??= new();
            scene.Name ??= "";
            foreach (var step in scene.Steps)
            {
                step.Args ??= new();
                step.UndoArgs ??= new();
                step.Module ??= ""; step.Verb ??= ""; step.UndoModule ??= ""; step.UndoVerb ??= "";
                if (step.Undo is not (UndoMode.None or UndoMode.Recorded or UndoMode.Fixed)) step.Undo = UndoMode.None;
            }
            if (!SceneRules.IsId(scene.Id) || !seen.Add(scene.Id))
            {
                scene.Id = NewId(seen);
                scene.ApprovedFingerprint = "";
                scene.Enabled = false;
                seen.Add(scene.Id);
            }
        }
    }

    private static string NewId(ISet<string> taken)
    {
        string id;
        do id = "scene-" + Guid.NewGuid().ToString("N")[..6];
        while (taken.Contains(id));
        return id;
    }

    private void Save()
    {
        try { _host.Settings.Save(_s); }
        catch (IOException ex) { _host.Log.Warn("scenes: the settings could not be saved: " + ex.Message); }
    }

    internal SceneState State(string id)
    {
        lock (_state)
        {
            if (!_states.TryGetValue(id, out var st)) _states[id] = st = new SceneState();
            return st;
        }
    }

    private SceneSettings? Find(string id) { lock (_state) return _s.Scenes.FirstOrDefault(s => s.Id == id); }

    private List<SceneSettings> Snapshot() { lock (_state) return _s.Scenes.Select(s => s.Clone()).ToList(); }

    // ------------------------------------------------------------------ approval

    /// <summary>True when the scene may run by itself: switched on, valid, approved by the host as it is, and approved here with the same programs.</summary>
    internal bool IsArmed(SceneSettings scene)
    {
        if (!scene.Enabled) return false;
        return IsApproved(scene);
    }

    internal bool IsApproved(SceneSettings scene)
    {
        if (SceneRules.Check(scene) != SceneProblem.None) return false;
        var st = State(scene.Id);
        return st.Approval.IsApproved && scene.ApprovedFingerprint.Length > 0 && string.Equals(scene.ApprovedFingerprint, ScenePlans.Fingerprint(scene), StringComparison.Ordinal);
    }

    /// <summary>Asks the host about every scene's plan. Blocks for as long as the host runs the previews (a few seconds at most); call it from a task.</summary>
    private void RefreshApprovals()
    {
        foreach (var scene in Snapshot())
        {
            var st = State(scene.Id);
            PlanApproval answer;
            if (SceneRules.Check(scene) != SceneProblem.None) answer = new PlanApproval(PlanApprovalState.Unavailable, "the scene is not complete");
            else
            {
                try { answer = Verbs.GetApproval(ScenePlans.Build(scene)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { answer = new PlanApproval(PlanApprovalState.Unavailable, ex.Message); }
            }
            lock (_state) st.Approval = answer;
        }
    }

    /// <summary>Shows the owner the host's one dialog for this scene. On yes the scene is approved and, with <paramref name="enable"/>, switched on.</summary>
    private async Task ApproveAsync(string id, bool enable)
    {
        var scene = Find(id)?.Clone();
        if (scene is null) return;
        var st = State(id);
        lock (_state) { if (st.Approving) return; st.Approving = true; st.LastAnswer = null; }
        PlanApproval answer;
        try
        {
            if (SceneRules.Check(scene) != SceneProblem.None) answer = new PlanApproval(PlanApprovalState.Unavailable, "the scene is not complete");
            else answer = await Verbs.RequestApprovalAsync(ScenePlans.Build(scene), _host.StopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { answer = new PlanApproval(PlanApprovalState.Declined); }
        finally { lock (_state) st.Approving = false; }

        lock (_state)
        {
            st.LastAnswer = answer;
            st.Approval = answer;
            var live = _s.Scenes.FirstOrDefault(s => s.Id == id);
            if (live is not null)
            {
                if (answer.IsApproved)
                {
                    // the fingerprint of what the owner saw; if the scene was edited while the dialog was open it no longer fits and the scene asks again
                    live.ApprovedFingerprint = ScenePlans.Fingerprint(scene);
                    if (enable) live.Enabled = true;
                }
                else if (enable) live.Enabled = false;
                Save();
            }
        }
        _host.Log.Info($"scenes: {id}: approval answer {answer.State}{(answer.Reason.Length > 0 ? " (" + answer.Reason + ")" : "")}");
        AfterChange();
    }

    private void RevokeScene(string id)
    {
        try { Verbs.Revoke(id); } catch (Exception ex) when (ex is not OperationCanceledException) { _host.Log.Warn("scenes: revoke failed: " + ex.Message); }
        lock (_state)
        {
            var scene = _s.Scenes.FirstOrDefault(s => s.Id == id);
            if (scene is not null) { scene.Enabled = false; scene.ApprovedFingerprint = ""; Save(); }
            var st = State(id);
            st.Approval = new PlanApproval(PlanApprovalState.Revoked);
            st.Block = BlockKind.None;
        }
        AfterChange();
    }

    // ------------------------------------------------------------------ the poll

    /// <summary>
    /// The process list is read every few seconds ONLY while at least one scene is switched on and approved, or a run is open (to see the game end). With nothing armed
    /// there is no timer. Windows can tell a process start by a WMI event as well, but a poll is needed anyway to find a game that already runs when Rigbay starts, and
    /// a read of the processes by name every 5 s costs nothing worth a second mechanism.
    /// </summary>
    private bool NeedsPolling()
    {
        lock (_state) return _s.Scenes.Any(s => IsArmed(s) || State(s.Id).RunId is not null);
    }

    private void UpdatePolling()
    {
        if (_stopping || !_started) return;
        bool need = NeedsPolling();
        lock (_state)
        {
            if (need && _ticker is null) _ticker = _deps.Ticker.Every(_deps.Poll, OnTick);
            else if (!need && _ticker is not null) { _ticker.Dispose(); _ticker = null; }
        }
    }

    /// <summary>Whether the poll is running (tests).</summary>
    internal bool Polling { get { lock (_state) return _ticker is not null; } }

    private void OnTick()
    {
        if (_stopping || Interlocked.Exchange(ref _tickBusy, 1) == 1) return;
        Track(async () =>
        {
            try { if (_work.CurrentCount > 0) await TickAsync().ConfigureAwait(false); }
            finally { Volatile.Write(ref _tickBusy, 0); }
        }, "poll");
    }

    /// <summary>One look at the processes: starts the scenes whose game runs, ends (after the grace, newest first) the ones whose game is gone. Tests call it by hand.</summary>
    internal async Task TickAsync()
    {
        await _work.WaitAsync().ConfigureAwait(false);
        try { await TickLockedAsync().ConfigureAwait(false); }
        finally { _work.Release(); }
        AfterChange();
    }

    private async Task TickLockedAsync()
    {
        if (_stopping) return;
        var now = _deps.Now();
        var scenes = Snapshot();
        var wanted = scenes.Where(s => IsArmed(s) || State(s.Id).RunId is not null).ToList();
        foreach (var s in scenes) State(s.Id).WaitingFor = "";
        if (wanted.Count == 0) return;

        var names = wanted.SelectMany(s => s.Games.Select(Games.FileName)).Distinct().ToList();
        bool withPath = wanted.Any(s => s.Games.Any(Games.HasPath));
        IReadOnlyList<RunningProgram> found = Array.Empty<RunningProgram>();
        try { found = _deps.Processes.Find(names, withPath); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _host.Log.Warn("scenes: reading the process list failed: " + ex.Message); }

        string? GameOf(SceneSettings s) => found.Where(p => s.Games.Any(g => Games.Matches(g, p))).Select(p => p.Name).FirstOrDefault();

        // starts, in the order of the page
        foreach (var scene in wanted)
        {
            var st = State(scene.Id);
            string? game = GameOf(scene);
            if (st.RunId is null)
            {
                if (game is null) { if (st.Block != BlockKind.None) { st.Block = BlockKind.None; st.BlockDetail = ""; } continue; }
                if (st.Block == BlockKind.None && IsArmed(scene)) await StartSceneAsync(scene.Id, game, manual: false).ConfigureAwait(false);
            }
            else if (!st.Manual)
            {
                if (game is not null) { st.Missing = null; st.RunGame = game; if (st.Block == BlockKind.Stopped) st.Block = BlockKind.None; }
                else st.Missing ??= now;
            }
        }

        // ends: last started first. A run that is not due stops the loop, so an older run is never put back from under a newer one.
        var open = wanted.Select((s, index) => (Scene: s, Index: index)).Where(x => State(x.Scene.Id).RunId is not null && !State(x.Scene.Id).EndFailed)
            .OrderByDescending(x => State(x.Scene.Id).RunStarted).ThenByDescending(x => x.Index).Select(x => x.Scene).ToList();
        for (int i = 0; i < open.Count; i++)
        {
            var scene = open[i];
            var st = State(scene.Id);
            bool due = !st.Manual && st.Missing is { } since && now - since >= _deps.Grace;
            if (!due)
            {
                foreach (var older in open.Skip(i + 1)) if (State(older.Id).Missing is not null) State(older.Id).WaitingFor = scene.Name;
                break;
            }
            await EndSceneAsync(scene.Id, "the game ended").ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ runs

    private async Task StartSceneAsync(string id, string game, bool manual)
    {
        var scene = Find(id)?.Clone();
        if (scene is null) return;
        var st = State(id);
        PlanRun run;
        try { run = await Verbs.StartAsync(ScenePlans.Build(scene), _host.StopToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        var now = _deps.Now();
        switch (run.Outcome)
        {
            case PlanOutcome.Done:
                lock (_state)
                {
                    st.RunId = run.RunId; st.RunStarted = now; st.RunGame = game; st.Manual = manual; st.Missing = null;
                    st.EndFailed = false; st.Block = BlockKind.None; st.BlockDetail = ""; st.WaitingFor = "";
                }
                _host.Log.Info($"scenes: {id} started for {game} (run {run.RunId}, {run.Steps.Count} step(s))");
                return;
            case PlanOutcome.UndoFailed:
                // a step failed and the host could not put back what it had done: the run stays open at the host, for the owner to settle
                lock (_state)
                {
                    st.RunId = run.RunId; st.RunStarted = now; st.RunGame = game; st.Manual = true; st.Missing = null; st.EndFailed = true;
                    RecordLast(id, "undo-failed", run.Message, 0);
                }
                return;
            case PlanOutcome.AlreadyRunning:
                await ReconcileLockedAsync(OpenRunsNotKnown()).ConfigureAwait(false);   // the caller holds _work
                lock (_state) { if (st.RunId is null) SetBlock(st, BlockKind.Invalid, run.Message); }
                return;
        }

        var kind = run.Outcome switch
        {
            PlanOutcome.NeedsApproval => run.Message.Contains("(Changed)", StringComparison.Ordinal) ? BlockKind.Changed : BlockKind.NeedsApproval,
            PlanOutcome.RateLimited => BlockKind.RateLimited,
            PlanOutcome.TooManyRuns => BlockKind.TooManyRuns,
            PlanOutcome.Failed => BlockKind.Failed,
            PlanOutcome.Cancelled => BlockKind.Cancelled,
            _ => BlockKind.Invalid,
        };
        lock (_state)
        {
            SetBlock(st, kind, run.Message);
            RecordLast(id, kind == BlockKind.Failed ? "failed" : "refused", run.Message, 0);
        }
        _host.Log.Warn($"scenes: {id}: the host did not run the scene: {run.Outcome} {run.Message}");

        // The host already raised a notification for a failed run, a rate limit and a changed plan. These it does not announce, so the module does.
        if (kind is BlockKind.NeedsApproval or BlockKind.TooManyRuns or BlockKind.Invalid)
            _host.Toast.Show(new ToastMessage(LocalisedText.Of("plan.name", scene.Name), LocalisedText.Of("toast.not-run", Why(kind)), ToastKind.Warning));
        if (kind is BlockKind.NeedsApproval or BlockKind.Changed) await Task.Run(RefreshApprovals).ConfigureAwait(false);
    }

    private static void SetBlock(SceneState st, BlockKind kind, string detail) { st.Block = kind; st.BlockDetail = detail; }

    private string Why(BlockKind kind) => _host.Localisation.T("block." + kind.ToString().ToLowerInvariant());

    /// <summary>Ends the open run of a scene (the caller holds <c>_work</c>).</summary>
    private async Task EndSceneAsync(string id, string reason)
    {
        var st = State(id);
        string? runId = st.RunId;
        if (runId is null) return;
        PlanRun run;
        try { run = await Verbs.EndAsync(runId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { run = new PlanRun(runId, PlanOutcome.UndoFailed, [], ex.Message); }
        var now = _deps.Now();
        if (run.Outcome is PlanOutcome.Done or PlanOutcome.NotFound)
        {
            int seconds = (int)Math.Max(0, Math.Round((now - st.RunStarted).TotalSeconds));
            lock (_state)
            {
                st.RunId = null; st.Missing = null; st.EndFailed = false; st.Manual = false; st.WaitingFor = "";
                RecordLast(id, "ran", "", seconds);
            }
            _host.Log.Info($"scenes: {id} ended ({reason}), {run.Steps.Count(s => s.IsUndo)} step(s) put back");
        }
        else
        {
            lock (_state) { st.EndFailed = true; RecordLast(id, "undo-failed", run.Message, 0); }
            _host.Log.Warn($"scenes: {id}: putting back failed ({run.Outcome}) {run.Message}");
        }
    }

    private void RecordLast(string id, string kind, string detail, int seconds)
    {
        var scene = _s.Scenes.FirstOrDefault(s => s.Id == id);
        if (scene is null) return;
        scene.LastKind = kind; scene.LastAt = _deps.Now(); scene.LastDetail = detail; scene.LastSeconds = seconds;
        Save();
    }

    /// <summary>"Run now" (a test of the scene): the steps run once the way a game start would run them; only Stop (or turning it off) puts them back.</summary>
    private async Task RunNowAsync(string id)
    {
        await _work.WaitAsync().ConfigureAwait(false);
        try
        {
            var scene = Find(id)?.Clone();
            var st = State(id);
            if (scene is null || st.RunId is not null || !IsApproved(scene)) return;
            await StartSceneAsync(id, "manual", manual: true).ConfigureAwait(false);
        }
        finally { _work.Release(); }
        AfterChange();
    }

    /// <summary>"Stop": puts back the open run now. If the game still runs, the scene does not start again until the game has ended and started again.</summary>
    private async Task StopRunAsync(string id)
    {
        await _work.WaitAsync().ConfigureAwait(false);
        try
        {
            var st = State(id);
            if (st.RunId is null) return;
            bool gameRuns = !st.Manual && st.Missing is null;
            st.EndFailed = false;   // a retry
            await EndSceneAsync(id, "stopped by the owner").ConfigureAwait(false);
            if (st.RunId is null && gameRuns) lock (_state) SetBlock(st, BlockKind.Stopped, "");
        }
        finally { _work.Release(); }
        AfterChange();
    }

    private async Task DisableAsync(string id)
    {
        await _work.WaitAsync().ConfigureAwait(false);
        try
        {
            var st = State(id);
            lock (_state) { st.Block = BlockKind.None; st.BlockDetail = ""; }
            if (st.RunId is not null) { st.EndFailed = false; await EndSceneAsync(id, "the scene was switched off").ConfigureAwait(false); }
        }
        finally { _work.Release(); }
        AfterChange();
    }

    /// <summary>Removes a scene. A scene with an open run is put back first; if that fails the scene stays, so the run is never orphaned. Returns false then.</summary>
    private async Task<bool> DeleteAsync(string id)
    {
        await _work.WaitAsync().ConfigureAwait(false);
        try
        {
            var st = State(id);
            if (st.RunId is not null) { st.EndFailed = false; await EndSceneAsync(id, "the scene was deleted").ConfigureAwait(false); }
            if (st.RunId is not null) return false;
            try { Verbs.Revoke(id); } catch (Exception ex) when (ex is not OperationCanceledException) { _host.Log.Warn("scenes: revoke failed: " + ex.Message); }
            lock (_state) { _s.Scenes.RemoveAll(s => s.Id == id); _states.Remove(id); _views.Remove(id); Save(); }
            return true;
        }
        finally { _work.Release(); }
    }

    // ------------------------------------------------------------------ a Rigbay that restarted

    /// <summary>
    /// Reads the host's open runs of this module. A run of a scene whose game still runs is adopted (the scene goes on as if nothing happened); one whose game is gone is
    /// ended; one that no scene owns any more is ended. A run whose undo failed before is adopted as "could not put back" and left to the owner.
    /// </summary>
    private async Task ReconcileAsync(bool initial)
    {
        var runs = OpenRunsNotKnown();
        if (runs.Count == 0) return;
        if (initial) await WaitForTargetsAsync(runs).ConfigureAwait(false);
        await _work.WaitAsync().ConfigureAwait(false);
        try { await ReconcileLockedAsync(OpenRunsNotKnown()).ConfigureAwait(false); }
        finally { _work.Release(); }
    }

    /// <summary>The host's open runs of this module that no scene here has adopted yet.</summary>
    private IReadOnlyList<ActiveRun> OpenRunsNotKnown()
    {
        IReadOnlyList<ActiveRun> runs;
        try { runs = Verbs.ActiveRuns(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _host.Log.Warn("scenes: reading the open runs failed: " + ex.Message); return Array.Empty<ActiveRun>(); }
        lock (_state) return runs.Where(r => !_states.TryGetValue(r.PlanId, out var st) || st.RunId != r.RunId).ToList();
    }

    /// <summary>The adoption itself. The caller holds <c>_work</c>.</summary>
    private async Task ReconcileLockedAsync(IReadOnlyList<ActiveRun> runs)
    {
        foreach (var run in runs.OrderByDescending(r => r.StartedAt))
        {
            var scene = Find(run.PlanId)?.Clone();
            if (scene is null)
            {
                if (run.UndoFailed) { _host.Log.Warn($"scenes: run {run.RunId} belongs to no scene and could not be put back before; use `rigbay plans undo {run.RunId}`"); continue; }
                _host.Log.Info($"scenes: run {run.RunId} belongs to no scene any more; putting it back");
                try { await Verbs.EndAsync(run.RunId, CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { _host.Log.Warn("scenes: " + ex.Message); }
                continue;
            }
            var st = State(scene.Id);
            string? game = null;
            try { game = _deps.Processes.Find(scene.Games.Select(Games.FileName).Distinct().ToList(), scene.Games.Any(Games.HasPath)).Where(p => scene.Games.Any(g => Games.Matches(g, p))).Select(p => p.Name).FirstOrDefault(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _host.Log.Warn("scenes: reading the process list failed: " + ex.Message); }

            if (run.UndoFailed || game is not null)
            {
                lock (_state)
                {
                    st.RunId = run.RunId; st.RunStarted = run.StartedAt; st.RunGame = game ?? ""; st.Manual = false; st.Missing = null; st.EndFailed = run.UndoFailed;
                }
                _host.Log.Info($"scenes: {scene.Id}: adopted run {run.RunId} ({(run.UndoFailed ? "its undo failed before" : "the game still runs")})");
            }
            else
            {
                lock (_state) { st.RunId = run.RunId; st.RunStarted = run.StartedAt; st.Manual = false; }
                await EndSceneAsync(scene.Id, "the game was gone when Rigbay started").ConfigureAwait(false);
            }
        }
    }

    /// <summary>The modules start one after the other; an undo of a run left by the last Rigbay needs its modules. Waits (at most <see cref="Dependencies.TargetWait"/>) until they are listed.</summary>
    private async Task WaitForTargetsAsync(IReadOnlyList<ActiveRun> runs)
    {
        var needed = runs.SelectMany(r => r.Steps).Select(l => l.Split(' ', 2)[0]).Where(m => m.Length > 0).ToHashSet();
        var until = _deps.Now() + _deps.TargetWait;
        var step = TimeSpan.FromSeconds(2);
        while (!_stopping)
        {
            HashSet<string> listed;
            try { listed = Verbs.List().Select(v => v.ModuleId).ToHashSet(); } catch (Exception) { return; }
            if (needed.IsSubsetOf(listed) || _deps.Now() >= until) return;
            try { await _deps.Delay(step, _host.StopToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    // ------------------------------------------------------------------ events

    private void OnVerbsChanged()
    {
        if (_stopping) return;
        // a module started or stopped: the plans may be judgeable now (or not any more), and a scene refused because a module was missing may try at its next game start
        Track(async () =>
        {
            await Task.Run(RefreshApprovals).ConfigureAwait(false);
            lock (_state) foreach (var st in _states.Values) if (st.Block == BlockKind.Invalid) { st.Block = BlockKind.None; st.BlockDetail = ""; }
            AfterChange();
        }, "refresh after a module change");
    }

    private void OnPageVisible()
    {
        if (_stopping || !_host.PageVisible.Value) return;
        Track(async () =>
        {
            await Task.Run(RefreshApprovals).ConfigureAwait(false);
            RefreshPrograms();
            AfterChange();
        }, "refresh when the page opens");
    }

    private void OnLanguageChanged() { RefreshTexts(); _host.RequestPageRefresh(); }

    /// <summary>After anything changed: the poll follows the need, the page follows the state.</summary>
    private void AfterChange()
    {
        UpdatePolling();
        RefreshTexts();
    }
}
