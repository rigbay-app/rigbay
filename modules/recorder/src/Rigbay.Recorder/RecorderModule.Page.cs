using System.Globalization;
using Rigbay.Abstractions;
using Rigbay.Recorder.Recording;

namespace Rigbay.Recorder;

public sealed partial class RecorderModule
{
    private bool _syncingPicks;

    /// <summary>One metric the picker offers.</summary>
    private sealed record Offered(string Source, MetricId Id, string Label, MetricUnit Unit);

    // The option rows are made once and kept: the page is rebuilt when a recording starts or ends, and a new row instance would lose its
    // status and its Revert link in the host. Start and Stop are made new with every page on purpose: the host keeps a button's last message
    // on its row instance, and "Recording to x.csv" must not come back beside Start once that recording is over (the line says how it ended).
    private ChoiceRow? _periodRow;
    private ChoiceRow? _durationRow;
    private TextRow? _folderRow;
    private ButtonRow? _openFolderRow;
    private ButtonRow? _pickAllRow;
    private ButtonRow? _pickNoneRow;

    // 1 while the page that was built last shows Stop (a recording was running), 0 while it shows Start, -1 before the first page.
    private int _pageShowsRecording = -1;

    /// <inheritdoc />
    /// <remarks>
    /// Review of November 2026 (RC-1): the main job is one big button whose label is its state (<see cref="PageModel.Primary"/>: "Start
    /// recording" while idle, "Stop recording" while one runs), with a live line (file, time, rows, size; or how the last one ended). The six
    /// status rows that read "–" while idle are gone. The folder is one row with its Open button in the header and the path inside.
    /// </remarks>
    public PageModel? GetPageModel(IHostServices host)
    {
        var offered = OfferedMetrics();
        _pageMetrics = new HashSet<string>(offered.Select(o => o.Id.Value), StringComparer.Ordinal);
        _pageSources = offered.GroupBy(o => o.Source).ToDictionary(g => g.Key, g => g.Select(o => o.Id.Value).ToList(), StringComparer.Ordinal);
        SyncPicks(offered);
        bool recording = _recording.Value;
        Volatile.Write(ref _pageShowsRecording, recording ? 1 : 0);

        Row primary = recording
            ? new ButtonRow("row.stop.header", "row.stop.button", _ => Task.FromResult(StopFromPage()))
            {
                Line = _primaryLine,
                Enabled = _recording,
            }
            : new ButtonRow("row.start.header", "row.start.button", _ => Task.FromResult(StartFromPage()))
            {
                DescriptionKey = "row.start.description",
                HelpKey = "row.start.help",
                Line = _primaryLine,
                Enabled = _idle,
            };
        _periodRow ??= new ChoiceRow("row.period.header", RecorderSettings.PeriodChoices.Select(c => new ChoiceOption(Id(c.Seconds), c.LabelKey)).ToArray(), _period)
        {
            HelpKey = "row.period.help",
            Enabled = _idle,
        };
        _durationRow ??= new ChoiceRow("row.duration.header", RecorderSettings.DurationChoices.Select(c => new ChoiceOption(Id(c.Minutes), c.LabelKey)).ToArray(), _duration)
        {
            Enabled = _idle,
        };
        _folderRow ??= new TextRow("row.folder-path.header", _folder)
        {
            PlaceholderKey = "row.folder.placeholder",
            MaxLength = 240,
            // A path is typed one character at a time: nothing is used (or created) until the user says Apply.
            Apply = ApplyMode.ApplyRevert,
            Validate = ValidateFolder,
            Enabled = _idle,
        };
        _openFolderRow ??= new ButtonRow("row.open-folder.header", "row.open-folder.button", _ => Task.FromResult(OpenFolder()));

        var options = new List<Row>
        {
            _periodRow,
            _durationRow,
            // The folder in one row: where the files go in its line, Open in its header, the path to change inside.
            new ExpanderRow("row.folder.header", new Row[] { _folderRow }) { Line = _folderLine, HeaderRow = _openFolderRow },
        };
        if (_hotkey is not null)
            options.Add(new ShortcutRow("row.hotkey.header", _hotkey.Gesture) { DescriptionKey = "row.hotkey.description" });

        var groups = new[]
        {
            new RowGroup("group.options", options),
            new RowGroup("group.metrics", MetricRows(offered)),
            new RowGroup("group.history", HistoryRows()),
        };
        UpdatePickedText();
        return new PageModel
        {
            Primary = primary,
            Groups = groups,
            // The bay says "Recording 00:12:31" or "Not recording". recorder.rows is on the bus for whoever wants a counter, but a reading that
            // says "Not available" next to "Not recording" would look like an error.
            Summary = new PageSummary(_stateLine),
        };
    }

