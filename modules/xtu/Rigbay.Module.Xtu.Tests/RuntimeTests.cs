using System.Text.RegularExpressions;
using Rigbay.Abstractions;
using Rigbay.Module.Xtu;
using Xunit;

namespace Rigbay.Module.Xtu.Tests;

internal sealed class Rig : IDisposable
{
    public readonly FakeHost Host = new();
    public readonly FakeClient Client = new();
    public readonly FakeSystem Sys = new();
    public readonly FakePower Power = new();
    public readonly XtuRuntime Runtime;
    public readonly string Dir;

    public Rig(bool startRuntime = true, string? profilesXml = null, Action<XtuSettings>? settings = null, string? uiSettings = null, string? hotkeyFile = null)
    {
        Dir = Path.Combine(Path.GetTempPath(), "xtu-rig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "Profiles.xml"), profilesXml ?? SampleXml);
        if (uiSettings is not null) File.WriteAllText(Path.Combine(Dir, "uisettings.xml"), uiSettings);
        if (hotkeyFile is not null) File.WriteAllBytes(Path.Combine(Dir, "xtu.hotkey.settings"), System.Text.Encoding.Latin1.GetBytes(hotkeyFile));
        if (settings is not null)
        {
            var s = new XtuSettings();
            settings(s);
            Host.FakeSettings.Save(s);
        }
        Power.Line = PowerLine.Online;
        Runtime = new XtuRuntime(Host, Client, Sys, new XtuPaths(Path.Combine(Dir, "Drivers"), Dir), Power) { ResumeDelay = TimeSpan.Zero };
        if (startRuntime) Runtime.Start();
    }

    public const string SampleXml = """
        <ProfileDatabase><Profiles>
          <DatabaseProfile><Name>PL55-60W</Name><ControlSettings>
            <ControlSetting><Id>48</Id><Value>55</Value></ControlSetting><ControlSetting><Id>47</Id><Value>60</Value></ControlSetting>
            <ControlSetting><Id>49</Id><Value>1</Value></ControlSetting><ControlSetting><Id>66</Id><Value>56</Value></ControlSetting>
          </ControlSettings></DatabaseProfile>
          <DatabaseProfile><Name>PL35W</Name><ControlSettings>
            <ControlSetting><Id>48</Id><Value>35</Value></ControlSetting><ControlSetting><Id>47</Id><Value>40</Value></ControlSetting>
            <ControlSetting><Id>49</Id><Value>1</Value></ControlSetting><ControlSetting><Id>66</Id><Value>28</Value></ControlSetting>
          </ControlSettings></DatabaseProfile>
        </Profiles></ProfileDatabase>
        """;

    public PageModel Page => Runtime.BuildPage();
    public T Row<T>(string header) where T : Row => Page.EnumerateRows().OfType<T>().First(r => r.HeaderKey == header);

    public static async Task<bool> WaitFor(Func<bool> condition, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    public void Dispose() => Runtime.Dispose();
}

public class RuntimeTests
{
    [Fact]
    public void Start_registers_commands_and_one_hotkey_and_tray_item_per_profile()
    {
        using var rig = new Rig();

        Assert.Equal(new[] { "apply", "list", "status" }, rig.Host.FakeCommands.Handlers.Keys.OrderBy(k => k));
        Assert.Equal(3, rig.Host.FakeHotkeys.Registered.Count);       // Default (BIOS), PL55-60W, PL35W
        Assert.Equal(3, rig.Host.FakeTray.Items.Count);
        Assert.All(rig.Host.FakeHotkeys.Registered, r => Assert.Null(r.Default));   // nothing bound until the user chooses
        Assert.All(rig.Host.FakeHotkeys.Registered, r => Assert.StartsWith("=", r.NameKey));
        Assert.All(rig.Host.FakeTray.Items.Values, t => Assert.True(t.Checkable));
    }

    [Fact]
    public void The_active_profile_is_marked_in_the_tray_and_shown_on_the_page()
    {
        using var rig = new Rig();    // hardware: 55 / 60 / on / 56 = PL55-60W

        Assert.Equal("PL55-60W", rig.Row<ValueRow>("row.active.header").Value.Value);
        Assert.Equal("PL1 55 W / PL2 60 W / 56 s", rig.Row<ValueRow>("row.limits.header").Value.Value);
        Assert.Equal("p:PL55-60W", rig.Row<ChoiceRow>("row.profile.header").Value.Value);
        var checkedItems = rig.Host.FakeTray.Items.Values.Where(t => t.Checked.Value).ToList();
        Assert.Single(checkedItems);
        Assert.Equal("=PL55-60W", checkedItems[0].TextKey);
        Assert.Equal(55, rig.Host.FakeMetrics.Last("cpu.power.pl1"));
        Assert.Equal(60, rig.Host.FakeMetrics.Last("cpu.power.pl2"));
    }

    [Fact]
    public void Values_that_match_no_profile_show_as_custom_values()
    {
        using var rig = new Rig(startRuntime: false);
        rig.Client.State = rig.Client.State with { Pl1 = 47 };
        rig.Runtime.Start();

        Assert.Equal("Custom values", rig.Row<ValueRow>("row.active.header").Value.Value);
        Assert.Equal("-", rig.Row<ChoiceRow>("row.profile.header").Value.Value);
        Assert.DoesNotContain(rig.Host.FakeTray.Items.Values, t => t.Checked.Value);
    }

    [Fact]
    public void The_page_has_the_expected_groups_and_marks_the_profile_choice_as_a_hardware_change()
    {
        using var rig = new Rig();
        var page = rig.Page;

        Assert.Equal(new[] { "group.now", "group.readings", "group.auto", "group.custom", "group.hotkeys", "group.about" }, page.Groups.Select(g => g.HeaderKey));
        var choice = rig.Row<ChoiceRow>("row.profile.header");
        Assert.NotNull(choice.HardwareEffect);
        Assert.Equal(ApplyService.EffectKey, choice.HardwareEffect!.DescriptionKey);
        Assert.Equal(new[] { "-", "p:Default (BIOS)", "p:PL55-60W", "p:PL35W" }, choice.Options.Select(o => o.Id));
        Assert.Equal("=PL55-60W  ·  PL1 55 W / PL2 60 W / 56 s", choice.Options[2].LabelKey);
        Assert.True(Text.IsLiteral(choice.Options[2].LabelKey));       // the host shows it as it is
        Assert.NotNull(rig.Row<ToggleRow>("row.auto.header").HardwareEffect);
        Assert.NotNull(page.Summary);
        Assert.Equal(new[] { "cpu.temp", "cpu.power" }, page.Summary!.Readings!.Select(m => m.Value));
        Assert.Equal(3, page.EnumerateRows().OfType<ShortcutRow>().Count());
    }

    [Fact]
    public void Every_key_the_page_uses_exists_in_en_lang()
    {
        using var rig = new Rig();
        var loc = rig.Host.FakeLocalisation;
        var missing = new List<string>();
        void Need(string? key)
        {
            if (string.IsNullOrEmpty(key) || key[0] == '=') return;
            if (!loc.Has(key)) missing.Add(key);
        }
        foreach (var g in rig.Page.Groups)
        {
            Need(g.HeaderKey);
            foreach (var r in g.Rows) CheckRow(r);
        }
        void CheckRow(Row r)
        {
            Need(r.HeaderKey); Need(r.DescriptionKey); Need(r.HardwareEffect?.DescriptionKey);
            switch (r)
            {
                case ChoiceRow c: foreach (var o in c.Options) Need(o.LabelKey); break;
                case ButtonRow b: Need(b.ButtonKey); break;
                case TextRow t: Need(t.PlaceholderKey); break;
                case ExpanderRow e: foreach (var ch in e.Children) CheckRow(ch); break;
            }
        }
        foreach (var l in rig.Page.Links) Need(l.TextKey);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task Choosing_a_profile_on_the_page_applies_it_without_a_second_question()
    {
        using var rig = new Rig();
        var choice = rig.Row<ChoiceRow>("row.profile.header");

        choice.Value.Value = "p:PL35W";    // the host sets this after its own confirmation

        Assert.True(await Rig.WaitFor(() => rig.Client.Applied.Count == 1));
        Assert.Equal("PL35W", rig.Client.Applied[0].Name);
        Assert.Empty(rig.Host.FakeApproval.Asked);
        Assert.True(await Rig.WaitFor(() => choice.Value.Value == "p:PL35W"));
        Assert.True(await Rig.WaitFor(() => rig.Row<ValueRow>("row.status.header").Value.Value.StartsWith("Applied PL35W", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task When_XTU_refuses_the_choice_goes_back_to_what_is_really_active_and_the_user_is_told()
    {
        using var rig = new Rig();
        rig.Client.ApplyError = new XtuException("err.rejected", "InvalidValue");
        var choice = rig.Row<ChoiceRow>("row.profile.header");

        choice.Value.Value = "p:PL35W";

        Assert.True(await Rig.WaitFor(() => rig.Host.FakeToast.Shown.Count == 1));
        Assert.True(await Rig.WaitFor(() => choice.Value.Value == "p:PL55-60W"));
        Assert.Equal(ToastKind.Error, rig.Host.FakeToast.Shown[0].Kind);
        Assert.Contains("rejected the values (InvalidValue)", rig.Row<ValueRow>("row.status.header").Value.Value);
    }

    [Fact]
    public async Task A_hotkey_asks_first_and_declined_means_nothing_is_written()
    {
        using var rig = new Rig();
        rig.Host.FakeApproval.Answer = false;
        var hotkey = rig.Host.FakeHotkeys.Registered.First(r => r.NameKey == "=PL35W");

        hotkey.Invoke();

        Assert.True(await Rig.WaitFor(() => rig.Host.FakeApproval.Asked.Count == 1));
        await Task.Delay(100);
        Assert.Empty(rig.Client.Applied);
        Assert.Equal("approve.detail", rig.Host.FakeApproval.Asked[0].Detail!.Value.Key);
        Assert.Equal(new object[] { "PL35W", "35", "40", "28", "PL1 55 W / PL2 60 W / 56 s" }, rig.Host.FakeApproval.Asked[0].Detail!.Value.Args!);
    }

    [Fact]
    public async Task A_hotkey_with_approval_applies_and_says_so()
    {
        using var rig = new Rig();
        rig.Host.FakeHotkeys.Registered.First(r => r.NameKey == "=PL35W").Invoke();

        Assert.True(await Rig.WaitFor(() => rig.Client.Applied.Count == 1));
        Assert.True(await Rig.WaitFor(() => rig.Host.FakeToast.Shown.Count == 1));
        Assert.Equal(ToastKind.Success, rig.Host.FakeToast.Shown[0].Kind);
    }

    [Fact]
    public async Task A_tray_item_asks_first_too()
    {
        using var rig = new Rig();
        rig.Host.FakeApproval.Answer = false;
        rig.Host.FakeTray.Items.Values.First(t => t.TextKey == "=PL35W").Invoke();
        Assert.True(await Rig.WaitFor(() => rig.Host.FakeApproval.Asked.Count == 1));
        Assert.Empty(rig.Client.Applied);
    }

    [Fact]
    public void A_gesture_XTU_already_uses_is_refused_and_the_old_one_comes_back()
    {
        const string ui = "<Settings><NameValuePair><Name>HotkeyPairingOn</Name><Value>True</Value></NameValuePair></Settings>";
        using var rig = new Rig(uiSettings: ui, hotkeyFile: "\0CONTROL+SHIFT+F1\0");
        var binding = rig.Host.FakeHotkeys.Bindings.Values.First();

        binding.Gesture.Value = HotkeyGesture.Parse("Ctrl+Alt+F5");
        Assert.Equal("Ctrl+Alt+F5", binding.Gesture.Value!.ToString());

        binding.Gesture.Value = HotkeyGesture.Parse("Ctrl+Shift+F1");      // XTU's
        Assert.Equal("Ctrl+Alt+F5", binding.Gesture.Value!.ToString());
        Assert.Contains(rig.Host.FakeToast.Shown, t => t.Body.Key == "hotkey.xtu-combo");

        binding.Gesture.Value = HotkeyGesture.Parse("Shift+F6");           // no Ctrl or Alt
        Assert.Equal("Ctrl+Alt+F5", binding.Gesture.Value!.ToString());
        Assert.Contains(rig.Host.FakeToast.Shown, t => t.Body.Key == "hotkey.needs-modifier");
    }

    [Fact]
    public void Unreadable_XTU_is_shown_honestly_and_the_choice_is_disabled()
    {
        using var rig = new Rig(startRuntime: false);
        rig.Client.ReadError = new XtuException("err.unavailable", "pipe closed");
        rig.Runtime.Start();

        Assert.True(rig.Row<InfoRow>("row.unreachable.header").IsOpen.Value);
        Assert.False(rig.Row<ChoiceRow>("row.profile.header").Enabled.Value);
        Assert.Contains("pipe closed", rig.Row<ValueRow>("row.status.header").Value.Value);
        Assert.Equal("state.unreachable", rig.Runtime.BuildPage().Summary!.StateLine!.Value.Key);

        rig.Client.ReadError = null;
        rig.Runtime.Refresh();
        Assert.False(rig.Row<InfoRow>("row.unreachable.header").IsOpen.Value);
        Assert.True(rig.Row<ChoiceRow>("row.profile.header").Enabled.Value);
    }

    [Fact]
    public void Fast_Startup_on_shows_the_warning()
    {
        using var rig = new Rig(startRuntime: false);
        rig.Sys.Fast = "On";
        rig.Runtime.Start();
        Assert.True(rig.Row<InfoRow>("row.faststart.header").IsOpen.Value);
    }

    [Fact]
    public void The_dashboard_line_names_the_active_profile()
    {
        using var rig = new Rig();
        var line = rig.Runtime.BuildPage().Summary!.StateLine!.Value;
        Assert.Equal(("state.active", "PL55-60W"), (line.Key, line.Args![0]));
    }

    [Fact]
    public void Monitor_ticks_publish_on_the_bus()
    {
        using var rig = new Rig();
        rig.Client.Monitor.Enqueue((new Dictionary<uint, decimal> { [Mon.PkgTemp] = 70, [Mon.PkgPower] = 30 }, true));
        rig.Client.Monitor.Enqueue((new Dictionary<uint, decimal> { [Mon.PkgTemp] = 71, [Mon.PkgPower] = 31 }, false));
        rig.Runtime.MonitorOnce();
        rig.Runtime.MonitorOnce();
        Assert.Equal(71, rig.Host.FakeMetrics.Last("cpu.temp"));
        Assert.Equal(31, rig.Host.FakeMetrics.Last("cpu.power"));
    }

    [Fact]
    public void Disposing_closes_the_client()
    {
        var rig = new Rig();
        rig.Dispose();
        Assert.True(rig.Client.Disposed);
    }
}

public class RuntimeAutoSwitchTests
{
    [Fact]
    public async Task Plugging_in_applies_the_ac_profile_without_asking()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL35W"; s.BatteryProfile = "PL55-60W"; });
        rig.Power.Line = PowerLine.Online;

        rig.Runtime.AutoCheck(force: false);

        Assert.True(await Rig.WaitFor(() => rig.Client.Applied.Count == 1));
        Assert.Equal("PL35W", rig.Client.Applied[0].Name);
        Assert.Empty(rig.Host.FakeApproval.Asked);
    }

    [Fact]
    public async Task Unplugging_applies_the_battery_profile_and_a_manual_change_stays_until_the_source_changes()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL35W"; s.BatteryProfile = "PL55-60W"; });
        rig.Runtime.AutoCheck(false);                       // first look: AC
        Assert.True(await Rig.WaitFor(() => rig.Client.Applied.Count == 1));

        rig.Runtime.AutoCheck(false);                       // nothing changed: leave a manual choice alone
        await Task.Delay(100);
        Assert.Single(rig.Client.Applied);

        rig.Power.Line = PowerLine.Offline;
        rig.Power.RaiseChanged();
        Assert.True(await Rig.WaitFor(() => rig.Client.Applied.Count == 2));
        Assert.Equal("PL55-60W", rig.Client.Applied[1].Name);
    }

    [Fact]
    public async Task Waking_from_sleep_applies_again_even_though_the_source_did_not_change()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL35W"; });
        rig.Runtime.AutoCheck(false);
        Assert.True(await Rig.WaitFor(() => rig.Client.Applied.Count == 1));
        // the firmware put the limits back during sleep
        rig.Client.State = rig.Client.State with { Pl1 = 90, Pl2 = 115, Window = 56 };

        rig.Power.RaiseResumed();

        Assert.True(await Rig.WaitFor(() => rig.Client.Applied.Count == 2));
        Assert.Equal("PL35W", rig.Client.Applied[1].Name);
    }

    [Fact]
    public async Task Resume_does_nothing_when_auto_switch_is_off()
    {
        using var rig = new Rig(settings: s => { s.AcProfile = "PL35W"; });
        rig.Power.RaiseResumed();
        await Task.Delay(150);
        Assert.Empty(rig.Client.Applied);
    }

    [Fact]
    public async Task A_profile_that_no_longer_exists_is_reported()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "Gone"; });
        rig.Runtime.AutoCheck(false);
        await Task.Delay(50);
        Assert.Empty(rig.Client.Applied);
        Assert.Contains(rig.Host.FakeToast.Shown, t => t.Body.Key == "msg.auto-missing");
    }

    [Fact]
    public async Task Turning_auto_switch_on_applies_the_profile_for_the_current_source_at_once()
    {
        using var rig = new Rig(settings: s => { s.AcProfile = "PL35W"; });
        rig.Row<ToggleRow>("row.auto.header").Value.Value = true;
        Assert.True(await Rig.WaitFor(() => rig.Client.Applied.Count == 1));
        Assert.True(rig.Host.FakeSettings.Load<XtuSettings>().AutoSwitch);
    }

    [Fact]
    public void Nothing_is_applied_just_by_starting_the_module()
    {
        using var rig = new Rig(settings: s => { s.AutoSwitch = true; s.AcProfile = "PL35W"; });
        Assert.Empty(rig.Client.Applied);       // the first check happens on the timer, not in Start
    }
}

public class RuntimeProfileEditingTests
{
    [Fact]
    public async Task Creating_a_profile_saves_it_and_adds_a_hotkey_and_tray_item()
    {
        using var rig = new Rig();
        rig.Row<TextRow>("row.new-name.header").Value.Value = "  Quiet 30  ";
        rig.Row<NumberRow>("row.edit-pl1.header").Value.Value = 30;
        rig.Row<NumberRow>("row.edit-pl2.header").Value.Value = 35;

        var result = await rig.Row<ButtonRow>("row.create.header").InvokeAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var saved = rig.Host.FakeSettings.Load<XtuSettings>().Customs.Single();
        Assert.Equal(("Quiet 30", 30.0, 35.0, true, 28.0), (saved.Name, saved.Pl1, saved.Pl2, saved.Pl2Enable, saved.Window));
        Assert.Contains(rig.Runtime.Profiles, p => p.Name == "Quiet 30" && p.Kind == ProfileKind.Custom);
        Assert.Contains(rig.Host.FakeTray.Items.Values, t => t.TextKey == "=Quiet 30");
        Assert.Contains(rig.Host.FakeHotkeys.Registered, h => h.NameKey == "=Quiet 30");
        Assert.True(rig.Host.Refreshes >= 1, "the host is asked to redraw the page");
    }

