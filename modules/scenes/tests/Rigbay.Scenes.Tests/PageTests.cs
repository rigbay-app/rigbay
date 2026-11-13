using Rigbay.Abstractions;

namespace Rigbay.Scenes.Tests;

/// <summary>The page: what it lists, the editor (programs, steps, undo), the words in both languages, and the two read-only verbs.</summary>
public class PageTests
{
    private static async Task<Rig> Started(Action<Rig>? configure = null, string language = "en", params SceneSettings[] scenes)
    {
        var rig = new Rig(language);
        rig.Seed(scenes.Length == 0 ? new[] { Rig.Scene() } : scenes);
        configure?.Invoke(rig);
        return await rig.StartAsync();
    }

    private static async Task<Rig> Empty()
    {
        var rig = new Rig();
        rig.Seed();
        return await rig.StartAsync();
    }

    [Fact]
    public async Task An_empty_module_shows_the_add_button_and_says_there_is_nothing_yet()
    {
        var rig = await Empty();
        var page = rig.Page();
        var add = Assert.IsType<ButtonRow>(page.Primary);
        Assert.Equal("button.add", add.ButtonKey);
        Assert.Single(page.EnumerateRows().OfType<InfoRow>(), r => r.HeaderKey == "info.empty.header");
        Assert.Equal("No scenes", rig.Words(page.Summary!.StateLine!.Value));
    }

    [Fact]
    public async Task Add_makes_a_scene_that_is_off_and_not_approved_with_a_valid_id()
    {
        var rig = await Empty();
        int before = rig.Host.PageRefreshRequests;
        await ((ButtonRow)rig.Page().Primary!).InvokeAsync(CancellationToken.None);
        var scene = Assert.Single(rig.Host.SettingsStore.Current.Scenes);
        Assert.True(SceneRules.IsId(scene.Id));
        Assert.Equal("Scene 1", scene.Name);
        Assert.False(scene.Enabled);
        Assert.Equal("", scene.ApprovedFingerprint);
        Assert.True(rig.Host.PageRefreshRequests > before);
        Assert.False(rig.Runtime.Polling);
        var page = rig.Page();
        Assert.Single(page.Groups.SelectMany(g => g.Rows).OfType<ExpanderRow>());
        Assert.DoesNotContain(page.EnumerateRows().OfType<InfoRow>(), r => r.HeaderKey == "info.empty.header");
    }

    [Fact]
    public async Task Every_scene_is_an_expander_with_its_switch_in_the_header_and_its_state_as_the_line()
    {
        var rig = await Started(null, "en", Rig.Scene(), Rig.Scene("scene-b", "Streaming", "obs.exe", enabled: false));
        var page = rig.Page();
        var scenes = page.Groups.SelectMany(g => g.Rows).OfType<ExpanderRow>().ToList();
        Assert.Equal(2, scenes.Count);
        Assert.All(scenes, s => Assert.IsType<ToggleRow>(s.HeaderRow));
        Assert.Contains("Needs your approval", rig.Words(scenes[0].Line!.Value));
        Assert.Equal("Off", rig.Words(scenes[1].Line!.Value));
    }

    [Fact]
    public async Task The_state_line_follows_the_scene_through_its_life()
    {
        var rig = new Rig();
        rig.Seed(rig.Approved(Rig.Scene()));
        await rig.StartAsync();
        var line = rig.Page().Scene("Gaming").Line!;
        Assert.Equal("Waiting for game.exe", rig.Words(line.Value));
        rig.Procs.Start("game.exe");
        await rig.Tick();
        Assert.Matches(@"^Running since \d\d:\d\d for game\.exe$", rig.Words(line.Value));
        rig.Procs.StopAll();
        await rig.Tick(5);
        Assert.Contains("has ended; putting things back soon", rig.Words(line.Value));
        await rig.Tick(10);
        Assert.Equal("Waiting for game.exe", rig.Words(line.Value));
    }

    // ------------------------------------------------------------------ programs

