using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Watch;

/// <summary>What a rule compares a reading with.</summary>
public enum RuleKind
{
    /// <summary>The reading is at or above the limit.</summary>
    Above,

    /// <summary>The reading is at or below the limit.</summary>
    Below,

    /// <summary>The reading went up by the limit or more within the rule's time window (a counter such as errors).</summary>
    Rises,

    /// <summary>Any of the bits in the limit is set in the reading (a mask such as the GPU clock-limit reasons).</summary>
    Bits,
}

/// <summary>One rule: "alert when <see cref="Metric"/> is past <see cref="Threshold"/> for <see cref="Seconds"/>". Plain data, saved as JSON.</summary>
public sealed class WatchRule
{
    public const int MaxRules = 24;
    public const int MaxSeconds = 86_400;
    public const int MaxQuietMinutes = 1_440;
    public const int MinIntervalSeconds = 5;
    public const int MaxIntervalSeconds = 3_600;
    public const int DefaultIntervalSeconds = 10;
    public const int DefaultSeconds = 30;
    public const int DefaultQuietMinutes = 15;

    /// <summary>A short stable id (settings and page rows use it; it is never shown).</summary>
    public string Id { get; set; } = "";

    /// <summary>False: the rule is kept but not checked, and nothing is subscribed for it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The metric id the rule reads.</summary>
    public string Metric { get; set; } = "";

    public RuleKind Kind { get; set; } = RuleKind.Above;

    /// <summary>The limit. For <see cref="RuleKind.Bits"/> it is the mask (a whole number).</summary>
    public double Threshold { get; set; }

    /// <summary>Hysteresis: the second limit at which an alert ends. Null: the same limit (no band). Above: at or below this value; below: at or above it; rises: the rise is at most this.</summary>
    public double? ClearAt { get; set; }

    /// <summary>How long the reading must stay past the limit before the rule fires. For <see cref="RuleKind.Rises"/> it is the length of the window the rise is measured in.</summary>
    public int Seconds { get; set; } = DefaultSeconds;

    /// <summary>After an alert, no new alert for this long.</summary>
    public int QuietMinutes { get; set; } = DefaultQuietMinutes;

    /// <summary>How often the reading is wanted from its publisher (the background subscription's interval). A reading older than twice this is "no data".</summary>
    public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;

    /// <summary>"Only while" another reading is past a limit; null or empty: no condition.</summary>
    public string? ConditionMetric { get; set; }

    /// <summary>Only <see cref="RuleKind.Above"/> and <see cref="RuleKind.Below"/>.</summary>
    public RuleKind ConditionKind { get; set; } = RuleKind.Above;

    public double ConditionThreshold { get; set; }

    /// <summary>The built-in suggestion this rule was added from (so the page stops offering it), or null.</summary>
    public string? Suggestion { get; set; }

    public bool HasCondition => !string.IsNullOrEmpty(ConditionMetric);

    public WatchRule Clone() => (WatchRule)MemberwiseClone();

    /// <summary>Everything that decides what the rule means; when it changes, a running episode is dropped. (Enabled is not in it.)</summary>
    internal string Signature => string.Join("|",
        Metric, Kind, Num(Threshold), Num(ClearAt ?? double.NaN), Seconds, IntervalSeconds, HasCondition ? ConditionMetric : "", ConditionKind, Num(ConditionThreshold));

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A copy with every value inside its range; null when the rule cannot be repaired (no valid metric id). The file may have been edited by hand.</summary>
    internal WatchRule? Normalised()
    {
        if (!MetricId.IsValid(Metric)) return null;
        var r = Clone();
        r.Id = string.IsNullOrWhiteSpace(Id) ? NewId() : Id.Trim();
        if (!Enum.IsDefined(r.Kind)) r.Kind = RuleKind.Above;
        if (!double.IsFinite(r.Threshold)) r.Threshold = 0;
        if (r.Kind == RuleKind.Bits) r.Threshold = Math.Clamp(Math.Truncate(r.Threshold), 0, 9_007_199_254_740_991d);
        r.Seconds = Math.Clamp(r.Seconds, r.Kind == RuleKind.Rises ? 1 : 0, MaxSeconds);
        r.QuietMinutes = Math.Clamp(r.QuietMinutes, 0, MaxQuietMinutes);
        r.IntervalSeconds = Math.Clamp(r.IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
        if (r.ClearAt is { } c && (!double.IsFinite(c) || c == r.Threshold || r.Kind == RuleKind.Bits || BadClear(r.Kind, r.Threshold, c))) r.ClearAt = null;
        if (r.HasCondition && !MetricId.IsValid(r.ConditionMetric)) r.ConditionMetric = null;
        if (!r.HasCondition) r.ConditionMetric = null;
        if (r.ConditionKind is not (RuleKind.Above or RuleKind.Below)) r.ConditionKind = RuleKind.Above;
        if (!double.IsFinite(r.ConditionThreshold)) r.ConditionThreshold = 0;
        return r;
    }

    /// <summary>True when the clearing limit is on the wrong side of the limit (an alert that could never end, or one that ends before it starts).</summary>
    internal static bool BadClear(RuleKind kind, double threshold, double clear) => kind switch
    {
        RuleKind.Above => clear > threshold,
        RuleKind.Below => clear < threshold,
        RuleKind.Rises => clear < 0 || clear >= threshold,
        _ => true,
    };

    internal static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>The module's saved settings.</summary>
public sealed class WatchSettings
{
    public List<WatchRule> Rules { get; set; } = new();

    /// <summary>Show the tray item "Pause alerts for 1 hour".</summary>
    public bool TrayItem { get; set; }

    /// <summary>A copy with every rule repaired or dropped, at most <see cref="WatchRule.MaxRules"/>, ids unique.</summary>
    internal WatchSettings Clamped()
    {
        var rules = new List<WatchRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in Rules ?? new())
        {
            if (rule is null || rules.Count >= WatchRule.MaxRules) continue;
            var fixedRule = rule.Normalised();
            if (fixedRule is null) continue;
            while (!seen.Add(fixedRule.Id)) fixedRule.Id = WatchRule.NewId();
            rules.Add(fixedRule);
        }
        return new WatchSettings { Rules = rules, TrayItem = TrayItem };
    }
}
