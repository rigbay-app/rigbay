using Rigbay.App.Services;
using Rigbay.Core;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>
/// The tray tooltip (src/Rigbay.App/Services/TrayTip.cs, linked into this project): Automatic is the tooltip the app always had, Custom is the owner's
/// list of items. A fake source, so nothing here needs a window, a module or the bus.
/// </summary>
public class TrayTipTests
{
    sealed class FakeSource : ITrayTipSource
    {
        public List<TipModule> Mods { get; } = new();
        public Dictionary<string, TipMetric> Metrics { get; } = new();
        public IReadOnlyList<TipModule> Modules => Mods;
        public TipMetric? Metric(string id) => Metrics.TryGetValue(id, out var m) ? m : null;

        public FakeSource Module(string id, string name, bool running = true, string? state = null, params TipReading[] summary)
        {
            Mods.Add(new TipModule(id, name, running, state, summary));
            return this;
        }
        public FakeSource Reading(string id, string label, string? value, string owner = "xtu", bool running = true, bool fresh = true)
        {
            Metrics[id] = new TipMetric(id, label, owner, running, value, fresh);
            return this;
        }
    }

    // the machine of the owner's screenshot: CPU 55 °C, CPU fan 2,830 rpm, a Steam proxy that is off, an XTU profile and a tunnel
    static FakeSource Rack()
    {
        var s = new FakeSource();
        s.Module("predator", "PredatorSense", true, "Fans Auto", new TipReading("cpu.temp", "CPU", "55 °C"), new TipReading("cpu.fan", "CPU fan", "2,830 rpm"));
        s.Module("steam", "Steam access", true, "Bypass off");
        s.Module("xtu", "XTU profiles", true, "Profile Quiet", new TipReading("cpu.power.pl1", "PL1", "28 W"));
        s.Module("tunnel", "Remote tunnel", true, "Home: connected");
        s.Reading("cpu.temp", "CPU", "55 °C", "predator");
        s.Reading("cpu.fan", "CPU fan", "2,830 rpm", "predator");
        s.Reading("cpu.power.pl1", "PL1", "28 W", "xtu");
        s.Reading("tunnel.home.rx_rate", "Home down", "1.2 MB/s", "tunnel");
        return s;
    }

    static TrayTipSettings Custom(params TrayTipItem[] items) => new() { Mode = TrayTipSettings.Custom, Items = items.ToList() };
    static TrayTipItem Item(string id, string label = "", bool newLine = false, string module = "") => new() { Id = id, Label = label, NewLine = newLine, Module = module };

    // ------------------------------------------------------------------ Automatic

