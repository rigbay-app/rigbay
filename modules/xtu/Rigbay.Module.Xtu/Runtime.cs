using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

/// <summary>
/// One running instance of the module: owns the profile list, the state read from XTU, the timers and every registration
/// with the host. Created by <see cref="XtuModule.Initialize"/>; tests create it with fakes. Everything the module does on
/// its own threads is caught here, because an exception on a thread the module started would end the host process.
/// </summary>
internal sealed partial class XtuRuntime : IDisposable
{
    private readonly IHostServices _host;
    private readonly IXtuClient _client;
    private readonly ISystemProbe _sys;
    private readonly XtuPaths _paths;
    private readonly IPowerEnvironment _power;
    private readonly WriteLatch _latch;
    private readonly ApplyService _apply;
    private readonly MonitorSampler _sampler;
    private readonly AutoSwitcher _auto = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IDisposable> _disposables = new();

    private XtuSettings _settings = new();
    private List<XtuProfile> _xtuProfiles = new();
    private List<XtuProfile> _profiles = new();
    private XtuState? _state;
    private XtuException? _error;
    private Timer? _statusTimer;
    private Thread? _monitorThread;
    private int _statusBusy;
    private readonly ManualResetEventSlim _wake = new(false);
    private int _monitorFailures;
    private int _stateFailures;

    /// <summary>Failed monitor reads (or state reads) in a row after which the readings are taken off the bus.</summary>
    public const int FailureLimit = 3;

    public XtuRuntime(IHostServices host, IXtuClient client, ISystemProbe sys, XtuPaths paths, IPowerEnvironment power, WriteLatch? latch = null)
    {
        _host = host;
        _client = client;
        _sys = sys;
        _paths = paths;
        _power = power;
        _latch = latch ?? WriteLatch.Off;
        _apply = new ApplyService(client, host.Approval, host.Log, _latch);
        _sampler = new MonitorSampler(client, host.Metrics);
        _apply.Completed += OnApplyCompleted;
        InitObservables();
    }

    /// <summary>How long after waking from sleep the profile for the current power source is applied again.</summary>
    public TimeSpan ResumeDelay { get; set; } = TimeSpan.FromSeconds(4);

    public IReadOnlyList<XtuProfile> Profiles { get { lock (_gate) return _profiles.ToList(); } }
    public XtuState? State { get { lock (_gate) return _state; } }
    public XtuSettings Settings => _settings;
    internal ApplyService Apply => _apply;
    internal MonitorSampler Sampler => _sampler;

    // ---- start and stop -------------------------------------------------------------------------------------------------

    /// <summary>Reads settings and the first state, registers commands, hotkeys and tray items. Starts no timer (see <see cref="StartTimers"/>).</summary>
    public void Start()
    {
        _host.Log.Info(_latch.Reason is { } why ? $"writes are switched OFF ({why}); this session only reads XTU" : "writes are allowed (each change is confirmed first)");
        _settings = _host.Settings.Load<XtuSettings>();
        _sampler.PerCore = _settings.PublishPerCore;
        _suppressAuto = true;    // loading the saved values must not look like the user changing them
        try
        {
            _perCore.Value = _settings.PublishPerCore;
            _autoSwitch.Value = _settings.AutoSwitch;
        }
        finally { _suppressAuto = false; }

        LoadProfiles();
        Refresh();
        LoadAutoChoices();

        new XtuCommands(_client, _sys, _apply, _host.Localisation, () => Profiles).Register(_host.Commands);
        RegisterProfileEntries(_profiles);

        _power.Changed += OnPowerChanged;
        _power.Resumed += OnResumed;
        UpdateAutoLine();
        // The approval text of the profile choice is built in the current language when the page is built: build it again.
        _host.Localisation.LanguageChanged += RequestPageRefresh;

        // Say once what every metric is called, so the dashboard shows "CPU temperature" and not "cpu.temp".
        foreach (var descriptor in XtuMetrics.Descriptors) _host.Metrics.Describe(descriptor);

        // The monitor is read only while somebody looks (page visible or a subscriber) or asks in the background; these signals wake it up.
        _host.PageVisible.Changed += OnWatchSignal;
        _host.Metrics.SubscribersChanged += OnWatchSignal;
    }

