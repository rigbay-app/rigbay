using Rigbay.PredatorSense;
using Rigbay.PredatorSense.Sensors;
using Rigbay.PredatorSense.Wmi;
using Xunit;

namespace Rigbay.PredatorSense.Tests;

public class AllowListTests
{
    [Fact]
    public void Exactly_two_fan_values_are_allowed()
    {
        Assert.Equal(0x01410019UL, WmiAllowList.FanAuto);
        Assert.Equal(0x02820019UL, WmiAllowList.FanMax);
        var allowed = new List<ulong>();
        // Every value up to 0x40000 plus both approved values' neighbours and some shifted patterns.
        for (ulong v = 0; v < 0x40000; v++) if (WmiAllowList.IsAllowedFanWrite(v)) allowed.Add(v);
        foreach (var baseValue in new[] { WmiAllowList.FanAuto, WmiAllowList.FanMax })
            for (int bit = 0; bit < 64; bit++)
            {
                Assert.False(WmiAllowList.IsAllowedFanWrite(baseValue ^ (1UL << bit)));   // any one-bit change is refused
            }
        Assert.Empty(allowed);   // neither approved value is below 0x40000
        Assert.True(WmiAllowList.IsAllowedFanWrite(WmiAllowList.FanAuto));
        Assert.True(WmiAllowList.IsAllowedFanWrite(WmiAllowList.FanMax));
        Assert.False(WmiAllowList.IsAllowedFanWrite(0x03C30019));   // custom for all three fans
        Assert.False(WmiAllowList.IsAllowedFanWrite(0x02820018));
        Assert.False(WmiAllowList.IsAllowedFanWrite(ulong.MaxValue));
        Assert.False(WmiAllowList.IsAllowedFanWrite(0));
    }

    [Fact]
    public void Exactly_six_sensor_indexes_are_allowed()
    {
        var allowed = Enumerable.Range(-5, 300).Where(WmiAllowList.IsAllowedRead).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 6, 9, 10 }, allowed);
        Assert.Equal(0x0101u, WmiAllowList.ReadInput(1));
        Assert.Equal(0x0A01u, WmiAllowList.ReadInput(10));
    }

    [Fact]
    public void The_guard_refuses_other_writes_and_never_calls_through()
    {
        var fake = new FakeAcerWmi();
        var guard = new GuardedAcerWmi(fake);
        foreach (ulong v in new ulong[] { 0, 1, 0x10007, 0x2005, 0x03C30019, 0x02820018, 0x01410018, ulong.MaxValue })
        {
            var ex = Assert.Throws<AcerWmiException>(() => guard.SetFanBehavior(v));
            Assert.Equal(AcerWmiFailure.Refused, ex.Failure);
        }
        Assert.Equal(0, fake.WriteCount);
    }

    [Fact]
    public void The_guard_refuses_other_reads()
    {
        var fake = new FakeAcerWmi();
        var guard = new GuardedAcerWmi(fake);
        foreach (int i in new[] { -1, 0, 4, 5, 7, 8, 11, 12, 17, 255 })
            Assert.Equal(AcerWmiFailure.Refused, Assert.Throws<AcerWmiException>(() => guard.ReadSysInfo(i)).Failure);
        Assert.Empty(fake.Reads);
    }

    [Fact]
    public void The_guard_passes_the_two_approved_values_with_the_exact_value()
    {
        var fake = new FakeAcerWmi();
        var guard = new GuardedAcerWmi(fake);
        Assert.Equal(0u, guard.SetFanBehavior(WmiAllowList.FanMax));
        Assert.Equal(0u, guard.SetFanBehavior(WmiAllowList.FanAuto));
        Assert.Equal(new[] { 0x02820019UL, 0x01410019UL }, fake.Writes.Select(w => w.Value).ToArray());
    }

    [Fact]
    public void One_write_at_a_time()
    {
        var fake = new FakeAcerWmi();
        var guard = new GuardedAcerWmi(fake);
        AcerWmiException? second = null;
        fake.InsideWrite = () => second = Assert.Throws<AcerWmiException>(() => guard.SetFanBehavior(WmiAllowList.FanAuto));
        guard.SetFanBehavior(WmiAllowList.FanMax);
        Assert.NotNull(second);
        Assert.Equal(AcerWmiFailure.Refused, second!.Failure);
        Assert.Equal(1, fake.WriteCount);
        // The gate is open again afterwards.
        fake.InsideWrite = null;
        guard.SetFanBehavior(WmiAllowList.FanAuto);
        Assert.Equal(2, fake.WriteCount);
    }

    // The real implementation checks the list too. These calls are refused before any WMI object is created, so they are
    // safe to run on any machine. Approved values are deliberately NOT sent to the real class here.
    [Fact]
    public void The_real_implementation_refuses_before_touching_wmi()
    {
        using var real = new AcerWmi();
        Assert.Equal(AcerWmiFailure.Refused, Assert.Throws<AcerWmiException>(() => real.SetFanBehavior(0x1234)).Failure);
        Assert.Equal(AcerWmiFailure.Refused, Assert.Throws<AcerWmiException>(() => real.SetFanBehavior(0x03C30019)).Failure);
        Assert.Equal(AcerWmiFailure.Refused, Assert.Throws<AcerWmiException>(() => real.ReadSysInfo(4)).Failure);
        Assert.Equal(AcerWmiFailure.Refused, Assert.Throws<AcerWmiException>(() => real.ReadSysInfo(17)).Failure);
    }
}

public class ProbeTests
{
    [Fact]
    public void Supported_when_service_and_class_exist_whatever_the_model()
    {
        foreach (var model in new[] { "Predator PT516-51s", "Nitro AN515-58", null })
            Assert.True(MachineProbe.Run(new FakeMachine { Model = model }).Supported);
    }

