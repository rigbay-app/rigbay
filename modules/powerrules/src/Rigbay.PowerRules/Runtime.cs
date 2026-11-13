using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.PowerRules;

/// <summary>
/// The module's working part: reads, the two writes, the rules and the approval of the rule set. <c>Page.cs</c> is the page. All writes go
/// through <see cref="ChangeRefresh"/> and <see cref="ChangeMode"/> under one lock; both are tested with fakes for every Windows call.
/// </summary>
internal sealed partial class PowerRulesRuntime
{
    private readonly IHostServices _host;
    private readonly Dependencies _deps;
    private readonly object _write = new();      // one machine change at a time
    private readonly object _state = new();      // settings and the last reads
    private readonly SourceTracker _tracker = new();
    private readonly List<Task> _tasks = new();
    private PowerRulesSettings _s = new();
    private IDisposable? _timer;
    private IDisposable? _fallback;
    private bool _pendingForce;
    private volatile bool _stopping;
    private bool _hooked;

    private PowerSource _source;
    private PanelLookup _panel = PanelLookup.None;
    private PowerMode _mode = PowerMode.Unknown;
    private bool _modeAvailable;
    private bool _rulesChanged;
    private volatile bool _approving;

    [ThreadStatic] private static bool _own;

    public PowerRulesRuntime(IHostServices host, Dependencies deps)
    {
        _host = host;
        _deps = deps;
        CreateRows();
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        _s = _host.Settings.Load<PowerRulesSettings>();
        _s.KnownRates ??= Array.Empty<int>();

        // Reads only. Nothing is written here: the rules run on the timer below, after the owner's approval.
        ReadAll();
        EnsureDefaults();
        LoadRows();
        RefreshTexts();

        RegisterCommands();
        _host.Localisation.LanguageChanged += OnLanguageChanged;
        _host.PageVisible.Changed += OnPageVisible;
        _deps.Source.SourceChanged += OnSourceChanged;
        _deps.Source.Resumed += OnResumed;
        _hooked = true;
        _deps.Source.Start();
        if (!_deps.Source.Listening)
        {
            _host.Log.Warn("power-rules: Windows did not accept the power source notification; reading the source every " + (int)_deps.FallbackPoll.TotalSeconds + " s instead");
            ArmFallback();
        }

        // The first source seen counts as a change (as in XTU's auto-switch): the rules for the source now run after the settle delay.
        if (IsApproved()) Arm(_deps.SettleDelay, force: false);
    }

    public void Stop()
    {
        _stopping = true;
        if (_hooked)
        {
            _host.Localisation.LanguageChanged -= OnLanguageChanged;
            _host.PageVisible.Changed -= OnPageVisible;
            _deps.Source.SourceChanged -= OnSourceChanged;
            _deps.Source.Resumed -= OnResumed;
            _hooked = false;
        }
        lock (_state) { _timer?.Dispose(); _timer = null; _fallback?.Dispose(); _fallback = null; }

        // The approved plan says that turning the module off puts back the values from before the first automatic change.
        if (IsApproved() && HasBefore())
        {
            var restore = Task.Run(() =>
            {
                try { lock (_write) RestoreBefore(); }
                catch (Exception ex) { _host.Log.Error("power-rules: putting the previous values back failed", ex); }
            });
            try { restore.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        }

        Task[] pending;
        lock (_tasks) pending = _tasks.ToArray();
        try { Task.WaitAll(pending, TimeSpan.FromSeconds(1)); } catch (Exception) { }
        try { _deps.Source.Dispose(); } catch (Exception ex) { _host.Log.Warn("power-rules: stopping the power listener failed", ex); }
    }

    public async Task WhenIdleAsync()
    {
        for (; ; )
        {
            Task[] pending;
            lock (_tasks) pending = _tasks.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ background work

    private Task Track(Func<Task> work, string what)
    {
        Task task = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _host.Log.Error("power-rules: " + what + " failed", ex); }
        });
        lock (_tasks) _tasks.Add(task);
        task.ContinueWith(t => { lock (_tasks) _tasks.Remove(t); }, TaskScheduler.Default);
        return task;
    }

