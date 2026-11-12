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
            ToolTipService.SetToolTip(openFolder, AppPaths.ModulesRoot);   // the path is a fact for whoever asks, not the page's subtitle (MD-1)
            var check = Ui.LinkButton(_m.IsChecking ? Host.T("modules.checking") : Host.T("modules.check"), () => _ = Safe(InstallServices.CheckNowAsync));
            check.IsEnabled = !_m.IsChecking;
            AutomationProperties.SetName(check, Host.T("modules.check"));
            bool welcome = Host.Core.Modules.Count == 0 && _m.Snapshot.Document is null && _m.Snapshot.Problem is null;
            // the first start: one block that says what Rigbay is for and gets the list (FR-1); the header buttons would only repeat it
            page.Children.Add(welcome ? Ui.PageHeader(Host.T("nav.modules"), null) : Ui.PageHeader(Host.T("nav.modules"), HeaderLine(), openFolder, check));

            AddBanners(page);
            if (welcome) { AddWelcome(page); }
            else
            {
                // nothing installed yet but the list is here: the list comes first, an empty "Installed" section says nothing the header does not
                if (Host.Core.Modules.Count > 0) AddInstalled(page);
                AddAvailable(page, first: Host.Core.Modules.Count == 0);
            }

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

        // ---------------------------------------------------------------- header line and first start

        /// <summary>"5 installed · list checked 12 Nov, 14:02" (MD-1): what is here and how fresh the list is. The folder is behind "Open folder".</summary>
        string HeaderLine()
        {
            var parts = new List<string> { Host.F("modules.count-installed", Host.Core.Modules.Count) };
            var s = _m.Snapshot;
            if (s.Document is not null && s.FetchedAt is { } at && s.Problem is null && s.Status == RegistryStatus.Fresh)
                parts.Add(Host.F("modules.count-checked", ShortWhen(at)));
            return string.Join("  ·  ", parts);
        }

        /// <summary>"12 Nov, 14:02" in the language the app shows (the date order of that language).</summary>
        static string ShortWhen(DateTimeOffset at)
        {
            var culture = UiCulture();
            var local = at.ToLocalTime();
            var day = local.ToString(culture.DateTimeFormat.MonthDayPattern.Contains("MMMM") ? culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM") : "d MMM", culture);
            return $"{day}, {local.ToString("t", culture)}";
        }

        internal static CultureInfo UiCulture()
        {
            try { return CultureInfo.GetCultureInfo(Host.Core.Languages.CurrentCode); }
            catch (CultureNotFoundException) { return CultureInfo.CurrentCulture; }
        }

        /// <summary>
        /// The first start, nothing installed and the list never asked for (FR-1): what Rigbay is for, the privacy sentence and one action. When the list
        /// arrives the page shows it straight away.
        /// </summary>
        void AddWelcome(StackPanel page)
        {
            var block = new StackPanel { Spacing = 10, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            var title = new TextBlock { Text = Host.T("modules.welcome-title"), Style = Ui.Style("SubtitleTextBlockStyle"), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            block.Children.Add(title);
            block.Children.Add(Ui.Text(Host.T("modules.welcome-body"), wrap: true));
            block.Children.Add(Ui.Text(Host.T("modules.none-body"), "RbSecondaryText", wrap: true));
            var get = new Button { Content = Host.T(_m.IsChecking ? "modules.checking" : "modules.get-list"), Style = Ui.Style("AccentButtonStyle"), IsEnabled = !_m.IsChecking, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(get, Host.T("modules.get-list"));
            get.Click += (_, _) => { get.IsEnabled = false; _ = Safe(InstallServices.CheckNowAsync); };
            block.Children.Add(get);
            page.Children.Add(block);
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

        /// <summary>Width kept for the on/off switch at the right of an installed row, whether or not the row has one.</summary>
        const double SwitchSlot = 52;

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

            // the right side: a link to the module's page, the switch, or what is waiting for the restart (the progress has its own line)
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            // a removal waiting for the restart: the module still runs until then, but the switch must not suggest it will stay (E2E-07)
            bool removalQueued = row.Pending is { Op: PendingKinds.Remove };
            // navigation belongs on the row, not at the bottom of the details (MD-3)
            if (e.Page != null || e.Lifecycle != ModuleLifecycle.Invalid)
            {
                var open = new HyperlinkButton { Content = Host.T("modules.page-link"), Padding = new Thickness(6, 4, 6, 4) };
                AutomationProperties.SetName(open, Host.F("modules.page-link-of", e.DisplayName));
                ToolTipService.SetToolTip(open, Host.T("modules.open-page"));
                open.Click += (_, _) => Host.Window.Navigate(e);
                right.Children.Add(open);
            }
            if (working) { /* the progress has its own line under the description (E2E-08) */ }
            else if (row.Pending is { } pending)
                right.Children.Add(Btn(Host.T("modules.undo"), () => _m.UndoAsync(e.Id), Host.F("modules.undo-of", e.DisplayName)));
            else if (row.Update is { } update)
                right.Children.Add(Btn(Host.F("modules.update-to", update.Version), () => AskAndUpdateAsync(e, update), Host.F("modules.update-of", e.DisplayName)));
            // a switch only where it can be used: a failed, unsupported or incompatible module has none, the state word says why (MD-3)
            // the slot keeps its width without a switch, so the links and buttons of every row line up
            var slot = new Grid { MinWidth = SwitchSlot };
            if (removalQueued || e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running) slot.Children.Add(Switch(e, removalQueued).With(s => s.HorizontalAlignment = HorizontalAlignment.Right));
            right.Children.Add(slot);

            // the row says the state and its reason; what the module is (its description) is in the details, once (MD-2)
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
                description = reason.Length > 0 ? $"{word}  ·  {reason}" : word;

            if (working)
            {
                var lines = new StackPanel { Spacing = 4 };
                lines.Children.Add(description is string text ? Ui.Text(text, "RbSecondaryCaption", wrap: true) : (UIElement)description);
                lines.Children.Add(ProgressPanel(e.Id, e.DisplayName));
                description = lines;
            }
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

        ToggleSwitch Switch(ModuleEntry e, bool removalQueued = false)
        {
            // while a removal is queued the switch shows "off" and cannot be moved; Undo draws the row again with the real state
            bool canToggle = !removalQueued && e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running;
            var sw = new ToggleSwitch { IsOn = !removalQueued && e.Lifecycle == ModuleLifecycle.Running, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = canToggle };
            AutomationProperties.SetName(sw, Host.F("module.enable", e.DisplayName));
            bool suppress = false;
            sw.Toggled += async (_, _) =>
            {
                if (suppress) return;
                sw.IsEnabled = false;
                try { bool now = await ModulePage.ToggleAsync(e, sw.IsOn); if (now != sw.IsOn) { suppress = true; sw.IsOn = now; suppress = false; } }
                catch (Exception ex) { AppLog.Error("enable switch failed", ex); }
                finally { sw.IsEnabled = canToggle; }
            };
            return sw;
        }

        static void AddFacts(SettingsExpander expander, ModuleEntry e)
        {
            var m = e.Manifest;
            var facts = new FactSheet();
            facts.Add(Host.T("fact.description"), e.DisplayDescription);
            facts.Add(Host.T("fact.id"), e.Id);
            facts.Add(Host.T("fact.author"), e.AuthorText());
            facts.Add(Host.T("fact.group"), m != null ? Host.T("group." + e.GroupKey()) : "");
            facts.Add(Host.T("fact.host-api"), m?.HostApi.Text ?? "");
            facts.Add(Host.T("fact.admin"), m == null ? "" : Host.T(m.Requires.Admin ? "value.yes" : "value.no"));
            facts.Add(Host.T("fact.network"), m == null ? "" : string.Join("\n", m.Requires.Network));
            facts.Add(Host.T("fact.registry"), m == null ? "" : string.Join("\n", m.Requires.Registry));
            facts.Add(Host.T("fact.services"), m == null ? "" : string.Join("\n", m.Requires.Services));
            facts.Add(Host.T(ModuleManifest.OutsideFilesLabelKey), string.Join("\n", m?.DeclaredProbePaths ?? Array.Empty<string>()));
            facts.Add(Host.T("fact.problems"), string.Join("\n", e.IssueLines()));
            facts.Add(Host.T("fact.folder"), e.Folder);
            if (m?.Homepage is { } home && Uri.TryCreate(home, UriKind.Absolute, out var uri))
                facts.Add(Host.T("fact.homepage"), new HyperlinkButton { Content = home, NavigateUri = uri, Padding = new Thickness(0) });
            expander.Items.Add(facts.Card());
        }

        // ---------------------------------------------------------------- available

        void AddAvailable(StackPanel page, bool first = false)
        {
            page.Children.Add(new TextBlock { Text = Host.T("modules.available"), Style = Ui.Style("RbGroupHeader"), Margin = new Thickness(1, first ? 4 : 20, 0, 4) });
            var snap = _m.Snapshot;
            var line = RegistryLine(snap);
            // a fresh list is already said in the header line (MD-1); the line stays for every state that needs saying (saved, offline, unreachable)
            if (line != null && !(snap.Document is not null && snap.Problem is null && snap.Status == RegistryStatus.Fresh && !_m.IsChecking))
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

            var description = new StackPanel { Spacing = 1, Tag = "available-description:" + module.Id };
            // the whole description, wrapped (E2E-08: it used to be cut to one line to make room for the progress, which now has its own line below)
            description.Children.Add(Ui.Text(module.Description, "RbSecondaryCaption", wrap: true).With(t => t.TextTrimming = TextTrimming.None));
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
                case AvailableState.Incompatible:
                    // only the revision is too low (the module uses something added in 0.1.x): say which Rigbay it needs, not a range (R2)
                    status = version?.HostApiRange.NeededRevision(HostApi.Current) is { } needed
                        ? Host.F("modules.needs-host-revision", needed.ToString(3))
                        : Host.F("modules.needs-host", version?.HostApi ?? "?");
                    break;
            }
            if (status != null)
            {
                var t = Ui.Text(status, error ? "RbErrorText" : "RbSecondaryCaption", wrap: true);
                if (failure != null) ToolTipService.SetToolTip(t, failure.Detail);
                description.Children.Add(t);
            }

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            if (working) description.Children.Add(ProgressPanel(module.Id, module.Name).With(p => p.Margin = new Thickness(0, 4, 0, 0)));
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
            var facts = new FactSheet();
            facts.Add(Host.T("fact.id"), module.Id);
            facts.Add(Host.T("modules.version"), v.Version);
            facts.Add(Host.T("modules.released"), v.Released.ToString("d", UiCulture()));
            facts.Add(Host.T("modules.size"), FormatSize(v.Size));
            facts.Add(Host.T("fact.host-api"), v.HostApi);
            facts.Add(Host.T("fact.admin"), Host.T(v.Declares.Admin ? "value.yes" : "value.no"));
            facts.Add(Host.T("fact.network"), string.Join("\n", v.Declares.Network));
            facts.Add(Host.T("fact.registry"), string.Join("\n", v.Declares.Registry));
            facts.Add(Host.T("fact.services"), string.Join("\n", v.Declares.Services));
            facts.Add(Host.T(ModuleManifest.OutsideFilesLabelKey), string.Join("\n", v.Declares.OutsideFiles));
            facts.Add(Host.T("modules.works-here"), Host.T("modules.works-here-unknown"));
            if (Uri.TryCreate(module.Repo, UriKind.Absolute, out var repo) && repo.Scheme == Uri.UriSchemeHttps)
                facts.Add(Host.T("modules.source"), new HyperlinkButton { Content = module.Repo, NavigateUri = repo, Padding = new Thickness(0) });
            facts.Add(Host.T("modules.sha"), v.Sha256);
            expander.Items.Add(facts.Card());
        }

        // ---------------------------------------------------------------- pieces

        StackPanel ProgressPanel(string id, string name)
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
                PrimaryButtonText = Host.T("modules.remove"), CloseButtonText = Host.T("approval.cancel"), DefaultButton = ContentDialogButton.None, PrimaryButtonStyle = Ui.Style("AccentButtonStyle"),
                RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
            };
            FocusCancelOnOpen(dialog);
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
                    PrimaryButtonText = Host.F("modules.update-to", update.Version), CloseButtonText = Host.T("approval.cancel"), DefaultButton = ContentDialogButton.None, PrimaryButtonStyle = Ui.Style("AccentButtonStyle"),
                    RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
                };
                FocusCancelOnOpen(dialog);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { Host.Window.RefreshPageKeepingView(); return; }
            }
            await _m.UpdateAsync(e.Id);
        }

        /// <summary>
        /// DL-1: the action wears the accent and Enter must still be safe. With no default button a dialog puts the focus on its first button (the
        /// action), so the focus is moved to Cancel when it opens. The same rule as package C's Dialogs.Confirm; call that instead once both are on main.
        /// </summary>
        static void FocusCancelOnOpen(ContentDialog dialog)
        {
            void Try(int attempt)
            {
                var close = SelfTest.Find<Button>(dialog).FirstOrDefault(b => b.Name == "CloseButton" && b.Visibility == Visibility.Visible);
                if (close != null && close.Focus(FocusState.Programmatic)) return;
                if (attempt < 8) dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Try(attempt + 1));
                else AppLog.Warn($"dialog '{dialog.Title}': Cancel could not take the focus");
            }
            dialog.Opened += (_, _) => Try(0);
        }

        static async Task Safe(Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { AppLog.Error("modules page action failed", ex); }
        }
    }
}