    [Theory]
    [InlineData("", 45, 65, 28, "val.name-empty")]
    [InlineData("PL35W", 45, 65, 28, "val.name-taken")]
    [InlineData("pl35w", 45, 65, 28, "val.name-taken")]
    [InlineData("@bad", 45, 65, 28, "val.name-chars")]
    [InlineData("Hot", 100, 90, 28, "val.pl2")]
    [InlineData("Hot", 45, 65, 200, "val.window")]
    public async Task Bad_input_is_refused_and_nothing_is_saved(string name, double pl1, double pl2, double window, string key)
    {
        using var rig = new Rig();
        rig.Row<TextRow>("row.new-name.header").Value.Value = name;
        rig.Row<NumberRow>("row.edit-pl1.header").Value.Value = pl1;
        rig.Row<NumberRow>("row.edit-pl2.header").Value.Value = pl2;
        rig.Row<NumberRow>("row.edit-window.header").Value.Value = window;

        var result = await rig.Row<ButtonRow>("row.create.header").InvokeAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(key, result.Message!.Value.Key);
        Assert.Empty(rig.Host.FakeSettings.Load<XtuSettings>().Customs);
    }

    [Fact]
    public async Task Editing_a_value_of_a_custom_profile_saves_it_and_validates_against_the_other_values()
    {
        using var rig = new Rig(settings: s => s.Customs.Add(new CustomProfile { Name = "Mine", Pl1 = 40, Pl2 = 50, Window = 20 }));
        var expander = rig.Page.Groups.First(g => g.HeaderKey == "group.custom").Rows.OfType<ExpanderRow>().First(e => e.HeaderKey == "=Mine");
        var pl1 = expander.Children.OfType<NumberRow>().First(r => r.HeaderKey == "row.edit-pl1.header");

        Assert.Equal("val.pl2", pl1.Validate!(80)!.Value.Key);       // PL2 is 50, so PL1 80 would be above it
        Assert.Null(pl1.Validate!(45));
        Assert.Equal(ApplyMode.ApplyRevert, pl1.Apply);

        pl1.Value.Value = 45;
        Assert.Equal(45, rig.Host.FakeSettings.Load<XtuSettings>().Customs.Single().Pl1);
        Assert.Equal(45m, rig.Runtime.Profiles.Single(p => p.Name == "Mine").Pl1);
        Assert.True(rig.Host.Refreshes >= 1);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Deleting_removes_the_profile_its_tray_item_and_the_auto_switch_choice()
    {
        using var rig = new Rig(settings: s =>
        {
            s.Customs.Add(new CustomProfile { Name = "Mine", Pl1 = 40, Pl2 = 50, Window = 20 });
            s.AutoSwitch = true; s.AcProfile = "Mine";
        });
        var expander = rig.Page.Groups.First(g => g.HeaderKey == "group.custom").Rows.OfType<ExpanderRow>().First(e => e.HeaderKey == "=Mine");

        var result = await expander.Children.OfType<ButtonRow>().First().InvokeAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var saved = rig.Host.FakeSettings.Load<XtuSettings>();
        Assert.Empty(saved.Customs);
        Assert.Equal("", saved.AcProfile);
        Assert.DoesNotContain(rig.Runtime.Profiles, p => p.Name == "Mine");
        Assert.DoesNotContain(rig.Host.FakeTray.Items.Values, t => t.TextKey == "=Mine");
        Assert.True(rig.Host.Refreshes >= 1);
    }

    [Fact]
    public void Saved_profiles_come_back_after_the_module_is_turned_off_and_on()
    {
        using var rig = new Rig(settings: s => s.Customs.Add(new CustomProfile { Name = "Mine", Pl1 = 40, Pl2 = 50, Window = 20 }));
        Assert.Contains(rig.Runtime.Profiles, p => p.Name == "Mine");
        Assert.Contains(rig.Row<ChoiceRow>("row.profile.header").Options, o => o.Id == "p:Mine");
    }

    [Fact]
    public void An_XTU_profile_with_the_same_name_hides_a_saved_one()
    {
        using var rig = new Rig(settings: s => s.Customs.Add(new CustomProfile { Name = "pl35w", Pl1 = 1, Pl2 = 2, Window = 3 }));
        Assert.Equal(35m, rig.Runtime.Profiles.Single(p => p.Name.Equals("PL35W", StringComparison.OrdinalIgnoreCase)).Pl1);
    }
}

public class CommandTests
{
    [Fact]
    public async Task Status_prints_the_same_lines_as_the_standalone_app()
    {
        using var rig = new Rig();
        var r = await rig.Host.FakeCommands.Run("status");
        Assert.Equal(0, r.ExitCode);
        var lines = r.Output.Split(Environment.NewLine);
        Assert.Equal("service: Running", lines[0]);
        Assert.Equal("connected: True", lines[1]);
        Assert.Equal("active: PL1=55 PL2=60 pl2enable=True window=56", lines[2]);
        Assert.Equal("matching profile: PL55-60W", lines[3]);
        Assert.Equal("restore after reboot: True   fast startup: Off", lines[4]);
        Assert.Equal("profile: Default (BIOS)  PL1 90 W / PL2 115 W / 56 s", lines[5]);
        Assert.Equal("profile: PL55-60W  PL1 55 W / PL2 60 W / 56 s   <== active", lines[6]);
        Assert.Equal("profile: PL35W  PL1 35 W / PL2 40 W / 28 s", lines[7]);
    }

