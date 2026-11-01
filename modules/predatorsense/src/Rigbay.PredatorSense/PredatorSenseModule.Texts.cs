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
        _checkText.Value = _watching ? loc.T("check.watching") : _result is null ? loc.T("check.none") : CheckText(loc, _result);
        _stateLine.Value = StateLine(poller.State, fan.Last);
    }

    private static LocalisedText StateLine(ReadingsState state, LastSet? last) => state switch
    {
        ReadingsState.Unavailable => LocalisedText.Of("state.unavailable"),
        ReadingsState.Starting => LocalisedText.Of("state.starting"),
        _ => last switch
        {
            null => LocalisedText.Of("state.live"),
            { Mode: FanMode.Max, Uncertain: true } => LocalisedText.Of("state.max-unconfirmed"),
            { Mode: FanMode.Max } => LocalisedText.Of("state.max"),
            _ => LocalisedText.Of("state.auto"),
        },
    };

    internal static string ModeLabel(ILocalisation loc, FanMode mode) => loc.T(mode == FanMode.Max ? "fan.max" : "fan.auto");

    internal static string LastSetText(ILocalisation loc, LastSet? last)
    {
        if (last is null) return loc.T("last.none");
        var time = last.At.ToString("HH:mm", CultureInfo.InvariantCulture);
        return loc.F(last.Uncertain ? "last.unconfirmed" : "last.value", ModeLabel(loc, last.Mode), time);
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
