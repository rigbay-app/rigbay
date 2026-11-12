using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Rigbay.Abstractions;
using Rigbay.Core;
using Windows.Foundation;

namespace Rigbay.App;

/// <summary>
/// Turns the rows of a module's <see cref="PageModel"/> into <c>SettingsCard</c> rows. Values go through the core's
/// <see cref="ValueEditor{T}"/> (draft, validation, confirmation for hardware effects, previous value for Revert); every call
/// into the module goes through the module's fault boundary, and every change that comes from a module thread reaches the UI
/// thread through <see cref="Ui.Post"/>. A row that changes hardware: shows what will be written, asks, applies, shows the
/// value the module reports afterwards, and offers Revert.
/// <para>A row's control can be drawn in three places (<see cref="Place"/>): in its own card (a group, the default), in the header of the
/// expander that holds it (<see cref="ExpanderRow.HeaderRow"/>), or as the page's primary control (<see cref="PageModel.Primary"/>, large, in
/// the card under the page header). The editor, the approval and Revert are the same in all three.</para>
/// </summary>
sealed class RowRenderer
{
    /// <summary>Per row instance: the last status line shown next to it, so it survives a page rebuild. Editors are cached by the core (<c>RowEditors.For</c>).</summary>
    sealed class RowState { public string Status = ""; public bool StatusError; public bool ButtonCanRevert; }

    static readonly ConditionalWeakTable<Row, RowState> States = new();

    /// <summary>Where a row's control is drawn.</summary>
    enum Place
    {
        /// <summary>In a card of its own, in a group or an expander.</summary>
        Card,
        /// <summary>Alone, as the header control of the expander that holds it (the expander shows the header and the line).</summary>
        Header,
        /// <summary>Large, as the page's main control (header, live line, control; a segmented choice under them).</summary>
        Primary,
    }

    readonly ModuleEntry _e;
    readonly PageScope _scope;
    string? _holder;   // header text of the expander whose children are being drawn: {0} of a ButtonRow.ConfirmKey question
    ILocalisation L => _e.Localisation;

    public int RowsRendered { get; private set; }
    /// <summary>Test hooks: one per value row, to drive the same code the controls drive.</summary>
    public List<RowDriver> Drivers { get; } = new();
    /// <summary>The choice controls drawn so far (the self-test checks which of them became segments).</summary>
    public List<ChoiceSelector> Choices { get; } = new();

    /// <summary>The question the last <see cref="ButtonRow.ConfirmKey"/> dialog asked (self-test), or null.</summary>
    public static string? LastConfirmQuestion { get; private set; }

    public RowRenderer(ModuleEntry entry, PageScope scope) { _e = entry; _scope = scope; }

    /// <summary>What the self-test uses to act like the user without a mouse.</summary>
    public sealed class RowDriver
    {
        public required Row Row { get; init; }
        public required string Name { get; init; }
        public required Func<object?, Task> Edit { get; init; }
        public required Func<Task> Apply { get; init; }
        public required Func<Task> Revert { get; init; }
        public required Func<string> Status { get; init; }
        public required Func<bool> CanRevert { get; init; }
    }

    public UIElement RenderGroups(PageModel page)
    {
        var all = new StackPanel { Spacing = 0 };
        foreach (var group in page.Groups)
        {
            all.Children.Add(new TextBlock { Text = L.T(group.HeaderKey), Style = Ui.Style("RbGroupHeader") });
            var box = new StackPanel { Spacing = 2 };
            foreach (var row in group.Rows) box.Children.Add(Render(row));
            all.Children.Add(box);
        }
        return all;
    }

    public UIElement Render(Row row) => Render(row, Place.Card);

    /// <summary>
    /// The page's primary row (<see cref="PageModel.Primary"/>) drawn large, without a card of its own (the page puts it in the card under the
    /// header): its header, description and live line at the left, the status, Revert and the control at the right; a choice of 2 to 5 short
    /// options is a segmented control under the header.
    /// </summary>
    public UIElement RenderPrimary(Row row) => Render(row, Place.Primary);

