using System.Diagnostics;
using Rigbay.Abstractions;
using Rigbay.Core;
using Rigbay.Core.Install;

namespace Rigbay.App.Services;

/// <summary>
/// The app's side of install, update and remove (R1, docs/INSTALL.md): applies the pending changes before the loader runs, builds the
/// <see cref="ModuleManager"/> the Modules page talks to, checks the registry when asked (or once a day when the user switched that on),
/// and deletes the kept previous version after a healthy start.
/// </summary>
static class InstallServices
{
    static readonly List<string> StartupLines = new();
    static HttpClient? _http;

    public static InstallPaths Paths { get; private set; } = new(AppPaths.ModulesRoot);
    public static InstallPolicy Policy { get; private set; } = InstallPolicy.Production;
    public static Uri RegistryUrl { get; private set; } = InstallPolicy.DefaultRegistryUrl;

    /// <summary>True when <c>--registry</c> pointed the app at a local registry (scratch folders only).</summary>
    public static bool LocalRegistry { get; private set; }

    public static ModuleManager Manager { get; private set; } = null!;
    public static PendingRunReport? StartupReport { get; private set; }

    static IFolderSecurity Security { get; } = new AdminFolderSecurity();

    /// <summary>
    /// Reads the test switch <c>--registry &lt;file or url&gt;</c>. It is honoured only when <c>--data</c> and <c>--modules</c> name scratch
    /// folders (never the real <c>%ProgramData%\Rigbay\modules</c>): the registry is the trust anchor, so a product build offers no way to
    /// replace it.
    /// </summary>
    public static void Configure(Options opt)
    {
        Paths = new InstallPaths(AppPaths.ModulesRoot);
        Policy = InstallPolicy.Production;
        RegistryUrl = InstallPolicy.DefaultRegistryUrl;
        LocalRegistry = false;
        if (string.IsNullOrWhiteSpace(opt.Registry)) return;
        bool scratch = opt.Data != null && opt.Modules != null && !AppPaths.IsDefaultModulesRoot;
        if (!scratch)
        {
            StartupLines.Add("WARN --registry ignored: it needs --data and --modules naming scratch folders, and never the real modules folder");
            return;
        }
        var text = opt.Registry!;
        RegistryUrl = File.Exists(text) ? new Uri(Path.GetFullPath(text)) : new Uri(text);
        Policy = InstallPolicy.ForLocalTesting(opt.SlowDownloadMs > 0 ? TimeSpan.FromMilliseconds(opt.SlowDownloadMs) : null);
        LocalRegistry = true;
        StartupLines.Add($"INFO test registry: {RegistryUrl} (local sources allowed, scratch folders only)");
    }

    /// <summary>Called from <c>Program.Main</c> before any XAML or module code: nothing is loaded yet, so the swap can rename folders.</summary>
    public static void ApplyPendingAtStart()
    {
        try
        {
            StartupReport = PendingRunner.Run(Paths, Security, AppPaths.DataRoot, line => StartupLines.Add("INFO install: " + line));
        }
        catch (Exception ex)
        {
            StartupLines.Add("ERROR install: applying pending changes failed: " + ex);
        }
    }

    public static void Create(RigbayHost core)
    {
        var log = core.Log.ForSource("install");
        _http = SafeDownloader.CreateHttpClient(Policy);
        var downloader = new SafeDownloader(_http, Policy);
        var installer = new ModuleInstaller(Paths, Policy, downloader, Security, HostApi.Current, log);
        var registry = new RegistryClient(downloader, Policy, Paths, Security, RegistryUrl, log);
        Manager = new ModuleManager(installer, registry, new HostInstalledModules(core), HostApi.Current, log);
    }

    /// <summary>Writes what happened before the host existed (pending changes applied, a refused test switch) into the Rigbay log.</summary>
    public static void FlushStartupLog()
    {
        foreach (var line in StartupLines)
        {
            if (line.StartsWith("WARN ")) AppLog.Warn(line[5..]);
            else if (line.StartsWith("ERROR ")) AppLog.Error(line[6..]);
            else AppLog.Info(line.StartsWith("INFO ") ? line[5..] : line);
        }
        StartupLines.Clear();
    }

    /// <summary>After the modules started: forget the kept previous versions of the modules that are healthy, take the registry copy from disk, schedule the daily check.</summary>
    public static void AfterStart(RigbayHost core)
    {
        try
        {
            PendingRunner.CommitHealthy(Paths, id => core.Find(id) is not { } e || e.Lifecycle is not (ModuleLifecycle.Invalid or ModuleLifecycle.Incompatible or ModuleLifecycle.Faulted), line => AppLog.Info("install: " + line));
        }
        catch (Exception ex) { AppLog.Warn("could not clean up previous module versions", ex); }
        Manager.LoadCache();
    }

    /// <summary>"Check for updates": one request to the registry. Remembers when, so the daily check does not repeat it.</summary>
    public static async Task CheckNowAsync()
    {
        AppState.Settings.LastModuleCheck = DateTimeOffset.UtcNow;
        AppState.Save();
        await Manager.CheckAsync();
    }

    /// <summary>True when the user switched the daily check on and the last attempt is older than a day.</summary>
    public static bool DailyCheckDue(DateTimeOffset now) =>
        AppState.Settings.CheckDaily && (AppState.Settings.LastModuleCheck is not { } last || now - last >= TimeSpan.FromHours(24));
}

/// <summary>Restart of the app for "Restart now": the new process waits for this one to exit, then takes over the single-instance slot.</summary>
static class Relaunch
{
    public const string WaitSwitch = "--wait-pid";

    // Test-only switches that drive one run (a script, a screenshot, a self-test) are not carried into the successor: a restart must not repeat them.
    static readonly HashSet<string> DropWithValue = new(StringComparer.OrdinalIgnoreCase) { WaitSwitch, "--script", "--shot", "--shot-mode", "--shot-scale", "--exit-after", "--selftest-out", "--page", "--size", "--accent" };
    static readonly HashSet<string> DropFlags = new(StringComparer.OrdinalIgnoreCase) { "--tray", "--selftest", "--first-run" };   // a restart the user asked for shows the window

    /// <summary>The arguments for the successor: the original ones without the switches above, plus <c>--wait-pid</c>.</summary>
    internal static List<string> SuccessorArguments(string[] originalArgs, int pid)
    {
        var args = new List<string>();
        for (int i = 0; i < originalArgs.Length; i++)
        {
            if (DropWithValue.Contains(originalArgs[i])) { i++; continue; }
            if (DropFlags.Contains(originalArgs[i])) continue;
            args.Add(originalArgs[i]);
        }
        args.Add(WaitSwitch);
        args.Add(pid.ToString());
        return args;
    }

    /// <summary>Starts a copy of this program (elevated, as this one is) that waits for this process to end. Throws when it cannot be started.</summary>
    public static void StartSuccessor(string[] originalArgs)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("the path of the program is not known");
        var args = SuccessorArguments(originalArgs, Environment.ProcessId);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
        // the program's manifest asks for administrator, so the shell elevates the child exactly as it elevated this process
        psi.Arguments = string.Join(" ", args.Select(Quote));
        using var started = Process.Start(psi);
        if (started == null) throw new InvalidOperationException("the new process did not start");
    }

    static string Quote(string a) => a.Length > 0 && a.All(c => !char.IsWhiteSpace(c) && c != '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    /// <summary>In the successor: wait (up to a minute) for the process that started it.</summary>
    public static void WaitForPredecessor(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(60_000);
        }
        catch (ArgumentException) { }   // already gone
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
