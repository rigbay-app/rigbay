using Rigbay.PredatorSense.Sensors;
using Rigbay.PredatorSense.Wmi;

namespace Rigbay.PredatorSense.Fan;

/// <summary>The two fan modes the module can set.</summary>
public enum FanMode
{
    /// <summary>The firmware controls the fans.</summary>
    Auto,
    /// <summary>Every fan at full speed.</summary>
    Max,
}

/// <summary>How a fan change ended.</summary>
public enum FanOutcome
{
    /// <summary>The firmware accepted the write (status 0).</summary>
    Applied,
    /// <summary>Nothing was sent: the readings are not available, so no write is allowed.</summary>
    RefusedNoReadings,
    /// <summary>Nothing was sent: another write is in progress.</summary>
    RefusedBusy,
    /// <summary>Nothing was sent: the allow-list or the schema check refused the value.</summary>
    RefusedByGuard,
    /// <summary>The write was sent and the firmware answered a non-zero status. The mode is whatever it was before.</summary>
    StatusRefused,
    /// <summary>The write was sent and did not return in time or raised an error. The mode is unknown.</summary>
    Unknown,
}

/// <summary>What the speed readings after a write say. A reading, never proof of the mode.</summary>
public enum RpmVerdict
{
    /// <summary>No check was made (the write did not happen or the readings were not there).</summary>
    NotChecked,
    /// <summary>After Maximum: a fan is faster than before.</summary>
    Rose,
    /// <summary>After Maximum: no fan got faster within the window.</summary>
    NotRose,
    /// <summary>After Auto that followed Maximum: the fans slowed down or are back near their old speed.</summary>
    Fell,
    /// <summary>After Auto that followed Maximum: the fans are still fast at the end of the window.</summary>
    NotFell,
    /// <summary>After Auto with no Maximum before: the speeds stayed where they were (the expected result).</summary>
    Steady,
    /// <summary>After Auto with no Maximum before: a fan changed speed by hundreds of rpm.</summary>
    Moved,
}

/// <summary>What the module remembers about its own last fan write.</summary>
/// <param name="Mode">The mode of the last write the firmware accepted (or may have accepted when <paramref name="Uncertain"/>).</param>
/// <param name="At">When it was sent.</param>
/// <param name="Uncertain">True when the write did not return in time: it may have taken effect.</param>
public sealed record LastSet(FanMode Mode, DateTimeOffset At, bool Uncertain);

/// <summary>The result of <see cref="FanController.ApplyAsync"/>.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Status">The firmware status byte, or -1 when there was no answer.</param>
/// <param name="Verdict">What the readings after the write said.</param>
/// <param name="Before">Fan speeds just before the write, by sensor.</param>
/// <param name="After">Fan speeds at the end of the check, by sensor.</param>
/// <param name="Message">English detail for the log (the user sees localised text built from the other fields).</param>
public sealed record FanResult(
    FanOutcome Outcome, int Status, RpmVerdict Verdict,
    IReadOnlyDictionary<Sensor, double?> Before, IReadOnlyDictionary<Sensor, double?> After, string Message)
{
    /// <summary>True when the firmware accepted the write.</summary>
    public bool Applied => Outcome == FanOutcome.Applied;
}

/// <summary>
/// The only place that writes to the fan. One write at a time; never while the readings are not available (the exception is
/// putting the fans back to Auto when Rigbay set them to Maximum - the safe direction); the write goes through
/// <see cref="IAcerWmi"/>, which has its own allow-list. After the write it watches the fan speeds for
/// <see cref="CheckWindow"/> and reports whether they rose (Maximum) or came back (Auto). It does not ask the user: the
/// callers (the page row through the host, the hotkey and the verb through the approval service) do that first.
/// </summary>
public sealed class FanController
{
    /// <summary>How long the speeds are watched after a write.</summary>
    public static readonly TimeSpan CheckWindow = TimeSpan.FromSeconds(10);

    /// <summary>How often they are read during that window.</summary>
    public static readonly TimeSpan CheckStep = TimeSpan.FromSeconds(2);

    /// <summary>A change of this many rpm counts as a real move. Auto jitters by one step of about 24 rpm (FINDINGS 13.3).</summary>
    public const double RpmJump = 300;

    private readonly IAcerWmi _wmi;
    private readonly SensorPoller _poller;
    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateLock = new();
    private LastSet? _last;
    private Dictionary<Sensor, double?>? _autoBaseline;

