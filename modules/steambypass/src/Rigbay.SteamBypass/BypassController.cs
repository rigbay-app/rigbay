using System.Net.Sockets;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass;

/// <summary>Why turning the bypass on failed.</summary>
internal enum StartFailure
{
    /// <summary>It did not fail.</summary>
    None,

    /// <summary>Another program (the standalone Steam Bypass app, for example) already listens on the proxy port.</summary>
    AddressInUse,

    /// <summary>The proxy could not start for another reason.</summary>
    ListenFailed,

    /// <summary>The proxy started but the Windows setting could not be written; the proxy was stopped again.</summary>
    SettingFailed,

    /// <summary>The controller was closed by the module's shutdown: nothing may be turned on any more.</summary>
    Closed,
}

/// <summary>Result of <see cref="BypassController.TurnOn"/>.</summary>
/// <param name="Ok">True when the bypass is on.</param>
/// <param name="Failure">Why not, when it is not.</param>
/// <param name="Detail">The system's error text, for the log and the page.</param>
/// <param name="DownloadsAvailable">False when port 80 was not available (browsing works, game downloads do not).</param>
internal sealed record TurnOnResult(bool Ok, StartFailure Failure, string? Detail, bool DownloadsAvailable);

/// <summary>Result of <see cref="BypassController.TurnOff"/>.</summary>
/// <param name="Outcome">What the restore did.</param>
/// <param name="Error">The error text when the setting could not be restored.</param>
internal sealed record TurnOffResult(RestoreOutcome Outcome, string? Error)
{
    /// <summary>True unless the Windows setting is still ours because it could not be restored.</summary>
    public bool Ok => Outcome != RestoreOutcome.Failed;
}

/// <summary>
/// The on/off logic and its safety rules, with no UI and no real network or registry (all injected):
/// the proxy is listening before Windows is pointed at it; every way off restores the saved value first and then stops the proxy; a
/// restore that failed is retried by the next off, the next start or the process exit; a crash is cleaned up at the next start.
/// </summary>
internal sealed class BypassController
{
    private readonly ProxyEngine _engine;
    private readonly SystemProxyManager _proxy;
    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _otherInstanceListening;
    private readonly object _gate = new();
    private bool _on;
    private bool _closed;
    private DateTimeOffset? _onSince;

    /// <param name="engine">The proxy.</param>
    /// <param name="proxy">The Windows setting.</param>
    /// <param name="log">Events for the log.</param>
    /// <param name="now">The clock.</param>
    /// <param name="otherInstanceListening">True when something already answers on the proxy port while this module's proxy is stopped (a second Steam Bypass).</param>
    public BypassController(ProxyEngine engine, SystemProxyManager proxy, Action<string> log, Func<DateTimeOffset> now, Func<bool> otherInstanceListening)
    {
        _engine = engine;
        _proxy = proxy;
        _log = log;
        _now = now;
        _otherInstanceListening = otherInstanceListening;
    }

    /// <summary>Raised after the on/off state changed. May run on any thread, outside the controller's lock.</summary>
    public event Action? Changed;

    /// <summary>The proxy.</summary>
    public ProxyEngine Engine => _engine;

    /// <summary>True while the bypass is on.</summary>
    public bool IsOn { get { lock (_gate) return _on; } }

    /// <summary>True when the Windows setting holds our address right now.</summary>
    public bool IsApplied => _proxy.IsApplied();

    /// <summary>When it was turned on, or null.</summary>
    public DateTimeOffset? OnSince { get { lock (_gate) return _onSince; } }

    /// <summary>The last failure of turn-on or turn-off, or null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Crash cleanup, run once at module start before anything is turned on: when Windows still points at our address (the previous run
    /// ended without restoring), put the saved value back. Does nothing when another program answers on our port, because then the
    /// address belongs to a running Steam Bypass that is not this module.
    /// </summary>
    public RestoreResult RecoverStale()
    {
        lock (_gate)
        {
            if (_on) return new RestoreResult(RestoreOutcome.NothingToRestore);
            if (_proxy.IsApplied() && _otherInstanceListening())
            {
                _log("Windows points at the Steam proxy address and another program answers there; left as it is.");
                return new RestoreResult(RestoreOutcome.NothingToRestore);
            }
            var result = _proxy.Restore();
            if (result.Outcome == RestoreOutcome.Restored) _log("The previous run ended without turning the bypass off; the Windows proxy setting was restored.");
            else if (result.Outcome == RestoreOutcome.Failed) _log("Could not restore the Windows proxy setting left by an earlier run: " + result.Error);
            return result;
        }
    }

