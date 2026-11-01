// net48 entry for the offline wire check. Usage: WireProbe48.exe dump | load FILE
// Intel assemblies are resolved in place, as in the helper. Makes no call to the XTU service.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XtuModule
{
    internal static class WireProbeMain
    {
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
            {
                string p = Path.Combine(@"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers", new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            return Go(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Go(string[] args)
        {
            if (args.Length >= 1 && args[0] == "dump") Console.Write(WireProbe.Dump());
            else if (args.Length >= 2 && args[0] == "load") Console.Write(WireProbe.Load(File.ReadAllText(args[1])));
            else return 2;
            return 0;
        }
    }
}
