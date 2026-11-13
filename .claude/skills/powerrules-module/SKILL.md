---
name: powerrules-module
description: Working knowledge of the Rigbay Power-source rules module (modules/powerrules, id power-rules, group power, .NET 8, in process; switches the INTERNAL panel's refresh rate and the Windows power mode when the PC goes on battery or is plugged in; source from RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE) on a message-only window, panel from QueryDisplayConfig/DisplayConfigGetDeviceInfo/EnumDisplaySettingsEx/ChangeDisplaySettingsEx, mode from the undocumented powrprof PowerGetEffectiveOverlayScheme/PowerSetActiveOverlayScheme, one approval of the exact rule set, values-before restore, rigbay power-rules status). Use it before reading or changing anything under modules/powerrules, when touching the display or power-mode calls, the allow-lists, the approval hash, the settle delays (XTU ordering), a language key or a test, and before any live check on the real machine. Saves re-deriving the layers, the safety rules and the traps below.
---

# Power-source rules module: codebase guide

Module id `power-rules`, group `power`, `modules/powerrules/src/Rigbay.PowerRules` (net8.0), tests in `modules/powerrules/tests/Rigbay.PowerRules.Tests`
(about 75, all on fakes; the `WindowsReadTests` class reads the real machine and writes nothing). Version 0.1.0, host API `>=0.1.3 <0.2` (uses `PageModel.Primary`, `Row.Line`, `Row.HelpKey`).
User document and the live-test plan for the owner's gate: `modules/powerrules/README.md`. This repository is PUBLIC.

## Hard constraints (check before you write code)

- **No live write without the owner's gate.** A refresh-rate change, a power-mode change and even `CDS_TEST` are writes for the purpose of a live check. Tests and the live
  read-only run use fakes or reads. The only real classes the tests touch are `WindowsPowerSource` (read + listener), `WindowsPanel.Find` and `WindowsPowerMode.Read`;
  the two refusal tests call `Test`/`Apply` with a rate (1 or 7 Hz) that is rejected **before** `ChangeDisplaySettingsExW`. Keep it that way.
- **The refresh rate is the only display write, and only the INTERNAL panel's.** `WindowsPanel.IsInternal` (output technology internal `0x80000000`, embedded DisplayPort 11,
  embedded UDI 13) picks the display from the *active* paths; `Change` re-finds it and re-lists the rates from Windows on every call (defence in depth), so a stale or
  forged `PanelInfo` cannot reach another monitor or an unlisted rate. Rates 0 and 1 are never sent. The call is `ChangeDisplaySettingsExW` with `DM_DISPLAYFREQUENCY` (the
  other fields are the display's current values, so nothing moves): `CDS_TEST` (`ChangeTest`) first, then flags 0 (`ChangeNow`: this session only). **No `CDS_UPDATEREGISTRY`,
  no `CDS_GLOBAL`/`CDS_FULLSCREEN`/`CDS_RESET`**; a hygiene test scans for them. If a live check shows the change does not survive something, that is a finding for the owner, not a reason to add the flag.
- **The power mode is the three overlay GUIDs and nothing else.** `PowerModes.Guids` (efficiency `961cc777-2547-4f9d-8174-7d86181b8a7a`, balanced = the empty GUID, performance `ded574b5-45a0-4f42-8737-46345c09c238`). The
  native layer takes a `PowerMode`, never a GUID. The two exports are undocumented: bound by name with `NativeLibrary.TryGetExport` from `System32\powrprof.dll`; missing = `Available` false = "unknown", never an exception.
  No `powercfg`, no `PowerSetActiveScheme`, no registry (hygiene tests).
- **The native allow-list** is `HygieneTests.AllowedImports` (kernel32/user32 only). A new `DllImport` fails the test until it is listed on purpose.
- **Every machine change goes through `PowerRulesRuntime.ChangeRefresh` / `ChangeMode`**, under the `_write` lock, and is read back. A change the display or Windows accepts but does not keep is `Failed`, and the "value before" it
  recorded is rolled back. Tests count call sites (`Panel.Test`, `Panel.Apply`, `Mode.Set` once each, test before apply).
- **Automatic switching only runs for the exact rule set the owner approved.** `RuleSet.Hash()` (SHA-256 of both columns) is stored with `AutoApproved`; `IsApproved()` recomputes it on every run, so an edited rule or a
  hand-edited settings file stops the switching (no Arm at start, nothing at the timer). Editing a rule turns the switch off and the line says so. The question is `host.Approval.ConfirmAsync` with the rules in the detail
  text; if the rules changed while it was open the answer is dropped.
- **Initialize writes nothing and arms nothing unless the rules are approved**; then the first check runs on the settle timer (first source seen counts as a change), never inside `Initialize`.
- **Values before**: the first automatic change records the old rate/mode in the settings (before the call, rolled back on failure); a remembered value equal to the fresh read is forgotten (`ClearSettledBefore`, only ever
  called with FRESH values: an earlier version compared against cached state and lost the value before). Turning the switch off, turning the module off (`Stop`, only while approved) and the "Put back" button restore them.
- Rigbay rules (`docs/ABSTRACTIONS.md` section 12): constructor does nothing, `Probe` is read-only (`HasBattery`), all text is a language key, threads and timers catch everything (`TimerScheduler`, the window thread, `Track`), UI only through the host.

## Layers

`PowerRulesModule` (IModule; makes the real `Dependencies` and drops them on `Shutdown` because `WindowsPowerSource.Dispose` is final) -> `PowerRulesRuntime` (partial: `Runtime.cs` reads, writes, approval, switching, messages, status verb;
`Page.cs` rows and page) -> ports `IPowerSourcePort`, `IPanelPort`, `IPowerModePort`, `IScheduler` (`Ports.cs`) -> `Windows/WindowsPowerSource.cs` (message-only window thread, `GetSystemPowerStatus`),
`WindowsPanel.cs` (display config as byte buffers with named offsets, DEVMODE as a 220-byte buffer), `WindowsPowerMode.cs`. Pure: `Model.cs` (`PowerModes`, `Rule`, `RuleSet`, `SourceTracker`, `StepResult`). Settings: `Settings.cs`.

## Behaviour to keep

- **Source**: notification, not polling (an idle module costs nothing). `PBT_POWERSETTINGCHANGE` for `GUID_ACDC_POWER_SOURCE` -> `SourceChanged`; resume (`PBT_APMRESUMEAUTOMATIC`/`SUSPEND`) -> `Resumed`. Both only arm a one-shot timer (`Arm`: a burst runs once; a force sticks). `Read()` after the delay is the truth; a UPS (2) is `Unknown` and switches nothing.
- **Order with XTU**: XTU's auto-switch acts on the same change at once and again on its 5 s timer, and 4 s after resume. This module waits `SettleDelay` 5 s (and `ResumeDelay` 8 s) so Windows has swapped its own per-source power mode and XTU has applied its profile first. The modules share no resource, but an overlay change might make Intel's tuning re-apply power limits: **not verified**, the live plan reads XTU's PL1/PL2 before and after.
- **Steps**: refresh first, then mode; each step yields a `StepResult`; one notification per automatic switch only when something changed or went wrong (`Notify`); no internal panel active is silent (`PanelNotActive`); `NotListed`/`Refused`/`Failed`/`ModeUnavailable` are warnings that say what happened.
- **Defaults** (first run, `EnsureDefaults`): battery = efficiency (+ 60 Hz when the panel lists it), plugged in = balanced (+ the highest rate); rates stay "leave" until a panel has been seen. Switching is off until the owner turns it on.
- **Page**: `Primary` = the switch with its line (`Off` / `Rules on · <source>` / `The rules changed...`); group Now (three value rows + two info rows); Change now (two choice rows with `HardwareEffect` of kind `SystemSetting`: the host asks and offers Revert; the handler runs the write on a pool task and puts the row back to what the machine reports); On battery / Plugged in (four choice rows, no effect of their own); Values before (a button with a literal effect text listing the values, only while something is remembered). The page is rebuilt (`RequestPageRefresh`) when the known rates, the panel state or the remembered values change (`_builtKey`).
- **`[ThreadStatic] _own`** marks a programmatic set of a row value (`SetOwn`); the host's Revert and the user's edit are not own.

## Traps

- `ObservableValue` raises `Changed` on the setter's thread: handlers only `Track` a task and return. `ConfirmAsync` blocks nothing because it runs inside that task.
- Do not compare against `_panel`/`_mode` after a write; read again (this was a real bug found by the tests).
- `FakeCommands` keeps verbs across a second `Initialize`; the real host removes them. Tests clear them (`Verbs.Clear()`).
- Windows 11 keeps a separate power mode for battery and plugged in and probably swaps it by itself at the change; whether `PowerSetActiveOverlayScheme` writes the slot of the current source is **not verified**.
- `ChangeDisplaySettingsEx` without `CDS_UPDATEREGISTRY` may be undone by Windows after a display configuration change (dock, external monitor). Not verified; the resume path re-applies.
- The internal panel is not active while the external monitor on the NVIDIA GPU is the only display on this machine: that is the normal case here, and the rate step is then silent.

## Recipes

**Add a language key**: `module.power-rules.<key>` in `en.lang` and `vi.lang`, same placeholders; `HygieneTests.Every_key_written_in_the_source_exists_in_the_reference_language` (both directions) and LangCheck `--strict` catch mistakes.
**Add a rule item** (for example the keyboard backlight from the PredatorSense module): a field in `Rule`, `RuleSet.Hash()` text (bump `power-rules/1` so old approvals end), a step in `ApplyRule`, a port with an allow-list, the settings, the rows, the dialog text, tests for the same failure cases as the two existing steps. Do not add a write without the owner's gate.

## Live check record

See the section "What was verified" in the module README. Recipes for a live read-only run: `tools\run-elevated.ps1 -Wait -Data <own> -Modules <own> --allow-multi ...` with a copy of the module in your own modules folder; `rigbay power-rules status` prints the reads.
