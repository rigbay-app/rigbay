using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Scenes;

/// <summary>The values behind the rows of one scene. They outlive a page rebuild: the handlers are wired once, in <c>EnsureView</c>.</summary>
internal sealed class SceneView
{
    public SceneView(string id) => Id = id;

    public string Id { get; }
    public bool Expanded { get; set; }

    public ObservableValue<bool> Enabled { get; } = new(false);
    public ObservableValue<LocalisedText> Line { get; } = new(default);
    public ObservableValue<LocalisedText> Attention { get; } = new(default);
    public ObservableValue<bool> AttentionOpen { get; } = new(false);
    public ObservableValue<string> Last { get; } = new("");
    public ObservableValue<string> Name { get; } = new("");
    public ObservableValue<string> Games { get; } = new("");
    public ObservableValue<string> PickGame { get; } = new("");
    public ObservableValue<bool> CanApprove { get; } = new(false);
    public ObservableValue<bool> CanRevoke { get; } = new(false);
    public ObservableValue<bool> CanRun { get; } = new(false);
    public ObservableValue<bool> CanStop { get; } = new(false);

    // the step being added
    public ObservableValue<string> AddModule { get; } = new("");
    public ObservableValue<string> AddVerb { get; } = new("");
    public ObservableValue<string> AddArgs { get; } = new("");
    public ObservableValue<string> AddUndo { get; } = new(UndoMode.None);
    public ObservableValue<string> AddUndoLine { get; } = new("");
    public ObservableValue<LocalisedText> AddInfo { get; } = new(default);
    public ObservableValue<bool> AddUndoLineEnabled { get; } = new(false);

    /// <summary>The "move / remove" choices of the steps in the page that is shown now (rebuilt with the page).</summary>
    public List<ObservableValue<string>> StepActions { get; } = new();
}

internal sealed partial class ScenesRuntime
{
    private readonly Dictionary<string, SceneView> _views = new();
    private ObservableValue<LocalisedText> _summary = null!;
    private IReadOnlyList<string> _programs = Array.Empty<string>();
    private string? _builtKey;

    private void CreateRows()
    {
        _summary = new(LocalisedText.Of("summary.none"));
    }

    // ------------------------------------------------------------------ views

    /// <summary>Makes the values of a scene's rows and wires what the user's edits do. Call with <c>_state</c> held (or before the page exists).</summary>
    private SceneView EnsureView(SceneSettings scene)
    {
        if (_views.TryGetValue(scene.Id, out var existing)) return existing;
        var view = new SceneView(scene.Id);
        _views[scene.Id] = view;
        string id = scene.Id;

        view.Enabled.Changed += () =>
        {
            if (_own) return;
            if (!view.Enabled.Value) { Track(() => DisableAsync(id), "switching a scene off"); lock (_state) { var s = Find(id); if (s is not null) { s.Enabled = false; Save(); } } return; }
            var scene = Find(id)?.Clone();
            if (scene is null) return;
            if (IsApproved(scene)) { lock (_state) { var s = Find(id); if (s is not null) { s.Enabled = true; Save(); } } Track(() => { AfterChange(); return Task.CompletedTask; }, "switching a scene on"); }
            else Track(() => ApproveAsync(id, enable: true), "approval of a scene");
        };

        view.Name.Changed += () =>
        {
            if (_own) return;
            lock (_state)
            {
                var s = Find(id);
                if (s is null) return;
                s.Name = view.Name.Value.Trim();
                if (s.Name.Length > SceneRules.MaxNameLength) s.Name = s.Name[..SceneRules.MaxNameLength];
                Save();
            }
            RefreshTexts();
            _host.RequestPageRefresh();
        };

        view.Games.Changed += () =>
        {
            if (_own) return;
            var parsed = ParseGames(view.Games.Value, out string bad);
            var st = State(id);
            lock (_state)
            {
                st.BadInput = bad;
                var s = Find(id);
                if (s is null) return;
                if (parsed is not null) { s.Games = parsed; Save(); }
            }
            AfterChange();
        };

        view.PickGame.Changed += () =>
        {
            if (_own || view.PickGame.Value.Length == 0) return;
            string pick = view.PickGame.Value;
            SetOwn(view.PickGame, "");
            lock (_state)
            {
                var s = Find(id);
                if (s is null) return;
                var all = s.Games.ToList();
                if (Games.Normalise(pick) is { } n && !all.Contains(n, StringComparer.OrdinalIgnoreCase) && all.Count < SceneRules.MaxGames) { all.Add(n); s.Games = all; Save(); }
                SetOwn(view.Games, string.Join(", ", s.Games));
                State(id).BadInput = "";
            }
            AfterChange();
        };

        view.AddModule.Changed += () => { if (_own) return; view.Expanded = true; SetOwn(view.AddVerb, ""); ResetDraft(view); _host.RequestPageRefresh(); };
        view.AddVerb.Changed += () =>
        {
            if (_own) return;
            view.Expanded = true;
            ChooseDefaultUndo(view);
            _host.RequestPageRefresh();
        };
        view.AddUndo.Changed += () => { if (_own) return; view.AddUndoLineEnabled.Value = view.AddUndo.Value == UndoMode.Fixed; };

        return view;
    }

