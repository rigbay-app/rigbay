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

/// <summary>Language, theme, start with Windows, log level, open the log folder, and the hotkeys of all modules.</summary>
static class SettingsPage
{
    public static UIElement Build(PageScope scope)
    {
        var page = new StackPanel();
        page.Children.Add(Ui.PageHeader(Host.T("nav.settings"), null));
        page.Children.Add(new TextBlock { Text = Host.T("settings.general"), Style = Ui.Style("RbGroupHeader"), Margin = new Thickness(1, 0, 0, 6) });
        var general = new StackPanel { Spacing = 2 };

        // language
        var lang = new ComboBox { MinWidth = 220 };
        foreach (var l in Host.Core.Languages.Languages) lang.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        lang.SelectedItem = lang.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Host.Core.Languages.CurrentCode);
        AutomationProperties.SetName(lang, Host.T("settings.language"));
        lang.SelectionChanged += (_, _) =>
        {
            if (lang.SelectedItem is not ComboBoxItem it || (string)it.Tag == Host.Core.Languages.CurrentCode) return;
            Host.Dq.TryEnqueue(() => Host.Core.SetLanguage((string)it.Tag));   // remembers the choice; the window rebuilds itself from LanguageChanged
        };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.language"), Description = Host.T("settings.language-desc"), Content = lang });

        // theme
        var theme = new ComboBox { MinWidth = 220 };
        foreach (var t in new[] { "system", "light", "dark" }) theme.Items.Add(new ComboBoxItem { Content = Host.T("theme." + t), Tag = t });
        theme.SelectedItem = theme.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == AppState.Settings.Theme) ?? theme.Items[0];
        AutomationProperties.SetName(theme, Host.T("settings.theme"));
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is not ComboBoxItem it) return;
            AppState.Settings.Theme = (string)it.Tag;
            AppState.Save();
            Host.Window.ApplyTheme((string)it.Tag);
        };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.theme"), Content = theme });

        // start with Windows
        var startStatus = Ui.Text("", "RbSecondaryCaption", trim: true);
        startStatus.MaxWidth = 280;
        var start = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = false };
        AutomationProperties.SetName(start, Host.T("settings.start"));
        bool suppress = false;
        async Task RefreshStart()
        {
            bool exists = await Task.Run(StartupTask.Exists);
            suppress = true; start.IsOn = exists; start.IsEnabled = true; suppress = false;
            startStatus.Text = exists ? Host.T("settings.start-on") : "";
        }
        _ = RefreshStart();
        start.Toggled += async (_, _) =>
        {
            if (suppress) return;
            start.IsEnabled = false;
            var on = start.IsOn;
            var (ok, msg) = await Task.Run(() => on ? StartupTask.Create() : StartupTask.Delete());
            if (!ok) { startStatus.Style = Ui.Style("RbErrorText"); startStatus.Text = msg; }
            await RefreshStart();
            if (!ok) { startStatus.Style = Ui.Style("RbErrorText"); startStatus.Text = msg; } else startStatus.Style = Ui.Style("RbSecondaryCaption");
        };
        var startPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { startStatus, start } };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.start"), Description = Host.T("settings.start-desc"), Content = startPanel });

        // log level
        var level = new ComboBox { MinWidth = 220 };
        foreach (var l in new[] { LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error }) level.Items.Add(new ComboBoxItem { Content = Host.T("level." + l.ToString().ToLowerInvariant()), Tag = l });
        level.SelectedItem = level.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (LogLevel)i.Tag == Host.Core.Log.Minimum);
        AutomationProperties.SetName(level, Host.T("settings.log-level"));
        level.SelectionChanged += (_, _) =>
        {
            if (level.SelectedItem is not ComboBoxItem it) return;
            Host.Core.Log.Minimum = (LogLevel)it.Tag;
            AppState.Settings.LogLevel = ((LogLevel)it.Tag).ToString().ToLowerInvariant();
            AppState.Save();
        };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.log-level"), Description = Host.T("settings.log-level-desc"), Content = level });

        // daily check of the module list (off by default: nothing leaves the PC unless the user asks)
        var daily = new ToggleSwitch { IsOn = AppState.Settings.CheckDaily, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(daily, Host.T("settings.module-check"));
        daily.Toggled += (_, _) =>
        {
            AppState.Settings.CheckDaily = daily.IsOn;
            AppState.Save();
            if (daily.IsOn && InstallServices.DailyCheckDue(DateTimeOffset.UtcNow)) _ = InstallServices.CheckNowAsync();
        };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.module-check"), Description = Host.T("settings.module-check-desc"), Content = daily });

        // log folder
        general.Children.Add(new SettingsCard { Header = Host.T("settings.log-folder"), Description = Host.Core.Paths.LogDirectory, Content = Ui.LinkButton(Host.T("settings.open"), () => ModulesPage.OpenFolder(Host.Core.Paths.LogDirectory)) });

        // version and where the source is
        general.Children.Add(new SettingsCard { Header = Host.T("settings.about"), Description = Host.F("settings.about-desc", AppPaths.Version, HostApi.Current), Content = new HyperlinkButton { Content = Host.T("settings.about-repo"), NavigateUri = new Uri("https://github.com/rigbay-app/rigbay") } });
        page.Children.Add(general);

        // hotkeys
        page.Children.Add(new TextBlock { Text = Host.T("settings.hotkeys"), Style = Ui.Style("RbGroupHeader") });
        var hot = new StackPanel { Spacing = 2 };
        var entries = Host.Core.Hotkeys.Entries;
        if (entries.Count == 0) hot.Children.Add(Ui.Text(Host.T("settings.hotkeys-none"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 0, 0, 0)));
        foreach (var h in entries)
        {
            var mod = Host.Core.Find(h.ModuleId);
            var gesture = Ui.Text(h.Gesture.Value?.ToString() ?? Host.T("row.shortcut-none"));
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            if (h.HasConflict.Value || Host.Hotkeys?.IsRefused(h) == true) content.Children.Add(Ui.Text(Host.T(Host.Hotkeys?.IsRefused(h) == true ? "settings.hotkey-refused" : "settings.hotkey-conflict"), "RbErrorText"));
            content.Children.Add(gesture);
            hot.Children.Add(new SettingsCard { Header = mod?.Localisation.T(h.NameKey) ?? h.NameKey, Description = mod?.DisplayName ?? "", Content = content });
        }
        page.Children.Add(hot);
        return page;
    }
}
