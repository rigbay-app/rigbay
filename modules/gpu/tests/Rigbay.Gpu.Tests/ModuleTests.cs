using Rigbay.Abstractions;
using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu.Tests;

/// <summary>The module on a fake host and a fake GPU: probe, who is looking, the page, the texts, the command, shutdown.</summary>
public class ModuleTests
{
    // ------------------------------------------------------------------ probe

    private static ProbeResult ProbeWith(Func<FakeNvml, Dependencies.Opened> open, out FakeNvml gpu)
    {
        var g = new FakeNvml();
        gpu = g;
        return new GpuModule(new Dependencies { OpenLibrary = () => open(g), AutoPoll = false }).Probe();
    }

    [Fact]
    public void Probe_says_yes_when_nvml_starts_and_there_is_a_gpu_and_leaves_nvml_shut_down()
    {
        var result = ProbeWith(g => new Dependencies.Opened(g, null), out var gpu);
        Assert.True(result.Supported);
        Assert.Equal(1, gpu.InitCount);
        Assert.Equal(0, gpu.Depth);
        Assert.Equal(0, gpu.ReadCalls);   // the probe reads no value
    }

    [Theory]
    [InlineData("probe.no-dll")]
    [InlineData("probe.dll-not-loaded")]
    public void Probe_says_why_when_the_library_is_missing_or_will_not_load(string key)
    {
        var result = ProbeWith(_ => new Dependencies.Opened(null, key), out _);
        Assert.False(result.Supported);
        Assert.Equal(key, result.ReasonKey);
    }

    [Fact]
    public void Probe_says_no_with_nvmls_own_words_when_init_fails()
    {
        var result = ProbeWith(g => { g.Results["init"] = NvmlReturn.DriverNotLoaded; return new Dependencies.Opened(g, null); }, out var gpu);
        Assert.False(result.Supported);
        Assert.Equal("probe.init-failed", result.ReasonKey);
        Assert.Equal("error 9", result.ReasonArgs![0]);
        Assert.Equal(0, gpu.Depth);
    }

    [Fact]
    public void Probe_says_no_device_when_nvml_has_no_gpu_and_still_shuts_down()
    {
        var result = ProbeWith(g => { g.Count = 0; return new Dependencies.Opened(g, null); }, out var gpu);
        Assert.False(result.Supported);
        Assert.Equal("probe.no-device", result.ReasonKey);
        Assert.Equal(0, gpu.Depth);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("handle")]
    [InlineData("name")]
    public void Probe_says_no_when_a_step_after_init_fails_and_shuts_down(string step)
    {
        var result = ProbeWith(g => { g.Results[step] = NvmlReturn.Unknown; return new Dependencies.Opened(g, null); }, out var gpu);
        Assert.False(result.Supported);
        Assert.Equal("probe.init-failed", result.ReasonKey);
        Assert.Equal(0, gpu.Depth);
    }

    [Fact]
    public void Probe_that_throws_is_an_answer_not_an_exception()
    {
        var module = new GpuModule(new Dependencies { OpenLibrary = () => throw new IOException("disk"), AutoPoll = false });
        var result = module.Probe();
        Assert.False(result.Supported);
        Assert.Equal("probe.check-failed", result.ReasonKey);
        Assert.Equal("disk", result.ReasonArgs![0]);
    }

    [Fact]
    public void The_constructor_the_host_uses_touches_nothing()
    {
        var module = new GpuModule();   // no library opened, no NVML, no thread
        Assert.Null(module.Polling);
        Assert.False(module.IsOpen);
    }

    // ------------------------------------------------------------------ nobody is looking

    [Fact]
    public void Enabled_with_nobody_looking_the_module_opens_nvml_but_reads_nothing()
    {
        var rig = new Rig(autoPoll: true).Start();
        Thread.Sleep(300);
        Assert.True(rig.Module.IsOpen);
        Assert.Equal(1, rig.Gpu.Depth);
        Assert.Equal(0, rig.Gpu.ReadCalls);
        Assert.Equal(0, rig.Host.MetricsBus.PublishCount);
        Assert.False(rig.Module.Polling);
        Assert.Equal("Not read yet", rig.StateLine());
        rig.Module.Shutdown();
    }

