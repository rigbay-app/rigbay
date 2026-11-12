using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.PredatorSense.Tests;

/// <summary>
/// The page after the UI review of November 2026 (docs/ui-review/REVIEW-2026-11.md PS-1, PS-2, sketch 3): the fan mode is the main control with
/// one line for what Rigbay set and what the fans did; each temperature is a tile with its own fan; the fans are rows, not tiles and not charted
/// again; the machine and the poll interval fold away in Details.
/// </summary>
public class PageTests
{
    [Fact]
    public void Each_temperature_is_a_tile_with_its_own_fan_and_the_fans_are_rows_only()
    {
        using var rig = new ModuleRig();
        var readings = rig.Page.Groups.Single(g => g.HeaderKey == "group.readings").Rows.Cast<ReadingRow>().ToList();

        var tiles = readings.Where(r => r.Tile == TileHint.Always).ToList();
        Assert.Equal(new[] { "cpu.temp", "gpu.temp", "system.temp" }, tiles.Select(r => r.Metric.Value));
        Assert.Equal(("tile.fan", "fan.cpu.rpm"), (tiles[0].Secondary!.LabelKey, string.Join(" ", tiles[0].Secondary!.Metrics.Select(m => m.Value))));
        Assert.Equal(("tile.fans", "fan.gpu1.rpm fan.gpu2.rpm"), (tiles[1].Secondary!.LabelKey, string.Join(" ", tiles[1].Secondary!.Metrics.Select(m => m.Value))));
        Assert.Null(tiles[2].Secondary);
        Assert.Equal("row.cpu-temp.help", tiles[0].HelpKey);   // the note about the firmware sensor is a tooltip, not a line of text

        var fans = readings.Except(tiles).ToList();
        Assert.Equal(new[] { "fan.cpu.rpm", "fan.gpu1.rpm", "fan.gpu2.rpm" }, fans.Select(r => r.Metric.Value));
        Assert.All(fans, f => Assert.Equal((TileHint.Never, ""), (f.Tile, f.ChartGroupKey)));
    }

    [Fact]
    public void A_GPU_with_one_fan_gets_one_fan_on_its_tile()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Wmi.Sensors[9] = FakeAcerWmi.Raw(0);   // no second GPU fan
        rig.Module.Probe();
        rig.Module.Initialize(rig.Host);
        using var _ = rig;
        var gpu = rig.Rows.OfType<ReadingRow>().Single(r => r.Metric.Value == "gpu.temp");
        Assert.Equal(("tile.fan", "fan.gpu1.rpm"), (gpu.Secondary!.LabelKey, gpu.Secondary.Metrics.Single().Value));
    }

    [Fact]
    public void The_fan_mode_is_the_main_control_with_its_line_and_its_help()
    {
        using var rig = new ModuleRig();
        var fan = rig.FanRow;
        Assert.Equal("row.fan-mode.header", fan.HeaderKey);
        Assert.Equal("row.fan-mode.help", fan.HelpKey);
        Assert.Equal("Maximum runs every fan at full speed. It is loud.", rig.Host.Loc.T(fan.DescriptionKey!));
        Assert.Equal("Not set by Rigbay yet", rig.FanLine);
        // the rows the line replaces are gone
        Assert.DoesNotContain(rig.Rows, r => r.HeaderKey is "row.last-set.header" or "row.check.header" or "row.opmodes.header");
    }

    [Fact]
    public void Settings_then_the_facts_folded_away_in_Details()
    {
        using var rig = new ModuleRig();
        var settings = rig.Page.Groups.Single(g => g.HeaderKey == "group.settings").Rows;
        Assert.Equal(new[] { "row.hotkey.header", "row.restore.header", "row.details.header" }, settings.Select(r => r.HeaderKey));
        var details = Assert.IsType<ExpanderRow>(settings[2]);
        Assert.False(details.IsExpanded);
        Assert.Equal(new[] { "row.model.header", "row.poll.header" }, details.Children.Select(r => r.HeaderKey));
    }
}
