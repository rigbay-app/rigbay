using System.Text;
using Rigbay.Abstractions;
using Rigbay.PredatorSense.Fan;
using Rigbay.PredatorSense.Sensors;

namespace Rigbay.PredatorSense;

public sealed partial class PredatorSenseModule
{
    /// <summary>How a change that needed the user's approval ended.</summary>
    internal enum ChangeStatus
    {
        /// <summary>The module is not running (nothing to write with).</summary>
        NotRunning,
        /// <summary>The readings are not available, so no write was offered.</summary>
        NoReadings,
        /// <summary>The user said no, or the host has no way to ask (no window).</summary>
        Declined,
        /// <summary>The write was tried; see the result.</summary>
        Done,
    }

    /// <param name="Status">How the request ended.</param>
    /// <param name="Result">The write and its check, when one was made.</param>
    /// <param name="Before">The mode Rigbay had set before this write (null when it had set nothing); only for <see cref="ChangeStatus.Done"/>.</param>
    internal sealed record ChangeAnswer(ChangeStatus Status, FanResult? Result, FanMode? Before = null);

    private void RegisterCommands(IHostServices host)
    {
        // Host API 0.1.4: say what each verb does. status only reads (the sensors and the two performance-mode reads). fan writes the fan mode (hardware, the same
        // effect the page and the hotkey describe); its preview is the mode the argument names, nothing from the machine's state.
        host.Commands.Register(new CommandInfo("status", "cmd.status") { Access = VerbAccess.ReadOnly }, _ => StatusCommand(host));
        host.Commands.Register(new CommandInfo("fan", "cmd.fan")
        {
            Access = VerbAccess.Writes,
            Effect = new HardwareEffect("effect.fan-mode"),
            UsageKey = "cmd.fan-usage",
            Preview = PreviewFan,
            UndoVerb = "fan",
        }, ctx => FanCommandAsync(host, ctx));
    }

