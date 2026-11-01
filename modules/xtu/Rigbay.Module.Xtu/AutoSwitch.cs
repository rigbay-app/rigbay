namespace Rigbay.Module.Xtu;

internal enum PowerLine { Unknown, Online, Offline }

/// <summary>Where the machine's power comes from, and when that may have changed.</summary>
internal interface IPowerEnvironment : IDisposable
{
    PowerLine Line { get; }
    /// <summary>Raised on any thread when the power source or the power mode changed (a hint; the timer is the backup).</summary>
    event Action? Changed;
    /// <summary>Raised on any thread after the machine woke from sleep.</summary>
    event Action? Resumed;
}

internal enum AutoOutcome
{
    /// <summary>Nothing to do (switch off, no change, no profile chosen, or the power source is unknown).</summary>
    None,
    /// <summary>Apply <see cref="AutoDecision.Profile"/>.</summary>
    Apply,
    /// <summary>The chosen profile no longer exists; <see cref="AutoDecision.MissingName"/> says which.</summary>
    Missing,
}

internal sealed record AutoDecision(AutoOutcome Outcome, XtuProfile? Profile = null, string? MissingName = null, bool OnAc = false);

/// <summary>
/// The auto-switch rule, as in the standalone app: when the power source changes (or on a forced check after resume) apply
/// the profile chosen for AC or for battery. A manual change stays until the power source changes again.
/// </summary>
internal sealed class AutoSwitcher
{
    private PowerLine _last = PowerLine.Unknown;

    /// <param name="enabled">The user's auto-switch setting.</param>
    /// <param name="acProfile">Name chosen for AC (may be empty).</param>
    /// <param name="batteryProfile">Name chosen for battery (may be empty).</param>
    /// <param name="now">The power source now.</param>
    /// <param name="force">True for the check after resume: apply even when the source did not change.</param>
    /// <param name="profiles">The known profiles.</param>
    public AutoDecision Check(bool enabled, string? acProfile, string? batteryProfile, PowerLine now, bool force, IReadOnlyList<XtuProfile> profiles)
    {
        if (now == PowerLine.Unknown) return new AutoDecision(AutoOutcome.None);

        bool changed = now != _last;
        _last = now;
        if (!enabled || (!changed && !force)) return new AutoDecision(AutoOutcome.None);

        bool onAc = now == PowerLine.Online;
        var name = onAc ? acProfile : batteryProfile;
        if (string.IsNullOrEmpty(name)) return new AutoDecision(AutoOutcome.None, OnAc: onAc);

        var profile = Catalog.FindByName(profiles, name);
        return profile is null
            ? new AutoDecision(AutoOutcome.Missing, MissingName: name, OnAc: onAc)
            : new AutoDecision(AutoOutcome.Apply, profile, OnAc: onAc);
    }

    /// <summary>Forget the last power source (after the module was turned off and on again).</summary>
    public void Reset() => _last = PowerLine.Unknown;
}
