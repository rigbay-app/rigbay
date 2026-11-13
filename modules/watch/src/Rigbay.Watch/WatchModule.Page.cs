using Rigbay.Abstractions;

namespace Rigbay.Watch;

public sealed partial class WatchModule
{
    private const double BigNumber = 1_000_000_000;
    private const double MaskLimit = 9_007_199_254_740_991d;

    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host)
    {
        var catalogue = host.Metrics.GetAllMetricInfo();
        var rules = Rules;

        var ruleRows = new List<Row>();
        foreach (var rule in rules) ruleRows.Add(RuleRow(host, rule, catalogue));
        if (rules.Count == 0) ruleRows.Add(new InfoRow(Severity.Info, "info.no-rules") { DescriptionKey = "info.no-rules.description" });
        ruleRows.Add(new InfoRow(Severity.Info, "info.waiting") { DescriptionKey = "info.waiting.description" });

        var groups = new List<RowGroup>
        {
            new("group.rules", ruleRows),
            new("group.add", AddRows(host, catalogue)),
            new("group.suggestions", SuggestionRows(host, catalogue, rules)),
            new("group.settings", SettingsRows()),
        };

        return new PageModel
        {
            Primary = new ToggleRow("row.alerts.header", _alertsOn) { HelpKey = "row.alerts.help", Line = _pauseLine },
            Groups = groups,
            Summary = new PageSummary(_stateLine),
        };
    }

    // ------------------------------------------------------------------ the picker

    /// <summary>The readings to choose from, in the catalogue's order (by module, then reading): "xtu · CPU temperature (61 °C)". The value is the one at the moment the page was built.</summary>
    private static List<ChoiceOption> PickerOptions(IHostServices host, IReadOnlyList<MetricInfo> catalogue, string? alsoInclude)
    {
        var loc = host.Localisation;
        var options = new List<ChoiceOption>();
        foreach (var info in catalogue)
        {
            var label = info.OwnerModuleId + " · " + info.Label;
            if (host.Metrics.Latest(info.Id) is { IsAvailable: true } sample) label += " (" + RuleText.Amount(sample.Value, info.Unit, loc) + ")";
            options.Add(new ChoiceOption(info.Id.Value, Text.Literal(label)));
        }
        if (!string.IsNullOrEmpty(alsoInclude) && options.All(o => o.Id != alsoInclude))
            options.Add(new ChoiceOption(alsoInclude, Text.Literal(loc.F("picker.absent", alsoInclude))));
        return options;
    }

    private static List<ChoiceOption> KindOptions(bool conditionOnly)
    {
        var options = new List<ChoiceOption> { new("above", "kind.above"), new("below", "kind.below") };
        if (!conditionOnly)
        {
            options.Add(new ChoiceOption("rises", "kind.rises"));
            options.Add(new ChoiceOption("bits", "kind.bits"));
        }
        return options;
    }

    private static string KindId(RuleKind kind) => kind switch { RuleKind.Below => "below", RuleKind.Rises => "rises", RuleKind.Bits => "bits", _ => "above" };

    private static RuleKind KindOf(string id) => id switch { "below" => RuleKind.Below, "rises" => RuleKind.Rises, "bits" => RuleKind.Bits, _ => RuleKind.Above };

    // ------------------------------------------------------------------ one rule

    private WatchRule? Find(string id)
    {
        lock (_gate) return _settings.Rules.FirstOrDefault(r => r.Id == id)?.Clone();
    }

    private ExpanderRow RuleRow(IHostServices host, WatchRule rule, IReadOnlyList<MetricInfo> catalogue)
    {
        string id = rule.Id;
        var unit = catalogue.FirstOrDefault(m => m.Id.Value == rule.Metric)?.Unit ?? UnitOf(host, rule.Metric);
        var condUnit = rule.HasCondition ? catalogue.FirstOrDefault(m => m.Id.Value == rule.ConditionMetric)?.Unit ?? UnitOf(host, rule.ConditionMetric!) : MetricUnit.None;
        var defaultCondition = catalogue.Any(m => m.Id.Value == "cpu.temp") ? "cpu.temp" : catalogue.Count > 0 ? catalogue[0].Id.Value : "";

        var enabled = new ObservableValue<bool>(rule.Enabled);
        var metric = new ObservableValue<string>(rule.Metric);
        var kind = new ObservableValue<string>(KindId(rule.Kind));
        var limit = new ObservableValue<double>(rule.Threshold);
        var clear = new ObservableValue<double>(rule.ClearAt ?? rule.Threshold);
        var seconds = new ObservableValue<double>(rule.Seconds);
        var quiet = new ObservableValue<double>(rule.QuietMinutes);
        var interval = new ObservableValue<double>(rule.IntervalSeconds);
        var condOn = new ObservableValue<bool>(rule.HasCondition);
        var condMetric = new ObservableValue<string>(rule.ConditionMetric ?? defaultCondition);
        var condKind = new ObservableValue<string>(KindId(rule.ConditionKind));
        var condLimit = new ObservableValue<double>(rule.ConditionThreshold);

        // An edit goes through UpdateRule, which repairs the values to their ranges; the rows then show what was kept (a clearing limit on the wrong
        // side of the limit is dropped, a window is at least a second). The guard keeps that echo from being taken as another edit.
        bool applying = false;
        void Edit(Action<WatchRule> change, bool rebuildPage = false)
        {
            if (applying) return;
            applying = true;
            try
            {
                if (!UpdateRule(id, change)) return;
                if (Find(id) is { } kept)
                {
                    limit.Value = kept.Threshold;
                    clear.Value = kept.ClearAt ?? kept.Threshold;
                    seconds.Value = kept.Seconds;
                    quiet.Value = kept.QuietMinutes;
                    interval.Value = kept.IntervalSeconds;
                    condLimit.Value = kept.ConditionThreshold;
                    condOn.Value = kept.HasCondition;
                }
                if (rebuildPage) { _expanded = id; host.RequestPageRefresh(); }
            }
            finally { applying = false; }
        }

        enabled.Changed += () => Edit(r => r.Enabled = enabled.Value);
        metric.Changed += () => Edit(r => r.Metric = metric.Value, rebuildPage: true);
        kind.Changed += () => Edit(r => r.Kind = KindOf(kind.Value));
        limit.Changed += () => Edit(r => r.Threshold = limit.Value);
        clear.Changed += () => Edit(r => r.ClearAt = clear.Value == r.Threshold ? null : clear.Value);
        seconds.Changed += () => Edit(r => r.Seconds = (int)Math.Round(seconds.Value));
        quiet.Changed += () => Edit(r => r.QuietMinutes = (int)Math.Round(quiet.Value));
        interval.Changed += () => Edit(r => r.IntervalSeconds = (int)Math.Round(interval.Value));
        condOn.Changed += () => Edit(r => r.ConditionMetric = condOn.Value && condMetric.Value.Length > 0 ? condMetric.Value : null, rebuildPage: true);
        condMetric.Changed += () => Edit(r => { if (condOn.Value) r.ConditionMetric = condMetric.Value; }, rebuildPage: true);
        condKind.Changed += () => Edit(r => r.ConditionKind = KindOf(condKind.Value));
        condLimit.Changed += () => Edit(r => r.ConditionThreshold = condLimit.Value);

        double step = unit is MetricUnit.Volt or MetricUnit.Ampere ? 0.1 : 1;
        double condStep = condUnit is MetricUnit.Volt or MetricUnit.Ampere ? 0.1 : 1;
        bool bits = rule.Kind == RuleKind.Bits;

        var children = new List<Row>
        {
            new ChoiceRow("row.metric.header", PickerOptions(host, catalogue, rule.Metric), metric),
            new ChoiceRow("row.kind.header", KindOptions(conditionOnly: false), kind) { HelpKey = "row.kind.help" },
            new NumberRow("row.limit.header", limit, bits ? 0 : -BigNumber, bits ? MaskLimit : BigNumber, step) { Unit = bits ? MetricUnit.None : unit },
            new NumberRow("row.clear.header", clear, -BigNumber, BigNumber, step) { Unit = unit, HelpKey = "row.clear.help" },
            new NumberRow("row.for.header", seconds, 0, WatchRule.MaxSeconds, 1) { Unit = MetricUnit.Seconds, HelpKey = "row.for.help" },
            new NumberRow("row.quiet.header", quiet, 0, WatchRule.MaxQuietMinutes, 1) { DescriptionKey = "row.quiet.description" },
            new NumberRow("row.interval.header", interval, WatchRule.MinIntervalSeconds, WatchRule.MaxIntervalSeconds, 1) { Unit = MetricUnit.Seconds, HelpKey = "row.interval.help" },
            new ToggleRow("row.condition.header", condOn) { HelpKey = "row.condition.help" },
            new ChoiceRow("row.condition.metric.header", PickerOptions(host, catalogue, condMetric.Value.Length > 0 ? condMetric.Value : null), condMetric) { Enabled = condOn },
            new ChoiceRow("row.condition.kind.header", KindOptions(conditionOnly: true), condKind) { Enabled = condOn },
            new NumberRow("row.condition.limit.header", condLimit, -BigNumber, BigNumber, condStep) { Unit = condUnit, Enabled = condOn },
            new ButtonRow("row.remove.header", "row.remove.button", _ => Task.FromResult(Remove(host, id))) { ConfirmKey = "confirm.remove" },
        };

        return new ExpanderRow(Text.Literal(LabelOf(host, rule.Metric)), children)
        {
            HeaderRow = new ToggleRow("row.enabled.header", enabled),
            Line = LineOf(id),
            IsExpanded = _expanded == id,
        };
    }

    private ActionResult Remove(IHostServices host, string id)
    {
        if (!RemoveRule(id)) return ActionResult.Fail("remove.gone");
        host.RequestPageRefresh();
        return ActionResult.Ok(LocalisedText.Of("remove.done"));
    }

    // ------------------------------------------------------------------ add a rule

    private List<Row> AddRows(IHostServices host, IReadOnlyList<MetricInfo> catalogue)
    {
        if (catalogue.Count == 0) return new List<Row> { new InfoRow(Severity.Info, "info.no-readings") { DescriptionKey = "info.no-readings.description" } };
        var chosen = new ObservableValue<string>(catalogue.Any(m => m.Id.Value == _newMetric) ? _newMetric : "");
        chosen.Changed += () => _newMetric = chosen.Value;
        return new List<Row>
        {
            new ChoiceRow("row.new.header", PickerOptions(host, catalogue, null), chosen) { DescriptionKey = "row.new.description" },
            new ButtonRow("row.add.header", "row.add.button", _ => Task.FromResult(AddFromPicker(host, chosen.Value))),
        };
    }

    private ActionResult AddFromPicker(IHostServices host, string metric)
    {
        if (!MetricId.IsValid(metric)) return ActionResult.Fail("add.pick");
        var unit = UnitOf(host, metric);
        var latest = host.Metrics.Latest(new MetricId(metric));
        var rule = new WatchRule
        {
            Metric = metric,
            Kind = RuleKind.Above,
            Threshold = unit switch
            {
                MetricUnit.Celsius => 90,
                MetricUnit.Percent => 95,
                _ => latest is { IsAvailable: true } s ? Math.Ceiling(s.Value * 1.1) : 0,
            },
        };
        return AddAndOpen(host, rule);
    }

    private ActionResult AddAndOpen(IHostServices host, WatchRule rule)
    {
        if (AddRule(rule) is not { } stored) return ActionResult.Fail("add.full", WatchRule.MaxRules);
        _expanded = stored.Id;
        host.RequestPageRefresh();
        return ActionResult.Ok(LocalisedText.Of("add.done"));
    }

    // ------------------------------------------------------------------ suggestions

    private List<Row> SuggestionRows(IHostServices host, IReadOnlyList<MetricInfo> catalogue, IReadOnlyList<WatchRule> rules)
    {
        var offered = Suggestions.For(catalogue).Where(s => rules.All(r => r.Suggestion != s.Key)).ToList();
        if (offered.Count == 0)
            return new List<Row> { new InfoRow(Severity.Info, rules.Any(r => r.Suggestion is not null) ? "info.suggest-done" : "info.suggest-none") { DescriptionKey = "info.suggest.description" } };
        return offered.Select(s => (Row)new ButtonRow(s.HeaderKey ?? Text.Literal(s.HeaderLiteral), "row.suggest.add", _ => Task.FromResult(AddAndOpen(host, s.Rule.Clone())))
        {
            DescriptionKey = Text.Literal(SummaryOf(host, s.Rule)),
            HelpKey = s.HelpKey,
        }).ToList();
    }

    // ------------------------------------------------------------------ settings and the test

    private List<Row> SettingsRows()
    {
        var tray = new ObservableValue<bool>(_settings.TrayItem);
        tray.Changed += () => SetTrayItem(tray.Value);
        return new List<Row>
        {
            new ToggleRow("row.tray.header", tray) { DescriptionKey = "row.tray.description" },
            new ButtonRow("row.test.header", "row.test.button", _ =>
            {
                SendTestAlert();
                return Task.FromResult(ActionResult.Ok(LocalisedText.Of("test.done")));
            }) { HelpKey = "row.test.help" },
        };
    }

    private void SetTrayItem(bool on)
    {
        lock (_gate) _settings.TrayItem = on;
        if (_trayItem is { } tray) tray.Visible.Value = on;
        try { lock (_gate) _host?.Settings.Save(_settings); }
        catch (Exception ex) { _host?.Log.Warn("Saving the Watch settings failed", ex); }
    }

    /// <summary>The tray item's visibility (tests).</summary>
    internal bool TrayItemShown => _trayItem?.Visible.Value == true;
}
