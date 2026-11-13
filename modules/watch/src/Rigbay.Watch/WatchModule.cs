using Rigbay.Abstractions;

namespace Rigbay.Watch;

/// <summary>
/// Alerts on any reading of the metrics bus. READ ONLY: it subscribes to readings, keeps its rules in its own settings and shows a Warning notification when
/// a rule fires. It never publishes a reading, never asks the host to approve anything, never starts a program and never touches the machine.
/// <para>It asks for readings with <see cref="IMetricsBus.SubscribeBackground"/> and never with <c>Subscribe</c>: a background request does not count as a
/// reader, so it does not hold the publishers at their fast rate all day. A publisher built before host API 0.1.4 ignores the request and may not publish
/// at all while nobody looks; the rule then shows "Waiting".</para>
/// Read the skill <c>watch-module</c> before changing it.
/// </summary>
public sealed partial class WatchModule : IModule
{
    /// <summary>How long the tray item and the toggle pause alerts.</summary>
    internal static readonly TimeSpan DefaultPause = TimeSpan.FromHours(1);

    /// <summary>The longest pause the verb accepts (a day).</summary>
    internal const int MaxPauseMinutes = 1_440;

    private readonly object _gate = new();

    /// <summary>When alerts are paused until (a pause that has run out is cleared at the next check). Read and written from the timer, the UI and the verbs.</summary>
    private DateTimeOffset? _pausedUntil
    {
        get { lock (_gate) return _pausedRaw; }
        set { lock (_gate) _pausedRaw = value; }
    }

    private Dependencies? _deps;
    private IHostServices? _host;
    private RuleEngine? _engine;
    private WatchSettings _settings = new();
    private Timer? _timer;
    private bool _running;
    private bool _languageHooked;
    private DateTimeOffset? _pausedRaw;
    private bool _settingToggle;
    private string? _expanded;
    private string _newMetric = "";
    private TrayItem? _trayItem;
    private readonly Dictionary<string, (TimeSpan Interval, IDisposable Subscription)> _subscriptions = new(StringComparer.Ordinal);
    private readonly ObservableValue<bool> _alertsOn = new(true);
    private readonly ObservableValue<LocalisedText> _pauseLine = new(default);
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.starting"));
    private readonly Dictionary<string, ObservableValue<LocalisedText>> _ruleLines = new(StringComparer.Ordinal);
    private Timer? _refreshTimer;
    private bool _catalogDirty;

    /// <summary>The host's constructor: sets nothing.</summary>
    public WatchModule() { }

    /// <summary>Tests: the same module with a fake clock and no timer of its own.</summary>
    internal WatchModule(Dependencies deps) => _deps = deps;

    // ------------------------------------------------------------------ lifecycle

    /// <inheritdoc />
    public ProbeResult Probe() => ProbeResult.Yes();   // it reads the bus and nothing else: every machine supports it

    /// <inheritdoc />
    public void Initialize(IHostServices host)
    {
        _deps ??= Dependencies.Real();
        _host = host;
        _settings = host.Settings.Load<WatchSettings>().Clamped();
        var engine = new RuleEngine(_deps.Now);
        engine.SetRules(_settings.Rules);
        _engine = engine;
        _pausedUntil = null;
        _settingToggle = false;
        _alertsOn.Value = true;
        _pauseLine.Value = default;
        _ruleLines.Clear();
        _expanded = null;

        RegisterCommands(host);
        var tray = new TrayItem("pause", "tray.pause", OnTrayPause) { Checkable = true };
        tray.Visible.Value = _settings.TrayItem;
        host.Tray.Add(tray);
        _trayItem = tray;

        if (!_languageHooked) { host.Localisation.LanguageChanged += OnLanguageChanged; _languageHooked = true; }
        host.PageVisible.Changed += OnPageVisibleChanged;
        host.Metrics.CatalogChanged += OnCatalogChanged;
        _alertsOn.Changed += OnAlertsToggled;

        _running = true;
        Resubscribe();
        Rearm();
        Tick();
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        var host = _host;
        _running = false;
        _alertsOn.Changed -= OnAlertsToggled;
        if (host is not null)
        {
            host.PageVisible.Changed -= OnPageVisibleChanged;
            host.Metrics.CatalogChanged -= OnCatalogChanged;
            host.Localisation.LanguageChanged -= OnLanguageChanged;
            _languageHooked = false;
        }
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
        DropSubscriptions();
        _engine = null;
        _trayItem = null;
    }

    // ------------------------------------------------------------------ the rules

    /// <summary>The saved rules, as copies (tests, the page).</summary>
    internal IReadOnlyList<WatchRule> Rules
    {
        get { lock (_gate) return _settings.Rules.Select(r => r.Clone()).ToList(); }
    }

    /// <summary>Adds a rule (at most <see cref="WatchRule.MaxRules"/>). Returns it as stored, or null when the list is full or the rule cannot be repaired.</summary>
    internal WatchRule? AddRule(WatchRule rule)
    {
        WatchRule? stored;
        lock (_gate)
        {
            if (_settings.Rules.Count >= WatchRule.MaxRules) return null;
            var copy = rule.Clone();
            copy.Id = WatchRule.NewId();
            stored = copy.Normalised();
            if (stored is null) return null;
            _settings.Rules.Add(stored);
        }
        Commit();
        return stored.Clone();
    }

