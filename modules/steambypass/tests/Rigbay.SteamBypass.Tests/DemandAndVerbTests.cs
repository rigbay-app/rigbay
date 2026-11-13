using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.SteamBypass.Tests;

/// <summary>Host API 0.1.4: the refresh rate follows <see cref="MetricDemand"/> (nothing, page, subscriber, background).</summary>
public class DemandTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);

    [Fact]
    public void Nothing_asking_is_the_idle_rate()
    {
        using var rig = new ModuleRig();
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void The_page_or_a_subscriber_is_the_fast_rate_even_with_a_slower_background_demand()
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(SteamBypassModule.ErrorsMetric, TimeSpan.FromSeconds(60));
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);

        rig.Host.PageVisibleValue.Value = true;
        Assert.Equal(Fast, rig.Module.CurrentPollInterval);
        rig.Host.PageVisibleValue.Value = false;

        rig.Host.MetricsBus.SetSubscribers(SteamBypassModule.ActiveMetric, 1);
        Assert.Equal(Fast, rig.Module.CurrentPollInterval);
        rig.Host.MetricsBus.SetSubscribers(SteamBypassModule.ActiveMetric, 0);
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Theory]
    [InlineData(30, 10)]   // slower than idle: the idle timer already runs faster, so not slower than idle
    [InlineData(5, 5)]     // between: the asked interval
    [InlineData(0.5, 1)]   // faster than the fast rate: the fast rate
    public void A_background_demand_is_max_of_the_fast_rate_and_the_asked_interval_and_at_most_idle(double asked, double expected)
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(SteamBypassModule.ErrorsMetric, TimeSpan.FromSeconds(asked));
        Assert.Equal(TimeSpan.FromSeconds(expected), rig.Module.CurrentPollInterval);
        rig.Host.MetricsBus.SetBackground(SteamBypassModule.ErrorsMetric, null);
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void The_shortest_background_demand_of_the_two_metrics_wins()
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(SteamBypassModule.ActiveMetric, TimeSpan.FromSeconds(8));
        rig.Host.MetricsBus.SetBackground(SteamBypassModule.ErrorsMetric, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void A_background_demand_on_somebody_elses_metric_changes_nothing()
    {
        using var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(new MetricId("cpu.temp"), TimeSpan.FromSeconds(5));
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void A_background_demand_before_the_module_starts_sets_the_first_rate()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Host.MetricsBus.SetBackground(SteamBypassModule.ErrorsMetric, TimeSpan.FromSeconds(5));
        Assert.True(rig.Module.Probe().Supported);
        rig.Module.Initialize(rig.Host);
        try { Assert.Equal(TimeSpan.FromSeconds(5), rig.Module.CurrentPollInterval); }
        finally { rig.Stop(); }
    }

    [Fact]
    public void After_shutdown_a_background_demand_is_no_longer_followed()
    {
        var rig = new ModuleRig();
        rig.Host.MetricsBus.SetBackground(SteamBypassModule.ErrorsMetric, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Module.CurrentPollInterval);

        rig.Stop();
        rig.Host.MetricsBus.SetBackground(SteamBypassModule.ErrorsMetric, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Module.CurrentPollInterval);   // unchanged: no signal is followed after Shutdown
    }
}

/// <summary>Host API 0.1.4: the verbs say what they do; the preview is the verb's own meaning; an undo is reported only when something changed.</summary>
public class VerbMetadataTests
{
    [Fact]
    public void Status_and_test_are_read_only_and_on_and_off_change_a_system_setting()
    {
        using var rig = new ModuleRig();
        var infos = rig.Host.CommandRegistry.Infos;
        Assert.Equal(new[] { "off", "on", "status", "test" }, infos.Keys.OrderBy(k => k));
        Assert.Equal(VerbAccess.ReadOnly, infos["status"].Access);
        Assert.Equal(VerbAccess.ReadOnly, infos["test"].Access);
        Assert.Null(infos["status"].Effect);
        Assert.Null(infos["test"].Effect);

        foreach (var verb in new[] { "on", "off" })
        {
            var info = infos[verb];
            Assert.Equal(VerbAccess.Writes, info.Access);
            Assert.Equal("effect.bypass", info.Effect!.DescriptionKey);
            Assert.Equal(EffectKind.SystemSetting, info.Effect.Kind);   // the kind the verb's own question asks with
            Assert.NotNull(info.Preview);
            Assert.True(FakeLocalisation.Load("en").ContainsKey("module.steam-bypass." + info.UsageKey));
        }
        Assert.Equal("off", infos["on"].UndoVerb);
        Assert.Equal("on", infos["off"].UndoVerb);
    }

    [Fact]
    public async Task The_declared_effect_is_the_effect_the_verb_asks_the_owner_about()
    {
        using var rig = new ModuleRig(approve: true);
        var declared = rig.Host.CommandRegistry.Infos["on"].Effect!;
        await rig.Run("on");
        Assert.Equal(declared, rig.Host.ApprovalService.Asked.Single().Effect);
    }

    [Theory]
    [InlineData("en", "on", "Steam bypass on")]
    [InlineData("en", "off", "Steam bypass off")]
    [InlineData("vi", "on", "Bật Steam bypass")]
    [InlineData("vi", "off", "Tắt Steam bypass")]
    public void The_preview_says_what_the_verb_does_in_both_languages(string language, string verb, string expected)
    {
        using var rig = new ModuleRig(language);
        var text = rig.Host.CommandRegistry.Infos[verb].Preview!(Array.Empty<string>())!.Value;
        Assert.Equal(expected, rig.Host.Loc.Resolve(text));
    }

    [Fact]
    public async Task The_preview_does_not_depend_on_whether_the_bypass_is_on()
    {
        using var rig = new ModuleRig(approve: true);
        var preview = rig.Host.CommandRegistry.Infos["on"].Preview!;
        var off = preview(Array.Empty<string>())!.Value;
        await rig.Run("on");
        var on = preview(Array.Empty<string>())!.Value;
        Assert.Equal(off.Key, on.Key);
        Assert.Equal(off.Args, on.Args);
    }

    [Fact]
    public async Task A_run_that_changed_something_reports_an_empty_undo_and_one_that_changed_nothing_reports_none()
    {
        using var rig = new ModuleRig(approve: true);
        var off0 = await rig.Run("off");   // already off
        Assert.Equal(0, off0.ExitCode);
        Assert.Null(off0.UndoArgs);

        var on = await rig.Run("on");
        Assert.Equal(0, on.ExitCode);
        Assert.NotNull(on.UndoArgs);       // an empty list, not null: "run the undo verb with no arguments"
        Assert.Empty(on.UndoArgs!);

        var on2 = await rig.Run("on");     // already on
        Assert.Null(on2.UndoArgs);

        var off = await rig.Run("off");
        Assert.Equal(0, off.ExitCode);
        Assert.NotNull(off.UndoArgs);
        Assert.Empty(off.UndoArgs!);
    }

    [Fact]
    public async Task Declined_failed_and_bad_runs_report_no_undo()
    {
        using var declined = new ModuleRig(approve: false);
        Assert.Null((await declined.Run("on")).UndoArgs);

        using var failed = new ModuleRig(approve: true, arrange: r => r.Listeners.Taken.Add(18081));
        var r = await failed.Run("on");
        Assert.Equal(1, r.ExitCode);
        Assert.Null(r.UndoArgs);

        using var bad = new ModuleRig(approve: true);
        var usage = await bad.Run("on", "now");
        Assert.Equal(2, usage.ExitCode);
        Assert.Null(usage.UndoArgs);
    }
}
