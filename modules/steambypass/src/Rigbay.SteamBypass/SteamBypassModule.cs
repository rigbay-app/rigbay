using Rigbay.Abstractions;
using Rigbay.SteamBypass.Check;
using Rigbay.SteamBypass.Net;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass;

/// <summary>
/// The Steam access module: restores access to Steam on a network that blocks it, with a local proxy, DNS-over-HTTPS and a PAC file.
/// Lifecycle and wiring are here; the page is in <c>SteamBypassModule.Page.cs</c>, texts and the connection check in
/// <c>SteamBypassModule.Texts.cs</c>, verbs in <c>SteamBypassModule.Commands.cs</c>. The on/off safety rules are in
/// <see cref="BypassController"/> and <see cref="SystemProxyManager"/>; see docs/steambypass/MODULE.md.
/// </summary>
public sealed partial class SteamBypassModule : IModule
{
    /// <summary>Metric: connections open right now.</summary>
    public static readonly MetricId ActiveMetric = new("steam.proxy.active");

    /// <summary>Metric: connections that failed (upstream unreachable, DNS failure, internal error) since the module started.</summary>
    public static readonly MetricId ErrorsMetric = new("steam.proxy.errors");

    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(10);

    private readonly Dependencies _deps;

    // Values the page shows. They live as long as the instance, so enabling again after disabling works.
    private readonly ObservableValue<bool> _bypass = new(false);
    private readonly ObservableValue<bool> _autoOn = new(false);
    private readonly ObservableValue<string> _listenText = new("");
    private readonly ObservableValue<string> _steamText = new("");
    private readonly ObservableValue<string> _domainsText = new("");
    private readonly ObservableValue<string> _problemText = new("");
    private readonly ObservableValue<bool> _port80Warning = new(false);
    private readonly ObservableValue<bool> _steamRestartHint = new(false);
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.off"));
    private readonly Dictionary<string, ObservableValue<string>> _checkTexts = CheckSpec.All.ToDictionary(s => s.Id, _ => new ObservableValue<string>(""));
    private readonly Dictionary<string, CheckResult> _checkResults = new();
    private readonly object _checkGate = new();

    private IHostServices? _host;
    private DomainSet _domains = DomainSet.Default();
    private IHostResolver? _resolver;
    private ProxyEngine? _engine;
    private BypassController? _controller;
    private IConnectionProbe? _probe;
    private ISteamProbe? _steam;
    private SteamBypassSettings _settings = new();
    private Timer? _timer;
    private bool _fast;
    private SteamState _steamState = SteamState.NotRunning;
    private volatile string? _problem;
    private bool _checksRunning;
    private volatile bool _stopping;
    private readonly object _publishGate = new();
    private EventHandler? _exitHandler;
    private LogThrottle? _logThrottle;

    /// <summary>Creates the module for the real machine. Does nothing but set fields: no registry, no socket, no file.</summary>
    public SteamBypassModule() : this(Dependencies.Real()) { }

    internal SteamBypassModule(Dependencies deps) => _deps = deps;

    /// <inheritdoc />
    public ProbeResult Probe() => ProbeResult.Yes();   // needs only the current user's registry and loopback sockets

