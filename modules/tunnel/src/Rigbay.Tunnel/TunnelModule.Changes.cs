using Rigbay.Abstractions;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel;

internal enum RequestStatus
{
    NotRunning,
    UnknownTunnel,
    AlreadyThere,
    Busy,
    Declined,
    Done,
    Failed,
    Unusable,
}

/// <summary>How a request to turn a tunnel on or off ended. <see cref="Handshake"/> is set when a turn-on was waited for (a verb waits, the others report by notification).</summary>
internal sealed record RequestOutcome(RequestStatus Status, string Name, Problem? Problem = null, HandshakeOutcome? Handshake = null);

public sealed partial class TunnelModule
{
    /// <summary>
    /// The one way a tunnel is turned on or off from the tray, a hotkey, a verb, the page or the auto-off timer.
    /// <paramref name="approved"/> is true only when the user has already said yes to exactly this: the page (the host asked, because the row carries a
    /// <see cref="HardwareEffect"/>) and the auto-off timer (the user chose it, and the host asked then). Every other path asks here, with the text
    /// "Turn on tunnel NAME" / "Turn off tunnel NAME"; when the host cannot ask the answer is no and nothing changes.
    /// </summary>
    internal async Task<RequestOutcome> RequestChangeAsync(string name, bool wanted, Origin origin, CancellationToken ct, bool approved = false)
    {
        var host = _host;
        var manager = _manager;
        if (host is null || manager is null || _stopping) return new RequestOutcome(RequestStatus.NotRunning, name);

        var info = manager.Find(name);
        if (info is null) return new RequestOutcome(RequestStatus.UnknownTunnel, name, new Problem("err.unknown", name));
        name = info.Name;
        if (wanted ? info.IsOn : !info.Service.Exists) return new RequestOutcome(RequestStatus.AlreadyThere, name);
        if (manager.Busy) return Refused(host, name, origin);   // nobody is asked about a change that cannot be made now

        if (!approved)
        {
            var loc = host.Localisation;
            var effect = new HardwareEffect(Text.Literal(loc.F(wanted ? "effect.turn-on" : "effect.turn-off", name)))
            {
                Kind = EffectKind.SystemSetting,
                RevertDescriptionKey = Text.Literal(loc.F(wanted ? "effect.turn-off" : "effect.turn-on", name)),
            };
            bool yes = await host.Approval.ConfirmAsync(effect, LocalisedText.Of(wanted ? "detail.turn-on" : "detail.turn-off", name), ct).ConfigureAwait(false);
            if (!yes)
            {
                host.Log.Info((wanted ? "Turn on" : "Turn off") + " a tunnel: not approved, nothing was changed.");
                return new RequestOutcome(RequestStatus.Declined, name);
            }
        }

        var result = await manager.ChangeAsync(name, wanted, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case ChangeStatus.Busy:
                return Refused(host, name, origin);

            case ChangeStatus.AlreadyThere:
                Resync();
                return new RequestOutcome(RequestStatus.AlreadyThere, name);

            case ChangeStatus.UnknownTunnel:
                return Failed(host, name, origin, RequestStatus.UnknownTunnel, result.Problem ?? new Problem("err.unknown", name), wanted);

            case ChangeStatus.ToolsUnusable:
                return Failed(host, name, origin, RequestStatus.Unusable, new Problem(result.Problem?.Key ?? "probe.no-wireguard"), wanted);

            case ChangeStatus.Failed:
                return Failed(host, name, origin, RequestStatus.Failed, result.Problem ?? new Problem("err.install-failed", name, -1), wanted);
        }

        // Done: the service was read back (running, or gone).
        host.Log.Info((wanted ? "Tunnel turned on" : "Tunnel turned off") + " (" + origin + ").");
        var options = OptionsFor(name, create: false);
        if (options is not null)
        {
            options.OnSinceUtc = wanted && options.AutoOffHours > 0 ? _deps!.Clock.Now.ToString("O") : "";
            SaveSettings();
        }
        lock (_uiGate) { if (_ui.TryGetValue(name, out var ui)) ui.Problem.Value = ""; }
        Resync();

        if (!wanted)
        {
            if (origin == Origin.Auto) Notify(host, ToastKind.Info, "toast.auto-off", name, options?.AutoOffHours ?? 0);
            else if (origin != Origin.Page) Notify(host, ToastKind.Info, "toast.off", name);
            return new RequestOutcome(RequestStatus.Done, name);
        }

        if (origin == Origin.Verb)
        {
            var handshake = await FinishTurnOnAsync(name, origin, ct).ConfigureAwait(false);
            return new RequestOutcome(RequestStatus.Done, name, null, handshake);
        }
        Track(Task.Run(async () =>
        {
            try { await FinishTurnOnAsync(name, origin, host.StopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception e) { host.Log.Warn("waiting for the handshake failed: " + e.GetType().Name, e); }
        }));
        return new RequestOutcome(RequestStatus.Done, name);
    }

    /// <summary>After a turn-on: waits up to 30 s for a handshake and says how it went (one notification, whatever the answer).</summary>
    private async Task<HandshakeOutcome> FinishTurnOnAsync(string name, Origin origin, CancellationToken ct)
    {
        var host = _host!;
        var outcome = await _manager!.WaitForHandshakeAsync(name, TunnelManager.HandshakeWait, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case HandshakeOutcome.Connected:
                if (origin != Origin.Page) Notify(host, ToastKind.Success, "toast.on-connected", name);
                break;
            case HandshakeOutcome.NoHandshake:
                Notify(host, ToastKind.Warning, "toast.no-handshake", name);   // always: the tunnel is on but the server does not answer
                lock (_uiGate) { if (_ui.TryGetValue(name, out var ui)) ui.Problem.Value = host.Localisation.F("err.no-handshake", name); }
                break;
            case HandshakeOutcome.Unreadable:
                if (origin != Origin.Page) Notify(host, ToastKind.Info, "toast.on", name);
                break;
        }
        try { await PollOnceAsync().ConfigureAwait(false); } catch (Exception) { }
        return outcome;
    }

    private RequestOutcome Refused(IHostServices host, string name, Origin origin)
    {
        if (origin != Origin.Verb) Notify(host, ToastKind.Warning, "toast.busy");
        Resync();
        return new RequestOutcome(RequestStatus.Busy, name);
    }

    private RequestOutcome Failed(IHostServices host, string name, Origin origin, RequestStatus status, Problem problem, bool wanted)
    {
        var text = host.Localisation.F(problem.Key, problem.Args);
        host.Log.Warn((wanted ? "Turn on" : "Turn off") + " failed: " + problem.Key);
        lock (_uiGate) { if (_ui.TryGetValue(name, out var ui)) ui.Problem.Value = text; }
        // Every failure is shown, whatever the origin (a page click gets the notification too: the host's in-window message needs the host's notification rule).
        host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.failed", text), ToastKind.Error));
        Resync();
        return new RequestOutcome(status, name, problem);
    }

    private void Notify(IHostServices host, ToastKind kind, string bodyKey, params object[] args)
    {
        try { host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of(bodyKey, args), kind)); }
        catch (Exception e) { host.Log.Warn("notification failed: " + e.GetType().Name); }
    }

    /// <summary>Puts every switch, text and tray check right after a change (the list is read again).</summary>
    private void Resync()
    {
        var host = _host;
        if (host is null || _stopping) return;
        var list = SafeList();
        if (list is null) return;
        ApplyList(host, list, firstTime: false);
        RefreshTexts(list);
    }

    // ------------------------------------------------------------------ edits that come from the page

    /// <summary>The switch of a tunnel: the host has already asked (the row has a hardware effect), so this only does it, in the background.</summary>
    private void OnToggleChanged(TunnelUi ui)
    {
        if (_ownChange || _stopping) return;
        bool wanted = ui.On.Value;
        var host = _host;
        if (host is null) return;
        Track(Task.Run(async () =>
        {
            try { await RequestChangeAsync(ui.Name, wanted, Origin.Page, host.StopToken, approved: true).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception e) { host.Log.Warn("switch failed: " + e.GetType().Name, e); }
            finally { try { Resync(); } catch (Exception) { } }   // whatever happened, the switch shows what is real
        }));
    }

    /// <summary>"Start with Windows" = the start type of the service: Automatic or Manual. The host has already asked.</summary>
    private void OnStartChanged(TunnelUi ui)
    {
        if (_ownChange || _stopping) return;
        bool automatic = ui.StartWithWindows.Value;
        var host = _host;
        var manager = _manager;
        if (host is null || manager is null) return;
        Track(Task.Run(() =>
        {
            try
            {
                int code = manager.SetStart(ui.Name, automatic);
                if (code != 0)
                {
                    var text = host.Localisation.F("err.start-type", ui.Name, new System.ComponentModel.Win32Exception(code).Message);
                    ui.Problem.Value = text;
                    host.Toast.Show(new ToastMessage(LocalisedText.Of("toast.title"), LocalisedText.Of("toast.failed", text), ToastKind.Error));
                }
                else host.Log.Info("Start type of a tunnel service set to " + (automatic ? "automatic" : "manual") + ".");
            }
            catch (Exception e) { host.Log.Warn("start type failed: " + e.GetType().Name, e); }
            finally { try { Resync(); } catch (Exception) { } }
        }));
    }

    private void OnAutoOffChanged(TunnelUi ui)
    {
        if (_ownChange) return;
        int hours = (int)Math.Clamp(Math.Round(ui.AutoOffHours.Value), 0, 168);
        if (hours != ui.AutoOffHours.Value) SetOwn(ui.AutoOffHours, hours);
        var options = OptionsFor(ui.Name, create: true)!;
        options.AutoOffHours = hours;
        // The count starts now when the tunnel is on already; it stops when the option is off.
        options.OnSinceUtc = hours > 0 && ui.On.Value ? _deps!.Clock.Now.ToString("O") : "";
        SaveSettings();
    }

    private void OnCheckPortChanged(TunnelUi ui)
    {
        if (_ownChange) return;
        int port = (int)Math.Clamp(Math.Round(ui.CheckPort.Value), 0, 65535);
        if (port != ui.CheckPort.Value) SetOwn(ui.CheckPort, port);
        OptionsFor(ui.Name, create: true)!.CheckPort = port;
        SaveSettings();
        if (!_stopping) Track(Task.Run(async () => { try { await PollOnceAsync().ConfigureAwait(false); } catch (Exception) { } }));
    }

    private void OnPublicAddressChanged(TunnelUi ui)
    {
        if (_ownChange) return;
        var text = (ui.PublicAddress.Value ?? "").Trim();
        if (text.Length > 200) text = text[..200];
        if (text != ui.PublicAddress.Value) SetOwn(ui.PublicAddress, text);
        OptionsFor(ui.Name, create: true)!.PublicAddress = text;
        SaveSettings();
        _host?.RequestPageRefresh();   // the link at the bottom of the page changes
    }

    /// <summary>Turns off the tunnels whose automatic turn-off time has come. The user chose this and was asked then, so it does not ask again; it always says so.</summary>
    private async Task AutoOffAsync(IHostServices host, TunnelList list)
    {
        var now = _deps!.Clock.Now;
        foreach (var info in list.Tunnels)
        {
            if (!info.IsOn) continue;
            var options = OptionsFor(info.Name, create: false);
            if (options is not { AutoOffHours: > 0 } || !DateTimeOffset.TryParse(options.OnSinceUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var since)) continue;
            if (now - since < TimeSpan.FromHours(options.AutoOffHours)) continue;
            host.Log.Info("Automatic turn-off time reached for a tunnel.");
            await RequestChangeAsync(info.Name, false, Origin.Auto, host.StopToken, approved: true).ConfigureAwait(false);
        }
    }
}
