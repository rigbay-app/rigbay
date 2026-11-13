namespace Rigbay.App.Services;

/// <summary>
/// When to give memory back after the window went to the tray: <see cref="Delay"/> after it was hidden, once per hide, and not at all when the
/// window comes back first. No UI types and no timer of its own, so a test drives it with a fake <see cref="TimeProvider"/>; the window asks
/// <see cref="Remaining"/> for how long to wait, arms one dispatcher timer for that long, and calls <see cref="TryTake"/> when it fires.
/// <para>The class is compiled into the app and, as a linked source file, into <c>Rigbay.Core.Tests</c>. Keep it free of WinUI.</para>
/// </summary>
sealed class TrayTrimPolicy
{
    /// <summary>Shortest and longest delay accepted (a settings file edited by hand, or a test switch, cannot make the trim run in a loop or never).</summary>
    public static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromHours(1);

    /// <summary>The delay the setting starts with.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(60);

    readonly TimeProvider _clock;
    TimeSpan _delay = DefaultDelay;
    long _hiddenAt;

    public TrayTrimPolicy(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>The setting "Release memory when Rigbay is in the tray". Off: <see cref="Remaining"/> is null and <see cref="TryTake"/> false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the window has to stay hidden before the trim; kept between <see cref="MinDelay"/> and <see cref="MaxDelay"/>.</summary>
    public TimeSpan Delay
    {
        get => _delay;
        set => _delay = value < MinDelay ? MinDelay : value > MaxDelay ? MaxDelay : value;
    }

    /// <summary>True from <see cref="Hidden"/> to <see cref="Shown"/>.</summary>
    public bool IsHidden { get; private set; }

    /// <summary>True once <see cref="TryTake"/> has said yes for the current hide (so the next hide trims again).</summary>
    public bool Taken { get; private set; }

    /// <summary>How many times <see cref="TryTake"/> said yes since the policy was made.</summary>
    public int Trims { get; private set; }

    /// <summary>The window went to the tray. A second call while it is already hidden changes nothing (the delay counts from the first).</summary>
    public void Hidden()
    {
        if (IsHidden) return;
        IsHidden = true;
        Taken = false;
        _hiddenAt = _clock.GetTimestamp();
    }

    /// <summary>The window is on screen again: whatever was waiting is cancelled.</summary>
    public void Shown()
    {
        IsHidden = false;
        Taken = false;
    }

    /// <summary>Time until the trim is due: null when nothing is waiting (shown, switched off, or already done for this hide); zero when it is due now.</summary>
    public TimeSpan? Remaining()
    {
        if (!Enabled || !IsHidden || Taken) return null;
        var left = _delay - _clock.GetElapsedTime(_hiddenAt);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>True exactly once per hide, when the delay has passed; the caller then trims. Early calls and calls after the first yes return false.</summary>
    public bool TryTake()
    {
        if (Remaining() is not { } left || left > TimeSpan.Zero) return false;
        Taken = true;
        Trims++;
        return true;
    }
}

/// <summary>The steps of a trim, in the order they run. <see cref="All"/> is what the app does; the test switch <c>--trim-steps</c> picks single ones so each can be measured.</summary>
[Flags]
enum TrimSteps
{
    None = 0,
    /// <summary>Take the open page off the window, dispose its scope, forget the statics that point into it.</summary>
    DropPages = 1,
    /// <summary>Clear the boxes that hold UI objects (search results, the last-built page blocks).</summary>
    Caches = 2,
    /// <summary>Collect and compact the heap once (large object heap included).</summary>
    Collect = 4,
    /// <summary>Trim the working set once.</summary>
    WorkingSet = 8,
    All = DropPages | Caches | Collect | WorkingSet,
    /// <summary>What the app runs: only the working set trim. Measured on 2026-11-13 (docs/perf/TRAY-MEMORY-2026-11.md): it takes the process from about 205 MB to 30-45 MB; dropping the page, clearing the caches and collecting the heap moved neither the working set nor the private bytes by more than the noise, and dropping the page made the next show slower (about 130 ms against 25 ms to the first frame).</summary>
    Default = WorkingSet,
}

static class TrimStepsText
{
    /// <summary>"all", "none", or a list of drop, caches, gc, ws separated by comma or plus; null when a word is not one of them.</summary>
    public static TrimSteps? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var result = TrimSteps.None;
        foreach (var word in text.Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (word.ToLowerInvariant())
            {
                case "all": result |= TrimSteps.All; break;
                case "none": break;
                case "drop": result |= TrimSteps.DropPages; break;
                case "caches": result |= TrimSteps.Caches; break;
                case "gc": result |= TrimSteps.Collect; break;
                case "ws": result |= TrimSteps.WorkingSet; break;
                default: return null;
            }
        }
        return result;
    }
}
