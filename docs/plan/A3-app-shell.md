# Task A3: the Rigbay app shell (WinUI 3), built on the A2 contract

## Goal
Turn the spike host into the real application shell: `src/Rigbay.App`, a WinUI 3 app, unpackaged, elevated, that uses `Rigbay.Core` to find and load modules and renders their pages. The visual direction is in `docs/UI-DIRECTION.md` (PowerToys-Settings vibe, flat rows, bay-rack dashboard); the working proof is `spike/src/Rigbay.Host` and its screenshots in `docs/spike/`. Read first: `docs/plan/WORKER-RULES.md`, `docs/UI-DIRECTION.md`, `docs/spike/SPIKE.md`, `docs/spike/ABSTRACTIONS-NOTES.md`, `docs/ABSTRACTIONS.md` (A2's contract), `docs/DESIGN.md`. Load the `impeccable` and `design-taste-frontend` skills before you touch the visuals.

## Rules from the spike (tested; follow them)
- Windows App SDK: pin one line in `Directory.Build.props` or the App csproj and record why. Framework-dependent against the installed runtime is the main path; self-contained 1.8 crashed once unexplained, so if you offer a self-contained build, re-test it and write the result down.
- `WindowsPackageType=None`, manifest `requireAdministrator`, PerMonitorV2, single instance (named mutex; second launch shows the first), `MicaBackdrop`, 48 px extended title bar with the search box.
- Use `CommunityToolkit.WinUI.Controls.SettingsControls` (8.2.251219 worked) for rows; stock controls otherwise.
- Host supplies `RunOnUi`; wrap every module call, every row action and every UI update coming from a module thread in the fault boundary. Keep `Application.UnhandledException` with `Handled = true` + log + mark the module faulted. Never use a raw `DispatcherQueue.TryEnqueue` with code that can throw.
- Brushes from code follow the app theme, not the window theme: use `ThemeResource` in XAML for text brushes (a bug the spike found in light mode).
- No compiled-XAML module pages; level 1 (declarative rows) and level 2 (`CreateCustomControl`) only.
- Never capture the real screen; render the window's own HWND (PrintWindow) like `spike/shots.ps1`, in dark and light, and look at every PNG with the Read tool.

## What to build
1. **Shell:** window, title bar, search over module names and row header/description (runtime index from the page models), `NavigationView` with Dashboard, module groups from the manifests (empty groups hidden), then fixed items **Modules** (list of installed modules with state, enable/disable and the manifest facts it declares: admin, ports, registry, services; install/update/remove are a placeholder page saying what will come, honest not fake), **Settings** (language, theme System/Light/Dark, start with Windows, log level, open log folder), **Log** (read-only tail of the host log). Status line as in the spike.
2. **Dashboard:** the bay rack. One slim row per installed module: bay mark (green ready, red failed, outline unsupported/off/empty), name, state line (Probe reason or error text), the module's `Summary` readings on the right in tabular figures, a hairline separator. Honest empty state when no module is installed (one button to Modules).
3. **Module page:** rendered from the PageModel: enable row first, groups of `SettingsCard`/`SettingsExpander` rows below (disabled while the module is off), links at the bottom, custom control rows via `CreateCustomControl`. Hardware-changing rows go through the approval service from the contract: show what will be written, ask, apply, show the read-back and a revert. Include a **demo of that flow with a fake module** in the sample (no real hardware).
4. **Language:** `Rigbay.Core` loader; `lang/en.lang` and `lang/vi.lang` for every host string; module keys `module.<id>.<key>`; switching at run time without restart; `--langcheck` returns non-zero on problems.
5. **Host services behind the contract** that need the HWND: hotkey registration (RegisterHotKey from the host window, using Core's conflict detection), tray icon with a menu built from modules' tray items (Open, per-module items, Quit; close button hides to tray as in xtu-profile-switcher), toast through `AppNotificationManager`, command-line verbs through Core's command registry (`Rigbay.App.exe <module> <verb>` runs headless and prints the result; with a running instance it forwards to it).
6. **Start with Windows:** a scheduled task `Rigbay` (logon, highest privileges, `--tray`, delay about 15 s) created and removed from Settings. Test it with `schtasks /run` like the spike did, then delete it; say plainly that a real logon was not tested.
7. **Dashboard/Module-page states:** also render and check (PNG, dark and light) these states: empty, one failed module, one unsupported module, enabled and disabled module page, Vietnamese.
8. A `build.ps1`, `run-elevated.ps1`, `stop-elevated.ps1`, `shots.ps1` under `src/Rigbay.App/` (or a top-level `tools/`), and a short `docs/APP.md` (how to build, run, test, where logs and settings live).

## Verification you must show
`dotnet build` clean; run elevated and run the in-app self-test (adapt the spike's `SelfTest.cs`: shell up, modules loaded, page rendered, search index, hotkey, tray add, fault boundary cases, language switch); the PNGs listed above; the scheduled-task test. List what you did not verify (real logon, 150%/200% DPI, high contrast, real clicks on module buttons, toast on screen).

## Areas you own (touch nothing else)
`src/Rigbay.App/`, `docs/APP.md`, `tools/` for app scripts. Task A2 owns `src/Rigbay.Abstractions`, `src/Rigbay.Core`, `tests/`, `samples/`, `Directory.Build.props`, `Rigbay.sln`, `docs/ABSTRACTIONS.md`. If you need a contract change, mail the coordinator with the exact change and why; do not edit those files. Adding your project to `Rigbay.sln` is the one allowed edit there. The spike stays in `spike/` untouched.
