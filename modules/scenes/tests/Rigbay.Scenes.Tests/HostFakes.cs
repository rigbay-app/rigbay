using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Scenes.Tests;

// ------------------------------------------------------------------ host fakes (copied in shape from the other modules' tests)

internal sealed class FakeBus : IMetricsBus
{
    public void Publish(MetricId id, double value, MetricUnit unit) { }
    public void Withdraw(MetricId id) { }
    public void Describe(MetricDescriptor descriptor) { }
    public int SubscriberCount(MetricId id) => 0;
    public event Action? SubscribersChanged { add { } remove { } }
    public IDisposable Subscribe(Action<MetricSample> handler) => new Nop();
    public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => new Nop();
    public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
    public IReadOnlyList<MetricSample> GetLatest() => Array.Empty<MetricSample>();
    private sealed class Nop : IDisposable { public void Dispose() { } }
}

/// <summary>Reads the module's real en.lang or vi.lang, so a key mistake shows up as the key text.</summary>
internal sealed class FakeLocalisation : ILocalisation
{
    private const string Prefix = "module.scenes.";
    private readonly Dictionary<string, string> _english;
    private Dictionary<string, string> _current;

    public FakeLocalisation(string language = "en")
    {
        _english = Load("en");
        _current = language == "en" ? _english : Load(language);
        Language = language;
    }

    public string Language { get; private set; }
    public event Action? LanguageChanged;

    public void Switch(string language)
    {
        _current = language == "en" ? _english : Load(language);
        Language = language;
        LanguageChanged?.Invoke();
    }

    public static Dictionary<string, string> Load(string language)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "lang", language + ".lang")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '@') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            d[line[..eq].Trim()] = Unescape(line[(eq + 1)..].Trim());
        }
        return d;
    }

    /// <summary>The host's rule: <c>\n</c> is a line break, <c>\t</c> a tab, <c>\X</c> is X.</summary>
    public static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { char c = s[++i]; sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c); }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public bool Has(string key) => Text.IsLiteral(key) || _english.ContainsKey(Prefix + key);

    public string T(string key)
    {
        if (Text.IsLiteral(key)) return Text.Unwrap(key);
        return _current.TryGetValue(Prefix + key, out var v) ? v : _english.TryGetValue(Prefix + key, out var e) ? e : Prefix + key;
    }

    public string F(string key, params object[] args) => Text.IsLiteral(key) && args.Length == 0 ? Text.Unwrap(key) : string.Format(CultureInfo.InvariantCulture, T(key), args);
    public string Resolve(LocalisedText text) => text.IsEmpty ? "" : text.Args is { Length: > 0 } ? F(text.Key, text.Args) : T(text.Key);
}

internal sealed class FakeLog : ILog
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public void Write(LogLevel level, string message, Exception? exception = null) { lock (Entries) Entries.Add((level, message + (exception is null ? "" : " | " + exception.GetType().Name + ": " + exception.Message))); }
    public string All() { lock (Entries) return string.Join("\n", Entries.Select(e => e.Message)); }
}

internal sealed class FakeSettings : ISettingsStore
{
    // Like the real store: what is saved is a copy, and Load hands out a fresh object every time.
    public string? Json;
    public int Saves;
    public T Load<T>() where T : class, new() => Json is null ? new T() : System.Text.Json.JsonSerializer.Deserialize<T>(Json) ?? new T();
    public T Load<T>(out int storedVersion) where T : class, new() { storedVersion = Json is null ? 0 : 1; return Load<T>(); }
    public void Save<T>(T value, int version = 1) where T : class { lock (this) { Json = System.Text.Json.JsonSerializer.Serialize(value); Saves++; } }
    public ScenesSettings Current { get { lock (this) return Load<ScenesSettings>(); } }
}

/// <summary>Nothing the module does goes through the host's single-question approval: a scene is approved by the plan dialog. Any question is a bug.</summary>
internal sealed class FakeApproval : IHardwareApproval
{
    public int Asked;
    public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Asked);
        return Task.FromResult(false);
    }
}

internal sealed class FakeHotkeys : IHotkeyService
{
    public readonly List<HotkeyRegistration> Registered = new();
    public IHotkeyBinding Register(HotkeyRegistration registration) { Registered.Add(registration); throw new NotSupportedException("the module registers no hotkey"); }
}

