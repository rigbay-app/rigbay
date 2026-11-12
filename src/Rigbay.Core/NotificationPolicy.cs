using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>Where one notification goes.</summary>
/// <param name="Toast">A Windows toast.</param>
/// <param name="InApp">A transient message at the top of the window.</param>
public readonly record struct NoticeRoute(bool Toast, bool InApp);

/// <summary>
/// The one rule for telling the user about a hardware change (docs/APP.md, "Notifications"): the result is shown where the user is
/// looking. A pure function, so every case is a plain test.
/// </summary>
public static class NotificationPolicy
{
    /// <param name="kind">What happened.</param>
    /// <param name="origin">What caused it.</param>
    /// <param name="windowInUse">The window is visible, not minimised and the foreground window.</param>
    /// <param name="canShowInApp">The app can show a message inside the window.</param>
    public static NoticeRoute Decide(ToastKind kind, ToastOrigin origin, bool windowInUse, bool canShowInApp = true)
    {
        bool inApp = windowInUse && canShowInApp;

        // A failure or a refusal always reaches the user outside the window as well; inside, it is shown too when they look.
        if (kind is ToastKind.Warning or ToastKind.Error) return new NoticeRoute(Toast: true, InApp: inApp);

        // The user clicked it in the window they are looking at: a message there is enough, no toast.
        if (origin == ToastOrigin.Page && inApp) return new NoticeRoute(Toast: false, InApp: true);

        // Hidden, minimised or in the background; or not from the page (hotkey, tray, CLI verb, auto-switch).
        return new NoticeRoute(Toast: true, InApp: false);
    }
}
