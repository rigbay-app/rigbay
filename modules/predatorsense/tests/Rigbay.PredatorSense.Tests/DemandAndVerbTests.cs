using Rigbay.Abstractions;
using Rigbay.PredatorSense.Sensors;
using Xunit;

namespace Rigbay.PredatorSense.Tests;

/// <summary>Host API 0.1.4: the sensors and the performance mode follow <see cref="MetricDemand"/> (nothing, page, subscriber, background).</summary>
public class DemandTests
{
    private static readonly MetricId Cpu = new("cpu.temp");
    private static readonly MetricId Mode = new("predatorsense.opmode");

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(2);

    // ---- sensors: fast = the poll rate (2 s), idle = 10 s

    [Fact]
    public void Nothing_asking_reads_the_sensors_at_the_idle_rate()
    {
        using var rig = new ModuleRig();
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void Page_and_subscriber_read_at_the_poll_rate_even_with_a_slower_background_demand()
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(Cpu, TimeSpan.FromSeconds(60));
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
        rig.Host.PageVisibleValue.Value = true;
        Assert.Equal(Fast, rig.Module.CurrentPollInterval);
        rig.Host.PageVisibleValue.Value = false;
        rig.Host.MetricsBus.SetSubscribers(new MetricId("fan.cpu.rpm"), 1);
        Assert.Equal(Fast, rig.Module.CurrentPollInterval);
    }

    [Theory]
    [InlineData(30, 10)]   // slower than idle: not slower than the idle rate (the sensors are read every 10 s anyway)
    [InlineData(5, 5)]     // between the poll rate and idle: the asked interval
    [InlineData(1, 2)]     // faster than the poll rate: the poll rate
    public void A_background_demand_reads_at_max_of_the_poll_rate_and_the_asked_interval_and_at_most_idle(int asked, int expected)
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(Cpu, TimeSpan.FromSeconds(asked));
        Assert.Equal(TimeSpan.FromSeconds(expected), rig.Module.CurrentPollInterval);
        rig.Host.MetricsBus.SetBackground(Cpu, null);
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void The_poll_rate_setting_is_the_floor_of_a_background_demand()
    {
        using var rig = new ModuleRig();
        var row = rig.Rows.OfType<NumberRow>().Single();
        ((ObservableValue<double>)row.Value).Value = 8;
        rig.Host.MetricsBus.SetBackground(Cpu, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(8), rig.Module.CurrentPollInterval);
        ((ObservableValue<double>)row.Value).Value = 3;   // the setting moves: the demand is evaluated again
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void A_faster_background_demand_reads_at_once_instead_of_waiting_for_the_armed_timer()
    {
        using var rig = new ModuleRig();
        int before = rig.Wmi.Reads.Count;
        rig.Host.MetricsBus.SetBackground(Cpu, TimeSpan.FromSeconds(5));   // 10 s timer armed, 5 s asked
        Assert.True(Wait.Until(() => rig.Wmi.Reads.Count > before, 30000), "no read after a faster background demand");
    }

    [Fact]
    public void A_background_demand_on_somebody_elses_metric_changes_nothing()
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(new MetricId("gpu.nvml.temp"), TimeSpan.FromSeconds(5));
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
        Assert.False(rig.Module.OpModePolling);
    }

    // ---- the performance mode: fast = 10 s, idle = not at all

    [Fact]
    public void Nothing_asking_does_not_read_the_mode_at_all()
    {
        using var rig = new ModuleRig();
        Assert.False(rig.Module.OpModePolling);
        Assert.DoesNotContain(0x0Bu, rig.Wmi.MiscReads);
    }

    [Fact]
    public void A_background_demand_on_the_mode_starts_reading_it_at_once_and_stops_when_it_is_dropped()
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(Mode, TimeSpan.FromSeconds(30));
        Assert.True(rig.Module.OpModePolling);
        Assert.True(Wait.Until(() => rig.Wmi.MiscReads.Contains(0x0B), 30000), "the mode was not read under a background demand");
        Assert.True(Wait.Until(() => rig.Host.MetricsBus.Latest(Mode) is not null, 30000));