internal sealed class FakeCommands : ICommandRegistry
{
    public readonly Dictionary<string, (string DescriptionKey, CommandHandler Handler, CommandInfo? Info)> Verbs = new();

    public void Register(string verb, string descriptionKey, CommandHandler handler)
    {
        if (Verbs.ContainsKey(verb)) throw new ArgumentException("duplicate verb");
        Verbs[verb] = (descriptionKey, handler, null);
    }

    public void Register(CommandInfo info, CommandHandler handler)
    {
        if (Verbs.ContainsKey(info.Verb)) throw new ArgumentException("duplicate verb");
        if (info.Access == VerbAccess.Writes && info.Effect is null) throw new ArgumentException("a write needs an effect");
        Verbs[info.Verb] = (info.DescriptionKey, handler, info);
    }

    public Task<CommandResult> Run(string verb, params string[] args) => Verbs[verb].Handler(new CommandContext(args, CancellationToken.None));
}

internal sealed class FakeToast : IToastService
{
    public readonly List<ToastMessage> Shown = new();
    public void Show(ToastMessage message) { lock (Shown) Shown.Add(message); }
    public ToastMessage[] All() { lock (Shown) return Shown.ToArray(); }
}

internal sealed class FakeTray : ITrayService
{
    public readonly List<TrayItem> Items = new();
    public void Add(TrayItem item) => Items.Add(item);
    public void Remove(string id) => Items.RemoveAll(i => i.Id == id);
}

internal sealed class FakeHost : IHostServices
{
    private readonly CancellationTokenSource _stop = new();

    public FakeHost(FakeVerbs verbs, string language = "en")
    {
        VerbService = verbs;
        Localisation = new FakeLocalisation(language);
    }

    public readonly FakeBus MetricsBus = new();
    public readonly FakeLog LogSink = new();
    public readonly FakeSettings SettingsStore = new();
    public readonly FakeApproval ApprovalService = new();
    public readonly FakeHotkeys HotkeyService = new();
    public readonly FakeCommands CommandRegistry = new();
    public readonly FakeToast ToastService = new();
    public readonly FakeTray TrayService = new();
    public readonly FakeVerbs VerbService;
    public readonly ObservableValue<bool> PageVisibleValue = new(false);
    public int PageRefreshRequests;
    public FakeLocalisation Loc => (FakeLocalisation)Localisation;

    public string ModuleId => "scenes";
    public string ModuleDirectory => AppContext.BaseDirectory;
    public string DataDirectory => Path.GetTempPath();
    public Version HostVersion { get; } = new(0, 1, 4);
    public CancellationToken StopToken => _stop.Token;
    public ISettingsStore Settings => SettingsStore;
    public ILocalisation Localisation { get; }
    public ILog Log => LogSink;
    public IHotkeyService Hotkeys => HotkeyService;
    public ITrayService Tray => TrayService;
    public ICommandRegistry Commands => CommandRegistry;
    public IToastService Toast => ToastService;
    public IMetricsBus Metrics => MetricsBus;
    public IHardwareApproval Approval => ApprovalService;
    public IObservableValue<bool> PageVisible => PageVisibleValue;
    public IVerbService Verbs => VerbService;
    public void RunOnUi(Action action) => action();
    public void RequestPageRefresh() => Interlocked.Increment(ref PageRefreshRequests);
}

// ------------------------------------------------------------------ the host's plan engine, as a fake

