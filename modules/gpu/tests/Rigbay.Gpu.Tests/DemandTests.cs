using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Gpu.Tests;

/// <summary>Host API 0.1.4: the poll rate comes from <see cref="MetricDemand"/> (nothing, page, subscriber, background), and the verb says what it does.</summary>
public class DemandTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(2);   // the default poll rate

    [Fact]
    public void Nothing_asking_means_not_reading_and_the_interval_is_the_users_poll_rate()
    {
        var rig = new Rig().Start();
        Assert.False(rig.Module.Polling);
        Assert.Equal(Fast, rig.Module.PollInterval);
    }

    [Fact]
    public void The_page_visible_reads_at_the_poll_rate()
    {
        var rig = new Rig().Start();
        rig.Host.PageVisibleValue.Value = true;
        Assert.True(rig.Module.Polling);
        Assert.Equal(Fast, rig.Module.PollInterval);
    }

    [Fact]
    public void A_subscriber_reads_at_the_poll_rate_even_when_a_slower_background_demand_exists()
    {
        var rig = new Rig().Start();
        rig.Host.MetricsBus.SetBackground("gpu.nvml.temp", TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(60), rig.Module.PollInterval);
        rig.Host.MetricsBus.Subscribed.Add("gpu.power");
        rig.Host.MetricsBus.RaiseSubscribersChanged();
        Assert.True(rig.Module.Polling);
        Assert.Equal(Fast, rig.Module.PollInterval);
    }

    [Theory]
    [InlineData(30, 30)]    // background 30 s: read every 30 s
    [InlineData(1, 2)]      // faster than the poll rate: not faster than the poll rate
    [InlineData(3600, 3600)] // slower than anything: idle is "do not poll", so the asked interval holds
    public void A_background_demand_reads_at_max_of_the_poll_rate_and_the_asked_interval(int asked, int expected)
    {
        var rig = new Rig().Start();
        rig.Host.MetricsBus.SetBackground("gpu.nvml.temp", TimeSpan.FromSeconds(asked));
        Assert.True(rig.Module.Polling);
        Assert.Equal(TimeSpan.FromSeconds(expected), rig.Module.PollInterval);
    }

    [Fact]
    public void The_user_poll_rate_is_the_floor_of_a_background_demand()
    {
        var rig = new Rig().Start();
        rig.Row<NumberRow>("row.poll.header").Value.Value = 10;
        rig.Host.MetricsBus.SetBackground("gpu.usage", TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(10), rig.Module.PollInterval);
    }

    [Fact]
    public void The_shortest_background_demand_wins_and_the_next_one_takes_over_when_it_goes()
    {
        var rig = new Rig().Start();
        rig.Host.MetricsBus.SetBackground("gpu.usage", TimeSpan.FromSeconds(60));
        rig.Host.MetricsBus.SetBackground("gpu.power", TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(15), rig.Module.PollInterval);
        rig.Host.MetricsBus.SetBackground("gpu.power", null);
        Assert.Equal(TimeSpan.FromSeconds(60), rig.Module.PollInterval);
    }

    [Fact]
    public void A_background_demand_on_somebody_elses_metric_does_not_wake_the_gpu()
    {
        var rig = new Rig().Start();
        rig.Host.MetricsBus.SetBackground("cpu.temp", TimeSpan.FromSeconds(10));
        Assert.False(rig.Module.Polling);
    }

    [Fact]
    public void A_background_demand_starts_reading_and_its_end_stops_it_again()
    {
        var rig = new Rig(autoPoll: true).Start();
        Thread.Sleep(200);
        Assert.Equal(0, rig.Gpu.ReadCalls);

        rig.Host.MetricsBus.SetBackground("gpu.nvml.temp", TimeSpan.FromSeconds(30));
        Assert.True(SpinWait.SpinUntil(() => rig.Host.MetricsBus.Has("gpu.nvml.temp"), 5000), "a reading within seconds of the demand");

        rig.Host.MetricsBus.SetBackground("gpu.nvml.temp", null);
        Assert.False(rig.Module.Polling);
        Thread.Sleep(200);   // a poll that was running ends
        int reads = rig.Gpu.ReadCalls;
        Thread.Sleep(2300);
        Assert.Equal(reads, rig.Gpu.ReadCalls);
        rig.Module.Shutdown();
    }

    [Fact]
    public void A_background_demand_before_the_module_starts_is_read_at_start()
    {
        var rig = new Rig();
        rig.Host.MetricsBus.SetBackground("gpu.power", TimeSpan.FromSeconds(20));
        rig.Start();
        Assert.True(rig.Module.Polling);
        Assert.Equal(TimeSpan.FromSeconds(20), rig.Module.PollInterval);
    }

    [Fact]
    public void The_status_verb_is_declared_read_only_with_its_usage()
    {
        var rig = new Rig().Start();
        var info = Assert.Contains("status", (IDictionary<string, CommandInfo>)rig.Host.CommandRegistry.Infos);
        Assert.Equal(VerbAccess.ReadOnly, info.Access);
        Assert.Null(info.Effect);
        Assert.True(rig.Host.Loc.Has(info.UsageKey!));
    }
}