    [Fact]
    public void No_service_gives_a_key_and_not_an_exception()
    {
        var r = MachineProbe.Run(new FakeMachine { Service = false });
        Assert.False(r.Supported);
        Assert.Equal("probe.no-service", r.ReasonKey);
    }

    [Fact]
    public void No_wmi_class_gives_a_key()
    {
        var r = MachineProbe.Run(new FakeMachine { WmiClass = false });
        Assert.False(r.Supported);
        Assert.Equal("probe.no-wmi", r.ReasonKey);
    }

    [Fact]
    public void Both_missing_reports_the_service_first()
    {
        Assert.Equal("probe.no-service", MachineProbe.Run(new FakeMachine { Service = false, WmiClass = false }).ReasonKey);
    }

    [Fact]
    public void A_failing_check_is_unsupported_with_the_message_never_a_throw()
    {
        var r = MachineProbe.Run(new FakeMachine { Throws = new TimeoutException("WMI did not answer") });
        Assert.False(r.Supported);
        Assert.Equal("probe.check-failed", r.ReasonKey);
        Assert.Equal("WMI did not answer", r.ReasonArgs![0]);
    }

    [Fact]
    public void The_module_probe_uses_the_machine_and_never_calls_wmi()
    {
        var wmi = new FakeAcerWmi();
        var module = new PredatorSenseModule(new FakeMachine { Service = false }, () => wmi, null, null);
        Assert.False(module.Probe().Supported);
        Assert.Empty(wmi.Reads);
        Assert.Equal(0, wmi.WriteCount);
    }

    [Fact]
    public void Resolved_reason_texts_exist_in_both_languages()
    {
        foreach (var lang in new[] { "en", "vi" })
        {
            var loc = new FakeLocalisation(lang);
            Assert.DoesNotContain("module.predatorsense.", loc.T("probe.no-service"));
            Assert.DoesNotContain("module.predatorsense.", loc.T("probe.no-wmi"));
            Assert.DoesNotContain("module.predatorsense.", loc.F("probe.check-failed", "x"));
        }
    }
}

public class DecoderTests
{
    private static readonly SensorInfo CpuTemp = SensorInfo.Of(Sensor.CpuTemp);
    private static readonly SensorInfo Fan = SensorInfo.Of(Sensor.CpuFan);

    [Theory]
    [InlineData(0x4900UL, 73)]
    [InlineData(0x3F00UL, 63)]
    [InlineData(0x0100UL, 1)]
    public void Status_zero_gives_the_value_from_bits_8_to_23(ulong raw, int expected)
    {
        var d = SensorDecoder.Decode(CpuTemp, raw, hasReported: false);
        Assert.Equal(expected, d.Value);
        Assert.True(d.AnswerOk);
    }

    [Fact]
    public void Real_answers_from_the_b3_and_b5_logs_decode()
    {
        Assert.Equal(2857, SensorDecoder.Decode(Fan, 0xB2900, false).Value);
        Assert.Equal(5555, SensorDecoder.Decode(Fan, FakeAcerWmi.Raw(5555), false).Value);
        Assert.Equal(3061, SensorDecoder.Decode(SensorInfo.Of(Sensor.GpuFan1), 0xBF500, false).Value);
    }

    [Theory]
    [InlineData(0x4901UL)]
    [InlineData(0x02E1UL)]   // the odd Cool Booster answer: status 0xE1
    [InlineData(0xFFFFFFFFFFFFFFFFUL)]
    public void A_non_zero_status_is_not_available(ulong raw)
    {
        var d = SensorDecoder.Decode(CpuTemp, raw, hasReported: true);
        Assert.Null(d.Value);
        Assert.Equal(Unavailable.Status, d.Why);
        Assert.False(d.AnswerOk);
    }

    [Fact]
    public void Zero_on_an_absent_part_is_not_available_but_still_an_answer()
    {
        var d = SensorDecoder.Decode(SensorInfo.Of(Sensor.GpuFan2), 0, hasReported: false);
        Assert.Null(d.Value);
        Assert.Equal(Unavailable.Absent, d.Why);
        Assert.True(d.AnswerOk);
    }

    [Fact]
    public void Zero_on_a_part_that_has_reported_before_is_a_real_zero()
    {
        var d = SensorDecoder.Decode(Fan, 0, hasReported: true);
        Assert.Equal(0, d.Value);
    }

    [Fact]
    public void A_temperature_of_zero_is_never_a_reading_even_after_earlier_readings()
    {
        var d = SensorDecoder.Decode(CpuTemp, 0, hasReported: true);
        Assert.Null(d.Value);
        Assert.Equal(Unavailable.Absent, d.Why);
    }

    [Fact]
    public void Impossible_values_are_not_available()
    {
        Assert.Equal(Unavailable.OutOfRange, SensorDecoder.Decode(CpuTemp, FakeAcerWmi.Raw(200), true).Why);
        Assert.Equal(Unavailable.OutOfRange, SensorDecoder.Decode(Fan, FakeAcerWmi.Raw(60000), true).Why);
        Assert.Equal(130, SensorDecoder.Decode(CpuTemp, FakeAcerWmi.Raw(130), true).Value);
    }

    [Fact]
    public void Metric_ids_and_units_are_stable()
    {
        Assert.Equal(
            new[] { "cpu.temp:Celsius:1", "system.temp:Celsius:3", "gpu.temp:Celsius:10", "fan.cpu.rpm:Rpm:2", "fan.gpu1.rpm:Rpm:6", "fan.gpu2.rpm:Rpm:9" },
            SensorInfo.All.Select(s => $"{s.Metric.Value}:{s.Unit}:{s.Index}").ToArray());
    }
}
