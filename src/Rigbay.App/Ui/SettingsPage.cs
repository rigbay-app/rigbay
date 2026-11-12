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

/// <summary>
/// Settings, grouped by what the user came for (SE-1): General (language, theme, start with Windows, the daily module check), Hotkeys (every
/// module's shortcuts, changeable here, conflicts named), Troubleshooting (log level, log folder, version info to copy) and About.
/// </summary>
static class SettingsPage
{
    public static UIElement Build(PageScope scope)
    {
        var page = new StackPanel();
        page.Children.Add(Ui.PageHeader(Host.T("nav.settings"), null));

        // ---------------------------------------------------------------- General
        page.Children.Add(Group(Host.T("settings.general"), first: true));
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
        page.Children.Add(general);

        // ---------------------------------------------------------------- Hotkeys
        page.Children.Add(Group(Host.T("settings.hotkeys")));
        page.Children.Add(Hotkeys(scope));

        // ---------------------------------------------------------------- Troubleshooting
        page.Children.Add(Group(Host.T("settings.troubleshooting")));
        var trouble = new StackPanel { Spacing = 2 };

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
        trouble.Children.Add(new SettingsCard { Header = Host.T("settings.log-level"), Description = Host.T("settings.log-level-desc"), Content = level });

        // log folder
        var openLog = Ui.LinkButton(Host.T("settings.open"), () => ModulesPage.OpenFolder(Host.Core.Paths.LogDirectory));
        AutomationProperties.SetName(openLog, Host.T("settings.log-folder") + ": " + Host.T("settings.open"));
        trouble.Children.Add(new SettingsCard { Header = Host.T("settings.log-folder"), Description = Host.Core.Paths.LogDirectory, Content = openLog });

        // version info for a bug report: the versions the status bar used to show on every page (SH-2), and every module's
        var copied = Ui.Text(Host.T("settings.copied"), "RbSecondaryCaption");
        copied.Visibility = Visibility.Collapsed;
        var copy = Ui.LinkButton(Host.T("settings.copy-version"), () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(Host.Window.VersionInfo());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            copied.Visibility = Visibility.Visible;
        });
        AutomationProperties.SetName(copy, Host.T("settings.copy-version"));
        trouble.Children.Add(new SettingsCard
        {
            Header = Host.T("settings.version-info"), Description = Host.T("settings.version-info-desc"),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { copied, copy } },
        });
        page.Children.Add(trouble);

        // ---------------------------------------------------------------- About
        page.Children.Add(Group(Host.T("settings.about-group")));
        var about = new SettingsExpander
        {
            Header = Host.T("settings.about"), Description = Host.F("settings.about-desc", AppPaths.Version, HostApi.Current),
            Content = new HyperlinkButton { Content = Host.T("settings.about-repo"), NavigateUri = new Uri("https://github.com/rigbay-app/rigbay") },
            Tag = "about",
        };
        var facts = new FactSheet();
        facts.Add("Rigbay", AppPaths.Version.ToString());
        facts.Add(Host.T("fact.host-api"), HostApi.Current.ToString());
        facts.Add("Windows App SDK", MainWindow.RuntimeVersion());
        facts.Add(".NET", Environment.Version.ToString());
        facts.Add("Windows", Environment.OSVersion.Version.ToString());
        facts.Add(Host.T("fact.admin"), Native.IsUserAnAdmin() ? Host.T("status.admin") : Host.T("status.not-admin"));
        about.Items.Add(facts.Card());
        page.Children.Add(about);
        return page;
    }

    static TextBlock Group(string text, bool first = false)
    {
        var t = new TextBlock { Text = text, Style = Ui.Style("RbGroupHeader") };
        if (first) t.Margin = new Thickness(1, 0, 0, 6);
        AutomationProperties.SetHeadingLevel(t, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return t;
    }

    // ---------------------------------------------------------------- hotkeys (SE-3)

    /// <summary>
    /// Every module's shortcuts in one list. The shortcut is a button that opens the same capture dialog as the module page and sets the same binding
    /// (the registry saves the choice and checks conflicts). A conflict names who else holds the key.
    /// </summary>
    static UIElement Hotkeys(PageScope scope)
    {
        var hot = new StackPanel { Spacing = 2 };
        var entries = Host.Core.Hotkeys.Entries;
        if (entries.Count == 0) hot.Children.Add(Ui.Text(Host.T("settings.hotkeys-none"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 0, 0, 0)));
        foreach (var h in entries) hot.Children.Add(HotkeyCard(h, scope));
        return hot;
    }

    static string GestureText(HotkeyGesture? g) => g?.ToString() ?? Host.T("row.shortcut-none");

    /// <summary>Who else holds this entry's key: "Fan studio: Boost", or the host's own name for a key the host reserved.</summary>
    internal static string OthersOf(HotkeyEntry h)
    {
        if (h.Gesture.Value is not { } g) return "";
        var conflict = Host.Core.Hotkeys.Conflicts.FirstOrDefault(c => c.Gesture == g);
        if (conflict == null) return "";
        var names = new List<string>();
        foreach (var owner in conflict.Owners)
        {
            if (owner == h.Key) continue;
            if (owner.StartsWith("host:", StringComparison.Ordinal)) { names.Add("Rigbay"); continue; }
            var other = Host.Core.Hotkeys.Entries.FirstOrDefault(x => x.Key == owner);
            var mod = other != null ? Host.Core.Find(other.ModuleId) : null;
            names.Add(other == null ? owner : mod != null ? $"{mod.DisplayName}: {mod.Localisation.T(other.NameKey)}" : other.NameKey);
        }
        return string.Join(", ", names);
    }

    static UIElement HotkeyCard(HotkeyEntry h, PageScope scope)
    {
        var mod = Host.Core.Find(h.ModuleId);
        var name = mod?.Localisation.T(h.NameKey) ?? h.NameKey;
        var description = new StackPanel { Spacing = 1 };
        description.Children.Add(Ui.Text(mod?.DisplayName ?? "", "RbSecondaryCaption", trim: true));
        var problem = Ui.Text("", "RbErrorText", wrap: true);
        description.Children.Add(problem);

        var button = new Button { MinWidth = 140 };
        AutomationProperties.SetName(button, Host.F("settings.hotkey-change-of", name));
        void Show()
        {
            button.Content = GestureText(h.Gesture.Value);
            bool refused = Host.Hotkeys?.IsRefused(h) == true;
            var others = h.HasConflict.Value ? OthersOf(h) : "";
            problem.Text = refused ? Host.T("settings.hotkey-refused")
                : h.HasConflict.Value ? (others.Length > 0 ? Host.F("settings.hotkey-conflict-with", others) : Host.T("settings.hotkey-conflict"))
                : "";
            problem.Visibility = problem.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetHelpText(button, problem.Text);
        }
        Show();
        scope.Bind(h.Gesture, mod, _ => Show());
        scope.Bind(h.HasConflict, mod, _ => Show());

        button.Click += async (_, _) =>
        {
            try
            {
                var (ok, gesture) = await CaptureDialog.AskAsync(name);
                if (!ok) return;
                // the same binding the module page's shortcut row sets; inside the module's boundary, because the module listens to it
                void Set() => h.Gesture.Value = gesture;
                if (mod != null) mod.Invoke("hotkey change", Set); else Set();
                Show();
            }
            catch (Exception ex) { AppLog.Error("hotkey change failed", ex); }
        };
        // the header stays the plain name (the search and the self-test find the card by it)
        return new SettingsCard { Header = name, Description = description, Content = button };
    }
}
