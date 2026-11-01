using System.Reflection;
using System.Runtime.Loader;

namespace Rigbay.Core;

/// <summary>
/// One <see cref="AssemblyLoadContext"/> per module. Dependencies are resolved from the module's own folder (through
/// its <c>deps.json</c> when present, else by file name), but the host's default context wins for
/// <c>Rigbay.Abstractions</c>, the framework and the UI framework assemblies, so there is exactly one copy of each type
/// the module and the host exchange. Not collectible: a module is never unloaded, install and update need a restart.
/// </summary>
public sealed class ModuleLoadContext : AssemblyLoadContext
{
    /// <summary>Assembly name prefixes that always come from the host (the module's own copy, if it ships one, is ignored).</summary>
    private static readonly string[] HostPrefixes =
    {
        "Rigbay.Abstractions", "System.", "Microsoft.NETCore.", "Microsoft.Win32.", "Microsoft.WinUI", "Microsoft.UI.",
        "Microsoft.Windows.", "WinRT.", "WindowsBase",
    };

    private static readonly string[] HostExact = { "System", "mscorlib", "netstandard", "WindowsBase" };

    private readonly string _folder;
    private readonly AssemblyDependencyResolver _resolver;

    public ModuleLoadContext(string moduleId, string entryAssemblyPath)
        : base("module:" + moduleId, isCollectible: false)
    {
        _folder = Path.GetDirectoryName(entryAssemblyPath)!;
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    public Assembly LoadEntry(string entryAssemblyPath) => LoadFromAssemblyPath(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null) return null;
        if (HostExact.Contains(name) || HostPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            return null; // default context

        // Anything the host process already loaded wins too (one copy of a shared library).
        if (Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal)))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            var guess = Path.Combine(_folder, name + ".dll");
            if (File.Exists(guess)) path = guess;
        }
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