    [Fact]
    public async Task Status_with_XTU_unreachable_says_so_and_still_exits_zero_like_the_old_app()
    {
        using var rig = new Rig();
        rig.Client.ReadError = new XtuException("err.unavailable", "pipe closed");
        var r = await rig.Host.FakeCommands.Run("status");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("connected: False (Can't reach XTU. pipe closed)", r.Output);
    }

    [Fact]
    public async Task List_marks_the_active_profile()
    {
        using var rig = new Rig();
        var r = await rig.Host.FakeCommands.Run("list");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(3, r.Output.Split(Environment.NewLine).Length);
        Assert.Contains("PL55-60W  PL1 55 W / PL2 60 W / 56 s   <== active", r.Output);
    }

    [Fact]
    public async Task Apply_without_a_name_is_usage_exit_2()
    {
        using var rig = new Rig();
        Assert.Equal(2, (await rig.Host.FakeCommands.Run("apply")).ExitCode);
    }

    [Fact]
    public async Task Apply_unknown_profile_is_exit_2()
    {
        using var rig = new Rig();
        var r = await rig.Host.FakeCommands.Run("apply", "nope");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("profile not found: nope", r.Output);
        Assert.Empty(rig.Client.Applied);
    }

    [Fact]
    public async Task Apply_declined_is_exit_1_and_writes_nothing()
    {
        using var rig = new Rig();
        rig.Host.FakeApproval.Answer = false;
        var r = await rig.Host.FakeCommands.Run("apply", "PL35W");
        Assert.Equal(1, r.ExitCode);
        Assert.Empty(rig.Client.Applied);
    }

