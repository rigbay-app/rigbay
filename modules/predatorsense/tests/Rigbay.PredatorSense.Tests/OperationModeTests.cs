using System.Reflection;
using Rigbay.Abstractions;
using Rigbay.PredatorSense.Sensors;
using Rigbay.PredatorSense.Wmi;
using Xunit;

namespace Rigbay.PredatorSense.Tests;

/// <summary>The decoding of the two operation-mode reads (FINDINGS 15.9): status in the low byte, the value in bits 8 to 15, never a guess.</summary>
public class OperationModeDecoderTests
{
    [Fact]
    public void The_answer_the_real_machine_gave_in_b10_is_Default()
    {
        Assert.Equal(OperationMode.Default, OperationModeDecoder.DecodeCurrent(0x0B0100));
    }

    [Theory]
    [InlineData(0, OperationMode.Quiet)]
    [InlineData(1, OperationMode.Default)]
    [InlineData(2, OperationMode.Render)]
    [InlineData(3, OperationMode.Edit)]
    [InlineData(4, OperationMode.Extreme)]
    [InlineData(5, OperationMode.Turbo)]
    public void Each_known_id_in_bits_8_to_15_is_its_mode(int id, OperationMode expected)
    {
        Assert.Equal(expected, OperationModeDecoder.DecodeCurrent((ulong)id << 8));
        Assert.Equal(expected, OperationModeDecoder.DecodeCurrent(((ulong)id << 8) | 0x0B0000));   // byte 2 is ignored
    }

    [Theory]
    [InlineData(0x0B0601UL)]   // an id nobody knows (6)
    [InlineData(0x0B0600UL)]
    [InlineData(0x0BFF00UL)]
    [InlineData(0x0B0101UL)]   // status 1 with a mode byte: the status wins
    [InlineData(0x0B01E1UL)]   // the odd 0xE1 of section 11
    [InlineData(0xFFFFFFFFFFFFFFFFUL)]
    public void A_non_zero_status_or_an_unknown_id_is_unknown(ulong raw)
    {
        Assert.Null(OperationModeDecoder.DecodeCurrent(raw));
    }

    [Fact]
    public void The_b10_capability_answer_is_Quiet_Default_Extreme_Turbo_and_not_Render_or_Edit()
    {
        var modes = OperationModeDecoder.DecodeSupported(0x0B3300);
        Assert.Equal(new[] { OperationMode.Quiet, OperationMode.Default, OperationMode.Extreme, OperationMode.Turbo }, modes);
        Assert.DoesNotContain(OperationMode.Render, modes!);
        Assert.DoesNotContain(OperationMode.Edit, modes!);
    }

    [Fact]
    public void A_capability_mask_with_every_known_bit_lists_six_and_unknown_bits_are_ignored()
    {
        Assert.Equal(6, OperationModeDecoder.DecodeSupported(0x3F00)!.Count);
        Assert.Equal(6, OperationModeDecoder.DecodeSupported(0xFF00)!.Count);   // bits 6 and 7 mean nothing known
        Assert.Equal(new[] { OperationMode.Render }, OperationModeDecoder.DecodeSupported(0x0400));
    }

    [Theory]
    [InlineData(0x0B3301UL)]   // non-zero status
    [InlineData(0x0B3300UL | 0xE1)]
    [InlineData(0x0B0000UL)]   // nothing advertised: not a list
    [InlineData(0x0BC000UL)]   // only bits nobody knows
    public void A_capability_with_a_non_zero_status_or_no_known_bit_is_unknown(ulong raw)
    {
        Assert.Null(OperationModeDecoder.DecodeSupported(raw));
    }
}

/// <summary>Only the two approved inputs of <c>GetGamingMiscSetting</c> can leave the module, and no <c>Set*</c> but the fan one can be named.</summary>
public class OperationModeAllowListTests
{
    [Fact]
    public void Exactly_0A_and_0B_are_allowed_and_everything_else_is_refused()
    {
        Assert.Equal(0x0Bu, WmiAllowList.MiscCurrentMode);
        Assert.Equal(0x0Au, WmiAllowList.MiscSupportedModes);
        Assert.Equal("GetGamingMiscSetting", WmiAllowList.MiscReadMethod);

        var allowed = new List<ulong>();
        for (ulong v = 0; v < 0x20000; v++) if (WmiAllowList.IsAllowedMiscRead(v)) allowed.Add(v);
        Assert.Equal(new ulong[] { 0x0A, 0x0B }, allowed);

        // The mode WRITE values, the GPU-step value, the other Misc inputs and large values, never allowed here.
        foreach (ulong v in new ulong[] { 0x05, 0x0C, 0x105, 0x10B, 0x20B, 0x30B, 0x40B, 0x50B, 2, 9, 0x100000000UL, 0x10000000AUL, 0x10000000BUL, ulong.MaxValue })
            Assert.False(WmiAllowList.IsAllowedMiscRead(v), $"0x{v:X}");
        foreach (ulong baseValue in new ulong[] { 0x0A, 0x0B })
            for (int bit = 0; bit < 64; bit++)
                if (baseValue != (baseValue ^ (1UL << bit))) Assert.Equal(WmiAllowList.IsAllowedMiscRead(baseValue ^ (1UL << bit)), (baseValue ^ (1UL << bit)) == 0x0A || (baseValue ^ (1UL << bit)) == 0x0B);
    }

