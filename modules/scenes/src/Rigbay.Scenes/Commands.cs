using Rigbay.Abstractions;

namespace Rigbay.Scenes;

internal sealed partial class ScenesRuntime
{
    /// <summary>
    /// <c>rigbay scenes list|status</c>. Both only read: they say what the scenes are and what runs. Starting, stopping and approving a scene is done on the page (the approval is a
    /// question to the person at the window), and a scene runs only from its game, so there is no verb that runs one.
    /// </summary>
    private void RegisterCommands()
    {
        _host.Commands.Register(new CommandInfo("list", "cmd.list") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.list.usage" }, _ => Task.FromResult(CommandResult.Ok(ListText())));
        _host.Commands.Register(new CommandInfo("status", "cmd.status") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.status.usage" }, _ => Task.FromResult(CommandResult.Ok(StatusText())));
    }

    internal string ListText()
    {
        var loc = _host.Localisation;
        var scenes = Snapshot();
        if (scenes.Count == 0) return loc.T("cmd.list.none");
        return string.Join(Environment.NewLine, scenes.Select(s =>
            loc.F("cmd.list.line", s.Id, s.Name, loc.Resolve(LineOf(s, State(s.Id))), string.Join(", ", s.Games), s.Steps.Count)));
    }

    internal string StatusText()
    {
        var loc = _host.Localisation;
        var scenes = Snapshot();
        int armed = scenes.Count(s => IsArmed(s));
        var lines = new List<string> { loc.F("cmd.status.head", scenes.Count, armed, Polling ? loc.T("cmd.status.polling") : loc.T("cmd.status.not-polling")) };
        foreach (var s in scenes)
        {
            var st = State(s.Id);
            if (st.RunId is null) continue;
            lines.Add(loc.F("cmd.status.run", s.Name, Clock(st.RunStarted), st.Manual ? loc.T("cmd.status.manual") : st.RunGame, st.RunId));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
