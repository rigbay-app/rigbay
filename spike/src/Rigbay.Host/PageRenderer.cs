using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Rigbay.Abstractions;

namespace Rigbay.Host;

/// <summary>Turns a declarative PageModel into SettingsCard rows. Modules never see a WinUI type for level 1.</summary>
sealed class PageRenderer
{
    readonly DispatcherQueue _dq;
    readonly IPageModelProvider _provider;
    readonly IHostServices _host;
    readonly string _moduleId;
    public int RowsRendered;

    public PageRenderer(DispatcherQueue dq, IPageModelProvider provider, IHostServices host, string moduleId)
    { _dq = dq; _provider = provider; _host = host; _moduleId = moduleId; }

    public UIElement Build(PageModel m)
    {
        var page = new StackPanel { Spacing = 20 };
        var groups = new StackPanel { Spacing = 20 };
        var groupsHost = new ContentControl { Content = groups, HorizontalContentAlignment = HorizontalAlignment.Stretch };

        if (m.Enable != null)
        {
            page.Children.Add(Render(m.Enable));
            void Sync() => groupsHost.IsEnabled = m.Enable.Value.Value;
            Sync();
            m.Enable.Value.Changed += () => OnUi(Sync);
        }
        foreach (var g in m.Groups)
        {
            var box = new StackPanel { Spacing = 2 };
            box.Children.Add(new TextBlock { Text = g.Header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var r in g.Rows) box.Children.Add(Render(r));
            groups.Children.Add(box);
        }
        page.Children.Add(groupsHost);
        return page;
    }

    // Changes may arrive on any thread; hop to the UI thread inside a try/catch (a raw throw in a dispatcher callback kills the process).
    public void OnUi(Action a) => _dq.TryEnqueue(() => { try { a(); } catch (Exception ex) { Log.W($"module {_moduleId}: UI update threw {ex.GetType().Name}: {ex.Message}"); } });

    UIElement Render(Row row)
    {
        RowsRendered++;
        switch (row)
        {
            case ToggleRow t:
            {
                var sw = new ToggleSwitch { IsOn = t.Value.Value, OnContent = "", OffContent = "", MinWidth = 0 };
                bool guard = false;
                sw.Toggled += (_, _) => { if (!guard) t.Value.Value = sw.IsOn; };
                t.Value.Changed += () => OnUi(() => { guard = true; sw.IsOn = t.Value.Value; guard = false; });
                return new SettingsCard { Header = t.Header, Description = t.Description, Content = sw };
            }
            case ChoiceRow c:
            {
                var cb = new ComboBox { MinWidth = 240, ItemsSource = c.Options.ToList(), SelectedItem = c.Value.Value };
                cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s) c.Value.Value = s; };
                c.Value.Changed += () => OnUi(() => cb.SelectedItem = c.Value.Value);
                return new SettingsCard { Header = c.Header, Description = c.Description, Content = cb };
            }
            case NumberRow n:
            {
                var nb = new NumberBox { Minimum = n.Min, Maximum = n.Max, SmallChange = n.Step, Value = n.Value.Value, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 140 };
                nb.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) n.Value.Value = e.NewValue; };
                return new SettingsCard { Header = n.Header, Description = n.Description, Content = nb };
            }
            case TextRow x:
            {
                var tb = new TextBox { MinWidth = 240, Text = x.Value.Value };
                tb.TextChanged += (_, _) => x.Value.Value = tb.Text;
                return new SettingsCard { Header = x.Header, Description = x.Description, Content = tb };
            }
            case ButtonRow b:
            {
                var btn = new Button { Content = b.ButtonText };
                btn.Click += (_, _) => { try { b.Click(); } catch (Exception ex) { Log.W($"module {_moduleId}: button threw {ex.GetType().Name}: {ex.Message}"); } };
                return new SettingsCard { Header = b.Header, Description = b.Description, Content = btn };
            }
            case ValueRow v:
                return new SettingsCard { Header = v.Header, Description = v.Description, Content = ValueText(v.Value, "") };
            case ReadingRow r:
                return new SettingsCard { Header = r.Header, Description = r.Description, Content = ValueText(r.Value, r.Unit) };
            case ExpanderRow e:
            {
                var ex = new SettingsExpander { Header = e.Header, Description = e.Description };
                foreach (var child in e.Rows) ex.Items.Add(Render(child));
                return ex;
            }
            case InfoRow i:
                return new InfoBar
                {
                    IsOpen = true, IsClosable = false, Title = i.Header, Message = i.Description,
                    Severity = i.Severity switch { Severity.Success => InfoBarSeverity.Success, Severity.Warning => InfoBarSeverity.Warning, Severity.Error => InfoBarSeverity.Error, _ => InfoBarSeverity.Informational }
                };
            case CustomRow cr:
            {
                UIElement? inner = null; string? err = null;
                try { inner = _provider.CreateCustomControl(_host, cr.ControlId) as UIElement; if (inner == null) err = "The module returned no control for '" + cr.ControlId + "'."; }
                catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                if (inner == null) return new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = cr.Header, Message = err };
                return new SettingsCard { Header = cr.Header, Description = cr.Description, Content = inner, ContentAlignment = ContentAlignment.Vertical, HorizontalContentAlignment = HorizontalAlignment.Left };
            }
            default:
                return new TextBlock { Text = "Unknown row kind " + row.GetType().Name };
        }
    }

    UIElement ValueText(IObservableValue<string> v, string unit)
    {
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        void Set() => tb.Text = unit.Length > 0 ? v.Value + " " + unit : v.Value;
        Set();
        v.Changed += () => OnUi(Set);
        return tb;
    }

    /// <summary>Everything searchable on a model: header and description of every row.</summary>
    public static IEnumerable<(string Text, string Where)> Index(PageModel m)
    {
        IEnumerable<Row> Flat(IEnumerable<Row> rows) => rows.SelectMany(r => r is ExpanderRow e ? new[] { r }.Concat(Flat(e.Rows)) : new[] { r });
        var rows = Flat(m.Groups.SelectMany(g => g.Rows));
        if (m.Enable != null) rows = rows.Prepend(m.Enable);
        foreach (var r in rows) yield return (r.Header + (r.Description.Length > 0 ? " - " + r.Description : ""), r.Header);
    }
}
