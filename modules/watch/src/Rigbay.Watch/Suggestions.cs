using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Watch;

/// <summary>A rule the page offers with one click. It is not a rule until the owner adds it.</summary>
/// <param name="Key">Stable key stored in the rule, so the page stops offering it once added.</param>
/// <param name="HeaderKey">Language key of the name; null when <paramref name="HeaderLiteral"/> is used.</param>
/// <param name="HeaderLiteral">A name that comes from data (a tunnel's name).</param>
/// <param name="HelpKey">Language key of a caution shown with the row, or null.</param>
internal sealed record SuggestedRule(string Key, string? HeaderKey, string? HeaderLiteral, string? HelpKey, WatchRule Rule);

/// <summary>
/// The built-in suggestions. Each one is offered only when the metric it reads is in the catalogue, that is when a module that publishes it is on. The
/// numbers are starting points for a laptop with a 45 W CPU; the owner edits them after adding.
/// </summary>
internal static class Suggestions
{
    public const string CpuTemp = "cpu-temp";
    public const string GpuTemp = "gpu-temp";
    public const string Tunnel = "tunnel";
    public const string SteamErrors = "steam-errors";
    public const string GpuThermal = "gpu-thermal";
    public const string FanStopped = "fan-stopped";

    /// <summary>The GPU clock-limit bits that mean "too hot": software thermal (0x20) and hardware thermal (0x40).</summary>
    public const double GpuThermalMask = 0x20 | 0x40;

    private static readonly Regex TunnelHandshake = new(@"^tunnel\.[a-z0-9_-]+\.handshake_age$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<SuggestedRule> For(IReadOnlyList<MetricInfo> catalogue)
    {
        var ids = catalogue.ToDictionary(m => m.Id.Value, m => m, StringComparer.Ordinal);
        var list = new List<SuggestedRule>();

        if (ids.ContainsKey("cpu.temp"))
            list.Add(new(CpuTemp, "suggest.cpu-temp", null, null, Make(CpuTemp, "cpu.temp", RuleKind.Above, 95, clear: 90, seconds: 30)));
        if (ids.ContainsKey("gpu.nvml.temp"))
            list.Add(new(GpuTemp, "suggest.gpu-temp", null, null, Make(GpuTemp, "gpu.nvml.temp", RuleKind.Above, 87, clear: 82, seconds: 30)));
        foreach (var info in catalogue.Where(m => TunnelHandshake.IsMatch(m.Id.Value)).OrderBy(m => m.Id.Value, StringComparer.Ordinal))
            list.Add(new(Tunnel + ":" + info.Id.Value, null, info.Label, "suggest.tunnel.help", Make(Tunnel + ":" + info.Id.Value, info.Id.Value, RuleKind.Above, 180, clear: null, seconds: 15)));
        if (ids.ContainsKey("steam.proxy.errors"))
            list.Add(new(SteamErrors, "suggest.steam-errors", null, null, Make(SteamErrors, "steam.proxy.errors", RuleKind.Rises, 5, clear: null, seconds: 60)));
        if (ids.ContainsKey("gpu.throttle"))
            list.Add(new(GpuThermal, "suggest.gpu-thermal", null, "suggest.gpu-thermal.help", Make(GpuThermal, "gpu.throttle", RuleKind.Bits, GpuThermalMask, clear: null, seconds: 30)));
        if (ids.ContainsKey("fan.cpu.rpm") && ids.ContainsKey("cpu.temp"))
        {
            var rule = Make(FanStopped, "fan.cpu.rpm", RuleKind.Below, 0, clear: null, seconds: 20);
            rule.ConditionMetric = "cpu.temp";
            rule.ConditionKind = RuleKind.Above;
            rule.ConditionThreshold = 80;
            list.Add(new(FanStopped, "suggest.fan-stopped", null, "suggest.fan-stopped.help", rule));
        }
        return list;
    }

    private static WatchRule Make(string key, string metric, RuleKind kind, double threshold, double? clear, int seconds) => new()
    {
        Id = WatchRule.NewId(),
        Enabled = true,
        Metric = metric,
        Kind = kind,
        Threshold = threshold,
        ClearAt = clear,
        Seconds = seconds,
        QuietMinutes = WatchRule.DefaultQuietMinutes,
        IntervalSeconds = WatchRule.DefaultIntervalSeconds,
        Suggestion = key,
    };
}
