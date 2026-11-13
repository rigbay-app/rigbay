using System.Text;
using Rigbay.Abstractions;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel;

public sealed partial class TunnelModule
{
    private void RegisterCommands(IHostServices host)
    {
        // Host API 0.1.4: say what each verb does. list and status only read (service states, wg.exe show, a local port check). on and off start or remove the Windows
        // service of a WireGuard tunnel (a system change, not hardware). The effect text is generic here (the dialog of a plan names the tunnel in the command line and in the
        // preview); the question the verb asks itself names the tunnel and has the same kind, which is what the host matches.
        host.Commands.Register(new CommandInfo("list", "cmd.list") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.list-usage" }, ListCommand);
        host.Commands.Register(new CommandInfo("status", "cmd.status") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.status-usage" }, StatusCommandAsync);
        host.Commands.Register(new CommandInfo("on", "cmd.on")
        {
            Access = VerbAccess.Writes,
            Effect = new HardwareEffect("effect.verb-on") { Kind = EffectKind.SystemSetting },
            UsageKey = "cmd.on-usage",
            Preview = args => PreviewChange(args, "preview.turn-on"),
            UndoVerb = "off",
        }, ctx => ChangeCommandAsync(ctx, wanted: true));
        host.Commands.Register(new CommandInfo("off", "cmd.off")
        {
            Access = VerbAccess.Writes,
            Effect = new HardwareEffect("effect.verb-off") { Kind = EffectKind.SystemSetting },
            UsageKey = "cmd.off-usage",
            Preview = args => PreviewChange(args, "preview.turn-off"),
            UndoVerb = "on",
        }, ctx => ChangeCommandAsync(ctx, wanted: false));
    }

    /// <summary>"Turn on tunnel Home": the tunnel the argument names. A pure function of the argument; whether it is on now is not looked at.</summary>
    internal static LocalisedText? PreviewChange(IReadOnlyList<string> args, string key) =>
        args.Count == 1 && args[0].Trim().Length > 0 ? LocalisedText.Of(key, args[0].Trim()) : null;

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
            // One line for the log that names nothing (no tunnel name, server or key): what was read, so "the status verb said X" can be checked later.
            host.Log.Info("status read: on=" + t.IsOn + ", handshake age " + (last?.Handshake is { } h ? (long)(now - h).TotalSeconds + " s" : "none") + ", received " + (last?.Rx?.ToString() ?? "-") + ", sent " + (last?.Tx?.ToString() ?? "-"));
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

        // A run that changed the tunnel reports the way back: the opposite verb with the tunnel's name. A run that found it already there changed nothing and reports none.
        var undo = new[] { outcome.Name };
        if (!wanted) return CommandResult.Ok(loc.F("cmd.turned-off", outcome.Name)) with { UndoArgs = undo };
        return outcome.Handshake switch
        {
            HandshakeOutcome.Connected => CommandResult.Ok(loc.F("cmd.turned-on-connected", outcome.Name)) with { UndoArgs = undo },
            HandshakeOutcome.NoHandshake => CommandResult.Failed(loc.F("err.no-handshake", outcome.Name)),
            HandshakeOutcome.TurnedOff => CommandResult.Failed(loc.F("cmd.turned-off-meanwhile", outcome.Name)),
            _ => CommandResult.Ok(loc.F("cmd.turned-on", outcome.Name)) with { UndoArgs = undo },
        };
    }
}
