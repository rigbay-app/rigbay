using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Rigbay.Abstractions;
using Windows.System;

namespace Rigbay.App;

/// <summary>
/// "Press the new shortcut": a small dialog that reads the next chord and shows it as key caps.
/// <para>
/// Keyboard (review AX-1): a chord needs Ctrl, Alt or Win. Tab, Shift+Tab, Enter, Space, Esc and the arrow keys pressed without
/// one of them are not taken as a shortcut, so they move between and press the buttons as in any dialog. Focus starts on
/// Cancel. A key pressed alone says what is missing instead of being taken.
/// </para>
/// </summary>
static class CaptureDialog
{
    /// <summary>Asks for a chord without knowing the shortcut set now (nothing is shown until a chord is pressed).</summary>
    /// <param name="title">The row's header.</param>
    public static Task<(bool Ok, HotkeyGesture? Gesture)> AskAsync(string title) => AskCoreAsync(title, false, null, null);

    /// <summary>Asks for a chord and shows the shortcut set now (quieter) until one is pressed; "Not set" and no Clear when there is none.</summary>
    /// <param name="title">The row's header.</param>
    /// <param name="current">The shortcut set now, or null when none is set.</param>
    public static Task<(bool Ok, HotkeyGesture? Gesture)> AskAsync(string title, HotkeyGesture? current) => AskCoreAsync(title, true, current, null);

