using Rigbay.Abstractions;
using Rigbay.Core;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>The one rule for telling the user about a change (docs/APP.md, "Notifications"): where each message goes.</summary>
public class NotificationPolicyTests
{
    // kind, origin, window in use -> toast, in-window
    [Theory]
    // The user clicked it on the page and is looking at the window: a message in the window, no toast.
    [InlineData(ToastKind.Success, ToastOrigin.Page, true, false, true)]
    [InlineData(ToastKind.Info, ToastOrigin.Page, true, false, true)]
    // The window is hidden, minimised or not in front: a toast, nothing inside the window.
    [InlineData(ToastKind.Success, ToastOrigin.Page, false, true, false)]
    [InlineData(ToastKind.Info, ToastOrigin.Page, false, true, false)]
    // Not from the page (hotkey, tray item, verb, auto-switch): always a toast, even with the window in front.
    [InlineData(ToastKind.Success, ToastOrigin.Background, true, true, false)]
    [InlineData(ToastKind.Success, ToastOrigin.Background, false, true, false)]
    [InlineData(ToastKind.Info, ToastOrigin.Background, true, true, false)]
    // Failures and refusals always get a toast; with the window in use they are shown inside it as well.
    [InlineData(ToastKind.Warning, ToastOrigin.Page, true, true, true)]
    [InlineData(ToastKind.Error, ToastOrigin.Page, true, true, true)]
    [InlineData(ToastKind.Warning, ToastOrigin.Page, false, true, false)]
    [InlineData(ToastKind.Error, ToastOrigin.Page, false, true, false)]
    [InlineData(ToastKind.Warning, ToastOrigin.Background, true, true, true)]
    [InlineData(ToastKind.Error, ToastOrigin.Background, false, true, false)]
    public void Each_case_goes_where_the_user_is_looking(ToastKind kind, ToastOrigin origin, bool windowInUse, bool toast, bool inApp)
    {
        var route = NotificationPolicy.Decide(kind, origin, windowInUse);
        Assert.Equal(new NoticeRoute(toast, inApp), route);
    }

    [Theory]
    [InlineData(ToastKind.Success, ToastOrigin.Page)]
    [InlineData(ToastKind.Warning, ToastOrigin.Page)]
    [InlineData(ToastKind.Error, ToastOrigin.Background)]
    public void Without_a_window_message_everything_is_a_toast(ToastKind kind, ToastOrigin origin)
    {
        var route = NotificationPolicy.Decide(kind, origin, windowInUse: true, canShowInApp: false);
        Assert.Equal(new NoticeRoute(Toast: true, InApp: false), route);
    }

    [Fact]
    public void A_message_never_goes_nowhere()
    {
        foreach (var kind in Enum.GetValues<ToastKind>())
        foreach (var origin in Enum.GetValues<ToastOrigin>())
        foreach (var inUse in new[] { true, false })
        foreach (var canShow in new[] { true, false })
        {
            var r = NotificationPolicy.Decide(kind, origin, inUse, canShow);
            Assert.True(r.Toast || r.InApp, $"{kind}/{origin}/inUse={inUse}/canShow={canShow}");
        }
    }
}

/// <summary>The host's toast service, reached through a real module (the sample's Reset button reports with <see cref="ToastOrigin.Page"/>).</summary>
public class NotificationRoutingTests
{
    private sealed class ToastRecorder(List<string> log) : IToastSink
    {
        public void Show(string moduleId, string title, string body, ToastKind kind) => log.Add($"toast:{moduleId}:{body}");
    }

    private sealed class InAppRecorder(List<string> log) : IInAppNoticeSink
    {
        public void Show(string moduleId, string title, string body, ToastKind kind) => log.Add($"window:{moduleId}:{body}");
    }

    private static async Task<List<string>> PressResetAsync(bool windowInUse, bool withWindowSink = true)
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var module = host.Find("sample")!;
        var log = new List<string>();
        host.ToastSink = new ToastRecorder(log);
        if (withWindowSink) host.InAppSink = new InAppRecorder(log);
        host.WindowInUse = () => windowInUse;

        var reset = module.Page!.EnumerateRows().OfType<ButtonRow>().Single(b => b.HeaderKey == "row.reset.header");
        var result = await RowEditors.InvokeButtonAsync(module, reset);
        Assert.True(result.Succeeded);
        host.Shutdown();
        return log;
    }

    [Fact]
    public async Task A_page_result_with_the_window_in_use_is_shown_in_the_window_only()
        => Assert.Equal(new[] { "window:sample:Settings were reset" }, await PressResetAsync(windowInUse: true));

    [Fact]
    public async Task A_page_result_with_the_window_hidden_or_in_the_background_is_a_toast_only()
        => Assert.Equal(new[] { "toast:sample:Settings were reset" }, await PressResetAsync(windowInUse: false));

    [Fact]
    public async Task Without_a_window_sink_a_page_result_falls_back_to_a_toast()
        => Assert.Equal(new[] { "toast:sample:Settings were reset" }, await PressResetAsync(windowInUse: true, withWindowSink: false));
}