    [Fact]
    public void The_name_of_the_card_is_on_the_page_as_soon_as_nvml_is_open()
    {
        var rig = new Rig().Start();
        Assert.Equal("NVIDIA GeForce RTX 3060 Laptop GPU", rig.Row<ValueRow>("row.device.header").Value.Value);
    }

    [Fact]
    public void Opening_the_page_starts_the_reading_at_once_and_leaving_it_stops_it()
    {
        var rig = new Rig(autoPoll: true).Start();
        rig.Host.PageVisibleValue.Value = true;
        Assert.True(SpinWait.SpinUntil(() => rig.Host.MetricsBus.Has("gpu.usage"), 5000), "a reading within seconds of opening the page");
        Assert.True(rig.Module.Polling);

        rig.Host.PageVisibleValue.Value = false;
        Assert.False(rig.Module.Polling);
        SpinWait.SpinUntil(() => false, 150);   // a poll that was running ends
        int reads = rig.Gpu.ReadCalls;
        Thread.Sleep(2300);                      // longer than two intervals of the default 2 s
        Assert.Equal(reads, rig.Gpu.ReadCalls);
        rig.Module.Shutdown();
    }

    [Fact]
    public void A_reader_of_any_metric_of_the_module_counts_as_somebody_looking_and_leaving_ends_it()
    {
        var rig = new Rig().Start();
        Assert.False(rig.Module.Polling);

        rig.Host.MetricsBus.Subscribed.Add("gpu.power");   // the recorder, the Dashboard, the tray tooltip
        rig.Host.MetricsBus.RaiseSubscribersChanged();
        Assert.True(rig.Module.Polling);

        rig.Host.MetricsBus.Subscribed.Clear();
        rig.Host.MetricsBus.RaiseSubscribersChanged();
        Assert.False(rig.Module.Polling);
    }

    [Theory]
    [InlineData("gpu.usage")]
    [InlineData("gpu.memctl.usage")]
    [InlineData("gpu.power")]
    [InlineData("gpu.power.limit")]
    [InlineData("gpu.clock")]
    [InlineData("gpu.mem.clock")]
    [InlineData("gpu.mem.used")]
    [InlineData("gpu.mem.total")]
    [InlineData("gpu.nvml.temp")]
    [InlineData("gpu.pstate")]
    [InlineData("gpu.throttle")]
    public void Every_metric_of_the_module_can_be_the_reason_to_read(string id)
    {
        var rig = new Rig().Start();
        rig.Host.MetricsBus.Subscribed.Add(id);
        rig.Host.MetricsBus.RaiseSubscribersChanged();
        Assert.True(rig.Module.Polling);
    }

    [Fact]
    public void A_reader_of_somebody_elses_metric_does_not_wake_the_gpu()
    {
        var rig = new Rig().Start();
        rig.Host.MetricsBus.Subscribed.Add("gpu.temp");   // PredatorSense's id, not ours
        rig.Host.MetricsBus.Subscribed.Add("cpu.temp");
        rig.Host.MetricsBus.RaiseSubscribersChanged();
        Assert.False(rig.Module.Polling);
    }

    [Fact]
    public void A_page_that_is_already_visible_when_the_module_is_turned_on_reads_at_once()
    {
        var rig = new Rig(autoPoll: true);
        rig.Host.PageVisibleValue.Value = true;
        rig.Start();
        Assert.True(SpinWait.SpinUntil(() => rig.Host.MetricsBus.Has("gpu.usage"), 5000));
        rig.Module.Shutdown();
    }

    // ------------------------------------------------------------------ describe and page

    [Fact]
    public void Every_metric_is_described_once_with_a_label_and_the_unit_it_is_published_in()
    {
        var rig = new Rig().Start();
        var d = rig.Host.MetricsBus.Descriptors;
        Assert.Equal(11, d.Count);
        Assert.Equal(11, d.Select(x => x.Id.Value).Distinct().Count());
        foreach (var metric in GpuMetrics.All)
        {
            var one = d.Single(x => x.Id == metric.Id);
            Assert.Equal(metric.Unit, one.Unit);
            Assert.True(rig.Host.Loc.Has(one.LabelKey), one.LabelKey);
            if (one.DescriptionKey is { } k) Assert.True(rig.Host.Loc.Has(k), k);
        }
    }