/// <summary>
/// Behaves like the host's <c>PlanEngine</c> where the module can see it: an approval is the hash of the plan, a start needs the same hash, a plan has one open run, at most four
/// runs are open, and every call is recorded in <see cref="Calls"/> in the order it happened. Every outcome the host can answer can be forced.
/// </summary>
internal sealed class FakeVerbs : IVerbService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _approved = new();
    private readonly Dictionary<string, FakeRun> _open = new();
    private int _counter;

    public sealed class FakeRun
    {
        public string RunId = "";
        public string PlanId = "";
        public DateTimeOffset StartedAt;
        public bool Interrupted, UndoFailed;
        public List<string> Steps = new();
    }

    public readonly List<string> Calls = new();
    public readonly List<VerbPlan> Asked = new();
    public readonly List<VerbPlan> Started = new();
    public readonly List<string> Ended = new();
    public readonly List<string> Revoked = new();
    public List<VerbInfo> Infos = new();

    /// <summary>The answer to the next question (default yes). A test sets it.</summary>
    public Func<VerbPlan, PlanApprovalState> Answer = _ => PlanApprovalState.Approved;
    /// <summary>Runs while the question is open (a test edits something then).</summary>
    public Action? OnAsk;
    /// <summary>When set, StartAsync answers this instead of running (RateLimited, TooManyRuns, ...).</summary>
    public Func<VerbPlan, PlanRun?>? ForceStart;
    /// <summary>When set, EndAsync answers this outcome instead of closing the run.</summary>
    public PlanOutcome? ForceEnd;
    /// <summary>When set, EndAsync answers this outcome for the runs it names.</summary>
    public Func<string, PlanOutcome?>? ForceEndFor;
    /// <summary>The state GetApproval reports even for a plan that matches (Untrusted, Unavailable).</summary>
    public PlanApproval? ForceApproval;
    public DateTimeOffset Now = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();

    public static string HashOf(VerbPlan plan) =>
        plan.Id + "|" + string.Join(";", plan.Steps.Select(s => $"{s.ModuleId} {s.Verb} [{string.Join(",", s.Args)}] r={s.RestoreRecorded} u={(s.FixedUndo is { } u ? $"{u.ModuleId} {u.Verb} [{string.Join(",", u.Args)}]" : "-")}"));

    /// <summary>Marks a plan as approved exactly as it is (what the owner's yes does).</summary>
    public void Approve(VerbPlan plan) { lock (_gate) _approved[plan.Id] = HashOf(plan); }

    public IReadOnlyList<VerbInfo> List() => Infos.ToList();

    public Task<VerbResult> InvokeAsync(string moduleId, string verb, IReadOnlyList<string> args, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Scenes must never call a verb of another module on its own");

    public PlanApproval GetApproval(VerbPlan plan)
    {
        lock (_gate)
        {
            Calls.Add("approval " + plan.Id);
            if (ForceApproval is { } forced) return forced;
            if (!_approved.TryGetValue(plan.Id, out var hash)) return new PlanApproval(PlanApprovalState.NotApproved);
            return hash == HashOf(plan) ? new PlanApproval(PlanApprovalState.Approved) : new PlanApproval(PlanApprovalState.Changed, "a step, an argument or what a step will do is different");
        }
    }

    public Task<PlanApproval> RequestApprovalAsync(VerbPlan plan, CancellationToken cancellationToken = default)
    {
        lock (_gate) { Calls.Add("ask " + plan.Id); Asked.Add(plan); }
        OnAsk?.Invoke();
        var state = Answer(plan);
        if (state == PlanApprovalState.Approved) { Approve(plan); return Task.FromResult(new PlanApproval(PlanApprovalState.Approved)); }
        return Task.FromResult(new PlanApproval(state, state is PlanApprovalState.Untrusted or PlanApprovalState.Unavailable ? "because" : ""));
    }

    public void Revoke(string planId) { lock (_gate) { Calls.Add("revoke " + planId); Revoked.Add(planId); _approved.Remove(planId); } }

    public IReadOnlyList<ApprovedPlanInfo> ApprovedPlans() { lock (_gate) return _approved.Select(a => new ApprovedPlanInfo(a.Key, a.Value, Now, Array.Empty<string>())).ToList(); }

    /// <summary>Per step: the exit the verb answers (default 0). Set an index to 1 to make that step fail.</summary>
    public HashSet<int> FailSteps = new();

    public Task<PlanRun> StartAsync(VerbPlan plan, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Calls.Add("start " + plan.Id);
            Started.Add(plan);
            if (ForceStart?.Invoke(plan) is { } forced) return Task.FromResult(forced);
            if (!_approved.TryGetValue(plan.Id, out var hash)) return Task.FromResult(new PlanRun("", PlanOutcome.NeedsApproval, [], "not approved (NotApproved)"));
            if (hash != HashOf(plan)) return Task.FromResult(new PlanRun("", PlanOutcome.NeedsApproval, [], "not approved (Changed) a step is different"));
            if (_open.Values.Any(r => r.PlanId == plan.Id)) return Task.FromResult(new PlanRun("", PlanOutcome.AlreadyRunning, [], "this plan already has an open run"));
            if (_open.Count >= 4) return Task.FromResult(new PlanRun("", PlanOutcome.TooManyRuns, [], "4 runs are open already"));
            var steps = new List<PlanStepResult>();
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                bool fail = FailSteps.Contains(i);
                steps.Add(new PlanStepResult(plan.Steps[i], false, fail ? VerbOutcome.Failed : VerbOutcome.Done, fail ? 1 : 0, ""));
                if (fail) return Task.FromResult(new PlanRun("", PlanOutcome.Failed, steps, "step " + (i + 1) + " failed"));
            }
            var run = new FakeRun { RunId = "run" + (++_counter), PlanId = plan.Id, StartedAt = Now, Steps = plan.Steps.Select(s => $"{s.ModuleId} {s.Verb} {string.Join(" ", s.Args)}".Trim()).ToList() };
            _open[run.RunId] = run;
            return Task.FromResult(new PlanRun(run.RunId, PlanOutcome.Done, steps));
        }
    }

    public Task<PlanRun> EndAsync(string runId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Calls.Add("end " + runId);
            Ended.Add(runId);
            if (!_open.TryGetValue(runId, out var run)) return Task.FromResult(new PlanRun(runId, PlanOutcome.NotFound, [], "this module has no such open run"));
            if ((ForceEnd ?? ForceEndFor?.Invoke(runId)) is { } outcome)
            {
                if (outcome == PlanOutcome.UndoFailed) run.UndoFailed = true;
                return Task.FromResult(new PlanRun(runId, outcome, [], "a step could not be put back"));
            }
            _open.Remove(runId);
            return Task.FromResult(new PlanRun(runId, PlanOutcome.Done, run.Steps.Select(s => new PlanStepResult(new PlanStep("x", "undo", []), true, VerbOutcome.Done, 0, "")).ToList()));
        }
    }

    public IReadOnlyList<ActiveRun> ActiveRuns()
    {
        lock (_gate) return _open.Values.OrderBy(r => r.StartedAt).Select(r => new ActiveRun(r.RunId, r.PlanId, r.StartedAt, r.Interrupted, r.UndoFailed, r.Steps)).ToList();
    }

    /// <summary>A run left by a Rigbay that ended: open at the host, flagged Interrupted.</summary>
    public string LeaveRun(string planId, DateTimeOffset startedAt, bool undoFailed = false, params string[] steps)
    {
        lock (_gate)
        {
            var run = new FakeRun { RunId = "run" + (++_counter), PlanId = planId, StartedAt = startedAt, Interrupted = true, UndoFailed = undoFailed, Steps = steps.Length > 0 ? steps.ToList() : new() { "demo-target set 5" } };
            _open[run.RunId] = run;
            return run.RunId;
        }
    }

    public int OpenCount { get { lock (_gate) return _open.Count; } }
    public bool IsOpen(string runId) { lock (_gate) return _open.ContainsKey(runId); }
    public string CallLog() { lock (_gate) return string.Join(" | ", Calls); }
    public int Count(string prefix) { lock (_gate) return Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal)); }
}

