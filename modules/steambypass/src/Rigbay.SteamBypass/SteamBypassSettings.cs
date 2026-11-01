namespace Rigbay.SteamBypass;

/// <summary>The module's settings, one JSON file in the module's data folder.</summary>
public sealed class SteamBypassSettings
{
    /// <summary>
    /// Turn the bypass on by itself when the module starts (Rigbay starts, or the module is enabled). The user agreed to this when
    /// they switched it on: the host asked, with the exact effect, because it changes the Windows proxy setting.
    /// </summary>
    public bool AutoOn { get; set; }
}
