using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// Turns the rows of a module's <see cref="PageModel"/> into <c>SettingsCard</c> rows. Values go through the core's
/// <see cref="ValueEditor{T}"/> (draft, validation, confirmation for hardware effects, previous value for Revert); every call
/// into the module goes through the module's fault boundary, and every change that comes from a module thread reaches the UI
/// thread through <see cref="Ui.Post"/>. A row that changes hardware: shows what will be written, asks, applies, shows the
/// value the module reports afterwards, and offers Revert.
/// </summary>
sealed class RowRenderer
{
    /// <summary>Per row instance: the last status line shown next to it, so it survives a page rebuild. Editors are cached by the core (<c>RowEditors.For</c>).</summary>
    sealed class RowState { public string Status = ""; public bool StatusError; public bool ButtonCanRevert; }

    static readonly ConditionalWeakTable<Row, RowState> States = new();

    readonly ModuleEntry _e;
    readonly PageScope _scope;
    ILocalisation L => _e.Localisation;

    public int RowsRendered { get; private set; }
    /// <summary>Test hooks: one per value row, to drive the same code the controls drive.</summary>
    public List<RowDriver> Drivers { get; } = new();

    public RowRenderer(ModuleEntry entry, PageScope scope) { _e = entry; _scope = scope; }

    /// <summary>What the self-test uses to act like the user without a mouse.</summary>
    public sealed class RowDriver
    {
        public required Row Row { get; init; }
        public required string Name { get; init; }
        public required Func<object?, Task> Edit { get; init; }
        public required Func<Task> Apply { get; init; }
        public required Action Revert { get; init; }
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