        rig.Host.MetricsBus.SetBackground(Mode, null);
        Assert.False(rig.Module.OpModePolling);
        Thread.Sleep(150);   // a read that was running ends
        int reads = rig.Wmi.MiscReads.Count(r => r == 0x0B);
        Thread.Sleep(300);
        Assert.Equal(reads, rig.Wmi.MiscReads.Count(r => r == 0x0B));
    }

    [Fact]
    public void A_background_demand_on_the_mode_before_the_module_starts_is_read_at_start()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Host.MetricsBus.SetBackground(Mode, TimeSpan.FromSeconds(30));
        Assert.True(rig.Module.Probe().Supported);
        rig.Module.Initialize(rig.Host);
        try
        {
            Assert.True(rig.Module.OpModePolling);
            Assert.Equal(1, rig.Host.MetricsBus.Latest(Mode)!.Value);
        }
        finally { rig.Stop(); }
    }

    [Fact]
    public void A_sensor_demand_does_not_wake_the_mode_and_a_mode_demand_does_not_speed_the_sensors()
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(Cpu, TimeSpan.FromSeconds(5));
        Assert.False(rig.Module.OpModePolling);
        rig.Host.MetricsBus.SetBackground(Cpu, null);

        rig.Host.MetricsBus.SetBackground(Mode, TimeSpan.FromSeconds(5));
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void After_shutdown_a_background_demand_starts_nothing()
    {
        var rig = new ModuleRig();
        rig.Stop();
        int reads = rig.Wmi.MiscReads.Count;
        rig.Host.MetricsBus.SetBackground(Mode, TimeSpan.FromSeconds(5));
        Assert.Null(rig.Module.OpModePolling);
        Thread.Sleep(100);
        Assert.Equal(reads, rig.Wmi.MiscReads.Count);
    }

    // ---- the scheduler

    [Fact]
    public void A_scheduler_with_a_background_interval_polls_at_it_and_stops_when_it_is_cleared()
    {
        int polls = 0;
        using var scheduler = new PollScheduler(() => Interlocked.Increment(ref polls), TimeSpan.FromMilliseconds(30), Timeout.InfiniteTimeSpan, _ => { });
        scheduler.Watched = false;
        scheduler.Start(Timeout.InfiniteTimeSpan);
        Assert.False(scheduler.IsPolling);
        Thread.Sleep(150);
        Assert.Equal(0, polls);

        scheduler.BackgroundInterval = TimeSpan.FromMilliseconds(40);
        Assert.True(scheduler.IsPolling);
        Assert.Equal(TimeSpan.FromMilliseconds(40), scheduler.CurrentInterval);
        scheduler.PollSoon();
        Assert.True(Wait.Until(() => Volatile.Read(ref polls) >= 3, 10000), "no polling at the background interval");

        scheduler.BackgroundInterval = null;
        Assert.False(scheduler.IsPolling);
        Thread.Sleep(150);
        int at = Volatile.Read(ref polls);
        Thread.Sleep(300);
        Assert.InRange(Volatile.Read(ref polls), at, at + 1);
    }

    [Fact]
    public void Watched_beats_a_background_interval()
    {
        using var scheduler = new PollScheduler(() => { }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), _ => { });
        scheduler.BackgroundInterval = TimeSpan.FromSeconds(5);
        scheduler.Watched = true;
        Assert.Equal(TimeSpan.FromSeconds(2), scheduler.CurrentInterval);
        scheduler.Watched = false;
        Assert.Equal(TimeSpan.FromSeconds(5), scheduler.CurrentInterval);
    }
}

/// <summary>Host API 0.1.4: the verbs say what they do; the preview is the argument's own meaning; an undo is reported only when the mode changed.</summary>
public class VerbMetadataTests
{
    [Fact]
    public void Status_is_read_only_and_fan_writes_hardware_and_undoes_with_fan()
    {
        using var rig = new ModuleRig();
        var infos = rig.Host.CommandRegistry.Infos;
        Assert.Equal(new[] { "fan", "status" }, infos.Keys.OrderBy(k => k));

        Assert.Equal(VerbAccess.ReadOnly, infos["status"].Access);
        Assert.Null(infos["status"].Effect);

        var fan = infos["fan"];
        Assert.Equal(VerbAccess.Writes, fan.Access);
        Assert.Equal("effect.fan-mode", fan.Effect!.DescriptionKey);
        Assert.Equal(EffectKind.Hardware, fan.Effect.Kind);
        Assert.Equal("fan", fan.UndoVerb);
        Assert.True(FakeLocalisation.Load("en").ContainsKey("module.predatorsense." + fan.UsageKey));
        Assert.NotNull(fan.Preview);
    }

