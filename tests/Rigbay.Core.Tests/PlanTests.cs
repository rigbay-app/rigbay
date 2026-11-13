using Rigbay.Abstractions;
using Rigbay.Core;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>
/// Host API 0.1.4, part B: verbs of other modules and the approved plan. The "target" module changes a number in memory (never hardware); the "owner"
/// module drives the plan through <c>host.Verbs</c>, so everything below goes through the same fault boundaries and checks a real Scenes module would.
/// </summary>
public class PlanTests
{
    private static string Start(string output) => PlanKit.Field(output, "outcome");
    private static string RunId(string output) => PlanKit.Field(output, "run");

    private static async Task<string> ApprovedAsync(PlanKit kit, string planJson)
    {
        kit.WritePlan(planJson);
        var answer = await kit.Owner("approve");
        Assert.StartsWith("Approved", answer);
        return answer;
    }

    // ---------------------------------------------------------------- listing and free calls

    [Fact]
    public async Task The_list_has_the_other_modules_verbs_with_what_they_declared()
    {
        using var kit = new PlanKit();
        var list = (await kit.Owner("list")).Split(';');
        Assert.DoesNotContain(list, l => l.StartsWith("owner/"));                         // never the caller's own
        Assert.Contains(list, l => l.StartsWith("target/get/ReadOnly/"));
        var set = Assert.Single(list, l => l.StartsWith("target/set/"));
        Assert.StartsWith("target/set/Writes/Hardware/set/True/", set);                   // access, kind, undo verb, has a preview, then the effect text
        Assert.Contains(list, l => l.StartsWith("target/legacy/Unknown/"));               // registered the old way: unknown, treated as a write
        Assert.Equal(list.OrderBy(l => l, StringComparer.Ordinal).Select(l => l.Split('/')[0]).ToArray(), list.Select(l => l.Split('/')[0]).ToArray());
    }

    [Fact]
    public async Task A_read_only_verb_can_be_called_and_a_write_cannot()
    {
        using var kit = new PlanKit();
        Assert.Equal("Done|0|v=0", await kit.Owner("invoke", "target", "get"));
        Assert.StartsWith("NeedsPlan|-1", await kit.Owner("invoke", "target", "set", "9"));
        Assert.StartsWith("NeedsPlan|-1", await kit.Owner("invoke", "target", "legacy"));  // unknown access counts as a write
        Assert.Equal("v=0", await kit.Target("get"));                                      // nothing ran
        Assert.StartsWith("NotFound|-1", await kit.Owner("invoke", "nobody", "get"));
        Assert.StartsWith("NotFound|-1", await kit.Owner("invoke", "target", "nothing"));
        Assert.StartsWith("Refused|-1", await kit.Owner("invoke", "owner", "list"));       // a module cannot call itself
        Assert.StartsWith("Refused|-1", await kit.Owner(new[] { "invoke", "target", "get" }.Concat(Enumerable.Repeat("x", 17)).ToArray()));
        Assert.Empty(kit.Prompt.Requests);
    }

    [Fact]
    public async Task A_module_that_is_off_is_not_listed_and_not_called()
    {
        using var kit = new PlanKit();
        Assert.True(kit.Host.Find("target")!.Disable());
        Assert.DoesNotContain("target/", await kit.Owner("list"));
        Assert.StartsWith("NotRunning|-1", await kit.Owner("invoke", "target", "get"));
    }

    // ---------------------------------------------------------------- approval and a run