    [Fact]
    public async Task Apply_approved_is_exit_0_with_the_read_back()
    {
        using var rig = new Rig();
        var r = await rig.Host.FakeCommands.Run("apply", "pl35w");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("apply [PL35W] PL1 35 W / PL2 40 W / 28 s", r.Output);
        Assert.Contains("result: Applied: PL1 35 W · PL2 40 W · 28 s", r.Output);
    }

    [Fact]
    public async Task Apply_names_with_spaces_are_joined()
    {
        using var rig = new Rig();
        rig.Host.FakeApproval.Answer = true;
        var r = await rig.Host.FakeCommands.Run("apply", "Default", "(BIOS)");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Default (BIOS)", rig.Client.Applied.Single().Name);
    }

    [Fact]
    public async Task Apply_already_in_use_is_exit_0_without_asking()
    {
        using var rig = new Rig();
        var r = await rig.Host.FakeCommands.Run("apply", "PL55-60W");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("result: Already in use", r.Output);
        Assert.Empty(rig.Host.FakeApproval.Asked);
    }

    [Fact]
    public async Task Apply_failure_is_exit_1()
    {
        using var rig = new Rig();
        rig.Client.ApplyError = new XtuException("err.locked", "PL1");
        var r = await rig.Host.FakeCommands.Run("apply", "PL35W");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("PL1 is locked in XTU.", r.Output);
    }
}

public class LanguageTests
{
    private static readonly string[] Prefixes =
        { "probe", "err", "val", "msg", "state", "value", "row", "group", "choice", "effect", "approve", "toast", "cmd", "hotkey", "reason", "link", "new", "metric" };

