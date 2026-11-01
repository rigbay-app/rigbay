using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Core.Tests;

public class MetricsBusTests
{
    private static readonly MetricId Temp = new("cpu.temp");
    private static readonly MetricId Power = new("cpu.power");

    [Theory]
    [InlineData("cpu.temp", true)]
    [InlineData("gpu.fan.rpm", true)]
    [InlineData("sample.ticks", true)]
    [InlineData("a-b_c.d1", true)]
    [InlineData("", false)]
    [InlineData("CPU.temp", false)]
    [InlineData("1cpu", false)]
    [InlineData("cpu..temp", false)]
    [InlineData("cpu.", false)]
    [InlineData("cpu temp", false)]
    public void Ids_have_a_fixed_form(string id, bool valid)
    {
        Assert.Equal(valid, MetricId.IsValid(id));
        if (valid) Assert.Equal(id, new MetricId(id).Value);
        else Assert.Throws<ArgumentException>(() => new MetricId(id));
        Assert.False(MetricId.IsValid(new string('a', 65)));
    }

    [Fact]
    public void Published_samples_are_cached_and_delivered_with_source_and_time()
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var bus = new MetricsBus(new ListLog(), () => now);
        var received = new List<MetricSample>();
        using var sub = bus.Subscribe(received.Add);

        bus.Publish("xtu", Temp, 61.5, MetricUnit.Celsius);

        var s = Assert.Single(received);
        Assert.Equal(("xtu", 61.5, MetricUnit.Celsius, now), (s.SourceModule, s.Value, s.Unit, s.Timestamp));
        Assert.True(bus.TryGetLatest(Temp, out var latest));
        Assert.Equal(s, latest);
        Assert.False(bus.TryGetLatest(Power, out _));
    }

    [Fact]
    public void A_late_subscriber_can_read_the_latest_values()
    {
        var bus = new MetricsBus(new ListLog());
        bus.Publish("a", Temp, 1, MetricUnit.Celsius);
        bus.Publish("a", Temp, 2, MetricUnit.Celsius);
        bus.Publish("a", Power, 30, MetricUnit.Watt);
        var all = bus.GetLatest();
        Assert.Equal(new[] { "cpu.power", "cpu.temp" }, all.Select(s => s.Id.Value).ToArray());
        Assert.Equal(2, all.Single(s => s.Id == Temp).Value);
    }

    [Fact]
    public void A_filtered_subscription_only_sees_its_id_and_dispose_stops_delivery()
    {
        var bus = new MetricsBus(new ListLog());
        var temps = new List<double>();
        var sub = bus.Subscribe(s => temps.Add(s.Value), Temp);
        bus.Publish("a", Temp, 1, MetricUnit.Celsius);
        bus.Publish("a", Power, 99, MetricUnit.Watt);
        sub.Dispose();
        bus.Publish("a", Temp, 2, MetricUnit.Celsius);
        Assert.Equal(new[] { 1.0 }, temps);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_values_are_dropped_and_logged(double value)
    {
        var log = new ListLog();
        var bus = new MetricsBus(log);
        var received = new List<MetricSample>();
        bus.Subscribe(received.Add);
        bus.Publish("a", Temp, value, MetricUnit.Celsius);
        Assert.Empty(received);
        Assert.False(bus.TryGetLatest(Temp, out _));
        Assert.Contains(log.Lines, l => l.Contains("non-finite"));
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_stop_the_others_or_the_publisher()
    {
        var log = new ListLog();
        var bus = new MetricsBus(log);
        var good = new List<MetricSample>();
        bus.Subscribe(_ => throw new InvalidOperationException("bad subscriber"));
        bus.Subscribe(good.Add);
        bus.Publish("a", Temp, 1, MetricUnit.Celsius);
        Assert.Single(good);
        Assert.Contains(log.Lines, l => l.Contains("subscriber failed"));
    }

    [Fact]
    public void The_newest_sample_wins_when_two_modules_publish_the_same_id()
    {
        var bus = new MetricsBus(new ListLog());
        bus.Publish("intel", Temp, 60, MetricUnit.Celsius);
        bus.Publish("amd", Temp, 70, MetricUnit.Celsius);
        bus.TryGetLatest(Temp, out var s);
        Assert.Equal(("amd", 70.0), (s.SourceModule, s.Value));
    }

    [Fact]
    public void Concurrent_publishing_and_subscribing_is_safe()
    {
        var bus = new MetricsBus(new ListLog());
        int count = 0;
        using var sub = bus.Subscribe(_ => Interlocked.Increment(ref count));
        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 500; i++)
            {
                bus.Publish("m" + t, Temp, i, MetricUnit.Celsius);
                using var temp = bus.Subscribe(_ => { });
            }
        });
        Assert.Equal(4000, count);
    }

    [Fact]
    public void Units_have_symbols()
    {
        Assert.Equal("°C", MetricUnit.Celsius.Symbol());
        Assert.Equal("W", MetricUnit.Watt.Symbol());
        Assert.Equal("", MetricUnit.None.Symbol());
        foreach (var unit in Enum.GetValues<MetricUnit>().Where(u => u != MetricUnit.None && u != MetricUnit.Count))
            Assert.NotEqual("", unit.Symbol());
    }
}

