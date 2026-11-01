// Design A through the real Rigbay.Core.ModuleLoadContext. Usage: CoreCheck.exe [--module PATH] [--repeat N]
using System.Reflection;
using Rigbay.Core;

if (args.Contains("--no-hook")) Environment.SetEnvironmentVariable("XTU_SPIKE_NO_HOOK", "1");
string modulePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "module", "XtuModule.dll"));
int repeat = 3;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--module") modulePath = Path.GetFullPath(args[++i]);
    else if (args[i] == "--repeat") repeat = int.Parse(args[++i]);
}
Console.WriteLine($"runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; module {modulePath}");
Console.WriteLine("XTU_SPIKE_NO_HOOK=" + (Environment.GetEnvironmentVariable("XTU_SPIKE_NO_HOOK") ?? "(unset)"));

var ctx = new ModuleLoadContext("xtu-spike", modulePath);
var asm = ctx.LoadEntry(modulePath);
var run = asm.GetType("XtuModule.XtuReader")!.GetMethod("Run")!;
try { Console.WriteLine((string)run.Invoke(null, new object[] { repeat })!); }
catch (TargetInvocationException ex) { Console.WriteLine("INVOKE FAILED: " + ex.InnerException?.GetType().FullName + ": " + ex.InnerException?.Message); return 1; }
Console.WriteLine("assemblies in the module context: " + string.Join(", ", ctx.Assemblies.Select(a => a.GetName().Name)));
return 0;
