# Changelog

All notable changes to the Rigbay app (`rigbay-app/rigbay`). Modules are released from their own repositories and have their own history.
Both releases are pre-releases (major version 0). The files are not code-signed.

## Unreleased

- **Readings are visual.** Every reading row has a sparkline; a module page with readings starts with up to four tiles (big number, unit, trend, a thin meter for temperatures and percents) and one trend chart
  (tabs per group, 1 / 5 / 10 minutes, crosshair and tooltip, legend for several lines); the Dashboard bay rack shows a sparkline beside each key reading. Every module gets it without a module release; modules can steer it with
  the optional `ReadingRow.Tile`, `ChartGroupKey`, `RangeMin` and `RangeMax` (host API stays 0.1, additive). The host keeps the last 10 minutes of every metric in memory (about 1.8 MB at most, never on disk, cleared when a metric is withdrawn).
- **A hidden window costs nothing for the page that was open.** The open page now drops its bus subscriptions and its redraw timer while the window is hidden to the tray or minimised, so modules see no reader and slow down (before, an open page kept them polling).
- **Every hardware change, refusal and failure now tells the user (N1).** One rule for all modules (`docs/APP.md`, "Notifications"): after a change the user confirmed, the result is shown where they are looking. A click on a module page with the window in front shows a message at the top of the window, no toast; a hidden or minimised window, a hotkey, a tray item, a command or the XTU auto-switch gives a Windows notification; a failure or refusal always does. New `ToastMessage.Origin` (additive; old modules keep working). XTU: apply, auto-switch on and off, profile per power source, auto-switch applying on a power change, busy and already-active. PredatorSense: fan mode (page, revert, hotkey, command-line verb) with the speed-check result in the message.
- **Notifications from the elevated app really go out.** `AppNotificationManager` does nothing in an elevated process (`IsSupported()` is false, `Show` returns without a toast), so 0.1.1 showed no toast at all although the log said `toast: registered`. Rigbay now shows them as balloons from its tray icon and logs every decision and what Windows answered. Focus assist / Do not disturb can still hold them back; `docs/USER-GUIDE.md` says so.
- `tools\selftest.ps1 -AllowMulti` runs the self-test next to an already running Rigbay (its "second start shows the first" check then reaches the running one, not its own).

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