    /// <summary>
    /// Turns the bypass on: start the proxy (the proxy port is required, port 80 is not), then point Windows at it.
    /// If the Windows setting cannot be written the proxy is stopped again, so a failure leaves nothing behind.
    /// </summary>
    public TurnOnResult TurnOn()
    {
        TurnOnResult result;
        lock (_gate)
        {
            // A worker that was already past the module's "stopping" check when Shutdown ran must not turn the bypass on behind its back.
            if (_closed) return Fail(new TurnOnResult(false, StartFailure.Closed, "the module is stopping", false));
            if (_on) return new TurnOnResult(true, StartFailure.None, null, _engine.DownloadsError is null);
            try
            {
                _engine.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied)
            {
                return Fail(new TurnOnResult(false, StartFailure.AddressInUse, ex.Message, false));
            }
            catch (Exception ex)
            {
                return Fail(new TurnOnResult(false, StartFailure.ListenFailed, ex.Message, false));
            }

            try
            {
                _proxy.Apply();
            }
            catch (Exception ex)
            {
                _engine.Stop();
                return Fail(new TurnOnResult(false, StartFailure.SettingFailed, ex.Message, false));
            }

            _on = true;
            _onSince = _now();
            LastError = null;
            result = new TurnOnResult(true, StartFailure.None, null, _engine.DownloadsError is null);
            _log("Bypass on: proxy and PAC at " + _proxy.PacUrl + (result.DownloadsAvailable ? "" : "; game downloads unavailable (" + _engine.DownloadsError + ")"));
        }
        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Turns the bypass off: puts the previous Windows value back, then stops the proxy and closes its connections. Safe to call when
    /// it is already off (it still checks the Windows setting and restores it if it is ours, which is what a failed earlier restore needs).
    /// </summary>
    public TurnOffResult TurnOff() => TurnOffCore(close: false);

    /// <summary>
    /// The module is shutting down: like <see cref="TurnOff"/>, and in the same step (under the same lock) nothing can be turned on again.
    /// Without that, a switch worker that had passed the "stopping" check just before could still turn the bypass on after the restore.
    /// </summary>
    public TurnOffResult TurnOffAndClose() => TurnOffCore(close: true);

    private TurnOffResult TurnOffCore(bool close)
    {
        TurnOffResult result;
        lock (_gate)
        {
            if (close) _closed = true;
            var restore = _proxy.Restore();
            _engine.Stop();
            bool wasOn = _on;
            _on = false;
            _onSince = null;
            result = new TurnOffResult(restore.Outcome, restore.Error);
            if (restore.Outcome == RestoreOutcome.Failed)
            {
                LastError = restore.Error;
                _log("Could not restore the Windows proxy setting: " + restore.Error + ". It will be tried again at the next off, start or exit.");
            }
            else
            {
                LastError = null;
                if (wasOn) _log("Bypass off: the Windows proxy setting was " + (restore.Outcome == RestoreOutcome.Restored ? "restored." : "no longer ours; left as it is."));
            }
        }
        RaiseChanged();
        return result;
    }

    /// <summary>
    /// The host process is ending. Restores the setting without raising events or waiting for anything but the registry, so it is safe in
    /// a process-exit handler.
    /// </summary>
    public void RestoreOnExit()
    {
        try
        {
            if (!Monitor.TryEnter(_gate, TimeSpan.FromSeconds(2))) { _proxy.Restore(); return; }
            try
            {
                _proxy.Restore();
                _on = false;
            }
            finally { Monitor.Exit(_gate); }
        }
        catch (Exception) { /* nothing more can be done while exiting */ }
    }

    private TurnOnResult Fail(TurnOnResult result)
    {
        LastError = result.Detail;
        _log("Could not turn the bypass on (" + result.Failure + "): " + result.Detail);
        // The caller holds the lock; Changed is raised by the caller of TurnOn through the page refresh, not here.
        return result;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch (Exception) { /* a subscriber's bug must not undo a change */ }
    }
}