    /// <summary>The main button is Start or Stop: ask for a new page when the state changed since the page was built. Safe from any thread.</summary>
    private void RefreshPageIfRecordingChanged(bool recording)
    {
        int shown = Volatile.Read(ref _pageShowsRecording);
        if (shown < 0 || (shown == 1) == recording) return;
        Volatile.Write(ref _pageShowsRecording, recording ? 1 : 0);   // one request per change; the rebuild writes the same value again
        try { _host?.RequestPageRefresh(); }
        catch (Exception ex) { _host?.Log.Warn("Asking for a new page failed", ex); }
    }

    private static string Id(int n) => n.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ buttons

    private ActionResult StartFromPage()
    {
        var engine = _engine;
        if (engine is null) return ActionResult.Fail("cmd.not-running");
        // A refusal is the button's own message, beside it; the line keeps saying how the last recording ended (said once, not twice).
        var before = _lastNotice;
        var outcome = StartRecording(RequestFromSettings());
        if (!outcome.Started)
        {
            _lastNotice = before;
            RefreshTexts();
        }
        return outcome.Started ? ActionResult.Ok(LocalisedText.Of("start.ok", Path.GetFileName(outcome.Path ?? ""))) : new ActionResult(false, outcome.Message);
    }

    private ActionResult StopFromPage()
    {
        var summary = _engine?.Stop(StopReason.User);
        return summary is null ? ActionResult.Fail("stop.not-recording") : ActionResult.Ok(EndedNotice(summary));
    }

    /// <summary>Starts a recording and remembers a refusal as the "last message", so the page keeps saying why nothing is running.</summary>
    internal StartOutcome StartRecording(StartRequest request)
    {
        var engine = _engine;
        if (engine is null) return new StartOutcome(false, LocalisedText.Of("cmd.not-running"));
        // Cleared before the start, not after: a recording that ends at once (a write error, no space) has set how it ended by the time Start returns.
        _lastNotice = null;
        var outcome = engine.Start(request);
        if (!outcome.Started)
        {
            _lastNotice = outcome.Message;
            RefreshTexts();
        }
        return outcome;
    }

    private ActionResult OpenFolder()
    {
        try
        {
            var folder = ResolveFolder();
            Directory.CreateDirectory(folder);
            _shell.OpenFolder(folder);
            return ActionResult.Ok();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn("Opening the folder failed", ex);
            return ActionResult.Fail("open.failed", ex.Message);
        }
    }

    private ActionResult OpenFile(string path)
    {
        try
        {
            _shell.OpenFile(path);
            return ActionResult.Ok();
        }
        catch (Exception ex)
        {
            _host?.Log.Warn("Opening a recording failed", ex);
            return ActionResult.Fail("open.failed", ex.Message);
        }
    }

    private static LocalisedText? ValidateFolder(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return null;   // empty means the default folder
        try
        {
            return Path.IsPathFullyQualified(trimmed) && trimmed.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? null : LocalisedText.Of("error.folder-absolute");
        }
        catch (Exception)
        {
            return LocalisedText.Of("error.folder-absolute");
        }
    }

    // ------------------------------------------------------------------ the metric picker

    /// <summary>The metrics on the bus that the recorder would record now, in the order of the columns.</summary>
    private List<Offered> OfferedMetrics()
    {
        var host = _host;
        if (host is null) return new List<Offered>();
        var catalog = _services.Catalog();
        return host.Metrics.GetLatest()
            .Where(s => s.IsAvailable && s.SourceModule != _services.OwnModuleId)
            .OrderBy(s => s.SourceModule, StringComparer.Ordinal).ThenBy(s => s.Id.Value, StringComparer.Ordinal)
            .Select(s => new Offered(s.SourceModule, s.Id, catalog.Describe(s.Id)?.Label is { Length: > 0 } label ? label : s.Id.Value, s.Unit))
            .ToList();
    }

    /// <summary>Makes sure each offered metric has a switch that follows the settings, and that a change of it is saved.</summary>
    private void SyncPicks(IEnumerable<Offered> offered)
    {
        var excluded = new HashSet<string>(_settings.ExcludedMetrics, StringComparer.Ordinal);
        _syncingPicks = true;
        try
        {
            foreach (var o in offered)
            {
                string id = o.Id.Value;
                bool on = !excluded.Contains(id);
                if (_picks.TryGetValue(id, out var existing)) { existing.Value = on; continue; }
                var pick = new ObservableValue<bool>(on);
                _picks[id] = pick;
                pick.Changed += () => OnPickChanged(id, pick);
            }
        }
        finally
        {
            _syncingPicks = false;
        }
    }

