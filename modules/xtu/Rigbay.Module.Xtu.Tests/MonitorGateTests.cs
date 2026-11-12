using Rigbay.Abstractions;
using Rigbay.Module.Xtu;
using Xunit;

namespace Rigbay.Module.Xtu.Tests;

/// <summary>M1: labels, revert questions, a monitor that is read only while somebody looks, and withdrawn readings.</summary>
public class MetricLabelTests
{
    internal static Dictionary<uint, decimal> EveryMonitorValue()
    {
        var all = new Dictionary<uint, decimal>();
        foreach (var f in typeof(Mon).GetFields().Where(f => f.FieldType == typeof(uint))) all[(uint)f.GetRawConstantValue()!] = 5m;
        for (int i = 0; i < Mon.Cores; i++)
        {
            all[Mon.CoreTemp0 + (uint)i] = 50; all[Mon.CoreFreq0 + (uint)i] = 4; all[Mon.CoreVolt0 + (uint)i] = 1; all[Mon.CoreUtil0 + (uint)i] = 20;
        }
        return all;
    }

    [Fact]
    public void Every_metric_the_module_can_publish_has_a_descriptor_with_a_label_in_both_languages()
    {
        using var rig = new Rig();
        var described = rig.Host.FakeMetrics.Descriptors;
        Assert.Equal(XtuMetrics.All.Select(i => i.Value).OrderBy(s => s), described.Select(d => d.Id.Value).OrderBy(s => s));
        Assert.Equal(described.Count, described.Select(d => d.Id).Distinct().Count());
        Assert.Equal(54, described.Count);   // 20 monitor ids + PL1 + PL2 + 32 per-core

        var en = new FakeLocalisation("en.lang");
        var vi = new FakeLocalisation("vi.lang");
        foreach (var d in described)
        {
            Assert.True(en.Has(d.LabelKey), "en: " + d.LabelKey);
            Assert.True(vi.Has(d.LabelKey), "vi: " + d.LabelKey);
            Assert.NotEqual(en.T(d.LabelKey), vi.T(d.LabelKey));
        }
    }

    [Fact]
    public void A_descriptor_unit_is_the_unit_the_sampler_and_the_state_refresh_publish()
    {
        using var rig = new Rig();
        rig.Runtime.Sampler.PerCore = true;
        rig.Client.Monitor.Enqueue((EveryMonitorValue(), false));
        rig.Runtime.MonitorOnce();

        var published = rig.Host.FakeMetrics.Published.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Last().Unit);
        Assert.Equal(54, published.Count);
        foreach (var d in rig.Host.FakeMetrics.Descriptors)
            Assert.True(published.TryGetValue(d.Id, out var unit) && unit == d.Unit, $"{d.Id.Value}: published unit differs from the descriptor ({d.Unit})");
    }

    [Fact]
    public void The_dashboard_readings_have_labels()
    {
        using var rig = new Rig();
        var ids = rig.Runtime.BuildPage().Summary!.Readings!;
        Assert.NotEmpty(ids);
        foreach (var id in ids) Assert.Contains(rig.Host.FakeMetrics.Descriptors, d => d.Id == id);
    }
}

public class RevertTextTests
{
    [Fact]
    public void Every_hardware_row_asks_for_a_revert_and_has_its_own_revert_text_in_both_languages()
    {
        using var rig = new Rig();
        var rows = rig.Page.EnumerateRows().Where(r => r.HardwareEffect is not null).ToList();
        Assert.Equal(4, rows.Count);   // profile, auto-switch, AC choice, battery choice

        var en = new FakeLocalisation("en.lang");
        var vi = new FakeLocalisation("vi.lang");
        foreach (var row in rows)
        {
            var effect = row.HardwareEffect!;
            Assert.False(effect.SafeRevert, row.HeaderKey);   // putting the old limits back can raise them: asked
            Assert.NotNull(effect.RevertDescriptionKey);
            // the profile choice with bare names builds its texts from these keys plus the limits of every profile
            var (key, revertKey) = Text.IsLiteral(effect.DescriptionKey) ? (ApplyService.EffectKey, "effect.apply-profile-revert") : (effect.DescriptionKey, effect.RevertDescriptionKey!);
            Assert.True(en.Has(revertKey) && vi.Has(revertKey), revertKey);
            Assert.True(en.Has(key) && vi.Has(key), key);
            Assert.NotEqual(en.T(key), en.T(revertKey));
            Assert.NotEqual(effect.DescriptionKey, effect.RevertDescriptionKey);
        }
    }