    private static string ModuleSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Rigbay.Module.Xtu"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Rigbay.Module.Xtu");
    }

    [Fact]
    public void Every_key_used_in_the_source_exists_in_en_lang()
    {
        var loc = new FakeLocalisation();
        var literal = new Regex("\"((?:" + string.Join("|", Prefixes) + ")[.][a-z0-9-]+(?:[.][a-z0-9-]+)*)\"");
        var missing = new SortedSet<string>();
        foreach (var file in Directory.GetFiles(ModuleSource(), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            foreach (Match m in literal.Matches(File.ReadAllText(file)))
                if (!loc.Has(m.Groups[1].Value)) missing.Add(m.Groups[1].Value + " (" + Path.GetFileName(file) + ")");
        Assert.Empty(missing);
    }

    [Fact]
    public void Vietnamese_has_every_english_key_with_the_same_placeholders()
    {
        var en = new FakeLocalisation("en.lang");
        var vi = new FakeLocalisation("vi.lang");
        var ph = new Regex(@"\{(\d+)\}");
        static string Raw(FakeLocalisation l, string k) => l.T(k["module.xtu.".Length..]);
        var problems = new List<string>();
        foreach (var key in en.Keys.Where(k => k.StartsWith("module.xtu.", StringComparison.Ordinal)))
        {
            var e = Raw(en, key); var v = Raw(vi, key);
            if (v == key) { problems.Add("missing in vi: " + key); continue; }
            var pe = ph.Matches(e).Select(m => m.Value).OrderBy(x => x).ToArray();
            var pv = ph.Matches(v).Select(m => m.Value).OrderBy(x => x).ToArray();
            if (!pe.SequenceEqual(pv)) problems.Add("placeholders differ: " + key);
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void The_manifest_is_the_honest_one_the_brief_asks_for()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("xtu", root.GetProperty("id").GetString());
        Assert.Equal("power", root.GetProperty("group").GetString());
        Assert.True(root.GetProperty("requires").GetProperty("admin").GetBoolean());
        Assert.Contains(@"Intel(R) Extreme Tuning Utility\Drivers", root.GetProperty("probePaths")[0].GetString());
        Assert.Contains("XTU3SERVICE", root.GetProperty("requires").GetProperty("services")[0].GetString());
        Assert.Contains("Profiles.xml", root.GetProperty("requires").GetProperty("services")[1].GetString());
    }
}