    private Task Track(Action work, string what) => Track(() => { work(); return Task.CompletedTask; }, what);

    // ------------------------------------------------------------------ reads

    /// <summary>Reads the source, the panel and the power mode. Each read is guarded; a failing read is "unknown", never an exception.</summary>
    private void ReadAll()
    {
        var source = Safe(() => _deps.Source.Read(), PowerSource.Unknown, "power source");
        var panel = Safe(() => _deps.Panel.Find(), new PanelLookup(PanelState.Unreadable), "panel");
        bool available = Safe(() => _deps.Mode.Available, false, "power mode export");
        var mode = available ? Safe(() => _deps.Mode.Read(), PowerMode.Unknown, "power mode") : PowerMode.Unknown;
        lock (_state)
        {
            _source = source;
            _panel = panel;
            _modeAvailable = available;
            _mode = mode;
        }
        if (panel.Panel is { } p) RememberRates(p);
        ClearSettledBefore(panel.Panel?.CurrentHz, available ? mode : null);
    }

    private T Safe<T>(Func<T> read, T fallback, string what)
    {
        try { return read(); }
        catch (Exception ex)
        {
            _host.Log.Warn("power-rules: reading the " + what + " failed", ex);
            return fallback;
        }
    }

    private void RememberRates(PanelInfo panel)
    {
        bool changed;
        lock (_state)
        {
            changed = !_s.KnownRates.SequenceEqual(panel.Rates);
            if (changed) _s.KnownRates = panel.Rates.ToArray();
        }
        if (changed) SaveSettings();
    }

    // ------------------------------------------------------------------ rules and approval

    private RuleSet CurrentRules()
    {
        lock (_state)
            return new RuleSet(new Rule(_s.BatteryHz, PowerModes.FromId(_s.BatteryMode)), new Rule(_s.PluggedHz, PowerModes.FromId(_s.PluggedMode)));
    }

    /// <summary>True only while the owner's approval is for exactly the rules now: an edited rule, or a hand-edited settings file, fails the hash.</summary>
    private bool IsApproved()
    {
        var rules = CurrentRules();
        lock (_state) return _s.AutoApproved && !string.IsNullOrEmpty(_s.ApprovedHash) && string.Equals(_s.ApprovedHash, rules.Hash(), StringComparison.Ordinal);
    }

