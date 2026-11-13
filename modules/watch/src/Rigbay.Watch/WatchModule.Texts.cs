using Rigbay.Abstractions;

namespace Rigbay.Watch;

public sealed partial class WatchModule
{
    /// <summary>The unit of a metric as the catalogue knows it, else as its last sample says, else none.</summary>
    private static MetricUnit UnitOf(IHostServices host, string id)
    {
        if (!MetricId.IsValid(id)) return MetricUnit.None;
        var metric = new MetricId(id);
        return host.Metrics.GetMetricInfo(metric)?.Unit ?? host.Metrics.Latest(metric)?.Unit ?? MetricUnit.None;
    }

    private static string LabelOf(IHostServices host, string id) =>
        MetricId.IsValid(id) && host.Metrics.GetMetricInfo(new MetricId(id)) is { } info ? info.Label : id;

    /// <summary>"At or above 95 °C for 30 s": what the rule says, in the current language.</summary>
    internal static string SummaryOf(IHostServices host, WatchRule rule) =>
        RuleText.Summary(rule, UnitOf(host, rule.Metric),
            rule.HasCondition ? LabelOf(host, rule.ConditionMetric!) : null,
            rule.HasCondition ? UnitOf(host, rule.ConditionMetric!) : MetricUnit.None,
            host.Localisation);

    /// <summary>The live line under a rule on the page (one observable per rule, created on first use).</summary>
    private ObservableValue<LocalisedText> LineOf(string ruleId)
    {
        lock (_gate)
        {
            if (!_ruleLines.TryGetValue(ruleId, out var line)) _ruleLines[ruleId] = line = new ObservableValue<LocalisedText>(default);
            return line;
        }
    }

    /// <summary>The live line of a rule (tests).</summary>
    internal LocalisedText LineText(string ruleId) => LineOf(ruleId).Value;

    /// <summary>The state line (tests).</summary>
    internal LocalisedText StateLine => _stateLine.Value;

    /// <summary>Recomputes every text the page shows from the engine's state. Called after a check, an edit, a pause and a language switch. Safe from any thread.</summary>
    private void RefreshTexts()
    {
        var host = _host;
        var engine = _engine;
        if (host is null || engine is null || !_running) return;
        var loc = host.Localisation;
        var now = _deps!.Now();
        var rules = Rules;

        int enabled = 0, firing = 0;
        foreach (var rule in rules)
        {
            var view = engine.View(rule.Id);
            if (rule.Enabled) enabled++;
            if (view is { InEpisode: true } && rule.Enabled) firing++;
            var summary = SummaryOf(host, rule);
            LineOf(rule.Id).Value = LocalisedText.Of(Text.Literal(RuleText.Line(rule, view, summary, UnitOf(host, rule.Metric), now, loc)));
        }

        var pausedUntil = PausedUntil;
        _pauseLine.Value = pausedUntil is { } until ? LocalisedText.Of("pause.until", RuleText.When(until, now, loc)) : default;
        _stateLine.Value =
            rules.Count == 0 ? LocalisedText.Of("state.no-rules")
            : enabled == 0 ? LocalisedText.Of("state.all-off")
            : firing > 0 ? LocalisedText.Of("state.firing-count", firing, enabled)
            : pausedUntil is { } ? LocalisedText.Of("state.paused", RuleText.When(pausedUntil.Value, now, loc))
            : LocalisedText.Of("state.watching", enabled);
    }
}
