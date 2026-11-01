# Rigbay.Abstractions v0.1: the module contract

Status: v0.1, written before the code it describes (task A2). The assembly `Rigbay.Abstractions` is the only
thing a module references. `Rigbay.Core` (the host logic) and the WinUI shell are written against it. Where this
file and the code disagree, fix the one that is wrong and say so in the commit message.

Contents: 1 Principles, 2 Module package and manifest, 3 Lifecycle, 4 Host services, 5 Page model,
6 Metrics bus, 7 Localisation, 8 Settings, 9 Commands, hotkeys, tray, toast, 10 Fault boundary and loading,
11 API version, 12 Rules for module authors, 13 How to write a module, 14 Reasons behind the choices,
15 What is not in v0.1.

## 1. Principles

- **Small.** A module author reads this file once and has everything. Anything that can be added later without
  breaking modules is left out now.
- **No UI framework.** `Rigbay.Abstractions` targets `net8.0`, references nothing but the BCL and has no Windows
  App SDK type. A module that only reads sensors does not need a UI stack, and the contract survives a change of
  UI framework (the WinUI spike may still fail and fall back to WPF).
- **Declarative first.** A module describes its page as data (`ModulePage > RowGroup > Row`). The host draws it,
  so every module looks the same, follows theme, DPI and language, and the host can search it. A module that
  needs more supplies one custom control through an escape hatch (section 5.4).
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
   is enabled. It must be read-only and quick (it is given 10 s). It returns `ProbeResult.Supported()` or
   `ProbeResult.Unsupported(reasonKey)`; the reason key is a module-relative language key shown to the user, for
   example `probe.no-service` -> "The Acer gaming service is not installed". Throwing is a fault, not "unsupported".
3. `Initialize(IHostServices host)` is called when the module is enabled and was probed as supported (10 s limit).
   Register hotkeys, tray items, commands and metrics subscriptions here, start timers, read settings. Hardware
   is not written during `Initialize` except to restore state the user chose.
4. `CreatePage()` is called after `Initialize` succeeds and returns the page model (section 5).
5. `Shutdown()` is called when the user disables the module and when the host exits (5 s limit). Stop timers,
   release handles. The host removes whatever the module registered (hotkeys, tray items, commands,
   subscriptions) after `Shutdown`, so a module need not undo them.
6. Enabling again after disabling calls `Initialize` on the same instance. Module code must tolerate that.
7. Install, update and removal of a module need a host restart, so the loader never unloads a module in a
   running host; `Shutdown` is not an unload.

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

All services are safe to call from any thread. Callbacks you give to a service (hotkey, tray, command,
subscription, button) may run on any thread, and always run inside the fault boundary.

## 5. Page model

### 5.1 Shape

```
ModulePage
  Groups : RowGroup[]            RowGroup = Id, HeaderKey, Rows[]
  Links : PageLink[]             "Learn more" style links at the bottom
  HeadlineMetrics : MetricId[]   the one or two readings the Dashboard shows next to the module
  Status : IObservableValue<LocalisedText>?   short state line for the Dashboard ("Proxy on :18081")
  Custom : ICustomPageProvider?  escape hatch, section 5.4
```

The host owns the first row of every module page (the enable toggle, name and one-line help from the manifest)
and disables every group below it while the module is off. A module never draws its own enable switch.

### 5.2 Rows

Every `Row` has: `Id` (unique inside the page, `[a-z0-9.-]`, used for search results and tests), `HeaderKey`,
optional `DescriptionKey` (language keys, module-relative), `Enabled` (an observable bool, default always
true), and `HardwareEffect` (see 5.3). Kinds:

| Row | Control the host draws | Value |
| --- | --- | --- |
| `ToggleRow` | switch | `IMutableValue<bool>` |
| `ChoiceRow` | drop-down | `Options` (`ChoiceOption(Id, LabelKey)`), selected option id in `IMutableValue<string>` |
| `NumberRow` | number box | `IMutableValue<double>`, `Minimum`, `Maximum`, `Step`, `Unit` (`MetricUnit`) |
| `TextRow` | text box | `IMutableValue<string>`, `MaxLength`, `PlaceholderKey` |
| `ShortcutRow` | key capture | `IMutableValue<HotkeyGesture?>`, normally `IHotkeyBinding.Gesture` |
| `ButtonRow` | button | `InvokeAsync(CancellationToken) -> Task<ActionResult>`, `ButtonKey` |
| `ValueRow` | read-only text | `IObservableValue<string>` (already localised by the module) |
| `ReadingRow` | live number with unit | `MetricId` (the host subscribes to the metrics bus), `Decimals` |
| `ExpanderRow` | row that folds `Children` | `IsExpanded` initially |
| `InfoBarRow` | inline message | `Severity` (info, success, warning, error), `Message` (observable `LocalisedText`), `IsOpen` |