    private void EnsureDefaults()
    {
        bool changed = false;
        lock (_state)
        {
            if (!_s.Initialised)
            {
                _s.BatteryMode = PowerModes.Id(PowerMode.BestEfficiency);
                _s.PluggedMode = PowerModes.Id(PowerMode.Balanced);
                _s.Initialised = true;
                changed = true;
            }
            // The rates need a panel that has been seen: battery 60 Hz when it lists 60, plugged the highest it lists. Until then: leave.
            if (!_s.RatesDefaulted && _s.BatteryHz == 0 && _s.PluggedHz == 0 && _panel.Panel is { Rates.Count: > 0 } panel)
            {
                if (panel.Lists(60)) _s.BatteryHz = 60;
                _s.PluggedHz = panel.Rates.Max();
                _s.RatesDefaulted = true;
                changed = true;
            }
        }
        if (changed) SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            PowerRulesSettings copy;
            lock (_state) copy = _s.Clone();
            _host.Settings.Save(copy);
        }
        catch (Exception ex) { _host.Log.Warn("power-rules: saving the settings failed", ex); }
    }

    /// <summary>The owner turned automatic switching on: ask once about exactly these rules.</summary>
    private async Task ApproveAsync()
    {
        _approving = true;
        try { await AskAndApproveAsync().ConfigureAwait(false); }
        finally { _approving = false; }
        RefreshTexts();
    }

    private async Task AskAndApproveAsync()
    {
        var rules = CurrentRules();
        if (rules.IsEmpty)
        {
            SetOwn(_auto, false);
            _host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.no-rules"), ToastKind.Warning) { Origin = ToastOrigin.Page });
            return;
        }
        var detail = LocalisedText.Of("confirm.rules", DescribeRule(rules.Battery), DescribeRule(rules.Plugged));
        bool yes = await _host.Approval.ConfirmAsync(AutoEffect, detail, _host.StopToken).ConfigureAwait(false);

        // The rules may have been edited while the question was open; the answer then belongs to other rules and counts for nothing.
        bool same = CurrentRules().Hash() == rules.Hash();
        if (!yes || !same)
        {
            SetOwn(_auto, false);
            if (yes && !same) _host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.rules-changed"), ToastKind.Warning) { Origin = ToastOrigin.Page });
            return;
        }
        lock (_state)
        {
            _s.AutoApproved = true;
            _s.ApprovedHash = rules.Hash();
            _rulesChanged = false;
        }
        SaveSettings();
        _host.Log.Info("power-rules: automatic switching approved for rule set " + rules.Hash()[..12]);
        RefreshTexts();
        Arm(TimeSpan.Zero, force: true);   // the dialog said the rule for the source now also runs now
    }

    /// <summary>The owner turned automatic switching off: the approval ends and the values from before are put back.</summary>
    private void TurnOff()
    {
        bool had;
        lock (_state)
        {
            had = _s.AutoApproved;
            _s.AutoApproved = false;
            _s.ApprovedHash = "";
            _rulesChanged = false;
        }
        SaveSettings();
        lock (_state) { _timer?.Dispose(); _timer = null; _pendingForce = false; }
        List<StepResult> results;
        lock (_write) results = HasBefore() ? RestoreBefore() : new List<StepResult>();
        ReadAll();
        RefreshTexts();
        if (had && results.Count > 0) NotifyNow("toast.restored", results, ToastOrigin.Page);
    }

    /// <summary>A rule was edited on the page: the approval was for other rules, so it ends and the owner is asked again to turn it on.</summary>
    private void RuleEdited()
    {
        if (_own) return;
        bool wasApproved;
        lock (_state)
        {
            ReadRows();
            wasApproved = _s.AutoApproved;
            if (wasApproved) { _s.AutoApproved = false; _s.ApprovedHash = ""; _rulesChanged = true; }
        }
        SaveSettings();
        if (wasApproved) SetOwn(_auto, false);
        RefreshTexts();
    }

    // ------------------------------------------------------------------ switching when the source changes

    private void OnSourceChanged() { if (!_stopping) Arm(_deps.SettleDelay, force: false); }

    private void OnResumed() { if (!_stopping) Arm(_deps.ResumeDelay, force: true); }

    private void OnPageVisible()
    {
        if (_host.PageVisible.Value && !_stopping) Track(() => { ReadAll(); RefreshTexts(); }, "read on page open");
    }

    private void OnLanguageChanged()
    {
        RefreshTexts();
        _host.RequestPageRefresh();
    }

    /// <summary>Runs the rules once after <paramref name="delay"/>. A later call replaces the timer (a burst of changes runs once) and keeps a force.</summary>
    private void Arm(TimeSpan delay, bool force)
    {
        lock (_state)
        {
            if (_stopping) return;
            _pendingForce |= force;
            _timer?.Dispose();
            _timer = _deps.Scheduler.Schedule(delay, Fire);
        }
    }

    /// <summary>Only when the notification could not be registered: read the source on a slow timer, and run the usual check (which does nothing unless the source changed).</summary>
    private void ArmFallback()
    {
        lock (_state)
        {
            if (_stopping) return;
            _fallback?.Dispose();
            _fallback = _deps.Scheduler.Schedule(_deps.FallbackPoll, () =>
            {
                if (_stopping) return;
                Track(() => AutoRun(force: false), "source poll");
                ArmFallback();
            });
        }
    }

    private void Fire()
    {
        bool force;
        lock (_state) { force = _pendingForce; _pendingForce = false; }
        Track(() => AutoRun(force), "automatic switch");
    }

    /// <summary>One automatic check. Does nothing unless the source changed (or <paramref name="force"/>) and the approval fits the rules now.</summary>
    internal void AutoRun(bool force)
    {
        if (_stopping) return;
        List<StepResult> results;
        PowerSource source;
        lock (_write)
        {
            source = Safe(() => _deps.Source.Read(), PowerSource.Unknown, "power source");
            if (!_tracker.Check(source, force)) { ReadAll(); RefreshTexts(); return; }
            var rules = CurrentRules();
            if (!IsApproved())
            {
                _host.Log.Debug("power-rules: source is " + source + ", the rules are not approved, nothing changed");
                ReadAll();
                RefreshTexts();
                return;
            }
            results = ApplyRule(rules.For(source), trackBefore: true);
        }
        ReadAll();
        RefreshTexts();
        Notify(source, results);
    }

    private List<StepResult> ApplyRule(Rule rule, bool trackBefore) => new()
    {
        rule.Hz == 0 ? new StepResult(StepKind.Refresh, StepOutcome.Leave) : ChangeRefresh(rule.Hz, trackBefore),
        rule.Mode is null ? new StepResult(StepKind.Mode, StepOutcome.Leave) : ChangeMode(rule.Mode.Value, trackBefore),
    };

    // ------------------------------------------------------------------ the two writes (call with _write held)

    /// <summary>
    /// Sets the internal panel's refresh rate. Only a rate the panel lists at its current resolution; <c>CDS_TEST</c> first and the real call only
    /// when it says yes; the rate is read back. No internal panel active is not an error and says nothing.
    /// </summary>
    internal StepResult ChangeRefresh(int hz, bool trackBefore)
    {
        var lookup = Safe(() => _deps.Panel.Find(), new PanelLookup(PanelState.Unreadable), "panel");
        if (lookup.State == PanelState.NotActive) return new StepResult(StepKind.Refresh, StepOutcome.PanelNotActive);
        if (lookup.State != PanelState.Active || lookup.Panel is null) return new StepResult(StepKind.Refresh, StepOutcome.Unreadable);
        var panel = lookup.Panel;
        string to = hz.ToString(CultureInfo.InvariantCulture), from = panel.CurrentHz.ToString(CultureInfo.InvariantCulture);
        RememberRates(panel);

        if (hz <= 1 || !panel.Lists(hz)) return new StepResult(StepKind.Refresh, StepOutcome.NotListed, from, to);
        if (panel.CurrentHz == hz)
        {
            ClearSettledBefore(hz: hz);
            return new StepResult(StepKind.Refresh, StepOutcome.AlreadySet, from, to);
        }

        DisplayChange test;
        try { test = _deps.Panel.Test(panel, hz); }
        catch (Exception ex) { _host.Log.Warn("power-rules: the display test call failed", ex); return new StepResult(StepKind.Refresh, StepOutcome.Failed, from, to, -1); }
        if (!test.Ok)
        {
            _host.Log.Warn($"power-rules: Windows refused {hz} Hz in the test (code {test.Code}); nothing was changed");
            return new StepResult(StepKind.Refresh, StepOutcome.Refused, from, to, test.Code);
        }

        // Remember the value before the change first, so a crash in the middle still leaves it in the settings file.
        bool remembered = false;
        if (trackBefore)
            lock (_state) if (_s.BeforeHz == 0) { _s.BeforeHz = panel.CurrentHz; remembered = true; }
        if (remembered) SaveSettings();

        DisplayChange applied;
        try { applied = _deps.Panel.Apply(panel, hz); }
        catch (Exception ex) { _host.Log.Warn("power-rules: the display call failed", ex); applied = new DisplayChange(-1); }

        var after = Safe(() => _deps.Panel.Find(), new PanelLookup(PanelState.Unreadable), "panel");
        int now = after.Panel?.CurrentHz ?? 0;
        if (!applied.Ok || now != hz)
        {
            if (remembered) { lock (_state) _s.BeforeHz = 0; SaveSettings(); }
            _host.Log.Warn($"power-rules: refresh rate {from} -> {hz} Hz did not hold (code {applied.Code}, read back {now})");
            return new StepResult(StepKind.Refresh, StepOutcome.Failed, now > 0 ? now.ToString(CultureInfo.InvariantCulture) : from, to, applied.Code);
        }
        _host.Log.Info($"power-rules: refresh rate {from} -> {hz} Hz");
        ClearSettledBefore(hz: now);
        return new StepResult(StepKind.Refresh, StepOutcome.Changed, from, to);
    }

    /// <summary>Sets the Windows power mode to one of the three known modes and reads it back.</summary>
    internal StepResult ChangeMode(PowerMode mode, bool trackBefore)
    {
        string to = PowerModes.Id(mode);
        if (!PowerModes.Guids.ContainsKey(mode)) return new StepResult(StepKind.Mode, StepOutcome.Failed, "", to, -2);
        if (!Safe(() => _deps.Mode.Available, false, "power mode export")) return new StepResult(StepKind.Mode, StepOutcome.ModeUnavailable, "", to);

        var before = Safe(() => _deps.Mode.Read(), PowerMode.Unknown, "power mode");
        string from = PowerModes.Id(before);
        if (before == mode)
        {
            ClearSettledBefore(mode: mode);
            return new StepResult(StepKind.Mode, StepOutcome.AlreadySet, from, to);
        }

        bool remembered = false;
        if (trackBefore && before != PowerMode.Unknown)
            lock (_state) if (string.IsNullOrEmpty(_s.BeforeMode)) { _s.BeforeMode = from; remembered = true; }
        if (remembered) SaveSettings();

        int code;
        try { code = _deps.Mode.Set(mode); }
        catch (Exception ex) { _host.Log.Warn("power-rules: the power mode call failed", ex); code = -3; }

        var after = Safe(() => _deps.Mode.Read(), PowerMode.Unknown, "power mode");
        if (code != 0 || after != mode)
        {
            if (remembered) { lock (_state) _s.BeforeMode = ""; SaveSettings(); }
            _host.Log.Warn($"power-rules: power mode {from} -> {to} did not hold (code {code}, read back {PowerModes.Id(after)})");
            return new StepResult(StepKind.Mode, code == -1 ? StepOutcome.ModeUnavailable : StepOutcome.Failed, PowerModes.Id(after), to, code);
        }
        _host.Log.Info($"power-rules: power mode {from} -> {to}");
        ClearSettledBefore(mode: after);
        return new StepResult(StepKind.Mode, StepOutcome.Changed, from, to);
    }

    /// <summary>Puts back the values remembered from before the first automatic change (call with <c>_write</c> held). A value that cannot be put back now stays remembered.</summary>
    private List<StepResult> RestoreBefore()
    {
        int hz;
        PowerMode? mode;
        lock (_state) { hz = _s.BeforeHz; mode = PowerModes.FromId(_s.BeforeMode); }
        var results = new List<StepResult>();
        if (hz != 0) results.Add(ChangeRefresh(hz, trackBefore: false));
        if (mode is { } m) results.Add(ChangeMode(m, trackBefore: false));
        return results;
    }

    private bool HasBefore()
    {
        lock (_state) return _s.BeforeHz != 0 || !string.IsNullOrEmpty(_s.BeforeMode);
    }

    /// <summary>
    /// A remembered value that equals the current one is not "before" any more: forget it. Only the value that is passed is compared, and it must
    /// be a fresh read (never the cached state, which lags behind a change that was just made).
    /// </summary>
    private void ClearSettledBefore(int? hz = null, PowerMode? mode = null)
    {
        bool changed = false;
        lock (_state)
        {
            if (hz is { } currentHz && _s.BeforeHz != 0 && _s.BeforeHz == currentHz) { _s.BeforeHz = 0; changed = true; }
            if (mode is { } currentMode && !string.IsNullOrEmpty(_s.BeforeMode) && PowerModes.FromId(_s.BeforeMode) == currentMode) { _s.BeforeMode = ""; changed = true; }
        }
        if (changed) SaveSettings();
    }

    // ------------------------------------------------------------------ manual changes from the page (the host asked the owner first)

    private void ManualRefresh(int hz)
    {
        List<StepResult> results;
        lock (_write) results = new List<StepResult> { ChangeRefresh(hz, trackBefore: false) };
        ReadAll();
        RefreshTexts();
        NotifyNow("toast.now", results, ToastOrigin.Page);
    }

    private void ManualMode(PowerMode mode)
    {
        List<StepResult> results;
        lock (_write) results = new List<StepResult> { ChangeMode(mode, trackBefore: false) };
        ReadAll();
        RefreshTexts();
        NotifyNow("toast.now", results, ToastOrigin.Page);
    }

    private ActionResult RestoreNow()
    {
        List<StepResult> results;
        lock (_write) results = RestoreBefore();
        ReadAll();
        RefreshTexts();
        NotifyNow("toast.restored", results, ToastOrigin.Page);
        return results.Any(r => r.IsProblem) ? ActionResult.Fail("err.restore") : ActionResult.Ok();
    }

    // ------------------------------------------------------------------ messages

    private string Part(StepResult r)
    {
        var loc = _host.Localisation;
        if (r.Kind == StepKind.Refresh)
            return r.Outcome switch
            {
                StepOutcome.Changed => loc.F("part.refresh", r.To),
                StepOutcome.Refused => loc.F("part.refresh-refused", r.To),
                StepOutcome.Failed => loc.F("part.refresh-failed", r.To, r.From),
                StepOutcome.NotListed => loc.F("part.refresh-not-listed", r.To),
                StepOutcome.Unreadable => loc.T("part.panel-unreadable"),
                _ => "",
            };
        var label = PowerModes.FromId(r.To) is { } mode ? loc.T(PowerModes.LabelKey(mode)) : r.To;
        return r.Outcome switch
        {
            StepOutcome.Changed => loc.F("part.mode", label),
            StepOutcome.Failed => loc.F("part.mode-failed", label),
            StepOutcome.ModeUnavailable => loc.T("part.mode-unavailable"),
            _ => "",
        };
    }

    private string Parts(IEnumerable<StepResult> results) => string.Join(", ", results.Select(Part).Where(p => p.Length > 0));

    /// <summary>One notification per automatic switch, and none when nothing changed and nothing went wrong.</summary>
    private void Notify(PowerSource source, List<StepResult> results)
    {
        var text = Parts(results);
        if (text.Length == 0) return;
        bool problem = results.Any(r => r.IsProblem);
        _host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of(source == PowerSource.Battery ? "toast.battery" : "toast.plugged", text), problem ? ToastKind.Warning : ToastKind.Info));
    }

    private void NotifyNow(string bodyKey, List<StepResult> results, ToastOrigin origin)
    {
        var text = Parts(results);
        if (text.Length == 0) return;
        bool problem = results.Any(r => r.IsProblem);
        _host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of(bodyKey, text), problem ? ToastKind.Warning : ToastKind.Success) { Origin = origin });
    }

    /// <summary>A rule in words, for the approval question and the status verb: "60 Hz, Best efficiency", or "leave as is".</summary>
    private string DescribeRule(Rule rule)
    {
        var loc = _host.Localisation;
        var parts = new List<string>();
        if (rule.Hz != 0) parts.Add(loc.F("rule.hz", rule.Hz));
        if (rule.Mode is { } mode) parts.Add(loc.T(PowerModes.LabelKey(mode)));
        return parts.Count == 0 ? loc.T("rule.leave") : string.Join(", ", parts);
    }

    private static void SetOwn<T>(ObservableValue<T> value, T v)
    {
        bool before = _own;
        _own = true;
        try { value.Value = v; }
        finally { _own = before; }
    }

    // ------------------------------------------------------------------ command

    private void RegisterCommands()
    {
        _host.Commands.Register("status", "command.status", _ =>
        {
            ReadAll();
            var loc = _host.Localisation;
            PanelLookup panel;
            PowerSource source;
            PowerMode mode;
            bool available;
            lock (_state) { panel = _panel; source = _source; mode = _mode; available = _modeAvailable; }
            var rules = CurrentRules();
            var lines = new List<string>
            {
                loc.F("status.source", SourceWord(source)),
                loc.F("status.panel", PanelWord(panel)),
                loc.F("status.mode", ModeWord(mode, available)),
                loc.F("status.battery", DescribeRule(rules.Battery)),
                loc.F("status.plugged", DescribeRule(rules.Plugged)),
                loc.T(IsApproved() ? "status.approved" : "status.not-approved"),
            };
            return CommandResult.Ok(string.Join(Environment.NewLine, lines));
        });
    }
}
