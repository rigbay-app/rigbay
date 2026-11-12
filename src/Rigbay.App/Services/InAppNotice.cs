using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// The transient message at the top of the window (the "in-window" half of <see cref="NotificationPolicy"/>). One bar: a new
/// message replaces the one shown. Safe to call from any thread.
/// </summary>
sealed class InAppNotice : IInAppNoticeSink
{
    readonly InfoBar _bar;
    readonly DispatcherQueueTimer _timer;

    /// <summary>The last message shown, for the self-test.</summary>
    public string? Last { get; private set; }
    public int Count { get; private set; }
    public bool IsOpen => _bar.IsOpen;

    public InAppNotice(InfoBar bar, DispatcherQueue dq)
    {
        _bar = bar;
        _timer = dq.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { _timer.Stop(); _bar.IsOpen = false; };
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
            _timer.Stop();
            _timer.Interval = Lifetime(kind);
            _timer.Start();
            Last = _bar.Title + " / " + body;
            Count++;
        }, "in-window notice");
    }
}
