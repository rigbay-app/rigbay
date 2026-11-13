using System.Diagnostics;

namespace Rigbay.Scenes.Windows;

/// <summary>
/// The process list through <see cref="Process"/>. Reads only: it looks up processes by name (no scan of all of them while a scene waits), reads the path of the ones that
/// matched when a scene needs it, and never touches a process it found (no kill, no priority, no affinity).
/// </summary>
internal sealed class WindowsProcesses : IProcessSource
{
    public IReadOnlyList<RunningProgram> Find(IReadOnlyCollection<string> exeNames, bool withPath)
    {
        var found = new List<RunningProgram>();
        foreach (var exe in exeNames)
        {
            string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (Exception) { continue; }
            foreach (var p in processes)
            {
                try
                {
                    string? path = null;
                    if (withPath)
                    {
                        try { path = p.MainModule?.FileName; }
                        catch (Exception) { path = null; }   // access denied or the process is ending
                    }
                    found.Add(new RunningProgram(p.ProcessName.ToLowerInvariant() + ".exe", path, p.Id));
                }
                catch (Exception) { /* ended while we looked */ }
                finally { p.Dispose(); }
            }
        }
        return found;
    }

    public IReadOnlyList<string> WindowedPrograms()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception) { return Array.Empty<string>(); }
        foreach (var p in all)
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) names.Add(p.ProcessName.ToLowerInvariant() + ".exe"); }
            catch (Exception) { /* ended while we looked */ }
            finally { p.Dispose(); }
        }
        return names.ToList();
    }
}
