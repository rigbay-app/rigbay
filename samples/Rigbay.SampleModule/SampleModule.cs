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

    private IHostServices? _host;
    private IHotkeyBinding? _hotkey;
    private Timer? _timer;
    private int _ticks;

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

        UpdateStatus();
        // A thread of our own: exceptions on it are NOT caught by the host, so the callback catches them itself.
        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
                    new ExpanderRow("row.more.header", new Row[]
                    {
                        new InfoRow(Severity.Info, "row.note.header") { DescriptionKey = "row.note.description" },
                    }) { DescriptionKey = "row.more.description" },
                }),
                new RowGroup("group.live", new Row[]
                {
                    new ReadingRow("row.ticks.header", new MetricId(TicksMetric)) { DescriptionKey = "row.ticks.description", Decimals = 0 },
                    new ValueRow("row.status.header", _status),
                }),
            },
            Links = new[] { new PageLink("link.docs", "https://github.com/rigbay-app/rigbay/blob/main/docs/ABSTRACTIONS.md") },
            Summary = new PageSummary(_stateLine, new[] { new MetricId(TicksMetric) }),
        };
    }

    public void Shutdown()
    {
        _timer?.Dispose();
        _timer = null;
        _echo.Changed -= OnValueChanged;
        _mode.Changed -= OnValueChanged;
        _limit.Changed -= OnValueChanged;
        _label.Changed -= OnValueChanged;
        // Hotkeys, tray items, commands and subscriptions are removed by the host after Shutdown.
    }

    private Task<ActionResult> ResetAsync(CancellationToken cancellationToken)
    {
        _echo.Value = false;
        _mode.Value = "normal";
        _limit.Value = 45;
        _label.Value = "";
        _host?.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.body"), ToastKind.Info));
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
            int n = Interlocked.Increment(ref _ticks);
            host.Metrics.Publish(new MetricId(TicksMetric), n, MetricUnit.Count);
            _stateLine.Value = LocalisedText.Of("state.line", n);
        }
        catch (Exception ex)
        {
            _host?.Log.Error("tick failed", ex);
        }
    }

    private void UpdateStatus() => _status.Value = $"{_mode.Value}, {_limit.Value:0} W" + (_echo.Value ? ", echo" : "");
}