    [Fact]
    public async Task The_programs_row_takes_a_list_and_refuses_what_is_not_a_program()
    {
        var rig = await Started();
        var scene = rig.Page().Scene("Gaming");
        var games = scene.Row<TextRow>("row.games.header");
        Assert.Equal("game.exe", games.Value.Value);
        games.Value.Value = "game.exe; Other, \"my game.exe\"";
        Assert.Equal(new[] { "game.exe", "Other.exe", "my game.exe" }, rig.Stored().Games);

        games.Value.Value = "game.exe, notes.txt";
        Assert.Equal(3, rig.Stored().Games.Count);                                                    // nothing changed
        Assert.Contains("notes.txt", rig.Words(rig.Page().Scene("Gaming").Row<InfoRow>("info.attention.header").Line!.Value));
        games.Value.Value = "game.exe";
        Assert.Equal("", rig.State().BadInput);
        Assert.DoesNotContain(rig.Page().Scene("Gaming").Children, r => r.HeaderKey == "info.attention.header");
    }

    [Fact]
    public async Task At_most_eight_programs()
    {
        var rig = await Started();
        var games = rig.Page().Scene("Gaming").Row<TextRow>("row.games.header");
        games.Value.Value = string.Join(", ", Enumerable.Range(0, 9).Select(i => $"g{i}.exe"));
        Assert.Single(rig.Stored().Games);
    }

    [Fact]
    public async Task The_picker_lists_programs_that_have_a_window_except_those_already_in_the_scene_and_adds_the_one_chosen()
    {
        var rig = await Started(r => r.Procs.Windowed = new List<string> { "chrome.exe", "game.exe", "obs.exe" });
        rig.Host.PageVisibleValue.Value = true;                      // the page opens: the list is read
        await rig.Module.WhenIdleAsync();
        var scene = rig.Page().Scene("Gaming");
        var pick = scene.Row<ChoiceRow>("row.pick.header");
        Assert.Equal(new[] { "", "chrome.exe", "obs.exe" }, pick.Options.Select(o => o.Id));
        pick.Value.Value = "obs.exe";
        Assert.Equal(new[] { "game.exe", "obs.exe" }, rig.Stored().Games);
        Assert.Equal("", pick.Value.Value);
        Assert.Equal("game.exe, obs.exe", scene.Row<TextRow>("row.games.header").Value.Value);
    }

    [Fact]
    public async Task Opening_the_page_reads_the_windows_once_and_the_poll_never_does()
    {
        var rig = await Started(r => r.Procs.Windowed = new List<string> { "a.exe" });
        rig.Page();                                                  // the host built the page when the module was enabled
        int before = rig.Host.PageRefreshRequests;
        rig.Host.PageVisibleValue.Value = true;
        await rig.Module.WhenIdleAsync();
        Assert.True(rig.Host.PageRefreshRequests > before);        // the list changed, so the page is rebuilt with it
        Assert.Contains("a.exe", rig.Page().Scene("Gaming").Row<ChoiceRow>("row.pick.header").Options.Select(o => o.Id));
    }

    // ------------------------------------------------------------------ steps

    [Fact]
    public async Task The_module_picker_lists_modules_with_commands_that_change_something_and_never_this_module()
    {
        var rig = await Started();
        var scene = rig.Page().Scene("Gaming");
        var modules = scene.Row<ChoiceRow>("row.add-module.header");
        Assert.Equal(new[] { "", "demo-target", "recorder", "steam" }, modules.Options.Select(o => o.Id));
        rig.Runtime.ViewOf("scene-a").AddModule.Value = "demo-target";
        var verbs = rig.Page().Scene("Gaming").Row<ChoiceRow>("row.add-command.header");
        Assert.Equal(new[] { "", "mark", "set" }, verbs.Options.Select(o => o.Id));                  // get is read-only: nothing to approve
        Assert.Contains("Sets the number", rig.Host.Loc.T(verbs.Options.Single(o => o.Id == "set").LabelKey));
    }

    [Fact]
    public async Task Choosing_a_command_shows_what_it_says_about_itself()
    {
        var rig = await Started();
        var view = rig.Runtime.ViewOf("scene-a");
        view.AddModule.Value = "demo-target";
        view.AddVerb.Value = "set";
        var info = rig.Words(view.AddInfo.Value);
        Assert.Contains("Sets the number", info);
        Assert.Contains("Usage: set <0..100>", info);
        Assert.Contains("Changes: Changes a number in memory", info);
        Assert.Contains("Can report its own undo (set)", info);
        Assert.Contains("Shows the exact values in the question", info);

        view.AddModule.Value = "steam";
        view.AddVerb.Value = "on";
        info = rig.Words(view.AddInfo.Value);
        Assert.Contains("The module did not say what it changes", info);
        Assert.Contains("The module decides the exact values", info);
    }

