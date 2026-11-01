using Rigbay.Abstractions;
using Rigbay.PredatorSense.Wmi;

namespace Rigbay.PredatorSense.Sensors;

/// <summary>State of the readings as the rest of the module sees it.</summary>
public enum ReadingsState
{
    /// <summary>No poll has finished yet.</summary>
    Starting,
    /// <summary>The last poll got answers.</summary>
    Available,
    /// <summary>The last poll or the last few got no answer at all (fewer than <see cref="SensorPoller.FailureLimit"/> in a row). Writes are off.</summary>
    Degraded,
    /// <summary>The sensors could not be read <see cref="SensorPoller.FailureLimit"/> times in a row. Writes are off; the page says so.</summary>
    Unavailable,
}

/// <summary>The result of one poll.</summary>
/// <param name="At">When it finished.</param>
/// <param name="Values">One entry per sensor; null = not available.</param>
/// <param name="AnyAnswer">True when at least one call returned status 0.</param>
public sealed record SensorSnapshot(DateTimeOffset At, IReadOnlyDictionary<Sensor, double?> Values, bool AnyAnswer)
{
    /// <summary>The value of a sensor or null.</summary>
    public double? this[Sensor sensor] => Values.TryGetValue(sensor, out var v) ? v : null;

    /// <summary>An empty snapshot (nothing read yet).</summary>
    public static SensorSnapshot Empty { get; } = new(DateTimeOffset.MinValue, new Dictionary<Sensor, double?>(), false);
}

/// <summary>
/// Reads the six approved sensors through <see cref="IAcerWmi"/> and publishes them on the metrics bus. A poll never throws:
/// a call that fails, times out or answers a non-zero status becomes "not available" for that sensor (nothing is published
/// for it). A poll in which no call got a status-0 answer counts as failed; <see cref="FailureLimit"/> failed polls in a row
/// make the state <see cref="ReadingsState.Unavailable"/>. One successful poll brings it back. A timeout ends the poll at once
/// (the line is still held by the stalled call, so asking again only wastes time). A sensor that has given a value before and
/// then gets no value <see cref="FailureLimit"/> polls in a row is withdrawn from the bus (<see cref="IMetricsBus.Withdraw"/>), so
/// the page and dashboard say "not available" instead of showing the last number; its next value publishes it again.
/// </summary>
public sealed class SensorPoller
{
    /// <summary>Failed polls in a row after which the readings count as not available.</summary>
    public const int FailureLimit = 5;

    private readonly IAcerWmi _wmi;
    private readonly IMetricsBus _bus;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _pollLock = new();
    private readonly object _stateLock = new();
    private readonly HashSet<Sensor> _hasReported = new();
    private readonly Dictionary<Sensor, int> _misses = new();
    private readonly HashSet<Sensor> _withdrawn = new();
    private int _failedPolls;
    private SensorSnapshot _latest = SensorSnapshot.Empty;
    private ReadingsState _state = ReadingsState.Starting;

