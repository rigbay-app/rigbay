using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Gpu;

public sealed partial class GpuModule
{
    /// <summary>The most reasons the state line names (the "Clock limit" row on the page names all of them).</summary>
    internal const int StateLineReasons = 2;

    /// <summary>Recomputes every text the page shows from the current state. Called after a poll, a state change and a language switch. Safe from any thread.</summary>
    private void RefreshTexts()
    {
        var host = _host;
        var poller = _poller;
        if (host is null) return;
        var loc = host.Localisation;

        if (poller is null)
        {
            _stateLine.Value = LocalisedText.Of("state.unavailable");
            return;
        }

        var snapshot = poller.Latest;
        _deviceText.Value = poller.DeviceName;
        _limitText.Value = LimitText(loc, snapshot);
        _stateLine.Value = StateLine(loc, poller.State, snapshot);
    }

    /// <summary>
    /// The state line: "P0 · 72 W · power cap" (performance state, board power, the two most serious reasons the clocks are held down). A part the
    /// GPU did not give is left out. Before the first reading, and when NVML does not answer, a plain sentence.
    /// </summary>
    internal static LocalisedText StateLine(ILocalisation loc, GpuState state, GpuSnapshot snapshot)
    {
        switch (state)
        {
            case GpuState.Starting: return LocalisedText.Of("state.starting");
            case GpuState.Unavailable: return LocalisedText.Of("state.unavailable");
            case GpuState.Degraded: return LocalisedText.Of("state.degraded");
        }

        var parts = new List<string>();
        if (snapshot.PerformanceState is { } p) parts.Add("P" + p.ToString(CultureInfo.InvariantCulture));
        if (snapshot.PowerWatts is { } w) parts.Add(Math.Round(w).ToString("0", CultureInfo.InvariantCulture) + " W");
        if (snapshot.ClockReasons is { } mask)
        {
            var words = ClockReasons.Keys(mask).Take(StateLineReasons).Select(k => loc.T(k)).ToList();
            if (words.Count > 0) parts.Add(string.Join(", ", words));
        }
        return parts.Count == 0 ? LocalisedText.Of("state.reading") : LocalisedText.Of(Text.Literal(string.Join(" · ", parts)));
    }

    /// <summary>Every reason the clocks are held down, or "Not limited"; empty while the GPU has not said.</summary>
    internal static string LimitText(ILocalisation loc, GpuSnapshot snapshot)
    {
        if (snapshot.ClockReasons is not { } mask) return "";
        var keys = ClockReasons.Keys(mask);
        return keys.Count == 0 ? loc.T("row.limit.none") : string.Join(", ", keys.Select(k => loc.T(k)));
    }
}
