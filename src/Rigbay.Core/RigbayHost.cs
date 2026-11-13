using System.Text;
using Rigbay.Abstractions;
using Rigbay.Core.Install;

namespace Rigbay.Core;

public sealed class RigbayHostOptions
{
    /// <summary>The admin-only folder that holds one sub-folder per module.</summary>
    public required string ModulesRoot { get; init; }

    /// <summary>Data root; null means <c>%LOCALAPPDATA%\Rigbay</c>.</summary>
    public string? DataRoot { get; init; }

    public Version HostVersion { get; init; } = new(0, 1, 1);

    /// <summary>
    /// Creates the modules folder when it is missing at start (the first start of a fresh install). Null means the real admin-only folder
    /// (<see cref="AdminFolderSecurity"/>), because the host loads whatever is in that folder as administrator; tests pass <see cref="NoFolderSecurity"/>.
    /// </summary>
    public IFolderSecurity? FolderSecurity { get; init; }
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public LogLevel LogLevel { get; init; } = LogLevel.Info;

    /// <summary>The clock of the metrics bus (sample timestamps and the history); null is the system clock. Tests and screenshots may run it faster.</summary>
    public Func<DateTimeOffset>? MetricsClock { get; init; }
    public long LogMaxBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// The admin-only folder of the two files that let the host write by itself (<c>approved-plans.json</c>, <c>plan-runs.json</c>). Null means
    /// <c>%ProgramData%\Rigbay\plans\&lt;user SID&gt;</c> when <see cref="DataRoot"/> is null (a normal run), otherwise <c>&lt;DataRoot&gt;\plans</c> (a run with its own data
    /// folder: tests, <c>--data</c>), so such a run never touches the real folder. Either way the folder is made admin-only with <see cref="FolderSecurity"/>
    /// when missing and only verified when it exists (never repaired silently).
    /// </summary>
    public string? PlansRoot { get; init; }

    /// <summary>The clock for time limits and the plan rate limit; null is the system clock. Tests pass a manual one.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Time limit for one verb called for another module, plan steps included.</summary>
    public TimeSpan VerbTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time limit for a whole plan run.</summary>
    public TimeSpan PlanRunTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How long the host waits for a verb to describe the exact change its arguments make.</summary>
    public TimeSpan PreviewTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Starts of one plan allowed per hour.</summary>
    public int PlanStartsPerHour { get; init; } = 6;

    /// <summary>Starts of all plans allowed per hour.</summary>
    public int PlanStartsPerHourTotal { get; init; } = 20;
}

/// <summary>Shown to the user before a hardware change is applied. The UI implements it; with none set the answer is no.</summary>
public sealed record ApprovalRequest(string ModuleId, string ModuleName, string EffectText, string? Detail, bool Revertible)
{
    /// <summary>True when the question comes before a revert (the user pressed Revert); <see cref="Detail"/> then names the current and the previous value.</summary>
    public bool IsRevert { get; init; }

    /// <summary>What the change is (<see cref="EffectKind"/>); the dialog picks its title and hint from it.</summary>
    public EffectKind Kind { get; init; } = EffectKind.Hardware;

    /// <summary>Set when the question is the one approval of a plan (H1): the steps to list. <see cref="EffectText"/> then says what approving means.</summary>
    public IReadOnlyList<PlanApprovalLine>? Plan { get; init; }

    /// <summary>The plan's name in the current language, when <see cref="Plan"/> is set.</summary>
    public string? PlanName { get; init; }
}

public interface IApprovalPrompt
{
    Task<bool> ConfirmAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>The UI side of <see cref="IToastService"/>. Texts are already localised.</summary>
public interface IToastSink
{
    void Show(string moduleId, string title, string body, ToastKind kind);
}

/// <summary>The UI side of a transient message inside the window (see <see cref="NotificationPolicy"/>). Texts are already localised; may be called from any thread.</summary>
public interface IInAppNoticeSink
{
    void Show(string moduleId, string title, string body, ToastKind kind);
}

/// <summary>Host state that outlives a run: which modules the user enabled, and the language.</summary>
public sealed class HostState
{
    public List<string> EnabledModules { get; set; } = new();
    public string? Language { get; set; }
}

/// <summary>
/// The UI-independent host: finds modules, loads and supervises them, and owns the registries. The app creates one,
/// calls <see cref="Discover"/> and <see cref="StartEnabled"/> (off the UI thread), and binds its windows to
/// <see cref="Modules"/>, <see cref="Metrics"/>, <see cref="Hotkeys"/>, <see cref="Tray"/> and <see cref="Log"/>.
/// </summary>
public sealed class RigbayHost : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ModuleEntry> _modules = new();
    private readonly JsonSettingsStore _stateStore;
    private readonly ILog _hostLog;
    private HostState _state;
    private bool _discovered;
    private readonly object _verbsGate = new();
    private readonly List<Action> _verbsChanged = new();

