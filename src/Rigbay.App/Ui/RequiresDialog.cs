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

/// <summary>Before the first enable: what the module declares it uses (a declaration, not a sandbox).</summary>
static class RequiresDialog
{
    public static async Task<bool> AskAsync(ModuleEntry e)
    {
        var root = Host.Window?.Content?.XamlRoot;
        if (root == null) return false;
        var m = e.Manifest!;
        var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
        body.Children.Add(Ui.Text(Host.F("requires.intro", e.DisplayName, e.AuthorText()), wrap: true));
        void Add(string label, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            body.Children.Add(Ui.Text(label, weight: FontWeights.SemiBold));
            foreach (var i in list) body.Children.Add(Ui.Text(i, "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(12, 0, 0, 0)));
        }
        if (m.Requires.Admin) Add(Host.T("requires.admin"), new[] { Host.T("requires.admin-yes") });
        Add(Host.T("requires.network"), m.Requires.Network);
        Add(Host.T("requires.registry"), m.Requires.Registry);
        Add(Host.T("requires.services"), m.Requires.Services);
        Add(Host.T(ModuleManifest.OutsideFilesLabelKey), m.DeclaredProbePaths);
        body.Children.Add(Ui.Text(Host.T("requires.note"), "RbSecondaryCaption", wrap: true));
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = Host.F("requires.title", e.DisplayName), Content = body,
            PrimaryButtonText = Host.T("requires.turn-on"), CloseButtonText = Host.T("approval.cancel"), DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
