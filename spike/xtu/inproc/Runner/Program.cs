// Host stand-in for design A. Loads XtuModule.dll into its own AssemblyLoadContext (like the Rigbay loader does),
// resolves the module's NuGet dependencies from its .deps.json, and resolves Intel's assemblies from the Intel folder
// in place (nothing is copied). Then calls XtuModule.XtuReader.Run by reflection.
using System.Reflection;
using System.Runtime.Loader;

string modulePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "module", "XtuModule.dll"));
int repeat = 20;
bool noIntelHook = false, trace = false;
string? wire = null, wireFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--wire") { wire = args[++i]; if (wire == "load") wireFile = args[++i]; continue; }
    if (args[i] == "--module") modulePath = Path.GetFullPath(args[++i]);
    else if (args[i] == "--repeat") repeat = int.Parse(args[++i]);
    else if (args[i] == "--no-intel-hook") noIntelHook = true;   // negative test: what happens without the resolve hook
    else if (args[i] == "--trace") trace = true;
}

Console.WriteLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, pid {Environment.ProcessId}");
Console.WriteLine($"module: {modulePath}");
Console.WriteLine("files next to the module: " + string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(modulePath)!).Select(Path.GetFileName).Where(n => !n!.EndsWith(".pdb"))));

double Ws() => Math.Round(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1);
Console.WriteLine($"working set before loading the module: {Ws()} MB");
var alc = new ModuleContext(modulePath, noIntelHook, trace);
var asm = alc.LoadFromAssemblyPath(modulePath);
if (wire != null)
{
    // Offline serialisation check of the write-path types; makes no call to the XTU service.
    var probe = asm.GetType("XtuModule.WireProbe")!;
    object? res = wire == "dump" ? probe.GetMethod("Dump")!.Invoke(null, null)
                                 : probe.GetMethod("Load")!.Invoke(null, new object[] { File.ReadAllText(wireFile!) });
    Console.Write((string)res!);
    return 0;
}
var run = asm.GetType("XtuModule.XtuReader")!.GetMethod("Run")!;
Console.WriteLine(new string('-', 70));
try { Console.WriteLine((string)run.Invoke(null, new object[] { repeat })!); }
catch (TargetInvocationException ex) { Console.WriteLine("INVOKE FAILED: " + ex.InnerException); return 1; }
Console.WriteLine(new string('-', 70));
Console.WriteLine($"working set after the run: {Ws()} MB");
Console.WriteLine("assemblies loaded into the module context:");
foreach (var a in alc.Assemblies) Console.WriteLine($"  {a.GetName().Name} {a.GetName().Version}  {(string.IsNullOrEmpty(a.Location) ? "" : a.Location)}");
return 0;

sealed class ModuleContext : AssemblyLoadContext
{
    private const string IntelDir = @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers";
    private readonly AssemblyDependencyResolver resolver;
    private readonly bool noIntelHook, trace;

    public ModuleContext(string modulePath, bool noIntelHook, bool trace) : base("xtu-module", isCollectible: false)
    {
        resolver = new AssemblyDependencyResolver(modulePath);
        this.noIntelHook = noIntelHook; this.trace = trace;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // 1. the module's own dependencies (deps.json)
        var p = resolver.ResolveAssemblyToPath(name);
        if (p != null) { if (trace) Console.WriteLine($"  [alc] {name} -> {p}"); return LoadFromAssemblyPath(p); }
        // 2. Intel's assemblies, in place
        if (!noIntelHook && name.Name is "XtuInterface" or "XtuCommon" or "Microsoft.Practices.Unity" or "Microsoft.Win32.TaskScheduler")
        {
            var ip = Path.Combine(IntelDir, name.Name + ".dll");
            if (File.Exists(ip)) { if (trace) Console.WriteLine($"  [alc] {name} -> {ip}"); return LoadFromAssemblyPath(ip); }
        }
        if (trace) Console.WriteLine($"  [alc] {name} -> (default context)");
        return null;   // fall back to the default context (host copy of shared assemblies, framework)
    }
}
