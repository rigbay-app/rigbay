namespace Rigbay.Core;

/// <summary>Where Rigbay keeps its data. The root is overridable so tests never touch the real profile.</summary>
public sealed class RigbayPaths
{
    public RigbayPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rigbay");
    }

    /// <summary><c>%LOCALAPPDATA%\Rigbay</c> by default.</summary>
    public string DataRoot { get; }

    public string LogDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>Optional user language files that add a language or override single keys.</summary>
    public string UserLanguageDirectory => Path.Combine(DataRoot, "lang");

    public string HotkeysFile => Path.Combine(DataRoot, "hotkeys.json");

    public string HostSettingsFile => Path.Combine(DataRoot, "host.json");

    /// <summary><c>&lt;root&gt;\modules\&lt;id&gt;</c>: the module's data folder.</summary>
    public string ModuleDataDirectory(string moduleId) => Path.Combine(DataRoot, "modules", moduleId);
}