    [Fact]
    public async Task The_declared_effect_is_the_effect_the_verb_asks_the_owner_about()
    {
        using var rig = new ModuleRig(approve: true);
        var declared = rig.Host.CommandRegistry.Infos["fan"].Effect!;
        await rig.Host.CommandRegistry.Run("fan", "max");
        Assert.Equal(declared, rig.Host.ApprovalService.Asked.Single().Effect);
    }

    [Theory]
    [InlineData("en", "max", "Fan mode: Maximum")]
    [InlineData("en", "maximum", "Fan mode: Maximum")]
    [InlineData("en", "AUTO", "Fan mode: Auto")]
    [InlineData("vi", "max", "Chế độ quạt: Tối đa")]
    public void The_preview_says_what_the_argument_selects_in_both_languages(string language, string arg, string expectedStart)
    {
        using var rig = new ModuleRig(language);
        var text = rig.Host.CommandRegistry.Infos["fan"].Preview!(new[] { arg })!.Value;
        Assert.StartsWith(expectedStart, rig.Host.Loc.Resolve(text));
    }

    [Fact]
    public void A_preview_of_a_bad_argument_is_none()
    {
        using var rig = new ModuleRig();
        var preview = rig.Host.CommandRegistry.Infos["fan"].Preview!;
        Assert.Null(preview(Array.Empty<string>()));
        Assert.Null(preview(new[] { "turbo" }));
        Assert.Null(preview(new[] { "max", "auto" }));
    }

    [Fact]
    public async Task The_preview_does_not_depend_on_what_the_fans_do_now()
    {
        using var rig = new ModuleRig(approve: true);
        var preview = rig.Host.CommandRegistry.Infos["fan"].Preview!;
        var before = preview(new[] { "max" })!.Value;

        await rig.Host.CommandRegistry.Run("fan", "max");   // the live state changes (Rigbay set Maximum, the fans sped up)
        var after = preview(new[] { "max" })!.Value;

        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.Args, after.Args);
        Assert.Empty(rig.Wmi.Writes.Skip(1));   // and previewing wrote nothing
    }

    [Fact]
    public async Task Max_after_nothing_set_reports_auto_as_the_way_back_and_auto_after_max_reports_max()
    {
        using var rig = new ModuleRig(approve: true);
        var max = await rig.Host.CommandRegistry.Run("fan", "max");
        Assert.Equal(0, max.ExitCode);
        Assert.Equal(new[] { "auto" }, max.UndoArgs);

        var auto = await rig.Host.CommandRegistry.Run("fan", "auto");
        Assert.Equal(0, auto.ExitCode);
        Assert.Equal(new[] { "max" }, auto.UndoArgs);
    }

    [Fact]
    public async Task Auto_when_nothing_was_set_and_a_repeat_of_the_same_mode_report_no_undo()
    {
        using var rig = new ModuleRig(approve: true);
        var auto = await rig.Host.CommandRegistry.Run("fan", "auto");
        Assert.Equal(0, auto.ExitCode);
        Assert.Null(auto.UndoArgs);   // nothing known to put back

        await rig.Host.CommandRegistry.Run("fan", "max");
        var again = await rig.Host.CommandRegistry.Run("fan", "max");
        Assert.Null(again.UndoArgs);  // the mode did not change
    }

    [Fact]
    public async Task Declined_refused_and_bad_runs_report_no_undo()
    {
        using var declined = new ModuleRig(approve: false);
        Assert.Null((await declined.Host.CommandRegistry.Run("fan", "max")).UndoArgs);

        using var refused = new ModuleRig(approve: true);
        refused.Wmi.WriteAnswer = 0x03;
        var r = await refused.Host.CommandRegistry.Run("fan", "max");
        Assert.Equal(1, r.ExitCode);
        Assert.Null(r.UndoArgs);

        using var bad = new ModuleRig(approve: true);
        Assert.Null((await bad.Host.CommandRegistry.Run("fan", "turbo")).UndoArgs);
    }
}