    [Fact]
    public void The_real_AcerWmi_refuses_every_other_input_before_any_wmi_object_exists()
    {
        using var real = new AcerWmi(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        foreach (ulong v in new ulong[] { 0, 1, 2, 5, 9, 0x0C, 0x105, 0x10B, 0x20B, 0x30B, 0x40B, 0x50B, 0xFF, 0x1FF, 0x1000B, 0x10000000BUL, ulong.MaxValue })
        {
            var ex = Assert.Throws<AcerWmiException>(() => real.ReadMisc(v));
            Assert.Equal(AcerWmiFailure.Refused, ex.Failure);
        }
    }

    [Fact]
    public void The_firmware_interface_has_exactly_four_methods_and_the_only_write_is_the_fan_one()
    {
        var names = typeof(IAcerWmi).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "ReadOperationMode", "ReadSupportedOperationModes", "ReadSysInfo", "SetFanBehavior" }, names);
        // No method takes the Misc input as an argument: nobody above the allow-list can choose one.
        Assert.Empty(typeof(IAcerWmi).GetMethod("ReadOperationMode")!.GetParameters());
        Assert.Empty(typeof(IAcerWmi).GetMethod("ReadSupportedOperationModes")!.GetParameters());

        // Every method name the allow-list holds: reads start with Get, and the single Set is the fan one.
        var strings = typeof(WmiAllowList).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToArray();
        Assert.Equal(new[] { "SetGamingFanBehavior" }, strings.Where(s => s.StartsWith("Set", StringComparison.Ordinal)).ToArray());
        Assert.Equal(new[] { "GetGamingMiscSetting", "GetGamingSysInfo" }, strings.Where(s => s.StartsWith("Get", StringComparison.Ordinal)).OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Through_the_guard_each_read_sends_exactly_its_own_input_and_nothing_is_written()
    {
        var fake = new FakeAcerWmi();
        var guard = new GuardedAcerWmi(fake);
        Assert.Equal(0x0B0100UL, guard.ReadOperationMode());
        Assert.Equal(0x0B3300UL, guard.ReadSupportedOperationModes());
        Assert.Equal(new uint[] { 0x0B, 0x0A }, fake.MiscReads);
        Assert.Equal(0, fake.WriteCount);
        Assert.Empty(fake.Reads);   // not a sensor read
    }
}

public class OperationModeReaderTests
{
    private static (OperationModeReader Reader, FakeAcerWmi Wmi, FakeMetrics Bus, FakeLog Log) Make()
    {
        var wmi = new FakeAcerWmi();
        var bus = new FakeMetrics();
        var log = new FakeLog();
        return (new OperationModeReader(wmi, bus, log), wmi, bus, log);
    }

    [Fact]
    public void A_read_publishes_the_mode_id_on_the_metric_and_logs_the_raw_value()
    {
        var (reader, wmi, bus, log) = Make();
        Assert.Equal(OperationMode.Default, reader.ReadCurrent());
        Assert.Equal(OperationMode.Default, reader.Current);
        var sample = bus.Latest(new MetricId("predatorsense.opmode"));
        Assert.Equal(1, sample!.Value);
        Assert.Equal(MetricUnit.None, sample.Unit);
        Assert.True(log.Has("0xB0100"));
        Assert.Equal(new uint[] { 0x0B }, wmi.MiscReads);
        Assert.Equal(0, wmi.WriteCount);
    }

    [Fact]
    public void Each_mode_is_published_as_its_number()
    {
        var (reader, wmi, bus, _) = Make();
        foreach (var (id, value) in new[] { (0, 0.0), (4, 4.0), (5, 5.0), (1, 1.0) })
        {
            wmi.OperationModeAnswer = FakeAcerWmi.Raw(id) | 0x0B0000;
            reader.ReadCurrent();
            Assert.Equal(value, bus.Latest(new MetricId("predatorsense.opmode"))!.Value);
        }
    }

