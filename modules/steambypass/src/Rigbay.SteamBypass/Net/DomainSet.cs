using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rigbay.SteamBypass.Net;

/// <summary>
/// The Steam host names the bypass handles: which hosts the PAC file sends to the proxy, which hosts the proxy accepts and which
/// are resolved over DNS-over-HTTPS. A domain also covers its subdomains (suffix match on whole labels). The file format is the
/// one of the standalone app: one domain per line, lower case, <c>#</c> starts a comment.
/// </summary>
/// <remarks>
/// Entries are validated (letters, digits, dot, hyphen, underscore) because they are written into the PAC script: a line that
/// is not a plain domain name is dropped and counted, never copied into the script.
/// </remarks>
internal sealed class DomainSet
{
    /// <summary>The list used when no file is found or the file has no valid line. Same as the file shipped next to the module.</summary>
    public static readonly IReadOnlyList<string> BuiltIn = new[]
    {
        "steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com", "steamusercontent.com", "steamserver.net",
        "steamgames.com", "steam-chat.com", "steamcdn-a.akamaihd.net", "steamcommunity-a.akamaihd.net", "steamuserimages-a.akamaihd.net",
        "steamvideo-a.akamaihd.net", "steampipe.akamaized.net", "valvesoftware.com", "steamdeck.com",
    };

    private static readonly Regex Valid = new(@"^[a-z0-9_]([a-z0-9_.-]*[a-z0-9_])?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string[] _domains;

    /// <summary>Builds the set from the lines of a list file. Falls back to <see cref="BuiltIn"/> when no line is usable.</summary>
    /// <param name="lines">The lines.</param>
    /// <param name="source">Where they came from, for the log and the page.</param>
    public DomainSet(IEnumerable<string> lines, string source = "built-in")
    {
        var list = new List<string>();
        int dropped = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var d = Normalise(line);
            if (d is null) { dropped++; continue; }
            if (!list.Contains(d)) list.Add(d);
        }
        if (list.Count == 0)
        {
            list.AddRange(BuiltIn);
            source = "built-in";
        }
        _domains = list.ToArray();
        Dropped = dropped;
        Source = source;
    }

    /// <summary>The domains, lower case, without duplicates.</summary>
    public IReadOnlyList<string> Domains => _domains;

    /// <summary>Lines of the file that were not valid domain names and were left out.</summary>
    public int Dropped { get; }

    /// <summary>The file the list came from, or <c>built-in</c>.</summary>
    public string Source { get; }

    /// <summary>The built-in list.</summary>
    public static DomainSet Default() => new(BuiltIn);

    /// <summary>Reads the first existing file of <paramref name="candidates"/> that has at least one valid domain.</summary>
    public static DomainSet Load(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var set = new DomainSet(File.ReadAllLines(path, Encoding.UTF8), path);
                if (set.Source == path) return set;
            }
            catch (Exception) { /* unreadable: try the next one */ }
        }
        return Default();
    }

    /// <summary>Lower case, no leading <c>*.</c> or dot, no trailing dot; null when it is not a plain domain name.</summary>
    public static string? Normalise(string value)
    {
        var d = value.Trim().ToLowerInvariant();
        if (d.StartsWith("*.", StringComparison.Ordinal)) d = d[2..];
        d = d.Trim('.');
        if (d.Length is 0 or > 253) return null;
        if (d.Contains("..", StringComparison.Ordinal)) return null;
        return Valid.IsMatch(d) ? d : null;
    }

    /// <summary>True when <paramref name="host"/> is a listed domain or a subdomain of one. IP addresses and odd strings never match.</summary>
    public bool Contains(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 260) return false;
        var h = host.TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return false;
        foreach (var d in _domains)
        {
            if (h == d) return true;
            if (h.Length > d.Length && h.EndsWith(d, StringComparison.Ordinal) && h[h.Length - d.Length - 1] == '.') return true;
        }
        return false;
    }

    /// <summary>
    /// The PAC script: Steam hosts go to <c>PROXY 127.0.0.1:port; DIRECT</c>, everything else is <c>DIRECT</c>. Plain ES3 (no
    /// <c>endsWith</c>), because the Windows script host that evaluates it is old.
    /// </summary>
    public string BuildPac(int port)
    {
        var sb = new StringBuilder();
        sb.Append("function FindProxyForURL(url, host) {\n");
        sb.Append("  host = host.toLowerCase();\n");
        sb.Append("  if (host.charAt(host.length - 1) === '.') host = host.substring(0, host.length - 1);\n");
        sb.Append("  var d = [");
        for (int i = 0; i < _domains.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('\'').Append(_domains[i]).Append('\'');
        }
        sb.Append("];\n");
        sb.Append("  for (var i = 0; i < d.length; i++) {\n");
        sb.Append("    if (host === d[i] || (host.length > d[i].length && host.substring(host.length - d[i].length - 1) === '.' + d[i]))\n");
        sb.Append("      return 'PROXY 127.0.0.1:").Append(port.ToString(CultureInfo.InvariantCulture)).Append("; DIRECT';\n");
        sb.Append("  }\n  return 'DIRECT';\n}\n");
        return sb.ToString();
    }
}
