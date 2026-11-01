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

        SetMessage(LocalisedText.Of("msg.none-yet"));
        LoadProfiles();
        Refresh();
        LoadAutoChoices();

        new XtuCommands(_client, _sys, _apply, _host.Localisation, () => Profiles).Register(_host.Commands);
        RegisterProfileEntries(_profiles);

        _power.Changed += OnPowerChanged;
        _power.Resumed += OnResumed;
        UpdatePowerText();

        // Say once what every metric is called, so the dashboard shows "CPU temperature" and not "cpu.temp".
        foreach (var descriptor in XtuMetrics.Descriptors) _host.Metrics.Describe(descriptor);

        // The monitor is read only while somebody looks (page visible or a subscriber); these signals wake it up.
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
            UpdatePowerText();
            AutoCheck(force: false);
        }
        finally { Volatile.Write(ref _statusBusy, 0); }
    }

    // ---- monitor --------------------------------------------------------------------------------------------------------

    private void MonitorLoop()
    {
        var stop = _stop.Token;
        var next = Environment.TickCount64;
        bool failedBefore = false;
        while (!stop.IsCancellationRequested)
        {
            if (!MonitorWanted())
            {
                // Nobody looks: do not read XTU. The bus keeps the last value (a reader checks its age, see RECORDER-NOTES.md).
                _wake.Reset();
                if (MonitorWanted()) continue;                       // somebody arrived between the check and the reset
                if (WaitHandle.WaitAny(new[] { _wake.WaitHandle, stop.WaitHandle }) == 1) break;
                if (!MonitorWanted()) continue;                      // a signal that changed nothing for us
                // What XTU would give after a pause may be old: connect again and skip samples until a value moves (the warm-up).
                try { _sampler.Reset(); } catch (Exception) { }
                next = Environment.TickCount64;
                continue;
            }
            try
            {
                MonitorOnce();
                MonitorSucceeded();
                failedBefore = false;
                next += 1000;
                var wait = next - Environment.TickCount64;
                if (wait < -1000) { next = Environment.TickCount64; wait = 0; }   // far behind: reset the slot instead of bursting
                if (wait > 0 && stop.WaitHandle.WaitOne((int)wait)) break;
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
    /// True when somebody can see the readings: the module's page is on screen, or one of the monitor metrics has a subscriber
    /// (the dashboard bay, a recorder, another module). Otherwise the monitor is not read.
    /// </summary>
    internal bool MonitorWanted()
    {
        if (_host.PageVisible.Value) return true;
        var bus = _host.Metrics;
        foreach (var id in XtuMetrics.Monitored)
            if (bus.SubscriberCount(id) > 0) return true;
        if (_settings.PublishPerCore)
            foreach (var id in XtuMetrics.PerCore)
                if (bus.SubscriberCount(id) > 0) return true;
        return false;
    }

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
            SetMessage(LocalisedText.Of("msg.not-found", name));
            _host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("msg.not-found", name), ToastKind.Error));
            return Task.FromResult(new ApplyResult(ApplyStatus.Failed, new XtuProfile(name, 0, 0, false, 0, ProfileKind.Custom), Error: new XtuException("msg.not-found", name)));
        }
        return _apply.ApplyAsync(profile, origin, _stop.Token);
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

    private void Announce(ApplyResult r, ApplyOrigin origin)
    {
        var name = r.Profile.Name;
        switch (r.Status)
        {
            case ApplyStatus.Applied:
                var after = r.After!;
                SetMessage(LocalisedText.Of("msg.applied-named", name, Fmt.N(after.Pl1), Fmt.N(after.Pl2), Fmt.N(after.Window)));
                if (origin != ApplyOrigin.PageConfirmed)
                    _host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.applied", name), ToastKind.Success));
                break;
            case ApplyStatus.AlreadyActive:
                SetMessage(LocalisedText.Of("msg.already-named", name));
                break;
            case ApplyStatus.Declined:
                SetMessage(LocalisedText.Of("msg.declined"));
                break;
            case ApplyStatus.Busy:
                SetMessage(LocalisedText.Of("msg.busy"));
                break;
            default:
                var text = r.Error!.ToText();
                SetMessage(LocalisedText.Of("msg.failed", _host.Localisation.Resolve(text)));
                _host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title-failed"), text, ToastKind.Error));
                break;
        }
    }

    // ---- auto-switch ----------------------------------------------------------------------------------------------------

    private void OnPowerChanged() => Guard("power change", () => { UpdatePowerText(); AutoCheck(force: false); });

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
    public void AutoCheck(bool force)
    {
        var decision = _auto.Check(_settings.AutoSwitch, _settings.AcProfile, _settings.BatteryProfile, _power.Line, force, Profiles);
        switch (decision.Outcome)
        {
            case AutoOutcome.Apply:
                _ = _apply.ApplyAsync(decision.Profile!, ApplyOrigin.Auto, _stop.Token);
                break;
            case AutoOutcome.Missing:
                var reason = LocalisedText.Of(decision.OnAc ? "reason.ac" : "reason.battery");
                var text = LocalisedText.Of("msg.auto-missing", decision.MissingName!, _host.Localisation.Resolve(reason));
                SetMessage(text);
                _host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title-failed"), text, ToastKind.Warning));
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
