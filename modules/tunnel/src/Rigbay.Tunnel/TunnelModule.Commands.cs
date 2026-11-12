using System.Text;
using Rigbay.Abstractions;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel;

public sealed partial class TunnelModule
{
    private void RegisterCommands(IHostServices host)
    {
        host.Commands.Register("list", "cmd.list", ListCommand);
        host.Commands.Register("status", "cmd.status", StatusCommandAsync);
        host.Commands.Register("on", "cmd.on", ctx => ChangeCommandAsync(ctx, wanted: true));
        host.Commands.Register("off", "cmd.off", ctx => ChangeCommandAsync(ctx, wanted: false));
    }

    // ------------------------------------------------------------------ rigbay tunnel list

    private CommandResult ListCommand(CommandContext ctx)
    {
        var host = _host;
        var manager = _manager;
        if (host is null || manager is null) return CommandResult.Failed("");
        var loc = host.Localisation;
        if (ctx.Args.Count != 0) return CommandResult.Usage(loc.T("cmd.list-usage"));
        var list = manager.List();
        if (!list.StoreReadable) return CommandResult.Failed(loc.T("err.store-unreadable"));
        if (list.Tunnels.Count == 0) return CommandResult.Ok(loc.T("cmd.no-tunnels"));
        var text = new StringBuilder();
        foreach (var t in list.Tunnels)
            text.AppendLine(loc.F("cmd.list-line", t.Name, loc.T(t.IsOn ? "word.on" : "word.off")));
        return CommandResult.Ok(text.ToString().TrimEnd());
    }

    // ------------------------------------------------------------------ rigbay tunnel status [name]

    private async Task<CommandResult> StatusCommandAsync(CommandContext ctx)
    {
        var host = _host;
        var manager = _manager;
        if (host is null || manager is null) return CommandResult.Failed("");
        var loc = host.Localisation;
        if (ctx.Args.Count > 1) return CommandResult.Usage(loc.T("cmd.status-usage"));

        var list = manager.List();
        if (!list.StoreReadable) return CommandResult.Failed(loc.T("err.store-unreadable"));
        IEnumerable<TunnelInfo> shown = list.Tunnels;
        if (ctx.Args.Count == 1)
        {
            shown = list.Tunnels.Where(t => string.Equals(t.Name, ctx.Args[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (!shown.Any()) return CommandResult.Failed(loc.F("err.unknown", ctx.Args[0]));
        }
        if (!shown.Any()) return CommandResult.Ok(loc.T("cmd.no-tunnels"));

        var text = new StringBuilder();
        var now = _deps!.Clock.Now;
        foreach (var t in shown)
        {
            WgSummary? last = null;
            if (t.IsOn && await manager.ReadAsync(t.Name, ctx.CancellationToken).ConfigureAwait(false) is { } r)
                last = new WgSummary(r.LatestHandshake, r.RxBytes, r.TxBytes, r.Endpoint);
            text.AppendLine(loc.F("cmd.status-line", t.Name, StatusText(loc, t.IsOn, last, now)));
            if (last is { Endpoint: { } endpoint }) text.AppendLine("  " + loc.F("cmd.status-endpoint", endpoint));
            if (last is { Rx: { } rx, Tx: { } tx }) text.AppendLine("  " + loc.F("cmd.status-transfer", Size(rx), Size(tx)));
            var port = OptionsFor(t.Name, create: false)?.CheckPort ?? 0;
            if (port > 0) text.AppendLine("  " + LocalText(loc, manager.CheckPort(t.Name, port, t.IsOn), port));
        }
        return CommandResult.Ok(text.ToString().TrimEnd());
    }

    // ------------------------------------------------------------------ rigbay tunnel on|off <name>

    private async Task<CommandResult> ChangeCommandAsync(CommandContext ctx, bool wanted)
    {
        var host = _host;
        if (host is null) return CommandResult.Failed("");
        var loc = host.Localisation;
        if (ctx.Args.Count != 1) return CommandResult.Usage(loc.T(wanted ? "cmd.on-usage" : "cmd.off-usage"));

        var outcome = await RequestChangeAsync(ctx.Args[0], wanted, Origin.Verb, ctx.CancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case RequestStatus.NotRunning: return CommandResult.Failed(loc.T("cmd.not-running"));
            case RequestStatus.UnknownTunnel: return CommandResult.Failed(loc.F("err.unknown", ctx.Args[0]));
            case RequestStatus.AlreadyThere: return CommandResult.Ok(loc.F(wanted ? "cmd.already-on" : "cmd.already-off", outcome.Name));
            case RequestStatus.Busy: return CommandResult.Failed(loc.T("cmd.busy"));
            case RequestStatus.Declined: return CommandResult.Failed(loc.T("cmd.not-confirmed"));
            case RequestStatus.Failed or RequestStatus.Unusable:
                return CommandResult.Failed(loc.F(outcome.Problem!.Key, outcome.Problem.Args));
        }

        if (!wanted) return CommandResult.Ok(loc.F("cmd.turned-off", outcome.Name));
        return outcome.Handshake switch
        {
            HandshakeOutcome.Connected => CommandResult.Ok(loc.F("cmd.turned-on-connected", outcome.Name)),
            HandshakeOutcome.NoHandshake => CommandResult.Failed(loc.F("err.no-handshake", outcome.Name)),
            HandshakeOutcome.TurnedOff => CommandResult.Failed(loc.F("cmd.turned-off-meanwhile", outcome.Name)),
            _ => CommandResult.Ok(loc.F("cmd.turned-on", outcome.Name)),
        };
    }
}
