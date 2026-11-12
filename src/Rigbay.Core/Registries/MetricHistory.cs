using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>One kept reading: the (mean of the) samples of one time slot, stamped with the time of the newest sample in the slot.</summary>
/// <param name="Time">When the newest sample of the slot arrived (the bus clock).</param>
/// <param name="Value">The mean of the samples in the slot.</param>
public readonly record struct HistoryPoint(DateTimeOffset Time, double Value);

/// <summary>
/// What the host remembers of every metric so that something can be drawn: a bounded, in-memory ring per metric id, fed by the
/// <see cref="MetricsBus"/> (it is not a subscriber, so it never keeps a module polling), cleared when the metric is withdrawn, never
/// written to disk and gone when the app ends.
/// <para><b>Bounds.</b> Each id keeps the last <see cref="Window"/> (10 minutes) in at most <see cref="MaxPoints"/> (300) points: time is cut
/// into slots of <see cref="SlotWidth"/> (2 s) and the samples of a slot are averaged into one point, so a 1 Hz source and a 10 Hz source
/// cost the same. At most <see cref="MaxMetrics"/> (256) ids are tracked; a 257th is ignored. One point is 24 bytes, so the worst case is
/// 256 x 300 x 24 B = about 1.8 MB (a typical machine has 20 to 40 ids: under 250 KB).</para>
/// <para><b>Time.</b> Everything is measured with the bus clock, so tests drive it with a fake clock. A module that stops publishing (it
/// sleeps while nobody looks) leaves a gap, not made-up points: <see cref="Get"/> returns only what was published and callers break the
/// line at a gap (<see cref="Segments"/>).</para>
/// </summary>
public sealed class MetricHistory
{
    /// <summary>Default length of what is kept.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    private struct Slot { public long Newest; public double Sum; public int Count; }

