using System.Text;

namespace Rigbay.SteamBypass.Net;

/// <summary>One HTTP request head (everything up to the blank line) and the bytes that arrived after it.</summary>
internal sealed class HttpHead
{
    /// <summary>Largest head accepted. Anything longer is refused.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The head including the final blank line, one byte per character (Latin-1, so every byte survives a round trip).</summary>
    public required string Text { get; init; }

    /// <summary>Bytes read from the stream after the head (the start of a body or of the next request).</summary>
    public required byte[] Leftover { get; init; }

    /// <summary>The header lines and the request line, without line ends and without the final blank line.</summary>
    public string[] Lines => Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Request line pieces; null when the first line is not <c>METHOD target VERSION</c>.</summary>
    public RequestLine? Request
    {
        get
        {
            var lines = Lines;
            if (lines.Length == 0) return null;
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            if (!parts[2].StartsWith("HTTP/", StringComparison.Ordinal)) return null;
            foreach (var c in parts[0]) if (c is < 'A' or > 'Z') return null;   // methods are upper-case letters
            return new RequestLine(parts[0], parts[1], parts[2]);
        }
    }

    /// <summary>The value of the first header called <paramref name="name"/> (case-insensitive), trimmed; null when absent.</summary>
    public string? Header(string name)
    {
        var lines = Lines;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            if (lines[i].AsSpan(0, colon).Equals(name, StringComparison.OrdinalIgnoreCase)) return lines[i][(colon + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Reads one head from <paramref name="stream"/>. <paramref name="initial"/> holds bytes that were already read.
    /// Returns null when the stream ends first, when the head is longer than <see cref="MaxBytes"/> or when
    /// <paramref name="ct"/> is cancelled before a complete head arrives (the caller closes the connection).
    /// </summary>
    public static async Task<HttpHead?> ReadAsync(Stream stream, byte[]? initial, CancellationToken ct)
    {
        var data = new List<byte>(1024);
        if (initial is { Length: > 0 }) data.AddRange(initial);
        var buf = new byte[4096];
        int scanned = 0;
        while (data.Count < MaxBytes)
        {
            // Look for CR LF CR LF; start a few bytes back so a terminator split over two reads is found.
            for (int i = Math.Max(3, scanned); i < data.Count; i++)
            {
                if (data[i] == '\n' && data[i - 1] == '\r' && data[i - 2] == '\n' && data[i - 3] == '\r')
                {
                    var head = new byte[i + 1];
                    data.CopyTo(0, head, 0, head.Length);
                    var left = new byte[data.Count - (i + 1)];
                    data.CopyTo(i + 1, left, 0, left.Length);
                    return new HttpHead { Text = Encoding.Latin1.GetString(head), Leftover = left };
                }
            }
            scanned = Math.Max(0, data.Count - 3);
            int n;
            try { n = await stream.ReadAsync(buf, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            if (n <= 0) return null;
            for (int i = 0; i < n; i++) data.Add(buf[i]);
        }
        return null;
    }

    /// <summary>Splits <c>host:port</c>, <c>[v6]:port</c> or <c>host</c>. Returns false for an empty host or a port outside 1..65535.</summary>
    public static bool TrySplitHostPort(string target, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(target) || target.Length > 300) return false;
        string h = target;
        if (target.StartsWith('['))
        {
            int close = target.IndexOf(']');
            if (close < 0) return false;
            h = target[1..close];
            var rest = target[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !TryPort(rest[1..], out port)) return false;
            }
        }
        else
        {
            int colon = target.LastIndexOf(':');
            if (colon >= 0)
            {
                h = target[..colon];
                if (!TryPort(target[(colon + 1)..], out port)) return false;
            }
        }
        if (h.Length == 0) return false;
        foreach (var c in h) if (c < 0x21 || c > 0x7E) return false;   // printable ASCII only: no spaces, controls, NUL, non-ASCII
        host = h;
        return true;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}

/// <summary>The three pieces of a request line.</summary>
internal sealed record RequestLine(string Method, string Target, string Version);
