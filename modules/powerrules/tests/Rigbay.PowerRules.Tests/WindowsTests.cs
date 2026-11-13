using Rigbay.PowerRules.Windows;

namespace Rigbay.PowerRules.Tests;

/// <summary>The small pure parts.</summary>
public class ModelTests
{
    [Fact]
    public void The_hash_of_the_rules_changes_with_every_value_and_is_stable()
    {
        var a = new RuleSet(new Rule(60, PowerMode.BestEfficiency), new Rule(165, PowerMode.Balanced));
        Assert.Equal(a.Hash(), new RuleSet(new Rule(60, PowerMode.BestEfficiency), new Rule(165, PowerMode.Balanced)).Hash());
        Assert.Matches("^[0-9a-f]{64}$", a.Hash());
        var others = new[]
        {
            new RuleSet(new Rule(120, PowerMode.BestEfficiency), new Rule(165, PowerMode.Balanced)),
            new RuleSet(new Rule(60, PowerMode.Balanced), new Rule(165, PowerMode.Balanced)),
            new RuleSet(new Rule(60, null), new Rule(165, PowerMode.Balanced)),
            new RuleSet(new Rule(60, PowerMode.BestEfficiency), new Rule(120, PowerMode.Balanced)),
            new RuleSet(new Rule(60, PowerMode.BestEfficiency), new Rule(165, PowerMode.BestPerformance)),
            new RuleSet(new Rule(60, PowerMode.BestEfficiency), new Rule(0, PowerMode.Balanced)),
            // the columns are not interchangeable
            new RuleSet(new Rule(165, PowerMode.Balanced), new Rule(60, PowerMode.BestEfficiency)),
        };
        Assert.All(others, o => Assert.NotEqual(a.Hash(), o.Hash()));
        Assert.Equal(others.Length, others.Select(o => o.Hash()).Distinct().Count());
    }

    [Fact]
    public void A_rule_set_of_leave_everywhere_is_empty()
    {
        Assert.True(new RuleSet(Rule.Leave, Rule.Leave).IsEmpty);
        Assert.False(new RuleSet(new Rule(60, null), Rule.Leave).IsEmpty);
        Assert.False(new RuleSet(Rule.Leave, new Rule(0, PowerMode.Balanced)).IsEmpty);
    }

    [Fact]
    public void Power_modes_map_to_their_guids_and_back_and_any_other_guid_is_unknown()
    {
        foreach (var (mode, guid) in PowerModes.Guids) Assert.Equal(mode, PowerModes.FromGuid(guid));
        Assert.Equal(PowerMode.Unknown, PowerModes.FromGuid(new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f")));
        Assert.Equal(Guid.Empty, PowerModes.Guids[PowerMode.Balanced]);
        Assert.Equal(PowerMode.BestEfficiency, PowerModes.FromId("efficiency"));
        Assert.Null(PowerModes.FromId("leave"));
        Assert.Null(PowerModes.FromId("turbo"));
        Assert.Null(PowerModes.FromId(null));
    }

    [Fact]
    public void The_source_tracker_counts_the_first_source_as_a_change_ignores_unknown_and_honours_force()
    {
        var t = new SourceTracker();
        Assert.False(t.Check(PowerSource.Unknown, force: true));
        Assert.True(t.Check(PowerSource.Plugged, force: false));
        Assert.False(t.Check(PowerSource.Plugged, force: false));
        Assert.True(t.Check(PowerSource.Plugged, force: true));
        Assert.True(t.Check(PowerSource.Battery, force: false));
        Assert.False(t.Check(PowerSource.Unknown, force: false));
        Assert.False(t.Check(PowerSource.Battery, force: false));   // unknown did not count as a change
        t.Reset();
        Assert.True(t.Check(PowerSource.Battery, force: false));
    }

    [Theory]
    [InlineData(0x80000000u, true)]    // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
    [InlineData(11u, true)]            // DISPLAYPORT_EMBEDDED
    [InlineData(13u, true)]            // UDI_EMBEDDED
    [InlineData(10u, false)]           // DISPLAYPORT_EXTERNAL
    [InlineData(5u, false)]            // HDMI
    [InlineData(0u, false)]            // VGA
    [InlineData(0xFFFFFFFFu, false)]   // OTHER
    public void Only_the_internal_output_technologies_count_as_the_internal_panel(uint technology, bool internalPanel) =>
        Assert.Equal(internalPanel, WindowsPanel.IsInternal(technology));
}

/// <summary>
/// The real Windows classes, on the machine that runs the tests. READS ONLY: nothing here changes a refresh rate or a power mode, and the
/// two refusal tests are answered before any <c>ChangeDisplaySettingsEx</c> call (a rate of 1 or 7 Hz is never listed).
/// </summary>
public class WindowsReadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void The_reads_do_not_throw_and_agree_with_each_other()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var source = new WindowsPowerSource();
        _ = source.HasBattery();
        Assert.True(Enum.IsDefined(source.Read()));

        var panel = new WindowsPanel().Find();
        output.WriteLine("internal panel: " + panel.State + (panel.Panel is { } pp ? " " + pp.Width + "x" + pp.Height + " " + pp.CurrentHz + " Hz, listed: " + string.Join(",", pp.Rates) : ""));
        Assert.True(Enum.IsDefined(panel.State));
        if (panel.State == PanelState.Active)
        {
            var p = panel.Panel!;
            Assert.StartsWith(@"\\.\", p.DeviceName);
            Assert.True(p.Width > 0 && p.Height > 0);
            Assert.Contains(p.CurrentHz, p.Rates);
            Assert.Equal(p.Rates.OrderBy(r => r), p.Rates);
            Assert.All(p.Rates, r => Assert.True(r > 1));
        }
        else Assert.Null(panel.Panel);

        var mode = new WindowsPowerMode();
        output.WriteLine($"power mode export available: {mode.Available}; effective mode: {(mode.Available ? mode.Read() : PowerMode.Unknown)}");
        if (mode.Available) Assert.True(Enum.IsDefined(mode.Read()));
        else Assert.Equal(PowerMode.Unknown, mode.Read());
    }

    [Fact]
    public void The_real_panel_port_refuses_a_rate_it_does_not_list_before_it_calls_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var port = new WindowsPanel();
        var made_up = new PanelInfo(@"\\.\DISPLAY1", "", 1, 1, 60, new[] { 60 });
        Assert.False(port.Test(made_up, 1).Ok);   // 1 means "hardware default", never sent
        Assert.False(port.Apply(made_up, 1).Ok);
        Assert.False(port.Test(made_up, 7).Ok);   // no panel lists 7 Hz
        Assert.False(port.Apply(made_up, 7).Ok);
    }

    [Fact]
    public void The_real_power_mode_port_refuses_a_mode_that_is_none_of_the_three()
    {
        if (!OperatingSystem.IsWindows()) return;
        var rc = new WindowsPowerMode().Set(PowerMode.Unknown);
        Assert.NotEqual(0, rc);   // -2 (not allow-listed) or -1 (no export): never a call with a GUID
    }

    [Fact]
    public void The_listener_starts_and_stops_cleanly_and_twice_is_harmless()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = new WindowsPowerSource();
        source.Start();
        source.Start();
        // Whether Windows accepted the notification is reported, not required: without it the module reads the source on a slow timer.
        output.WriteLine($"power source notification registered: {source.Listening}; source now: {source.Read()}; has battery: {source.HasBattery()}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        source.Dispose();
        source.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
