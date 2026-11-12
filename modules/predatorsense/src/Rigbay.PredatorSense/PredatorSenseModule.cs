using Rigbay.Abstractions;
using Rigbay.PredatorSense.Fan;
using Rigbay.PredatorSense.Sensors;
using Rigbay.PredatorSense.Wmi;

namespace Rigbay.PredatorSense;

/// <summary>
/// The PredatorSense module: shows the sensors of an Acer Predator laptop and sets the fan mode to Auto or Maximum.
/// Lifecycle and wiring are here; the page is in <c>PredatorSenseModule.Page.cs</c>, the texts in
/// <c>PredatorSenseModule.Texts.cs</c>, verbs and hotkey in <c>PredatorSenseModule.Commands.cs</c>. What this module may send to
/// the firmware is fixed in <see cref="WmiAllowList"/>; see docs/predatorsense/MODULE.md.
/// </summary>
public sealed partial class PredatorSenseModule : IModule
{
    private readonly IMachineInfo _machine;
    private readonly Func<IAcerWmi> _wmiFactory;
    private readonly Func<DateTimeOffset>? _now;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;

    // Values the page shows. They live as long as the instance, so enabling again after disabling works.
    private readonly ObservableValue<string> _choice = new(ChoiceNotSet);
    private readonly ObservableValue<bool> _choiceEnabled = new(true);
    private readonly ObservableValue<string> _modelText = new("");
    private readonly ObservableValue<string> _opModeText = new("");
    private readonly ObservableValue<string> _opModeChoice = new("");
    private readonly ObservableValue<LocalisedText> _opModeLine = new(default);
    private readonly ObservableValue<bool> _readOnly = new(false);   // the performance-mode row is never enabled
    private readonly ObservableValue<LocalisedText> _fanLine = new(default);
    private readonly ObservableValue<string> _lastSetText = new("");
    private readonly ObservableValue<string> _checkText = new("");
    private readonly ObservableValue<double> _pollSeconds = new(2);
    private readonly ObservableValue<bool> _restore = new(true);
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.starting"));

    private IHostServices? _host;
    private IAcerWmi? _wmi;
    private IDisposable? _wmiOwner;
    private SensorPoller? _poller;
    private FanController? _fan;
    private PollScheduler? _scheduler;
    private OperationModeReader? _opMode;
    private PollScheduler? _opModeScheduler;
    private IHotkeyBinding? _hotkey;
    private PredatorSenseSettings _settings = new();
    private string? _modelName;
    private LastSet? _carried;
    private FanResult? _result;
    private bool _watching;
    private bool _noPrevious;
    private volatile IReadOnlyList<OperationMode>? _pageModes;   // the modes the last page was built with

    /// <summary>Creates the module for the real machine. Does nothing but set fields.</summary>
    public PredatorSenseModule() : this(new MachineInfo(), () => new AcerWmi(), null, null) { }

    /// <summary>Creates the module with its machine access replaced (tests).</summary>
    internal PredatorSenseModule(IMachineInfo machine, Func<IAcerWmi> wmiFactory, Func<DateTimeOffset>? now, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _machine = machine;
        _wmiFactory = wmiFactory;
        _now = now;
        _delay = delay;
    }

    /// <inheritdoc />
    public ProbeResult Probe()
    {
        var result = MachineProbe.Run(_machine);
        if (result.Supported) _modelName = SafeModel();
        return result;
    }

