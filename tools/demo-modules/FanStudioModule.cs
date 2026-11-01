using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Rigbay.Abstractions;

namespace Rigbay.DemoModules;

/// <summary>
/// A pretend fan and power-limit module for a pretend laptop. Every "write" goes to a few fields in memory, so the host's
/// whole hardware flow can be shown without touching the machine: the fan mode and the power limit are marked with a
/// <see cref="HardwareEffect"/>, the limit is apply-or-revert and the fake device clamps it to 55 W (so the read-back differs from
/// what was typed), the boost button has a revert, and the fan curve is a custom control (level 2) built from XAML text.
/// </summary>
public sealed class FanStudioModule : IModule
{
    private const double DeviceMaxWatts = 55;   // the fake firmware refuses more than this, whatever the row allows

    private readonly ObservableValue<string> _mode = new("auto");
    private readonly ObservableValue<double> _pl1 = new(45);
    private readonly ObservableValue<double> _ramp = new(2);
    private readonly ObservableValue<string> _profile = new("Quiet nights");
    private readonly ObservableValue<string> _lastWrite = new("none yet");
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.line", "", 45));

    private IHostServices? _host;
    private IHotkeyBinding? _hotkey;
    private Timer? _timer;
    private bool _boost;
    private int _tick;

    public ProbeResult Probe() => ProbeResult.Yes();

    public void Initialize(IHostServices host)
    {
        _host = host;
        _mode.Changed += OnModeChanged;
        _pl1.Changed += OnLimitChanged;
        host.Localisation.LanguageChanged += UpdateState;

        _hotkey = host.Hotkeys.Register(new HotkeyRegistration("max-fans", "hotkey.max", HotkeyGesture.Parse("Ctrl+Alt+F9"), () => _ = AskMaxFans()));
        host.Tray.Add(new TrayItem("max", "tray.max", () => _ = AskMaxFans()));
        host.Commands.Register("status", "cmd.status", _ => CommandResult.Ok($"mode={_mode.Value} pl1={_pl1.Value:0}W boost={_boost}"));
        host.Commands.Register("max", "cmd.max", async ctx =>
            await host.Approval.ConfirmAsync(new HardwareEffect("effect.fan-max"), LocalisedText.Of("detail.max"), ctx.CancellationToken)
                ? Apply("max") : CommandResult.Failed(host.Localisation.T("cmd.refused")));

        UpdateState();
        _timer = new Timer(OnTick, null, 0, 1000);
    }

    public PageModel? GetPageModel(IHostServices host)
    {
        var modes = new[] { new ChoiceOption("auto", "mode.auto"), new ChoiceOption("max", "mode.max"), new ChoiceOption("custom", "mode.custom") };
        return new PageModel
        {
            Icon = "",
            Groups = new[]
            {
                new RowGroup("group.cooling", new Row[]
                {
                    new ChoiceRow("row.mode.header", modes, _mode) { DescriptionKey = "row.mode.description", HardwareEffect = new HardwareEffect("effect.fan") },
                    new CustomRow("curve", "row.curve.header") { DescriptionKey = "row.curve.description" },
                    new ExpanderRow("row.advanced.header", new Row[]
                    {
                        new NumberRow("row.ramp.header", _ramp, Minimum: 0, Maximum: 30, Step: 1) { DescriptionKey = "row.ramp.description", Unit = MetricUnit.Seconds },
                        new TextRow("row.profile.header", _profile) { DescriptionKey = "row.profile.description", MaxLength = 24, Apply = ApplyMode.ApplyRevert },
                    }) { DescriptionKey = "row.advanced.description" },
                }),
                new RowGroup("group.performance", new Row[]
                {
                    new NumberRow("row.pl1.header", _pl1, Minimum: 15, Maximum: 65, Step: 5)
                    {
                        DescriptionKey = "row.pl1.description",
                        Unit = MetricUnit.Watt,
                        Apply = ApplyMode.ApplyRevert,
                        HardwareEffect = new HardwareEffect("effect.pl1"),
                        Validate = w => w % 5 != 0 ? LocalisedText.Of("pl1.step") : null,
                    },
                    new ButtonRow("row.boost.header", "row.boost.button", BoostAsync)
                    {
                        DescriptionKey = "row.boost.description",
                        HardwareEffect = new HardwareEffect("effect.boost"),
                        RevertAsync = UnboostAsync,
                    },
                    new ShortcutRow("row.hotkey.header", _hotkey!.Gesture) { DescriptionKey = "row.hotkey.description" },
                    new InfoRow(Severity.Warning, "row.info.header") { DescriptionKey = "row.info.description" },
                }),
                new RowGroup("group.readings", new Row[]
                {
                    new ReadingRow("row.temp.header", WellKnownMetrics.CpuTemp) { Decimals = 0 },
                    new ReadingRow("row.fan.header", WellKnownMetrics.CpuFan) { Decimals = 0 },
                    new ValueRow("row.last.header", _lastWrite) { DescriptionKey = "row.last.description" },
                }),
            },
            Links = new[] { new PageLink("link.docs", "https://github.com/rigbay-app/rigbay") },
            Summary = new PageSummary(_stateLine, new[] { WellKnownMetrics.CpuTemp, WellKnownMetrics.CpuFan }),
        };
    }

