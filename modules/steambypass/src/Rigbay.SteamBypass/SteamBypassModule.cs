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
    private bool _stopping;
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

        if (_settings.AutoOn) _ = Task.Run(AutoTurnOn);
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
            var result = _controller?.TurnOff();
            if (result is { Ok: false }) host?.Log.Error("The Windows proxy setting could not be restored at shutdown: " + result.Error);
        }
        catch (Exception ex) { host?.Log.Error("Turning the bypass off at shutdown failed", ex); }

        try { host?.Metrics.Withdraw(ActiveMetric); host?.Metrics.Withdraw(ErrorsMetric); } catch (Exception) { }
        _bypass.Value = false;
        (_resolver as IDisposable)?.Dispose();
        _resolver = null;
        _engine = null;
        _controller = null;
        _probe = null;
        _steam = null;
    }

    // ------------------------------------------------------------------ the switch, the setting

    private int _switching;

    /// <summary>
    /// The user flipped the switch. The host has already shown the exact effect and asked (the row carries a hardware effect), so this
    /// does the work, on a pool thread: it never blocks the caller. One worker at a time follows the switch until the bypass matches it,
    /// so a quick on, off, on ends where the user's last flip says, whatever order the work finishes in.
    /// </summary>
    private void OnBypassChanged()
    {
        var controller = _controller;
        if (controller is null || _stopping) return;
        if (_bypass.Value == controller.IsOn) return;   // our own sync
        StartSwitchWorker();
    }

    private void StartSwitchWorker()
    {
        if (Interlocked.CompareExchange(ref _switching, 1, 0) == 0) _ = Task.Run(SwitchWorker);
    }

    private void SwitchWorker()
    {
        var host = _host;
        try
        {
            while (true)
            {
                var controller = _controller;
                if (controller is null || host is null || _stopping) return;
                bool wanted = _bypass.Value;   // read now, not when the flip happened: a later flip wins
                if (wanted != controller.IsOn && !ApplyOnce(controller, host, wanted))
                {
                    _bypass.Value = controller.IsOn;   // it failed: the switch shows what is really the case
                    break;
                }
                // Done unless the user flipped again while the work ran; the flag is released first so a flip right now starts a new worker.
                Interlocked.Exchange(ref _switching, 0);
                if (_bypass.Value == controller.IsOn || Interlocked.CompareExchange(ref _switching, 1, 0) != 0) return;
            }
        }
        catch (Exception ex)
        {
            host?.Log.Error("Switching the bypass failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _switching, 0);
            RefreshTexts();
            PublishMetrics();
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
            host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.failed", text), ToastKind.Error));
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

    /// <summary>Makes the switch show what is really the case (the proxy may have failed to start, or a verb changed it).</summary>
    private void SyncSwitch()
    {
        var controller = _controller;
        if (controller is not null) _bypass.Value = controller.IsOn;
    }

    private void OnAutoOnChanged()
    {
        _settings.AutoOn = _autoOn.Value;
        try { _host?.Settings.Save(_settings); }
        catch (Exception ex) { _host?.Log.Warn("Settings could not be saved", ex); }
    }

    private void AutoTurnOn()
    {
        var host = _host;
        var controller = _controller;
        if (host is null || controller is null || _stopping) return;
        try
        {
            host.Log.Info("Turning the bypass on at start (setting)");
            var result = controller.TurnOn();
            if (!result.Ok)
            {
                _problem = ProblemText(host.Localisation, result);
                host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.failed", ProblemText(host.Localisation, result)), ToastKind.Error));
            }
        }
        catch (Exception ex) { host.Log.Error("Turning the bypass on at start failed", ex); }
        finally { SyncSwitch(); RefreshTexts(); PublishMetrics(); }
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
        if (controller.IsOn)
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
