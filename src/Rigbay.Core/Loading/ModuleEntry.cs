using System.Reflection;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>Where a module is in its life. <see cref="BayStatus"/> maps this onto the dashboard's closed set of states.</summary>
public enum ModuleLifecycle
{
    /// <summary>module.json is missing or invalid; no code was loaded.</summary>
    Invalid,
    /// <summary>The manifest's hostApi range does not contain this host's API version; no code was loaded.</summary>
    Incompatible,
    /// <summary>Probe said this machine is not supported.</summary>
    Unsupported,
    /// <summary>Probed as supported, not enabled.</summary>
    Off,
    /// <summary>Initialised.</summary>
    Running,
    /// <summary>Failed to load, or threw or timed out in a call; disabled until the next host start.</summary>
    Faulted,
}

/// <summary>Set by the installer, not by the module.</summary>
public enum PendingChange { None, UpdateAvailable, RestartRequired }

/// <summary>
/// Why a state is what it is, as a language key. A null <see cref="ModuleId"/> means a host key (<c>core.reason.*</c>);
/// otherwise the key is module-relative and belongs to that module.
/// </summary>
public sealed record ModuleReason(string Key, object[] Args, string? ModuleId = null)
{
    public static ModuleReason Host(string key, params object[] args) => new(key, args, null);
}

/// <summary>State and reason for one dashboard bay.</summary>
public sealed record BayStatus(BayState State, ModuleReason? Reason);

