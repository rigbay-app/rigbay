using System.Globalization;
using System.Text;
using Rigbay.Abstractions;

namespace Rigbay.Gpu;

public sealed partial class GpuModule
{
    private void RegisterCommands(IHostServices host) =>
        host.Commands.Register(new CommandInfo("status", "cmd.status") { Access = VerbAccess.ReadOnly, UsageKey = "cmd.status-usage" }, StatusCommandAsync);

    /// <summary>
    /// <c>rigbay gpu status</c>: reads the GPU once now (the person asked, so the GPU is woken for it) and prints the readings, one per line.
    /// It publishes the same readings on the bus, as a poll would.
    /// </summary>
    private async Task<CommandResult> StatusCommandAsync(CommandContext ctx)
    {
        var host = _host;
        var poller = _poller;
        if (host is null) return CommandResult.Failed("");
        var loc = host.Localisation;
        if (ctx.Args.Count != 0) return CommandResult.Usage(loc.T("cmd.status-usage"));
        if (poller is null || !poller.IsOpen) return CommandResult.Failed(loc.T("state.unavailable"));

        var snapshot = await Task.Run(() => poller.PollOnce(), ctx.CancellationToken).ConfigureAwait(false);
        if (!snapshot.AnyAnswer) return CommandResult.Failed(loc.T("state.degraded"));
        return CommandResult.Ok(StatusText(loc, poller.DeviceName, snapshot));
    }

    /// <summary>The text of the status command. Numbers use the invariant format so that scripts can read them.</summary>
    internal static string StatusText(ILocalisation loc, string device, GpuSnapshot s)
    {
        var text = new StringBuilder();
        if (device.Length > 0) text.AppendLine(device);
        void Line(GpuMetric metric, double? value, string format, string unit)
        {
            if (value is null) return;
            text.AppendLine(loc.T(metric.LabelKey) + ": " + value.Value.ToString(format, CultureInfo.InvariantCulture) + unit);
        }
        Line(GpuMetrics.Load, s.Load, "0", " %");
        Line(GpuMetrics.MemoryController, s.MemoryController, "0", " %");
        Line(GpuMetrics.Power, s.PowerWatts, "0.0", " W");
        Line(GpuMetrics.PowerLimit, s.PowerLimitWatts, "0", " W");
        Line(GpuMetrics.Clock, s.GraphicsMegahertz, "0", " MHz");
        Line(GpuMetrics.MemoryClock, s.MemoryMegahertz, "0", " MHz");
        Line(GpuMetrics.MemoryUsed, s.MemoryUsedBytes / (1024 * 1024), "0", " MiB");
        Line(GpuMetrics.MemoryTotal, s.MemoryTotalBytes / (1024 * 1024), "0", " MiB");
        Line(GpuMetrics.Temperature, s.TemperatureCelsius, "0", " °C");
        Line(GpuMetrics.PerformanceState, s.PerformanceState, "0", "");
        if (s.ClockReasons is not null) text.AppendLine(loc.T("row.limit.header") + ": " + LimitText(loc, s));
        return text.ToString().TrimEnd();
    }
}
