using Xunit;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>
/// Host API 0.1.3 (UI review, package B): the optional page-model members a module may set (<see cref="PageModel.Primary"/>,
/// <see cref="Row.Line"/>, <see cref="Row.HelpKey"/>, <see cref="ExpanderRow.HeaderRow"/>, <see cref="ReadingRow.ChartRole"/>,
/// <see cref="ReadingRow.Warn"/>, <see cref="ReadingRow.Secondary"/>, <see cref="ButtonRow.ConfirmKey"/>): where the core finds them,
/// how the reading plan uses them and what the validator refuses. The drawing is checked by the app's self-test.
/// </summary>
public class PageModelAdditionsTests
{
    private static readonly MetricId Temp = new("cpu.temp");
    private static readonly MetricId Power = new("cpu.power");
    private static readonly MetricId Pl1 = new("cpu.power.pl1");
    private static readonly MetricId Pl2 = new("cpu.power.pl2");
    private static readonly MetricId Fan = new("cpu.fan");
    private static readonly MetricId Usage = new("cpu.usage");

    private static MetricUnit Unit(MetricId id) =>
        id == Temp ? MetricUnit.Celsius : id == Power || id == Pl1 || id == Pl2 ? MetricUnit.Watt : id == Fan ? MetricUnit.Rpm : id == Usage ? MetricUnit.Percent : MetricUnit.None;

    private static PageModel PageOf(params Row[] rows) => new() { Groups = new[] { new RowGroup("g", rows) } };

    [Fact]
    public void Every_new_member_is_optional_and_off_by_default()
    {
        var row = new ReadingRow("t", Temp);
        Assert.Equal(ChartRole.Series, row.ChartRole);
        Assert.Null(row.Warn);
        Assert.Null(row.Secondary);
        Assert.Null(row.HelpKey);
        Assert.Null(row.Line);
        Assert.Null(new ExpanderRow("x", Array.Empty<Row>()).HeaderRow);
        Assert.Null(new ButtonRow("b", "go", _ => Task.FromResult(ActionResult.Ok())).ConfirmKey);
        Assert.Null(new PageModel().Primary);
        Assert.Equal(new Version(0, 1, 3), HostApi.Current);
    }

    [Fact]
    public void EnumerateRows_gives_the_primary_first_and_a_header_row_right_after_its_expander()
    {
        var mode = new ChoiceRow("mode", new[] { new ChoiceOption("a", "a"), new ChoiceOption("b", "b") }, new ObservableValue<string>("a"));
        var head = new ToggleRow("on", new ObservableValue<bool>(true));
        var child = new ValueRow("v", new ObservableValue<string>("x"));
        var expander = new ExpanderRow("x", new Row[] { child }) { HeaderRow = head };
        var page = new PageModel { Primary = mode, Groups = new[] { new RowGroup("g", new Row[] { expander }) } };
        Assert.Equal(new Row[] { mode, expander, head, child }, page.EnumerateRows().ToArray());
    }

    [Fact]
    public void The_primary_and_header_rows_are_searched_and_charted_like_any_row()
    {
        // a reading in an expander header still feeds the plan (EnumerateRows reaches it)
        var page = new PageModel
        {
            Primary = new ToggleRow("bypass", new ObservableValue<bool>(false)),
            Groups = new[] { new RowGroup("g", new Row[] { new ExpanderRow("x", Array.Empty<Row>()) { HeaderRow = new ReadingRow("t", Temp) } }) },
        };
        var plan = ReadingPlan.Build(page, Unit);
        Assert.Equal(new[] { Temp }, plan.Tiles.Select(t => t.Row.Metric));
    }

    [Fact]
    public void A_reference_is_never_a_tile_and_joins_the_chart_of_its_unit_as_a_reference_not_a_series()
    {
        var page = PageOf(
            new ReadingRow("t", Temp), new ReadingRow("p", Power),
            new ReadingRow("l1", Pl1) { ChartRole = ChartRole.Reference, Tile = TileHint.Always },   // Always does not make a limit a tile
            new ReadingRow("l2", Pl2) { ChartRole = ChartRole.Reference });
        var plan = ReadingPlan.Build(page, Unit);
        Assert.Equal(new[] { Temp, Power }, plan.Tiles.Select(t => t.Row.Metric));
        var power = Assert.Single(plan.Charts, c => c.Unit == MetricUnit.Watt);
        Assert.Equal(new[] { Power }, power.Series.Select(s => s.Metric));
        Assert.Equal(new[] { Pl1, Pl2 }, power.References.Select(s => s.Metric));
        Assert.Empty(plan.Charts.Single(c => c.Unit == MetricUnit.Celsius).References);
    }