    public UIElement Render(Row row)
    {
        RowsRendered++;
        try
        {
            return row switch
            {
                ToggleRow t => Toggle(t),
                ChoiceRow c => Choice(c),
                NumberRow n => Number(n),
                TextRow x => TextEntry(x),
                ShortcutRow s => Shortcut(s),
                ButtonRow b => Button(b),
                ValueRow v => ValueText(v),
                ReadingRow r => Reading(r),
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

    SettingsCard Card(Row row, UIElement content)
    {
        object? description;
        string? desc = row.DescriptionKey is { Length: > 0 } k ? L.T(k) : null;
        if (row.ChangesHardware)
        {
            var stack = new StackPanel { Spacing = 0 };
            if (desc != null) stack.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(Ui.Text(Host.T("row.hardware-note"), "RbSecondaryCaption", wrap: true));
            description = stack;
        }
        else description = desc;
        var card = new SettingsCard { Header = L.T(row.HeaderKey), Description = description!, Content = content };
        card.IsEnabled = row.Enabled.Value;
        _scope.Bind(row.Enabled, _e, v => card.IsEnabled = v);
        return card;
    }

    sealed class Chrome
    {
        public StackPanel Panel = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        public TextBlock Status = new() { Style = Ui.Style("RbSecondaryCaption"), MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        public HyperlinkButton Revert = new() { Content = Host.T("row.revert"), Visibility = Visibility.Collapsed, Padding = new Thickness(4, 2, 4, 2) };
        public Button Discard = new() { Content = Host.T("row.discard"), Visibility = Visibility.Collapsed };
        public Button Apply = new() { Content = Host.T("row.apply"), Visibility = Visibility.Collapsed, Style = Ui.Style("AccentButtonStyle") };

        public Chrome(UIElement control)
        {
            Panel.Children.Add(Status);
            Panel.Children.Add(Revert);
            Panel.Children.Add(Discard);
            Panel.Children.Add(Apply);
            Panel.Children.Add(control);
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

        void Revert()
        {
            var r = editor.Revert();
            state.Status = r.Outcome == EditOutcome.Applied ? Host.F("row.reverted", fmt(read())) : r.Message ?? "";
            state.StatusError = r.Outcome == EditOutcome.Faulted;
            Sync();
        }

        chrome.Apply.Click += async (_, _) => { try { await Apply(); } catch (Exception ex) { AppLog.For(_e.Id).Error("apply failed", ex); } };
        chrome.Discard.Click += (_, _) => { editor.Discard(); state.Status = ""; Sync(); };
        chrome.Revert.Click += (_, _) => Revert();

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

    UIElement Toggle(ToggleRow t)
    {
        bool suppress = false;
        var sw = new ToggleSwitch { IsOn = t.Value.Value, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(sw, L.T(t.HeaderKey));
        var chrome = new Chrome(sw);
        var editor = RowEditors.For(_e, t); var state = StateOf(t);
        var edit = Wire(t, editor, state, chrome, () => t.Value.Value, v => Host.T(v ? "value.on" : "value.off"),
            v => { if (sw.IsOn != v) { suppress = true; sw.IsOn = v; suppress = false; } });
        sw.Toggled += (_, _) => { if (!suppress) edit(sw.IsOn); };
        return Card(t, chrome.Panel);
    }

    UIElement Choice(ChoiceRow c)
    {
        bool suppress = false;
        var cb = new ComboBox { MinWidth = 240 };
        foreach (var o in c.Options) cb.Items.Add(new ComboBoxItem { Content = L.T(o.LabelKey), Tag = o.Id });
        void Select(string id)
        {
            suppress = true;
            cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == id);
            suppress = false;
        }
        Select(c.Value.Value);
        AutomationProperties.SetName(cb, L.T(c.HeaderKey));
        var chrome = new Chrome(cb);
        string Label(string id) => c.Options.FirstOrDefault(o => o.Id == id) is { } o ? L.T(o.LabelKey) : id;
        var editor = RowEditors.For(_e, c); var state = StateOf(c);
        var edit = Wire(c, editor, state, chrome, () => c.Value.Value, Label, Select);
        cb.SelectionChanged += (_, _) => { if (!suppress && cb.SelectedItem is ComboBoxItem it) edit((string)it.Tag); };
        return Card(c, chrome.Panel);
    }

    UIElement Number(NumberRow n)
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
        return Card(n, chrome.Panel);
    }

    UIElement TextEntry(TextRow x)
    {
        bool suppress = false;
        var tb = new TextBox { MinWidth = 240, Text = x.Value.Value, MaxLength = x.MaxLength, PlaceholderText = x.PlaceholderKey != null ? L.T(x.PlaceholderKey) : "" };
        AutomationProperties.SetName(tb, L.T(x.HeaderKey));
        var chrome = new Chrome(tb);
        var editor = RowEditors.For(_e, x); var state = StateOf(x);
        var edit = Wire(x, editor, state, chrome, () => x.Value.Value, v => v, v => { if (tb.Text != v) { suppress = true; tb.Text = v; suppress = false; } });
        tb.TextChanged += (_, _) => { if (!suppress) edit(tb.Text); };
        return Card(x, chrome.Panel);
    }

    UIElement Shortcut(ShortcutRow s)
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
        return Card(s, chrome.Panel);
    }

    UIElement Button(ButtonRow b)
    {
        var btn = new Button { Content = L.T(b.ButtonKey) };
        var chrome = new Chrome(btn);
        var state = StateOf(b);
        bool busy = false;
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
            var result = await RowEditors.RevertButtonAsync(_e, b, _e.StopToken);
            var msg = Message(result);
            state.ButtonCanRevert = false;
            state.Status = msg.Length > 0 ? msg : Host.T(result.Succeeded ? "row.reverted-plain" : "row.failed");
            state.StatusError = !result.Succeeded;
            Ui.Post(null, Show);
        }

        btn.Click += async (_, _) => { try { await Click(); } catch (Exception ex) { AppLog.For(_e.Id).Error("button failed", ex); } };
        chrome.Revert.Click += async (_, _) => { try { await Revert(); } catch (Exception ex) { AppLog.For(_e.Id).Error("revert failed", ex); } };
        Show();

        Drivers.Add(new RowDriver
        {
            Row = b, Name = L.T(b.HeaderKey),
            Edit = _ => Click(), Apply = Click, Revert = () => { _ = Revert(); },
            Status = () => state.Status, CanRevert = () => state.ButtonCanRevert,
        });
        return Card(b, chrome.Panel);
    }

    UIElement ValueText(ValueRow v)
    {
        var tb = new TextBlock { Text = v.Value.Value, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 };
        Ui.Tabular(tb);
        _scope.Bind(v.Value, _e, s => tb.Text = s);
        return Card(v, tb);
    }

    UIElement Reading(ReadingRow r)
    {
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = "—" };
        Ui.Tabular(tb);
        string styleKey = "RbPrimaryText";
        void Show(MetricSample s)
        {
            var sym = s.Unit.Symbol();
            tb.Text = s.Value.ToString("F" + Math.Max(0, r.Decimals), System.Globalization.CultureInfo.InvariantCulture) + (sym.Length > 0 ? " " + sym : "");
            var key = ReadingStyle(r.Color, s.Unit);
            if (key != styleKey) { styleKey = key; tb.Style = Ui.Style(key); }
        }
        if (Host.Core.Metrics.TryGetLatest(r.Metric, out var latest)) Show(latest);
        _scope.Add(Host.Core.Metrics.Subscribe(s => Ui.Post(_e, () => { if (!_scope.Disposed) Show(s); }), r.Metric));
        return Card(r, tb);
    }

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
            Header = L.T(ex.HeaderKey),
            Description = ex.DescriptionKey is { Length: > 0 } k ? L.T(k) : "",
            IsExpanded = ex.IsExpanded,
        };
        // The expander gives every item the SettingsCard style, so only a SettingsCard can sit in it directly (an InfoBar throws when it is expanded);
        // anything else is carried inside a card that lets it use the full width.
        foreach (var child in ex.Children)
        {
            var el = Render(child);
            x.Items.Add(el is SettingsCard ? el : new SettingsCard { Content = el, ContentAlignment = ContentAlignment.Vertical, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
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
