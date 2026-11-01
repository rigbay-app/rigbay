# Rigbay.Abstractions v0.1: the module contract

Status: v0.1 (task A2), updated with the findings of spike A1 (`docs/spike/ABSTRACTIONS-NOTES.md`). Everything is in the namespace `Rigbay.Abstractions`. The assembly `Rigbay.Abstractions` is the only
thing a module references. `Rigbay.Core` (the host logic) and the WinUI shell are written against it. Where this
file and the code disagree, fix the one that is wrong and say so in the commit message.

Contents: 1 Principles, 2 Module package and manifest, 3 Lifecycle, 4 Host services, 5 Page model,
6 Metrics bus, 7 Localisation, 8 Settings, 9 Commands, hotkeys, tray, toast, 10 Fault boundary and loading,
11 API version, 12 Rules for module authors, 13 How to write a module, 14 Reasons behind the choices,
15 What is not in v0.1, 16 Using Rigbay.Core.

## 1. Principles

- **Small.** A module author reads this file once and has everything. Anything that can be added later without
  breaking modules is left out now.
- **No UI framework.** `Rigbay.Abstractions` targets `net8.0`, references nothing but the BCL and has no Windows
  App SDK type. A module that only reads sensors does not need a UI stack, and the contract survives a change of
  UI framework (the WinUI 3 spike passed, but the framework stays out of the contract).
- **Declarative first.** A module describes its page as data (`PageModel > RowGroup > Row`). The host draws it,
  so every module looks the same, follows theme, DPI and language, and the host can search it. A module that
  needs more supplies one custom control through an escape hatch (section 5.5).
- **Trust is in the install step, not in the contract.** A module runs as administrator inside the host. The
  contract cannot sandbox it. What it does is make mistakes survivable (fault boundary) and visible (manifest
  `requires`, log).

## 2. Module package and manifest

A module is a folder, installed under the host's admin-only modules root:

```
<modules root>\
  steam-access\                  folder name == manifest id
    module.json                  read first, before any module code is loaded
    SteamAccess.dll              entry assembly
    SomeDependency.dll           its own dependencies, next to it (and SteamAccess.deps.json if present)
    lang\en.lang  lang\vi.lang   optional language files
```

Do not put `Rigbay.Abstractions.dll` in the folder. If it is there, the host ignores it and uses its own copy
(type identity: `IModule` must be the same type on both sides).

### 2.1 module.json

```json
{
  "manifestVersion": 1,
  "id": "steam-access",
  "name": "Steam access",
  "version": "0.3.1",
  "author": "A. Person",
  "description": "DNS and DPI workaround for networks that block Steam.",
  "entry": { "assembly": "SteamAccess.dll", "type": "SteamAccess.SteamModule" },
  "hostApi": ">=0.1 <0.2",
  "group": "network",
  "requires": {
    "admin": true,
    "network": ["tcp:18081 (local proxy)", "dns-over-https"],
    "registry": ["HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings"],
    "services": []
  },
  "languages": "lang",
  "homepage": "https://github.com/rigbay-app/module-steam-access"
}
```

| Field | Required | Rule |
| --- | --- | --- |
| `manifestVersion` | yes | Integer, must be `1`. A higher value is refused with "newer manifest format". |
| `id` | yes | Lowercase `[a-z0-9]` words joined by single `-`, 2 to 40 characters, equal to the folder name. Reserved: `rigbay`, `host`, `help`, `list`, `version`. It is the prefix of language keys, the settings folder name and the `rigbay <id> <verb>` command word. |
| `name` | yes | 1 to 60 characters, shown before any code runs. A language file may override it with the key `module.<id>.name`. |
| `version` | yes | Semantic Version 2.0 (`1.2.3`, `1.2.3-beta.1`). |
| `author` | yes | Free text, 1 to 100 characters. |
| `description` | yes | One sentence, up to 200 characters. Override key `module.<id>.description`. |
| `entry.assembly` | yes | A file name (no directory part) ending in `.dll`, present in the folder. |
| `entry.type` | yes | Full name of a public class that implements `IModule` and has a public parameterless constructor. |
| `hostApi` | yes | Range of host API versions the module supports, see section 11. |
| `group` | yes | One of `power`, `cooling`, `network`, `display`, `monitoring`, `other`. Decides where the module sits in the navigation. A module cannot add a group. |
| `requires` | no | Informational list of what the module uses: `admin` (bool), `network`, `registry`, `services` (arrays of short strings). Shown to the user before the first enable. It is a declaration, not a sandbox, and the host does not enforce it. |
| `languages` | no | Folder with `*.lang` files, relative to the module folder. Default `lang`. |
| `homepage` | no | `http` or `https` URL. |

