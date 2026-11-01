using System.Text;
using Rigbay.Abstractions;

namespace Rigbay.Core;

public sealed class RigbayHostOptions
{
    /// <summary>The admin-only folder that holds one sub-folder per module.</summary>
    public required string ModulesRoot { get; init; }

    /// <summary>Data root; null means <c>%LOCALAPPDATA%\Rigbay</c>.</summary>
    public string? DataRoot { get; init; }

    public Version HostVersion { get; init; } = new(0, 1, 0);
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public LogLevel LogLevel { get; init; } = LogLevel.Info;
    public long LogMaxBytes { get; init; } = 1024 * 1024;
}

/// <summary>Shown to the user before a hardware change is applied. The UI implements it; with none set the answer is no.</summary>
public sealed record ApprovalRequest(string ModuleId, string ModuleName, string EffectText, string? Detail, bool Revertible);

public interface IApprovalPrompt
{
    Task<bool> ConfirmAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>The UI side of <see cref="IToastService"/>. Texts are already localised.</summary>
public interface IToastSink
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

    public RigbayHost(RigbayHostOptions options)
    {
        Options = options;
        Paths = new RigbayPaths(options.DataRoot);
        Log = new HostLog(Paths.LogDirectory, options.LogMaxBytes, minimum: options.LogLevel);
        _hostLog = Log.ForSource("host");
        Languages = new LanguageCatalog(_hostLog);
        Languages.LoadEmbedded(typeof(RigbayHost).Assembly, "Rigbay.Core.lang.");
        Languages.LoadDirectory(Paths.UserLanguageDirectory);
        Metrics = new MetricsBus(Log.ForSource("metrics"));
        Hotkeys = new HotkeyRegistry(Log.ForSource("hotkeys"), Paths.HotkeysFile);
        Tray = new TrayRegistry();
        Commands = new CommandRegistry();

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

    /// <summary>The UI's way to run an action on its thread; null runs actions inline (tests, command-line mode).</summary>
    public Action<Action>? UiDispatcher { get; set; }
    public IToastSink? ToastSink { get; set; }
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

    /// <summary>Raised after <see cref="Discover"/> filled <see cref="Modules"/>.</summary>
    public event Action? ModulesDiscovered;

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
        if (Directory.Exists(Options.ModulesRoot))
        {
            foreach (var folder in Directory.GetDirectories(Options.ModulesRoot).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var entry = new ModuleEntry(this, folder);
                lock (_gate) _modules.Add(entry);
                try { entry.Load(); }
                catch (Exception ex) { entry.FaultNow("Load", ex); }
            }
        }
        else _hostLog.Warn($"modules folder '{Options.ModulesRoot}' does not exist");
        _hostLog.Info($"discovered {_modules.Count} module(s): " + string.Join(", ", _modules.Select(m => $"{m.Id}={m.Lifecycle}")));
        ModulesDiscovered?.Invoke();
    }

    /// <summary>Enables every module the user had enabled and that is supported. Blocks like <see cref="Discover"/>.</summary>
    public void StartEnabled()
    {
        HashSet<string> enabled;
        lock (_gate) enabled = new HashSet<string>(_state.EnabledModules, StringComparer.Ordinal);
        foreach (var m in Modules.Where(m => m.Lifecycle == ModuleLifecycle.Off && enabled.Contains(m.Id)))
            m.Enable(persist: false);
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

    internal void RaiseFaulted(ModuleEntry entry)
    {
        try { ModuleFaulted?.Invoke(entry); } catch (Exception ex) { _hostLog.Warn("a ModuleFaulted handler threw: " + ex.Message); }
    }

    /// <summary>Asks the user to confirm a hardware write; false when nobody can be asked.</summary>
    public Task<bool> RequestApprovalAsync(ModuleEntry entry, HardwareEffect effect, LocalisedText? detail, CancellationToken cancellationToken) =>
        RequestApprovalTextAsync(entry, effect, detail is { IsEmpty: false } d ? entry.Localisation.Resolve(d) : null, cancellationToken);

    /// <summary>Like <see cref="RequestApprovalAsync"/> with a detail that is already text in the current language (it becomes <see cref="ApprovalRequest.Detail"/>).</summary>
    public async Task<bool> RequestApprovalTextAsync(ModuleEntry entry, HardwareEffect effect, string? detailText, CancellationToken cancellationToken)
    {
        var prompt = ApprovalPrompt;
        if (prompt is null)
        {
            entry.Log.Warn("hardware change refused: the host has no way to ask for confirmation");
            return false;
        }
        var request = new ApprovalRequest(entry.Id, entry.DisplayName, entry.Localisation.T(effect.DescriptionKey),
            string.IsNullOrEmpty(detailText) ? null : detailText, effect.Revertible);
        try { return await prompt.ConfirmAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entry.Log.Warn("approval prompt failed, treated as no: " + ex.Message);
            return false;
        }
    }

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
