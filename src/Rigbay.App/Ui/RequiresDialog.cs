using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
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
        var body = new StackPanel { Spacing = 6, MaxWidth = 480 };
        body.Children.Add(Ui.Text(Host.F("requires.intro", e.DisplayName, e.AuthorText()), wrap: true).With(t => t.Margin = new Thickness(0, 0, 0, 4)));
        void Add(string label, IEnumerable<string> items)
        {
            var list = items.ToList();
            body.Children.Add(Ui.Text(label, weight: FontWeights.SemiBold, wrap: true).With(t => t.Margin = new Thickness(0, 4, 0, 0)));
            foreach (var i in list) body.Children.Add(Ui.Text(i, "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(12, 0, 0, 0)));
        }
        if (m.Requires.Admin) Add(Host.T("requires.admin"), Array.Empty<string>());
        if (m.Requires.Network.Count > 0) Add(Host.T("requires.network"), m.Requires.Network);
        if (m.Requires.Registry.Count > 0) Add(Host.T("requires.registry"), m.Requires.Registry);
        if (m.Requires.Services.Count > 0) Add(Host.T("requires.services"), m.Requires.Services);
        if (m.DeclaredProbePaths.Count > 0) Add(Host.T(ModuleManifest.OutsideFilesLabelKey), m.DeclaredProbePaths);
        body.Children.Add(Ui.Text(Host.T("requires.note"), "RbSecondaryCaption", wrap: true).With(t => t.Margin = new Thickness(0, 10, 0, 0)));
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = Host.F("requires.title", e.DisplayName), Content = body,
            PrimaryButtonText = Host.T("requires.turn-on"), CloseButtonText = Host.T("approval.cancel"),
            RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
        };
        Dialogs.Confirm(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