    /// <summary>The mode an argument names: <c>auto</c>, or <c>max</c>/<c>maximum</c>, nothing else.</summary>
    internal static bool TryParseMode(IReadOnlyList<string> args, out FanMode mode)
    {
        mode = FanMode.Auto;
        if (args.Count != 1) return false;
        if (args[0].Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
        if (args[0].Equals("max", StringComparison.OrdinalIgnoreCase) || args[0].Equals("maximum", StringComparison.OrdinalIgnoreCase)) { mode = FanMode.Max; return true; }
        return false;
    }

    /// <summary>"Fan mode: Maximum (all fans at full speed)": what the argument selects. A pure function of the argument; the fan state now is not looked at.</summary>
    internal static LocalisedText? PreviewFan(IReadOnlyList<string> args) =>
        TryParseMode(args, out var mode) ? LocalisedText.Of(mode == FanMode.Max ? "preview.fan-max" : "preview.fan-auto") : null;

    // ------------------------------------------------------------------ rigbay predatorsense status

    private CommandResult StatusCommand(IHostServices host)
    {
        var poller = _poller;
        if (poller is null) return CommandResult.Failed(host.Localisation.T("cmd.not-running"));
        var loc = host.Localisation;

        // A fresh read, so the numbers are the machine's now and not the last timer tick's.
        var snapshot = poller.PollOnce();
        var text = new StringBuilder();
        text.AppendLine(loc.F("status.model", string.IsNullOrWhiteSpace(_modelName) ? loc.T("model.unknown") : _modelName!));
        foreach (var info in SensorInfo.All)
        {
            string label = loc.T(SensorLabelKey(info.Sensor));
            string value = snapshot[info.Sensor] is { } v
                ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " " + info.Unit.Symbol()
                : loc.T("status.not-available");
            text.AppendLine(loc.F("status.line", label, value));
        }
        // The performance mode is read on demand too (read only).
        if (_opMode is { } opMode) text.AppendLine(loc.F("status.opmode", OpModeText(loc, opMode.ReadCurrent())));
        text.Append(loc.F("status.fan-mode", LastSetText(loc, _fan?.Last)));
        return snapshot.AnyAnswer ? CommandResult.Ok(text.ToString()) : CommandResult.Failed(text.ToString());
    }

    // ------------------------------------------------------------------ rigbay predatorsense fan auto|max

    private async Task<CommandResult> FanCommandAsync(IHostServices host, CommandContext ctx)
    {
        var loc = host.Localisation;
        if (!TryParseMode(ctx.Args, out var mode)) return CommandResult.Usage(loc.T("cmd.fan-usage"));

        var answer = await ChangeWithApprovalAsync(mode, ctx.CancellationToken).ConfigureAwait(false);
        switch (answer.Status)
        {
            case ChangeStatus.NotRunning: return CommandResult.Failed(loc.T("cmd.not-running"));
            case ChangeStatus.NoReadings: return CommandResult.Failed(loc.T("cmd.no-readings"));
            case ChangeStatus.Declined: return CommandResult.Failed(loc.T("cmd.not-confirmed"));
        }

        var result = answer.Result!;
        string line = CheckText(loc, result);
        if (!result.Applied) return CommandResult.Failed(line);
        // What puts it back: the mode Rigbay had set before, or Auto when it had set nothing (the firmware is in charge then, which is what Auto hands back, and what the
        // module itself does when it stops). Nothing to report when the mode did not change.
        var before = answer.Before ?? FanMode.Auto;
        return CommandResult.Ok(loc.F("cmd.fan-done", ModeLabel(loc, mode), line))
            with { UndoArgs = before == mode ? null : new[] { before == FanMode.Max ? "max" : "auto" } };
    }

    // ------------------------------------------------------------------ hotkey

    /// <summary>The hotkey toggles Maximum and Auto, asking for approval each time like every other way in.</summary>
    private async Task OnHotkeyAsync()
    {
        var host = _host;
        var fan = _fan;
        if (host is null || fan is null) return;
        try
        {
            var next = fan.MaxIsSet ? FanMode.Auto : FanMode.Max;
            // The result is announced inside ChangeWithApprovalAsync (hotkey and verb alike), so it is told once.
            await ChangeWithApprovalAsync(next, host.StopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A task nobody awaits would swallow this silently: log it.
            host.Log.Error("The fan hotkey failed", ex);
        }
    }

    // ------------------------------------------------------------------ the shared path for hotkey and verb

    /// <summary>
    /// Asks the user through the host's approval service, and only on a yes makes the one write. A declined or unanswerable
    /// question (no window, command-line run) makes no call at all.
    /// </summary>
    internal async Task<ChangeAnswer> ChangeWithApprovalAsync(FanMode mode, CancellationToken ct)
    {
        var host = _host;
        var fan = _fan;
        if (host is null || fan is null) return new ChangeAnswer(ChangeStatus.NotRunning, null);
        if (!fan.CanWrite(mode))
        {
            // A refusal before the question: the user pressed a key or ran a verb and nothing happens, so say why.
            Notify(host, LocalisedText.Of("check.refused-readings"), ToastKind.Warning, ToastOrigin.Background);
            return new ChangeAnswer(ChangeStatus.NoReadings, null);
        }

        var loc = host.Localisation;
        var detail = fan.Last is { } last
            ? LocalisedText.Of("detail.change", ModeLabel(loc, last.Mode), ModeLabel(loc, mode))
            : LocalisedText.Of("detail.set", ModeLabel(loc, mode));
        bool approved = await host.Approval.ConfirmAsync(new HardwareEffect("effect.fan-mode"), detail, ct).ConfigureAwait(false);
        if (!approved)
        {
            host.Log.Info($"Fan {mode}: the user did not approve; nothing was sent.");
            return new ChangeAnswer(ChangeStatus.Declined, null);
        }

        var before = fan.Last?.Mode;
        var result = await fan.ApplyAsync(mode, ct).ConfigureAwait(false);
        AnnounceFan(host, mode, result, ToastOrigin.Background);   // before the page shows it, so a reader of the page sees the toast already
        ShowResult(result);
        SyncChoice();
        RefreshTexts();
        return new ChangeAnswer(ChangeStatus.Done, result, before);
    }

    // ------------------------------------------------------------------ telling the user

    /// <summary>
    /// Says how a fan change ended, by the rule in docs/APP.md ("Notifications"): once per change, always for a failure or a
    /// refusal, and the speed-check result in the same message. Where it appears (a message in the window or a Windows toast) is
    /// the host's decision from <paramref name="origin"/>. A "no" from the user is not announced.
    /// </summary>
    internal static void AnnounceFan(IHostServices host, FanMode mode, FanResult result, ToastOrigin origin)
    {
        var loc = host.Localisation;
        string check = CheckText(loc, result);
        if (result.Applied)
        {
            // The firmware accepted it, but the fans did not follow within the check window: that is worth a toast even from the page.
            bool unseen = result.Verdict is RpmVerdict.NotRose or RpmVerdict.NotFell;
            Notify(host, LocalisedText.Of("toast.fan-set", ModeLabel(loc, mode), check), unseen ? ToastKind.Warning : ToastKind.Success, origin);
        }
        else if (result.Outcome == FanOutcome.Unknown)
            Notify(host, LocalisedText.Of("toast.fan-unconfirmed", ModeLabel(loc, mode), check), ToastKind.Warning, origin);
        else
            Notify(host, LocalisedText.Of("toast.fan-failed", check), ToastKind.Warning, origin);
    }

    private static void Notify(IHostServices host, LocalisedText body, ToastKind kind, ToastOrigin origin)
        => host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.fan-title"), body, kind) { Origin = origin });
}