    [Fact]
    public void The_page_has_four_tiles_a_chart_per_kind_and_two_reference_lines()
    {
        var rig = new Rig().Start();
        var readings = rig.Page().EnumerateRows().OfType<ReadingRow>().ToList();
        Assert.Equal(9, readings.Count);
        Assert.Equal(4, readings.Count(r => r.Tile == TileHint.Always));
        Assert.Equal(new[] { "gpu.usage", "gpu.power", "gpu.nvml.temp", "gpu.mem.used" }, readings.Where(r => r.Tile == TileHint.Always).Select(r => r.Metric.Value));

        var references = readings.Where(r => r.ChartRole == ChartRole.Reference).ToList();
        Assert.Equal(new[] { "gpu.power.limit", "gpu.mem.total" }, references.Select(r => r.Metric.Value));
        Assert.All(references, r => Assert.Equal(TileHint.Never, r.Tile));   // a limit is a dashed line, never a tile

        // a chart has one axis: every group holds one unit only
        foreach (var group in readings.GroupBy(r => r.ChartGroupKey))
        {
            var units = group.Select(r => GpuMetrics.All.Single(m => m.Id == r.Metric).Unit).Distinct().ToList();
            Assert.Single(units);
        }
        Assert.All(readings, r => Assert.True(r.RangeMin is null || r.RangeMax is null || r.RangeMin < r.RangeMax));
        Assert.Equal(new[] { 0.0, 100.0 }, new[] { readings.Single(r => r.Metric.Value == "gpu.usage").RangeMin!.Value, readings.Single(r => r.Metric.Value == "gpu.usage").RangeMax!.Value });
    }

    [Fact]
    public void The_dashboard_gets_load_and_power_not_the_temperature_predatorsense_already_shows()
    {
        var rig = new Rig().Start();
        var summary = rig.Page().Summary!;
        Assert.Equal(new[] { "gpu.usage", "gpu.power" }, summary.Readings!.Select(m => m.Value));
    }

    [Fact]
    public void The_poll_row_is_a_number_of_seconds_with_a_help_text()
    {
        var rig = new Rig().Start();
        var row = rig.Row<NumberRow>("row.poll.header");
        Assert.Equal(1, row.Minimum);
        Assert.Equal(30, row.Maximum);
        Assert.Equal(2, row.Value.Value);
        Assert.Equal(MetricUnit.Seconds, row.Unit);
        Assert.NotNull(row.HelpKey);
        Assert.Null(row.HardwareEffect);   // nothing on this page changes the machine
        Assert.DoesNotContain(rig.Page().EnumerateRows(), r => r.ChangesHardware);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void Changing_the_interval_sets_the_timer_and_saves_it_inside_the_limits()
    {
        var rig = new Rig().Start();
        var row = rig.Row<NumberRow>("row.poll.header");
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Module.PollInterval);

        row.Value.Value = 5;
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Module.PollInterval);
        Assert.Equal(5, ((GpuSettings)rig.Host.SettingsStore.Stored!).PollSeconds);

