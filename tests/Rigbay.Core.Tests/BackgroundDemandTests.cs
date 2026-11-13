using Rigbay.Abstractions;
using Rigbay.Core;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>Host API 0.1.4, part A: a background demand is a subscription that publishers read as "at least this often" and that never counts as a fast subscriber.</summary>
public class BackgroundDemandTests
{
    private static readonly MetricId Temp = new("target.temp");

    // ---- the pure rule, with doubles (also proves a bus that does not implement the new members still compiles and says "nobody demands")

    private sealed class StubBus : IMetricsBus
    {
        public int Subscribers { get; set; }
        public TimeSpan? Background { get; set; }
        public void Publish(MetricId id, double value, MetricUnit unit) { }
        public IDisposable Subscribe(Action<MetricSample> handler) => throw new NotSupportedException();
        public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => throw new NotSupportedException();
        public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
        public IReadOnlyList<MetricSample> GetLatest() => [];
        public int SubscriberCount(MetricId id) => Subscribers;
        public TimeSpan? BackgroundInterval(MetricId id) => Background;
    }

    private sealed class PlainBus : IMetricsBus
    {
        public void Publish(MetricId id, double value, MetricUnit unit) { }
        public IDisposable Subscribe(Action<MetricSample> handler) => throw new NotSupportedException();
        public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => throw new NotSupportedException();
        public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
        public IReadOnlyList<MetricSample> GetLatest() => [];
    }