public class HotkeyTests
{
    private static HotkeyRegistry NewRegistry(string? path = null) => new(new ListLog(), path);
    private static readonly HotkeyGesture F5 = HotkeyGesture.Parse("Ctrl+Alt+F5");

    [Theory]
    [InlineData("Ctrl+Alt+F5", "Ctrl+Alt+F5")]
    [InlineData("alt+ctrl+f5", "Ctrl+Alt+F5")]
    [InlineData("Win+Shift+a", "Shift+Win+A")]
    [InlineData("Ctrl+Escape", "Ctrl+Esc")]
    [InlineData("pageup", "PageUp")]
    [InlineData("Ctrl+num7", "Ctrl+Num7")]
    [InlineData("Ctrl+F24", "Ctrl+F24")]
    [InlineData("5", "5")]
    public void Gestures_parse_and_print_in_one_canonical_form(string text, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(canonical, g.Value.ToString());
        Assert.Equal(g, HotkeyGesture.Parse(canonical));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Hyper+A")]
    [InlineData("Ctrl+AB")]
    [InlineData("Ctrl+Alt")]
    public void Bad_gestures_are_refused(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
        Assert.Throws<FormatException>(() => HotkeyGesture.Parse(text));
    }

    [Fact]
    public void Two_modules_with_the_same_gesture_conflict_and_the_flag_clears_when_one_leaves()
    {
        var reg = NewRegistry();
        var a = reg.Register("alpha", "toggle", "k.toggle", F5, () => { });
        Assert.False(a.HasConflict.Value);

        var b = reg.Register("beta", "boost", "k.boost", F5, () => { });
        Assert.True(a.HasConflict.Value);
        Assert.True(b.HasConflict.Value);
        var conflict = Assert.Single(reg.Conflicts);
        Assert.Equal(F5, conflict.Gesture);
        Assert.Equal(new[] { "alpha.toggle", "beta.boost" }, conflict.Owners.OrderBy(x => x).ToArray());

        reg.RemoveModule("beta");
        Assert.False(a.HasConflict.Value);
        Assert.Empty(reg.Conflicts);
    }

    [Fact]
    public void Rebinding_to_a_free_gesture_resolves_the_conflict_and_back_creates_it_again()
    {
        var reg = NewRegistry();
        var a = reg.Register("alpha", "x", "k", F5, () => { });
        var b = reg.Register("beta", "y", "k", F5, () => { });
        b.Gesture.Value = HotkeyGesture.Parse("Ctrl+Alt+F6");
        Assert.False(a.HasConflict.Value);
        Assert.False(b.HasConflict.Value);
        b.Gesture.Value = F5;
        Assert.True(a.HasConflict.Value);
        b.Gesture.Value = null; // unbound never conflicts
        Assert.False(a.HasConflict.Value);
    }

    [Fact]
    public void A_host_reserved_gesture_conflicts_with_a_module()
    {
        var reg = NewRegistry();
        reg.ReserveHost("show window", F5);
        var a = reg.Register("alpha", "x", "k", F5, () => { });
        Assert.True(a.HasConflict.Value);
        Assert.Contains("host:show window", Assert.Single(reg.Conflicts).Owners);
    }

    [Fact]
    public void Pressing_a_gesture_runs_its_action_unless_it_is_in_conflict()
    {
        var reg = NewRegistry();
        int a = 0, b = 0;
        reg.Register("alpha", "x", "k", F5, () => a++);
        Assert.Equal(1, reg.Press(F5));
        reg.Register("beta", "y", "k", F5, () => b++);
        Assert.Equal(0, reg.Press(F5));
        Assert.Equal((1, 0), (a, b));
        Assert.Equal(0, reg.Press(HotkeyGesture.Parse("Ctrl+Q")));
    }

    [Fact]
    public void The_same_action_cannot_be_registered_twice_and_ids_are_checked()
    {
        var reg = NewRegistry();
        reg.Register("alpha", "x", "k", null, () => { });
        Assert.Throws<ArgumentException>(() => reg.Register("alpha", "x", "k", null, () => { }));
        Assert.Throws<ArgumentException>(() => reg.Register("alpha", "Bad Id", "k", null, () => { }));
        reg.Register("beta", "x", "k", null, () => { }); // same action id in another module is fine
    }

    [Fact]
    public void A_users_rebinding_is_kept_across_runs()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.DataRoot, "hotkeys.json");
        var first = NewRegistry(path);
        var a = first.Register("alpha", "x", "k", F5, () => { });
        var b = first.Register("alpha", "unbound-by-default", "k", null, () => { });
        a.Gesture.Value = HotkeyGesture.Parse("Ctrl+Alt+F7");
        b.Gesture.Value = HotkeyGesture.Parse("Ctrl+B");