    /// <summary>Removes a rule. Returns false when there is none with this id.</summary>
    internal bool RemoveRule(string id)
    {
        lock (_gate)
        {
            if (_settings.Rules.RemoveAll(r => r.Id == id) == 0) return false;
            _ruleLines.Remove(id);
            if (_expanded == id) _expanded = null;
        }
        Commit();
        return true;
    }

    /// <summary>Changes one rule. The result is repaired to the rule's limits; false when there is no such rule or the result cannot be repaired.</summary>
    internal bool UpdateRule(string id, Action<WatchRule> change)
    {
        lock (_gate)
        {
            int index = _settings.Rules.FindIndex(r => r.Id == id);
            if (index < 0) return false;
            var edited = _settings.Rules[index].Clone();
            change(edited);
            var fixedRule = edited.Normalised();
            if (fixedRule is null) return false;
            fixedRule.Id = id;
            _settings.Rules[index] = fixedRule;
        }
        Commit();
        return true;
    }

    /// <summary>The rules changed: hand them to the engine, save, ask for the readings that are now needed, re-time the check, show the new state.</summary>
    private void Commit()
    {
        var host = _host;
        var engine = _engine;
        if (host is null || engine is null || !_running) return;
        List<WatchRule> snapshot;
        lock (_gate) snapshot = _settings.Rules.Select(r => r.Clone()).ToList();
        engine.SetRules(snapshot);
        try { lock (_gate) host.Settings.Save(_settings); }
        catch (Exception ex) { host.Log.Warn("Saving the Watch settings failed", ex); }
        Resubscribe();
        Rearm();
        Tick();
    }

    // ------------------------------------------------------------------ readings

    /// <summary>
    /// Makes the background subscriptions match the rules: one per metric, at the shortest interval any enabled rule wants. A subscription that is still
    /// right is left alone. Each new one is seeded with the bus's last sample of that metric (the age test decides whether it counts).
    /// </summary>
    private void Resubscribe()
    {
        var host = _host;
        var engine = _engine;
        if (host is null || engine is null || !_running) return;
        var wanted = engine.Wanted();
        lock (_subscriptions)
        {
            foreach (var id in _subscriptions.Keys.ToList())
            {
                if (wanted.TryGetValue(id, out var interval) && _subscriptions[id].Interval == interval) continue;
                try { _subscriptions[id].Subscription.Dispose(); } catch (Exception ex) { host.Log.Warn("Dropping a subscription failed", ex); }
                _subscriptions.Remove(id);
            }
            foreach (var (id, interval) in wanted)
            {
                if (_subscriptions.ContainsKey(id) || !MetricId.IsValid(id)) continue;
                var metric = new MetricId(id);
                _subscriptions[id] = (interval, host.Metrics.SubscribeBackground(metric, interval, engine.Record));
                if (host.Metrics.Latest(metric) is { } latest) engine.Record(latest);
            }
        }
    }

    private void DropSubscriptions()
    {
        lock (_subscriptions)
        {
            foreach (var (_, sub) in _subscriptions.Values)
            {
                try { sub.Dispose(); } catch (Exception) { /* the host removes what is left when the module stops */ }
            }
            _subscriptions.Clear();
        }
    }

    // ------------------------------------------------------------------ the check

