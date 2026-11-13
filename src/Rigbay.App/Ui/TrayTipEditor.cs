using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rigbay.App.Services;
using Windows.System;

namespace Rigbay.App;

/// <summary>
/// Settings, "Tray tooltip": the mode (Automatic or Custom), a live preview of the exact text the icon gets with its length, and, in Custom, the list of
/// items (add from a menu grouped by module, label, new line, move up and down, remove). Every change is saved and put on the icon at once
/// (<see cref="TrayHost.UpdateTip"/>); the preview follows the readings with the page's own redraw tick, so a hidden page costs nothing.
/// </summary>
static class TrayTipEditor
{
    const string DeleteGlyph = "", UpGlyph = "", DownGlyph = "";

    /// <summary>The tooltip for the current setting: the icon's own, else (before the tray exists) the same builder.</summary>
    static TipResult Compose() => Host.Tray?.Compose() ?? TrayTipBuilder.Build(AppState.Settings.TrayTip, new TrayTipSource(Host.Core));

    /// <summary>Saves, puts the new tooltip on the icon.</summary>
    static void Apply()
    {
        AppState.Save();
        Host.Tray?.UpdateTip();
    }

    /// <summary>What was built for the last preview, so a test can find the controls.</summary>
    internal static string LastPreview { get; private set; } = "";

    public static UIElement Build(PageScope scope)
    {
        var tip = AppState.Settings.TrayTip;
        var panel = new StackPanel { Spacing = 2 };

        // ---- mode
        var mode = new ComboBox { MinWidth = 220 };
        mode.Items.Add(new ComboBoxItem { Content = Host.T("settings.tray-tip-auto"), Tag = TrayTipSettings.Automatic });
        mode.Items.Add(new ComboBoxItem { Content = Host.T("settings.tray-tip-custom"), Tag = TrayTipSettings.Custom });
        mode.SelectedIndex = tip.IsCustom ? 1 : 0;
        AutomationProperties.SetName(mode, Host.T("settings.tray-tip"));
        panel.Children.Add(new SettingsCard { Header = Host.T("settings.tray-tip"), Description = Host.T("settings.tray-tip-desc"), Content = mode, Tag = "tray-tip" });

        // ---- preview: the exact text of the icon and how much of the 127 characters it uses
        var previewText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(0, 4, 0, 0) };
        var dropped = Ui.Text("", "RbErrorText", wrap: true);
        dropped.Visibility = Visibility.Collapsed;
        var previewBox = new Border
        {
            Child = previewText, Padding = new Thickness(10, 6, 10, 8), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorAltBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            MinWidth = 160, MaxWidth = 460,
        };
        var previewHolder = new StackPanel { Spacing = 4, Children = { previewBox, dropped } };
        var counter = Ui.Text("", "RbSecondaryText");
        Ui.Tabular(counter);
        var previewCard = new SettingsCard { Header = Host.T("settings.tray-tip-preview"), Description = previewHolder, Content = counter, Tag = "tray-tip-preview" };
        panel.Children.Add(previewCard);