Values are observable: `IObservableValue<T>` has `Value` and a `Changed` event; `IMutableValue<T>` adds a setter.
`ObservableValue<T>` is a ready implementation. Two directions of travel:

- **Module to UI:** the module sets `Value` (from a timer, after a hardware read); the host redraws.
- **UI to module:** the user changes a control; the host sets `Value`; the module's `Changed` handler does the
  work (the hardware write) and, if it fails or clamps, sets `Value` back. The host calls the setter inside the
  fault boundary.

Events can be raised on any thread; the host marshals to the UI thread. Rows are plain objects: a module may
build the page once and keep mutating the values.

### 5.3 Hardware effects

A toggle, choice, number or button that changes the state of the machine sets
`HardwareEffect = new HardwareEffect("effect.turbo")` (a description key, for example "Raises fan speed to the
maximum. Noise will increase."). The host then, before applying: shows the effect text and asks for confirmation;
remembers the previous value; shows a "Revert" action next to the control (for value rows it sets the previous
value back; for a `ButtonRow` it calls `RevertAsync` when the module provides one). A module must not rely on
this for safety: refuse dangerous values inside the module. This marking is what the user's approval rule for
hardware writes (`docs/DESIGN.md`) looks for in the UI.

### 5.4 Custom page escape hatch

`ModulePage.Custom` is an `ICustomPageProvider` with one method, `object CreateControl(CustomPageContext context)`.
The returned `object` must be a control of the host's UI framework (for WinUI 3, a `Microsoft.UI.Xaml.UIElement`);
`CustomPageContext.Framework` names that framework (`"winui3"`, later maybe `"wpf"`) so a module can refuse a
framework it does not support, and a module can build for several. The contract holds an `object` so
`Rigbay.Abstractions` stays free of any UI reference. The host places the control below the declarative groups (or
alone if the page has no groups). It is for things a row cannot express: a curve editor, a graph, an activity log.
Whether a dynamically loaded assembly can build such a control, and how a module gets host styles, is being
tested by task A1; the shape of `CustomPageContext` may grow (it is a class so it can) without breaking modules.

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
observable `Visible`, `Enabled`, `Checked` (with `Checkable`). Data only; the app draws the menu, grouped by module.

**Toast.** `host.Toast.Show(new ToastMessage(title, body, ToastKind.Warning))`. Interface only: the app supplies the
implementation. Titles and bodies are `LocalisedText` (a module-relative key plus optional arguments).

## 10. Fault boundary, loading, logging

- **Boundary.** Every call from the host into a module (`Probe`, `Initialize`, `CreatePage`, `Shutdown`, a value
  setter, a button, a hotkey, a tray item, a command, a metrics subscriber, a custom page factory) runs in the
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
2. Target `net8.0` or later. Do not reference WinUI, WPF or Windows Forms in code that is not the custom page.
3. Constructors do nothing. Probe is read-only. Initialize registers things.
4. Never block a callback for long. Value setters and buttons are called from the UI thread's path: start work in
   the background and return.
5. Any write to the machine is `HardwareEffect`-marked, clamped and validated inside the module. Reading is free.
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
       public ProbeResult Probe() => ProbeResult.Supported();
       public void Initialize(IHostServices host) { /* settings, registrations, timers */ }
       public ModulePage? CreatePage() => new ModulePage(new[]
       {
           new RowGroup("main", "group.main", new Row[]
           {
               new ToggleRow("echo", "row.echo.header", enabledValue) { DescriptionKey = "row.echo.description" },
               new ReadingRow("temp", "row.temp.header", new MetricId("cpu.temp")),
           })
       });
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
- **`object` for the custom control.** The alternative, an abstract UI base class in Abstractions, would tie the
  contract to one framework.
- **Observable values, not property-changed bindings.** `INotifyPropertyChanged` needs no UI assembly either, but a
  two-member interface is easier to implement correctly by someone who does not write Windows apps for a living,
  and it does not suggest XAML binding semantics.
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
- Custom page details beyond the framework name (waiting for spike A1).