// ------------------------------------------------------------------ machine fakes

internal sealed class FakeProcesses : IProcessSource
{
    private readonly object _gate = new();
    private readonly List<RunningProgram> _running = new();
    public int FindCalls;
    public List<string> Windowed = new();
    public bool Throw;

    public void Start(string name, string? path = null) { lock (_gate) _running.Add(new RunningProgram(name.ToLowerInvariant(), path, 1000 + _running.Count)); }
    public void Stop(string name) { lock (_gate) _running.RemoveAll(p => p.Name == name.ToLowerInvariant()); }
    public void StopAll() { lock (_gate) _running.Clear(); }

    public IReadOnlyList<RunningProgram> Find(IReadOnlyCollection<string> exeNames, bool withPath)
    {
        Interlocked.Increment(ref FindCalls);
        if (Throw) throw new InvalidOperationException("cannot read the processes");
        lock (_gate) return _running.Where(p => exeNames.Contains(p.Name)).Select(p => withPath ? p : p with { Path = null }).ToList();
    }

    public IReadOnlyList<string> WindowedPrograms() => Windowed.ToList();
}

internal sealed class FakeTicker : ITicker
{
    public int Created, Disposed;
    public TimeSpan? Period;
    public int Running => Created - Disposed;

    public IDisposable Every(TimeSpan period, Action action)
    {
        Created++;
        Period = period;
        return new Handle(this);
    }

    private sealed class Handle(FakeTicker owner) : IDisposable
    {
        private bool _done;
        public void Dispose() { if (_done) return; _done = true; owner.Disposed++; }
    }
}