        // ---- custom only
        var custom = new StackPanel { Spacing = 2 };
        var showTitle = new ToggleSwitch { IsOn = tip.ShowTitle, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(showTitle, Host.T("settings.tray-tip-show-title"));
        custom.Children.Add(new SettingsCard { Header = Host.T("settings.tray-tip-show-title"), Content = showTitle });

        var itemsHeader = Ui.Text(Host.T("settings.tray-tip-items"), "RbSecondaryCaption");
        itemsHeader.Margin = new Thickness(1, 10, 0, 4);
        AutomationProperties.SetHeadingLevel(itemsHeader, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
        custom.Children.Add(itemsHeader);
        var itemsPanel = new StackPanel { Spacing = 2 };
        custom.Children.Add(itemsPanel);

        var add = new Button { Content = Host.T("settings.tray-tip-add") };
        AutomationProperties.SetName(add, Host.T("settings.tray-tip-add"));
        var addCard = new SettingsCard { Header = Host.T("settings.tray-tip-add"), Description = Host.T("settings.tray-tip-add-desc"), Content = add, Tag = "tray-tip-add" };
        custom.Children.Add(addCard);
        panel.Children.Add(custom);

        // ---- the rows
        var rows = new List<Row>();
        var saveLater = new DispatcherQueueTimerHolder(TimeSpan.FromMilliseconds(500), () => AppState.Save());
        scope.Add(() => saveLater.Stop());

        void Refresh()
        {
            var result = Compose();
            LastPreview = result.Text;
            previewText.Text = result.Text;
            counter.Text = TrayTipBuilder.Counter(result.Text);
            AutomationProperties.SetName(counter, Host.F("settings.tray-tip-count-name", result.Text.Length, TrayTipBuilder.MaxTip));
            AutomationProperties.SetName(previewText, Host.T("settings.tray-tip-preview") + ": " + result.Text.ReplaceLineEndings(", "));
            int drop = result.DroppedForLength;
            dropped.Text = drop == 0 ? "" : drop == 1 ? Host.F("settings.tray-tip-dropped-one", TrayTipBuilder.MaxTip) : Host.F("settings.tray-tip-dropped-many", drop, TrayTipBuilder.MaxTip);
            dropped.Visibility = drop == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (result.Items.Count == rows.Count)
                for (int i = 0; i < rows.Count; i++) rows[i].Show(result.Items[i]);
        }

        void Rebuild(int focusIndex = -1, FocusPart part = FocusPart.Label)
        {
            itemsPanel.Children.Clear();
            rows.Clear();
            var items = AppState.Settings.TrayTip.Items;
            if (items.Count == 0)
                itemsPanel.Children.Add(Ui.Text(Host.T("settings.tray-tip-empty"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 0, 0, 6)));
            for (int i = 0; i < items.Count; i++)
            {
                var row = new Row(items[i], i, items.Count);
                int index = i;
                row.LabelChanged += () => { Refresh(); Host.Tray?.UpdateTip(); saveLater.Restart(); };
                row.LabelCommitted += () => { saveLater.Stop(); Apply(); };
                row.NewLineChanged += () => { Apply(); Refresh(); };
                row.MoveRequested += (delta, p) =>
                {
                    if (!AppState.Settings.TrayTip.Move(index, delta)) return;
                    Apply(); Rebuild(index + delta, p); Refresh();
                };
                row.RemoveRequested += () =>
                {
                    AppState.Settings.TrayTip.Items.RemoveAt(index);
                    Apply();
                    Rebuild(Math.Min(index, AppState.Settings.TrayTip.Items.Count - 1), FocusPart.Remove);
                    if (AppState.Settings.TrayTip.Items.Count == 0) add.Focus(FocusState.Keyboard);
                    Refresh();
                };
                rows.Add(row);
                itemsPanel.Children.Add(row.Card);
            }
            if (focusIndex >= 0 && focusIndex < rows.Count) rows[focusIndex].Focus(part);
        }

        // ---- the add menu, built when it opens so the values in it are current
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => FillMenu(flyout, newId =>
        {
            var tipSettings = AppState.Settings.TrayTip;
            if (!tipSettings.Add(newId.Id, newId.ModuleId)) return;
            Apply();
            Rebuild(tipSettings.Items.Count - 1, FocusPart.Label);
            Refresh();
        });
        add.Flyout = flyout;

        void ApplyMode()
        {
            custom.Visibility = AppState.Settings.TrayTip.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        }

        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedItem is not ComboBoxItem it) return;
            var chosen = (string)it.Tag;
            var s = AppState.Settings.TrayTip;
            if (chosen == s.Mode) return;
            s.Mode = chosen;
            if (s.IsCustom && s.Items.Count == 0) s.SeedFrom(Host.Tray?.TipSource ?? new TrayTipSource(Host.Core));   // start from the tooltip that is on the icon now
            Apply();
            ApplyMode();
            Rebuild();
            Refresh();
        };
        showTitle.Toggled += (_, _) =>
        {
            AppState.Settings.TrayTip.ShowTitle = showTitle.IsOn;
            Apply();
            Refresh();
        };

