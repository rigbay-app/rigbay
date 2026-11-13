namespace Rigbay.Watch;

/// <summary>What the module takes from outside, so tests can swap it: the clock and whether its own timer runs.</summary>
internal sealed class Dependencies
{
    /// <summary>The time. Samples on the bus carry the bus clock, which is the system clock in the app.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>False in tests: they call the check themselves.</summary>
    public bool AutoTick { get; init; } = true;

    public static Dependencies Real() => new();
}
