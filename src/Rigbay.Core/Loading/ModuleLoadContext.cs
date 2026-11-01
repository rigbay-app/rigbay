using System.Reflection;
using System.Runtime.Loader;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>
/// One <see cref="AssemblyLoadContext"/> per module. Not collectible: a module is never unloaded, install and update
/// need a restart.
/// <para>
/// Rule for every assembly the module asks for, in this order (the first that has it wins; the winner is logged at debug level):
/// </para>
/// <list type="number">
/// <item><b>Host.</b> The host's default context has it (loaded already, or loadable there at a version that satisfies the
/// request): the shared framework, <c>Rigbay.Abstractions</c>, WinUI/WinRT, anything the app ships. The module's own copy is
/// ignored, so there is one copy of every type the module and the host exchange.</item>
/// <item><b>Module folder.</b> Its <c>deps.json</c>, else <c>&lt;name&gt;.dll</c> next to the entry assembly.</item>
/// <item><b>Probe paths.</b> The folders the module declared in <c>probePaths</c> (<c>&lt;name&gt;.dll</c> there).</item>
/// <item>Nothing: the load fails (<see cref="FileNotFoundException"/>).</item>
/// </list>
/// <c>Rigbay.Abstractions</c> never comes from a module or a probe path.
/// </summary>
public sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly string _folder;
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyList<string> _probePaths;
    private readonly ILog? _log;

    public ModuleLoadContext(string moduleId, string entryAssemblyPath, IReadOnlyList<string>? probePaths = null, ILog? log = null)
        : base("module:" + moduleId, isCollectible: false)
    {
        _folder = Path.GetDirectoryName(entryAssemblyPath)!;
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _probePaths = probePaths ?? Array.Empty<string>();
        _log = log;
    }

    public Assembly LoadEntry(string entryAssemblyPath) => LoadFromAssemblyPath(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null) return null;

        // 1. Host
        var host = FromHost(assemblyName);
        if (host is not null)
        {
            _log?.Debug($"{name} {assemblyName.Version}: host copy {host.GetName().Version}");
            return host;
        }
        if (string.Equals(name, "Rigbay.Abstractions", StringComparison.Ordinal)) return null; // never a module's copy

        // 2. Module folder
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            var guess = Path.Combine(_folder, name + ".dll");
            if (File.Exists(guess)) path = guess;
        }
        if (path is not null)
        {
            _log?.Debug($"{name} {assemblyName.Version}: module copy {path}");
            return LoadFromAssemblyPath(path);
        }

        // 3. Declared probe paths
        foreach (var dir in _probePaths)
        {
            var candidate = Path.Combine(dir, name + ".dll");
            if (!File.Exists(candidate)) continue;
            _log?.Debug($"{name} {assemblyName.Version}: probe path copy {candidate}");
            return LoadFromAssemblyPath(candidate);
        }

        _log?.Debug($"{name} {assemblyName.Version}: not found in the host, the module folder or the probe paths");
        return null;
    }

    private static Assembly? FromHost(AssemblyName request)
    {
        foreach (var a in Default.Assemblies)
            if (string.Equals(a.GetName().Name, request.Name, StringComparison.Ordinal) && Satisfies(a.GetName().Version, request.Version)) return a;
        try { return Default.LoadFromAssemblyName(request); }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { return null; }
    }

    private static bool Satisfies(Version? have, Version? wanted) => wanted is null || have is null || have >= wanted;

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is not null) return LoadUnmanagedDllFromPath(path);
        foreach (var dir in _probePaths)
        {
            var candidate = Path.Combine(dir, unmanagedDllName);
            if (!candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) candidate += ".dll";
            if (File.Exists(candidate)) return LoadUnmanagedDllFromPath(candidate);
        }
        return IntPtr.Zero;
    }
}