        row.Value.Value = 0;
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Module.PollInterval);
        row.Value.Value = 600;
        Assert.Equal(TimeSpan.FromSeconds(30), rig.Module.PollInterval);
    }

    [Fact]
    public void A_settings_file_with_a_silly_value_is_clamped_on_load()
    {
        var rig = new Rig();
        rig.Host.SettingsStore.Stored = new GpuSettings { PollSeconds = 99_999 };
        rig.Start();
        Assert.Equal(TimeSpan.FromSeconds(30), rig.Module.PollInterval);
        rig.Host.SettingsStore.Stored = new GpuSettings { PollSeconds = -4 };
        Assert.Equal(1, new GpuSettings { PollSeconds = -4 }.Clamped().PollSeconds);
    }

    // ------------------------------------------------------------------ the state line

    [Fact]
    public void The_state_line_reads_pstate_power_and_the_reason_like_the_task_example()
    {
        var rig = new Rig().Start();
        rig.Gpu.PowerMilliwatts = 72_400;
        rig.Module.PollNow();
        Assert.Equal("P0 · 72 W · power cap", rig.StateLine());
    }

    [Theory]
    [InlineData(ClockReasons.GpuIdle, 8, 9_100u, "P8 · 9 W · idle")]
    [InlineData(0ul, 0, 72_400u, "P0 · 72 W")]
    [InlineData(ClockReasons.SoftwarePowerCap | ClockReasons.HardwareThermal | ClockReasons.HardwareSlowdown, 2, 55_000u, "P2 · 55 W · hardware slowdown, thermal limit")]
    [InlineData(ClockReasons.GpuIdle | ClockReasons.ApplicationClocks, 5, 20_000u, "P5 · 20 W · app clock setting")]
    [InlineData(ClockReasons.SoftwareThermal | ClockReasons.HardwareThermal, 1, 60_000u, "P1 · 60 W · thermal limit")]
    [InlineData(ClockReasons.HardwarePowerBrake, 0, 90_000u, "P0 · 90 W · power brake")]
    public void The_state_line_names_at_most_two_reasons_most_serious_first_and_idle_only_alone(ulong reasons, int pstate, uint milliwatts, string expected)
    {
        var rig = new Rig().Start();
        rig.Gpu.Reasons = reasons;
        rig.Gpu.PState = pstate;
        rig.Gpu.PowerMilliwatts = milliwatts;
        rig.Module.PollNow();
        Assert.Equal(expected, rig.StateLine());
    }

    [Fact]
    public void A_part_the_gpu_did_not_give_is_left_out_of_the_state_line()
    {
        var rig = new Rig().Start();
        rig.Gpu.Results["power"] = NvmlReturn.NotSupported;
        rig.Module.PollNow();
        Assert.Equal("P0 · power cap", rig.StateLine());

        rig.Gpu.Results["reasons"] = NvmlReturn.NotSupported;
        rig.Gpu.Results["pstate"] = NvmlReturn.NotSupported;
        rig.Module.PollNow();
        Assert.Equal("Reading", rig.StateLine());
    }

    [Fact]
    public void The_first_reading_of_a_run_is_logged_in_one_line_and_the_next_ones_are_not()
    {
        var rig = new Rig().Start();
        rig.Module.PollNow();
        rig.Module.PollNow();
        rig.Module.PollNow();
        var lines = rig.Host.LogSink.Entries.Where(e => e.Level == LogLevel.Info && e.Message.StartsWith("GPU readings available:", StringComparison.Ordinal)).ToList();
        Assert.Single(lines);
        Assert.DoesNotContain("\n", lines[0].Message);
        Assert.Contains("NVIDIA GeForce RTX 3060 Laptop GPU | GPU load: 31 % |", lines[0].Message);
        Assert.Contains("GPU power: 72.4 W", lines[0].Message);
    }

    [Fact]
    public void The_clock_limit_row_names_every_reason_or_says_not_limited()
    {
        var rig = new Rig().Start();
        Assert.Equal("", rig.Row<ValueRow>("row.limit.header").Value.Value);   // the GPU has not said yet

        rig.Gpu.Reasons = ClockReasons.SoftwarePowerCap | ClockReasons.SyncBoost | ClockReasons.HardwareThermal;
        rig.Module.PollNow();
        Assert.Equal("thermal limit, power cap, sync boost", rig.Row<ValueRow>("row.limit.header").Value.Value);

        rig.Gpu.Reasons = 0;
        rig.Module.PollNow();
        Assert.Equal("Not limited", rig.Row<ValueRow>("row.limit.header").Value.Value);
    }

    [Fact]
    public void A_gpu_that_stops_answering_is_said_in_the_state_line()
    {
        var rig = new Rig().Start();
        foreach (var step in new[] { "util", "power", "limit", "grclock", "memclock", "memory", "temp", "pstate", "reasons" })
            rig.Gpu.Results[step] = NvmlReturn.GpuIsLost;
        rig.Module.PollNow();
        Assert.Equal("No answer from the GPU", rig.StateLine());
        rig.Module.PollNow();
        rig.Module.PollNow();
        Assert.Equal("NVIDIA driver not available", rig.StateLine());
    }

    [Fact]
    public void A_language_switch_rewrites_the_state_line_and_the_limit_row()
    {
        var rig = new Rig().Start();
        rig.Module.PollNow();
        Assert.Equal("P0 · 72 W · power cap", rig.StateLine());

        rig.Host.Loc.Switch("vi");
        Assert.Equal("P0 · 72 W · trần công suất", rig.StateLine());
        Assert.Equal("trần công suất", rig.Row<ValueRow>("row.limit.header").Value.Value);
        rig.Host.Loc.Switch("en");
        Assert.Equal("P0 · 72 W · power cap", rig.StateLine());
    }

    // ------------------------------------------------------------------ nvml does not open

    [Fact]
    public void If_nvml_will_not_open_when_the_module_is_turned_on_the_page_says_so_and_nothing_throws()
    {
        var rig = new Rig();
        Assert.True(rig.Module.Probe().Supported);
        rig.Gpu.Results["init"] = NvmlReturn.DriverNotLoaded;   // the driver went away between the probe and now
        rig.Module.Initialize(rig.Host);
        Assert.False(rig.Module.IsOpen);
        Assert.Null(rig.Module.Polling);
        Assert.Equal("NVIDIA driver not available", rig.StateLine());
        Assert.Contains(rig.Host.LogSink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("did not open"));
        rig.Host.PageVisibleValue.Value = true;   // nothing is hooked: no crash, no read
        Assert.Equal(0, rig.Gpu.ReadCalls);
        rig.Module.Shutdown();
        Assert.Equal(0, rig.Gpu.Depth);
    }

    [Fact]
    public void Initialize_without_a_library_at_all_leaves_a_page_that_says_not_available()
    {
        var host = new FakeHost();
        var module = new GpuModule(new Dependencies { OpenLibrary = () => new Dependencies.Opened(null, "probe.no-dll"), AutoPoll = false });
        module.Initialize(host);
        Assert.Equal("NVIDIA driver not available", host.Loc.Resolve(module.GetPageModel(host)!.Summary!.StateLine!.Value));
        module.Shutdown();
    }

    // ------------------------------------------------------------------ shutdown and turning on again

    [Fact]
    public void Shutdown_shuts_nvml_down_takes_the_readings_off_the_bus_and_unhooks_the_host()
    {
        var rig = new Rig(autoPoll: true).Start();
        rig.Host.PageVisibleValue.Value = true;
        Assert.True(SpinWait.SpinUntil(() => rig.Host.MetricsBus.Has("gpu.power"), 5000));

        rig.Module.Shutdown();
        Assert.Equal(0, rig.Gpu.Depth);
        Assert.Empty(rig.Host.MetricsBus.Latest);
        foreach (var metric in GpuMetrics.All) Assert.True(rig.Host.MetricsBus.WasWithdrawn(metric.Id.Value), metric.Id.Value);

        int calls = rig.Gpu.Calls.Count;
        rig.Host.PageVisibleValue.Value = false;    // no handler left behind
        rig.Host.PageVisibleValue.Value = true;
        rig.Host.MetricsBus.RaiseSubscribersChanged();
        Thread.Sleep(150);
        Assert.Equal(calls, rig.Gpu.Calls.Count);
        Assert.Null(rig.Module.Polling);
    }

    [Fact]
    public void Shutdown_twice_and_shutdown_before_initialize_are_harmless()
    {
        var rig = new Rig().Start();
        rig.Module.Shutdown();
        rig.Module.Shutdown();
        Assert.Equal(0, rig.Gpu.Depth);
        new GpuModule(new Dependencies { AutoPoll = false }).Shutdown();
    }

    [Fact]
    public void Turned_off_and_on_again_the_module_opens_nvml_once_more_and_balances_it()
    {
        var gpu = new FakeNvml();
        var module = new GpuModule(new Dependencies { OpenLibrary = () => new Dependencies.Opened(gpu, null), AutoPoll = false });
        Assert.True(module.Probe().Supported);
        for (int round = 0; round < 3; round++)
        {
            var host = new FakeHost();
            module.Initialize(host);
            Assert.Equal(1, gpu.Depth);
            host.PageVisibleValue.Value = true;
            Assert.True(module.Polling);
            module.PollNow();
            Assert.True(host.MetricsBus.Has("gpu.usage"));
            module.Shutdown();
            Assert.Equal(0, gpu.Depth);
        }
    }

    [Fact]
    public async Task Shutdown_during_a_poll_waits_for_it_and_never_leaves_nvml_open_or_enters_it_twice()
    {
        var gpu = new FakeNvml { Delay = 10 };
        var rig = new Rig(gpu: gpu).Start();
        var poll = Task.Run(() => rig.Module.PollNow());
        SpinWait.SpinUntil(() => gpu.ReadCalls > 0, 5000);
        rig.Module.Shutdown();
        await poll;
        Assert.Equal(0, gpu.Depth);
        Assert.Equal(1, gpu.MaxInside);
    }

    // ------------------------------------------------------------------ rigbay gpu status

    [Fact]
    public async Task The_status_command_reads_once_and_prints_every_reading()
    {
        var rig = new Rig().Start();
        var result = await rig.Host.CommandRegistry.Run("status");
        Assert.Equal(0, result.ExitCode);
        var lines = result.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("NVIDIA GeForce RTX 3060 Laptop GPU", lines[0]);
        Assert.Contains("GPU load: 31 %", lines);
        Assert.Contains("Memory controller load: 12 %", lines);
        Assert.Contains("GPU power: 72.4 W", lines);
        Assert.Contains("Power limit: 80 W", lines);
        Assert.Contains("GPU clock: 1545 MHz", lines);
        Assert.Contains("Memory clock: 6000 MHz", lines);
        Assert.Contains("Memory in use: 1500 MiB", lines);
        Assert.Contains("Memory total: 6144 MiB", lines);
        Assert.Contains("GPU core temperature: 64 °C", lines);
        Assert.Contains("Performance state: 0", lines);
        Assert.Contains("Clock limit: power cap", lines);
        Assert.True(rig.Host.MetricsBus.Has("gpu.power"));   // the same poll also fed the bus
    }

    [Fact]
    public async Task The_status_command_leaves_out_a_reading_that_is_not_there_and_refuses_arguments()
    {
        var rig = new Rig().Start();
        rig.Gpu.Results["power"] = NvmlReturn.NotSupported;
        var result = await rig.Host.CommandRegistry.Run("status");
        Assert.DoesNotContain("GPU power", result.Output);
        Assert.Contains("GPU load", result.Output);

        var usage = await rig.Host.CommandRegistry.Run("status", "extra");
        Assert.Equal(2, usage.ExitCode);
        Assert.Equal("Usage: rigbay gpu status", usage.Output);
    }

    [Fact]
    public async Task The_status_command_fails_with_a_sentence_when_nvml_is_not_open_or_does_not_answer()
    {
        var rig = new Rig();
        Assert.True(rig.Module.Probe().Supported);
        rig.Gpu.Results["init"] = NvmlReturn.DriverNotLoaded;
        rig.Module.Initialize(rig.Host);
        var closed = await rig.Host.CommandRegistry.Run("status");
        Assert.Equal(1, closed.ExitCode);
        Assert.Equal("NVIDIA driver not available", closed.Output);

        var other = new Rig().Start();
        foreach (var step in new[] { "util", "power", "limit", "grclock", "memclock", "memory", "temp", "pstate", "reasons" })
            other.Gpu.Results[step] = NvmlReturn.Timeout;
        var silent = await other.Host.CommandRegistry.Run("status");
        Assert.Equal(1, silent.ExitCode);
        Assert.Equal("No answer from the GPU", silent.Output);
    }
}
