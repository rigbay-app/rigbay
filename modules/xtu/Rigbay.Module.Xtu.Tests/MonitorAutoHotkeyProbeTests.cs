using Rigbay.Abstractions;
using Rigbay.Module.Xtu;
using Xunit;

namespace Rigbay.Module.Xtu.Tests;

public class MonitorSamplerTests
{
    private static Dictionary<uint, decimal> Sample(decimal pkgPower = 22.5m, decimal temp = 61m) => new()
    {
        [Mon.PkgPower] = pkgPower, [Mon.CorePower] = 15m, [Mon.GfxPower] = 0.4m, [Mon.PkgTemp] = temp, [Mon.MaxPFreq] = 4.2m,
        [Mon.Uncore] = 3.6m, [Mon.ActiveCores] = 3, [Mon.CpuThrottle] = 0, [Mon.PowerThrottle] = 12, [Mon.CurrentThrottle] = 0, [Mon.VrThrottle] = 0,
        [Mon.CoreTemp0] = 58m, [Mon.CoreTemp0 + 1] = 63m, [Mon.CoreTemp0 + 2] = 60m,
        [Mon.CoreUtil0] = 10m, [Mon.CoreUtil0 + 1] = 30m,
        [Mon.CoreFreq0] = 4.0m,
    };

    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    private static (MonitorSampler s, FakeClient c, FakeMetrics m, Clock t) Make()
    {
        var client = new FakeClient();
        var metrics = new FakeMetrics();
        var clock = new Clock();
        return (new MonitorSampler(client, metrics, clock.Get), client, metrics, clock);
    }

    [Fact]
    public void Right_after_connecting_the_stale_values_are_not_published()
    {
        var (s, c, m, _) = Make();
        c.Monitor.Enqueue((Sample(99, 99), true));    // what XTU kept from the last run
        Assert.Equal(0, s.Tick());
        Assert.True(s.Warming);
        Assert.Empty(m.Published);
    }

    [Fact]
    public void Publishing_starts_when_a_probe_value_moves()
    {
        var (s, c, m, _) = Make();
        c.Monitor.Enqueue((Sample(99, 99), true));
        c.Monitor.Enqueue((Sample(99, 99), false));   // still the old numbers
        c.Monitor.Enqueue((Sample(20, 61), false));   // moved
        Assert.Equal(0, s.Tick());
        Assert.Equal(0, s.Tick());
        Assert.True(s.Tick() > 0);
        Assert.False(s.Warming);
        Assert.Equal(61, m.Last("cpu.temp"));
    }

    [Fact]
    public void Publishing_starts_after_four_seconds_even_if_nothing_moved()
    {
        var (s, c, m, t) = Make();
        c.Monitor.Enqueue((Sample(), true));
        c.Monitor.Enqueue((Sample(), false));
        Assert.Equal(0, s.Tick());
        t.Now += TimeSpan.FromSeconds(3.9);
        Assert.Equal(0, s.Tick());
        Assert.Empty(m.Published);
        t.Now += TimeSpan.FromSeconds(0.2);
        Assert.True(s.Tick() > 0);
    }

    [Fact]
    public void A_new_connection_warms_up_again()
    {
        var (s, c, m, _) = Make();
        c.Monitor.Enqueue((Sample(1, 1), true));
        c.Monitor.Enqueue((Sample(20, 61), false));
        s.Tick(); Assert.True(s.Tick() > 0);
        m.Published.Clear();

        c.Monitor.Clear();
        c.Monitor.Enqueue((Sample(70, 70), true));    // reconnected: old values again
        Assert.Equal(0, s.Tick());
        Assert.Empty(m.Published);
    }