    private sealed class Ring
    {
        public Slot[] Items;
        public int Head, Count;
        public long Version;
        public Ring(int capacity) { Items = new Slot[capacity]; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<MetricId, Ring> _rings = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly long _windowTicks;
    private readonly long _slotTicks;
    private long _version;

    /// <param name="clock">The bus clock; null uses the system clock.</param>
    /// <param name="window">How far back to keep; null is <see cref="DefaultWindow"/>.</param>
    /// <param name="maxPoints">Most points per id (the slot width is the window divided by this).</param>
    /// <param name="maxMetrics">Most ids tracked.</param>
    public MetricHistory(Func<DateTimeOffset>? clock = null, TimeSpan? window = null, int maxPoints = 300, int maxMetrics = 256)
    {
        if (maxPoints < 2) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        if (maxMetrics < 1) throw new ArgumentOutOfRangeException(nameof(maxMetrics));
        _clock = clock ?? (() => DateTimeOffset.Now);
        Window = window ?? DefaultWindow;
        if (Window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        MaxPoints = maxPoints;
        MaxMetrics = maxMetrics;
        _windowTicks = Window.Ticks;
        _slotTicks = Math.Max(1, _windowTicks / maxPoints);
    }

    /// <summary>How far back each id is kept.</summary>
    public TimeSpan Window { get; }

    /// <summary>Most points kept per id.</summary>
    public int MaxPoints { get; }

    /// <summary>Most ids tracked.</summary>
    public int MaxMetrics { get; }

    /// <summary>Width of one time slot: samples inside a slot are averaged into one point.</summary>
    public TimeSpan SlotWidth => TimeSpan.FromTicks(_slotTicks);

    /// <summary>Grows with every recorded sample or clear; compare it to know whether anything changed (a chart skips a redraw when it did not).</summary>
    public long Version { get { lock (_gate) return _version; } }

    /// <summary>Grows when <paramref name="id"/> got a new sample or was cleared; 0 for an id never seen.</summary>
    public long VersionOf(MetricId id)
    {
        lock (_gate) return _rings.TryGetValue(id, out var ring) ? ring.Version : 0;
    }

    /// <summary>How many ids are tracked now.</summary>
    public int TrackedCount { get { lock (_gate) return _rings.Count; } }

    /// <summary>The most memory the points of every id can take (the arrays are allocated lazily, one per id, in full).</summary>
    public long WorstCaseBytes => (long)MaxMetrics * (MaxPoints + 1) * System.Runtime.InteropServices.Marshal.SizeOf<Slot>();

    /// <summary>Adds a sample. Unavailable samples and non-finite values are ignored; a sample older than the slot of the newest point is dropped.</summary>
    public void Record(MetricSample sample)
    {
        if (!sample.IsAvailable || !double.IsFinite(sample.Value) || sample.Id.Value.Length == 0) return;
        long t = sample.Timestamp.UtcTicks;
        long index = t / _slotTicks;
        lock (_gate)
        {
            if (!_rings.TryGetValue(sample.Id, out var ring))
            {
                if (_rings.Count >= MaxMetrics) return;
                ring = new Ring(MaxPoints + 1);
                _rings[sample.Id] = ring;
            }
            Prune(ring, t);
            if (ring.Count > 0)
            {
                ref var last = ref ring.Items[(ring.Head + ring.Count - 1) % ring.Items.Length];
                if (index < last.Newest / _slotTicks) return;                      // out of order across threads: keep what is there
                if (index == last.Newest / _slotTicks)
                {
                    last.Sum += sample.Value; last.Count++;
                    if (t > last.Newest) last.Newest = t;
                    ring.Version++; _version++;
                    return;
                }
            }
            if (ring.Count == ring.Items.Length) { ring.Head = (ring.Head + 1) % ring.Items.Length; ring.Count--; }
            ring.Items[(ring.Head + ring.Count) % ring.Items.Length] = new Slot { Newest = t, Sum = sample.Value, Count = 1 };
            ring.Count++;
            ring.Version++; _version++;
        }
    }

    /// <summary>Forgets an id (the bus calls it when the metric is withdrawn: what was drawn is no longer true). True when there was something.</summary>
    public bool Clear(MetricId id)
    {
        lock (_gate)
        {
            if (!_rings.Remove(id, out var ring)) return false;
            _version++;
            return ring.Count > 0;
        }
    }

    /// <summary>Forgets everything.</summary>
    public void ClearAll()
    {
        lock (_gate) { _rings.Clear(); _version++; }
    }

    private void Prune(Ring ring, long nowTicks)
    {
        long cutoff = nowTicks - _windowTicks;
        while (ring.Count > 0 && ring.Items[ring.Head].Newest < cutoff)
        {
            ring.Head = (ring.Head + 1) % ring.Items.Length;
            ring.Count--;
        }
    }

    /// <summary>How many points an id has inside the window now.</summary>
    public int PointCount(MetricId id) => Get(id).Count;

    /// <summary>
    /// The points of an id, oldest first, from the last <paramref name="span"/> (default: everything kept) up to the bus clock's now.
    /// Empty for an unknown id.
    /// </summary>
    public IReadOnlyList<HistoryPoint> Get(MetricId id, TimeSpan? span = null)
    {
        long now = _clock().UtcTicks;
        long cutoff = now - Math.Min(span?.Ticks ?? _windowTicks, _windowTicks);
        lock (_gate)
        {
            if (!_rings.TryGetValue(id, out var ring) || ring.Count == 0) return Array.Empty<HistoryPoint>();
            var list = new List<HistoryPoint>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
            {
                var s = ring.Items[(ring.Head + i) % ring.Items.Length];
                if (s.Newest < cutoff) continue;
                list.Add(new HistoryPoint(new DateTimeOffset(s.Newest, TimeSpan.Zero), s.Sum / s.Count));
            }
            return list;
        }
    }

    /// <summary>The bus clock's now (what a chart draws as its right edge).</summary>
    public DateTimeOffset Now => _clock();

    /// <summary>
    /// Cuts a point list into runs without a hole: a new run starts where two neighbours are further apart than
    /// <paramref name="maxGap"/> (a module that slept, a metric that was withdrawn and came back). A chart draws each run as its own line.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<HistoryPoint>> Segments(IReadOnlyList<HistoryPoint> points, TimeSpan maxGap)
    {
        var runs = new List<IReadOnlyList<HistoryPoint>>();
        List<HistoryPoint>? run = null;
        for (int i = 0; i < points.Count; i++)
        {
            if (run is null || points[i].Time - points[i - 1].Time > maxGap) { run = new List<HistoryPoint>(); runs.Add(run); }
            run.Add(points[i]);
        }
        return runs;
    }

    /// <summary>The gap that breaks a line for a series: three times the usual spacing of its points, never under 8 seconds.</summary>
    public static TimeSpan GapFor(IReadOnlyList<HistoryPoint> points)
    {
        if (points.Count < 3) return TimeSpan.FromSeconds(8);
        var deltas = new List<double>(Math.Min(points.Count - 1, 24));
        for (int i = Math.Max(1, points.Count - 24); i < points.Count; i++) deltas.Add((points[i].Time - points[i - 1].Time).TotalSeconds);
        deltas.Sort();
        double typical = deltas[deltas.Count / 2];
        return TimeSpan.FromSeconds(Math.Max(8, 3 * typical));
    }
}
