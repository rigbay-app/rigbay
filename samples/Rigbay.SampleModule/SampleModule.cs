using Rigbay.Abstractions;

namespace Rigbay.SampleModule;

/// <summary>The settings object: one class, public settable properties, defaults in the initialisers.</summary>
public sealed class SampleSettings
{
    public bool Echo { get; set; }
    public string Mode { get; set; } = "normal";
    public double Limit { get; set; } = 45;
    public string Label { get; set; } = "";
}

/// <summary>
/// A minimal real module. It shows the lifecycle (Probe, Initialize, GetPageModel, Shutdown), settings, a hotkey, a tray
/// item, commands, the metrics bus and one row of every kind. It never touches the machine.
/// </summary>
public sealed class SampleModule : IModule
{
    private const string TicksMetric = "sample.ticks";

    // The values the page shows. They live as long as the instance, so enabling again after disabling works.
    private readonly ObservableValue<bool> _echo = new(false);
    private readonly ObservableValue<string> _mode = new("normal");
    private readonly ObservableValue<double> _limit = new(45);
    private readonly ObservableValue<string> _label = new("");
    private readonly ObservableValue<string> _status = new("");
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.line", 0));
    private readonly ObservableValue<bool> _paused = new(false);

    // Pretend hardware rows: they only hold a value, nothing on the machine changes. They show how the host asks before a
    // hardware change and again before its revert (profile), and the opt-in that skips the second question (light).
    private readonly ObservableValue<string> _profile = new("auto");
    private readonly ObservableValue<bool> _light = new(false);
    private bool _boost;

    // A list that changes while the module runs: the page is built once, so changing it asks the host for a new page.
    private readonly List<string> _devices = new() { "Sensor 1" };

    private IHostServices? _host;
    private IHotkeyBinding? _hotkey;
    private Timer? _timer;
    private int _ticks;
    private bool _fast = true; // the poll rate the timer runs at (see UpdatePollRate)
    private readonly object _rateLock = new(); // guards _fast, _timer and every re-arm of the timer

    // The poll rate pattern: poll fast only while somebody can see the result, slow down otherwise.
    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(10);

    // Constructors do nothing but set fields.
    public SampleModule() { }

    // Read-only check whether this machine can use the module. A module for specific hardware would look for its
    // service or WMI class here and return ProbeResult.No("probe.reason-key") when it is missing.
    public ProbeResult Probe() => ProbeResult.Yes();

    public void Initialize(IHostServices host)
    {
        _host = host;

        var settings = host.Settings.Load<SampleSettings>();
        _echo.Value = settings.Echo;
        _mode.Value = settings.Mode;
        _limit.Value = settings.Limit;
        _label.Value = settings.Label;

        // Handlers are removed again in Shutdown, so Initialize can run more than once.
        _echo.Changed += OnValueChanged;
        _mode.Changed += OnValueChanged;
        _limit.Changed += OnValueChanged;
        _label.Changed += OnValueChanged;

        _hotkey = host.Hotkeys.Register(new HotkeyRegistration("toggle-echo", "hotkey.toggle", HotkeyGesture.Parse("Ctrl+Alt+F11"),
            () => _echo.Value = !_echo.Value));

        var tray = new TrayItem("toggle", "tray.toggle", () => _echo.Value = !_echo.Value) { Checkable = true };
        tray.Checked.Value = _echo.Value;
        _echo.Changed += () => tray.Checked.Value = _echo.Value;
        host.Tray.Add(tray);

        host.Commands.Register("status", "cmd.status", _ => CommandResult.Ok($"echo={_echo.Value} mode={_mode.Value} limit={_limit.Value} ticks={_ticks}"));
        host.Commands.Register("echo", "cmd.echo", ctx =>
            ctx.Args.Count == 0 ? CommandResult.Usage(host.Localisation.T("cmd.usage")) : CommandResult.Ok(string.Join(' ', ctx.Args)));

        // Say once what the metric is called, so the dashboard can show "Ticks" instead of "sample.ticks".
        // The description is optional. Both keys are relative to this module; other modules (a recorder) read the resolved texts
        // with host.Metrics.GetMetricInfo(id), in the language the user has now, so you never resolve them for others.
        host.Metrics.Describe(new MetricDescriptor(new MetricId(TicksMetric), "metric.ticks", MetricUnit.Count, "0") { DescriptionKey = "metric.ticks.description" });

        _paused.Changed += OnPausedChanged;

        UpdateStatus();
        // A thread of our own: exceptions on it are NOT caught by the host, so the callback catches them itself.
        // The first tick comes after a second whatever the rate; the poll rate is adjusted after each tick.
        // The timer fires once and each tick arms the next one (like a poll that ends before the next starts). A periodic timer
        // would queue a second callback while the first is still waiting for a busy thread pool, and the module would tick twice
        // in a row before the first tick had slowed the rate down (task FL2).
        lock (_rateLock)
        {
            _fast = true;
            _timer = new Timer(OnTick, null, FastPoll, Timeout.InfiniteTimeSpan);
        }
        host.PageVisible.Changed += UpdatePollRate;
        host.Metrics.SubscribersChanged += UpdatePollRate;
    }

