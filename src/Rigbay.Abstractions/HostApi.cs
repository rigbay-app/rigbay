using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Rigbay.Abstractions;

/// <summary>
/// The version of the host API (this contract), written <c>major.minor.revision</c>. A module declares the range it supports in
/// <c>module.json</c> (<c>hostApi</c>); the host refuses a module whose range does not contain <see cref="Current"/> before it loads
/// any module code. While the major number is 0 any minor version may break modules. The revision counts the additive changes made
/// inside a minor version (a new optional member): a module that uses a member added in revision <c>n</c> declares
/// <c>&gt;=0.1.n &lt;0.2</c>. The list of revisions is in <c>docs/ABSTRACTIONS.md</c>, section 11.
/// </summary>
public static class HostApi
{
    /// <summary>The major number of <see cref="Current"/>.</summary>
    public const int Major = 0;

    /// <summary>The minor number of <see cref="Current"/>.</summary>
    public const int Minor = 1;

    /// <summary>The revision of this assembly's contract inside <see cref="Minor"/>: the number of additive changes since 0.1.0.</summary>
    public const int Revision = 3;

    /// <summary>The API version of this assembly: <c>0.1.3</c> (major.minor.revision; <see cref="Version.Build"/> is the revision).</summary>
    public static readonly Version Current = new(Major, Minor, Revision);

    /// <summary>Returns true when <paramref name="range"/> parses and contains <see cref="Current"/>.</summary>
    /// <param name="range">A range such as <c>&gt;=0.1 &lt;0.2</c> or <c>&gt;=0.1.2 &lt;0.2</c>.</param>
    public static bool IsSupported(string? range) =>
        ApiVersionRange.TryParse(range, out var parsed, out _) && parsed.Contains(Current);

    /// <summary>The revision of <paramref name="version"/>: its third number, or 0 when it has none.</summary>
    /// <param name="version">A host API version.</param>
    public static int RevisionOf(Version version) => version.Build < 0 ? 0 : version.Build;
}

/// <summary>
/// A range of host API versions: space-separated comparators that must all hold. Operators are
/// <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c> and <c>=</c>; a bare version means <c>=</c>. A version is <c>major.minor</c>
/// or <c>major.minor.revision</c>. A comparator without a revision compares major and minor only, so <c>&gt;=0.1 &lt;0.2</c> and
/// <c>=0.1</c> mean "any 0.1.x"; a comparator with a revision compares all three numbers, so <c>&gt;=0.1.2</c> needs a host whose
/// revision is 2 or more. Examples: <c>&gt;=0.1 &lt;0.2</c>, <c>&gt;=0.1.2 &lt;0.2</c>, <c>&gt;=1.0 &lt;2.0</c>, <c>=0.1</c>.
/// </summary>
public sealed class ApiVersionRange
{
    // Revision -1: the comparator wrote none, so only major and minor are compared.
    private readonly (string Op, int Major, int Minor, int Revision)[] _comparators;

    private ApiVersionRange(string text, (string, int, int, int)[] comparators)
    {
        Text = text;
        _comparators = comparators;
    }

    /// <summary>The range as written (normalised whitespace).</summary>
    public string Text { get; }

    /// <summary>Parses a range. On failure returns false and <paramref name="error"/> says what is wrong.</summary>
    /// <param name="text">The range text.</param>
    /// <param name="range">The parsed range when the result is true.</param>
    /// <param name="error">An English description of the problem when the result is false.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ApiVersionRange? range, [NotNullWhen(false)] out string? error)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "the range is empty"; return false; }

        var list = new List<(string, int, int, int)>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string op = "=";
            string rest = token;
            foreach (var candidate in new[] { ">=", "<=", ">", "<", "=" })
            {
                if (token.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; rest = token[candidate.Length..]; break; }
            }
            if (!TryParseVersion(rest, out var major, out var minor, out var revision))
            {
                error = $"'{token}' is not a comparator like '>=0.1' or '>=0.1.2' (versions are major.minor or major.minor.revision)";
                return false;
            }
            list.Add((op, major, minor, revision));
        }

        if (list.Count == 0) { error = "the range is empty"; return false; }
        range = new ApiVersionRange(string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)), list.ToArray());
        error = null;
        return true;
    }

    /// <summary>True when <paramref name="version"/> satisfies every comparator.</summary>
    /// <param name="version">The host's API version; a missing third number counts as revision 0.</param>
    public bool Contains(Version version) => Holds(version, useRevision: true);

    /// <summary>
    /// When <paramref name="version"/> is refused only because its revision is too low (the range would hold if every revision were
    /// ignored, and a lower bound names a higher revision), the lowest revision that would be accepted, as a version with the same
    /// major and minor; otherwise null. This is the "Needs host API 0.1.3 or newer" case.
    /// </summary>
    /// <param name="version">The host's API version.</param>
    public Version? NeededRevision(Version version)
    {
        if (Holds(version, useRevision: true) || !Holds(version, useRevision: false)) return null;
        int have = HostApi.RevisionOf(version);
        int need = have;
        foreach (var (op, major, minor, revision) in _comparators)
        {
            if (revision < 0 || major != version.Major || minor != version.Minor) continue;
            if (op is ">=" or "=") { if (revision > have) need = Math.Max(need, revision); }
            else if (op == ">" && revision >= have) need = Math.Max(need, revision + 1);
        }
        return need > have ? new Version(version.Major, version.Minor, need) : null;
    }

    private bool Holds(Version version, bool useRevision)
    {
        int hostRevision = HostApi.RevisionOf(version);
        foreach (var (op, major, minor, revision) in _comparators)
        {
            int c = version.Major.CompareTo(major);
            if (c == 0) c = version.Minor.CompareTo(minor);
            if (c == 0 && useRevision && revision >= 0) c = hostRevision.CompareTo(revision);
            // Revisions ignored: a strict bound that names a revision still allows the same minor (">0.1.2" holds from 0.1.3 on).
            var effective = !useRevision && revision >= 0 ? (op == ">" ? ">=" : op == "<" ? "<=" : op) : op;
            bool ok = effective switch
            {
                ">=" => c >= 0,
                ">" => c > 0,
                "<=" => c <= 0,
                "<" => c < 0,
                _ => c == 0,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    private static bool TryParseVersion(string text, out int major, out int minor, out int revision)
    {
        major = minor = 0;
        revision = -1;
        var parts = text.Split('.');
        if (parts.Length is < 2 or > 3) return false;
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        major = numbers[0];
        minor = numbers[1];
        if (parts.Length == 3) revision = numbers[2];
        return true;
    }
}
