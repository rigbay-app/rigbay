using System.Text;
using Rigbay.Abstractions;
using Rigbay.SteamBypass.Check;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass;

public sealed partial class SteamBypassModule
{
    /// <summary>How a change that needed the user's approval ended.</summary>
    internal enum ChangeStatus
    {
        /// <summary>The module is not running.</summary>
        NotRunning,
        /// <summary>The bypass already is in the wanted state: nothing to ask, nothing to do.</summary>
        AlreadyThere,
        /// <summary>The user said no, or the host has no way to ask (no window).</summary>
        Declined,
        /// <summary>The change was tried; see the results.</summary>
        Done,
        /// <summary>Another change (the switch, another verb, auto-on) is running: this one was refused and nothing was changed.</summary>
        Busy,
    }

    internal sealed record ChangeAnswer(ChangeStatus Status, TurnOnResult? On, TurnOffResult? Off);

    private void RegisterCommands(IHostServices host)
    {
        // Host API 0.1.4: say what each verb does. status only reads. test runs the four connection checks (it opens connections and changes no setting on this
        // machine, the same as the Test button), so it is read-only. on and off change the Windows proxy setting of the current user (a system setting, not hardware).
        host.Commands.Register(new CommandInfo("status", "cmd.status") { Access = VerbAccess.ReadOnly }, _ => StatusCommand(host));
        host.Commands.Register(new CommandInfo("on", "cmd.on")
        {
            Access = VerbAccess.Writes,
            Effect = BypassEffect,
            UsageKey = "cmd.on-usage",
            Preview = _ => LocalisedText.Of("preview.on"),
            UndoVerb = "off",
        }, ctx => ChangeCommandAsync(host, ctx, wanted: true));
        host.Commands.Register(new CommandInfo("off", "cmd.off")
        {
            Access = VerbAccess.Writes,
            Effect = BypassEffect,
            UsageKey = "cmd.off-usage",
            Preview = _ => LocalisedText.Of("preview.off"),
            UndoVerb = "on",
        }, ctx => ChangeCommandAsync(host, ctx, wanted: false));
        host.Commands.Register(new CommandInfo("test", "cmd.test") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.test-usage" }, ctx => TestCommandAsync(host, ctx));
    }

    /// <summary>What the owner is told, and the kind of question the verbs ask with (the host answers inside a plan only a question of this kind). One value for the page, the switch and the verbs.</summary>
    internal static readonly HardwareEffect BypassEffect = new("effect.bypass") { Kind = EffectKind.SystemSetting, RevertDescriptionKey = "effect.bypass-revert" };

    // ------------------------------------------------------------------ rigbay steam-bypass status

    private CommandResult StatusCommand(IHostServices host)
    {
        var controller = _controller;
        var engine = _engine;
        if (controller is null || engine is null) return CommandResult.Failed(host.Localisation.T("cmd.not-running"));
        var loc = host.Localisation;
        var c = engine.Counters;
        bool on = controller.IsOn;

        var state = ComputeSteamState();
        var text = new StringBuilder();
        text.AppendLine(on && controller.OnSince is { } since ? loc.F("status.on-since", Clock(since)) : loc.T("status.off"));
        text.AppendLine(loc.F("status.listening", on ? ListenText(loc, engine) : loc.T("listen.off")));
        text.AppendLine(loc.F("status.connections", c.Active, c.Total, c.Failed, c.Rejected));
        text.AppendLine(loc.F("status.steam", SteamText(loc, state)));
        text.Append(loc.F("status.windows", controller.IsApplied ? loc.T("status.windows-ours") : loc.T("status.windows-not-ours")));
        return CommandResult.Ok(text.ToString());
    }

    // ------------------------------------------------------------------ rigbay steam-bypass on | off

    private async Task<CommandResult> ChangeCommandAsync(IHostServices host, CommandContext ctx, bool wanted)
    {
        var loc = host.Localisation;
        if (ctx.Args.Count != 0) return CommandResult.Usage(loc.T(wanted ? "cmd.on-usage" : "cmd.off-usage"));

        var answer = await ChangeWithApprovalAsync(wanted, ctx.CancellationToken).ConfigureAwait(false);
        switch (answer.Status)
        {
            case ChangeStatus.NotRunning: return CommandResult.Failed(loc.T("cmd.not-running"));
            case ChangeStatus.AlreadyThere: return CommandResult.Ok(loc.T(wanted ? "cmd.already-on" : "cmd.already-off"));
            case ChangeStatus.Declined: return CommandResult.Failed(loc.T("cmd.not-confirmed"));
            case ChangeStatus.Busy: return CommandResult.Failed(loc.T("cmd.busy"));
        }

        // A run that changed something reports the way back: the opposite verb, which takes no arguments, so the list is empty (not null). An "already on/off" run
        // changed nothing and reports none, so a plan that finds the bypass on already does not turn it off at its end. This is why the verbs report their undo instead of
        // leaving it to a fixed "steam-bypass off": a fixed undo would also switch off a bypass the owner had on before the game started.
        var undo = Array.Empty<string>();
        if (wanted)
        {
            var on = answer.On!;
            if (!on.Ok) return CommandResult.Failed(ProblemText(loc, on));
            return CommandResult.Ok(loc.T(on.DownloadsAvailable ? "cmd.turned-on" : "cmd.turned-on-no-downloads")) with { UndoArgs = undo };
        }
        var off = answer.Off!;
        return off.Ok ? CommandResult.Ok(loc.T("cmd.turned-off")) with { UndoArgs = undo } : CommandResult.Failed(loc.F("problem.restore", off.Error ?? ""));
    }

    /// <summary>
    /// The path for a verb: ask the user through the host's approval service with the exact effect, and only on a yes change anything.
    /// A declined or unanswerable question (no window, command-line run) changes nothing at all. It takes the same one-change-at-a-time
    /// slot as the switch, and only for the work itself (not while the question is open): when another change is running the answer is
    /// <see cref="ChangeStatus.Busy"/> and nothing was asked or changed.
    /// </summary>
    internal async Task<ChangeAnswer> ChangeWithApprovalAsync(bool wanted, CancellationToken ct)
    {
        var host = _host;
        var controller = _controller;
        if (host is null || controller is null || _stopping) return new ChangeAnswer(ChangeStatus.NotRunning, null, null);
        if (ChangeInProgress) return new ChangeAnswer(ChangeStatus.Busy, null, null);   // do not ask the user about a change that cannot be made now
        if (AlreadyThere(controller, wanted)) return new ChangeAnswer(ChangeStatus.AlreadyThere, null, null);

        var effect = BypassEffect;
        var detail = LocalisedText.Of(wanted ? "detail.turn-on" : "detail.turn-off");
        bool approved = await host.Approval.ConfirmAsync(effect, detail, ct).ConfigureAwait(false);
        if (!approved)
        {
            host.Log.Info($"Bypass {(wanted ? "on" : "off")}: the user did not approve; nothing was changed.");
            return new ChangeAnswer(ChangeStatus.Declined, null, null);
        }

        // The question may have been open for a long time: take the slot now, and look at the state again.
        var ticket = TryBeginChange(wanted, out _);
        if (ticket is null)
        {
            host.Log.Info($"Bypass {(wanted ? "on" : "off")}: approved, but another change is in progress; nothing was changed.");
            return new ChangeAnswer(ChangeStatus.Busy, null, null);
        }

        TurnOnResult? on = null;
        TurnOffResult? off = null;
        try
        {
            if (_stopping || _controller is null) return new ChangeAnswer(ChangeStatus.NotRunning, null, null);
            if (AlreadyThere(controller, wanted)) return new ChangeAnswer(ChangeStatus.AlreadyThere, null, null);
            if (wanted)
            {
                on = await Task.Run(controller.TurnOn, ct).ConfigureAwait(false);
                _problem = on.Ok ? null : ProblemText(host.Localisation, on);
            }
            else
            {
                off = await Task.Run(controller.TurnOff, ct).ConfigureAwait(false);
                _problem = off.Ok ? null : host.Localisation.F("problem.restore", off.Error ?? "");
            }
        }
        finally
        {
            // Whatever happened (done, failed, cancelled, thrown), the switch shows what is real and the slot is given back by its owner.
            try { SyncSwitch(); } catch (Exception) { }
            EndChange(ticket);
            RefreshTexts();
            PublishMetrics();
        }
        return new ChangeAnswer(ChangeStatus.Done, on, off);
    }

    /// <summary>Off also runs when it is off but the last restore failed, so the user can try again.</summary>
    private static bool AlreadyThere(BypassController controller, bool wanted) =>
        wanted == controller.IsOn && (wanted || controller.LastError is null);

    // ------------------------------------------------------------------ rigbay steam-bypass test

    private async Task<CommandResult> TestCommandAsync(IHostServices host, CommandContext ctx)
    {
        var loc = host.Localisation;
        if (ctx.Args.Count != 0) return CommandResult.Usage(loc.T("cmd.test-usage"));
        if (_controller is null) return CommandResult.Failed(loc.T("cmd.not-running"));

        var run = await RunChecksAsync(ctx.CancellationToken).ConfigureAwait(false);
        if (run.Results.Count == 0) return CommandResult.Failed(loc.T("test.busy"));
        var text = new StringBuilder();
        foreach (var spec in CheckSpec.All)
        {
            var result = run.Results.FirstOrDefault(r => r.Id == spec.Id);
            text.AppendLine(loc.F("test.line", loc.T(spec.TitleKey), result is null ? "-" : FormatCheck(loc, result)));
        }
        text.Append(run.Failed == 0 ? loc.F("test.passed", run.Total) : loc.F("test.failed", run.Failed, run.Total));
        return run.Failed == 0 ? CommandResult.Ok(text.ToString()) : CommandResult.Failed(text.ToString());
    }

    private SteamState ComputeSteamState()
    {
        var controller = _controller;
        var steam = _steam;
        if (controller is null || steam is null) return SteamState.NotRunning;
        var processes = steam.Processes();
        bool uses = controller.IsOn && processes.Count > 0 && steam.AnyConnectedTo(processes.Select(p => p.Pid).ToArray(), controller.Engine.ProxyPort);
        return _steamState = SteamLink.Evaluate(controller.IsOn, controller.OnSince, processes, uses, _deps.Now());
    }

    private static string ListenText(ILocalisation loc, ProxyEngine engine) =>
        engine.DownloadsError is null ? loc.F("listen.on", engine.ProxyPort, engine.DownloadPort) : loc.F("listen.on-no-downloads", engine.ProxyPort);
}