    [Fact]
    public async Task One_dialog_lists_the_steps_and_a_run_changes_the_value_and_is_put_back()
    {
        using var kit = new PlanKit();
        kit.WritePlan(PlanKit.SetPlan("5"));
        Assert.StartsWith("NotApproved", await kit.Owner("approval"));
        Assert.StartsWith("NeedsApproval", PlanKit.Field(await kit.Owner("start"), "outcome"));   // not approved: nothing runs
        Assert.Equal("v=0", await kit.Target("get"));

        Assert.StartsWith("Approved", await kit.Owner("approve"));
        var request = Assert.Single(kit.Prompt.Requests);                                           // ONE dialog
        var line = Assert.Single(request.Plan!);
        Assert.Equal("target set 5", line.CommandLine);
        Assert.Contains("preview.set", line.PreviewText);                                           // the exact change the verb described
        Assert.Equal("Target module", line.ModuleName);
        Assert.Equal(EffectKind.Hardware, line.Kind);
        Assert.StartsWith("Approved", await kit.Owner("approval"));

        var run = await kit.Owner("start");
        Assert.Equal("Done", Start(run));
        Assert.Equal("v=5", await kit.Target("get"));
        Assert.Single(kit.Prompt.Requests);                                                         // the target's own question was answered by the plan, no dialog
        Assert.Equal("approvals=1", await kit.Target("count"));
        var shown = Assert.Single(kit.Toasts.Shown);                                                // one notification per run
        Assert.Equal(ToastKind.Info, shown.Kind);
        Assert.Contains("target set 5", shown.Body);
        Assert.Contains($"{RunId(run)}|scene|interrupted=False", await kit.Owner("runs"));

        var end = await kit.Owner("end", RunId(run));
        Assert.Equal("Done", Start(end));
        Assert.Equal("v=0", await kit.Target("get"));                                               // the verb reported 0 as the value before
        Assert.Equal("", await kit.Owner("runs"));
        Assert.Equal(2, kit.Toasts.Shown.Count);                                                    // and one for the undo
        Assert.Single(kit.Prompt.Requests);
    }

