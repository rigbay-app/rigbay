namespace Rigbay.PredatorSense.Wmi;

/// <summary>
/// Puts the allow-list in front of any <see cref="IAcerWmi"/> and lets only one write through at a time. The real
/// implementation checks the list again by itself; this decorator exists so the rule also holds for a fake or a future
/// second implementation, and so the tests can prove "any other call is refused" against the logic layer.
/// </summary>
public sealed class GuardedAcerWmi : IAcerWmi
{
    private readonly IAcerWmi _inner;
    private readonly object _writeGate = new();
    private bool _writing;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public GuardedAcerWmi(IAcerWmi inner) => _inner = inner;

    /// <inheritdoc />
    public ulong ReadSysInfo(int index)
    {
        WmiAllowList.RequireRead(index);
        return _inner.ReadSysInfo(index);
    }

    /// <inheritdoc />
    public ulong ReadOperationMode() => _inner.ReadOperationMode();

    /// <inheritdoc />
    public ulong ReadSupportedOperationModes() => _inner.ReadSupportedOperationModes();

    /// <inheritdoc />
    public uint SetFanBehavior(ulong value, TimeSpan? timeout = null)
    {
        WmiAllowList.RequireFanWrite(value);
        lock (_writeGate)
        {
            if (_writing) throw new AcerWmiException(AcerWmiFailure.Refused, "Another write is still in progress.");
            _writing = true;
        }
        try { return _inner.SetFanBehavior(value, timeout); }
        finally { lock (_writeGate) _writing = false; }
    }
}