    [Fact]
    public void A_reference_follows_an_explicit_chart_group_and_is_dropped_without_data_of_its_unit_to_read_against()
    {
        var page = PageOf(
            new ReadingRow("p", Power) { ChartGroupKey = "chart.power" },
            new ReadingRow("l1", Pl1) { ChartRole = ChartRole.Reference, ChartGroupKey = "chart.power" },
            new ReadingRow("t", Temp),
            new ReadingRow("warn", new MetricId("cpu.temp.warn")) { ChartRole = ChartRole.Reference, ChartGroupKey = "chart.none" },   // no such group
            new ReadingRow("l2", Pl2) { ChartRole = ChartRole.Reference });                                                            // no unit group of watts
        var plan = ReadingPlan.Build(page, Unit);
        Assert.Equal(new[] { "key:chart.power", "unit:Celsius" }, plan.Charts.Select(c => c.Id));
        Assert.Equal(new[] { Pl1 }, plan.Charts[0].References.Select(r => r.Metric));
        Assert.Empty(plan.Charts[1].References);
    }

    [Fact]
    public void A_page_of_references_only_draws_nothing()
    {
        var plan = ReadingPlan.Build(PageOf(new ReadingRow("l1", Pl1) { ChartRole = ChartRole.Reference }), Unit);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void A_secondary_reading_takes_its_metrics_as_arguments_or_as_a_list()
    {
        var a = new SecondaryReading("tile.fans", Fan, new MetricId("gpu.fan"));
        Assert.Equal("tile.fans", a.LabelKey);
        Assert.Equal(new[] { Fan, new MetricId("gpu.fan") }, a.Metrics);
        var b = new SecondaryReading("", new List<MetricId> { Fan });
        Assert.Single(b.Metrics);
    }

    [Fact]
    public void The_validator_accepts_the_new_members_used_as_documented()
    {
        var on = new ObservableValue<bool>(true);
        var page = new PageModel
        {
            Primary = new ChoiceRow("mode", new[] { new ChoiceOption("auto", "auto"), new ChoiceOption("max", "max") }, new ObservableValue<string>("auto"))
            {
                HelpKey = "mode.help", Line = new ObservableValue<LocalisedText>(default),
                HardwareEffect = new HardwareEffect("effect.fan"),
            },
            Groups = new[]
            {
                new RowGroup("g", new Row[]
                {
                    new ReadingRow("t", Temp) { Warn = 95, Secondary = new SecondaryReading("tile.fan", Fan) },
                    new ReadingRow("l1", Pl1) { ChartRole = ChartRole.Reference },
                    new ExpanderRow("tunnel", new Row[] { new ButtonRow("del", "delete", _ => Task.FromResult(ActionResult.Ok())) { ConfirmKey = "del.confirm" } })
                    {
                        HeaderRow = new ToggleRow("on", on), Line = new ObservableValue<LocalisedText>(LocalisedText.Of("line.on")),
                    },
                }),
            },
        };
        Assert.Empty(PageValidator.Validate(page));
    }

    [Fact]
    public void The_validator_refuses_a_primary_or_a_header_row_that_has_no_control_of_its_own()
    {
        var expanderPrimary = new PageModel { Primary = new ExpanderRow("x", Array.Empty<Row>()), Groups = Array.Empty<RowGroup>() };
        Assert.Contains(PageValidator.Validate(expanderPrimary), p => p.StartsWith("Primary must be a value row", StringComparison.Ordinal));
        var infoPrimary = new PageModel { Primary = new InfoRow(Severity.Info, "i"), Groups = Array.Empty<RowGroup>() };
        Assert.Contains(PageValidator.Validate(infoPrimary), p => p.Contains("InfoRow"));

        var nested = PageOf(new ExpanderRow("x", Array.Empty<Row>()) { HeaderRow = new ExpanderRow("y", Array.Empty<Row>()) });
        Assert.Contains(PageValidator.Validate(nested), p => p.Contains("HeaderRow must be"));
        var custom = PageOf(new ExpanderRow("x", Array.Empty<Row>()) { HeaderRow = new CustomRow("c") });
        Assert.Contains(PageValidator.Validate(custom), p => p.Contains("HeaderRow must be"));
    }

    [Fact]
    public void The_validator_checks_the_rows_inside_the_new_slots_like_any_row()
    {
        var badPrimary = new PageModel { Primary = new ChoiceRow("mode", Array.Empty<ChoiceOption>(), new ObservableValue<string>("")), Groups = Array.Empty<RowGroup>() };
        Assert.Contains(PageValidator.Validate(badPrimary), p => p.Contains("no options"));
        var badHead = PageOf(new ExpanderRow("x", Array.Empty<Row>()) { HeaderRow = new ToggleRow("on", null!) });
        Assert.Contains(PageValidator.Validate(badHead), p => p.Contains("Value is null"));
    }

    [Fact]
    public void The_validator_refuses_a_warning_that_is_not_a_number_and_a_secondary_without_metrics()
    {
        Assert.Contains(PageValidator.Validate(PageOf(new ReadingRow("t", Temp) { Warn = double.NaN })), p => p.Contains("Warn"));
        Assert.Contains(PageValidator.Validate(PageOf(new ReadingRow("t", Temp) { Secondary = new SecondaryReading("tile.fan") })), p => p.Contains("Secondary"));
        Assert.DoesNotContain(PageValidator.Validate(PageOf(new ReadingRow("t", Temp) { Warn = 95 })), p => p.Contains("Warn"));
    }
}
