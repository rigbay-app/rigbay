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
    private readonly ObservableValue<string> _choice = new("auto");
    private readonly ObservableValue<bool> _choiceEnabled = new(true);
    private readonly ObservableValue<bool> _unavailableOpen = new(false);
    private readonly ObservableValue<string> _modelText = new("");
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
    private IHotkeyBinding? _hotkey;
    private PredatorSenseSettings _settings = new();
    private string? _modelName;
    private LastSet? _carried;
    private FanResult? _result;
    private bool _watching;

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
        _scheduler = new PollScheduler(_poller, TimeSpan.FromSeconds(_settings.PollSeconds), TimeSpan.FromSeconds(10),
            ex => host.Log.Error("Polling the sensors failed", ex));

        _poller.StateChanged += OnReadingsState;
        _poller.Polled += OnPolled;
        _fan.Changed += OnFanChanged;
        _fan.Checking += OnChecking;
        _pollSeconds.Changed += OnPollSecondsChanged;
        _restore.Changed += OnRestoreChanged;
        _choice.Changed += OnChoiceChanged;
        host.Localisation.LanguageChanged += RefreshTexts;

        // Starting at 0 means "off": the user binds a gesture in the page. A press goes through the approval service.
        _hotkey = host.Hotkeys.Register(new HotkeyRegistration("toggle-fan", "hotkey.toggle-fan", null, () => _ = OnHotkeyAsync()));
        RegisterCommands(host);

        // One poll now (about 70 ms, or one short timeout), so the page knows which fans this machine has.
        _poller.PollOnce();
        SyncChoice();
        RefreshTexts();
        _scheduler.Start(TimeSpan.FromSeconds(_settings.PollSeconds));
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        var host = _host;
        var scheduler = _scheduler;
        var poller = _poller;
        var fan = _fan;
        scheduler?.Stop();

        if (poller is not null)
        {
            poller.StateChanged -= OnReadingsState;
            poller.Polled -= OnPolled;
        }
        if (fan is not null)
        {
            fan.Changed -= OnFanChanged;
            fan.Checking -= OnChecking;
        }
        _pollSeconds.Changed -= OnPollSecondsChanged;
        _restore.Changed -= OnRestoreChanged;
        _choice.Changed -= OnChoiceChanged;
        if (host is not null) host.Localisation.LanguageChanged -= RefreshTexts;

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
        _hotkey = null;
    }

    // ------------------------------------------------------------------ reactions

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
    /// The user picked another mode in the drop-down. The host has already shown the effect and asked (that is what the row's
    /// hardware flag does), so this writes. It never blocks the caller: the write runs on a pool thread.
    /// </summary>
    private void OnChoiceChanged()
    {
        var fan = _fan;
        if (fan is null) return;
        var wanted = _choice.Value == "max" ? FanMode.Max : FanMode.Auto;
        if (wanted == (fan.Last?.Mode ?? FanMode.Auto)) return;   // our own sync, or nothing to do
        _ = Task.Run(() => ApplyFromRowAsync(fan, wanted));
    }

    private async Task ApplyFromRowAsync(FanController fan, FanMode wanted)
    {
        try
        {
            var result = await fan.ApplyAsync(wanted, _host?.StopToken ?? CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>Makes the drop-down show what Rigbay last set (Auto when it has set nothing).</summary>
    private void SyncChoice()
    {
        var fan = _fan;
        if (fan is null) return;
        _choice.Value = fan.Last?.Mode == FanMode.Max ? "max" : "auto";
    }

    private void ShowResult(FanResult result)
    {
        _result = result;
        _watching = false;
    }

    private string? SafeModel()
    {
        try { return _machine.ModelName(); }
        catch (Exception) { return null; }
    }
}