    [Theory]
    [InlineData(0x0B01E1UL)]   // non-zero status
    [InlineData(0x0B0900UL)]   // an id nobody knows
    public void A_non_zero_status_or_an_unknown_id_is_unknown_and_withdraws_the_metric(ulong raw)
    {
        var (reader, wmi, bus, _) = Make();
        reader.ReadCurrent();
        Assert.NotNull(bus.Latest(new MetricId("predatorsense.opmode")));
        wmi.OperationModeAnswer = raw;
        Assert.Null(reader.ReadCurrent());
        Assert.Null(reader.Current);
        Assert.Null(bus.Latest(new MetricId("predatorsense.opmode")));
        Assert.Contains(new MetricId("predatorsense.opmode"), bus.Withdrawn);
    }

    [Fact]
    public void A_failed_or_stalled_read_is_unknown_and_never_throws()
    {
        var (reader, wmi, bus, log) = Make();
        reader.ReadCurrent();
        wmi.MiscReadThrows = new AcerWmiException(AcerWmiFailure.Timeout, "stalled");
        Assert.Null(reader.ReadCurrent());
        Assert.Null(reader.ReadSupported());
        Assert.Null(bus.Latest(new MetricId("predatorsense.opmode")));
        Assert.True(log.Has("stalled"));
        wmi.MiscReadThrows = new InvalidOperationException("boom");
        Assert.Null(reader.ReadCurrent());   // an unexpected exception is caught too
        wmi.MiscReadThrows = null;
        Assert.Equal(OperationMode.Default, reader.ReadCurrent());   // and the next read recovers
        Assert.NotNull(bus.Latest(new MetricId("predatorsense.opmode")));
    }

    [Fact]
    public void Changed_is_raised_only_when_the_mode_or_the_list_changes()
    {
        var (reader, wmi, _, _) = Make();
        int changes = 0;
        reader.Changed += () => changes++;
        reader.ReadCurrent();
        Assert.Equal(1, changes);
        reader.ReadCurrent();
        Assert.Equal(1, changes);   // same answer
        wmi.OperationModeAnswer = 0x0B0400;
        reader.ReadCurrent();
        Assert.Equal(2, changes);
        reader.ReadSupported();
        Assert.Equal(3, changes);
        reader.ReadSupported();
        Assert.Equal(3, changes);
        Assert.Equal(new[] { OperationMode.Quiet, OperationMode.Default, OperationMode.Extreme, OperationMode.Turbo }, reader.Supported);
    }
}

public class PollSchedulerIdleTests
{
    [Fact]
    public void An_infinite_idle_interval_means_no_poll_at_all_until_somebody_looks()
    {
        int polls = 0;
        using var scheduler = new PollScheduler(() => Interlocked.Increment(ref polls), TimeSpan.FromMilliseconds(30), Timeout.InfiniteTimeSpan, _ => { });
        scheduler.Watched = false;
        scheduler.Start(Timeout.InfiniteTimeSpan);
        Thread.Sleep(300);
        Assert.Equal(0, polls);

        scheduler.Watched = true;
        scheduler.PollSoon();
        Assert.True(Wait.Until(() => Volatile.Read(ref polls) >= 3), "no polling after the scheduler was watched");

        // Not watched any more: the poll that is running or armed right now may still happen (once, and only one of the two can
        // exist), then the timer is armed with the infinite interval. Counting from this point makes the check independent of how
        // late the pool runs that last poll: a correct scheduler can never exceed the bound, a scheduler that keeps polling does.
        scheduler.Watched = false;
        int atUnwatch = Volatile.Read(ref polls);
        Thread.Sleep(600);
        Assert.InRange(Volatile.Read(ref polls), atUnwatch, atUnwatch + 1);
    }
}

/// <summary>The module: capability once at start, the mode only while somebody looks, a read-only row with a note, a metric, texts in both languages.</summary>
public class OperationModeModuleTests
{
    private static readonly MetricId Metric = new("predatorsense.opmode");