        ApplyMode();
        Rebuild();
        Refresh();
        scope.Tick(Refresh);   // readings move: the page's own tick, which stops while the page is hidden
        return panel;
    }

    /// <summary>
    /// "Show in tray tooltip" for a reading tile (right click, or the menu key): a check mark that adds the metric to the custom tooltip or takes it out.
    /// While the tooltip is Automatic the mark says whether Automatic shows the reading now, and the first click turns Custom on from what Automatic shows,
    /// so one click changes one item and nothing else.
    /// </summary>
    internal static MenuFlyout TileMenu(string moduleId, string metricId)
    {
        var item = new ToggleMenuFlyoutItem { Text = Host.T("tile.show-in-tray-tip") };
        var menu = new MenuFlyout { Items = { item } };
        menu.Opening += (_, _) =>
        {
            item.Text = Host.T("tile.show-in-tray-tip");
            item.IsChecked = ShownInTip(metricId);
        };
        item.Click += (_, _) => SetInTip(moduleId, metricId, item.IsChecked);
        return menu;
    }

    /// <summary>Whether the custom list has the metric; while the tooltip is Automatic, whether Automatic shows it now.</summary>
    internal static bool ShownInTip(string metricId)
    {
        var s = AppState.Settings.TrayTip;
        if (s.IsCustom) return s.Contains(metricId);
        var seeded = new TrayTipSettings { Mode = TrayTipSettings.Custom };
        seeded.SeedFrom(Host.Tray?.TipSource ?? new TrayTipSource(Host.Core));
        return seeded.Contains(metricId);
    }

    /// <summary>What a click on "Show in tray tooltip" does: Custom is turned on (from what Automatic shows) when it was not, then the metric is added or taken out. Saved and on the icon at once.</summary>
    internal static void SetInTip(string moduleId, string metricId, bool on)
    {
        var s = AppState.Settings.TrayTip;
        if (!s.IsCustom)
        {
            s.Mode = TrayTipSettings.Custom;
            if (s.Items.Count == 0) s.SeedFrom(Host.Tray?.TipSource ?? new TrayTipSource(Host.Core));
        }
        if (on) s.Add(metricId, moduleId); else s.Remove(metricId);
        Apply();
    }

    enum FocusPart { Label, Up, Down, Remove }

    /// <summary>What the menu hands back.</summary>
    readonly record struct Pick(string Id, string ModuleId);

    static void FillMenu(MenuFlyout flyout, Action<Pick> chosen)
    {
        flyout.Items.Clear();
        var tip = AppState.Settings.TrayTip;
        var groups = (Host.Tray?.TipSource ?? new TrayTipSource(Host.Core)).Catalogue(Host.T("settings.tray-tip-state-line"));
        bool full = tip.Items.Count >= TrayTipSettings.MaxItems;
        foreach (var g in groups)
        {
            var sub = new MenuFlyoutSubItem { Text = g.ModuleName };
            foreach (var c in g.Choices)
            {
                var item = new MenuFlyoutItem { Text = c.Name + "  ·  " + c.Value, IsEnabled = !full && !tip.Contains(c.Id) };
                AutomationProperties.SetName(item, g.ModuleName + ": " + c.Name + ", " + c.Value + (tip.Contains(c.Id) ? ", " + Host.T("settings.tray-tip-added") : ""));
                var pick = new Pick(c.Id, c.ModuleId);
                item.Click += (_, _) => chosen(pick);
                sub.Items.Add(item);
            }
            flyout.Items.Add(sub);
        }
        if (groups.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = Host.T("settings.tray-tip-add-none"), IsEnabled = false });
    }

    /// <summary>One item of the list: a card with the label box, the new line choice and the move and remove buttons.</summary>
    sealed class Row
    {
        public readonly SettingsCard Card;
        readonly TrayTipItem _item;
        readonly TextBox _label = new() { Width = 132, MaxLength = TrayTipSettings.MaxLabel };
        readonly CheckBox _newLine = new() { MinWidth = 0, Padding = new Thickness(6, 0, 0, 0) };
        readonly Button _up = Icon(UpGlyph), _down = Icon(DownGlyph), _remove = Icon(DeleteGlyph);
        readonly TextBlock _description = Ui.Text("", "RbSecondaryCaption", trim: true);
        readonly TextBlock _header = Ui.Text("", null, trim: true);

        public event Action? LabelChanged, LabelCommitted, NewLineChanged, RemoveRequested;
        public event Action<int, FocusPart>? MoveRequested;

        static Button Icon(string glyph) => new() { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(8, 6, 8, 6), MinWidth = 0 };

        public Row(TrayTipItem item, int index, int count)
        {
            _item = item;
            _label.Text = item.Label;
            _newLine.Content = Host.T("settings.tray-tip-newline");
            _newLine.IsChecked = item.NewLine;
            _newLine.IsEnabled = index > 0;   // the first item starts a line anyway
            _up.IsEnabled = index > 0;
            _down.IsEnabled = index < count - 1;

            ToolTipService.SetToolTip(_up, Host.T("settings.tray-tip-up"));
            ToolTipService.SetToolTip(_down, Host.T("settings.tray-tip-down"));
            ToolTipService.SetToolTip(_remove, Host.T("settings.tray-tip-remove"));
            ToolTipService.SetToolTip(_label, Host.T("settings.tray-tip-label-tip"));

            _label.TextChanged += (_, _) =>
            {
                _item.Label = TrayTipSettings.CleanLabel(_label.Text);
                LabelChanged?.Invoke();
            };
            _label.LostFocus += (_, _) => LabelCommitted?.Invoke();
            _newLine.Click += (_, _) => { _item.NewLine = _newLine.IsChecked == true; NewLineChanged?.Invoke(); };
            _up.Click += (_, _) => MoveRequested?.Invoke(-1, FocusPart.Up);
            _down.Click += (_, _) => MoveRequested?.Invoke(1, FocusPart.Down);
            _remove.Click += (_, _) => RemoveRequested?.Invoke();

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(_label);
            controls.Children.Add(_newLine);
            controls.Children.Add(_up);
            controls.Children.Add(_down);
            controls.Children.Add(_remove);

            // keyboard: Alt+Up and Alt+Down move the item from anywhere in the row
            AddAccelerator(controls, VirtualKey.Up, -1);
            AddAccelerator(controls, VirtualKey.Down, 1);

            Card = new SettingsCard { Header = _header, Description = _description, Content = controls, Tag = "tray-tip-item" };
        }

        void AddAccelerator(UIElement host, VirtualKey key, int delta)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Menu };
            a.Invoked += (_, args) =>
            {
                // the focus part the move was asked from: the control that has focus, so the keyboard user stays where they were
                var focused = FocusManager.GetFocusedElement(host.XamlRoot);
                var p = ReferenceEquals(focused, _up) ? FocusPart.Up : ReferenceEquals(focused, _down) ? FocusPart.Down : FocusPart.Label;
                MoveRequested?.Invoke(delta, p);
                args.Handled = true;
            };
            host.KeyboardAccelerators.Add(a);
        }

        /// <summary>Shows what became of the item: its name, its module and what it says now (or why it says nothing).</summary>
        public void Show(TipItemResult r)
        {
            bool isState = TrayTipIds.IsState(_item.Id, out _);
            // a state line is named "State line" (its text is the value, shown below); a reading by its label, the owner's or the default one
            var name = isState && _item.Label.Length == 0 ? Host.T("settings.tray-tip-state-line") : r.Title.Length > 0 ? r.Title : _item.Id;
            _header.Text = name;
            string status = r.Status switch
            {
                TipItemStatus.Shown => r.Text ?? "",
                TipItemStatus.TooLong => Host.T("settings.tray-tip-status-long"),
                TipItemStatus.ModuleOff => Host.T("settings.tray-tip-status-off"),
                TipItemStatus.NoReading => Host.T("settings.tray-tip-status-none"),
                TipItemStatus.Stale => Host.T("settings.tray-tip-status-stale"),
                _ => Host.T("settings.tray-tip-status-missing"),
            };
            _description.Text = r.Group.Length > 0 ? r.Group + "  ·  " + status : status;
            _description.Style = Ui.Style(r.Status is TipItemStatus.Shown ? "RbSecondaryCaption" : "RbErrorText");
            // the default label is the box's hint, so the owner sees what an empty box means
            _label.PlaceholderText = !isState && _item.Label.Length == 0 && r.Status != TipItemStatus.Missing && r.Title.Length > 0 ? r.Title : Host.T("settings.tray-tip-label");
            var up = Host.F("settings.tray-tip-move-up", name);
            var down = Host.F("settings.tray-tip-move-down", name);
            AutomationProperties.SetName(_up, up);
            AutomationProperties.SetName(_down, down);
            AutomationProperties.SetName(_remove, Host.F("settings.tray-tip-remove-of", name));
            AutomationProperties.SetName(_label, Host.F("settings.tray-tip-label-of", name));
            AutomationProperties.SetName(_newLine, Host.F("settings.tray-tip-newline-of", name));
        }

        public void Focus(FocusPart part)
        {
            Control target = part switch { FocusPart.Up when _up.IsEnabled => _up, FocusPart.Down when _down.IsEnabled => _down, FocusPart.Remove => _remove, _ => _label };
            Host.Dq.TryEnqueue(() => target.Focus(FocusState.Keyboard));
        }
    }
}