    /// <summary>Creates the controller.</summary>
    /// <param name="wmi">Where writes go.</param>
    /// <param name="poller">Source of the fan speeds and of the "readings are available" rule.</param>
    /// <param name="log">English log lines.</param>
    /// <param name="now">Clock (tests replace it).</param>
    /// <param name="delay">Waits between the checks (tests replace it).</param>
    /// <param name="initial">What an earlier run of the module knew about its last write, or null.</param>
    public FanController(IAcerWmi wmi, SensorPoller poller, Action<string> log, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null, LastSet? initial = null)
    {
        _wmi = wmi;
        _poller = poller;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
        _delay = delay ?? Task.Delay;
        _last = initial;
    }

    /// <summary>Raised after a write attempt changed what Rigbay knows (on the writing thread).</summary>
    public event Action? Changed;

    /// <summary>Raised with a progress state while the speeds are watched (on the writing thread).</summary>
    public event Action<FanMode>? Checking;

    /// <summary>The module's own record of its last write, or null when it has made none. The firmware cannot be asked.</summary>
    public LastSet? Last { get { lock (_stateLock) return _last; } }

    /// <summary>True when Rigbay set Maximum and has not set Auto since (also when that write may have taken effect).</summary>
    public bool MaxIsSet => Last is { Mode: FanMode.Max };

    /// <summary>True when a write is running.</summary>
    public bool Busy => _writeGate.CurrentCount == 0;

    /// <summary>Whether a write to <paramref name="mode"/> would be allowed right now (the readings rule).</summary>
    public bool CanWrite(FanMode mode) => _poller.WritesAllowed || (mode == FanMode.Auto && MaxIsSet);