    [Fact]
    public void Start_reads_the_capability_once_and_the_mode_not_at_all_while_nobody_looks()
    {
        using var rig = new ModuleRig();
        Assert.Equal(new uint[] { 0x0A }, rig.Wmi.MiscReads);   // exactly one read, the capability
        Assert.False(rig.Module.OpModePolling);
        Assert.Null(rig.Host.MetricsBus.Latest(Metric));
        Assert.Equal("Quiet, Default, Extreme, Turbo", rig.ValueOf("row.opmodes.header"));
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public void Start_with_the_page_already_visible_reads_both_once_and_shows_Default()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Host.PageVisibleValue.Value = true;
        Assert.True(rig.Module.Probe().Supported);
        rig.Module.Initialize(rig.Host);
        try
        {
            Assert.Equal(new uint[] { 0x0A, 0x0B }, rig.Wmi.MiscReads.Take(2).ToArray());
            Assert.Equal("Default", rig.ValueOf("row.opmode.header"));
            Assert.Equal(1, rig.Host.MetricsBus.Latest(Metric)!.Value);
        }
        finally { rig.Stop(); }
    }

    [Fact]
    public void Showing_the_page_reads_the_mode_at_once_and_hiding_it_stops_the_polling()
    {
        using var rig = new ModuleRig();
        Assert.DoesNotContain(0x0Bu, rig.Wmi.MiscReads);

        rig.Host.PageVisibleValue.Value = true;
        Assert.True(rig.Module.OpModePolling);
        Assert.True(Wait.Until(() => rig.Wmi.MiscReads.Contains(0x0B), 30000), "no read of the mode after the page became visible");
        Assert.True(Wait.Until(() => rig.ValueOf("row.opmode.header") == "Default", 30000));
        Assert.Equal(1, rig.Host.MetricsBus.Latest(Metric)!.Value);

        rig.Host.PageVisibleValue.Value = false;
        Assert.False(rig.Module.OpModePolling);
    }

    [Fact]
    public void A_subscriber_of_the_mode_metric_counts_but_a_subscriber_of_a_sensor_does_not()
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetSubscribers(new MetricId("cpu.temp"), 1);
        Assert.False(rig.Module.OpModePolling);
        Assert.DoesNotContain(0x0Bu, rig.Wmi.MiscReads);

        rig.Host.MetricsBus.SetSubscribers(Metric, 1);
        Assert.True(rig.Module.OpModePolling);
        Assert.True(Wait.Until(() => rig.Wmi.MiscReads.Contains(0x0B), 30000));

        rig.Host.MetricsBus.SetSubscribers(Metric, 0);
        Assert.False(rig.Module.OpModePolling);
    }

    [Fact]
    public void After_shutdown_the_signals_no_longer_start_a_read()
    {
        var rig = new ModuleRig();
        rig.Stop();
        rig.Host.PageVisibleValue.Value = true;
        rig.Host.MetricsBus.SetSubscribers(Metric, 1);
        Assert.Null(rig.Module.OpModePolling);
        Thread.Sleep(100);
        Assert.DoesNotContain(0x0Bu, rig.Wmi.MiscReads);
    }

    [Fact]
    public void The_row_is_read_only_with_the_honest_note_and_no_selector_for_the_mode()
    {
        using var rig = new ModuleRig();
        var cooling = rig.Page.Groups.Single(g => g.HeaderKey == "group.cooling").Rows;
        Assert.Equal("row.opmode.header", cooling[0].HeaderKey);   // near the top of the group
        Assert.IsType<ValueRow>(cooling[0]);
        Assert.Equal("row.opmode.description", cooling[0].DescriptionKey);
        var note = Assert.Single(cooling.OfType<InfoRow>());
        Assert.Equal("info.opmode-note.header", note.HeaderKey);

        // The only selector on the page is the fan mode; nothing that changes the performance mode exists.
        var choice = Assert.Single(rig.Rows.OfType<ChoiceRow>());
        Assert.Equal("row.fan-mode.header", choice.HeaderKey);
        Assert.Equal("Read from the laptop", rig.Host.Loc.T(cooling[0].DescriptionKey!));
        Assert.Equal("Change it in PredatorSense for now", rig.Host.Loc.T(note.HeaderKey));
        Assert.Equal("Rigbay will offer this after a supervised test.", rig.Host.Loc.T(note.DescriptionKey!));
    }

    [Fact]
    public void The_metric_has_a_descriptor_with_a_label_and_a_description_in_both_languages()
    {
        using var rig = new ModuleRig();
        var d = Assert.Single(rig.Host.MetricsBus.Descriptors, x => x.Id == Metric);
        Assert.Equal("sensor.opmode", d.LabelKey);
        Assert.Equal("sensor.opmode.description", d.DescriptionKey);
        Assert.Equal(MetricUnit.None, d.Unit);
        foreach (var lang in new[] { "en", "vi" })
        {
            var strings = FakeLocalisation.Load(lang);
            Assert.True(strings.ContainsKey("module.predatorsense." + d.LabelKey));
            Assert.True(strings.ContainsKey("module.predatorsense." + d.DescriptionKey));
        }
    }

