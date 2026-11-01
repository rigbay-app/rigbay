using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>Toast notifications through <c>AppNotificationManager</c> (unpackaged app). It is the core's <see cref="IToastSink"/>; fails quietly into the log.</summary>
sealed class ToastHost : IToastSink
{
    bool _registered;
    public int Shown { get; private set; }
    public bool Registered => _registered;

    public void Register()
    {
        try
        {
            var mgr = AppNotificationManager.Default;
            mgr.NotificationInvoked += (_, _) => { };   // clicking a toast does nothing yet
            mgr.Register();
            _registered = true;
            AppLog.Info("toast: registered");
        }
        catch (Exception ex) { AppLog.Warn("toast: register failed (notifications are off)", ex); }
    }

    public void Unregister()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch { }
        _registered = false;
    }

    public void Show(string title, string body)
    {
        if (!_registered) { AppLog.Info($"toast (not shown, not registered): {title} / {body}"); return; }
        try
        {
            var n = new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification();
            AppNotificationManager.Default.Show(n);
            Shown++;
        }
        catch (Exception ex) { AppLog.Warn("toast: show failed", ex); }
    }

    public void Show(string moduleId, string title, string body, ToastKind kind)
    {
        var name = Host.Core.Find(moduleId)?.DisplayName ?? moduleId;
        Show(name + ": " + title, body);
    }
}
