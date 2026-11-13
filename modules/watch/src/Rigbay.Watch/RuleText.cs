using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Watch;

/// <summary>The words about a rule: its limit, its duration, its one-line summary and its state line. Pure functions of a rule, a unit and the language.</summary>
internal static class RuleText
{
    /// <summary>The number format of the current language (Vietnamese writes 21,2); the invariant one when the code is unknown.</summary>
    public static CultureInfo Culture(ILocalisation loc)
    {
        try { return CultureInfo.GetCultureInfo(loc.Language); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>A number with at most one decimal.</summary>
    public static string Number(double value, ILocalisation loc) => value.ToString("#,0.#", Culture(loc));

    /// <summary>A value with its unit symbol: "95 °C", "4,350 rpm", "12".</summary>
    public static string Amount(double value, MetricUnit unit, ILocalisation loc)
    {
        var symbol = unit.Symbol();
        return symbol.Length == 0 ? Number(value, loc) : Number(value, loc) + " " + symbol;
    }

    /// <summary>A mask as the hexadecimal number people see in the driver's documentation: 0x60.</summary>
    public static string Mask(double value) => value >= 0 && value <= long.MaxValue ? "0x" + ((long)value).ToString("X", CultureInfo.InvariantCulture) : "?";

    /// <summary>The limit or the reading, as the rule's kind writes it (a mask in hexadecimal, the rest with the unit).</summary>
    public static string Limit(RuleKind kind, double value, MetricUnit unit, ILocalisation loc) =>
        kind == RuleKind.Bits ? Mask(value) : Amount(value, unit, loc);

    /// <summary>"30 s", "5 min", "2 h": the largest whole unit.</summary>
    public static string Duration(int seconds, ILocalisation loc)
    {
        if (seconds > 0 && seconds % 3600 == 0) return loc.F("unit.hours", seconds / 3600);
        if (seconds > 0 && seconds % 60 == 0) return loc.F("unit.minutes", seconds / 60);
        return loc.F("unit.seconds", seconds);
    }

    /// <summary>"At or above 95 °C for 30 s, only while CPU temperature is at or above 80 °C".</summary>
    public static string Summary(WatchRule rule, MetricUnit unit, string? conditionLabel, MetricUnit conditionUnit, ILocalisation loc)
    {
        var limit = Limit(rule.Kind, rule.Threshold, unit, loc);
        var text = rule.Kind switch
        {
            RuleKind.Rises => loc.F("rule.rises", limit, Duration(rule.Seconds, loc)),
            RuleKind.Below => rule.Seconds > 0 ? loc.F("rule.below.for", limit, Duration(rule.Seconds, loc)) : loc.F("rule.below", limit),
            RuleKind.Bits => rule.Seconds > 0 ? loc.F("rule.bits.for", limit, Duration(rule.Seconds, loc)) : loc.F("rule.bits", limit),
            _ => rule.Seconds > 0 ? loc.F("rule.above.for", limit, Duration(rule.Seconds, loc)) : loc.F("rule.above", limit),
        };
        if (rule.HasCondition)
        {
            var key = rule.ConditionKind == RuleKind.Below ? "rule.while.below" : "rule.while.above";
            text += ", " + loc.F(key, conditionLabel ?? rule.ConditionMetric!, Amount(rule.ConditionThreshold, conditionUnit, loc));
        }
        return text;
    }

    /// <summary>A time of day, with the date when it is not today.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, ILocalisation loc)
    {
        var local = at.ToLocalTime();
        var culture = Culture(loc);
        return local.Date == now.ToLocalTime().Date ? local.ToString("t", culture) : local.ToString("g", culture);
    }

    /// <summary>The state line under a rule: "Quiet · now 61 °C", "Firing since 14:02 · last alert 14:02", "Waiting for xtu".</summary>
    public static string Line(WatchRule rule, RuleView? view, string summary, MetricUnit unit, DateTimeOffset now, ILocalisation loc)
    {
        var parts = new List<string> { summary };
        if (view is null) return string.Join(" · ", parts);
        switch (view.State)
        {
            case RuleState.Off:
                parts.Add(loc.T("state.off"));
                break;
            case RuleState.Waiting:
                parts.Add(loc.F("state.waiting", view.WaitingFor ?? rule.Metric));
                if (view.InEpisode && view.FiringSince is { } was) parts.Add(loc.F("state.was-firing", When(was, now, loc)));
                break;
            case RuleState.Pending:
                parts.Add(loc.F("state.pending", Duration((int)Math.Max(0, view.PendingFor?.TotalSeconds ?? 0), loc), Duration(rule.Seconds, loc)));
                break;
            case RuleState.Firing:
                parts.Add(loc.F("state.firing", view.FiringSince is { } since ? When(since, now, loc) : ""));
                if (view.NextAlertAfter is { } next) parts.Add(loc.F("state.next", When(next, now, loc)));
                break;
            default:
                parts.Add(loc.T("state.quiet"));
                break;
        }
        if (view.Value is { } v && view.State is not (RuleState.Off or RuleState.Waiting)) parts.Add(loc.F("state.now", Limit(rule.Kind, v, unit, loc)));
        if (view.LastAlert is { } last) parts.Add(loc.F("state.last", When(last, now, loc)));
        return string.Join(" · ", parts);
    }
}
