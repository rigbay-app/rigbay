using Rigbay.Abstractions;
using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu.Tests;

/// <summary>The poller on a fake NVML: every reading, a reading the GPU does not have, errors, losing the session.</summary>
public class PollerTests
{
    private static readonly string[] AllIds =
    {
        "gpu.usage", "gpu.memctl.usage", "gpu.power", "gpu.power.limit", "gpu.clock", "gpu.mem.clock",
        "gpu.mem.used", "gpu.mem.total", "gpu.nvml.temp", "gpu.pstate", "gpu.throttle",
    };

    private sealed class Setup
    {
        public readonly FakeNvml Gpu = new();
        public readonly FakeBus Bus = new();
        public readonly FakeLog Log = new();
        public readonly GpuPoller Poller;
        public DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

        public Setup(bool open = true, Func<FakeNvml, INvml>? wrap = null)
        {
            Poller = new GpuPoller(wrap?.Invoke(Gpu) ?? Gpu, Bus, Log, () => Now);
            if (open) Assert.Equal(NvmlReturn.Success, Poller.Open());
        }
    }

    // ------------------------------------------------------------------ open and close

    [Fact]
    public void Open_initialises_once_reads_the_name_and_close_shuts_down_once()
    {
        var s = new Setup();
        Assert.True(s.Poller.IsOpen);
        Assert.Equal("NVIDIA GeForce RTX 3060 Laptop GPU", s.Poller.DeviceName);
        Assert.Equal(1, s.Gpu.Depth);
        Assert.Equal(NvmlReturn.Success, s.Poller.Open());   // already open: nothing happens
        Assert.Equal(1, s.Gpu.Depth);

        s.Poller.Close();
        s.Poller.Close();
        Assert.False(s.Poller.IsOpen);
        Assert.Equal(0, s.Gpu.Depth);
        Assert.Equal(1, s.Gpu.ShutdownCount);
    }

    [Theory]
    [InlineData("init", (int)NvmlReturn.DriverNotLoaded, 0)]
    [InlineData("count", (int)NvmlReturn.Unknown, 1)]
    [InlineData("handle", (int)NvmlReturn.GpuIsLost, 1)]
    [InlineData("name", (int)NvmlReturn.NotSupported, 1)]
    public void Open_that_fails_says_why_and_leaves_nvml_shut_down(string step, int number, int initDone)
    {
        var code = (NvmlReturn)number;
        var s = new Setup(open: false);
        s.Gpu.Results[step] = code;
        Assert.Equal(code, s.Poller.Open());
        Assert.False(s.Poller.IsOpen);
        Assert.Equal(initDone, s.Gpu.InitCount);
        Assert.Equal(0, s.Gpu.Depth);
    }

    [Fact]
    public void Open_with_no_gpu_is_not_found_and_shuts_down()
    {
        var s = new Setup(open: false);
        s.Gpu.Count = 0;
        Assert.Equal(NvmlReturn.NotFound, s.Poller.Open());
        Assert.Equal(0, s.Gpu.Depth);
    }

    [Fact]
    public void A_poll_before_open_or_after_close_calls_nothing()
    {
        var s = new Setup(open: false);
        s.Poller.PollOnce();
        Assert.Empty(s.Gpu.Calls);
        s.Poller.Open();
        s.Poller.Close();
        int calls = s.Gpu.Calls.Count;
        s.Poller.PollOnce();
        Assert.Equal(calls, s.Gpu.Calls.Count);
    }

    // ------------------------------------------------------------------ every reading

