using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Windows.System;

namespace Rigbay.App;

/// <summary>"Press the new shortcut": a small dialog that reads the next chord.</summary>
static class CaptureDialog
{
    public static async Task<(bool Ok, HotkeyGesture? Gesture)> AskAsync(string title)
    {
        var root = Host.Window?.Content?.XamlRoot;
        if (root == null) return (false, null);
        HotkeyGesture? result = null;
        var hint = Ui.Text(Host.T("capture.hint"), "RbSecondaryText", wrap: true);
        var shown = Ui.Text("", weight: FontWeights.SemiBold);
        var body = new StackPanel { Spacing = 8, Children = { hint, shown } };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = body,
            PrimaryButtonText = Host.T("capture.set"), SecondaryButtonText = Host.T("capture.clear"), CloseButtonText = Host.T("approval.cancel"),
            IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
        };
        dialog.PreviewKeyDown += (_, e) =>
        {
            var name = KeyName(e.Key);
            if (name == null) return;   // a bare modifier
            if (e.Key == VirtualKey.Escape) return;
            e.Handled = true;
            var mods = HotkeyModifiers.None;
            bool Down(VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (Down(VirtualKey.Control)) mods |= HotkeyModifiers.Ctrl;
            if (Down(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
            if (Down(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
            if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) mods |= HotkeyModifiers.Win;
            if (HotkeyGesture.TryParse(string.Join('+', Enumerable.Empty<string>().Append(ModText(mods)).Where(s => s.Length > 0).Append(name)), out var g))
            {
                result = g;
                shown.Text = g.ToString();
                dialog.IsPrimaryButtonEnabled = true;
            }
        };
        var r = await dialog.ShowAsync();
        return r switch { ContentDialogResult.Primary => (true, result), ContentDialogResult.Secondary => (true, null), _ => (false, null) };
    }

    static string ModText(HotkeyModifiers m)
    {
        var p = new List<string>();
        if (m.HasFlag(HotkeyModifiers.Ctrl)) p.Add("Ctrl");
        if (m.HasFlag(HotkeyModifiers.Alt)) p.Add("Alt");
        if (m.HasFlag(HotkeyModifiers.Shift)) p.Add("Shift");
        if (m.HasFlag(HotkeyModifiers.Win)) p.Add("Win");
        return string.Join('+', p);
    }

    static string? KeyName(VirtualKey k)
    {
        if (k >= VirtualKey.A && k <= VirtualKey.Z) return ((char)('A' + (k - VirtualKey.A))).ToString();
        if (k >= VirtualKey.Number0 && k <= VirtualKey.Number9) return ((char)('0' + (k - VirtualKey.Number0))).ToString();
        if (k >= VirtualKey.F1 && k <= VirtualKey.F24) return "F" + (k - VirtualKey.F1 + 1);
        if (k >= VirtualKey.NumberPad0 && k <= VirtualKey.NumberPad9) return "Num" + (k - VirtualKey.NumberPad0);
        return k switch
        {
            VirtualKey.Space => "Space", VirtualKey.Enter => "Enter", VirtualKey.Tab => "Tab", VirtualKey.Back => "Backspace", VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert", VirtualKey.Home => "Home", VirtualKey.End => "End", VirtualKey.PageUp => "PageUp", VirtualKey.PageDown => "PageDown",
            VirtualKey.Up => "Up", VirtualKey.Down => "Down", VirtualKey.Left => "Left", VirtualKey.Right => "Right",
            _ => null,
        };
    }
}
