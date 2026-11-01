# Spike A1: WinUI 3 (unpackaged, elevated, modules, logon task)

Run on the author's machine on 2026-11-01. Everything below was run, not read from documentation, except where it says "not verified".

**Verdict: WinUI 3 is go.** All three required points pass, with one real limit (a throw inside a raw `DispatcherQueue` callback kills the process) and one sharp edge (compiled XAML inside a dynamically loaded module). Recommendation at the end.

## Versions

| Item | Value |
| --- | --- |
| Windows | 11, 10.0.26300 |
| .NET SDK / runtime | 10.0.403 / Microsoft.NETCore.App 10.0.14 |
| TFM | `net10.0-windows10.0.19041.0`, `win-x64` |
| Windows App SDK (main line) | NuGet `Microsoft.WindowsAppSDK` 1.8.260921001 (WinUI package resolved to 1.8.260803003); installed runtime package 1.8 = 8000.994.2142.0 |
| Windows App SDK (also tried) | 2.6.4 (installed runtime 2.6.4.0), switch with `.\build.ps1 -WinAppSdk 2.6.4` |
| Community Toolkit | `CommunityToolkit.WinUI.Controls.SettingsControls` 8.2.251219 |
| Packaging | `WindowsPackageType=None` (unpackaged), manifest `requireAdministrator`, PerMonitorV2 |

## How to reproduce

```powershell
cd spike
.\build.ps1                       # modules + host-fd + host-sc into spike\out  (about 18 s after restore)
.\run-elevated.ps1 -HostArgs @('--selftest','--log',"$PWD\out\run.log",'--exit-after','200')
.\shots.ps1                       # window PNGs into docs\spike (PrintWindow of the window's own HWND)
.\measure.ps1 -Variant fd         # cold start numbers
.\schtask-test.ps1                # part (c), relaunches itself elevated, deletes the task
.\stop-elevated.ps1               # stops any Rigbay.Host (elevated)
```

Host switches: `--log`, `--selftest`, `--shot <png>`, `--theme dark|light`, `--lang en|vi`, `--page <module id>`, `--modules <dir>`, `--exit-after <ms>`, `--allow-multi`, `--crash-probe dispatcher|asyncvoid`.

## (a) Unpackaged, elevated WinUI 3 window: PASS

- Starts elevated (`IsUserAnAdmin=True`), Mica backdrop set (`MicaBackdrop`, no error), 48 px extended title bar with a search box in it, stays responsive, exits with code 0 (`Application.Exit`), no leftover process.
- Started dozens of times during the spike (no count kept); one startup failure class seen, see "Self-contained 1.8" below.
- Single instance (named mutex): second start logs `second instance: exiting` and ends; first keeps running. PASS.

Variants (all with the same code; sizes of the whole output folder; "first frame" is milliseconds from process creation to the first `CompositionTarget.Rendered`, from `measure.ps1`, 6 to 8 runs):

| Variant | Size | First run after build | Median of the later runs | Needs an installed runtime? |
| --- | --- | --- | --- | --- |
| 1.8, framework-dependent (`host-fd`) | 43.5 MB, 97 files | 556-582 ms | 566-578 ms | Yes: Windows App Runtime 1.8 (loaded from `C:\Program Files\WindowsApps\...1.8_8000.994.2142.0...`) and .NET 10 |
| 1.8, self-contained (`host-sc`) | 220.8 MB, 562 files | 543 ms | 559 ms | No: `Microsoft.UI.Xaml.dll` was loaded from the app folder (checked in the process) |
| 2.6.4, framework-dependent | 84.7 MB, 102 files | 2434 ms | 545 ms | Yes: Windows App Runtime 2 (2.6.4.0) |
| 2.6.4, self-contained | 238.4 MB, 571 files | 3563 ms | 540 ms | No (same check) |

Notes:
- The very first launch of a fresh 2.6.4 build took 2.4 and 3.6 s, later ones 0.54 s. The 1.8 builds did not show this. Cause not investigated (possibly first-time file scanning); worth knowing for an installer's first start.
- The `sc` variants include the .NET runtime too (that is most of the size). Trimming was not tried.
- The "installed runtime" columns say what this machine had; a machine **without** the runtime was not available, so "framework-dependent fails there" is expected, not observed.
- The window frame in a 1240 x 800 window is rendered by PrintWindow; the Mica tint is only faintly visible in those PNGs. Whether Mica renders as on screen was not confirmed.

