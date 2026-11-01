namespace Rigbay.Module.Xtu;

// The client layers.
//
//   IXtuClient   what the module logic sees: read the state, read the monitor values, apply a profile.
//   XtuClient    the logic (validation, the apply sequence, the safety latch). Plain C#, no Intel type.
//   IXtuTuningPort / IXtuMonitorPort
//                thin ports over the XTU service. The real ones are in Intel/ and are the only code that touches
//                Intel's assemblies and WCF. Tests use fakes that record the calls.

/// <summary>One tuning control as the service reports it.</summary>
internal sealed record ControlInfo(uint Id, decimal Active, decimal Default, bool ReadOnly, bool Enabled, IReadOnlyList<decimal>? Supported);

/// <summary>One value to propose. Only ids in <see cref="XtuIds.Controls"/> are ever built.</summary>
internal sealed record Proposal(uint Id, decimal Value);

/// <summary>Outcome of a propose or apply call: success, or a short English detail (the error codes) for the message.</summary>
internal sealed record PortResult(bool Success, string Detail = "", bool RequiresReboot = false);

internal interface IXtuTuningPort : IDisposable
{
    bool IsOpen { get; }
    /// <summary>Opens the channel and registers with the service. Throws when the service cannot be reached or refuses.</summary>
    void Connect();
    /// <summary>Drops the channel. Never throws.</summary>
    void Close();
    /// <summary>Makes the service re-read the hardware (its cached "active" values can be stale or 0 until then).</summary>
    void Refresh();
    /// <summary>Reads one control. Only the four ids of <see cref="XtuIds.Controls"/> are accepted.</summary>
    ControlInfo GetControl(uint id);
    bool GetRestoreOnBoot();
    /// <summary>WRITE PATH: drops pending proposals.</summary>
    void Discard();
    /// <summary>WRITE PATH: proposes values (nothing changes on the hardware until <see cref="ApplyPending"/>).</summary>
    PortResult Propose(IReadOnlyList<Proposal> proposals);
    /// <summary>WRITE PATH: applies the proposals made before.</summary>
    PortResult ApplyPending();
}

internal interface IXtuMonitorPort : IDisposable
{
    /// <summary>Reads all monitor values. Opens the channel on first use (then <paramref name="justOpened"/> is true once).</summary>
    IReadOnlyDictionary<uint, decimal> GetValues(out bool justOpened);
    /// <summary>Forgets the connection so the next read opens a new one.</summary>
    void Reset();
}

internal sealed record ApplyOutcome(bool AlreadyActive, XtuState After);

/// <summary>What the module logic uses. One instance serves the whole module; calls are serialised inside.</summary>
internal interface IXtuClient : IDisposable
{
    /// <summary>Reads the active values and the defaults. Throws <see cref="XtuException"/> when XTU cannot be read.</summary>
    XtuState ReadState();
    /// <summary>The monitor read (about 1 Hz at the source). <paramref name="justOpened"/> is true on the first read after (re)connecting.</summary>
    IReadOnlyDictionary<uint, decimal> ReadMonitor(out bool justOpened);
    void ResetMonitor();
    /// <summary>
    /// Validates, then applies the profile through the proven sequence and reads the result back. This is the only
    /// write path of the module. Throws <see cref="XtuException"/> with a language key on every refusal and failure.
    /// </summary>
    ApplyOutcome Apply(XtuProfile profile);
}

/// <summary>
/// A latch against writes. While the environment variable <c>RIGBAY_XTU_NO_WRITE</c> is 1 or true, or a file
/// <c>no-write.flag</c> exists in the module's data folder, no write call is made at all: the apply fails before it asks
/// anyone, and the Intel port refuses its three write-path methods. It exists so a verification run on a real machine can be
/// made structurally read-only (an elevated process does not inherit the caller's environment, hence the file).
/// </summary>
internal sealed class WriteLatch
{
    public const string Variable = "RIGBAY_XTU_NO_WRITE";
    public const string FileName = "no-write.flag";

    private readonly string? _dataDir;
    private readonly Func<string, string?> _env;

