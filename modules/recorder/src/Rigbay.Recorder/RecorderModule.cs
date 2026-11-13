using System.Globalization;
using Rigbay.Abstractions;
using Rigbay.Recorder.Recording;

namespace Rigbay.Recorder;

/// <summary>
/// The recorder module: writes every metric on the bus to a CSV file. It knows no other module; whatever a module publishes
/// is recorded. Lifecycle and wiring are here, the page is in <c>RecorderModule.Page.cs</c>, verbs, tray and hotkey in
/// <c>RecorderModule.Commands.cs</c>, the recording itself in <see cref="RecorderEngine"/>. See docs/recorder/README.md.
/// </summary>
public sealed partial class RecorderModule : IModule
{
    /// <summary>The module's own metric: rows written by the running recording. Withdrawn when idle.</summary>
    internal static readonly MetricId RowsMetric = new("recorder.rows");

    private readonly Func<IHostServices, RecorderServices>? _servicesFactory;
    private readonly IShell _shell;
    private readonly Func<string> _defaultFolder;

    // Values the page shows. They live as long as the instance, so enabling again after disabling works.
    private readonly ObservableValue<string> _period = new("1");
    private readonly ObservableValue<string> _duration = new("0");
    private readonly ObservableValue<string> _folder = new("");
    private readonly ObservableValue<bool> _idle = new(true);
    private readonly ObservableValue<bool> _recording = new(false);
    private readonly object _textGate = new();
    private readonly ObservableValue<LocalisedText> _primaryLine = new(default);   // under Start or Stop: the running file, or how the last one ended
    private readonly ObservableValue<LocalisedText> _folderLine = new(default);    // where the files go
    private readonly ObservableValue<LocalisedText> _pickedLine = new(default);    // "12 of 40"
    private readonly ObservableValue<LocalisedText> _stateLine = new(LocalisedText.Of("state.idle"));
    private readonly Dictionary<string, ObservableValue<bool>> _picks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObservableValue<LocalisedText>> _sourceLines = new(StringComparer.Ordinal);
    private readonly object _uiGate = new();

    private IHostServices? _host;
    private RecorderServices _services = new();
    private RecorderEngine? _engine;
    private RecorderSettings _settings = new();
    private Timer? _clock;
    private IHotkeyBinding? _hotkey;
    private TrayItem? _trayStart;
    private TrayItem? _trayStop;
    private LocalisedText? _lastNotice;
    private HashSet<string> _pageMetrics = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _pageSources = new(StringComparer.Ordinal);

    /// <summary>Creates the module for the real machine. Does nothing but set fields.</summary>
    public RecorderModule() : this(null, new SystemShell(), DefaultLogsFolder) { }

    /// <summary>Creates the module with its outside world replaced (tests).</summary>
    internal RecorderModule(Func<IHostServices, RecorderServices>? servicesFactory, IShell shell, Func<string> defaultFolder)
    {
        _servicesFactory = servicesFactory;
        _shell = shell;
        _defaultFolder = defaultFolder;
    }

    /// <summary><c>Documents\Rigbay\logs</c> of the current user.</summary>
    internal static string DefaultLogsFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rigbay", "logs");

    /// <inheritdoc />
    public ProbeResult Probe() => ProbeResult.Yes();   // a file and the bus: every machine can do that