### Self-contained 1.8: one unexplained crash

Early in the spike, `host-sc` with 1.8 died at startup in all of 4 attempts and 3 build configurations (manifest removed, .NET shared, a copied `resources.pri`): exit code `-1073741189` (0xC000027B, stowed exception, fault module `Microsoft.UI.Xaml.dll`, HRESULT 0x80004005), after `App` was constructed and before `OnLaunched`. The same code on 2.6.4 self-contained and framework-dependent started fine. After the shell was rewritten (NavigationView shell, toolkit package) with unchanged project settings, 1.8 self-contained started 8 of 8 times and passed the full self-test. I did not find what changed; treat "1.8 self-contained" as unproven and prefer to re-test it in the product's real project before relying on it.

## (b) A module DLL loaded at run time contributes a page: PASS (with a ranking)

Each module is a folder `modules\<name>\<name>.dll`, loaded into its own `AssemblyLoadContext` (`ModuleLoadContext`): shared assemblies resolve from the host first, everything else from the module's `.deps.json`.

Type identity: PASS. In the process there is exactly one copy of `Microsoft.WinUI`, `WinRT.Runtime`, `Microsoft.Windows.SDK.NET` and `Rigbay.Abstractions` (`single copies` check), `UIElement` comes from the Default context, a module page is `is UIElement`, and a module-drawn control uses the host's `AccentFillColorDefaultBrush` from `Application.Current.Resources`. The module output folders still contain their own copy of `Rigbay.Abstractions.dll` and the WebView2 assemblies; they are never loaded, but a packaged module should not ship them.

| # | Approach | Result | Detail |
| --- | --- | --- | --- |
| 0 | **Declarative page model**, rendered by the host (`Rigbay.Module.Rows`) | **PASS** | WinUI-free model in `Rigbay.Abstractions`; host built 13 rows into 10 `SettingsCard` and 1 `SettingsExpander`; editing works; a value changed on a module timer thread shows up through the UI thread. |
| 1 | Module returns a `UIElement` built in C# (`Rigbay.Module.Code`, also used for the escape hatch in `Rows`) | **PASS** | Controls, slider, toggle, theme resources all work. Needs the module to reference the same Windows App SDK as the host. |
| 3 | `XamlReader.Load` of a XAML string embedded in the module (`Rigbay.Module.Loose`) | **PASS** | Works for plain XAML. No `x:Class`, no event attributes, no `x:Bind` (wire up in code with `FindName`). |
| 2 | Compiled XAML page in the module (`Rigbay.Module.Xaml`) | **FAIL as built, PASS with a workaround** | See below. |

Approach 2 exact failure, from the module's generated `InitializeComponent`:

```
Microsoft.UI.Xaml.Markup.XamlParseException: XAML parsing failed.   (HResult 0x802B000A)
   at Microsoft.UI.Xaml.Application.LoadComponent(Object component, Uri resourceLocator, ComponentResourceLocation componentResourceLocation)
   at Rigbay.Module.Xaml.SamplePage.InitializeComponent() in ...\SamplePage.g.i.cs:line 41
```

The generated code asks for `ms-appx:///Rigbay.Module.Xaml/SamplePage.xaml`. In an unpackaged app `ms-appx:///` is the **host exe folder**, and the compiled page (`SamplePage.xbf`) is only in the module's `obj`. Two workarounds, both tested:
1. Copy `SamplePage.xbf` to `<host folder>\Rigbay.Module.Xaml\SamplePage.xbf`. Works, but it puts module files next to the host exe.
2. Ship the `.xbf` inside the module folder (a small MSBuild target in `Rigbay.Module.Xaml.csproj` does it) and call `Application.LoadComponent` yourself with a module-relative URI, `ms-appx:///modules/Rigbay.Module.Xaml/Rigbay.Module.Xaml/SamplePage.xaml`. Works (the page loaded in the final runs through this path, `InitializeComponent` failing first and the catch taking over). It relies on the module knowing where it sits relative to the host.

Both depend on `ms-appx` details and on the XAML compiler's output layout, so I would not build the platform on them.

**Fault boundary**