    public WriteLatch(string? dataDir = null, Func<string, string?>? env = null)
    {
        _dataDir = dataDir;
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>A latch that is never on (tests that are not about the latch).</summary>
    public static WriteLatch Off => new(null, _ => null);

    /// <summary>Why writes are off ("RIGBAY_XTU_NO_WRITE=1" or "no-write.flag"), or null when they are allowed.</summary>
    public string? Reason
    {
        get
        {
            var v = _env(Variable);
            if (string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
                return Variable + "=" + v;
            if (_dataDir is not null && File.Exists(Path.Combine(_dataDir, FileName))) return FileName;
            return null;
        }
    }

    public bool IsOn => Reason is not null;

    public void ThrowIfOn()
    {
        if (Reason is { } reason) throw new XtuException("err.write-forbidden", reason);
    }
}

internal sealed class XtuClient : IXtuClient
{
    private readonly IXtuTuningPort _tuning;
    private readonly IXtuMonitorPort _monitor;
    private readonly WriteLatch _latch;
    private readonly object _tuningGate = new();
    private readonly object _monitorGate = new();

    public XtuClient(IXtuTuningPort tuning, IXtuMonitorPort monitor, WriteLatch? latch = null)
    {
        _tuning = tuning;
        _monitor = monitor;
        _latch = latch ?? WriteLatch.Off;
    }

    public XtuState ReadState()
    {
        lock (_tuningGate)
        {
            try
            {
                EnsureOpen();
                _tuning.Refresh();
                return ReadStateLocked();
            }
            catch (XtuException) { throw; }
            catch (Exception ex)
            {
                _tuning.Close();
                throw new XtuException("err.unavailable", ex.Message);
            }
        }
    }

    public IReadOnlyDictionary<uint, decimal> ReadMonitor(out bool justOpened)
    {
        lock (_monitorGate)
        {
            try { return _monitor.GetValues(out justOpened); }
            catch (Exception ex)
            {
                _monitor.Reset();
                throw new XtuException("err.unavailable", ex.Message);
            }
        }
    }

    public void ResetMonitor()
    {
        lock (_monitorGate) _monitor.Reset();
    }

    /// <summary>
    /// Sequence (the same as the standalone app, which is proven on the real machine): latch, validate, refresh, "already in
    /// use" check, locked and supported-value checks, discard, propose (PL2 enable, PL2, PL1, window), apply, refresh,
    /// read back. Every failure after the first service call discards pending proposals.
    /// </summary>
    public ApplyOutcome Apply(XtuProfile profile)
    {
        _latch.ThrowIfOn();
        Rules.Validate(profile);

        lock (_tuningGate)
        {
            bool touched = false;   // true once a write-path call may have reached the service
            try
            {
                EnsureOpen();
                _tuning.Refresh();

                var before = ReadStateLocked();
                if (profile.Matches(before)) return new ApplyOutcome(true, before);

                var pl1 = _tuning.GetControl(XtuIds.Pl1);
                var pl2 = _tuning.GetControl(XtuIds.Pl2);
                if (pl1.ReadOnly || !pl1.Enabled) throw new XtuException("err.locked", "PL1");
                if (pl2.ReadOnly || !pl2.Enabled) throw new XtuException("err.locked", "PL2");
                RequireSupported(pl1, profile.Pl1, "PL1");
                RequireSupported(pl2, profile.Pl2, "PL2");

                touched = true;
                _tuning.Discard();
                var proposals = new List<Proposal>
                {
                    new(XtuIds.Pl2Enable, profile.Pl2Enable ? 1m : 0m),
                    new(XtuIds.Pl2, profile.Pl2),
                    new(XtuIds.Pl1, profile.Pl1),
                    new(XtuIds.Window, profile.Window),
                };

                var proposed = _tuning.Propose(proposals);
                if (!proposed.Success || proposed.RequiresReboot)
                {
                    SafeDiscard();
                    throw new XtuException(proposed.RequiresReboot ? "err.rejected-reboot" : "err.rejected", proposed.Detail);
                }

                var applied = _tuning.ApplyPending();
                if (!applied.Success)
                {
                    SafeDiscard();
                    throw new XtuException("err.applyfail", applied.Detail);
                }

                _tuning.Refresh();
                return new ApplyOutcome(false, ReadStateLocked());
            }
            catch (XtuException) { throw; }
            catch (Exception ex)
            {
                if (touched) SafeDiscard();
                _tuning.Close();
                throw new XtuException("err.unavailable", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        lock (_tuningGate) _tuning.Dispose();
        lock (_monitorGate) _monitor.Dispose();
    }

    private void EnsureOpen()
    {
        if (!_tuning.IsOpen) _tuning.Connect();
    }

    private XtuState ReadStateLocked()
    {
        var c1 = _tuning.GetControl(XtuIds.Pl1);
        var c2 = _tuning.GetControl(XtuIds.Pl2);
        var ce = _tuning.GetControl(XtuIds.Pl2Enable);
        var cw = _tuning.GetControl(XtuIds.Window);
        return new XtuState(
            c1.Active, c2.Active, ce.Active != 0m, cw.Active,
            c1.Default, c2.Default, ce.Default != 0m, cw.Default,
            _tuning.GetRestoreOnBoot());
    }

    private static void RequireSupported(ControlInfo c, decimal value, string label)
    {
        if (c.Supported is null) return;
        foreach (var v in c.Supported)
            if (v == value) return;
        throw new XtuException("err.unsupported", label, Fmt.N(value));
    }

    private void SafeDiscard()
    {
        try { _tuning.Discard(); } catch (Exception) { /* the original error is what the caller needs */ }
    }
}
