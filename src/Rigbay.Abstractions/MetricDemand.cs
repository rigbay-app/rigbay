namespace Rigbay.Abstractions;

/// <summary>Why a publisher should poll at a rate (see <see cref="MetricDemand.Evaluate"/>).</summary>
public enum DemandKind
{
    /// <summary>Nobody asks: poll at the idle rate (which may be "do not poll").</summary>
    Idle,
    /// <summary>Somebody wants the reading in the background, at most every <see cref="DemandState.Interval"/>.</summary>
    Background,
    /// <summary>The page is visible or somebody subscribed: poll at the fast rate.</summary>
    Fast,
}

/// <summary>The rate to poll at and why.</summary>
/// <param name="Kind">Why.</param>
/// <param name="Interval">How often; <see cref="Timeout.InfiniteTimeSpan"/> means do not poll.</param>
public readonly record struct DemandState(DemandKind Kind, TimeSpan Interval);

/// <summary>
/// The publisher side of background demand (host API 0.1.4). A module that publishes a metric asks this once per decision instead of repeating
/// "page visible or subscriber" by hand. Subscribers of any kind still mean fast; a background demand
/// (<see cref="IMetricsBus.SubscribeBackground"/>) means "at least this often", never faster than the module's fast rate and never slower than its idle rate.
/// </summary>
public static class MetricDemand
{
    /// <summary>
    /// Decides how the module should read the metrics <paramref name="ids"/> right now.
    /// <list type="bullet">
    /// <item>page visible, or a subscriber on any id: <see cref="DemandKind.Fast"/> at <paramref name="fast"/>;</item>
    /// <item>else a background demand of N on any id: <see cref="DemandKind.Background"/> at min(<paramref name="idle"/>, max(<paramref name="fast"/>, N)); when idle is infinite, max(fast, N);</item>
    /// <item>else <see cref="DemandKind.Idle"/> at <paramref name="idle"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="host">This module's host services.</param>
    /// <param name="ids">The metrics this module publishes and reads for.</param>
    /// <param name="fast">The fast interval.</param>
    /// <param name="idle">The idle interval; <see cref="Timeout.InfiniteTimeSpan"/> for "do not poll when nobody asks".</param>
    public static DemandState Evaluate(this IHostServices host, IEnumerable<MetricId> ids, TimeSpan fast, TimeSpan idle)
    {
        if (host.PageVisible.Value) return new DemandState(DemandKind.Fast, fast);
        var bus = host.Metrics;
        TimeSpan? background = null;
        foreach (var id in ids)
        {
            if (bus.SubscriberCount(id) > 0) return new DemandState(DemandKind.Fast, fast);
            if (bus.BackgroundInterval(id) is { } b && (background is null || b < background)) background = b;
        }
        if (background is not { } wanted) return new DemandState(DemandKind.Idle, idle);
        var interval = wanted < fast ? fast : wanted;
        if (idle != Timeout.InfiniteTimeSpan && interval > idle) interval = idle;
        return new DemandState(DemandKind.Background, interval);
    }

    /// <summary>The interval of <see cref="Evaluate"/>.</summary>
    /// <param name="host">This module's host services.</param>
    /// <param name="ids">The metrics this module publishes.</param>
    /// <param name="fast">The fast interval.</param>
    /// <param name="idle">The idle interval.</param>
    public static TimeSpan PollInterval(this IHostServices host, IEnumerable<MetricId> ids, TimeSpan fast, TimeSpan idle) => host.Evaluate(ids, fast, idle).Interval;
}