    public RigbayHost(RigbayHostOptions options)
    {
        Options = options;
        Paths = new RigbayPaths(options.DataRoot);
        Log = new HostLog(Paths.LogDirectory, options.LogMaxBytes, minimum: options.LogLevel);
        _hostLog = Log.ForSource("host");
        Languages = new LanguageCatalog(_hostLog);
        Languages.LoadEmbedded(typeof(RigbayHost).Assembly, "Rigbay.Core.lang.");
        Languages.LoadDirectory(Paths.UserLanguageDirectory);
        Metrics = new MetricsBus(Log.ForSource("metrics"), options.MetricsClock);
        // metric descriptors carry owner-relative keys: resolve them through the owner's localisation (null owner = the host's own texts)
        Metrics.TextResolver = (owner, key) =>
        {
            string? text = null;
            bool found = owner is not null && Find(owner) is { } module
                ? module.Localisation.TryGet(key, out text)
                : Languages.TryGet(key, out text);
            return found && !string.IsNullOrWhiteSpace(text) ? text : null;
        };
        Languages.LanguageChanged += Metrics.NotifyCatalogChanged; // labels differ after a language switch
        Hotkeys = new HotkeyRegistry(Log.ForSource("hotkeys"), Paths.HotkeysFile);
        Tray = new TrayRegistry();
        Commands = new CommandRegistry();
        Time = options.TimeProvider ?? TimeProvider.System;
        Plans = new PlanEngine(this, OpenPlanStorage(options), Time);
        Languages.LanguageChanged += RaiseVerbsChanged;

        _stateStore = new JsonSettingsStore(Paths.HostSettingsFile, _hostLog);
        _state = _stateStore.Load<HostState>();
        if (_state.Language is not null) Languages.SetLanguage(_state.Language);
        _hostLog.Info($"Rigbay host {options.HostVersion}, API {HostApi.Current}, modules in {options.ModulesRoot}");
    }

    public RigbayHostOptions Options { get; }
    public RigbayPaths Paths { get; }
    public HostLog Log { get; }
    public LanguageCatalog Languages { get; }
    public MetricsBus Metrics { get; }
    public HotkeyRegistry Hotkeys { get; }
    public TrayRegistry Tray { get; }
    public CommandRegistry Commands { get; }

    /// <summary>The clock for time limits and the plan rate limit.</summary>
    public TimeProvider Time { get; }

    /// <summary>Approved plans and cross-module verbs (host API 0.1.4): approvals, journal, runs. The app uses it for a list with Revoke.</summary>
    public PlanEngine Plans { get; }

    private PlanStorage OpenPlanStorage(RigbayHostOptions options)
    {
        var security = options.FolderSecurity ?? DefaultFolderSecurity();
        string root;
        var chain = new List<string>();
        if (options.PlansRoot is { Length: > 0 } custom)
        {
            root = Path.GetFullPath(custom);
            chain.Add(root);
        }
        else if (options.DataRoot is { Length: > 0 } dataRoot)
        {
            // a run with its own data folder (--data, tests): keep the plans inside it, still checked as admin-only; never touch the real ProgramData folder
            root = Path.Combine(Path.GetFullPath(dataRoot), "plans");
            chain.Add(root);
        }
        else
        {
            string sid = "user";
            if (OperatingSystem.IsWindows()) sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? sid;
            var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rigbay");
            chain.Add(programData);
            chain.Add(Path.Combine(programData, "plans"));
            root = Path.Combine(programData, "plans", sid);
            chain.Add(root);
        }
        return new PlanStorage(root, chain, security, Log.ForSource("plans"));
    }