    /// <param name="title">The row's header.</param>
    /// <param name="knowsCurrent">True when <paramref name="current"/> is the shortcut set now (null = none); false when it is not known.</param>
    /// <param name="current">The shortcut set now.</param>
    /// <param name="pressed">A chord to show as already pressed (screenshots); null for a real capture.</param>
    static async Task<(bool Ok, HotkeyGesture? Gesture)> AskCoreAsync(string title, bool knowsCurrent, HotkeyGesture? current, HotkeyGesture? pressed)
    {
        var root = Host.Window?.Content?.XamlRoot;
        if (root == null) return (false, null);
        HotkeyGesture? result = null;
        var hint = Ui.Text(Host.T("capture.hint"), "RbSecondaryText", wrap: true);
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, MinHeight = 34, Margin = new Thickness(10, 8, 10, 8), VerticalAlignment = VerticalAlignment.Center };
        // the place the keys appear: a dashed field, so an empty one reads as "keys go here" rather than as a gap
        var field = (Grid)XamlReader.Load(FieldXaml);
        field.Children.Add(keys);
        var status = Ui.Text("", "RbSecondaryCaption", wrap: true);
        status.Visibility = Visibility.Collapsed;
        var body = new StackPanel { Spacing = 12, MinWidth = 320, Children = { hint, field, status } };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = body,
            PrimaryButtonText = Host.T("capture.set"), SecondaryButtonText = Host.T("capture.clear"), CloseButtonText = Host.T("approval.cancel"),
            IsPrimaryButtonEnabled = false,
            // nothing to clear when it is known that no shortcut is set
            IsSecondaryButtonEnabled = !knowsCurrent || current != null,
            RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
        };
        Dialogs.Confirm(dialog);
        if (knowsCurrent) ShowKeys(keys, current, dim: true);

        void Announce(string text)
        {
            if (hint.XamlRoot == null) return;   // not on screen yet (a chord given by a script)
            try
            {
                var peer = FrameworkElementAutomationPeer.FromElement(hint) ?? FrameworkElementAutomationPeer.CreatePeerForElement(hint);
                peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, "rigbay.capture");
            }
            catch (Exception ex) { AppLog.Warn("capture: the screen reader notification failed", ex); }
        }
        void Say(string? problem)
        {
            status.Text = problem ?? "";
            status.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
            if (problem != null) Announce(problem);
        }
        void Take(HotkeyGesture g)
        {
            result = g;
            ShowKeys(keys, g, dim: false);
            dialog.IsPrimaryButtonEnabled = true;
            Say(null);
            Announce(g.ToString());
        }

        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) return;   // always the way out
            var name = KeyName(e.Key);
            if (name == null) return;   // a bare modifier, or a key a shortcut cannot use
            bool Down(VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var mods = HotkeyModifiers.None;
            if (Down(VirtualKey.Control)) mods |= HotkeyModifiers.Ctrl;
            if (Down(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
            if (Down(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
            if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) mods |= HotkeyModifiers.Win;
            bool strong = (mods & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;
            // without Ctrl, Alt or Win these keys do what they do in every dialog: move focus, press a button, close
            if (!strong && IsDialogKey(e.Key)) return;
            e.Handled = true;
            if (!strong) { Say(Host.T("capture.need-modifier")); return; }
            if (HotkeyGesture.ReservedReason(name) != null) { Say(Host.F("capture.reserved", name)); return; }
            if (HotkeyGesture.TryParse(Join(mods, name), out var g)) Take(g.Value);
        };
        if (pressed is { } p) Take(p);
        var r = await dialog.ShowAsync();
        return r switch { ContentDialogResult.Primary => (true, result), ContentDialogResult.Secondary => (true, null), _ => (false, null) };
    }

    /// <summary>
    /// The script step <c>capture:&lt;shortcut row header key&gt;[=&lt;chord&gt;]</c> (screenshots): opens the dialog of that row on
    /// the open module page, as its button does, optionally with a chord already pressed. Not awaited by the script.
    /// </summary>
    public static async Task ScriptAsync(string arg)
    {
        var eq = arg.IndexOf('=');
        var key = eq < 0 ? arg : arg[..eq];
        var driver = Host.Window.LastRenderer?.Drivers.FirstOrDefault(d => d.Row.HeaderKey == key);
        if (driver?.Row is not ShortcutRow row) { AppLog.Warn("script: no shortcut row '" + key + "' on this page"); return; }
        HotkeyGesture? pressed = eq >= 0 && HotkeyGesture.TryParse(arg[(eq + 1)..], out var g) ? g : null;
        try { await AskCoreAsync(driver.Name, true, row.Value.Value, pressed); }
        catch (Exception ex) { AppLog.Error("script: capture dialog failed", ex); }
    }

    static bool IsDialogKey(VirtualKey k) => k is VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Space
        or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down;

    static string Join(HotkeyModifiers mods, string key)
    {
        var p = new List<string>();
        if (mods.HasFlag(HotkeyModifiers.Ctrl)) p.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Alt)) p.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Shift)) p.Add("Shift");
        if (mods.HasFlag(HotkeyModifiers.Win)) p.Add("Win");
        p.Add(key);
        return string.Join('+', p);
    }

    /// <summary>One key cap per part of the chord; "Not set" when there is none. The panel's UIA name is the chord as text.</summary>
    static void ShowKeys(StackPanel keys, HotkeyGesture? g, bool dim)
    {
        keys.Children.Clear();
        if (g is not { } gesture)
        {
            keys.Children.Add(Ui.Text(Host.T("row.shortcut-none"), "RbSecondaryText").With(t => t.VerticalAlignment = VerticalAlignment.Center));
            AutomationProperties.SetName(keys, Host.T("row.shortcut-none"));
            return;
        }
        foreach (var part in gesture.ToString().Split('+')) keys.Children.Add(KeyCap(part, dim));
        AutomationProperties.SetName(keys, gesture.ToString());
    }

    // Built from XAML text so the theme brushes follow the dialog's own theme (a brush looked up in code would follow the app's).
    const string FieldXaml =
        "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        "<Rectangle RadiusX='6' RadiusY='6' StrokeThickness='1' StrokeDashArray='4,3' Stroke='{ThemeResource ControlStrongStrokeColorDefaultBrush}'/></Grid>";

    const string KeyCapXaml =
        "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' MinWidth='36' Padding='10,5,10,6' CornerRadius='4' BorderThickness='1,1,1,2' " +
        "Background='{ThemeResource ControlFillColorDefaultBrush}' BorderBrush='{ThemeResource ControlStrokeColorSecondaryBrush}'>" +
        "<TextBlock HorizontalAlignment='Center' FontWeight='SemiBold' Foreground='{ThemeResource TextFillColorPrimaryBrush}'/></Border>";

    static Border KeyCap(string text, bool dim)
    {
        var cap = (Border)XamlReader.Load(KeyCapXaml);
        var t = (TextBlock)cap.Child;
        t.Text = text;
        // the shortcut set now is drawn quieter than a chord just pressed
        if (dim) { t.FontWeight = FontWeights.Normal; cap.Opacity = 0.8; }
        return cap;
    }

    static string? KeyName(VirtualKey k)
    {
        if (k >= VirtualKey.A && k <= VirtualKey.Z) return ((char)('A' + (k - VirtualKey.A))).ToString();
        if (k >= VirtualKey.Number0 && k <= VirtualKey.Number9) return ((char)('0' + (k - VirtualKey.Number0))).ToString();
        if (k >= VirtualKey.F1 && k <= VirtualKey.F24) return "F" + (k - VirtualKey.F1 + 1);
        if (k >= VirtualKey.NumberPad0 && k <= VirtualKey.NumberPad9) return "Num" + (k - VirtualKey.NumberPad0);
        return k switch
        {
            VirtualKey.Space => "Space", VirtualKey.Enter => "Enter", VirtualKey.Tab => "Tab", VirtualKey.Back => "Backspace",
            VirtualKey.Delete => "Delete", VirtualKey.Insert => "Insert", VirtualKey.Home => "Home", VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp", VirtualKey.PageDown => "PageDown",
            VirtualKey.Up => "Up", VirtualKey.Down => "Down", VirtualKey.Left => "Left", VirtualKey.Right => "Right",
            _ => null,
        };
    }
}