    private void Rearm()
    {
        var engine = _engine;
        if (engine is null || !_running) return;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_deps is not { AutoTick: true } || engine.CheckEvery() is not { } every) return;   // no enabled rule: no timer, nothing wakes
            _timer = new Timer(OnTimer, null, every, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(object? state)
    {
        try
        {
            if (!_running) return;
            Tick();
        }
        catch (Exception ex)
        {
            _host?.Log.Error("Checking the rules failed", ex);
        }
        finally
        {
            // re-armed after the check, so a slow check never queues a second one behind it
            if (_running) Rearm();
        }
    }

    /// <summary>Checks every rule now, sends the alerts that are due and refreshes the texts. The timer calls it; tests call it with a fake clock.</summary>
    internal void Tick()
    {
        var host = _host;
        var engine = _engine;
        if (host is null || engine is null || !_running) return;
        var now = _deps!.Now();
        EndPauseIfOver(now);
        var alerts = engine.Evaluate(IsPaused(now));
        foreach (var alert in alerts) SendAlert(host, alert, now);
        RefreshTexts();
    }

    private void SendAlert(IHostServices host, Alert alert, DateTimeOffset now)
    {
        var loc = host.Localisation;
        var rule = alert.Rule;
        var info = host.Metrics.GetMetricInfo(new MetricId(rule.Metric));
        var label = info?.Label ?? rule.Metric;
        var summary = SummaryOf(host, rule);
        var reading = RuleText.Limit(rule.Kind, alert.Value, alert.Unit, loc);
        try
        {
            host.Toast.Show(new ToastMessage(LocalisedText.Of(Text.Literal(label)), LocalisedText.Of("toast.body", summary, reading), ToastKind.Warning)
            {
                Origin = ToastOrigin.Background,
            });
        }
        catch (Exception ex)
        {
            host.Log.Error("Showing an alert failed", ex);
        }
        host.Log.Info($"alert: {rule.Metric} {summary} (now {reading}); next alert after {rule.QuietMinutes} min");
    }

    // ------------------------------------------------------------------ pause

    internal bool IsPaused(DateTimeOffset now) => _pausedUntil is { } until && now < until;

    /// <summary>When alerts are paused until, or null.</summary>
    internal DateTimeOffset? PausedUntil => _pausedUntil is { } until && _deps!.Now() < until ? until : null;

    /// <summary>Pauses alerts. Rules keep checking and show their state; an alert that is due waits until the pause ends.</summary>
    internal void Pause(TimeSpan length)
    {
        if (_host is null || !_running) return;
        var minutes = Math.Clamp(length.TotalMinutes, 1, MaxPauseMinutes);
        _pausedUntil = _deps!.Now() + TimeSpan.FromMinutes(minutes);
        _host.Log.Info($"alerts paused for {minutes:0} min");
        SyncPauseControls();
        RefreshTexts();
    }

    /// <summary>Ends a pause now; an episode that is still open sends its alert at the next check.</summary>
    internal void Resume()
    {
        if (_host is null || !_running) return;
        if (_pausedUntil is not null) _host.Log.Info("alerts resumed");
        _pausedUntil = null;
        SyncPauseControls();
        Tick();
    }

    private void EndPauseIfOver(DateTimeOffset now)
    {
        if (_pausedUntil is { } until && now >= until)
        {
            _pausedUntil = null;
            _host?.Log.Info("the pause ended");
            SyncPauseControls();
        }
    }

    /// <summary>Brings the toggle and the tray item in line with the pause, without the toggle's handler starting another pause.</summary>
    private void SyncPauseControls()
    {
        bool paused = _pausedUntil is not null;
        _settingToggle = true;
        try { _alertsOn.Value = !paused; }
        finally { _settingToggle = false; }
        if (_trayItem is { } tray) tray.Checked.Value = paused;
    }

    /// <summary>The user flipped the page's toggle: off pauses for an hour, on resumes.</summary>
    private void OnAlertsToggled()
    {
        if (_settingToggle) return;
        if (_alertsOn.Value) Resume();
        else Pause(DefaultPause);
    }

    private void OnTrayPause()
    {
        if (_pausedUntil is not null) Resume();
        else Pause(DefaultPause);
    }

    // ------------------------------------------------------------------ the test alert

    /// <summary>Shows a Warning notification the way a real alert is shown, so the owner can see whether Windows lets it through.</summary>
    internal void SendTestAlert()
    {
        var host = _host;
        if (host is null || !_running) return;
        host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.test.title"), LocalisedText.Of("toast.test.body"), ToastKind.Warning) { Origin = ToastOrigin.Background });
        host.Log.Info("test alert shown");
    }

    // ------------------------------------------------------------------ the page keeps up

    private void OnLanguageChanged() => RefreshTexts();

    /// <summary>The page opened: the list of readings and their values are rebuilt so the pickers are current. Closing does nothing.</summary>
    private void OnPageVisibleChanged()
    {
        if (_host?.PageVisible.Value == true) _host.RequestPageRefresh();
    }

    /// <summary>
    /// A module started, stopped or published a new reading. The page is rebuilt for it only after a quiet moment and only while it is on screen, so a
    /// reading that comes and goes does not rebuild the page again and again.
    /// </summary>
    private void OnCatalogChanged()
    {
        if (!_running) return;
        lock (_gate)
        {
            _catalogDirty = true;
            if (_refreshTimer is not null) return;
            _refreshTimer = new Timer(_ =>
            {
                try
                {
                    lock (_gate) { _refreshTimer?.Dispose(); _refreshTimer = null; }
                    if (_running && _catalogDirty && _host?.PageVisible.Value == true)
                    {
                        _catalogDirty = false;
                        _host.RequestPageRefresh();
                    }
                }
                catch (Exception ex) { _host?.Log.Error("Refreshing the Watch page failed", ex); }
            }, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
    }

    // ------------------------------------------------------------------ tests

    /// <summary>The engine (tests).</summary>
    internal RuleEngine? Engine => _engine;

    /// <summary>The metrics asked for in the background, with their intervals (tests).</summary>
    internal IReadOnlyDictionary<string, TimeSpan> Subscribed
    {
        get { lock (_subscriptions) return _subscriptions.ToDictionary(kv => kv.Key, kv => kv.Value.Interval); }
    }

    /// <summary>True while the module's own check timer is armed (tests).</summary>
    internal bool TimerArmed { get { lock (_gate) return _timer is not null; } }

    /// <summary>The tray item (tests).</summary>
    internal TrayItem? Tray => _trayItem;
}
