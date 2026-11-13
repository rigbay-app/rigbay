using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Scenes;

/// <summary>What is wrong with a scene, or <see cref="None"/>. Each value has a language key <c>problem.&lt;name&gt;</c> (see <see cref="SceneRules.ProblemKey"/>).</summary>
internal enum SceneProblem { None, NoName, BadId, NoGames, TooManyGames, BadGame, NoSteps, TooManySteps, BadStep, BadUndo }

/// <summary>The limits of the host (<c>PlanEngine</c>) and the checks that keep a scene inside them, so a scene that the host would refuse is never offered for approval.</summary>
internal static class SceneRules
{
    public const int MaxSteps = 8, MaxArgs = 16, MaxArgLength = 256, MaxGames = 8, MaxGameLength = 260, MaxNameLength = 60;

    private static readonly Regex IdPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex ModulePattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex VerbPattern = new("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);

    /// <summary>The module's own id: a step may not target it (the host refuses that too).</summary>
    public const string OwnModule = "scenes";

    public static bool IsId(string? id) => id is { Length: > 0 and <= 48 } && IdPattern.IsMatch(id);

    public static string ProblemKey(SceneProblem problem) => "problem." + problem.ToString().ToLowerInvariant();

    public static SceneProblem Check(SceneSettings scene)
    {
        if (!IsId(scene.Id)) return SceneProblem.BadId;
        if (string.IsNullOrWhiteSpace(scene.Name)) return SceneProblem.NoName;
        var games = scene.Games ?? new();
        if (games.Count == 0) return SceneProblem.NoGames;
        if (games.Count > MaxGames) return SceneProblem.TooManyGames;
        if (games.Any(g => !Games.IsValid(g))) return SceneProblem.BadGame;
        var steps = scene.Steps ?? new();
        if (steps.Count == 0) return SceneProblem.NoSteps;
        if (steps.Count > MaxSteps) return SceneProblem.TooManySteps;
        foreach (var s in steps)
        {
            if (!IsStep(s.Module, s.Verb, s.Args)) return SceneProblem.BadStep;
            switch (s.Undo)
            {
                case UndoMode.None or UndoMode.Recorded: break;
                case UndoMode.Fixed when IsStep(s.UndoModule, s.UndoVerb, s.UndoArgs): break;
                default: return SceneProblem.BadUndo;
            }
        }
        return SceneProblem.None;
    }

    public static bool IsStep(string? module, string? verb, IReadOnlyList<string>? args) =>
        module is { Length: > 0 and <= 64 } && ModulePattern.IsMatch(module) && module != OwnModule
        && verb is { Length: > 0 and <= 64 } && VerbPattern.IsMatch(verb)
        && args is not null && args.Count <= MaxArgs && args.All(a => a is { Length: <= MaxArgLength });
}

/// <summary>The programs that start a scene.</summary>
internal static class Games
{
    private static readonly char[] Forbidden = { '<', '>', '|', '*', '?', '"', '\r', '\n', '\t', ':' };

    /// <summary>
    /// The stored form of what the owner typed: trimmed, without quotes, <c>.exe</c> added to a bare name. Returns null for something that is not a program name or a
    /// full path to one (a colon is allowed only as a drive letter).
    /// </summary>
    public static string? Normalise(string? text)
    {
        var t = (text ?? "").Trim().Trim('"').Trim();
        if (t.Length == 0 || t.Length > SceneRules.MaxGameLength) return null;
        bool isPath = t.Contains('\\') || t.Contains('/');
        if (isPath)
        {
            t = t.Replace('/', '\\');
            if (!(t.Length > 3 && char.IsAsciiLetter(t[0]) && t[1] == ':' && t[2] == '\\')) return null;   // rooted on a drive; no relative paths, no UNC (a share could change under the approval)
            if (t[3..].IndexOfAny(Forbidden) >= 0 || t.Contains("..", StringComparison.Ordinal)) return null;
        }
        else if (t.IndexOfAny(Forbidden) >= 0) return null;
        string file = Path.GetFileName(t);
        if (file.Length == 0 || file.StartsWith('.')) return null;
        if (!Path.HasExtension(file)) t += ".exe";
        else if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return t;
    }

    public static bool IsValid(string? stored) => Normalise(stored) is { } n && string.Equals(n, stored, StringComparison.Ordinal);

