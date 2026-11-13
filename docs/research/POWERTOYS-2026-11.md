# PowerToys, second study (November 2026): processes, memory, tray, UX, and Rigbay's next modules

Task b84c2477 (M1), 2026-11-13. The first study (`docs/UI-DIRECTION.md` section 1, 2026-11-01) read only `src/settings-ui`. This one reads
the runner, the module interface, the Settings and Quick Access processes and a few modules, and answers the owner's question of 2026-11-13:
Rigbay uses about 256 MB of RAM (169 MB private) and about 1.6 % of one core with 4 modules on and the window open. How does PowerToys
handle that, what can Rigbay learn from its UI, and which modules come next?

**Source.** `microsoft/PowerToys` (MIT) at commit `2ca80df` (2026-11-12), a shallow sparse clone in `D:\Documents\Projects\_repos\PowerToys`
(outside this repository): `src/runner`, `src/settings-ui`, `src/common`, `src/modules/interface`, `doc/devdocs`, and the module interface
folders of Awake, Color Picker, Light Switch, PowerDisplay, Find My Mouse, Always on Top and Command Palette. PowerToys was **not installed**
on this machine. Every statement about PowerToys comes from its code, its developer docs or its GitHub issues, and says which. Paths in this
document that start with `src/` or `doc/` are PowerToys paths unless they name a Rigbay file. Nothing was copied into Rigbay; identifiers are quoted
as facts.

## Verdict

1. **PowerToys keeps its always-on part small by making it native, not by being clever with WinUI.** The runner (`PowerToys.exe`) is C++ with
   a hidden window, a message loop, one keyboard hook and the module DLLs. The big UI, Settings (WinUI 3, .NET), is a **separate process that
   starts when you open it and ends when you close its window**. Rigbay's always-on part is a .NET 10 process with WinUI 3 loaded in it, and
   the XAML framework stays in memory for the whole session even while the window sits in the tray.