    [Fact]
    public void One_poll_publishes_every_reading_with_its_unit_and_value()
    {
        var s = new Setup();
        var snap = s.Poller.PollOnce();

        Assert.Equal(AllIds.OrderBy(x => x), s.Bus.Published.Distinct().OrderBy(x => x));
        Assert.Equal(31, s.Bus.Value("gpu.usage"));
        Assert.Equal(12, s.Bus.Value("gpu.memctl.usage"));
        Assert.Equal(72.4, s.Bus.Value("gpu.power"), 6);        // milliwatts to watts
        Assert.Equal(80, s.Bus.Value("gpu.power.limit"));
        Assert.Equal(1545, s.Bus.Value("gpu.clock"));
        Assert.Equal(6000, s.Bus.Value("gpu.mem.clock"));
        Assert.Equal(1_500d * 1024 * 1024, s.Bus.Value("gpu.mem.used"));
        Assert.Equal(6d * 1024 * 1024 * 1024, s.Bus.Value("gpu.mem.total"));
        Assert.Equal(64, s.Bus.Value("gpu.nvml.temp"));
        Assert.Equal(0, s.Bus.Value("gpu.pstate"));
        Assert.Equal(4, s.Bus.Value("gpu.throttle"));           // the software power cap bit

        Assert.Equal(MetricUnit.Percent, s.Bus.UnitOf("gpu.usage"));
        Assert.Equal(MetricUnit.Percent, s.Bus.UnitOf("gpu.memctl.usage"));
        Assert.Equal(MetricUnit.Watt, s.Bus.UnitOf("gpu.power"));
        Assert.Equal(MetricUnit.Watt, s.Bus.UnitOf("gpu.power.limit"));
        Assert.Equal(MetricUnit.Megahertz, s.Bus.UnitOf("gpu.clock"));
        Assert.Equal(MetricUnit.Megahertz, s.Bus.UnitOf("gpu.mem.clock"));
        Assert.Equal(MetricUnit.Bytes, s.Bus.UnitOf("gpu.mem.used"));
        Assert.Equal(MetricUnit.Bytes, s.Bus.UnitOf("gpu.mem.total"));
        Assert.Equal(MetricUnit.Celsius, s.Bus.UnitOf("gpu.nvml.temp"));
        Assert.Equal(MetricUnit.None, s.Bus.UnitOf("gpu.pstate"));
        Assert.Equal(MetricUnit.None, s.Bus.UnitOf("gpu.throttle"));

        Assert.True(snap.AnyAnswer);
        Assert.Equal(31, snap.Load);
        Assert.Equal(72.4, snap.PowerWatts!.Value, 6);
        Assert.Equal(0, snap.PerformanceState);
        Assert.Equal(ClockReasons.SoftwarePowerCap, snap.ClockReasons);
        Assert.Equal(s.Now, snap.At);
        Assert.Equal(GpuState.Available, s.Poller.State);
    }

    [Fact]
    public void The_temperature_is_not_the_id_predatorsense_publishes()
    {
        // two sources on one id would alternate in the chart and the recorder; this module keeps its own id
        Assert.NotEqual("gpu.temp", GpuMetrics.Temperature.Id.Value);
        Assert.DoesNotContain(GpuMetrics.All, m => m.Id.Value == "gpu.temp");
        Assert.Contains(GpuMetrics.All, m => m.Id == WellKnownMetrics.GpuUsage);
        Assert.Contains(GpuMetrics.All, m => m.Id == WellKnownMetrics.GpuPower);
        Assert.Contains(GpuMetrics.All, m => m.Id == WellKnownMetrics.GpuClock);
    }

    [Fact]
    public void A_reading_follows_the_gpu_from_poll_to_poll()
    {
        var s = new Setup();
        s.Poller.PollOnce();
        s.Gpu.GpuPercent = 97;
        s.Gpu.PowerMilliwatts = 80_100;
        s.Gpu.Celsius = 78;
        s.Poller.PollOnce();
        Assert.Equal(97, s.Bus.Value("gpu.usage"));
        Assert.Equal(80.1, s.Bus.Value("gpu.power"), 6);
        Assert.Equal(78, s.Bus.Value("gpu.nvml.temp"));
    }

    // ------------------------------------------------------------------ not supported

    [Fact]
    public void A_reading_that_is_not_supported_is_withdrawn_at_once_and_never_published_as_zero()
    {
        var s = new Setup();
        s.Gpu.Results["power"] = NvmlReturn.NotSupported;
        s.Gpu.Results["limit"] = NvmlReturn.NotSupported;
        var snap = s.Poller.PollOnce();

        Assert.DoesNotContain("gpu.power", s.Bus.Published);
        Assert.DoesNotContain("gpu.power.limit", s.Bus.Published);
        Assert.True(s.Bus.WasWithdrawn("gpu.power"));
        Assert.True(s.Bus.WasWithdrawn("gpu.power.limit"));
        Assert.Null(snap.PowerWatts);
        Assert.Null(snap.PowerLimitWatts);
        Assert.True(s.Bus.Has("gpu.usage"));              // the others are there
        Assert.Equal(GpuState.Available, s.Poller.State);  // part of the card not supported is not a failure
    }

