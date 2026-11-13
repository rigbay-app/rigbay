using System.Diagnostics;
using System.Globalization;

namespace Rigbay.App.Services;

/// <summary>One reading of this process's memory and load, for the tray trim and for <c>tools\perf\measure.ps1</c>.</summary>
/// <param name="WorkingSetMb">Pages of the process that are in RAM now.</param>
/// <param name="PrivateMb">Private bytes: committed memory that only this process uses (the working set can shrink without this changing).</param>
/// <param name="ManagedMb">Bytes the .NET heap holds in live and not yet collected objects (<c>GC.GetTotalMemory(false)</c>).</param>
/// <param name="GcHeapMb">Heap size as of the last collection (<c>GCMemoryInfo.HeapSizeBytes</c>).</param>
/// <param name="GcCommittedMb">Memory the GC has committed (<c>GCMemoryInfo.TotalCommittedBytes</c>).</param>
/// <param name="GcFragmentedMb">Free space between live objects as of the last collection.</param>
/// <param name="CpuMs">Processor time of the process so far (user plus kernel).</param>
readonly record struct MemSnapshot(double WorkingSetMb, double PrivateMb, double ManagedMb, double GcHeapMb, double GcCommittedMb, double GcFragmentedMb, int Threads, int Handles, double CpuMs, long WallMs)
{
    const double Mb = 1024.0 * 1024.0;

    public static MemSnapshot Take()
    {
        using var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new MemSnapshot(p.WorkingSet64 / Mb, p.PrivateMemorySize64 / Mb, GC.GetTotalMemory(false) / Mb, gc.HeapSizeBytes / Mb, gc.TotalCommittedBytes / Mb, gc.FragmentedBytes / Mb,
            p.Threads.Count, p.HandleCount, p.TotalProcessorTime.TotalMilliseconds, Environment.TickCount64);
    }

    /// <summary>One log line of key=value pairs: <c>MEM hidden-2min ws=121.4 private=98.2 ...</c> (the script reads these back from the log).</summary>
    public string Line(string label)
    {
        string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        return $"MEM {label} ws={F(WorkingSetMb)} private={F(PrivateMb)} managed={F(ManagedMb)} gcheap={F(GcHeapMb)} gccommitted={F(GcCommittedMb)} gcfrag={F(GcFragmentedMb)} threads={Threads} handles={Handles} cpu_ms={CpuMs:0} wall_ms={WallMs}";
    }
}
