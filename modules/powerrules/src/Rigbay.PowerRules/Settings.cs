namespace Rigbay.PowerRules;

/// <summary>The module's settings file. Plain values only; the rules are read back into <see cref="RuleSet"/>.</summary>
public sealed class PowerRulesSettings
{
    /// <summary>On battery: refresh rate in Hz, 0 for leave.</summary>
    public int BatteryHz { get; set; }

    /// <summary>On battery: power mode id (<c>efficiency</c>, <c>balanced</c>, <c>performance</c>), empty for leave.</summary>
    public string BatteryMode { get; set; } = "";

    /// <summary>Plugged in: refresh rate in Hz, 0 for leave.</summary>
    public int PluggedHz { get; set; }

    /// <summary>Plugged in: power mode id, empty for leave.</summary>
    public string PluggedMode { get; set; } = "";

    /// <summary>True once the first defaults were written (power modes first, then the rates when a panel was seen).</summary>
    public bool Initialised { get; set; }

    /// <summary>True once the rates were filled in from a real panel; until then both rates may still be filled.</summary>
    public bool RatesDefaulted { get; set; }

    /// <summary>The owner approved the rules whose hash is <see cref="ApprovedHash"/>. Off by default.</summary>
    public bool AutoApproved { get; set; }

    /// <summary>The hash of the approved rule set. Automatic switching runs only while it equals the hash of the rules now.</summary>
    public string ApprovedHash { get; set; } = "";

    /// <summary>The refresh rate before the first automatic change, 0 when nothing is remembered.</summary>
    public int BeforeHz { get; set; }

    /// <summary>The power mode id before the first automatic change, empty when nothing is remembered.</summary>
    public string BeforeMode { get; set; } = "";

    /// <summary>The rates the panel listed the last time it was seen, so the rule rows can offer them while it is not active.</summary>
    public int[] KnownRates { get; set; } = Array.Empty<int>();

    internal PowerRulesSettings Clone()
    {
        var copy = (PowerRulesSettings)MemberwiseClone();
        copy.KnownRates = (int[])(KnownRates ?? Array.Empty<int>()).Clone();
        return copy;
    }
}
