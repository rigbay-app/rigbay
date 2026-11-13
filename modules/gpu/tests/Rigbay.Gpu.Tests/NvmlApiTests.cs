using System.Reflection;
using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu.Tests;

/// <summary>The real marshalling code (<see cref="NvmlApi"/>) against a table of managed functions that stand in for nvml.dll.</summary>
public class NvmlApiTests
{
    private static readonly nint Device = 0x1000;

    [Fact]
    public void Every_reading_goes_through_the_function_pointers_and_comes_back_right()
    {
        var gpu = new FakeNvml();
        var api = new NvmlApi(FakeExports.Complete(gpu));

        Assert.Equal(NvmlReturn.Success, api.Init());
        Assert.Equal(NvmlReturn.Success, api.GetCount(out var count));
        Assert.Equal(1u, count);
        Assert.Equal(NvmlReturn.Success, api.GetHandle(0, out var device));
        Assert.Equal(Device, device);
        Assert.Equal(NvmlReturn.Success, api.GetName(device, out var name));
        Assert.Equal("NVIDIA GeForce RTX 3060 Laptop GPU", name);
        Assert.Equal(NvmlReturn.Success, api.GetUtilization(device, out var g, out var m));
        Assert.Equal((31u, 12u), (g, m));
        Assert.Equal(NvmlReturn.Success, api.GetPowerUsage(device, out var mw));
        Assert.Equal(72_400u, mw);
        Assert.Equal(NvmlReturn.Success, api.GetEnforcedPowerLimit(device, out var limit));
        Assert.Equal(80_000u, limit);
        Assert.Equal(NvmlReturn.Success, api.GetClock(device, NvmlClock.Graphics, out var gr));
        Assert.Equal(1_545u, gr);
        Assert.Equal(NvmlReturn.Success, api.GetClock(device, NvmlClock.Memory, out var mem));
        Assert.Equal(6_000u, mem);
        Assert.Equal(NvmlReturn.Success, api.GetMemory(device, out var total, out var used));
        Assert.Equal(gpu.TotalBytes, total);
        Assert.Equal(gpu.UsedBytes, used);
        Assert.Equal(NvmlReturn.Success, api.GetTemperature(device, out var c));   // the fake answers only for sensor 0, the GPU core
        Assert.Equal(64u, c);
        Assert.Equal(NvmlReturn.Success, api.GetPerformanceState(device, out var p));
        Assert.Equal(0, p);
        Assert.Equal(NvmlReturn.Success, api.GetClockEventReasons(device, out var reasons));
        Assert.Equal(ClockReasons.SoftwarePowerCap, reasons);
        Assert.Equal(NvmlReturn.Success, api.Shutdown());
    }

    [Fact]
    public void A_code_from_the_library_is_passed_on_and_the_values_stay_zero()
    {
        var gpu = new FakeNvml();
        gpu.Results["power"] = NvmlReturn.NotSupported;
        gpu.Results["util"] = NvmlReturn.GpuIsLost;
        var api = new NvmlApi(FakeExports.Complete(gpu));
        Assert.Equal(NvmlReturn.NotSupported, api.GetPowerUsage(Device, out var mw));
        Assert.Equal(0u, mw);
        Assert.Equal(NvmlReturn.GpuIsLost, api.GetUtilization(Device, out var g, out var m));
        Assert.Equal((0u, 0u), (g, m));
    }

    [Fact]
    public void A_driver_that_exports_only_the_old_reasons_name_is_read_through_it()
    {
        var gpu = new FakeNvml { Reasons = ClockReasons.HardwareThermal };
        var exports = FakeExports.Complete(gpu, newReasonsName: false);
        var api = new NvmlApi(exports);
        Assert.Equal(NvmlReturn.Success, api.GetClockEventReasons(Device, out var reasons));
        Assert.Equal(ClockReasons.HardwareThermal, reasons);
        Assert.Equal(new[] { NvmlAllowList.ClocksEventReasons, NvmlAllowList.ClocksThrottleReasons }, exports.Asked);   // the new name first, then the old
    }

    [Fact]
    public void A_driver_that_exports_the_new_reasons_name_is_never_asked_for_the_old_one()
    {
        var exports = FakeExports.Complete(new FakeNvml());
        var api = new NvmlApi(exports);
        api.GetClockEventReasons(Device, out _);
        Assert.Equal(new[] { NvmlAllowList.ClocksEventReasons }, exports.Asked);
    }

    [Fact]
    public void A_driver_with_neither_reasons_call_says_not_supported()
    {
        var gpu = new FakeNvml();
        var exports = new FakeExports();   // nothing exported at all
        var api = new NvmlApi(exports);
        Assert.Equal(NvmlReturn.NotSupported, api.GetClockEventReasons(Device, out var reasons));
        Assert.Equal(0ul, reasons);
        _ = gpu;
    }

