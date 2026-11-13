using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Module.Xtu.Tests;

/// <summary>Host API 0.1.4: the monitor follows <see cref="MetricDemand"/> (nobody, page, subscriber, background).</summary>
public class MonitorDemandTests
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
    public void Nobody_asking_is_idle_and_idle_means_do_not_read()
    {
        using var rig = new Rig();
        var d = rig.Runtime.MonitorDemand();
        Assert.Equal(DemandKind.Idle, d.Kind);
        Assert.Equal(Timeout.InfiniteTimeSpan, d.Interval);
        Assert.False(rig.Runtime.MonitorWanted());
    }

    [Fact]
    public void The_page_or_a_subscriber_reads_every_second()
    {
        using var rig = new Rig();
        rig.Host.PageVisibleValue.Value = true;
        Assert.Equal(new DemandState(DemandKind.Fast, TimeSpan.FromSeconds(1)), rig.Runtime.MonitorDemand());
        rig.Host.PageVisibleValue.Value = false;

        rig.Host.FakeMetrics.SetSubscribers(WellKnownMetrics.CpuTemp, 1);
        Assert.Equal(new DemandState(DemandKind.Fast, TimeSpan.FromSeconds(1)), rig.Runtime.MonitorDemand());
    }

    [Fact]
    public void A_subscriber_beats_a_slower_background_demand()
    {
        using var rig = new Rig();
        rig.Host.FakeMetrics.SetBackground(WellKnownMetrics.CpuTemp, TimeSpan.FromSeconds(60));
        rig.Host.FakeMetrics.SetSubscribers(XtuMetrics.CpuPower, 1);
        Assert.Equal(new DemandState(DemandKind.Fast, TimeSpan.FromSeconds(1)), rig.Runtime.MonitorDemand());
    }

    [Theory]
    [InlineData(30, 30)]       // background 30 s: read every 30 s
    [InlineData(0.5, 1)]       // faster than the fast rate: the fast rate
    [InlineData(3600, 3600)]   // slower than any idle rate: idle is "do not read", so the asked interval holds
    public void A_background_demand_reads_at_max_of_one_second_and_the_asked_interval(double asked, double expected)
    {
        using var rig = new Rig();
        rig.Host.FakeMetrics.SetBackground(WellKnownMetrics.CpuTemp, TimeSpan.FromSeconds(asked));
        Assert.Equal(new DemandState(DemandKind.Background, TimeSpan.FromSeconds(expected)), rig.Runtime.MonitorDemand());
        Assert.True(rig.Runtime.MonitorWanted());
    }

    [Fact]
    public void The_shortest_background_demand_wins()
    {
        using var rig = new Rig();
        rig.Host.FakeMetrics.SetBackground(XtuMetrics.ThrottleVr, TimeSpan.FromSeconds(60));
        rig.Host.FakeMetrics.SetBackground(XtuMetrics.CpuTemp, TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), rig.Runtime.MonitorDemand().Interval);
    }

    [Fact]
    public void A_background_demand_on_a_per_core_metric_counts_only_when_the_per_core_option_is_on()
    {
        using var off = new Rig();
        off.Host.FakeMetrics.SetBackground(XtuMetrics.CoreTemp(2), TimeSpan.FromSeconds(10));
        Assert.Equal(DemandKind.Idle, off.Runtime.MonitorDemand().Kind);

        using var on = new Rig(settings: s => s.PublishPerCore = true);
        on.Host.FakeMetrics.SetBackground(XtuMetrics.CoreTemp(2), TimeSpan.FromSeconds(10));
        Assert.Equal(DemandKind.Background, on.Runtime.MonitorDemand().Kind);
    }

    [Fact]
    public void A_background_demand_on_somebody_elses_metric_does_nothing()
    {
        using var rig = new Rig();
        rig.Host.FakeMetrics.SetBackground(new MetricId("gpu.nvml.temp"), TimeSpan.FromSeconds(10));
        Assert.Equal(DemandKind.Idle, rig.Runtime.MonitorDemand().Kind);
    }

    [Fact]
    public async Task The_monitor_thread_starts_reading_under_a_background_demand_and_stops_when_it_goes()
    {
        using var rig = Started();
        rig.Runtime.StartTimers();
        await Task.Delay(1200);
        Assert.Equal(0, rig.Client.MonitorReads);

        rig.Host.FakeMetrics.SetBackground(WellKnownMetrics.CpuTemp, TimeSpan.FromSeconds(30));
        // the first read only starts the warm-up; with a 30 s demand the second one must not wait 30 s
        Assert.True(await Rig.WaitFor(() => rig.Host.FakeMetrics.Last("cpu.temp") == 60, 15000), "no reading under a background demand");
        Assert.True(rig.Client.MonitorResets >= 1);

        rig.Host.FakeMetrics.SetBackground(WellKnownMetrics.CpuTemp, null);
        await Task.Delay(400);   // a read that was already running ends
        int reads = rig.Client.MonitorReads;
        await Task.Delay(1800);
        Assert.Equal(reads, rig.Client.MonitorReads);
        Assert.Empty(rig.Host.FakeMetrics.Withdrawn);          // idle is not a failure
        Assert.True(rig.Host.FakeMetrics.IsCurrent(WellKnownMetrics.CpuTemp));
    }

    [Fact]
    public async Task Under_a_background_demand_the_monitor_does_not_read_every_second()
    {
        using var rig = Started();
        rig.Host.FakeMetrics.SetBackground(WellKnownMetrics.CpuTemp, TimeSpan.FromSeconds(30));
        rig.Runtime.StartTimers();
        Assert.True(await Rig.WaitFor(() => rig.Host.FakeMetrics.Last("cpu.temp") == 60, 15000));
        int reads = rig.Client.MonitorReads;
        await Task.Delay(2500);
        Assert.Equal(reads, rig.Client.MonitorReads);   // the next one is 30 s away
    }

    [Fact]
    public async Task Showing_the_page_while_a_slow_background_demand_waits_reads_at_once()
    {
        using var rig = Started();
        rig.Host.FakeMetrics.SetBackground(WellKnownMetrics.CpuTemp, TimeSpan.FromSeconds(30));
        rig.Runtime.StartTimers();
        Assert.True(await Rig.WaitFor(() => rig.Host.FakeMetrics.Last("cpu.temp") == 60, 15000));
        int reads = rig.Client.MonitorReads;

        rig.Host.PageVisibleValue.Value = true;
        Assert.True(await Rig.WaitFor(() => rig.Client.MonitorReads > reads, 5000), "the page did not speed the monitor up");
    }

    [Fact]
    public void Disposing_the_runtime_ends_the_monitor_thread_even_under_a_demand()
    {
        var rig = Started();
        rig.Host.FakeMetrics.SetBackground(WellKnownMetrics.CpuTemp, TimeSpan.FromSeconds(30));
        rig.Runtime.StartTimers();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        rig.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "Dispose waited for the 30 s slot");
    }
}

