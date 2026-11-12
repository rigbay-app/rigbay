using System.Globalization;

namespace Rigbay.Tunnel.Wg;

/// <summary>What the three <c>wg show</c> fields said, with the peers' public keys already dropped (they are never kept or logged).</summary>
internal sealed record WgReading(DateTimeOffset? LatestHandshake, long? RxBytes, long? TxBytes, string? Endpoint);

/// <summary>Parsers for the tab-separated output of <c>wg show &lt;name&gt; latest-handshakes|transfer|endpoints</c>. Unreadable lines are skipped.</summary>
internal static class WgParsers
{
    /// <summary>Lines <c>peer-key TAB unix-seconds</c>. The newest handshake of any peer; null when none ever happened (0 means "never").</summary>
    public static DateTimeOffset? LatestHandshake(string? text)
    {
        long best = 0;
        foreach (var fields in Lines(text))
            if (fields.Length >= 2 && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > best && seconds < 253402300799)
                best = seconds;
        return best == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(best);
    }

    /// <summary>Lines <c>peer-key TAB received TAB sent</c>. Sums over the peers; null when no line could be read.</summary>
    public static (long Rx, long Tx)? Transfer(string? text)
    {
        long rx = 0, tx = 0;
        bool any = false;
        foreach (var fields in Lines(text))
        {
            if (fields.Length < 3) continue;
            if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var r)) continue;
            if (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) continue;
            rx += r; tx += t; any = true;
        }
        return any ? (rx, tx) : null;
    }

    /// <summary>Lines <c>peer-key TAB endpoint</c> (<c>(none)</c> when the peer has none). The first real endpoint, or null.</summary>
    public static string? Endpoint(string? text)
    {
        foreach (var fields in Lines(text))
            if (fields.Length >= 2 && fields[1].Length is > 0 and <= 200 && fields[1] != "(none)") return fields[1];
        return null;
    }

    private static IEnumerable<string[]> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return line.Split('\t', StringSplitOptions.TrimEntries);
    }
}
