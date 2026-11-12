using System.Globalization;
using Rigbay.Abstractions;
using Rigbay.PredatorSense.Fan;
using Rigbay.PredatorSense.Sensors;

namespace Rigbay.PredatorSense;

public sealed partial class PredatorSenseModule
{
    /// <summary>
    /// Recomputes every text and flag the page shows from the current state. Called after a poll, a fan change, a language
    /// switch. Cheap, and safe from any thread.
    /// </summary>
    private void RefreshTexts()
    {
        var host = _host;
        var poller = _poller;
        var fan = _fan;
        if (host is null || poller is null || fan is null) return;
        var loc = host.Localisation;

        _modelText.Value = string.IsNullOrWhiteSpace(_modelName) ? loc.T("model.unknown") : _modelName!;
        _choiceEnabled.Value = fan.CanWrite(FanMode.Max) || fan.MaxIsSet;
        _lastSetText.Value = LastSetText(loc, fan.Last);
        _checkText.Value = _watching ? loc.T("check.watching") : _noPrevious ? loc.T("check.no-previous") : _result is null ? "" : CheckText(loc, _result);
        _fanLine.Value = FanLine(fan.Last, _lastSetText.Value, _checkText.Value);
        _stateLine.Value = StateLine(poller.State, fan.Last);
        if (_opMode is { } opMode)
        {
            var current = opMode.Current;
            var supported = opMode.Supported;
            _opModeText.Value = OpModeText(loc, current);
            _opModeChoice.Value = current is { } c ? OpModeId(c) : "";
            // The choice shows which mode is on; the line speaks only when it cannot: not read, or a mode the laptop did not list.
            _opModeLine.Value = current is null ? LocalisedText.Of("opmode.line-unknown")
                : supported is null || !supported.Contains(current.Value) ? LocalisedText.Of("opmode.line-now", OpModeText(loc, current))
                : default;
            // The options are the modes the laptop offers, fixed when the page is built: build it again when that list changes.
            if (_pageBuilt && !SameModes(_pageModes, supported))
            {
                _pageModes = supported;
                host.RequestPageRefresh();
            }
        }
    }