    /// <summary>The tooltip as it was before it could be customised (TrayHost.BuildTip of 0.1.2), written out again on the fake data: the new code has to give the same text.</summary>
    static string Legacy(FakeSource src)
    {
        var lines = new List<string> { "Rigbay" };
        var said = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in src.Modules)
        {
            if (!e.Running) continue;
            var parts = new List<string>();
            if (e.State is { Length: > 0 } state) parts.Add(state);
            foreach (var r in e.Summary)
            {
                if (!said.Add(r.Id) || r.Value == null) continue;
                parts.Add(r.Label + " " + r.Value);
            }
            if (parts.Count == 0) continue;
            lines.Add(string.Join(" · ", parts));
            if (lines.Count == 3) break;
        }
        var tip = string.Join("\n", lines);
        return tip.Length <= 127 ? tip : tip[..126] + "…";
    }

    [Fact]
    public void Automatic_is_the_old_tooltip_first_two_modules_with_something_to_say()
    {
        var s = Rack();
        var tip = TrayTipBuilder.Build(new TrayTipSettings(), s);
        Assert.Equal("Rigbay\nFans Auto · CPU 55 °C · CPU fan 2,830 rpm\nBypass off", tip.Text);
        Assert.Equal(Legacy(s), tip.Text);
        Assert.Empty(tip.Items);
    }

    [Fact]
    public void Automatic_never_shows_the_xtu_profile_or_the_tunnel_with_four_modules_on_which_is_why_custom_exists()
    {
        var tip = TrayTipBuilder.Build(new TrayTipSettings(), Rack()).Text;
        Assert.DoesNotContain("Profile Quiet", tip);
        Assert.DoesNotContain("Home", tip);
    }

    [Fact]
    public void Automatic_matches_the_old_tooltip_on_awkward_racks()
    {
        var cases = new List<FakeSource>();
        cases.Add(new FakeSource());   // no modules: just "Rigbay"
        cases.Add(new FakeSource().Module("a", "A", false, "State A"));   // off modules say nothing
        cases.Add(new FakeSource().Module("a", "A", true, null, new TipReading("cpu.temp", "CPU", null)).Module("b", "B", true, "State B"));   // a reading without a value
        cases.Add(new FakeSource().Module("a", "A", true, "", new TipReading("cpu.temp", "CPU", "50 °C")).Module("b", "B", true, null, new TipReading("cpu.temp", "CPU", "50 °C")));   // a figure already said is not repeated
        cases.Add(new FakeSource().Module("a", "A", true, new string('x', 140)));   // too long: cut with an ellipsis, as it always was
        foreach (var c in cases)
            Assert.Equal(Legacy(c), TrayTipBuilder.Build(new TrayTipSettings(), c).Text);
        Assert.Equal("Rigbay", TrayTipBuilder.Build(new TrayTipSettings(), cases[0]).Text);
        var cut = TrayTipBuilder.Build(new TrayTipSettings(), cases[4]).Text;
        Assert.Equal(127, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void An_unknown_mode_word_is_automatic()
    {
        var s = TrayTipSettings.Clean(new TrayTipSettings { Mode = "banana", Items = { Item("cpu.temp") } });
        Assert.False(s.IsCustom);
        Assert.Equal(Legacy(Rack()), TrayTipBuilder.Build(s, Rack()).Text);
    }

    // ------------------------------------------------------------------ Custom: order, labels, lines

    [Fact]
    public void Custom_keeps_the_order_of_the_list_and_joins_a_line_with_a_dot()
    {
        var tip = TrayTipBuilder.Build(Custom(Item("cpu.fan"), Item("state:xtu"), Item("cpu.temp")), Rack());
        Assert.Equal("Rigbay\nCPU fan 2,830 rpm · Profile Quiet · CPU 55 °C", tip.Text);
        Assert.All(tip.Items, i => Assert.Equal(TipItemStatus.Shown, i.Status));
    }

    [Fact]
    public void The_owner_decides_where_a_line_starts()
    {
        var tip = TrayTipBuilder.Build(Custom(Item("state:xtu"), Item("cpu.power.pl1"), Item("cpu.temp", newLine: true), Item("cpu.fan"), Item("state:tunnel", newLine: true)), Rack());
        Assert.Equal("Rigbay\nProfile Quiet · PL1 28 W\nCPU 55 °C · CPU fan 2,830 rpm\nHome: connected", tip.Text);
    }

    [Fact]
    public void A_line_break_on_the_first_item_does_nothing_it_already_starts_a_line()
    {
        Assert.Equal("Rigbay\nCPU 55 °C", TrayTipBuilder.Build(Custom(Item("cpu.temp", newLine: true)), Rack()).Text);
        Assert.Equal("CPU 55 °C · CPU fan 2,830 rpm", TrayTipBuilder.Build(new TrayTipSettings { Mode = "custom", ShowTitle = false, Items = { Item("cpu.temp", newLine: true), Item("cpu.fan") } }, Rack()).Text);
    }

    [Fact]
    public void A_label_of_the_owner_replaces_the_default_short_label_and_an_empty_one_keeps_it()
    {
        var tip = TrayTipBuilder.Build(Custom(Item("cpu.temp", "Chip"), Item("cpu.fan", ""), Item("cpu.power.pl1", "   ")), Rack());
        Assert.Equal("Rigbay\nChip 55 °C · CPU fan 2,830 rpm · PL1 28 W", tip.Text);
    }

    [Fact]
    public void A_state_line_has_no_label_unless_the_owner_gives_one()
    {
        Assert.Equal("Rigbay\nBypass off", TrayTipBuilder.Build(Custom(Item("state:steam")), Rack()).Text);
        Assert.Equal("Rigbay\nSteam Bypass off", TrayTipBuilder.Build(Custom(Item("state:steam", "Steam")), Rack()).Text);
    }

    [Fact]
    public void A_label_is_one_short_line()
    {
        var tip = TrayTipBuilder.Build(Custom(Item("cpu.temp", "  The\r\n  chip   temperature of the whole machine ")), Rack());
        Assert.Equal("Rigbay\nThe chip temperature of 55 °C", tip.Text);   // 24 characters, single spaces, no line break from the label
    }

    [Fact]
    public void The_first_line_can_be_hidden_and_an_empty_result_still_says_Rigbay()
    {
        var s = new TrayTipSettings { Mode = "custom", ShowTitle = false, Items = { Item("cpu.temp"), Item("state:steam", newLine: true) } };
        Assert.Equal("CPU 55 °C\nBypass off", TrayTipBuilder.Build(s, Rack()).Text);
        s.Items.Clear();
        Assert.Equal("Rigbay", TrayTipBuilder.Build(s, Rack()).Text);   // never an empty tooltip: the shell would remove it
        s.ShowTitle = true;
        Assert.Equal("Rigbay", TrayTipBuilder.Build(s, Rack()).Text);
    }

    // ------------------------------------------------------------------ Custom: items with nothing to say

    [Fact]
    public void An_item_of_a_module_that_is_off_is_left_out_silently()
    {
        var s = Rack();
        s.Mods[2] = s.Mods[2] with { Running = false, State = null };
        s.Metrics["cpu.power.pl1"] = s.Metrics["cpu.power.pl1"] with { OwnerRunning = false };
        var tip = TrayTipBuilder.Build(Custom(Item("state:xtu"), Item("cpu.power.pl1", module: "xtu"), Item("cpu.temp")), s);
        Assert.Equal("Rigbay\nCPU 55 °C", tip.Text);
        Assert.Equal(new[] { TipItemStatus.ModuleOff, TipItemStatus.ModuleOff, TipItemStatus.Shown }, tip.Items.Select(i => i.Status));
    }

    [Fact]
    public void An_unavailable_or_stale_reading_is_left_out_silently()
    {
        var s = Rack();
        s.Reading("cpu.fan", "CPU fan", null, "predator");                                   // withdrawn
        s.Reading("cpu.power.pl1", "PL1", "28 W", "xtu", fresh: false);                      // the module stopped publishing it
        var tip = TrayTipBuilder.Build(Custom(Item("cpu.temp"), Item("cpu.fan"), Item("cpu.power.pl1")), s);
        Assert.Equal("Rigbay\nCPU 55 °C", tip.Text);
        Assert.Equal(new[] { TipItemStatus.Shown, TipItemStatus.NoReading, TipItemStatus.Stale }, tip.Items.Select(i => i.Status));
    }

    [Fact]
    public void A_metric_the_bus_does_not_know_yet_of_a_module_that_is_off_is_off_not_missing()
    {
        var s = Rack();
        s.Module("recorder", "Recorder", false);
        var tip = TrayTipBuilder.Build(Custom(Item("recorder.rate", module: "recorder"), Item("recorder.size")), s);
        Assert.Equal(TipItemStatus.ModuleOff, tip.Items[0].Status);
        Assert.Equal(TipItemStatus.NoReading, tip.Items[1].Status);   // no module named: nothing says it is gone
    }

    [Fact]
    public void A_state_line_that_is_empty_is_no_reading()
    {
        var s = Rack();
        s.Mods[1] = s.Mods[1] with { State = "" };
        var tip = TrayTipBuilder.Build(Custom(Item("state:steam"), Item("cpu.temp")), s);
        Assert.Equal("Rigbay\nCPU 55 °C", tip.Text);
        Assert.Equal(TipItemStatus.NoReading, tip.Items[0].Status);
    }

    [Fact]
    public void An_item_of_a_module_that_is_not_installed_stays_in_the_list_as_missing_and_adds_nothing()
    {
        var s = Rack();
        var settings = Custom(Item("state:gone"), Item("gone.metric", module: "gone"), Item("cpu.temp"));
        var tip = TrayTipBuilder.Build(settings, s);
        Assert.Equal("Rigbay\nCPU 55 °C", tip.Text);
        Assert.Equal(3, tip.Items.Count);   // the list itself is untouched
        Assert.Equal(new[] { TipItemStatus.Missing, TipItemStatus.Missing, TipItemStatus.Shown }, tip.Items.Select(i => i.Status));
        Assert.Equal("gone", tip.Items[0].Group);
        Assert.Equal(3, settings.Items.Count);
        // the module comes back (reinstalled): the same items work again
        s.Module("gone", "Gone", true, "Back again");
        s.Reading("gone.metric", "Gone", "7 W", "gone");
        Assert.Equal("Rigbay\nBack again · Gone 7 W · CPU 55 °C", TrayTipBuilder.Build(settings, s).Text);
    }

    [Fact]
    public void A_line_break_before_an_item_that_is_left_out_moves_to_the_next_item_that_is_shown()
    {
        var s = Rack();
        s.Reading("cpu.fan", "CPU fan", null, "predator");
        var tip = TrayTipBuilder.Build(Custom(Item("cpu.temp"), Item("cpu.fan", newLine: true), Item("state:steam")), s);
        Assert.Equal("Rigbay\nCPU 55 °C\nBypass off", tip.Text);   // the owner wanted the fan and what follows on a line of their own
    }

    // ------------------------------------------------------------------ the 127 characters

    [Fact]
    public void Too_long_drops_whole_items_from_the_end_never_a_cut_in_a_number()
    {
        var s = Rack();
        s.Reading("m.a", "Alpha reading", "1,234,567 rpm");
        s.Reading("m.b", "Beta reading", "7,654,321 rpm");
        s.Reading("m.c", "Gamma reading", "1,111,111 rpm");
        s.Reading("m.d", "Delta reading", "2,222,222 rpm");
        s.Reading("m.e", "Epsilon reading", "3,333,333 rpm");
        var items = new[] { "m.a", "m.b", "m.c", "m.d", "m.e" }.Select(id => Item(id)).ToArray();
        var tip = TrayTipBuilder.Build(Custom(items), s);
        Assert.True(tip.Text.Length <= TrayTipBuilder.MaxTip, tip.Text);
        Assert.StartsWith("Rigbay\nAlpha reading 1,234,567 rpm · Beta reading 7,654,321 rpm", tip.Text);
        Assert.EndsWith("rpm", tip.Text);   // ends on a whole value
        int shown = tip.Items.Count(i => i.Status == TipItemStatus.Shown);
        Assert.Equal(5 - shown, tip.DroppedForLength);
        Assert.True(tip.DroppedForLength > 0);
        // the dropped ones are the last ones, in order
        Assert.All(tip.Items.Take(shown), i => Assert.Equal(TipItemStatus.Shown, i.Status));
        Assert.All(tip.Items.Skip(shown), i => Assert.Equal(TipItemStatus.TooLong, i.Status));
        // and the longest text that still fits is kept: one more item would not fit
        var oneMore = tip.Text + " · " + tip.Items[shown].Text;
        Assert.True(oneMore.Length > TrayTipBuilder.MaxTip);
    }

    [Fact]
    public void Exactly_127_characters_fits_and_128_does_not()
    {
        var s = new FakeSource();
        // "Rigbay\n" is 7 characters, "A " 2 more
        s.Reading("m.a", "A", new string('1', 127 - 7 - 2));
        var fits = TrayTipBuilder.Build(Custom(Item("m.a")), s);
        Assert.Equal(127, fits.Text.Length);
        Assert.Equal(0, fits.DroppedForLength);
        s.Reading("m.a", "A", new string('1', 127 - 7 - 2 + 1));
        var over = TrayTipBuilder.Build(Custom(Item("m.a")), s);
        Assert.Equal("Rigbay", over.Text);   // the only item does not fit; nothing is cut
        Assert.Equal(1, over.DroppedForLength);
    }

    [Fact]
    public void A_dropped_item_takes_its_line_break_with_it()
    {
        var s = new FakeSource();
        s.Reading("m.a", "A", new string('1', 100));
        s.Reading("m.b", "B", "2");
        var fits = TrayTipBuilder.Build(Custom(Item("m.a"), Item("m.b", newLine: true)), s);
        Assert.Equal("Rigbay\nA " + new string('1', 100) + "\nB 2", fits.Text);   // 7 + 102 + 4 = 113
        s.Reading("m.a", "A", new string('1', 116));
        var tooLong = TrayTipBuilder.Build(Custom(Item("m.a"), Item("m.b", newLine: true)), s);
        Assert.Equal("Rigbay\nA " + new string('1', 116), tooLong.Text);   // 125; "\nB 2" would make 129
        Assert.Equal(TipItemStatus.TooLong, tooLong.Items[1].Status);
    }

    [Fact]
    public void The_counter_reads_n_of_127()
    {
        Assert.Equal("6 / 127", TrayTipBuilder.Counter("Rigbay"));
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void Clean_drops_empty_and_repeated_ids_trims_and_caps_the_list()
    {
        var raw = new TrayTipSettings { Mode = "CUSTOM", Items = { Item(" cpu.temp "), Item(""), Item("cpu.temp", "again"), Item("state:xtu", "x\ny") } };
        for (int i = 0; i < 40; i++) raw.Items.Add(Item("m." + i));
        raw.Items.Add(null!);
        var c = TrayTipSettings.Clean(raw);
        Assert.True(c.IsCustom);
        Assert.Equal(TrayTipSettings.MaxItems, c.Items.Count);
        Assert.Equal("cpu.temp", c.Items[0].Id);
        Assert.Equal("state:xtu", c.Items[1].Id);
        Assert.Equal("x y", c.Items[1].Label);
        Assert.NotSame(raw.Items[0], c.Items[0]);
    }

    sealed class AppFileLikeSettings
    {
        public string Theme { get; set; } = "system";
        public bool ReleaseMemoryInTray { get; set; } = true;
        public TrayTipSettings TrayTip { get; set; } = new();
    }

    [Fact]
    public void An_old_settings_file_without_the_new_fields_reads_as_automatic()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "app.json");
        File.WriteAllText(path, "{ \"version\": 1, \"data\": { \"theme\": \"dark\", \"releaseMemoryInTray\": false } }");
        var store = new JsonSettingsStore(path, new ListLog());
        var s = store.Load<AppFileLikeSettings>();
        Assert.Equal("dark", s.Theme);
        Assert.NotNull(s.TrayTip);
        Assert.False(s.TrayTip.IsCustom);
        Assert.True(s.TrayTip.ShowTitle);
        Assert.Empty(s.TrayTip.Items);
        Assert.Equal(Legacy(Rack()), TrayTipBuilder.Build(s.TrayTip, Rack()).Text);
    }

    [Fact]
    public void A_null_or_odd_tray_tip_in_the_file_cleans_to_automatic_or_what_can_be_kept()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "app.json");
        File.WriteAllText(path, "{ \"version\": 1, \"data\": { \"trayTip\": null } }");
        var s = new JsonSettingsStore(path, new ListLog()).Load<AppFileLikeSettings>();
        Assert.False(TrayTipSettings.Clean(s.TrayTip).IsCustom);
        File.WriteAllText(path, "{ \"version\": 1, \"data\": { \"trayTip\": { \"mode\": \"custom\", \"items\": [ { \"id\": \"cpu.temp\" }, { \"label\": \"no id\" } ] } } }");
        var c = TrayTipSettings.Clean(new JsonSettingsStore(path, new ListLog()).Load<AppFileLikeSettings>().TrayTip);
        Assert.True(c.IsCustom);
        Assert.Single(c.Items);
        Assert.True(c.ShowTitle);
    }

    [Fact]
    public void Custom_settings_survive_a_save_and_a_load()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Root, "app.json");
        var store = new JsonSettingsStore(path, new ListLog());
        var written = new AppFileLikeSettings
        {
            TrayTip = new TrayTipSettings
            {
                Mode = TrayTipSettings.Custom, ShowTitle = false,
                Items = { Item("cpu.temp", "Chip", false, "predator"), Item("state:xtu", "", true, "xtu"), Item("gone.metric", "Gone", false, "gone") },
            },
        };
        store.Save(written);
        var read = new JsonSettingsStore(path, new ListLog()).Load<AppFileLikeSettings>().TrayTip;
        Assert.True(read.IsCustom);
        Assert.False(read.ShowTitle);
        Assert.Equal(written.TrayTip.Items.Select(i => (i.Id, i.Module, i.Label, i.NewLine)), read.Items.Select(i => (i.Id, i.Module, i.Label, i.NewLine)));
        Assert.Equal(TrayTipBuilder.Build(written.TrayTip, Rack()).Text, TrayTipBuilder.Build(read, Rack()).Text);
    }
}