    /// <inheritdoc />
    public void Initialize(IHostServices host)
    {
        _host = host;
        _modelName ??= SafeModel();
        _settings = host.Settings.Load<PredatorSenseSettings>().Clamped();
        _pollSeconds.Value = _settings.PollSeconds;
        _restore.Value = _settings.RestoreAutoOnExit;
        _result = null;
        _watching = false;

        var inner = _wmiFactory();
        _wmiOwner = inner as IDisposable;
        _wmi = new GuardedAcerWmi(inner);
        _poller = new SensorPoller(_wmi, host.Metrics, host.Log, _now);
        _fan = new FanController(_wmi, _poller, message => host.Log.Info(message), _now, _delay, _carried);
        _scheduler = new PollScheduler(_poller, TimeSpan.FromSeconds(_settings.PollSeconds), TimeSpan.FromSeconds(PredatorSenseSettings.IdlePollSeconds),
            ex => host.Log.Error("Polling the sensors failed", ex));
        _scheduler.Watched = IsWatched(host);

        // The performance mode: read only. Its own reader and its own slow timer, which runs only while somebody looks at it.
        _opMode = new OperationModeReader(_wmi, host.Metrics, host.Log);
        _opModeScheduler = new PollScheduler(_opMode.PollOnce, TimeSpan.FromSeconds(PredatorSenseSettings.OpModePollSeconds), Timeout.InfiniteTimeSpan,
            ex => host.Log.Error("Reading the performance mode failed", ex));
        bool opWatched = IsOpModeWatched(host);
        _opModeScheduler.Watched = opWatched;

        // Say once what each metric is called, so the dashboard shows "CPU temperature" and not "cpu.temp".
        foreach (var info in SensorInfo.All)
            host.Metrics.Describe(new MetricDescriptor(info.Metric, SensorLabelKey(info.Sensor), info.Unit, "0"));
        host.Metrics.Describe(new MetricDescriptor(OperationModeReader.Metric, "sensor.opmode", MetricUnit.None, "0") { DescriptionKey = "sensor.opmode.description" });

        _poller.StateChanged += OnReadingsState;
        _poller.Polled += OnPolled;
        _opMode.Changed += RefreshTexts;
        _fan.Changed += OnFanChanged;
        _fan.Checking += OnChecking;
        _pollSeconds.Changed += OnPollSecondsChanged;
        _restore.Changed += OnRestoreChanged;
        _choice.Changed += OnChoiceChanged;
        host.Localisation.LanguageChanged += RefreshTexts;
        host.PageVisible.Changed += OnWatchSignal;
        host.Metrics.SubscribersChanged += OnWatchSignal;

        // Starting at 0 means "off": the user binds a gesture in the page. A press goes through the approval service.
        _hotkey = host.Hotkeys.Register(new HotkeyRegistration("toggle-fan", "hotkey.toggle-fan", null, () => _ = OnHotkeyAsync()));
        RegisterCommands(host);

        // One poll now (about 70 ms, or one short timeout), so the page knows which fans this machine has.
        _poller.PollOnce();
        // Which modes exist: once now. The current mode only when somebody looks (otherwise the first look reads it at once).
        _opMode.ReadSupported();
        if (opWatched) _opMode.ReadCurrent();
        SyncChoice();
        RefreshTexts();
        _scheduler.Start(TimeSpan.FromSeconds(_settings.PollSeconds));
        _opModeScheduler.Start(opWatched ? _opModeScheduler.FastInterval : Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        var host = _host;
        var scheduler = _scheduler;
        var poller = _poller;
        var fan = _fan;
        var opMode = _opMode;
        scheduler?.Stop();
        _opModeScheduler?.Stop();

        if (poller is not null)
        {
            poller.StateChanged -= OnReadingsState;
            poller.Polled -= OnPolled;
        }
        if (opMode is not null) opMode.Changed -= RefreshTexts;
        if (fan is not null)
        {
            fan.Changed -= OnFanChanged;
            fan.Checking -= OnChecking;
        }
        _pollSeconds.Changed -= OnPollSecondsChanged;
        _restore.Changed -= OnRestoreChanged;
        _choice.Changed -= OnChoiceChanged;
        if (host is not null)
        {
            host.Localisation.LanguageChanged -= RefreshTexts;
            host.PageVisible.Changed -= OnWatchSignal;
            host.Metrics.SubscribersChanged -= OnWatchSignal;
        }

        // Rigbay set Maximum and is stopping: give the fans back to the firmware. Two seconds waiting for a running
        // write plus two for this one fit the 5 seconds the host allows.
        if (fan is not null)
        {
            if (_restore.Value && fan.MaxIsSet) fan.RestoreAuto(TimeSpan.FromSeconds(2));
            _carried = fan.Last;
        }

        try { _wmiOwner?.Dispose(); } catch (Exception ex) { host?.Log.Warn("Releasing WMI failed", ex); }
        _wmiOwner = null;
        _wmi = null;
        _poller = null;
        _fan = null;
        _scheduler = null;
        _opModeScheduler = null;
        _opMode = null;
        _hotkey = null;
    }

    // ------------------------------------------------------------------ reactions

    /// <summary>
    /// True when somebody can see the readings: the module's page is on screen, or one of the sensor metrics has a subscriber
    /// (the dashboard bay, a recorder, another module). Otherwise the sensors are read at the slow rate.
    /// </summary>
    internal static bool IsWatched(IHostServices host)
    {
        if (host.PageVisible.Value) return true;
        foreach (var info in SensorInfo.All)
            if (host.Metrics.SubscriberCount(info.Metric) > 0) return true;
        return false;
    }

    /// <summary>
    /// The page was shown or left, or somebody (un)subscribed. Runs on the thread that changed it (often the UI thread): it only
    /// flips the scheduler's rate and asks for a poll now when somebody started to look; the read itself runs on the timer thread.
    /// </summary>
    private void OnWatchSignal()
    {
        var host = _host;
        if (host is null) return;
        if (_scheduler is { } scheduler)
        {
            bool watched = IsWatched(host);
            if (watched != scheduler.Watched)
            {
                scheduler.Watched = watched;
                if (watched) scheduler.PollSoon();
            }
        }
        if (_opModeScheduler is { } opScheduler)
        {
            bool watched = IsOpModeWatched(host);
            if (watched != opScheduler.Watched)
            {
                opScheduler.Watched = watched;
                if (watched) opScheduler.PollSoon();   // somebody looks now: read the mode at once, then every 10 s
            }
        }
    }

    /// <summary>
    /// True when somebody can see the performance mode: the module's page is on screen, or the metric <see cref="OperationModeReader.Metric"/>
    /// has a subscriber. When it is false the mode is not read at all.
    /// </summary>
    internal static bool IsOpModeWatched(IHostServices host) =>
        host.PageVisible.Value || host.Metrics.SubscriberCount(OperationModeReader.Metric) > 0;

    /// <summary>The interval the sensors are read at now (tests).</summary>
    internal TimeSpan? CurrentPollInterval => _scheduler?.CurrentInterval;

    /// <summary>True while the performance mode is being polled (page visible or the metric has a subscriber) (tests).</summary>
    internal bool? OpModePolling => _opModeScheduler?.Watched;

    /// <summary>What Rigbay set last, as the fan line starts with it ("Maximum at 14:02") (tests).</summary>
    internal string LastSetShown => _lastSetText.Value;

    /// <summary>How the fans answered the last change, the second part of the fan line; empty before one (tests).</summary>
    internal string CheckShown => _checkText.Value;

    /// <summary>The current performance mode in words ("Default", "Unknown") (tests).</summary>
    internal string OpModeShown => _opModeText.Value;

    private void OnReadingsState(ReadingsState state) => RefreshTexts();

    private void OnPolled(SensorSnapshot snapshot) => RefreshTexts();

    private void OnFanChanged()
    {
        SyncChoice();
        RefreshTexts();
    }

    private void OnChecking(FanMode mode)
    {
        _watching = true;
        RefreshTexts();
    }

    private void OnPollSecondsChanged()
    {
        double seconds = Math.Clamp(Math.Round(_pollSeconds.Value), PredatorSenseSettings.MinPollSeconds, PredatorSenseSettings.MaxPollSeconds);
        if (seconds != _pollSeconds.Value) { _pollSeconds.Value = seconds; return; }   // the setter raises Changed again with the clamped value
        _settings.PollSeconds = (int)seconds;
        if (_scheduler is { } scheduler) scheduler.FastInterval = TimeSpan.FromSeconds(seconds);
        SaveSettings();
    }

    private void OnRestoreChanged()
    {
        _settings.RestoreAutoOnExit = _restore.Value;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try { _host?.Settings.Save(_settings); }
        catch (Exception ex) { _host?.Log.Warn("Settings could not be saved", ex); }
    }

    /// <summary>
    /// The user picked another mode in the fan-mode choice. The host has already shown the effect and asked (that is what the row's
    /// hardware flag does), so this writes. It never blocks the caller: the write runs on a pool thread.
    /// </summary>
    private void OnChoiceChanged()
    {
        var fan = _fan;
        if (fan is null) return;
        var value = _choice.Value;

        if (value == ChoiceNotSet)
        {
            // "Not set" is a state, not a value: nothing is written for it. It arrives from our own sync (nothing set yet), or from a
            // Revert of the very first change, whose "previous" value is "Not set". Rigbay then does know what it set, so the row goes
            // back to that and says there is no earlier mode to return to.
            if (fan.Last is not null)
            {
                _host?.Log.Info("Fan mode: there is no earlier known mode to go back to; nothing was sent.");
                _noPrevious = true;
                _watching = false;
                SyncChoice();
                RefreshTexts();
            }
            return;
        }

        var wanted = value == ChoiceMax ? FanMode.Max : FanMode.Auto;
        // Our own sync, or nothing to do. With nothing set yet, "Auto" is a real request: the firmware's mode is unknown.
        if (fan.Last is { } last && wanted == last.Mode) return;
        _noPrevious = false;
        _ = Task.Run(() => ApplyFromRowAsync(fan, wanted));
    }

    private async Task ApplyFromRowAsync(FanController fan, FanMode wanted)
    {
        try
        {
            var result = await fan.ApplyAsync(wanted, _host?.StopToken ?? CancellationToken.None).ConfigureAwait(false);
            // The user chose it on the page and the host confirmed: the host shows the result in the window while they look at it,
            // as a toast otherwise, and always as a toast when it failed.
            if (_host is { } host) AnnounceFan(host, wanted, result, ToastOrigin.Page);
            ShowResult(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _host?.Log.Error("Setting the fan mode from the page failed", ex);
        }
        finally
        {
            SyncChoice();
            RefreshTexts();
        }
    }

    /// <summary>
    /// Makes the choice show what Rigbay last set, or nothing (<see cref="ChoiceNotSet"/>, not an option) while it has set nothing in this run.
    /// </summary>
    private void SyncChoice()
    {
        var fan = _fan;
        if (fan is null) return;
        _choice.Value = fan.Last is null ? ChoiceNotSet : fan.Last.Mode == FanMode.Max ? ChoiceMax : ChoiceAuto;
    }

    private void ShowResult(FanResult result)
    {
        _result = result;
        _noPrevious = false;
        _watching = false;
    }

    private string? SafeModel()
    {
        try { return _machine.ModelName(); }
        catch (Exception) { return null; }
    }
}
