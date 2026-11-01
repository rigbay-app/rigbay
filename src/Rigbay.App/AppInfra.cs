using System.Diagnostics;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>Where the app looks for modules and keeps data. <c>--data</c> and <c>--modules</c> override (tests and screenshots use them).</summary>
static class AppPaths
{
    public static string? DataRoot { get; private set; }
    public static string ModulesRoot { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rigbay", "modules");
    public static string HostLangDir => Path.Combine(AppContext.BaseDirectory, "lang");

    public static void Init(string? dataRoot, string? modulesRoot)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot)) DataRoot = Path.GetFullPath(dataRoot);
        if (!string.IsNullOrWhiteSpace(modulesRoot)) ModulesRoot = Path.GetFullPath(modulesRoot);
    }

    public static RigbayHostOptions HostOptions() => new()
    {
        ModulesRoot = ModulesRoot,
        DataRoot = DataRoot,
        HostVersion = Version,
    };

    public static Version Version { get; } = typeof(AppPaths).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 1, 0);
}

/// <summary>The app's own log lines (source "host") in the one Rigbay log; before the host exists they go to the debugger only.</summary>
static class AppLog
{
    static ILog? _log;
    public static void Attach(RigbayHost host) => _log = host.Log.ForSource("host");
    /// <summary>One log line in the form the Log page and the file show.</summary>
    public static string FormatLine(LogEntry e) =>
        $"{e.Time:yyyy-MM-dd HH:mm:ss.fff} {e.Level.ToString().ToUpperInvariant(),-5} {e.Source,-12} {e.Message}" + (e.Exception is null ? "" : " :: " + e.Exception.ReplaceLineEndings(" | "));
    public static ILog For(string source) => Host.Core?.Log.ForSource(source) ?? new DebugLog(source);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
    static void Write(LogLevel level, string message, Exception? ex)
    {
        if (_log != null) _log.Write(level, message, ex);
        else Debug.WriteLine($"[host {level}] {message} {ex}");
    }

    sealed class DebugLog(string source) : ILog
    {
        public void Write(LogLevel level, string message, Exception? exception = null) => Debug.WriteLine($"[{source} {level}] {message} {exception}");
    }
}

/// <summary>The app's own choices that the core does not keep: theme, log level, modules whose declarations the user has seen.</summary>
sealed class AppSettings
{
    public string Theme { get; set; } = "system";
    public string LogLevel { get; set; } = "info";
    public List<string> Acknowledged { get; set; } = new();
}

static class AppState
{
    static JsonSettingsStore? _store;
    public static AppSettings Settings { get; private set; } = new();

    public static void Load(RigbayHost host)
    {
        _store = new JsonSettingsStore(Path.Combine(host.Paths.DataRoot, "app.json"), host.Log.ForSource("host"));
        Settings = _store.Load<AppSettings>();
        if (Enum.TryParse<LogLevel>(Settings.LogLevel, true, out var level)) host.Log.Minimum = level;
    }

    public static void Save()
    {
        try { _store?.Save(Settings); } catch (Exception ex) { AppLog.Warn("could not save app.json", ex); }
    }

    public static bool IsAcknowledged(ModuleEntry e) => Settings.Acknowledged.Contains(e.Id);

    public static void Acknowledge(ModuleEntry e)
    {
        if (Settings.Acknowledged.Contains(e.Id)) return;
        Settings.Acknowledged.Add(e.Id);
        Save();
    }
}

/// <summary>Reading a <see cref="ModuleEntry"/> the way the pages need it.</summary>
static class EntryExtensions
{
    public static string ReasonText(this ModuleEntry e) => e.Bay.Reason is { } r ? e.ResolveReason(r) : "";
    public static string VersionText(this ModuleEntry e) => e.Manifest?.Version ?? "";
    public static string AuthorText(this ModuleEntry e) => e.Manifest?.Author ?? "";
    public static string GroupKey(this ModuleEntry e) => (e.Manifest?.Group ?? ModuleGroup.Other).ToString().ToLowerInvariant();
    public static bool CanToggle(this ModuleEntry e) => e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running;
}


static class ReasonExtensions
{
    /// <summary>
    /// A reason that fits one line (dashboard row, modules list). An invalid manifest becomes "N problems in module.json" (the
    /// list itself is on the module page, see <see cref="IssueLines"/>); any other reason is the full text on one line, which
    /// the row trims with an ellipsis and shows in full as its tooltip.
    /// </summary>
    public static string ShortReasonText(this ModuleEntry e) => e.ShortReasonText(Host.Core.Languages);

    /// <summary>Same, with the languages given (the headless <c>list</c> verb has no <see cref="Host"/>).</summary>
    public static string ShortReasonText(this ModuleEntry e, Rigbay.Core.LanguageCatalog languages)
    {
        if (e.Bay.Reason is not { } r) return "";
        var issues = r.Args.OfType<IReadOnlyList<ManifestIssue>>().FirstOrDefault();
        if (issues != null)
        {
            int n = issues.Count(i => i.IsError);
            return n == 1 ? languages.T("reason.invalid-one") : languages.F("reason.invalid-many", n);
        }
        return e.ResolveReason(r).ReplaceLineEndings(" ");
    }

    /// <summary>The manifest problems of a module, one localised line each ("entry.type: must be ..."), warnings included.</summary>
    public static IReadOnlyList<string> IssueLines(this ModuleEntry e) =>
        e.Issues.Select(i => (i.IsError ? "" : Host.T("fact.warning-prefix") + " ") + i.LocaliseLine(Host.Core.Languages)).ToList();
}

/// <summary>
/// Runs a command line for the app. Identical to <see cref="RigbayHost.ExecuteCommandAsync"/> except for the bare <c>list</c> verb,
/// whose reason column is the short form ("3 problems in module.json") instead of the core's full multi-problem text; the full
/// text stays in the log and on the module page.
/// </summary>
static class AppCommands
{
    public static Task<CommandOutcome> ExecuteAsync(RigbayHost core, IReadOnlyList<string> words)
    {
        if (words.Count == 1 && words[0] == "list") return Task.FromResult(new CommandOutcome(CommandOutcome.Ok, List(core)));
        return core.ExecuteCommandAsync(words);
    }


    /// <summary>The reason column of the <c>list</c> verb: for an invalid manifest the first problem and how many more there are; otherwise the one-line reason.</summary>
    static string CliReason(ModuleEntry m, Rigbay.Core.LanguageCatalog languages)
    {
        var errors = m.Issues.Where(i => i.IsError).ToList();
        if (m.Bay.Reason is { } r && r.Args.OfType<IReadOnlyList<ManifestIssue>>().Any() && errors.Count > 0)
        {
            var first = errors[0].LocaliseLine(languages).ReplaceLineEndings(" ");
            return errors.Count == 1 ? first : languages.F("reason.cli-more", first, errors.Count - 1);
        }
        return m.ShortReasonText(languages);
    }
    public static string List(RigbayHost core)
    {
        var modules = core.Modules;
        if (modules.Count == 0) return core.Languages.T("core.command.no-modules");
        var sb = new System.Text.StringBuilder();
        foreach (var m in modules)
        {
            sb.Append($"{m.Id}  {m.Bay.State}  {m.Manifest?.Version ?? "-"}  {(core.IsEnabledByUser(m.Id) ? "enabled" : "disabled")}");
            var reason = CliReason(m, core.Languages);
            if (reason.Length > 0) sb.Append("  ").Append(reason);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