    /// <inheritdoc />
    public void Initialize(IHostServices host)
    {
        _host = host;
        _stopping = false;
        _logThrottle = new LogThrottle(20, TimeSpan.FromMinutes(1), _deps.Now);
        _settings = host.Settings.Load<SteamBypassSettings>();
        _autoOn.Value = _settings.AutoOn;
        _bypass.Value = false;

        _domains = DomainSet.Load(new[] { Path.Combine(host.DataDirectory, "steam-domains.txt"), Path.Combine(host.ModuleDirectory, "steam-domains.txt") });
        _resolver = _deps.Resolver();
        var dialer = _deps.Dialer?.Invoke(_domains, _resolver) ?? new UpstreamDialer(_resolver, _domains);
        _engine = new ProxyEngine(_domains, dialer, _deps.Listeners(), _deps.Options,
            log: message => ThrottledWarn(host, message), verbose: message => host.Log.Debug(message));
        var proxy = new SystemProxyManager(_deps.ProxyStore(), _deps.StateStore(Path.Combine(host.DataDirectory, "proxy-state.json")),
            $"http://127.0.0.1:{_deps.Options.ProxyPort}/proxy.pac");
        _controller = new BypassController(_engine, proxy, message => host.Log.Info(message), _deps.Now, () => _deps.PortAnswers(_deps.Options.ProxyPort));
        _probe = _deps.Probe();
        _steam = _deps.Steam();

        // A crash or kill of the previous run may have left Windows pointing at a proxy that is gone: undo that before anything else.
        _controller.RecoverStale();

        host.Metrics.Describe(new MetricDescriptor(ActiveMetric, "metric.active", MetricUnit.Count, "0"));
        host.Metrics.Describe(new MetricDescriptor(ErrorsMetric, "metric.errors", MetricUnit.Count, "0"));

        _bypass.Changed += OnBypassChanged;
        _autoOn.Changed += OnAutoOnChanged;
        _controller.Changed += OnControllerChanged;
        host.Localisation.LanguageChanged += RefreshTexts;
        host.PageVisible.Changed += OnWatchSignal;
        host.Metrics.SubscribersChanged += OnWatchSignal;
        RegisterCommands(host);

        // If the host ends the process without calling Shutdown, Windows must still not keep pointing at a dead proxy.
        var controller = _controller;
        _exitHandler = (_, _) => controller.RestoreOnExit();
        AppDomain.CurrentDomain.ProcessExit += _exitHandler;

        foreach (var spec in CheckSpec.All) _checkTexts[spec.Id].Value = host.Localisation.T("check.idle");
        _fast = IsWatched(host);
        RefreshTexts();
        _timer = new Timer(_ => Tick(), null, _fast ? FastPoll : SlowPoll, _fast ? FastPoll : SlowPoll);

        // The change is claimed here, before Initialize returns, so a flip right after start is refused instead of racing the auto-on.
        if (_settings.AutoOn && TryBeginChange(wanted: true, out _) is { } autoTicket) RunBackground(() => AutoTurnOn(autoTicket));
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        var host = _host;
        _stopping = true;
        var timer = _timer;
        _timer = null;
        timer?.Dispose();

        _bypass.Changed -= OnBypassChanged;
        _autoOn.Changed -= OnAutoOnChanged;
        if (_controller is not null) _controller.Changed -= OnControllerChanged;
        if (host is not null)
        {
            host.Localisation.LanguageChanged -= RefreshTexts;
            host.PageVisible.Changed -= OnWatchSignal;
            host.Metrics.SubscribersChanged -= OnWatchSignal;
        }
        if (_exitHandler is not null) AppDomain.CurrentDomain.ProcessExit -= _exitHandler;
        _exitHandler = null;

        // The rule of the module: whatever turned the bypass on, stopping puts the Windows setting back. Restore first, then stop the proxy.
        try
        {
            var result = _controller?.TurnOffAndClose();
            if (result is { Ok: false }) host?.Log.Error("The Windows proxy setting could not be restored at shutdown: " + result.Error);
        }
        catch (Exception ex) { host?.Log.Error("Turning the bypass off at shutdown failed", ex); }

        // Under the publish lock: a worker that is publishing right now has finished, and one that comes later sees _stopping.
        try { lock (_publishGate) { host?.Metrics.Withdraw(ActiveMetric); host?.Metrics.Withdraw(ErrorsMetric); } } catch (Exception) { }
        SetSwitch(false);
        (_resolver as IDisposable)?.Dispose();
        _resolver = null;
        _engine = null;
        _controller = null;
        _probe = null;
        _steam = null;
    }

    // ------------------------------------------------------------------ the switch, the setting

