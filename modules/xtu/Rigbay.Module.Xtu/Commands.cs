using System.Text;
using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

/// <summary>
/// <c>rigbay xtu status | list | apply &lt;name&gt;</c>. The output of <c>status</c> has the same lines as
/// <c>xtu-switcher --status</c> on purpose, so the two can be compared; exit codes as in the standalone app:
/// 0 ok, 1 failed, 2 profile not found or bad usage.
/// </summary>
internal sealed class XtuCommands
{
    private readonly IXtuClient _client;
    private readonly ISystemProbe _sys;
    private readonly ApplyService _apply;
    private readonly ILocalisation _loc;
    private readonly Func<IReadOnlyList<XtuProfile>> _profiles;

    public XtuCommands(IXtuClient client, ISystemProbe sys, ApplyService apply, ILocalisation loc, Func<IReadOnlyList<XtuProfile>> profiles)
    {
        _client = client;
        _sys = sys;
        _apply = apply;
        _loc = loc;
        _profiles = profiles;
    }

    public void Register(ICommandRegistry registry)
    {
        registry.Register("status", "cmd.status", _ => Status());
        registry.Register("list", "cmd.list", _ => List());
        registry.Register("apply", "cmd.apply", ctx => ApplyAsync(ctx.Args, ctx.CancellationToken));
    }

    internal CommandResult Status()
    {
        var sb = new StringBuilder();
        XtuState? state = null;
        string error = "";
        try { state = _client.ReadState(); }
        catch (XtuException ex) { error = _loc.Resolve(ex.ToText()); }

        var profiles = BuildProfiles(state);
        var active = Catalog.FindActive(profiles, state);

        sb.AppendLine("service: " + ServiceText(_sys.Service(XtuPaths.ServiceName)));
        sb.AppendLine("connected: " + (state is not null) + (error.Length > 0 ? " (" + error + ")" : ""));
        if (state is not null)
        {
            sb.AppendLine($"active: PL1={Fmt.N(state.Pl1)} PL2={Fmt.N(state.Pl2)} pl2enable={state.Pl2Enable} window={Fmt.N(state.Window)}");
            sb.AppendLine("matching profile: " + (active?.Name ?? "(none)"));
            sb.AppendLine($"restore after reboot: {state.RestoreOnBoot}   fast startup: {_sys.FastStartup()}");
        }
        foreach (var p in profiles) sb.AppendLine(ProfileLine(p, active));
        // Exit code 0 even when XTU cannot be read, as `xtu-switcher --status` does: the "connected:" line says it.
        return CommandResult.Ok(sb.ToString().TrimEnd());
    }

    internal CommandResult List()
    {
        XtuState? state = null;
        try { state = _client.ReadState(); }
        catch (XtuException) { }
        var profiles = BuildProfiles(state);
        var active = Catalog.FindActive(profiles, state);
        return CommandResult.Ok(string.Join(Environment.NewLine, profiles.Select(p => ProfileLine(p, active))));
    }

    internal async Task<CommandResult> ApplyAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var name = string.Join(' ', args).Trim();
        if (name.Length == 0) return CommandResult.Usage(_loc.T("cmd.apply-usage"));

        var target = Catalog.FindByName(_profiles(), name);
        if (target is null) return CommandResult.Usage("apply: profile not found: " + name);

        var result = await _apply.ApplyAsync(target, ApplyOrigin.Command, ct).ConfigureAwait(false);
        var head = $"apply [{target.Name}] {target.Describe()}" + Environment.NewLine;
        switch (result.Status)
        {
            case ApplyStatus.Applied:
                return CommandResult.Ok(head + "result: " + _loc.F("msg.applied", Fmt.N(result.After!.Pl1), Fmt.N(result.After.Pl2), Fmt.N(result.After.Window)));
            case ApplyStatus.AlreadyActive:
                return CommandResult.Ok(head + "result: " + _loc.T("msg.already"));
            case ApplyStatus.Declined:
                return CommandResult.Failed(head + "apply failed: " + _loc.T("msg.declined"));
            case ApplyStatus.Busy:
                return CommandResult.Failed(head + "apply failed: " + _loc.T("msg.busy"));
            default:
                return CommandResult.Failed(head + "apply failed: " + _loc.Resolve(result.Error!.ToText()));
        }
    }

    private List<XtuProfile> BuildProfiles(XtuState? state)
    {
        // The module's own list (XTU's profiles + custom ones) keeps its order; the "Default (BIOS)" entry is rebuilt from
        // the state just read so the defaults are current.
        var list = _profiles().Where(p => p.Kind != ProfileKind.Builtin).ToList();
        list.Insert(0, Catalog.Build(state, Array.Empty<XtuProfile>(), Array.Empty<XtuProfile>())[0]);
        return list;
    }

    private static string ProfileLine(XtuProfile p, XtuProfile? active) =>
        "profile: " + p.Name + "  " + p.Describe() + (ReferenceEquals(p, active) || (active is not null && p.Name == active.Name) ? "   <== active" : "");

    private static string ServiceText(ServiceState s) => s switch
    {
        ServiceState.NotFound => "NotFound",
        ServiceState.Stopped => "Stopped",
        ServiceState.Running => "Running",
        _ => "Other",
    };
}
