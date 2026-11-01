using System.Diagnostics;
using Microsoft.Win32;

namespace Rigbay.Recorder.Recording;

/// <summary>Free space of a drive. A seam so a test can pretend the disk is full.</summary>
internal interface IDiskSpace
{
    /// <summary>Bytes free for the current user on the drive that holds <paramref name="folder"/>, or null when that cannot be told (a network share).</summary>
    long? FreeBytes(string folder);
}

/// <summary>The real thing: <see cref="DriveInfo"/>.</summary>
internal sealed class SystemDiskSpace : IDiskSpace
{
    public long? FreeBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Opens a folder or a file in Windows. A seam so tests do not start Explorer.</summary>
internal interface IShell
{
    /// <summary>Shows the folder in Explorer.</summary>
    void OpenFolder(string folder);

    /// <summary>Opens the file in the program Windows has for it.</summary>
    void OpenFile(string path);
}

/// <summary>Explorer and the shell association.</summary>
internal sealed class SystemShell : IShell
{
    public void OpenFolder(string folder)
    {
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = false });
    }

    public void OpenFile(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

/// <summary>What the .meta.json says about the machine. Plain facts, no identifiers of the user.</summary>
internal sealed record MachineFacts(string? Manufacturer, string? Model, string? Cpu, string? Os);

/// <summary>Reads <see cref="MachineFacts"/>. A seam for tests.</summary>
internal interface IMachineFactsSource
{
    /// <summary>The facts; a value that cannot be read is null. Never throws.</summary>
    MachineFacts Read();
}

/// <summary>Make and model and processor from the registry (the keys the firmware tables are published under), Windows from the runtime.</summary>
internal sealed class RegistryMachineFacts : IMachineFactsSource
{
    public MachineFacts Read() => new(
        Value(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer"),
        Value(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName"),
        Value(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString"),
        Os());

    private static string? Value(string key, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            var text = k?.GetValue(name) as string;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Os()
    {
        try { return System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim(); }
        catch (Exception) { return null; }
    }
}

/// <summary>Version of the modules the columns come from, for the .meta.json. A seam for tests.</summary>
internal interface IModuleVersions
{
    /// <summary>The manifest version of an installed module, or null when it cannot be found.</summary>
    string? VersionOf(string moduleId);
}

/// <summary>
/// Reads <c>module.json</c> of a sibling folder: modules are installed side by side, one folder per module named like its id, so the
/// recorder's own folder tells where the others are. A module that is not installed that way (or the host itself) gives null.
/// </summary>
internal sealed class SiblingModuleVersions(string ownModuleDirectory) : IModuleVersions
{
    public string? VersionOf(string moduleId)
    {
        try
        {
            if (!Rigbay.Abstractions.MetricId.IsValid(moduleId)) return null;   // a folder name must look like an id
            var root = Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownModuleDirectory)))?.FullName;
            if (root is null) return null;
            var manifest = Path.Combine(root, moduleId, "module.json");
            if (!File.Exists(manifest)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(manifest), new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
