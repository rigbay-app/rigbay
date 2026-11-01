# Rigbay.Abstractions v0.1: the module contract

Status: v0.1 (task A2), updated with the findings of spike A1 (`docs/spike/ABSTRACTIONS-NOTES.md`). Everything is in the namespace `Rigbay.Abstractions`. The assembly `Rigbay.Abstractions` is the only
thing a module references. `Rigbay.Core` (the host logic) and the WinUI shell are written against it. Where this
file and the code disagree, fix the one that is wrong and say so in the commit message.

Contents: 1 Principles, 2 Module package and manifest, 3 Lifecycle, 4 Host services, 5 Page model,
6 Metrics bus, 7 Localisation, 8 Settings, 9 Commands, hotkeys, tray, toast, 10 Fault boundary and loading,
11 API version, 12 Rules for module authors, 13 How to write a module, 14 Reasons behind the choices,
15 What is not in v0.1, 16 Using Rigbay.Core, 17 Changelog.

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
  "probePaths": ["%ProgramFiles%\\Vendor\\Contracts"],
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
| `probePaths` | no | Up to 8 extra folders the module reads assemblies from, for files that stay where another product installed them and are never copied (for example `C:\Program Files\Intel\...`). Each is an absolute local path (drive letter; no `..`, no network share, no wildcards), and may use `%NAME%` environment variables, which must exist on the machine. Folders are used only by the loader rule in section 10 (last resort, after the host and the module folder). They are listed in the facts of the Modules page and the turn-on dialog as "Reads files outside its folder" (host key `core.requires.outside-files`; the app shows `ModuleManifest.DeclaredProbePaths` under that label). A declaration, not a sandbox. |

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
| `RequestPageRefresh()` | Asks the host to call `GetPageModel` again and replace the page (a list that changed). Any thread, coalesced, no-op while the module is off. | 7.1, 13 |
| `PageVisible` | `IObservableValue<bool>`: true while the host shows this module's page. Lets a polling module slow down when nobody looks. The module only observes it (a set from the module is ignored); false while the module is off; read it in `Initialize`. | 13 |
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
| `ButtonRow` | button | `InvokeAsync(ct) -> Task<ActionResult>`, `ButtonKey`, optional `RevertAsync` and `RevertButtonKey` (label of the revert action, shown in the question) |
| `ValueRow` | read-only text | `IObservableValue<string>` (already localised by the module) |
| `ReadingRow` | live number with unit | `MetricId` (host subscribes to the bus and formats), `Decimals`, `Color`. Shows "not available" instead of a number when the metric has no sample or was withdrawn (section 6) |
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

**A revert is a hardware write like any other, so the host asks again.** Putting a previous value back is a change of
the machine too: Maximum to Auto is harmless, but Auto to Maximum (the revert of a revert, or a revert after the
read-back showed a different value than the one written) is not. Every call that changes hardware goes through the
approval service, Revert included, and the question names the exact current and previous value: `Fan mode: Maximum → Auto`
(`ApprovalRequest.IsRevert` is true; the effect text is `HardwareEffect.RevertDescriptionKey` when the module gave one).
A declined revert changes nothing and the row stays revertible. This holds for value rows
(`ValueEditor<T>.RevertAsync(ct)`) and for buttons (`RowEditors.RevertButtonAsync(entry, row, ct, detail?)`; its default
question is `<header>: <RevertButtonKey text, or "Revert">`).

One opt-out exists, and it is the module's to claim: `HardwareEffect.SafeRevert = true` (default **false**). The host then
reverts without a question, but the status line still names what was written (`EditResult.Message` for a value row, for
example `Reverted: Status light is now Off (was On)`; `ActionResult.Message` for a button: the module's own message, or the
host's `core.revert.button-done`). Set it only when every possible revert of that row is harmless for the machine (a
lighting colour); never on fan, power, voltage or charge rows, where the previous value can be the more dangerous one.
The synchronous `ValueEditor<T>.Revert()` cannot ask, so it only reverts rows that need no question (no hardware
effect, or `SafeRevert`); for any other hardware row it writes nothing and returns `EditOutcome.Declined`. The UI calls
`RevertAsync`. The sample module has a `Profile` row (revert asked), a `Status light` row (`SafeRevert`) and a `Boost`
button (revert asked) that show both.

