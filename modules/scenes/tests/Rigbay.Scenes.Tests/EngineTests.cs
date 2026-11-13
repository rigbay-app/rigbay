using Rigbay.Abstractions;

namespace Rigbay.Scenes.Tests;

/// <summary>The trigger and the runs: start and stop of a game, the grace, two scenes, a Rigbay that restarted, the host's refusals, an edit.</summary>
public class EngineTests
{
    private static async Task<Rig> Armed(Action<Rig>? configure = null, params SceneSettings[] scenes)
    {
        var rig = new Rig();
        if (scenes.Length == 0) scenes = new[] { rig.Approved(Rig.Scene()) };
        else foreach (var s in scenes) rig.Approved(s);
        rig.Seed(scenes);
        configure?.Invoke(rig);
        return await rig.StartAsync();
    }

    // ------------------------------------------------------------------ the poll

    [Fact]
    public async Task With_no_scene_nothing_is_read_and_no_timer_runs()
    {
        var rig = new Rig();
        await rig.StartAsync();
        Assert.Equal(0, rig.Ticker.Created);
        Assert.False(rig.Runtime.Polling);
        await rig.Tick();
        Assert.Equal(0, rig.Procs.FindCalls);
    }

    [Fact]
    public async Task A_scene_that_is_off_or_not_approved_does_not_start_the_poll()
    {
        var rig = new Rig();
        var off = Rig.Scene("scene-off", "Off", "a.exe", enabled: false);
        rig.Approved(off);
        var unapproved = Rig.Scene("scene-new", "New", "b.exe");   // switched on in the file but never approved
        rig.Seed(off, unapproved);
        await rig.StartAsync();
        Assert.Equal(0, rig.Ticker.Created);
        Assert.False(rig.Runtime.IsArmed(rig.Stored("scene-new")));
        rig.Procs.Start("a.exe"); rig.Procs.Start("b.exe");
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));
    }

    [Fact]
    public async Task An_approved_scene_that_is_on_polls_every_5_seconds_and_only_then()
    {
        var rig = await Armed();
        Assert.Equal(1, rig.Ticker.Running);
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Ticker.Period);
        Assert.True(rig.Runtime.Polling);
    }

    [Fact]
    public async Task The_poll_looks_only_for_the_programs_of_scenes_that_are_on()
    {
        var rig = await Armed(null, Rig.Scene("scene-a", "A", "a.exe"), Rig.Scene("scene-b", "B", "b.exe", enabled: false));
        rig.Procs.Start("b.exe");
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));
        Assert.Equal(1, rig.Procs.FindCalls);
    }

    [Fact]
    public async Task A_failing_process_list_is_no_game_and_not_an_error()
    {
        var rig = await Armed();
        rig.Procs.Throw = true;
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));
        Assert.Contains(rig.Host.LogSink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("process list"));
    }

    // ------------------------------------------------------------------ start and stop of a game

    [Fact]
    public async Task When_the_game_starts_the_plan_runs_once()
    {
        var rig = await Armed();
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));
        rig.Procs.Start("Game.EXE");
        await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));
        Assert.Equal("scene-a", rig.Verbs.Started[0].Id);
        Assert.Equal("run1", rig.State().RunId);
        Assert.Equal("game.exe", rig.State().RunGame);
        await rig.Tick(5);
        await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));
        Assert.Contains("Running since", rig.Words(rig.Page().Scene("Gaming").Line!.Value));
    }

    [Fact]
    public async Task The_plan_that_is_started_is_exactly_the_plan_that_was_approved()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        Assert.Equal(FakeVerbs.HashOf(ScenePlans.Build(rig.Stored())), FakeVerbs.HashOf(rig.Verbs.Started.Single()));
        Assert.Equal(1, rig.Verbs.OpenCount);
    }

    [Fact]
    public async Task The_scene_is_put_back_after_the_game_has_been_gone_for_10_seconds()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Procs.StopAll();
        await rig.Tick(5);      // first look without the game
        Assert.Equal(0, rig.Verbs.Count("end"));
        await rig.Tick(4);      // 4 s gone
        Assert.Equal(0, rig.Verbs.Count("end"));
        await rig.Tick(1);      // 5 s
        Assert.Equal(0, rig.Verbs.Count("end"));
        await rig.Tick(5);      // 10 s
        Assert.Equal(1, rig.Verbs.Count("end"));
        Assert.Null(rig.State().RunId);
        Assert.Equal(0, rig.Verbs.OpenCount);
        Assert.Equal("ran", rig.Stored().LastKind);
        Assert.Contains("ran for 15 s and was put back", rig.Page().Scene("Gaming").Row<ValueRow>("row.last.header").Value.Value);
    }

    [Fact]
    public async Task A_launcher_that_restarts_the_game_inside_the_grace_does_not_flap_the_machine()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Procs.StopAll();
        await rig.Tick(5);
        await rig.Tick(4);
        rig.Procs.Start("game.exe");
        await rig.Tick(1);
        Assert.Equal(0, rig.Verbs.Count("end"));
        Assert.Equal(1, rig.Verbs.Count("start"));
        // and the grace starts again from the next time the game is gone
        rig.Procs.StopAll();
        await rig.Tick(5);
        await rig.Tick(5);
        Assert.Equal(0, rig.Verbs.Count("end"));
        await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("end"));
    }

    [Fact]
    public async Task After_the_end_the_scene_waits_for_the_next_start_of_the_game()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Procs.StopAll();
        await rig.Tick(5);
        await rig.Tick(10);
        Assert.Equal(1, rig.Verbs.Count("end"));
        Assert.True(rig.Runtime.Polling);        // still on: the poll goes on, waiting for a game
        rig.Procs.Start("game.exe");
        await rig.Tick(5);
        Assert.Equal(2, rig.Verbs.Count("start"));
        Assert.NotNull(rig.State().RunId);
    }

    [Fact]
    public async Task A_game_that_already_runs_when_the_module_starts_runs_the_scene()
    {
        var rig = new Rig();
        rig.Seed(rig.Approved(Rig.Scene()));
        rig.Procs.Start("game.exe");
        await rig.StartAsync();
        await rig.Tick();
        Assert.Equal(1, rig.Verbs.Count("start"));
    }

    [Fact]
    public async Task One_of_several_programs_is_enough_and_the_scene_ends_after_the_last()
    {
        var scene = Rig.Scene();
        scene.Games = new List<string> { "game.exe", "launcher.exe" };
        var rig = await Armed(null, scene);
        rig.Procs.Start("launcher.exe");
        await rig.Tick();
        Assert.Equal(1, rig.Verbs.Count("start"));
        rig.Procs.Start("game.exe");
        rig.Procs.Stop("launcher.exe");
        await rig.Tick(5);
        await rig.Tick(30);                      // the game still runs: nothing ends
        Assert.Equal(0, rig.Verbs.Count("end"));
        rig.Procs.Stop("game.exe");
        await rig.Tick(5);
        await rig.Tick(10);
        Assert.Equal(1, rig.Verbs.Count("end"));
    }

    [Fact]
    public async Task A_full_path_matches_only_that_file()
    {
        var scene = Rig.Scene(game: "D:\\Games\\game.exe");
        var rig = await Armed(null, scene);
        rig.Procs.Start("game.exe", "E:\\Copy\\game.exe");
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));
        rig.Procs.StopAll();
        rig.Procs.Start("game.exe", "d:\\games\\game.exe");
        await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));
    }

    // ------------------------------------------------------------------ two scenes

    private static SceneSettings Second() => Rig.Scene("scene-b", "Streaming", "obs.exe");

    [Fact]
    public async Task Scenes_start_in_the_order_of_the_page_and_end_with_the_last_started_first()
    {
        var rig = await Armed(null, Rig.Scene(), Second());
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Procs.Start("obs.exe");
        await rig.Tick(5);
        Assert.Equal("start scene-a | start scene-b", string.Join(" | ", rig.Verbs.Calls.Where(c => c.StartsWith("start"))));
        rig.Procs.StopAll();
        await rig.Tick(5);
        await rig.Tick(10);
        // the newest ends first
        var ends = rig.Verbs.Ended;
        Assert.Equal(new[] { "run2", "run1" }, ends);
    }

    [Fact]
    public async Task An_older_scene_whose_game_ended_first_waits_until_the_newer_one_is_put_back()
    {
        var rig = await Armed(null, Rig.Scene(), Second());
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Procs.Start("obs.exe");
        await rig.Tick(5);
        rig.Procs.Stop("game.exe");              // the older game ends, the newer one runs on
        await rig.Tick(5);
        await rig.Tick(10);
        await rig.Tick(10);
        Assert.Equal(0, rig.Verbs.Count("end"));
        Assert.NotNull(rig.State("scene-a").RunId);
        Assert.Contains("Waiting for Streaming", rig.Words(rig.Page().Scene("Gaming").Line!.Value));
        rig.Procs.Stop("obs.exe");
        await rig.Tick(5);
        await rig.Tick(10);
        Assert.Equal(new[] { "run2", "run1" }, rig.Verbs.Ended);   // reverse order of starting, in one pass
        Assert.Equal(0, rig.Verbs.OpenCount);
    }

    [Fact]
    public async Task A_failed_undo_does_not_hold_up_the_scenes_under_it()
    {
        var rig = await Armed(null, Rig.Scene(), Second());
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Procs.Start("obs.exe");
        await rig.Tick(5);
        rig.Procs.StopAll();
        await rig.Tick(5);
        rig.Verbs.ForceEndFor = id => id == "run2" ? PlanOutcome.UndoFailed : null;
        await rig.Tick(10);                      // run2 (newest) fails to come back
        Assert.True(rig.State("scene-b").EndFailed);
        Assert.Null(rig.State("scene-a").RunId);  // the older one is not stuck behind it: it was put back in the same pass
        Assert.Equal(new[] { "run2", "run1" }, rig.Verbs.Ended);
        await rig.Tick(30);
        Assert.Equal(new[] { "run2", "run1" }, rig.Verbs.Ended);   // and the failed one is not retried by itself
    }

    // ------------------------------------------------------------------ a Rigbay that restarted

    [Fact]
    public async Task A_run_left_by_the_last_rigbay_is_adopted_while_the_game_still_runs()
    {
        var rig = new Rig();
        rig.Seed(rig.Approved(Rig.Scene()));
        string run = rig.Verbs.LeaveRun("scene-a", rig.Now - TimeSpan.FromMinutes(40), steps: "demo-target set 5");
        rig.Procs.Start("game.exe");
        await rig.StartAsync();
        Assert.Equal(run, rig.State().RunId);
        Assert.Equal(rig.Now - TimeSpan.FromMinutes(40), rig.State().RunStarted);
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));          // not started a second time
        Assert.Equal(0, rig.Verbs.Count("end"));
        rig.Procs.StopAll();
        await rig.Tick(5);
        await rig.Tick(10);
        Assert.Equal(new[] { run }, rig.Verbs.Ended);       // and it is ended when the game ends
    }

    [Fact]
    public async Task A_run_left_by_the_last_rigbay_is_ended_when_the_game_is_gone()
    {
        var rig = new Rig();
        rig.Seed(rig.Approved(Rig.Scene()));
        string run = rig.Verbs.LeaveRun("scene-a", rig.Now - TimeSpan.FromMinutes(40));
        await rig.StartAsync();
        Assert.Equal(new[] { run }, rig.Verbs.Ended);
        Assert.Equal(0, rig.Verbs.OpenCount);
        Assert.Null(rig.State().RunId);
    }

    [Fact]
    public async Task The_module_waits_for_the_modules_a_left_run_needs_before_it_ends_it()
    {
        var rig = new Rig();
        rig.Seed(rig.Approved(Rig.Scene()));
        rig.Verbs.Infos = new();                              // no module is running yet
        rig.Verbs.LeaveRun("scene-a", rig.Now - TimeSpan.FromMinutes(5), steps: "demo-target set 5");
        await rig.StartAsync();
        Assert.NotEmpty(rig.Delays);                          // it waited (the fake clock jumped 30 s) and then went on
        Assert.True(rig.Delays.Count <= 16);
        Assert.Single(rig.Verbs.Ended);
    }

    [Fact]
    public async Task A_left_run_whose_scene_is_gone_is_ended()
    {
        var rig = new Rig();
        rig.Seed();
        string run = rig.Verbs.LeaveRun("scene-deleted", rig.Now - TimeSpan.FromMinutes(5));
        await rig.StartAsync();
        Assert.Equal(new[] { run }, rig.Verbs.Ended);
    }

    [Fact]
    public async Task A_left_run_whose_undo_failed_before_is_shown_and_not_retried_by_itself()
    {
        var rig = new Rig();
        rig.Seed(rig.Approved(Rig.Scene()));
        string run = rig.Verbs.LeaveRun("scene-a", rig.Now - TimeSpan.FromMinutes(5), undoFailed: true);
        await rig.StartAsync();
        Assert.Equal(run, rig.State().RunId);
        Assert.True(rig.State().EndFailed);
        await rig.Tick(5); await rig.Tick(30);
        Assert.Empty(rig.Verbs.Ended);
        Assert.Contains("Could not put everything back", rig.Words(rig.Page().Scene("Gaming").Line!.Value));
        Assert.Contains("rigbay plans undo " + run, rig.Words(rig.Page().Scene("Gaming").Row<InfoRow>("info.attention.header").Line!.Value));
    }

    // ------------------------------------------------------------------ what the host answers

    [Fact]
    public async Task A_rate_limited_scene_is_shown_and_not_retried_until_the_game_has_ended_and_started_again()
    {
        var rig = await Armed(r => r.Verbs.ForceStart = _ => new PlanRun("", PlanOutcome.RateLimited, [], "6 starts of this plan in the last hour"));
        rig.Procs.Start("game.exe");
        await rig.Tick();
        for (int i = 0; i < 5; i++) await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));                     // asked once, never in a loop
        Assert.Equal(BlockKind.RateLimited, rig.State().Block);
        Assert.Contains("too often", rig.Words(rig.Page().Scene("Gaming").Line!.Value) + rig.Words(rig.Page().Scene("Gaming").Row<InfoRow>("info.attention.header").Line!.Value));
        Assert.Equal("refused", rig.Stored().LastKind);
        Assert.Empty(rig.Host.ToastService.All());                     // the host already announced it

        rig.Procs.StopAll();
        await rig.Tick(5);                                              // the game is gone: the block is lifted
        Assert.Equal(BlockKind.None, rig.State().Block);
        rig.Verbs.ForceStart = null;
        rig.Procs.Start("game.exe");
        await rig.Tick(5);
        Assert.Equal(2, rig.Verbs.Count("start"));
        Assert.NotNull(rig.State().RunId);
    }

    [Fact]
    public async Task A_plan_the_host_says_changed_is_shown_and_not_announced_twice()
    {
        var rig = await Armed(r => r.Verbs.ForceStart = _ => new PlanRun("", PlanOutcome.NeedsApproval, [], "not approved (Changed) Steam access was updated from 1 to 2"));
        rig.Procs.Start("game.exe");
        await rig.Tick();
        await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));
        Assert.Equal(BlockKind.Changed, rig.State().Block);
        Assert.Empty(rig.Host.ToastService.All());                     // the host raised its own warning for a changed plan
    }

    [Fact]
    public async Task An_approval_that_was_revoked_elsewhere_is_announced_by_the_module_once()
    {
        var rig = await Armed();
        rig.Verbs.Revoke("scene-a");                                    // `rigbay plans revoke` while the module still believes it
        rig.Verbs.Calls.Clear();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        await rig.Tick(5);
        await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));
        var toasts = rig.Host.ToastService.All();
        Assert.Single(toasts);
        Assert.Equal(ToastKind.Warning, toasts[0].Kind);
        Assert.False(rig.Runtime.IsArmed(rig.Stored()));               // the module asked the host again and now knows
        Assert.False(rig.Runtime.Polling);                              // so the poll stops too
    }

    [Theory]
    [InlineData(PlanOutcome.TooManyRuns, true)]
    [InlineData(PlanOutcome.Invalid, true)]
    [InlineData(PlanOutcome.Failed, false)]
    public async Task The_module_announces_only_what_the_host_does_not(PlanOutcome outcome, bool moduleToasts)
    {
        var rig = await Armed(r => r.Verbs.ForceStart = _ => new PlanRun("", outcome, [], "x"));
        rig.Procs.Start("game.exe");
        await rig.Tick();
        await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));
        Assert.Equal(moduleToasts ? 1 : 0, rig.Host.ToastService.All().Length);
    }

    [Fact]
    public async Task A_step_that_fails_is_recorded_as_failed_and_the_scene_is_not_retried()
    {
        var rig = await Armed(r => r.Verbs.FailSteps.Add(1));
        rig.Procs.Start("game.exe");
        await rig.Tick();
        await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));
        Assert.Equal("failed", rig.Stored().LastKind);
        Assert.Null(rig.State().RunId);
        Assert.Equal(BlockKind.Failed, rig.State().Block);
    }

    [Fact]
    public async Task A_start_whose_rollback_failed_leaves_an_open_run_for_the_owner()
    {
        var rig = await Armed(r => r.Verbs.ForceStart = _ => new PlanRun("run9", PlanOutcome.UndoFailed, [], "step 2 failed"));
        rig.Procs.Start("game.exe");
        await rig.Tick();
        Assert.Equal("run9", rig.State().RunId);
        Assert.True(rig.State().EndFailed);
        Assert.Equal("undo-failed", rig.Stored().LastKind);
        await rig.Tick(5);
        await rig.Tick(30);
        Assert.Empty(rig.Verbs.Ended);
    }

    [Fact]
    public async Task A_start_that_finds_an_open_run_at_the_host_adopts_it()
    {
        var rig = await Armed();
        string run = rig.Verbs.LeaveRun("scene-a", rig.Now - TimeSpan.FromMinutes(1));   // the module did not know about it
        rig.Procs.Start("game.exe");
        await rig.Tick();
        Assert.Equal(run, rig.State().RunId);
        Assert.Equal(1, rig.Verbs.Count("start"));                                          // asked once, answered AlreadyRunning, then adopted
    }

    [Fact]
    public async Task A_failed_undo_is_shown_and_retried_only_when_the_owner_presses_stop()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Verbs.ForceEnd = PlanOutcome.UndoFailed;
        rig.Procs.StopAll();
        await rig.Tick(5);
        await rig.Tick(10);
        Assert.Equal(1, rig.Verbs.Count("end"));
        Assert.True(rig.State().EndFailed);
        Assert.Equal("undo-failed", rig.Stored().LastKind);
        await rig.Tick(5); await rig.Tick(30);
        Assert.Equal(1, rig.Verbs.Count("end"));                 // no loop
        rig.Verbs.ForceEnd = null;

        var stop = (ButtonRow)rig.Page().Scene("Gaming").Children.Single(r => r.HeaderKey == "row.stop.header");
        var result = await stop.InvokeAsync(CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(2, rig.Verbs.Count("end"));
        Assert.Null(rig.State().RunId);
    }

    // ------------------------------------------------------------------ an edit means a new approval

    [Fact]
    public async Task Adding_a_step_makes_the_scene_ask_again_and_it_stops_running_by_itself()
    {
        var rig = await Armed();
        var page = rig.Page();
        var scene = page.Scene("Gaming");
        Assert.True(rig.Runtime.IsArmed(rig.Stored()));

        // pick a command and add it
        var view = rig.Runtime.ViewOf("scene-a");
        view.AddModule.Value = "recorder";
        view.AddVerb.Value = "start";
        var added = await rig.Page().Scene("Gaming").Row<ButtonRow>("row.add-step.header").InvokeAsync(CancellationToken.None);
        Assert.True(added.Succeeded);
        Assert.Equal(3, rig.Stored().Steps.Count);

        Assert.False(rig.Runtime.IsArmed(rig.Stored()));
        Assert.False(rig.Runtime.Polling);                                          // nothing is armed: the poll stops
        rig.Procs.Start("game.exe");
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));
        Assert.Contains("Needs your approval", rig.Words(rig.Page().Scene("Gaming").Line!.Value) + rig.Words(rig.Page().Scene("Gaming").Line!.Value));
    }

    [Fact]
    public async Task Approving_the_edited_scene_arms_it_again()
    {
        var rig = await Armed();
        var view = rig.Runtime.ViewOf("scene-a");
        view.AddModule.Value = "steam"; view.AddVerb.Value = "off";
        await rig.Page().Scene("Gaming").Row<ButtonRow>("row.add-step.header").InvokeAsync(CancellationToken.None);
        Assert.False(rig.Runtime.IsArmed(rig.Stored()));

        var approve = rig.Page().Scene("Gaming").Row<ButtonRow>("row.approve.header");
        Assert.True(approve.Enabled.Value);
        var answer = await approve.InvokeAsync(CancellationToken.None);
        Assert.True(answer.Succeeded);
        Assert.Equal(1, rig.Verbs.Count("ask"));
        Assert.Equal(3, rig.Verbs.Asked.Single().Steps.Count);                      // the owner is asked about the scene as it is now
        Assert.True(rig.Runtime.IsArmed(rig.Stored()));
        Assert.True(rig.Runtime.Polling);
        rig.Procs.Start("game.exe");
        await rig.Tick();
        Assert.Equal(1, rig.Verbs.Count("start"));
    }

    [Fact]
    public async Task Changing_the_programs_asks_again_though_the_hosts_hash_is_the_same()
    {
        var rig = await Armed();
        var games = rig.Page().Scene("Gaming").Row<TextRow>("row.games.header");
        games.Value.Value = "game.exe, other.exe";
        Assert.Equal(2, rig.Stored().Games.Count);
        Assert.Equal(PlanApprovalState.Approved, rig.Verbs.GetApproval(ScenePlans.Build(rig.Stored())).State);   // the host would still run it ...
        Assert.False(rig.Runtime.IsArmed(rig.Stored()));                                                           // ... the module does not start it
        rig.Procs.Start("other.exe");
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));
    }

    [Fact]
    public async Task Renaming_a_scene_does_not_ask_again()
    {
        var rig = await Armed();
        rig.Page().Scene("Gaming").Row<TextRow>("row.name.header").Value.Value = "Racing";
        Assert.Equal("Racing", rig.Stored().Name);
        Assert.True(rig.Runtime.IsArmed(rig.Stored()));
    }

    [Fact]
    public async Task An_edit_in_the_settings_file_is_not_trusted()
    {
        var rig = new Rig();
        var scene = rig.Approved(Rig.Scene());
        scene.Steps[0].Args[0] = "100";                            // someone edited the file after the approval
        rig.Seed(scene);
        await rig.StartAsync();
        Assert.False(rig.Runtime.IsArmed(rig.Stored()));
        Assert.False(rig.Runtime.Polling);
        rig.Procs.Start("game.exe");
        await rig.Tick();
        Assert.Equal(0, rig.Verbs.Count("start"));
    }

    [Fact]
    public async Task The_hosts_changed_answer_is_shown_with_its_reason()
    {
        var rig = new Rig();
        var scene = rig.Approved(Rig.Scene());
        rig.Seed(scene);
        rig.Verbs.ForceApproval = new PlanApproval(PlanApprovalState.Changed, "Steam access was updated from 1.0.0 to 2.0.0");
        await rig.StartAsync();
        Assert.False(rig.Runtime.Polling);
        Assert.Contains("Steam access was updated from 1.0.0 to 2.0.0", rig.Words(rig.Page().Scene("Gaming").Line!.Value));
    }

    [Fact]
    public async Task A_module_that_starts_or_stops_makes_the_module_ask_the_host_about_the_plans_again()
    {
        var rig = new Rig();
        var scene = rig.Approved(Rig.Scene());
        rig.Seed(scene);
        rig.Verbs.ForceApproval = new PlanApproval(PlanApprovalState.Unavailable, "module 'steam' is not running");
        await rig.StartAsync();
        Assert.False(rig.Runtime.Polling);
        Assert.Contains("Cannot start yet", rig.Words(rig.Page().Scene("Gaming").Line!.Value));
        rig.Verbs.ForceApproval = null;                                 // steam started
        rig.Verbs.RaiseChanged();
        await rig.Module.WhenIdleAsync();
        Assert.True(rig.Runtime.Polling);
    }

    // ------------------------------------------------------------------ the owner's buttons

    [Fact]
    public async Task Switching_a_scene_on_asks_the_hosts_one_question_and_switches_it_on_when_the_answer_is_yes()
    {
        var rig = new Rig();
        rig.Seed(Rig.Scene(enabled: false));
        await rig.StartAsync();
        var toggle = (ToggleRow)rig.Page().Scene("Gaming").HeaderRow!;
        toggle.Value.Value = true;
        await rig.Module.WhenIdleAsync();
        Assert.Equal(1, rig.Verbs.Count("ask"));
        Assert.Equal(0, rig.Host.ApprovalService.Asked);                 // never the single-question approval
        Assert.True(rig.Stored().Enabled);
        Assert.True(rig.Runtime.Polling);
        Assert.True(toggle.Value.Value);
    }

    [Fact]
    public async Task A_no_to_the_question_leaves_the_scene_off_and_says_so()
    {
        var rig = new Rig();
        rig.Seed(Rig.Scene(enabled: false));
        rig.Verbs.Answer = _ => PlanApprovalState.Declined;
        await rig.StartAsync();
        var scene = rig.Page().Scene("Gaming");
        Assert.Throws<InvalidOperationException>(() => scene.Row<InfoRow>("info.attention.header"));   // nothing to say yet: no row
        ((ToggleRow)scene.HeaderRow!).Value.Value = true;
        await rig.Module.WhenIdleAsync();
        Assert.False(rig.Stored().Enabled);
        Assert.False(((ToggleRow)scene.HeaderRow!).Value.Value);
        Assert.False(rig.Runtime.Polling);
        Assert.Contains("did not approve", rig.Words(rig.Page().Scene("Gaming").Row<InfoRow>("info.attention.header").Line!.Value));
    }

    [Fact]
    public async Task Switching_on_a_scene_that_is_approved_already_asks_nothing()
    {
        var rig = new Rig();
        var scene = rig.Approved(Rig.Scene(enabled: false));
        rig.Seed(scene);
        await rig.StartAsync();
        ((ToggleRow)rig.Page().Scene("Gaming").HeaderRow!).Value.Value = true;
        await rig.Module.WhenIdleAsync();
        Assert.Equal(0, rig.Verbs.Count("ask"));
        Assert.True(rig.Stored().Enabled);
        Assert.True(rig.Runtime.Polling);
    }

    [Fact]
    public async Task An_edit_while_the_question_is_open_leaves_the_scene_needing_approval()
    {
        var rig = new Rig();
        rig.Seed(Rig.Scene(enabled: false));
        await rig.StartAsync();
        rig.Verbs.OnAsk = () => rig.Page().Scene("Gaming").Row<TextRow>("row.games.header").Value.Value = "game.exe, sneaky.exe";
        ((ToggleRow)rig.Page().Scene("Gaming").HeaderRow!).Value.Value = true;
        await rig.Module.WhenIdleAsync();
        Assert.False(rig.Runtime.IsArmed(rig.Stored()));
    }

    [Fact]
    public async Task Revoke_withdraws_the_approval_and_switches_the_scene_off()
    {
        var rig = await Armed();
        var revoke = rig.Page().Scene("Gaming").Row<ButtonRow>("row.revoke.header");
        await revoke.InvokeAsync(CancellationToken.None);
        Assert.Equal(new[] { "scene-a" }, rig.Verbs.Revoked);
        Assert.False(rig.Stored().Enabled);
        Assert.Equal("", rig.Stored().ApprovedFingerprint);
        Assert.False(rig.Runtime.Polling);
    }

    [Fact]
    public async Task Run_now_runs_the_steps_without_the_game_and_only_stop_puts_them_back()
    {
        var rig = await Armed();
        var scene = rig.Page().Scene("Gaming");
        var run = scene.Row<ButtonRow>("row.run.header");
        Assert.True(run.Enabled.Value);
        Assert.True((await run.InvokeAsync(CancellationToken.None)).Succeeded);
        Assert.Equal(1, rig.Verbs.Count("start"));
        Assert.True(rig.State().Manual);
        for (int i = 0; i < 4; i++) await rig.Tick(30);                       // no game, a long time: a test run is not ended by the poll
        Assert.Equal(0, rig.Verbs.Count("end"));
        Assert.False(run.Enabled.Value);
        var stop = rig.Page().Scene("Gaming").Row<ButtonRow>("row.stop.header");
        Assert.True(stop.Enabled.Value);
        Assert.True((await stop.InvokeAsync(CancellationToken.None)).Succeeded);
        Assert.Equal(1, rig.Verbs.Count("end"));
        Assert.Null(rig.State().RunId);
    }

    [Fact]
    public async Task Run_now_is_not_offered_for_a_scene_that_is_not_approved()
    {
        var rig = new Rig();
        rig.Seed(Rig.Scene());
        await rig.StartAsync();
        Assert.False(rig.Page().Scene("Gaming").Row<ButtonRow>("row.run.header").Enabled.Value);
        Assert.False(rig.Page().Scene("Gaming").Row<ButtonRow>("row.stop.header").Enabled.Value);
    }

    [Fact]
    public async Task Stop_while_the_game_runs_keeps_the_scene_off_until_the_game_has_ended_and_started_again()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        await rig.Page().Scene("Gaming").Row<ButtonRow>("row.stop.header").InvokeAsync(CancellationToken.None);
        Assert.Equal(1, rig.Verbs.Count("end"));
        await rig.Tick(5); await rig.Tick(5);
        Assert.Equal(1, rig.Verbs.Count("start"));                            // not started again behind the owner's back
        rig.Procs.StopAll();
        await rig.Tick(5);
        rig.Procs.Start("game.exe");
        await rig.Tick(5);
        Assert.Equal(2, rig.Verbs.Count("start"));
    }

    [Fact]
    public async Task Switching_off_a_scene_that_runs_puts_it_back()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        ((ToggleRow)rig.Page().Scene("Gaming").HeaderRow!).Value.Value = false;
        await rig.Module.WhenIdleAsync();
        Assert.Equal(1, rig.Verbs.Count("end"));
        Assert.False(rig.Stored().Enabled);
        Assert.False(rig.Runtime.Polling);
    }

    [Fact]
    public async Task Deleting_a_scene_puts_it_back_first_and_revokes_the_approval()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        var delete = rig.Page().Scene("Gaming").Row<ButtonRow>("row.delete.header");
        Assert.Equal("confirm.delete", delete.ConfirmKey);
        Assert.True((await delete.InvokeAsync(CancellationToken.None)).Succeeded);
        Assert.Equal(1, rig.Verbs.Count("end"));
        Assert.Equal(new[] { "scene-a" }, rig.Verbs.Revoked);
        Assert.Empty(rig.Host.SettingsStore.Current.Scenes);
        Assert.False(rig.Runtime.Polling);
    }

    [Fact]
    public async Task A_scene_that_cannot_be_put_back_is_not_deleted()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Verbs.ForceEnd = PlanOutcome.UndoFailed;
        var result = await rig.Page().Scene("Gaming").Row<ButtonRow>("row.delete.header").InvokeAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Single(rig.Host.SettingsStore.Current.Scenes);
        Assert.Empty(rig.Verbs.Revoked);
    }

    // ------------------------------------------------------------------ shutdown

    [Fact]
    public async Task Turning_the_module_off_puts_back_what_a_scene_changed_and_stops_the_poll()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Module.Shutdown();
        Assert.Equal(1, rig.Verbs.Count("end"));
        Assert.Equal(0, rig.Verbs.OpenCount);
        Assert.Equal(1, rig.Ticker.Disposed);
        Assert.Null(rig.Module.Runtime);
    }

    [Fact]
    public async Task A_shutdown_that_cannot_put_back_leaves_the_run_in_the_hosts_journal()
    {
        var rig = await Armed();
        rig.Procs.Start("game.exe");
        await rig.Tick();
        rig.Verbs.ForceEnd = PlanOutcome.UndoFailed;
        rig.Module.Shutdown();
        Assert.Equal(1, rig.Verbs.OpenCount);        // `rigbay plans undo` and the next start of this module can still settle it
    }

    [Fact]
    public async Task Shutdown_with_nothing_running_asks_the_host_nothing()
    {
        var rig = await Armed();
        rig.Verbs.Calls.Clear();
        rig.Module.Shutdown();
        Assert.Equal(0, rig.Verbs.Count("end"));
    }

    // ------------------------------------------------------------------ hand-edited settings

    [Fact]
    public async Task Ids_that_are_not_valid_or_not_unique_are_repaired_and_the_scene_has_to_be_approved_again()
    {
        var rig = new Rig();
        var a = rig.Approved(Rig.Scene("scene-a", "A", "a.exe"));
        var b = rig.Approved(Rig.Scene("scene-a", "B", "b.exe"));    // same id twice
        var c = Rig.Scene("Not Valid", "C", "c.exe");
        rig.Seed(a, b, c);
        await rig.StartAsync();
        rig.Runtime.SceneSnapshot(out var scenes);
        Assert.Equal(3, scenes.Select(s => s.Id).Distinct().Count());
        Assert.All(scenes, s => Assert.True(SceneRules.IsId(s.Id)));
        Assert.True(rig.Runtime.IsArmed(scenes.Single(s => s.Name == "A")));
        Assert.False(rig.Runtime.IsArmed(scenes.Single(s => s.Name == "B")));
        Assert.False(rig.Runtime.IsArmed(scenes.Single(s => s.Name == "C")));
    }

    [Fact]
    public async Task A_settings_file_with_nulls_loads()
    {
        var rig = new Rig();
        rig.Host.SettingsStore.Json = "{\"Scenes\":[{\"Id\":\"scene-a\",\"Name\":null,\"Games\":null,\"Steps\":[{\"Module\":null,\"Args\":null}]}]}";
        await rig.StartAsync();
        Assert.False(rig.Runtime.Polling);
        Assert.NotNull(rig.Page());
    }
}
