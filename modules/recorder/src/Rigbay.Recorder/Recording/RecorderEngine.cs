using System.Collections.Concurrent;
using System.Text;
using Rigbay.Abstractions;

namespace Rigbay.Recorder.Recording;

/// <summary>Why a recording ended.</summary>
internal enum StopReason
{
    /// <summary>The user pressed Stop, used the verb or the tray item.</summary>
    User,
    /// <summary>The time limit was reached.</summary>
    Duration,
    /// <summary>Less than <see cref="RecorderEngine.MinFreeToContinue"/> was free on the drive.</summary>
    DiskLow,
    /// <summary>The file could not be written.</summary>
    WriteError,
    /// <summary>The module was turned off or the host is closing.</summary>
    ModuleStopped,
}

/// <summary>What to record. <see cref="Only"/> names the metrics (the verb); otherwise everything except <see cref="Excluded"/> (the page).</summary>
/// <param name="Period">Time between rows, at least one second.</param>
/// <param name="Duration">Time limit, or null for none.</param>
/// <param name="Folder">Where the files go; created when missing.</param>
/// <param name="Only">The metrics to record, or null for all that are not excluded.</param>
/// <param name="Excluded">Metric ids the user switched off.</param>
internal sealed record StartRequest(TimeSpan Period, TimeSpan? Duration, string Folder, IReadOnlyList<MetricId>? Only = null, IReadOnlySet<string>? Excluded = null);

/// <summary>Answer to <see cref="RecorderEngine.Start"/>.</summary>
/// <param name="Started">True when the file is open and the first row is being written.</param>
/// <param name="Message">Why it did not start (a module-relative key with its arguments); empty when it did.</param>
/// <param name="Path">The CSV when it started.</param>
internal sealed record StartOutcome(bool Started, LocalisedText Message, string? Path = null);

/// <summary>A recording that has ended.</summary>
/// <param name="Path">The CSV.</param>
/// <param name="Rows">Rows written.</param>
/// <param name="Bytes">Size of the CSV.</param>
/// <param name="Started">Time of the first row.</param>
/// <param name="Ended">When it ended.</param>
/// <param name="Reason">Why.</param>
/// <param name="Detail">The exception text for <see cref="StopReason.WriteError"/>, or what was free for <see cref="StopReason.DiskLow"/>; null otherwise.</param>
internal sealed record RecordingSummary(string Path, long Rows, long Bytes, DateTimeOffset Started, DateTimeOffset Ended, StopReason Reason, string? Detail);

/// <summary>Where a recording stands now.</summary>
internal sealed record RecorderStatus(bool Recording, string? Path, long Rows, long Bytes, TimeSpan Elapsed, TimeSpan Period, TimeSpan? Duration, int Columns, int LateMetrics);

/// <summary>Everything the engine takes from outside, so a test can replace each of them. The defaults are the real ones.</summary>
internal sealed class RecorderServices
{
    /// <summary>The time. Rows are stamped with it.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>Waits; throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (span, ct) => Task.Delay(span, ct);

    /// <summary>Free space of the target drive.</summary>
    public IDiskSpace Disk { get; init; } = new SystemDiskSpace();

    /// <summary>Make, model, processor, Windows.</summary>
    public IMachineFactsSource Machine { get; init; } = new RegistryMachineFacts();

    /// <summary>Versions of the modules the columns come from.</summary>
    public IModuleVersions Versions { get; init; } = new SiblingModuleVersions(AppContext.BaseDirectory);

    /// <summary>The labels (asked once, when a recording starts).</summary>
    public Func<IMetricCatalog> Catalog { get; init; } = () => new BareCatalog();

    /// <summary>Creates the CSV file; it must fail when the file exists. The default is <c>CreateNew</c> with <c>FileShare.Read</c>.</summary>
    public Func<string, Stream> CreateFile { get; init; } = path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096);

    /// <summary>Id of this module; its own metrics are not recorded.</summary>
    public string OwnModuleId { get; init; } = "recorder";

    /// <summary>Version of this module, for the .meta.json.</summary>
    public string ToolVersion { get; init; } = "0.1.0";

    /// <summary>Version of the host, for the .meta.json.</summary>
    public string? HostVersion { get; init; }
}