    /// <summary>Tests: the values behind one scene's rows.</summary>
    internal SceneView ViewOf(string id) { lock (_state) return _views[id]; }

    /// <summary>Tests: copies of the scenes as stored now.</summary>
    internal void SceneSnapshot(out List<SceneSettings> scenes) => scenes = Snapshot();

    private static List<string>? ParseGames(string text, out string bad)
    {
        bad = "";
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Games.Normalise(raw) is not { } n) { bad = raw; return null; }
            if (!result.Contains(n, StringComparer.OrdinalIgnoreCase)) result.Add(n);
        }
        if (result.Count > SceneRules.MaxGames) { bad = $">{SceneRules.MaxGames}"; return null; }
        return result;
    }

    private static void SetOwn<T>(ObservableValue<T> value, T next)
    {
        bool before = _own;
        _own = true;
        try { value.Value = next; }
        finally { _own = before; }
    }

    private void ResetDraft(SceneView view)
    {
        SetOwn(view.AddArgs, "");
        SetOwn(view.AddUndo, UndoMode.None);
        SetOwn(view.AddUndoLine, "");
        view.AddUndoLineEnabled.Value = false;
        view.AddInfo.Value = default;
    }

    /// <summary>When a command is chosen: its own undo if it has one, else the usual opposite (on to off), else none. The owner can change it.</summary>
    private void ChooseDefaultUndo(SceneView view)
    {
        SetOwn(view.AddArgs, "");
        SetOwn(view.AddUndoLine, "");
        var info = SelectedVerb(view);
        string mode = info?.UndoVerb is not null ? UndoMode.Recorded : info is not null && UndoGuess.For(info.ModuleId, info.Verb, Array.Empty<string>()) is not null ? UndoMode.Fixed : UndoMode.None;
        SetOwn(view.AddUndo, mode);
        view.AddUndoLineEnabled.Value = mode == UndoMode.Fixed;
        if (mode == UndoMode.Fixed && info is not null) SetOwn(view.AddUndoLine, UndoGuess.For(info.ModuleId, info.Verb, Array.Empty<string>()) ?? "");
        view.AddInfo.Value = info is null ? default : new LocalisedText(Text.Literal(DescribeVerb(info)));
    }

    private VerbInfo? SelectedVerb(SceneView view)
    {
        string module = view.AddModule.Value, verb = view.AddVerb.Value;
        if (module.Length == 0 || verb.Length == 0) return null;
        return PlanVerbs().FirstOrDefault(v => v.ModuleId == module && v.Verb == verb);
    }

    /// <summary>The verbs a scene can use: those that change something, or did not say (a read-only verb has nothing to approve).</summary>
    private List<VerbInfo> PlanVerbs()
    {
        try { return Verbs.List().Where(v => v.Access != VerbAccess.ReadOnly && v.ModuleId != SceneRules.OwnModule).ToList(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _host.Log.Warn("scenes: listing the verbs failed: " + ex.Message); return new(); }
    }

    private string DescribeVerb(VerbInfo v)
    {
        var loc = _host.Localisation;
        var parts = new List<string> { v.Description };
        if (!string.IsNullOrWhiteSpace(v.Usage)) parts.Add(loc.F("verb.usage", v.Usage!));
        parts.Add(v.Access == VerbAccess.Writes && v.EffectText is not null ? loc.F("verb.changes", v.EffectText) : loc.T("verb.unknown-effect"));
        parts.Add(v.UndoVerb is not null ? loc.F("verb.undo", v.UndoVerb) : loc.T("verb.no-undo"));
        parts.Add(loc.T(v.HasPreview ? "verb.preview" : "verb.no-preview"));
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ the texts on the page

    private void LoadRows()
    {
        lock (_state)
            foreach (var scene in _s.Scenes)
            {
                var view = EnsureView(scene);
                SetOwn(view.Name, scene.Name);
                SetOwn(view.Games, string.Join(", ", scene.Games));
                SetOwn(view.Enabled, scene.Enabled);
            }
    }

    private string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    private string Duration(int seconds)
    {
        var loc = _host.Localisation;
        if (seconds < 60) return loc.F("duration.s", Math.Max(1, seconds));
        int minutes = seconds / 60;
        return minutes < 60 ? loc.F("duration.min", minutes) : loc.F("duration.h", minutes / 60, minutes % 60);
    }

    private string When(DateTimeOffset t) => t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private LocalisedText LineOf(SceneSettings scene, SceneState st)
    {
        if (st.RunId is not null)
        {
            if (st.EndFailed) return LocalisedText.Of("line.undo-failed");
            if (st.Manual) return LocalisedText.Of("line.running-manual", Clock(st.RunStarted));
            if (st.WaitingFor.Length > 0) return LocalisedText.Of("line.waiting", st.WaitingFor);
            if (st.Missing is not null) return LocalisedText.Of("line.closing", st.RunGame);
            return LocalisedText.Of("line.running", Clock(st.RunStarted), st.RunGame);
        }
        if (!scene.Enabled) return LocalisedText.Of("line.off");
        var problem = SceneRules.Check(scene);
        if (problem != SceneProblem.None) return LocalisedText.Of("line.incomplete", _host.Localisation.T(SceneRules.ProblemKey(problem)));
        if (!IsApproved(scene))
        {
            return st.Approval.State switch
            {
                PlanApprovalState.Changed => LocalisedText.Of("line.changed", st.Approval.Reason),
                PlanApprovalState.Untrusted => LocalisedText.Of("line.untrusted", st.Approval.Reason),
                PlanApprovalState.Unavailable => LocalisedText.Of("line.unavailable", st.Approval.Reason),
                _ => LocalisedText.Of("line.needs-approval"),
            };
        }
        if (st.Block != BlockKind.None) return LocalisedText.Of("line.blocked", Why(st.Block));
        return LocalisedText.Of("line.armed", string.Join(", ", scene.Games));
    }

    private LocalisedText AttentionOf(SceneSettings scene, SceneState st)
    {
        if (st.BadInput.Length > 0) return LocalisedText.Of("attention.bad-game", st.BadInput);
        if (st.RunId is not null && st.EndFailed) return LocalisedText.Of("attention.undo-failed", st.RunId, scene.LastDetail);
        if (st.Block != BlockKind.None && st.Block != BlockKind.Stopped) return LocalisedText.Of("attention.blocked", Why(st.Block), st.BlockDetail);
        if (st.Block == BlockKind.Stopped) return LocalisedText.Of("attention.stopped");
        var problem = SceneRules.Check(scene);
        if (problem != SceneProblem.None) return LocalisedText.Of("attention.problem", _host.Localisation.T(SceneRules.ProblemKey(problem)));
        if (st.LastAnswer is { IsApproved: false } a)
            return a.State switch
            {
                PlanApprovalState.Declined => LocalisedText.Of("attention.declined"),
                PlanApprovalState.Untrusted => LocalisedText.Of("attention.untrusted", a.Reason),
                PlanApprovalState.Unavailable => LocalisedText.Of("attention.unavailable", a.Reason),
                _ => default,
            };
        return default;
    }

    private string LastOf(SceneSettings scene)
    {
        var loc = _host.Localisation;
        if (scene.LastAt is not { } at || scene.LastKind.Length == 0) return loc.T("last.none");
        return scene.LastKind switch
        {
            "ran" => loc.F("last.ran", When(at), Duration(scene.LastSeconds)),
            "failed" => loc.F("last.failed", When(at), scene.LastDetail),
            "refused" => loc.F("last.refused", When(at), scene.LastDetail),
            "undo-failed" => loc.F("last.undo-failed", When(at), scene.LastDetail),
            _ => loc.T("last.none"),
        };
    }

    /// <summary>Brings the values on the page up to date with the state and asks for a new page when the lists on it changed.</summary>
    private void RefreshTexts()
    {
        List<(SceneSettings Scene, SceneState St, SceneView View)> items;
        lock (_state)
        {
            items = new();
            foreach (var scene in _s.Scenes.ToList())
                items.Add((scene.Clone(), State(scene.Id), EnsureView(scene)));
        }

        int armed = 0, running = 0;
        foreach (var (scene, st, view) in items)
        {
            bool approved = IsApproved(scene);
            bool valid = SceneRules.Check(scene) == SceneProblem.None;
            if (scene.Enabled && approved) armed++;
            if (st.RunId is not null) running++;
            view.Line.Value = LineOf(scene, st);
            var attention = AttentionOf(scene, st);
            view.Attention.Value = attention;
            view.AttentionOpen.Value = !attention.IsEmpty;
            view.Last.Value = LastOf(scene);
            if (!st.Approving) SetOwn(view.Enabled, scene.Enabled);
            view.CanApprove.Value = valid && !st.Approving && !approved;
            view.CanRevoke.Value = scene.ApprovedFingerprint.Length > 0 || st.Approval.State is PlanApprovalState.Approved;
            view.CanRun.Value = approved && st.RunId is null && !st.Approving;
            view.CanStop.Value = st.RunId is not null;
        }
        _summary.Value = items.Count == 0 ? LocalisedText.Of("summary.none")
            : running > 0 ? LocalisedText.Of("summary.running", running, items.First(i => i.St.RunId is not null).Scene.Name)
            : armed > 0 ? LocalisedText.Of("summary.armed", armed, items.Count) : LocalisedText.Of("summary.idle", items.Count);

        if (_builtKey is not null && _builtKey != PageKey()) _host.RequestPageRefresh();
    }

    /// <summary>What the shape of the page depends on: the scenes and their steps, the lists of modules and commands, the programs.</summary>
    private string PageKey()
    {
        var parts = new List<string>();
        lock (_state)
            foreach (var s in _s.Scenes)
            {
                var v = _views.GetValueOrDefault(s.Id);
                parts.Add(s.Id + "|" + s.Name + "|" + string.Join(">", s.Steps.Select(x => ScenePlans.Line(x) + "/" + x.Undo + "/" + ScenePlans.UndoLine(x))) + "|" + v?.AddModule.Value + "|" + v?.AddVerb.Value + "|" + v?.AttentionOpen.Value);
            }
        var verbs = PlanVerbs();
        parts.Add(string.Join(",", verbs.Select(x => x.ModuleId + "/" + x.Verb)));
        parts.Add(string.Join(",", _programs));
        return string.Join("\n", parts);
    }

    private void RefreshPrograms()
    {
        try { _programs = _deps.Processes.WindowedPrograms(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _host.Log.Warn("scenes: listing the programs failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ steps

    private ActionResult AddStep(SceneView view)
    {
        string module = view.AddModule.Value, verb = view.AddVerb.Value;
        var info = SelectedVerb(view);
        if (info is null) return ActionResult.Fail("err.pick-command");
        var args = CommandLine.Split(view.AddArgs.Value);
        if (args is null || args.Count > SceneRules.MaxArgs || args.Any(a => a.Length > SceneRules.MaxArgLength)) return ActionResult.Fail("err.bad-args");

        var step = new StepSettings { Module = module, Verb = verb, Args = args, Undo = view.AddUndo.Value };
        switch (step.Undo)
        {
            case UndoMode.Recorded:
                if (info.UndoVerb is null) return ActionResult.Fail("err.no-recorded-undo");
                break;
            case UndoMode.Fixed:
            {
                string text = view.AddUndoLine.Value;
                if (string.IsNullOrWhiteSpace(text)) text = UndoGuess.For(module, verb, args) ?? "";
                var words = CommandLine.Split(text);
                if (words is null || words.Count < 2 || !SceneRules.IsStep(words[0], words[1], words.Skip(2).ToList())) return ActionResult.Fail("err.bad-undo");
                step.UndoModule = words[0]; step.UndoVerb = words[1]; step.UndoArgs = words.Skip(2).ToList();
                break;
            }
            default: step.Undo = UndoMode.None; break;
        }

        lock (_state)
        {
            var scene = Find(view.Id);
            if (scene is null) return ActionResult.Fail("err.gone");
            if (scene.Steps.Count >= SceneRules.MaxSteps) return ActionResult.Fail("err.too-many-steps");
            scene.Steps.Add(step);
            Save();
        }
        view.Expanded = true;
        ResetDraft(view);
        AfterChange();
        _host.RequestPageRefresh();
        return ActionResult.Ok(LocalisedText.Of("msg.step-added", ScenePlans.Line(step)));
    }

    private void StepAction(SceneView view, int index, string action)
    {
        lock (_state)
        {
            var scene = Find(view.Id);
            if (scene is null || index < 0 || index >= scene.Steps.Count) return;
            var steps = scene.Steps;
            switch (action)
            {
                case "up" when index > 0: (steps[index - 1], steps[index]) = (steps[index], steps[index - 1]); break;
                case "down" when index < steps.Count - 1: (steps[index + 1], steps[index]) = (steps[index], steps[index + 1]); break;
                case "remove": steps.RemoveAt(index); break;
                default: return;
            }
            Save();
        }
        view.Expanded = true;
        AfterChange();
        _host.RequestPageRefresh();
    }

    private string UndoWords(StepSettings s) => s.Undo switch
    {
        UndoMode.Recorded => _host.Localisation.T("step.undo-recorded"),
        UndoMode.Fixed => _host.Localisation.F("step.undo-fixed", ScenePlans.UndoLine(s)),
        _ => _host.Localisation.T("step.undo-none"),
    };

    // ------------------------------------------------------------------ scenes

    private void AddScene()
    {
        lock (_state)
        {
            var taken = _s.Scenes.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var scene = new SceneSettings { Id = NewId(taken), Name = _host.Localisation.F("default-name", _s.Scenes.Count + 1) };
            _s.Scenes.Add(scene);
            var view = EnsureView(scene);
            view.Expanded = true;
            SetOwn(view.Name, scene.Name);
            Save();
        }
        AfterChange();
        _host.RequestPageRefresh();
    }

    // ------------------------------------------------------------------ the page

    public PageModel BuildPage()
    {
        var loc = _host.Localisation;
        _builtKey = null;
        RefreshTexts();
        List<SceneSettings> scenes;
        lock (_state) scenes = _s.Scenes.Select(s => s.Clone()).ToList();
        var verbs = PlanVerbs();

        var modules = verbs.GroupBy(v => v.ModuleId).Select(g => (Id: g.Key, Name: g.First().ModuleName)).OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        string Lit(string text) => Text.Literal(text);

        var rows = new List<Row>
        {
            new InfoRow(Severity.Info, "info.how.header") { DescriptionKey = "info.how.description", HelpKey = "info.how.help" },
        };
        if (scenes.Count == 0) rows.Add(new InfoRow(Severity.Info, "info.empty.header") { DescriptionKey = "info.empty.description" });

        foreach (var scene in scenes)
        {
            SceneView view;
            lock (_state) view = EnsureView(scene);
            view.StepActions.Clear();

            var children = new List<Row>();
            if (view.AttentionOpen.Value) children.Add(new InfoRow(Severity.Warning, "info.attention.header") { Line = view.Attention, IsOpen = view.AttentionOpen });
            children.AddRange(new Row[]
            {
                new ValueRow("row.last.header", view.Last),
                new TextRow("row.name.header", view.Name) { MaxLength = SceneRules.MaxNameLength },
                new TextRow("row.games.header", view.Games) { PlaceholderKey = "row.games.placeholder", HelpKey = "row.games.help" },
            });

            var programs = _programs.Where(p => !scene.Games.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            var pick = new List<ChoiceOption> { new("", "opt.pick-program") };
            pick.AddRange(programs.Select(p => new ChoiceOption(p, Lit(p))));
            children.Add(new ChoiceRow("row.pick.header", pick, view.PickGame) { HelpKey = "row.pick.help" });
            children.Add(new ButtonRow("row.refresh.header", "button.refresh", _ => { Track(() => { RefreshPrograms(); AfterChange(); _host.RequestPageRefresh(); }, "refreshing the programs"); return Task.FromResult(ActionResult.Ok()); }));

            for (int i = 0; i < scene.Steps.Count; i++)
            {
                int index = i;
                var step = scene.Steps[i];
                var action = new ObservableValue<string>("keep");
                view.StepActions.Add(action);
                action.Changed += () =>
                {
                    if (_own) return;
                    string chosen = action.Value;
                    SetOwn(action, "keep");
                    if (chosen != "keep") StepAction(view, index, chosen);
                };
                children.Add(new ChoiceRow(Lit($"{i + 1}. {ScenePlans.Line(step)}"), new[]
                {
                    new ChoiceOption("keep", "opt.step-keep"), new ChoiceOption("up", "opt.step-up"), new ChoiceOption("down", "opt.step-down"), new ChoiceOption("remove", "opt.step-remove"),
                }, action) { Line = new ObservableValue<LocalisedText>(new LocalisedText(Lit(UndoWords(step)))) });
            }

            var moduleOptions = new List<ChoiceOption> { new("", "opt.pick-module") };
            moduleOptions.AddRange(modules.Select(m => new ChoiceOption(m.Id, Lit($"{m.Name} ({m.Id})"))));
            var verbOptions = new List<ChoiceOption> { new("", "opt.pick-command") };
            verbOptions.AddRange(verbs.Where(v => v.ModuleId == view.AddModule.Value).Select(v => new ChoiceOption(v.Verb, Lit($"{v.Verb} · {v.Description}"))));
            var undoOptions = new List<ChoiceOption> { new(UndoMode.None, "opt.undo-none") };
            var chosenVerb = SelectedVerb(view);
            if (chosenVerb?.UndoVerb is not null) undoOptions.Add(new ChoiceOption(UndoMode.Recorded, "opt.undo-recorded"));
            undoOptions.Add(new ChoiceOption(UndoMode.Fixed, "opt.undo-fixed"));
            view.AddUndoLineEnabled.Value = view.AddUndo.Value == UndoMode.Fixed;

            children.Add(new ChoiceRow("row.add-module.header", moduleOptions, view.AddModule));
            children.Add(new ChoiceRow("row.add-command.header", verbOptions, view.AddVerb) { Line = view.AddInfo, Enabled = new ObservableValue<bool>(view.AddModule.Value.Length > 0) });
            children.Add(new TextRow("row.add-args.header", view.AddArgs) { PlaceholderKey = "row.add-args.placeholder", HelpKey = "row.add-args.help" });
            children.Add(new ChoiceRow("row.add-undo.header", undoOptions, view.AddUndo) { HelpKey = "row.add-undo.help" });
            children.Add(new TextRow("row.add-undo-line.header", view.AddUndoLine) { PlaceholderKey = "row.add-undo-line.placeholder", Enabled = view.AddUndoLineEnabled });
            children.Add(new ButtonRow("row.add-step.header", "button.add-step", _ => Task.FromResult(AddStep(view))) { DescriptionKey = "row.add-step.description" });

            string id = scene.Id;
            children.Add(new ButtonRow("row.approve.header", "button.approve", async ct =>
            {
                await ApproveAsync(id, enable: false).ConfigureAwait(false);
                var st = State(id);
                return st.LastAnswer is { IsApproved: true } ? ActionResult.Ok(LocalisedText.Of("msg.approved")) : ActionResult.Fail("msg.not-approved");
            }) { Enabled = view.CanApprove, DescriptionKey = "row.approve.description" });
            children.Add(new ButtonRow("row.run.header", "button.run", async ct =>
            {
                await RunNowAsync(id).ConfigureAwait(false);
                var st = State(id);
                return st.RunId is not null ? ActionResult.Ok(LocalisedText.Of("msg.running")) : ActionResult.Fail("msg.not-run", Why(st.Block == BlockKind.None ? BlockKind.Invalid : st.Block));
            }) { Enabled = view.CanRun, DescriptionKey = "row.run.description" });
            children.Add(new ButtonRow("row.stop.header", "button.stop", async ct => { await StopRunAsync(id).ConfigureAwait(false); return State(id).RunId is null ? ActionResult.Ok(LocalisedText.Of("msg.stopped")) : ActionResult.Fail("msg.not-stopped"); })
                { Enabled = view.CanStop, DescriptionKey = "row.stop.description" });
            children.Add(new ButtonRow("row.revoke.header", "button.revoke", async ct => { await DisableAsync(id).ConfigureAwait(false); RevokeScene(id); return ActionResult.Ok(LocalisedText.Of("msg.revoked")); })
                { Enabled = view.CanRevoke, DescriptionKey = "row.revoke.description" });
            children.Add(new ButtonRow("row.delete.header", "button.delete", async ct => await DeleteAsync(id).ConfigureAwait(false) ? Done() : ActionResult.Fail("err.cannot-delete")) { ConfirmKey = "confirm.delete" });

            rows.Add(new ExpanderRow(Lit(string.IsNullOrWhiteSpace(scene.Name) ? scene.Id : scene.Name), children)
            {
                IsExpanded = view.Expanded,
                Line = view.Line,
                HeaderRow = new ToggleRow("row.enabled.header", view.Enabled),
            });
        }

        _builtKey = PageKey();
        RefreshTexts();
        return new PageModel
        {
            Primary = new ButtonRow("row.add.header", "button.add", _ => { AddScene(); return Task.FromResult(ActionResult.Ok()); }) { DescriptionKey = "row.add.description" },
            Groups = new[] { new RowGroup("group.scenes", rows) },
            Summary = new PageSummary(_summary),
        };

        ActionResult Done() { _host.RequestPageRefresh(); AfterChange(); return ActionResult.Ok(); }
    }
}