    public object? CreateCustomControl(IHostServices host, string controlId)
    {
        if (controlId != "curve") return null;
        // Static markup, no code-behind, theme resources: it follows the window's theme, which brushes fetched from code do not.
        const string xaml = """
            <Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="360" Height="120">
              <Polyline Stroke="{ThemeResource AccentFillColorDefaultBrush}" StrokeThickness="2"
                        Points="0,112 90,100 180,64 270,24 360,12"/>
              <Line X1="0" Y1="119" X2="360" Y2="119" Stroke="{ThemeResource DividerStrokeColorDefaultBrush}" StrokeThickness="1"/>
              <TextBlock Canvas.Left="0" Canvas.Top="0" Text="rpm" FontSize="11" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
              <TextBlock Canvas.Left="318" Canvas.Top="100" Text="°C" FontSize="11" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
            </Canvas>
            """;
        return (UIElement)XamlReader.Load(xaml);
    }

    public void Shutdown()
    {
        _timer?.Dispose();
        _timer = null;
        _mode.Changed -= OnModeChanged;
        _pl1.Changed -= OnLimitChanged;
        if (_host is not null) _host.Localisation.LanguageChanged -= UpdateState;
    }

    // ---------------------------------------------------------------- the pretend device

    private void OnModeChanged()
    {
        _lastWrite.Value = $"fan mode = {_mode.Value}";
        _host?.Log.Info("fake device: fan mode = " + _mode.Value);
        UpdateState();
    }

    private void OnLimitChanged()
    {
        if (_pl1.Value > DeviceMaxWatts)
        {
            _host?.Log.Info($"fake device: refused {_pl1.Value:0} W, keeps {DeviceMaxWatts:0} W");
            _pl1.Value = DeviceMaxWatts;   // the firmware clamps; the host shows the value read back
            return;
        }
        _lastWrite.Value = $"PL1 = {_pl1.Value:0} W";
        _host?.Log.Info("fake device: PL1 = " + _pl1.Value.ToString("0") + " W");
        UpdateState();
    }

    private CommandResult Apply(string mode)
    {
        _mode.Value = mode;
        return CommandResult.Ok($"fan mode = {mode}");
    }

    private async Task AskMaxFans()
    {
        var host = _host;
        if (host is null) return;
        try
        {
            if (await host.Approval.ConfirmAsync(new HardwareEffect("effect.fan-max"), LocalisedText.Of("detail.max"), host.StopToken)) Apply("max");
            else host.Log.Info("max fans: not confirmed");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { host.Log.Error("max fans failed", ex); }
    }

    private Task<ActionResult> BoostAsync(CancellationToken ct)
    {
        _boost = true;
        _lastWrite.Value = "boost on";
        _host?.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.boost"), ToastKind.Info));
        return Task.FromResult(ActionResult.Ok(LocalisedText.Of("boost.on")));
    }

    private Task<ActionResult> UnboostAsync(CancellationToken ct)
    {
        _boost = false;
        _lastWrite.Value = "boost off";
        return Task.FromResult(ActionResult.Ok(LocalisedText.Of("boost.off")));
    }

    private void UpdateState()
    {
        var host = _host;
        if (host is null) return;
        _stateLine.Value = LocalisedText.Of("state.line", host.Localisation.T("mode." + _mode.Value), _pl1.Value.ToString("0"));
    }

    private void OnTick(object? state)
    {
        try
        {
            var host = _host;
            if (host is null) return;
            int n = Interlocked.Increment(ref _tick);
            double load = _boost ? 14 : 0;
            double temp = 58 + load + 6 * Math.Sin(n / 5.0) - (_mode.Value == "max" ? 7 : 0);
            double rpm = _mode.Value switch { "max" => 5200 + 60 * Math.Sin(n / 3.0), "custom" => 3600 + 80 * Math.Sin(n / 3.0), _ => 2400 + (temp - 55) * 60 + 40 * Math.Sin(n / 4.0) };
            host.Metrics.Publish(WellKnownMetrics.CpuTemp, Math.Round(temp), MetricUnit.Celsius);
            host.Metrics.Publish(WellKnownMetrics.CpuFan, Math.Round(rpm / 10) * 10, MetricUnit.Rpm);
        }
        catch (Exception ex) { _host?.Log.Error("tick failed", ex); }   // an exception on our own timer thread would end the process
    }
}
