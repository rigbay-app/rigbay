using Rigbay.Abstractions;

namespace Rigbay.Scenes.Tests;

/// <summary>The pure parts: program names, command lines, the rules a scene must keep, the plan and the fingerprint.</summary>
public class ModelTests
{
    [Theory]
    [InlineData("game.exe", "game.exe")]
    [InlineData("  Game.EXE ", "Game.EXE")]
    [InlineData("game", "game.exe")]
    [InlineData("\"my game.exe\"", "my game.exe")]
    [InlineData("D:\\Games\\Game\\game.exe", "D:\\Games\\Game\\game.exe")]
    [InlineData("D:/Games/game.exe", "D:\\Games\\game.exe")]
    public void A_program_name_is_stored_in_one_form(string typed, string stored) => Assert.Equal(stored, Games.Normalise(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("game.dll")]
    [InlineData("game.bat")]
    [InlineData("..\\game.exe")]
    [InlineData("games\\game.exe")]                       // relative path
    [InlineData("\\\\server\\share\\game.exe")]           // a share could change under the approval
    [InlineData("D:\\Games\\..\\other\\game.exe")]
    [InlineData("a|b.exe")]
    [InlineData("a:b.exe")]
    [InlineData("a*.exe")]
    [InlineData(".exe")]
    public void Something_that_is_not_a_program_name_is_refused(string typed) => Assert.Null(Games.Normalise(typed));

    [Fact]
    public void A_name_matches_on_the_file_name_in_any_case_and_a_path_only_on_that_file()
    {
        Assert.True(Games.Matches("Game.exe", new RunningProgram("game.exe", null, 1)));
        Assert.False(Games.Matches("game.exe", new RunningProgram("other.exe", null, 1)));
        Assert.True(Games.Matches("D:\\Games\\game.exe", new RunningProgram("game.exe", "d:\\games\\GAME.exe", 1)));
        Assert.False(Games.Matches("D:\\Games\\game.exe", new RunningProgram("game.exe", "E:\\Copy\\game.exe", 1)));
        Assert.False(Games.Matches("D:\\Games\\game.exe", new RunningProgram("game.exe", null, 1)));   // the path could not be read: not a match, never a guess
    }

    [Fact]
    public void Command_lines_split_on_spaces_and_keep_quoted_words()
    {
        Assert.Equal(new[] { "tunnel", "on", "home office", "x" }, CommandLine.Split("tunnel on \"home office\" x"));
        Assert.Equal(new[] { "a", "" }, CommandLine.Split("a \"\""));
        Assert.Empty(CommandLine.Split("   ")!);
        Assert.Null(CommandLine.Split("a \"b"));
        Assert.Equal("tunnel on \"home office\"", CommandLine.Of("tunnel", "on", new[] { "home office" }));
        var words = new[] { "xtu", "apply", "Game mode", "" };
        Assert.Equal(words, CommandLine.Split(CommandLine.Join(words)));
    }

    private static SceneSettings Scene() => Rig.Scene();

    [Fact]
    public void A_complete_scene_has_no_problem() => Assert.Equal(SceneProblem.None, SceneRules.Check(Scene()));

    [Fact]
    public void Each_way_a_scene_can_be_wrong_is_named()
    {
        SceneProblem Check(Action<SceneSettings> edit) { var s = Scene(); edit(s); return SceneRules.Check(s); }
        Assert.Equal(SceneProblem.BadId, Check(s => s.Id = "Scene A"));
        Assert.Equal(SceneProblem.NoName, Check(s => s.Name = "  "));
        Assert.Equal(SceneProblem.NoGames, Check(s => s.Games.Clear()));
        Assert.Equal(SceneProblem.TooManyGames, Check(s => s.Games = Enumerable.Range(0, 9).Select(i => $"g{i}.exe").ToList()));
        Assert.Equal(SceneProblem.BadGame, Check(s => s.Games.Add("not an exe.txt")));
        Assert.Equal(SceneProblem.BadGame, Check(s => s.Games = new List<string> { "game" }));   // stored form only: the page adds .exe before storing
        Assert.Equal(SceneProblem.NoSteps, Check(s => s.Steps.Clear()));
        Assert.Equal(SceneProblem.TooManySteps, Check(s => { while (s.Steps.Count < 9) s.Steps.Add(s.Steps[0].Clone()); }));
        Assert.Equal(SceneProblem.BadStep, Check(s => s.Steps[0].Module = "scenes"));          // never its own module (the host refuses it too)
        Assert.Equal(SceneProblem.BadStep, Check(s => s.Steps[0].Verb = "Set Value"));
        Assert.Equal(SceneProblem.BadStep, Check(s => s.Steps[0].Args = Enumerable.Range(0, 17).Select(i => "a").ToList()));
        Assert.Equal(SceneProblem.BadStep, Check(s => s.Steps[0].Args = new List<string> { new string('x', 257) }));
        Assert.Equal(SceneProblem.BadUndo, Check(s => s.Steps[1].UndoVerb = ""));
        Assert.Equal(SceneProblem.BadUndo, Check(s => s.Steps[0].Undo = "sometimes"));
        Assert.Equal(SceneProblem.BadUndo, Check(s => { s.Steps[1].UndoModule = "scenes"; }));
    }

    [Fact]
    public void Every_problem_has_a_language_key()
    {
        var en = FakeLocalisation.Load("en");
        foreach (var p in Enum.GetValues<SceneProblem>().Where(p => p != SceneProblem.None))
            Assert.True(en.ContainsKey("module.scenes." + SceneRules.ProblemKey(p)), p.ToString());
    }

    [Fact]
    public void The_plan_carries_the_scene_id_the_steps_and_how_each_is_undone()
    {
        var plan = ScenePlans.Build(Scene());
        Assert.Equal("scene-a", plan.Id);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(new PlanStep("demo-target", "set", ["5"]).Verb, plan.Steps[0].Verb);
        Assert.True(plan.Steps[0].RestoreRecorded);
        Assert.Null(plan.Steps[0].FixedUndo);
        Assert.False(plan.Steps[1].RestoreRecorded);
        Assert.Equal("steam", plan.Steps[1].FixedUndo!.ModuleId);
        Assert.Equal("off", plan.Steps[1].FixedUndo!.Verb);
        Assert.Equal("Gaming", plan.Name.Args![0]);

        var none = Scene();
        none.Steps[0].Undo = UndoMode.None;
        var p2 = ScenePlans.Build(none);
        Assert.False(p2.Steps[0].RestoreRecorded);
        Assert.Null(p2.Steps[0].FixedUndo);
    }

    [Fact]
    public void The_fingerprint_covers_the_programs_and_every_part_of_every_step_but_not_the_name()
    {
        string Fp(Action<SceneSettings> edit) { var s = Scene(); edit(s); return ScenePlans.Fingerprint(s); }
        string baseline = Fp(_ => { });
        Assert.Equal(baseline, Fp(_ => { }));
        Assert.Equal(baseline, Fp(s => s.Name = "Another name"));
        Assert.Equal(baseline, Fp(s => s.Enabled = false));
        Assert.Equal(baseline, Fp(s => s.Games[0] = "GAME.EXE"));                       // the match ignores case, so does the fingerprint
        Assert.NotEqual(baseline, Fp(s => s.Games.Add("other.exe")));
        Assert.NotEqual(baseline, Fp(s => s.Games[0] = "other.exe"));
        Assert.NotEqual(baseline, Fp(s => s.Steps[0].Args[0] = "6"));
        Assert.NotEqual(baseline, Fp(s => s.Steps[0].Verb = "mark"));
        Assert.NotEqual(baseline, Fp(s => s.Steps[0].Module = "other"));
        Assert.NotEqual(baseline, Fp(s => s.Steps[0].Undo = UndoMode.None));
        Assert.NotEqual(baseline, Fp(s => s.Steps[1].UndoVerb = "stop"));
        Assert.NotEqual(baseline, Fp(s => s.Steps[1].UndoArgs.Add("x")));
        Assert.NotEqual(baseline, Fp(s => s.Steps.Reverse()));
        Assert.NotEqual(baseline, Fp(s => s.Steps.RemoveAt(1)));
        Assert.NotEqual(baseline, Fp(s => s.Id = "scene-b"));
    }

    [Theory]
    [InlineData("steam", "on", "", "steam off")]
    [InlineData("steam", "off", "", "steam on")]
    [InlineData("recorder", "start", "", "recorder stop")]
    [InlineData("tunnel", "on", "home", "tunnel off home")]
    [InlineData("x", "enable", "", "x disable")]
    public void The_usual_opposite_is_suggested_for_a_known_pair(string module, string verb, string args, string expected) =>
        Assert.Equal(expected, UndoGuess.For(module, verb, args.Length == 0 ? Array.Empty<string>() : args.Split(' ')));

    [Fact]
    public void Nothing_is_suggested_for_a_verb_that_has_no_known_opposite() => Assert.Null(UndoGuess.For("xtu", "apply", ["Gaming"]));

    [Fact]
    public void A_scene_survives_the_settings_file()
    {
        var s = Scene();
        s.LastAt = new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);
        var json = System.Text.Json.JsonSerializer.Serialize(new ScenesSettings { Scenes = new() { s } });
        var back = System.Text.Json.JsonSerializer.Deserialize<ScenesSettings>(json)!;
        Assert.Equal(ScenePlans.Fingerprint(s), ScenePlans.Fingerprint(back.Scenes[0]));
        Assert.Equal(s.LastAt, back.Scenes[0].LastAt);
    }
}
