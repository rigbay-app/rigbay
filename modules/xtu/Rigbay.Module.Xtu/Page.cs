using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

// The page (level 1, declarative rows), the values it binds to, and the registrations that depend on the profile list
// (hotkeys, tray items). Order on the page: what is active now and how to change it; live readings; auto-switch;
// the profiles of your own; hotkeys; facts about XTU.

internal sealed partial class XtuRuntime
{
    private const string NoneId = "-";

    // Values the rows bind to. They live as long as the runtime.
    private readonly ObservableValue<string> _activeText = new("");
    private readonly ObservableValue<string> _limitsText = new("");
    private readonly ObservableValue<string> _message = new("");
    private readonly ObservableValue<string> _selected = new(NoneId);
    private readonly ObservableValue<bool> _profileEnabled = new(true);
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.unreachable"));
    private readonly ObservableValue<bool> _unreachable = new(false);
    private readonly ObservableValue<bool> _fastStartup = new(false);
    private readonly ObservableValue<string> _restoreText = new("");
    private readonly ObservableValue<string> _versionText = new("");
    private readonly ObservableValue<string> _powerNow = new("");
    private readonly ObservableValue<bool> _autoSwitch = new(false);
    private readonly ObservableValue<string> _acChoice = new(NoneId);
    private readonly ObservableValue<string> _batChoice = new(NoneId);
    private readonly ObservableValue<bool> _perCore = new(false);
    private readonly ObservableValue<bool> _noCustom = new(true);

    // The draft of "New profile".
    private readonly ObservableValue<string> _newName = new("");
    private readonly ObservableValue<double> _newPl1 = new(45);
    private readonly ObservableValue<double> _newPl2 = new(65);
    private readonly ObservableValue<bool> _newPl2Enable = new(true);
    private readonly ObservableValue<double> _newWindow = new(28);

    private readonly Dictionary<string, ProfileEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedIds = new();
    private volatile bool _suppressChoice;
    private volatile bool _suppressAuto;
    private string _lastErrorKey = "";

    private sealed record ProfileEntry(string ActionId, TrayItem Tray, IHotkeyBinding? Binding);

    private static string IdOf(XtuProfile p) => "p:" + p.Name;

    private ILocalisation Loc => _host.Localisation;

    /// <summary>Wires the value handlers once (the values outlive Shutdown, so enabling again must not double them).</summary>
    private void InitObservables()
    {
        _selected.Changed += OnProfileChosen;
        _autoSwitch.Changed += () => OnAutoSettingChanged(toggled: true);
        _acChoice.Changed += () => OnAutoSettingChanged(toggled: false);
        _batChoice.Changed += () => OnAutoSettingChanged(toggled: false);
        _perCore.Changed += () =>
        {
            if (_suppressAuto) return;
            _settings.PublishPerCore = _perCore.Value;
            _sampler.PerCore = _perCore.Value;
            SaveSettings();
        };
    }

    // ---- showing state --------------------------------------------------------------------------------------------------

    private void ShowState(XtuState? state, XtuException? error)
    {
        if (error is not null)
        {
            _unreachable.Value = true;
            _profileEnabled.Value = false;
            _stateLine.Value = LocalisedText.Of("state.unreachable");
            _activeText.Value = Loc.T("value.unknown");
            _limitsText.Value = "";
            if (_lastErrorKey != error.Message)
            {
                _lastErrorKey = error.Message;
                SetMessage(error.ToText());
            }
            return;
        }

        _lastErrorKey = "";
        _unreachable.Value = false;
        _profileEnabled.Value = true;
        var active = ActiveProfile();
        _activeText.Value = active?.Name ?? Loc.T("value.custom-values");
        _limitsText.Value = state is null ? "" : Fmt.Limits(state);
        _stateLine.Value = active is null ? LocalisedText.Of("state.custom") : LocalisedText.Of("state.active", active.Name);

        _suppressChoice = true;
        try { _selected.Value = active is null ? NoneId : IdOf(active); }
        finally { _suppressChoice = false; }

        UpdateTrayChecks();

        if (state is not null)
        {
            _restoreText.Value = Loc.T(state.RestoreOnBoot ? "value.yes" : "value.no");
            _host.Metrics.Publish(XtuMetrics.Pl1, (double)state.Pl1, MetricUnit.Watt);
            _host.Metrics.Publish(XtuMetrics.Pl2, (double)state.Pl2, MetricUnit.Watt);
        }
        _fastStartup.Value = string.Equals(_sys.FastStartup(), "On", StringComparison.Ordinal);
        var version = _sys.ServiceVersion(XtuPaths.ServiceName);
        _versionText.Value = version.Length > 0 ? version : Loc.T("value.unknown");
    }

