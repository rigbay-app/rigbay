using System.Text;
using System.Text.RegularExpressions;

namespace Rigbay.Core;

/// <summary>
/// One parsed <c>.lang</c> file (same format as xtu-profile-switcher's <c>Loc.cs</c>): UTF-8, <c>key = text</c>,
/// <c>#</c> comments, <c>\n</c> line break, <c>\\</c> backslash, header lines <c>@code</c> and <c>@name</c>.
/// </summary>
public sealed class LanguageFile
{
    private static readonly Regex CodePattern = new("^[a-z]{2,3}(-[a-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

    /// <summary>Problems found while parsing: lines without '=', duplicate keys, a bad code.</summary>
    public List<string> Problems { get; } = new();

    /// <summary>Keys that appeared more than once in the file (the last one wins).</summary>
    public List<string> DuplicateKeys { get; } = new();

    public static LanguageFile Parse(TextReader reader, string source)
    {
        var file = new LanguageFile { Source = source };
        string? line;
        int n = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            n++;
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) { file.Problems.Add($"{source} line {n}: expected 'key = text'"); continue; }
            var key = t[..eq].Trim();
            var rawValue = t[(eq + 1)..].Trim();
            if (FindUnknownEscape(rawValue) is { } bad)
                file.Problems.Add($"{source} line {n}: the backslash before '{bad}' is dropped when the text is shown; write \\\\ for a backslash (for example root\\\\WMI)");
            var value = Unescape(rawValue);
            if (key == "@code") file.Code = value.Trim().ToLowerInvariant();
            else if (key == "@name") file.Name = value;
            else
            {
                if (file.Text.ContainsKey(key)) file.DuplicateKeys.Add(key);
                file.Text[key] = value;
            }
        }

        if (file.Code.Length == 0)
        {
            // No header: take the code from the file name (vi.lang -> vi).
            file.Code = Path.GetFileNameWithoutExtension(source).ToLowerInvariant();
        }
        if (!CodePattern.IsMatch(file.Code))
        {
            file.Problems.Add($"{source}: bad language code '{file.Code}'");
            file.Code = "";
        }
        if (file.Name.Length == 0) file.Name = file.Code;
        return file;
    }

    public static LanguageFile ParseFile(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return Parse(reader, path);
    }

    /// <summary>The sorted set of placeholder numbers a text uses, for example <c>0,1</c>.</summary>
    public static string Placeholders(string text)
    {
        var set = new SortedSet<int>();
        foreach (Match m in Regex.Matches(text, @"\{(\d+)(?:[:,][^}]*)?\}"))
            set.Add(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(',', set);
    }

    /// <summary>
    /// The first character after a backslash that is not part of a known escape (<c>\n</c>, <c>\t</c>, <c>\\</c>), or null.
    /// <see cref="Unescape"/> drops the backslash before such a character, so <c>root\WMI</c> would show as <c>rootWMI</c>.
    /// </summary>
    public static char? FindUnknownEscape(string s)
    {
        for (int i = 0; i + 1 < s.Length; i++)
        {
            if (s[i] != '\\') continue;
            char c = s[++i];
            if (c is not ('n' or 't' or '\\')) return c;
        }
        return null;
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char c = s[++i];
                sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