    [Fact]
    public void Readings_use_the_shared_ids_units_and_conversions()
    {
        var (s, c, m, _) = Make();
        c.Monitor.Enqueue((Sample(1, 1), true));
        c.Monitor.Enqueue((Sample(22.5m, 61m), false));
        s.Tick(); s.Tick();

        var byId = m.Published.GroupBy(p => p.Id.Value).ToDictionary(g => g.Key, g => g.Last());
        Assert.Equal((61.0, MetricUnit.Celsius), (byId["cpu.temp"].Value, byId["cpu.temp"].Unit));
        Assert.Equal((22.5, MetricUnit.Watt), (byId["cpu.power"].Value, byId["cpu.power"].Unit));
        Assert.Equal((4200.0, MetricUnit.Megahertz), (byId["cpu.clock"].Value, byId["cpu.clock"].Unit));   // GHz to MHz
        Assert.Equal((20.0, MetricUnit.Percent), (byId["cpu.usage"].Value, byId["cpu.usage"].Unit));       // mean of 10 and 30
        Assert.Equal(63.0, byId["xtu.cpu.temp.core-max"].Value);
        Assert.Equal(12.0, byId["xtu.throttle.power"].Value);
        Assert.Equal(3600.0, byId["xtu.cpu.uncore"].Value);
        Assert.DoesNotContain("xtu.core0.temp", byId.Keys);   // per-core is off by default
        Assert.DoesNotContain("xtu.igpu.temp", byId.Keys);    // not in this sample: missing means no reading, not zero
    }

    [Fact]
    public void Per_core_readings_are_published_only_when_asked()
    {
        var (s, c, m, _) = Make();
        s.PerCore = true;
        c.Monitor.Enqueue((Sample(1, 1), true));
        c.Monitor.Enqueue((Sample(22.5m, 61m), false));
        s.Tick(); s.Tick();
        Assert.Equal(63.0, m.Last("xtu.core1.temp"));
        Assert.Equal(4000.0, m.Last("xtu.core0.clock"));
        Assert.Equal(30.0, m.Last("xtu.core1.usage"));
    }

    [Fact]
    public void All_metric_ids_are_valid_bus_ids()
    {
        foreach (var f in typeof(XtuMetrics).GetFields().Where(f => f.FieldType == typeof(MetricId)))
            Assert.True(MetricId.IsValid(((MetricId)f.GetValue(null)!).Value), f.Name);
        for (int i = 0; i < Mon.Cores; i++)
            foreach (var id in new[] { XtuMetrics.CoreTemp(i), XtuMetrics.CoreFreq(i), XtuMetrics.CoreVolt(i), XtuMetrics.CoreUtil(i) })
                Assert.True(MetricId.IsValid(id.Value));
    }

    [Fact]
    public void A_failed_read_surfaces_to_the_caller_and_reset_asks_for_a_new_connection()
    {
        var client = new ThrowingClient();
        var s = new MonitorSampler(client, new FakeMetrics());
        Assert.Throws<XtuException>(() => s.Tick());
        s.Reset();
        Assert.Equal(1, client.Resets);
    }

    private sealed class ThrowingClient : IXtuClient
    {
        public int Resets;
        public XtuState ReadState() => throw new XtuException("err.unavailable");
        public IReadOnlyDictionary<uint, decimal> ReadMonitor(out bool justOpened) => throw new XtuException("err.unavailable");
        public void ResetMonitor() => Resets++;
        public ApplyOutcome Apply(XtuProfile profile) => throw new InvalidOperationException();
        public void Dispose() { }
    }
}

public class AutoSwitchTests
{
    private static readonly IReadOnlyList<XtuProfile> Profiles = new[]
    {
        new XtuProfile("Default (BIOS)", 90, 115, true, 56, ProfileKind.Builtin),
        new XtuProfile("Quiet", 35, 40, true, 28, ProfileKind.Custom),
        new XtuProfile("Fast", 90, 115, true, 56, ProfileKind.Xtu),
    };

    [Fact]
    public void A_change_of_power_source_applies_the_profile_for_the_new_source()
    {
        var a = new AutoSwitcher();
        var first = a.Check(true, "Fast", "Quiet", PowerLine.Online, false, Profiles);
        Assert.Equal((AutoOutcome.Apply, "Fast"), (first.Outcome, first.Profile!.Name));   // first look at the source counts as a change

        Assert.Equal(AutoOutcome.None, a.Check(true, "Fast", "Quiet", PowerLine.Online, false, Profiles).Outcome);   // same source: leave a manual change alone

        var unplugged = a.Check(true, "Fast", "Quiet", PowerLine.Offline, false, Profiles);
        Assert.Equal((AutoOutcome.Apply, "Quiet", false), (unplugged.Outcome, unplugged.Profile!.Name, unplugged.OnAc));
    }

