using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.App.Services;
using Rigbay.Core;
using Rigbay.Core.Install;

namespace Rigbay.App;

/// <summary>
/// The Modules page: what is installed (state, version, update, restart required, remove) and what the registry offers (what each
/// module declares, where its source is, install). Short rows, one hairline between them, honest text for every state: offline, list
/// unreachable, hash mismatch, wrong host API, already installed, failed with the reason. The logic is <see cref="ModuleManager"/>; this
/// only draws it and redraws when it changes (progress ticks update the bars in place, everything else rebuilds the page with the view kept).
/// </summary>
static class ModulesPage
{
    public static UIElement Build(PageScope scope) => new View(scope).Build();

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Warn("could not open " + path, ex); }
    }

    /// <summary>Shown once per run; the user can close it.</summary>
    internal static bool StartupReportClosed;

    /// <summary>The reason "Restart now" failed, shown on the page.</summary>
    internal static string? RestartError;

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
        return (bytes / 1048576.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
    }

    /// <summary>One line about what a release declares, for the row (the full list is in the row's details).</summary>
    internal static string DeclaresSummary(RegistryDeclares d)
    {
        var parts = new List<string>();
        if (d.Admin) parts.Add(Host.T("decl.admin"));
        if (d.Network.Count > 0) parts.Add(Host.F("decl.network", d.Network.Count));
        if (d.Registry.Count > 0) parts.Add(Host.F("decl.registry", d.Registry.Count));
        if (d.Services.Count > 0) parts.Add(Host.F("decl.services", d.Services.Count));
        if (d.OutsideFiles.Count > 0) parts.Add(Host.T("decl.outside"));
        return parts.Count == 0 ? Host.T("decl.none") : string.Join("  ·  ", parts);
    }

    internal static string FailureText(OperationFailure f) => Host.F("modules.failed", Host.T(InstallException.KeyOf(f.Code)));

    internal static string PhaseText(OperationState s)
    {
        var phase = Host.T("core.install.phase." + s.Phase.ToString().ToLowerInvariant());
        return s.Phase == InstallPhase.Downloading && s.Total is { } total && total > 0 ? $"{phase}  {FormatSize(s.Done)} / {FormatSize(total)}" : phase;
    }

    sealed class View
    {
        readonly PageScope _scope;
        readonly ModuleManager _m = InstallServices.Manager;
        string _signature = "";
        readonly List<(string Id, ProgressBar Bar, TextBlock Text)> _bars = new();

        public View(PageScope scope) => _scope = scope;

        public UIElement Build()
        {
            _signature = Signature();
            var page = new StackPanel { Spacing = 2 };

            var openFolder = Ui.LinkButton(Host.T("modules.open-folder"), () => OpenFolder(AppPaths.ModulesRoot));
            var check = Ui.LinkButton(_m.IsChecking ? Host.T("modules.checking") : Host.T("modules.check"), () => _ = Safe(InstallServices.CheckNowAsync));
            check.IsEnabled = !_m.IsChecking;
            AutomationProperties.SetName(check, Host.T("modules.check"));
            page.Children.Add(Ui.PageHeader(Host.T("nav.modules"), AppPaths.ModulesRoot, openFolder, check));

            AddBanners(page);
            AddInstalled(page);
            AddAvailable(page);

            Action handler = () => Ui.Post(null, OnManagerChanged, "modules page");
            _m.Changed += handler;
            _scope.Add(() => _m.Changed -= handler);
            ApplyProgress();
            return page;
        }

        // ---------------------------------------------------------------- redraw

        string Signature()
        {
            var s = _m.Snapshot;
            var parts = new List<string>
            {
                $"{s.Status}/{s.Problem}/{s.FetchedAt?.Ticks}/{_m.IsChecking}/{s.Document?.Modules.Count}",
                $"op:{_m.Operation?.Id}/{_m.Operation?.Kind}",
                string.Join(",", _m.PendingOps.Select(p => p.Op + ":" + p.Id + ":" + p.Version)),
                string.Join(",", Host.Core.Modules.Select(e => $"{e.Id}/{e.VersionText()}/{e.Lifecycle}/{e.Pending}/{e.PendingVersion}/{_m.InstalledRowOf(Installed(e)).PreviousVersion}")),
                RestartError ?? "",
                StartupReportClosed.ToString(),
            };
            foreach (var e in Host.Core.Modules) if (_m.FailureOf(e.Id) is { } f) parts.Add($"f:{f.Id}:{f.Code}");
            if (s.Document is { } doc) foreach (var mod in doc.Modules) if (_m.FailureOf(mod.Id) is { } f) parts.Add($"f:{f.Id}:{f.Code}");
            return string.Join("|", parts);
        }

        void OnManagerChanged()
        {
            if (_scope.Disposed) return;
            if (Signature() == _signature) { ApplyProgress(); return; }
            Host.Window.RefreshPageKeepingView();
        }

        void ApplyProgress()
        {
            var op = _m.Operation;
            foreach (var (id, bar, text) in _bars)
            {
                if (op is null || op.Id != id) continue;
                bool known = op.Phase == InstallPhase.Downloading && op.Total is > 0;
                bar.IsIndeterminate = !known;
                if (known) bar.Value = Math.Clamp(100.0 * op.Done / op.Total!.Value, 0, 100);
                text.Text = PhaseText(op);
            }
        }

        // ---------------------------------------------------------------- banners

        void AddBanners(StackPanel page)
        {
            if (RestartError != null)
                page.Children.Add(Info(InfoBarSeverity.Error, Host.T("modules.restart-title"), Host.F("modules.restart-failed", RestartError), closable: false));

            var pending = _m.PendingOps;
            if (pending.Count > 0)
            {
                var bar = Info(InfoBarSeverity.Warning, Host.T("modules.restart-title"), Host.T("modules.restart-body"), closable: false);
                var restart = new Button { Content = Host.T("modules.restart-now") };
                AutomationProperties.SetName(restart, Host.T("modules.restart-now"));
                restart.Click += (_, _) => { restart.IsEnabled = false; _ = Safe(() => Host.Window.RestartAsync()); };
                bar.ActionButton = restart;
                page.Children.Add(bar);
            }

            var report = InstallServices.StartupReport;
            if (report is { Outcomes.Count: > 0 } && !StartupReportClosed)
            {
                bool bad = report.Outcomes.Any(o => !o.Applied && o.Message != null);
                var lines = report.Outcomes.Select(ReportLine).ToList();
                var bar = Info(bad ? InfoBarSeverity.Warning : InfoBarSeverity.Success, Host.T("modules.report-title"), string.Join("\n", lines), closable: true);
                bar.CloseButtonClick += (_, _) => StartupReportClosed = true;
                page.Children.Add(bar);
            }
        }

        string ReportLine(PendingOutcome o)
        {
            // a removed module has no folder and no manifest any more: take its name from the saved registry list before falling back to the id (E2E1)
            var name = Host.Core.Find(o.Id)?.DisplayName ?? _m.RegistryNameOf(o.Id) ?? o.Id;
            if (!o.Applied && o.Message != null)
                return Host.F(o.WillRetry ? "modules.report-retry" : "modules.report-failed", name, o.Message);
            return o.Op switch
            {
                PendingKinds.Install => Host.F("modules.report-install", name, o.ToVersion ?? ""),
                PendingKinds.Update => Host.F(o.NeedsApproval ? "modules.report-update-ask" : "modules.report-update", name, o.FromVersion ?? "", o.ToVersion ?? ""),
                PendingKinds.Remove => Host.F("modules.report-remove", name),
                _ => Host.F("modules.report-rollback", name, o.ToVersion ?? ""),
            };
        }

        static InfoBar Info(InfoBarSeverity severity, string title, string message, bool closable) => new()
        {
            IsOpen = true, IsClosable = closable, Severity = severity, Title = title, Message = message, Margin = new Thickness(0, 0, 0, 12),
        };

        // ---------------------------------------------------------------- installed

        static InstalledModule Installed(ModuleEntry e) => new(e.Id, e.Manifest?.Version, e.Lifecycle);

        void AddInstalled(StackPanel page)
        {
            page.Children.Add(new TextBlock { Text = Host.T("modules.installed"), Style = Ui.Style("RbGroupHeader"), Margin = new Thickness(1, 4, 0, 6) });
            var entries = Host.Core.Modules;
            if (entries.Count == 0)
            {
                bool offerList = _m.Snapshot.Document is null && !_m.IsChecking;
                page.Children.Add(Ui.Text(Host.T(offerList ? "modules.none-installed-welcome" : "modules.none-installed"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 0, 0, 8)));
                return;
            }
            var list = new StackPanel { Spacing = 2 };
            foreach (var e in entries) list.Children.Add(Item(e));
            page.Children.Add(list);
        }

        UIElement Item(ModuleEntry e)
        {
            var row = _m.InstalledRowOf(Installed(e));
            var op = _m.Operation;
            bool working = op?.Id == e.Id;
            var failure = _m.FailureOf(e.Id);

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            header.Children.Add(Ui.Bay(e.Bay.State, 28));
            header.Children.Add(ModuleIconUi.Tile(e, 32));
            header.Children.Add(Ui.Text(e.DisplayName + (e.VersionText().Length > 0 ? "  v" + e.VersionText() : "")));

            // the right side: switch, or the progress of what is running, or what is waiting for the restart
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            if (working) right.Children.Add(ProgressPanel(e.Id, e.DisplayName));
            else if (row.Pending is { } pending)
                right.Children.Add(Btn(Host.T("modules.undo"), () => _m.UndoAsync(e.Id), Host.F("modules.undo-of", e.DisplayName)));
            else if (row.Update is { } update)
                right.Children.Add(Btn(Host.F("modules.update-to", update.Version), () => AskAndUpdateAsync(e, update), Host.F("modules.update-of", e.DisplayName)));
            right.Children.Add(Switch(e));

            string word = Ui.StateWord(e.Bay.State);
            var reason = e.ShortReasonText();
            object description;
            if (failure != null)
                description = new TextBlock { Text = $"{word}  ·  {FailureText(failure)}", Style = Ui.Style("RbErrorText"), TextWrapping = TextWrapping.Wrap }.With(t => ToolTipService.SetToolTip(t, failure.Detail));
            else if (row.Pending is { } p)
                description = $"{Ui.StateWord(BayState.RestartRequired)}  ·  {PendingText(p)}";
            else if (row.Update is { } u)
                description = $"{word}  ·  {Host.F("modules.update-available", u.Version)}";
            else
                description = reason.Length > 0 ? $"{word}  ·  {reason}" : $"{word}  ·  {e.DisplayDescription}";

            var expander = new SettingsExpander { Header = header, Description = description, Content = right, Tag = e.Id };
            // the header is a stack of icon and text, which gives the expander no accessible name; the available rows have one because their header is a string (E2E1)
            AutomationProperties.SetName(expander, e.DisplayName);
            AutomationProperties.SetName(header, e.DisplayName);
            AddFacts(expander, e);

            if (row.PreviousVersion is { } previous && e.Lifecycle is ModuleLifecycle.Invalid or ModuleLifecycle.Incompatible or ModuleLifecycle.Faulted && row.Pending is null && !working)
                expander.Items.Add(new SettingsCard
                {
                    Header = Host.F("modules.rollback", previous), Description = Host.T("modules.rollback-desc"),
                    Content = Btn(Host.F("modules.rollback", previous), () => _m.RollbackAsync(e.Id), Host.F("modules.rollback-of", e.DisplayName)),
                });
            if (row.Pending is null && !working)
                expander.Items.Add(new SettingsCard
                {
                    Header = Host.T("modules.remove"), Description = Host.T("modules.remove-desc"),
                    Content = Btn(Host.T("modules.remove"), () => AskAndRemoveAsync(e), Host.F("modules.remove-of", e.DisplayName)),
                });
            return expander;
        }

        static string PendingText(PendingOp p) => p.Op switch
        {
            PendingKinds.Update => Host.F("modules.queued-update", p.Version ?? ""),
            PendingKinds.Remove => Host.T("modules.queued-remove"),
            PendingKinds.Rollback => Host.F("modules.queued-rollback", p.Version ?? ""),
            _ => Host.T("modules.queued-install"),
        };

        ToggleSwitch Switch(ModuleEntry e)
        {
            bool canToggle = e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running;
            var sw = new ToggleSwitch { IsOn = e.Lifecycle == ModuleLifecycle.Running, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = canToggle };
            AutomationProperties.SetName(sw, Host.F("module.enable", e.DisplayName));
            bool suppress = false;
            sw.Toggled += async (_, _) =>
            {
                if (suppress) return;
                sw.IsEnabled = false;
                try { bool now = await ModulePage.ToggleAsync(e, sw.IsOn); if (now != sw.IsOn) { suppress = true; sw.IsOn = now; suppress = false; } }
                catch (Exception ex) { AppLog.Error("enable switch failed", ex); }
                finally { sw.IsEnabled = e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running; }
            };
            return sw;
        }

        static void AddFacts(SettingsExpander expander, ModuleEntry e)
        {
            void Fact(string label, string value)
            {
                if (value.Length == 0) return;
                var tb = Ui.Text(value, trim: false, wrap: true);
                tb.MaxWidth = 420; tb.IsTextSelectionEnabled = true; tb.TextAlignment = TextAlignment.Right;
                expander.Items.Add(new SettingsCard { Header = label, Content = tb });
            }
            var m = e.Manifest;
            Fact(Host.T("fact.id"), e.Id);
            Fact(Host.T("fact.author"), e.AuthorText());
            Fact(Host.T("fact.description"), e.DisplayDescription);
            Fact(Host.T("fact.group"), m != null ? Host.T("group." + e.GroupKey()) : "");
            Fact(Host.T("fact.host-api"), m?.HostApi.Text ?? "");
            Fact(Host.T("fact.admin"), m == null ? "" : Host.T(m.Requires.Admin ? "value.yes" : "value.no"));
            Fact(Host.T("fact.network"), m == null ? "" : string.Join("\n", m.Requires.Network));
            Fact(Host.T("fact.registry"), m == null ? "" : string.Join("\n", m.Requires.Registry));
            Fact(Host.T("fact.services"), m == null ? "" : string.Join("\n", m.Requires.Services));
            foreach (var path in m?.DeclaredProbePaths ?? Array.Empty<string>()) Fact(Host.T(ModuleManifest.OutsideFilesLabelKey), path);
            Fact(Host.T("fact.problems"), string.Join("\n", e.IssueLines()));
            Fact(Host.T("fact.folder"), e.Folder);
            if (m?.Homepage is { } home && Uri.TryCreate(home, UriKind.Absolute, out var uri))
                expander.Items.Add(new SettingsCard { Header = Host.T("fact.homepage"), Content = new HyperlinkButton { Content = home, NavigateUri = uri } });
            if (e.Page != null || e.Lifecycle != ModuleLifecycle.Invalid)
                expander.Items.Add(new SettingsCard { Header = Host.T("modules.open-page"), IsClickEnabled = true }.With(c => c.Click += (_, _) => Host.Window.Navigate(e)));
        }

        // ---------------------------------------------------------------- available

        void AddAvailable(StackPanel page)
        {
            page.Children.Add(new TextBlock { Text = Host.T("modules.available"), Style = Ui.Style("RbGroupHeader"), Margin = new Thickness(1, 20, 0, 4) });
            var snap = _m.Snapshot;
            var line = RegistryLine(snap);
            if (line != null)
            {
                var t = Ui.Text(line.Value.Text, line.Value.Error ? "RbErrorText" : "RbSecondaryCaption", wrap: true);
                t.Margin = new Thickness(1, 0, 0, 8);
                t.IsTextSelectionEnabled = true;
                page.Children.Add(t);
            }

            if (snap.Document is null)
            {
                bool neverAsked = snap.Problem is null;
                var button = new Button { Content = Host.T(neverAsked ? "modules.get-list" : "modules.try-again"), IsEnabled = !_m.IsChecking, Style = neverAsked ? Ui.Style("AccentButtonStyle") : null };
                AutomationProperties.SetName(button, button.Content.ToString());
                button.Click += (_, _) => { button.IsEnabled = false; _ = Safe(InstallServices.CheckNowAsync); };
                if (neverAsked)
                    page.Children.Add(Ui.StateBlock(BayState.Off, Host.T("modules.none-title"), Host.T("modules.none-body"), button));
                else
                    page.Children.Add(button.With(b => b.Margin = new Thickness(1, 2, 0, 0)));
                return;
            }

            var rows = _m.AvailableRows();
            if (rows.Count == 0)
            {
                page.Children.Add(Ui.Text(Host.T("modules.registry-empty"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 0, 0, 0)));
                return;
            }
            var list = new StackPanel { Spacing = 2 };
            foreach (var r in rows) list.Children.Add(AvailableItem(r));
            page.Children.Add(list);
        }

        (string Text, bool Error)? RegistryLine(RegistrySnapshot s)
        {
            var when = s.FetchedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
            if (_m.IsChecking) return (Host.T("modules.checking"), false);
            if (s.Document is not null)
            {
                return s.Problem switch
                {
                    null when s.Status == RegistryStatus.Fresh => (Host.F("modules.list-fresh", when), false),
                    null => (Host.F("modules.list-saved", when), false),
                    InstallErrorCode.Offline => (Host.F("modules.list-offline", when), false),
                    InstallErrorCode.RegistryInvalid => (Host.F("modules.list-invalid", when), true),
                    InstallErrorCode.FolderSecurity => (Host.T(InstallException.KeyOf(InstallErrorCode.FolderSecurity)) + Detail(s), true),
                    _ => (Host.F("modules.list-unreachable", when), true),
                };
            }
            return s.Problem switch
            {
                null => null,
                InstallErrorCode.Offline => (Host.T("modules.list-offline-none"), false),
                InstallErrorCode.RegistryInvalid => (Host.T("modules.list-invalid-none") + Detail(s), true),
                InstallErrorCode.FolderSecurity => (Host.T(InstallException.KeyOf(InstallErrorCode.FolderSecurity)) + Detail(s), true),
                _ => (Host.T("modules.list-unreachable-none") + Detail(s), true),
            };
        }

        static string Detail(RegistrySnapshot s) => string.IsNullOrEmpty(s.ProblemDetail) ? "" : "\n" + char.ToUpperInvariant(s.ProblemDetail[0]) + s.ProblemDetail[1..];

        UIElement AvailableItem(AvailableRow r)
        {
            var module = r.Module;
            var version = r.Version;
            var op = _m.Operation;
            bool working = op?.Id == module.Id;
            var failure = _m.FailureOf(module.Id);

            var description = new StackPanel { Spacing = 1 };
            description.Children.Add(Ui.Text(module.Description, "RbSecondaryCaption", trim: true).With(t => { t.TextWrapping = TextWrapping.NoWrap; ToolTipService.SetToolTip(t, module.Description); }));
            if (version != null && r.State != AvailableState.Installed)   // an installed module's facts are in the Installed list
            {
                var meta = $"v{version.Version}  ·  {FormatSize(version.Size)}  ·  {DeclaresSummary(version.Declares)}";
                description.Children.Add(Ui.Text(meta, "RbSecondaryCaption", trim: true).With(t => { t.TextWrapping = TextWrapping.NoWrap; ToolTipService.SetToolTip(t, meta); }));
            }
            // one honest line for the states that are not simply "you can install this"
            string? status = null;
            bool error = false;
            if (failure != null) { status = FailureText(failure); error = true; }
            else switch (r.State)
            {
                case AvailableState.Installed: status = Host.F("modules.state-installed", r.InstalledVersion ?? "?"); break;
                case AvailableState.Queued: status = Host.T("modules.queued-install"); break;
                case AvailableState.Incompatible: status = Host.F("modules.needs-host", version?.HostApi ?? "?"); break;
            }
            if (status != null)
            {
                var t = Ui.Text(status, error ? "RbErrorText" : "RbSecondaryCaption", wrap: true);
                if (failure != null) ToolTipService.SetToolTip(t, failure.Detail);
                description.Children.Add(t);
            }

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            if (working) right.Children.Add(ProgressPanel(module.Id, module.Name));
            else if (failure != null && r.State == AvailableState.Installable)
                right.Children.Add(Btn(Host.T("modules.try-again"), () => _m.InstallAsync(module.Id), Host.F("modules.install-of", module.Name)));
            else switch (r.State)
            {
                case AvailableState.Installable:
                    right.Children.Add(Btn(Host.T("modules.install"), () => _m.InstallAsync(module.Id), Host.F("modules.install-of", module.Name)));
                    break;
                case AvailableState.Queued:
                    right.Children.Add(Btn(Host.T("modules.undo"), () => _m.UndoAsync(module.Id), Host.F("modules.undo-of", module.Name)));
                    break;
            }

            var expander = new SettingsExpander { Header = module.Name, Description = description, Content = right, Tag = "available:" + module.Id };
            if (version != null) AddReleaseFacts(expander, module, version);
            return expander;
        }

        static void AddReleaseFacts(SettingsExpander expander, RegistryModule module, RegistryVersion v)
        {
            void Fact(string label, string value)
            {
                if (value.Length == 0) return;
                var tb = Ui.Text(value, trim: false, wrap: true);
                tb.MaxWidth = 420; tb.IsTextSelectionEnabled = true; tb.TextAlignment = TextAlignment.Right;
                expander.Items.Add(new SettingsCard { Header = label, Content = tb });
            }
            Fact(Host.T("fact.id"), module.Id);
            Fact(Host.T("modules.version"), v.Version);
            Fact(Host.T("modules.released"), v.Released.ToString("d", CultureInfo.CurrentCulture));
            Fact(Host.T("modules.size"), FormatSize(v.Size));
            Fact(Host.T("fact.host-api"), v.HostApi);
            Fact(Host.T("fact.admin"), Host.T(v.Declares.Admin ? "value.yes" : "value.no"));
            Fact(Host.T("fact.network"), string.Join("\n", v.Declares.Network));
            Fact(Host.T("fact.registry"), string.Join("\n", v.Declares.Registry));
            Fact(Host.T("fact.services"), string.Join("\n", v.Declares.Services));
            foreach (var path in v.Declares.OutsideFiles) Fact(Host.T(ModuleManifest.OutsideFilesLabelKey), path);
            Fact(Host.T("modules.works-here"), Host.T("modules.works-here-unknown"));
            if (Uri.TryCreate(module.Repo, UriKind.Absolute, out var repo) && repo.Scheme == Uri.UriSchemeHttps)
                expander.Items.Add(new SettingsCard { Header = Host.T("modules.source"), Content = new HyperlinkButton { Content = module.Repo, NavigateUri = repo } });
            Fact(Host.T("modules.sha"), v.Sha256);
        }

        // ---------------------------------------------------------------- pieces

        UIElement ProgressPanel(string id, string name)
        {
            var bar = new ProgressBar { Width = 150, IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(bar, name);
            var text = Ui.Text("", "RbSecondaryCaption");
            text.MinWidth = 120;
            _bars.Add((id, bar, text));
            var cancel = new Button { Content = Host.T("modules.cancel") };
            AutomationProperties.SetName(cancel, Host.F("modules.cancel-of", name));
            cancel.Click += (_, _) => { cancel.IsEnabled = false; _m.CancelOperation(); };
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { text, bar, cancel } };
        }

        Button Btn(string text, Func<Task> action, string automationName)
        {
            var b = new Button { Content = text };
            AutomationProperties.SetName(b, automationName);
            b.Click += (_, _) =>
            {
                FocusKey.RememberForRefresh(b.XamlRoot, Host.Window.PageContent ?? b);
                b.IsEnabled = false;
                _ = Safe(action);
            };
            return b;
        }

        async Task AskAndRemoveAsync(ModuleEntry e)
        {
            var root = Host.Window?.Content?.XamlRoot;
            if (root == null) return;
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = Host.F("modules.remove-title", e.DisplayName), Content = Ui.Text(Host.T("modules.remove-body"), "RbSecondaryText", wrap: true).With(t => t.MaxWidth = 440),
                PrimaryButtonText = Host.T("modules.remove"), CloseButtonText = Host.T("approval.cancel"), DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await _m.RemoveAsync(e.Id);
            else Host.Window.RefreshPageKeepingView();   // the button was disabled at the click
        }

        async Task AskAndUpdateAsync(ModuleEntry e, RegistryVersion update)
        {
            bool asksMore = e.Manifest is { } m && !RegistryDeclares.Of(m).Covers(update.Declares);
            if (asksMore)
            {
                var root = Host.Window?.Content?.XamlRoot;
                if (root == null) return;
                var body = new StackPanel { Spacing = 6, MaxWidth = 460 };
                body.Children.Add(Ui.Text(Host.T("modules.update-more-body"), "RbSecondaryText", wrap: true));
                body.Children.Add(Ui.Text(DeclaresSummary(update.Declares), weight: FontWeights.SemiBold, wrap: true));
                var dialog = new ContentDialog
                {
                    XamlRoot = root, Title = Host.F("modules.update-more-title", e.DisplayName, update.Version), Content = body,
                    PrimaryButtonText = Host.F("modules.update-to", update.Version), CloseButtonText = Host.T("approval.cancel"), DefaultButton = ContentDialogButton.Close,
                    RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { Host.Window.RefreshPageKeepingView(); return; }
            }
            await _m.UpdateAsync(e.Id);
        }

        static async Task Safe(Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { AppLog.Error("modules page action failed", ex); }
        }
    }
}
