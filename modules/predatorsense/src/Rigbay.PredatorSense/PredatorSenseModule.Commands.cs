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

    internal sealed record ChangeAnswer(ChangeStatus Status, FanResult? Result);

    private void RegisterCommands(IHostServices host)
    {
        host.Commands.Register("status", "cmd.status", _ => StatusCommand(host));
        host.Commands.Register("fan", "cmd.fan", ctx => FanCommandAsync(host, ctx));
    }

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
        text.Append(loc.F("status.fan-mode", LastSetText(loc, _fan?.Last)));
        return snapshot.AnyAnswer ? CommandResult.Ok(text.ToString()) : CommandResult.Failed(text.ToString());
    }

    // ------------------------------------------------------------------ rigbay predatorsense fan auto|max

    private async Task<CommandResult> FanCommandAsync(IHostServices host, CommandContext ctx)
    {
        var loc = host.Localisation;
        FanMode mode;
        if (ctx.Args.Count == 1 && ctx.Args[0].Equals("auto", StringComparison.OrdinalIgnoreCase)) mode = FanMode.Auto;
        else if (ctx.Args.Count == 1 && (ctx.Args[0].Equals("max", StringComparison.OrdinalIgnoreCase) || ctx.Args[0].Equals("maximum", StringComparison.OrdinalIgnoreCase))) mode = FanMode.Max;
        else return CommandResult.Usage(loc.T("cmd.fan-usage"));

        var answer = await ChangeWithApprovalAsync(mode, ctx.CancellationToken).ConfigureAwait(false);
        switch (answer.Status)
        {
            case ChangeStatus.NotRunning: return CommandResult.Failed(loc.T("cmd.not-running"));
            case ChangeStatus.NoReadings: return CommandResult.Failed(loc.T("cmd.no-readings"));
            case ChangeStatus.Declined: return CommandResult.Failed(loc.T("cmd.not-confirmed"));
        }

        var result = answer.Result!;
        string line = CheckText(loc, result);
        return result.Applied
            ? CommandResult.Ok(loc.F("cmd.fan-done", ModeLabel(loc, mode), line))
            : CommandResult.Failed(line);
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
            var answer = await ChangeWithApprovalAsync(next, host.StopToken).ConfigureAwait(false);
            var loc = host.Localisation;
            switch (answer.Status)
            {
                case ChangeStatus.NoReadings:
                    host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.fan-title"), LocalisedText.Of("check.refused-readings"), ToastKind.Warning));
                    break;
                case ChangeStatus.Done when answer.Result!.Applied:
                    host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.fan-title"), LocalisedText.Of("toast.fan-set", ModeLabel(loc, next)), ToastKind.Success));
                    break;
                case ChangeStatus.Done:
                    host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.fan-title"), LocalisedText.Of("toast.fan-failed", CheckText(loc, answer.Result!)), ToastKind.Warning));
                    break;
            }
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
        if (!fan.CanWrite(mode)) return new ChangeAnswer(ChangeStatus.NoReadings, null);

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

        var result = await fan.ApplyAsync(mode, ct).ConfigureAwait(false);
        ShowResult(result);
        SyncChoice();
        RefreshTexts();
        return new ChangeAnswer(ChangeStatus.Done, result);
    }
}
