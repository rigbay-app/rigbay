namespace Rigbay.Recorder.Recording;

/// <summary>
/// The row schedule: slots at <c>first + n * period</c>, so the rows do not drift however late the thread wakes up
/// (<c>next += period</c>, never <c>next = now + period</c>). If the thread is more than one period late (the laptop slept, the
/// process was suspended) the slot is reset to the present instead of writing a burst of rows to catch up.
/// </summary>
internal sealed class FixedSchedule
{
    private readonly TimeSpan _period;

    /// <summary>Creates the schedule; the first slot is <paramref name="first"/>.</summary>
    public FixedSchedule(DateTimeOffset first, TimeSpan period)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        Next = first;
    }

    /// <summary>The time of the next slot.</summary>
    public DateTimeOffset Next { get; private set; }

    /// <summary>
    /// Takes the slot that is due at <paramref name="now"/> and moves to the next one. Returns the time to stamp the row with: the
    /// slot itself, or <paramref name="now"/> when the slot was missed by more than one period.
    /// </summary>
    public DateTimeOffset Take(DateTimeOffset now)
    {
        var slot = now - Next > _period ? now : Next;
        Next = slot + _period;
        return slot;
    }
}