    [Fact]
    public void A_forced_check_applies_even_without_a_change()
    {
        var a = new AutoSwitcher();
        a.Check(true, "Fast", "Quiet", PowerLine.Offline, false, Profiles);
        var forced = a.Check(true, "Fast", "Quiet", PowerLine.Offline, true, Profiles);
        Assert.Equal((AutoOutcome.Apply, "Quiet"), (forced.Outcome, forced.Profile!.Name));
    }

    [Fact]
    public void Switched_off_does_nothing_but_still_remembers_the_source()
    {
        var a = new AutoSwitcher();
        Assert.Equal(AutoOutcome.None, a.Check(false, "Fast", "Quiet", PowerLine.Online, false, Profiles).Outcome);
        // turned on later, source unchanged: no surprise apply
        Assert.Equal(AutoOutcome.None, a.Check(true, "Fast", "Quiet", PowerLine.Online, false, Profiles).Outcome);
    }

    [Fact]
    public void Unknown_power_source_is_ignored_and_does_not_count_as_a_change()
    {
        var a = new AutoSwitcher();
        a.Check(true, "Fast", "Quiet", PowerLine.Online, false, Profiles);
        Assert.Equal(AutoOutcome.None, a.Check(true, "Fast", "Quiet", PowerLine.Unknown, true, Profiles).Outcome);
        Assert.Equal(AutoOutcome.None, a.Check(true, "Fast", "Quiet", PowerLine.Online, false, Profiles).Outcome);
    }

    [Fact]
    public void No_profile_chosen_for_a_source_means_keep_what_is_set()
    {
        var a = new AutoSwitcher();
        Assert.Equal(AutoOutcome.None, a.Check(true, "", "Quiet", PowerLine.Online, false, Profiles).Outcome);
        Assert.Equal(AutoOutcome.None, a.Check(true, null, null, PowerLine.Offline, false, Profiles).Outcome);
    }

    [Fact]
    public void A_chosen_profile_that_no_longer_exists_is_reported_not_applied()
    {
        var a = new AutoSwitcher();
        var d = a.Check(true, "Gone", "Quiet", PowerLine.Online, false, Profiles);
        Assert.Equal((AutoOutcome.Missing, "Gone", true), (d.Outcome, d.MissingName, d.OnAc));
        Assert.Null(d.Profile);
    }

    [Fact]
    public void Profile_names_match_ignoring_case()
    {
        var a = new AutoSwitcher();
        Assert.Equal("Quiet", a.Check(true, "quiet", "", PowerLine.Online, false, Profiles).Profile!.Name);
    }
}

public class HotkeyTests
{
    private static HotkeyGesture G(string text) => HotkeyGesture.Parse(text);

    [Theory]
    [InlineData("Ctrl+Alt+F2")]
    [InlineData("Alt+1")]
    [InlineData("Ctrl+Shift+P")]
    public void Ctrl_or_Alt_plus_a_key_is_accepted(string text) =>
        Assert.Null(HotkeyPolicy.Refuse(G(text), new HashSet<HotkeyGesture>()));

    [Theory]
    [InlineData("F5")]
    [InlineData("Shift+A")]
    [InlineData("Win+F2")]
    public void Without_Ctrl_or_Alt_it_is_refused(string text) =>
        Assert.Equal("hotkey.needs-modifier", HotkeyPolicy.Refuse(G(text), new HashSet<HotkeyGesture>()));

    [Fact]
    public void XTUs_own_combinations_are_refused()
    {
        var xtu = new HashSet<HotkeyGesture> { G("Ctrl+Shift+F1"), G("Ctrl+Alt+F2") };
        Assert.Equal("hotkey.xtu-combo", HotkeyPolicy.Refuse(G("Ctrl+Alt+F2"), xtu));
        Assert.Equal("hotkey.xtu-combo", HotkeyPolicy.Refuse(G("shift+ctrl+F1"), xtu));
        Assert.Null(HotkeyPolicy.Refuse(G("Ctrl+Alt+F3"), xtu));
    }

    [Fact]
    public void XTUs_combinations_are_read_only_when_pairing_is_on()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xtu-hk-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var ui = Path.Combine(dir, "uisettings.xml");
        var hk = Path.Combine(dir, "xtu.hotkey.settings");
        File.WriteAllBytes(hk, System.Text.Encoding.Latin1.GetBytes("\0\0CONTROL+SHIFT+F1\0junk\0ALT+F9\0not a combo\0CTRL+ALT+2\0"));

