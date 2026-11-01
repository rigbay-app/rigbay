using Microsoft.UI.Xaml;
using Rigbay.Abstractions;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// The approval service behind the contract's <c>HardwareEffect</c>: before a row (or a module-started write) changes the
/// machine, the user sees what will happen and the exact value, and answers. Without a window (command line) the answer is no.
/// </summary>
sealed class ApprovalPrompt : IApprovalPrompt
{
    static readonly SemaphoreSlim OneAtATime = new(1, 1);
    static ContentDialog? _current;

    /// <summary>Self-test and screenshots answer without a dialog (null = ask the user for real).</summary>
    public static bool? TestAnswer { get; set; }
    public static int Asked { get; private set; }
    public static string? LastQuestion { get; private set; }
    /// <summary>True when the last question was a revert (<see cref="ApprovalRequest.IsRevert"/>).</summary>
    public static bool LastWasRevert { get; private set; }
    public static bool IsOpen => _current != null;

    public async Task<bool> ConfirmAsync(ApprovalRequest request, CancellationToken ct)
    {
        var detail = request.Detail;
        LastQuestion = request.EffectText + (detail != null ? " | " + detail : "");
        LastWasRevert = request.IsRevert;
        Asked++;
        AppLog.For(request.ModuleId).Info("approval asked: " + LastQuestion);
        if (TestAnswer is { } canned) { AppLog.For(request.ModuleId).Info("approval answered by the test harness: " + canned); return canned; }
        if (Host.Window == null || Host.Dq == null) return false;

        await OneAtATime.WaitAsync(ct);
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            Host.Dq.TryEnqueue(async () =>
            {
                try
                {
                    Host.Window.ShowFromTray();
                    var root = Host.Window.Content.XamlRoot;
                    if (root == null) { tcs.TrySetResult(false); return; }
                    var body = new StackPanel { Spacing = 10, MaxWidth = 460 };
                    body.Children.Add(Ui.Text(request.ModuleName, "RbSecondaryCaption"));
                    body.Children.Add(Ui.Text(request.EffectText, wrap: true));
                    if (!string.IsNullOrEmpty(detail)) body.Children.Add(Ui.Text(detail, weight: Microsoft.UI.Text.FontWeights.SemiBold, wrap: true));
                    // a revert is asked about like any other change: its own title and wording, the detail names current -> previous
                    body.Children.Add(Ui.Text(Host.T(request.IsRevert ? "approval.hint-revert" : request.Revertible ? "approval.hint-revertible" : "approval.hint-not-revertible"), "RbSecondaryCaption", wrap: true));
                    var dialog = new ContentDialog
                    {
                        XamlRoot = root,
                        Title = Host.T(request.IsRevert ? "approval.revert-title" : "approval.title"),
                        Content = body,
                        PrimaryButtonText = Host.T(request.IsRevert ? "approval.revert-apply" : "approval.apply"),
                        CloseButtonText = Host.T("approval.cancel"),
                        DefaultButton = ContentDialogButton.Close,
                        RequestedTheme = ((FrameworkElement)Host.Window.Content).ActualTheme,
                    };
                    _current = dialog;
                    var result = await dialog.ShowAsync();
                    _current = null;
                    tcs.TrySetResult(result == ContentDialogResult.Primary);
                }
                catch (Exception ex) { AppLog.Error("approval dialog failed", ex); tcs.TrySetResult(false); }
            });
            using var reg = ct.Register(() => { Host.Dq.TryEnqueue(() => _current?.Hide()); tcs.TrySetResult(false); });
            return await tcs.Task;
        }
        finally { OneAtATime.Release(); }
    }

    /// <summary>Closes an open approval dialog as "cancel" (screenshots).</summary>
    public static void CancelOpen() => Host.Dq.TryEnqueue(() => _current?.Hide());
}
