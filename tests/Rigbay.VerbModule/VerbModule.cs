using System.Globalization;
using System.Text.Json;
using Rigbay.Abstractions;

namespace Rigbay.VerbModule;

/// <summary>
/// Test module for the H1 host additions. The first line of <c>behavior.txt</c> is the role: <c>target</c> or <c>owner</c>; the remaining lines are flags
/// (<c>kind=system</c> declares the write verbs as a Windows setting). It touches nothing but its own memory.
/// <para>Target verbs: <c>get</c> (read-only), <c>set &lt;n&gt;</c> (declared write; asks <c>Approval</c>; reports the old value as undo; <c>boom</c> throws,
/// <c>fail</c> fails, <c>hang</c> waits for cancellation), <c>legacy</c> (registered the old way), <c>ask &lt;n&gt;</c> (asks n times),
/// <c>ask-system</c> (asks with the other kind), <c>leak</c> / <c>leaked</c> (asks later from a task the verb left running), <c>nested</c> (calls the
/// verb service from inside a verb), <c>config &lt;text&gt;</c> (changes what the preview says), <c>count</c> (number of approvals it got).</para>
/// <para>Owner verbs read <c>plan.json</c> from the module folder each time: <c>list</c>, <c>invoke</c>, <c>approval</c>, <c>approve</c>, <c>start</c>, <c>end</c>,
/// <c>revoke</c>, <c>runs</c>, <c>approved</c>, <c>bg</c> (background demand), <c>bg-interval</c>.</para>
/// </summary>
public sealed class VerbModule : IModule
{
    private static readonly string Folder = Path.GetDirectoryName(typeof(VerbModule).Assembly.Location)!;
    private readonly string _role;
    private readonly HashSet<string> _flags;
    private IHostServices? _host;
    private int _value;
    private string _config = "base";
    private int _approvals;
    private string _leaked = "none";
    private readonly List<IDisposable> _background = new();
    private readonly List<string> _backgroundSamples = new();

    public VerbModule()
    {
        var file = Path.Combine(Folder, "behavior.txt");
        var lines = File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray() : new[] { "target" };
        _role = lines.Length > 0 ? lines[0] : "target";
        _flags = new HashSet<string>(lines.Skip(1));
    }

    public ProbeResult Probe() => ProbeResult.Yes();

    public void Shutdown()
    {
        foreach (var b in _background) b.Dispose();
        _background.Clear();
    }

    public void Initialize(IHostServices host)
    {
        _host = host;
        if (_role == "target") InitTarget(host); else InitOwner(host);
        InitMetrics(host);
    }

    private EffectKind Kind => _flags.Contains("kind=system") ? EffectKind.SystemSetting : EffectKind.Hardware;

