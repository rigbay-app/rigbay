using System.Globalization;
using System.Text;
using Rigbay.Abstractions;
using Rigbay.Recorder.Recording;

namespace Rigbay.Recorder;

public sealed partial class RecorderModule
{
    // ------------------------------------------------------------------ registration

    private void RegisterCommands(IHostServices host)
    {
        host.Commands.Register("start", "cmd.start", ctx => StartCommand(host, ctx));
        host.Commands.Register("stop", "cmd.stop", _ => StopCommand(host));
        host.Commands.Register("status", "cmd.status", _ => StatusCommand(host));
    }

    private void RegisterShortcuts(IHostServices host)
    {
        _trayStart = new TrayItem("start", "tray.start", () => ToggleFromShortcut(wantRecording: true));
        _trayStop = new TrayItem("stop", "tray.stop", () => ToggleFromShortcut(wantRecording: false));
        _trayStop.Visible.Value = false;
        host.Tray.Add(_trayStart);
        host.Tray.Add(_trayStop);

        // Unbound by default: a key that starts writing files is the user's choice. The press does what the tray item does.
        _hotkey = host.Hotkeys.Register(new HotkeyRegistration("toggle", "hotkey.toggle", null, () => ToggleFromShortcut(wantRecording: !(_engine?.IsRecording ?? false))));
    }

    /// <summary>The tray items and the hotkey. Nobody is looking at the page, so a refusal is shown as a notification.</summary>
    private void ToggleFromShortcut(bool wantRecording)
    {
        var engine = _engine;
        if (engine is null) return;
        try
        {
            if (!wantRecording)
            {
                engine.Stop(StopReason.User);
                return;
            }
            var outcome = StartRecording(RequestFromSettings());
            if (!outcome.Started) Toast(ToastKind.Warning, outcome.Message);
        }
        catch (Exception ex)
        {
            _host?.Log.Error("Starting or stopping from the tray or the hotkey failed", ex);
        }
    }

    // ------------------------------------------------------------------ rigbay recorder start [--every S] [--minutes N] [--metrics a,b]

    /// <summary>The arguments of <c>start</c> after parsing. <see cref="Problem"/> is set when they are wrong.</summary>
    internal sealed record StartArgs(int? EverySeconds, int? Minutes, IReadOnlyList<MetricId>? Metrics, string? Problem);

    /// <summary>Reads <c>--every S</c>, <c>--minutes N</c> and <c>--metrics a,b</c> (also as <c>--every=S</c>). Anything else is a problem.</summary>
    internal static StartArgs ParseStart(IReadOnlyList<string> args)
    {
        int? every = null, minutes = null;
        List<MetricId>? metrics = null;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string name = arg, value;
            int eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                name = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else if (arg is "--every" or "--minutes" or "--metrics")
            {
                if (i + 1 >= args.Count) return Bad(arg);
                value = args[++i];
            }
            else return Bad(arg);

            switch (name)
            {
                case "--every":
                    if (!TryWhole(value, out int s) || s < RecorderSettings.MinPeriodSeconds || s > RecorderSettings.MaxPeriodSeconds) return Bad(arg);
                    every = s;
                    break;
                case "--minutes":
                    if (!TryWhole(value, out int m) || m > RecorderSettings.MaxDurationMinutes) return Bad(arg);
                    minutes = m;
                    break;
                case "--metrics":
                    var ids = new List<MetricId>();
                    foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!MetricId.IsValid(part)) return Bad(arg);
                        var id = new MetricId(part);
                        if (!ids.Contains(id)) ids.Add(id);
                    }
                    if (ids.Count == 0) return Bad(arg);
                    metrics = ids;
                    break;
                default:
                    return Bad(arg);
            }
        }
        return new StartArgs(every, minutes, metrics, null);

        static StartArgs Bad(string arg) => new(null, null, null, arg);
        static bool TryWhole(string text, out int number) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private CommandResult StartCommand(IHostServices host, CommandContext ctx)
    {
        var loc = host.Localisation;
        var args = ParseStart(ctx.Args);
        if (args.Problem is { } problem) return CommandResult.Usage(loc.F("cmd.start-bad-argument", problem) + "\n" + loc.T("cmd.start-usage"));
        if (_engine is null) return CommandResult.Failed(loc.T("cmd.not-running"));

        // What the command does not say comes from the page, so "rigbay recorder start" equals the Start button.
        var request = RequestFromSettings();
        if (args.EverySeconds is { } every) request = request with { Period = TimeSpan.FromSeconds(every) };
        if (args.Minutes is { } minutes) request = request with { Duration = minutes > 0 ? TimeSpan.FromMinutes(minutes) : null };
        if (args.Metrics is { } only) request = request with { Only = only };

        var outcome = StartRecording(request);
        if (!outcome.Started) return CommandResult.Failed(loc.Resolve(outcome.Message));
        string limit = request.Duration is { } d ? loc.F("cmd.limit-minutes", d.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)) : loc.T("cmd.limit-none");
        return CommandResult.Ok(loc.F("cmd.started", outcome.Path ?? "", request.Period.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), limit));
    }

    // ------------------------------------------------------------------ rigbay recorder stop

    private CommandResult StopCommand(IHostServices host)
    {
        var loc = host.Localisation;
        var engine = _engine;
        if (engine is null) return CommandResult.Failed(loc.T("cmd.not-running"));
        if (!engine.IsRecording) return CommandResult.Ok(loc.T("cmd.not-recording"));   // already stopped (a time limit, the tray): not an error for a script
        var summary = engine.Stop(StopReason.User);
        return summary is null
            ? CommandResult.Failed(loc.T("cmd.stop-pending"))
            : CommandResult.Ok(loc.F("cmd.stopped", summary.Path, summary.Rows.ToString(CultureInfo.InvariantCulture), CsvFormat.Size(summary.Bytes)));
    }

    // ------------------------------------------------------------------ rigbay recorder status

    private CommandResult StatusCommand(IHostServices host)
    {
        var loc = host.Localisation;
        var engine = _engine;
        if (engine is null) return CommandResult.Failed(loc.T("cmd.not-running"));
        var st = engine.Status();
        var text = new StringBuilder();
        if (st.Recording)
        {
            text.AppendLine(loc.F("status.line-recording", st.Path ?? ""));
            text.AppendLine(loc.F("status.line-progress",
                st.Rows.ToString(CultureInfo.InvariantCulture), CsvFormat.Size(st.Bytes), CsvFormat.Clock(st.Elapsed),
                st.Period.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), st.Columns.ToString(CultureInfo.InvariantCulture)));
            if (st.Duration is { } limit) text.AppendLine(loc.F("status.line-limit", CsvFormat.Clock(limit - st.Elapsed < TimeSpan.Zero ? TimeSpan.Zero : limit - st.Elapsed)));
            if (st.LateMetrics > 0) text.AppendLine(loc.F("status.line-late", st.LateMetrics.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            text.AppendLine(loc.T("status.line-idle"));
            if (engine.LastSummary is { } last)
                text.AppendLine(loc.F("status.line-last", last.Path, last.Rows.ToString(CultureInfo.InvariantCulture), CsvFormat.Size(last.Bytes), loc.Resolve(EndedNotice(last))));
        }
        text.Append(loc.F("status.line-folder", ResolveFolder()));
        return CommandResult.Ok(text.ToString());
    }
}