    private static bool SameModes(IReadOnlyList<OperationMode>? a, IReadOnlyList<OperationMode>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    /// <summary>The id of a performance mode in the read-only choice.</summary>
    internal static string OpModeId(OperationMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>The label key of a performance mode.</summary>
    internal static string OpModeKey(OperationMode mode) => "opmode." + OpModeId(mode);

    /// <summary>
    /// The line under the fan-mode choice: what Rigbay set and when, then how the fans answered ("Set to Maximum at 14:02 · CPU fan sped up:
    /// 2857 to 5555 rpm"); "Not set by Rigbay yet" before the first change. It replaces the rows "Last set by Rigbay" and "Speed check".
    /// </summary>
    internal static LocalisedText FanLine(LastSet? last, string lastSetText, string checkText) => (last, checkText.Length) switch
    {
        (null, 0) => LocalisedText.Of("line.not-set"),
        (null, _) => LocalisedText.Of("line.not-set-check", checkText),
        (_, 0) => LocalisedText.Of("line.set", lastSetText),
        _ => LocalisedText.Of("line.set-check", lastSetText, checkText),
    };

    /// <summary>The label of a performance mode, or "Unknown" for a mode that could not be read or understood.</summary>
    internal static string OpModeText(ILocalisation loc, OperationMode? mode) => mode switch
    {
        OperationMode.Quiet => loc.T("opmode.quiet"),
        OperationMode.Default => loc.T("opmode.default"),
        OperationMode.Render => loc.T("opmode.render"),
        OperationMode.Edit => loc.T("opmode.edit"),
        OperationMode.Extreme => loc.T("opmode.extreme"),
        OperationMode.Turbo => loc.T("opmode.turbo"),
        _ => loc.T("opmode.unknown"),
    };

    /// <summary>
    /// The line under the module's name on the dashboard and the page header: the state of the fans when Rigbay set them ("Fans Maximum since
    /// 14:02"), else nothing (the readings beside it say the rest; the firmware cannot report its fan mode). Review DB-5.
    /// </summary>
    internal static LocalisedText StateLine(ReadingsState state, LastSet? last) => state switch
    {
        ReadingsState.Unavailable => LocalisedText.Of("state.unavailable"),
        ReadingsState.Starting => LocalisedText.Of("state.starting"),
        _ => last switch
        {
            null => default,
            { Mode: FanMode.Max, Uncertain: true } => LocalisedText.Of("state.max-unconfirmed"),
            { Mode: FanMode.Max } => LocalisedText.Of("state.max", Time(last)),
            _ => LocalisedText.Of("state.auto", Time(last)),
        },
    };

    private static string Time(LastSet last) => last.At.ToString("HH:mm", CultureInfo.InvariantCulture);

    internal static string ModeLabel(ILocalisation loc, FanMode mode) => loc.T(mode == FanMode.Max ? "fan.max" : "fan.auto");

    internal static string LastSetText(ILocalisation loc, LastSet? last)
    {
        if (last is null) return loc.T("last.none");
        return loc.F(last.Uncertain ? "last.unconfirmed" : "last.value", ModeLabel(loc, last.Mode), Time(last));
    }

    /// <summary>One short line about how a fan change ended. A reading of the speeds, never a statement about the mode.</summary>
    internal static string CheckText(ILocalisation loc, FanResult r)
    {
        switch (r.Outcome)
        {
            case FanOutcome.RefusedNoReadings: return loc.T("check.refused-readings");
            case FanOutcome.RefusedBusy: return loc.T("check.refused-busy");
            case FanOutcome.RefusedByGuard: return loc.T("check.refused-guard");
            case FanOutcome.StatusRefused: return loc.F("check.status-refused", r.Status);
            case FanOutcome.Unknown: return loc.T("check.no-answer");
        }

        var (sensor, from, to) = BiggestChange(r);
        string fan = sensor is { } s ? loc.T(FanLabelKey(s)) : "";
        return r.Verdict switch
        {
            RpmVerdict.Rose => loc.F("check.rose", fan, from, to),
            RpmVerdict.NotRose => loc.T("check.not-rose"),
            RpmVerdict.Fell => loc.F("check.fell", fan, from, to),
            RpmVerdict.NotFell => loc.T("check.not-fell"),
            RpmVerdict.Steady => loc.T("check.steady"),
            RpmVerdict.Moved => loc.F("check.moved", fan, from, to),
            _ => loc.T("check.unchecked"),
        };
    }

    private static (Sensor? Sensor, int From, int To) BiggestChange(FanResult r)
    {
        Sensor? best = null;
        double bestDelta = -1, from = 0, to = 0;
        foreach (var fan in SensorInfo.Fans)
        {
            if (!r.Before.TryGetValue(fan.Sensor, out var b) || b is null) continue;
            if (!r.After.TryGetValue(fan.Sensor, out var a) || a is null) continue;
            double delta = Math.Abs(a.Value - b.Value);
            if (delta > bestDelta) { best = fan.Sensor; bestDelta = delta; from = b.Value; to = a.Value; }
        }
        return (best, (int)Math.Round(from), (int)Math.Round(to));
    }

    internal static string FanLabelKey(Sensor sensor) => sensor switch
    {
        Sensor.CpuFan => "sensor.cpu-fan",
        Sensor.GpuFan1 => "sensor.gpu-fan1",
        _ => "sensor.gpu-fan2",
    };

    /// <summary>The label key of a sensor (also the page row header).</summary>
    internal static string SensorLabelKey(Sensor sensor) => sensor switch
    {
        Sensor.CpuTemp => "sensor.cpu-temp",
        Sensor.SystemTemp => "sensor.system-temp",
        Sensor.GpuTemp => "sensor.gpu-temp",
        _ => FanLabelKey(sensor),
    };
}
