using System.Globalization;
using Rigbay.Abstractions;
using Rigbay.Recorder.Recording;

namespace Rigbay.Recorder;

public sealed partial class RecorderModule
{
    private bool _syncingPicks;

    /// <summary>One metric the picker offers.</summary>
    private sealed record Offered(string Source, MetricId Id, string Label, MetricUnit Unit);

    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host)
    {
        var offered = OfferedMetrics();
        _pageMetrics = new HashSet<string>(offered.Select(o => o.Id.Value), StringComparer.Ordinal);
        SyncPicks(offered);
        UpdatePickedText();

        var periodOptions = RecorderSettings.PeriodChoices.Select(c => new ChoiceOption(Id(c.Seconds), c.LabelKey)).ToArray();
        var durationOptions = RecorderSettings.DurationChoices.Select(c => new ChoiceOption(Id(c.Minutes), c.LabelKey)).ToArray();

        var recording = new List<Row>
        {
            new ButtonRow("row.start.header", "row.start.button", _ => Task.FromResult(StartFromPage()))
            {
                DescriptionKey = "row.start.description",
                Enabled = _idle,
            },
            new ButtonRow("row.stop.header", "row.stop.button", _ => Task.FromResult(StopFromPage()))
            {
                DescriptionKey = "row.stop.description",
                Enabled = _recording,
            },
            new ValueRow("row.state.header", _stateText),
            new ValueRow("row.rows.header", _rowsText) { DescriptionKey = "row.rows.description" },
            new ValueRow("row.size.header", _sizeText),
            new ValueRow("row.elapsed.header", _elapsedText),
            new ValueRow("row.file.header", _fileText) { DescriptionKey = "row.file.description" },
            new ValueRow("row.message.header", _messageText) { DescriptionKey = "row.message.description" },
            new ChoiceRow("row.period.header", periodOptions, _period) { DescriptionKey = "row.period.description", Enabled = _idle },
            new ChoiceRow("row.duration.header", durationOptions, _duration) { DescriptionKey = "row.duration.description", Enabled = _idle },
            new TextRow("row.folder.header", _folder)
            {
                DescriptionKey = "row.folder.description",
                PlaceholderKey = "row.folder.placeholder",
                MaxLength = 240,
                // A path is typed one character at a time: nothing is used (or created) until the user says Apply.
                Apply = ApplyMode.ApplyRevert,
                Validate = ValidateFolder,
                Enabled = _idle,
            },
            new ButtonRow("row.open-folder.header", "row.open-folder.button", _ => Task.FromResult(OpenFolder())) { DescriptionKey = "row.open-folder.description" },
        };
        if (_hotkey is not null)
            recording.Add(new ShortcutRow("row.hotkey.header", _hotkey.Gesture) { DescriptionKey = "row.hotkey.description" });

        return new PageModel
        {
            Groups = new[]
            {
                new RowGroup("group.recording", recording),
                new RowGroup("group.metrics", MetricRows(offered)),
                new RowGroup("group.history", HistoryRows()),
            },
            Summary = new PageSummary(_stateLine, new[] { RowsMetric }),
        };
    }

    private static string Id(int n) => n.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ buttons

    private ActionResult StartFromPage()
    {
        var engine = _engine;
        if (engine is null) return ActionResult.Fail("cmd.not-running");
        var outcome = StartRecording(RequestFromSettings());
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
        var outcome = engine.Start(request);
        if (!outcome.Started)
        {
            _lastNotice = outcome.Message;
            RefreshTexts();
        }
        else
        {
            _lastNotice = null;
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

        rows.Add(new ValueRow("row.picked.header", _pickedText) { DescriptionKey = "row.picked.description" });
        rows.Add(new ButtonRow("row.pick-all.header", "row.pick-all.button", _ => Task.FromResult(SetAllPicks(true))) { Enabled = _idle });
        rows.Add(new ButtonRow("row.pick-none.header", "row.pick-none.button", _ => Task.FromResult(SetAllPicks(false))) { Enabled = _idle });
        foreach (var group in offered.GroupBy(o => o.Source))
        {
            var toggles = group.Select(o => (Row)new ToggleRow(Text.Literal(o.Label), _picks[o.Id.Value])
            {
                DescriptionKey = Text.Literal(o.Unit.Symbol().Length > 0 ? o.Id.Value + " · " + o.Unit.Symbol() : o.Id.Value),
                Enabled = _idle,
            }).ToList();
            rows.Add(new ExpanderRow(Text.Literal(group.Key), toggles));
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
        var loc = _host?.Localisation;
        if (loc is null) return;
        int total = _pageMetrics.Count;
        int on = _pageMetrics.Count(id => _picks.TryGetValue(id, out var p) && p.Value);
        _pickedText.Value = loc.F("status.picked", on, total);
    }

    /// <summary>The metric set the page was built from no longer matches the bus (a module started publishing): rebuild it when the page opens.</summary>
    private void OnPageVisibleChanged()
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