    /// <summary>The tray item of the active profile gets the check mark.</summary>
    private void UpdateTrayChecks()
    {
        var active = ActiveProfile();
        foreach (var (name, entry) in _entries)
            entry.Tray.Checked.Value = active is not null && string.Equals(active.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    private void SetMessage(LocalisedText text) => _message.Value = Loc.Resolve(text);

    private void UpdatePowerText()
    {
        _powerNow.Value = Loc.T(_power.Line switch
        {
            PowerLine.Online => "value.on-ac",
            PowerLine.Offline => "value.on-battery",
            _ => "value.unknown",
        });
    }

    // ---- the profile choice ---------------------------------------------------------------------------------------------

    private void OnProfileChosen()
    {
        if (_suppressChoice) return;
        var id = _selected.Value;
        if (id == NoneId) return;
        var profile = Profiles.FirstOrDefault(p => IdOf(p) == id);
        if (profile is null) return;
        // The host has shown the change and the user confirmed; the work is slow, so it leaves the UI thread.
        Fire("apply " + profile.Name, () => ApplyReportingAsync(profile, ApplyOrigin.PageConfirmed));
    }

    private void Fire(string what, Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception ex) { _host.Log.Error(what + " failed", ex); }
        });
    }

    // ---- auto-switch settings -------------------------------------------------------------------------------------------

    private void LoadAutoChoices()
    {
        _suppressAuto = true;
        try
        {
            _acChoice.Value = ChoiceFor(_settings.AcProfile);
            _batChoice.Value = ChoiceFor(_settings.BatteryProfile);
        }
        finally { _suppressAuto = false; }
    }

    private string ChoiceFor(string name) =>
        Catalog.FindByName(Profiles, name) is { } p ? IdOf(p) : NoneId;

    private string NameFor(string id) =>
        id == NoneId ? "" : Profiles.FirstOrDefault(p => IdOf(p) == id)?.Name ?? "";

    private void OnAutoSettingChanged(bool toggled)
    {
        if (_suppressAuto) return;
        _settings.AutoSwitch = _autoSwitch.Value;
        _settings.AcProfile = NameFor(_acChoice.Value);
        _settings.BatteryProfile = NameFor(_batChoice.Value);
        SaveSettings();
        // Turning it on, or changing the profile of the power source in use, applies at once (the host confirmed the change).
        // That apply is the user's own click, so it is announced as one (in the window when they look, a toast otherwise).
        bool applyNow = toggled ? _settings.AutoSwitch : true;
        if (!applyNow)
            Notify(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.auto-off"), ToastKind.Info, ToastOrigin.Page);
        else
            Fire("auto-switch", () => { AutoCheck(force: true, ApplyOrigin.PageConfirmed); return Task.CompletedTask; });
    }

    // ---- hotkeys and tray -----------------------------------------------------------------------------------------------

    private void RegisterProfileEntries(IEnumerable<XtuProfile> profiles)
    {
        var xtuCombos = HotkeyPolicy.ReadXtuCombos(_paths.UiSettings, _paths.HotkeySettings);
        foreach (var p in profiles)
        {
            if (_entries.ContainsKey(p.Name)) continue;
            var name = p.Name;
            var id = HotkeyPolicy.ActionId(name, _usedIds);

            IHotkeyBinding? binding = null;
            try
            {
                binding = _host.Hotkeys.Register(new HotkeyRegistration(id, Text.Literal(name), null,
                    () => Fire("hotkey " + name, () => ApplyByNameAsync(name, ApplyOrigin.Hotkey))));
                GuardBinding(binding);
            }
            catch (ArgumentException ex)
            {
                _host.Log.Warn($"hotkey for '{name}' not registered: {ex.Message}");
            }

            var tray = new TrayItem(id, Text.Literal(name),
                () => Fire("tray " + name, () => ApplyByNameAsync(name, ApplyOrigin.Tray))) { Checkable = true };
            try { _host.Tray.Add(tray); }
            catch (ArgumentException ex) { _host.Log.Warn($"tray item for '{name}' not added: {ex.Message}"); }

            _entries[name] = new ProfileEntry(id, tray, binding);
        }
        UpdateTrayChecks();
    }

    /// <summary>A gesture XTU already listens for (or one without Ctrl or Alt) is refused: the previous gesture comes back.</summary>
    private void GuardBinding(IHotkeyBinding binding)
    {
        HotkeyGesture? lastGood = binding.Gesture.Value;
        bool restoring = false;
        binding.Gesture.Changed += () =>
        {
            if (restoring) return;
            var g = binding.Gesture.Value;
            if (g is null) { lastGood = null; return; }
            var reason = HotkeyPolicy.Refuse(g.Value, HotkeyPolicy.ReadXtuCombos(_paths.UiSettings, _paths.HotkeySettings));
            if (reason is null) { lastGood = g; return; }

            restoring = true;
            try { binding.Gesture.Value = lastGood; }
            finally { restoring = false; }
            var text = LocalisedText.Of(reason, g.Value.ToString());
            SetMessage(text);
            Notify(LocalisedText.Of("toast.title-failed"), text, ToastKind.Warning, ToastOrigin.Background);
        };
    }

    // ---- the page -------------------------------------------------------------------------------------------------------

    public PageModel BuildPage()
    {
        var profiles = Profiles;
        var options = new List<ChoiceOption> { new(NoneId, "choice.custom-values") };
        options.AddRange(profiles.Select(p => new ChoiceOption(IdOf(p), Text.Literal($"{p.Name}  ·  {p.Describe()}"))));
        var autoOptions = new List<ChoiceOption> { new(NoneId, "choice.keep-current") };
        autoOptions.AddRange(profiles.Select(p => new ChoiceOption(IdOf(p), Text.Literal($"{p.Name}  ·  {p.Describe()}"))));

        var now = new RowGroup("group.now", new Row[]
        {
            new InfoRow(Severity.Error, "row.unreachable.header") { DescriptionKey = "row.unreachable.description", IsOpen = _unreachable },
            new InfoRow(Severity.Warning, "row.faststart.header") { DescriptionKey = "row.faststart.description", IsOpen = _fastStartup },
            new ValueRow("row.active.header", _activeText) { DescriptionKey = "row.active.description" },
            new ValueRow("row.limits.header", _limitsText) { DescriptionKey = "row.limits.description" },
            new ChoiceRow("row.profile.header", options, _selected)
            {
                DescriptionKey = "row.profile.description",
                HardwareEffect = new HardwareEffect(ApplyService.EffectKey) { RevertDescriptionKey = "effect.apply-profile-revert" },   // no SafeRevert: a revert can raise the limits, so it is asked about
                Enabled = _profileEnabled,
            },
            new ValueRow("row.status.header", _message),
            new ButtonRow("row.reload.header", "row.reload.button", ReloadAsync) { DescriptionKey = "row.reload.description" },
        });

        var readings = new RowGroup("group.readings", new Row[]
        {
            new ReadingRow("row.temp.header", XtuMetrics.CpuTemp) { Decimals = 0 },
            new ReadingRow("row.power.header", XtuMetrics.CpuPower) { Decimals = 1 },
            new ReadingRow("row.usage.header", XtuMetrics.CpuUsage) { Decimals = 0 },
            new ReadingRow("row.clock.header", XtuMetrics.CpuClock) { Decimals = 0 },
            new ReadingRow("row.pl1.header", XtuMetrics.Pl1) { Decimals = 0 },
            new ReadingRow("row.pl2.header", XtuMetrics.Pl2) { Decimals = 0 },
            new ExpanderRow("row.throttle.header", new Row[]
            {
                new ReadingRow("row.throttle-thermal.header", XtuMetrics.ThrottleThermal) { Decimals = 0 },
                new ReadingRow("row.throttle-power.header", XtuMetrics.ThrottlePower) { Decimals = 0 },
                new ReadingRow("row.throttle-current.header", XtuMetrics.ThrottleCurrent) { Decimals = 0 },
                new ReadingRow("row.throttle-vr.header", XtuMetrics.ThrottleVr) { Decimals = 0 },
            }) { DescriptionKey = "row.throttle.description" },
        });

        var auto = new RowGroup("group.auto", new Row[]
        {
            new ToggleRow("row.auto.header", _autoSwitch)
            {
                DescriptionKey = "row.auto.description",
                HardwareEffect = new HardwareEffect("effect.auto-switch") { RevertDescriptionKey = "effect.auto-switch-revert" },
            },
            new ChoiceRow("row.ac.header", autoOptions, _acChoice)
            {
                DescriptionKey = "row.ac.description",
                HardwareEffect = new HardwareEffect("effect.auto-choice") { RevertDescriptionKey = "effect.auto-choice-revert" },
            },
            new ChoiceRow("row.battery.header", autoOptions, _batChoice)
            {
                DescriptionKey = "row.battery.description",
                HardwareEffect = new HardwareEffect("effect.auto-choice") { RevertDescriptionKey = "effect.auto-choice-revert" },
            },
            new ValueRow("row.power-now.header", _powerNow),
        });

        var custom = new List<Row>
        {
            NewProfileRow(),
            new InfoRow(Severity.Info, "row.no-custom.header") { DescriptionKey = "row.no-custom.description", IsOpen = _noCustom },
        };
        foreach (var c in _settings.Customs.ToList()) custom.Add(CustomProfileRow(c));
        _noCustom.Value = _settings.Customs.Count == 0;

        var hotkeyRows = profiles
            .Where(p => _entries.TryGetValue(p.Name, out var e) && e.Binding is not null)
            .Select(p => (Row)new ShortcutRow(Text.Literal(p.Name), _entries[p.Name].Binding!.Gesture) { DescriptionKey = "row.hotkey.description" })
            .ToList();

        var groups = new List<RowGroup>
        {
            now,
            readings,
            auto,
            new RowGroup("group.custom", custom),
        };
        if (hotkeyRows.Count > 0)
            groups.Add(new RowGroup("group.hotkeys", new Row[]
            {
                new ExpanderRow("row.hotkeys.header", hotkeyRows) { DescriptionKey = "row.hotkeys.description" },
            }));
        groups.Add(new RowGroup("group.about", new Row[]
        {
            new ValueRow("row.version.header", _versionText),
            new ValueRow("row.restore.header", _restoreText) { DescriptionKey = "row.restore.description" },
            new ToggleRow("row.percore.header", _perCore) { DescriptionKey = "row.percore.description" },
        }));

        return new PageModel
        {
            Icon = "Processing",
            Groups = groups,
            Links = new[] { new PageLink("link.docs", "https://github.com/rigbay-app/module-xtu") },
            Summary = new PageSummary(_stateLine, new[] { XtuMetrics.CpuTemp, XtuMetrics.CpuPower }),
        };
    }

    private Task<ActionResult> ReloadAsync(CancellationToken ct)
    {
        LoadProfiles();
        Refresh();
        RegisterProfileEntries(Profiles);
        RequestPageRefresh();
        return Task.FromResult(ActionResult.Ok());
    }

    // ---- profiles of your own -------------------------------------------------------------------------------------------

    private static LocalisedText? ToText(XtuException? e) => e?.ToText();

    private Row NewProfileRow() => new ExpanderRow("row.new.header", new Row[]
    {
        new TextRow("row.new-name.header", _newName)
        {
            DescriptionKey = "row.new-name.description",
            PlaceholderKey = "new.placeholder",
            MaxLength = Limits.MaxNameLength,
        },
        new NumberRow("row.edit-pl1.header", _newPl1, (double)Limits.MinPl1, (double)Limits.MaxPl1) { Unit = MetricUnit.Watt, DescriptionKey = "row.edit-pl1.description" },
        new NumberRow("row.edit-pl2.header", _newPl2, (double)Limits.MinPl1, (double)Limits.MaxPl2) { Unit = MetricUnit.Watt, DescriptionKey = "row.edit-pl2.description" },
        new ToggleRow("row.edit-pl2enable.header", _newPl2Enable) { DescriptionKey = "row.edit-pl2enable.description" },
        new NumberRow("row.edit-window.header", _newWindow, (double)Limits.MinWindow, (double)Limits.MaxWindow) { Unit = MetricUnit.Seconds, DescriptionKey = "row.edit-window.description" },
        new ButtonRow("row.create.header", "row.create.button", CreateAsync) { DescriptionKey = "row.create.description" },
    }) { DescriptionKey = "row.new.description" };

    private Task<ActionResult> CreateAsync(CancellationToken ct)
    {
        var name = _newName.Value.Trim();
        var problem = Rules.CheckName(name, Profiles.Select(p => p.Name))
                      ?? Rules.Check((decimal)_newPl1.Value, (decimal)_newPl2.Value, (decimal)_newWindow.Value);
        if (problem is not null) return Task.FromResult(ActionResult.Fail(problem.Key, problem.Args));

        var created = new CustomProfile
        {
            Name = name,
            Pl1 = _newPl1.Value,
            Pl2 = _newPl2.Value,
            Pl2Enable = _newPl2Enable.Value,
            Window = _newWindow.Value,
        };
        _settings.Customs.Add(created);
        SaveSettings();
        LoadProfiles();
        RegisterProfileEntries(Profiles);
        _noCustom.Value = false;
        _newName.Value = "";
        Refresh();
        RequestPageRefresh();
        return Task.FromResult(ActionResult.Ok(LocalisedText.Of("msg.created", name)));
    }

    private Row CustomProfileRow(CustomProfile c)
    {
        var pl1 = new ObservableValue<double>(c.Pl1);
        var pl2 = new ObservableValue<double>(c.Pl2);
        var enable = new ObservableValue<bool>(c.Pl2Enable);
        var window = new ObservableValue<double>(c.Window);

        void Changed()
        {
            c.Pl1 = pl1.Value;
            c.Pl2 = pl2.Value;
            c.Pl2Enable = enable.Value;
            c.Window = window.Value;
            SaveSettings();
            LoadProfiles();
            Refresh();
            RequestPageRefresh();
        }
        pl1.Changed += Changed;
        pl2.Changed += Changed;
        enable.Changed += Changed;
        window.Changed += Changed;

        LocalisedText? Check(double a, double b, double w) => ToText(Rules.Check((decimal)a, (decimal)b, (decimal)w));

        return new ExpanderRow(Text.Literal(c.Name), new Row[]
        {
            new NumberRow("row.edit-pl1.header", pl1, (double)Limits.MinPl1, (double)Limits.MaxPl1)
            {
                Unit = MetricUnit.Watt, Apply = ApplyMode.ApplyRevert, DescriptionKey = "row.edit-pl1.description",
                Validate = v => Check(v, pl2.Value, window.Value),
            },
            new NumberRow("row.edit-pl2.header", pl2, (double)Limits.MinPl1, (double)Limits.MaxPl2)
            {
                Unit = MetricUnit.Watt, Apply = ApplyMode.ApplyRevert, DescriptionKey = "row.edit-pl2.description",
                Validate = v => Check(pl1.Value, v, window.Value),
            },
            new ToggleRow("row.edit-pl2enable.header", enable) { DescriptionKey = "row.edit-pl2enable.description" },
            new NumberRow("row.edit-window.header", window, (double)Limits.MinWindow, (double)Limits.MaxWindow)
            {
                Unit = MetricUnit.Seconds, Apply = ApplyMode.ApplyRevert, DescriptionKey = "row.edit-window.description",
                Validate = v => Check(pl1.Value, pl2.Value, v),
            },
            new ButtonRow("row.delete.header", "row.delete.button", ct => DeleteAsync(c)) { DescriptionKey = "row.delete.description" },
        }) { DescriptionKey = "row.custom.description" };
    }

    private Task<ActionResult> DeleteAsync(CustomProfile c)
    {
        _settings.Customs.Remove(c);
        if (string.Equals(_settings.AcProfile, c.Name, StringComparison.OrdinalIgnoreCase)) _settings.AcProfile = "";
        if (string.Equals(_settings.BatteryProfile, c.Name, StringComparison.OrdinalIgnoreCase)) _settings.BatteryProfile = "";
        SaveSettings();
        if (_entries.Remove(c.Name, out var entry)) _host.Tray.Remove(entry.ActionId);
        LoadProfiles();
        LoadAutoChoices();
        _noCustom.Value = _settings.Customs.Count == 0;
        Refresh();
        RequestPageRefresh();
        return Task.FromResult(ActionResult.Ok(LocalisedText.Of("msg.deleted", c.Name)));
    }
}