| Case | Result |
| --- | --- |
| Module `Probe`/`CreatePage` throws (`Rigbay.Module.Broken`) | PASS. Caught at the host call; shown as a red slot with the reason on the dashboard and on its page. Host created all pages at start, so the dashboard is honest before a click. |
| Module DLL missing or without `IRigbayModule` | Handled in the loader (failed state with reason); only the "no class implements" text path was not exercised. |
| Exception in a handler raised by the XAML core (tested with a `Loaded` handler) | PASS. Goes to `Application.UnhandledException`; with `e.Handled = true` the app continues. |
| Exception in `async void` after an await | PASS. Same route, app continues. |
| Exception thrown inside a callback queued with `DispatcherQueue.TryEnqueue` | **FAIL. The process dies**: exit code `-1073741189` (0xC000027B), faulting module `CoreMessagingXP.dll`; `Application.UnhandledException` is not raised. Reproduced with `--crash-probe dispatcher`. |
| Click handler exception through UI Automation `Invoke` | The exception came back to the caller of `Invoke`. A real mouse click on a module's button was not tested. |

Consequence: the host must give modules a wrapped `RunOnUi` (added to `IHostServices`; the renderer uses the same pattern) and tell module authors not to use a raw dispatcher. That cannot be enforced against a malicious or careless module, which is in line with DESIGN.md (not a sandbox).

## (c) Logon task with highest privileges: PASS (without a real logon)

`schtask-test.ps1`, elevated, `-Variant fd`:

```
schtasks /create /tn RigbaySpike /sc onlogon /rl highest /tr "<exe> --log <log> --selftest --exit-after 9000" /f
   SUCCESS: The scheduled task "RigbaySpike" has successfully been created.
   task XML: <LogonType>InteractiveToken</LogonType> <RunLevel>HighestAvailable</RunLevel> <LogonTrigger>
schtasks /run /tn RigbaySpike
   process: pid 27820, session 1, MainWindowHandle 0x1F6073E, MainWindowTitle 'Rigbay', responding=True
   still running after 11 s: True
   host log: SELFTEST PASS elevated: IsUserAnAdmin=True ... pid=27820
schtasks /delete /tn RigbaySpike /f   -> SUCCESS; task gone: True
```

The task ran in session 1 (the interactive desktop), the process was elevated and had a responding window titled Rigbay, with no prompt. The task was deleted afterwards; no task and no process were left.

**Not verified:** a real logoff and logon (I cannot do it); that the logon trigger fires at that moment; start-up with other programs competing at logon; a delay for the shell to be ready (a tray icon added at logon may need a retry if Explorer is not up yet).

## Visual result (screenshots of the window only, `docs/spike/`)

