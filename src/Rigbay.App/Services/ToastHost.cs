using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// Notifications outside the window. It is the core's <see cref="IToastSink"/>. Nothing here is silent: a notification that is not
/// shown, and why, is always a line in the log.
/// <para>
/// Rigbay runs elevated (its manifest asks for it), and <c>AppNotificationManager</c> does not work in an elevated process: its
/// <c>IsSupported()</c> is false, <c>Setting</c> says Unsupported, and <c>Show</c> returns without error and without a toast (found
/// on 2026-11-12; <c>docs/APP.md</c>, "Notifications"). So an elevated Rigbay shows them as balloons from its tray icon, which
/// Windows 10 and 11 draw as ordinary toast banners and which work elevated. The AppNotificationManager path stays for a process that
/// is not elevated.
/// </para>
/// </summary>
sealed class ToastHost : IToastSink
{
    bool _registered;
    public int Shown { get; private set; }
    public int Failed { get; private set; }

    /// <summary>True when <c>AppNotificationManager</c> is registered (only possible when the process is not elevated).</summary>
    public bool Registered => _registered;

    /// <summary>True when notifications go through the tray icon's balloon (the process is elevated).</summary>
    public bool UsesBalloon { get; private set; }

    /// <summary>Why Windows will not show our toasts (a "Disabled..." <see cref="AppNotificationSetting"/>), or null when it is not known to block them.</summary>
    public string? BlockedBy { get; private set; }

    /// <summary>The raw <see cref="AppNotificationSetting"/> last read (for the log and the self-test).</summary>
    public string? Setting { get; private set; }

    /// <summary>The last text accepted by Windows (self-test).</summary>
    public string? LastShown { get; private set; }

    public void Register()
    {
        bool supported = false;
        try { supported = AppNotificationManager.IsSupported(); }
        catch (Exception ex) { AppLog.Warn("toast: AppNotificationManager.IsSupported failed", ex); }

        if (!supported)
        {
            UsesBalloon = true;
            AppLog.Info("toast: AppNotificationManager is not supported in this process (it is elevated); notifications are shown as balloons from the tray icon");
            return;
        }
        try
        {
            var mgr = AppNotificationManager.Default;
            mgr.NotificationInvoked += (_, _) => { };   // clicking a toast does nothing yet
            mgr.Register();
            _registered = true;
            AppLog.Info("toast: registered");
            CheckSetting();
            AppLog.Info($"toast: Windows notification setting for this app: {Setting}");
        }
        catch (Exception ex)
        {
            UsesBalloon = true;
            AppLog.Warn("toast: register failed; notifications are shown as balloons from the tray icon", ex);
        }
    }

    public void Unregister()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch { }
        _registered = false;
    }

    /// <summary>
    /// Reads whether Windows lets this app show toasts (settings, group policy). Only the "Disabled..." answers count as blocked.
    /// It cannot see Focus assist / Do not disturb, which hold a delivered notification back from popping up.
    /// </summary>
    string? CheckSetting()
    {
        try
        {
            var s = AppNotificationManager.Default.Setting;
            Setting = s.ToString();
            bool blocked = s is AppNotificationSetting.DisabledForApplication or AppNotificationSetting.DisabledForUser
                or AppNotificationSetting.DisabledByGroupPolicy or AppNotificationSetting.DisabledByManifest;
            BlockedBy = blocked ? Setting : null;
            if (blocked) AppLog.Warn($"toast: Windows has notifications for Rigbay switched off ({Setting}); they will not appear until the user turns them on in Settings > System > Notifications");
            return BlockedBy;
        }
        catch (Exception ex)
        {
            AppLog.Warn("toast: could not read the notification setting", ex);
            return null;
        }
    }

    public void Show(string title, string body, ToastKind kind = ToastKind.Info)
    {
        try
        {
            if (!UsesBalloon && !_registered) { Failed++; AppLog.Warn($"notification NOT shown (not set up yet): {title} / {body}"); return; }
            if (UsesBalloon) { ShowBalloon(title, body, kind); return; }

            if (CheckSetting() is { } blocked) { Failed++; AppLog.Warn($"toast NOT shown (Windows setting: {blocked}): {title} / {body}"); return; }
            var n = new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification();
            AppNotificationManager.Default.Show(n);
            if (n.Id == 0)
            {
                // Show returns without error when the platform did not take it; an id is assigned only to one it did.
                AppLog.Warn($"toast: AppNotificationManager did not take it (no id); showing it as a balloon instead: {title} / {body}");
                ShowBalloon(title, body, kind);
                return;
            }
            Shown++;
            LastShown = title + " / " + body;
            AppLog.Info($"toast shown (id {n.Id}): {title} / {body}");
        }
        catch (Exception ex) { Failed++; AppLog.Warn("toast: show failed", ex); }
    }

    void ShowBalloon(string title, string body, ToastKind kind)
    {
        if (Host.Tray is { } tray && tray.ShowBalloon(title, body, kind))
        {
            Shown++;
            LastShown = title + " / " + body;
            AppLog.Info($"notification sent to the tray icon: {title} / {body}");
        }
        else Failed++;
    }

    public void Show(string moduleId, string title, string body, ToastKind kind)
    {
        var name = Host.Core.Find(moduleId)?.DisplayName ?? moduleId;
        Show(name + ": " + title, body, kind);
    }
}
