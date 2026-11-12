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
    private readonly ObservableValue<string> _light = new("off");
    private readonly ObservableValue<string> _proxy = new("off");   // a Windows setting, not hardware: EffectKind.SystemSetting
    private readonly ObservableValue<string> _profile = new("Quiet nights");
    private readonly ObservableValue<string> _folder = new(@"C:\Users\someone\Documents\Rigbay\logs\recordings\2026-11\a-rather-long-folder-name-for-a-test");
    private readonly ObservableValue<string> _lastWrite = new("none yet");
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.line", "", 45));

    private IHostServices? _host;
    private IHotkeyBinding? _hotkey;
    private Timer? _timer;
    private bool _boost;
    private bool _beacon;
    private bool _pumpWithdrawn;
    private bool _pageVisible = true;
    private int _tick;

    private static readonly MetricId PumpMetric = new("demo-fan.pump");

    public ProbeResult Probe() => ProbeResult.Yes();

    public void Initialize(IHostServices host)
    {
        _host = host;
        _mode.Changed += OnModeChanged;
        _pl1.Changed += OnLimitChanged;
        _light.Changed += OnLightChanged;
        host.Localisation.LanguageChanged += UpdateState;
        _pageVisible = host.PageVisible.Value;
        host.PageVisible.Changed += OnPageVisibleChanged;
        host.Metrics.Describe(new MetricDescriptor(PumpMetric, "metric.pump", MetricUnit.Percent, "0"));

        _hotkey = host.Hotkeys.Register(new HotkeyRegistration("max-fans", "hotkey.max", HotkeyGesture.Parse("Ctrl+Alt+F9"), () => _ = AskMaxFans()));
        host.Tray.Add(new TrayItem("max", "tray.max", () => _ = AskMaxFans()));
        host.Commands.Register("status", "cmd.status", _ => CommandResult.Ok($"mode={_mode.Value} pl1={_pl1.Value:0}W boost={_boost} light={_light.Value} beacon={_beacon} page-visible={_pageVisible} pump={(_pumpWithdrawn ? "withdrawn" : "published")}"));
        // "sensor off" takes the pump reading back (withdraw), "sensor on" publishes it again: the page and the dashboard show "Not available" in between.
        host.Commands.Register("sensor", "cmd.sensor", ctx =>
        {
            if (ctx.Args.Count != 1 || ctx.Args[0] is not ("on" or "off")) return CommandResult.Usage(host.Localisation.T("cmd.sensor"));
            _pumpWithdrawn = ctx.Args[0] == "off";
            if (_pumpWithdrawn) host.Metrics.Withdraw(PumpMetric); else PublishPump(host);
            return CommandResult.Ok("pump " + (_pumpWithdrawn ? "withdrawn" : "published"));
        });
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
                    new ChoiceRow("row.mode.header", modes, _mode) { DescriptionKey = "row.mode.description", HardwareEffect = new HardwareEffect("effect.fan") { RevertDescriptionKey = "effect.fan-revert" } },
                    new CustomRow("curve", "row.curve.header") { DescriptionKey = "row.curve.description" },
                    new ExpanderRow("row.advanced.header", new Row[]
                    {
                        new NumberRow("row.ramp.header", _ramp, Minimum: 0, Maximum: 30, Step: 1) { DescriptionKey = "row.ramp.description", Unit = MetricUnit.Seconds },
                        new TextRow("row.profile.header", _profile) { DescriptionKey = "row.profile.description", MaxLength = 24, Apply = ApplyMode.ApplyRevert },
                        // a long value (a folder path) with a description: the layout must keep the label readable and the box a sensible width
                        new TextRow("row.folder.header", _folder) { DescriptionKey = "row.folder.description", MaxLength = 260 },
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
                        HardwareEffect = new HardwareEffect("effect.boost") { RevertDescriptionKey = "effect.boost-revert" },
                        RevertAsync = UnboostAsync,
                        RevertButtonKey = "row.boost.revert",
                    },
                    // a harmless pair: the revert (also a change) is not asked about again, the status line still says what was written
                    new ChoiceRow("row.light.header", new[] { new ChoiceOption("off", "light.off"), new ChoiceOption("blue", "light.blue"), new ChoiceOption("red", "light.red") }, _light)
                    {
                        DescriptionKey = "row.light.description",
                        HardwareEffect = new HardwareEffect("effect.light") { SafeRevert = true },
                    },
                    // a system setting rather than hardware: the host words the question and the revert for a Windows setting
                    new ChoiceRow("row.proxy.header", new[] { new ChoiceOption("off", "proxy.off"), new ChoiceOption("local", "proxy.local") }, _proxy)
                    {
                        DescriptionKey = "row.proxy.description",
                        HardwareEffect = new HardwareEffect("effect.proxy") { Kind = EffectKind.SystemSetting, RevertDescriptionKey = "effect.proxy-revert" },
                    },
                    new ButtonRow("row.beacon.header", "row.beacon.button", BeaconAsync)
                    {
                        DescriptionKey = "row.beacon.description",
                        HardwareEffect = new HardwareEffect("effect.beacon") { SafeRevert = true },
                        RevertAsync = UnbeaconAsync,
                    },
                    new ShortcutRow("row.hotkey.header", _hotkey!.Gesture) { DescriptionKey = "row.hotkey.description" },
                    new InfoRow(Severity.Warning, "row.info.header") { DescriptionKey = "row.info.description" },
                }),
                new RowGroup("group.readings", new Row[]
                {
                    new ReadingRow("row.temp.header", WellKnownMetrics.CpuTemp) { Decimals = 0 },
                    new ReadingRow("row.power.header", WellKnownMetrics.CpuPower) { Decimals = 1 },
                    new ReadingRow("row.usage.header", WellKnownMetrics.CpuUsage) { Decimals = 0 },
                    new ReadingRow("row.clock.header", WellKnownMetrics.CpuClock) { Decimals = 0 },
                    new ReadingRow("row.pl1-reading.header", WellKnownMetrics.CpuPl1) { Decimals = 0, Tile = TileHint.Never },
                    new ReadingRow("row.fan.header", WellKnownMetrics.CpuFan) { Decimals = 0 },
                    new ReadingRow("row.pump.header", PumpMetric) { Decimals = 0, Tile = TileHint.Never },
                    new ValueRow("row.last.header", _lastWrite) { DescriptionKey = "row.last.description" },
                }),
            },
            Links = new[] { new PageLink("link.docs", "https://github.com/rigbay-app/rigbay") },
            Summary = new PageSummary(_stateLine, new[] { WellKnownMetrics.CpuTemp, PumpMetric }),
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
        _light.Changed -= OnLightChanged;
        if (_host is not null) _host.PageVisible.Changed -= OnPageVisibleChanged;
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

    private void OnLightChanged()
    {
        _lastWrite.Value = $"status light = {_light.Value}";
        _host?.Log.Info("fake device: status light = " + _light.Value);
    }

    private void OnPageVisibleChanged()
    {
        _pageVisible = _host?.PageVisible.Value ?? true;
        _host?.Log.Info("page visible = " + _pageVisible);
    }

    private static void PublishPump(IHostServices host) => host.Metrics.Publish(PumpMetric, 40, MetricUnit.Percent);

    private Task<ActionResult> BeaconAsync(CancellationToken ct)
    {
        _beacon = true;
        _lastWrite.Value = "beacon on";
        return Task.FromResult(ActionResult.Ok(LocalisedText.Of("beacon.on")));
    }

    private Task<ActionResult> UnbeaconAsync(CancellationToken ct)
    {
        _beacon = false;
        _lastWrite.Value = "beacon off";
        return Task.FromResult(ActionResult.Ok(LocalisedText.Of("beacon.off")));
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
            // a pretend CPU: package power follows the temperature, load and clock wander on their own, PL1 is the limit the user set (a flat line to read against)
            host.Metrics.Publish(WellKnownMetrics.CpuPower, Math.Round(24 + (temp - 52) * 2.1 + 3 * Math.Sin(n / 2.3), 1), MetricUnit.Watt);
            host.Metrics.Publish(WellKnownMetrics.CpuUsage, Math.Round(Math.Clamp(38 + 24 * Math.Sin(n / 7.0) + 9 * Math.Sin(n / 1.7), 2, 98)), MetricUnit.Percent);
            host.Metrics.Publish(WellKnownMetrics.CpuClock, Math.Round((3500 + 650 * Math.Sin(n / 7.0) + 90 * Math.Sin(n / 1.9)) / 10) * 10, MetricUnit.Megahertz);
            host.Metrics.Publish(WellKnownMetrics.CpuPl1, _pl1.Value, MetricUnit.Watt);
            if (!_pumpWithdrawn) host.Metrics.Publish(PumpMetric, Math.Round(40 + 10 * Math.Sin(n / 6.0)), MetricUnit.Percent);
        }
        catch (Exception ex) { _host?.Log.Error("tick failed", ex); }   // an exception on our own timer thread would end the process
    }
}
