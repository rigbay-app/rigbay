using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.PowerRules;

internal sealed partial class PowerRulesRuntime
{
    private const string Leave = "leave";

    private static readonly HardwareEffect AutoEffect = new("effect.auto") { Kind = EffectKind.SystemSetting };
    private static readonly HardwareEffect RefreshEffect = new("effect.refresh") { Kind = EffectKind.SystemSetting };
    private static readonly HardwareEffect ModeEffect = new("effect.mode") { Kind = EffectKind.SystemSetting };

    // The rows' values outlive a page rebuild: the handlers are wired once, here.
    private ObservableValue<bool> _auto = null!;
    private ObservableValue<LocalisedText> _autoLine = null!;      // the state of the module (dashboard and page header)
    private ObservableValue<LocalisedText> _rowLine = null!;       // under the switch: only the one thing it must say, that the rules changed
    private ObservableValue<string> _sourceText = null!, _panelText = null!, _modeText = null!;
    private ObservableValue<string> _curRate = null!, _curMode = null!;
    private ObservableValue<string> _batRate = null!, _batMode = null!, _plugRate = null!, _plugMode = null!;
    private ObservableValue<bool> _rateEnabled = null!, _modeEnabled = null!, _panelInfoOpen = null!, _modeInfoOpen = null!;
    private string? _builtKey;

    private void CreateRows()
    {
        _auto = new(false);
        _autoLine = new(LocalisedText.Of("line.off"));
        _rowLine = new(default);
        _sourceText = new("");
        _panelText = new("");
        _modeText = new("");
        _curRate = new("");
        _curMode = new("");
        _batRate = new(Leave);
        _batMode = new(Leave);
        _plugRate = new(Leave);
        _plugMode = new(Leave);
        _rateEnabled = new(false);
        _modeEnabled = new(false);
        _panelInfoOpen = new(false);
        _modeInfoOpen = new(false);

        // These run on the thread that sets the value, usually the UI thread: hand the work to a background task and return.
        _auto.Changed += () =>
        {
            if (_own) return;
            if (_auto.Value) Track(ApproveAsync, "approval of the rules");
            else Track(TurnOff, "turning automatic switching off");
        };
        _curRate.Changed += () =>
        {
            if (_own || !int.TryParse(_curRate.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int hz)) return;
            Track(() => ManualRefresh(hz), "refresh rate change");
        };
        _curMode.Changed += () =>
        {
            if (_own || PowerModes.FromId(_curMode.Value) is not { } mode) return;
            Track(() => ManualMode(mode), "power mode change");
        };
        foreach (var rule in new[] { _batRate, _batMode, _plugRate, _plugMode }) rule.Changed += RuleEdited;
    }

    /// <summary>The rows from the settings.</summary>
    private void LoadRows()
    {
        lock (_state)
        {
            SetOwn(_batRate, _s.BatteryHz == 0 ? Leave : _s.BatteryHz.ToString(CultureInfo.InvariantCulture));
            SetOwn(_batMode, string.IsNullOrEmpty(_s.BatteryMode) ? Leave : _s.BatteryMode);
            SetOwn(_plugRate, _s.PluggedHz == 0 ? Leave : _s.PluggedHz.ToString(CultureInfo.InvariantCulture));
            SetOwn(_plugMode, string.IsNullOrEmpty(_s.PluggedMode) ? Leave : _s.PluggedMode);
        }
    }

    /// <summary>The settings from the rows (call with <c>_state</c> held).</summary>
    private void ReadRows()
    {
        _s.BatteryHz = ParseHz(_batRate.Value);
        _s.BatteryMode = PowerModes.FromId(_batMode.Value) is { } b ? PowerModes.Id(b) : "";
        _s.PluggedHz = ParseHz(_plugRate.Value);
        _s.PluggedMode = PowerModes.FromId(_plugMode.Value) is { } p ? PowerModes.Id(p) : "";
    }