Unknown fields are ignored (a warning is logged) so a newer manifest still loads on an older host. The host
validates every field and reports all problems at once; a module with an invalid manifest is listed as
"invalid" with the problems, and no code from it is loaded.

## 3. Lifecycle

```
                 module.json read, validated, hostApi range checked   (no module code yet)
                                  |
              invalid ------------+------------ api mismatch
                 |                |                  |
              Invalid             |              Incompatible        (refused, reason shown)
                                  v
                    load context created, entry type instantiated
                                  |
                          IModule.Probe()   <- "does this machine support me?"
                                  |
                 unsupported -----+----- supported
                      |                      |
                 Unsupported                Off  <------------------------------+
              (reason key shown)             |   Disable(): Shutdown()          |
                                             | Enable()                          |
                                             v                                   |
                                    IModule.Initialize(host)                     |
                                             |                                   |
                                             v                                   |
                                          Running ---------- user disables ------+
                                             |
                       any exception, timeout, or bad result from a module call
                                             v
                                          Faulted   (module disabled, log written, reason shown;
                                                     cleared on the next host start, not before)
```

Rules:

1. The constructor does nothing but set fields. No hardware access, no file access, no threads.
2. `Probe()` is called once per host start, before the user is shown the module as available, whether or not it
   is enabled. It must be read-only and quick (it is given 10 s). It returns `ProbeResult.Yes()` or
   `ProbeResult.No(reasonKey)`; the reason key is a module-relative language key shown to the user, for
   example `probe.no-service` -> "The Acer gaming service is not installed". Throwing is a fault, not "unsupported".
3. `Initialize(IHostServices host)` is called when the module is enabled and was probed as supported (10 s limit).
   Register hotkeys, tray items, commands and metrics subscriptions here, start timers, read settings. Hardware
   is not written during `Initialize` except to restore state the user chose.
4. `GetPageModel(host)` is called after `Initialize` succeeds and returns the page model (section 5); `CreateCustomControl(host, controlId)` when a `CustomRow` is drawn. Both have default bodies that return null.
5. `Shutdown()` is called when the user disables the module and when the host exits (5 s limit). Stop timers,
   release handles. The host removes whatever the module registered (hotkeys, tray items, commands,
   subscriptions) after `Shutdown`, so a module need not undo them.
6. Enabling again after disabling calls `Initialize` on the same instance. Module code must tolerate that.
7. Install, update and removal of a module need a host restart, so the loader never unloads a module in a
   running host; `Shutdown` is not an unload.
8. The constructor, `Probe`, `Initialize` and `Shutdown` run on a thread-pool thread (so the host can enforce the
   time limits), never on the UI thread. A call that exceeds its limit faults the module; its thread is left behind.
   `Shutdown` after a fault is attempted once, in the background, and cannot fault the module again.
9. A module cannot be enabled while it is `Invalid`, `Incompatible`, `Unsupported` or `Faulted`. The user's choice
   (enabled or not) is kept in `%LOCALAPPDATA%\Rigbay\host.json` and applied by `RigbayHost.StartEnabled()` at the next
   start; a fault does not change it.

Bay states (section 5.6) are derived from this lifecycle: `Invalid` and `Faulted` show as Failed, `Incompatible` and
`Unsupported` as Unsupported, `Running` as Ready, `Off` as Off.

## 4. Host services (`IHostServices`)

Passed to `Initialize`; one instance per module (so the host knows who is calling). Members:

| Member | What it gives | Section |
| --- | --- | --- |
| `ModuleId`, `ModuleDirectory`, `DataDirectory` | Identity and the two folders: the install folder (read-only) and `%LOCALAPPDATA%\Rigbay\modules\<id>` (module data, created on first use). | |
| `StopToken` | Cancelled when `Shutdown` starts. Pass it to background work. | 3 |
| `Settings` | `ISettingsStore`: load and save one typed settings object. | 8 |
| `Localisation` | `ILocalisation`: `T(key)`, `F(key, args)`, current language, change event. | 7 |
| `Log` | `ILog`: `Debug/Info/Warn/Error`. Written to `%LOCALAPPDATA%\Rigbay\logs`. | 10 |
| `Hotkeys` | `IHotkeyService`: register named actions with a default gesture; the user can rebind; conflicts across modules are detected. | 9 |
| `Tray` | `ITrayService`: add or remove menu items. | 9 |
| `Commands` | `ICommandRegistry`: add verbs to `rigbay <module> <verb>`. | 9 |
| `Toast` | `IToastService`: show a notification. | 9 |
| `Metrics` | `IMetricsBus`: publish readings, subscribe to others'. | 6 |
| `Approval` | `IHardwareApproval`: ask the user to confirm a hardware write the module starts itself (rows marked `HardwareEffect` are confirmed by the host without this). | 5.4 |
| `RunOnUi(Action)` | **Mandatory way to reach the UI thread.** Runs the action there inside the fault boundary. A module never uses a dispatcher of its own. | 5.3 |
| `HostVersion` | Version of the host application (informational; compatibility is `HostApi.Current`). | 11 |