/// <summary>
/// The recorder: takes a snapshot of the latest value of every chosen metric on a fixed schedule and writes it to a CSV file
/// with a .meta.json next to it. One recording at a time. Nothing here throws to the caller: a failure ends the recording, is
/// logged and shows up in the summary. It never deletes a file.
/// <para>Threads: <see cref="Start"/> and <see cref="Stop"/> run on the caller's thread, the rows are written by one task of
/// the engine's own, and that task is the only one that touches the file after <see cref="Start"/> returned.</para>
/// </summary>
internal sealed class RecorderEngine
{
    /// <summary>A recording does not start with less than this free on the drive (200 MB).</summary>
    public const long MinFreeToStart = 200L * 1024 * 1024;

    /// <summary>A recording stops by itself when less than this is free (50 MB).</summary>
    public const long MinFreeToContinue = 50L * 1024 * 1024;

    /// <summary>A value whose newest sample is older than this is written as an empty cell (the source stopped updating it).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly IMetricsBus _bus;
    private readonly ILog _log;
    private readonly RecorderServices _svc;
    private readonly object _gate = new();
    private Session? _current;
    private RecordingSummary? _last;

    /// <summary>Creates the engine.</summary>
    public RecorderEngine(IMetricsBus bus, ILog log, RecorderServices services)
    {
        _bus = bus;
        _log = log;
        _svc = services;
    }

    /// <summary>Raised (on the writing thread, or the caller's for a start) when the status changed: a row was written, a recording began or ended. Handlers must be short and must not throw.</summary>
    public event Action? Changed;

    /// <summary>Raised once when a recording has ended, after it is closed and <see cref="Status"/> says idle.</summary>
    public event Action<RecordingSummary>? Ended;

    /// <summary>True while a recording runs.</summary>
    public bool IsRecording => Volatile.Read(ref _current) is not null;

    /// <summary>The recording that ended last, or null.</summary>
    public RecordingSummary? LastSummary => Volatile.Read(ref _last);

    // ------------------------------------------------------------------ status

    /// <summary>A snapshot of the running recording (or an idle status).</summary>
    public RecorderStatus Status()
    {
        var s = Volatile.Read(ref _current);
        if (s is null) return new RecorderStatus(false, null, 0, 0, TimeSpan.Zero, TimeSpan.Zero, null, 0, 0);
        return new RecorderStatus(true, s.Path, Interlocked.Read(ref s.Rows), Interlocked.Read(ref s.Bytes), _svc.Clock() - s.Started, s.Period, s.Duration, s.Ids.Length, s.Late.Count);
    }

    // ------------------------------------------------------------------ start

