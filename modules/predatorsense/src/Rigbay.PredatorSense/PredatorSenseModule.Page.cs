using Rigbay.Abstractions;
using Rigbay.PredatorSense.Fan;
using Rigbay.PredatorSense.Sensors;

namespace Rigbay.PredatorSense;

public sealed partial class PredatorSenseModule
{
    /// <summary>
    /// Id of the fan-mode drop-down's first option while Rigbay has set nothing in this run. A state, not a value: choosing it
    /// writes nothing, and the page is rebuilt without it after the first set.
    /// </summary>
    internal const string ChoiceNotSet = "notset";

    /// <summary>Id of the fan-mode drop-down option for <see cref="FanMode.Auto"/>.</summary>
    internal const string ChoiceAuto = "auto";

    /// <summary>Id of the fan-mode drop-down option for <see cref="FanMode.Max"/>.</summary>
    internal const string ChoiceMax = "max";

    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host)
    {
        var present = _poller?.Present ?? Array.Empty<Sensor>();

        var readings = new List<Row>
        {
            // A sensor that cannot be read is withdrawn from the bus, so its row says "not available" by itself.
            Reading(Sensor.CpuTemp, "sensor.cpu-temp", "row.cpu-temp.description"),
            Reading(Sensor.GpuTemp, "sensor.gpu-temp", null),
        };
        // Parts this machine does not have answer "0" and are left out: only sensors that have reported are listed.
        if (present.Contains(Sensor.SystemTemp)) readings.Add(Reading(Sensor.SystemTemp, "sensor.system-temp", null));
        readings.Add(Reading(Sensor.CpuFan, "sensor.cpu-fan", null));
        if (present.Contains(Sensor.GpuFan1)) readings.Add(Reading(Sensor.GpuFan1, "sensor.gpu-fan1", null));
        if (present.Contains(Sensor.GpuFan2)) readings.Add(Reading(Sensor.GpuFan2, "sensor.gpu-fan2", null));
        readings.Add(new ValueRow("row.model.header", _modelText) { DescriptionKey = "row.model.description" });

        // The firmware cannot say which mode it is in, so until Rigbay has set one the control must not show one: it starts on
        // "Not set". A ChoiceRow has no placeholder text, so the option goes away after the first set, when the page is rebuilt.
        bool notSet = _fan?.Last is null;
        _pageOffersNotSet = notSet;
        var options = new List<ChoiceOption>();
        if (notSet) options.Add(new ChoiceOption(ChoiceNotSet, "fan.not-set"));
        options.Add(new ChoiceOption(ChoiceAuto, "fan.auto"));
        options.Add(new ChoiceOption(ChoiceMax, "fan.max"));

        var cooling = new List<Row>
        {
            new ChoiceRow("row.fan-mode.header", options, _choice)
            {
                DescriptionKey = "row.fan-mode.description",
                // No SafeRevert: both directions are asked about. Auto -> Maximum (what a Revert after "Auto" does) is loud.
                HardwareEffect = new HardwareEffect("effect.fan-mode") { RevertDescriptionKey = "effect.fan-mode-revert" },
                Enabled = _choiceEnabled,
            },
            new ValueRow("row.last-set.header", _lastSetText) { DescriptionKey = "row.last-set.description" },
            new ValueRow("row.check.header", _checkText) { DescriptionKey = "row.check.description" },
        };

        var settings = new List<Row>
        {
            new NumberRow("row.poll.header", _pollSeconds, PredatorSenseSettings.MinPollSeconds, PredatorSenseSettings.MaxPollSeconds, 1)
            {
                DescriptionKey = "row.poll.description",
                Unit = MetricUnit.Seconds,
            },
            new ToggleRow("row.restore.header", _restore) { DescriptionKey = "row.restore.description" },
        };
        if (_hotkey is not null)
            settings.Add(new ShortcutRow("row.hotkey.header", _hotkey.Gesture) { DescriptionKey = "row.hotkey.description" });

        return new PageModel
        {
            Groups = new[]
            {
                new RowGroup("group.readings", readings),
                new RowGroup("group.cooling", cooling),
                new RowGroup("group.settings", settings),
            },
            Summary = new PageSummary(_stateLine, new[] { SensorInfo.Of(Sensor.CpuTemp).Metric, SensorInfo.Of(Sensor.CpuFan).Metric }),
        };
    }

    private static ReadingRow Reading(Sensor sensor, string headerKey, string? descriptionKey) =>
        new(headerKey, SensorInfo.Of(sensor).Metric) { DescriptionKey = descriptionKey, Decimals = 0 };
}
