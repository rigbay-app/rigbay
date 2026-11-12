using Rigbay.Abstractions;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel;

/// <summary>
/// Turns WireGuard tunnels on and off from Rigbay. It depends on the official WireGuard for Windows (nothing is bundled), lists the tunnels in
/// its store by name only, and does exactly what the WireGuard app does on Activate and Deactivate. Every change asks the user first.
/// Read <c>docs/tunnel/MODULE.md</c> and the skill <c>tunnel-module</c> before changing it.
/// </summary>
public sealed partial class TunnelModule : IModule
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(2), SlowPoll = TimeSpan.FromSeconds(15);

    private Dependencies? _deps;
    private IHostServices? _host;
    private TunnelManager? _manager;
    private Timer? _timer;
    private TunnelSettings _settings = new();
    private readonly object _settingsGate = new();
    private readonly object _uiGate = new();
    private readonly Dictionary<string, TunnelUi> _ui = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly List<Task> _background = new();
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.none"));
    private readonly ObservableValue<bool> _noTunnels = new(false);
    private readonly ObservableValue<bool> _storeUnreadable = new(false);
    private volatile bool _stopping;
    private bool _fast;
    private bool _languageHooked;
    private int _tunnelsKey;   // changes whenever the set of names changes, to ask for a new page only then

    [ThreadStatic] private static bool _ownChange;

    /// <summary>The host's constructor: sets nothing (the real dependencies are made in <see cref="Probe"/>).</summary>
    public TunnelModule() { }

    /// <summary>Tests: the same module with fakes.</summary>
    internal TunnelModule(Dependencies deps) => _deps = deps;

    // ------------------------------------------------------------------ lifecycle

    /// <inheritdoc />
    public ProbeResult Probe()
    {
        _deps ??= Dependencies.Real();
        var probe = _deps.Tools.Probe();
        return probe.Usable ? ProbeResult.Yes() : ProbeResult.No(probe.ReasonKey ?? "probe.no-wireguard");
    }

    /// <inheritdoc />
    public void Initialize(IHostServices host)
    {
        _deps ??= Dependencies.Real();
        _host = host;
        _stopping = false;
        _manager = new TunnelManager(_deps.Tools, _deps.Store, _deps.Services, _deps.Network, _deps.Clock);
        _settings = host.Settings.Load<TunnelSettings>();
        _settings.Tunnels = new Dictionary<string, TunnelOptions>(_settings.Tunnels ?? new(), StringComparer.OrdinalIgnoreCase);
        lock (_uiGate) _ui.Clear();   // the host removed the tray items and hotkeys at Shutdown: a second Initialize registers them again
        _tunnelsKey = 0;

        RegisterCommands(host);
        if (!_languageHooked) { host.Localisation.LanguageChanged += OnLanguageChanged; _languageHooked = true; }
        host.PageVisible.Changed += UpdatePollRate;
        host.Metrics.SubscribersChanged += UpdatePollRate;

        // Read only: what is in the store and what runs. Nothing is turned on or off here.
        var list = SafeList();
        if (list is not null) ApplyList(host, list, firstTime: true);
        RefreshTexts(list);

        bool wanted = Wanted(host);
        _fast = wanted;
        if (_deps.AutoPoll) _timer = new Timer(_ => TimerTick(), null, wanted ? FastPoll : SlowPoll, wanted ? FastPoll : SlowPoll);
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        _stopping = true;
        var host = _host;
        _timer?.Dispose();
        _timer = null;
        if (host is not null)
        {
            host.PageVisible.Changed -= UpdatePollRate;
            host.Metrics.SubscribersChanged -= UpdatePollRate;
            if (_languageHooked) { host.Localisation.LanguageChanged -= OnLanguageChanged; _languageHooked = false; }
        }
        // A tunnel is never turned on or off by Shutdown: it keeps running as the user left it (the service lives on its own).
        lock (_uiGate)
        {
            if (host is not null) foreach (var ui in _ui.Values) WithdrawMetrics(host, ui);
        }
        Task[] pending;
        lock (_background) pending = _background.ToArray();
        try { Task.WaitAll(pending, TimeSpan.FromSeconds(3)); } catch (Exception) { }
    }

    // ------------------------------------------------------------------ polling

    private bool Wanted(IHostServices host) => host.PageVisible.Value || AnySubscriber(host);

    /// <summary>Somebody (the dashboard, the recorder, another module) reads one of this module's metrics: the ids are known from the tunnel names.</summary>
    private bool AnySubscriber(IHostServices host)
    {
        lock (_uiGate)
            foreach (var ui in _ui.Values)
                if (host.Metrics.SubscriberCount(new MetricId(ui.RxMetric)) > 0 || host.Metrics.SubscriberCount(new MetricId(ui.TxMetric)) > 0
                    || host.Metrics.SubscriberCount(new MetricId(ui.HandshakeMetric)) > 0) return true;
        return false;
    }

    private void UpdatePollRate()
    {
        var host = _host;
        var timer = _timer;
        if (host is null || timer is null || _stopping) return;
        bool wanted = Wanted(host);
        if (wanted == _fast) return;
        _fast = wanted;
        timer.Change(wanted ? TimeSpan.Zero : SlowPoll, wanted ? FastPoll : SlowPoll);   // zero: look at once when somebody starts looking
    }

    private void TimerTick()
    {
        if (_stopping) return;
        try { PollOnceAsync().GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            // A timer thread is outside the host's boundary: nothing may escape from here.
            try { _host?.Log.Warn("tunnel poll failed: " + e.GetType().Name, e); } catch (Exception) { }
        }
    }

    /// <summary>One look at the machine: tunnels, services, then (only when somebody looks) the counters. Called by the timer; tests call it directly.</summary>
    internal async Task PollOnceAsync()
    {
        var host = _host;
        var manager = _manager;
        if (host is null || manager is null || _stopping) return;
        if (!await _pollGate.WaitAsync(0).ConfigureAwait(false)) return;   // a poll is still running: skip this one
        try
        {
            var list = SafeList();
            if (list is null) return;
            ApplyList(host, list, firstTime: false);

            bool wanted = Wanted(host);
            foreach (var info in list.Tunnels)
            {
                if (_stopping) return;
                TunnelUi ui;
                lock (_uiGate) { if (!_ui.TryGetValue(info.Name, out ui!)) continue; }
                if (!info.IsOn) { ui.Last = null; ui.LastTransfer = null; WithdrawMetrics(host, ui); continue; }
                if (wanted) await ReadAndPublishAsync(host, manager, ui, info).ConfigureAwait(false);
            }
            await AutoOffAsync(host, list).ConfigureAwait(false);
            RefreshTexts(list);
        }
        finally { _pollGate.Release(); }
    }

    private TunnelList? SafeList()
    {
        try { return _manager?.List(); }
        catch (Exception e)
        {
            _host?.Log.Warn("tunnel list failed: " + e.GetType().Name, e);
            return null;
        }
    }

    /// <summary>Brings the per-tunnel objects, the tray and the hotkeys in line with the store, and the switches with the services.</summary>
    private void ApplyList(IHostServices host, TunnelList list, bool firstTime)
    {
        bool namesChanged = false;
        lock (_uiGate)
        {
            foreach (var ui in _ui.Values) { ui.WasPresent = ui.Present; ui.Present = false; }
            foreach (var info in list.Tunnels)
            {
                if (!_ui.TryGetValue(info.Name, out var ui))
                {
                    ui = new TunnelUi(info.Name);
                    _ui[info.Name] = ui;
                    AttachUi(host, ui);
                    namesChanged = true;
                }
                else if (!ui.WasPresent && ui.Tray is not null) { ui.Tray.Visible.Value = true; namesChanged = true; }
                ui.Present = true;
                if (!(_manager?.Busy ?? false)) SetOwn(ui.On, info.IsOn);   // while a change runs the switch shows what was asked; the end of the change puts it right
                SetOwn(ui.StartWithWindows, info.Service.Start == StartKind.Automatic);
                ui.StartEnabled.Value = info.IsOn;
                if (ui.Tray is not null) ui.Tray.Checked.Value = info.IsOn;
            }
            foreach (var ui in _ui.Values)
                if (!ui.Present && ui.Tray is { } tray && tray.Visible.Value) { tray.Visible.Value = false; namesChanged = true; }
        }

        // A tunnel found running that Rigbay did not turn on: count its automatic turn-off from now (the option is off unless the user set it).
        foreach (var info in list.Tunnels)
        {
            var options = OptionsFor(info.Name, create: false);
            if (info.IsOn && options is { AutoOffHours: > 0, OnSinceUtc.Length: 0 }) { options.OnSinceUtc = _deps!.Clock.Now.ToString("O"); SaveSettings(); }
            if (!info.IsOn && options is { OnSinceUtc.Length: > 0 }) { options.OnSinceUtc = ""; SaveSettings(); }
        }

        _noTunnels.Value = list.StoreReadable && list.Tunnels.Count == 0;
        _storeUnreadable.Value = !list.StoreReadable;
        int key = string.Join('|', list.Tunnels.Select(t => t.Name)).GetHashCode();
        if (key != _tunnelsKey || namesChanged)
        {
            _tunnelsKey = key;
            if (!firstTime) host.RequestPageRefresh();
        }
    }

    /// <summary>Tray item and hotkey of one tunnel. Registered once; the host removes them when the module stops.</summary>
    private void AttachUi(IHostServices host, TunnelUi ui)
    {
        var options = OptionsFor(ui.Name, create: false);
        ui.AutoOffHours.Value = options?.AutoOffHours ?? 0;
        ui.CheckPort.Value = options?.CheckPort ?? 0;
        ui.PublicAddress.Value = options?.PublicAddress ?? "";

        ui.On.Changed += () => OnToggleChanged(ui);
        ui.StartWithWindows.Changed += () => OnStartChanged(ui);
        ui.AutoOffHours.Changed += () => OnAutoOffChanged(ui);
        ui.CheckPort.Changed += () => OnCheckPortChanged(ui);
        ui.PublicAddress.Changed += () => OnPublicAddressChanged(ui);

        var tray = new TrayItem("toggle-" + ui.Slug, Text.Literal(ui.Name), () => Track(Task.Run(() => ToggleFrom(ui.Name, Origin.Tray)))) { Checkable = true };
        host.Tray.Add(tray);
        ui.Tray = tray;

        try
        {
            ui.Hotkey = host.Hotkeys.Register(new HotkeyRegistration("toggle-" + ui.Slug, Text.Literal(host.Localisation.F("hotkey.toggle", ui.Name)), null,
                () => Track(Task.Run(() => ToggleFrom(ui.Name, Origin.Hotkey)))));
        }
        catch (ArgumentException e) { host.Log.Warn("hotkey of a tunnel could not be registered: " + e.Message); }
    }

    private async Task ToggleFrom(string name, Origin origin)
    {
        try
        {
            var info = _manager?.Find(name);
            if (info is null) return;
            await RequestChangeAsync(name, !info.IsOn, origin, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _host?.Log.Warn("toggle failed: " + e.GetType().Name, e); }
    }

    private void OnLanguageChanged()
    {
        var host = _host;
        if (host is null || _stopping) return;
        lock (_uiGate)
            foreach (var ui in _ui.Values) { ui.MetricsDescribed = false; }
        host.RequestPageRefresh();
        Track(Task.Run(async () => { try { await PollOnceAsync().ConfigureAwait(false); } catch (Exception) { } }));
    }

    // ------------------------------------------------------------------ status and metrics

    private async Task ReadAndPublishAsync(IHostServices host, TunnelManager manager, TunnelUi ui, TunnelInfo info)
    {
        DescribeMetrics(host, ui);
        WgReading? reading;
        try { reading = await manager.ReadAsync(info.Name, host.StopToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (reading is null) { WithdrawMetrics(host, ui); ui.Last = null; return; }

        var now = _deps!.Clock.Now;
        ui.Last = new WgSummary(reading.LatestHandshake, reading.RxBytes, reading.TxBytes, reading.Endpoint);

        if (reading.LatestHandshake is { } at) host.Metrics.Publish(new MetricId(ui.HandshakeMetric), Math.Max(0, (now - at).TotalSeconds), MetricUnit.Seconds);
        else host.Metrics.Withdraw(new MetricId(ui.HandshakeMetric));

        if (reading.RxBytes is { } rx && reading.TxBytes is { } tx)
        {
            if (ui.LastTransfer is { } last && now > last.At && rx >= last.Rx && tx >= last.Tx)
            {
                double seconds = (now - last.At).TotalSeconds;
                host.Metrics.Publish(new MetricId(ui.RxMetric), (rx - last.Rx) / seconds, MetricUnit.BytesPerSecond);
                host.Metrics.Publish(new MetricId(ui.TxMetric), (tx - last.Tx) / seconds, MetricUnit.BytesPerSecond);
                ui.MetricsPublished = true;
            }
            ui.LastTransfer = (now, rx, tx);   // after a counter reset (the service restarted) this is simply the new baseline
        }
        else { host.Metrics.Withdraw(new MetricId(ui.RxMetric)); host.Metrics.Withdraw(new MetricId(ui.TxMetric)); ui.LastTransfer = null; }

        var options = OptionsFor(info.Name, create: false);
        int port = options?.CheckPort ?? 0;
        ui.Local.Value = LocalText(host.Localisation, port > 0 ? manager.CheckPort(info.Name, port, tunnelOn: true) : null, port);
    }

    private void DescribeMetrics(IHostServices host, TunnelUi ui)
    {
        if (ui.MetricsDescribed) return;
        var loc = host.Localisation;
        host.Metrics.Describe(new MetricDescriptor(new MetricId(ui.RxMetric), Text.Literal(loc.F("metric.rx", ui.Name)), MetricUnit.BytesPerSecond, "0") { DescriptionKey = "metric.rx.description" });
        host.Metrics.Describe(new MetricDescriptor(new MetricId(ui.TxMetric), Text.Literal(loc.F("metric.tx", ui.Name)), MetricUnit.BytesPerSecond, "0") { DescriptionKey = "metric.tx.description" });
        host.Metrics.Describe(new MetricDescriptor(new MetricId(ui.HandshakeMetric), Text.Literal(loc.F("metric.handshake", ui.Name)), MetricUnit.Seconds, "0") { DescriptionKey = "metric.handshake.description" });
        ui.MetricsDescribed = true;
    }

    private static void WithdrawMetrics(IHostServices host, TunnelUi ui)
    {
        host.Metrics.Withdraw(new MetricId(ui.RxMetric));
        host.Metrics.Withdraw(new MetricId(ui.TxMetric));
        host.Metrics.Withdraw(new MetricId(ui.HandshakeMetric));
        ui.LastTransfer = null;
    }

    /// <summary>Recomputes every text from the last list and the last reading. Cheap; no I/O.</summary>
    private void RefreshTexts(TunnelList? list)
    {
        var host = _host;
        if (host is null) return;
        var loc = host.Localisation;
        var now = _deps!.Clock.Now;
        if (list is not null)
        {
            int on = list.Tunnels.Count(t => t.IsOn);
            _stateLine.Value = list.Tunnels.Count == 0 ? LocalisedText.Of("state.none") : LocalisedText.Of("state.line", on, list.Tunnels.Count);
        }
        lock (_uiGate)
        {
            foreach (var ui in _ui.Values)
            {
                var info = list?.Tunnels.FirstOrDefault(t => string.Equals(t.Name, ui.Name, StringComparison.OrdinalIgnoreCase));
                bool on = info?.IsOn ?? ui.On.Value;
                ui.Status.Value = StatusText(loc, on, ui.Last, now);
                ui.Endpoint.Value = on && ui.Last?.Endpoint is { } e ? e : loc.T("value.none");
                ui.Transfer.Value = on && ui.Last is { Rx: { } rx, Tx: { } tx } ? loc.F("transfer.value", Size(rx), Size(tx)) : loc.T("value.none");
                if (!on) ui.Local.Value = LocalText(loc, ui.CheckPort.Value > 0 ? LocalCheck.TunnelOff : null, (int)ui.CheckPort.Value);
                else if (ui.CheckPort.Value <= 0) ui.Local.Value = loc.T("local.none");
            }
        }
    }

    internal static string StatusText(ILocalisation loc, bool on, WgSummary? last, DateTimeOffset now)
    {
        if (!on) return loc.T("status.off");
        if (last is null) return loc.T("status.on");
        if (last.Handshake is not { } at) return loc.T("status.no-handshake");
        var age = now - at;
        return age <= TunnelManager.ConnectedLimit ? loc.F("status.connected", Age(loc, age)) : loc.F("status.stale", Age(loc, age));
    }

    internal static string LocalText(ILocalisation loc, LocalCheck? check, int port) => check switch
    {
        null => loc.T("local.none"),
        LocalCheck.TunnelOff => loc.T("local.tunnel-off"),
        LocalCheck.NoAddress => loc.T("local.no-address"),
        LocalCheck.Listening => loc.F("local.listening", port),
        _ => loc.F("local.not-listening", port),
    };

    internal static string Age(ILocalisation loc, TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 120) return loc.F("age.seconds", (int)age.TotalSeconds);
        if (age.TotalMinutes < 120) return loc.F("age.minutes", (int)age.TotalMinutes);
        return loc.F("age.hours", (int)age.TotalHours);
    }

    internal static string Size(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0") : v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + " " + units[i];
    }

    // ------------------------------------------------------------------ settings

    private TunnelOptions? OptionsFor(string name, bool create)
    {
        lock (_settingsGate)
        {
            if (_settings.Tunnels.TryGetValue(name, out var o)) return o;
            if (!create) return null;
            o = new TunnelOptions();
            _settings.Tunnels[name] = o;
            return o;
        }
    }

    private void SaveSettings()
    {
        var host = _host;
        if (host is null) return;
        try { lock (_settingsGate) host.Settings.Save(_settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { host.Log.Warn("settings could not be saved: " + e.Message); }
    }

    private void Track(Task task)
    {
        lock (_background)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }

    /// <summary>Tests: waits for the work the module started in the background (a turn-on waiting for its handshake, a tray click).</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_background) pending = _background.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    /// <summary>Sets a value the module owns without the module taking it for the user's edit (<c>Changed</c> runs synchronously on this thread).</summary>
    private static void SetOwn<T>(ObservableValue<T> value, T next)
    {
        bool before = _ownChange;
        _ownChange = true;
        try { value.Value = next; }
        finally { _ownChange = before; }
    }
}

/// <summary>Where a request came from; it decides whether a notification is shown.</summary>
internal enum Origin
{
    Page,
    Tray,
    Hotkey,
    Verb,
    Auto,
}
