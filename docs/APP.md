# Rigbay.App: the WinUI 3 shell

`src/Rigbay.App` is the Rigbay application: an unpackaged, elevated WinUI 3 program that uses `Rigbay.Core` (task A2, see
`docs/ABSTRACTIONS.md` section 16) to find, load and supervise modules and draws their pages. The look follows
`docs/UI-DIRECTION.md`; the spike that proved the approach is `docs/spike/SPIKE.md`.

## Build, run, test

All scripts are in `tools\` and work from any folder. Nothing here installs anything; it needs the .NET 10 SDK and the
Windows App Runtime 1.8, both present on this machine.

| Script | What it does |
| --- | --- |
| `tools\build.ps1` | Publishes the app to `tools\out\app` (framework-dependent, 38 MB) and lays out the demo modules in `tools\out\modules` and `tools\out\modules-test`. |
| `tools\run-elevated.ps1 [-Wait] [app args]` | Starts the app elevated (UAC auto-elevates for this account) with its own data folder and the demo modules, so your real `%LOCALAPPDATA%\Rigbay` and modules folder stay untouched. |
| `tools\stop-elevated.ps1` | Stops every `Rigbay.App` (an elevated process cannot be stopped from a normal shell, so it relaunches itself elevated). |
| `tools\selftest.ps1 [-AllowMulti]` | Runs the in-app self-test inside an elevated process (about 170 checks, 16 of them install checks on a scratch copy of the demo modules and a local registry), prints the report. Exit code 0 when all pass. `-AllowMulti` runs it next to a Rigbay that is already running; the pipe and single-instance checks are then skipped (they would reach the other instance). |
| `tools\shots.ps1 [-ShotMode rtb] [name...]` | Renders the window itself (PrintWindow of the app's own HWND, never the screen) into `tools\shots\*.png`: dashboard, empty, failed, unsupported, module page on/off, approval dialog, Vietnamese, modules, settings, log, narrow; dark and light; and `hero-dark` for the README, the `visual-*` shots of V1, the `ub-*` module page shots of the UI review, the `released-*` pages of the released modules on made-up machines (when `tools\out\modules-released` was built) and the README animation `visual-live.gif` (frames of the window put together by ffmpeg when it is on the PATH; the script never installs it). `-ShotMode rtb` renders with RenderTargetBitmap instead (see `--shot-mode`): use it while the display sleeps, when PrintWindow gives black images. It works in `C:\Rigbay-demo` (`-Demo <dir>` changes it; a copy of the demo modules and one data folder per shot), so no developer path, user name or machine name is drawn; the app makes that folder admin-only, so delete it with an elevated shell. |
| `tools\cli-test.ps1` | Command-line verbs (headless and forwarded to a running instance) and `--langcheck`. |
| `tools\schtask-test.ps1` | "Start with Windows": creates the task through the app's own code, runs it with `schtasks /run`, checks, deletes it. Refuses to run if a task named `Rigbay` already exists. |
| `tools\sync-brand-assets.ps1 [-Check]` | Copies the brand files the app carries from `assets\brand` (list: `src\Rigbay.App\Assets\brand-files.txt`); `-Check` only compares and fails on a difference (`build.ps1` runs it). |
| `tools\tray-mock.ps1` | Draws `tools\shots\tray-mock.png`: the tray icons by taskbar theme and size, and the menu model (a mock, not the real taskbar). |
| `tools\make-local-registry.ps1`, `serve-registry.ps1` | A scratch modules folder and a local registry built from the demo modules (an update, an installed one, an installable one, a wrong hash, another host API); a loopback HTTP server for it. For rendering and testing install; never the real modules folder (`docs/INSTALL.md` section 8). |
| `tools\shots-install.ps1` | The Modules page in every install state, dark and light, English and Vietnamese, into `tools\shots\install-*.png`. Those images are not kept in the repository: the script works under `tools\out`, so the Modules page would print a developer path. Point it at a neutral scratch folder (as `shots.ps1` does with `C:\Rigbay-demo`) before keeping its output. |
| `tools\install-http-test.ps1`, `tools\restart-test.ps1` | Install over real HTTP on loopback with the start-up swap, offline with a cache and `icacls` of the real folders; "Restart now" with two real elevated processes. |
| `tools\remove-elevated.ps1` | Deletes the admin-only scratch folders those scripts leave under `tools\out` (an elevated PowerShell; refuses anything else). |

`dotnet build Rigbay.sln` also builds the app (0 warnings, 0 errors) and `dotnet test Rigbay.sln` still passes the Core tests (600 now, after R1).
Demo modules (`tools\demo-modules`) are test fixtures, not product: "Fan studio" talks to a pretend device kept in memory and
shows the whole confirm, apply, read-back, revert flow; the others are unsupported, incompatible, invalid and failing
modules, plus three that misbehave on purpose (self-test only).

## Where things are

| What | Where |
| --- | --- |
| Log (rotating, 1 MiB, 3 files; also the Log page) | `%LOCALAPPDATA%\Rigbay\logs\rigbay.log` |
| Host state (enabled modules, language) | `%LOCALAPPDATA%\Rigbay\host.json` (written by the core) |
| App settings (theme, log level, modules whose declarations you accepted) | `%LOCALAPPDATA%\Rigbay\app.json` |
| Hotkey rebinding | `%LOCALAPPDATA%\Rigbay\hotkeys.json` |
| Module settings and data | `%LOCALAPPDATA%\Rigbay\modules\<id>\` |
| User language files (add a language or override keys) | `%LOCALAPPDATA%\Rigbay\lang\*.lang` |
| Modules | `%ProgramData%\Rigbay\modules\<id>\` (default). Since R1 the installer creates it, and `%ProgramData%\Rigbay` and the install folder, with an ACL that lets only Administrators and SYSTEM write (`docs/INSTALL.md` section 3); nothing is created until the first install or registry check. |
| Install work folder | `%ProgramData%\Rigbay\modules.install\` (next to the modules folder, never inside it): downloads, staging, `ready`, `previous`, the registry cache, `pending.json`, `last-run.json`. Admin-only. |
| Host language files | `lang\en.lang`, `lang\vi.lang` (embedded in the exe and copied next to it for `--langcheck`) |

`--data <dir>` and `--modules <dir>` move the first set and the modules folder; the scripts use them.

## Command line

```
Rigbay.App.exe                       the UI
Rigbay.App.exe --tray                the UI, hidden in the tray (what "start with Windows" runs)
Rigbay.App.exe <module> <verb> ...   headless; forwarded to a running instance when there is one
Rigbay.App.exe list | version        modules and their state / versions
Rigbay.App.exe --version             "Rigbay <version>" and exit
Rigbay.App.exe --langcheck           the core's LangCheck over the host's files and every module's
Rigbay.App.exe --uninstall-cleanup [--keep-task]   quit a running instance, remove the logon task (install/uninstall scripts)
```

Exit codes follow the contract: 0 ok, 1 failed, 2 not found or bad usage; text goes to stdout (stderr when not 0). Switches such
as `--out <file>`, `--data`, `--modules` must come **before** the verb words; everything after the first word belongs to the verb.
The exe is a GUI-subsystem program: it attaches to the parent console if there is one, and `--out <file>` always works (use it
from PowerShell, which does not wait for GUI programs, or when the elevated process cannot reach your console).

A headless run loads the modules, turns on only the one the verb needs (and only if the user had enabled it), runs the verb and
stops it. A hardware change asked for in a headless run is refused (no window to ask in).

### Every switch

**Elevation.** `app.manifest` asks for `requireAdministrator`, so **every** start of `Rigbay.App.exe` is an elevated process, whichever
switch it carries, including `--version` and `--help` (Windows asks, or auto-elevates for an administrator account that has UAC set so).
The last column therefore says what a switch needs *beyond* that: "no" means the switch does nothing that depends on the elevated
token; "yes" means it acts on something only an elevated process may change.

| Switch | Value | What it does | Needs the elevated token itself |
|---|---|---|---|
| *(none)* | | Opens the UI. A second start only brings the running window forward. | yes (modules run as administrator; the modules folder is admin-only) |
| `--tray` | | UI hidden in the tray. What the "Start with Windows" task runs. | yes (same) |
| `<module> <verb> [args]` | | Headless verb; forwarded to the running instance when there is one. `list`, `version`, `help` are answered by the host. | yes for a verb that touches hardware (and those are refused headless anyway) |
| `--version` | | Prints `Rigbay <version>` and exits (install scripts, bug reports). | no |
| `--help`, `-h` | | One-line usage and exit. | no |
| `--langcheck` | | `LangCheck` over the host's files and every installed module's; exit 1 on errors. | no |
| `--uninstall-cleanup` | | Asks the running instance to quit (waits up to 30 s), then deletes the logon task through the code that created it. Leaves modules and settings alone. | yes in practice (the task has the highest run level, and the process is elevated anyway) |
| `--keep-task` | | With `--uninstall-cleanup`: only the quit, keep the task (an upgrade). | no |
| `--task-name` | `<name>` | Use another name for the logon task (letters, digits, `.`, `_`, `-`, up to 60 characters). Tests only: keeps the real task `Rigbay` out of reach. | same as the task it names |
| `--data` | `<dir>` | Moves the settings, log and cache folder (default `%LOCALAPPDATA%\Rigbay`). | no |
| `--modules` | `<dir>` | Moves the modules folder (default `%ProgramData%\Rigbay\modules`). | yes for the default folder (admin-only) |
| `--out` | `<file>` | Also writes a headless run's output to a file. Before the verb words. | no |
| `--registry` | `<file\|url>` | Test only: a local registry. Ignored unless `--data` and `--modules` are scratch folders (`docs/INSTALL.md`). | no |
| `--slow-download` | `<ms>` | Test only, with `--registry`: waits before each 16 KB read, so progress and cancel can be seen. | no |
| `--first-run` | | Test only: behave as the first start without modules (opens the Modules page). | no |
| `--wait-pid` | `<pid>` | Set by "Restart now": the new process waits for that process to end first. | no |
| `--allow-multi` | | Test only: skip the single-instance check. | no |
| `--theme` | `dark\|light\|system` | Test and screenshots: force the theme. | no |
| `--lang` | `<code>` | Test and screenshots: start in this language. | no |
| `--page` | `<id>` | Test and screenshots: open this page first. | no |
| `--script` | `"step;step"` | Test and screenshots: drive the UI (steps below). | no |
| `--shot` | `<png>` | Render the window's own HWND (PrintWindow) to a PNG; never the screen. | no |
| `--shot-mode` | `printwindow\|rtb` | `printwindow` (default) as above. `rtb` renders the window content with RenderTargetBitmap at 1x (or at `--shot-scale`), open dialogs and flyouts laid over it; no Mica, no caption buttons. Use it when PrintWindow returns a black image, which it does while the display is asleep. | no |
| `--shot-scale` | `1.5\|2` | Render the shot as if the display scale were this (RenderTargetBitmap of the window content). | no |
| `--size` | `WxH` | Window size for a shot. | no |
| `--accent` | `RRGGBB` | Test: pretend the Windows accent colour is this (title bar mark). | no |
| `--time-scale` | `<n>` | Screenshots only: the metrics clock (sample times, the history, the charts) runs n times faster than real time, so 52 s of waiting shows five minutes of history. | no |
| `--trim-delay` | `<seconds>` | Test only: how long the window stays hidden before the memory trim runs, instead of the Settings value (default 60 s). | no |
| `--trim-steps` | `all\|none\|drop,caches,gc,ws` | Test only: which steps of the memory trim run (`tools\perf\measure.ps1` measures them one by one). Default `ws`: the working set trim, the one step that paid. | no |
| `--exit-after` | `<ms>` | Quit after this long (shots and tests). | no |
| `--selftest` | | Run the in-app self-test. | yes (it checks the admin-only folders and elevated-only behaviour) |
| `--selftest-out` | `<file>` | Where the self-test writes its report. | no |

`--script` steps: `enable:id`, `disable:id`, `page:id`, `lang:vi`, `theme:dark`, `edit:<row header key>[=<value>]` (no value: press the button),
`revert:<row header key>`,
`approve:true|false|ask`, `cancel-dialog`, `startup:on|off`, `turn-on:id`, `expand:id`, `expand-all`, `scroll:px`, `wait:ms`, `cmd:<verb words>`,
and the install steps `check`, `install:<id>`, `install-bg:<id>`, `update:<id>`, `remove:<id>`, `rollback:<id>`, `undo:<id>`, `cancel-install`,
`restart` (`docs/INSTALL.md` section 8). V1 adds `hide` and `show` (what the close button and the tray do: the window goes to the tray and comes back), `mem:<label>` (logs working set, private bytes, managed heap, threads, handles and CPU time as a `MEM` line), `trim` (the memory trim now, the window must be hidden), `cpu:<label>` (logs this process's CPU time and the
time spent redrawing, as a `CPUMARK` line, so a script can measure a stretch of waiting), `chart:<n>` (pick a chart tab, 0-based), `range:<1|5|10>` (minutes), `hover:<0..1>` (put the crosshair at that share of the plot width) and
`frames:<count>x<ms>` (PNG frames of the window's own HWND into `<--shot name>-frames`, for the README animation). The UI review's package C adds
`capture:<shortcut row header key>[=<chord>]` (opens that row's "Press the new shortcut" dialog, not awaited; with `=Ctrl+Alt+M` that chord is shown as
already pressed) and
`notice:<module id>,<success|info|warning|error>,<title key>,<body key>` (the in-window message as that module's toast shows it while the window is in use).
Package A adds `pane:open|close` (the menu button), `tab-order` (logs the open page's tab stops with their names), `search:<text>` (types into
the search box and logs the results), `search-open:<title>` (opens a result as a click does and logs where the focus landed),
`log-filter:all|warnings|errors` and `invoke:<accessible name>` (presses that button through UI Automation). `focus:<accessible name>` puts
keyboard focus on that control of the open page (a picture of where the focus goes after `invoke:`), `axis` logs the trend chart's time and
value labels, and `shot:<suffix>` takes one more PNG at that moment, `<--shot name>-<suffix>.png`, the way `--shot` is taken (a long page shot
at the top, then again after `scroll:`; the window is never taller than the screen).

**A row whose header is a literal.** In `edit:`, `revert:` and `capture:` the first single `=` separates the row's header key from the value,
and a `=` that belongs to the key is written twice. A module-made name (`Text.Literal`, a key that starts with `=`) is therefore written with
`==` in front: the Remote tunnel row `=example-tunnel` is `edit:==example-tunnel=true`, its Revert `revert:==example-tunnel`. The value is
the rest of the step as written (it may hold `=`, but cannot start with one).

## What it does

- **Shell.** Mica window, 48 px title bar with the search box (Ctrl+F or F3), `NavigationView` (Dashboard; up to 8 modules as one flat
  list, more in their manifest groups; at the bottom Modules, Settings, Log), a sheet with a hairline and 8 px top corners at most 1000 px
  wide. There is no status bar: the versions are in Settings > Troubleshooting. A module's item has a dot only when it asks for attention
  (red: failed; attention dot: an update or a restart), never for "running". Labels from 1000 px, an icon rail below that, and the menu
  button is there at every width (a window snapped to 640 px still reaches every page); the window is at least 500 px wide. "Not running
  as administrator" is a bar above the page, only when it is true. Picking a setting in the search results opens its page at that row
  (expander opened, row focused). The close button hides to the tray when the tray icon exists (Quit is in the tray menu); a second start
  brings the first window forward (named mutex plus a pipe).
- **Dashboard.** First the machine, "This PC now": one tile per key reading of the running modules, each figure once. Then the bay rack:
  one slim row per module with its bay mark, state line and fixed reading slots for key readings without a tile; a reading that is not
  available is left out. Failed modules lead the summary. No modules: an honest empty state with one button.
- **Module page** (`docs/UI-DIRECTION.md` section 9). The **header card**: the module's tile and name, a state dot with the state word
  and the module's live line, the on/off switch with its state in words, Learn more and Log small at the top right (under the name when the
  card is narrower than 560 px, so a long link does not squeeze the name and state), and once for the page
  what the shield after a row's name means (the first time the switch is turned on, a dialog lists what the module declares it uses).
  The **main card**: the module's `PageModel.Primary` large (a short choice as a segmented control), then the reading **tiles, which are
  the tabs of the trend chart** under them (1, 5 or 10 minutes; Compare adds the readings of the same kind). Then the module's groups as
  `SettingsCard` / `SettingsExpander` rows (a row that says nothing is not drawn; a short choice in a row is a segmented control while that
  leaves the row's name 200 px, else a drop-down; the host's own switches take the cards' switch style, so every switch on the page ends at
  the same x) and **All readings**, folded. A failed module gets one
  sentence, "Restart and try again", "View log" and its error under Details. A turned-off module keeps its rows, greyed out. Every
  value goes through the core's `ValueEditor`: apply-or-revert rows keep a draft with Apply and Discard; rows that change hardware
  show the exact change ("Fan mode: Auto → Maximum") in a confirmation, then show the value the module reports afterwards and a
  Revert. A custom control (level 2) is created inside the module's boundary and type-checked.
- **Modules.** The subtitle is a summary ("5 installed · list checked ..."; the folder path is the tooltip of Open folder). Every module
  with state, switch and the manifest's facts as a compact list (admin, network, registry, services, "Reads files outside its folder" (one
  line per `probePaths` entry, also in the turn-on dialog), host API, folder, problems); a failed, unsupported or incompatible module has no
  switch, its state word says why, and the row links to its page. Since R1 the page also installs, updates, removes and rolls back (next bullet).
- **Modules: install, update, remove (R1).** Two lists. **Installed**: a flat row per module (state bar, icon tile, name and version, state and reason),
  the switch, **Update to X** when the registry has a newer release that fits this host, **Undo** while a change waits, and in the details the manifest's
  facts, **Go back to X** (a module that failed after an update) and **Remove** (with a confirmation). **Available**: the registry's modules, each with its
  description, version, size and a one-line summary of what it declares ("Needs administrator · Network (1) · Registry (2)"); the details have the full
  declarations, host API, release date, SHA-256, the source repository link and "Works on this PC: not known until it is installed". Honest states in
  the row: installable, installed (with the version), waiting for a restart, needs another host API, failed with the reason. While one runs: a progress bar
  with the phase and "16 KB / 23.9 KB" and a Cancel button. A registry line says whether the list is fresh, saved on this PC, **offline** (showing the
  list saved on that date), unreachable, or not valid. **Restart required** is an amber bar with **Restart now** and a dot on Modules in the pane. The page
  redraws itself when something changes, with scroll, open rows and focus kept; progress ticks only move the bar. The first start with no module at all
  opens this page once, with "Check for modules". Short copy, no cards, language keys `modules.*` and `decl.*` in en and vi, errors `core.install.*` in Core.
  How it works, what it protects and what not: `docs/INSTALL.md`; the list and its rules: `docs/REGISTRY.md`.
- **Settings.** Grouped by intent. General: language (switches at once, module strings too), theme (system, light, dark), start with
  Windows, **check for module updates every day (off by default; asks github.com once a day, sends no identifier)**. Hotkeys: the shortcuts
  of all modules, changeable here, conflicts named. Troubleshooting: log level, open the log folder, **Version info** (Rigbay, host API,
  Windows App SDK, .NET, Windows, every module; no user or machine name) with **Copy version info** for a bug report. About.
- **Log.** The last 500 entries as a list, not a text dump: a level filter (all, warnings and errors, errors) and a source filter (the
  host or one module), a glyph on error and warning lines, time and source in their own columns; Copy puts the shown lines on the
  clipboard in the log file's format.
- **Search.** A runtime index of module names and descriptions, the primary row and every row's header, description and help, in the current language.
- **Tray, hotkeys, toast.** Tray menu: Open, one submenu per module that registered items, Quit; the icon's tooltip names the first two
  running modules' state ("Balanced · CPU 82 °C"). Hotkeys: `RegisterHotKey` on the
  window, only for gestures nobody else claims; a gesture the OS refuses is flagged. Notifications: see "Notifications" below.
- **Page refresh and literal text (A8, used since A7).** The app subscribes once to `RigbayHost.PageRefreshed`. When the open page
  belongs to that module it is rebuilt in place: scroll offset kept, open expanders kept (matched by header), focus given back to the
  control with the same type and name (a button that disables itself while it works has its focus remembered at click time), swapped
  and restored inside one UI callback so nothing is painted in between. A control of the primary block is looked for in the new primary
  block, and when the module relabelled it (the Recorder's Start becomes Stop) the new primary control takes the focus; keyboard focus
  comes back as keyboard focus. The search index is rebuilt either way. Text that starts
  with `=` (`Text.Literal`) is resolved by `entry.Localisation`, so pane items, tray items, hotkey names (also on Settings) and choice
  labels show it without the `module.<id>.` prefix; nothing in the app prefixes keys by hand.
- **Expander items.** `SettingsExpander` styles every item as a `SettingsCard`; an `InfoBar` in it threw when the expander was opened.
  The renderer now wraps anything that is not a `SettingsCard` in one.
- **Fault boundary.** `Application.UnhandledException` is kept (`Handled = true`, logged); when the stack shows a module's code that
  module is marked faulted. Updates that come from a module thread go through `Ui.Post`, which runs inside that module's
  boundary. A module is never given a raw dispatcher.

## Approved plans (H1)

A module may ask the owner, once, to approve an exact list of steps (module, verb, arguments) and then run them with nobody at the window (Game scenes). `ApprovalPrompt` draws the
question when `ApprovalRequest.Plan` is set: the title names the scene ("Let *Demo scene* run these steps by itself?"), one block per step (the command line in bold, what it changes, the
exact values the verb says it will use, how it is undone), the caption says every run shows a notification and the approval can be revoked, the primary button counts the steps
("Approve 2 step(s)") and keyboard focus starts on Cancel. A run's notification is a tray balloon raised by the host (`core.plan.*` texts). The list with a Revoke button for the
approvals (`RigbayHost.Plans.AllApprovals()`) is not in the Settings page yet (that page is being changed elsewhere): until then the owner revokes with `rigbay plans revoke <module>/<plan>`,
on the module's own page, or by disabling the module. The approvals live in `%ProgramData%\Rigbay\plans\<user SID>` (admin-only); the self-test uses `<data folder>\plans` and removes it with `tools\remove-elevated.ps1`.

## Notifications (N1)

**One rule for every module.** After a hardware change the user confirmed, the result is shown where they are looking.

| The change came from | Window in use (visible, not minimised, in front) | Window hidden, minimised, in the tray or behind another one |
|---|---|---|
| A click on the module's page | A message at the top of the window (`InfoBar`, floats over the page, closes by itself after 6 s; 12 s for a failure). **No toast.** | A Windows notification |
| A hotkey, tray item, command-line verb, auto-switch (power change, resume) | A Windows notification | A Windows notification |
| Any failure or refusal (`ToastKind.Warning` or `Error`), whatever the origin | A Windows notification **and** the message in the window | A Windows notification |

Quiet by design: one message per change; nothing for the periodic reading or a refresh; nothing when the user said no in the approval
dialog (they know). A module that finds nothing to change when the user asked (the profile is already active) says so; the automatic
rule stays silent in that case. Auto-switch applying limits by itself always raises a notification, because nobody clicked.

How it is built: the module says what happened and what caused it, `host.Toast.Show(new ToastMessage(title, body, kind) { Origin = ToastOrigin.Page })`
(default `Background`). `Rigbay.Core` (`NotificationPolicy.Decide`, a pure function with its own tests) chooses the place from the kind, the
origin and `RigbayHost.WindowInUse` (the app keeps it from the window's visibility, minimised state and activation); the app draws the
in-window message (`Services/InAppNotice.cs`, the bar `NoticeBar` in `MainWindow.xaml`) and the notification (`Services/ToastHost.cs`). Without a window
(command-line mode, tests) everything is a notification. Every decision is a log line (`notice [kind, origin, window in use] -> toast / in-window`),
and `ToastHost` logs what Windows did with it, so "no notification appeared" can be answered from `rigbay.log`.

**Found on 2026-11-12: `AppNotificationManager` does nothing in an elevated process.** Rigbay's manifest asks for administrator rights, and
in such a process `AppNotificationManager.IsSupported()` is false, `Setting` is `Unsupported`, `Register()` returns without an error, and
`Show()` returns without an error and without a notification (the notification keeps `Id` 0; `GetAllAsync()` returns null). Checked with a
scratch probe (the same package, 1.8.260921001) run elevated and with the app's own self-test. Until now the app counted such a call as "shown";
that is why the owner saw nothing from 0.1.1 although the log said `toast: registered`. So an elevated Rigbay shows notifications as
**balloons from its tray icon** (`Shell_NotifyIcon` with `NIF_INFO`), which Windows 10 and 11 draw as ordinary toast banners. This needs no
registration and no app id. Windows reports back to the icon's callback: `NIN_BALLOONSHOW` when it takes the balloon and `NIN_BALLOONTIMEOUT`
when it is gone; both are logged (`tray balloon: ...`). Clicking a balloon opens the window. The `AppNotificationManager` path is kept for a
process that is not elevated.

**What can still hide a notification, and what Rigbay cannot see.** Focus assist / Do not disturb, and the user's own notification settings,
decide whether a banner pops up; a balloon sent during Focus assist goes quietly to the notification list (if at all). Rigbay cannot read that
state reliably. That is also why the in-window message exists and why a failure is shown both ways. Rigbay does not change any Windows
notification setting.

**Verified, and not.** Verified: the routing for every case (unit tests with fake sinks), the in-window message (self-test, and a PNG of the
window's own render), `Shell_NotifyIcon` accepting the balloon and Windows answering `NIN_BALLOONSHOW` (self-test, elevated, 4 runs). **Not verified:
a banner on the screen.** On the author's machine, during the test runs, a banner-window detector (windows of the shell host, titles only; no screen
capture) saw no banner, not even for a notification sent from a known-good classic app (Windows PowerShell's own app id), the balloon's
`NIN_BALLOONTIMEOUT` followed `NIN_BALLOONSHOW` after about 30 ms, and the Focus assist state value read from Windows was 1 (not off; how
to read that value was taken from community notes, not documentation). All of that fits "Focus assist or Do not disturb was on", which would
hold back every banner on that machine, and it is not proof of it. Before relying on the notification, the owner should look at Settings >
System > Notifications.

## Brand in the app (A11)

The kit is `assets/brand` (guidelines `docs/BRAND.md`, hand-over `assets/brand/integration/README.md`). The app carries **copies** of the files it needs
(`src\Rigbay.App\Assets\brand-files.txt` lists them); `tools\sync-brand-assets.ps1` copies them, `-Check` fails when one differs, `tools\build.ps1`
runs the check, and `ModuleIconTests` checks the same list, so the masters in `assets/brand` stay the single source. The placeholder
`tools\make-icon.ps1` (three bays, the last one green; green is a state colour) is deleted.

| Where | What | How |
| --- | --- | --- |
| exe, taskbar, Start, window | `rigbay.ico` (16 to 256 px) | `ApplicationIcon` in `Rigbay.App.csproj` and `AppWindow.SetIcon`. |
| Tray | `rigbay-tray-light.ico` on a light taskbar, `rigbay-tray-dark.ico` on a dark one, `rigbay-tray-neutral.ico` when the theme cannot be read, `rigbay-hc-black.ico` / `rigbay-hc-white.ico` in high contrast (black for a light theme) | `TrayHost.Pick` chooses from `HKCU\...\Themes\Personalize\SystemUsesLightTheme` (the taskbar's theme, not `AppsUseLightTheme`) and `AccessibilitySettings.HighContrast`. The frame is the size Windows wants on the primary monitor (`GetSystemMetricsForDpi(SM_CXSMICON)`: 16, 20, 24, 32 at 100, 125, 150, 200 %). `WM_SETTINGCHANGE` with `ImmersiveColorSet` (or high contrast), `WM_THEMECHANGED`, `WM_DISPLAYCHANGE`, `WM_DPICHANGED`, a `UISettings` colour change and `TaskbarCreated` call `TrayHost.Refresh`, which puts the new icon on the shell (`NIM_MODIFY`) before it destroys the old handle. The tray is not drawn with the Windows accent (the shell draws it). |
| Title bar | The Dock mark, small cut, 20 DIP, as XAML paths in a `Viewbox` (`Brand\RigbayMark.xaml`, a copy of `integration/xaml/RigbayMark.xaml`; used as `Path.Data="{StaticResource ...}"`, which WinUI accepted) | Bay in the text colour, block in `AccentFillColorDefaultBrush` (the Windows accent; high contrast comes from the theme resources). When the accent's hue is within 30 degrees of the state green (154), red (358) or amber (37) and its saturation is at least 0.12, the **quiet form** is shown instead (bay slate `#7B8497`, block in the text colour), see `BrandMark.NeedsQuietForm`. `UISettings.ColorValuesChanged` re-evaluates at run time. A vector in a `Viewbox` is sharp at every scale, so no raster is used. |
| Module icons | The manifest field `icon` (`docs/ABSTRACTIONS.md` section 2.1) | Core validates the files and says which one fits (`ModuleIcons.Choose`); `Ui\ModuleIconUi.cs` draws them. Dashboard row: tile, 24 DIP, between the state bar and the name. Navigation pane: quiet glyph, 16 DIP (`ImageIcon`); a module without icon files keeps the one-character icon its page names, else the neutral "?" glyph. Module page header: tile, 48 DIP. Modules page: tile, 32 DIP. An unsupported or incompatible module uses its `icon-unsupported.svg` (the tile in grey); without that file its own icon is drawn at 45 % opacity. No icon, a bad icon (warning `core.manifest.icon-*`) or a file that cannot be decoded: the neutral "?" tile from `Assets\modules\unknown`; a decode failure is logged once (`icon of <id>: ... could not be drawn`) and never throws. |