    [Fact]
    public void A_function_the_library_does_not_export_is_function_not_found_not_a_crash()
    {
        var api = new NvmlApi(new FakeExports());
        Assert.Equal(NvmlReturn.FunctionNotFound, api.Init());
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetCount(out _));
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetName(Device, out var name));
        Assert.Equal("", name);
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetMemory(Device, out _, out _));
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetTemperature(Device, out _));
    }

    [Fact]
    public void The_answer_about_an_export_is_asked_once_and_remembered()
    {
        var exports = FakeExports.Complete(new FakeNvml());
        var api = new NvmlApi(exports);
        for (int i = 0; i < 5; i++) api.GetPowerUsage(Device, out _);
        Assert.Equal(1, exports.Asked.Count(n => n == NvmlAllowList.PowerUsage));
    }

    [Fact]
    public void The_library_is_asked_for_nothing_that_is_not_on_the_allow_list()
    {
        var exports = FakeExports.Complete(new FakeNvml());
        var api = new NvmlApi(exports);
        api.Init(); api.GetCount(out _); api.GetHandle(0, out var d); api.GetName(d, out _); api.GetUtilization(d, out _, out _);
        api.GetPowerUsage(d, out _); api.GetEnforcedPowerLimit(d, out _); api.GetClock(d, NvmlClock.Graphics, out _); api.GetClock(d, NvmlClock.Memory, out _);
        api.GetMemory(d, out _, out _); api.GetTemperature(d, out _); api.GetPerformanceState(d, out _); api.GetClockEventReasons(d, out _);
        api.ErrorString(NvmlReturn.NotSupported); api.Shutdown();
        Assert.NotEmpty(exports.Asked);
        Assert.All(exports.Asked, name => Assert.True(NvmlAllowList.IsAllowed(name), name));
    }

    [Theory]
    [InlineData("nvmlDeviceSetPowerManagementLimit")]
    [InlineData("nvmlDeviceResetGpuLockedClocks")]
    [InlineData("nvmlDeviceSetFanSpeed_v2")]
    [InlineData("nvmlDeviceGetCount")]          // a near miss: the _v2 name is the one on the list
    [InlineData("NVMLINIT_V2")]                 // case matters
    [InlineData("")]
    public void The_one_place_that_looks_a_function_up_refuses_a_name_that_is_not_on_the_list_before_it_asks_the_library(string name)
    {
        var exports = FakeExports.Complete(new FakeNvml());
        var api = new NvmlApi(exports);
        var resolve = typeof(NvmlApi).GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(typeof(NvmlApi.NoArguments));
        var thrown = Assert.Throws<TargetInvocationException>(() => resolve.Invoke(api, new object[] { name }));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Empty(exports.Asked);
    }

    [Fact]
    public void A_clock_that_is_not_graphics_or_memory_is_refused_without_a_call()
    {
        var exports = FakeExports.Complete(new FakeNvml());
        var api = new NvmlApi(exports);
        Assert.Equal(NvmlReturn.InvalidArgument, api.GetClock(Device, (NvmlClock)1, out var mhz));
        Assert.Equal(0u, mhz);
        Assert.Empty(exports.Asked);
    }

    [Fact]
    public void The_error_text_comes_from_the_library_and_falls_back_to_the_number()
    {
        Assert.Equal("fake NVML text", new NvmlApi(FakeExports.Complete(new FakeNvml())).ErrorString(NvmlReturn.NotSupported));
        Assert.Equal("NVML error 3", new NvmlApi(new FakeExports()).ErrorString(NvmlReturn.NotSupported));
    }

    [Fact]
    public void A_long_name_is_cut_at_the_buffer_and_trimmed()
    {
        var gpu = new FakeNvml { Name = "  NVIDIA " + new string('X', 200) };
        var api = new NvmlApi(FakeExports.Complete(gpu));
        Assert.Equal(NvmlReturn.Success, api.GetName(Device, out var name));
        Assert.StartsWith("NVIDIA XXX", name);
        Assert.True(name.Length <= 95);
    }

    [Fact]
    public void The_serial_wrapper_lets_one_thread_in_at_a_time_and_the_bare_fake_does_not()
    {
        // Dedicated threads released together by a barrier: Parallel.For may run every iteration on one thread on a busy two-core runner (CI, 2026-11-13).
        var bare = new FakeNvml { Delay = 100 };
        RunTogether(4, _ => bare.GetPowerUsage(Device, out var ignored));
        Assert.True(bare.MaxInside > 1, "the fake must be able to see an overlap, or the next check proves nothing");

        var gpu = new FakeNvml { Delay = 3 };
        var serial = new SerialNvml(gpu);
        RunTogether(8, i =>
        {
            for (int n = 0; n < 5; n++)
            {
                serial.GetPowerUsage(Device, out _);
                serial.GetUtilization(Device, out _, out _);
                if (i == 0 && n == 2) { serial.Init(); serial.Shutdown(); }
            }
        });
        Assert.Equal(1, gpu.MaxInside);
        Assert.True(gpu.ReadCalls >= 80);
    }

    /// <summary>Runs <paramref name="body"/> on <paramref name="count"/> threads of their own that all start at the same moment, and waits for them.</summary>
    private static void RunTogether(int count, Action<int> body)
    {
        using var start = new Barrier(count);
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() => { start.SignalAndWait(); body(i); }) { IsBackground = true }).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(30)), "a worker thread did not finish");
    }
}
