# Rigbay.Abstractions v0.1: advice from spike A1

Advice, not the final API. The working sketch is in `spike/src/Rigbay.Abstractions` (about 120 lines, no UI reference) and was exercised by `spike/src/Rigbay.Module.Rows`. Evidence for each point is in `SPIKE.md`.

## 1. Shape of the contract

```
Rigbay.Abstractions   (net10.0, references nothing UI, nothing Windows)
  IRigbayModule        Id, Name, Group, Version, Probe(), CreatePage(host) -> object?   (legacy level 2, may be null)
  IPageModelProvider   GetPageModel(host) -> PageModel ;  CreateCustomControl(host, controlId) -> object?
  PageModel            Title, Enable (ToggleRow), Groups { Header, Rows }, Links
  Row (records)        ToggleRow, ChoiceRow, NumberRow, TextRow, ButtonRow, ValueRow, ReadingRow,
                       ExpanderRow, InfoRow, CustomRow(controlId)
  IObservableValue<T>  Value + Changed   (ObservableValue<T> helper)
  IHostServices        HostVersion, AbstractionsVersion, Log, T(key), RunOnUi(action)
  ProbeResult          Supported + Reason
```

Rules this implies:

- **No WinUI, XAML, `Windows.*` or WPF type anywhere in `Abstractions`.** The spike's contract compiles for plain `net10.0`. This keeps the module's reference to the platform small, lets non-UI modules (a recorder, a metrics source) avoid the Windows App SDK entirely (the `Broken` sample references only `Abstractions`), and keeps the contract stable when the host's Windows App SDK moves from 1.8 to 2.x.
- **Level 1 (declarative) is the default and the only thing most modules write.** A page is data: groups of rows. The host renders `SettingsCard` rows, so theme, DPI, language keys and search come from the host and every module looks the same. Row kinds that the spike needed: toggle, choice, number, text, button, read-only value, live reading, expander, info bar, custom. A shortcut row (hotkey picker) is the next one to add; it was not built.
- **Level 2 (custom control) returns `object` from a host-called method, not from a module-chosen moment.** `CreateCustomControl(host, controlId)` is asked for by a `CustomRow`; the host checks `is UIElement` and shows an error row if not. This crossed the load-context boundary fine, the control used the host's theme brushes, and it fits into a `SettingsCard`. The module that uses it takes a Windows App SDK dependency and must match the host's version.
- **Do not offer "give me a XAML page"** as a supported route. Compiled XAML in a dynamically loaded module fails with `XamlParseException 0x802B000A` until the `.xbf` is placed where `ms-appx:///` points (the host exe folder) or loaded through a hand-built module-relative URI. `XamlReader.Load` of a string works but has no code-behind, events or `x:Bind`.
- **Values go through `IObservableValue<T>`**, not through WinUI binding. The host subscribes and updates the UI; the module can change a value from any thread (the sample's reading is set on a `System.Threading.Timer` thread and displayed correctly).

## 2. Threading and the fault boundary

- The host must expose `RunOnUi(Action)` and the module must use it instead of a dispatcher of its own. In the spike, an exception thrown inside a `DispatcherQueue.TryEnqueue` callback ended the process (exit code `0xC000027B`) without reaching `Application.UnhandledException`. XAML-raised events and `async void` are caught by `UnhandledException`, so keep that handler (`Handled = true`, log, mark the module faulted).
- Every call into a module is wrapped by the host: `Probe`, `GetPageModel`, `CreatePage`, `CreateCustomControl`, button actions, value setters. The renderer already wraps button actions and UI-thread updates. A module that throws becomes a failed bay with the exception type and message shown, not a crash.
- Build pages at start (or at least run `Probe` and build the model), so a faulty module is red on the dashboard before anybody opens it.
- Value `Changed` events fire on the thread that set them. Say so in the interface documentation; host code must marshal.

## 3. Loading rules the host should enforce (they worked in the spike)

- One `AssemblyLoadContext` per module, with the host's Default context winning for any assembly the host already has (`Rigbay.Abstractions`, `Microsoft.WinUI`, `WinRT.Runtime`, `Microsoft.Windows.SDK.NET`). Result: one copy of each in the process, no type identity problems, module pages are `UIElement` of the host.
- The module folder must not ship `Rigbay.Abstractions.dll` or the Windows App SDK assemblies; they were never loaded, but they invite version confusion. Package with `ExcludeAssets="runtime"` on those references.
- The contract carries a version (`AbstractionsInfo.Current`, `0.1.0`); a module declares the range it supports; the host refuses a different major. Also record which Windows App SDK line the host was built with and let a level-2 module declare it: a module's WinUI types bind to that line. Loading a module built for another line was **not** tested.
- Non-collectible contexts: there is no unload. Update and remove need a restart, as DESIGN.md already says.

## 4. Host services: what to avoid

- No service that returns or accepts a WinUI type (`Window`, `DispatcherQueue`, `XamlRoot`, `Brush`, `ImageSource`). Offer plain data instead: colours as semantic names (`ok`, `warning`, `error`, `reading-temperature`), icons as glyph names or file paths, text as language keys.
- No "get main window" or "get HWND" for modules in v0.1. Hotkeys, tray items, toasts and command-line verbs are host services with plain-data descriptions (`Hotkey { Id, DefaultKeys, DisplayNameKey }`); the host owns the HWND (the spike shows the subclass, `RegisterHotKey` and `Shell_NotifyIcon` all work from the host's HWND while elevated).
- No raw dispatcher; `RunOnUi` only.
- Localisation: rows carry keys (`module.<id>.<key>`), not text. The spike passes literal text in the model to stay small; the host's `Loc` (same `@code`/`@name` + `key = text` format) already switches language at run time.
- Hardware writes stay out of the contract's convenience path: a `ButtonRow` action that writes must go through a host approval service that shows what will be written (UI-DIRECTION section 4). Not built in the spike; nothing in it writes to hardware.

## 5. Search, dashboard and state, derived from the model

- Search needs no extra module work: the host indexes module names and the header and description of every row (18 entries for the sample). Keep `Header` and `Description` as the only searchable fields.
- The dashboard rack needs one thing the model already has: a module's first `ReadingRow`. Better, add a `Summary` (state line plus up to two readings) to `PageModel` so a module chooses what its bay shows.
- State shown per bay should be a closed set: ready, off, unsupported (with `Probe` reason), failed (with error), update available, restart required. The spike shows ready, unsupported/failed with the reason, and an empty bay; "off", "update" and "restart required" were not built.

## 6. Open points this leaves

- A module's enable row (`PageModel.Enable`) disables the groups below it in the host; whether "off" also unloads the module's background work is a contract question the spike did not answer.
- Number, text and shortcut rows need validation and an "apply or revert" model before hardware modules can use them; the spike's values apply immediately.
- A `net48` module (the XTU contract assemblies) cannot sit in this in-process, net10 host; DESIGN.md open point 2 still has to settle that. Nothing here tests it.