Shell shaped after `docs/UI-DIRECTION.md` section 3: 48 px title bar with the product mark and a search box, a `NavigationView` on the left (Dashboard, then groups Power / Monitoring / Other built from each module's `Group`, empty groups hidden), a sheet (hairline border, layer fill, 8 px top corners, content at most 1000 px wide) and a status line with host, Abstractions, Windows App SDK, .NET and elevation. Rigbay's own element is the **bay slot** mark: a 5 px bar per module, filled green when ready, filled red when failed, outline only when unsupported or empty; the dashboard is a rack of slim rows with state, probe or error text and, where the module has one, its first live reading in tabular figures. Rows are stock `SettingsCard` and `SettingsExpander`, no icons per setting, no cards around groups, no gradients or shadows of my own.

| State | Dark | Light |
| --- | --- | --- |
| Dashboard (rack, one failed module, one live reading, empty bay) | `dashboard-dark.png` | `dashboard-light.png` |
| Declarative module page (enable row, groups, expander, custom control, info bar) | `rows-dark.png` | `rows-light.png` |
| Failed module | `failed-dark.png` | |
| No modules at all | | `empty-light.png` |
| Vietnamese UI | `dashboard-vi.png` | |

Found and fixed by looking: secondary text was almost invisible in the light theme because brushes looked up from code follow the app theme, not the window's; they are now `ThemeResource` styles in XAML, and the caption buttons got explicit per-theme colours. A module whose page fails was green on the dashboard until clicked; pages are now built at start. Contrast was judged by eye on the PNGs, not measured (4.5:1 not verified numerically). The accent colour is the user's Windows accent (cyan here).

## Extra probes

| Probe | Result | Detail |
| --- | --- | --- |
| **Community Toolkit `SettingsControls` unpackaged + elevated + dynamically loaded modules** | **PASS** | `SettingsCard` and `SettingsExpander` render with theme, work with a card created from a module's control (`ContentAlignment=Vertical`), on 1.8 and 2.6.4, framework-dependent and self-contained. Needs no `App.xaml` entry beyond `XamlControlsResources`. The package declares Windows App SDK 1.6 as its minimum. |
| Tray icon | PASS (add) | `Shell_NotifyIconW` NIM_ADD returned true from the elevated app, using a subclassed window procedure. A real click and a context menu were not tested; icon visibility in the tray was not looked at. |
| Global hotkey | PASS | `RegisterHotKey` Ctrl+Alt+F24 on the window's HWND; the host pressed the chord with `SendInput` (6 of 6 events) and received `WM_HOTKEY`. A physical keyboard was not used. |
| Toast | PARTIAL | `AppNotificationManager.Register` and `Show` returned without error from the unpackaged elevated app; whether a toast appeared on screen was not checked (no screen capture allowed). |
| Single instance | PASS | Named mutex, see (a). |
| Theme | PASS | Dark and light both rendered (`--theme`); default follows Windows (`ActualTheme=Dark` here). Switching Windows theme while running was not tested. High contrast not tested. |
| DPI | PARTIAL | Only 96 dpi (100%) was rendered; `GetDpiForWindow` and `RasterizationScale` are logged. 150% and 200% not verified. |
| Language at run time | PASS | `lang\en.lang` and `vi.lang` (`@code`/`@name` header, `key = text`), button in the status line switches all host strings without restart (`dashboard-vi.png`). |
| Search | PASS (index) | Title-bar `AutoSuggestBox` over an index built at run time from module names and row headers and descriptions (18 entries in the sample). Typing into it was not automated; the index size is what was checked. |

## What was not verified

- A real logoff/logon through the task (see (c)).
- A machine without the Windows App Runtime or the .NET 10 runtime (framework-dependent failure is expected, not seen).
- 150% and 200% DPI, high contrast, switching the Windows theme live, multiple monitors.
- A real mouse click on a button inside a module (the throw path through a click); real tray clicks; the toast on screen.
- A module compiled against one Windows App SDK version loaded into a host of another (all modules were rebuilt with each host version).
- Hot reload, unload or update of a module in-process (not possible with non-collectible contexts; restart is the plan in DESIGN.md anyway).
- `net48` modules (the XTU contract DLLs) in this host; nothing here tests them.
- Numeric contrast, screen reader, keyboard navigation of the shell.
- Trimming or compressing the self-contained output.

## Recommendation: WinUI 3 is go

1. **Use WinUI 3, unpackaged, elevated, loading modules in-process.** (a), (b) and (c) pass; the toolkit's `SettingsCard` and `SettingsExpander` work in this setup, so the PowerToys-like look in `docs/UI-DIRECTION.md` is reachable.
2. **Module pages: level 1 declarative page model is the default** (see `ABSTRACTIONS-NOTES.md`). It needs no WinUI type in `Abstractions`, avoids the `ms-appx` problem, gives search and language keys for free, and is what the sample module's 13 rows exercised.
3. **Level 2 is a control built in code**, returned from a host-called `CreateCustomControl`. It works across the load-context boundary and picks up the host's theme resources. Do not use compiled XAML in modules (works only through a workaround that depends on the `ms-appx` layout); `XamlReader.Load` is acceptable for simple static markup.
4. **Windows App SDK line:** 1.8 and 2.6.4 both worked. Pick one and make the Abstractions version imply it, because a module's WinUI types bind to that version. 1.8 is the conservative choice here (2.x showed a slow very first start; 1.8 self-contained had one unexplained early crash).
5. **Deployment is the open decision for the owner:** framework-dependent is small (43.5 MB) but needs the Windows App Runtime and .NET 10 installed (an installer step, a system change); self-contained is 220 to 240 MB and needs nothing. Not decided here.
6. **Limits to tell module authors:** never throw inside a raw `DispatcherQueue` callback (process dies), use the host's `RunOnUi`; modules are in-process admin code, not sandboxed; a module cannot be unloaded.

No hard blocker was found, so the WPF alternative was not built or tested.