    /// <summary>
    /// Sets the fan mode and watches the speeds. The caller has the user's approval already. Never throws for an expected
    /// failure; the result says what happened.
    /// </summary>
    public async Task<FanResult> ApplyAsync(FanMode mode, CancellationToken cancellationToken = default)
    {
        var none = new Dictionary<Sensor, double?>();
        if (!CanWrite(mode))
        {
            _log($"Fan {mode} refused: the readings are not available ({_poller.State}).");
            return new FanResult(FanOutcome.RefusedNoReadings, -1, RpmVerdict.NotChecked, none, none, "readings not available");
        }
        if (!await _writeGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            _log($"Fan {mode} refused: another write is in progress.");
            return new FanResult(FanOutcome.RefusedBusy, -1, RpmVerdict.NotChecked, none, none, "another write is in progress");
        }
        try
        {
            return await ApplyLockedAsync(mode, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Puts the fans back to Auto when Rigbay had set Maximum (module stop, host exit, fault). Waits at most
    /// <paramref name="wait"/> for a running write. No speed check, no readings rule. Returns true when nothing is left to restore.
    /// </summary>
    public bool RestoreAuto(TimeSpan wait)
    {
        if (!MaxIsSet) return true;
        if (!_writeGate.Wait(wait)) { _log("Restore to Auto skipped: a write is still running."); return false; }
        try
        {
            if (!MaxIsSet) return true;
            var (outcome, status, _) = Send(FanMode.Auto, wait);
            _log($"Restore to Auto: {outcome} (status {status}).");
            if (outcome == FanOutcome.Applied) { Remember(FanMode.Auto, false); return true; }
            return false;
        }
        finally { _writeGate.Release(); }
    }

    private async Task<FanResult> ApplyLockedAsync(FanMode mode, CancellationToken ct)
    {
        var before = Fans(_poller.Latest);
        bool afterMax = MaxIsSet;
        if (mode == FanMode.Max && !afterMax) lock (_stateLock) _autoBaseline = new Dictionary<Sensor, double?>(before);

        var (outcome, status, message) = Send(mode, null);
        if (outcome == FanOutcome.Applied) Remember(mode, false);
        else if (outcome == FanOutcome.Unknown && mode == FanMode.Max) Remember(mode, true);   // it may have landed: restore on exit
        else if (outcome == FanOutcome.Unknown) Raise();
        _log($"Fan {mode}: {outcome}, status {status}. {message}");

        if (outcome != FanOutcome.Applied)
            return new FanResult(outcome, status, RpmVerdict.NotChecked, before, before, message);

        var (verdict, after) = await WatchAsync(mode, afterMax, before, ct).ConfigureAwait(false);
        _log($"Fan {mode}: speeds after {CheckWindow.TotalSeconds:0} s say {verdict}.");
        return new FanResult(FanOutcome.Applied, status, verdict, before, after, "ok");
    }

    private (FanOutcome Outcome, int Status, string Message) Send(FanMode mode, TimeSpan? timeout)
    {
        ulong value = mode == FanMode.Auto ? WmiAllowList.FanAuto : WmiAllowList.FanMax;
        try
        {
            uint raw = _wmi.SetFanBehavior(value, timeout);
            int status = (int)(raw & 0xFF);
            return status == 0
                ? (FanOutcome.Applied, 0, "accepted")
                : (FanOutcome.StatusRefused, status, $"the firmware answered status {status}");
        }
        catch (AcerWmiException ex) when (ex.Failure == AcerWmiFailure.Refused) { return (FanOutcome.RefusedByGuard, -1, ex.Message); }
        catch (AcerWmiException ex) { return (FanOutcome.Unknown, -1, ex.Message); }
        catch (Exception ex) { return (FanOutcome.Unknown, -1, ex.GetType().Name + ": " + ex.Message); }
    }

    private void Remember(FanMode mode, bool uncertain)
    {
        lock (_stateLock) _last = new LastSet(mode, _now(), uncertain);
        Raise();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { _log("A fan handler threw: " + ex.Message); }
    }

    private async Task<(RpmVerdict, IReadOnlyDictionary<Sensor, double?>)> WatchAsync(FanMode mode, bool afterMax, IReadOnlyDictionary<Sensor, double?> before, CancellationToken ct)
    {
        try { Checking?.Invoke(mode); } catch (Exception ex) { _log("A fan handler threw: " + ex.Message); }
        IReadOnlyDictionary<Sensor, double?> latest = before;
        var verdict = RpmVerdict.NotChecked;
        var waited = TimeSpan.Zero;
        try
        {
            while (waited < CheckWindow)
            {
                await _delay(CheckStep, ct).ConfigureAwait(false);
                waited += CheckStep;
                var snapshot = _poller.PollOnce();
                if (!snapshot.AnyAnswer) continue;
                latest = Fans(snapshot);
                verdict = Judge(mode, afterMax, before, latest);
                if (verdict is RpmVerdict.Rose or RpmVerdict.Fell) break;   // conclusive; the others are judged at the end of the window
            }
        }
        catch (OperationCanceledException) { /* the module is stopping: report what we have */ }
        return (verdict, latest);
    }

    /// <summary>
    /// Compares fan speeds before and after a write. Maximum: Rose when any fan is <see cref="RpmJump"/> faster. Auto after
    /// Maximum: Fell when any fan is <see cref="RpmJump"/> slower than at the write, or every fan is within <see cref="RpmJump"/>
    /// of its speed from before Maximum. Auto without Maximum before: Steady when no fan moved by <see cref="RpmJump"/>.
    /// </summary>
    internal RpmVerdict Judge(FanMode mode, bool afterMax, IReadOnlyDictionary<Sensor, double?> before, IReadOnlyDictionary<Sensor, double?> now)
    {
        var present = SensorInfo.Fans.Select(f => f.Sensor).Where(s => before.TryGetValue(s, out var b) && b is > 0 && now.TryGetValue(s, out var n) && n is not null).ToList();
        if (present.Count == 0) return RpmVerdict.NotChecked;

        if (mode == FanMode.Max)
            return present.Any(s => now[s]!.Value - before[s]!.Value >= RpmJump) ? RpmVerdict.Rose : RpmVerdict.NotRose;

        if (afterMax)
        {
            Dictionary<Sensor, double?>? baseline;
            lock (_stateLock) baseline = _autoBaseline;
            bool fell = present.Any(s => before[s]!.Value - now[s]!.Value >= RpmJump);
            bool back = baseline is not null && present.All(s => baseline.TryGetValue(s, out var b) && b is > 0 && Math.Abs(now[s]!.Value - b.Value) < RpmJump);
            return fell || back ? RpmVerdict.Fell : RpmVerdict.NotFell;
        }

        return present.Any(s => Math.Abs(now[s]!.Value - before[s]!.Value) >= RpmJump) ? RpmVerdict.Moved : RpmVerdict.Steady;
    }

    private static Dictionary<Sensor, double?> Fans(SensorSnapshot snapshot)
    {
        var d = new Dictionary<Sensor, double?>();
        foreach (var f in SensorInfo.Fans) d[f.Sensor] = snapshot[f.Sensor];
        return d;
    }
}