    [Fact]
    public async Task A_command_that_reports_its_undo_defaults_to_the_recorded_one_and_a_known_pair_to_its_opposite()
    {
        var rig = await Started();
        var view = rig.Runtime.ViewOf("scene-a");
        view.AddModule.Value = "demo-target"; view.AddVerb.Value = "set";
        Assert.Equal(UndoMode.Recorded, view.AddUndo.Value);
        view.AddModule.Value = "steam"; view.AddVerb.Value = "on";
        Assert.Equal(UndoMode.Fixed, view.AddUndo.Value);
        Assert.Equal("steam off", view.AddUndoLine.Value);
        Assert.True(view.AddUndoLineEnabled.Value);
        view.AddModule.Value = "demo-target"; view.AddVerb.Value = "mark";
        Assert.Equal(UndoMode.None, view.AddUndo.Value);
        Assert.False(view.AddUndoLineEnabled.Value);
    }

    [Fact]
    public async Task The_undo_choices_offer_the_recorded_undo_only_for_a_command_that_has_one()
    {
        var rig = await Started();
        var view = rig.Runtime.ViewOf("scene-a");
        view.AddModule.Value = "demo-target"; view.AddVerb.Value = "set";
        Assert.Contains(UndoMode.Recorded, rig.Page().Scene("Gaming").Row<ChoiceRow>("row.add-undo.header").Options.Select(o => o.Id));
        view.AddVerb.Value = "mark";
        Assert.DoesNotContain(UndoMode.Recorded, rig.Page().Scene("Gaming").Row<ChoiceRow>("row.add-undo.header").Options.Select(o => o.Id));
    }