No member of any service takes or returns a UI framework or Windows type. Where a UI concept is needed it is plain
data: colours are `SemanticColor` names (`Ok`, `Warning`, `Error`, `Temperature`, ...), icons are glyph names or file
paths, text is a language key. There is no "main window" or window handle for modules in v0.1: hotkeys, tray items,
toasts and command verbs are host services described as data, and the host owns the window.

All services are safe to call from any thread. Callbacks you give to a service (hotkey, tray, command,
subscription, button) may run on any thread, and always run inside the fault boundary.

## 5. Page model

Everything in this section lives in namespace `Rigbay.Abstractions`. Rows are C# records; text is always a
module-relative language key; colours are `SemanticColor` names; icons are glyph names or file paths. No type from
WinUI, XAML, `Windows.*` or WPF appears anywhere in the assembly, and none may appear in a host service.

### 5.1 Shape

`IModule.GetPageModel(host)` returns the page, after `Initialize` succeeded (null for a module with no page).

```
PageModel
  Icon : string?                      glyph name or path for the navigation pane
  Groups : RowGroup[]                 RowGroup(HeaderKey, Rows)
  Links : PageLink[]                  PageLink(TextKey, Url), "Learn more" at the bottom
  Summary : PageSummary?              what the dashboard bay shows: StateLine (observable) + up to two MetricIds
```

The host owns the first row of every module page (the enable switch with name and one-line help from the manifest)
and disables every group below it while the module is off. "Off" means `Shutdown` was called: the module has no
background work. A module never draws its own enable switch.

### 5.2 Rows (level 1)

Every `Row` has `HeaderKey`, optional `DescriptionKey` (the host indexes both for search), `Enabled` (an
`IObservableValue<bool>`, default true) and `HardwareEffect` (5.4). Kinds:

| Row | Control the host draws | Value |
| --- | --- | --- |
| `ToggleRow` | switch | `IObservableValue<bool>` |
| `ChoiceRow` | drop-down | `Options` (`ChoiceOption(Id, LabelKey)`), selected option id in `IObservableValue<string>` |
| `NumberRow` | number box | `IObservableValue<double>`, `Minimum`, `Maximum`, `Step`, `Unit` (`MetricUnit`), `Apply`, `Validate` |
| `TextRow` | text box | `IObservableValue<string>`, `MaxLength`, `PlaceholderKey`, `Apply`, `Validate` |
| `ShortcutRow` | key capture | `IObservableValue<HotkeyGesture?>`, normally `IHotkeyBinding.Gesture`; `Apply` |
| `ButtonRow` | button | `InvokeAsync(ct) -> Task<ActionResult>`, `ButtonKey`, optional `RevertAsync` |
| `ValueRow` | read-only text | `IObservableValue<string>` (already localised by the module) |
| `ReadingRow` | live number with unit | `MetricId` (host subscribes to the bus and formats), `Decimals`, `Color` |
| `ExpanderRow` | row that folds `Children` | `IsExpanded` initially |
| `InfoRow` | inline message | `Severity` (info, success, warning, error), `IsOpen`; header = title, description = text |
| `CustomRow` | whatever the module builds | `ControlId` (5.5) |

### 5.3 Values and threads

`IObservableValue<T>` has a settable `Value` and an event `Changed`. Either side may set it. **`Changed` is raised
synchronously on the thread that set the value**, after the new value is stored; the host marshals to the UI thread
when it needs to. `ObservableValue<T>` is a ready implementation (equal values do not raise the event).

- **Module to UI:** the module sets `Value` from a timer thread or after a hardware read; the host redraws on the UI thread.
- **UI to module:** the user changes a control; the host sets `Value` on the UI thread, inside the fault boundary; the
  module's `Changed` handler does the work. If the work fails or the value must be clamped, set `Value` back.
- A module must reach the UI thread only through `IHostServices.RunOnUi(Action)`, never through a dispatcher of
  its own. The spike proved why: an exception thrown inside a raw `DispatcherQueue` callback ends the whole process
  without reaching any handler. `RunOnUi` runs the action inside the fault boundary.

### 5.4 Hardware effects, approval, apply and revert