2. **PowerToys does not keep every module cheap.** Most utilities with a UI (Color Picker, Always on Top, Awake, PowerDisplay, Light Switch,
   Command Palette) run their own process from the moment they are enabled, warm, so the hotkey answers at once. Users have complained about
   it since 2021: a report in issue #10357 (June 2024) puts an idle Color Picker at about 560 MB after first use. An opt-in "close when inactive" pull
   request (#47487) was closed unmerged on 2026-08-21, and issue #47518 is still open. The Quick Access flyout is also a warm WinUI process;
   it gives memory back 2 s after it hides (a full GC plus a working-set trim).
3. **In Rigbay the UI holds most of the memory, not the modules.** A scratch host with no UI (.NET 10, `Rigbay.Core` 0.1.2, the demo Fan studio
   module enabled, every metric subscribed) sat at **39 MB working set, 10 MB private, 0.38 % of one core** over 45 s. The two `Rigbay.App`
   processes running on this machine at the time showed 110 to 118 MB working set and **157 to 162 MB private**. Real modules add their
   own libraries (WMI, WCF, sockets), which this test did not load.
4. **Recommendation: (a) now, step 1 of (b) next, then decide.** (a) is M2's in-process trim on `perf/tray-trim`: drop the page and give memory
   back after the window has been in the tray for a while. It costs little and keeps one process. Its limit is that WinUI cannot be unloaded from
   a process. Step 1 of (b) moves the tray, the hotkeys and the balloons off the WinUI window onto a message-only window the host part owns. It is small, worth
   having on its own, and it lets (a) go one step further: close the window instead of hiding it. Build the full split (b), a light module host
   plus a WinUI process started on demand like PowerToys Settings, only if the measured result of (a) is still too high for the owner. The split
   is the largest change since the app shell (size L). It needs a page protocol over the pipe, an authenticated approval channel, a self-test
   in two parts and two executables in the installer. Section 1.7 has the costs and 1.8 the steps.
5. **UX: six lessons to adopt**, all size S. Search that ignores accents (Vietnamese), an OS check while a shortcut is being captured, a
   one-time "what changed" bar after an update, the modules' main controls in the tray menu, UI Automation landmarks and ids, and one teaching tip the
   first time a module is turned on. They are written into `docs/UI-DIRECTION.md` section 10. Not adopted: an OOBE window, a separate Quick Access
   process, Group Policy.
6. **Next modules (ranked in `docs/plan/MODULE-IDEAS.md`): top 3 are GPU readings through NVML (read only), Game scenes (run a set of module
   verbs when a game starts and undo them when it ends) and Watch (alerts on the metrics bus).** Power-source rules (refresh rate and Windows power mode
   on battery) come fourth because every reading taken on this machine so far was on AC with an external monitor.

## Contents

1. Processes and memory: 1.1 PowerToys at a glance, 1.2 how settings and state flow, 1.3 what runs with only the tray icon, 1.4 how they keep
   idle cost low and where they do not, 1.5 Rigbay measured, 1.6 proposal (a), 1.7 proposal (b), 1.8 migration path, 1.9 the decision
2. UX lessons not yet in `UI-DIRECTION.md`
3. Module ideas (summary; the list is `docs/plan/MODULE-IDEAS.md`)
4. What was not verified

## 1. Processes and memory

### 1.1 PowerToys at a glance

| Process | Built with | When it runs | Ends when | Talks to the runner by | Where (PowerToys) |
| --- | --- | --- | --- | --- | --- |
| `PowerToys.exe`, the runner | C++, Win32 | From sign-in (or start) to Exit | Exit in the tray menu | (it is the hub) | `src/runner/main.cpp` `runner()` (tray, Quick Access, keyboard hook, module DLLs, update thread, message loop) |
| `PowerToys.Settings.exe` | WinUI 3, .NET | **On demand**: tray double-click, the Settings menu item, a left click when Quick Access is off, a deep link, first run (OOBE) and after an update (what's new) | Its window closes (unless a secondary window is open, then it hides); the runner exits (`WaitForPowerToysRunner`); the runner's terminate event | A two-way named pipe with JSON messages; pipe names on the command line | `src/runner/settings_window.cpp:441` `run_settings_window` (start, then `WaitForSingleObject` on the process at :635, then the pipe is dropped); `src/settings-ui/Settings.UI/SettingsXAML/App.xaml.cs:100` and `:223`; `MainWindow.xaml.cs` `Window_Closed` |
| `PowerToys.QuickAccess.exe` | WinUI 3, .NET | Started with the runner when Quick Access is on; **kept warm**, hidden | The runner stops it (exit event, 2 s wait, then `TerminateProcess`); a job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` kills it if the runner dies | Its own two-way pipe (random names per start); named events "show" and "exit" | `src/runner/quick_access_host.cpp` `start()` (process created suspended, put in the job, resumed), `show()`, `stop()`; `src/settings-ui/QuickAccess.UI/QuickAccessXAML/MainWindow.xaml.cs` |
| Module helper processes (Color Picker UI, Always on Top, Awake, PowerDisplay, Light Switch service, Command Palette, ...) | C#/WPF/WinUI or C++ | **From enable**, kept running for an instant hotkey | The module is disabled (exit event, short wait, `TerminateProcess`) | Named events (invoke, terminate, settings changed), some a pipe; settings from their JSON file | `src/modules/colorPicker/ColorPicker/dllmain.cpp:238` (`enable` launches, `on_hotkey` at :267 relaunches if needed and sets the invoke event); `awake/AwakeModuleInterface/dllmain.cpp:60`; `powerdisplay/PowerDisplayModuleInterface/dllmain.cpp:399`; `LightSwitch/LightSwitchModuleInterface/dllmain.cpp:497`; `cmdpal/CmdPalModuleInterface/dllmain.cpp:215` (started unelevated through Explorer) |
| In-runner modules (Find My Mouse, Mouse Highlighter, crosshairs, ...) | C++ in the runner | A thread from enable | Disable | Direct calls | `src/modules/MouseUtils/FindMyMouse/dllmain.cpp:159` |

The module contract is a C++ interface (`src/modules/interface/powertoy_module_interface.h`): `get_key`, `get_config`/`set_config` (JSON
strings), `enable`, `disable`, `is_enabled`, hotkeys (`get_hotkeys`, `on_hotkey`, `GetHotkeyEx`), `call_custom_action`,
`is_enabled_by_default`, `gpo_policy_enabled_configuration`. Every DLL is listed by name in the runner (`main.cpp:305`); there is no discovery
or third-party install. A DLL that fails to load gives a message box in release builds.

**Elevation.** PowerToys runs unelevated by default. "Always run as administrator" and "Restart as administrator" exist
(`src/runner/restart_elevated.cpp`; `GeneralPage.xaml`). Settings shows "Administrator: PowerToys Settings" in its title when elevated. The
code carries the scars: Settings once ran unelevated under an elevated runner and wrote to a different profile folder (comment in
`settings_window.cpp` about #22659), so now it runs at the runner's level.

### 1.2 How settings and state flow

| Path | What travels | Where |
| --- | --- | --- |
| Settings to runner | JSON over the pipe: `{"powertoys": {"<module>": {...}}}` or `{"general": ...}`; the runner calls the module's `set_config`; hotkey changes trigger a conflict check | `settings_window.cpp:174` `dispatch_json_config_to_modules`, `:163`; `doc/devdocs/core/settings/runner-ipc.md` |
| Runner to Settings | `{"ShowYourself": "<page>"}`, update state, hotkey conflicts, bug-report state | `tray_icon.cpp`, `settings_window.cpp:733` `open_settings_window` |
| Files | `%LOCALAPPDATA%\Microsoft\PowerToys\<module>\settings.json`: Settings writes, the module or its helper reads | `doc/devdocs/core/settings/communication-with-modules.md` |
| File watchers | PowerToys Run watches its own settings file | same doc |
| Shared file with a lock | Keyboard Manager and Settings share `default.json` under a named mutex | same doc |
| Named events | show, exit, invoke, terminate, settings changed (PowerDisplay reads its file again when signalled) | `powerdisplay/PowerDisplayModuleInterface/dllmain.cpp` comments near :395 |
| Command line | Settings has `set`, `setAdditional` and `get` modes that edit a module's settings file and exit; the runner holds a lock file meanwhile | `App.xaml.cs` `OnLaunchedToSetSetting`; `main.cpp` `create_settings_cli_lock` |
| Pipe client check | The runner accepts the Settings or Quick Access pipe client only if its image is in the expected folder, has the expected file name and version, and (release builds) a Microsoft signature | `settings_window.cpp:607`, `quick_access_host.cpp` `CallerPolicy` |

### 1.3 What runs while only the tray icon exists

- The runner: tray window (`PToyTrayIconWindow`), message loop, the low-level keyboard hook (`centralized_kb_hook.cpp:276`, one hook for all
  modules, with early exits for keys PowerToys sent itself or no key pressed), `RegisterHotKey` for simple shortcuts (`centralized_hotkeys.cpp:61`),
  the periodic update check (`main.cpp`, `PeriodicUpdateWorker` thread), the settings telemetry timer.
- Every enabled module's interface DLL, and threads for in-runner modules.
- Every enabled module's helper process, warm.
- Quick Access, warm and trimmed, if enabled.
- **Not** Settings.

### 1.4 How they keep idle cost low, and where they do not

| Technique | Where (PowerToys) | Effect | Rigbay today |
| --- | --- | --- | --- |
| Native always-on process | `src/runner` | No .NET runtime, no XAML in the part that never exits | No: the always-on process is .NET plus WinUI |
| Big UI in its own process, started on demand, ends on close | `settings_window.cpp`, Settings `App.xaml.cs` | Settings' memory is gone when its window closes | No: the window hides to the tray and the XAML stays |
| Warm flyout that trims itself after hiding | `QuickAccess.UI/.../MainWindow.xaml.cs` `HideWindow` → `ScheduleMemoryTrim` (2 s) → `TrimMemory` (`GC.Collect`, `WaitForPendingFinalizers`, `GC.Collect`, `SetProcessWorkingSetSize(-1, -1)`) | Small working set while hidden; private bytes only shrink by what the GC frees | M2 is building the same idea for the main window (section 1.6) |
| Events, not polling, between processes | named events throughout | Idle helpers wait on a handle | Rigbay's modules poll their devices; they slow down when nobody watches (`PageVisible`, `SubscriberCount`; PredatorSense's `PollScheduler` has a fast and an idle interval) |
| One keyboard hook for all modules, written to return fast | `centralized_kb_hook.cpp` | One hook instead of one per module | Rigbay uses `RegisterHotKey` only (no hook), which costs nothing between key presses |
| A module turned off by policy is never started | `main.cpp` load loop; `gpo_policy_enabled_configuration` | No process, no thread | A module that is off is not initialised; there is no policy |
| WMI through a lighter library | PowerDisplay uses WmiLight and Native AOT because "`System.Management` ... leaks memory on each WMI operation" (their words, `doc/devdocs/modules/powerdisplay/design.md:640`) | Long-running WMI polling stays flat | **PredatorSense polls WMI through `System.Management`** (`modules/predatorsense/src/Rigbay.PredatorSense/Wmi/AcerWmi.cs`). Not measured here; worth a soak test (MODULE-IDEAS has it as a follow-up) |
| **Where they do not**: warm helper processes | Color Picker and others start at enable | Each enabled utility holds its own process all day. Issue #10357 (open since March 2021): "more than 3x the base PowerToys app" (2021), "~560MB of RAM, even when closed" (June 2024, a user report). PR #47487 (opt-in "close the app when not in use") closed unmerged 2026-08-21; issue #47518 open | Rigbay has one process, so a module costs only its own objects and threads |

PowerToys publishes no idle memory figure for the runner or Settings; I found none in `doc/devdocs`. The installed size is documented (about
660 MB as of v0.66, `doc/devdocs/disk-usage-footprint.md`), which says nothing about RAM.

### 1.5 Rigbay, measured

| What | Working set | Private | CPU | Source |
| --- | --- | --- | --- | --- |
| Owner's report: Rigbay 0.1.2, 4 modules on, window open | 256 MB | 169 MB | about 1.6 % of one core | owner, 2026-11-13 |
| `Rigbay.App` processes running on this machine at 2026-11-13 (two were running; window state not known; `Get-Process`) | 110.3 and 118.3 MB | 157.2 and 161.8 MB | n/a | this task, read only |
| Scratch host **without any UI**: .NET 10, `Rigbay.Core` and `Rigbay.Abstractions` from `tools\out\app`, the demo module Fan studio (a pretend device) enabled, one all-metrics subscriber like the Dashboard, 45 s | 39.0 MB | 10.0 MB | 0.38 % of one core (includes start-up JIT) | this task, scratch program in the session scratchpad, not committed; non-elevated |
| V1: window hidden in the tray, module page open, 60 s | n/a | n/a | 0.08 to 0.16 % | `docs/APP.md` "CPU use" |
| V1: window visible, module page open | n/a | n/a | 2.2 to 3.4 % | same |
| M2 (`perf/tray-trim`), in-process trim | see the note under the table | | | M2's own report |

**M2 note.** M2's branch had uncommitted work and no published measurements when this was written (its approach is in section 1.6). Its
`tools\perf\measure.ps1` and `MEM ...` log lines will give the numbers; read them in M2's report or `docs/APP.md` once merged.

**Reading.** With the UI gone, the host and a module need about a tenth of the private memory the running app has. So the window, not the
modules, holds most of the memory: the XAML framework, the compositor and its graphics resources, the theme dictionaries, and the page's visual tree
(charts are many shapes). The CPU the owner saw with the window open is the drawing of a live page (V1 measured it). Hidden, Rigbay already costs
almost nothing in CPU.

### 1.6 Proposal (a): one process, release the UI while hidden (M2, `perf/tray-trim`)

What M2 is building, read from its working tree (not yet committed): a setting "Release memory when Rigbay is in the tray", a delay (60 s by default,
1 s to 1 h). After the window has been hidden that long, the open page is dropped: its visual tree, its renderer and the statics that point into it.
The .NET heap is collected and compacted once and the working set is trimmed once. Showing the window rebuilds the page with its scroll,
open expanders and focus. Data stays: page models, metrics history, search index. It is PowerToys Quick Access's trim, applied to the main window.

| | |
| --- | --- |
| Gain | The page's objects (private) and most of the working set. Task Manager's "Memory" column shows the private working set, so the trim is what the owner will see go down |
| What stays | Everything WinUI loaded: `Microsoft.UI.Xaml` and its native parts, the window, its composition target and graphics device, the app's resource dictionaries. **WinUI cannot be unloaded from a running process** |
| Cost | Small; one class and a policy, already written. A rebuild on show (M2 times it) |
| Risks | The first show after a trim is slower (pages fault back in, the page is rebuilt); a bug that keeps a reference to a dropped element makes the trim useless (M2 checks with weak references); a working-set trim alone moves pages to the standby list and does not lower commit |
| Step further, "(a+)" | **Close the window instead of hiding it**, keep the app alive (WinUI's explicit shutdown mode) and create a new window on show. That frees the window's swap chain and composition tree. It is blocked today because the tray icon and the hotkeys live on the main window's `HWND` (`src/Rigbay.App/Services/TrayHost.cs`, `HotkeyHost.cs` take it in their constructors), and that is exactly step 1 of (b). How much (a+) frees is not known until it is measured |

### 1.7 Proposal (b): a light module host plus a UI started on demand

```
 sign-in (scheduled task, highest privileges)
        |
        v
 +-----------------------------------------+        named pipe, ACL: Administrators;        +-------------------------------------+
 | Rigbay.exe  (host, elevated, no WinUI)  |        client checked: elevated, image is      | Rigbay.App.exe  (UI, elevated)       |
 |  Rigbay.Core, modules (in process)      | <----- the installed Rigbay.App.exe, same -----> |  WinUI 3 shell, pages, charts,      |
 |  metrics bus + history                  |        version                                  |  dialogs, search                     |
 |  tray icon + native menu, hotkeys,      |                                                 |  started by: tray click, second      |
 |  balloons (message-only window)         |                                                 |  start, an approval, a deep link     |
 |  CLI pipe, install swap at start        |                                                 |  ends: N minutes after its window    |
 +-----------------------------------------+                                                 |  closes (default 5), or with the host |
                                                                                             +-------------------------------------+
```

What it means, area by area:

| Area | Today | With the split | Cost | Risk |
| --- | --- | --- | --- | --- |
| In-process modules | Loaded by `RigbayHost` in the app | Unchanged, now in `Rigbay.exe`. Modules never touch the UI thread (no released module calls `RunOnUi`) | none | low |
| Page model, level 1 | Data (`PageModel`, rows, `IObservableValue<T>`), rendered in process | Still data. Needs a **page protocol**: a snapshot of the page (rows with ids, kinds, resolved texts, values, enabled state), then deltas (a value or a row changed, a page refresh), and intents from the UI (set draft, apply, discard, revert, press a button). `ValueEditor`, validation and approval stay in the host, where the module is | **L**, the largest part | Stale or lost deltas; a reconnect must resend the snapshot |
| Custom controls, level 2 | `CreateCustomControl` returns a WinUI `UIElement` | Cannot cross a process. Only the demo module Fan studio uses it. Keep host API 0.1 and draw a `CustomRow` as "not available in this window" (additive), or add data row kinds for the cases a module needed (a curve) | S | A future module that wanted a custom editor has to wait for a row kind |
| Approvals | `ApprovalPrompt` shows a `ContentDialog`; headless verbs refuse hardware writes | The host sends the request to the UI and waits for the answer. With no UI running (a hotkey, a tray item), the host starts the UI in approval mode or shows a native task dialog itself (`TaskDialogIndirect`, no WinUI). **The answer must come from a process the host trusts**: today's pipe lets any process of the same user connect (ACL: the current user's SID). Use Administrators only, plus a client check (`GetNamedPipeClientProcessId`, image path inside the admin-only install folder, elevated token, same version); PowerToys checks folder, name, version and signature. Rigbay has no code signing, so the folder ACL carries the check | M | **Security**: a forged "yes" would be a hardware write nobody approved. This part needs its own review |
| Hotkeys | `RegisterHotKey` on the main window | `RegisterHotKey` on the host's message-only window; the capture dialog in the UI asks the host to pause hotkeys while it listens | S | low |
| Tray | Win32 already (`Shell_NotifyIcon`, `TrackPopupMenuEx`) on the main window | Moves to the host unchanged; "Open" starts or shows the UI | S | low |
| Notifications | Tray balloons (elevated) and the in-window notice, chosen by `NotificationPolicy` from `WindowInUse` | Balloons from the host; the UI reports "window in use" over the pipe so the policy still works | S | A lost "in use" message means a balloon instead of an in-window notice (harmless) |
| Metrics bus | In process; pages subscribe; `PageScope` drops subscriptions while hidden | Stays in the host. The UI subscribes per open page over the pipe, asks for the history once on page open, and its subscriptions count in `SubscriberCount`; `PageVisible` comes from the UI | M | Traffic is small (a few samples a second); a slow UI must not block a module's publish (queue and drop) |
| Search | Runtime index from the page models | Built from the snapshots the host sends | S | low |
| Commands | Pipe to the running instance, headless otherwise | Pipe to the host; `Cli.RunHeadless` already runs `RigbayHost` without XAML and becomes the resident host's start | S | low |
| Log page | `host.Log.Recent()` in process | Over the pipe (or the UI reads the log file) | S | low |
| Self-test | One elevated process drives the UI and the core (about 170 checks) | Two parts: a host self-test (core, modules, pipe, tray, hotkeys, approval channel) and a UI self-test against a fake host. New checks: the UI crashes and the host keeps running, the host restarts and the UI reconnects, a foreign pipe client is refused | M to L | The suite is the project's safety net; it must not get weaker during the move |
| Installer and start | One exe; the logon task runs `Rigbay.App.exe --tray` | Two exes in the same admin-only folder; the task runs `Rigbay.exe`; `--uninstall-cleanup` stops both; "Restart now" restarts the host; the module swap stays at host start. Only the UI needs the Windows App Runtime | M | Upgrade from 0.1.x must replace the task's command line |
| Start of the window | Instant (hidden window shown) | A cold WinUI start each time it was closed for longer than N minutes. Not measured for `Rigbay.App`; the spike saw 2.4 to 3.6 s for the very first start on Windows App SDK 2.6.4 and chose 1.8 partly because it was faster | S (keep-alive timer) | The owner feels the wait; keep-alive hides it for quick re-opens |
| Faults | A XAML crash (for example the self-contained `0xC000027B` crash in `docs/APP.md`) takes the modules down with it | A UI crash no longer stops a recording, a tunnel or the Steam proxy | (gain) | A host crash leaves a UI that must say "Rigbay is not running" |

**Expected numbers (estimate, not measured).** With the window closed: the host only, which is the scratch floor (39 MB working set, 10 MB private)
plus what the real modules load. Step 1 of the path below measures that in the real app. With the window open: about what Rigbay uses today.

### 1.8 Migration path

Each step leaves a working app and can stop there.

| Step | What | Size | What it proves | Undo |
| --- | --- | --- | --- | --- |
| 0 | Merge (a) (M2). Soak PredatorSense's WMI polling for a few hours and log private bytes (section 1.4, the `System.Management` leak PowerToys reports) | S | The baseline after the trim; whether a module leaks | revert the merge |
| 1 | A host message-only window inside today's process: tray, hotkeys, balloons, the instance pipe move off the WinUI window | S to M | Tray and hotkeys work with no WinUI window at all | revert |
| 1b | (a+): close the window instead of hiding it, create it again on show; measure | S | How much a closed window frees inside one process. **Decision point: if this is enough for the owner, stop here** | setting off |
| 2 | Page protocol in `Rigbay.Core` (snapshot, deltas, intents) with an in-process loopback; the existing UI renders through it, still one process | L | The protocol is complete; the self-test still runs in one process | keep the direct path behind a switch until the protocol is proven |
| 3 | Approval channel with the client check, and the native task-dialog fallback when no UI is running | M | No write without an answer from a trusted UI, tested with a foreign client | n/a (security work stays) |
| 4 | Split: `Rigbay.exe` (the headless path grown into a resident host) and `Rigbay.App.exe` over the pipe; logon task points to the host | M | Two processes, same behaviour; self-test in two parts | the task points back to the single exe |
| 5 | UI lifetime: ends N minutes after its window closes | S | The idle number reaches the host floor | N = forever |
| 6 | Installer, release notes, `docs/APP.md`, measurements before and after | M | The owner's numbers on the real machine | previous release |

### 1.9 The decision for the owner

| Option | Idle memory with the window in the tray | Work | Risk |
| --- | --- | --- | --- |
| (a) only | Lower working set; private lower by the page's share; WinUI stays loaded | done by M2 | low |
| (a) + step 1 + (a+) | Lower again by the window's resources (to measure) | about a week | low |
| (b), the full split | Near the host floor (estimate: tens of MB) | several weeks | medium (the approval channel, the protocol, the self-test) |

My recommendation: (a), then step 1 and (a+), measure, and only then decide on (b). Most of what makes PowerToys light comes from the native runner, and
Rigbay will not rewrite its host in C++. (b) is the closest Rigbay can get, and it is only worth its cost if the owner keeps Rigbay open all day
and still finds the number too high after (a+).

## 2. UX lessons not yet in `UI-DIRECTION.md`

Size: S under a day, M a few days, L a week or more (as in `docs/ui-review/REVIEW-2026-11.md`).

| Topic | What PowerToys does (file) | Rigbay has | Recommendation | Size |
| --- | --- | --- | --- | --- |
| First run (OOBE) | An OOBE window on the first start (`main.cpp`: shown when the "OOBE opened" flag is not set); one page per utility (`SettingsXAML/OOBE/Views/Oobe*.xaml`, an overview page) and an animated welcome hero (`Controls/WelcomeHero`). The main Settings window is created hidden at the same time so the tray can open it at once | The Modules page opens once with a short hero block and "Check for modules" (FR-1) | **Do not add an OOBE window.** Rigbay's first job is "install a module"; a tour of five modules nobody has installed teaches nothing. Instead, the first time a module is turned on, one `TeachingTip` on its main control: what it does, that Rigbay asks before each hardware change, where Revert is. Once per module, dismissible, remembered in `app.json` | S |
| What's new | After an update ("SCOOBE"): the runner compares the version with the last one run and opens release notes fetched from GitHub releases (`main.cpp`, `ScoobeReleaseNotesPage`, `ScoobeReleaseGroupViewModel.cs:88`); a setting and a policy turn it off; the Dashboard has a "Learn what's new" button with the version | Nothing in the app; `CHANGELOG.md` in the repo | **Adopt, smaller:** after the host or a module changed version, one bar at the top of the Dashboard: "Rigbay 0.1.3 is installed. What changed" with a link to that release's notes, a close button, shown once. No window, no fetch at start-up | S |
| Dashboard | A header row with "Learn what's new", shortcut conflicts and update check; cards: Quick access actions, Shortcuts (every utility's shortcut), Utilities (toggles, sort by name or status) (`Views/DashboardPage.xaml:88-260`) | "This PC now" tiles, the bay rack with each module's state and main control | Rigbay's Dashboard is already more useful for its subject (readings). The one thing worth taking, a conflict count, is part of the shortcut lesson below | none |
| Quick Access flyout | Left click on the tray icon opens a small WinUI flyout near the tray (actions of each utility, settings, docs, bug report); double click opens the Dashboard; right click the menu (`tray_icon.cpp:188` `click_timer_elapsed`, `QuickAccess.UI`). It is a warm process | Left click opens the window; right click the menu: Open, one submenu per module that registered items, Quit | **Adopt the content, not the process:** put each running module's main control (`PageModel.Primary`) in its tray submenu: a choice as radio items, a toggle as a check item, a button as an item, with the current value marked. The writes go through the same approval. A WinUI flyout would mean a warm UI process, which works against section 1 | S to M |
| Shortcut conflicts | A conflict manager in the runner: conflicts between utilities, and with the system, found by trying `RegisterHotKey` (`hotkey_conflict_detector.cpp:367`, with a special case for Win+Shift+C on Windows 11); a window listing every conflicting shortcut with the utilities that use it and "Ignore conflict" (`Controls/Dashboard/ShortcutConflictWindow.xaml`); a button with the count on the Dashboard | Conflicts between modules named in Settings > Hotkeys; a gesture the OS refuses is flagged after registration | **Adopt the early check:** in the capture dialog, try the chord with the OS before it is saved (register and release at once) and say "Windows or another app already uses this shortcut" next to the Set button. While any conflict exists, the Dashboard header shows "1 shortcut conflict", opening Settings > Hotkeys; nothing when there is none. Skip "Ignore conflict": a refused hotkey simply does not work, so there is nothing to ignore | S |
| Settings search | An index built at build time from the XAML (`Settings.UI.XamlIndexBuilder`), fuzzy matching on header and description, **accents and case removed** (`Services/SearchIndexService.cs:237`, `NormalizeString`: lower case, Unicode form KD, non-spacing marks dropped) | A runtime index; a plain substring match that ignores case only (`src/Rigbay.App/MainWindow.xaml.cs:764`) | **Adopt:** fold accents in both the query and the index (and map `đ` to `d`, which form KD does not split), so "nhiet do" finds "Nhiệt độ"; rank matches at a word start first; keep the runtime index | S |
| Enable and disable | One enable card first on every page; a utility turned off by policy shows the card locked; the Dashboard and the flyout list every utility with its toggle | The header card's switch, the Modules page switch, the Dashboard main controls | Already covered. Nothing to add | none |
| Managed and admin states | Policies under `HKLM`/`HKCU\SOFTWARE\Policies\Microsoft\PowerToys`; a module disabled by policy is not started; locked controls with an info bar "This setting is managed by your organization." (`Controls/GPOInfoControl.xaml`, `doc/devdocs/processes/gpo.md`); "Running as administrator" with a restart button | Always elevated; "Not running as administrator" bar only when true | **Not now.** Rigbay has one owner per machine. If Rigbay ever runs on a shared or school PC, the first policy to add is "no hardware writes" (read-only mode), shown the PowerToys way: controls locked, one info bar | M, later |
| Accessibility | `AutomationProperties.LandmarkType` on the shell (34 uses), `AutomationId` on almost every control (136; their UI tests use it), `HeadingLevel` on page titles, `RaiseNotificationEvent` when the pane opens or a value is copied, `LiveSetting` on the update status | `SetName` (57), `SetHeadingLevel` (8), a live region on the in-window notice, two notification events; the self-test finds controls by their **accessible name**, which changes with the language | **Adopt:** landmarks on the pane (navigation), the page (main) and the search box (search); an `AutomationId` on every control the self-test drives, so tests stop depending on English names; announce "Copied" and "Applied" with `RaiseNotificationEvent` | S |

## 3. Module ideas

The ranked list with all fields is `docs/plan/MODULE-IDEAS.md`. Top 3 to build next:

1. **GPU readings (NVML, read only).** The RTX 3060 drives the external monitor and the games; Rigbay knows only its temperature and fans
   (PredatorSense). NVML (`nvml.dll`, present in `System32` on this machine) gives load, power, clocks, memory, P-state and throttle reasons. No
   write, no gate, no admin. Size M.
2. **Game scenes.** When a chosen game starts, run a list of existing module verbs (`xtu apply <profile>`, `recorder start`, `steam-bypass on`,
   `tunnel on <name>`, later `predatorsense fan ...`), and undo them when it ends. It needs one host addition (a module may invoke another module's verb)
   and an owner decision on approving an automatic hardware write once per scene. Size L.
3. **Watch.** Rules over the metrics bus ("CPU at or above 95 °C for 30 s", "GPU throttling", "fan at 0 rpm while hot", "tunnel handshake older
   than 3 minutes") that raise one notification. Read only. It needs a host addition so a background reader does not keep every module at its fast
   poll rate. Size S to M.

## 4. What was not verified

- Nothing about PowerToys was run. Its memory and CPU are not measured here; the only figures are users' reports in its issues.
- That PowerToys Settings ends when its window closes is read from the code: the runner waits on the process and drops the pipe when it ends,
  and `Window_Closed` clears the window. I took the WinUI default "end when the last window closes" from the Windows App SDK documentation,
  not from a run.
- The scratch host measurement used the demo module only, non-elevated, one run of 45 s; real modules (WMI, WCF, sockets, `wg.exe`) were not
  loaded. The two `Rigbay.App` figures are one `Get-Process` reading of processes I did not start; why two were running is not known to me.
- (a+) and (b) are designs. No code was written for them; their memory gains are estimates.
- M2's results were not committed when this was written (section 1.5 note).
- The `System.Management` leak is PowerToys' statement; Rigbay's PredatorSense module was not soak-tested for it.

Sources (web): [issue #10357](https://github.com/microsoft/PowerToys/issues/10357), [issue #47518](https://github.com/microsoft/PowerToys/issues/47518),
[pull request #47487](https://github.com/microsoft/PowerToys/pull/47487).