    [Fact]
    public void A_reading_that_was_published_and_becomes_not_supported_leaves_the_bus_and_comes_back()
    {
        var s = new Setup();
        s.Poller.PollOnce();
        Assert.True(s.Bus.Has("gpu.power"));

        s.Gpu.Results["power"] = NvmlReturn.NotSupported;   // for example the card went to sleep on a battery
        s.Poller.PollOnce();
        Assert.False(s.Bus.Has("gpu.power"));
        Assert.Contains("gpu.power", s.Bus.Withdrawn);

        s.Gpu.Results.Remove("power");
        s.Poller.PollOnce();
        Assert.True(s.Bus.Has("gpu.power"));
    }

    [Fact]
    public void Not_supported_is_withdrawn_once_not_every_poll()
    {
        var s = new Setup();
        s.Gpu.Results["power"] = NvmlReturn.NotSupported;
        for (int i = 0; i < 6; i++) s.Poller.PollOnce();
        Assert.Equal(1, s.Bus.WithdrawCalls.Count(id => id == "gpu.power"));
    }

    [Fact]
    public void A_performance_state_of_unknown_is_not_a_reading()
    {
        var s = new Setup();
        s.Gpu.PState = 32;   // NVML's "unknown"
        var snap = s.Poller.PollOnce();
        Assert.Null(snap.PerformanceState);
        Assert.DoesNotContain("gpu.pstate", s.Bus.Published);
        Assert.True(s.Bus.WasWithdrawn("gpu.pstate"));
    }

    [Fact]
    public void A_driver_without_the_reasons_call_just_has_no_reasons()
    {
        var s = new Setup();
        s.Gpu.Results["reasons"] = NvmlReturn.NotSupported;
        var snap = s.Poller.PollOnce();
        Assert.Null(snap.ClockReasons);
        Assert.DoesNotContain("gpu.throttle", s.Bus.Published);
        Assert.True(snap.AnyAnswer);
    }

    // ------------------------------------------------------------------ errors

    [Fact]
    public void A_reading_that_fails_other_ways_keeps_its_last_value_for_two_polls_and_is_withdrawn_on_the_third()
    {
        var s = new Setup();
        s.Poller.PollOnce();
        s.Gpu.Results["temp"] = NvmlReturn.Unknown;
        s.Poller.PollOnce();
        Assert.True(s.Bus.Has("gpu.nvml.temp"));
        s.Poller.PollOnce();
        Assert.True(s.Bus.Has("gpu.nvml.temp"));
        s.Poller.PollOnce();
        Assert.False(s.Bus.Has("gpu.nvml.temp"));
        Assert.Contains("gpu.nvml.temp", s.Bus.Withdrawn);

        s.Gpu.Results.Remove("temp");
        s.Poller.PollOnce();
        Assert.True(s.Bus.Has("gpu.nvml.temp"));
    }

    [Fact]
    public void One_good_poll_resets_the_count_of_misses()
    {
        var s = new Setup();
        s.Poller.PollOnce();
        for (int round = 0; round < 4; round++)
        {
            s.Gpu.Results["temp"] = NvmlReturn.Timeout;
            s.Poller.PollOnce();
            s.Poller.PollOnce();
            s.Gpu.Results.Remove("temp");
            s.Poller.PollOnce();
        }
        Assert.True(s.Bus.Has("gpu.nvml.temp"));
        Assert.Empty(s.Bus.Withdrawn);
    }

    [Fact]
    public void The_first_miss_of_a_reading_is_logged_once_with_nvmls_own_words()
    {
        var s = new Setup();
        s.Gpu.Results["clock"] = NvmlReturn.Unknown;
        s.Gpu.Results["grclock"] = NvmlReturn.Unknown;
        for (int i = 0; i < 5; i++) s.Poller.PollOnce();
        var warnings = s.Log.Entries.Where(e => e.Level == LogLevel.Warn && e.Message.Contains("gpu.clock")).ToList();
        Assert.Single(warnings);
        Assert.Contains("error 999", warnings[0].Message);
    }