    /// <inheritdoc />
    public void Initialize(IHostServices host)
    {
        _host = host;
        _settings = host.Settings.Load<RecorderSettings>().Clamped();
        _services = _servicesFactory?.Invoke(host) ?? new RecorderServices
        {
            OwnModuleId = host.ModuleId,
            ToolVersion = typeof(RecorderModule).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            HostVersion = host.HostVersion.ToString(),
            Versions = new SiblingModuleVersions(host.ModuleDirectory),
            Catalog = () => new BusCatalog(host.Metrics),
        };

        _period.Value = _settings.PeriodSeconds.ToString(CultureInfo.InvariantCulture);
        _duration.Value = _settings.DurationMinutes.ToString(CultureInfo.InvariantCulture);
        _folder.Value = _settings.Folder;
        _lastNotice = null;

        host.Metrics.Describe(new MetricDescriptor(RowsMetric, "metric.rows", MetricUnit.Count, "0") { DescriptionKey = "metric.rows.description" });

        _engine = new RecorderEngine(host.Metrics, host.Log, _services);
        _engine.Changed += OnEngineChanged;
        _engine.Ended += OnEngineEnded;

        _period.Changed += OnPeriodChanged;
        _duration.Changed += OnDurationChanged;
        _folder.Changed += OnFolderChanged;
        host.Localisation.LanguageChanged += OnLanguageChanged;
        host.PageVisible.Changed += RefreshPageIfStale;
        host.Metrics.CatalogChanged += RefreshPageIfStale;   // a metric appeared (or the language changed, see OnLanguageChanged)

        RegisterCommands(host);
        RegisterShortcuts(host);

        ShowEnded(_engine.LastSummary);
        RefreshTexts();
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        var host = _host;
        var engine = _engine;
        StopClock();
        if (engine is not null)
        {
            engine.Changed -= OnEngineChanged;
            engine.Ended -= OnEngineEnded;
            // Turned off or the host is closing: close the file properly (the engine waits a few seconds at most, the host allows five).
            engine.Stop(StopReason.ModuleStopped);
        }
        _period.Changed -= OnPeriodChanged;
        _duration.Changed -= OnDurationChanged;
        _folder.Changed -= OnFolderChanged;
        if (host is not null)
        {
            host.Localisation.LanguageChanged -= OnLanguageChanged;
            host.PageVisible.Changed -= RefreshPageIfStale;
            host.Metrics.CatalogChanged -= RefreshPageIfStale;
            try { host.Metrics.Withdraw(RowsMetric); } catch (Exception ex) { host.Log.Warn("Withdrawing recorder.rows failed", ex); }
        }
        _engine = null;
        _hotkey = null;
        _trayStart = null;
        _trayStop = null;
    }

    // ------------------------------------------------------------------ reactions

    private void OnEngineChanged()
    {
        var engine = _engine;
        var host = _host;
        if (engine is null || host is null) return;
        var st = engine.Status();
        try
        {
            if (st.Recording) host.Metrics.Publish(RowsMetric, st.Rows, MetricUnit.Count);
        }
        catch (Exception ex)
        {
            host.Log.Warn("Publishing recorder.rows failed", ex);
        }
        ApplyRecordingState(st.Recording);
        RefreshTexts();
    }

    private void OnEngineEnded(RecordingSummary summary)
    {
        var host = _host;
        if (host is null) return;
        try { host.Metrics.Withdraw(RowsMetric); }
        catch (Exception ex) { host.Log.Warn("Withdrawing recorder.rows failed", ex); }

        ShowEnded(summary);
        var name = Path.GetFileName(summary.Path);
        // A stop the user asked for needs no toast; the three automatic ones do, because nobody is looking.
        switch (summary.Reason)
        {
            case StopReason.Duration:
                Toast(ToastKind.Success, LocalisedText.Of("toast.ended", name, summary.Rows));
                break;
            case StopReason.DiskLow:
                Toast(ToastKind.Warning, LocalisedText.Of("toast.disk-low", name, summary.Detail ?? "?"));
                break;
            case StopReason.WriteError:
                Toast(ToastKind.Error, LocalisedText.Of("toast.write-error", name, summary.Detail ?? "?"));
                break;
        }
        ApplyRecordingState(false);
        RefreshTexts();
        host.RequestPageRefresh();   // the History group has a new entry
    }