    /// <summary>
    /// Opens the file, writes the header and the first .meta.json, and starts the schedule. The columns are the available metrics
    /// at this moment that the request wants, from other modules, in a fixed order (source module, then id).
    /// </summary>
    public StartOutcome Start(StartRequest request)
    {
        Session? s = null;
        try
        {
            lock (_gate)
            {
                if (_current is not null) return No("start.already");
                if (request.Period < TimeSpan.FromSeconds(1)) return No("start.bad-period");
                if (request.Duration is { } d && d <= TimeSpan.Zero) return No("start.bad-duration");

                string folder = request.Folder;
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { return No("start.folder-failed", folder, ex.Message); }

                var free = _svc.Disk.FreeBytes(folder);
                if (free is { } f && f < MinFreeToStart) return No("start.disk-low", ToMb(f), ToMb(MinFreeToStart));

                // Subscribe first: it wakes the modules that only poll while somebody reads, and a metric published between this and the
                // snapshot below is then not missed.
                s = new Session { Folder = folder, Period = request.Period, Duration = request.Duration };
                s.Subscription = _bus.Subscribe(sample => OnSample(s, sample));

                var available = _bus.GetLatest().Where(x => x.IsAvailable && x.SourceModule != _svc.OwnModuleId).ToList();
                Func<string, bool> wants = request.Only is { } only
                    ? (id => only.Any(o => o.Value == id))
                    : (id => request.Excluded is null || !request.Excluded.Contains(id));
                s.Wants = wants;

                if (request.Only is { } named)
                {
                    var missing = named.Where(n => available.All(a => a.Id != n)).Select(n => n.Value).ToList();
                    if (missing.Count > 0) { Abandon(s); return No("start.unknown-metrics", string.Join(", ", missing)); }
                }
                var chosen = available.Where(x => wants(x.Id.Value))
                    .OrderBy(x => x.SourceModule, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal).ToList();
                if (chosen.Count == 0) { Abandon(s); return No("start.no-metrics"); }

                var catalog = _svc.Catalog();
                s.Ids = chosen.Select(x => x.Id).ToArray();
                s.ColumnSet = new HashSet<string>(chosen.Select(x => x.Id.Value), StringComparer.Ordinal);
                s.Columns = chosen.Select(x =>
                {
                    var text = catalog.Describe(x.Id);
                    string label = string.IsNullOrWhiteSpace(text?.Label) ? x.Id.Value : text!.Label;
                    string description = string.IsNullOrWhiteSpace(text?.Description) ? label : text!.Description;
                    return new ColumnInfo(x.Id.Value, label, x.Unit.Symbol(), x.SourceModule, description);
                }).ToArray();

                s.Started = _svc.Clock();
                s.EndAt = request.Duration is { } span ? s.Started + span : null;
                s.Schedule = new FixedSchedule(s.Started, request.Period);
                s.Machine = _svc.Machine.Read();
                s.Modules = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var source in s.Columns.Select(c => c.Source).Append(_svc.OwnModuleId).Distinct(StringComparer.Ordinal))
                    if (_svc.Versions.VersionOf(source) is { } version) s.Modules[source] = version;

                if (!OpenFiles(s, out var failure)) { Abandon(s); return failure; }

                Volatile.Write(ref _current, s);   // before the task starts: its Finish clears it, and takes the same lock
                s.Loop = Task.Run(() => RunAsync(s));
            }
            RaiseChanged();
            return new StartOutcome(true, default, s.Path);
        }
        catch (Exception ex)
        {
            _log.Error("Starting a recording failed", ex);
            if (s is not null) Abandon(s);
            lock (_gate) { if (_current == s) Volatile.Write(ref _current, null); }
            return No("start.failed", ex.Message);
        }
    }

    private static StartOutcome No(string key, params object[] args) => new(false, LocalisedText.Of(key, args));

    private static long ToMb(long bytes) => bytes / (1024 * 1024);

    /// <summary>Picks a free file name, opens the CSV, writes the header and the first meta.</summary>
    private bool OpenFiles(Session s, out StartOutcome failure)
    {
        failure = default!;
        string stem = CsvFormat.FileStem(s.Started);
        Stream? stream = null;
        for (int n = 1; n <= 999 && stream is null; n++)
        {
            string name = n == 1 ? stem : stem + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string csv = Path.Combine(s.Folder, name + CsvFormat.CsvExtension);
            string meta = Path.Combine(s.Folder, name + CsvFormat.MetaSuffix);
            if (File.Exists(csv) || File.Exists(meta)) continue;   // never write over a file that is already there
            try
            {
                stream = _svc.CreateFile(csv);
                s.Path = csv;
                s.MetaPath = meta;
            }
            catch (IOException) when (File.Exists(csv)) { /* somebody took the name between the check and the open: try the next */ }
            catch (Exception ex)
            {
                failure = No("start.file-failed", csv, ex.Message);
                return false;
            }
        }
        if (stream is null) { failure = No("start.file-failed", s.Folder, "no free file name"); return false; }

        try
        {
            s.Stream = stream;
            s.Writer = new StreamWriter(stream, CsvFormat.Utf8NoBom, 4096) { AutoFlush = false };
            string header = "timestamp,elapsed_s," + string.Join(",", s.Ids.Select(i => CsvFormat.Field(i.Value))) + CsvFormat.NewLine;
            s.Writer.Write(header);
            s.Writer.Flush();
            s.Bytes = CsvFormat.Utf8NoBom.GetByteCount(header);
            WriteMeta(s, ended: null, reason: null, error: null);
            return true;
        }
        catch (Exception ex)
        {
            try { (s.Writer as IDisposable ?? stream).Dispose(); } catch (Exception) { /* closing a file that just failed */ }
            failure = No("start.file-failed", s.Path ?? s.Folder, ex.Message);
            return false;
        }
    }

    private static void Abandon(Session s)
    {
        try { s.Subscription?.Dispose(); } catch (Exception) { /* nothing to do */ }
        s.Subscription = null;
    }

    // ------------------------------------------------------------------ stop

    /// <summary>
    /// Asks the running recording to end and waits (a few seconds at most) until the file is closed. Returns the summary, or null
    /// when nothing was recording. Safe to call twice and from any thread.
    /// </summary>
    public RecordingSummary? Stop(StopReason reason = StopReason.User)
    {
        var s = Volatile.Read(ref _current);
        if (s is null) return null;
        Interlocked.CompareExchange(ref s.Requested, (int)reason, -1);
        try { s.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* it ended by itself in the meantime */ }
        try { s.Loop?.Wait(TimeSpan.FromSeconds(4)); }
        catch (Exception) { /* RunAsync does not throw; a timeout just returns */ }
        return LastSummary is { } last && last.Path == s.Path ? last : null;
    }

    // ------------------------------------------------------------------ the writing task

    private async Task RunAsync(Session s)
    {
        var reason = StopReason.User;
        string? detail = null;
        try
        {
            var token = s.Cts.Token;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var now = _svc.Clock();

                if (s.EndAt is { } end && s.Schedule.Next >= end)
                {
                    if (end > now) await _svc.Delay(end - now, token).ConfigureAwait(false);
                    reason = StopReason.Duration;
                    break;
                }
                if (s.Schedule.Next > now) await _svc.Delay(s.Schedule.Next - now, token).ConfigureAwait(false);

                var slot = s.Schedule.Take(_svc.Clock());
                var free = _svc.Disk.FreeBytes(s.Folder);
                if (free is { } f && f < MinFreeToContinue)
                {
                    reason = StopReason.DiskLow;
                    detail = ToMb(f).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                }
                WriteRow(s, slot);
                if (s.LateDirty)
                {
                    s.LateDirty = false;
                    TryWriteMeta(s, ended: null, reason: null, error: null);
                }
                RaiseChanged();
            }
        }
        catch (OperationCanceledException)
        {
            int asked = Volatile.Read(ref s.Requested);
            reason = asked >= 0 ? (StopReason)asked : StopReason.User;
        }
        catch (Exception ex)
        {
            reason = StopReason.WriteError;
            detail = ex.Message;
            _log.Error("Writing the recording failed; it was stopped", ex);
        }
        Finish(s, reason, detail);
    }

    private void WriteRow(Session s, DateTimeOffset slot)
    {
        var line = s.Line;
        line.Clear();
        line.Append(CsvFormat.Time(slot)).Append(',').Append(CsvFormat.Number((slot - s.Started).TotalSeconds));
        foreach (var id in s.Ids)
        {
            line.Append(',');
            if (TryValue(id, slot, out double value)) line.Append(CsvFormat.Number(value));
        }
        line.Append(CsvFormat.NewLine);
        string text = line.ToString();
        s.Writer.Write(text);
        s.Writer.Flush();   // one flush per row: a crash loses at most the row being written
        Interlocked.Add(ref s.Bytes, CsvFormat.Utf8NoBom.GetByteCount(text));
        Interlocked.Increment(ref s.Rows);
    }

    private bool TryValue(MetricId id, DateTimeOffset slot, out double value)
    {
        value = 0;
        if (!_bus.TryGetLatest(id, out var sample) || !sample.IsAvailable || !double.IsFinite(sample.Value)) return false;
        if (slot - sample.Timestamp > StaleAfter) return false;
        value = sample.Value;
        return true;
    }

    private void OnSample(Session s, MetricSample sample)
    {
        try
        {
            if (!sample.IsAvailable || sample.SourceModule == _svc.OwnModuleId) return;
            string id = sample.Id.Value;
            if (s.ColumnSet.Contains(id) || !s.Wants(id)) return;
            if (s.Late.TryAdd(id, new LateMetric(id, sample.SourceModule, sample.Unit.Symbol(), CsvFormat.Time(sample.Timestamp)))) s.LateDirty = true;
        }
        catch (Exception ex)
        {
            _log.Warn("The recorder could not note a new metric", ex);
        }
    }

    // ------------------------------------------------------------------ end

    private void Finish(Session s, StopReason reason, string? detail)
    {
        try { s.Writer.Flush(); }
        catch (Exception ex)
        {
            // The rows are already flushed one by one; this can only lose what was in flight. Say so unless a worse reason is already known.
            if (reason is StopReason.User or StopReason.Duration or StopReason.ModuleStopped) { reason = StopReason.WriteError; detail = ex.Message; }
        }
        try { s.Writer.Dispose(); } catch (Exception) { /* the file is closed as far as it can be */ }
        Abandon(s);
        var ended = _svc.Clock();
        TryWriteMeta(s, ended, reason, reason is StopReason.WriteError ? detail : null);
        var summary = new RecordingSummary(s.Path, Interlocked.Read(ref s.Rows), Interlocked.Read(ref s.Bytes), s.Started, ended, reason, detail);
        lock (_gate)
        {
            Volatile.Write(ref _last, summary);
            if (ReferenceEquals(_current, s)) Volatile.Write(ref _current, null);
        }
        try { s.Cts.Dispose(); } catch (Exception) { /* already gone */ }
        _log.Info($"Recording ended ({reason}): {summary.Rows} rows, {CsvFormat.Size(summary.Bytes)}, {summary.Path}");
        try { Ended?.Invoke(summary); } catch (Exception ex) { _log.Warn("An Ended handler threw", ex); }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.Warn("A Changed handler threw", ex); }
    }

    // ------------------------------------------------------------------ meta

    private void TryWriteMeta(Session s, DateTimeOffset? ended, StopReason? reason, string? error)
    {
        try { WriteMeta(s, ended, reason, error); }
        catch (Exception ex) { _log.Warn("Writing the .meta.json failed", ex); }
    }

    private void WriteMeta(Session s, DateTimeOffset? ended, StopReason? reason, string? error)
    {
        var doc = new MetaDocument
        {
            ToolVersion = _svc.ToolVersion,
            HostVersion = _svc.HostVersion,
            Csv = Path.GetFileName(s.Path),
            Started = CsvFormat.Time(s.Started),
            Ended = ended is { } e ? CsvFormat.Time(e) : null,
            PeriodSeconds = s.Period.TotalSeconds,
            LimitMinutes = s.Duration?.TotalMinutes,
            Rows = Interlocked.Read(ref s.Rows),
            StopReason = reason switch
            {
                StopReason.User => "user",
                StopReason.Duration => "duration",
                StopReason.DiskLow => "disk-low",
                StopReason.WriteError => "write-error",
                StopReason.ModuleStopped => "module-stopped",
                _ => null,
            },
            Error = error,
            Machine = new MetaDocument.MachineSection { Manufacturer = s.Machine?.Manufacturer, Model = s.Machine?.Model, Cpu = s.Machine?.Cpu, Os = s.Machine?.Os },
            Modules = s.Modules,
            Columns = s.Columns,
            LateMetrics = s.Late.Values.OrderBy(l => l.FirstSeen, StringComparer.Ordinal).ThenBy(l => l.Id, StringComparer.Ordinal).ToArray(),
            Notes = MetaDocument.StandardNotes,
        };
        // Write beside the target and rename over it, so a full disk cannot leave a half-written meta in place of the good one.
        string temp = s.MetaPath + ".tmp";
        File.WriteAllBytes(temp, doc.ToUtf8());
        File.Move(temp, s.MetaPath, overwrite: true);
    }

    // ------------------------------------------------------------------ one recording

    private sealed class Session
    {
        public string Folder = "";
        public string Path = "";
        public string MetaPath = "";
        public Stream Stream = null!;
        public StreamWriter Writer = null!;
        public MetricId[] Ids = Array.Empty<MetricId>();
        public ColumnInfo[] Columns = Array.Empty<ColumnInfo>();
        public HashSet<string> ColumnSet = new();
        public Func<string, bool> Wants = _ => true;
        public DateTimeOffset Started;
        public DateTimeOffset? EndAt;
        public TimeSpan Period;
        public TimeSpan? Duration;
        public FixedSchedule Schedule = null!;
        public MachineFacts? Machine;
        public SortedDictionary<string, string> Modules = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, LateMetric> Late = new(StringComparer.Ordinal);
        public volatile bool LateDirty;
        public IDisposable? Subscription;
        public readonly CancellationTokenSource Cts = new();
        public Task? Loop;
        public int Requested = -1;
        public long Rows;
        public long Bytes;
        public readonly StringBuilder Line = new();
    }
}
