using Rigbay.Abstractions;
using Rigbay.PredatorSense.Fan;
using Rigbay.PredatorSense.Sensors;
using Rigbay.PredatorSense.Wmi;
using Xunit;

namespace Rigbay.PredatorSense.Tests;

public class PollerTests
{
    private static (SensorPoller Poller, FakeAcerWmi Wmi, FakeMetrics Bus) Make()
    {
        var wmi = new FakeAcerWmi();
        var bus = new FakeMetrics();
        return (new SensorPoller(new GuardedAcerWmi(wmi), bus, new FakeLog()), wmi, bus);
    }

    [Fact]
    public void A_poll_reads_exactly_the_six_approved_indexes_and_publishes_them()
    {
        var (poller, wmi, bus) = Make();
        var snap = poller.PollOnce();

        Assert.Equal(new[] { 1, 3, 10, 2, 6, 9 }, wmi.Reads.ToArray());
        var published = bus.Snapshot().ToDictionary(p => p.Id.Value, p => (p.Value, p.Unit));
        Assert.Equal((61.0, MetricUnit.Celsius), published["cpu.temp"]);
        Assert.Equal((52.0, MetricUnit.Celsius), published["gpu.temp"]);
        Assert.Equal((44.0, MetricUnit.Celsius), published["system.temp"]);
        Assert.Equal((2564.0, MetricUnit.Rpm), published["fan.cpu.rpm"]);
        Assert.Equal((2654.0, MetricUnit.Rpm), published["fan.gpu1.rpm"]);
        Assert.Equal((2663.0, MetricUnit.Rpm), published["fan.gpu2.rpm"]);
        Assert.Equal(ReadingsState.Available, poller.State);
        Assert.Equal(61, snap[Sensor.CpuTemp]);
        Assert.Equal(0, wmi.WriteCount);
    }

    [Fact]
    public void An_absent_part_is_not_published_and_not_a_failure()
    {
        var (poller, wmi, bus) = Make();
        wmi.Sensors[9] = FakeAcerWmi.Raw(0);   // no second GPU fan
        wmi.Sensors[3] = FakeAcerWmi.Raw(0);   // no system sensor
        var snap = poller.PollOnce();
        Assert.Null(snap[Sensor.GpuFan2]);
        Assert.Null(snap[Sensor.SystemTemp]);
        Assert.DoesNotContain(bus.Snapshot(), p => p.Id.Value is "fan.gpu2.rpm" or "system.temp");
        Assert.Equal(ReadingsState.Available, poller.State);
        Assert.DoesNotContain(Sensor.GpuFan2, poller.Present);
    }

    [Fact]
    public void A_non_zero_status_makes_that_sensor_not_available_and_nothing_crashes()
    {
        var (poller, wmi, bus) = Make();
        wmi.Sensors[1] = FakeAcerWmi.Raw(61, status: 0xE1);
        var snap = poller.PollOnce();
        Assert.Null(snap[Sensor.CpuTemp]);
        Assert.DoesNotContain(bus.Snapshot(), p => p.Id.Value == "cpu.temp");
        Assert.NotNull(snap[Sensor.CpuFan]);
        Assert.Equal(ReadingsState.Available, poller.State);
    }

    [Fact]
    public void A_fan_that_stops_after_reporting_is_a_real_zero()
    {
        var (poller, wmi, bus) = Make();
        poller.PollOnce();
        wmi.Sensors[2] = FakeAcerWmi.Raw(0);
        var snap = poller.PollOnce();
        Assert.Equal(0, snap[Sensor.CpuFan]);
        Assert.Contains(bus.Snapshot(), p => p.Id.Value == "fan.cpu.rpm" && p.Value == 0);
    }

