using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.App.Services;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>Read-only tail of the host log (the last 500 entries kept in memory).</summary>
static class LogPage
{
    public static UIElement Build(PageScope scope)
    {
        var box = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12, MinHeight = 420, Height = 560, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(box, Host.T("nav.log"));

        void Fill()
        {
            box.Text = string.Join("\r", Host.Core.Log.Recent().Select(AppLog.FormatLine));
            box.Select(box.Text.Length, 0);
            try { box.UpdateLayout(); var sv = FindScroll(box); sv?.ChangeView(null, sv.ScrollableHeight, null, true); } catch { }
        }
        Fill();

        // Throttle: at most one refresh per 400 ms while the page is open.
        var timer = Host.Dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        bool dirty = false;
        void OnWritten() { dirty = true; }
        Action<LogEntry> onEntry = _ => OnWritten();
        Host.Core.Log.EntryWritten += onEntry;
        timer.Tick += (_, _) => { if (dirty && !scope.Disposed) { dirty = false; Fill(); } };
        timer.Start();
        scope.Add(() => { Host.Core.Log.EntryWritten -= onEntry; timer.Stop(); });

        var folder = Ui.LinkButton(Host.T("settings.open"), () => ModulesPage.OpenFolder(Host.Core.Paths.LogDirectory));
        var copy = Ui.LinkButton(Host.T("log.copy"), () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(string.Join(Environment.NewLine, Host.Core.Log.Recent().Select(AppLog.FormatLine)));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        });
        var page = new StackPanel();
        page.Children.Add(Ui.PageHeader(Host.T("nav.log"), Host.Core.Log.CurrentFile, folder, copy));
        page.Children.Add(box);
        return page;
    }

    static ScrollViewer? FindScroll(DependencyObject root)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (c is ScrollViewer sv) return sv;
            var r = FindScroll(c);
            if (r != null) return r;
        }
        return null;
    }
}
