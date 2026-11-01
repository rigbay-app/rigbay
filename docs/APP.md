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
| `tools\selftest.ps1` | Runs the in-app self-test inside an elevated process (63 checks), prints the report. Exit code 0 when all pass. |
| `tools\shots.ps1 [name...]` | Renders the window itself (PrintWindow of the app's own HWND, never the screen) into `tools\shots\*.png`: dashboard, empty, failed, unsupported, module page on/off, approval dialog, Vietnamese, modules, settings, log, narrow; dark and light. |
| `tools\cli-test.ps1` | Command-line verbs (headless and forwarded to a running instance) and `--langcheck`. |
| `tools\schtask-test.ps1` | "Start with Windows": creates the task through the app's own code, runs it with `schtasks /run`, checks, deletes it. Refuses to run if a task named `Rigbay` already exists. |
| `tools\make-icon.ps1` | Draws `src\Rigbay.App\Assets\rigbay.ico` (three bays, the last one filled). Run once; the file is committed. |

`dotnet build Rigbay.sln` also builds the app (0 warnings, 0 errors) and `dotnet test Rigbay.sln` still passes A2's 198 tests.
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
| Modules | `%ProgramData%\Rigbay\modules\<id>\` (default; creating it with an admin-only ACL is the installer's job, not done here) |
| Host language files | `lang\en.lang`, `lang\vi.lang` (embedded in the exe and copied next to it for `--langcheck`) |

`--data <dir>` and `--modules <dir>` move the first set and the modules folder; the scripts use them.

## Command line

```
Rigbay.App.exe                       the UI
Rigbay.App.exe --tray                the UI, hidden in the tray (what "start with Windows" runs)
Rigbay.App.exe <module> <verb> ...   headless; forwarded to a running instance when there is one
Rigbay.App.exe list | version        modules and their state / versions
Rigbay.App.exe --langcheck           the core's LangCheck over the host's files and every module's
```

Exit codes follow the contract: 0 ok, 1 failed, 2 not found or bad usage; text goes to stdout (stderr when not 0). Switches such
as `--out <file>`, `--data`, `--modules` must come **before** the verb words; everything after the first word belongs to the verb.
The exe is a GUI-subsystem program: it attaches to the parent console if there is one, and `--out <file>` always works (use it
from PowerShell, which does not wait for GUI programs, or when the elevated process cannot reach your console).

A headless run loads the modules, turns on only the one the verb needs (and only if the user had enabled it), runs the verb and
stops it. A hardware change asked for in a headless run is refused (no window to ask in).

Test and screenshot switches: `--theme dark|light|system`, `--lang`, `--page`, `--script "enable:id;disable:id;page:id;lang:vi;theme:dark;edit:<row header key>=<value>;approve:true|false;cancel-dialog;startup:on|off;wait:ms"`,
`--shot <png>`, `--size WxH`, `--selftest --selftest-out <file>`, `--exit-after <ms>`, `--allow-multi`.

## What it does

- **Shell.** Mica window, 48 px title bar with the search box, `NavigationView` (Dashboard; groups from the manifests, empty groups
  hidden, a dot on running and failed modules; at the bottom Modules, Settings, Log), a sheet with a hairline and 8 px top corners
  at most 1000 px wide, a status line. Below 1000 px the pane collapses to icons. The close button hides to the tray when the
  tray icon exists (Quit is in the tray menu); a second start brings the first window forward (named mutex plus a pipe).
- **Dashboard.** The bay rack: one slim row per module with a bay mark (filled green running, filled red failed, outline off,
  unsupported or incompatible), the state and its reason, up to two live readings in tabular figures (hue by kind: temperature
  warm, power blue, speed teal, load amber), a hairline between rows. No modules: an honest empty state with one button.
- **Module page.** Host-owned enable row first (the first time, a dialog lists what the module declares it uses), then the
  module's groups as `SettingsCard` / `SettingsExpander` rows, then links. A turned-off module keeps its rows, greyed out. Every
  value goes through the core's `ValueEditor`: apply-or-revert rows keep a draft with Apply and Discard; rows that change hardware
  show the exact change ("Fan mode: Auto → Maximum") in a confirmation, then show the value the module reports afterwards and a
  Revert. A custom control (level 2) is created inside the module's boundary and type-checked.
- **Modules.** Every module with state, switch and the manifest's facts (admin, network, registry, services, host API, folder,
  problems). Install, update and remove are not built; the page says so.
- **Settings.** Language (switches at once, module strings too), theme (system, light, dark), start with Windows, log level, open
  the log folder, and the hotkeys of all modules with conflicts marked.
- **Search.** A runtime index of module names and descriptions and every row's header and description, in the current language.
- **Tray, hotkeys, toast.** Tray menu: Open, one submenu per module that registered items, Quit. Hotkeys: `RegisterHotKey` on the
  window, only for gestures nobody else claims; a gesture the OS refuses is flagged. Toasts through `AppNotificationManager`.
- **Fault boundary.** `Application.UnhandledException` is kept (`Handled = true`, logged); when the stack shows a module's code that
  module is marked faulted. Updates that come from a module thread go through `Ui.Post`, which runs inside that module's
  boundary. A module is never given a raw dispatcher.

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

Evidence is in `tools\shots\`: `selftest.txt` (63 checks, all PASS), `cli-test.txt`, `schtask-report.txt`, `dotnet-build.txt`, and
the PNGs (each one was looked at).

- Clean `dotnet build Rigbay.sln` (Release, `--no-incremental`): 0 warnings, 0 errors; Debug the same. `dotnet test`: 198 passed.
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
dialog and the shortcut-capture dialog were seen only in a screenshot, never clicked); a toast actually appearing on screen
(`Show` returned without error); the tray menu on screen (the menu model and callbacks are tested, not the native popup); contrast
measured as numbers (judged by eye on the PNGs); screen readers and keyboard-only use; the pane in the compact state beyond one
screenshot; a machine without the Windows App Runtime; more than 8 modules; a module built against another Windows App SDK
line; Windows 10.

## Contract gaps found while building (reported to the coordinator)

1. `ValueEditor.ApplyAsync` and `RowEditors.InvokeButtonAsync` call the approval prompt with no detail, so the dialog cannot show
   the exact value. The app passes it in a `AsyncLocal` (`ApprovalPrompt.WithDetail`). Suggested: an optional `Func<T, string>`
   formatter or a `detail` argument on `ApplyAsync`.
2. `ValueEditor<T>` subscribes to the module's observable and has no `Dispose`; every editor made for a page stays subscribed.
   The app keeps one editor per row instance to bound this. Suggested: `IDisposable`, or a cache inside `RowEditors.For`.
3. `LocalisedText` has an implicit conversion from `string`, so `cond ? LocalisedText.Of("k") : null` becomes a `LocalisedText` with
   a null key instead of "no value". In a `Validate` that reported every edit as invalid with the text `module.<id>.`. The sample
   module casts around it. Suggested: treat a null or empty key as "no error" in the editors, or drop the implicit conversion.
4. The core clears `ModuleEntry.Page` on disable; the UI wants the rows greyed out (UI-DIRECTION section 4). The app caches the last
   page itself (`PageCache`). Suggested: keep it on the entry.
5. `HotkeyGesture` accepts `F12`, which Windows reserves for debuggers: `RegisterHotKey` always refuses it. The app flags such
   a binding; the contract could say so.
6. Manifest problems are English text from the core inside a localised sentence (seen in the Vietnamese screenshot).
7. `Rigbay.App.exe version` is answered by the app only when headless; `ExecuteCommandAsync` does not know it, so against a running
   instance it is "no module named version" (exit 2). `<module> help` also exits 2 with the verb list, as the usage rule says.
