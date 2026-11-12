# Changelog

All notable changes to the Rigbay app (`rigbay-app/rigbay`). Modules are released from their own repositories and have their own history.
All releases are pre-releases (major version 0). The files are not code-signed.

## 0.1.2 (2026-11-13)

A larger release than 0.1.1: the window was redesigned after a UI/UX review, readings became visual, and the app now tells you about every hardware change. The module contract grew by additive members (host API 0.1.3, see below), so the module releases that go with it need **Rigbay 0.1.2 or newer**.

### What you will notice

- **A new shell.** Navigation is a menu with a button that works at every width (a window snapped to 640 px still reaches every page), there is no status bar any more (versions are in Settings > Troubleshooting), and a module's item in the pane has a dot only when it needs you: red for failed, an attention dot for an update or a restart. The old green dot on every running module is gone because a signal that is always on says nothing.
- **Dashboard.** First the machine, "This PC now": one tile per key reading of the running modules. Below it one slim row per module. Failed modules lead.
- **Module pages.** One header card (icon, name, state with the module's own live line, the on/off switch, Learn more, Log), then a main card with the module's main control first, the reading tiles that are also the tabs of the trend chart, and the rows. Rows that say nothing are not drawn; a failed module gets one sentence, "Restart and try again" and "View log".
- **Log page.** A list instead of a text dump: filter by level (all, warnings and errors, errors) and by source (host or one module), an icon on warning and error lines, time and source in their own columns. Copy still gives the raw lines.
- **Modules and Settings.** The Modules page has a summary line and "Check for modules"; Settings is grouped by intent and "Start with Windows" shows no status beside its switch unless something went wrong.
- **Dialogs.** The title of a confirmation is the change itself, the focus starts on Cancel (Enter never confirms by accident), the shortcut-capture dialog works from the keyboard and names the current shortcut, and messages appear in the window as a notice that a screen reader announces.
- **Wording.** Plainer English and a re-read of the Vietnamese texts.
- **Chart axis.** The time ticks of the trend chart are distinct; focus goes to the new button when a module relabels the primary one (the Recorder's Start becomes Stop); the header card and rows stay readable in a narrow window.

### Readings, notifications, contract

- **Readings are visual.** Every reading row has a sparkline; a module page with readings starts with up to four tiles (big number, unit, trend, a thin meter for temperatures and percents) and one trend chart
  (tabs per group, 1 / 5 / 10 minutes, crosshair and tooltip, legend for several lines); the Dashboard bay rack shows a sparkline beside each key reading. Every module gets it without a module release; modules can steer it with
  the optional `ReadingRow.Tile`, `ChartGroupKey`, `RangeMin` and `RangeMax` (host API stays 0.1, additive). The host keeps the last 10 minutes of every metric in memory (about 1.8 MB at most, never on disk, cleared when a metric is withdrawn).
- **A hidden window costs nothing for the page that was open.** The open page now drops its bus subscriptions and its redraw timer while the window is hidden to the tray or minimised, so modules see no reader and slow down (before, an open page kept them polling).
- **Every hardware change, refusal and failure now tells the user (N1).** One rule for all modules (`docs/APP.md`, "Notifications"): after a change the user confirmed, the result is shown where they are looking. A click on a module page with the window in front shows a message at the top of the window, no toast; a hidden or minimised window, a hotkey, a tray item, a command or the XTU auto-switch gives a Windows notification; a failure or refusal always does. New `ToastMessage.Origin` (additive; old modules keep working). XTU: apply, auto-switch on and off, profile per power source, auto-switch applying on a power change, busy and already-active. PredatorSense: fan mode (page, revert, hotkey, command-line verb) with the speed-check result in the message.
- **Notifications from the elevated app really go out.** `AppNotificationManager` does nothing in an elevated process (`IsSupported()` is false, `Show` returns without a toast), so 0.1.1 showed no toast at all although the log said `toast: registered`. Rigbay now shows them as balloons from its tray icon and logs every decision and what Windows answered. Focus assist / Do not disturb can still hold them back; `docs/USER-GUIDE.md` says so.
- `tools\selftest.ps1 -AllowMulti` runs the self-test next to an already running Rigbay (its "second start shows the first" check then reaches the running one, not its own).

- **Host API 0.1.3.** Additive members for module pages (`PageModel.Primary`, `HeaderRow`, `ChartRole`, `Warn`, `Secondary`, `ConfirmKey`, `HelpKey`, `Line`), on top of `ToastMessage.Origin` (revision 1) and the `ReadingRow` hints (revision 2). A module's `hostApi` range may now name a revision (`>=0.1.3 <0.2`); **0.1.2 and newer hosts refuse a module that needs a revision they lack, 0.1.0 and 0.1.1 ignore the revision** (so the module releases that use 0.1.3 are published only together with this host). Old modules keep working.
- **Search** finds a module's main control and every row's name, description and help text, not only the group names.
- **Accessibility work** in the new shell and dialogs: names for the rows, tab order, focus on Cancel, a live region for notices. It was checked with the UI Automation tree and the self-test, not with Narrator.
- **Tools.** `tools\selftest.ps1 -AllowMulti` (above); `tools\shots.ps1 -ShotMode rtb` renders screens while the display sleeps; `tools\new-module.ps1` creates a module from the sample; the E2E scripts follow the new wording. Flaky tests found under CPU stress were fixed (the sample module's timer, the Recorder's real-clock test, PredatorSense waits).

### Modules that go with this release

Each is released from its own repository and needs **Rigbay 0.1.2 or newer**: XTU profiles 0.1.1, PredatorSense 0.1.2 (shows the performance mode as read-only text; it still never writes a mode), Steam access 0.1.2, Recorder 0.1.1 and Remote tunnel 0.1.1. They are not in this zip; install them from the Modules page.

### Not verified in 0.1.2

High contrast, Narrator, and a real 150 % and 200 % display scale (only 100 % was driven). The notification banner and the tray balloon were never seen on a real screen (the log says what Windows answered). Windows 10, ARM, a PC without the prerequisites and a real sign-in were not tried. The files are not signed.

## 0.1.1 (2026-11-02)

Fixes for what the first-hour end-to-end run found against 0.1.0 (`docs/E2E.md`, section 6). No new features, no change to the module contract (host API stays 0.1).

- **Accessible names for the Installed rows (E2E-03).** The rows of the Modules page's Installed list had no name for a screen reader (their header is an icon and text); each row is now named after its module, like the Available rows.
- **Removal message shows the display name (E2E-04).** After a restart that removed a module, the Modules page said "recorder was removed." (the id); it now says "Recorder was removed.".
- **A queued removal no longer looks like the module stays (E2E-07).** The row's switch is shown off and cannot be moved while the removal waits for the restart; Undo gives the real state back.
- **A downloading row keeps its whole description (E2E-08).** The progress bar and Cancel moved to their own line under the description instead of squeezing it to a cut line.
- **Long text on module pages (E2E-06).** A text box (for example a folder path) now grows with the page, from 280 to 560 px, and has a tooltip with the whole text; read-only values and the status text next to a control wrap instead of being cut with "...".
- **No warning on the first start (E2E-09).** A fresh install without a modules folder used to log `WARN ... does not exist`. The host now creates the folder with the same administrators-only permissions the installer uses, and logs one INFO line; if it cannot, it logs one INFO line that says what to do.
- Test and tool fixes: the language-file test skips admin-only and build-output folders, so `dotnet test` also runs from a normal shell while scratch folders exist (E2E-05). The end-to-end scripts in `tools\e2e` are part of the repository.

Not part of the app, so not in this zip: the Recorder's default folder is under the real Documents whatever `--data` says (E2E-02, still open in the module), and the PredatorSense fan box reading "Auto" before anything was set (E2E-10) is fixed in the module's source on `main` (PS011, module version 0.1.1); that module is released from its own repository.

## 0.1.0 (2026-11-01)

The first pre-release: the WinUI 3 shell that installs, runs and supervises modules.

- Host app, unpackaged, runs as administrator, Mica window with a light and a dark theme; English and Vietnamese UI from language files.
- Module loading in-process with a fault boundary (a module that throws is turned off and logged), a probe per module ("does this machine support me"), settings, hotkeys, tray items, command-line verbs (`rigbay <module> <verb>`), toasts, a log, and a metrics bus.
- Approval dialog before every hardware write, with the exact change, and Revert for what can be reverted.
- Modules page: browse the registry (`rigbay-app/registry`), install, update, roll back, remove, with SHA-256 verification, an administrators-only modules folder, what a module declares it uses shown before it is turned on, and a restart to apply.
- `install.ps1` and `uninstall.ps1` (Program Files, Start menu, optional start-at-logon task, Uninstall entry), and a reproducible release zip made by `tools\package-app.ps1`.
- Framework-dependent: needs the .NET 10 runtime and the Windows App Runtime 1.8.

Known and not done in 0.1.0: nothing was verified on Windows 10, on ARM, on a PC without the prerequisites, or with a real logon; see the release notes.