    [Theory]
    [InlineData(0x0B0000UL, "Quiet")]
    [InlineData(0x0B0100UL, "Default")]
    [InlineData(0x0B0400UL, "Extreme")]
    [InlineData(0x0B0500UL, "Turbo")]
    [InlineData(0x0B0200UL, "Render")]
    [InlineData(0x0B0300UL, "Edit")]
    [InlineData(0x0B0900UL, "Unknown")]
    [InlineData(0x0B01E1UL, "Unknown")]
    public async Task The_page_and_the_status_verb_show_what_the_firmware_said_and_Unknown_otherwise(ulong raw, string expected)
    {
        using var rig = new ModuleRig();
        rig.Wmi.OperationModeAnswer = raw;
        var result = await rig.Host.CommandRegistry.Run("status");
        Assert.Contains("Performance mode: " + expected, result.Output);
        Assert.Equal(expected, rig.ValueOf("row.opmode.header"));
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task A_change_made_in_the_acer_app_shows_up_with_the_next_read()
    {
        using var rig = new ModuleRig();
        rig.Host.PageVisibleValue.Value = true;
        Assert.True(Wait.Until(() => rig.ValueOf("row.opmode.header") == "Default", 30000));
        rig.Wmi.OperationModeAnswer = 0x0B0500;
        await rig.Host.CommandRegistry.Run("status");   // a fresh read, standing in for the 10 s poll
        Assert.Equal("Turbo", rig.ValueOf("row.opmode.header"));
        Assert.Equal(5, rig.Host.MetricsBus.Latest(Metric)!.Value);
    }

    [Fact]
    public void A_capability_without_Render_or_Edit_lists_only_what_the_laptop_has_and_a_failed_read_says_Unknown()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Wmi.SupportedModesAnswer = 0x0B01E1;   // status non-zero
        Assert.True(rig.Module.Probe().Supported);
        rig.Module.Initialize(rig.Host);
        try { Assert.Equal("Unknown", rig.ValueOf("row.opmodes.header")); }
        finally { rig.Stop(); }

        var all = new ModuleRig(initialize: false);
        all.Wmi.SupportedModesAnswer = 0x0B3F00;
        Assert.True(all.Module.Probe().Supported);
        all.Module.Initialize(all.Host);
        try { Assert.Equal("Quiet, Default, Render, Edit, Extreme, Turbo", all.ValueOf("row.opmodes.header")); }
        finally { all.Stop(); }
    }

    [Fact]
    public void The_texts_follow_the_language_and_both_languages_have_every_key()
    {
        using var rig = new ModuleRig();
        rig.Host.PageVisibleValue.Value = true;
        Assert.True(Wait.Until(() => rig.ValueOf("row.opmode.header") == "Default", 30000));
        rig.Host.Loc.Switch("vi");
        Assert.Equal("Mặc định", rig.ValueOf("row.opmode.header"));
        Assert.Equal("Yên tĩnh, Mặc định, Extreme, Turbo", rig.ValueOf("row.opmodes.header"));

        foreach (var key in new[] { "row.opmode.header", "row.opmode.description", "row.opmodes.header", "row.opmodes.description", "info.opmode-note.header",
            "info.opmode-note.description", "opmode.quiet", "opmode.default", "opmode.render", "opmode.edit", "opmode.extreme", "opmode.turbo", "opmode.unknown",
            "sensor.opmode", "sensor.opmode.description", "status.opmode", "group.cooling" })
            foreach (var lang in new[] { "en", "vi" })
                Assert.True(FakeLocalisation.Load(lang).ContainsKey("module.predatorsense." + key), $"{lang}: {key}");
    }

    [Fact]
    public void Nothing_in_the_language_files_says_the_performance_mode_cannot_be_read()
    {
        foreach (var lang in new[] { "en", "vi" })
            foreach (var (key, text) in FakeLocalisation.Load(lang))
            {
                bool aboutMode = key.Contains("opmode", StringComparison.Ordinal) || text.Contains("performance mode", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("chế độ hiệu năng", StringComparison.OrdinalIgnoreCase);
                if (aboutMode) Assert.DoesNotContain("cannot", text, StringComparison.OrdinalIgnoreCase);
            }
    }
}
