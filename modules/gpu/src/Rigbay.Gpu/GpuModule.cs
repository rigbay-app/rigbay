using Rigbay.Abstractions;
using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu;

/// <summary>
/// Reads the NVIDIA GPU through NVML and publishes load, power, clocks, memory, temperature, performance state and the reasons the clocks are
/// held down on the metrics bus. READ ONLY: the only NVML functions it can reach are on <see cref="NvmlAllowList"/>. It reads only while somebody
/// wants the readings (its page is on screen, or a reader such as the Dashboard or the recorder subscribes), so a laptop's GPU can sleep.
/// Read the skill <c>gpu-module</c> before changing it.
/// </summary>
public sealed partial class GpuModule : IModule
{
    private Dependencies? _deps;
    private INvml? _nvml;
    private IHostServices? _host;
    private GpuPoller? _poller;
    private PollScheduler? _scheduler;
    private GpuSettings _settings = new();
    private bool _languageHooked;
    private bool _opened;
    private readonly object _demandLock = new();
    private readonly ObservableValue<double> _pollSeconds = new(GpuSettings.DefaultPollSeconds);
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.starting"));
    private readonly ObservableValue<string> _deviceText = new("");
    private readonly ObservableValue<string> _limitText = new("");

    /// <summary>The host's constructor: sets nothing (the library is opened in <see cref="Probe"/>).</summary>
    public GpuModule() { }

    /// <summary>Tests: the same module with a fake NVML.</summary>
    internal GpuModule(Dependencies deps) => _deps = deps;

    // ------------------------------------------------------------------ lifecycle

    /// <inheritdoc />
    public ProbeResult Probe()
    {
        try
        {
            _deps ??= Dependencies.Real();
            var open = _deps.OpenLibrary();
            if (open.Nvml is null) return ProbeResult.No(open.ReasonKey ?? "probe.no-dll");
            _nvml = new SerialNvml(open.Nvml);
            return GpuProbe.Run(_nvml);
        }
        catch (Exception ex)
        {
            return ProbeResult.No("probe.check-failed", ex.Message);
        }
    }

    /// <inheritdoc />
    public void Initialize(IHostServices host)
    {
        _deps ??= Dependencies.Real();
        _host = host;
        if (_nvml is null)
        {
            var open = _deps.OpenLibrary();
            if (open.Nvml is not null) _nvml = new SerialNvml(open.Nvml);
        }
        _settings = host.Settings.Load<GpuSettings>().Clamped();
        _pollSeconds.Value = _settings.PollSeconds;
        _opened = false;

        foreach (var metric in GpuMetrics.All)
            host.Metrics.Describe(new MetricDescriptor(metric.Id, metric.LabelKey, metric.Unit, metric.Format) { DescriptionKey = metric.DescriptionKey });

        if (!_languageHooked) { host.Localisation.LanguageChanged += OnLanguageChanged; _languageHooked = true; }
        RegisterCommands(host);

        if (_nvml is null)
        {
            _stateLine.Value = LocalisedText.Of("state.unavailable");
            return;
        }

        // NVML is opened once here and shut down in Shutdown. Opening it reads the name of the GPU; no reading is taken until somebody looks.
        var poller = new GpuPoller(_nvml, host.Metrics, host.Log, _deps.Now);
        var code = poller.Open();
        if (code != NvmlReturn.Success)
        {
            host.Log.Warn($"NVML did not open: {_nvml.ErrorString(code)} ({(int)code}).");
            _stateLine.Value = LocalisedText.Of("state.unavailable");
            return;
        }
        _poller = poller;
        _opened = true;

        poller.StateChanged += OnReadingsState;
        poller.Polled += OnPolled;
        _pollSeconds.Changed += OnPollSecondsChanged;
        host.PageVisible.Changed += OnWatchSignal;
        host.Metrics.SubscribersChanged += OnWatchSignal;

        var scheduler = new PollScheduler(() => poller.PollOnce(), TimeSpan.FromSeconds(_settings.PollSeconds), ex => host.Log.Error("Polling the GPU failed", ex));
        var demand = Demand(host, _settings.PollSeconds);
        scheduler.Watched = demand.Kind != DemandKind.Idle;
        scheduler.Interval = IntervalOf(demand, _settings.PollSeconds);
        _scheduler = scheduler;
        RefreshTexts();
        if (_deps.AutoPoll) scheduler.Start(TimeSpan.Zero);
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        var host = _host;
        var scheduler = _scheduler;
        var poller = _poller;
        scheduler?.Stop();
        if (poller is not null)
        {
            poller.StateChanged -= OnReadingsState;
            poller.Polled -= OnPolled;
        }
        _pollSeconds.Changed -= OnPollSecondsChanged;
        if (host is not null)
        {
            host.Localisation.LanguageChanged -= OnLanguageChanged;
            _languageHooked = false;
            host.PageVisible.Changed -= OnWatchSignal;
            host.Metrics.SubscribersChanged -= OnWatchSignal;
        }

        // Waits for a poll that is running, then shuts NVML down; the readings are no longer true, so they leave the bus.
        poller?.Close();
        if (host is not null)
        {
            foreach (var metric in GpuMetrics.All)
            {
                try { host.Metrics.Withdraw(metric.Id); }
                catch (Exception ex) { host.Log.Warn("Withdrawing " + metric.Id + " failed", ex); }
            }
        }
        _scheduler = null;
        _poller = null;
        _opened = false;
    }

