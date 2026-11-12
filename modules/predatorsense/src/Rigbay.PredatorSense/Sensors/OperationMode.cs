using Rigbay.Abstractions;
using Rigbay.PredatorSense.Wmi;

namespace Rigbay.PredatorSense.Sensors;

/// <summary>
/// The performance (operation) modes PredatorSense knows, by the id the service uses (FINDINGS 15.1). Render and Edit exist in
/// the id table but this laptop's firmware does not advertise them (FINDINGS 15.9).
/// </summary>
public enum OperationMode
{
    /// <summary>Quiet, id 0.</summary>
    Quiet = 0,
    /// <summary>Default, id 1 (Acer's "Normal").</summary>
    Default = 1,
    /// <summary>Render, id 2.</summary>
    Render = 2,
    /// <summary>Edit, id 3.</summary>
    Edit = 3,
    /// <summary>Extreme, id 4.</summary>
    Extreme = 4,
    /// <summary>Turbo, id 5.</summary>
    Turbo = 5,
}

/// <summary>Turns the raw 64-bit answers of the two operation-mode reads into a mode, a set of modes, or "unknown". Never guesses, never throws.</summary>
public static class OperationModeDecoder
{
    /// <summary>The id of the highest known mode (Turbo).</summary>
    public const int HighestId = 5;

    /// <summary>
    /// The current mode from the answer of <c>GetGamingMiscSetting(0x0B)</c>: status is the low byte (anything but 0 is unknown), the id is
    /// bits 8 to 15. An id that is not 0 to 5 is unknown. Observed once: <c>0x0B0100</c> = Default (FINDINGS 15.9).
    /// </summary>
    public static OperationMode? DecodeCurrent(ulong raw)
    {
        if ((raw & 0xFF) != 0) return null;
        int id = (int)((raw >> 8) & 0xFF);
        return id <= HighestId ? (OperationMode)id : null;
    }

    /// <summary>
    /// The modes the firmware has, from the answer of <c>GetGamingMiscSetting(0x0A)</c>: status is the low byte (anything but 0 is unknown), bit N of
    /// bits 8 to 15 means "mode id N exists". Bits for ids above 5 are ignored (nothing is known about them). An empty set is unknown.
    /// Observed once: <c>0x0B3300</c> = Quiet, Default, Extreme, Turbo.
    /// </summary>
    public static IReadOnlyList<OperationMode>? DecodeSupported(ulong raw)
    {
        if ((raw & 0xFF) != 0) return null;
        int mask = (int)((raw >> 8) & 0xFF);
        var modes = new List<OperationMode>();
        for (int id = 0; id <= HighestId; id++)
            if ((mask & (1 << id)) != 0) modes.Add((OperationMode)id);
        return modes.Count == 0 ? null : modes;
    }
}

/// <summary>
/// Reads the current operation mode (and, once or on demand, which modes exist) through <see cref="IAcerWmi"/>, publishes the mode as the
/// metric <see cref="Metric"/> (its id, 0 to 5) and keeps the last answer. READ ONLY: the module has no write for this. A read that
/// fails, times out, answers a non-zero status or an id nobody knows gives "unknown" and withdraws the metric; the raw value is logged
/// when it changes so a surprise can be seen.
/// </summary>
public sealed class OperationModeReader
{
    /// <summary>The metric the current mode is published on: its numeric id (Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5).</summary>
    public static readonly MetricId Metric = new("predatorsense.opmode");

    private readonly IAcerWmi _wmi;
    private readonly IMetricsBus _bus;
    private readonly ILog _log;
    private readonly object _lock = new();
    private readonly object _readLock = new();
    private OperationMode? _current;
    private IReadOnlyList<OperationMode>? _supported;
    private bool _published;
    private ulong? _lastRawLogged;

    /// <summary>Creates the reader (nothing is read yet).</summary>
    public OperationModeReader(IAcerWmi wmi, IMetricsBus bus, ILog log)
    {
        _wmi = wmi;
        _bus = bus;
        _log = log;
    }

    /// <summary>Raised after a read when the mode or the supported list changed (on the reading thread).</summary>
    public event Action? Changed;

    /// <summary>The mode of the last read, or null when unknown (not read yet, failed, or not understood).</summary>
    public OperationMode? Current { get { lock (_lock) return _current; } }

    /// <summary>The modes the firmware advertises, or null when not read yet or not understood.</summary>
    public IReadOnlyList<OperationMode>? Supported { get { lock (_lock) return _supported; } }

    /// <summary>Reads <c>GetGamingMiscSetting(0x0A)</c> (which modes exist). Safe from any thread; never throws.</summary>
    public IReadOnlyList<OperationMode>? ReadSupported()
    {
        IReadOnlyList<OperationMode>? decoded = null;
        lock (_readLock)
        {
            try { decoded = OperationModeDecoder.DecodeSupported(_wmi.ReadSupportedOperationModes()); }
            catch (AcerWmiException ex) { _log.Warn("The supported performance modes could not be read: " + ex.Message); }
            catch (Exception ex) { _log.Warn($"Reading the supported performance modes failed unexpectedly: {ex.GetType().Name}: {ex.Message}"); }
        }
        bool changed;
        lock (_lock)
        {
            changed = !SameModes(_supported, decoded);
            _supported = decoded;
        }
        if (changed) Raise();
        return decoded;
    }

    /// <summary>Reads <c>GetGamingMiscSetting(0x0B)</c> (the current mode) and publishes it. Safe from any thread; never throws.</summary>
    public OperationMode? ReadCurrent()
    {
        OperationMode? mode = null;
        ulong? raw = null;
        lock (_readLock)
        {
            try
            {
                raw = _wmi.ReadOperationMode();
                mode = OperationModeDecoder.DecodeCurrent(raw.Value);
            }
            catch (AcerWmiException ex) { _log.Warn("The performance mode could not be read: " + ex.Message); }
            catch (Exception ex) { _log.Warn($"Reading the performance mode failed unexpectedly: {ex.GetType().Name}: {ex.Message}"); }
        }

        if (raw is { } r && _lastRawLogged != r)
        {
            _lastRawLogged = r;
            _log.Info($"Performance mode read: raw 0x{r:X} -> {(mode is { } m ? m.ToString() : "unknown")}.");
        }

        bool changed;
        lock (_lock)
        {
            changed = _current != mode;
            _current = mode;
        }

        if (mode is { } known)
        {
            try { _bus.Publish(Metric, (double)(int)known, MetricUnit.None); _published = true; }
            catch (Exception ex) { _log.Warn("Publishing " + Metric + " failed: " + ex.Message); }
        }
        else if (_published)
        {
            try { _bus.Withdraw(Metric); }
            catch (Exception ex) { _log.Warn("Withdrawing " + Metric + " failed: " + ex.Message); }
            _published = false;
        }

        if (changed) Raise();
        return mode;
    }

    /// <summary>One scheduled poll: just the current mode (the supported list is read at start and on demand).</summary>
    public void PollOnce() => ReadCurrent();

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.Warn("A performance-mode handler threw: " + ex.Message); }
    }

    private static bool SameModes(IReadOnlyList<OperationMode>? a, IReadOnlyList<OperationMode>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);
}