    private void InitTarget(IHostServices host)
    {
        host.Commands.Register(new CommandInfo("get", "cmd.get") { Access = VerbAccess.ReadOnly }, _ => CommandResult.Ok("v=" + _value));
        host.Commands.Register(new CommandInfo("count", "cmd.count") { Access = VerbAccess.ReadOnly }, _ => CommandResult.Ok("approvals=" + _approvals));
        host.Commands.Register(new CommandInfo("config", "cmd.config") { Access = VerbAccess.ReadOnly }, ctx => { _config = ctx.Args.Count > 0 ? ctx.Args[0] : "base"; return CommandResult.Ok("config=" + _config); });
        host.Commands.Register(new CommandInfo("leaked", "cmd.leaked") { Access = VerbAccess.ReadOnly }, _ => CommandResult.Ok("leaked=" + _leaked));

        var set = new CommandInfo("set", "cmd.set")
        {
            Access = VerbAccess.Writes,
            Effect = new HardwareEffect("effect.set") { Kind = Kind },
            // a pure function of the arguments and the module's own configuration; the live value is deliberately not in it
            Preview = args => LocalisedText.Of("preview.set", args.Count > 0 ? args[0] : "?", _config),
            UndoVerb = "set",
        };
        host.Commands.Register(set, async ctx =>
        {
            if (ctx.Args.Count != 1) return CommandResult.Usage("usage: set <n>");
            var arg = ctx.Args[0];
            if (arg == "boom") throw new InvalidOperationException("set blew up");
            if (arg == "hang") { await Task.Delay(Timeout.Infinite, ctx.CancellationToken).ConfigureAwait(false); }
            if (arg == "fail") return CommandResult.Failed("set failed on purpose");
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return CommandResult.Usage("usage: set <n>");
            var approved = await host.Approval.ConfirmAsync(new HardwareEffect("effect.set") { Kind = Kind }, LocalisedText.Of("detail.set", _value, n), ctx.CancellationToken).ConfigureAwait(false);
            if (!approved) return CommandResult.Failed("declined");
            Interlocked.Increment(ref _approvals);
            var previous = _value;
            _value = n;
            return previous == n ? CommandResult.Ok("already " + n) : CommandResult.Ok("set " + n) with { UndoArgs = [previous.ToString(CultureInfo.InvariantCulture)] };
        });

        // registered the old way: no metadata, so the host treats it as a write; it also asks
        host.Commands.Register("legacy", "cmd.legacy", async ctx =>
        {
            var ok = await host.Approval.ConfirmAsync(new HardwareEffect("effect.legacy"), null, ctx.CancellationToken).ConfigureAwait(false);
            return ok ? CommandResult.Ok("legacy done") : CommandResult.Failed("declined");
        });

        host.Commands.Register(new CommandInfo("ask", "cmd.ask") { Access = VerbAccess.Writes, Effect = new HardwareEffect("effect.set") { Kind = Kind } }, async ctx =>
        {
            int asks = int.Parse(ctx.Args[0], CultureInfo.InvariantCulture), yes = 0;
            for (int i = 0; i < asks; i++)
                if (await host.Approval.ConfirmAsync(new HardwareEffect("effect.set") { Kind = Kind }, LocalisedText.Of("detail.ask", i), ctx.CancellationToken).ConfigureAwait(false)) yes++;
            return CommandResult.Ok($"yes={yes}/{asks}");
        });
        host.Commands.Register(new CommandInfo("ask-system", "cmd.ask-system") { Access = VerbAccess.Writes, Effect = new HardwareEffect("effect.set") { Kind = Kind } }, async ctx =>
        {
            var other = Kind == EffectKind.Hardware ? EffectKind.SystemSetting : EffectKind.Hardware;
            var yes = await host.Approval.ConfirmAsync(new HardwareEffect("effect.other") { Kind = other }, null, ctx.CancellationToken).ConfigureAwait(false);
            return CommandResult.Ok("yes=" + yes);
        });
        host.Commands.Register(new CommandInfo("leak", "cmd.leak") { Access = VerbAccess.Writes, Effect = new HardwareEffect("effect.set") { Kind = Kind } }, ctx =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400).ConfigureAwait(false);   // after the step has ended: its scope is closed, so the host must ask normally
                try { _leaked = "yes=" + await host.Approval.ConfirmAsync(new HardwareEffect("effect.set") { Kind = Kind }, null, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _leaked = "error " + ex.GetType().Name; }
            });
            return CommandResult.Ok("started");
        });
        host.Commands.Register(new CommandInfo("nested", "cmd.nested") { Access = VerbAccess.Writes, Effect = new HardwareEffect("effect.set") { Kind = Kind } }, async ctx =>
        {
            var result = await host.Verbs.InvokeAsync("owner", "list", [], ctx.CancellationToken).ConfigureAwait(false);
            return CommandResult.Ok("nested=" + result.Outcome);
        });
    }

    // ---------------------------------------------------------------- owner

    private sealed class PlanFile
    {
        public string Id { get; set; } = "scene";
        public List<StepFile> Steps { get; set; } = new();
    }

    private sealed class StepFile
    {
        public string Module { get; set; } = "target";
        public string Verb { get; set; } = "set";
        public List<string> Args { get; set; } = new();
        public bool Restore { get; set; }
        public StepFile? Undo { get; set; }
    }

    private static PlanStep ToStep(StepFile s) =>
        new(s.Module, s.Verb, s.Args) { RestoreRecorded = s.Restore, FixedUndo = s.Undo is null ? null : new PlanStep(s.Undo.Module, s.Undo.Verb, s.Undo.Args) };

    private VerbPlan ReadPlan()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var file = JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(Path.Combine(Folder, "plan.json")), options)!;
        return new VerbPlan(file.Id, LocalisedText.Of("plan.name"), file.Steps.Select(ToStep).ToList());
    }

    private static string Describe(PlanRun run) =>
        $"outcome={run.Outcome};run={run.RunId};steps=" + string.Join(",", run.Steps.Select(s => (s.IsUndo ? "u:" : "") + s.Step.Verb + "=" + s.Outcome))
        + ";out=" + string.Join(",", run.Steps.Select(s => s.Output.Replace(';', ','))) + (run.Message.Length > 0 ? ";msg=" + run.Message : "");

    private void InitOwner(IHostServices host)
    {
        var verbs = host.Verbs;
        host.Commands.Register("list", "cmd.list", _ => CommandResult.Ok(string.Join(";", verbs.List().Select(v => $"{v.ModuleId}/{v.Verb}/{v.Access}/{v.EffectKind}/{v.UndoVerb}/{v.HasPreview}/{v.EffectText}"))));
        host.Commands.Register("invoke", "cmd.invoke", async ctx =>
        {
            var r = await verbs.InvokeAsync(ctx.Args[0], ctx.Args[1], ctx.Args.Skip(2).ToList(), ctx.CancellationToken).ConfigureAwait(false);
            return CommandResult.Ok($"{r.Outcome}|{r.ExitCode}|{r.Output}");
        });
        host.Commands.Register("approval", "cmd.approval", _ => { var a = verbs.GetApproval(ReadPlan()); return CommandResult.Ok($"{a.State}|{a.Reason}"); });
        host.Commands.Register("approve", "cmd.approve", async ctx => { var a = await verbs.RequestApprovalAsync(ReadPlan(), ctx.CancellationToken).ConfigureAwait(false); return CommandResult.Ok($"{a.State}|{a.Reason}"); });
        host.Commands.Register("start", "cmd.start", async ctx => CommandResult.Ok(Describe(await verbs.StartAsync(ReadPlan(), ctx.CancellationToken).ConfigureAwait(false))));
        host.Commands.Register("end", "cmd.end", async ctx => CommandResult.Ok(Describe(await verbs.EndAsync(ctx.Args[0], ctx.CancellationToken).ConfigureAwait(false))));
        host.Commands.Register("revoke", "cmd.revoke", ctx => { verbs.Revoke(ctx.Args.Count > 0 ? ctx.Args[0] : ReadPlan().Id); return CommandResult.Ok("revoked"); });
        host.Commands.Register("runs", "cmd.runs", _ => CommandResult.Ok(string.Join(";", verbs.ActiveRuns().Select(r => $"{r.RunId}|{r.PlanId}|interrupted={r.Interrupted}|undoFailed={r.UndoFailed}|{string.Join(" / ", r.Steps)}"))));
        host.Commands.Register("approved", "cmd.approved", _ => CommandResult.Ok(string.Join(";", verbs.ApprovedPlans().Select(p => p.PlanId + ":" + string.Join(" / ", p.Lines)))));
    }

    private void InitMetrics(IHostServices host)
    {
        host.Commands.Register("bg", "cmd.bg", ctx =>
        {
            var id = new MetricId(ctx.Args[0]);
            var seconds = double.Parse(ctx.Args[1], CultureInfo.InvariantCulture);
            _background.Add(host.Metrics.SubscribeBackground(id, TimeSpan.FromSeconds(seconds), s => { lock (_backgroundSamples) _backgroundSamples.Add($"{s.Id}={s.Value}"); }));
            return CommandResult.Ok("background " + _background.Count);
        });
        host.Commands.Register("bg-stop", "cmd.bg-stop", _ => { foreach (var b in _background) b.Dispose(); _background.Clear(); return CommandResult.Ok("stopped"); });
        host.Commands.Register("bg-samples", "cmd.bg-samples", _ => { lock (_backgroundSamples) return CommandResult.Ok(string.Join(";", _backgroundSamples)); });
        host.Commands.Register("bg-interval", "cmd.bg-interval", ctx =>
        {
            var i = host.Metrics.BackgroundInterval(new MetricId(ctx.Args[0]));
            return CommandResult.Ok(i is null ? "none" : i.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        });
        host.Commands.Register("subs", "cmd.subs", ctx => CommandResult.Ok("subs=" + host.Metrics.SubscriberCount(new MetricId(ctx.Args[0]))));
        host.Commands.Register("poll", "cmd.poll", ctx =>
        {
            // what a publisher built for 0.1.4 would decide: fast 2 s, idle 60 s
            var ids = ctx.Args.Select(a => new MetricId(a)).ToList();
            var d = host.Evaluate(ids, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
            return CommandResult.Ok($"{d.Kind}|{d.Interval.TotalSeconds.ToString(CultureInfo.InvariantCulture)}");
        });
        host.Commands.Register("publish", "cmd.publish", ctx =>
        {
            host.Metrics.Publish(new MetricId(ctx.Args[0]), double.Parse(ctx.Args[1], CultureInfo.InvariantCulture), MetricUnit.Celsius);
            return CommandResult.Ok("published");
        });
        host.Commands.Register("listen", "cmd.listen", ctx => { host.Metrics.Subscribe(new MetricId(ctx.Args[0]), _ => { }); return CommandResult.Ok("listening"); });
    }

    public PageModel? GetPageModel(IHostServices host) => null;
}
