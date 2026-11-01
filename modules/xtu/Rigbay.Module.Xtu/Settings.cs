namespace Rigbay.Module.Xtu;

/// <summary>
/// The module's settings file (<c>%LOCALAPPDATA%\Rigbay\modules\xtu\settings.json</c>). Hotkey rebinding is kept by the host,
/// not here. Nothing in this file can make the module write to the hardware by itself except <see cref="AutoSwitch"/>, which
/// the user turns on after a confirmation.
/// </summary>
public sealed class XtuSettings
{
    /// <summary>Profiles created in Rigbay (XTU's own profiles are read from its Profiles.xml).</summary>
    public List<CustomProfile> Customs { get; set; } = new();

    /// <summary>Apply a profile by itself when the power source changes (and after resume).</summary>
    public bool AutoSwitch { get; set; }

    /// <summary>Name of the profile to use on AC power; empty for none.</summary>
    public string AcProfile { get; set; } = "";

    /// <summary>Name of the profile to use on battery; empty for none.</summary>
    public string BatteryProfile { get; set; } = "";

    /// <summary>Publish per-core temperature, clock, voltage and usage on the metrics bus too (32 more readings).</summary>
    public bool PublishPerCore { get; set; }
}