    /// <summary>Creates the poller.</summary>
    public SensorPoller(IAcerWmi wmi, IMetricsBus bus, ILog log, Func<DateTimeOffset>? now = null)
    {
        _wmi = wmi;
        _bus = bus;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Raised after a poll when the state changed (on the polling thread).</summary>
    public event Action<ReadingsState>? StateChanged;

    /// <summary>Raised after every poll (on the polling thread).</summary>
    public event Action<SensorSnapshot>? Polled;

    /// <summary>The result of the last poll.</summary>
    public SensorSnapshot Latest { get { lock (_stateLock) return _latest; } }

    /// <summary>The state of the readings.</summary>
    public ReadingsState State { get { lock (_stateLock) return _state; } }

    /// <summary>Failed polls in a row.</summary>
    public int FailedPolls { get { lock (_stateLock) return _failedPolls; } }

    /// <summary>True when the last poll got answers: the condition for a fan write.</summary>
    public bool WritesAllowed => State == ReadingsState.Available;

    /// <summary>Sensors that have given a value at least once in this session (they exist on this machine).</summary>
    public IReadOnlyCollection<Sensor> Present { get { lock (_stateLock) return _hasReported.ToArray(); } }

    /// <summary>Reads every sensor once, publishes the values, updates the state. Safe to call from any thread; polls are serialised.</summary>
    public SensorSnapshot PollOnce()
    {
        SensorSnapshot snapshot;
        ReadingsState? changed = null;
        lock (_pollLock)
        {
            var values = new Dictionary<Sensor, double?>();
            bool anyAnswer = false;
            bool stalled = false;
            bool firstFailureOfRun;
            lock (_stateLock) firstFailureOfRun = _failedPolls == 0;

            foreach (var info in SensorInfo.All)
            {
                if (stalled) { values[info.Sensor] = null; continue; }
                bool reported;
                lock (_stateLock) reported = _hasReported.Contains(info.Sensor);

                Decoded d;
                try
                {
                    ulong raw = _wmi.ReadSysInfo(info.Index);
                    d = SensorDecoder.Decode(info, raw, reported);
                }
                catch (AcerWmiException ex)
                {
                    d = new Decoded(null, Unavailable.CallFailed, -1);
                    if (ex.Failure == AcerWmiFailure.Timeout) stalled = true;
                    if (firstFailureOfRun) _log.Warn($"Sensor {info.Sensor} (index {info.Index}) could not be read: {ex.Message}");
                }
                catch (Exception ex)
                {
                    d = new Decoded(null, Unavailable.CallFailed, -1);
                    if (firstFailureOfRun) _log.Warn($"Sensor {info.Sensor} (index {info.Index}) failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
                }

                if (d.AnswerOk) anyAnswer = true;
                values[info.Sensor] = d.Value;
                if (d.Value is { } v)
                {
                    lock (_stateLock)
                    {
                        _hasReported.Add(info.Sensor);
                        _misses[info.Sensor] = 0;
                        _withdrawn.Remove(info.Sensor);   // publishing makes the id available again
                    }
                    SafePublish(info, v);
                }
                else CountMiss(info);
            }

            lock (_stateLock)
            {
                snapshot = new SensorSnapshot(_now(), values, anyAnswer);
                _latest = snapshot;
                if (anyAnswer) _failedPolls = 0;
                else _failedPolls++;
                var next = anyAnswer ? ReadingsState.Available
                    : _failedPolls >= FailureLimit ? ReadingsState.Unavailable
                    : ReadingsState.Degraded;
                if (next != _state) { _state = next; changed = next; }
            }
        }

        if (changed is { } state)
        {
            _log.Info($"Readings are now {state} (failed polls in a row: {FailedPolls}).");
            Invoke(() => StateChanged?.Invoke(state));
        }
        Invoke(() => Polled?.Invoke(snapshot));
        return snapshot;
    }

    /// <summary>One more poll without a value for this sensor; at <see cref="FailureLimit"/> in a row a sensor that had values is withdrawn.</summary>
    private void CountMiss(SensorInfo info)
    {
        bool withdraw;
        lock (_stateLock)
        {
            int misses = _misses.TryGetValue(info.Sensor, out var m) ? m + 1 : 1;
            _misses[info.Sensor] = misses;
            withdraw = misses >= FailureLimit && _hasReported.Contains(info.Sensor) && _withdrawn.Add(info.Sensor);
        }
        if (!withdraw) return;
        try { _bus.Withdraw(info.Metric); }
        catch (Exception ex) { _log.Warn("Withdrawing " + info.Metric + " failed: " + ex.Message); }
    }

    private void SafePublish(SensorInfo info, double value)
    {
        try { _bus.Publish(info.Metric, value, info.Unit); }
        catch (Exception ex) { _log.Warn("Publishing " + info.Metric + " failed: " + ex.Message); }
    }

    private void Invoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { _log.Warn("A readings handler threw: " + ex.Message); }
    }
}
