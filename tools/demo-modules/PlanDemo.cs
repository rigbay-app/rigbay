using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.DemoModules;

// Used only by the self-test (tools/out/modules-test), host API 0.1.4. Two modules and no hardware: the "target" changes two values that live in its own
// memory, the "owner" asks for the approval of a plan and runs it, the way a Game scenes module would.

/// <summary>Verbs that change an in-memory number and flag. They ask for approval like a real write verb does.</summary>
public sealed class PlanTargetModule : IModule
{
    private int _value;
    private bool _mark;
    private string _profile = "gaming";   // the module's own configuration: part of what "set 5" means
    private Timer? _timer;
    private IHostServices? _host;

    public ProbeResult Probe() => ProbeResult.Yes();

    public void Initialize(IHostServices host)
    {
        _host = host;
        var effect = new HardwareEffect("effect.set");
        host.Commands.Register(new CommandInfo("get", "cmd.get") { Access = VerbAccess.ReadOnly }, _ => CommandResult.Ok($"v={_value};mark={(_mark ? "on" : "off")};profile={_profile}"));
        host.Commands.Register(new CommandInfo("profile", "cmd.profile") { Access = VerbAccess.ReadOnly }, ctx => { _profile = ctx.Args.Count > 0 ? ctx.Args[0] : "gaming"; return CommandResult.Ok("profile=" + _profile); });

        host.Commands.Register(new CommandInfo("set", "cmd.set")
        {
            Access = VerbAccess.Writes,
            Effect = effect,
            UsageKey = "cmd.set.usage",
            Preview = args => LocalisedText.Of("preview.set", args.Count > 0 ? args[0] : "?", _profile),   // arguments and configuration only, never the live value
            UndoVerb = "set",
        }, async ctx =>
        {
            if (ctx.Args.Count != 1 || !int.TryParse(ctx.Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 0 or > 100) return CommandResult.Usage("usage: set <0..100>");
            var yes = await host.Approval.ConfirmAsync(effect, LocalisedText.Of("detail.set", _value, n), ctx.CancellationToken).ConfigureAwait(false);
            if (!yes) return CommandResult.Failed("declined");
            var before = _value;
            _value = n;
            Publish();
            return before == n ? CommandResult.Ok("already") : CommandResult.Ok("set") with { UndoArgs = [before.ToString(CultureInfo.InvariantCulture)] };
        });

        host.Commands.Register(new CommandInfo("mark", "cmd.mark") { Access = VerbAccess.Writes, Effect = new HardwareEffect("effect.mark") { Kind = EffectKind.SystemSetting }, Preview = args => LocalisedText.Of("preview.mark", args.Count > 0 ? args[0] : "?") }, async ctx =>
        {
            var wanted = ctx.Args.Count == 1 && ctx.Args[0] == "on";
            var yes = await host.Approval.ConfirmAsync(new HardwareEffect("effect.mark") { Kind = EffectKind.SystemSetting }, LocalisedText.Of("detail.mark", wanted ? "on" : "off"), ctx.CancellationToken).ConfigureAwait(false);
            if (!yes) return CommandResult.Failed("declined");
            _mark = wanted;
            return CommandResult.Ok("mark " + (wanted ? "on" : "off"));
        });

        // a publisher built for 0.1.4: the decision comes from MetricDemand, and "poll" reports it
        host.Metrics.Describe(new MetricDescriptor(new MetricId("demo-plan-target.value"), "metric.value", MetricUnit.Count));
        host.Commands.Register(new CommandInfo("poll", "cmd.poll") { Access = VerbAccess.ReadOnly }, _ =>
        {
            var d = host.Evaluate([new MetricId("demo-plan-target.value")], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
            return CommandResult.Ok($"{d.Kind}|{d.Interval.TotalSeconds.ToString(CultureInfo.InvariantCulture)}");
        });
        host.Commands.Register(new CommandInfo("subs", "cmd.subs") { Access = VerbAccess.ReadOnly }, _ =>
            CommandResult.Ok($"subs={host.Metrics.SubscriberCount(new MetricId("demo-plan-target.value"))}"));
        _timer = new Timer(_ => Publish(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void Publish()
    {
        try { _host?.Metrics.Publish(new MetricId("demo-plan-target.value"), _value, MetricUnit.Count); }
        catch (Exception) { /* stopped */ }
    }

    public void Shutdown() { _timer?.Dispose(); _timer = null; }
}

/// <summary>Owns one plan ("scene": two steps) and drives it through <c>host.Verbs</c> with verbs, so the self-test can press its buttons.</summary>
public sealed class PlanOwnerModule : IModule
{
    private readonly List<IDisposable> _background = new();

    public ProbeResult Probe() => ProbeResult.Yes();

    private static VerbPlan Plan(string value) => new("scene", LocalisedText.Of("plan.name"), new[]
    {
        new PlanStep("demo-plan-target", "set", [value]) { RestoreRecorded = true },
        new PlanStep("demo-plan-target", "mark", ["on"]) { FixedUndo = new PlanStep("demo-plan-target", "mark", ["off"]) },
    });

    private static string Describe(PlanRun run) =>
        $"{run.Outcome}|{run.RunId}|" + string.Join(",", run.Steps.Select(s => (s.IsUndo ? "u:" : "") + s.Step.Verb + "=" + s.Outcome));

    public void Initialize(IHostServices host)
    {
        var verbs = host.Verbs;
        host.Commands.Register("approval", "cmd.approval", _ => { var a = verbs.GetApproval(Plan("5")); return CommandResult.Ok($"{a.State}|{a.Reason}"); });
        host.Commands.Register("approve", "cmd.approve", async ctx => { var a = await verbs.RequestApprovalAsync(Plan("5"), ctx.CancellationToken).ConfigureAwait(false); return CommandResult.Ok($"{a.State}|{a.Reason}"); });
        host.Commands.Register("start", "cmd.start", async ctx => CommandResult.Ok(Describe(await verbs.StartAsync(Plan("5"), ctx.CancellationToken).ConfigureAwait(false))));
        host.Commands.Register("start-changed", "cmd.start-changed", async ctx => CommandResult.Ok(Describe(await verbs.StartAsync(Plan("6"), ctx.CancellationToken).ConfigureAwait(false))));
        host.Commands.Register("end", "cmd.end", async ctx => CommandResult.Ok(Describe(await verbs.EndAsync(ctx.Args[0], ctx.CancellationToken).ConfigureAwait(false))));
        host.Commands.Register("revoke", "cmd.revoke", _ => { verbs.Revoke("scene"); return CommandResult.Ok("revoked"); });
        host.Commands.Register("runs", "cmd.runs", _ => CommandResult.Ok(string.Join(";", verbs.ActiveRuns().Select(r => $"{r.RunId}|{r.PlanId}|{r.Interrupted}"))));
        host.Commands.Register("list", "cmd.list", _ => CommandResult.Ok(string.Join(";", verbs.List().Select(v => $"{v.ModuleId}/{v.Verb}/{v.Access}"))));
        host.Commands.Register("invoke", "cmd.invoke", async ctx =>
        {
            var r = await verbs.InvokeAsync(ctx.Args[0], ctx.Args[1], ctx.Args.Skip(2).ToList(), ctx.CancellationToken).ConfigureAwait(false);
            return CommandResult.Ok($"{r.Outcome}|{r.Output}");
        });
        host.Commands.Register("watch", "cmd.watch", _ =>
        {
            _background.Add(host.Metrics.SubscribeBackground(new MetricId("demo-plan-target.value"), TimeSpan.FromSeconds(30), s => { }));
            return CommandResult.Ok("watching");
        });
        host.Commands.Register("unwatch", "cmd.unwatch", _ => { foreach (var b in _background) b.Dispose(); _background.Clear(); return CommandResult.Ok("stopped"); });
    }

    public void Shutdown() { foreach (var b in _background) b.Dispose(); _background.Clear(); }
}