    /// <summary>
    /// The right to change the bypass. Whoever holds the ticket in <see cref="_activeChange"/> is the one change in progress; only that
    /// holder may release it (<see cref="EndChange"/>). A worker that lost the right, or released it already, cannot clear someone else's.
    /// </summary>
    internal sealed class ChangeTicket
    {
        internal ChangeTicket(bool wanted) => Wanted = wanted;

        /// <summary>True for a change to On, false for a change to Off.</summary>
        public bool Wanted { get; }
    }

    private readonly object _changeGate = new();
    private ChangeTicket? _activeChange;

    /// <summary>
    /// Claims the one change slot. Returns the ticket, or null when a change is already running (then <paramref name="inProgressWanted"/> says
    /// what that change is doing). It never touches the controller, so it never waits for a change that is stuck in the registry.
    /// </summary>
    internal ChangeTicket? TryBeginChange(bool wanted, out bool inProgressWanted)
    {
        lock (_changeGate)
        {
            if (_activeChange is { } running)
            {
                inProgressWanted = running.Wanted;
                return null;
            }
            inProgressWanted = wanted;
            return _activeChange = new ChangeTicket(wanted);
        }
    }

    /// <summary>Gives the change slot back, but only if <paramref name="ticket"/> still is the holder. Anyone else is ignored.</summary>
    internal void EndChange(ChangeTicket ticket)
    {
        lock (_changeGate)
            if (ReferenceEquals(_activeChange, ticket)) _activeChange = null;
    }

    /// <summary>True while some change (the switch, a verb, auto-on) holds the slot.</summary>
    internal bool ChangeInProgress { get { lock (_changeGate) return _activeChange is not null; } }

    /// <summary>
    /// The user flipped the switch. The host has already shown the exact effect and asked (the row carries a hardware effect), so this
    /// does the work, on a pool thread: it never blocks the caller, not even while another change is stuck. Only one change runs at a time:
    /// a flip to what is already in progress is nothing new, and a flip to the opposite is refused ("A change is already in progress")
    /// and the switch goes back to what is in progress or real. Refusing is safer than queueing: a queued change would run later than
    /// the user's confirmation, on a Windows setting and a state that may have moved on.
    /// </summary>
    private void OnBypassChanged()
    {
        if (_programmaticSwitch > 0) return;   // our own sync of the switch
        if (_controller is null || _stopping) return;
        bool wanted = _bypass.Value;
        var ticket = TryBeginChange(wanted, out bool inProgressWanted);
        if (ticket is null)
        {
            if (inProgressWanted != wanted) RefuseFlip();
            return;
        }
        RunBackground(() => SwitchWorker(ticket));
    }

    private void RefuseFlip()
    {
        var host = _host;
        try
        {
            host?.Log.Info("The Steam bypass switch was flipped while a change was in progress; the flip was refused.");
            host?.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.busy"), ToastKind.Warning));
        }
        catch (Exception ex) { host?.Log.Warn("Showing the refusal failed", ex); }
        // Not here: this runs inside the setter of the switch, and setting it again from its own change event would reach the host's
        // listeners in the wrong order. A pool thread puts it back once the event has been delivered.
        RunBackground(ReconcileSwitch);
    }

    /// <summary>What the switch should show: the change that is running (it is about to be true), or else what is real.</summary>
    private bool SwitchShouldShow()
    {
        lock (_changeGate)
            if (_activeChange is { } running) return running.Wanted;
        return _controller?.IsOn ?? false;
    }