    [Fact]
    public void No_button_row_changes_hardware_so_no_revert_button_text_is_needed()
    {
        using var rig = new Rig(settings: s => s.Customs.Add(new CustomProfile { Name = "Mine", Pl1 = 40, Pl2 = 50, Window = 20 }));
        Assert.All(rig.Page.EnumerateRows().OfType<ButtonRow>(), b => { Assert.Null(b.HardwareEffect); Assert.Null(b.RevertButtonKey); });
    }
}

public class MonitorGateTests
{
    private static Dictionary<uint, decimal> Sample(decimal power) => new() { [Mon.PkgPower] = power, [Mon.PkgTemp] = 60, [Mon.MaxPFreq] = 4 };

    private static Rig Started()
    {
        var rig = new Rig();
        rig.Client.Monitor.Enqueue((Sample(20), true));
        rig.Client.Monitor.Enqueue((Sample(21), false));
        return rig;
    }

    [Fact]
    public void Nobody_looking_means_not_wanted_and_the_page_or_any_monitor_subscriber_means_wanted()
    {
        using var rig = new Rig();
        Assert.False(rig.Runtime.MonitorWanted());

        rig.Host.PageVisibleValue.Value = true;
        Assert.True(rig.Runtime.MonitorWanted());
        rig.Host.PageVisibleValue.Value = false;
        Assert.False(rig.Runtime.MonitorWanted());

        rig.Host.FakeMetrics.SetSubscribers(WellKnownMetrics.CpuTemp, 1);
        Assert.True(rig.Runtime.MonitorWanted());
        rig.Host.FakeMetrics.SetSubscribers(WellKnownMetrics.CpuTemp, 0);
        rig.Host.FakeMetrics.SetSubscribers(XtuMetrics.ThrottleVr, 1);
        Assert.True(rig.Runtime.MonitorWanted());
        rig.Host.FakeMetrics.SetSubscribers(XtuMetrics.ThrottleVr, 0);
        Assert.False(rig.Runtime.MonitorWanted());
    }

    [Fact]
    public void A_per_core_subscriber_counts_only_when_the_per_core_option_is_on()
    {
        using var off = new Rig();
        off.Host.FakeMetrics.SetSubscribers(XtuMetrics.CoreTemp(3), 1);
        Assert.False(off.Runtime.MonitorWanted());   // nothing publishes it

        using var on = new Rig(settings: s => s.PublishPerCore = true);
        on.Host.FakeMetrics.SetSubscribers(XtuMetrics.CoreTemp(3), 1);
        Assert.True(on.Runtime.MonitorWanted());
    }

    [Fact]
    public async Task The_monitor_thread_does_not_read_XTU_while_nobody_looks_and_reads_once_the_page_is_shown()
    {
        using var rig = Started();
        rig.Runtime.StartTimers();
        await Task.Delay(1500);
        Assert.Equal(0, rig.Client.MonitorReads);
        Assert.Null(rig.Host.FakeMetrics.Last("cpu.temp"));

        rig.Host.PageVisibleValue.Value = true;
        Assert.True(await Rig.WaitFor(() => rig.Host.FakeMetrics.Last("cpu.temp") == 60, 30000), "no reading after the page was shown");
        Assert.True(rig.Client.MonitorResets >= 1);   // a new warm-up after the pause
    }