    private sealed class StubHost(IMetricsBus bus, bool pageVisible) : IHostServices
    {
        private readonly ObservableValue<bool> _visible = new(pageVisible);
        public string ModuleId => "stub";
        public string ModuleDirectory => "";
        public string DataDirectory => "";
        public Version HostVersion => new(0, 1, 0);
        public CancellationToken StopToken => CancellationToken.None;
        public ISettingsStore Settings => null!;
        public ILocalisation Localisation => null!;
        public ILog Log => null!;
        public IHotkeyService Hotkeys => null!;
        public ITrayService Tray => null!;
        public ICommandRegistry Commands => null!;
        public IToastService Toast => null!;
        public IMetricsBus Metrics => bus;
        public IHardwareApproval Approval => null!;
        public void RunOnUi(Action action) { }
        public void RequestPageRefresh() { }
        public IObservableValue<bool> PageVisible => _visible;
    }

    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(2), Idle = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(false, 0, null, DemandKind.Idle, 60)]            // nobody asks: idle
    [InlineData(true, 0, null, DemandKind.Fast, 2)]              // page visible: fast
    [InlineData(false, 1, null, DemandKind.Fast, 2)]             // a subscriber: fast
    [InlineData(false, 0, 30.0, DemandKind.Background, 30)]      // a demand of 30 s with idle 60 s: 30 s
    [InlineData(false, 0, 1.0, DemandKind.Background, 2)]        // never faster than the fast rate
    [InlineData(false, 0, 120.0, DemandKind.Background, 60)]     // never slower than idle
    [InlineData(false, 1, 30.0, DemandKind.Fast, 2)]             // a subscriber beats a demand
    [InlineData(true, 0, 30.0, DemandKind.Fast, 2)]              // so does the visible page
    public void The_poll_rule(bool visible, int subscribers, double? demandSeconds, DemandKind kind, double seconds)
    {
        var bus = new StubBus { Subscribers = subscribers, Background = demandSeconds is { } d ? TimeSpan.FromSeconds(d) : null };
        var state = new StubHost(bus, visible).Evaluate([Temp], Fast, Idle);
        Assert.Equal(kind, state.Kind);
        Assert.Equal(TimeSpan.FromSeconds(seconds), state.Interval);
        Assert.Equal(state.Interval, new StubHost(bus, visible).PollInterval([Temp], Fast, Idle));
    }

    [Fact]
    public void When_idle_means_do_not_poll_a_demand_still_sets_the_rate()
    {
        var bus = new StubBus { Background = TimeSpan.FromSeconds(30) };
        var state = new StubHost(bus, false).Evaluate([Temp], Fast, Timeout.InfiniteTimeSpan);
        Assert.Equal(new DemandState(DemandKind.Background, TimeSpan.FromSeconds(30)), state);
        Assert.Equal(new DemandState(DemandKind.Idle, Timeout.InfiniteTimeSpan), new StubHost(new StubBus(), false).Evaluate([Temp], Fast, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void The_shortest_demand_over_several_ids_wins()
    {
        var bus = new MultiBus();
        var state = new StubHost(bus, false).Evaluate([new MetricId("a.x"), new MetricId("b.y")], Fast, Idle);
        Assert.Equal(new DemandState(DemandKind.Background, TimeSpan.FromSeconds(10)), state);
    }

    private sealed class MultiBus : IMetricsBus
    {
        public void Publish(MetricId id, double value, MetricUnit unit) { }
        public IDisposable Subscribe(Action<MetricSample> handler) => throw new NotSupportedException();
        public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => throw new NotSupportedException();
        public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
        public IReadOnlyList<MetricSample> GetLatest() => [];
        public TimeSpan? BackgroundInterval(MetricId id) => id.Value == "a.x" ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10);
    }

    [Fact]
    public void A_bus_that_does_not_know_demands_says_nobody_demands()
    {
        IMetricsBus plain = new PlainBus();
        Assert.Null(plain.BackgroundInterval(Temp));
        using var sub = plain.SubscribeBackground(Temp, TimeSpan.FromSeconds(30), _ => { });
        Assert.Equal(DemandKind.Idle, new StubHost(plain, false).Evaluate([Temp], Fast, Idle).Kind);
    }

    // ---- the host's bus, through real modules

    [Fact]
    public async Task A_demand_is_not_a_subscriber_and_a_publisher_sees_it_but_not_its_asker()
    {
        using var kit = new PlanKit();
        Assert.Equal("background 1", await kit.Owner("bg", Temp.Value, "30"));

        Assert.Equal("subs=0", await kit.Target("subs", Temp.Value));          // the publisher counts no fast subscriber
        Assert.Equal("30", await kit.Target("bg-interval", Temp.Value));      // but sees the demand
        Assert.Equal("none", await kit.Owner("bg-interval", Temp.Value));      // the asker does not count its own
        Assert.Equal("Background|30", await kit.Target("poll", Temp.Value));

        await kit.Target("listen", Temp.Value);                                // a real subscriber: fast
        Assert.Equal("subs=1", await kit.Owner("subs", Temp.Value));
        Assert.Equal("Fast|2", await kit.Owner("poll", Temp.Value));           // another module's subscriber means fast for the owner
        Assert.Equal("Background|30", await kit.Target("poll", Temp.Value));   // the target's own subscriber is not counted for itself, the owner's demand is
    }

    [Fact]
    public async Task The_shortest_demand_counts_and_disposing_it_gives_the_next_one_back()
    {
        using var kit = new PlanKit();
        await kit.Owner("bg", Temp.Value, "30");
        var host = kit.Host.Metrics.SubscribeBackground(Temp, TimeSpan.FromSeconds(10), _ => { });
        Assert.Equal("10", await kit.Target("bg-interval", Temp.Value));
        host.Dispose();
        Assert.Equal("30", await kit.Target("bg-interval", Temp.Value));
        await kit.Owner("bg-stop");
        Assert.Equal("none", await kit.Target("bg-interval", Temp.Value));
        Assert.Equal("Idle|60", await kit.Target("poll", Temp.Value));
    }

    [Fact]
    public async Task Intervals_are_kept_between_five_seconds_and_one_hour()
    {
        using var kit = new PlanKit();
        await kit.Owner("bg", Temp.Value, "0.5");
        Assert.Equal("5", await kit.Target("bg-interval", Temp.Value));
        await kit.Owner("bg-stop");
        await kit.Owner("bg", Temp.Value, "99999");
        Assert.Equal("3600", await kit.Target("bg-interval", Temp.Value));
        Assert.Equal("Background|60", await kit.Target("poll", Temp.Value));    // but never slower than the publisher's idle rate
    }

    [Fact]
    public async Task Subscribers_changed_is_raised_when_a_demand_comes_and_goes()
    {
        using var kit = new PlanKit();
        int changes = 0;
        kit.Host.Metrics.SubscribersChanged += _ => Interlocked.Increment(ref changes);
        await kit.Owner("bg", Temp.Value, "30");
        Assert.Equal(1, Volatile.Read(ref changes));
        await kit.Owner("bg-stop");
        Assert.Equal(2, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task The_demand_receives_every_sample_inside_the_asker_boundary()
    {
        using var kit = new PlanKit();
        await kit.Owner("bg", Temp.Value, "30");
        kit.Host.Metrics.Publish("test", Temp, 41, MetricUnit.Celsius);
        kit.Host.Metrics.Publish("test", Temp, 42, MetricUnit.Celsius);
        kit.Host.Metrics.Publish("test", new MetricId("other.id"), 1, MetricUnit.Celsius);
        Assert.Equal("target.temp=41;target.temp=42", await kit.Owner("bg-samples"));
        Assert.Equal(ModuleLifecycle.Running, kit.Host.Find("owner")!.Lifecycle);
    }

    [Fact]
    public async Task A_stopped_module_loses_its_demands()
    {
        using var kit = new PlanKit();
        await kit.Owner("bg", Temp.Value, "30");
        Assert.Single(kit.Host.Metrics.GetBackgroundDemands());
        Assert.True(kit.Host.Find("owner")!.Disable());
        Assert.Empty(kit.Host.Metrics.GetBackgroundDemands());
        Assert.Equal("none", await kit.Target("bg-interval", Temp.Value));
    }

    [Fact]
    public async Task A_module_may_hold_at_most_64_background_subscriptions()
    {
        using var kit = new PlanKit();
        for (int i = 0; i < 64; i++) Assert.Equal("background " + (i + 1), await kit.Owner("bg", Temp.Value, "30"));
        var outcome = await kit.Host.ExecuteCommandAsync(["owner", "bg", Temp.Value, "30"]);
        Assert.Equal(CommandOutcome.Failed, outcome.ExitCode);                  // the module's own bug faults it, like any other
        Assert.Equal(ModuleLifecycle.Faulted, kit.Host.Find("owner")!.Lifecycle);
        Assert.Empty(kit.Host.Metrics.GetBackgroundDemands());
    }

    [Fact]
    public void A_demand_costs_no_timer_and_the_host_polls_nothing()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        using var a = host.Metrics.SubscribeBackground(Temp, TimeSpan.FromSeconds(30), _ => { });
        Assert.Equal(0, host.Metrics.SubscriberCount(Temp));
        Assert.Equal(TimeSpan.FromSeconds(30), host.Metrics.BackgroundInterval(Temp));
        var demand = Assert.Single(host.Metrics.GetBackgroundDemands());
        Assert.Equal((Temp, TimeSpan.FromSeconds(30), (string?)null), demand);
    }
}
