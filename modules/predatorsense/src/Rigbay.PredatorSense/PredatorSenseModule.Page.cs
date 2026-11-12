using Rigbay.Abstractions;
using Rigbay.PredatorSense.Fan;
using Rigbay.PredatorSense.Sensors;

namespace Rigbay.PredatorSense;

// The page after the UI review of November 2026 (docs/ui-review/REVIEW-2026-11.md PS-1, PS-2, sketch 3): the fan mode is the page's main
// control with what Rigbay set and what the fans did as its line; each temperature is a tile with its own fan beside the number; the
// performance mode shows the modes this laptop offers with the current one chosen, READ ONLY; settings, then the facts in Details.
public sealed partial class PredatorSenseModule
{
    /// <summary>
    /// Value of the fan-mode choice while Rigbay has set nothing in this run. It is not one of the options, so the host selects none of them:
    /// the firmware cannot say which mode it is in. A state, not a value: it is never written, and a Revert to it writes nothing.
    /// </summary>
    internal const string ChoiceNotSet = "?";

    /// <summary>Id of the fan-mode option for <see cref="FanMode.Auto"/>.</summary>
    internal const string ChoiceAuto = "auto";

    /// <summary>Id of the fan-mode option for <see cref="FanMode.Max"/>.</summary>
    internal const string ChoiceMax = "max";

    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host)
    {
        var present = _poller?.Present ?? Array.Empty<Sensor>();
        bool has(Sensor s) => present.Contains(s);
        var cpuFan = SensorInfo.Of(Sensor.CpuFan).Metric;
        var gpuFan1 = SensorInfo.Of(Sensor.GpuFan1).Metric;
        var gpuFan2 = SensorInfo.Of(Sensor.GpuFan2).Metric;

        // Each temperature is a tile with its own fan as the second figure ("fan 5,200 rpm"); the fans themselves are rows, never tiles, never
        // charted again. A sensor that cannot be read is withdrawn from the bus, so its row says "not available" by itself; parts this machine
        // does not have answer "0" and are left out (only sensors that have reported are listed).
        var readings = new List<Row>
        {
            Temperature(Sensor.CpuTemp, new SecondaryReading("tile.fan", cpuFan)) with { HelpKey = "row.cpu-temp.help" },
            Temperature(Sensor.GpuTemp, has(Sensor.GpuFan1) && has(Sensor.GpuFan2) ? new SecondaryReading("tile.fans", gpuFan1, gpuFan2)
                : has(Sensor.GpuFan1) ? new SecondaryReading("tile.fan", gpuFan1) : null),
        };
        if (has(Sensor.SystemTemp)) readings.Add(Temperature(Sensor.SystemTemp, null));
        readings.Add(Fan(Sensor.CpuFan));
        if (has(Sensor.GpuFan1)) readings.Add(Fan(Sensor.GpuFan1));
        if (has(Sensor.GpuFan2)) readings.Add(Fan(Sensor.GpuFan2));

        // The module's one control, drawn large under the page header: Auto or Maximum, nothing chosen while Rigbay has set nothing.
        var fanMode = new ChoiceRow("row.fan-mode.header", new[] { new ChoiceOption(ChoiceAuto, "fan.auto"), new ChoiceOption(ChoiceMax, "fan.max") }, _choice)
        {
            DescriptionKey = "row.fan-mode.description",
            HelpKey = "row.fan-mode.help",
            Line = _fanLine,
            // No SafeRevert: both directions are asked about. Auto -> Maximum (what a Revert after "Auto" does) is loud.
            HardwareEffect = new HardwareEffect("effect.fan-mode") { RevertDescriptionKey = "effect.fan-mode-revert" },
            Enabled = _choiceEnabled,
        };

        var settings = new List<Row>();
        if (_hotkey is not null)
            settings.Add(new ShortcutRow("row.hotkey.header", _hotkey.Gesture) { DescriptionKey = "row.hotkey.description" });
        settings.Add(new ToggleRow("row.restore.header", _restore) { DescriptionKey = "row.restore.description" });
        settings.Add(new ExpanderRow("row.details.header", new Row[]
        {
            new ValueRow("row.model.header", _modelText),
            new NumberRow("row.poll.header", _pollSeconds, PredatorSenseSettings.MinPollSeconds, PredatorSenseSettings.MaxPollSeconds, 1)
            {
                DescriptionKey = "row.poll.description",
                HelpKey = "row.poll.help",
                Unit = MetricUnit.Seconds,
            },
        }) { DescriptionKey = "row.details.description" });

        return new PageModel
        {
            Primary = fanMode,
            Groups = new[]
            {
                new RowGroup("group.readings", readings),
                new RowGroup("group.performance", new[] { PerformanceModeRow() }),
                new RowGroup("group.settings", settings),
            },
            Summary = new PageSummary(_stateLine, new[] { SensorInfo.Of(Sensor.CpuTemp).Metric, cpuFan }),
        };
    }

    /// <summary>
    /// The performance mode, READ ONLY (the owner has not approved a mode write): the modes this laptop offers as a choice that can never be used,
    /// with the current one chosen, so the page shows all of them and which one is on. Nothing handles a change of its value, and the row is never
    /// enabled. When the laptop did not say which modes it has, the current mode as text.
    /// </summary>
    private Row PerformanceModeRow()
    {
        var modes = _opMode?.Supported;
        _pageModes = modes;
        _pageBuilt = true;
        if (modes is not { Count: > 0 })
            return new ValueRow("row.opmode.header", _opModeText) { DescriptionKey = "row.opmode.description", HelpKey = "row.opmode.help" };
        return new ChoiceRow("row.opmode.header", modes.Select(m => new ChoiceOption(OpModeId(m), OpModeKey(m))).ToList(), _opModeChoice)
        {
            DescriptionKey = "row.opmode.description",
            HelpKey = "row.opmode.help",
            Line = _opModeLine,
            Enabled = _readOnly,
        };
    }

    private static ReadingRow Temperature(Sensor sensor, SecondaryReading? fan) =>
        new(SensorLabelKey(sensor), SensorInfo.Of(sensor).Metric) { Decimals = 0, Tile = TileHint.Always, Secondary = fan };

    private static ReadingRow Fan(Sensor sensor) =>
        new(SensorLabelKey(sensor), SensorInfo.Of(sensor).Metric) { Decimals = 0, Tile = TileHint.Never, ChartGroupKey = "" };
}