/// <summary>
/// The facts of a module as one compact card (MD-2): a two-column grid, the label (160 px, secondary ink) left and the value (selectable, wraps) right,
/// instead of one full-height card per fact. Empty values are left out.
/// </summary>
sealed class FactSheet
{
    public const double LabelWidth = 160;
    readonly Grid _grid = new() { ColumnSpacing = 16, RowSpacing = 8 };

    public FactSheet()
    {
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition());
    }

    public int Count => _grid.RowDefinitions.Count;

    public void Add(string label, string value)
    {
        if (value.Length == 0) return;
        var tb = Ui.Text(value, wrap: true);
        tb.IsTextSelectionEnabled = true;
        tb.VerticalAlignment = VerticalAlignment.Top;
        Add(label, tb);
    }

    public void Add(string label, FrameworkElement value)
    {
        int row = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = Ui.Text(label, "RbSecondaryText", wrap: true);
        l.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(l, row);
        _grid.Children.Add(l);
        value.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetLabeledBy(value, l);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        _grid.Children.Add(value);
    }

    /// <summary>The grid inside a card, the way an expander item is drawn.</summary>
    public SettingsCard Card() => new()
    {
        Content = _grid, ContentAlignment = ContentAlignment.Vertical, HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = "facts",
    };
}