    /// <summary>Starts the 5 second state refresh (and the backup power check) and the 1 Hz monitor thread.</summary>
    public void StartTimers()
    {
        _statusTimer = new Timer(_ => Guard("status refresh", StatusTick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        _monitorThread = new Thread(MonitorLoop) { IsBackground = true, Name = "xtu-monitor" };
        _monitorThread.Start();
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        _host.PageVisible.Changed -= OnWatchSignal;
        _host.Metrics.SubscribersChanged -= OnWatchSignal;
        _host.Localisation.LanguageChanged -= RequestPageRefresh;
        _wake.Set();
        _power.Changed -= OnPowerChanged;
        _power.Resumed -= OnResumed;
        _apply.Completed -= OnApplyCompleted;
        _statusTimer?.Dispose();
        _monitorThread?.Join(TimeSpan.FromSeconds(2));
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch (Exception ex) { _host.Log.Warn("dispose failed", ex); }
        }
        _disposables.Clear();
        try { _client.Dispose(); } catch (Exception ex) { _host.Log.Warn("closing the XTU channels failed", ex); }
        _power.Dispose();
    }

    // ---- profiles and state ---------------------------------------------------------------------------------------------

    /// <summary>Re-reads XTU's Profiles.xml and rebuilds the list (XTU profiles, then the ones made in Rigbay).</summary>
    public void LoadProfiles()
    {
        var fromXtu = ProfilesXml.Load(_paths.ProfilesXml);
        lock (_gate)
        {
            _xtuProfiles = fromXtu;
            _profiles = Catalog.Build(_state, fromXtu, _settings.Customs.Select(c => c.ToProfile()));
        }
    }

    /// <summary>Reads the active values from XTU and updates everything that shows them. Never throws.</summary>
    public void Refresh()
    {
        XtuState? state = null;
        XtuException? error = null;
        try { state = _client.ReadState(); }
        catch (XtuException ex) { error = ex; }
        catch (Exception ex) { error = new XtuException("err.unavailable", ex.Message); }

        bool withdrawLimits = false;
        lock (_gate)
        {
            _state = state ?? _state;
            _error = error;
            // PL1 and PL2 on the bus are the last values read from XTU; when XTU stays unreadable they are not true any more.
            if (state is not null) _stateFailures = 0;
            else if (++_stateFailures == FailureLimit) withdrawLimits = true;
            // The BIOS defaults come from the service, so rebuild the first entry when we have them.
            if (state is not null)
                _profiles = Catalog.Build(state, _xtuProfiles, _settings.Customs.Select(c => c.ToProfile()));
        }
        if (withdrawLimits)
        {
            _host.Metrics.Withdraw(XtuMetrics.Pl1);
            _host.Metrics.Withdraw(XtuMetrics.Pl2);
        }
        ShowState(state, error);
    }

    private XtuProfile? ActiveProfile()
    {
        lock (_gate) return Catalog.FindActive(_profiles, _error is null ? _state : null);
    }

    private void StatusTick()
    {
        if (Interlocked.Exchange(ref _statusBusy, 1) != 0) return;
        try
        {
            Refresh();
            UpdateAutoLine();
            AutoCheck(force: false);
        }
        finally { Volatile.Write(ref _statusBusy, 0); }
    }

    // ---- monitor --------------------------------------------------------------------------------------------------------

    /// <summary>How often the monitor is read when the page is open or somebody subscribed (the dashboard, a recorder, another module).</summary>
    public static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(1);

    private void MonitorLoop()
    {
        var stop = _stop.Token;
        long lastRead = Environment.TickCount64;
        long next = lastRead;
        bool failedBefore = false;
        bool idle = true;
        while (!stop.IsCancellationRequested)
        {
            // Reset first, then look: a signal that arrives after the reset is seen by the wait below, one before it is seen by the look.
            _wake.Reset();
            var demand = MonitorDemand();
            if (demand.Kind == DemandKind.Idle)
            {
                // Nobody looks and nobody asked in the background: do not read XTU. The bus keeps the last value (a reader checks its age, see RECORDER-NOTES.md).
                idle = true;
                if (WaitHandle.WaitAny(new[] { _wake.WaitHandle, stop.WaitHandle }) == 1) break;
                continue;
            }
            if (idle)
            {
                // What XTU would give after a pause may be old: connect again and skip samples until a value moves (the warm-up).
                idle = false;
                try { _sampler.Reset(); } catch (Exception) { }
                next = Environment.TickCount64;
            }
            long intervalMs = (long)demand.Interval.TotalMilliseconds;
            try
            {
                if (Environment.TickCount64 >= next)
                {
                    MonitorOnce();
                    MonitorSucceeded();
                    failedBefore = false;
                    lastRead = Environment.TickCount64;
                    // The first read after (re)connecting only starts the warm-up: with a slow demand do not wait a whole interval for the second one.
                    long step = _sampler.Warming ? Math.Min(intervalMs, (long)FastInterval.TotalMilliseconds) : intervalMs;
                    next += step;
                    if (next < lastRead - 1000) next = lastRead + step;   // far behind: reset the slot instead of bursting
                }
                else
                {
                    // Woken by a change of demand: keep the time of the last read, but never wait longer than the new interval asks.
                    next = Math.Min(next, lastRead + intervalMs);
                }
                var wait = next - Environment.TickCount64;
                if (wait > 0 && WaitHandle.WaitAny(new[] { _wake.WaitHandle, stop.WaitHandle }, (int)Math.Min(wait, int.MaxValue)) == 1) break;
            }
            catch (Exception ex)
            {
                // XTU gone or restarting: say so once, then try again in 5 s (the next read opens a new connection).
                if (!failedBefore) _host.Log.Warn("monitor read failed: " + ex.Message);
                failedBefore = true;
                MonitorFailed();
                try { _sampler.Reset(); } catch (Exception) { }
                if (stop.WaitHandle.WaitOne(5000)) break;
                next = Environment.TickCount64;
            }
        }
    }

    /// <summary>
    /// How the monitor should be read now (host API 0.1.4, <see cref="MetricDemand"/>): <see cref="DemandKind.Fast"/> (every second) when the page is on
    /// screen or one of the monitor metrics has a subscriber (the dashboard bay, a recorder, another module); <see cref="DemandKind.Background"/> when only
    /// a background reader asks (Watch), at max(1 s, asked interval); otherwise <see cref="DemandKind.Idle"/>, which for this module means do not read.
    /// The per-core metrics count only while the per-core option publishes them.
    /// </summary>
    internal DemandState MonitorDemand()
    {
        var ids = _settings.PublishPerCore ? XtuMetrics.Monitored.Concat(XtuMetrics.PerCore) : XtuMetrics.Monitored;
        return _host.Evaluate(ids, FastInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>True when somebody can see the readings or asked for them in the background. Otherwise the monitor is not read.</summary>
    internal bool MonitorWanted() => MonitorDemand().Kind != DemandKind.Idle;

    /// <summary>The page was shown or left, or somebody (un)subscribed: wake the monitor thread, which decides what to do. Cheap.</summary>
    private void OnWatchSignal()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>A monitor read worked.</summary>
    internal void MonitorSucceeded() => Volatile.Write(ref _monitorFailures, 0);

    /// <summary>A monitor read failed; at <see cref="FailureLimit"/> in a row the readings leave the bus until XTU answers again.</summary>
    internal void MonitorFailed()
    {
        if (Interlocked.Increment(ref _monitorFailures) == FailureLimit) _sampler.WithdrawAll();
    }

    /// <summary>One monitor read and publish (the sampler thread calls this once a second).</summary>
    public int MonitorOnce() => _sampler.Tick();

    // ---- apply ----------------------------------------------------------------------------------------------------------

    /// <summary>Applies the profile with this name; for hotkeys and tray items.</summary>
    public Task<ApplyResult> ApplyByNameAsync(string name, ApplyOrigin origin)
    {
        var profile = Catalog.FindByName(Profiles, name);
        if (profile is null)
        {
            Notify(LocalisedText.Of("toast.title"), LocalisedText.Of("msg.not-found", name), ToastKind.Error, ToastOrigin.Background);
            return Task.FromResult(new ApplyResult(ApplyStatus.Failed, new XtuProfile(name, 0, 0, false, 0, ProfileKind.Custom), Error: new XtuException("msg.not-found", name)));
        }
        return ApplyReportingAsync(profile, origin);
    }

    /// <summary>
    /// Applies a profile. A request dropped because another apply is running never reaches <c>Completed</c>, so it is announced
    /// here: a hotkey that does nothing without a word is the case this exists for.
    /// </summary>
    private async Task<ApplyResult> ApplyReportingAsync(XtuProfile profile, ApplyOrigin origin)
    {
        var result = await _apply.ApplyAsync(profile, origin, _stop.Token).ConfigureAwait(false);
        if (result.Status == ApplyStatus.Busy)
        {
            try { Announce(result, origin); }
            catch (Exception ex) { _host.Log.Error("announcing a busy apply", ex); }
        }
        return result;
    }

    private void OnApplyCompleted(ApplyResult result, ApplyOrigin origin)
    {
        try
        {
            Announce(result, origin);
            Refresh();
        }
        catch (Exception ex) { _host.Log.Error("after apply", ex); }
    }

    /// <summary>
    /// Says how an apply ended, by the rule in docs/APP.md ("Notifications"): the host picks the place (a message in the window
    /// while the user is looking at it and clicked it, a Windows toast otherwise); this method picks the content and the kind.
    /// Every success and every failure or refusal is told once. Declined is silent (the user said no). So is an auto-switch that
    /// found nothing to change.
    /// </summary>
    private void Announce(ApplyResult r, ApplyOrigin origin)
    {
        var name = r.Profile.Name;
        var from = origin == ApplyOrigin.PageConfirmed ? ToastOrigin.Page : ToastOrigin.Background;
        switch (r.Status)
        {
            case ApplyStatus.Applied:
                var body = origin == ApplyOrigin.Auto
                    ? LocalisedText.Of("toast.auto-applied", name, _host.Localisation.Resolve(LocalisedText.Of(_power.Line == PowerLine.Online ? "reason.ac" : "reason.battery")))
                    : LocalisedText.Of("toast.applied", name);
                Notify(LocalisedText.Of("toast.title"), body, ToastKind.Success, from);
                break;
            case ApplyStatus.AlreadyActive:
                // The user asked (page, hotkey, tray item, verb) and nothing changed: say so. The automatic rule stays quiet.
                if (origin != ApplyOrigin.Auto)
                    Notify(LocalisedText.Of("toast.title"), LocalisedText.Of("msg.already-named", name), ToastKind.Info, from);
                break;
            case ApplyStatus.Declined:
                break;   // the user said no: nothing to tell
            case ApplyStatus.Busy:
                Notify(LocalisedText.Of("toast.title-failed"), LocalisedText.Of("msg.busy"), ToastKind.Warning, from);
                break;
            default:
                var text = r.Error!.ToText();
                Notify(LocalisedText.Of("toast.title-failed"), text, ToastKind.Error, from);
                break;
        }
    }

    private void Notify(LocalisedText title, LocalisedText body, ToastKind kind, ToastOrigin origin)
        => _host.Toast.Show(new ToastMessage(title, body, kind) { Origin = origin });

    // ---- auto-switch ----------------------------------------------------------------------------------------------------

    private void OnPowerChanged() => Guard("power change", () => { UpdateAutoLine(); AutoCheck(force: false); });

    private void OnResumed()
    {
        Guard("resume", () =>
        {
            if (!_settings.AutoSwitch) return;
            // Power limits can fall back to the firmware defaults around sleep: apply again shortly after waking.
            Task.Delay(ResumeDelay, _stop.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Guard("resume re-apply", () => AutoCheck(force: true));
            }, TaskScheduler.Default);
        });
    }

    /// <summary>The auto-switch rule. Applies without asking again: the user's consent was given with the setting.</summary>
    /// <param name="force">Apply even when the power source did not change.</param>
    /// <param name="origin"><see cref="ApplyOrigin.Auto"/> when the machine did it by itself (a power change, a resume, the timer); <see cref="ApplyOrigin.PageConfirmed"/> when the user's own click on the page asked for it now.</param>
    public void AutoCheck(bool force, ApplyOrigin origin = ApplyOrigin.Auto)
    {
        var decision = _auto.Check(_settings.AutoSwitch, _settings.AcProfile, _settings.BatteryProfile, _power.Line, force, Profiles);
        switch (decision.Outcome)
        {
            case AutoOutcome.Apply:
                _ = ApplyReportingAsync(decision.Profile!, origin);
                break;
            case AutoOutcome.None when origin == ApplyOrigin.PageConfirmed:
                // The user changed an auto-switch setting and nothing is applied now (no profile for this power source, or the switch is off).
                Notify(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.auto-nothing"), ToastKind.Info, ToastOrigin.Page);
                break;
            case AutoOutcome.Missing:
                var reason = LocalisedText.Of(decision.OnAc ? "reason.ac" : "reason.battery");
                var text = LocalisedText.Of("msg.auto-missing", decision.MissingName!, _host.Localisation.Resolve(reason));
                Notify(LocalisedText.Of("toast.title-failed"), text, ToastKind.Warning, origin == ApplyOrigin.Auto ? ToastOrigin.Background : ToastOrigin.Page);
                break;
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { _host.Log.Error(what + " failed", ex); }
    }

    private void SaveSettings()
    {
        try { _host.Settings.Save(_settings, 1); }
        catch (IOException ex) { _host.Log.Error("saving settings failed", ex); }
    }

    /// <summary>
    /// Asks the host to build the page again after the profile list or a profile's values changed (the choices, the hotkey
    /// rows and the expanders of your own profiles are made from the list when the page is built).
    /// </summary>
    private void RequestPageRefresh() => _host.RequestPageRefresh();
}