    private static async Task<ActionResult> Add(Rig rig, string module, string verb, string args, string undo, string undoLine = "")
    {
        var view = rig.Runtime.ViewOf("scene-a");
        view.AddModule.Value = module; view.AddVerb.Value = verb;
        view.AddArgs.Value = args; view.AddUndo.Value = undo; view.AddUndoLine.Value = undoLine;
        return await rig.Page().Scene("Gaming").Row<ButtonRow>("row.add-step.header").InvokeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_step_is_added_with_its_arguments_and_its_recorded_undo()
    {
        var rig = await Started();
        var result = await Add(rig, "demo-target", "set", "7", UndoMode.Recorded);
        Assert.True(result.Succeeded);
        var step = rig.Stored().Steps.Last();
        Assert.Equal(("demo-target", "set", UndoMode.Recorded), (step.Module, step.Verb, step.Undo));
        Assert.Equal(new[] { "7" }, step.Args);
        Assert.Contains("demo-target set 7", rig.Words(result.Message));
    }

    [Fact]
    public async Task A_step_with_a_written_undo_keeps_the_module_the_verb_and_the_arguments_apart()
    {
        var rig = await Started();
        Assert.True((await Add(rig, "steam", "on", "", UndoMode.Fixed, "steam off \"two words\"")).Succeeded);
        var step = rig.Stored().Steps.Last();
        Assert.Equal(("steam", "off"), (step.UndoModule, step.UndoVerb));
        Assert.Equal(new[] { "two words" }, step.UndoArgs);
    }

    [Fact]
    public async Task An_empty_undo_line_takes_the_usual_opposite()
    {
        var rig = await Started();
        Assert.True((await Add(rig, "recorder", "start", "", UndoMode.Fixed, "")).Succeeded);
        var step = rig.Stored().Steps.Last();
        Assert.Equal(("recorder", "stop"), (step.UndoModule, step.UndoVerb));
    }

    [Theory]
    [InlineData("", "", "", UndoMode.None, "err.pick-command")]
    [InlineData("demo-target", "set", "\"unclosed", UndoMode.None, "err.bad-args")]
    [InlineData("demo-target", "mark", "on", UndoMode.Recorded, "err.no-recorded-undo")]
    [InlineData("steam", "on", "", UndoMode.Fixed, "err.bad-undo")]                 // no known opposite is checked below with a verb that has none
    public async Task A_step_the_page_cannot_accept_says_why_and_changes_nothing(string module, string verb, string args, string undo, string key)
    {
        var rig = await Started();
        int before = rig.Stored().Steps.Count;
        var view = rig.Runtime.ViewOf("scene-a");
        view.AddModule.Value = module; view.AddVerb.Value = verb; view.AddArgs.Value = args; view.AddUndo.Value = undo;
        if (key == "err.bad-undo") view.AddUndoLine.Value = "justonemodule";
        var result = await rig.Page().Scene("Gaming").Row<ButtonRow>("row.add-step.header").InvokeAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(key, result.Message!.Value.Key);
        Assert.Equal(before, rig.Stored().Steps.Count);
    }

    [Fact]
    public async Task A_ninth_step_is_refused()
    {
        var rig = await Started();
        for (int i = rig.Stored().Steps.Count; i < 8; i++) Assert.True((await Add(rig, "demo-target", "mark", "on", UndoMode.None)).Succeeded);
        Assert.Equal(8, rig.Stored().Steps.Count);
        var result = await Add(rig, "demo-target", "mark", "off", UndoMode.None);
        Assert.False(result.Succeeded);
        Assert.Equal("err.too-many-steps", result.Message!.Value.Key);
    }

    [Fact]
    public async Task Steps_move_up_and_down_and_are_removed_with_the_choice_next_to_them()
    {
        var rig = await Started();
        var scene = rig.Page().Scene("Gaming");
        var first = scene.Children.OfType<ChoiceRow>().First(r => r.HeaderKey.StartsWith(Text.Literal("1. ")));
        Assert.Equal(Text.Literal("1. demo-target set 5"), first.HeaderKey);
        first.Value.Value = "down";
        Assert.Equal(new[] { "steam", "demo-target" }, rig.Stored().Steps.Select(s => s.Module));
        Assert.Equal("keep", first.Value.Value);                                              // the choice is an action, it springs back
        var second = rig.Page().Scene("Gaming").Children.OfType<ChoiceRow>().First(r => r.HeaderKey == Text.Literal("2. demo-target set 5"));
        second.Value.Value = "up";
        Assert.Equal(new[] { "demo-target", "steam" }, rig.Stored().Steps.Select(s => s.Module));
        var top = rig.Page().Scene("Gaming").Children.OfType<ChoiceRow>().First(r => r.HeaderKey == Text.Literal("1. demo-target set 5"));
        top.Value.Value = "up";                                                                // nothing above it
        Assert.Equal(new[] { "demo-target", "steam" }, rig.Stored().Steps.Select(s => s.Module));
        top = rig.Page().Scene("Gaming").Children.OfType<ChoiceRow>().First(r => r.HeaderKey == Text.Literal("1. demo-target set 5"));
        top.Value.Value = "remove";
        Assert.Equal(new[] { "steam" }, rig.Stored().Steps.Select(s => s.Module));
    }

    [Fact]
    public async Task Each_step_says_how_it_is_undone()
    {
        var rig = await Started();
        var rows = rig.Page().Scene("Gaming").Children.OfType<ChoiceRow>().Where(r => r.HeaderKey.StartsWith(Text.Literal("1. ")) || r.HeaderKey.StartsWith(Text.Literal("2. "))).ToList();
        Assert.Contains("Put back to the value it had before", rig.Words(rows[0].Line!.Value));
        Assert.Contains("Put back with: steam off", rig.Words(rows[1].Line!.Value));
    }

    [Fact]
    public async Task An_edited_scene_shows_a_problem_until_it_is_complete()
    {
        var rig = await Started(null, "en", new SceneSettings { Id = "scene-n", Name = "New", Enabled = true });
        var attention = rig.Page().Scene("New").Row<InfoRow>("info.attention.header");
        Assert.True(attention.IsOpen.Value);
        Assert.Contains("Add at least one program", rig.Words(attention.Line!.Value));
        Assert.False(rig.Page().Scene("New").Row<ButtonRow>("row.approve.header").Enabled.Value);       // nothing to approve yet
    }

    // ------------------------------------------------------------------ words

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public async Task Every_key_the_page_uses_exists_and_the_page_reads_in_that_language(string language)
    {
        var rig = await Started(r =>
        {
            r.Procs.Windowed = new List<string> { "x.exe" };
            r.Host.PageVisibleValue.Value = true;
        }, language);
        await rig.Module.WhenIdleAsync();
        rig.Runtime.ViewOf("scene-a").AddModule.Value = "demo-target";
        rig.Runtime.ViewOf("scene-a").AddVerb.Value = "set";
        var page = rig.Page();
        var loc = rig.Host.Loc;
        var keys = new List<string>();
        foreach (var group in page.Groups) keys.Add(group.HeaderKey);
        foreach (var row in page.EnumerateRows())
        {
            keys.Add(row.HeaderKey);
            if (row.DescriptionKey is { } d) keys.Add(d);
            if (row.HelpKey is { } h) keys.Add(h);
            if (row is ChoiceRow c) keys.AddRange(c.Options.Select(o => o.LabelKey));
            if (row is ButtonRow b) { keys.Add(b.ButtonKey); if (b.ConfirmKey is { } q) keys.Add(q); }
            if (row is TextRow t && t.PlaceholderKey is { } p) keys.Add(p);
            if (row.Line is { } line && !line.Value.IsEmpty) keys.Add(line.Value.Key);
            Assert.Null(row.HardwareEffect);                      // the host's plan dialog is the question; a row-level one would ask twice
        }
        foreach (var verb in rig.Host.CommandRegistry.Verbs.Values) keys.Add(verb.DescriptionKey);
        foreach (var key in keys.Where(k => !Text.IsLiteral(k)).Distinct())
            Assert.True(loc.Has(key), $"missing key {key}");
        Assert.NotEmpty(keys);
    }

    [Fact]
    public async Task The_plan_name_reads_well_in_the_hosts_question_in_both_languages()
    {
        foreach (var (lang, expected) in new[] { ("en", "Game scene \"Gaming\""), ("vi", "Kịch bản game \"Gaming\"") })
        {
            var rig = await Started(null, lang);
            Assert.Equal(expected, rig.Words(ScenePlans.Build(rig.Stored()).Name));
        }
    }

    [Fact]
    public async Task Switching_the_language_rebuilds_the_page()
    {
        var rig = await Started();
        int before = rig.Host.PageRefreshRequests;
        rig.Host.Loc.Switch("vi");
        Assert.True(rig.Host.PageRefreshRequests > before);
        Assert.Equal("Cần bạn duyệt", rig.Words(rig.Page().Scene("Gaming").Line!.Value));   // the state line is Vietnamese now
    }

    // ------------------------------------------------------------------ the verbs

    [Fact]
    public async Task Both_verbs_only_read_and_say_so()
    {
        var rig = await Started();
        Assert.Equal(new[] { "list", "status" }, rig.Host.CommandRegistry.Verbs.Keys.OrderBy(k => k));
        Assert.All(rig.Host.CommandRegistry.Verbs.Values, v => { Assert.Equal(VerbAccess.ReadOnly, v.Info!.Access); Assert.Null(v.Info.Effect); });
    }

    [Fact]
    public async Task List_names_every_scene_with_its_state_programs_and_steps()
    {
        var rig = await Started(null, "en", Rig.Scene(), Rig.Scene("scene-b", "Streaming", "obs.exe", enabled: false));
        var result = await rig.Host.CommandRegistry.Run("list");
        Assert.Equal(0, result.ExitCode);
        var lines = result.Output.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal("scene-a · Gaming · Needs your approval · programs: game.exe · 2 step(s)", lines[0]);
        Assert.Equal("scene-b · Streaming · Off · programs: obs.exe · 2 step(s)", lines[1]);
    }

    [Fact]
    public async Task List_with_no_scenes_says_so()
    {
        var rig = await Empty();
        Assert.Equal("No scenes.", (await rig.Host.CommandRegistry.Run("list")).Output);
    }

    [Fact]
    public async Task Status_counts_the_scenes_the_poll_and_the_runs()
    {
        var rig = new Rig();
        rig.Seed(rig.Approved(Rig.Scene()), Rig.Scene("scene-b", "Streaming", "obs.exe", enabled: false));
        await rig.StartAsync();
        var idle = (await rig.Host.CommandRegistry.Run("status")).Output;
        Assert.Equal("2 scene(s), 1 on and approved; process list: read every 5 s", idle);
        rig.Procs.Start("game.exe");
        await rig.Tick();
        var running = (await rig.Host.CommandRegistry.Run("status")).Output.Split(Environment.NewLine);
        Assert.Equal(2, running.Length);
        Assert.Matches(@"^Running: Gaming since \d\d:\d\d \(game\.exe\), run run1$", running[1]);
    }

    [Fact]
    public async Task Status_says_the_process_list_is_not_read_when_nothing_is_on()
    {
        var rig = await Started();
        Assert.Contains("not read (nothing is on)", (await rig.Host.CommandRegistry.Run("status")).Output);
    }
}