A row that changes the state of the machine sets `HardwareEffect = new HardwareEffect("effect.turbo")` (a description
key; for example "Sets the fans to maximum. Noise will increase."). `ChangesHardware` is then true. The host, before
it applies such a change: shows the effect, asks for confirmation through its approval service, remembers the previous
value, and shows "Revert" next to the control (value rows: sets the previous value back; `ButtonRow`: calls
`RevertAsync` when `Revertible` and the module provides it). A module that starts a write by itself (after a hotkey or
a command) asks `IHostServices.Approval.ConfirmAsync(effect)`; when the host has no way to ask, the answer is no. None
of this replaces the module's own checks: refuse dangerous values inside the module.

`NumberRow`, `TextRow` and `ShortcutRow` have `Apply`:

- `ApplyMode.Immediate` (default): the host sets the value as the user edits. Right for toggles, choices and values
  that are safe to change live.
- `ApplyMode.ApplyRevert`: the host keeps a draft; the control shows **Apply** and **Discard** while the draft differs.
  Apply checks `Minimum`/`Maximum`/`MaxLength`, then `Validate` (the module's check, returns an error text or null),
  then confirmation if `ChangesHardware`, then sets `Value`. The previous value is kept so **Revert** can put it back.
  Use it for every number, text or shortcut that writes to hardware (power limits, fan curves points). A toggle or
  choice that changes hardware is applied after confirmation and also gets Revert. `Rigbay.Core` implements this as
  `ValueEditor<T>`; the UI only binds to it.

### 5.5 Custom controls (level 2)

`CustomRow(controlId)` makes the host call `IModule.CreateCustomControl(host, controlId)`. The result is an `object`
and must be a control of the host's UI framework (WinUI 3: `Microsoft.UI.Xaml.UIElement`); the host checks the type
and shows an error row if it is not, or if the call throws. Build the control in code, with the host's theme resources;
it appears inside a row. It is for things a row cannot express: a curve editor, a graph, an activity log. A module that
does this references the same Windows App SDK line as the host (the host's line is part of the host release notes),
so it is an optional second package; the spike A1 notes explain the limits.

**Compiled XAML pages are not a supported route.** A compiled page asks for `ms-appx:///...`, which in an unpackaged
app is the host's folder, so loading fails with `XamlParseException 0x802B000A` unless files are placed there by
hand. `XamlReader.Load` of a string works for static markup without code-behind, events or `x:Bind`.

### 5.6 Dashboard and bay states

The Dashboard shows one bay per module. `PageModel.Summary` supplies the state line and up to two readings (from
the metrics bus). The state of a bay is from the closed set `BayState`:

| State | Meaning | Reason shown |
| --- | --- | --- |
| `Ready` | enabled, initialised, working | |
| `Off` | supported, switched off by the user | |
| `Unsupported` | probe said no, or the module's `hostApi` range does not match | the probe's reason, or "needs host API ..." |
| `Failed` | invalid manifest, load error, or an exception/timeout in the module | the error |
| `UpdateAvailable` | a newer version is in the registry (set by the installer) | |
| `RestartRequired` | an install, update or removal waits for a restart (set by the installer) | |

`Rigbay.Core` computes the state (`ModuleEntry.Bay`); precedence is Failed, Unsupported, RestartRequired,
UpdateAvailable, then Ready or Off.

## 6. Metrics bus

- `MetricId`: a lowercase dotted id such as `cpu.temp`, `gpu.fan.rpm`, `cpu.power.pl1` (segments of `[a-z0-9_-]`,
  at most 64 characters). Ids are a shared vocabulary, not module-private, so that a second sensor source (AMD,
  another vendor) can publish `cpu.temp` and the recorder and the Dashboard need no change. `WellKnownMetrics`
  lists the agreed ids. A module-specific id should start with the module id: `steam-access.proxy.connections`.
- `MetricUnit`: `None`, `Celsius`, `Watt`, `Rpm`, `Percent`, `Megahertz`, `Volt`, `Ampere`, `Bytes`,
  `BytesPerSecond`, `Milliseconds`, `Seconds`, `Count`. Section 15 explains why it is a closed list.
- `MetricSample`: `Id`, `Value` (double), `Unit`, `Timestamp` (`DateTimeOffset`, set by the host), `SourceModule`
  (set by the host; a module cannot claim to be another).
- `IMetricsBus.Publish(id, value, unit)`; `Subscribe(handler)` for all samples or `Subscribe(id, handler)`; both
  return an `IDisposable`. `TryGetLatest(id, out sample)` and `GetLatest()` read the cache, which holds the last
  sample of every id, so a page that opens late shows a value at once.
- NaN and infinity are dropped and logged. If two modules publish the same id, the newest sample wins and
  `SourceModule` says who. Delivery to subscribers is synchronous on the publisher's thread, each subscriber in
  its own module's fault boundary: a slow subscriber slows the publisher, so keep handlers short and hand off.

## 7. Localisation

Language files are the same format as in xtu-profile-switcher (`Loc.cs`, `lang\*.lang`):

```
# comment
@code = vi
@name = Tiếng Việt
module.steam-access.proxy.header = Proxy cục bộ
```

UTF-8, `key = text`, `#` starts a comment, `\n` a line break, `\\` a backslash, `{0}` `{1}` placeholders that a
translation may reorder but must keep. English is the reference. Lookup: current language, then English, then the
key itself (a typo in code is visible on screen).

- **Keys in a module's files are `module.<id>.<name>`.** In code the module uses the short form (`proxy.header`);
  the per-module `ILocalisation` adds the prefix. A key outside the prefix in a module's file is ignored and logged,
  so a module can neither override host strings nor another module's.
- **Where files live:** the module's `languages` folder; the host's own files; optional user files in
  `%LOCALAPPDATA%\Rigbay\lang\` that add a language or override single keys.
- **Checking:** `Rigbay.Core` has `LangCheck` and the console tool `Rigbay.LangCheck` (`--langcheck`). It reports,
  per language: missing keys (warning, shown in English), unknown keys that English does not have (error, probably
  a typo), placeholder mismatches (error), keys without the module prefix (error) and, when given the module's
  source folder, English keys that no string literal in the source uses (warning, unused).
- `ILocalisation.F` falls back to the English text when a translation has a broken placeholder, so a bad
  translation never crashes a page.

## 8. Settings

`ISettingsStore` keeps one JSON file per module at `%LOCALAPPDATA%\Rigbay\modules\<id>\settings.json`:

```json
{ "version": 2, "data": { "profile": "quiet", "startMinimised": true } }
```

- `T Load<T>()` returns the stored object, or `new T()` (the defaults) if there is no file; properties missing from
  the file keep their defaults, unknown ones are ignored. `Load<T>(out int storedVersion)` also tells the module
  which version wrote the file, for migrations.
- `Save<T>(value, version = 1)` writes atomically (temporary file in the same folder, then replace), so a crash or
  power loss leaves the old file or the new one, never half of one.
- A corrupt file (not JSON, wrong shape) is renamed to `settings.json.corrupt-<time>`, logged, and defaults are
  returned. This never throws into the module.
- Settings are for the user's choices. Do not store secrets there; they are plain text.
- Serialisation is `System.Text.Json` with camelCase names and enums as strings. Use simple classes with public
  settable properties.

## 9. Commands, hotkeys, tray, toast

**Commands.** `rigbay <module> <verb> [args]`. A module registers verbs in `Initialize`:
`host.Commands.Register("status", "cmd.status", ctx => ...)`. A handler receives `CommandContext` (`Args`,
`CancellationToken`) and returns `CommandResult.Ok(text)`, `CommandResult.Failed(text)` or
`CommandResult.Usage(text)`. Exit codes: **0** ok, **1** failed (the handler said so, or threw: then the module is
also faulted), **2** not found or bad usage (unknown module, module not running, unknown verb, `Usage`). Output is
text on standard output; failures write to standard error. Verbs are `[a-z][a-z0-9-]*`. `Rigbay.Core` parses and
dispatches; carrying the command line to the running host process belongs to the app.

**Hotkeys.** `host.Hotkeys.Register(new HotkeyRegistration("toggle-turbo", "hotkey.toggle-turbo", defaultGesture, () => ...))`
returns an `IHotkeyBinding` with `Gesture` (observable, user-changeable, nullable = unbound) and `HasConflict`
(true while another module or the host holds the same gesture). A `HotkeyGesture` is modifiers (`Ctrl`, `Alt`,
`Shift`, `Win`) plus a key name (`A`..`Z`, `0`..`9`, `F1`..`F24`, `Space`, `Enter`, `Tab`, `Esc`, `Backspace`,
`Delete`, `Insert`, `Home`, `End`, `PageUp`, `PageDown`, `Up`, `Down`, `Left`, `Right`, `Num0`..`Num9`), written
`Ctrl+Alt+F5`. The registry is data only: it stores the gestures, detects conflicts and persists the user's
rebinding in `%LOCALAPPDATA%\Rigbay\hotkeys.json`. Registering with the OS (and ignoring conflicting gestures
rather than registering them) belongs to the UI host.

**Tray.** `host.Tray.Add(new TrayItem("toggle", "tray.toggle", () => ...))`: `Id`, `TextKey`, a callback, and
observable `Visible`, `Enabled`, `Checked` (with `Checkable`) and an optional `Icon` (a glyph name or path). Data only; the app draws the menu, grouped by module.

**Toast.** `host.Toast.Show(new ToastMessage(title, body, ToastKind.Warning))`. Interface only: the app supplies the
implementation. Titles and bodies are `LocalisedText` (a module-relative key plus optional arguments).

## 10. Fault boundary, loading, logging

- **Boundary.** Every call from the host into a module (`Probe`, `Initialize`, `GetPageModel`, `Shutdown`, a value
  setter, a button, a hotkey, a tray item, a command, a metrics subscriber, `CreateCustomControl`, a `RunOnUi` action) runs in the
  boundary. An exception, or a timeout on the lifecycle calls, puts the module in `Faulted`: the host stops calling
  it (except `Shutdown`, once, best effort), removes its hotkeys, tray items, commands and subscriptions, writes the
  exception with its stack to the log, and raises `ModuleFaulted` so the app can say so. The host process continues.
- **Not faults:** `OperationCanceledException` after `StopToken` was cancelled; a `ButtonRow` or command returning
  a failed `ActionResult`/`CommandResult`. Expected failures (service not running, device busy) are results, not
  exceptions.
- **What the boundary cannot do:** stop an infinite loop on a module's own thread, a stack overflow, an
  access violation in native code, or an unhandled exception on a thread the module started itself (that kills the
  process like any .NET program). Catch exceptions on your own threads.
- **Loading.** One `AssemblyLoadContext` per module. `Rigbay.Abstractions` always comes from the host's default
  context. The module's other dependencies are resolved from its own folder (via its `deps.json` when there is one,
  or by file name), including native libraries. Framework assemblies come from the host's runtime.
- **Logging.** One file, `%LOCALAPPDATA%\Rigbay\logs\rigbay.log`, `rigbay.1.log`, `rigbay.2.log`; at 1 MiB the
  file is rotated, three files are kept. Line format `2026-11-01 17:40:12.345 INFO  steam-access  message`. The
  host keeps the last 500 entries in memory for the Log page. Do not log secrets.

## 11. API version

`HostApi.Current` is `0.1`. A module declares the range it supports as a string of space-separated comparators, all
of which must hold: `>=0.1 <0.2`, `>=1.0 <2.0`, `=0.1`, `>0.1`. A bare version means `=`. Versions are
`major.minor` (a third number is accepted and ignored for ordering of the minor).

Policy: while the major number is 0 any minor version may break modules, so a module declares `>=0.1 <0.2` and
is rebuilt for each. From 1.0 on, a minor version only adds, a major version may break. The host refuses a
module whose range does not contain `HostApi.Current`, before loading any of its code, and shows
"Needs host API >=0.1 <0.2, this is Rigbay 0.3" (the module is listed as `Incompatible`). A module may also
read `HostApi.Current` and adapt.

## 12. Rules for module authors

1. Reference `Rigbay.Abstractions` only. Set `<Private>false</Private>` / `ExcludeAssets="runtime"` so its DLL is not copied
   into your output.
2. Target `net8.0` or later. Do not reference WinUI, WPF or Windows Forms except in the code of a `CreateCustomControl` control, and then only the Windows App SDK line the host was built with. Do not ship compiled XAML.
3. Constructors do nothing. Probe is read-only. Initialize registers things.
4. Never block a callback for long, and reach the UI thread only through `host.RunOnUi` (never a dispatcher of your own: an exception in a raw dispatcher callback kills the process). `Changed` handlers run on the setter's thread, which is the UI thread when the user edits a control: start work in
   the background and return.
5. Any write to the machine is `HardwareEffect`-marked (so the host confirms and offers revert), uses `ApplyMode.ApplyRevert` for numbers, text and shortcuts, and is clamped and validated inside the module too. Reading is free.
6. Catch exceptions on threads and timers you start. Do not throw for an expected failure; return a result.
7. All user-visible text is a language key. Put `en.lang` first; keys are `module.<id>.<name>`.
8. No static state that survives `Shutdown`, no reliance on the current directory (use `ModuleDirectory` and
   `DataDirectory`), no `Environment.Exit`, no unhandled-exception handlers, no changes to the host's culture.
9. Do not store secrets in settings or the log.

## 13. How to write a module

A complete working module is `samples/Rigbay.SampleModule`; copy it.

1. **Project.** A class library for `net8.0`:

   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net8.0</TargetFramework>
       <Nullable>enable</Nullable>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="Rigbay.Abstractions" Version="0.1.*" ExcludeAssets="runtime" />
       <None Include="module.json;lang\*.lang" CopyToOutputDirectory="PreserveNewest" LinkBase="." />
     </ItemGroup>
   </Project>
   ```
2. **Manifest.** Write `module.json` (section 2). Pick an id, a group, a `hostApi` range.
3. **Class.**

   ```csharp
   public sealed class SampleModule : IModule
   {
       private readonly ObservableValue<bool> _echo = new(false);

       public ProbeResult Probe() => ProbeResult.Yes();               // or ProbeResult.No("probe.no-service")
       public void Initialize(IHostServices host) { /* settings, registrations, timers */ }
       public PageModel? GetPageModel(IHostServices host) => new PageModel
       {
           Groups = new[]
           {
               new RowGroup("group.main", new Row[]
               {
                   new ToggleRow("row.echo.header", _echo) { DescriptionKey = "row.echo.description" },
                   new ReadingRow("row.temp.header", WellKnownMetrics.CpuTemp) { Decimals = 0 },
               }),
           },
       };
       public void Shutdown() { }
   }
   ```
4. **Language file** `lang\en.lang` with `module.<id>.group.main = ...` and so on; add `vi.lang` if you can.
5. **Check.** `dotnet run --project src/Rigbay.LangCheck -- <your lang folder> --module <id> --source <your source folder>`.
6. **Try.** Build; copy the output folder to `<modules root>\<id>\`; start the host. Read `rigbay.log` first when
   something does not appear: manifest problems, refused API ranges and faults all end up there.
7. **Package.** Zip the folder (manifest at the root of the zip), attach it to a GitHub release, and add the zip's
   SHA256 to the `registry` repo by pull request.

## 14. Reasons behind the choices

- **Declarative pages.** PowerToys-style settings are, nearly without exception, a row of header, description and
  one control. Describing rows as data gives one look, search, localisation and theme for free, and avoids the
  hardest part of the WinUI spike (resources of a dynamically loaded assembly) for the common case.
- **`object` for the custom control, asked for by a row.** The alternative, an abstract UI base class in
  Abstractions, would tie the contract to one framework. The spike showed a control built in code crosses the
  load-context boundary and picks up host theme resources; a compiled XAML page does not (the `ms-appx` layout).
- **Observable values, not property-changed bindings.** `INotifyPropertyChanged` needs no UI assembly either, but a
  two-member interface (`Value`, `Changed`) is easier to implement correctly by someone who does not write Windows apps
  for a living, and it does not suggest XAML binding semantics. `Changed` fires on the setter's thread (documented rule)
  because the contract cannot know the UI thread.
- **`RunOnUi` and no dispatcher.** Measured in the spike: an exception in a raw `DispatcherQueue` callback ends the
  process; a wrapped callback becomes a faulted module.
- **Apply-or-revert for numbers, text and shortcuts.** A power limit typed one digit at a time must not reach the
  firmware at every keystroke. The draft lives in the host (`ValueEditor<T>`), so modules stay simple.
- **A closed set of bay states.** The dashboard can show every module honestly with one small vocabulary.
- **Short keys in code, long keys in files.** Short keys keep module code readable; long keys keep files unambiguous
  once the host merges them with the host's and other modules'.
- **Manifest before code.** The user sees what a module declares, and an incompatible or broken module is refused
  without running any of its code.
- **Probe separate from Initialize.** The Modules page can show "unsupported on this machine, because ..." without
  starting anything.
- **Results for expected failures, exceptions for bugs.** Otherwise a "service not running" would disable a module.
- **Host stamps source and time on metrics.** A module cannot publish as another and clocks agree.
- **`net8.0` for Abstractions, `net10.0` for Core.** Modules can target the older long-term runtime and still run in
  the newer host.
- **Settings as one typed object.** One file, atomic, with a version for migration, instead of a key-value API with
  a dozen overloads.

## 15. What is not in v0.1

- Async lifecycle (`Probe`/`Initialize` are synchronous; run slow work on a thread you own).
- More than one page per module; module-added navigation groups.
- A dynamic list of choices (a `ChoiceRow` has a fixed option list; replace the row to change it).
- An open set of metric units: the list is closed so the recorder and Dashboard can format every unit. Add values by
  a host API minor version.
- Module-to-module calls other than the metrics bus.
- Unloading a module without a restart; sandboxing; code signing (the registry's SHA256 and the install prompt are
  the trust steps).
- Compiled XAML pages from a module (unsupported, see 5.5), and any window handle or dispatcher for modules.
- Loading a custom-control module built for another Windows App SDK line than the host (not tested in spike A1).
- A `net48` module (the XTU contract assemblies) in this net10 host: DESIGN.md open point 2 still has to settle it.

## 16. Using Rigbay.Core (for the app and for tests)

`Rigbay.Core` (net10.0, no UI reference) is the host logic. The WinUI shell creates one `RigbayHost`, calls two methods
off the UI thread, and binds its windows to the objects below. All of it is covered by `tests/Rigbay.Core.Tests`.

```csharp
var host = new RigbayHost(new RigbayHostOptions { ModulesRoot = @"C:\Program Files\Rigbay\modules" });
host.UiDispatcher = action => dispatcherQueue.TryEnqueue(() => action());   // the app's own wrapped UI dispatch
host.ToastSink = myToastSink;           // IToastSink: Show(moduleId, title, body, kind), texts already localised
host.ApprovalPrompt = myPrompt;         // IApprovalPrompt: ConfirmAsync(ApprovalRequest) -> bool; none set = every hardware change refused
await Task.Run(() => { host.Discover(); host.StartEnabled(); });
```

| Object | What the app uses it for |
| --- | --- |
| `RigbayHost.Modules`, `Find(id)` | The list of `ModuleEntry`: one per folder, valid or not. `ModulesDiscovered`, `ModuleFaulted` events. |
| `ModuleEntry` | `Id`, `DisplayName`, `DisplayDescription` (manifest text, overridden by `module.<id>.name` / `.description`), `Manifest` (group, requires, version), `Lifecycle`, `Bay` (a `BayStatus`: `BayState` plus `ModuleReason`), `ResolveReason(reason)`, `Page`, `Fault`, `Enable()`, `Disable()`, `SetPending(...)` (installer sets update or restart), `Changed` event, `CreateCustomControl(id)`, `Invoke`/`InvokeAsync` (any other call into the module goes through these). `Enable()` and `Disable()` block for up to the time limits: call them off the UI thread. |
| `ValueEditor<T>` via `RowEditors.For(entry, row)` | The apply-or-revert model of section 5.4 for `ToggleRow`, `ChoiceRow`, `NumberRow`, `TextRow`, `ShortcutRow`: `Draft`, `SetDraft`, `IsDirty`, `Error`, `SubmitAsync` (immediate rows), `ApplyAsync`, `Discard`, `CanRevert`, `Revert`, `Changed`. Returns an `EditResult` with an outcome (`Applied`, `Invalid`, `Declined`, `Faulted`, `NothingToDo`) and a localised message. |
| `RowEditors.InvokeButtonAsync` / `RevertButtonAsync` | Button rows, with the confirmation for hardware effects. |
| `host.Metrics` (`MetricsBus`) | `Subscribe(handler, id?)`, `TryGetLatest`, `GetLatest` for the Dashboard, the live strip and `ReadingRow`s. |
| `host.Hotkeys` (`HotkeyRegistry`) | `Entries`, `Conflicts`, `ReserveHost(name, gesture)`, `Press(gesture)` (call it from the window's hotkey message), `Changed`. Entry `Gesture` and `HasConflict` are observable. |
| `host.Tray` (`TrayRegistry`) | `Items` (`TrayEntry`: module id, `TrayItem` data, a guarded `Invoke`), `Changed`. |
| `host.Commands`, `ExecuteCommandAsync(args)` | `rigbay <module> <verb> [args]` to a `CommandOutcome(ExitCode, Output)`. The app carries the command line to the running process and prints `Output` (stdout for 0, stderr otherwise). |
| `host.Languages` (`LanguageCatalog`) | `Languages` for the language picker, `SetLanguage`, `T`/`F` for host strings, `LanguageChanged`. `host.SetLanguage(code)` also remembers the choice. Embedded host files are `Rigbay.Core.lang.*` resources; the app adds its own the same way with `LoadEmbedded`. |
| `host.Log` (`HostLog`) | `Recent()` and `EntryWritten` for the Log page; the file is `rigbay.log` in `%LOCALAPPDATA%\Rigbay\logs`. |
| `PageValidator`, `PageModel.EnumerateRows()` | The host validates every page before showing it; the app can use `EnumerateRows()` to build the search index (header and description keys, resolved with `entry.Localisation`). |

**Language check.** `src/Rigbay.LangCheck` is the console front end of `LangCheck`:

```
dotnet run --project src/Rigbay.LangCheck -- <lang folder> [--module <id>] [--source <dir>] [--strict] [--out <file>]
```

Exit code 0 ok, 1 errors (unknown keys, placeholder mismatches, keys outside `module.<id>.`, duplicates, no English
file; with `--strict` also missing and unused keys), 2 bad usage. The same function is `LangCheck.Check(...)` for the
app's own `--langcheck` switch.

**Tests.** `dotnet test Rigbay.sln` builds `samples/Rigbay.SampleModule` and `tests/Rigbay.FaultyModule` (a module that
misbehaves as `behavior.txt` says), copies their output folders, and loads them through the real loader. `TestEnv` in
`tests/Rigbay.Core.Tests` shows how to host a module folder from a unit test.

**Package.** `dotnet pack src/Rigbay.Abstractions -c Release` makes `Rigbay.Abstractions.0.1.0.nupkg` (assembly plus XML
docs). Nothing is published from this repository by the build.
