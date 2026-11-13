using System.Globalization;
using System.Text;
using Rigbay.Abstractions;

namespace Rigbay.Watch;

public sealed partial class WatchModule
{
    /// <summary>
    /// <c>rigbay watch status|pause &lt;minutes&gt;|resume</c>. All three are declared <see cref="VerbAccess.ReadOnly"/>: <c>pause</c> and <c>resume</c> change only
    /// Watch's own state (whether it sends notifications), nothing on the machine, so another module may call them without an approved plan.
    /// </summary>
    private void RegisterCommands(IHostServices host)
    {
        host.Commands.Register(new CommandInfo("status", "cmd.status") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.status.usage" }, StatusCommand);
        host.Commands.Register(new CommandInfo("pause", "cmd.pause") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.pause.usage" }, PauseCommand);
        host.Commands.Register(new CommandInfo("resume", "cmd.resume") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.resume.usage" }, ResumeCommand);
    }

    private CommandResult StatusCommand(CommandContext ctx)
    {
        var host = _host;
        var engine = _engine;
        if (host is null || engine is null) return CommandResult.Failed("");
        var loc = host.Localisation;
        if (ctx.Args.Count != 0) return CommandResult.Usage(loc.T("cmd.status.usage"));
        var text = StatusText(host, engine);
        host.Log.Info("status: " + text.ReplaceLineEndings(" | "));   // so a run without a terminal (the app's test script) leaves what the verb said
        return CommandResult.Ok(text);
    }

    /// <summary>One line for the pause, then one per rule: the metric id, then what the page shows.</summary>
    internal string StatusText(IHostServices host, RuleEngine engine)
    {
        var loc = host.Localisation;
        var now = _deps!.Now();
        var text = new StringBuilder();
        text.AppendLine(PausedUntil is { } until ? loc.F("pause.until", RuleText.When(until, now, loc)) : loc.T("status.alerts-on"));
        var rules = Rules;
        if (rules.Count == 0) text.AppendLine(loc.T("state.no-rules"));
        foreach (var rule in rules)
            text.AppendLine(rule.Metric + ": " + RuleText.Line(rule, engine.View(rule.Id), SummaryOf(host, rule), UnitOf(host, rule.Metric), now, loc));
        return text.ToString().TrimEnd();
    }

    private CommandResult PauseCommand(CommandContext ctx)
    {
        var host = _host;
        if (host is null) return CommandResult.Failed("");
        var loc = host.Localisation;
        if (ctx.Args.Count != 1
            || !int.TryParse(ctx.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || minutes < 1 || minutes > MaxPauseMinutes)
            return CommandResult.Usage(loc.T("cmd.pause.usage"));
        Pause(TimeSpan.FromMinutes(minutes));
        var until = PausedUntil;
        return CommandResult.Ok(until is { } u ? loc.F("pause.until", RuleText.When(u, _deps!.Now(), loc)) : loc.T("status.alerts-on"));
    }

    private CommandResult ResumeCommand(CommandContext ctx)
    {
        var host = _host;
        if (host is null) return CommandResult.Failed("");
        var loc = host.Localisation;
        if (ctx.Args.Count != 0) return CommandResult.Usage(loc.T("cmd.resume.usage"));
        Resume();
        return CommandResult.Ok(loc.T("status.alerts-on"));
    }
}
