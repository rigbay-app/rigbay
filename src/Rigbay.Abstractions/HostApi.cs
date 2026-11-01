using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Rigbay.Abstractions;

/// <summary>
/// The version of the host API (this contract). A module declares the range it supports in <c>module.json</c>
/// (<c>hostApi</c>); the host refuses a module whose range does not contain <see cref="Current"/> before it loads any
/// module code. While the major number is 0 any minor version may break modules.
/// </summary>
public static class HostApi
{
    /// <summary>The API version of this assembly: major.minor only (the build number is always unset).</summary>
    public static readonly Version Current = new(0, 1);

    /// <summary>Returns true when <paramref name="range"/> parses and contains <see cref="Current"/>.</summary>
    /// <param name="range">A range such as <c>&gt;=0.1 &lt;0.2</c>.</param>
    public static bool IsSupported(string? range) =>
        ApiVersionRange.TryParse(range, out var parsed, out _) && parsed.Contains(Current);
}

/// <summary>
/// A range of host API versions: space-separated comparators that must all hold. Operators are
/// <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c> and <c>=</c>; a bare version means <c>=</c>. Versions are
/// <c>major.minor</c>; a third number is accepted and ignored. Examples: <c>&gt;=0.1 &lt;0.2</c>, <c>&gt;=1.0 &lt;2.0</c>, <c>=0.1</c>.
/// </summary>
public sealed class ApiVersionRange
{
    private readonly (string Op, Version Version)[] _comparators;

    private ApiVersionRange(string text, (string, Version)[] comparators)
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

        var list = new List<(string, Version)>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string op = "=";
            string rest = token;
            foreach (var candidate in new[] { ">=", "<=", ">", "<", "=" })
            {
                if (token.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; rest = token[candidate.Length..]; break; }
            }
            if (!TryParseVersion(rest, out var version))
            {
                error = $"'{token}' is not a comparator like '>=0.1' (versions are major.minor)";
                return false;
            }
            list.Add((op, version));
        }

        if (list.Count == 0) { error = "the range is empty"; return false; }
        range = new ApiVersionRange(string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)), list.ToArray());
        error = null;
        return true;
    }

    /// <summary>True when <paramref name="version"/> satisfies every comparator.</summary>
    /// <param name="version">The version to test; only major and minor are compared.</param>
    public bool Contains(Version version)
    {
        var v = new Version(version.Major, version.Minor);
        foreach (var (op, bound) in _comparators)
        {
            int c = v.CompareTo(bound);
            bool ok = op switch
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

    private static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0);
        var parts = text.Split('.');
        if (parts.Length is < 2 or > 3) return false;
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        version = new Version(numbers[0], numbers[1]);
        return true;
    }
}
