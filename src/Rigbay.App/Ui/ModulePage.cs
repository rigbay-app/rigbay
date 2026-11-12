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

/// <summary>A module's page: the enable row (host-owned), then the module's groups, then its links.</summary>
static class ModulePage
{
    /// <summary>The tiles and chart of the page built last (the self-test looks at them); null when that page had none.</summary>
    public static ReadingBlock? LastBlock { get; private set; }

    public static UIElement Build(ModuleEntry e, PageScope scope, out RowRenderer? renderer)
    {
        renderer = null;
        LastBlock = null;
        var page = new StackPanel();

        var right = new List<UIElement>();
        foreach (var link in e.LastPage?.Links ?? Array.Empty<PageLink>())
            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                right.Add(new HyperlinkButton { Content = e.Localisation.T(link.TextKey), NavigateUri = uri });
        if (right.Count == 0 && e.Manifest?.Homepage is { } home && Uri.TryCreate(home, UriKind.Absolute, out var hu))
            right.Add(new HyperlinkButton { Content = Host.T("page.homepage"), NavigateUri = hu });
        right.Add(new HyperlinkButton { Content = Host.T("nav.log") }.With(b => b.Click += (_, _) => Host.Window.Navigate("log")));
        var meta = string.Join("  ·  ", new[] { e.Id, e.VersionText().Length > 0 ? "v" + e.VersionText() : "", e.AuthorText() }.Where(s => s.Length > 0));
        page.Children.Add(Ui.PageHeader(ModuleIconUi.Tile(e, 48), e.DisplayName, meta, right.ToArray()));

        page.Children.Add(EnableCard(e));

        switch (e.Lifecycle)
        {
            case ModuleLifecycle.Faulted:
            case ModuleLifecycle.Invalid:
            {
                var log = Ui.LinkButton(Host.T("page.view-log"), () => Host.Window.Navigate("log"));
                if (e.Lifecycle == ModuleLifecycle.Invalid)
                {
                    // short summary up top, then every manifest problem on its own line, in the user's language
                    page.Children.Add(Ui.StateBlock(BayState.Failed, Host.T("page.invalid-title"), e.ShortReasonText() + "\n" + Host.T("page.invalid-hint"), log));
                    page.Children.Add(new TextBlock { Text = Host.T("page.invalid-problems"), Style = Ui.Style("RbGroupHeader") });
                    var list = new StackPanel { Spacing = 6, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 0, 0, 0) };
                    foreach (var line in e.IssueLines())
                    {
                        var t = Ui.Text(line, wrap: true);
                        t.IsTextSelectionEnabled = true;
                        list.Children.Add(t);
                    }
                    page.Children.Add(list);
                }
                else
                    page.Children.Add(Ui.StateBlock(BayState.Failed, Host.T("page.failed-title"), e.ReasonText(), log));
                break;
            }
            case ModuleLifecycle.Incompatible:
            case ModuleLifecycle.Unsupported:
                page.Children.Add(Ui.StateBlock(BayState.Unsupported, Host.T(e.Lifecycle == ModuleLifecycle.Incompatible ? "page.incompatible-title" : "page.unsupported-title"), e.ReasonText()));
                break;
            default:
                var shown = e.LastPage;
                if (shown == null)
                {
                    page.Children.Add(Ui.Text(Host.T(e.Lifecycle == ModuleLifecycle.Running ? "page.no-page" : "page.off-hint"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 14, 0, 0)));
                    break;
                }
                renderer = new RowRenderer(e, scope);
                bool running = e.Lifecycle == ModuleLifecycle.Running;
                // tiles and the trend chart come before the rows: the first thing on a page with readings is the readings
                var block = running ? ReadingBlock.For(e, shown, scope) : null;
                LastBlock = block;
                if (shown.Primary is { } primary)
                {
                    // the module's main job first and large, with its readings, in one card (Sketch 1): the control, then the tiles, then the chart
                    var hero = new StackPanel { Spacing = 18 };
                    hero.Children.Add(renderer.RenderPrimary(primary));
                    if (block != null) { block.Margin = new Thickness(0); hero.Children.Add(block); }
                    page.Children.Add(new Border
                    {
                        Style = Ui.Style("RbHeroCard"), Margin = new Thickness(0, 12, 0, 4),
                        Child = new ContentControl { Content = hero, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = running },
                    });
                }
                else if (block != null) page.Children.Add(block);
                var groups = renderer.RenderGroups(shown);
                var host = new ContentControl { Content = groups, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = e.Lifecycle == ModuleLifecycle.Running };
                page.Children.Add(host);
                break;
        }
        return page;
    }

    static UIElement EnableCard(ModuleEntry e)
    {
        bool canToggle = e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running;
        bool suppress = false;
        var sw = new ToggleSwitch { IsOn = e.Lifecycle == ModuleLifecycle.Running, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = canToggle };
        AutomationProperties.SetName(sw, Host.F("module.enable", e.DisplayName));
        sw.Toggled += async (_, _) =>
        {
            if (suppress) return;
            sw.IsEnabled = false;
            try
            {
                bool now = await ToggleAsync(e, sw.IsOn);
                if (now != sw.IsOn) { suppress = true; sw.IsOn = now; suppress = false; }
            }
            catch (Exception ex) { AppLog.Error("enable switch failed", ex); }
            finally { sw.IsEnabled = e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running; }
        };
        return new SettingsCard { Header = Host.F("module.enable", e.DisplayName), Description = e.DisplayDescription, Content = sw };
    }

    /// <summary>Turns a module on or off. The first time a module is turned on, the user sees what it declares it uses. Returns the new state.</summary>
    public static async Task<bool> ToggleAsync(ModuleEntry e, bool on)
    {
        if (on)
        {
            if (e.Lifecycle == ModuleLifecycle.Running) return true;
            if (!AppState.IsAcknowledged(e) && HasDeclarations(e))
            {
                if (!await RequiresDialog.AskAsync(e)) return false;
                AppState.Acknowledge(e);
            }
            await Task.Run(() => e.Enable());
            return e.Lifecycle == ModuleLifecycle.Running;
        }
        await Task.Run(() => e.Disable());
        return e.Lifecycle == ModuleLifecycle.Running;
    }

    public static bool HasDeclarations(ModuleEntry e) => e.Manifest is { } m && (m.Requires.Admin || m.Requires.Network.Count + m.Requires.Registry.Count + m.Requires.Services.Count > 0 || m.ReadsOutsideItsFolder);
}