    [Fact]
    public async Task A_subscriber_wakes_the_monitor_and_the_last_one_leaving_stops_it_again_and_the_bus_keeps_the_last_value()
    {
        using var rig = Started();
        rig.Runtime.StartTimers();
        rig.Host.FakeMetrics.SetSubscribers(WellKnownMetrics.CpuPower, 1);
        Assert.True(await Rig.WaitFor(() => rig.Host.FakeMetrics.Last("cpu.power") is not null, 30000));

        rig.Host.FakeMetrics.SetSubscribers(WellKnownMetrics.CpuPower, 0);
        await Task.Delay(400);   // a read that was already running ends
        int reads = rig.Client.MonitorReads;
        await Task.Delay(1500);
        Assert.Equal(reads, rig.Client.MonitorReads);
        Assert.Empty(rig.Host.FakeMetrics.Withdrawn);          // idle is not a failure: nothing is taken off the bus
        Assert.True(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuPower));
    }

    [Fact]
    public void Three_failed_monitor_reads_in_a_row_withdraw_every_monitor_reading_once()
    {
        using var rig = new Rig();
        rig.Runtime.Sampler.PerCore = true;
        rig.Client.Monitor.Enqueue((MetricLabelTests.EveryMonitorValue(), false));
        rig.Runtime.MonitorOnce();
        var published = rig.Host.FakeMetrics.Published.Select(p => p.Id).Distinct().ToList();
        Assert.True(published.Count > 15);

        rig.Runtime.MonitorFailed();
        rig.Runtime.MonitorFailed();
        Assert.Empty(rig.Host.FakeMetrics.Withdrawn);
        rig.Runtime.MonitorFailed();
        Assert.Equal(published.Count - 2, rig.Host.FakeMetrics.Withdrawn.Count);   // PL1 and PL2 come from the state refresh, not the monitor
        Assert.All(published.Where(id => id != XtuMetrics.Pl1 && id != XtuMetrics.Pl2), id => Assert.False(rig.Host.FakeMetrics.IsCurrent(id)));

        int after = rig.Host.FakeMetrics.Withdrawn.Count;
        rig.Runtime.MonitorFailed();   // a fourth failure does nothing more
        Assert.Equal(after, rig.Host.FakeMetrics.Withdrawn.Count);
    }

    [Fact]
    public void A_good_read_between_failures_starts_the_count_again_and_readings_come_back_by_publishing()
    {
        using var rig = new Rig();
        rig.Client.Monitor.Enqueue((Sample(20), false));
        rig.Runtime.MonitorOnce();
        rig.Runtime.MonitorFailed();
        rig.Runtime.MonitorFailed();
        rig.Runtime.MonitorSucceeded();
        rig.Runtime.MonitorFailed();
        rig.Runtime.MonitorFailed();
        Assert.Empty(rig.Host.FakeMetrics.Withdrawn);

        rig.Runtime.MonitorFailed();
        Assert.False(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuTemp));
        rig.Runtime.MonitorOnce();   // XTU answers again
        Assert.True(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuTemp));
    }

    [Fact]
    public void PL1_and_PL2_leave_the_bus_when_the_state_cannot_be_read_three_times_and_return_when_it_can()
    {
        using var rig = new Rig();
        Assert.True(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuPl1));

        rig.Client.ReadError = new XtuException("err.unavailable", "gone");
        rig.Runtime.Refresh();
        rig.Runtime.Refresh();
        Assert.True(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuPl1));
        rig.Runtime.Refresh();
        Assert.False(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuPl1));
        Assert.False(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuPl2));

        rig.Client.ReadError = null;
        rig.Runtime.Refresh();
        Assert.True(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuPl1));
        Assert.True(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuPl2));
    }

    [Fact]
    public void After_dispose_the_signals_are_not_followed()
    {
        var rig = new Rig();
        rig.Dispose();
        rig.Host.PageVisibleValue.Value = true;
        rig.Host.FakeMetrics.SetSubscribers(WellKnownMetrics.CpuTemp, 1);   // must not throw
    }
}
