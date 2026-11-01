using System.Globalization;
using System.Text.RegularExpressions;

namespace Rigbay.Core.Install;

/// <summary>Semantic Version 2.0 parsing and ordering (build metadata is ignored for ordering).</summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    private static readonly Regex Pattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant);

    private readonly string[] _pre;

    private SemVer(string text, long major, long minor, long patch, string[] pre)
    {
        Text = text;
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre;
    }

    public string Text { get; }
    public long Major { get; }
    public long Minor { get; }
    public long Patch { get; }
    public bool IsPrerelease => _pre.Length > 0;

    public static bool TryParse(string? text, out SemVer version)
    {
        version = null!;
        if (string.IsNullOrEmpty(text) || text.Length > 64) return false;
        var m = Pattern.Match(text);
        if (!m.Success) return false;
        if (!long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !long.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : Array.Empty<string>();
        version = new SemVer(text, major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (_pre.Length == 0 && other._pre.Length == 0) return 0;
        if (_pre.Length == 0) return 1;           // a release is above its prereleases
        if (other._pre.Length == 0) return -1;
        for (int i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i];
            var b = other._pre[i];
            bool an = a.All(char.IsAsciiDigit), bn = b.All(char.IsAsciiDigit);
            if (an && bn)
            {
                // numeric identifiers: by value, without overflow
                c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            }
            else if (an) c = -1;                   // numeric identifiers sort below alphanumeric ones
            else if (bn) c = 1;
            else c = string.CompareOrdinal(a, b);
            if (c != 0) return Math.Sign(c);
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVer s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _pre));
    public override string ToString() => Text;

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
}