    // ------------------------------------------------------------------ who is looking

    /// <summary>
    /// How the module should read right now (host API 0.1.4, <see cref="MetricDemand"/>): <see cref="DemandKind.Fast"/> when its page is on screen or a
    /// reader of one of its metrics exists (the Dashboard, a recorder, the tray tooltip, another module), at the user's poll rate;
    /// <see cref="DemandKind.Background"/> when only a background reader asks (Watch), at max(poll rate, the asked interval); otherwise
    /// <see cref="DemandKind.Idle"/>: NVML is not called at all (idle is "do not poll"). A background demand keeps the GPU awake, which on a
    /// laptop with Optimus means the NVIDIA chip stays powered; see the README.
    /// </summary>
    internal static DemandState Demand(IHostServices host, int pollSeconds) =>
        host.Evaluate(GpuMetrics.All.Select(m => m.Id), TimeSpan.FromSeconds(pollSeconds), Timeout.InfiniteTimeSpan);

    /// <summary>The scheduler's interval for a decision: the asked one, or the user's poll rate while idle (it is stored for when somebody looks).</summary>
    private static TimeSpan IntervalOf(DemandState demand, int pollSeconds) =>
        demand.Kind == DemandKind.Idle ? TimeSpan.FromSeconds(pollSeconds) : demand.Interval;

    /// <summary>The page was shown or left, somebody (un)subscribed, a background demand came or went, or the poll rate changed. Runs on the thread that changed it: it sets the scheduler and asks for a poll when somebody started to look or wants it faster.</summary>
    private void OnWatchSignal()
    {
        var host = _host;
        var scheduler = _scheduler;
        if (host is null || scheduler is null) return;
        lock (_demandLock)
        {
            var demand = Demand(host, _settings.PollSeconds);
            bool watched = demand.Kind != DemandKind.Idle;
            var interval = IntervalOf(demand, _settings.PollSeconds);
            bool wasWatched = scheduler.Watched;
            bool faster = interval < scheduler.Interval;
            scheduler.Interval = interval;
            scheduler.Watched = watched;
            if (watched && (!wasWatched || faster)) scheduler.PollSoon();
        }
    }

    private void OnPollSecondsChanged()
    {
        int seconds = Math.Clamp((int)Math.Round(_pollSeconds.Value), GpuSettings.MinPollSeconds, GpuSettings.MaxPollSeconds);
        _settings.PollSeconds = seconds;
        OnWatchSignal();
        try { _host?.Settings.Save(_settings); }
        catch (Exception ex) { _host?.Log.Warn("Saving the GPU settings failed", ex); }
    }

    /// <summary>The state changed. The first reading of a run (and the first one after the GPU came back) goes to the log in one line, so a bug report shows what the GPU said.</summary>
    private void OnReadingsState(GpuState state)
    {
        if (state == GpuState.Available && _host is { } host && _poller is { } poller)
            host.Log.Info("GPU readings available: " + StatusText(host.Localisation, poller.DeviceName, poller.Latest).ReplaceLineEndings(" | "));
        RefreshTexts();
    }

    /// <summary>A poll ended. With the log level set to debug every poll is written in one line (to compare with another tool); at the default level it costs nothing but the text.</summary>
    private void OnPolled(GpuSnapshot snapshot)
    {
        if (_host is { } host)
            host.Log.Debug("GPU read: " + StatusText(host.Localisation, "", snapshot).ReplaceLineEndings(" | "));
        RefreshTexts();
    }

    private void OnLanguageChanged() => RefreshTexts();

    // ------------------------------------------------------------------ tests

    /// <summary>True while NVML is open (tests).</summary>
    internal bool IsOpen => _opened && _poller is { IsOpen: true };

    /// <summary>True while the module reads on its own (tests).</summary>
    internal bool? Polling => _scheduler?.Watched;

    /// <summary>The interval the module reads at (tests).</summary>
    internal TimeSpan? PollInterval => _scheduler?.Interval;

    /// <summary>Reads once now, as the timer would (tests).</summary>
    internal GpuSnapshot? PollNow() => _poller?.PollOnce();

    /// <summary>The poller (tests).</summary>
    internal GpuPoller? Poller => _poller;
}