/// <summary>Host API 0.1.4: the verbs say what they do, the preview is the profile's own values, an undo is reported only when something changed.</summary>
public class XtuVerbMetadataTests
{
    [Fact]
    public void Status_and_list_are_read_only_and_apply_writes_hardware_and_undoes_with_apply()
    {
        using var rig = new Rig();
        var infos = rig.Host.FakeCommands.Infos;
        Assert.Equal(new[] { "apply", "list", "status" }, infos.Keys.OrderBy(k => k));
        Assert.Equal(VerbAccess.ReadOnly, infos["status"].Access);
        Assert.Equal(VerbAccess.ReadOnly, infos["list"].Access);
        Assert.Null(infos["status"].Effect);
        Assert.Null(infos["list"].UndoVerb);

        var apply = infos["apply"];
        Assert.Equal(VerbAccess.Writes, apply.Access);
        Assert.Equal(ApplyService.EffectKey, apply.Effect!.DescriptionKey);
        Assert.Equal(EffectKind.Hardware, apply.Effect.Kind);   // the kind the verb's own question asks with
        Assert.Equal("apply", apply.UndoVerb);
        Assert.NotNull(apply.Preview);
        Assert.True(new FakeLocalisation("en.lang").Has(apply.UsageKey!));
    }

    [Fact]
    public async Task The_declared_effect_is_the_effect_the_verb_asks_the_owner_about()
    {
        using var rig = new Rig();
        var declared = rig.Host.FakeCommands.Infos["apply"].Effect!;
        await rig.Host.FakeCommands.Run("apply", "PL35W");   // the fake approval records the effect it was asked about
        Assert.Equal(declared, rig.Host.FakeApproval.Asked.Single().Effect);
    }

