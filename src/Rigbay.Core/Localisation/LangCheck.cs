using System.Text;
using System.Text.RegularExpressions;

namespace Rigbay.Core;

/// <summary>What to check besides missing keys and placeholders.</summary>
/// <param name="RequiredPrefix">Every key must start with this (<c>module.&lt;id&gt;.</c> for a module); null for the host's own files.</param>
/// <param name="SourceTexts">Source code to scan for key usage; null skips the "unused" check.</param>
public sealed record LangCheckOptions(string? RequiredPrefix = null, IReadOnlyList<string>? SourceTexts = null);

public sealed class LanguageReport
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public int Total { get; init; }
    public int Translated { get; set; }

    /// <summary>English keys this language lacks (a warning: they show in English).</summary>
    public List<string> Missing { get; } = new();

    /// <summary>Keys that English does not have (an error: probably a typo).</summary>
    public List<string> Unknown { get; } = new();

    /// <summary>Keys whose <c>{0}</c> placeholders differ from English (an error).</summary>
    public List<string> PlaceholderMismatch { get; } = new();

    /// <summary>Keys outside the required prefix (an error).</summary>
    public List<string> BadPrefix { get; } = new();
}

public sealed class LangCheckReport
{
    public int EnglishKeys { get; init; }
    public List<LanguageReport> Languages { get; } = new();

    /// <summary>English keys that no string literal in the scanned source uses (a warning).</summary>
    public List<string> Unused { get; } = new();

    /// <summary>Parse problems and a missing English file (errors).</summary>
    public List<string> Problems { get; } = new();

    public bool HasErrors => Problems.Count > 0 || Languages.Any(l => l.Unknown.Count > 0 || l.PlaceholderMismatch.Count > 0 || l.BadPrefix.Count > 0);

    public bool HasWarnings => Unused.Count > 0 || Languages.Any(l => l.Missing.Count > 0);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"English keys: {EnglishKeys}");
        foreach (var p in Problems) sb.AppendLine("error: " + p);
        foreach (var l in Languages)
        {
            sb.AppendLine($"{l.Code} ({l.Name}): {l.Translated}/{l.Total} translated");
            if (l.Missing.Count > 0) sb.AppendLine("  missing (shown in English): " + string.Join(", ", l.Missing));
            if (l.Unknown.Count > 0) sb.AppendLine("  unknown keys, not in English (typo?): " + string.Join(", ", l.Unknown));
            if (l.PlaceholderMismatch.Count > 0) sb.AppendLine("  placeholders differ from English: " + string.Join(", ", l.PlaceholderMismatch));
            if (l.BadPrefix.Count > 0) sb.AppendLine("  keys outside the required prefix: " + string.Join(", ", l.BadPrefix));
        }
        if (Unused.Count > 0) sb.AppendLine("unused English keys (no string literal in the source): " + string.Join(", ", Unused));
        sb.AppendLine(HasErrors ? "result: errors" : HasWarnings ? "result: ok with warnings" : "result: ok");
        return sb.ToString();
    }
}

/// <summary>The <c>--langcheck</c> capability: missing keys, unknown keys, placeholder mismatches, prefix violations, unused keys.</summary>
public static class LangCheck
{
    /// <summary>Reads every <c>*.lang</c> file of a folder; files with the same code are merged.</summary>
    public static List<LanguageFile> LoadDirectory(string directory)
    {
        var merged = new Dictionary<string, LanguageFile>(StringComparer.OrdinalIgnoreCase);
        var result = new List<LanguageFile>();
        foreach (var path in Directory.GetFiles(directory, "*.lang").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var file = LanguageFile.ParseFile(path);
            if (file.Code.Length > 0 && merged.TryGetValue(file.Code, out var existing))
            {
                foreach (var kv in file.Text) existing.Text[kv.Key] = kv.Value;
                existing.Problems.AddRange(file.Problems);
                existing.DuplicateKeys.AddRange(file.DuplicateKeys);
            }
            else
            {
                if (file.Code.Length > 0) merged[file.Code] = file;
                result.Add(file);
            }
        }
        return result;
    }

    public static LangCheckReport Check(IReadOnlyList<LanguageFile> files, LangCheckOptions? options = null)
    {
        options ??= new LangCheckOptions();
        var english = files.FirstOrDefault(f => f.Code == LanguageCatalog.Fallback);
        IReadOnlyDictionary<string, string> englishKeys = english?.Text ?? new Dictionary<string, string>();

        var report = new LangCheckReport { EnglishKeys = englishKeys.Count };
        report.Problems.AddRange(files.SelectMany(f => f.Problems));
        if (english is null) report.Problems.Add("there is no English file (en.lang): English is the reference");
        foreach (var f in files.Where(f => f.DuplicateKeys.Count > 0))
            report.Problems.Add($"{f.Source}: duplicate keys: {string.Join(", ", f.DuplicateKeys.Distinct())}");

        foreach (var file in files.Where(f => f.Code.Length > 0))
        {
            var lr = new LanguageReport { Code = file.Code, Name = file.Name, Total = englishKeys.Count };
            bool isEnglish = file.Code == LanguageCatalog.Fallback;
            foreach (var (key, value) in englishKeys)
            {
                if (isEnglish) { lr.Translated++; continue; }
                if (!file.Text.TryGetValue(key, out var text)) { lr.Missing.Add(key); continue; }
                lr.Translated++;
                if (LanguageFile.Placeholders(value) != LanguageFile.Placeholders(text)) lr.PlaceholderMismatch.Add(key);
            }
            if (!isEnglish)
                foreach (var key in file.Text.Keys)
                    if (!englishKeys.ContainsKey(key)) lr.Unknown.Add(key);
            if (options.RequiredPrefix is { } prefix)
                foreach (var key in file.Text.Keys)
                    if (!key.StartsWith(prefix, StringComparison.Ordinal)) lr.BadPrefix.Add(key);
            report.Languages.Add(lr);
        }

        if (options.SourceTexts is not null) report.Unused.AddRange(FindUnused(englishKeys.Keys, options));
        return report;
    }

    /// <summary>English keys that no string literal in the source texts uses. A literal ending in '.' counts as a prefix of a family of keys.</summary>
    public static List<string> FindUnused(IEnumerable<string> englishKeys, LangCheckOptions options)
    {
        var literals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in options.SourceTexts ?? Array.Empty<string>())
            foreach (Match m in Regex.Matches(text, "\"((?:[^\"\\\r\n]|\\.)*)\""))
                if (!Rigbay.Abstractions.Text.IsLiteral(m.Groups[1].Value)) literals.Add(m.Groups[1].Value); // "=..." is literal text, not a key

        var prefixes = literals.Where(l => l.EndsWith('.')).ToList();
        var unused = new List<string>();
        foreach (var key in englishKeys)
        {
            var relative = options.RequiredPrefix is { } p && key.StartsWith(p, StringComparison.Ordinal) ? key[p.Length..] : key;
            // 'name' and 'description' are read from the lang files by the host to localise the manifest text.
            if (options.RequiredPrefix is not null && (relative == "name" || relative == "description")) continue;
            if (literals.Contains(key) || literals.Contains(relative)) continue;
            if (prefixes.Any(pre => key.StartsWith(pre, StringComparison.Ordinal) || relative.StartsWith(pre, StringComparison.Ordinal))) continue;
            unused.Add(key);
        }
        unused.Sort(StringComparer.Ordinal);
        return unused;
    }
}