    /// <summary>Raised when the list of verbs other modules can see may have changed (a module started or stopped, the language changed).</summary>
    internal IDisposable SubscribeVerbsChanged(Action handler)
    {
        lock (_verbsGate) _verbsChanged.Add(handler);
        return new VerbsHook(this, handler);
    }

    private void RaiseVerbsChanged()
    {
        Action[] handlers;
        lock (_verbsGate) handlers = _verbsChanged.ToArray();
        foreach (var h in handlers)
        {
            try { h(); } catch (Exception ex) { _hostLog.Warn("a Verbs.Changed handler threw: " + ex.Message); }
        }
    }

    private sealed class VerbsHook(RigbayHost host, Action handler) : IDisposable
    {
        public void Dispose() { lock (host._verbsGate) host._verbsChanged.Remove(handler); }
    }

    /// <summary>The UI's way to run an action on its thread; null runs actions inline (tests, command-line mode).</summary>
    public Action<Action>? UiDispatcher { get; set; }
    public IToastSink? ToastSink { get; set; }

    /// <summary>The UI's message inside the window; null when there is no window (command-line mode, tests): then every notice is a toast.</summary>
    public IInAppNoticeSink? InAppSink { get; set; }

    /// <summary>True while the window is visible, not minimised and the foreground window (the user is looking at it). Null means false.</summary>
    public Func<bool>? WindowInUse { get; set; }
    public IApprovalPrompt? ApprovalPrompt { get; set; }

    public IReadOnlyList<ModuleEntry> Modules
    {
        get { lock (_gate) return _modules.ToArray(); }
    }