    [Fact]
    public void The_preview_is_the_profiles_values_in_both_languages_and_follows_the_profile_list()
    {
        using var rig = new Rig();
        var preview = rig.Host.FakeCommands.Infos["apply"].Preview!;
        var en = new FakeLocalisation("en.lang");
        var vi = new FakeLocalisation("vi.lang");

        var text = preview(new[] { "pl35w" })!.Value;
        Assert.Equal("PL35W: PL1 35 W · PL2 40 W · 28 s", en.Resolve(text));
        Assert.Contains("PL1 35 W", vi.Resolve(text));
        Assert.Equal(text.Key, preview(new[] { "PL35W" })!.Value.Key);   // the profile's own name, so a case difference in the arguments changes nothing
        Assert.Equal(text.Args, preview(new[] { "PL35W" })!.Value.Args);
        Assert.Equal("Default (BIOS): PL1 90 W · PL2 115 W · 56 s", en.Resolve(preview(new[] { "Default", "(BIOS)" })!.Value));
        Assert.Null(preview(new[] { "no-such-profile" }));
        Assert.Null(preview(Array.Empty<string>()));
    }

    [Fact]
    public void A_preview_with_pl2_off_says_so()
    {
        using var rig = new Rig(settings: s => s.Customs.Add(new CustomProfile { Name = "NoBoost", Pl1 = 30, Pl2 = 30, Pl2Enable = false, Window = 20 }));
        var text = rig.Host.FakeCommands.Infos["apply"].Preview!(new[] { "NoBoost" })!.Value;
        Assert.Equal("NoBoost: PL1 30 W · PL2 off · 20 s", new FakeLocalisation("en.lang").Resolve(text));
    }

    [Fact]
    public async Task The_preview_does_not_depend_on_what_is_in_force_now()
    {
        using var rig = new Rig();
        var preview = rig.Host.FakeCommands.Infos["apply"].Preview!;
        var before = preview(new[] { "PL35W" });

        rig.Client.State = rig.Client.State with { Pl1 = 20, Pl2 = 25, Window = 10 };   // the live state moves
        await Task.Run(() => rig.Runtime.Refresh());
        await rig.Host.FakeCommands.Run("apply", "PL55-60W");                          // and so does a write
        var after = preview(new[] { "PL35W" });

        Assert.Equal(before!.Value.Key, after!.Value.Key);
        Assert.Equal(before.Value.Args, after.Value.Args);
    }

    [Fact]
    public async Task A_change_reports_the_profile_that_was_active_as_undo_arguments()
    {
        using var rig = new Rig();   // the hardware holds PL55-60W
        var r = await rig.Host.FakeCommands.Run("apply", "PL35W");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(new[] { "PL55-60W" }, r.UndoArgs);

        var back = await rig.Host.FakeCommands.Run("apply", "PL55-60W");
        Assert.Equal(new[] { "PL35W" }, back.UndoArgs);
    }

    [Fact]
    public async Task Nothing_changed_means_no_undo_arguments()
    {
        using var rig = new Rig();
        var r = await rig.Host.FakeCommands.Run("apply", "PL55-60W");   // already in force
        Assert.Equal(0, r.ExitCode);
        Assert.Null(r.UndoArgs);
    }

    [Fact]
    public async Task Declined_failed_and_unknown_runs_have_no_undo_arguments()
    {
        using var rig = new Rig();
        rig.Host.FakeApproval.Answer = false;
        Assert.Null((await rig.Host.FakeCommands.Run("apply", "PL35W")).UndoArgs);
        rig.Host.FakeApproval.Answer = true;
        rig.Client.ApplyError = new XtuException("err.locked", "PL1");
        Assert.Null((await rig.Host.FakeCommands.Run("apply", "PL35W")).UndoArgs);
        Assert.Null((await rig.Host.FakeCommands.Run("apply", "nope")).UndoArgs);
    }

    [Fact]
    public async Task Old_values_that_are_no_profile_give_no_undo_arguments()
    {
        using var rig = new Rig();
        rig.Client.State = rig.Client.State with { Pl1 = 33, Pl2 = 34, Window = 7 };   // matches nothing in the list
        var r = await rig.Host.FakeCommands.Run("apply", "PL35W");
        Assert.Equal(0, r.ExitCode);
        Assert.Null(r.UndoArgs);
    }
}