    private void OnPickChanged(string id, ObservableValue<bool> pick)
    {
        if (_syncingPicks) return;
        var list = _settings.ExcludedMetrics;
        if (pick.Value) list.RemoveAll(x => x == id);
        else if (!list.Contains(id)) list.Add(id);
        SaveSettings();
        UpdatePickedText();
    }

    private IReadOnlyList<Row> MetricRows(List<Offered> offered)
    {
        var rows = new List<Row>();
        if (offered.Count == 0)
        {
            rows.Add(new InfoRow(Severity.Info, "row.no-metrics.header") { DescriptionKey = "row.no-metrics.description" });
            return rows;
        }

        // The count and "Select all" on one row, then "Clear"; each source says how many of its metrics are picked while it is closed.
        _pickAllRow ??= new ButtonRow("row.picked.header", "row.pick-all.button", _ => Task.FromResult(SetAllPicks(true)))
        {
            HelpKey = "row.picked.help",
            Line = _pickedLine,
            Enabled = _idle,
        };
        _pickNoneRow ??= new ButtonRow("row.pick-none.header", "row.pick-none.button", _ => Task.FromResult(SetAllPicks(false))) { Enabled = _idle };
        rows.Add(_pickAllRow);
        rows.Add(_pickNoneRow);
        foreach (var group in offered.GroupBy(o => o.Source))
        {
            var toggles = group.Select(o => (Row)new ToggleRow(Text.Literal(o.Label), _picks[o.Id.Value])
            {
                DescriptionKey = Text.Literal(o.Unit.Symbol().Length > 0 ? o.Id.Value + " · " + o.Unit.Symbol() : o.Id.Value),
                Enabled = _idle,
            }).ToList();
            rows.Add(new ExpanderRow(Text.Literal(group.Key), toggles) { Line = SourceLine(group.Key) });
        }
        return rows;
    }

    private ActionResult SetAllPicks(bool on)
    {
        _syncingPicks = true;
        try
        {
            foreach (var id in _pageMetrics)
                if (_picks.TryGetValue(id, out var pick)) pick.Value = on;
        }
        finally
        {
            _syncingPicks = false;
        }
        var list = _settings.ExcludedMetrics;
        foreach (var id in _pageMetrics) list.RemoveAll(x => x == id);
        if (!on) list.AddRange(_pageMetrics);
        SaveSettings();
        UpdatePickedText();
        return ActionResult.Ok();
    }

    private void UpdatePickedText()
    {
        if (_host is null) return;
        int total = _pageMetrics.Count;
        int on = _pageMetrics.Count(IsPicked);
        _pickedLine.Value = LocalisedText.Of("status.picked", on, total);
        lock (_sourceLines)
            foreach (var (source, line) in _sourceLines)
            {
                var ids = _pageSources.TryGetValue(source, out var list) ? list : new List<string>();
                line.Value = LocalisedText.Of("status.picked", ids.Count(IsPicked), ids.Count);
            }
    }

    private bool IsPicked(string id) => _picks.TryGetValue(id, out var p) && p.Value;

    /// <summary>The live line of one source module's expander ("3 of 5"), kept per source so a rebuilt page keeps following it.</summary>
    private ObservableValue<LocalisedText> SourceLine(string source)
    {
        lock (_sourceLines)
        {
            if (!_sourceLines.TryGetValue(source, out var line)) _sourceLines[source] = line = new ObservableValue<LocalisedText>(default);
            return line;
        }
    }

    /// <summary>The metric set the page was built from no longer matches the bus (a module started publishing): rebuild it when the page opens.</summary>
    private void RefreshPageIfStale()
    {
        var host = _host;
        if (host is null || !host.PageVisible.Value) return;
        if (_engine?.IsRecording == true) return;
        var now = OfferedMetrics().Select(o => o.Id.Value);
        if (!_pageMetrics.SetEquals(now)) host.RequestPageRefresh();
    }

    // ------------------------------------------------------------------ history

    private IReadOnlyList<Row> HistoryRows()
    {
        var current = _engine?.Status().Path;
        var entries = History.List(ResolveFolder(), History.Limit, current);
        var rows = new List<Row>();
        if (entries.Count == 0)
        {
            rows.Add(new InfoRow(Severity.Info, "row.history-empty.header") { DescriptionKey = "row.history-empty.description" });
            return rows;
        }
        foreach (var e in entries)
        {
            string path = e.Path;
            string when = e.LastWrite.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            rows.Add(new ButtonRow(Text.Literal(e.Name), "row.history-open.button", _ => Task.FromResult(OpenFile(path)))
            {
                DescriptionKey = Text.Literal(when + " · " + CsvFormat.Size(e.Bytes)),
            });
        }
        return rows;
    }
}
