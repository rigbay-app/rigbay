using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Tunnel.Tests;

/// <summary>Host API 0.1.4: the poll rate and whether the counters are read follow <see cref="MetricDemand"/> (nothing, page, subscriber, background).</summary>
public class DemandTests
{
    private const string Rx = "tunnel.example-tunnel.rx_rate";
    private const string Age = "tunnel.example-tunnel.handshake_age";
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);

    [Fact]
    public void Nothing_asking_is_the_idle_rate()
    {
        var rig = new Rig().Running().Start();
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
        Assert.Equal(DemandKind.Idle, rig.Module.Demand(rig.Host).Kind);
    }

    [Fact]
    public void The_page_or_a_subscriber_is_the_fast_rate_even_with_a_slower_background_demand()
    {
        var rig = new Rig().Running().Start();
        rig.Host.MetricsBus.SetBackground(Age, TimeSpan.FromSeconds(60));
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);

        rig.Host.PageVisibleValue.Value = true;
        Assert.Equal(Fast, rig.Module.CurrentPollInterval);
        rig.Host.PageVisibleValue.Value = false;
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);

        rig.Host.MetricsBus.Subscribed.Add(Rx);
        rig.Host.MetricsBus.RaiseSubscribersChanged();
        Assert.Equal(Fast, rig.Module.CurrentPollInterval);
    }

    [Theory]
    [InlineData(60, 15)]   // slower than idle: not slower than the idle rate, which already runs
    [InlineData(5, 5)]     // between: the asked interval
    [InlineData(1, 2)]     // faster than the fast rate: the fast rate
    public void A_background_demand_is_max_of_the_fast_rate_and_the_asked_interval_and_at_most_idle(int asked, int expected)
    {
        var rig = new Rig().Running().Start();
        rig.Host.MetricsBus.SetBackground(Age, TimeSpan.FromSeconds(asked));
        Assert.Equal(TimeSpan.FromSeconds(expected), rig.Module.CurrentPollInterval);
        Assert.Equal(DemandKind.Background, rig.Module.Demand(rig.Host).Kind);
        rig.Host.MetricsBus.SetBackground(Age, null);
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Fact]
    public void A_background_demand_on_somebody_elses_metric_or_another_tunnel_than_exists_changes_nothing()
    {
        var rig = new Rig().Running().Start();
        rig.Host.MetricsBus.SetBackground("gpu.nvml.temp", TimeSpan.FromSeconds(5));
        rig.Host.MetricsBus.SetBackground("tunnel.no-such-tunnel.handshake_age", TimeSpan.FromSeconds(5));
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }

    [Fact]
    public async Task A_background_demand_reads_the_counters_without_the_page_and_without_it_nothing_is_read()
    {
        var rig = new Rig().Running().Start();
        await rig.Module.PollOnceAsync();
        Assert.Empty(rig.W.Tools.Shows);
        Assert.False(rig.Host.MetricsBus.Has(Age));

        rig.Host.MetricsBus.SetBackground(Age, TimeSpan.FromSeconds(30));
        await rig.Module.PollOnceAsync();
        Assert.NotEmpty(rig.W.Tools.Shows);
        Assert.True(rig.Host.MetricsBus.Has(Age), "Watch's rule on the handshake age would wait for ever");
        Assert.InRange(rig.Host.MetricsBus.Latest[Age].Value, 0, 5);

        rig.Host.MetricsBus.SetBackground(Age, null);
        int shows = rig.W.Tools.Shows.Count;
        await rig.Module.PollOnceAsync();
        Assert.Equal(shows, rig.W.Tools.Shows.Count);   // nobody asks any more: wg.exe is not asked
    }

    [Fact]
    public async Task A_tunnel_that_is_off_publishes_nothing_even_under_a_demand()
    {
        var rig = new Rig().Start();   // stored, not running
        rig.Host.MetricsBus.SetBackground(Age, TimeSpan.FromSeconds(30));
        await rig.Module.PollOnceAsync();
        Assert.Empty(rig.W.Tools.Shows);
        Assert.False(rig.Host.MetricsBus.Has(Age));
    }

    [Fact]
    public void After_shutdown_a_background_demand_is_no_longer_followed()
    {
        var rig = new Rig().Running().Start();
        rig.Module.Shutdown();
        rig.Host.MetricsBus.SetBackground(Age, TimeSpan.FromSeconds(5));
        Assert.Equal(Idle, rig.Module.CurrentPollInterval);
    }
}