    public static bool HasPath(string entry) => entry.Contains('\\');

    /// <summary>The file name part, lower case: what a process is looked up by.</summary>
    public static string FileName(string entry) => Path.GetFileName(entry).ToLowerInvariant();

    /// <summary>Whether a running program is this entry: the file name, or the whole path when the entry has one. Case does not matter.</summary>
    public static bool Matches(string entry, RunningProgram program)
    {
        if (!string.Equals(Path.GetFileName(entry), program.Name, StringComparison.OrdinalIgnoreCase)) return false;
        return !HasPath(entry) || (program.Path is not null && string.Equals(entry, program.Path, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Command lines the way the page edits them: words separated by spaces, a word with spaces in double quotes.</summary>
internal static class CommandLine
{
    public static List<string>? Split(string? text)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text ?? "")
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) { words.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (quoted) return null;
        if (any) words.Add(sb.ToString());
        return words;
    }

    public static string Join(IEnumerable<string> words) =>
        string.Join(' ', words.Select(w => w.Length == 0 || w.Any(char.IsWhiteSpace) ? "\"" + w + "\"" : w));

    public static string Of(string module, string verb, IEnumerable<string> args) => Join(new[] { module, verb }.Concat(args));
}

/// <summary>A scene as a host plan, and the fingerprint that covers more than the plan does.</summary>
internal static class ScenePlans
{
    public static VerbPlan Build(SceneSettings scene) =>
        new(scene.Id, LocalisedText.Of("plan.name", string.IsNullOrWhiteSpace(scene.Name) ? scene.Id : scene.Name.Trim()),
            scene.Steps.Select(ToStep).ToList());

    private static PlanStep ToStep(StepSettings s) => new(s.Module, s.Verb, s.Args.ToList())
    {
        RestoreRecorded = s.Undo == UndoMode.Recorded,
        FixedUndo = s.Undo == UndoMode.Fixed ? new PlanStep(s.UndoModule, s.UndoVerb, s.UndoArgs.ToList()) : null,
    };

    /// <summary>
    /// SHA-256 over the programs and every step with its undo. The name is not in it: renaming a scene does not ask again. The host's hash covers the steps and the
    /// versions of the modules they call; this adds the programs, which only this module knows.
    /// </summary>
    public static string Fingerprint(SceneSettings scene)
    {
        var sb = new StringBuilder();
        static void F(StringBuilder b, string? s) { s ??= ""; b.Append(s.Length).Append(':').Append(s).Append(';'); }
        F(sb, scene.Id);
        F(sb, scene.Games.Count.ToString());
        foreach (var g in scene.Games) F(sb, g.ToLowerInvariant());
        F(sb, scene.Steps.Count.ToString());
        foreach (var s in scene.Steps)
        {
            F(sb, s.Module); F(sb, s.Verb); F(sb, s.Args.Count.ToString());
            foreach (var a in s.Args) F(sb, a);
            F(sb, s.Undo);
            if (s.Undo == UndoMode.Fixed)
            {
                F(sb, s.UndoModule); F(sb, s.UndoVerb); F(sb, s.UndoArgs.Count.ToString());
                foreach (var a in s.UndoArgs) F(sb, a);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>The command lines of a scene as the page lists them ("xtu apply Gaming").</summary>
    public static string Line(StepSettings s) => CommandLine.Of(s.Module, s.Verb, s.Args);

    public static string UndoLine(StepSettings s) => s.Undo == UndoMode.Fixed ? CommandLine.Of(s.UndoModule, s.UndoVerb, s.UndoArgs) : "";
}

/// <summary>A suggestion for the undo of a step, only for the pairs everybody knows. The owner can change it.</summary>
internal static class UndoGuess
{
    private static readonly Dictionary<string, string> Pairs = new(StringComparer.Ordinal)
    {
        ["on"] = "off", ["off"] = "on", ["start"] = "stop", ["stop"] = "start", ["enable"] = "disable", ["disable"] = "enable",
    };

    /// <summary>The command line that probably puts back <c>module verb args</c>, or null.</summary>
    public static string? For(string module, string verb, IReadOnlyList<string> args) =>
        Pairs.TryGetValue(verb, out var other) ? CommandLine.Of(module, other, args) : null;
}
