using Rigbay.Abstractions;

namespace Rigbay.Watch;

/// <summary>What the page says about a rule.</summary>
internal enum RuleState
{
    /// <summary>The rule is switched off.</summary>
    Off,

    /// <summary>No fresh reading (the publisher has not sent one within twice the rule's interval, or withdrew it): the rule neither fires nor clears.</summary>
    Waiting,

    /// <summary>Nothing to report.</summary>
    Quiet,

    /// <summary>Past the limit, for less than the time the rule asks.</summary>
    Pending,

    /// <summary>An episode is open.</summary>
    Firing,
}

/// <summary>A snapshot of one rule for the page, the status verb and the tests.</summary>
internal sealed record RuleView(
    RuleState State,
    string? WaitingFor,
    double? Value,
    MetricUnit Unit,
    DateTimeOffset? FiringSince,
    TimeSpan? PendingFor,
    DateTimeOffset? LastAlert,
    DateTimeOffset? NextAlertAfter,
    bool InEpisode);

/// <summary>An alert the engine decided to send.</summary>
internal sealed record Alert(WatchRule Rule, double Value, MetricUnit Unit, DateTimeOffset At);

/// <summary>
/// The rules, applied to the readings. No bus, no timer, no host: <see cref="Record"/> takes samples, <see cref="Evaluate"/> takes the clock and returns the
/// alerts to send. Tests drive it with a fake clock.
/// <para>An episode starts when the reading has been past the limit without a break for the rule's time (checked at each <see cref="Evaluate"/>, so to
/// within one check interval), and ends when the reading is back past the clearing limit. One alert per episode: sent when the episode is open, nobody
/// has been told about it yet, alerts are not paused, the quiet period of the previous alert is over and the reading is fresh. An episode that starts
/// while alerts are paused or quiet is therefore announced when that ends, if it is still open. No data does not end an episode and does not count
/// towards its time: a gap drops the time already counted.</para>
/// </summary>
internal sealed class RuleEngine
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Reading> _readings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Runtime> _runtime = new(StringComparer.Ordinal);
    private List<WatchRule> _rules = new();

    public RuleEngine(Func<DateTimeOffset> now) => _now = now;

    private readonly record struct Reading(double Value, DateTimeOffset At, MetricUnit Unit);

    private sealed class Runtime
    {
        public string Signature = "";
        public DateTimeOffset? PendingSince;
        public bool Firing;
        public DateTimeOffset? FiringSince;
        public bool Notified;
        public DateTimeOffset? LastAlert;
        public DateTimeOffset? QuietUntil;
        public bool NoData = true;   // until the first check that finds a fresh reading
        public string? WaitingFor;
        public double? Value;
        public MetricUnit Unit;
        public readonly List<(DateTimeOffset At, double Value)> History = new();
    }

    /// <summary>
    /// Replaces the rule set. A rule that is still there with the same meaning keeps its state; one that changed starts over (its last alert time and quiet period
    /// stay); one that is gone is forgotten. Only rules that are switched on count towards what is subscribed (<see cref="Wanted"/>).
    /// </summary>
    public void SetRules(IEnumerable<WatchRule> rules)
    {
        lock (_gate)
        {
            _rules = rules.Select(r => r.Clone()).ToList();
            var ids = _rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _runtime.Keys.Where(k => !ids.Contains(k)).ToList()) _runtime.Remove(gone);
            foreach (var rule in _rules)
            {
                if (!_runtime.TryGetValue(rule.Id, out var rt)) { _runtime[rule.Id] = new Runtime { Signature = rule.Signature }; continue; }
                if (rt.Signature == rule.Signature && rule.Enabled) continue;
                _runtime[rule.Id] = new Runtime { Signature = rule.Signature, LastAlert = rt.LastAlert, QuietUntil = rt.QuietUntil };
            }
        }
    }

    /// <summary>The metrics to subscribe to, each with the shortest interval any enabled rule wants it at.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Wanted()
    {
        lock (_gate)
        {
            var wanted = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            void Want(string id, int seconds)
            {
                var interval = TimeSpan.FromSeconds(seconds);
                if (!wanted.TryGetValue(id, out var have) || interval < have) wanted[id] = interval;
            }
            foreach (var rule in _rules.Where(r => r.Enabled))
            {
                Want(rule.Metric, rule.IntervalSeconds);
                if (rule.HasCondition) Want(rule.ConditionMetric!, rule.IntervalSeconds);
            }
            return wanted;
        }
    }

    /// <summary>The shortest interval of an enabled rule, or null when none is enabled (nothing to check).</summary>
    public TimeSpan? CheckEvery()
    {
        lock (_gate)
        {
            var enabled = _rules.Where(r => r.Enabled).Select(r => r.IntervalSeconds).ToList();
            return enabled.Count == 0 ? null : TimeSpan.FromSeconds(enabled.Min());
        }
    }

    /// <summary>A sample arrived (a withdrawn notice removes the reading).</summary>
    public void Record(MetricSample sample)
    {
        lock (_gate)
        {
            if (!sample.IsAvailable || !double.IsFinite(sample.Value)) _readings.Remove(sample.Id.Value);
            else _readings[sample.Id.Value] = new Reading(sample.Value, sample.Timestamp, sample.Unit);
        }
    }

    /// <summary>The newest value of a metric the engine has, fresh or not, with its age; null when it has none.</summary>
    public (double Value, TimeSpan Age)? Newest(string metric)
    {
        lock (_gate) return _readings.TryGetValue(metric, out var r) ? (r.Value, _now() - r.At) : null;
    }

    /// <summary>Checks every enabled rule against the clock. Returns the alerts to send now (the caller sends them).</summary>
    public IReadOnlyList<Alert> Evaluate(bool paused)
    {
        var alerts = new List<Alert>();
        lock (_gate)
        {
            var now = _now();
            foreach (var rule in _rules)
            {
                if (!rule.Enabled || !_runtime.TryGetValue(rule.Id, out var rt)) continue;
                Step(rule, rt, now, paused, alerts);
            }
        }
        return alerts;
    }

    private bool Fresh(string metric, int intervalSeconds, DateTimeOffset now, out Reading reading) =>
        _readings.TryGetValue(metric, out reading) && now - reading.At <= TimeSpan.FromSeconds(2 * intervalSeconds);

    private void Step(WatchRule rule, Runtime rt, DateTimeOffset now, bool paused, List<Alert> alerts)
    {
        bool haveMain = Fresh(rule.Metric, rule.IntervalSeconds, now, out var main);
        Reading cond = default;
        bool haveCond = !rule.HasCondition || Fresh(rule.ConditionMetric!, rule.IntervalSeconds, now, out cond);
        if (!haveMain || !haveCond)
        {
            // No data: nothing fires, nothing clears, and the time counted so far is not carried over the gap.
            rt.NoData = true;
            rt.WaitingFor = !haveMain ? rule.Metric : rule.ConditionMetric;
            rt.PendingSince = null;
            rt.History.Clear();
            rt.Value = null;
            return;
        }
        rt.NoData = false;
        rt.WaitingFor = null;
        rt.Value = main.Value;
        rt.Unit = main.Unit;

        double v = main.Value;
        bool condOk = !rule.HasCondition || (rule.ConditionKind == RuleKind.Below ? cond.Value <= rule.ConditionThreshold : cond.Value >= rule.ConditionThreshold);
        bool entry, recovered;
        switch (rule.Kind)
        {
            case RuleKind.Below:
                entry = v <= rule.Threshold;
                recovered = rule.ClearAt is { } cb ? v >= cb : !entry;
                break;
            case RuleKind.Bits:
                entry = Bits(v, rule.Threshold);
                recovered = !entry;
                break;
            case RuleKind.Rises:
            {
                var window = TimeSpan.FromSeconds(Math.Max(1, rule.Seconds));
                rt.History.Add((now, v));
                rt.History.RemoveAll(h => now - h.At > window);
                double rise = Math.Max(0, v - rt.History.Min(h => h.Value));
                entry = rise >= rule.Threshold;
                recovered = rise <= (rule.ClearAt ?? 0);
                break;
            }
            default:
                entry = v >= rule.Threshold;
                recovered = rule.ClearAt is { } ca ? v <= ca : !entry;
                break;
        }
        entry &= condOk;
        recovered |= !condOk;

        if (!rt.Firing)
        {
            if (!entry) rt.PendingSince = null;
            else
            {
                rt.PendingSince ??= now;
                var needed = rule.Kind == RuleKind.Rises ? TimeSpan.Zero : TimeSpan.FromSeconds(rule.Seconds);
                if (now - rt.PendingSince >= needed)
                {
                    rt.Firing = true;
                    rt.FiringSince = now;
                    rt.Notified = false;
                    rt.PendingSince = null;
                }
            }
        }
        else if (recovered)
        {
            rt.Firing = false;
            rt.FiringSince = null;
            rt.PendingSince = null;
            rt.Notified = false;
        }

        if (rt.Firing && !rt.Notified && !paused && (rt.QuietUntil is null || now >= rt.QuietUntil))
        {
            rt.Notified = true;
            rt.LastAlert = now;
            rt.QuietUntil = now + TimeSpan.FromMinutes(rule.QuietMinutes);
            alerts.Add(new Alert(rule.Clone(), v, main.Unit, now));
        }
    }

    private static bool Bits(double value, double mask)
    {
        if (!double.IsFinite(value) || value < 0 || value > long.MaxValue) return false;
        return ((long)value & (long)mask) != 0;
    }

    /// <summary>What the page shows for a rule; null for an id the engine does not have.</summary>
    public RuleView? View(string ruleId)
    {
        lock (_gate)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule is null || !_runtime.TryGetValue(ruleId, out var rt)) return null;
            var now = _now();
            if (!rule.Enabled) return new RuleView(RuleState.Off, null, null, rt.Unit, null, null, rt.LastAlert, null, false);
            var state = rt.NoData ? RuleState.Waiting : rt.Firing ? RuleState.Firing : rt.PendingSince is not null ? RuleState.Pending : RuleState.Quiet;
            DateTimeOffset? next = rt.Firing && !rt.Notified && rt.QuietUntil is { } q && q > now ? q : null;
            return new RuleView(state, rt.NoData ? rt.WaitingFor ?? rule.Metric : null, rt.Value, rt.Unit,
                rt.FiringSince, rt.PendingSince is { } p ? now - p : null, rt.LastAlert, next, rt.Firing);
        }
    }
}
