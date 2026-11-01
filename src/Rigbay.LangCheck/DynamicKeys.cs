using System.Text.Json;
using Rigbay.Core;

namespace Rigbay.LangCheckTool;

/// <summary>
/// Declared dynamic keys. Some keys are built at run time (<c>"core.install.phase." + phase</c>), so no string literal
/// in the source spells them and the "unused" check would flag them. A <c>langcheck.json</c> next to the lang files
/// declares them:
/// <code>{ "dynamic": [ "core.install.phase.*", "modules.update-available" ] }</code>
/// An entry is either a key prefix ending in <c>.*</c> (a family) or one exact key. It matches a key written in full
/// or, for a module, without the <c>module.&lt;id&gt;.</c> prefix. It only silences the "unused" warning: missing keys,
/// unknown keys, placeholders and the prefix rule still apply. An entry that matches no English key is an error
/// (stale), so the file cannot drift away from the lang files. Comments (<c>//</c>) are allowed in the file.
/// </summary>
public static class DynamicKeys
{
    public const string FileName = "langcheck.json";

    /// <summary>One declared entry: an exact key, or a prefix (ends in '.') when <see cref="IsFamily"/>.</summary>
    public sealed record Entry(string Text, bool IsFamily)
    {
        public string Declared => IsFamily ? Text + "*" : Text;
    }

    /// <summary>Reads the declared entries. Problems are errors for the report.</summary>
    public static (List<Entry> Entries, List<string> Problems) Load(string folder)
    {
        var entries = new List<Entry>();
        var problems = new List<string>();
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return (entries, problems);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { problems.Add($"{path}: the file must be a JSON object like {{ \"dynamic\": [ \"prefix.*\" ] }}"); return (entries, problems); }
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name != "dynamic") { problems.Add($"{path}: unknown property '{prop.Name}' (only \"dynamic\" exists)"); continue; }
                if (prop.Value.ValueKind != JsonValueKind.Array) { problems.Add($"{path}: \"dynamic\" must be an array of strings"); continue; }
                foreach (var item in prop.Value.EnumerateArray())
                {
                    var text = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : "";
                    int star = text.IndexOf('*');
                    if (text.Length == 0 || text.Any(char.IsWhiteSpace) || (star >= 0 && (star != text.Length - 1 || !text.EndsWith(".*", StringComparison.Ordinal) || text.Length < 3)))
                        problems.Add($"{path}: {item} is not a key or a family: write a full key, or a key prefix followed by .* (for example core.install.phase.*)");
                    else entries.Add(star >= 0 ? new Entry(text[..^1], true) : new Entry(text, false));
                }
            }
        }
        catch (JsonException e) { problems.Add($"{path}: {e.Message}"); }
        return (entries, problems);
    }

    /// <summary>Removes the declared keys from the unused list; an entry that matches no English key becomes a problem.</summary>
    public static void Apply(LangCheckReport report, IReadOnlyList<Entry> entries, IEnumerable<string> englishKeys, string? requiredPrefix)
    {
        if (entries.Count == 0) return;
        var keys = englishKeys.ToList();
        bool Matches(string key, Entry e)
        {
            bool Test(string k) => e.IsFamily ? k.StartsWith(e.Text, StringComparison.Ordinal) : k == e.Text;
            return Test(key) || (requiredPrefix is not null && key.StartsWith(requiredPrefix, StringComparison.Ordinal) && Test(key[requiredPrefix.Length..]));
        }

        foreach (var e in entries)
            if (!keys.Any(k => Matches(k, e)))
                report.Problems.Add($"{FileName}: '{e.Declared}' matches no English key (stale declaration?)");
        report.Unused.RemoveAll(k => entries.Any(e => Matches(k, e)));
    }
}
