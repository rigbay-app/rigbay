using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// The approval service behind the contract's <c>HardwareEffect</c>: before a row (or a module-started write) changes the
/// machine, the user sees what will happen and the exact value, and answers. Without a window (command line) the answer is no.
/// <para>
/// The dialog (review DL-1, DL-2): the title is the change itself as a question ("Set fan mode to Maximum?"), then the module's
/// effect sentence, then one caption line (module · current value · whether it can be reverted). The primary button names the
/// change ("Set to Maximum") and wears the accent; keyboard focus starts on Cancel, so Enter alone never writes.
/// </para>
/// </summary>
sealed class ApprovalPrompt : IApprovalPrompt
{
    static readonly SemaphoreSlim OneAtATime = new(1, 1);
    static ContentDialog? _current;

    /// <summary>Self-test and screenshots answer without a dialog (null = ask the user for real).</summary>
    public static bool? TestAnswer { get; set; }
    public static int Asked { get; private set; }
    public static string? LastQuestion { get; private set; }
    /// <summary>The title of the last dialog built (self-test, screenshots).</summary>
    public static string? LastTitle { get; private set; }
    /// <summary>True when the last question was a revert (<see cref="ApprovalRequest.IsRevert"/>).</summary>
    public static bool LastWasRevert { get; private set; }
    public static bool IsOpen => _current != null;

