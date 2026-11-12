using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// The transient message at the top of the window (the "in-window" half of <see cref="NotificationPolicy"/>). One bar: a new
/// message replaces the one shown. Safe to call from any thread.
/// <para>
/// Accessibility (review AX-2): the bar is a live region and each message is also raised as a UIA notification, so a screen
/// reader speaks the result of a change. It closes by itself after <see cref="Lifetime"/>, but never while the pointer is over
/// it or keyboard focus is inside it; the time starts again when both have left.
/// </para>
/// </summary>
sealed class InAppNotice : IInAppNoticeSink
{
    readonly InfoBar _bar;
    readonly DispatcherQueueTimer _timer;
    bool _pointerIn, _focusIn;
    TimeSpan _lifetime;

    /// <summary>The last message shown, for the self-test.</summary>
    public string? Last { get; private set; }
    public int Count { get; private set; }
    public bool IsOpen => _bar.IsOpen;
    /// <summary>True while the close timer waits because the pointer or focus is on the bar (self-test).</summary>
    public bool IsHeld => _bar.IsOpen && !_timer.IsRunning;

    public InAppNotice(InfoBar bar, DispatcherQueue dq)
    {
        _bar = bar;
        _timer = dq.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { _timer.Stop(); _bar.IsOpen = false; };
        AutomationProperties.SetLiveSetting(_bar, AutomationLiveSetting.Assertive);
        _bar.PointerEntered += (_, _) => { _pointerIn = true; Hold(); };
        _bar.PointerExited += (_, _) => { _pointerIn = false; Release(); };
        _bar.PointerCanceled += (_, _) => { _pointerIn = false; Release(); };
        _bar.GotFocus += (_, _) => { _focusIn = true; Hold(); };
        _bar.LostFocus += (_, _) =>
        {
            // LostFocus also fires when focus moves between the bar's own parts (the close button and the text)
            _focusIn = _bar.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject f && IsInside(f);
            Release();
        };
        _bar.Closed += (_, _) => _timer.Stop();
    }

    public static TimeSpan Lifetime(ToastKind kind) => kind is ToastKind.Warning or ToastKind.Error ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(6);

    public void Show(string moduleId, string title, string body, ToastKind kind)
    {
        var name = Host.Core.Find(moduleId)?.DisplayName ?? moduleId;
        Ui.Post(null, () =>
        {
            _bar.Severity = kind switch
            {
                ToastKind.Success => InfoBarSeverity.Success,
                ToastKind.Warning => InfoBarSeverity.Warning,
                ToastKind.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            };
            _bar.Title = name + ": " + title;
            _bar.Message = body;
            _bar.IsOpen = true;
            _lifetime = Lifetime(kind);
            _timer.Stop();
            if (!_pointerIn && !_focusIn) { _timer.Interval = _lifetime; _timer.Start(); }
            Last = _bar.Title + " / " + body;
            Count++;
            Announce(_bar.Title + ". " + body, kind);
        }, "in-window notice");
    }

    void Announce(string text, ToastKind kind)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(_bar) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_bar);
            // a failure interrupts what the reader is saying; a success waits its turn
            var processing = kind is ToastKind.Warning or ToastKind.Error ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent;
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, processing, text, "rigbay.notice");
        }
        catch (Exception ex) { AppLog.Warn("in-window notice: the screen reader notification failed", ex); }
    }

    void Hold() => _timer.Stop();

    void Release()
    {
        if (_pointerIn || _focusIn || !_bar.IsOpen) return;
        _timer.Stop();
        _timer.Interval = _lifetime > TimeSpan.Zero ? _lifetime : Lifetime(ToastKind.Info);
        _timer.Start();
    }

    bool IsInside(DependencyObject d)
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p)) if (ReferenceEquals(p, _bar)) return true;
        return false;
    }

    /// <summary>
    /// The script step <c>notice:&lt;module id&gt;,&lt;success|info|warning|error&gt;,&lt;title key&gt;,&lt;body key&gt;</c> (screenshots):
    /// shows the message as that module's toast would while the window is in use. Keys are the module's own (no prefix).
    /// </summary>
    public static void ScriptSample(string arg)
    {
        var p = arg.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 4 || Host.Core.Find(p[0]) is not { } entry) { AppLog.Warn("script: notice needs <module id>,<kind>,<title key>,<body key>: '" + arg + "'"); return; }
        var kind = p[1] switch { "warning" => ToastKind.Warning, "error" => ToastKind.Error, "info" => ToastKind.Info, _ => ToastKind.Success };
        Host.Window.InAppNotice.Show(entry.Id, entry.Localisation.T(p[2]), entry.Localisation.T(p[3]), kind);
    }
}