        File.WriteAllText(ui, "<Settings><NameValuePair><Name>HotkeyPairingOn</Name><Value>False</Value></NameValuePair></Settings>");
        Assert.Empty(HotkeyPolicy.ReadXtuCombos(ui, hk));

        File.WriteAllText(ui, "<Settings><NameValuePair><Name>HotkeyPairingOn</Name><Value>True</Value></NameValuePair></Settings>");
        var combos = HotkeyPolicy.ReadXtuCombos(ui, hk);
        Assert.Contains(G("Ctrl+Shift+F1"), combos);
        Assert.Contains(G("Alt+F9"), combos);
        Assert.Contains(G("Ctrl+Alt+2"), combos);
        Assert.Equal(3, combos.Count);

        Assert.Empty(HotkeyPolicy.ReadXtuCombos(Path.Combine(dir, "missing.xml"), hk));
    }

    [Fact]
    public void Action_ids_are_valid_and_unique()
    {
        var used = new HashSet<string>();
        var a = HotkeyPolicy.ActionId("PL55-60W", used);
        var b = HotkeyPolicy.ActionId("pl55 60w", used);     // same slug
        var c = HotkeyPolicy.ActionId("Default (BIOS)", used);
        var d = HotkeyPolicy.ActionId("???", used);
        Assert.Equal("profile-pl55-60w", a);
        Assert.Equal("profile-pl55-60w-2", b);
        Assert.Equal("profile-default-bios", c);
        Assert.Equal("profile-x", d);
        Assert.All(new[] { a, b, c, d }, id => Assert.Matches("^[a-z0-9-]+$", id));
    }
}

public class ProbeTests
{
    private static readonly XtuPaths Paths = new(@"C:\Intel\Drivers", @"C:\ProgramData\Intel\XTU");

    private static FakeSystem Healthy()
    {
        var sys = new FakeSystem();
        sys.Files.Add(Paths.InterfaceDll);
        sys.Files.Add(Paths.CommonDll);
        return sys;
    }

    [Fact]
    public void Intel_with_the_service_running_and_the_dlls_is_supported() =>
        Assert.True(ProbeLogic.Run(Healthy(), Paths).Supported);

    [Fact]
    public void Not_an_Intel_CPU()
    {
        var sys = Healthy();
        sys.Vendor = "AuthenticAMD";
        var r = ProbeLogic.Run(sys, Paths);
        Assert.False(r.Supported);
        Assert.Equal("probe.not-intel", r.ReasonKey);
        Assert.Equal("AuthenticAMD", r.ReasonArgs![0]);
    }

    [Fact]
    public void Service_not_installed()
    {
        var sys = Healthy();
        sys.ServiceState = ServiceState.NotFound;
        Assert.Equal("probe.no-service", ProbeLogic.Run(sys, Paths).ReasonKey);
    }

    [Fact]
    public void Contract_dlls_missing()
    {
        var sys = Healthy();
        sys.Files.Remove(Paths.CommonDll);
        var r = ProbeLogic.Run(sys, Paths);
        Assert.Equal("probe.no-dlls", r.ReasonKey);
        Assert.Equal(Paths.DriversDir, r.ReasonArgs![0]);
    }

    [Theory]
    [InlineData("Stopped")]
    [InlineData("Other")]
    public void Service_not_running(string stateName)
    {
        var sys = Healthy();
        sys.ServiceState = Enum.Parse<ServiceState>(stateName);
        Assert.Equal("probe.service-stopped", ProbeLogic.Run(sys, Paths).ReasonKey);
    }

    [Fact]
    public void An_unknown_vendor_does_not_block_a_machine_that_has_XTU()
    {
        var sys = Healthy();
        sys.Vendor = "";
        Assert.True(ProbeLogic.Run(sys, Paths).Supported);
    }

    [Fact]
    public void The_real_probe_returns_a_result_without_throwing()
    {
        // Read-only checks of this machine; the outcome depends on the machine, only "no exception" is asserted.
        var r = new XtuModule().Probe();
        Assert.True(r.Supported || r.ReasonKey.StartsWith("probe.", StringComparison.Ordinal));
    }
}