The confirmation shows the exact change: `ApprovalRequest.Detail` is the new value as text (a toggle "On" or "Off", a
choice its label, a number, the text itself, a shortcut such as `Ctrl+F5` or "Not set"). The caller can pass its own
text, for example "Fan mode: Auto -> Maximum": `editor.ApplyAsync(detail)`, `editor.SubmitAsync(value, detail)`,
`RowEditors.InvokeButtonAsync(entry, row, ct, detail)` (a button shows its label when it gets none), or set
`editor.DetailFormatter`.

**No text.** A `LocalisedText` whose key is null or empty (including `default`) is "no text" (`IsEmpty`): `Validate`
returning it means no error, the host resolves it to an empty string and shows no detail for it. There is no implicit
conversion from `string` any more, so `cond ? LocalisedText.Of("k") : null` is a `LocalisedText?` that is null when
the condition is false.

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

**Labels of the summary readings.** The summary names metric ids, not texts. A module registers the text once, in
`Initialize`, with `host.Metrics.Describe(new MetricDescriptor(id, "metric.cpu-temp", MetricUnit.Celsius, "0"))`: the label
key is module-relative like every other key (`module.<id>.metric.cpu-temp` in the language file) or a `Text.Literal`. The
host resolves it in the current language: `RigbayHost.ResolveMetricLabel(id)` and `ModuleEntry.ResolveMetricLabel(id)`
return the module's label, else the host's own `metric.<id>` text (shared ids such as `cpu.temp`), else the raw id.
`ModuleEntry.GetSummaryReadings()` gives the dashboard everything for a bay in one call: up to two `SummaryReading(Id,
Label, Unit, FormatHint)`. If two modules describe the same id, the first description is kept (the id is a shared
vocabulary, so the label should be the same anyway).

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
- **Withdraw.** `Withdraw(id)` takes a metric back, for example after repeated read failures: the last value is no longer
  true and must not stay on the page. Afterwards `TryGetLatest` is false, `Latest(id)` is null and `GetLatest()` leaves
  the id out; every subscriber of the id (and every all-samples subscriber) gets one `MetricSample` with
  `IsAvailable == false` and a NaN `Value`, so **check `IsAvailable` before you use `Value`**. Publishing the id again
  makes it available again. `Withdraw` only acts on a reading this module published last (another module's newer sample
  is not touched) and does nothing twice. A `ReadingRow` shows "not available" for a metric with no sample or a withdrawn
  one (`ReadingFormat.Format(languages, row, sample)` in Core is the one function that formats it).
- **Describe.** `Describe(new MetricDescriptor(id, labelKey, unit, formatHint))` registers once what the metric is called
  and how to format it (section 5.6). `LabelKey` is module-relative or a `Text.Literal`; `FormatHint` is a .NET numeric
  format such as `"0"` or `"0.0"`, or null.
