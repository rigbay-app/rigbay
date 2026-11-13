using Rigbay.Abstractions;
using Rigbay.Core;
using Rigbay.Core.Install;

namespace Rigbay.App;

/// <summary>
/// Host API 0.1.4 (H1): background metric demand and cross-module verbs under an approved plan, run against two demo modules that change numbers in their own
/// memory. Nothing here touches hardware or a real module's verb. Approval answers come from the test harness (<see cref="ApprovalPrompt.TestAnswer"/>); the dialog
/// itself is composed (title, button, step lines) but not clicked.
/// </summary>
static partial class SelfTest
{
    static async Task<string> Say(params string[] args) => (await Host.Core.ExecuteCommandAsync(args)).Output;

    static int Notices(string text) =>
        Host.Core.Log.Recent().Count(e => e.Source == "plans" && e.Message.StartsWith("notice [Info]", StringComparison.Ordinal) && e.Message.Contains(text, StringComparison.Ordinal));

    static async Task PlanChecks(MainWindow w)
    {
        var core = Host.Core;
        var target = core.Find("demo-plan-target");
        var owner = core.Find("demo-plan-owner");
        if (target is null || owner is null) { Skip("plans: demo modules", "demo-plan-target and demo-plan-owner are not in this module set"); return; }
        var previousAnswer = ApprovalPrompt.TestAnswer;
        try
        {
            await Task.Run(() => { target.Enable(); owner.Enable(); });
            R("plans: the two demo modules run", target.Lifecycle == ModuleLifecycle.Running && owner.Lifecycle == ModuleLifecycle.Running, $"target={target.Lifecycle} owner={owner.Lifecycle}");

            // ---- listing and read-only calls
            var list = (await Say("demo-plan-owner", "list")).Split(';');
            R("plans: the owner lists the other module's verbs with what they declared", list.Contains("demo-plan-target/get/ReadOnly") && list.Contains("demo-plan-target/set/Writes") && list.Contains("demo-plan-target/mark/Writes") && list.All(l => !l.StartsWith("demo-plan-owner/")),
                string.Join(" ", list.Where(l => l.StartsWith("demo-plan-target/"))));
            var read = await Say("demo-plan-owner", "invoke", "demo-plan-target", "get");
            var write = await Say("demo-plan-owner", "invoke", "demo-plan-target", "set", "9");
            R("plans: a read-only verb runs for another module and a write verb does not", read.StartsWith("Done|v=0") && write.StartsWith("NeedsPlan|") && (await Say("demo-plan-target", "get")).StartsWith("v=0"), $"read='{read}' write='{write}'");

            // ---- background demand
            await Say("demo-plan-owner", "watch");
            var demands = core.Metrics.GetBackgroundDemands();
            R("demand: a background subscription is not a subscriber, and the publisher decides on its background rate",
                core.Metrics.SubscriberCount(new MetricId("demo-plan-target.value")) == 0 && demands.Count == 1 && (await Say("demo-plan-target", "subs")) == "subs=0" && (await Say("demo-plan-target", "poll")) == "Background|30",
                $"demands={demands.Count} subs={await Say("demo-plan-target", "subs")} poll={await Say("demo-plan-target", "poll")}");
            await Say("demo-plan-owner", "unwatch");
            R("demand: stopping it puts the publisher back to its idle rate", core.Metrics.GetBackgroundDemands().Count == 0 && (await Say("demo-plan-target", "poll")) == "Idle|60", $"poll={await Say("demo-plan-target", "poll")}");

            // ---- the folder that holds the approvals
            var root = core.Plans.StorageRoot;
            var probe = Path.Combine(Path.GetDirectoryName(root)!, "plans-probe");
            Directory.CreateDirectory(probe);      // an ordinary folder: inherits the parent's permissions, which let normal users write
            var security = new AdminFolderSecurity();
            var before = security.Verify(probe);
            security.EnsureAdminOnly(probe);
            var after = security.Verify(probe);
            R("plans: Verify tells an ordinary folder from an admin-only one", before is not null && after is null, $"ordinary: '{before}'; after EnsureAdminOnly: '{after}'");

            // ---- approval, one dialog
            ApprovalPrompt.TestAnswer = true;
            int asked = ApprovalPrompt.Asked;
            var approval = await Say("demo-plan-owner", "approve");
            var lines = ApprovalPrompt.LastPlanLines;
            R("plans: ONE dialog lists the steps and the owner approves", approval.StartsWith("Approved") && ApprovalPrompt.Asked == asked + 1 && lines.Count == 2 && lines[0] == "demo-plan-target set 5" && lines[1] == "demo-plan-target mark on",
                $"answer='{approval}' asked={ApprovalPrompt.Asked - asked}x lines=[{string.Join(" ; ", lines)}]");
            R("plans: the dialog title names the scene and the button counts the steps", ApprovalPrompt.LastTitle == Host.F("approval.plan-title", "Demo scene"), $"title='{ApprovalPrompt.LastTitle}'");
            var storedBad = FolderProblem(root);
            R("plans: the approval is stored in an admin-only folder", core.Plans.Trusted && storedBad is null && File.Exists(Path.Combine(root, "approved-plans.json")), $"root={root} problem='{storedBad}'");

            // ---- a run: no dialog for the target's own questions, one notification, values changed
            int notices = Notices("Demo scene");
            var run = await Say("demo-plan-owner", "start");
            var state = await Say("demo-plan-target", "get");
            R("plans: the run changes the demo values without asking again", run.StartsWith("Done|") && state.StartsWith("v=5;mark=on") && ApprovalPrompt.Asked == asked + 1, $"run='{run}' state='{state}' asked={ApprovalPrompt.Asked - asked}x");
            R("plans: one notification for the run", Notices("Demo scene") == notices + 1, $"notices {notices} -> {Notices("Demo scene")}");
            R("plans: the target's own questions were answered by the plan and logged", core.Log.Recent().Count(e => e.Message.StartsWith("plan step: the approved plan answered")) >= 2, "log lines 'plan step: the approved plan answered ...'");

            // ---- exact match
            var changed = await Say("demo-plan-owner", "start-changed");
            R("plans: a changed step is refused and runs nothing", changed.StartsWith("NeedsApproval|") && (await Say("demo-plan-target", "get")).StartsWith("v=5;mark=on"), $"'{changed}'");
            var again = await Say("demo-plan-owner", "start");
            R("plans: the same plan cannot start again while it has an open run", again.StartsWith("AlreadyRunning|"), $"'{again}'");

            // ---- undo
            var runId = run.Split('|')[1];
            var end = await Say("demo-plan-owner", "end", runId);
            R("plans: the end puts back the recorded value and the fixed step, with one more notification", end.StartsWith("Done|") && (await Say("demo-plan-target", "get")).StartsWith("v=0;mark=off") && Notices("Put back") >= 1, $"end='{end}' state='{await Say("demo-plan-target", "get")}'");

            // ---- what a preview says is part of the approval, the live value is not
            await Say("demo-plan-target", "profile", "other");
            var edited = await Say("demo-plan-owner", "approval");
            await Say("demo-plan-target", "profile", "gaming");
            var restored = await Say("demo-plan-owner", "approval");
            R("plans: an edited configuration voids the approval and putting it back restores it (the live value never counts)", edited.StartsWith("Changed|") && restored.StartsWith("Approved|"), $"edited='{edited}' restored='{restored}'");

            // ---- decline, revoke, the host verbs
            await Say("demo-plan-owner", "revoke");
            var afterRevoke = await Say("demo-plan-owner", "start");
            var plans = await Say("plans", "list");
            R("plans: a revoked plan does not run, and rigbay plans list says so", afterRevoke.StartsWith("NeedsApproval|") && plans.Contains("demo-plan-owner/scene  revoked"), $"start='{afterRevoke}' list='{plans.ReplaceLineEndings(" | ")}'");
            ApprovalPrompt.TestAnswer = false;
            asked = ApprovalPrompt.Asked;
            var declined = await Say("demo-plan-owner", "approve");
            R("plans: a no stores nothing", declined.StartsWith("Declined") && ApprovalPrompt.Asked == asked + 1 && (await Say("demo-plan-owner", "start")).StartsWith("NeedsApproval|"), $"'{declined}'");
        }
        finally { ApprovalPrompt.TestAnswer = previousAnswer; }
    }

    static string? FolderProblem(string path) => new AdminFolderSecurity().Verify(path);
}