    private void Toast(ToastKind kind, LocalisedText body)
    {
        try { _host?.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), body, kind)); }
        catch (Exception ex) { _host?.Log.Warn("Showing a notification failed", ex); }
    }

    /// <summary>The state changed between recording and idle: switch what depends on it.</summary>
    private void ApplyRecordingState(bool recording)
    {
        _recording.Value = recording;
        _idle.Value = !recording;
        RefreshPageIfRecordingChanged(recording);   // the main button is Start or Stop
        if (_trayStart is { } start) start.Visible.Value = !recording;
        if (_trayStop is { } stop) stop.Visible.Value = recording;
        if (recording) StartClock(); else StopClock();
    }

    private void StartClock()
    {
        lock (_uiGate)
        {
            // The engine reports a row at a time, which with a 10 s period would freeze the clock on the page: tick once a second.
            _clock ??= new Timer(_ => ClockTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    private void StopClock()
    {
        Timer? clock;
        lock (_uiGate) { clock = _clock; _clock = null; }
        clock?.Dispose();
    }

    private void ClockTick()
    {
        try { RefreshTexts(); }
        catch (Exception ex) { _host?.Log.Warn("Refreshing the recorder page failed", ex); }
    }

    private void ShowEnded(RecordingSummary? summary)
    {
        _lastNotice = summary is null ? null : EndedNotice(summary);
    }

    private static LocalisedText EndedNotice(RecordingSummary s)
    {
        string name = Path.GetFileName(s.Path);
        string detail = s.Detail ?? "?";
        return s.Reason switch
        {
            StopReason.Duration => LocalisedText.Of("ended.duration", name, s.Rows),
            StopReason.DiskLow => LocalisedText.Of("ended.disk-low", name, s.Rows, detail),
            StopReason.WriteError => LocalisedText.Of("ended.write-error", name, s.Rows, detail),
            StopReason.ModuleStopped => LocalisedText.Of("ended.module-stopped", name, s.Rows),
            _ => LocalisedText.Of("ended.user", name, s.Rows),
        };
    }

    /// <summary>
    /// The user switched language. The texts of the status rows follow at once; the metric picker shows the labels the owners gave,
    /// which the page resolved when it was built, so it is rebuilt.
    /// </summary>
    private void OnLanguageChanged()
    {
        RefreshTexts();
        _host?.RequestPageRefresh();
    }

    // ------------------------------------------------------------------ status texts

    /// <summary>Puts the engine's status into the observable values the page and the dashboard show. Safe to call from any thread.</summary>
    internal void RefreshTexts()
    {
        // One refresh at a time, from reading the state to writing the lines: the clock timer, the engine's Changed event and the Ended handler all call
        // this, and a call that read the state before the end could otherwise write its old result after the call that read the state after it
        // (the line under the Stop button stayed empty, not "Stopped because ...").
        lock (_textGate) RefreshTextsLocked();
    }

    private void RefreshTextsLocked()
    {
        var host = _host;
        var engine = _engine;
        if (host is null || engine is null) return;
        var loc = host.Localisation;
        var st = engine.Status();

        if (st.Recording)
        {
            string elapsed = st.Duration is { } limit
                ? loc.F("status.elapsed-of", CsvFormat.Clock(st.Elapsed), CsvFormat.Clock(limit))
                : CsvFormat.Clock(st.Elapsed);
            _stateLine.Value = LocalisedText.Of("state.recording-line", CsvFormat.Clock(st.Elapsed));
            _primaryLine.Value = LocalisedText.Of("line.recording", Path.GetFileName(st.Path ?? ""), elapsed,
                st.Rows.ToString("N0", CultureInfo.InvariantCulture), CsvFormat.Size(st.Bytes));
        }
        else
        {
            _stateLine.Value = LocalisedText.Of("state.idle");
            // How the last recording ended, or why one could not start; nothing before the first one (no row of dashes).
            _primaryLine.Value = _lastNotice ?? default;
        }
        _folderLine.Value = string.IsNullOrWhiteSpace(_settings.Folder) ? LocalisedText.Of("row.folder.placeholder") : new LocalisedText(Text.Literal(_settings.Folder));
        UpdatePickedText();
    }

    // ------------------------------------------------------------------ settings

    private void OnPeriodChanged()
    {
        if (!int.TryParse(_period.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)) return;
        _settings.PeriodSeconds = Math.Clamp(seconds, RecorderSettings.MinPeriodSeconds, RecorderSettings.MaxPeriodSeconds);
        SaveSettings();
    }

    private void OnDurationChanged()
    {
        if (!int.TryParse(_duration.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)) return;
        _settings.DurationMinutes = Math.Clamp(minutes, 0, RecorderSettings.MaxDurationMinutes);
        SaveSettings();
    }

    private void OnFolderChanged()
    {
        _settings.Folder = (_folder.Value ?? "").Trim();
        SaveSettings();
        RefreshTexts();   // the folder's line
        _host?.RequestPageRefresh();   // the History group lists another folder
    }

    private void SaveSettings()
    {
        try { _host?.Settings.Save(_settings); }
        catch (Exception ex) { _host?.Log.Warn("Settings could not be saved", ex); }
    }

    /// <summary>The folder recordings go to: the user's choice, else <c>Documents\Rigbay\logs</c>.</summary>
    internal string ResolveFolder()
    {
        var chosen = _settings.Folder;
        return string.IsNullOrWhiteSpace(chosen) ? _defaultFolder() : chosen;
    }

    /// <summary>The request the Start button, the tray item and the hotkey make: the page's settings.</summary>
    internal StartRequest RequestFromSettings() => new(
        TimeSpan.FromSeconds(_settings.PeriodSeconds),
        _settings.DurationMinutes > 0 ? TimeSpan.FromMinutes(_settings.DurationMinutes) : null,
        ResolveFolder(),
        Only: null,
        Excluded: new HashSet<string>(_settings.ExcludedMetrics, StringComparer.Ordinal));

    /// <summary>The engine (tests).</summary>
    internal RecorderEngine? Engine => _engine;
}
