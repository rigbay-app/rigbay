using Rigbay.App.Services;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>
/// The memory trim policy of the tray (src/Rigbay.App/Services/TrayTrimPolicy.cs, linked into this project): the delay counts from the hide, a
/// window that reopens first cancels it, it fires once per hide, and the setting turns it off. A fake clock, so no test waits.
/// </summary>
public class TrayTrimPolicyTests
{
    sealed class FakeClock : TimeProvider
    {
        long _ticks;   // in Stopwatch ticks of TimestampFrequency
        public override long TimestampFrequency => 1000;   // one tick = 1 ms
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += (long)by.TotalMilliseconds;
    }

    static (TrayTrimPolicy, FakeClock) Make(int delaySeconds = 60)
    {
        var clock = new FakeClock();
        return (new TrayTrimPolicy(clock) { Delay = TimeSpan.FromSeconds(delaySeconds) }, clock);
    }

    [Fact]
    public void Nothing_waits_while_the_window_is_shown()
    {
        var (p, clock) = Make();
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(p.Remaining());
        Assert.False(p.TryTake());
    }

    [Fact]
    public void The_trim_is_due_after_the_delay_and_not_before()
    {
        var (p, clock) = Make(60);
        p.Hidden();
        Assert.Equal(TimeSpan.FromSeconds(60), p.Remaining());
        clock.Advance(TimeSpan.FromSeconds(59.9));
        Assert.False(p.TryTake());
        Assert.True(p.Remaining() > TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(TimeSpan.Zero, p.Remaining());
        Assert.True(p.TryTake());
        Assert.Equal(1, p.Trims);
    }

    [Fact]
    public void It_fires_once_per_hide()
    {
        var (p, clock) = Make(60);
        p.Hidden();
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(p.TryTake());
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.False(p.TryTake());
        Assert.Null(p.Remaining());
        Assert.Equal(1, p.Trims);
    }

    [Fact]
    public void A_window_that_reopens_before_the_delay_cancels_the_trim()
    {
        var (p, clock) = Make(60);
        p.Hidden();
        clock.Advance(TimeSpan.FromSeconds(30));
        p.Shown();
        Assert.Null(p.Remaining());
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(p.TryTake());
        Assert.Equal(0, p.Trims);
    }

    [Fact]
    public void The_next_hide_counts_from_its_own_start_and_trims_again()
    {
        var (p, clock) = Make(60);
        p.Hidden();
        clock.Advance(TimeSpan.FromSeconds(30));
        p.Shown();
        clock.Advance(TimeSpan.FromSeconds(100));
        p.Hidden();                                   // 30 s of the first hide must not count towards this one
        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.False(p.TryTake());
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(p.TryTake());
        p.Shown();
        p.Hidden();
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(p.TryTake());
        Assert.Equal(2, p.Trims);
    }

    [Fact]
    public void A_second_hide_call_does_not_restart_the_wait()
    {
        var (p, clock) = Make(60);
        p.Hidden();
        clock.Advance(TimeSpan.FromSeconds(50));
        p.Hidden();                                   // e.g. the close button pressed on a window that is already hidden
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(p.TryTake());
    }

    [Fact]
    public void The_setting_off_means_no_trim()
    {
        var (p, clock) = Make(60);
        p.Enabled = false;
        p.Hidden();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(p.Remaining());
        Assert.False(p.TryTake());
        p.Enabled = true;                             // switched on while hidden: the delay counted from the hide, so it is due now
        Assert.True(p.TryTake());
    }

    [Fact]
    public void A_longer_delay_chosen_while_hidden_moves_the_due_time()
    {
        var (p, clock) = Make(60);
        p.Hidden();
        clock.Advance(TimeSpan.FromSeconds(50));
        p.Delay = TimeSpan.FromSeconds(120);
        Assert.Equal(TimeSpan.FromSeconds(70), p.Remaining());
        Assert.False(p.TryTake());
        clock.Advance(TimeSpan.FromSeconds(70));
        Assert.True(p.TryTake());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(45, 45)]
    [InlineData(999999, 3600)]
    public void The_delay_stays_inside_its_limits(int seconds, int expected)
    {
        var (p, _) = Make();
        p.Delay = TimeSpan.FromSeconds(seconds);
        Assert.Equal(TimeSpan.FromSeconds(expected), p.Delay);
    }

    // TrimSteps is internal to the app, so the expected value is its number: DropPages 1, Caches 2, Collect 4, WorkingSet 8
    [Theory]
    [InlineData("all", 15)]
    [InlineData("none", 0)]
    [InlineData("drop", 1)]
    [InlineData("drop+gc", 5)]
    [InlineData("drop,caches,gc,ws", 15)]
    [InlineData("WS", 8)]
    public void The_trim_steps_switch_reads_its_words(string text, int expected) => Assert.Equal((TrimSteps)expected, TrimStepsText.Parse(text));

    [Fact]
    public void The_default_trim_is_the_working_set_step_only() => Assert.Equal(TrimSteps.WorkingSet, TrimSteps.Default);

    [Theory]
    [InlineData("")]
    [InlineData("drop,bogus")]
    public void An_unknown_word_in_the_trim_steps_switch_is_refused(string text) => Assert.Null(TrimStepsText.Parse(text));
}