- **Subscribers.** `SubscriberCount(id)` is how many readers want the id: subscribers of that id plus all-samples
  subscribers, from the host (dashboard, recorder) and from other modules, not this module's own. `SubscribersChanged`
  is raised (inside the module's fault boundary, on the thread that subscribed or disposed) when that can have changed;
  it does not say for which id, so call `SubscriberCount` for the ids you publish. Both are for the poll-rate pattern in
  section 13.
- `Withdraw`, `Describe`, `SubscriberCount`, `SubscribersChanged` and `Latest` are default interface members on
  `IMetricsBus` (and `PageVisible` on `IHostServices`): a class that implements the interface itself, like a test double,
  keeps compiling and gets "nobody listens, nothing to withdraw".

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

### 7.1 Literal text and page refresh

**Literal text.** Every place that takes a language key (`ChoiceOption.LabelKey`, `TrayItem.TextKey`,
`HotkeyRegistration.NameKey`, `Row.HeaderKey`, `LocalisedText`, `T`, `F`, ...) also accepts a literal: a string that
starts with `=` is shown without the `=`, never searched in a language file and never translated. Make one with
`Text.Literal(name)` (`Text.IsLiteral(key)` tests for one); do not type the `=` by hand. With `F` the rest is the
format template (`F("=Fan {0}", 2)`); with no arguments it is not formatted. `ModuleLocalisation` does not add the
`module.<id>.` prefix to a literal. The module is responsible for using it only for user data (profile names, device
names), not for UI wording, which must stay a key so it can be translated. LangCheck ignores `=...` string literals
in the source (they are not keys and do not make a key "used"). Anything in the host that resolves text with
`entry.Localisation` (including the search index built from `EnumerateRows()`) therefore sees the literal text.

```csharp
new ChoiceOption(profile.Id, Text.Literal(profile.Name))
```

**Page refresh.** The page is built once, when the module is enabled. A module whose page shows a list that changes
(a profile was created or deleted) calls `host.RequestPageRefresh()` after changing it. The host calls `GetPageModel`
again off the UI thread, validates the page, replaces `ModuleEntry.Page` and `LastPage`, disposes the row editors of
the old rows, and raises `ModuleEntry.PageRefreshed` and `RigbayHost.PageRefreshed` (the app subscribes once and
rebuilds that module's page). Requests made while a rebuild is pending or running are coalesced into one more rebuild;
the call is safe from any thread and does nothing while the module is not running. A throwing or invalid
`GetPageModel` during a refresh faults the module like any other fault (`ModuleFaulted`).
`ModuleEntry.Changed` is not raised for a refresh.

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
dispatches; carrying the command line to the running host process belongs to the app. Two verbs belong to
the host itself, so `list` and `version` cannot be module ids: `rigbay list` prints one line per installed module
(`id`, bay state, manifest version or `-`, `enabled`/`disabled` as the user chose, and the reason when there is one) and
`rigbay version` prints the host version, the Rigbay.Abstractions version and the host API version, one per line
(`RigbayHost.ExecuteCommandAsync` answers both; `VersionText()` and `ListModules()` are public too).

**Hotkeys.** `host.Hotkeys.Register(new HotkeyRegistration("toggle-turbo", "hotkey.toggle-turbo", defaultGesture, () => ...))`
returns an `IHotkeyBinding` with `Gesture` (observable, user-changeable, nullable = unbound) and `HasConflict`
(true while another module or the host holds the same gesture). A `HotkeyGesture` is modifiers (`Ctrl`, `Alt`,
`Shift`, `Win`) plus a key name (`A`..`Z`, `0`..`9`, `F1`..`F24` (not `F12`, see below), `Space`, `Enter`, `Tab`, `Esc`, `Backspace`,
`Delete`, `Insert`, `Home`, `End`, `PageUp`, `PageDown`, `Up`, `Down`, `Left`, `Right`, `Num0`..`Num9`), written
`Ctrl+Alt+F5`. The registry is data only: it stores the gestures, detects conflicts and persists the user's
rebinding in `%LOCALAPPDATA%\Rigbay\hotkeys.json`. Registering with the OS (and ignoring conflicting gestures
rather than registering them) belongs to the UI host.

**F12 is not a hotkey.** Windows reserves `F12` for debuggers, so `RegisterHotKey` always refuses it, with any
modifiers. `HotkeyGesture.TryParse` returns false for it, `Parse` throws `FormatException`, the constructor throws
`ArgumentException`, and a stored binding such as `Ctrl+F12` is ignored (the default is used).
`HotkeyGesture.ReservedReason(key)` returns the explanation for a UI that wants to show it, or null.

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
- **Loading.** One `AssemblyLoadContext` per module, never unloaded. For every assembly the module asks for, the
  first of these that has it wins, and the winner is logged at debug level (`name version: host copy | module copy |
  probe path copy`):
  1. **The host's default context**, if it has the assembly (already loaded, or loadable there at a version that
     satisfies the request): the shared framework, `Rigbay.Abstractions`, WinUI/WinRT, anything the app ships. The
     module's own copy is ignored, so a type has one identity on both sides.
  2. **The module folder**: through the module's `deps.json` when there is one, else `<name>.dll` next to the entry
     assembly (native libraries the same way).
  3. **The `probePaths`** from `module.json`, in the order declared: `<name>.dll` in each folder.
  4. Nothing has it: `FileNotFoundException`, and the module faults when the code that needed it runs.

  So a module may ship a `System.*` package the host does not contain (`System.Management`,
  `System.ServiceModel.*`), and a module that ships its own copy of something the host has still gets the
  host's copy. `Rigbay.Abstractions` never comes from a module or a probe path. A module no longer needs its own
  `AssemblyLoadContext.Resolving` hook.
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
   Names that come from data, not from you (profiles, devices), go through `Text.Literal(name)` where a key is expected,
   and when that list changes you call `host.RequestPageRefresh()` so the host rebuilds the page (section 7.1; the sample
   module's "Devices" expander and "Add a device" button show both).
   **Polling modules: poll fast only while somebody can see the result.** A module cannot know whether its page is open
   or anybody reads its metrics, so give it the two signals the host offers and slow down without them:

   ```csharp
   private static readonly TimeSpan Fast = TimeSpan.FromSeconds(2), Slow = TimeSpan.FromSeconds(15);
   private bool _fast = true;

   // in Initialize, after the timer exists:
   host.Metrics.Describe(new MetricDescriptor(CpuTemp, "metric.cpu-temp", MetricUnit.Celsius, "0")); // label for the dashboard
   host.PageVisible.Changed += UpdateRate;           // the page opened or closed
   host.Metrics.SubscribersChanged += UpdateRate;    // the dashboard, a recorder or another module started or stopped reading

   private void UpdateRate()
   {
       // wanted = the page is open OR somebody reads a metric I publish (my own subscriptions are not counted)
       bool wanted = _host.PageVisible.Value || _host.Metrics.SubscriberCount(CpuTemp) > 0;
       if (wanted == _fast) return;
       _fast = wanted;
       _timer.Change(wanted ? TimeSpan.Zero : Slow, wanted ? Fast : Slow); // zero: read now when somebody starts looking
   }
   ```

   Both events run on the thread that changed them (often the UI thread) inside your fault boundary: only retime a
   timer there, hand anything slow to a background thread. Unsubscribe in `Shutdown`. Read `PageVisible.Value` in
   `Initialize` (it is the current state, not always false). When a read keeps failing, `host.Metrics.Withdraw(id)` the
   metric instead of leaving the last value on the page. The sample module does all of this (`UpdatePollRate`,
   `OnPausedChanged`).
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
host.ApprovalPrompt = myPrompt;         // IApprovalPrompt: ConfirmAsync(ApprovalRequest) -> bool (request.IsRevert for a revert); none set = every hardware change refused
await Task.Run(() => { host.Discover(); host.StartEnabled(); });
```

| Object | What the app uses it for |
| --- | --- |
| `RigbayHost.Modules`, `Find(id)` | The list of `ModuleEntry`: one per folder, valid or not. `ModulesDiscovered`, `ModuleFaulted` events. |
| `ModuleEntry` | `Id`, `DisplayName`, `DisplayDescription` (manifest text, overridden by `module.<id>.name` / `.description`), `Manifest` (group, requires, version), `Lifecycle`, `Bay` (a `BayStatus`: `BayState` plus `ModuleReason`), `ResolveReason(reason)`, `Page` (null while the module is off), `LastPage` (the last page built, kept after a stop or fault, so the UI can show the rows disabled), `Issues` (manifest problems as keys: `issue.Localise(host.Languages)`), `Fault`, `Enable()`, `Disable()`, `SetPending(...)` (installer sets update or restart), `Changed` event, `SetPageVisible(bool)` (call it when the module page is shown or left, 4), `ResolveMetricLabel(id)` and `GetSummaryReadings()` (dashboard labels, 5.6), `CreateCustomControl(id)`, `Invoke`/`InvokeAsync` (any other call into the module goes through these). `Enable()` and `Disable()` block for up to the time limits: call them off the UI thread. |
| `ValueEditor<T>` via `RowEditors.For(entry, row)` | The apply-or-revert model of section 5.4 for `ToggleRow`, `ChoiceRow`, `NumberRow`, `TextRow`, `ShortcutRow`: `Draft`, `SetDraft`, `IsDirty`, `Error`, `SubmitAsync` (immediate rows), `ApplyAsync`, `Discard`, `CanRevert`, `RevertAsync` (asks first for a hardware row, 5.4), `Revert` (synchronous: refuses hardware rows that need a question), `Changed`. Returns an `EditResult` with an outcome (`Applied`, `Invalid`, `Declined`, `Faulted`, `NothingToDo`) and a localised message. `For` returns one editor per row instance (the same object each time); `ValueEditor<T>` is `IDisposable` and the entry disposes its editors when the module stops or faults and before it is enabled again, so the UI need not. |
| `RowEditors.InvokeButtonAsync` / `RevertButtonAsync` | Button rows, with the confirmation for hardware effects, for the action and for its revert. `RevertButtonAsync(entry, row, ct, detail?)` asks too (5.4). |
| `host.Metrics` (`MetricsBus`) | `Subscribe(handler, id?)`, `TryGetLatest`, `Latest(id)`, `GetLatest` for the Dashboard, the live strip and `ReadingRow`s. A sample with `IsAvailable == false` is the "withdrawn" notice (6). `TryGetDescriptor`, `GetDescriptors`, `SubscriberCount(id)`, `SubscribersChanged`, `IsWithdrawn(id)`. `ReadingFormat.Format(host.Languages, row, sample)` turns a sample (or null) into the text of a reading, including "not available". |
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

## 17. Changelog

### 0.1 after A9 (host gaps found by the PredatorSense module; no version number change)

Additive in `Rigbay.Abstractions` (every new interface member is a default interface member, so a class that implements
`IHostServices` or `IMetricsBus` itself keeps compiling), one **behaviour change** in Core:

- **Behaviour change (safety).** A revert of a hardware row is asked about like any hardware change, with the exact
  current and previous value in the question. Before, "a revert is not asked about again", so Auto -> Maximum could be
  written by a Revert without a question. `ValueEditor<T>.Revert()` (synchronous) can no longer ask: for a hardware row it
  now writes nothing and returns `EditOutcome.Declined` ("must be confirmed"). **The UI must call `RevertAsync`.**
  `RowEditors.RevertButtonAsync` asks too (a declined question returns the failed result `core.approval.declined` and
  does not call `RevertAsync`). Section 5.4.
- `HardwareEffect.SafeRevert` (opt-in, default false): the module declares every revert of that row harmless; no question,
  the status line still names what was written. `HardwareEffect.RevertDescriptionKey` (effect text for the revert
  question). `ButtonRow.RevertButtonKey` (label of the revert action).
- `ValueEditor<T>.RevertAsync(CancellationToken)`. `RowEditors.RevertButtonAsync(entry, row, ct, detail = null)`.
  `ApprovalRequest.IsRevert` (init property). `RigbayHost.RequestApprovalTextAsync(..., bool isRevert = false)`.
  `EditResult.Message` of an applied revert of a hardware row names what was written.
- **Poll rate.** `IHostServices.PageVisible` (`IObservableValue<bool>`); Core: `ModuleEntry.SetPageVisible(bool)` (the app
  calls it when the module's page is shown or left; also while the module is off, the state is kept), `IsPageVisible`.
  `IMetricsBus.SubscriberCount(MetricId)` and `event SubscribersChanged` (own subscriptions of the module excluded);
  Core: `MetricsBus.SubscriberCount(MetricId)` / `(string)`, `event SubscribersChanged(MetricId?)` (host and all modules).
  Section 13 shows the pattern; the sample module uses it.
- **Withdraw.** `IMetricsBus.Withdraw(MetricId)`, `IMetricsBus.Latest(MetricId)`; `MetricSample.IsAvailable` (init, default
  true; false in the "unavailable" notification, whose `Value` is NaN); Core: `MetricsBus.Withdraw(source, id)`,
  `Withdraw(id)`, `WithdrawAll(source)`, `IsWithdrawn(id)`, `Latest(MetricId)` / `(string)`; `TryGetLatest` is false and
  `GetLatest` omits a withdrawn id. `ReadingFormat.IsAvailable(sample)` and `ReadingFormat.Format(languages, row, sample)`:
  the text of a `ReadingRow` including "not available" (`core.reading.unavailable`). Section 6.
- **Labels.** `MetricDescriptor(Id, LabelKey, Unit, FormatHint)` and `IMetricsBus.Describe(descriptor)`; Core:
  `MetricsBus.Describe(owner, descriptor)`, `TryGetDescriptor`, `GetDescriptors`; `RigbayHost.ResolveMetricLabel(MetricId | string)`,
  `ModuleEntry.ResolveMetricLabel(MetricId)`, `ModuleEntry.GetSummaryReadings()` / `(PageSummary?)` returning
  `SummaryReading(Id, Label, Unit, FormatHint)`. Order: the module's descriptor, the host's `metric.<id>` text, the raw id.
  Section 5.6.
- New host language keys (en and vi): `core.revert.done`, `core.revert.button-done`, `core.revert.needs-approval`,
  `core.revert.changed`, `core.revert.generic`, `core.reading.unavailable`.
- Sample module: `Profile` (hardware choice, revert asked, own revert text), `Status light` (`SafeRevert`), `Boost` (hardware
  button with revert), `Pause ticks` (withdraws the metric), a poll rate that follows `PageVisible` and
  `SubscriberCount`, and a `MetricDescriptor` for `sample.ticks`.
- For the App (task A7 or the next App task): (1) call `RowEditors.For(...).RevertAsync(ct)` instead of `Revert()` for the
  Revert link of value rows and show `EditResult.Message` for `Applied`/`Declined`; pass nothing else, the question text is
  built in Core; (2) let the approval dialog use `ApprovalRequest.IsRevert` for its title; (3) call
  `entry.SetPageVisible(true/false)` when the module page is navigated to or left (and false when the window is hidden);
  (4) show `ReadingFormat.Format(...)` for `ReadingRow` and handle `MetricSample.IsAvailable == false` in the live
  subscription (show "not available"); (5) take dashboard labels from `entry.GetSummaryReadings()` instead of the raw ids;
  (6) a module row that changes hardware and has a `RevertButtonKey` gets a more precise question from Core already.
- Source-breaking only for code that matched on the exact positional shape of `MetricSample`/`ApprovalRequest` (both are
  records that gained init properties, not constructor parameters) or that relied on the old "revert is not asked" rule.

### 0.1 after A8 (literal text and page refresh; no version number change)

Additive:

- `Rigbay.Abstractions.Text`: `Literal(string?)`, `IsLiteral(string?)`, `Unwrap(string)`, `Marker`. A key starting with `=`
  is literal text. `LanguageCatalog.T/F/TryGet` and `ModuleLocalisation.T/F/TryGet/Resolve` return it without the `=`
  (no lookup, no module prefix); `F` formats the rest as the template when arguments are given. LangCheck ignores `=...`
  string literals in scanned source. Section 7.1.
- `IHostServices.RequestPageRefresh()`; `ModuleEntry.PageRefreshed` and `RigbayHost.PageRefreshed`
  (`Action<ModuleEntry>`, raised on a worker thread). Coalesced, no-op when the module is off, old row editors disposed,
  a faulting `GetPageModel` faults the module. Section 7.1.
- The sample module shows one literal (device names) and one refresh (an "Add a device" button).
- Source-breaking only for a class that implements `IHostServices` itself (only Core does).
- For the App (task A7): subscribe to `RigbayHost.PageRefreshed` once and rebuild that module's page from
  `entry.Page`; the search index comes from `EnumerateRows()` resolved with `entry.Localisation`, which already returns
  literal text.

### 0.1 after A6 (loader rule for `System.*` assemblies, `probePaths`; no version number change)

Additive, except the loader rule which relaxes a restriction:

- **Loader rule** (`ModuleLoadContext`, exact order, first match wins, winner logged at debug level):
  1. the host's default context has the assembly (loaded, or loadable there at a version that satisfies the request): the
     host's copy, the module's is ignored (framework, `Rigbay.Abstractions`, WinUI/WinRT, whatever the app ships);
  2. the module folder (its `deps.json`, else `<name>.dll` beside the entry assembly);
  3. the module's `probePaths`, in the declared order (`<name>.dll`);
  4. otherwise `FileNotFoundException`.
  Before, every name starting with `System.`, `Microsoft.Win32.`, `Microsoft.NETCore.`, `Microsoft.WinUI`, `Microsoft.UI.`,
  `Microsoft.Windows.`, `WinRT.` or `WindowsBase` went to the host unconditionally, so a module could not ship
  `System.Management` or `System.ServiceModel.*`. Now such a name goes to the host only if the host has it. Behaviour change
  to know: an assembly of any name that the app's own folder ships now beats the module's copy even if the host had not
  loaded it yet (before: only if already loaded). A module that relied on its `AssemblyLoadContext.Resolving` hook
  (the XTU spike) can drop it.
- `module.json` field `probePaths` (section 2.1): up to 8 absolute local paths, `%ENV%` expanded and validated
  (`ManifestReader.TryExpandProbePath`; errors `core.manifest.probe-path-invalid`, `core.manifest.probe-paths-count`).
- `ModuleManifest.ProbePaths` (constructor parameter, last, optional), `DeclaredProbePaths`, `ResolvedProbePaths`,
  `ReadsOutsideItsFolder`, `ModuleManifest.OutsideFilesLabelKey` (`core.requires.outside-files`, "Reads files outside its
  folder", in `en.lang` and `vi.lang`, LangCheck strict clean). The Modules page and turn-on dialog should list
  `DeclaredProbePaths` under that label (App change, not done here).
- `ModuleLoadContext(moduleId, entryAssemblyPath, probePaths = null, log = null)`.

### 0.1 after A4 (fixes for the gaps the app shell found; no version number change)

Additive:

- `ValueEditor<T>.ApplyAsync(string? detail, ct)`, `SubmitAsync(T, string? detail, ct)`, `DetailFormatter`;
  `RowEditors.InvokeButtonAsync(entry, row, ct, detail)` (new last parameter). Without a detail the approval dialog
  gets the new value as text (`ApprovalRequest.Detail` is no longer null for value rows).
- `ValueEditor<T>` implements `IDisposable`. `RowEditors.For(...)` caches one editor per row instance, disposed by
  the `ModuleEntry` on stop, fault and enable.
- `ModuleEntry.LastPage`.
- `LocalisedText.IsEmpty`; a null or empty key means "no text" everywhere in Core.
- `HotkeyGesture.ReservedReason(key)`.
- `ManifestIssue.Key`, `ManifestIssue.Args`, `ManifestIssue.Localise(catalog)`, `LocaliseLine(catalog)`,
  `ManifestResult.LocaliseErrors(catalog)`, `ManifestIssues.Summary(...)`. New host keys `core.manifest.*`,
  `core.value.*`, `core.command.version-*`, `core.command.no-modules` in `en.lang` and `vi.lang` (LangCheck strict is clean).
- `RigbayHost.RequestApprovalTextAsync` (approval with a detail that is already text), `VersionText()`, `ListModules()`.
- `ExecuteCommandAsync` answers `version`; `list` now prints more per module (see section 9).

Breaking:

1. **`LocalisedText` has no implicit conversion from `string` any more.** Write `LocalisedText.Of("key")`. A
   `ToastMessage("a", "b", ...)` call must become `new ToastMessage(LocalisedText.Of("a"), LocalisedText.Of("b"), ...)`.
   The sample module is changed accordingly.
2. **`ManifestIssue` is now `(Path, Key, Args, IsError)`** instead of `(Path, Message, IsError)`. `Message` is still
   there as a read-only property (English), so code that only reads issues keeps working; code that constructs
   issues, or that put `ManifestIssue.ToString()` / `ManifestResult.ErrorSummary` into a localised sentence, should
   use `Localise` / `LocaliseErrors`.
3. **`ModuleReason` for an invalid manifest** now carries the issue list as its argument (not a joined English
   string). `ModuleEntry.ResolveReason` localises it; code that reads `Reason.Args[0]` as a string must handle an
   `IReadOnlyList<ManifestIssue>`.
4. **`HotkeyGesture` rejects `F12`** (`TryParse` false, `Parse`/constructor throw). A module that registered `F12` as
   its default now fails at that line; a stored `F12` binding is ignored.
5. **`rigbay list` output changed** from `id  State` to `id  State  version  enabled|disabled  [reason]`. The first
   two columns are the same.
6. `RowEditors.For` returns the same editor for the same row instance (before: a new one each call). An app that kept
   its own cache can drop it; one that relied on getting a fresh editor must create the row again or call `Discard()`.