    /// <summary>
    /// Puts the switch back after a refused flip. The answer can go stale between looking and setting (the change that was running may
    /// end, and a new one start, in that gap), so it looks again after setting and repeats until the two agree.
    /// </summary>
    private void ReconcileSwitch()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            bool target = SwitchShouldShow();
            SetSwitch(target);
            if (SwitchShouldShow() == target) return;
        }
    }

    private int _workersStarted;
    private int _workersFinished;

    /// <summary>True when every background step that was started (switch workers, auto-on, switch reverts) has finished its last step. Tests only.</summary>
    internal bool SwitchWorkersIdleForTests => Volatile.Read(ref _workersFinished) == Volatile.Read(ref _workersStarted);

    private void RunBackground(Action work)
    {
        Interlocked.Increment(ref _workersStarted);
        _ = Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { _host?.Log.Error("A background step of the Steam bypass failed", ex); }
            finally { Interlocked.Increment(ref _workersFinished); }
        });
    }

    /// <summary>
    /// Does one change and nothing else. It owns <paramref name="ticket"/>, releases it in <c>finally</c> (the owner only), and leaves the
    /// switch showing what is really the case, whatever happened.
    /// </summary>
    private void SwitchWorker(ChangeTicket ticket)
    {
        var host = _host;
        var controller = _controller;
        try
        {
            if (controller is null || host is null || _stopping) return;
            if (ticket.Wanted != controller.IsOn) ApplyOnce(controller, host, ticket.Wanted);
            SetSwitch(controller.IsOn);
        }
        catch (Exception ex)
        {
            host?.Log.Error("Switching the bypass failed", ex);
            if (host is not null) _problem = host.Localisation.F("problem.unexpected", ex.Message);
            try { if (controller is not null) SetSwitch(controller.IsOn); } catch (Exception) { }
        }
        finally
        {
            EndChange(ticket);
            try
            {
                RefreshTexts();
                PublishMetrics();
            }
            catch (Exception ex) { host?.Log.Warn("Refreshing the status after a switch failed", ex); }
        }
    }

    /// <summary>Turns the bypass on or off once. Returns false when it did not work (the page then says why).</summary>
    private bool ApplyOnce(BypassController controller, IHostServices host, bool wanted)
    {
        if (wanted)
        {
            var result = controller.TurnOn();
            if (result.Ok)
            {
                _problem = null;
                if (!result.DownloadsAvailable)
                    host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.no-downloads"), ToastKind.Warning));
                return true;
            }
            var text = ProblemText(host.Localisation, result);
            _problem = text;
            if (!_stopping) host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.failed", text), ToastKind.Error));
            return false;
        }
        var off = controller.TurnOff();
        if (off.Ok)
        {
            _problem = null;
            return true;
        }
        _problem = host.Localisation.F("problem.restore", off.Error ?? "");
        host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.restore-failed"), ToastKind.Error));
        return false;
    }

    // The change event of an ObservableValue is raised on the thread that set the value, so a marker on that thread says "this one is ours".
    // It is back to zero when SetSwitch returns; nothing is left behind.
    [ThreadStatic] private static int _programmaticSwitch;

    /// <summary>Sets the switch from inside the module without that counting as a flip of the user's.</summary>
    private void SetSwitch(bool value)
    {
        _programmaticSwitch++;
        try { _bypass.Value = value; }
        finally { _programmaticSwitch--; }
    }

    /// <summary>Makes the switch show what is really the case (the proxy may have failed to start, or a verb changed it).</summary>
    private void SyncSwitch()
    {
        var controller = _controller;
        if (controller is not null) SetSwitch(controller.IsOn);
    }

    private void OnAutoOnChanged()
    {
        _settings.AutoOn = _autoOn.Value;
        try { _host?.Settings.Save(_settings); }
        catch (Exception ex) { _host?.Log.Warn("Settings could not be saved", ex); }
    }

    private void AutoTurnOn(ChangeTicket ticket)
    {
        var host = _host;
        var controller = _controller;
        try
        {
            if (host is null || controller is null || _stopping) return;
            host.Log.Info("Turning the bypass on at start (setting)");
            var result = controller.TurnOn();
            if (!result.Ok)
            {
                _problem = ProblemText(host.Localisation, result);
                if (!_stopping) host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.failed", ProblemText(host.Localisation, result)), ToastKind.Error));
            }
        }
        catch (Exception ex) { host?.Log.Error("Turning the bypass on at start failed", ex); }
        finally
        {
            try { SyncSwitch(); } catch (Exception) { }
            EndChange(ticket);
            try { RefreshTexts(); PublishMetrics(); } catch (Exception ex) { host?.Log.Warn("Refreshing the status after auto-on failed", ex); }
        }
    }

    private void OnControllerChanged()
    {
        // The switch is not synced here: a user's newer flip must not be undone by the end of the work for an older one.
        RefreshTexts();
        PublishMetrics();
    }

    // ------------------------------------------------------------------ polling

    /// <summary>True when somebody can see the numbers: the page is on screen, or a metric of this module has a subscriber.</summary>
    internal static bool IsWatched(IHostServices host) =>
        host.PageVisible.Value || host.Metrics.SubscriberCount(ActiveMetric) > 0 || host.Metrics.SubscriberCount(ErrorsMetric) > 0;

    private void OnWatchSignal()
    {
        var host = _host;
        var timer = _timer;
        if (host is null || timer is null) return;
        bool watched = IsWatched(host);
        if (watched == _fast) return;
        _fast = watched;
        try { timer.Change(watched ? TimeSpan.Zero : SlowPoll, watched ? FastPoll : SlowPoll); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The last problem text shown on the page, or null (tests).</summary>
    internal string? LastFailureForTests => _problem;

    /// <summary>The interval the numbers are refreshed at now (tests).</summary>
    internal TimeSpan CurrentPollInterval => _fast ? FastPoll : SlowPoll;

    /// <summary>One tick: refresh the numbers, the Steam row and the metrics. Public to the tests, which call it instead of waiting for the timer.</summary>
    internal void Tick()
    {
        var host = _host;
        if (host is null || _stopping || _controller is null) return;
        try
        {
            UpdateSteamState();
            RefreshTexts();
            PublishMetrics();
        }
        catch (Exception ex)
        {
            host.Log.Warn("Refreshing the status failed", ex);
        }
    }

    private void UpdateSteamState()
    {
        var controller = _controller;
        var steam = _steam;
        if (controller is null || steam is null) return;
        // Looking at processes and the TCP table is only worth it while somebody sees the row.
        if (_host is { } host && !host.PageVisible.Value) return;
        var processes = steam.Processes();
        bool uses = controller.IsOn && processes.Count > 0 && steam.AnyConnectedTo(processes.Select(p => p.Pid).ToArray(), controller.Engine.ProxyPort);
        _steamState = SteamLink.Evaluate(controller.IsOn, controller.OnSince, processes, uses, _deps.Now());
    }

    /// <summary>Publishes the two readings while the bypass is on; takes them back from the bus while it is off, so the page says "not available".</summary>
    private void PublishMetrics()
    {
        var host = _host;
        var controller = _controller;
        if (host is null || controller is null) return;
        bool on = controller.IsOn;   // outside the publish lock: it may wait for a change that is running
        // One publisher at a time, and none once Shutdown has withdrawn the readings: a late worker must not bring them back.
        lock (_publishGate)
        {
            if (_stopping) return;
            if (on)
            {
                var c = controller.Engine.Counters;
                host.Metrics.Publish(ActiveMetric, c.Active, MetricUnit.Count);
                host.Metrics.Publish(ErrorsMetric, c.Failed, MetricUnit.Count);
            }
            else
            {
                host.Metrics.Withdraw(ActiveMetric);
                host.Metrics.Withdraw(ErrorsMetric);
            }
        }
    }

    /// <summary>Engine warnings and errors go to the log, at most 20 lines a minute: a failing network must not fill the log files.</summary>
    private void ThrottledWarn(IHostServices host, string message)
    {
        var throttle = _logThrottle;
        if (throttle is null || !throttle.Allow(out int left))
            return;
        if (left > 0) host.Log.Warn($"({left} more lines were left out in the last minute)");
        host.Log.Warn(message);
    }

    private static string ProblemText(ILocalisation loc, TurnOnResult result) => result.Failure switch
    {
        StartFailure.AddressInUse => loc.T("problem.address-in-use"),
        StartFailure.SettingFailed => loc.F("problem.setting", result.Detail ?? ""),
        _ => loc.F("problem.listen", result.Detail ?? ""),
    };
}