**SVG or PNG: SVG.** WinUI's `SvgImageSource` draws the brand's SVGs correctly (checked: all tiles, glyphs and grey variants in `tools\shots\*.png`, and the self-test counts that
every image opened). `RasterizePixelWidth/Height` is set to `DIP x display scale`, so a tile is rasterised at the pixels it needs and stays sharp at 150 and 200 %; the scale is
re-read when `XamlRoot.Changed` fires. The PNG sets are shipped and used as the fallback (`ModuleIcons.Choose(preferSvg: false)`: the smallest PNG with enough pixels) if SVG ever fails
to open on a machine (`ModuleIconUi.SvgBroken`); that path is covered by the Core tests, not exercised in the app because SVG never failed here.

New test switches: `--accent <RRGGBB>` (pretend the Windows accent is this colour, for the title bar mark) and `--shot-scale <1.5|2>` (render the shot as if the display scale were 150 % or 200 %:
a `RenderTargetBitmap` of the window's own content, with icons picked and rasterised for that scale; the Mica backdrop and the system caption buttons are not part of it). `tools\tray-mock.ps1`
draws a mock of the notification area (icons by taskbar theme and size, the menu model) into `tools\shots\tray-mock.png`.

## Start with Windows

A scheduled task `Rigbay`: logon trigger for this user, `RunLevel HighestAvailable` (no prompt), `--tray`, 15 s delay, and, unlike
the `schtasks /create` defaults, *not* "do not start on battery" and *not* "stop on battery" (a laptop app that silently does not
start when unplugged is no use). The Settings switch creates and removes it; it is written as XML for those settings.

## Windows App SDK line

Pinned in `src\Rigbay.App\Rigbay.App.csproj` (the root `Directory.Build.props` belongs to A2): **1.8.260921001** with
`CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219. Why 1.8: the spike ran it as its main line and everything passed;
2.6.4 also ran but its very first start took 2.4 to 3.6 s. A module with a custom control binds to the same line, so changing it
is a host API decision.

**Deployment: framework-dependent only.** It needs the Windows App Runtime 1.8 and .NET 10 installed (an installer step).
A self-contained build (`WindowsAppSDKSelfContained=true`, 214 MB) was tried in the real project on 2026-11-01 and crashed at
start 3 of 3 times: exit code -1073741189 (0xC000027B), fault module `Microsoft.UI.Xaml.dll` offset 0x3a7515, the same failure
the spike saw once. Cause not found, so the script has no self-contained switch.

## Verification (2026-11-01, this machine, Windows 11 10.0.26300, 100 % DPI)

Evidence is in `tools\shots\`: `selftest.txt` (113 checks, all PASS), `cli-test.txt`, `schtask-report.txt`, `dotnet-build.txt`, and
the PNGs (each one was looked at).

- Clean `dotnet build Rigbay.sln` (Release, `--no-incremental`): 0 warnings, 0 errors; Debug the same. `dotnet test`: 240 passed (2026-11-01, after A7).
- Self-test, elevated (`IsUserAnAdmin=True`, runtime from `C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.1.8_8000.994.2142.0`):
  shell up, module states (off, unsupported, incompatible, invalid, faulted), dashboard rows and readings, level 1 page (12 rows into
  10 cards and an expander), level 2 control, search, the hardware flow (declined, approved, read-back that differs from what was typed,
  revert, the module's own refusal, the row's range, a safe row that does not ask, a button with revert), a turned-off page, turning
  on again, hotkey (a real `SendInput` chord reaches the module), tray model and callback, toast, commands (exit 0, 1, 2 and over the
  pipe), language switch to Vietnamese and back with an open page redrawn, seven fault cases (Initialize throws, `RunOnUi` action throws,
  value handler throws on edit, button throws, XAML handler throws, `async void` throws, a throwing UI update) with the process alive
  after all of them, second start shows the first.
- Scheduled task: created by the app's own code, XML checked, `schtasks /run` started the process in session 1 with the tray
  icon added, the running instance answered a forwarded command, removed by the app's own code, no task and no process left.

### Not verified

A real logoff and logon (so also the 15 s delay and whether Explorer is ready; the tray icon is re-added on `TaskbarCreated` and
retried for a minute, which is untested); 150 % and 200 % DPI; high contrast (the reading hues have a high-contrast entry that
was never rendered); real mouse and keyboard clicks (the self-test drives the same code the controls call, and the approval
dialog and the shortcut-capture dialog were seen only in a screenshot, never clicked); a notification actually appearing on screen
(the 2026-11-01 run took `Show` returning without error for a toast; it was not one, see "Notifications"); the tray menu on screen (the menu model and callbacks are tested, not the native popup); contrast
measured as numbers (judged by eye on the PNGs); screen readers and keyboard-only use; the pane in the compact state beyond one
screenshot; a machine without the Windows App Runtime; more than 8 modules; a module built against another Windows App SDK
line; Windows 10.

## Contract gaps found while building (all closed by A4, adapted in A5)

A3 reported seven gaps in the Core and worked around them. A4 changed the Core (`docs/ABSTRACTIONS.md` section 17); A5 deleted the workarounds:

| Gap | A3 workaround | Now |
| --- | --- | --- |
| Approval dialog had no exact value | `AsyncLocal` `ApprovalPrompt.WithDetail` | `ValueEditor.ApplyAsync(detail)` / `SubmitAsync(value, detail)` and `RowEditors.InvokeButtonAsync(..., detail)`; the core hands it to `RigbayHost.RequestApprovalTextAsync`. |
| Editors never disposed | one editor per row in a `ConditionalWeakTable` | `RowEditors.For` caches one per row instance and the entry disposes them. The table that remains only keeps each row's last status text. |
| `string` converted to `LocalisedText` | casts in the sample | no implicit conversion; `LocalisedText.Of`. The fan-studio demo module is adapted. |
| Page dropped on disable | `PageCache` | `ModuleEntry.LastPage` (dashboard, module page, search). |
| `F12` accepted as a hotkey | flag in the UI | rejected by `HotkeyGesture`; nothing to flag. |
| Manifest problems in English inside a localised sentence | none | `ManifestIssue` is a key plus arguments; `ResolveReason` localises. The dashboard shows "4 problems in module.json" (one line), the module page shows the explanation and one localised line per problem, the Modules page lists the same lines. |
| `version` unknown to a running instance | headless-only branch in `Cli` | `ExecuteCommandAsync` answers `version` and `list` (`id State version enabled|disabled [reason]`), headless and forwarded alike. |

Dashboard rows are one line: name, then state and a short reason (`ShortReasonText`, trimmed with an ellipsis, full text as the
tooltip). Long reasons (a failed module's exception text) belong on the module page.

Still open: `<module> help` exits 2 with the verb list (the usage rule).

Since A7: the bare `list` verb is answered by the app (`AppCommands.List`), same columns as the core's, but the reason of an invalid
manifest is its first problem plus "(+N more in the log)"; the full text is in the log (written when the manifest is read) and on the
module page. Other verbs are still the core's.

## A9 follow-ups (task A10)

The A9 changes in the Core (`docs/ABSTRACTIONS.md`, "0.1 after A9") needed these App changes; each one is covered by self-test checks.

- **Revert is asked.** The Revert link of a value row calls `ValueEditor.RevertAsync(StopToken)` (never the synchronous `Revert()`, which
  now refuses hardware rows) and a button row calls `RowEditors.RevertButtonAsync`. The status line shows `EditResult.Message`
  ("Reverted: Fan mode is now Auto (was Maximum)"); a declined revert writes nothing, keeps the Revert link and says so. A button's
  Revert link uses `ButtonRow.RevertButtonKey` as its label. `HardwareEffect.SafeRevert` rows ask nothing and the status line names
  what was written.
- **Approval dialog** (UI review package C, DL-1 and DL-2). The title is the change, built from the detail the host makes for a row ("Fan mode: Auto → Maximum"
  becomes "Set fan mode to Maximum?"; a revert, `ApprovalRequest.IsRevert`, "Revert fan mode to Auto?"); then the module's effect sentence; then one caption
  "module · now Auto · you can revert it on the page". The primary button names the change ("Set to Maximum", "Revert to Auto") and wears the accent;
  there is no default button and focus starts on Cancel (`Dialogs.Confirm`, also used by the turn-on and capture dialogs), so Enter alone never writes.
  A detail in another shape (a module-started write with its own words) keeps the general title "Change your hardware?" / "Revert this hardware change?"
  with the detail as a bold line. Keys `approval.*` (en, vi).
- **System-setting wording (F1).** `HardwareEffect.Kind = EffectKind.SystemSetting` (`ApprovalRequest.Kind`) gives the general titles "Change a Windows setting?" /
  "Revert this Windows setting?" and the row line "Changes a Windows setting. Rigbay asks first."
  (`approval.title-setting`, `approval.revert-title-setting`, `row.system-note`; en, vi). The demo module has a "Web proxy" row for it; shots
  `approval-system-{dark,light,vi-dark}`, `revert-system-{dark,light}`. **`TextRow`** is a box at the right of the label, 280 px in 0.1.0 and since 0.1.1 half of the row between 280 and 560 px with the whole text as a tooltip (it used to take the width of its
  text, so a long path squeezed the label and description): see `textrow-tall-{dark,light}`, `textrow-narrow-dark`.
- **Page visibility.** `MainWindow.UpdatePageVisibility` calls `entry.SetPageVisible(true)` for the module page that is open while the
  window is shown, `false` for the one that was left, and `false` when the window is hidden to the tray or minimised (it runs on
  navigation, `AppWindow.Changed`, show and hide). `MainWindow.ShownEntry` is what the self-test reads.
- **Readings.** `ReadingRow` and the dashboard bay both format through `ReadingFormat`: a number with its unit, or "Not available"
  (`core.reading.unavailable`, secondary colour) when there is no sample or `MetricSample.IsAvailable` is false. The live subscription
  handles the withdraw notice and the value that comes back. The bay takes its readings from `entry.GetSummaryReadings()` (label from the
  module's descriptor, else the host's `metric.<id>` text, else the raw id); the descriptor's `FormatHint` gives the decimals (none = 0).
- **Demo module.** Fan studio gained a `Status light` (hardware choice, `SafeRevert`), a `Beacon` button (`SafeRevert`), a revert
  description and label on the existing rows, a `Pump flow` metric with a descriptor ("Pump") that the command `demo-fan sensor off|on`
  withdraws and publishes again, and `page-visible=` in `demo-fan status`.
- **Test switches.** `revert:<row header key>` (what the Revert link does), `approve:ask` (show the real dialog again after
  `approve:true`), `cmd:<module> <verb> [args]`. `tools\shots.ps1` has revert, unavailable and dashboard-unavailable shots (dark and light).

Verified 2026-11-01: `dotnet build Rigbay.sln --no-incremental` 0 warnings 0 errors, `dotnet test` 285 + 89 passed, `tools\selftest.ps1`
93 of 93 PASS (elevated; run three times, one earlier run had a single failure of the `SendInput` hotkey check, 0 hits, that did not
come back), PNGs looked at: `revert-dark/light/vi-dark/button-dark`, `unavailable-dark/light`, `dashboard-dark/light`,
`dashboard-unavailable-dark/light`.

Not verified: a real mouse click on Revert (the self-test drives the same code the link calls, the dialog was seen only in a PNG);
window minimised by the user (only hide-to-tray was exercised; the minimise check reads `OverlappedPresenter.State`); the
`SafeRevert` status line in a PNG (checked as text in the self-test); high contrast and 150 % / 200 % DPI.

## A11: the brand in the app (task A11)

Verified 2026-11-01 (this machine, Windows 11 10.0.26300, 100 % DPI): `dotnet build Rigbay.sln --no-incremental` 0 warnings 0 errors; `dotnet test`: 343 (Core, 16 of them new for icons and the brand copies)
and 102 (PredatorSense) passed; `tools\selftest.ps1` 113 of 113 PASS (elevated; includes tray swaps for all five looks, a real `WM_SETTINGCHANGE`, the mark's geometry, the accent rule, and
every module icon opening or falling back); `tools\cli-test.ps1` and `--langcheck` unchanged. PNGs looked at (dark and light): `dashboard-*`, `modules-*`, `module-on-*`, `unsupported-*`,
`mark-*` (blue, violet, green, red, amber accents), `scale150-*`, `scale200-*`, `tray-mock.png`; the exe's own icon extracted from `Rigbay.App.exe` (violet tile with the Dock).

Things seen in the renders: this machine's Windows accent is `#00B7C3` (hue 183), 29 degrees from the state green, so the real title bar shows the **quiet form**; that is the rule working, and
`--accent` shows the normal form. A Windows "green" accent such as `#107C10` (hue 120) is 34 degrees from the state green and keeps the normal form by the guide's 30 degree rule; `#10893E` (143) and
the Windows red, gold and orange accents go quiet.

### Not verified

The real tray at high DPI and on a real taskbar (the icons load at 16, 20, 24 and 32 px through `LoadImage`, `NIM_MODIFY` is accepted, but the shell's own drawing was not looked at; `tray-mock.png`
is a mock from the brand PNGs); a real change of the taskbar theme or of high contrast (the look was forced and a `WM_SETTINGCHANGE` with `ImmersiveColorSet` was sent to the window, the registry was
not touched); a real change of the Windows accent while the app runs; real 150 % and 200 % scaling (the `--shot-scale` renders simulate it, the window was never on a scaled monitor); Explorer
thumbnails and the Start menu entry; the SVG-to-PNG fallback inside the app (only the Core choice is tested); a symbolic link inside a module folder (rejected by code, not tested because creating one
needs a privilege).

## R1: install, update and remove from the Modules page (task R1)

The Modules page now installs, updates, removes and rolls back modules from the registry `rigbay-app/registry`, with a SHA-256 check before anything is
unpacked, admin-only folders, a swap at the next start and a kept previous version. Design, flow, crash recovery, threat model and the full verification are in
`docs/INSTALL.md`; the registry file and its rules are in `docs/REGISTRY.md`.

Verified 2026-11-01 (this machine, Windows 11 10.0.26300, 100 % DPI): `dotnet build Rigbay.sln --no-incremental` 0 warnings 0 errors; `dotnet test`: Core 600
(257 new), PredatorSense 102, XTU 190, all passed; `tools\selftest.ps1` 129 of 129 PASS (elevated; 16 install checks through the real page controls and the real
DACL of scratch folders); `tools\cli-test.ps1` 0 failed, `--langcheck` 209 of 209 keys in en and vi; real HTTP on loopback with a 304 and an offline
fallback, and "Restart now" with two real elevated processes (`tools\install-http-test.ps1`, `tools\restart-test.ps1`). PNGs looked at (dark and light, en and vi):
`install-modules-*`, `install-http-*`, `install-settings-install-dark`.

Not verified: a real download from GitHub; the real `%ProgramData%\Rigbay` folder (scratch folders with the same code were); a UAC prompt on Restart now;
150 % and 200 % scale and high contrast on the new page; a screen reader; Windows 10; the registry workflow's pull-request step; a full disk; an
antivirus holding a file during the swap. The list is in `docs/INSTALL.md` section 10.

## V1: the readings made visual (task V1)

Owner feedback from the real machine (2026-11-12): the XTU page was a long list of text rows, "no charts, too much text, nothing visual". Before V1 the metrics bus kept only the latest sample and nothing in the app drew a chart.
Everything below is host-side, so every module gets it without a release. The design, with the reasons (tile instead of a round gauge, the colour rules, the honest scale), is `docs/UI-DIRECTION.md` section 8;
the contract (optional hints on `ReadingRow`, the history) is `docs/ABSTRACTIONS.md` 5.7 and 6.

- **History.** `MetricsBus.History` (`MetricHistory`, `src/Rigbay.Core/Registries/MetricHistory.cs`): a ring per metric, 10 minutes in at most 300 points (2-second slots, averaged), 256 metrics at most (about 1.8 MB worst case),
  fed by `Publish`, cleared by a withdraw, memory only. It is not a subscriber, so it never keeps a module polling. A module that sleeps while nobody looks leaves a gap, which the charts show as a gap.
- **Where the visuals are** (`src/Rigbay.App/Ui`): `Sparkline.cs` (row, tile and bay rack), `ReadingTile.cs`, `TrendChart.cs` (tabs per group, 1 / 5 / 10 minutes, crosshair, tooltip, legend, keyboard: arrow keys, Home, End),
  `ReadingBlock.cs` (what sits above the rows; rebuilt when a metric's unit becomes known), `ChartKit.cs` (hues, `TileStrip` that wraps 4, 2 or 1 columns, `FlowPanel`, the fade-in), and the pure rules in Core
  (`ReadingPlan`, `ReadingScale`). All drawn with stock shapes (`Polyline`, `Path`, `Ellipse`, `Line`) on canvases; no new package. Brushes come from styles (`RbLine*`, `RbFill*`, `RbDot*`, `RbKey*`, `RbSeries1..6Brush` in `Styles.xaml`),
  because a brush fetched from code follows the app theme and not the window's.
- **Nothing runs while the page is not on screen.** `PageScope` now has an active state (`MainWindow.UpdatePageVisibility` sets it from the window: shown and not minimised). While inactive the page drops every bus subscription of its readings
  (so `SubscriberCount` falls and modules slow down, which the self-test reads with `demo-fan`) and its redraw timer stops; both come back, refreshed, when the window is shown. The redraw timer runs every 2 s (the history's slot).
  Before V1 the open page kept its subscriptions while the window sat in the tray, so modules kept polling for nobody.
- **Motion.** One 160 ms fade-in of the block, only when Windows "Animation effects" are on (`UISettings.AnimationsEnabled`); no tweening of numbers, no animated lines.
- **High contrast.** All hues become the window text colour and the lines are told apart by dash patterns (`ChartKit.Dashes`). Written, not seen: no high contrast theme was switched on.
- **Demo module.** Fan studio publishes `cpu.power`, `cpu.usage`, `cpu.clock` and `cpu.power.pl1` too, so its page has four tiles and five chart tabs; `PL1 limit` and `Pump flow` have `Tile = Never` (the hint at work).

Self-test (`tools\selftest.ps1`, run with `--allow-multi` here because the owner's own Rigbay was running): the `visual:` checks cover a sparkline in every reading row (drawn from the history, with a live end dot), 2 to 4 tiles with their
sparklines and a meter, the tiles before the rows, the trend chart with its time axis and tabs, the hover tooltip with time and value, the keyboard crosshair, the power tab with two lines (legend), one unit per chart, the tile columns,
the history cleared by a withdraw, the Dashboard bay rack sparklines, **nothing drawn, no timer and no subscription while the window is hidden** (and all back when it is shown), and no tiles or chart on a page without readings.

### CPU use (measured 2026-11-12, this machine, `cpu:` marks inside the elevated app, demo-fan page open, 100 % DPI)

Process CPU time over a stretch of waiting, as a share of one core; two rounds each. "Before" is `main` at 3707459 with only the measuring steps added (3 metrics from its demo module), "after" is V1 (7 metrics from its demo module, which publishes at 1 Hz whether or not anybody looks).

| State | Before | After |
| --- | --- | --- |
| Window hidden in the tray, module page open (60 s) | 0.13 %, 0.72 % | 0.16 %, 0.08 % |
| Window visible, module page open (45 s) | 0.45 %, 0.93 % | 2.2 %, 3.4 % |
| Window visible, Dashboard open (30 s) | 0.26 %, 0.72 % | 0.56 %, 0.56 % |

Hidden costs nothing extra: the redraw counter stays at 52 across the whole hidden minute, and the self-test shows 0 subscriptions and no timer. The run-to-run noise is as large as the difference (the "before" hidden rounds differ 5 times). Visible, the page costs about 2 % of a core more
for 11 sparklines, 4 tiles and a chart (our drawing code itself is about 6.6 ms per 2-second tick, 0.3 %; the rest is WinUI laying out and compositing the changed shapes). The demo module's own timer is in every number.

### Not verified

High contrast (written and reviewed, no high contrast theme was switched on); the real XTU or PredatorSense page (this ran on the demo modules; adopting hints in those modules is the follow-up after N1 merges); a real mouse hover (the tooltip was driven by the same code path, and by `hover:` for the PNGs); a screen reader reading the chart name; real 150 % / 200 % monitors (`--shot-scale` renders only);
Windows "Animation effects" off (the code reads `UISettings.AnimationsEnabled`, the setting was not toggled); a 10-minute-old history on a real 1 Hz source (the PNGs use `--time-scale 6`, so their lines have a point every 6 s); the two self-test checks `command: forwarded to the running instance over the pipe` and `single instance` fail
when another Rigbay is already running (one was, on this machine), because the pipe and the mutex belong to it; they are not touched by V1.

## 0.1.2: the release (task R3, 2026-11-13)

What the shell looks like since the UI review (packages A, B, C and the module pages): see "What it does" above. Three changes that older text of this file may still describe differently: the **status line** at the bottom is gone (versions are under Settings > Troubleshooting); a module's **green dot** is gone from the navigation pane (a dot means a failed module, an update or a restart, never "running"); the **Log** page is a filtered list (level and source) with an icon on warning and error lines, and its Copy button gives the lines in the log file's format.

The app version is `<Version>` in `Directory.Build.props` (0.1.2); host API 0.1.3 (`HostApi.Revision` 3). Modules whose `hostApi` is `>=0.1.3 <0.2` (the 0.1.2 module releases) need a host that has revision 3. Hosts 0.1.0 and 0.1.1 ignore the revision and would offer them, so they are released only after this host and say "needs Rigbay 0.1.2 or newer".

Not verified in 0.1.2: high contrast, Narrator, a real 150 % and 200 % display scale, a notification banner or tray balloon on a real screen, Windows 10, a real sign-in after the upgrade of the installed copy; the README pictures are the earlier renders (`tools\shots`).

## M2: memory in the tray (2026-11-13)

Setting **Release memory in the tray** (Settings > General, on by default, delay 30 s to 10 min, default 60 s). When the window has been hidden for the delay and is still hidden, the app takes the process out of RAM once (`EmptyWorkingSet`): about 205 MB become 30 to 45 MB in the tray, 25 ms to the first frame when the window comes back, 70 MB three seconds later. Private bytes (about 150 MB, native) do not change. The policy is `Services\TrayTrimPolicy.cs` (unit-tested with a fake clock), the work is `MainWindow.Trim.cs`; a `TRAYTRIM` line in the log says what each trim did. Dropping the page, clearing caches and a forced GC were measured and did not pay, so they are off; they stay behind the test switches `--trim-steps` and `--trim-delay` and the script steps `mem:<label>` and `trim`, and `tools\perf\measure.ps1` repeats the measurement. Numbers, method and what did not help: `docs/perf/TRAY-MEMORY-2026-11.md`.