    public async Task<bool> ConfirmAsync(ApprovalRequest request, CancellationToken ct)
    {
        var detail = request.Detail;
        LastQuestion = request.EffectText + (detail != null ? " | " + detail : "");
        LastWasRevert = request.IsRevert;
        LastTitle = Compose(request).Title;
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
                    var text = Compose(request);
                    var body = new StackPanel { Spacing = 8, MaxWidth = 460 };
                    // the change, when the title could not say it (a module-started write with its own wording)
                    if (text.DetailLine is { } line) body.Children.Add(Ui.Text(line, weight: Microsoft.UI.Text.FontWeights.SemiBold, wrap: true));
                    body.Children.Add(Ui.Text(request.EffectText, wrap: true));
                    body.Children.Add(Ui.Text(text.Caption, "RbSecondaryCaption", wrap: true).With(t => t.Margin = new Thickness(0, 4, 0, 0)));
                    var dialog = new ContentDialog
                    {
                        XamlRoot = root,
                        Title = text.Title,
                        Content = body,
                        PrimaryButtonText = text.Primary,
                        CloseButtonText = Host.T("approval.cancel"),
                        RequestedTheme = ((FrameworkElement)Host.Window.Content).ActualTheme,
                    };
                    Dialogs.Confirm(dialog);
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

    /// <summary>The words of one approval dialog.</summary>
    /// <param name="Title">The question: the change itself, or a general question when the change cannot be read from the detail.</param>
    /// <param name="Primary">The button that makes the change.</param>
    /// <param name="DetailLine">The module's own wording of the change, shown under the title when the title could not carry it.</param>
    /// <param name="Caption">Module name · current value · whether it can be reverted.</param>
    internal sealed record Words(string Title, string Primary, string? DetailLine, string Caption);

    // The detail the host builds for a row ("Fan mode: Auto → Maximum", RowRenderer and ValueEditor): setting, current value, new value.
    // A module-started write passes its own text, which may be anything; only this exact shape is taken apart.
    static readonly Regex HostDetail = new(@"^(?<what>[^:\r\n]{1,60}): (?<from>[^\r\n]*?) → (?<to>[^\r\n]+)$", RegexOptions.CultureInvariant);

    /// <summary>Longest new value that still fits on a dialog button ("Set to …"); a longer one keeps the plain verb.</summary>
    const int ButtonValueMax = 24;

    internal static Words Compose(ApprovalRequest r)
    {
        bool system = r.Kind == EffectKind.SystemSetting;
        var parts = new List<string> { r.ModuleName };
        var m = r.Detail is { } d ? HostDetail.Match(d) : Match.Empty;
        string title, primary;
        string? detailLine = null;
        if (m.Success && m.Groups["what"].Length + m.Groups["to"].Length <= 72)
        {
            var what = InSentence(m.Groups["what"].Value.Trim(), r.EffectText, r.ModuleName);
            var to = m.Groups["to"].Value.Trim();
            var from = m.Groups["from"].Value.Trim();
            title = Host.F(r.IsRevert ? "approval.revert-title-to" : "approval.title-to", what, to);
            primary = to.Length <= ButtonValueMax ? Host.F(r.IsRevert ? "approval.revert-to" : "approval.apply-to", to) : Host.T(r.IsRevert ? "approval.revert-apply" : "approval.apply");
            if (from.Length > 0) parts.Add(Host.F("approval.now", from));
        }
        else
        {
            title = Host.T(r.IsRevert ? (system ? "approval.revert-title-setting" : "approval.revert-title") : (system ? "approval.title-setting" : "approval.title"));
            primary = Host.T(r.IsRevert ? "approval.revert-apply" : "approval.apply");
            detailLine = string.IsNullOrWhiteSpace(r.Detail) ? null : r.Detail;
        }
        // a revert needs no promise of another revert; a change that cannot be undone says so every time
        if (!r.IsRevert) parts.Add(Host.T(r.Revertible ? "approval.hint-revertible" : "approval.hint-not-revertible"));
        return new Words(title, primary, detailLine, string.Join(" · ", parts));
    }

    /// <summary>
    /// A setting's name as it reads inside a sentence: "Fan mode" becomes "fan mode" in "Set fan mode to Maximum?". The first
    /// letter stays upper case when the first word looks like a name: an acronym or a mixed-case word (CPU, PL1, PredatorSense),
    /// or a word the module itself writes with a capital in the middle of a sentence (Steam, Windows).
    /// </summary>
    internal static string InSentence(string name, params string?[] context)
    {
        var first = name.Split(' ', 2)[0];
        if (first.Length < 2 || !char.IsUpper(first[0])) return name;
        for (int i = 1; i < first.Length; i++) if (!char.IsLower(first[i])) return name;
        foreach (var text in context) if (text != null && CapitalisedMidSentence(first, text)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    static bool CapitalisedMidSentence(string word, string text)
    {
        for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            int end = at + word.Length;
            if (end < text.Length && char.IsLetterOrDigit(text[end])) continue;      // part of a longer word
            if (at > 0 && char.IsLetterOrDigit(text[at - 1])) continue;
            int p = at - 1;
            while (p >= 0 && char.IsWhiteSpace(text[p])) p--;
            if (p >= 0 && text[p] is not ('.' or '!' or '?' or ':' or '(' or '"')) return true;   // not the first word of a sentence
        }
        return false;
    }
}

/// <summary>
/// The one way Rigbay's confirmation dialogs behave (review DL-1): the action that changes something wears the accent fill and
/// names what it does; nothing is the default button, so Enter does not answer for the user; keyboard focus starts on the
/// button that changes nothing (Cancel), so Enter and Space on it are safe and Tab reaches the action.
/// </summary>
static class Dialogs
{
    /// <summary>Applies the rule to <paramref name="dialog"/> before <c>ShowAsync</c>. Package A's Remove and Update dialogs can call it too.</summary>
    public static void Confirm(ContentDialog dialog)
    {
        dialog.DefaultButton = ContentDialogButton.None;
        dialog.PrimaryButtonStyle = Ui.Style("AccentButtonStyle");
        dialog.Opened += (d, _) => FocusClose(d);
    }

    /// <summary>Puts keyboard focus on the dialog's Close button (Cancel).</summary>
    public static void FocusClose(ContentDialog dialog)
    {
        if (Find(dialog, "CloseButton") is Button close && close.Visibility == Visibility.Visible) close.Focus(FocusState.Programmatic);
    }

    static FrameworkElement? Find(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (Find(child, name) is { } hit) return hit;
        }
        return null;
    }
}