    private static int ParseHz(string id) => int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int hz) && hz > 1 ? hz : 0;

    // ------------------------------------------------------------------ words

    private string SourceWord(PowerSource source) => _host.Localisation.T(source switch { PowerSource.Battery => "source.battery", PowerSource.Plugged => "source.plugged", _ => "source.unknown" });

    private string PanelWord(PanelLookup lookup)
    {
        var loc = _host.Localisation;
        return lookup.State switch
        {
            PanelState.Active when lookup.Panel is { } p => loc.F("panel.active", p.Width, p.Height, p.CurrentHz),
            PanelState.NotActive => loc.T("panel.not-active"),
            _ => loc.T("panel.unreadable"),
        };
    }

    private string ModeWord(PowerMode mode, bool available)
    {
        var loc = _host.Localisation;
        if (!available) return loc.T("mode.unavailable");
        return mode == PowerMode.Unknown ? loc.T("mode.unknown") : loc.T(PowerModes.LabelKey(mode));
    }

    /// <summary>Brings the values on the page up to date with the last reads and asks for a new page when the lists on it changed.</summary>
    private void RefreshTexts()
    {
        PowerSource source;
        PanelLookup panel;
        PowerMode mode;
        bool available, hasBefore;
        string key;
        lock (_state)
        {
            source = _source;
            panel = _panel;
            mode = _mode;
            available = _modeAvailable;
            hasBefore = _s.BeforeHz != 0 || !string.IsNullOrEmpty(_s.BeforeMode);
            key = string.Join(",", _s.KnownRates) + "|" + (panel.State == PanelState.Active) + "|" + _s.BeforeHz + "|" + _s.BeforeMode + "|" + hasBefore;
        }
        _sourceText.Value = SourceWord(source);
        _panelText.Value = PanelWord(panel);
        _modeText.Value = ModeWord(mode, available);
        SetOwn(_curRate, panel.Panel is { } p ? p.CurrentHz.ToString(CultureInfo.InvariantCulture) : "none");
        SetOwn(_curMode, PowerModes.Id(mode));
        _rateEnabled.Value = panel.State == PanelState.Active;
        _modeEnabled.Value = available;
        _panelInfoOpen.Value = panel.State != PanelState.Active;
        _modeInfoOpen.Value = !available;
        if (!_approving) SetOwn(_auto, IsApproved());
        bool approved = IsApproved();
        _autoLine.Value = approved ? LocalisedText.Of("line.on", SourceWord(source)) : _rulesChanged ? LocalisedText.Of("line.changed") : LocalisedText.Of("line.off");
        _rowLine.Value = !approved && _rulesChanged ? LocalisedText.Of("line.changed") : default;

        if (_builtKey is not null && _builtKey != key) _host.RequestPageRefresh();
    }

    // ------------------------------------------------------------------ the page

    public PageModel BuildPage()
    {
        var loc = _host.Localisation;
        PanelLookup panel;
        int beforeHz;
        string beforeMode;
        int[] known;
        lock (_state)
        {
            panel = _panel;
            beforeHz = _s.BeforeHz;
            beforeMode = _s.BeforeMode;
            known = _s.KnownRates.ToArray();
            _builtKey = string.Join(",", _s.KnownRates) + "|" + (panel.State == PanelState.Active) + "|" + _s.BeforeHz + "|" + _s.BeforeMode + "|" + (beforeHz != 0 || !string.IsNullOrEmpty(beforeMode));
        }
        var rates = panel.Panel?.Rates.ToArray() ?? known;

        // The rule rows list the rates the panel listed last time, plus the one stored in the rule, so a stored value never disappears.
        IReadOnlyList<ChoiceOption> RuleRates(ObservableValue<string> value)
        {
            var all = new SortedSet<int>(known.Concat(rates));
            if (ParseHz(value.Value) is var stored and > 0) all.Add(stored);
            var options = new List<ChoiceOption> { new(Leave, "opt.leave") };
            options.AddRange(all.Select(hz => new ChoiceOption(hz.ToString(CultureInfo.InvariantCulture), Text.Literal(loc.F("rule.hz", hz)))));
            return options;
        }

        IReadOnlyList<ChoiceOption> RuleModes()
        {
            var options = new List<ChoiceOption> { new(Leave, "opt.leave") };
            options.AddRange(PowerModes.All.Select(m => new ChoiceOption(PowerModes.Id(m), PowerModes.LabelKey(m))));
            return options;
        }

        var live = panel.Panel?.Rates.ToArray() ?? Array.Empty<int>();
        var nowRates = live.Length > 0
            ? live.Select(hz => new ChoiceOption(hz.ToString(CultureInfo.InvariantCulture), Text.Literal(loc.F("rule.hz", hz)))).ToList()
            : new List<ChoiceOption> { new("none", "opt.no-panel") };
        var nowModes = PowerModes.All.Select(m => new ChoiceOption(PowerModes.Id(m), PowerModes.LabelKey(m))).ToList();

        var groups = new List<RowGroup>
        {
            new("group.now", new Row[]
            {
                new ValueRow("row.source.header", _sourceText),
                new ValueRow("row.panel.header", _panelText),
                new ValueRow("row.mode.header", _modeText),
                new InfoRow(Severity.Info, "info.no-panel.header") { DescriptionKey = "info.no-panel.description", IsOpen = _panelInfoOpen },
                new InfoRow(Severity.Warning, "info.no-mode.header") { DescriptionKey = "info.no-mode.description", IsOpen = _modeInfoOpen },
            }),
            new("group.change", new Row[]
            {
                new ChoiceRow("row.rate.header", nowRates, _curRate) { HardwareEffect = RefreshEffect, Enabled = _rateEnabled, HelpKey = "row.rate.help" },
                new ChoiceRow("row.pmode.header", nowModes, _curMode) { HardwareEffect = ModeEffect, Enabled = _modeEnabled, HelpKey = "row.pmode.help" },
            }),
            new("group.battery", new Row[]
            {
                new ChoiceRow("row.bat-rate.header", RuleRates(_batRate), _batRate),
                new ChoiceRow("row.bat-mode.header", RuleModes(), _batMode),
            }),
            new("group.plugged", new Row[]
            {
                new ChoiceRow("row.plug-rate.header", RuleRates(_plugRate), _plugRate),
                new ChoiceRow("row.plug-mode.header", RuleModes(), _plugMode),
            }),
        };

        if (beforeHz != 0 || !string.IsNullOrEmpty(beforeMode))
        {
            var parts = new List<string>();
            if (beforeHz != 0) parts.Add(loc.F("rule.hz", beforeHz));
            if (PowerModes.FromId(beforeMode) is { } m) parts.Add(loc.T(PowerModes.LabelKey(m)));
            var effect = new HardwareEffect(Text.Literal(loc.F("effect.restore", string.Join(", ", parts)))) { Kind = EffectKind.SystemSetting, Revertible = false };
            groups.Add(new("group.before", new Row[]
            {
                new ButtonRow("row.restore.header", "button.restore", _ => Task.Run(RestoreNow)) { HardwareEffect = effect, DescriptionKey = "row.restore.description" },
            }));
        }

        return new PageModel
        {
            Primary = new ToggleRow("row.auto.header", _auto) { HelpKey = "row.auto.help", Line = _rowLine },
            Groups = groups,
            Summary = new PageSummary(_autoLine),
        };
    }
}
