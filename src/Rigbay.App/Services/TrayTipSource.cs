using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// The app's <see cref="ITrayTipSource"/>: reads the modules and the metrics bus of the running host, formats a reading the way the dashboard does
/// (<see cref="ReadingFormat"/>, the metric's own format hint, the current language) and names it with the dashboard's short label. Runs on the UI thread,
/// from the tooltip timer and the Settings page. It holds nothing: every call reads the host again.
/// </summary>
sealed class TrayTipSource : ITrayTipSource
{
    readonly RigbayHost _core;

    public TrayTipSource(RigbayHost core) => _core = core;

    /// <summary>A reading older than this and older than three of its usual intervals is stale (the tooltip is refreshed every 5 s).</summary>
    public static readonly TimeSpan StaleFloor = TimeSpan.FromSeconds(30);

    public IReadOnlyList<TipModule> Modules
    {
        get
        {
            var list = new List<TipModule>();
            foreach (var e in _core.Modules)
            {
                bool running = e.Lifecycle == ModuleLifecycle.Running;
                string? state = null;
                var readings = new List<TipReading>();
                if (running)
                {
                    try
                    {
                        if (e.Page?.Summary?.StateLine is { } line && e.Localisation.Resolve(line.Value) is { Length: > 0 } text) state = text;
                        foreach (var r in e.GetSummaryReadings())
                            readings.Add(new TipReading(r.Id.Value, DashboardPage.ShortLabel(r), Format(r.Id, r.FormatHint)));
                    }
                    catch (Exception ex) { AppLog.Warn("tray tip: " + e.Id + " could not be summarised", ex); }
                }
                list.Add(new TipModule(e.Id, e.DisplayName, running, state, readings));
            }
            return list;
        }
    }

    /// <summary>The reading as "55 °C", or null when there is none.</summary>
    string? Format(MetricId id, string? hint)
    {
        if (!_core.Metrics.TryGetLatest(id, out var latest) || !ReadingFormat.IsAvailable(latest)) return null;
        return ReadingFormat.Format(_core.Languages, latest, DashboardPage.DecimalsOf(hint));
    }

    public TipMetric? Metric(string id)
    {
        if (!MetricId.IsValid(id)) return null;   // a hand-edited id that cannot be a metric
        var mid = new MetricId(id);
        var info = _core.Metrics.GetMetricInfo(mid);
        if (info == null) return null;
        _core.Metrics.TryGetLatest(mid, out var sample);
        bool available = ReadingFormat.IsAvailable(sample);
        string owner = sample?.SourceModule is { Length: > 0 } source ? source : info.OwnerModuleId;
        bool ownerRunning = owner == "host" || _core.Find(owner) is not { } entry || entry.Lifecycle == ModuleLifecycle.Running;
        var value = available ? ReadingFormat.Format(_core.Languages, sample, DashboardPage.DecimalsOf(info.FormatHint)) : null;
        return new TipMetric(id, DashboardPage.ShortLabel(mid, info.Label), info.OwnerModuleId, ownerRunning, value, available && IsFresh(mid, sample!));
    }

    bool IsFresh(MetricId id, MetricSample sample)
    {
        var history = _core.Metrics.History;
        var age = history.Now - sample.Timestamp;
        if (age <= StaleFloor) return true;
        return age <= MetricHistory.GapFor(history.Get(id));   // a metric that is published rarely is not stale between its samples
    }

    /// <summary>One thing the owner can put in the tooltip: its id, name in the list and what it says now.</summary>
    public sealed record Choice(string Id, string ModuleId, string Name, string Value);

    /// <summary>A module and what it offers.</summary>
    public sealed record ChoiceGroup(string ModuleId, string ModuleName, IReadOnlyList<Choice> Choices);

    /// <summary>
    /// Every state line of a running module and every metric of the bus, grouped by module (in rack order, the host's own metrics last), each with its current value.
    /// </summary>
    public IReadOnlyList<ChoiceGroup> Catalogue(string stateWord)
    {
        var groups = new List<ChoiceGroup>();
        var modules = Modules;
        var metrics = _core.Metrics.GetAllMetricInfo();
        foreach (var m in modules)
        {
            var choices = new List<Choice>();
            if (m.Running && !string.IsNullOrEmpty(m.State)) choices.Add(new Choice(TrayTipIds.State(m.Id), m.Id, stateWord, m.State));
            foreach (var info in metrics.Where(i => i.OwnerModuleId == m.Id && i.IsAvailable))
                choices.Add(new Choice(info.Id.Value, m.Id, DashboardPage.ShortLabel(info.Id, info.Label), ValueOf(info)));
            if (choices.Count > 0) groups.Add(new ChoiceGroup(m.Id, m.Name, choices));
        }
        var known = modules.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var rest = metrics.Where(i => !known.Contains(i.OwnerModuleId) && i.IsAvailable).ToList();
        foreach (var g in rest.GroupBy(i => i.OwnerModuleId))
            groups.Add(new ChoiceGroup(g.Key == "host" ? "" : g.Key, g.Key == "host" ? "Rigbay" : g.Key,
                g.Select(info => new Choice(info.Id.Value, g.Key == "host" ? "" : g.Key, DashboardPage.ShortLabel(info.Id, info.Label), ValueOf(info))).ToList()));
        return groups;
    }

    string ValueOf(MetricInfo info) =>
        _core.Metrics.TryGetLatest(info.Id, out var sample) && ReadingFormat.IsAvailable(sample)
            ? ReadingFormat.Format(_core.Languages, sample, DashboardPage.DecimalsOf(info.FormatHint))
            : _core.Languages.T("core.reading.unavailable");
}