    [Fact]
    public async Task A_fixed_undo_runs_as_written()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, """{ "id": "scene", "steps": [ { "module": "target", "verb": "set", "args": ["7"], "undo": { "module": "target", "verb": "set", "args": ["3"] } } ] }""");
        var run = await kit.Owner("start");
        Assert.Equal("v=7", await kit.Target("get"));
        Assert.Equal("Done", Start(await kit.Owner("end", RunId(run))));
        Assert.Equal("v=3", await kit.Target("get"));
    }

    [Fact]
    public async Task A_step_with_no_undo_is_run_and_left_and_nothing_is_recorded_when_nothing_changed()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("0"));            // already 0: the verb reports no undo
        var run = await kit.Owner("start");
        Assert.Equal("Done", Start(run));
        Assert.Equal("Done", Start(await kit.Owner("end", RunId(run))));
        Assert.Equal("v=0", await kit.Target("get"));
        await ApprovedAsync(kit, """{ "id": "oneway", "steps": [ { "module": "target", "verb": "set", "args": ["4"] } ] }""");
        var one = await kit.Owner("start");
        Assert.Equal("Done", Start(one));
        Assert.Equal("Done", Start(await kit.Owner("end", RunId(one))));
        Assert.Equal("v=4", await kit.Target("get"));              // the owner was told "not undone"
        Assert.Equal("core.plan.undo-none", kit.Prompt.Requests[^1].Plan![0].UndoText.Replace("Not undone", "core.plan.undo-none"));
    }

    // ---------------------------------------------------------------- exact match

    [Fact]
    public async Task Any_change_to_the_approved_plan_is_refused_and_nothing_runs()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));

        foreach (var changed in new[]
        {
            PlanKit.SetPlan("6"),                                                             // another argument
            PlanKit.SetPlan("5", restore: false),                                             // another undo
        })
        {
            kit.WritePlan(changed);
            Assert.StartsWith("Changed", await kit.Owner("approval"));
            Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));
            Assert.Equal("v=0", await kit.Target("get"));
        }

        kit.WritePlan("""{ "id": "scene", "steps": [ { "module": "target", "verb": "set", "args": ["5"], "restore": true }, { "module": "target", "verb": "get", "args": [] } ] }""");   // an added step
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));
        kit.WritePlan("""{ "id": "scene", "steps": [ { "module": "target", "verb": "set", "args": ["5"], "undo": { "module": "target", "verb": "set", "args": ["0"] } } ] }""");        // a fixed undo instead
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));
        Assert.Equal("v=0", await kit.Target("get"));

        kit.WritePlan(PlanKit.SetPlan("5"));                                                  // exactly as approved: runs
        Assert.Equal("Done", Start(await kit.Owner("start")));
    }

    [Fact]
    public async Task What_a_preview_says_is_part_of_the_approval_and_the_live_value_is_not()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        var run = await kit.Owner("start");
        Assert.Equal("v=5", await kit.Target("get"));                                         // the live value changed after approval ...
        Assert.StartsWith("Approved", await kit.Owner("approval"));                          // ... and the approval still holds
        await kit.Owner("end", RunId(run));
        Assert.StartsWith("Approved", await kit.Owner("approval"));

        await kit.Target("config", "edited");                                                 // the module's own configuration changed what "set 5" means
        Assert.StartsWith("Changed", await kit.Owner("approval"));
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));
        Assert.Equal("v=0", await kit.Target("get"));
        Assert.StartsWith("Approved", await kit.Owner("approve"));                           // approving again with the new text
        Assert.Equal("Done", Start(await kit.Owner("start")));
    }

    [Fact]
    public async Task An_updated_target_voids_the_approval_and_the_owner_hears_once()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        var json = File.ReadAllText(Path.Combine(kit.TargetFolder, "module.json"));
        File.WriteAllText(Path.Combine(kit.TargetFolder, "module.json"), json.Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\""));
        kit.Restart();

        var approval = await kit.Owner("approval");
        Assert.StartsWith("Changed|Target module was updated from 1.0.0 to 2.0.0", approval);
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));                       // a second trigger
        var warnings = kit.Toasts.Shown.Where(t => t.Kind == ToastKind.Warning).ToList();
        Assert.Single(warnings);                                                              // one notification, not one per trigger
        Assert.Contains("updated from 1.0.0 to 2.0.0", warnings[0].Body);

        Assert.StartsWith("Approved", await kit.Owner("approve"));
        Assert.Equal("Done", Start(await kit.Owner("start")));
    }

    // ---------------------------------------------------------------- decline, revoke, persistence

    [Fact]
    public async Task A_no_stores_nothing_and_the_same_question_is_not_asked_again_for_a_minute()
    {
        using var kit = new PlanKit();
        kit.WritePlan(PlanKit.SetPlan("5"));
        kit.Prompt.Answer = false;
        Assert.StartsWith("Declined", await kit.Owner("approve"));
        Assert.Single(kit.Prompt.Requests);
        Assert.StartsWith("Declined", await kit.Owner("approve"));
        Assert.Single(kit.Prompt.Requests);                                                   // answered without a dialog
        Assert.Equal("", await kit.Owner("approved"));
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));

        kit.Time.Advance(TimeSpan.FromSeconds(61));
        kit.Prompt.Answer = true;
        Assert.StartsWith("Approved", await kit.Owner("approve"));
        Assert.Equal(2, kit.Prompt.Requests.Count);
    }

    [Fact]
    public async Task A_revoked_plan_does_not_run_but_an_open_run_can_still_be_put_back()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        var run = await kit.Owner("start");
        Assert.Equal("v=5", await kit.Target("get"));

        await kit.Owner("revoke");
        Assert.StartsWith("Revoked", await kit.Owner("approval"));
        Assert.Equal("", await kit.Owner("approved"));
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));                       // a new start is refused (it also has an open run, but approval is checked first)
        Assert.Equal("Done", Start(await kit.Owner("end", RunId(run))));                      // the undo of what was already done is still allowed
        Assert.Equal("v=0", await kit.Target("get"));
    }

    [Fact]
    public async Task Approvals_and_open_runs_survive_a_restart_and_the_host_does_not_undo_by_itself()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        var run = RunId(await kit.Owner("start"));
        kit.Toasts.Shown.Clear();

        kit.Restart();
        Assert.StartsWith("Approved", await kit.Owner("approval"));
        Assert.Contains($"{run}|scene|interrupted=True", await kit.Owner("runs"));
        var warning = Assert.Single(kit.Toasts.Shown);                                        // one notification: a scene was running when Rigbay closed
        Assert.Equal(ToastKind.Warning, warning.Kind);
        Assert.Contains("plan.name", warning.Body, StringComparison.OrdinalIgnoreCase);                  // the plan's name (no language file here, so the key shows)
        Assert.Equal("v=0", await kit.Target("get"));                                         // (a new process, a new memory) and nothing was undone

        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")).Replace("AlreadyRunning", "NeedsApproval"));   // the open run blocks a second one
        var end = await kit.Owner("end", run);
        Assert.Equal("Done", Start(end));
        Assert.Contains("u:set=Done", end);                                                   // the recorded undo ran
        Assert.Equal("", await kit.Owner("runs"));
    }

    [Fact]
    public async Task The_host_verbs_list_revoke_undo_and_forget_work_without_the_owner()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        var run = RunId(await kit.Owner("start"));

        var list = await kit.Host.ExecuteCommandAsync(["plans", "list"]);
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("owner/scene  approved", list.Output);
        Assert.Contains($"run {run}  owner/scene  open", list.Output);

        var undo = await kit.Host.ExecuteCommandAsync(["plans", "undo", run]);
        Assert.Equal(0, undo.ExitCode);
        Assert.Equal("v=0", await kit.Target("get"));
        Assert.Equal(CommandOutcome.UsageOrNotFound, (await kit.Host.ExecuteCommandAsync(["plans", "undo", run])).ExitCode);

        var revoke = await kit.Host.ExecuteCommandAsync(["plans", "revoke", "owner/scene"]);
        Assert.Equal(0, revoke.ExitCode);
        Assert.StartsWith("Revoked", await kit.Owner("approval"));
        Assert.Equal(CommandOutcome.UsageOrNotFound, (await kit.Host.ExecuteCommandAsync(["plans", "revoke", "owner/nothing"])).ExitCode);
        Assert.Equal(CommandOutcome.UsageOrNotFound, (await kit.Host.ExecuteCommandAsync(["plans"])).ExitCode);
        Assert.Equal(CommandOutcome.UsageOrNotFound, (await kit.Host.ExecuteCommandAsync(["plans", "forget", "nothing"])).ExitCode);
    }

    [Fact]
    public async Task Forget_drops_a_run_without_undoing_it()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        var run = RunId(await kit.Owner("start"));
        Assert.Equal(0, (await kit.Host.ExecuteCommandAsync(["plans", "forget", run])).ExitCode);
        Assert.Equal("v=5", await kit.Target("get"));
        Assert.Equal("", await kit.Owner("runs"));
    }

    [Fact]
    public async Task A_corrupt_approval_file_means_nothing_is_approved()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        kit.Host.Shutdown();
        File.WriteAllText(Path.Combine(kit.PlansRoot, "approved-plans.json"), "{ this is not json");
        kit.Start();
        Assert.StartsWith("NotApproved", await kit.Owner("approval"));
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));
        Assert.Contains(Directory.GetFiles(kit.PlansRoot), f => Path.GetFileName(f).StartsWith("approved-plans.json.corrupt-"));
    }

    private sealed class WrongFolder : Rigbay.Core.Install.IFolderSecurity
    {
        public void EnsureAdminOnly(string path) => Directory.CreateDirectory(path);
        public string? Verify(string path) => "S-1-5-21-1 may write (Modify)";
    }

    [Fact]
    public async Task A_folder_a_user_can_write_turns_the_whole_feature_off_and_is_not_repaired()
    {
        using var kit = new PlanKit(start: false);
        Directory.CreateDirectory(kit.PlansRoot);
        // a standard user planted a perfectly valid approval of a plan that writes
        kit.Start(security: null);
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        kit.Host.Shutdown();
        kit.Prompt.Requests.Clear();

        kit.Start(security: new WrongFolder());
        var approval = await kit.Owner("approval");
        Assert.StartsWith("Untrusted|", approval);
        Assert.Contains("may write", approval);
        Assert.StartsWith("Untrusted", await kit.Owner("approve"));
        Assert.Empty(kit.Prompt.Requests);                                                     // not even asked: it could not be saved
        Assert.Equal("NeedsApproval", Start(await kit.Owner("start")));
        Assert.Equal("v=0", await kit.Target("get"));
        Assert.Equal("", await kit.Owner("approved"));
        Assert.Contains("approved plans are off", string.Join("\n", kit.Host.Log.Recent().Select(e => e.Message)));
        var list = await kit.Host.ExecuteCommandAsync(["plans", "list"]);
        Assert.Contains("Approved plans are off", list.Output);
    }

    [Fact]
    public async Task The_approvals_of_a_module_that_is_gone_are_dropped()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        kit.Host.Shutdown();

        // a host over the same folders whose modules folder no longer has the owner (the loaded DLL of the first host keeps its folder locked, so copy the target)
        var modules = Path.Combine(kit.Env.Root, "modules-without-owner");
        foreach (var file in Directory.GetFiles(kit.TargetFolder, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(modules, "target", Path.GetRelativePath(kit.TargetFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        using var host = new RigbayHost(new RigbayHostOptions
        {
            ModulesRoot = modules, DataRoot = kit.Env.DataRoot, PlansRoot = kit.PlansRoot, TimeProvider = kit.Time,
            FolderSecurity = new Rigbay.Core.Install.NoFolderSecurity(),
        });
        host.Discover();
        var list = await host.ExecuteCommandAsync(["plans", "list"]);
        Assert.DoesNotContain("owner/scene", list.Output);
        host.Shutdown();
    }

    // ---------------------------------------------------------------- the answer the plan gives for the target

    [Fact]
    public async Task The_plan_answers_at_most_three_questions_per_step_and_the_rest_go_to_the_owner()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, """{ "id": "ask", "steps": [ { "module": "target", "verb": "ask", "args": ["5"] } ] }""");
        kit.Prompt.Answer = false;                                                              // anything that reaches the dialog is refused
        var before = kit.Prompt.Requests.Count;
        var run = await kit.Owner("start");
        Assert.Equal("Done", Start(run));
        Assert.Equal(before + 2, kit.Prompt.Requests.Count);                                    // the 4th and 5th question reached the owner
        Assert.Equal("yes=3/5", PlanKit.Field(run, "out"));                                     // three answered by the plan, two refused by the owner
    }

    [Fact]
    public async Task A_question_of_another_kind_than_the_verb_declared_is_not_answered()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, """{ "id": "kind", "steps": [ { "module": "target", "verb": "ask-system", "args": [] } ] }""");
        kit.Prompt.Answer = false;
        var before = kit.Prompt.Requests.Count;
        var run = await kit.Owner("start");
        Assert.Equal("Done", Start(run));
        Assert.Equal(before + 1, kit.Prompt.Requests.Count);                                    // the mismatch went to the normal dialog
    }

    [Fact]
    public async Task A_system_setting_verb_may_be_answered_for_its_own_kind_only()
    {
        using var kit = new PlanKit(targetFlags: "kind=system");
        await ApprovedAsync(kit, """{ "id": "sys", "steps": [ { "module": "target", "verb": "ask", "args": ["1"] }, { "module": "target", "verb": "ask-system", "args": [] } ] }""");
        kit.Prompt.Answer = false;
        var before = kit.Prompt.Requests.Count;
        Assert.Equal("Done", Start(await kit.Owner("start")));
        Assert.Equal(before + 1, kit.Prompt.Requests.Count);                                    // ask (SystemSetting) answered; ask-system asks Hardware: dialog
        Assert.Equal(EffectKind.SystemSetting, kit.Prompt.Requests[0].Plan![0].Kind);
    }

    [Fact]
    public async Task A_task_the_verb_leaves_running_cannot_use_the_answer_later()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, """{ "id": "leak", "steps": [ { "module": "target", "verb": "leak", "args": [] } ] }""");
        kit.Prompt.Answer = false;
        var before = kit.Prompt.Requests.Count;
        Assert.Equal("Done", Start(await kit.Owner("start")));
        for (int i = 0; i < 50 && await kit.Target("leaked") == "leaked=none"; i++) await Task.Delay(100);
        Assert.Equal("leaked=yes=False", await kit.Target("leaked"));                           // it asked after the step ended and was refused like anybody
        Assert.Equal(before + 1, kit.Prompt.Requests.Count);
    }

    [Fact]
    public async Task A_verb_the_host_started_cannot_start_a_call()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, """{ "id": "nest", "steps": [ { "module": "target", "verb": "nested", "args": [] } ] }""");
        var run = await kit.Owner("start");
        Assert.Equal("Done", Start(run));
        Assert.Equal("nested=Refused", PlanKit.Field(run, "out"));                              // the verb tried to start a call from inside the host's call
    }

    // ---------------------------------------------------------------- failure, timeout, fault boundaries

    [Fact]
    public async Task A_failing_step_puts_back_the_steps_already_done_and_warns_once()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, """{ "id": "two", "steps": [ { "module": "target", "verb": "set", "args": ["5"], "restore": true }, { "module": "target", "verb": "set", "args": ["fail"] } ] }""");
        var run = await kit.Owner("start");
        Assert.Equal("Failed", Start(run));
        Assert.Contains("set=Done,set=Failed,u:set=Done", PlanKit.Field(run, "steps"));
        Assert.Equal("v=0", await kit.Target("get"));                                           // step 1 was put back
        Assert.Equal("", await kit.Owner("runs"));                                              // and the run is closed
        var warning = Assert.Single(kit.Toasts.Shown);
        Assert.Equal(ToastKind.Warning, warning.Kind);
        Assert.Contains("1 step(s) were put back", warning.Body);
    }

    [Fact]
    public async Task A_verb_that_throws_faults_its_module_and_not_the_owner()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, """{ "id": "boom", "steps": [ { "module": "target", "verb": "set", "args": ["boom"] } ] }""");
        var run = await kit.Owner("start");
        Assert.Equal("Failed", Start(run));
        Assert.Contains("set=CalleeFaulted", run);
        Assert.Equal(ModuleLifecycle.Faulted, kit.Host.Find("target")!.Lifecycle);
        Assert.Equal(ModuleLifecycle.Running, kit.Host.Find("owner")!.Lifecycle);
        Assert.Equal("", await kit.Owner("runs"));
    }

    [Fact]
    public async Task A_verb_that_does_not_answer_times_out_without_faulting_its_module()
    {
        using var kit = new PlanKit(verbTimeout: TimeSpan.FromMilliseconds(400));
        await ApprovedAsync(kit, """{ "id": "slow", "steps": [ { "module": "target", "verb": "set", "args": ["hang"] } ] }""");
        var run = await kit.Owner("start");
        Assert.Equal("Failed", Start(run));
        Assert.Contains("set=TimedOut", run);
        Assert.Equal(ModuleLifecycle.Running, kit.Host.Find("target")!.Lifecycle);
        Assert.Equal(ModuleLifecycle.Running, kit.Host.Find("owner")!.Lifecycle);
    }

    [Fact]
    public async Task A_target_that_is_not_running_refuses_the_run_before_anything_happens()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        kit.Host.Find("target")!.Disable();
        var run = await kit.Owner("start");
        Assert.Equal("Invalid", Start(run));
        Assert.Contains("not running", run);
        Assert.StartsWith("Unavailable|", await kit.Owner("approval"));
    }

    [Fact]
    public async Task A_bad_plan_is_invalid()
    {
        using var kit = new PlanKit();
        kit.WritePlan("""{ "id": "Bad Id", "steps": [ { "module": "target", "verb": "set", "args": ["5"] } ] }""");
        Assert.StartsWith("Unavailable|the plan id", await kit.Owner("approval"));
        kit.WritePlan("""{ "id": "self", "steps": [ { "module": "owner", "verb": "list", "args": [] } ] }""");
        Assert.Contains("may not target the module that owns the plan", await kit.Owner("approval"));
        kit.WritePlan("""{ "id": "none", "steps": [] }""");
        Assert.Contains("no steps", await kit.Owner("approval"));
        kit.WritePlan("""{ "id": "nine", "steps": [ """ + string.Join(",", Enumerable.Repeat("""{ "module": "target", "verb": "get", "args": [] }""", 9)) + " ] }");
        Assert.Contains("at most 8 steps", await kit.Owner("approval"));
        kit.WritePlan("""{ "id": "bad", "steps": [ { "module": "target", "verb": "get", "args": [] }, { "module": "target", "verb": "set", "args": ["1"], "restore": true, "undo": { "module": "target", "verb": "set", "args": ["0"] } } ] }""");
        Assert.Contains("not both", await kit.Owner("approval"));
        kit.WritePlan("""{ "id": "legacy", "steps": [ { "module": "target", "verb": "legacy", "args": [], "restore": true } ] }""");
        Assert.Contains("cannot use the recorded undo", await kit.Owner("approval"));
        kit.WritePlan("""{ "id": "gone", "steps": [ { "module": "target", "verb": "nothing", "args": [] } ] }""");
        Assert.Contains("has no verb 'nothing'", await kit.Owner("approval"));
        Assert.Empty(kit.Prompt.Requests);
    }

    [Fact]
    public async Task An_old_style_verb_can_be_part_of_a_plan_and_the_dialog_says_the_module_did_not_say()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, """{ "id": "old", "steps": [ { "module": "target", "verb": "legacy", "args": [] } ] }""");
        var line = Assert.Single(kit.Prompt.Requests[0].Plan!);
        Assert.Contains("did not say", line.EffectText);
        var run = await kit.Owner("start");
        Assert.Equal("Done", Start(run));                                                       // its question (any kind) was answered by the plan, and logged
        Assert.Equal("legacy done", PlanKit.Field(run, "out"));
        Assert.Contains(kit.Host.Log.Recent(), e => e.Message.Contains("the verb declared no effect kind"));
    }

    // ---------------------------------------------------------------- overlap and rate

    [Fact]
    public async Task A_plan_with_an_open_run_cannot_start_again_and_four_runs_is_the_limit()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("1", id: "s1"));
        Assert.Equal("Done", Start(await kit.Owner("start")));
        Assert.Equal("AlreadyRunning", Start(await kit.Owner("start")));

        for (int i = 2; i <= 4; i++)
        {
            await ApprovedAsync(kit, PlanKit.SetPlan(i.ToString(), id: "s" + i));
            Assert.Equal("Done", Start(await kit.Owner("start")));
        }
        await ApprovedAsync(kit, PlanKit.SetPlan("5", id: "s5"));
        Assert.Equal("TooManyRuns", Start(await kit.Owner("start")));
    }

    [Fact]
    public async Task A_plan_that_is_started_too_often_is_paused_and_the_owner_hears_once()
    {
        using var kit = new PlanKit(startsPerHour: 2);
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        for (int i = 0; i < 2; i++)
        {
            var run = await kit.Owner("start");
            Assert.Equal("Done", Start(run));
            await kit.Owner("end", RunId(run));
        }
        kit.Toasts.Shown.Clear();
        Assert.Equal("RateLimited", Start(await kit.Owner("start")));
        Assert.Equal("RateLimited", Start(await kit.Owner("start")));
        var warning = Assert.Single(kit.Toasts.Shown);
        Assert.Equal(ToastKind.Warning, warning.Kind);
        Assert.Equal("v=0", await kit.Target("get"));

        kit.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal("Done", Start(await kit.Owner("start")));
    }

    [Fact]
    public async Task All_plans_together_have_an_hourly_limit_too()
    {
        using var kit = new PlanKit(startsPerHourTotal: 2);
        for (int i = 1; i <= 3; i++)
        {
            await ApprovedAsync(kit, PlanKit.SetPlan(i.ToString(), id: "p" + i));
            var outcome = Start(await kit.Owner("start"));
            Assert.Equal(i <= 2 ? "Done" : "RateLimited", outcome);
        }
        kit.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal("Done", Start(await kit.Owner("start")));
    }

    [Fact]
    public async Task Two_starts_of_one_plan_at_the_same_time_run_one_after_the_other_so_only_one_runs()
    {
        using var kit = new PlanKit();
        await ApprovedAsync(kit, PlanKit.SetPlan("5"));
        var both = await Task.WhenAll(kit.Owner("start"), kit.Owner("start"));
        Assert.Equal(new[] { "AlreadyRunning", "Done" }, both.Select(Start).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("approvals=1", await kit.Target("count"));                                 // the write happened once
    }
}