/// <summary>Host API 0.1.4: the verbs say what they do; the preview is the argument's own meaning; an undo is reported only when something changed.</summary>
public class VerbMetadataTests
{
    [Fact]
    public void List_and_status_are_read_only_and_on_and_off_change_a_system_setting()
    {
        var rig = new Rig().Start();
        var infos = rig.Host.CommandRegistry.Infos;
        Assert.Equal(new[] { "list", "off", "on", "status" }, infos.Keys.OrderBy(k => k));
        Assert.Equal(VerbAccess.ReadOnly, infos["list"].Access);
        Assert.Equal(VerbAccess.ReadOnly, infos["status"].Access);
        Assert.Null(infos["list"].Effect);
        Assert.Null(infos["status"].Effect);

        var en = FakeLocalisation.Load("en");
        foreach (var verb in new[] { "on", "off" })
        {
            var info = infos[verb];
            Assert.Equal(VerbAccess.Writes, info.Access);
            Assert.Equal(EffectKind.SystemSetting, info.Effect!.Kind);
            Assert.NotNull(info.Preview);
            Assert.True(en.ContainsKey("module.tunnel." + info.Effect.DescriptionKey));
            Assert.True(en.ContainsKey("module.tunnel." + info.UsageKey));
        }
        Assert.Equal("off", infos["on"].UndoVerb);
        Assert.Equal("on", infos["off"].UndoVerb);
    }

    [Fact]
    public async Task The_declared_kind_is_the_kind_of_the_question_the_verb_asks()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;
        await rig.Host.CommandRegistry.Run("on", Rig.Name);
        Assert.Equal(rig.Host.CommandRegistry.Infos["on"].Effect!.Kind, rig.Host.ApprovalService.Asked.Single().Effect.Kind);
    }

    [Theory]
    [InlineData("en", "on", "Turn on tunnel Home")]
    [InlineData("en", "off", "Turn off tunnel Home")]
    [InlineData("vi", "on", "Bật đường hầm Home")]
    [InlineData("vi", "off", "Tắt đường hầm Home")]
    public void The_preview_names_the_tunnel_in_both_languages(string language, string verb, string expected)
    {
        var rig = new Rig(language).Start();
        var text = rig.Host.CommandRegistry.Infos[verb].Preview!(new[] { "Home" })!.Value;
        Assert.Equal(expected, rig.Host.Loc.Resolve(text));
    }

    [Fact]
    public void A_preview_without_exactly_one_name_is_none()
    {
        var rig = new Rig().Start();
        var preview = rig.Host.CommandRegistry.Infos["on"].Preview!;
        Assert.Null(preview(Array.Empty<string>()));
        Assert.Null(preview(new[] { "a", "b" }));
        Assert.Null(preview(new[] { "  " }));
    }

    [Fact]
    public async Task The_preview_does_not_depend_on_whether_the_tunnel_is_on()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;
        var preview = rig.Host.CommandRegistry.Infos["on"].Preview!;
        var before = preview(new[] { Rig.Name })!.Value;
        await rig.Host.CommandRegistry.Run("on", Rig.Name);   // it is on now
        var after = preview(new[] { Rig.Name })!.Value;
        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.Args, after.Args);
    }

    [Fact]
    public async Task A_run_that_changed_the_tunnel_reports_its_stored_name_as_the_undo_and_one_that_changed_nothing_reports_none()
    {
        var rig = new Rig().Start();
        rig.Host.ApprovalService.Answer = true;

        var on = await rig.Host.CommandRegistry.Run("on", "EXAMPLE-TUNNEL");   // another spelling: the stored one comes back
        Assert.Equal(0, on.ExitCode);
        Assert.Equal(new[] { Rig.Name }, on.UndoArgs);
        Assert.Null((await rig.Host.CommandRegistry.Run("on", Rig.Name)).UndoArgs);   // already on

        var off = await rig.Host.CommandRegistry.Run("off", Rig.Name);
        Assert.Equal(0, off.ExitCode);
        Assert.Equal(new[] { Rig.Name }, off.UndoArgs);
        Assert.Null((await rig.Host.CommandRegistry.Run("off", Rig.Name)).UndoArgs);  // already off
    }

    [Fact]
    public async Task Declined_failed_unknown_and_no_handshake_runs_report_no_undo()
    {
        var declined = new Rig().Start();
        declined.Host.ApprovalService.Answer = false;
        Assert.Null((await declined.Host.CommandRegistry.Run("on", Rig.Name)).UndoArgs);

        var unknown = new Rig().Start();
        unknown.Host.ApprovalService.Answer = true;
        Assert.Null((await unknown.Host.CommandRegistry.Run("on", "nope")).UndoArgs);
        Assert.Null((await unknown.Host.CommandRegistry.Run("on")).UndoArgs);

        var silent = new Rig().Start();
        silent.Host.ApprovalService.Answer = true;
        silent.W.Tools.ShowOutput = (_, f) => f == Wg.WgField.LatestHandshakes ? World.Handshakes(null) : null;
        var r = await silent.Host.CommandRegistry.Run("on", Rig.Name);
        Assert.Equal(1, r.ExitCode);   // on, but no answer from the server: a failed verb, so nothing is recorded to undo
        Assert.Null(r.UndoArgs);
    }
}
