using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Module.Xtu.Tests;

/// <summary>
/// The page after the UI review of November 2026 (docs/ui-review/REVIEW-2026-11.md, XT-1 to XT-4, sketch 1): the profile choice is the page's
/// main control, the readings are four tiles with PL1 and PL2 as limits of the power chart, switching by power source is one row with its switch
/// in the header, your profiles are closed rows that say their limits, and XTU's own facts fold away in Details.
/// </summary>
public class PageTests
{
    private static string ManyProfiles(int count) =>
        "<ProfileDatabase><Profiles>" + string.Concat(Enumerable.Range(0, count).Select(i => $"""
            <DatabaseProfile><Name>P{i}</Name><ControlSettings>
              <ControlSetting><Id>48</Id><Value>{30 + i}</Value></ControlSetting><ControlSetting><Id>47</Id><Value>{40 + i}</Value></ControlSetting>
              <ControlSetting><Id>49</Id><Value>1</Value></ControlSetting><ControlSetting><Id>66</Id><Value>28</Value></ControlSetting>
            </ControlSettings></DatabaseProfile>
            """)) + "</Profiles></ProfileDatabase>";

    [Fact]
    public void More_profiles_than_segments_carry_their_limits_in_the_options_and_the_plain_effect_text()
    {
        using var rig = new Rig(profilesXml: ManyProfiles(5));   // with Default (BIOS): 6, more than the host draws as segments
        var choice = Assert.IsType<ChoiceRow>(rig.Page.Primary);

        Assert.Equal(6, choice.Options.Count);
        Assert.Equal("=P0  ·  PL1 30 W / PL2 40 W / 28 s", choice.Options[1].LabelKey);
        Assert.Equal(ApplyService.EffectKey, choice.HardwareEffect!.DescriptionKey);
        Assert.Equal("effect.apply-profile-revert", choice.HardwareEffect.RevertDescriptionKey);
    }

    [Fact]
    public void A_name_longer_than_a_segment_also_gets_the_drop_down_form()
    {
        Assert.True(XtuRuntime.NamesOnly(new[] { Profile("Quiet"), Profile("Balanced") }));
        Assert.False(XtuRuntime.NamesOnly(new[] { Profile("Quiet"), Profile(new string('x', XtuRuntime.MaxSegmentLabel + 1)) }));
        Assert.False(XtuRuntime.NamesOnly(new[] { Profile("Only one") }));
    }

    private static XtuProfile Profile(string name) => new(name, 45, 60, true, 28, ProfileKind.Custom);

    [Fact]
    public void Four_readings_are_tiles_and_the_limits_are_references_of_the_power_chart()
    {
        using var rig = new Rig();
        var readings = rig.Page.Groups.Single(g => g.HeaderKey == "group.readings").Rows.Cast<ReadingRow>().ToList();

        Assert.Equal(new[] { "cpu.temp", "cpu.power", "cpu.usage", "cpu.clock" },
            readings.Where(r => r.Tile == TileHint.Always).Select(r => r.Metric.Value));
        Assert.Equal(new[] { "cpu.power.pl1", "cpu.power.pl2" },
            readings.Where(r => r.ChartRole == ChartRole.Reference).Select(r => r.Metric.Value));
        Assert.All(readings, r => Assert.Null(r.DescriptionKey));
    }

    [Fact]
    public void Throttling_and_the_facts_about_XTU_fold_away_in_Details()
    {
        using var rig = new Rig();
        var about = rig.Page.Groups.Single(g => g.HeaderKey == "group.about").Rows;
        var details = about.OfType<ExpanderRow>().Single(e => e.HeaderKey == "row.details.header");

        Assert.False(details.IsExpanded);
        var throttle = details.Children.OfType<ReadingRow>().ToList();
        Assert.Equal(4, throttle.Count);
        Assert.All(throttle, r => Assert.Equal((TileHint.Never, "", "row.throttle.help"), (r.Tile, r.ChartGroupKey, r.HelpKey)));
        Assert.Contains(details.Children, r => r is ButtonRow { HeaderKey: "row.reload.header", ButtonKey: "row.reload.button" });
        Assert.Contains(details.Children, r => r.HeaderKey == "row.version.header");
        Assert.Contains(details.Children, r => r is ToggleRow { HeaderKey: "row.percore.header" });
        // the two messages about XTU stay outside, where they are seen; they are only open when they are true
        Assert.Equal(new[] { "row.unreachable.header", "row.faststart.header" }, about.OfType<InfoRow>().Select(i => i.HeaderKey));
    }

    [Fact]
    public void Switching_by_power_source_is_one_row_with_its_switch_in_the_header_and_the_power_source_in_its_line()
    {
        using var rig = new Rig(settings: s => s.AcProfile = "PL35W");   // on AC in this rig
        var auto = Assert.Single(rig.Page.Groups.Single(g => g.HeaderKey == "group.auto").Rows);
        var expander = Assert.IsType<ExpanderRow>(auto);

        var toggle = Assert.IsType<ToggleRow>(expander.HeaderRow);
        Assert.NotNull(toggle.HardwareEffect);
        Assert.Equal(new[] { "row.ac.header", "row.battery.header" }, expander.Children.Select(c => c.HeaderKey));
        Assert.Equal("auto.line-ac", expander.Line!.Value.Key);   // the switch is off: only where the power comes from

        rig.Row<ToggleRow>("row.auto.header").Value.Value = true;  // the host sets it after its own confirmation
        Assert.Equal(("auto.line-ac-uses", "PL35W"), (expander.Line.Value.Key, expander.Line.Value.Args![0]));

        rig.Power.Line = PowerLine.Offline;
        rig.Power.RaiseChanged();
        Assert.Equal("auto.line-battery", expander.Line.Value.Key);   // no profile for battery: nothing it uses
    }

    [Fact]
    public void Your_profiles_are_closed_rows_with_their_limits_and_delete_asks_first()
    {
        using var rig = new Rig(settings: s => s.Customs.Add(new CustomProfile { Name = "Mine", Pl1 = 40, Pl2 = 50, Window = 20 }));
        var rows = rig.Page.Groups.Single(g => g.HeaderKey == "group.custom").Rows;

        Assert.Equal(new[] { "=Mine", "row.new.header" }, rows.Select(r => r.HeaderKey));   // the form for a new one comes last
        var mine = Assert.IsType<ExpanderRow>(rows[0]);
        Assert.False(mine.IsExpanded);
        Assert.Equal(Text.Literal("PL1 40 W / PL2 50 W / 20 s"), mine.Line!.Value.Key);
        var delete = mine.Children.OfType<ButtonRow>().Single();
        Assert.Equal("row.delete.confirm", delete.ConfirmKey);
        Assert.False(Assert.IsType<ExpanderRow>(rows[1]).IsExpanded);
    }
}
