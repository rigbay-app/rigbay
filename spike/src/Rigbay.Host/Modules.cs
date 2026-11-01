using System.Reflection;
using System.Runtime.Loader;
using Rigbay.Abstractions;

namespace Rigbay.Host;

enum ModuleState { Ready, Unsupported, Failed }

/// <summary>One bay: a module folder, its load result and (once built) its page.</summary>
sealed class ModuleEntry
{
    public string Folder = "";
    public string Id = "";
    public string Name = "";
    public string Version = "";
    public ModuleState State;
    public string Reason = "";
    public IRigbayModule? Module;
    public object? Page;
    public PageModel? Model;
    public string LoadContext = "";
}

/// <summary>Loads each module into its own AssemblyLoadContext. Shared assemblies (Abstractions, WinUI, WinRT) always come from the host.</summary>
sealed class ModuleLoadContext : AssemblyLoadContext
{
    readonly AssemblyDependencyResolver _resolver;
    public ModuleLoadContext(string mainDll) : base("module:" + Path.GetFileNameWithoutExtension(mainDll), isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(mainDll);

    protected override Assembly? Load(AssemblyName name)
    {
        // Anything the host already has wins: this keeps one copy of Microsoft.WinUI and one IRigbayModule.
        try { var shared = Default.LoadFromAssemblyName(name); if (shared != null) return shared; } catch (FileNotFoundException) { }
        var path = _resolver.ResolveAssemblyToPath(name);
        return path == null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path == null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}

static class ModuleLoader
{
    public static List<ModuleEntry> LoadAll(string dir)
    {
        var list = new List<ModuleEntry>();
        if (!Directory.Exists(dir)) return list;
        foreach (var folder in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            var name = Path.GetFileName(folder);
            var e = new ModuleEntry { Folder = folder, Id = name, Name = name };
            try
            {
                var dll = Path.Combine(folder, name + ".dll");
                if (!File.Exists(dll)) throw new FileNotFoundException($"{name}.dll not found in the module folder");
                var alc = new ModuleLoadContext(dll);
                e.LoadContext = alc.Name ?? "";
                var asm = alc.LoadFromAssemblyPath(dll);
                Type? type;
                try { type = asm.GetTypes().FirstOrDefault(t => typeof(IRigbayModule).IsAssignableFrom(t) && !t.IsAbstract); }
                catch (ReflectionTypeLoadException ex) { throw new TypeLoadException("module types did not load: " + ex.LoaderExceptions.FirstOrDefault(x => x != null)?.Message, ex); }
                if (type == null)
                {
                    var refs = asm.GetReferencedAssemblies().Any(r => r.Name == "Rigbay.Abstractions");
                    throw new InvalidOperationException(refs ? "no class implements IRigbayModule" : "module does not reference Rigbay.Abstractions");
                }
                var m = (IRigbayModule)Activator.CreateInstance(type)!;
                e.Module = m; e.Id = m.Id; e.Name = m.Name; e.Version = m.Version.ToString();
                var probe = m.Probe();                         // fault boundary: throws land in catch below
                e.State = probe.Supported ? ModuleState.Ready : ModuleState.Unsupported;
                e.Reason = probe.Reason;
            }
            catch (Exception ex)
            {
                e.State = ModuleState.Failed;
                e.Reason = ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message);
            }
            list.Add(e);
        }
        return list;
    }
}