    public ModuleEntry? Find(string id)
    {
        lock (_gate) return _modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Raised when any module faulted (after it was disabled and cleaned up).</summary>
    public event Action<ModuleEntry>? ModuleFaulted;

    /// <summary>Raised after a module's page was rebuilt because the module called <c>RequestPageRefresh</c>; the page is <see cref="ModuleEntry.Page"/>.</summary>
    public event Action<ModuleEntry>? PageRefreshed;

    /// <summary>Raised after <see cref="Discover"/> filled <see cref="Modules"/>.</summary>
    public event Action? ModulesDiscovered;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IFolderSecurity AdminOnly() => new AdminFolderSecurity();

    private static IFolderSecurity DefaultFolderSecurity() => OperatingSystem.IsWindows() ? AdminOnly() : new NoFolderSecurity();

    /// <summary>
    /// Reads every module folder: manifest, API check, load, instantiate, probe. Nothing is initialised yet. Blocks
    /// (probes have a time limit each), so call it off the UI thread. Safe to call once.
    /// </summary>
    public void Discover()
    {
        lock (_gate)
        {
            if (_discovered) return;
            _discovered = true;
        }
        // The first start of a fresh install has no modules folder yet; that is normal (E2E-09), not a warning. Create it when the host may,
        // otherwise say once at Info what to do.
        if (!Directory.Exists(Options.ModulesRoot))
        {
            try
            {
                (Options.FolderSecurity ?? DefaultFolderSecurity()).EnsureAdminOnly(Options.ModulesRoot);
                _hostLog.Info($"modules folder '{Options.ModulesRoot}' did not exist and was created (administrators only)");
            }
            catch (Exception ex)
            {
                // do not leave behind a folder the admin-only check refused: the host would load modules from it
                try { if (Directory.Exists(Options.ModulesRoot) && !Directory.EnumerateFileSystemEntries(Options.ModulesRoot).Any()) Directory.Delete(Options.ModulesRoot); } catch { /* best effort */ }
                _hostLog.Info($"modules folder '{Options.ModulesRoot}' does not exist and could not be created ({ex.GetType().Name}); create it as an administrator, or install a module from the Modules page, and restart");
            }
        }
        if (Directory.Exists(Options.ModulesRoot))
        {
            foreach (var folder in Directory.GetDirectories(Options.ModulesRoot).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var entry = new ModuleEntry(this, folder);
                lock (_gate) _modules.Add(entry);
                entry.Changed += _ => RaiseVerbsChanged();
                try { entry.Load(); }
                catch (Exception ex) { entry.FaultNow("Load", ex); }
            }
        }
        _hostLog.Info($"discovered {_modules.Count} module(s): " + string.Join(", ", _modules.Select(m => $"{m.Id}={m.Lifecycle}")));
        Plans.DropMissingOwners(_modules.Select(m => m.Id));
        ModulesDiscovered?.Invoke();
    }

    /// <summary>Enables every module the user had enabled and that is supported. Blocks like <see cref="Discover"/>.</summary>
    public void StartEnabled()
    {
        HashSet<string> enabled;
        lock (_gate) enabled = new HashSet<string>(_state.EnabledModules, StringComparer.Ordinal);
        foreach (var m in Modules.Where(m => m.Lifecycle == ModuleLifecycle.Off && enabled.Contains(m.Id)))
            m.Enable(persist: false);
        Plans.AnnounceInterrupted();
    }

    /// <summary>Stops every running module (their Shutdown runs); the enabled choice is kept for the next start.</summary>
    public void Shutdown()
    {
        foreach (var m in Modules) m.Stop(persist: false);
    }

    public bool IsEnabledByUser(string id)
    {
        lock (_gate) return _state.EnabledModules.Contains(id);
    }

    public void SetLanguage(string code)
    {
        if (!Languages.SetLanguage(code)) return;
        lock (_gate) _state.Language = code;
        SaveState();
    }

    internal void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            _state.EnabledModules.Remove(id);
            if (enabled) _state.EnabledModules.Add(id);
        }
        SaveState();
    }

    private void SaveState()
    {
        try { lock (_gate) _stateStore.Save(_state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _hostLog.Warn("could not save host state: " + ex.Message); }
    }

    internal void RaisePageRefreshed(ModuleEntry entry)
    {
        try { PageRefreshed?.Invoke(entry); } catch (Exception ex) { _hostLog.Warn("a PageRefreshed handler threw: " + ex.Message); }
    }

    internal void RaiseFaulted(ModuleEntry entry)
    {
        try { ModuleFaulted?.Invoke(entry); } catch (Exception ex) { _hostLog.Warn("a ModuleFaulted handler threw: " + ex.Message); }
    }

    /// <summary>Asks the user to confirm a hardware write; false when nobody can be asked.</summary>
    public Task<bool> RequestApprovalAsync(ModuleEntry entry, HardwareEffect effect, LocalisedText? detail, CancellationToken cancellationToken) =>
        RequestApprovalTextAsync(entry, effect, detail is { IsEmpty: false } d ? entry.Localisation.Resolve(d) : null, cancellationToken);

    /// <summary>
    /// Like <see cref="RequestApprovalAsync"/> with a detail that is already text in the current language (it becomes
    /// <see cref="ApprovalRequest.Detail"/>). <paramref name="isRevert"/> marks the question as the one before a revert
    /// (<see cref="ApprovalRequest.IsRevert"/>; the effect text is then <see cref="HardwareEffect.RevertDescriptionKey"/> when the module gave one).
    /// </summary>
    public async Task<bool> RequestApprovalTextAsync(ModuleEntry entry, HardwareEffect effect, string? detailText, CancellationToken cancellationToken, bool isRevert = false)
    {
        // A step of an approved plan (H1): the owner approved the exact steps already, so the target module's own question is answered here, never more than
        // three times per step, only for the declared kind of change, only while the step runs (CallScope), and every answer is logged.
        if (!isRevert && CallScope.Current is { IsPlanStep: true } scope)
        {
            var asked = entry.Localisation.T(effect.DescriptionKey);
            if (scope.TryAnswer(entry.Id, effect.Kind, asked, detailText))
            {
                entry.Log.Info($"plan step: the approved plan answered '{asked}'{(string.IsNullOrEmpty(detailText) ? "" : " | " + detailText)} with yes" + (scope.DeclaredKind is null ? " (the verb declared no effect kind)" : ""));
                return true;
            }
        }
        var prompt = ApprovalPrompt;
        if (prompt is null)
        {
            entry.Log.Warn("hardware change refused: the host has no way to ask for confirmation");
            return false;
        }
        var effectKey = isRevert && !string.IsNullOrEmpty(effect.RevertDescriptionKey) ? effect.RevertDescriptionKey : effect.DescriptionKey;
        var request = new ApprovalRequest(entry.Id, entry.DisplayName, entry.Localisation.T(effectKey),
            string.IsNullOrEmpty(detailText) ? null : detailText, effect.Revertible) { IsRevert = isRevert, Kind = effect.Kind };
        try { return await prompt.ConfirmAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entry.Log.Warn("approval prompt failed, treated as no: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The short name the dashboard shows for a metric, in the current language. Order: the <see cref="MetricDescriptor"/>
    /// a module registered (its <c>LabelKey</c> is resolved with that module's prefix; a <see cref="Text.Literal"/> is shown
    /// as written), then the host's own <c>metric.&lt;id&gt;</c> text, then the raw id. Never empty for a valid id.
    /// </summary>
    public string ResolveMetricLabel(MetricId id)
    {
        if (Metrics.TryGetDescriptor(id, out var descriptor, out var owner))
        {
            var text = Find(owner) is { } module
                ? (module.Localisation.TryGet(descriptor.LabelKey, out var found) ? found : "")
                : (Languages.TryGet(descriptor.LabelKey, out var hostText) ? hostText : "");
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return Languages.TryGet("metric." + id.Value, out var shared) && !string.IsNullOrWhiteSpace(shared) ? shared : id.Value;
    }

    /// <summary>Same as <see cref="ResolveMetricLabel(MetricId)"/> for an id given as text; text that is not a valid id comes back unchanged.</summary>
    public string ResolveMetricLabel(string id) => MetricId.IsValid(id) ? ResolveMetricLabel(new MetricId(id)) : id;

    /// <summary>
    /// <c>rigbay &lt;module&gt; &lt;verb&gt; [args]</c>. <paramref name="args"/> are the words after <c>rigbay</c>. Exit codes:
    /// 0 ok, 1 failed, 2 not found or bad usage.
    /// </summary>
    public async Task<CommandOutcome> ExecuteCommandAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count == 0) return new(CommandOutcome.UsageOrNotFound, Languages.T("core.command.usage") + Environment.NewLine + ListModules());
        if (args[0] is "help" or "--help" or "-h") return new(CommandOutcome.Ok, Languages.T("core.command.usage") + Environment.NewLine + ListModules());
        if (args[0] == "list") return new(CommandOutcome.Ok, ListModules());
        if (args[0] == "version") return new(CommandOutcome.Ok, VersionText());
        if (args[0] == "plans") return await Plans.ExecuteAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);

        var module = Find(args[0]);
        if (module is null) return new(CommandOutcome.UsageOrNotFound, Languages.F("core.command.unknown-module", args[0]));
        if (module.Lifecycle != ModuleLifecycle.Running) return new(CommandOutcome.UsageOrNotFound, Languages.F("core.command.not-running", module.Id));
        if (args.Count == 1) return new(CommandOutcome.UsageOrNotFound, Languages.T("core.command.usage") + Environment.NewLine + ListVerbs(module));

        var entry = Commands.Find(module.Id, args[1]);
        if (entry is null) return new(CommandOutcome.UsageOrNotFound, Languages.F("core.command.unknown-verb", module.Id, args[1]) + Environment.NewLine + ListVerbs(module));
        return await CommandRegistry.RunAsync(entry, args.Skip(2).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The host version, the Rigbay.Abstractions version and the host API version, one per line (the <c>version</c> verb).</summary>
    public string VersionText() => string.Join(Environment.NewLine,
        Languages.F("core.command.version-host", Options.HostVersion),
        Languages.F("core.command.version-abstractions", typeof(IModule).Assembly.GetName().Version?.ToString(3) ?? "?"),
        Languages.F("core.command.version-api", HostApi.Current));

    /// <summary>One line per installed module: id, bay state, version, whether the user enabled it, and the reason when there is one (the <c>list</c> verb).</summary>
    public string ListModules()
    {
        var modules = Modules;
        if (modules.Count == 0) return Languages.T("core.command.no-modules");
        var sb = new StringBuilder();
        foreach (var m in modules)
        {
            sb.Append($"{m.Id}  {m.Bay.State}  {m.Manifest?.Version ?? "-"}  {(IsEnabledByUser(m.Id) ? "enabled" : "disabled")}");
            if (m.Reason is { } reason) sb.Append("  ").Append(m.ResolveReason(reason).ReplaceLineEndings(" "));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private string ListVerbs(ModuleEntry module)
    {
        var sb = new StringBuilder();
        foreach (var v in Commands.VerbsOf(module.Id)) sb.AppendLine($"{module.Id} {v.Verb}  {module.Localisation.T(v.DescriptionKey)}");
        return sb.ToString().TrimEnd();
    }

    public void Dispose() => Shutdown();
}