        var second = NewRegistry(path);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+Alt+F7"), second.Register("alpha", "x", "k", F5, () => { }).Gesture.Value);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+B"), second.Register("alpha", "unbound-by-default", "k", null, () => { }).Gesture.Value);

        // unbinding is remembered too, and going back to the default forgets the override
        second.Entries.Single(e => e.ActionId == "x").Gesture.Value = null;
        var third = NewRegistry(path);
        Assert.Null(third.Register("alpha", "x", "k", F5, () => { }).Gesture.Value);
        third.Entries.Single().Gesture.Value = F5;
        var fourth = NewRegistry(path);
        Assert.Equal(F5, fourth.Register("alpha", "x", "k", F5, () => { }).Gesture.Value);
    }

    [Fact]
    public void An_unreadable_hotkeys_file_is_ignored()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.DataRoot, "hotkeys.json");
        File.WriteAllText(path, "{{{ nope");
        var reg = NewRegistry(path);
        Assert.Equal(F5, reg.Register("alpha", "x", "k", F5, () => { }).Gesture.Value);
    }
}

public class LogTests
{
    [Fact]
    public void Entries_have_time_level_source_and_message_and_the_exception_stack()
    {
        using var env = new TestEnv();
        var log = new HostLog(Path.Combine(env.Root, "logs"));
        log.Write(LogLevel.Warn, "steam-access", "something odd\nsecond line", new InvalidOperationException("boom"));
        var text = File.ReadAllText(log.CurrentFile);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} WARN  steam-access\s+something odd second line", text);
        Assert.Contains("InvalidOperationException: boom", text);
    }

    [Fact]
    public void The_minimum_level_filters()
    {
        using var env = new TestEnv();
        var log = new HostLog(Path.Combine(env.Root, "logs"), minimum: LogLevel.Warn);
        log.Write(LogLevel.Info, "x", "quiet");
        log.Write(LogLevel.Error, "x", "loud");
        var text = File.ReadAllText(log.CurrentFile);
        Assert.DoesNotContain("quiet", text);
        Assert.Contains("loud", text);
    }

    [Fact]
    public void The_file_is_rotated_at_the_size_cap_and_only_three_files_are_kept()
    {
        using var env = new TestEnv();
        var dir = Path.Combine(env.Root, "logs");
        var log = new HostLog(dir, maxBytes: 2000, files: 3);
        for (int i = 0; i < 200; i++) log.Write(LogLevel.Info, "m", $"line {i:000} " + new string('x', 60));

        var names = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "rigbay.1.log", "rigbay.2.log", "rigbay.log" }, names);
        foreach (var f in Directory.GetFiles(dir)) Assert.True(new FileInfo(f).Length <= 2000 + 200, $"{f} is {new FileInfo(f).Length} bytes");
        // newest lines are in the current file, older ones in the numbered files
        Assert.Contains("line 199", File.ReadAllText(Path.Combine(dir, "rigbay.log")));
        Assert.DoesNotContain("line 199", File.ReadAllText(Path.Combine(dir, "rigbay.1.log")));
    }

    [Fact]
    public void The_size_cap_holds_across_restarts()
    {
        using var env = new TestEnv();
        var dir = Path.Combine(env.Root, "logs");
        for (int run = 0; run < 5; run++)
        {
            var log = new HostLog(dir, maxBytes: 1500, files: 2);
            for (int i = 0; i < 20; i++) log.Write(LogLevel.Info, "m", new string('y', 70));
        }
        Assert.Equal(2, Directory.GetFiles(dir).Length);
        Assert.All(Directory.GetFiles(dir), f => Assert.True(new FileInfo(f).Length <= 1500 + 200));
    }

    [Fact]
    public void Recent_entries_are_kept_in_memory_up_to_a_limit_and_an_event_fires()
    {
        using var env = new TestEnv();
        var log = new HostLog(Path.Combine(env.Root, "logs"), memoryEntries: 5);
        var seen = 0;
        log.EntryWritten += _ => seen++;
        for (int i = 0; i < 12; i++) log.Write(LogLevel.Info, "m", "n" + i);
        Assert.Equal(5, log.Recent().Count);
        Assert.Equal("n11", log.Recent()[^1].Message);
        Assert.Equal(12, seen);
    }

    [Fact]
    public void Writing_never_throws_even_when_the_folder_is_unusable()
    {
        using var env = new TestEnv();
        var blocker = Path.Combine(env.Root, "file-not-folder");
        File.WriteAllText(blocker, "x");
        var log = new HostLog(Path.Combine(blocker, "logs"));
        log.Write(LogLevel.Error, "m", "still fine");
        Assert.Single(log.Recent());
    }

    [Fact]
    public void A_module_log_stamps_its_id()
    {
        using var env = new TestEnv();
        var log = new HostLog(Path.Combine(env.Root, "logs"));
        log.ForSource("sample").Info("hello");
        Assert.Equal("sample", log.Recent().Single().Source);
    }
}