    // Fast while the module's page is on screen or somebody subscribed to the metric (the dashboard does, so does a
    // recorder); slow when nobody can see it. Both signals call this on the thread that changed them (often the UI
    // thread): it only retimes the timer, which is cheap. Anything slow would be handed to another thread.
    private void UpdatePollRate()
    {
        var host = _host;
        if (host is null) return;
        bool wanted = host.PageVisible.Value || host.Metrics.SubscriberCount(new MetricId(TicksMetric)) > 0;
        lock (_rateLock)
        {
            if (_timer is null || wanted == _fast) return;
            _fast = wanted;
            try { _timer.Change(wanted ? TimeSpan.Zero : SlowPoll, Timeout.InfiniteTimeSpan); } // zero: read now when somebody starts looking
            catch (ObjectDisposedException) { /* shut down */ }
        }
    }

    // Arms the next tick at the rate that applies now. Called when a tick ends, so ticks never queue up behind each other.
    private void ArmNextTick()
    {
        lock (_rateLock)
        {
            try { _timer?.Change(_fast ? FastPoll : SlowPoll, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* shut down */ }
        }
    }

    // Pausing takes the metric back instead of leaving the last number on the page: ReadingRow shows "not available".
    private void OnPausedChanged()
    {
        if (_paused.Value) _host?.Metrics.Withdraw(new MetricId(TicksMetric));
    }

    public PageModel? GetPageModel(IHostServices host)
    {
        var mode = new[]
        {
            new ChoiceOption("quiet", "mode.quiet"),
            new ChoiceOption("normal", "mode.normal"),
            new ChoiceOption("fast", "mode.fast"),
        };

        return new PageModel
        {
            Icon = "Repair",
            Groups = new[]
            {
                new RowGroup("group.basic", new Row[]
                {
                    new ToggleRow("row.echo.header", _echo) { DescriptionKey = "row.echo.description" },
                    new ChoiceRow("row.mode.header", mode, _mode) { DescriptionKey = "row.mode.description" },
                    new TextRow("row.label.header", _label)
                    {
                        DescriptionKey = "row.label.description",
                        PlaceholderKey = "label.placeholder",
                        MaxLength = 20,
                        Apply = ApplyMode.ApplyRevert,
                        Validate = text => text.Equals("reserved", StringComparison.OrdinalIgnoreCase) ? LocalisedText.Of("label.reserved") : null,
                    },
                    new ShortcutRow("row.shortcut.header", _hotkey!.Gesture) { DescriptionKey = "row.shortcut.description" },
                }),
                new RowGroup("group.safe", new Row[]
                {
                    // A number that would write to hardware: apply-or-revert, confirmation text, validated range.
                    new NumberRow("row.limit.header", _limit, Minimum: 15, Maximum: 65, Step: 5)
                    {
                        DescriptionKey = "row.limit.description",
                        Unit = MetricUnit.Watt,
                        Apply = ApplyMode.ApplyRevert,
                        HardwareEffect = new HardwareEffect("effect.limit"),
                    },
                    new ButtonRow("row.reset.header", "row.reset.button", ResetAsync) { DescriptionKey = "row.reset.description" },
                    // Hardware rows. Every call that changes hardware is asked about, Revert included: reverting profile
                    // from Maximum to Auto and back is asked with the exact old and new value. The light is harmless in
                    // both directions, so it opts out of the second question with SafeRevert (default false).
                    new ChoiceRow("row.profile.header", new[] { new ChoiceOption("auto", "profile.auto"), new ChoiceOption("max", "profile.max") }, _profile)
                    {
                        DescriptionKey = "row.profile.description",
                        HardwareEffect = new HardwareEffect("effect.profile") { RevertDescriptionKey = "effect.profile.revert" },
                    },
                    new ToggleRow("row.light.header", _light)
                    {
                        DescriptionKey = "row.light.description",
                        HardwareEffect = new HardwareEffect("effect.light") { SafeRevert = true },
                    },
                    new ButtonRow("row.boost.header", "row.boost.button", BoostAsync)
                    {
                        DescriptionKey = "row.boost.description",
                        HardwareEffect = new HardwareEffect("effect.boost"),
                        RevertAsync = BoostOffAsync,
                        RevertButtonKey = "row.boost.revert",
                    },
                    new ExpanderRow("row.more.header", new Row[]
                    {
                        new InfoRow(Severity.Info, "row.note.header") { DescriptionKey = "row.note.description" },
                    }) { DescriptionKey = "row.more.description" },
                }),
                new RowGroup("group.live", new Row[]
                {
                    new ReadingRow("row.ticks.header", new MetricId(TicksMetric)) { DescriptionKey = "row.ticks.description", Decimals = 0 },
                    new ToggleRow("row.pause.header", _paused) { DescriptionKey = "row.pause.description" },
                    new ValueRow("row.status.header", _status),
                    // Names the user or the machine decides are not UI wording: Text.Literal shows them as they are.
                    // The page is built once, so a list that changes asks the host for a new page (AddDeviceAsync).
                    new ExpanderRow("row.devices.header", DeviceRows()) { DescriptionKey = "row.devices.description" },
                    new ButtonRow("row.adddevice.header", "row.adddevice.button", AddDeviceAsync) { DescriptionKey = "row.adddevice.description" },
                }),
            },
            Links = new[] { new PageLink("link.docs", "https://github.com/rigbay-app/rigbay/blob/main/docs/ABSTRACTIONS.md") },
            Summary = new PageSummary(_stateLine, new[] { new MetricId(TicksMetric) }),
        };
    }

    public void Shutdown()
    {
        lock (_rateLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        if (_host is { } host)
        {
            host.PageVisible.Changed -= UpdatePollRate;
            host.Metrics.SubscribersChanged -= UpdatePollRate;
        }
        _paused.Changed -= OnPausedChanged;
        _echo.Changed -= OnValueChanged;
        _mode.Changed -= OnValueChanged;
        _limit.Changed -= OnValueChanged;
        _label.Changed -= OnValueChanged;
        // Hotkeys, tray items, commands and subscriptions are removed by the host after Shutdown.
    }

    private Row[] DeviceRows()
    {
        lock (_devices)
            return _devices.Select(name => (Row)new InfoRow(Severity.Info, Text.Literal(name))).ToArray();
    }

    private Task<ActionResult> AddDeviceAsync(CancellationToken cancellationToken)
    {
        lock (_devices) _devices.Add($"Sensor {_devices.Count + 1}");
        _host?.RequestPageRefresh(); // the host calls GetPageModel again and shows the new list
        return Task.FromResult(ActionResult.Ok());
    }

    // Pretend hardware button: the host asks before it runs, and again before BoostOffAsync (its revert).
    private Task<ActionResult> BoostAsync(CancellationToken cancellationToken)
    {
        _boost = true;
        UpdateStatus();
        return Task.FromResult(ActionResult.Ok(LocalisedText.Of("boost.on")));
    }

    private Task<ActionResult> BoostOffAsync(CancellationToken cancellationToken)
    {
        _boost = false;
        UpdateStatus();
        return Task.FromResult(ActionResult.Ok(LocalisedText.Of("boost.off")));
    }

    private Task<ActionResult> ResetAsync(CancellationToken cancellationToken)
    {
        _paused.Value = false;
        _profile.Value = "auto";
        _light.Value = false;
        _boost = false;
        UpdateStatus();
        _echo.Value = false;
        _mode.Value = "normal";
        _limit.Value = 45;
        _label.Value = "";
        // A button on the page: the user is looking, so the host shows this in the window (a toast when the window is not in use).
        _host?.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.body"), ToastKind.Info) { Origin = ToastOrigin.Page });
        return Task.FromResult(ActionResult.Ok());
    }

    private void OnValueChanged()
    {
        var host = _host;
        if (host is null) return;
        host.Settings.Save(new SampleSettings { Echo = _echo.Value, Mode = _mode.Value, Limit = _limit.Value, Label = _label.Value });
        host.Log.Info($"echo={_echo.Value} mode={_mode.Value} limit={_limit.Value} label='{_label.Value}'");
        UpdateStatus();
    }

    private void OnTick(object? state)
    {
        try
        {
            var host = _host;
            if (host is null) return;
            UpdatePollRate(); // also here, so the first tick settles the rate for a page that is not open
            if (_paused.Value) return; // paused: the metric stays withdrawn
            int n = Interlocked.Increment(ref _ticks);
            host.Metrics.Publish(new MetricId(TicksMetric), n, MetricUnit.Count);
            _stateLine.Value = LocalisedText.Of("state.line", n);
        }
        catch (Exception ex)
        {
            _host?.Log.Error("tick failed", ex);
        }
        finally
        {
            ArmNextTick(); // whatever happened above, the next tick is armed (a paused module keeps its rhythm too)
        }
    }

    private void UpdateStatus() => _status.Value = $"{_mode.Value}, {_limit.Value:0} W" + (_echo.Value ? ", echo" : "") + (_boost ? ", boost" : "");
}
