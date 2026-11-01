namespace Rigbay.Abstractions;

/// <summary>Version of this contract. The host refuses modules built for a different major.</summary>
public static class AbstractionsInfo
{
    public static readonly Version Current = new(0, 1, 0);
}

/// <summary>Answer to "does this machine support the module". Never throws; Reason is shown to the user.</summary>
public sealed record ProbeResult(bool Supported, string Reason)
{
    public static ProbeResult Yes() => new(true, "");
    public static ProbeResult No(string reason) => new(false, reason);
}

/// <summary>What the host offers a module. No UI framework types here.</summary>
public interface IHostServices
{
    Version HostVersion { get; }
    Version AbstractionsVersion { get; }
    void Log(string moduleId, string message);
    /// <summary>Localised text for a key; module keys are prefixed by module id. Falls back to English, then the key.</summary>
    string T(string key);
    /// <summary>Runs on the UI thread inside the host's fault boundary. Modules use this, never a raw dispatcher: a throw in a raw dispatcher callback kills the process.</summary>
    void RunOnUi(Action action);
}

/// <summary>The one thing a module DLL exports. The page is returned as object so this assembly does not reference WinUI.</summary>
public interface IRigbayModule
{
    string Id { get; }
    string Name { get; }
    /// <summary>Navigation group, chosen from a fixed list: Power, Cooling, Network, Display, Monitoring, Other.</summary>
    string Group { get; }
    Version Version { get; }
    ProbeResult Probe();
    /// <summary>Level 2, whole page: returns a Microsoft.UI.Xaml.UIElement (the host checks the type). May return null when the module implements IPageModelProvider.</summary>
    object? CreatePage(IHostServices host);
}