    [Fact]
    public void Five_failed_polls_in_a_row_make_the_readings_unavailable_and_one_success_brings_them_back()
    {
        var (poller, wmi, _) = Make();
        poller.PollOnce();
        var states = new List<ReadingsState>();
        poller.StateChanged += states.Add;

        wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "boom");
        for (int i = 1; i <= 4; i++)
        {
            poller.PollOnce();
            Assert.Equal(ReadingsState.Degraded, poller.State);
            Assert.False(poller.WritesAllowed);
        }
        poller.PollOnce();
        Assert.Equal(ReadingsState.Unavailable, poller.State);
        Assert.Equal(5, poller.FailedPolls);

        wmi.ReadThrows = null;
        poller.PollOnce();
        Assert.Equal(ReadingsState.Available, poller.State);
        Assert.True(poller.WritesAllowed);
        Assert.Equal(new[] { ReadingsState.Degraded, ReadingsState.Unavailable, ReadingsState.Available }, states);
    }

    [Fact]
    public void A_timeout_ends_the_poll_at_once_because_the_line_is_held()
    {
        var (poller, wmi, _) = Make();
        wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Timeout, "stalled");
        poller.PollOnce();
        Assert.Single(wmi.Reads);
        Assert.Equal(ReadingsState.Degraded, poller.State);
    }

    [Fact]
    public void Any_exception_type_from_the_layer_below_is_survived()
    {
        var (poller, wmi, _) = Make();
        wmi.ReadThrows = new InvalidOperationException("surprise");
        var snap = poller.PollOnce();
        Assert.False(snap.AnyAnswer);
        Assert.Equal(ReadingsState.Degraded, poller.State);
    }

    [Fact]
    public void Scheduler_polls_repeatedly_and_stops()
    {
        var (poller, wmi, _) = Make();
        using var scheduler = new PollScheduler(poller, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10), _ => { });
        scheduler.Start(TimeSpan.Zero);
        Assert.True(Wait.Until(() => wmi.Reads.Count >= 18));   // at least three polls
        scheduler.Stop();
        Thread.Sleep(80);
        int after = wmi.Reads.Count;
        Thread.Sleep(120);
        Assert.Equal(after, wmi.Reads.Count);
    }

    [Fact]
    public void Scheduler_uses_the_slow_interval_when_nobody_watches()
    {
        var (poller, _, _) = Make();
        using var scheduler = new PollScheduler(poller, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), _ => { });
        Assert.Equal(TimeSpan.FromSeconds(2), scheduler.CurrentInterval);
        scheduler.Watched = false;
        Assert.Equal(TimeSpan.FromSeconds(10), scheduler.CurrentInterval);
    }
}