    [Fact]
    public void When_nothing_answers_the_state_goes_degraded_then_unavailable_and_nvml_is_opened_again()
    {
        var s = new Setup();
        s.Poller.PollOnce();
        Assert.Equal(GpuState.Available, s.Poller.State);

        foreach (var step in new[] { "util", "power", "limit", "grclock", "memclock", "memory", "temp", "pstate", "reasons" })
            s.Gpu.Results[step] = NvmlReturn.GpuIsLost;
        var states = new List<GpuState>();
        s.Poller.StateChanged += states.Add;

        s.Poller.PollOnce();
        Assert.Equal(GpuState.Degraded, s.Poller.State);
        Assert.Equal(1, s.Gpu.InitCount);
        s.Poller.PollOnce();
        Assert.Equal(GpuState.Degraded, s.Poller.State);
        s.Poller.PollOnce();
        Assert.Equal(GpuState.Unavailable, s.Poller.State);
        Assert.Equal(new[] { GpuState.Degraded, GpuState.Unavailable }, states);
        Assert.Equal(2, s.Gpu.InitCount);                   // the third failed poll opened the session again
        Assert.Equal(1, s.Gpu.Depth);                        // and still exactly one level is open
        Assert.Equal(3, s.Poller.FailedPolls);
    }

    [Fact]
    public void After_the_gpu_comes_back_the_state_is_available_again()
    {
        var s = new Setup();
        foreach (var step in new[] { "util", "power", "limit", "grclock", "memclock", "memory", "temp", "pstate", "reasons" })
            s.Gpu.Results[step] = NvmlReturn.Uninitialized;
        for (int i = 0; i < 4; i++) s.Poller.PollOnce();
        Assert.Equal(GpuState.Unavailable, s.Poller.State);
        Assert.Empty(s.Bus.Published);   // never a number from a session that did not answer

        s.Gpu.Results.Clear();
        s.Poller.PollOnce();
        Assert.Equal(GpuState.Available, s.Poller.State);
        Assert.Equal(0, s.Poller.FailedPolls);
        Assert.Equal(11, s.Bus.Published.Distinct().Count());
    }

    [Fact]
    public void Opening_again_that_fails_leaves_the_poller_closed_and_the_polls_stop_calling_the_gpu()
    {
        var s = new Setup();
        foreach (var step in new[] { "util", "power", "limit", "grclock", "memclock", "memory", "temp", "pstate", "reasons" })
            s.Gpu.Results[step] = NvmlReturn.GpuIsLost;
        s.Gpu.Results["init"] = NvmlReturn.DriverNotLoaded;   // the driver is being updated
        for (int i = 0; i < 3; i++) s.Poller.PollOnce();
        Assert.False(s.Poller.IsOpen);
        int calls = s.Gpu.Calls.Count;
        s.Poller.PollOnce();
        Assert.Equal(calls, s.Gpu.Calls.Count);
        s.Poller.Close();   // closing a poller that is not open is harmless
        Assert.Equal(0, s.Gpu.Depth);
    }

    // ------------------------------------------------------------------ events and threads

    [Fact]
    public void Polled_is_raised_after_every_poll_and_a_handler_that_throws_does_not_break_the_poll()
    {
        var s = new Setup();
        int raised = 0;
        s.Poller.Polled += _ => { raised++; throw new InvalidOperationException("handler bug"); };
        s.Poller.PollOnce();
        s.Poller.PollOnce();
        Assert.Equal(2, raised);
        Assert.Contains(s.Log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("handler bug"));
        Assert.True(s.Bus.Has("gpu.usage"));
    }

    [Fact]
    public void Polls_from_many_threads_never_overlap_inside_nvml()
    {
        var s = new Setup(wrap: gpu => new SerialNvml(gpu));
        s.Gpu.Delay = 1;
        Parallel.For(0, 8, _ => { for (int i = 0; i < 4; i++) s.Poller.PollOnce(); });
        Assert.Equal(1, s.Gpu.MaxInside);
        Assert.Equal(32 * 9, s.Gpu.ReadCalls);   // 32 polls x 9 NVML reads, none lost or doubled
    }

    [Fact]
    public async Task Close_waits_for_a_poll_that_is_running()
    {
        var s = new Setup(wrap: gpu => new SerialNvml(gpu));
        s.Gpu.Delay = 20;
        var poll = Task.Run(() => s.Poller.PollOnce());
        SpinWait.SpinUntil(() => s.Gpu.ReadCalls > 0, 5000);
        s.Poller.Close();
        await poll;
        Assert.Equal(0, s.Gpu.Depth);
        Assert.Equal(9, s.Gpu.ReadCalls);   // the poll that started finished all its reads before the shutdown
        var last = s.Gpu.Calls.Last();
        Assert.Equal("shutdown", last);
    }
}
