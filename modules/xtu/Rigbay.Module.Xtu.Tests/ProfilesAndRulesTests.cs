using System.Text;
using Rigbay.Module.Xtu;
using Xunit;

namespace Rigbay.Module.Xtu.Tests;

public class CatalogTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ProfileDatabase>
          <Profiles>
            <DatabaseProfile>
              <Name>PL55-60W</Name>
              <ControlSettings>
                <ControlSetting><Id>48</Id><Value>55</Value></ControlSetting>
                <ControlSetting><Id>47</Id><Value>60</Value></ControlSetting>
                <ControlSetting><Id>49</Id><Value>1</Value></ControlSetting>
                <ControlSetting><Id>66</Id><Value>56</Value></ControlSetting>
                <ControlSetting><Id>1</Id><Value>999</Value></ControlSetting>
              </ControlSettings>
            </DatabaseProfile>
            <DatabaseProfile>
              <Name>Missing window</Name>
              <ControlSettings>
                <ControlSetting><Id>48</Id><Value>40</Value></ControlSetting>
                <ControlSetting><Id>47</Id><Value>50</Value></ControlSetting>
                <ControlSetting><Id>49</Id><Value>1</Value></ControlSetting>
              </ControlSettings>
            </DatabaseProfile>
            <DatabaseProfile>
              <Name>PL2 off</Name>
              <ControlSettings>
                <ControlSetting><Id>48</Id><Value>30.5</Value></ControlSetting>
                <ControlSetting><Id>47</Id><Value>30.5</Value></ControlSetting>
                <ControlSetting><Id>49</Id><Value>0</Value></ControlSetting>
                <ControlSetting><Id>66</Id><Value>10</Value></ControlSetting>
              </ControlSettings>
            </DatabaseProfile>
          </Profiles>
        </ProfileDatabase>
        """;

    private static List<XtuProfile> Parse() => ProfilesXml.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Xml)));

    [Fact]
    public void Profiles_xml_keeps_only_profiles_with_all_four_controls()
    {
        var list = Parse();
        Assert.Equal(new[] { "PL55-60W", "PL2 off" }, list.Select(p => p.Name));
        var p = list[0];
        Assert.Equal((55m, 60m, true, 56m), (p.Pl1, p.Pl2, p.Pl2Enable, p.Window));
        Assert.Equal(ProfileKind.Xtu, p.Kind);
        Assert.Equal((30.5m, false, 10m), (list[1].Pl1, list[1].Pl2Enable, list[1].Window));
    }

    [Fact]
    public void Missing_or_broken_file_gives_an_empty_list()
    {
        Assert.Empty(ProfilesXml.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".xml")));
        var bad = Path.GetTempFileName();
        File.WriteAllText(bad, "<not closed");
        Assert.Empty(ProfilesXml.Load(bad));
    }

    [Fact]
    public void List_is_default_then_xtu_then_custom_and_xtu_names_win()
    {
        var state = new XtuState(55, 60, true, 56, 90, 115, true, 56, true);
        var customs = new[]
        {
            new XtuProfile("quiet", 35, 40, true, 28, ProfileKind.Custom),
            new XtuProfile("pl55-60w", 1, 2, true, 3, ProfileKind.Custom),   // same name as an XTU profile, ignoring case
        };
        var list = Catalog.Build(state, Parse(), customs);
        Assert.Equal(new[] { "Default (BIOS)", "PL55-60W", "PL2 off", "quiet" }, list.Select(p => p.Name));
        Assert.Equal((90m, 115m, true, 56m), (list[0].Pl1, list[0].Pl2, list[0].Pl2Enable, list[0].Window));
        Assert.Equal(ProfileKind.Builtin, list[0].Kind);
    }

    [Fact]
    public void Default_falls_back_to_90_115_56_when_XTU_could_not_be_read()
    {
        var def = Catalog.Build(null, Array.Empty<XtuProfile>(), Array.Empty<XtuProfile>())[0];
        Assert.Equal((90m, 115m, true, 56m), (def.Pl1, def.Pl2, def.Pl2Enable, def.Window));
    }

    [Fact]
    public void Active_profile_is_the_first_whose_four_values_match_within_0_01()
    {
        var list = Catalog.Build(null, Parse(), Array.Empty<XtuProfile>());
        var state = new XtuState(55.005m, 59.995m, true, 56m, 90, 115, true, 56, true);
        Assert.Equal("PL55-60W", Catalog.FindActive(list, state)!.Name);

        Assert.Null(Catalog.FindActive(list, state with { Pl2Enable = false }));          // enable flag differs
        Assert.Null(Catalog.FindActive(list, state with { Window = 57m }));
        Assert.Null(Catalog.FindActive(list, state with { Pl1 = 55.02m }));               // outside the tolerance
        Assert.Null(Catalog.FindActive(list, null));
    }

    [Fact]
    public void First_match_wins_when_two_profiles_have_the_same_values()
    {
        var list = new List<XtuProfile>
        {
            new("A", 40, 50, true, 28, ProfileKind.Xtu),
            new("B", 40, 50, true, 28, ProfileKind.Custom),
        };
        var state = new XtuState(40, 50, true, 28, 90, 115, true, 56, true);
        Assert.Equal("A", Catalog.FindActive(list, state)!.Name);
    }

    [Fact]
    public void Find_by_name_ignores_case()
    {
        var list = Catalog.Build(null, Parse(), Array.Empty<XtuProfile>());
        Assert.NotNull(Catalog.FindByName(list, "default (bios)"));
        Assert.Null(Catalog.FindByName(list, "nope"));
        Assert.Null(Catalog.FindByName(list, ""));
    }

    [Fact]
    public void Describe_matches_the_standalone_app_text()
    {
        Assert.Equal("PL1 55 W / PL2 60 W / 56 s", new XtuProfile("x", 55, 60, true, 56, ProfileKind.Xtu).Describe());
        Assert.Equal("PL1 30.5 W / PL2 30.5 W (off) / 10 s", new XtuProfile("x", 30.5m, 30.5m, false, 10, ProfileKind.Xtu).Describe());
    }
}

public class RulesTests
{
    private static XtuProfile P(decimal pl1, decimal pl2, decimal window) => new("t", pl1, pl2, true, window, ProfileKind.Custom);

    [Theory]
    [InlineData(15, 15, 1)]
    [InlineData(120, 120, 128)]
    [InlineData(120, 150, 56)]
    [InlineData(45, 65, 28)]
    public void Values_on_the_edges_are_accepted(decimal pl1, decimal pl2, decimal window) =>
        Rules.Validate(P(pl1, pl2, window));

    [Theory]
    [InlineData(14.99, 60, 28, "val.pl1")]
    [InlineData(120.01, 130, 28, "val.pl1")]
    [InlineData(0, 60, 28, "val.pl1")]
    [InlineData(50, 49.99, 28, "val.pl2")]       // PL2 below PL1
    [InlineData(50, 150.01, 28, "val.pl2")]
    [InlineData(50, 60, 0.99, "val.window")]
    [InlineData(50, 60, 128.01, "val.window")]
    public void Values_outside_the_ranges_are_refused_with_a_key(double pl1, double pl2, double window, string key)
    {
        var ex = Assert.Throws<XtuException>(() => Rules.Validate(P((decimal)pl1, (decimal)pl2, (decimal)window)));
        Assert.Equal(key, ex.Key);
    }

    [Fact]
    public void The_ranges_are_the_standalone_apps()
    {
        Assert.Equal((15m, 120m, 150m, 1m, 128m), (Limits.MinPl1, Limits.MaxPl1, Limits.MaxPl2, Limits.MinWindow, Limits.MaxWindow));
    }

    [Fact]
    public void Only_the_four_controls_are_allowed()
    {
        Assert.Equal(new uint[] { 47, 48, 49, 66 }, XtuIds.Controls.OrderBy(x => x));
        foreach (uint id in new uint[] { 0, 1, 46, 50, 65, 67, 1000 }) Assert.False(XtuIds.IsAllowed(id));
        foreach (var id in XtuIds.Controls) Assert.True(XtuIds.IsAllowed(id));
    }

    [Theory]
    [InlineData("", "val.name-empty")]
    [InlineData("   ", "val.name-empty")]
    [InlineData("@x", "val.name-chars")]
    [InlineData("a\tb", "val.name-chars")]
    [InlineData("a\nb", "val.name-chars")]
    [InlineData(" lead", "val.name-chars")]
    [InlineData("Existing", "val.name-taken")]
    [InlineData("existing", "val.name-taken")]
    public void Bad_names_are_refused(string name, string key)
    {
        var ex = Rules.CheckName(name, new[] { "Existing" });
        Assert.NotNull(ex);
        Assert.Equal(key, ex!.Key);
    }

    [Fact]
    public void Names_up_to_40_characters_are_fine_and_41_are_not()
    {
        Assert.Null(Rules.CheckName(new string('a', 40), Array.Empty<string>()));
        Assert.Equal("val.name-long", Rules.CheckName(new string('a', 41), Array.Empty<string>())!.Key);
    }
}
