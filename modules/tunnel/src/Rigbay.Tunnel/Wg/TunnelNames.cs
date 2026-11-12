using System.Text;
using System.Text.RegularExpressions;

namespace Rigbay.Tunnel.Wg;

/// <summary>
/// Tunnel names. WireGuard for Windows accepts 1 to 32 characters from letters, digits and <c>_ = + . -</c>; the name becomes the service name
/// (<c>WireGuardTunnel$name</c>), the adapter name and a command-line argument, so anything else is refused here before it gets near a process.
/// </summary>
internal static class TunnelNames
{
    public const int MaxLength = 32;

    private static readonly Regex Valid = new(@"^[A-Za-z0-9_=+.\-]{1,32}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValid(string? name) => name is not null && Valid.IsMatch(name);

    public static string ServiceName(string name) => "WireGuardTunnel$" + name;

    /// <summary>
    /// A lowercase id made of <c>a-z 0-9 -</c> for tray items, hotkey actions and metric ids (those accept less than a tunnel name does). When the
    /// name needs more than lowercasing, four hex digits of a hash of the exact name are added so two names never share an id.
    /// </summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length + 5);
        foreach (char c in name)
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? c : c is >= 'A' and <= 'Z' ? char.ToLowerInvariant(c) : '-');
        var slug = sb.ToString();
        bool lossy = false;
        foreach (char c in name) if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')) { lossy = true; break; }
        return lossy ? slug + "-" + Hash4(name) : slug;
    }

    private static string Hash4(string text)
    {
        uint h = 2166136261;                       // FNV-1a over the UTF-16 code units
        foreach (char c in text) { h ^= c; h *= 16777619; }
        return (h & 0xFFFF).ToString("x4");
    }
}
