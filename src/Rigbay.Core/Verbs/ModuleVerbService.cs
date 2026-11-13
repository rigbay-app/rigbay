using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>The <see cref="IVerbService"/> of one calling module (<see cref="IHostServices.Verbs"/>).</summary>
internal sealed class ModuleVerbService : IVerbService
{
    private readonly RigbayHost _host;
    private readonly ModuleEntry _entry;
    private readonly object _gate = new();
    private readonly List<(Action Original, IDisposable Hook)> _hooks = new();

    public ModuleVerbService(RigbayHost host, ModuleEntry entry)
    {
        _host = host;
        _entry = entry;
    }

    public IReadOnlyList<VerbInfo> List()
    {
        var list = new List<VerbInfo>();
        foreach (var module in _host.Modules.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (module.Lifecycle != ModuleLifecycle.Running || string.Equals(module.Id, _entry.Id, StringComparison.Ordinal)) continue;
            foreach (var cmd in _host.Commands.VerbsOf(module.Id))
            {
                var info = cmd.Info;
                list.Add(new VerbInfo(module.Id, module.DisplayName, cmd.Verb, module.Localisation.T(cmd.DescriptionKey),
                    info?.UsageKey is { } u ? module.Localisation.T(u) : null,
                    cmd.Access, info?.Effect is { } e ? module.Localisation.T(e.DescriptionKey) : null, info?.Effect?.Kind ?? EffectKind.Hardware,
                    info?.UndoVerb, info?.Preview is not null));
            }
        }
        return list;
    }

    public event Action? Changed
    {
        add
        {
            if (value is null) return;
            var guarded = _entry.Guard("Verbs.Changed handler", value);
            var hook = _host.SubscribeVerbsChanged(guarded);
            lock (_gate) _hooks.Add((value, hook));
            _entry.TrackSubscription(hook);
        }
        remove
        {
            if (value is null) return;
            IDisposable? hook = null;
            lock (_gate)
            {
                var i = _hooks.FindIndex(h => h.Original == value);
                if (i < 0) return;
                hook = _hooks[i].Hook;
                _hooks.RemoveAt(i);
            }
            hook.Dispose();
        }
    }

    public async Task<VerbResult> InvokeAsync(string moduleId, string verb, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (_entry.Lifecycle != ModuleLifecycle.Running) return new VerbResult(VerbOutcome.Cancelled, -1, "the calling module is not running");
        if (CallScope.InsideOpenCall) return new VerbResult(VerbOutcome.Refused, -1, "a verb that the host started for another module cannot start a call");
        if (string.IsNullOrEmpty(moduleId) || string.IsNullOrEmpty(verb) || args is null) return new VerbResult(VerbOutcome.Refused, -1, "module, verb and arguments are required");
        if (string.Equals(moduleId, _entry.Id, StringComparison.Ordinal)) return new VerbResult(VerbOutcome.Refused, -1, "a module cannot call its own verbs through the host");
        if (args.Count > PlanEngine.MaxArgs || args.Any(a => a is null || a.Length > PlanEngine.MaxArgLength))
            return new VerbResult(VerbOutcome.Refused, -1, $"at most {PlanEngine.MaxArgs} arguments of {PlanEngine.MaxArgLength} characters");

        var target = _host.Find(moduleId);
        if (target is null) return new VerbResult(VerbOutcome.NotFound, -1, $"module '{moduleId}' is not installed");
        if (target.Lifecycle != ModuleLifecycle.Running) return new VerbResult(VerbOutcome.NotRunning, -1, $"module '{moduleId}' is not running");
        var cmd = _host.Commands.Find(moduleId, verb);
        if (cmd is null) return new VerbResult(VerbOutcome.NotFound, -1, $"module '{moduleId}' has no verb '{verb}'");
        if (cmd.Access != VerbAccess.ReadOnly)
            return new VerbResult(VerbOutcome.NeedsPlan, -1, $"{moduleId} {verb} is not declared read-only; it runs only inside a plan the owner approved");

        var result = await VerbInvoker.RunAsync(_host, _entry, target, cmd, args, new CallScope(target.Id, isPlanStep: false), _host.Options.VerbTimeout, cancellationToken).ConfigureAwait(false);
        return new VerbResult(result.Outcome, result.ExitCode, result.Output);
    }

    public PlanApproval GetApproval(VerbPlan plan) => _host.Plans.GetApprovalAsync(_entry, plan).GetAwaiter().GetResult();

    public Task<PlanApproval> RequestApprovalAsync(VerbPlan plan, CancellationToken cancellationToken = default) =>
        _host.Plans.RequestApprovalAsync(_entry, plan, cancellationToken);

    public void Revoke(string planId) => _host.Plans.Revoke(_entry.Id, planId);

    public IReadOnlyList<ApprovedPlanInfo> ApprovedPlans() => _host.Plans.ApprovedPlans(_entry.Id);

    public Task<PlanRun> StartAsync(VerbPlan plan, CancellationToken cancellationToken = default) => _host.Plans.StartAsync(_entry, plan, cancellationToken);

    public Task<PlanRun> EndAsync(string runId, CancellationToken cancellationToken = default) => _host.Plans.EndAsync(_entry, runId, cancellationToken);

    public IReadOnlyList<ActiveRun> ActiveRuns() => _host.Plans.ActiveRuns(_entry.Id);
}