    UIElement Render(Row row, Place place)
    {
        RowsRendered++;
        try
        {
            // an expander, a message or a custom control has no control of its own to put in a header or in the primary slot: drawn as a card
            if (place != Place.Card && row is ExpanderRow or InfoRow or CustomRow) place = Place.Card;
            return row switch
            {
                ToggleRow t => Toggle(t, place),
                ChoiceRow c => Choice(c, place),
                NumberRow n => Number(n, place),
                TextRow x => TextEntry(x, place),
                ShortcutRow s => Shortcut(s, place),
                ButtonRow b => Button(b, place),
                ValueRow v => ValueText(v, place),
                ReadingRow r => Reading(r, place),
                ExpanderRow ex => Expander(ex),
                InfoRow i => Info(i),
                CustomRow cr => Custom(cr),
                _ => new TextBlock { Text = "Unknown row kind " + row.GetType().Name },
            };
        }
        catch (Exception ex)
        {
            // The renderer is host code; a bug here must not take the page down, and it is not the module's fault.
            AppLog.For(_e.Id).Error("rendering row '" + row.HeaderKey + "' failed", ex);
            return new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = L.T(row.HeaderKey), Message = ex.GetType().Name + ": " + ex.Message };
        }
    }

    // ---------- card + chrome ----------

    static FontFamily? _icons;
    /// <summary>The system icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets before).</summary>
    public static FontFamily Icons => _icons ??= new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets");

    /// <summary>Info glyph after a header whose row has a <see cref="Row.HelpKey"/>.</summary>
    public const string InfoGlyph = "";

    /// <summary>
    /// The header of a row: its text, and when the row has a <see cref="Row.HelpKey"/> a small info glyph after the text, set inline so a long
    /// header still wraps; the explanation is its tooltip and the accessible help text. A plain string when there is nothing to add.
    /// </summary>
    object HeaderOf(Row row, out string text, out string? help)
    {
        text = L.T(row.HeaderKey);
        help = row.HelpKey is { Length: > 0 } hk ? L.T(hk) : null;
        if (help == null) return text;
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new Run { Text = text });
        tb.Inlines.Add(new Run { Text = " " + InfoGlyph, FontFamily = Icons, FontSize = 12 });
        ToolTipService.SetToolTip(tb, help);
        AutomationProperties.SetName(tb, text);
        AutomationProperties.SetHelpText(tb, help);
        return tb;
    }

    /// <summary>A live line (<see cref="Row.Line"/>) as text that follows the module; collapsed while the text is empty.</summary>
    TextBlock LineText(IObservableValue<LocalisedText> line, string? style = null)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (style != null) tb.Style = Ui.Style(style);
        Ui.Tabular(tb);
        void Show(LocalisedText v)
        {
            var s = v.IsEmpty ? "" : L.Resolve(v);
            tb.Text = s;
            tb.Visibility = s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Show(line.Value);
        _scope.Bind(line, _e, Show);
        return tb;
    }

    /// <summary>The description area of a card: the description, the live line, the hardware note; a plain string (or null) when it is only the description.</summary>
    object? DescriptionOf(Row row, bool hardwareNote)
    {
        string? desc = row.DescriptionKey is { Length: > 0 } k ? L.T(k) : null;
        if (row.Line is null && !(hardwareNote && row.ChangesHardware)) return desc;
        var stack = new StackPanel { Spacing = 0 };
        if (desc != null) stack.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap });
        if (row.Line is { } line) stack.Children.Add(LineText(line));
        if (hardwareNote && row.ChangesHardware)
            stack.Children.Add(Ui.Text(Host.T(row.HardwareEffect?.Kind == EffectKind.SystemSetting ? "row.system-note" : "row.hardware-note"), "RbSecondaryCaption", wrap: true));
        return stack;
    }

    SettingsCard Card(Row row, UIElement content)
    {
        var card = new SettingsCard { Header = HeaderOf(row, out var text, out var help), Description = DescriptionOf(row, hardwareNote: true)!, Content = content };
        // the card's accessible name is its header text, also when the header carries a glyph (search and tests find a row by it)
        AutomationProperties.SetName(card, text);
        if (help != null) AutomationProperties.SetHelpText(card, help);
        card.IsEnabled = row.Enabled.Value;
        _scope.Bind(row.Enabled, _e, v => card.IsEnabled = v);
        return card;
    }

    /// <summary>A control on its own (an expander's header control): carries the row's Enabled.</summary>
    UIElement Bare(Row row, UIElement content)
    {
        var host = new ContentControl { Content = content, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right };
        host.IsEnabled = row.Enabled.Value;
        _scope.Bind(row.Enabled, _e, v => host.IsEnabled = v);
        return host;
    }

    /// <summary>
    /// The primary block: header (with its glyphs), description and live line at the left; the chrome (status, Revert, Apply, the control) at the
    /// right; <paramref name="below"/> (a segmented choice) under them across the width.
    /// </summary>
    UIElement Primary(Row row, UIElement chrome, UIElement? below)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerObj = HeaderOf(row, out var text, out var help);
        var header = headerObj as TextBlock ?? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        header.Style = Ui.Style("RbPrimaryHeader");
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        grid.Children.Add(header);

        var sub = new StackPanel { Spacing = 0 };
        if (row.DescriptionKey is { Length: > 0 } dk) sub.Children.Add(Ui.Text(L.T(dk), "RbSecondaryCaption", wrap: true));
        if (row.Line is { } line) sub.Children.Add(LineText(line, "RbSecondaryCaption"));
        if (row.ChangesHardware) sub.Children.Add(Ui.Text(Host.T(row.HardwareEffect?.Kind == EffectKind.SystemSetting ? "row.system-note" : "row.hardware-note"), "RbSecondaryCaption", wrap: true));
        Grid.SetRow(sub, 1);
        grid.Children.Add(sub);

        if (chrome is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.HorizontalAlignment = HorizontalAlignment.Right; }
        Grid.SetColumn((FrameworkElement)chrome, 1);
        Grid.SetRowSpan((FrameworkElement)chrome, 2);
        grid.Children.Add(chrome);

        if (below is FrameworkElement b)
        {
            b.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(b, 2);
            Grid.SetColumnSpan(b, 2);
            grid.Children.Add(b);
        }

        var host = new ContentControl { Content = grid, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(host, text);
        if (help != null) AutomationProperties.SetHelpText(host, help);
        void Enable(bool on)
        {
            host.IsEnabled = on;
            // text does not grey out by itself outside a control template
            header.Style = Ui.Style(on ? "RbPrimaryHeader" : "RbPrimaryHeaderDisabled");
            sub.Opacity = on ? 1 : 0.6;
        }
        Enable(row.Enabled.Value);
        _scope.Bind(row.Enabled, _e, Enable);
        PrimaryRow = row;
        return host;
    }

    /// <summary>The row drawn by <see cref="RenderPrimary"/> (self-test), or null.</summary>
    public Row? PrimaryRow { get; private set; }

    UIElement Finish(Row row, UIElement content, Place place, UIElement? below = null) => place switch
    {
        Place.Header => Bare(row, content),
        Place.Primary => Primary(row, content, below),
        _ => Card(row, content),
    };

    sealed class Chrome
    {
        public StackPanel Panel = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        public TextBlock Status = new() { Style = Ui.Style("RbSecondaryCaption"), MaxWidth = 260, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        public HyperlinkButton Revert = new() { Content = Host.T("row.revert"), Visibility = Visibility.Collapsed, Padding = new Thickness(4, 2, 4, 2) };
        public Button Discard = new() { Content = Host.T("row.discard"), Visibility = Visibility.Collapsed };
        public Button Apply = new() { Content = Host.T("row.apply"), Visibility = Visibility.Collapsed, Style = Ui.Style("AccentButtonStyle") };

        /// <param name="control">The row's control, last in the panel; null when it is drawn elsewhere (a primary choice sits under the header).</param>
        public Chrome(UIElement? control)
        {
            Panel.Children.Add(Status);
            Panel.Children.Add(Revert);
            Panel.Children.Add(Discard);
            Panel.Children.Add(Apply);
            if (control != null) Panel.Children.Add(control);
        }

        public void SetStatus(string text, bool error)
        {
            Status.Text = text;
            Status.Style = Ui.Style(error ? "RbErrorText" : "RbSecondaryCaption");
            Status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(Status, text.Length > 0 ? text : null);
        }
    }

    /// <summary>
    /// Binds one value row to its editor: control edits go in as drafts (immediate rows apply at once), the editor's changes
    /// come back to the control, and the outcome of an apply is shown next to the control with Revert.
    /// </summary>
    Action<T> Wire<T>(Row row, ValueEditor<T> editor, RowState state, Chrome chrome, Func<T> read, Func<T, string> fmt, Action<T> setControl)
    {
        var cmp = EqualityComparer<T>.Default;
        bool busy = false;

        void Sync()
        {
            if (!busy) setControl(editor.Draft);
            bool showButtons = editor.Mode == ApplyMode.ApplyRevert && editor.IsDirty;
            chrome.Apply.Visibility = chrome.Discard.Visibility = showButtons ? Visibility.Visible : Visibility.Collapsed;
            bool revertible = editor.CanRevert && (editor.Mode == ApplyMode.ApplyRevert || row.ChangesHardware) && (row.HardwareEffect?.Revertible ?? true);
            chrome.Revert.Visibility = revertible ? Visibility.Visible : Visibility.Collapsed;
            chrome.SetStatus(state.Status, state.StatusError);
        }

        async Task Run(Func<string?, Task<EditResult>> apply, T target)
        {
            if (busy) return;
            busy = true;
            try
            {
                // the exact change the approval dialog shows; only rows that change hardware ask
                string? detail = row.ChangesHardware ? $"{L.T(row.HeaderKey)}: {fmt(read())} → {fmt(target)}" : null;
                var result = await apply(detail);
                switch (result.Outcome)
                {
                    case EditOutcome.Applied:
                    {
                        var actual = read();                                   // read-back: what the module reports now
                        bool same = cmp.Equals(actual, target);
                        if (!same) editor.Discard();                           // the module adjusted it: show its value
                        bool track = editor.Mode == ApplyMode.ApplyRevert || row.ChangesHardware;
                        state.Status = track ? Host.F(same ? "row.applied" : "row.applied-adjusted", fmt(actual)) : "";
                        state.StatusError = false;
                        break;
                    }
                    case EditOutcome.Declined:
                        state.Status = Host.T("row.cancelled"); state.StatusError = false;
                        editor.Discard();
                        break;
                    case EditOutcome.Invalid:
                        state.Status = result.Message ?? ""; state.StatusError = true;
                        if (editor.Mode == ApplyMode.Immediate) editor.Discard();
                        break;
                    case EditOutcome.Faulted:
                        state.Status = result.Message ?? ""; state.StatusError = true;
                        break;
                    default: break;   // NothingToDo
                }
            }
            catch (Exception ex) { AppLog.For(_e.Id).Error("apply failed", ex); }
            finally { busy = false; Sync(); }
        }

        Task Edit(T value)
        {
            state.Status = ""; state.StatusError = false;
            if (editor.Mode == ApplyMode.Immediate) return Run(d => editor.SubmitAsync(value, d), value);
            editor.SetDraft(value);                       // raises Changed -> Sync
            return Task.CompletedTask;
        }

        Task Apply() => Run(d => editor.ApplyAsync(d), editor.Draft);

        // A revert of a hardware row is a write like any other: RevertAsync asks first (with the exact current -> previous value),
        // and nothing is written when the answer is no. The synchronous Revert() would refuse, so it is never called here.
        async Task Revert()
        {
            if (busy) return;
            busy = true;
            try
            {
                var r = await editor.RevertAsync(_e.StopToken);
                switch (r.Outcome)
                {
                    case EditOutcome.Applied:
                        state.Status = !string.IsNullOrEmpty(r.Message) ? r.Message : Host.F("row.reverted", fmt(read()));
                        state.StatusError = false;
                        break;
                    case EditOutcome.Declined:
                        state.Status = !string.IsNullOrEmpty(r.Message) ? r.Message : Host.T("row.cancelled");
                        state.StatusError = false;
                        break;
                    case EditOutcome.Faulted:
                    case EditOutcome.Invalid:
                        state.Status = r.Message ?? ""; state.StatusError = true;
                        break;
                    default: break;   // NothingToDo
                }
            }
            catch (Exception ex) { AppLog.For(_e.Id).Error("revert failed", ex); }
            finally { busy = false; Sync(); }
        }

        chrome.Apply.Click += async (_, _) => { try { await Apply(); } catch (Exception ex) { AppLog.For(_e.Id).Error("apply failed", ex); } };
        chrome.Discard.Click += (_, _) => { editor.Discard(); state.Status = ""; Sync(); };
        chrome.Revert.Click += async (_, _) => { try { await Revert(); } catch (Exception ex) { AppLog.For(_e.Id).Error("revert failed", ex); } };

        // Changes the module makes itself (a timer, a hotkey, a clamp) reach the control through the editor.
        void OnEditorChanged() => Ui.Post(_e, () => { if (!_scope.Disposed) Sync(); });
        editor.Changed += OnEditorChanged;
        _scope.Add(() => editor.Changed -= OnEditorChanged);
        Sync();

        Drivers.Add(new RowDriver
        {
            Row = row, Name = L.T(row.HeaderKey),
            Edit = o => Edit((T)o!), Apply = Apply, Revert = Revert,
            Status = () => state.Status, CanRevert = () => editor.CanRevert,
        });
        return v => { _ = Edit(v); };
    }

    static RowState StateOf(Row row) => States.GetValue(row, _ => new RowState());

    // ---------- the row kinds ----------

    UIElement Toggle(ToggleRow t, Place place)
    {
        bool suppress = false;
        var sw = new ToggleSwitch { IsOn = t.Value.Value, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(sw, L.T(t.HeaderKey));
        UIElement control = sw;
        TextBlock? word = null;
        if (place == Place.Primary)
        {
            // the primary switch says its state in words, at its left (Windows Settings' Bluetooth card)
            word = Ui.Text("", "RbSecondaryText");
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            pair.Children.Add(word);
            pair.Children.Add(sw);
            control = pair;
        }
        void Word() { if (word != null) word.Text = Host.T(sw.IsOn ? "value.on" : "value.off"); }
        Word();
        var chrome = new Chrome(control);
        var editor = RowEditors.For(_e, t); var state = StateOf(t);
        var edit = Wire(t, editor, state, chrome, () => t.Value.Value, v => Host.T(v ? "value.on" : "value.off"),
            v => { if (sw.IsOn != v) { suppress = true; sw.IsOn = v; suppress = false; } Word(); });
        sw.Toggled += (_, _) => { Word(); if (!suppress) edit(sw.IsOn); };
        return Finish(t, chrome.Panel, place);
    }

    UIElement Choice(ChoiceRow c, Place place)
    {
        bool suppress = false;
        string header = L.T(c.HeaderKey);
        var options = c.Options.Select(o => (o.Id, Label: L.T(o.LabelKey))).ToList();
        var selector = new ChoiceSelector(options, header, primary: place == Place.Primary);
        Choices.Add(selector);
        void Select(string id) { suppress = true; selector.Selected = id; suppress = false; }
        Select(c.Value.Value);
        // a primary choice sits under its header across the card; elsewhere it is the control at the right of the row
        var chrome = new Chrome(place == Place.Primary ? null : selector);
        string Label(string id) => c.Options.FirstOrDefault(o => o.Id == id) is { } o ? L.T(o.LabelKey) : id;
        var editor = RowEditors.For(_e, c); var state = StateOf(c);
        var edit = Wire(c, editor, state, chrome, () => c.Value.Value, Label, Select);
        selector.Chosen += id => { if (!suppress) edit(id); };
        return Finish(c, chrome.Panel, place, place == Place.Primary ? selector : null);
    }

    UIElement Number(NumberRow n, Place place)
    {
        bool suppress = false;
        var nb = new NumberBox
        {
            Minimum = n.Minimum, Maximum = n.Maximum, SmallChange = n.Step, LargeChange = n.Step * 5, Value = n.Value.Value,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 140,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };
        AutomationProperties.SetName(nb, L.T(n.HeaderKey));
        var unit = n.Unit.Symbol();
        UIElement control = nb;
        if (unit.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(nb);
            row.Children.Add(Ui.Text(unit, "RbSecondaryText"));
            control = row;
        }
        var chrome = new Chrome(control);
        var editor = RowEditors.For(_e, n); var state = StateOf(n);
        var edit = Wire(n, editor, state, chrome, () => n.Value.Value,
            v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + (unit.Length > 0 ? " " + unit : ""),
            v => { if (nb.Value != v) { suppress = true; nb.Value = v; suppress = false; } });
        nb.ValueChanged += (_, e) => { if (!suppress && !double.IsNaN(e.NewValue)) edit(e.NewValue); };
        return Finish(n, chrome.Panel, place);
    }

    UIElement TextEntry(TextRow x, Place place)
    {
        bool suppress = false;
        // never the width of the text (a long path used to widen the box and squeeze the label), but not a fixed 280 either (E2E-06): half of the row, at least 280, at most 560
        var tb = new TextBox { Width = TextBoxMinWidth, TextWrapping = TextWrapping.NoWrap, Text = x.Value.Value, MaxLength = x.MaxLength, PlaceholderText = x.PlaceholderKey != null ? L.T(x.PlaceholderKey) : "" };
        AutomationProperties.SetName(tb, L.T(x.HeaderKey));
        void Tip() => ToolTipService.SetToolTip(tb, tb.Text.Length > 0 ? tb.Text : null);   // the whole text, for what the box still cannot show
        Tip();
        var chrome = new Chrome(tb);
        var editor = RowEditors.For(_e, x); var state = StateOf(x);
        var edit = Wire(x, editor, state, chrome, () => x.Value.Value, v => v, v => { if (tb.Text != v) { suppress = true; tb.Text = v; suppress = false; } });
        tb.TextChanged += (_, _) => { Tip(); if (!suppress) edit(tb.Text); };
        var built = Finish(x, chrome.Panel, place);
        if (built is FrameworkElement card)
            card.SizeChanged += (_, e) => { var w = TextBoxWidthFor(e.NewSize.Width); if (Math.Abs(tb.Width - w) >= 1) tb.Width = w; };
        return built;
    }

    public const double TextBoxMinWidth = 280, TextBoxMaxWidth = 560;

    /// <summary>The width of a text box in a row that is <paramref name="rowWidth"/> wide: half of the row, between <see cref="TextBoxMinWidth"/> and <see cref="TextBoxMaxWidth"/>.</summary>
    public static double TextBoxWidthFor(double rowWidth) =>
        double.IsNaN(rowWidth) || rowWidth <= 0 ? TextBoxMinWidth : Math.Clamp(Math.Floor(rowWidth * 0.5), TextBoxMinWidth, TextBoxMaxWidth);

    UIElement Shortcut(ShortcutRow s, Place place)
    {
        string Show(HotkeyGesture? g) => g?.ToString() ?? Host.T("row.shortcut-none");
        var btn = new Button { Content = Show(s.Value.Value), MinWidth = 140 };
        AutomationProperties.SetName(btn, L.T(s.HeaderKey));
        var chrome = new Chrome(btn);
        var editor = RowEditors.For(_e, s); var state = StateOf(s);
        var edit = Wire(s, editor, state, chrome, () => s.Value.Value, Show, g => btn.Content = Show(g));
        btn.Click += async (_, _) =>
        {
            try
            {
                var (ok, gesture) = await CaptureDialog.AskAsync(L.T(s.HeaderKey));
                if (ok) edit(gesture);
            }
            catch (Exception ex) { AppLog.For(_e.Id).Error("shortcut capture failed", ex); }
        };
        return Finish(s, chrome.Panel, place);
    }

    UIElement Button(ButtonRow b, Place place)
    {
        var btn = new Button { Content = L.T(b.ButtonKey) };
        AutomationProperties.SetName(btn, L.T(b.ButtonKey));
        if (place == Place.Primary)
        {
            // the module's main action: the accent button, a size up (Windows Update's "Check for updates")
            btn.Style = Ui.Style("AccentButtonStyle");
            btn.MinWidth = 132;
            btn.MinHeight = 36;
            btn.Padding = new Thickness(20, 6, 20, 7);
        }
        var chrome = new Chrome(btn);
        if (b.RevertButtonKey is { Length: > 0 } rk) chrome.Revert.Content = L.T(rk);   // the module's own word for the revert action
        var state = StateOf(b);
        bool busy = false;
        string? holder = _holder ?? L.T(b.HeaderKey);   // what a ConfirmKey question names: the expander the button sits in, else the row
        string Message(ActionResult? r) => r?.Message is { } m && !string.IsNullOrEmpty(m.Key)
            ? (m.Key.StartsWith("core.", StringComparison.Ordinal) ? Host.Core.Languages.F(m.Key, m.Args ?? Array.Empty<object>()) : L.Resolve(m))
            : "";

        void Show()
        {
            chrome.SetStatus(state.Status, state.StatusError);
            chrome.Revert.Visibility = state.ButtonCanRevert ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task Click()
        {
            if (busy) return;
            FocusKey.RememberForRefresh(btn.XamlRoot, Host.Window.PageContent!);   // the button is disabled while it works; a page refresh then gives focus back to its replacement
            busy = true; btn.IsEnabled = false;
            try
            {
                if (!await ConfirmAsync(b, holder))
                {
                    // the answer was no: nothing ran
                    state.Status = Host.T("row.cancelled"); state.StatusError = false;
                    return;
                }
                string? detail = b.ChangesHardware ? $"{L.T(b.HeaderKey)}: {L.T(b.ButtonKey)}" : null;
                var result = await RowEditors.InvokeButtonAsync(_e, b, _e.StopToken, detail);
                var msg = Message(result);
                state.Status = msg.Length > 0 ? msg : Host.T(result.Succeeded ? "row.done" : "row.failed");
                state.StatusError = !result.Succeeded && !(msg == Host.Core.Languages.T("core.approval.declined"));
                state.ButtonCanRevert = result.Succeeded && b.RevertAsync != null && (b.HardwareEffect?.Revertible ?? true);
            }
            catch (Exception ex) { AppLog.For(_e.Id).Error("button flow failed", ex); }
            finally { busy = false; btn.IsEnabled = true; Ui.Post(null, Show); }
        }

        async Task Revert()
        {
            // asks first unless the row declares SafeRevert; a "no" writes nothing, so the Revert link stays
            var result = await RowEditors.RevertButtonAsync(_e, b, _e.StopToken);
            var msg = Message(result);
            bool declined = !result.Succeeded && result.Message?.Key == "core.approval.declined";
            if (!declined) state.ButtonCanRevert = false;
            state.Status = msg.Length > 0 ? msg : Host.T(result.Succeeded ? "row.reverted-plain" : "row.failed");
            state.StatusError = !result.Succeeded && !declined;
            Ui.Post(null, Show);
        }

        btn.Click += async (_, _) => { try { await Click(); } catch (Exception ex) { AppLog.For(_e.Id).Error("button failed", ex); } };
        chrome.Revert.Click += async (_, _) => { try { await Revert(); } catch (Exception ex) { AppLog.For(_e.Id).Error("revert failed", ex); } };
        Show();

        Drivers.Add(new RowDriver
        {
            Row = b, Name = L.T(b.HeaderKey),
            Edit = _ => Click(), Apply = Click, Revert = Revert,
            Status = () => state.Status, CanRevert = () => state.ButtonCanRevert,
        });
        return Finish(b, chrome.Panel, place);
    }

    /// <summary>
    /// The question of a <see cref="ButtonRow.ConfirmKey"/>: a dialog titled with the question (its <c>{0}</c> is <paramref name="holder"/>),
    /// the row's description as its text, the button's own label on the action and Cancel. True to go on. A row with a hardware effect is not
    /// asked here: its approval asks. The self-test and screenshots answer through <see cref="ApprovalPrompt.TestAnswer"/>.
    /// </summary>
    async Task<bool> ConfirmAsync(ButtonRow b, string holder)
    {
        if (b.ConfirmKey is not { Length: > 0 } key || b.ChangesHardware) return true;
        var question = L.Resolve(new LocalisedText(key, new object[] { holder }));
        LastConfirmQuestion = question;
        AppLog.For(_e.Id).Info("confirmation asked: " + question);
        if (ApprovalPrompt.TestAnswer is { } canned) return canned;
        var root = Host.Window?.Content?.XamlRoot;
        if (root == null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = question,
            Content = b.DescriptionKey is { Length: > 0 } dk ? Ui.Text(L.T(dk), "RbSecondaryText", wrap: true) : null,
            PrimaryButtonText = L.T(b.ButtonKey),
            CloseButtonText = Host.T("row.confirm-cancel"),
            // Enter does not delete: no default button; the action wears the accent because it is what the dialog is about
            DefaultButton = ContentDialogButton.None,
            PrimaryButtonStyle = Ui.Style("AccentButtonStyle"),
            RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
        };
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        catch (Exception ex) { AppLog.For(_e.Id).Error("confirmation dialog failed", ex); return false; }
    }

    UIElement ValueText(ValueRow v, Place place)
    {
        var tb = new TextBlock { Text = v.Value.Value, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, IsTextSelectionEnabled = true, MaxWidth = 460 };   // read-only status text wraps, it is never cut (E2E-06)
        Ui.Tabular(tb);
        _scope.Bind(v.Value, _e, s => tb.Text = s);
        return Finish(v, tb, place);
    }

    UIElement Reading(ReadingRow r, Place place)
    {
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 72, TextAlignment = TextAlignment.Right };
        Ui.Tabular(tb);
        string styleKey = "";
        // One place decides the text: a number with its unit, or the host's "Not available" (never published, or the module withdrew it).
        void Show(MetricSample? s)
        {
            tb.Text = ReadingFormat.Format(Host.Core.Languages, r, s);
            var key = ReadingFormat.IsAvailable(s) ? ReadingStyle(r.Color, s!.Unit) : "RbSecondaryText";
            if (key != styleKey) { styleKey = key; tb.Style = Ui.Style(key); }
        }
        MetricSample? Latest() => Host.Core.Metrics.TryGetLatest(r.Metric, out var latest) ? latest : null;
        Show(Latest());
        // the number follows the bus while the page is on screen; the sparkline left of it is drawn from the host's history once a second
        _scope.Live(() => Host.Core.Metrics.Subscribe(s => Ui.Post(_e, () => { if (!_scope.Disposed && _scope.Active) Show(s); }), r.Metric), () => Show(Latest()));
        var spark = new Sparkline(r.Metric, r.Color, 84, 22);
        _scope.Tick(spark.Redraw);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Children = { spark, tb } };
        Sparklines.Add(spark);
        return Finish(r, content, place);
    }

    /// <summary>The sparklines of the reading rows rendered so far (the self-test counts them).</summary>
    public List<Sparkline> Sparklines { get; } = new();

    /// <summary>Style key of a reading: one hue family per kind (temperature warm, power blue, speed teal, load amber), neutral otherwise.</summary>
    public static string ReadingStyle(SemanticColor color, MetricUnit unit)
    {
        if (color == SemanticColor.Auto)
            color = unit switch
            {
                MetricUnit.Celsius => SemanticColor.Temperature,
                MetricUnit.Watt or MetricUnit.Volt or MetricUnit.Ampere => SemanticColor.Power,
                MetricUnit.Rpm or MetricUnit.Megahertz => SemanticColor.Speed,
                MetricUnit.Percent => SemanticColor.Load,
                _ => SemanticColor.Neutral,
            };
        return color switch
        {
            SemanticColor.Temperature => "RbReadingTemperature",
            SemanticColor.Power => "RbReadingPower",
            SemanticColor.Speed => "RbReadingSpeed",
            SemanticColor.Load => "RbReadingLoad",
            SemanticColor.Ok => "RbOkText",
            SemanticColor.Error => "RbErrorText",
            _ => "RbPrimaryText",
        };
    }

    UIElement Expander(ExpanderRow ex)
    {
        var x = new SettingsExpander
        {
            Header = HeaderOf(ex, out var text, out var help),
            Description = DescriptionOf(ex, hardwareNote: false) ?? "",
            IsExpanded = ex.IsExpanded,
        };
        AutomationProperties.SetName(x, text);
        if (help != null) AutomationProperties.SetHelpText(x, help);
        // a control in the expander's own header (a tunnel's switch): visible and usable while the expander is closed
        if (ex.HeaderRow is { } head) x.Content = Render(head, Place.Header);
        // The expander gives every item the SettingsCard style, so only a SettingsCard can sit in it directly (an InfoBar throws when it is expanded);
        // anything else is carried inside a card that lets it use the full width.
        var outer = _holder;
        _holder = text;   // the object a ConfirmKey question inside names
        try
        {
            foreach (var child in ex.Children)
            {
                var el = Render(child);
                x.Items.Add(el is SettingsCard ? el : new SettingsCard { Content = el, ContentAlignment = ContentAlignment.Vertical, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
        }
        finally { _holder = outer; }
        x.IsEnabled = ex.Enabled.Value;
        _scope.Bind(ex.Enabled, _e, v => x.IsEnabled = v);
        return x;
    }

    UIElement Info(InfoRow i)
    {
        var bar = new InfoBar
        {
            IsOpen = i.IsOpen.Value, IsClosable = false, Title = L.T(i.HeaderKey), Message = i.DescriptionKey is { Length: > 0 } k ? L.T(k) : "",
            Severity = i.Severity switch { Severity.Success => InfoBarSeverity.Success, Severity.Warning => InfoBarSeverity.Warning, Severity.Error => InfoBarSeverity.Error, _ => InfoBarSeverity.Informational },
        };
        _scope.Bind(i.IsOpen, _e, v => bar.IsOpen = v);
        return bar;
    }

    UIElement Custom(CustomRow cr)
    {
        if (_e.Lifecycle != ModuleLifecycle.Running)
            // a turned-off module is not called: its custom control is not drawn, the row keeps its place greyed out
            return new SettingsCard { Header = cr.HeaderKey.Length > 0 ? L.T(cr.HeaderKey) : cr.ControlId, Description = cr.DescriptionKey is { Length: > 0 } dk ? L.T(dk) : "", IsEnabled = false };
        var created = _e.CreateCustomControl(cr.ControlId);   // inside the boundary; null when the module returned nothing or faulted
        string? error = null;
        if (created == null) error = _e.Lifecycle == ModuleLifecycle.Faulted ? Host.F("row.custom-threw", _e.ReasonText()) : Host.F("row.custom-null", cr.ControlId);
        else if (created is not UIElement) error = Host.F("row.custom-wrong-type", created.GetType().FullName ?? "?");
        if (error != null || created is not UIElement ui)
            return new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = cr.HeaderKey.Length > 0 ? L.T(cr.HeaderKey) : cr.ControlId, Message = error ?? "" };
        return new SettingsCard
        {
            Header = cr.HeaderKey.Length > 0 ? L.T(cr.HeaderKey) : "",
            Description = cr.DescriptionKey is { Length: > 0 } k ? L.T(k) : "",
            Content = ui, ContentAlignment = ContentAlignment.Vertical, HorizontalContentAlignment = HorizontalAlignment.Left,
        };
    }
}

/// <summary>
/// One option from a short list. Drawn as a segmented control when the options are few and short (2 to 4 of at most 16 characters in a row,
/// 2 to 5 of at most 20 as a page's primary control): the stock <see cref="RadioButtons"/> with each option drawn as a segment, so it is one tab
/// stop, the arrow keys move the choice and a screen reader hears "Maximum, radio button, 2 of 2". A drop-down otherwise, and also when the
/// place it gets is narrower than the segments (a narrow window). Nothing is selected while the module's value is not one of the options.
/// </summary>
sealed class ChoiceSelector : Grid
{
    /// <summary>Most options drawn as segments in a row, and as a page's primary control.</summary>
    public const int MaxRowSegments = 4, MaxPrimarySegments = 5;
    /// <summary>Longest option label drawn as a segment, in a row and as a primary control.</summary>
    public const int MaxRowLabel = 16, MaxPrimaryLabel = 20;
    /// <summary>Widest a segmented control in a row may be before it is a drop-down (it sits at the right of the row's text).</summary>
    public const double MaxRowWidth = 380;

    const double Gap = 2, TrackPad = 3;

    readonly List<(string Id, string Label)> _options;
    readonly RadioButtons? _segments;
    readonly Border? _track;
    readonly ComboBox _combo;
    readonly double _needed;
    bool _useSegments;
    bool _suppress;
    string? _selected;

    /// <summary>The user picked an option (not raised when <see cref="Selected"/> is set from code).</summary>
    public event Action<string>? Chosen;

    /// <summary>True while the segments are on screen (false: the drop-down).</summary>
    public bool ShowsSegments => _useSegments;
    /// <summary>True when the options can be segments at all (count and length).</summary>
    public bool CanSegment => _segments != null;
    /// <summary>The width the segments need.</summary>
    public double SegmentsWidth => _needed;
    public RadioButtons? Segments => _segments;
    public ComboBox Combo => _combo;

    /// <summary>True when <paramref name="labels"/> can be drawn as segments (count and length of the labels).</summary>
    public static bool Suits(IReadOnlyList<string> labels, bool primary) =>
        labels.Count >= 2 && labels.Count <= (primary ? MaxPrimarySegments : MaxRowSegments) && labels.All(l => l.Length <= (primary ? MaxPrimaryLabel : MaxRowLabel));

    public ChoiceSelector(IReadOnlyList<(string Id, string Label)> options, string name, bool primary)
    {
        _options = options.ToList();
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = primary ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        _combo = new ComboBox { MinWidth = primary ? 220 : 240, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (id, label) in _options) _combo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        AutomationProperties.SetName(_combo, name);
        _combo.SelectionChanged += (_, _) =>
        {
            if (!_suppress && _combo.SelectedItem is ComboBoxItem it) Pick((string)it.Tag);
        };
        Children.Add(_combo);

        var labels = _options.Select(o => o.Label).ToList();
        if (Suits(labels, primary))
        {
            // every segment as wide as the widest label set in its selected weight, so the choice does not shift the row
            double widest = 0;
            foreach (var l in labels)
            {
                var probe = new TextBlock { Text = l, FontSize = 14, FontWeight = FontWeights.SemiBold };
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                widest = Math.Max(widest, probe.DesiredSize.Width);
            }
            double seg = Math.Ceiling(Math.Max(widest + 32, primary ? 112 : 72));
            _needed = labels.Count * seg + (labels.Count - 1) * Gap + 2 * TrackPad + 2;
            if (primary || _needed <= MaxRowWidth)
            {
                _segments = new RadioButtons { MaxColumns = labels.Count, HorizontalAlignment = HorizontalAlignment.Left };
                _segments.Resources["RadioButtonsColumnSpacing"] = Gap;
                _segments.Resources["RadioButtonsTopHeaderMargin"] = new Thickness(0);
                foreach (var (id, label) in _options)
                {
                    var item = new RadioButton { Content = label, Tag = id, Width = seg, Style = Ui.Style(primary ? "RbSegmentLarge" : "RbSegment") };
                    AutomationProperties.SetName(item, label);
                    _segments.Items.Add(item);
                }
                AutomationProperties.SetName(_segments, name);
                _segments.SelectionChanged += (_, _) =>
                {
                    if (!_suppress && _segments.SelectedIndex >= 0 && _segments.SelectedIndex < _options.Count) Pick(_options[_segments.SelectedIndex].Id);
                };
                _track = new Border { Style = Ui.Style("RbSegmentTrack"), Padding = new Thickness(TrackPad), Child = _segments, HorizontalAlignment = HorizontalAlignment.Left };
                Children.Add(_track);
                _useSegments = true;
                _combo.Visibility = Visibility.Collapsed;
            }
        }
    }

    void Pick(string id)
    {
        _selected = id;
        Chosen?.Invoke(id);
    }

    /// <summary>The selected option's id; an id that is not an option (or null) selects nothing.</summary>
    public string? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            int index = _options.FindIndex(o => o.Id == value);
            _suppress = true;
            try
            {
                _combo.SelectedIndex = index;
                if (_segments != null) _segments.SelectedIndex = index;
            }
            finally { _suppress = false; }
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        // the segments when they fit the place this control gets, the drop-down when they do not (a narrow window)
        if (_track != null)
        {
            bool fits = double.IsInfinity(available.Width) || available.Width >= _needed;
            if (fits != _useSegments)
            {
                _useSegments = fits;
                _track.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
                _combo.Visibility = fits ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        return base.MeasureOverride(available);
    }
}