public class FanControllerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 18, 42, 0, TimeSpan.FromHours(7));

    private sealed class Rig
    {
        public readonly FakeAcerWmi Wmi = new();
        public readonly SensorPoller Poller;
        public readonly FanController Fan;
        public readonly List<string> Log = new();

        public Rig(LastSet? initial = null)
        {
            var guarded = new GuardedAcerWmi(Wmi);
            Poller = new SensorPoller(guarded, new FakeMetrics(), new FakeLog());
            Fan = new FanController(guarded, Poller, Log.Add, () => At, (_, _) => Task.CompletedTask, initial);
            Poller.PollOnce();
            // What the real fans do (B5): Max about doubles the speed, Auto brings it back.
            Wmi.OnWrite = v =>
            {
                if (v == WmiAllowList.FanMax) Wmi.SetFans(5555, 5882, 5900);
                else if (v == WmiAllowList.FanAuto) Wmi.SetFans(2654, 2631, 2757);
            };
        }
    }

    [Fact]
    public async Task Max_sends_exactly_the_max_value_once_and_sees_the_speeds_rise()
    {
        var rig = new Rig();
        var r = await rig.Fan.ApplyAsync(FanMode.Max);
        Assert.Equal(FanOutcome.Applied, r.Outcome);
        Assert.Equal(RpmVerdict.Rose, r.Verdict);
        Assert.Equal(new[] { 0x02820019UL }, rig.Wmi.Writes.Select(w => w.Value).ToArray());
        Assert.Equal(FanMode.Max, rig.Fan.Last!.Mode);
        Assert.Equal(At, rig.Fan.Last.At);
        Assert.True(rig.Fan.MaxIsSet);
    }

    [Fact]
    public async Task Max_when_nothing_rises_says_so_and_still_remembers_what_was_sent()
    {
        var rig = new Rig();
        rig.Wmi.OnWrite = null;   // PredatorSense overrode it, or the fans were already at the limit
        var r = await rig.Fan.ApplyAsync(FanMode.Max);
        Assert.Equal(FanOutcome.Applied, r.Outcome);
        Assert.Equal(RpmVerdict.NotRose, r.Verdict);
        Assert.True(rig.Fan.MaxIsSet);
    }

    [Fact]
    public async Task Auto_after_max_sends_the_auto_value_and_sees_the_speeds_fall()
    {
        var rig = new Rig();
        await rig.Fan.ApplyAsync(FanMode.Max);
        var r = await rig.Fan.ApplyAsync(FanMode.Auto);
        Assert.Equal(RpmVerdict.Fell, r.Verdict);
        Assert.Equal(new[] { 0x02820019UL, 0x01410019UL }, rig.Wmi.Writes.Select(w => w.Value).ToArray());
        Assert.False(rig.Fan.MaxIsSet);
        Assert.Equal(FanMode.Auto, rig.Fan.Last!.Mode);
    }

    [Fact]
    public async Task Auto_when_fans_were_already_auto_is_steady()
    {
        var rig = new Rig();
        var r = await rig.Fan.ApplyAsync(FanMode.Auto);
        Assert.Equal(RpmVerdict.Steady, r.Verdict);
        Assert.Single(rig.Wmi.Writes);
    }

    [Fact]
    public async Task The_check_runs_for_ten_seconds_in_steps_of_two_when_nothing_moves()
    {
        var rig = new Rig();
        var waits = new List<TimeSpan>();
        var fan = new FanController(new GuardedAcerWmi(rig.Wmi), rig.Poller, _ => { }, () => At, (t, _) => { waits.Add(t); return Task.CompletedTask; });
        await fan.ApplyAsync(FanMode.Auto);
        Assert.Equal(5, waits.Count);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(2), w));
    }

    [Fact]
    public async Task A_rise_ends_the_check_early()
    {
        var rig = new Rig();
        int waits = 0;
        var fan = new FanController(new GuardedAcerWmi(rig.Wmi), rig.Poller, _ => { }, () => At, (_, _) => { waits++; return Task.CompletedTask; });
        await fan.ApplyAsync(FanMode.Max);
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task Jitter_of_one_step_is_not_a_move()
    {
        var rig = new Rig();
        rig.Wmi.OnWrite = _ => rig.Wmi.SetFans(2586, 2678, 2663);   // the B4 jitter
        var r = await rig.Fan.ApplyAsync(FanMode.Auto);
        Assert.Equal(RpmVerdict.Steady, r.Verdict);
    }

    [Fact]
    public async Task A_non_zero_status_is_a_refusal_and_changes_nothing_remembered()
    {
        var rig = new Rig();
        rig.Wmi.WriteAnswer = 0x05;
        var r = await rig.Fan.ApplyAsync(FanMode.Max);
        Assert.Equal(FanOutcome.StatusRefused, r.Outcome);
        Assert.Equal(5, r.Status);
        Assert.Null(rig.Fan.Last);
        Assert.Single(rig.Wmi.Writes);   // no retry
    }

    [Fact]
    public async Task A_timeout_on_a_max_write_is_remembered_as_possibly_max_so_it_gets_restored()
    {
        var rig = new Rig();
        rig.Wmi.WriteThrows = new AcerWmiException(AcerWmiFailure.Timeout, "stalled");
        var r = await rig.Fan.ApplyAsync(FanMode.Max);
        Assert.Equal(FanOutcome.Unknown, r.Outcome);
        Assert.True(rig.Fan.Last!.Uncertain);
        Assert.True(rig.Fan.MaxIsSet);
        Assert.Single(rig.Wmi.Writes);   // no retry
    }

    [Fact]
    public async Task Never_writes_max_while_the_readings_are_not_available()
    {
        var rig = new Rig();
        rig.Wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "gone");
        rig.Poller.PollOnce();   // degraded after one failed poll: writes are already off
        var r = await rig.Fan.ApplyAsync(FanMode.Max);
        Assert.Equal(FanOutcome.RefusedNoReadings, r.Outcome);
        for (int i = 0; i < 5; i++) rig.Poller.PollOnce();
        Assert.Equal(ReadingsState.Unavailable, rig.Poller.State);
        Assert.Equal(FanOutcome.RefusedNoReadings, (await rig.Fan.ApplyAsync(FanMode.Max)).Outcome);
        Assert.Equal(FanOutcome.RefusedNoReadings, (await rig.Fan.ApplyAsync(FanMode.Auto)).Outcome);   // nothing set by Rigbay: nothing to give back
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task Auto_is_still_allowed_when_rigbay_set_max_and_the_readings_went_away()
    {
        var rig = new Rig();
        await rig.Fan.ApplyAsync(FanMode.Max);
        rig.Wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "gone");
        for (int i = 0; i < 5; i++) rig.Poller.PollOnce();
        Assert.False(rig.Fan.CanWrite(FanMode.Max));
        Assert.True(rig.Fan.CanWrite(FanMode.Auto));
        var r = await rig.Fan.ApplyAsync(FanMode.Auto);
        Assert.Equal(FanOutcome.Applied, r.Outcome);
        Assert.Equal(0x01410019UL, rig.Wmi.Writes.Last().Value);
    }

    [Fact]
    public async Task A_second_write_while_one_runs_is_refused()
    {
        var rig = new Rig();
        Task<FanResult>? inner = null;
        rig.Wmi.InsideWrite = () => inner = rig.Fan.ApplyAsync(FanMode.Auto);
        var first = await rig.Fan.ApplyAsync(FanMode.Max);
        Assert.Equal(FanOutcome.Applied, first.Outcome);
        Assert.Equal(FanOutcome.RefusedBusy, (await inner!).Outcome);
        Assert.Single(rig.Wmi.Writes);
    }

    [Fact]
    public void RestoreAuto_writes_auto_only_when_rigbay_set_max()
    {
        var none = new Rig();
        Assert.True(none.Fan.RestoreAuto(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, none.Wmi.WriteCount);

        var autoSet = new Rig(new LastSet(FanMode.Auto, At, false));
        Assert.True(autoSet.Fan.RestoreAuto(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, autoSet.Wmi.WriteCount);

        var maxSet = new Rig(new LastSet(FanMode.Max, At, false));
        Assert.True(maxSet.Fan.RestoreAuto(TimeSpan.FromSeconds(1)));
        Assert.Equal(new[] { 0x01410019UL }, maxSet.Wmi.Writes.Select(w => w.Value).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(1), maxSet.Wmi.Writes[0].Timeout);   // a short timeout: the host allows 5 s in total
        Assert.False(maxSet.Fan.MaxIsSet);
    }

    [Fact]
    public void RestoreAuto_that_fails_keeps_max_remembered()
    {
        var rig = new Rig(new LastSet(FanMode.Max, At, true));
        rig.Wmi.WriteThrows = new AcerWmiException(AcerWmiFailure.Timeout, "stalled");
        Assert.False(rig.Fan.RestoreAuto(TimeSpan.FromSeconds(1)));
        Assert.True(rig.Fan.MaxIsSet);
    }
}

public class PollerWithdrawTests
{
    private static (SensorPoller Poller, FakeAcerWmi Wmi, FakeMetrics Bus) Make()
    {
        var wmi = new FakeAcerWmi();
        var bus = new FakeMetrics();
        return (new SensorPoller(new GuardedAcerWmi(wmi), bus, new FakeLog()), wmi, bus);
    }

    [Fact]
    public void Four_misses_keep_the_last_value_and_the_fifth_withdraws_every_sensor()
    {
        var (poller, wmi, bus) = Make();
        poller.PollOnce();
        wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "boom");
        for (int i = 0; i < SensorPoller.FailureLimit - 1; i++) poller.PollOnce();
        Assert.Empty(bus.Withdrawn);
        Assert.True(bus.TryGetLatest(new MetricId("cpu.temp"), out _));

        poller.PollOnce();
        Assert.Equal(6, bus.Withdrawn.Count);
        Assert.False(bus.TryGetLatest(new MetricId("cpu.temp"), out _));

        poller.PollOnce();   // a sixth miss does not withdraw again
        Assert.Equal(6, bus.Withdrawn.Count);
    }

    [Fact]
    public void Readings_that_come_back_are_published_again()
    {
        var (poller, wmi, bus) = Make();
        poller.PollOnce();
        wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "boom");
        for (int i = 0; i < SensorPoller.FailureLimit; i++) poller.PollOnce();
        Assert.False(bus.TryGetLatest(new MetricId("fan.cpu.rpm"), out _));

        wmi.ReadThrows = null;
        poller.PollOnce();
        Assert.True(bus.TryGetLatest(new MetricId("fan.cpu.rpm"), out var s));
        Assert.Equal(2564, s.Value);

        // and a new run of failures withdraws them again (the counters started over)
        wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "boom");
        for (int i = 0; i < SensorPoller.FailureLimit - 1; i++) poller.PollOnce();
        Assert.Equal(6, bus.Withdrawn.Count);
        poller.PollOnce();
        Assert.Equal(12, bus.Withdrawn.Count);
    }

    [Fact]
    public void One_sensor_that_stops_answering_is_withdrawn_alone()
    {
        var (poller, wmi, bus) = Make();
        poller.PollOnce();
        wmi.Sensors[9] = FakeAcerWmi.Raw(0, status: 5);   // GPU fan 2 refuses
        for (int i = 0; i < SensorPoller.FailureLimit; i++) poller.PollOnce();
        Assert.Equal(new[] { "fan.gpu2.rpm" }, bus.Withdrawn.Select(m => m.Value).ToArray());
        Assert.Equal(ReadingsState.Available, poller.State);
        Assert.True(bus.TryGetLatest(new MetricId("cpu.temp"), out _));
    }

    [Fact]
    public void A_part_the_machine_does_not_have_is_never_withdrawn()
    {
        var (poller, wmi, bus) = Make();
        wmi.Sensors[3] = FakeAcerWmi.Raw(0);   // no system temperature: status 0, value 0
        for (int i = 0; i < SensorPoller.FailureLimit + 2; i++) poller.PollOnce();
        Assert.Empty(bus.Withdrawn);
    }

    [Fact]
    public void Scheduler_PollSoon_reads_now_even_with_the_slow_interval_and_does_nothing_when_stopped()
    {
        var (poller, wmi, _) = Make();
        using var scheduler = new PollScheduler(poller, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), _ => { });
        scheduler.Watched = false;
        scheduler.PollSoon();   // not started
        Thread.Sleep(50);
        Assert.Empty(wmi.Reads);

        scheduler.Start(TimeSpan.FromSeconds(30));
        scheduler.PollSoon();
        Assert.True(Wait.Until(() => wmi.Reads.Count >= 6, 3000));
        scheduler.Stop();
        scheduler.PollSoon();
        int after = wmi.Reads.Count;
        Thread.Sleep(80);
        Assert.Equal(after, wmi.Reads.Count);
    }
}
