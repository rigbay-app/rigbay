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
        scheduler.Watched = IsWatched(host);
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
    /// True when somebody wants the readings: the module's page is on screen, or one of its metrics has a reader (the Dashboard, a recorder, the
    /// tray tooltip, another module). Otherwise NVML is not called at all.
    /// </summary>
    internal static bool IsWatched(IHostServices host)
    {
        if (host.PageVisible.Value) return true;
        foreach (var metric in GpuMetrics.All)
            if (host.Metrics.SubscriberCount(metric.Id) > 0) return true;
        return false;
    }

    /// <summary>The page was shown or left, or somebody (un)subscribed. Runs on the thread that changed it: it flips the scheduler and asks for a poll when somebody started to look.</summary>
    private void OnWatchSignal()
    {
        var host = _host;
        var scheduler = _scheduler;
        if (host is null || scheduler is null) return;
        bool watched = IsWatched(host);
        if (watched == scheduler.Watched) return;
        scheduler.Watched = watched;
        if (watched) scheduler.PollSoon();
    }

    private void OnPollSecondsChanged()
    {
        int seconds = Math.Clamp((int)Math.Round(_pollSeconds.Value), GpuSettings.MinPollSeconds, GpuSettings.MaxPollSeconds);
        _settings.PollSeconds = seconds;
        if (_scheduler is { } scheduler) scheduler.Interval = TimeSpan.FromSeconds(seconds);
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