/// <summary>
/// One module folder as the host sees it: manifest, load context, instance, state and the fault boundary. The UI works
/// with this type; every call it makes into the module goes through <see cref="Invoke"/> or <see cref="InvokeAsync{T}"/>.
/// </summary>
public sealed class ModuleEntry
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = new();
    private IModule? _instance;
    private ModuleLoadContext? _context;
    private ModuleHostServices? _services;
    private CancellationTokenSource _stop = new();
    private bool _initialized;
    private ModuleLifecycle _lifecycle;
    private ModuleReason? _reason;
    private Exception? _fault;
    private PageModel? _page;

    internal ModuleEntry(RigbayHost host, string folder)
    {
        Host = host;
        Folder = folder;
        Id = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        Localisation = new ModuleLocalisation(host.Languages, Id);
        Log = host.Log.ForSource(Id);
    }

    public RigbayHost Host { get; }
    public string Folder { get; }

    /// <summary>The manifest id, or the folder name when the manifest is invalid.</summary>
    public string Id { get; private set; }

    public ModuleManifest? Manifest { get; private set; }
    public IReadOnlyList<ManifestIssue> Issues { get; private set; } = Array.Empty<ManifestIssue>();
    public ModuleLocalisation Localisation { get; }
    public ILog Log { get; }

    public ModuleLifecycle Lifecycle { get { lock (_gate) return _lifecycle; } }
    public ModuleReason? Reason { get { lock (_gate) return _reason; } }
    public Exception? Fault { get { lock (_gate) return _fault; } }
    public PageModel? Page { get { lock (_gate) return _page; } }
    /// <summary>True once a load context for the module exists, that is, once any module code could have run. False for invalid or incompatible modules.</summary>
    public bool HasLoadedCode => _context is not null;

    /// <summary>The module's own load context; null until module code is about to load.</summary>
    public System.Runtime.Loader.AssemblyLoadContext? LoadContext => _context;

    public PendingChange Pending { get; private set; }
    public string? PendingVersion { get; private set; }

    /// <summary>Raised after the lifecycle, page, reason or pending change changed (on the thread that changed it).</summary>
    public event Action<ModuleEntry>? Changed;

    /// <summary>The manifest name, or the language file's <c>module.&lt;id&gt;.name</c> when there is one.</summary>
    public string DisplayName => Localisation.TryGet("name", out var t) ? t : Manifest?.Name ?? Id;

    public string DisplayDescription => Localisation.TryGet("description", out var t) ? t : Manifest?.Description ?? "";

    /// <summary>The state for the dashboard bay. Precedence: failed, unsupported, restart required, update available, ready or off.</summary>
    public BayStatus Bay
    {
        get
        {
            ModuleLifecycle lc;
            ModuleReason? reason;
            lock (_gate) { lc = _lifecycle; reason = _reason; }
            switch (lc)
            {
                case ModuleLifecycle.Invalid:
                case ModuleLifecycle.Faulted:
                    return new BayStatus(BayState.Failed, reason);
                case ModuleLifecycle.Incompatible:
                case ModuleLifecycle.Unsupported:
                    return new BayStatus(BayState.Unsupported, reason);
            }
            if (Pending == PendingChange.RestartRequired) return new BayStatus(BayState.RestartRequired, ModuleReason.Host("core.reason.restart"));
            if (Pending == PendingChange.UpdateAvailable) return new BayStatus(BayState.UpdateAvailable, ModuleReason.Host("core.reason.update", PendingVersion ?? ""));
            return new BayStatus(lc == ModuleLifecycle.Running ? BayState.Ready : BayState.Off, null);
        }
    }

    /// <summary>The reason as text in the current language.</summary>
    public string ResolveReason(ModuleReason reason) =>
        reason.ModuleId is null ? Host.Languages.F(reason.Key, reason.Args) : (reason.Args.Length > 0 ? Localisation.F(reason.Key, reason.Args) : Localisation.T(reason.Key));

    public void SetPending(PendingChange change, string? version = null)
    {
        Pending = change;
        PendingVersion = version;
        RaiseChanged();
    }

    // ---------------------------------------------------------------- loading

    internal void Load()
    {
        var result = ManifestReader.ReadFolder(Folder);
        Issues = result.Issues;
        foreach (var issue in result.Issues.Where(i => !i.IsError)) Log.Warn($"module.json {issue}");

        if (result.Manifest is not { } m)
        {
            SetState(ModuleLifecycle.Invalid, ModuleReason.Host("core.reason.manifest-invalid", result.ErrorSummary));
            Log.Error("manifest invalid: " + result.ErrorSummary);
            return;
        }
        Manifest = m;
        Id = m.Id;

        Host.Languages.LoadDirectory(Path.Combine(Folder, m.LanguagesFolder), LanguageCatalog.ModulePrefix(m.Id));

        if (!m.HostApi.Contains(HostApi.Current))
        {
            SetState(ModuleLifecycle.Incompatible, ModuleReason.Host("core.reason.api-mismatch", m.HostApi.Text, HostApi.Current.ToString()));
            Log.Warn($"refused: needs host API {m.HostApi}, this host is {HostApi.Current}");
            return;
        }

        try
        {
            var entryPath = Path.Combine(Folder, m.EntryAssembly);
            var context = _context = new ModuleLoadContext(m.Id, entryPath);
            var assembly = context.LoadEntry(entryPath);

            var wanted = assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == "Rigbay.Abstractions");
            var have = typeof(IModule).Assembly.GetName().Version;
            if (wanted?.Version is { } w && have is not null && w > have)
            {
                SetState(ModuleLifecycle.Incompatible, ModuleReason.Host("core.reason.abstractions-newer", w.ToString(), have.ToString()));
                Log.Warn($"refused: built for Rigbay.Abstractions {w}, this host has {have}");
                return;
            }

            var type = assembly.GetType(m.EntryType, throwOnError: false)
                ?? throw new TypeLoadException($"entry type '{m.EntryType}' not found in {m.EntryAssembly}");
            if (!typeof(IModule).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                throw new TypeLoadException($"'{m.EntryType}' is not a concrete class implementing Rigbay.Abstractions.IModule");
            if (type.GetConstructor(Type.EmptyTypes) is null)
                throw new TypeLoadException($"'{m.EntryType}' has no public parameterless constructor");

            IModule? instance = null;
            if (!RunGuarded("constructor", () => instance = (IModule)Activator.CreateInstance(type)!, Host.Options.ProbeTimeout)) return;
            _instance = instance;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetState(ModuleLifecycle.Faulted, ModuleReason.Host("core.reason.load-failed", Describe(ex)), ex);
            Log.Error("load failed", ex);
            return;
        }

        _services = new ModuleHostServices(Host, this);

        ProbeResult? probe = null;
        if (!RunGuarded("Probe", () => probe = _instance!.Probe(), Host.Options.ProbeTimeout)) return;
        if (probe is null)
        {
            FaultNow("Probe", new InvalidOperationException("Probe returned null"));
            return;
        }
        if (probe.Supported) SetState(ModuleLifecycle.Off, null);
        else
        {
            var reason = string.IsNullOrEmpty(probe.ReasonKey) ? ModuleReason.Host("core.reason.not-supported") : new ModuleReason(probe.ReasonKey, probe.ReasonArgs ?? Array.Empty<object>(), Id);
            SetState(ModuleLifecycle.Unsupported, reason);
            Log.Info("unsupported on this machine: " + probe.ReasonKey);
        }
    }

    // ---------------------------------------------------------------- enable / disable

    /// <summary>Initialises the module and builds its page. Returns true when it is running afterwards. Blocks up to the initialise timeout: call it off the UI thread.</summary>
    public bool Enable() => Enable(persist: true);

    internal bool Enable(bool persist)
    {
        lock (_gate)
        {
            if (_lifecycle == ModuleLifecycle.Running) return true;
            if (_lifecycle != ModuleLifecycle.Off || _instance is null || _services is null) return false;
            _stop = new CancellationTokenSource();
        }

        _initialized = true;
        if (!RunGuarded("Initialize", () => _instance!.Initialize(_services!), Host.Options.InitializeTimeout)) return false;
        lock (_gate)
        {
            if (_lifecycle != ModuleLifecycle.Off) return false; // faulted by a callback during Initialize
            _lifecycle = ModuleLifecycle.Running;
        }

        PageModel? page = null;
        if (!Invoke("GetPageModel", () => page = _instance!.GetPageModel(_services!))) return false;
        if (page is not null)
        {
            var problems = PageValidator.Validate(page);
            if (problems.Count > 0)
            {
                FaultNow("GetPageModel", new InvalidOperationException(string.Join("; ", problems)), badPage: true);
                return false;
            }
        }
        lock (_gate) _page = page;

        Log.Info("enabled");
        if (persist) Host.SetEnabled(Id, true);
        RaiseChanged();
        return true;
    }

    /// <summary>Calls Shutdown and removes the module's registrations. The module goes to Off and stays off at the next start.</summary>
    public bool Disable() => Stop(persist: true);

    internal bool Stop(bool persist)
    {
        lock (_gate)
        {
            if (_lifecycle != ModuleLifecycle.Running) return false;
            _lifecycle = ModuleLifecycle.Off;
            _page = null;
            _stop.Cancel();
        }
        RunGuardedOnly("Shutdown", () => _instance!.Shutdown(), Host.Options.ShutdownTimeout);
        Cleanup();
        _initialized = false;
        Log.Info(persist ? "disabled" : "stopped");
        if (persist) Host.SetEnabled(Id, false);
        RaiseChanged();
        return true;
    }

    // ---------------------------------------------------------------- the fault boundary

    public CancellationToken StopToken => _stop.Token;

    /// <summary>
    /// Runs a call into the module. Returns true when it completed. An exception faults the module (except cancellation
    /// after the module was asked to stop); a module that is not running is not called and the result is false.
    /// </summary>
    public bool Invoke(string operation, Action action)
    {
        if (Lifecycle != ModuleLifecycle.Running) return false;
        try { action(); return true; }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { FaultNow(operation, ex); return false; }
    }

    /// <summary>Async variant of <see cref="Invoke"/>.</summary>
    public async Task<(bool Completed, T? Value)> InvokeAsync<T>(string operation, Func<Task<T>> action)
    {
        if (Lifecycle != ModuleLifecycle.Running) return (false, default);
        try { return (true, await action().ConfigureAwait(false)); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return (false, default); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { FaultNow(operation, ex); return (false, default); }
    }

    /// <summary>Runs a call that returns a value; <paramref name="fallback"/> when the module is not running or it faulted.</summary>
    public T InvokeFor<T>(string operation, Func<T> action, T fallback)
    {
        T result = fallback;
        return Invoke(operation, () => result = action()) ? result : fallback;
    }

    /// <summary>Wraps a delegate the module gave to a service so that it runs in the boundary.</summary>
    public Action Guard(string operation, Action action) => () => Invoke(operation, action);

    public Action<T> Guard<T>(string operation, Action<T> action) => value => Invoke(operation, () => action(value));

    private bool RunGuarded(string operation, Action action, TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(action);
            if (!task.Wait(timeout))
            {
                FaultNow(operation, new TimeoutException($"{operation} did not finish within {timeout.TotalSeconds:0.#} s"), timedOut: true, timeout: timeout);
                return false;
            }
            return true;
        }
        catch (AggregateException ae)
        {
            FaultNow(operation, Unwrap(ae));
            return false;
        }
    }

    private void RunGuardedOnly(string operation, Action action, TimeSpan timeout)
    {
        // Shutdown: failures are logged but cannot fault a module that is already being stopped.
        try
        {
            var task = Task.Run(action);
            if (!task.Wait(timeout)) Log.Error($"{operation} did not finish within {timeout.TotalSeconds:0.#} s");
        }
        catch (AggregateException ae) { Log.Error($"{operation} threw", Unwrap(ae)); }
    }

    public void FaultNow(string operation, Exception ex, bool timedOut = false, TimeSpan timeout = default, bool badPage = false)
    {
        bool wasInitialized;
        lock (_gate)
        {
            if (_lifecycle == ModuleLifecycle.Faulted) return;
            _lifecycle = ModuleLifecycle.Faulted;
            _fault = ex;
            _page = null;
            _reason = timedOut ? ModuleReason.Host("core.reason.timeout", operation, timeout.TotalSeconds.ToString("0.#"))
                : badPage ? ModuleReason.Host("core.reason.bad-page", ex.Message)
                : ModuleReason.Host("core.reason.fault", operation, Describe(ex));
            wasInitialized = _initialized;
            _stop.Cancel();
        }
        Log.Error($"{operation} failed; module disabled", ex);
        Cleanup();
        if (wasInitialized && _instance is { } instance && !timedOut)
        {
            // Best effort, off this thread: the module is in an unknown state and may block.
            _ = Task.Run(() =>
            {
                try { instance.Shutdown(); }
                catch (Exception sx) { Log.Warn("Shutdown after a fault also threw: " + sx.Message); }
            });
        }
        RaiseChanged();
        Host.RaiseFaulted(this);
    }

    private void Cleanup()
    {
        Host.Hotkeys.RemoveModule(Id);
        Host.Tray.RemoveModule(Id);
        Host.Commands.RemoveModule(Id);
        IDisposable[] subs;
        lock (_gate) { subs = _subscriptions.ToArray(); _subscriptions.Clear(); }
        foreach (var s in subs) { try { s.Dispose(); } catch (Exception) { } }
    }

    internal void TrackSubscription(IDisposable subscription)
    {
        lock (_gate) _subscriptions.Add(subscription);
    }

    // ---------------------------------------------------------------- level 2

    /// <summary>
    /// Asks the module for the control behind a <see cref="CustomRow"/>. Returns null (and logs) if the module returned
    /// nothing or faulted; the UI checks the framework type of the result and shows an error row if it is wrong.
    /// </summary>
    public object? CreateCustomControl(string controlId) =>
        InvokeFor("CreateCustomControl", () => _instance!.CreateCustomControl(_services!, controlId), (object?)null);

    // ---------------------------------------------------------------- helpers

    private void SetState(ModuleLifecycle lifecycle, ModuleReason? reason, Exception? fault = null)
    {
        lock (_gate) { _lifecycle = lifecycle; _reason = reason; _fault = fault; }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this); } catch (Exception ex) { Log.Warn("a Changed handler threw: " + ex.Message); }
    }

    /// <summary>Strips the wrappers reflection and tasks put around the exception a module really threw.</summary>
    internal static Exception Unwrap(Exception ex)
    {
        while (true)
        {
            if (ex is AggregateException { InnerExceptions.Count: 1 } ae) ex = ae.InnerExceptions[0];
            else if (ex is System.Reflection.TargetInvocationException { InnerException: not null } tie) ex = tie.InnerException!;
            else return ex;
        }
    }

    internal static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
