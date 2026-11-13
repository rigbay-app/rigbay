using System.Security.Cryptography;
using System.Text;

namespace Rigbay.PowerRules;

/// <summary>Where the PC gets its power now. A UPS or an unreadable state is <see cref="Unknown"/>, and nothing is switched for it.</summary>
internal enum PowerSource { Unknown, Battery, Plugged }

/// <summary>The three Windows power modes (overlay schemes). <see cref="Unknown"/> is "could not be read, or not one of the three".</summary>
internal enum PowerMode { Unknown, BestEfficiency, Balanced, BestPerformance }

/// <summary>
/// The allow-list of power modes: the only three overlay GUIDs the module will ever pass to <c>PowerSetActiveOverlayScheme</c>. The native
/// layer takes a <see cref="PowerMode"/>, never a GUID, so no other value can be sent whatever a page, a settings file or a command says.
/// </summary>
internal static class PowerModes
{
    /// <summary>The overlay GUIDs of Windows 11 (community-documented; the export itself is undocumented).</summary>
    public static readonly IReadOnlyDictionary<PowerMode, Guid> Guids = new Dictionary<PowerMode, Guid>
    {
        [PowerMode.BestEfficiency] = new("961cc777-2547-4f9d-8174-7d86181b8a7a"),
        [PowerMode.Balanced] = Guid.Empty,
        [PowerMode.BestPerformance] = new("ded574b5-45a0-4f42-8737-46345c09c238"),
    };

    /// <summary>The three modes a rule may name, in the order the page lists them.</summary>
    public static readonly IReadOnlyList<PowerMode> All = new[] { PowerMode.BestEfficiency, PowerMode.Balanced, PowerMode.BestPerformance };

    public static string Id(PowerMode mode) => mode switch
    {
        PowerMode.BestEfficiency => "efficiency",
        PowerMode.Balanced => "balanced",
        PowerMode.BestPerformance => "performance",
        _ => "",
    };

    /// <summary>The mode with this id; <c>null</c> for "leave" (an empty or unknown id).</summary>
    public static PowerMode? FromId(string? id) => id switch
    {
        "efficiency" => PowerMode.BestEfficiency,
        "balanced" => PowerMode.Balanced,
        "performance" => PowerMode.BestPerformance,
        _ => null,
    };

    /// <summary>The mode an overlay GUID stands for; <see cref="PowerMode.Unknown"/> for any other GUID.</summary>
    public static PowerMode FromGuid(Guid guid)
    {
        foreach (var (mode, g) in Guids) if (g == guid) return mode;
        return PowerMode.Unknown;
    }

    /// <summary>Module-relative language key of the mode's name.</summary>
    public static string LabelKey(PowerMode mode) => "mode." + Id(mode);
}

/// <summary>What the module found out about the internal panel.</summary>
/// <param name="DeviceName">The GDI name of the display the panel is on (<c>\\.\DISPLAY1</c>); only passed back to the display calls.</param>
/// <param name="Name">The panel's friendly name, or empty.</param>
/// <param name="Width">Current width in pixels.</param>
/// <param name="Height">Current height in pixels.</param>
/// <param name="CurrentHz">Current refresh rate in Hz.</param>
/// <param name="Rates">Every rate the panel lists at its current resolution, ascending.</param>
internal sealed record PanelInfo(string DeviceName, string Name, int Width, int Height, int CurrentHz, IReadOnlyList<int> Rates)
{
    public bool Lists(int hz) => Rates.Contains(hz);
}

internal enum PanelState
{
    /// <summary>The internal panel is part of the desktop now.</summary>
    Active,
    /// <summary>No internal panel is active (lid closed, or only an external monitor): nothing to change, and not an error.</summary>
    NotActive,
    /// <summary>The display calls failed or answered something unreadable.</summary>
    Unreadable,
}

internal sealed record PanelLookup(PanelState State, PanelInfo? Panel = null)
{
    public static readonly PanelLookup None = new(PanelState.NotActive);
}

/// <summary>Result of <c>ChangeDisplaySettingsEx</c> (<c>DISP_CHANGE_*</c>: 0 successful, 1 restart, -1 failed, -2 bad mode, ...).</summary>
internal readonly record struct DisplayChange(int Code)
{
    public bool Ok => Code == 0;
}

/// <summary>A rule for one power source: a refresh rate (0 = leave) and a power mode (null = leave).</summary>
internal sealed record Rule(int Hz, PowerMode? Mode)
{
    public static readonly Rule Leave = new(0, null);
    public bool IsEmpty => Hz == 0 && Mode is null;
}

/// <summary>The two columns of the page. Its <see cref="Hash"/> is what the owner approves.</summary>
internal sealed record RuleSet(Rule Battery, Rule Plugged)
{
    /// <summary>A fingerprint of exactly these values. Changing any of them changes it, so an approval cannot carry over to different rules.</summary>
    public string Hash()
    {
        var text = $"power-rules/1|battery:{Battery.Hz}:{PowerModes.Id(Battery.Mode ?? PowerMode.Unknown)}|plugged:{Plugged.Hz}:{PowerModes.Id(Plugged.Mode ?? PowerMode.Unknown)}|restore-on-off:1";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public Rule For(PowerSource source) => source == PowerSource.Battery ? Battery : Plugged;
    public bool IsEmpty => Battery.IsEmpty && Plugged.IsEmpty;
}

internal enum StepKind { Refresh, Mode }

internal enum StepOutcome
{
    /// <summary>The rule says leave: nothing was asked of the machine.</summary>
    Leave,
    /// <summary>The machine already has the value.</summary>
    AlreadySet,
    /// <summary>Changed and read back.</summary>
    Changed,
    /// <summary>No internal panel is active. Silent: no message to the user.</summary>
    PanelNotActive,
    /// <summary>The panel does not list this rate at its current resolution; nothing was sent.</summary>
    NotListed,
    /// <summary>The display calls could not be read.</summary>
    Unreadable,
    /// <summary><c>CDS_TEST</c> said no; the real call was never made.</summary>
    Refused,
    /// <summary>The call failed, or the read-back shows another value.</summary>
    Failed,
    /// <summary>The power-mode export is missing or the mode cannot be read here.</summary>
    ModeUnavailable,
}

/// <summary>What one step did. <paramref name="From"/> and <paramref name="To"/> are numbers for a rate (Hz) and a <see cref="PowerMode"/> id for a mode.</summary>
internal sealed record StepResult(StepKind Kind, StepOutcome Outcome, string From = "", string To = "", int Code = 0)
{
    public bool IsProblem => Outcome is StepOutcome.NotListed or StepOutcome.Unreadable or StepOutcome.Refused or StepOutcome.Failed or StepOutcome.ModeUnavailable;
}

/// <summary>Tells when the power source really changed, in the way XTU's auto-switch does: the first source seen counts as a change.</summary>
internal sealed class SourceTracker
{
    private PowerSource _last = PowerSource.Unknown;

    /// <summary>True when the rules for <paramref name="now"/> should run.</summary>
    /// <param name="now">The source now.</param>
    /// <param name="force">True for the check after resume or after the owner approved: run even when the source did not change.</param>
    public bool Check(PowerSource now, bool force)
    {
        if (now == PowerSource.Unknown) return false;
        bool changed = now != _last;
        _last = now;
        return changed || force;
    }

    public void Reset() => _last = PowerSource.Unknown;
}
