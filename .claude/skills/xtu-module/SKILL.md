---
name: xtu-module
description: Working knowledge of the Rigbay XTU profiles module (modules/xtu, .NET 10, in process, Intel XTU power-limit profiles PL1/PL2/window over the XTU service's WCF named pipe, metrics on the bus, hotkeys, tray, auto-switch, rigbay xtu verbs). Use it before reading or changing anything under modules/xtu, when adding a profile feature, a metric, a language key or a command, when debugging the XTU connection, the approval flow, hotkeys or auto-switch, and before any live check on the real machine. Saves re-deriving the layers, the safety boundary and the gotchas below.
---

# XTU module: codebase guide

Module id `xtu`, group `power`, `modules/xtu/Rigbay.Module.Xtu` (net10.0, x64) with tests in `modules/xtu/Rigbay.Module.Xtu.Tests` (190 at the last run, 20 of them the wire tests).
It is the port of the standalone app `xtu-profile-switcher` (`D:\Documents\Projects\driver-setup\xtu-switcher`, read-only for us; its skill
`.claude/skills/xtu-switcher` there has the protocol facts: pipes, control ids, monitor ids, persistence, gotchas). User docs and the
verification record: `docs/xtu/README.md`. Recorder hand-off: `docs/xtu/RECORDER-NOTES.md`. Method index: `references/code-map.md`.
Build, run and live-check recipes: `references/testing.md`.

## Hard constraints (check before you write code)

- **Only four controls may be written: PL2 47, PL1 48, PL2 enable 49, window 66.** `XtuIds` is the allow-list; `IntelTuningPort` refuses other ids
  and `XtuClient` builds only those four proposals. Do not widen it without the owner asking. No `ProposeChange` or `ApplyChanges` for anything else.
- **Keep `Limits` and `Rules.Validate`** (PL1 15 to 120, PL2 from PL1 to 150, window 1 to 128). Validation runs before the first service call and before
  any approval dialog.
- **Every write goes through `ApplyService`.** It asks for approval for hotkey, tray and command; page rows are confirmed by the host (they carry a
  `HardwareEffect`), so `ApplyOrigin.PageConfirmed` does not ask again; `Auto` (auto-switch) does not ask because the user's consent was given when
  they turned it on. Never add a path that calls `IXtuClient.Apply` without `ApplyService`.
- **The write latch** (`WriteLatch`: env `RIGBAY_XTU_NO_WRITE=1|true`, or file `no-write.flag` in the module's data folder) makes every write fail before
  it asks. Use it for any run on the real machine where nothing must be written. The elevated process does not inherit your environment: use the file.
- **No Intel file is referenced, loaded, copied or committed** (C-lite, task C4). The module has its own copies of the XTU contracts in `Intel/Contracts.cs`; the csproj has no `Reference`/`XtuDir` and `module.json` no `probePaths`. Only `Intel/IntelPorts.cs` may use those types or WCF. Never add an Intel reference back: the point is that `dotnet build` works on a runner without XTU.
- **Rigbay rules** (`docs/ABSTRACTIONS.md` section 12): constructor does nothing, `Probe` is read-only, UI thread only through `RunOnUi` (the module has no
  UI code), catch everything on threads and timers the module starts (`Guard`, the monitor loop), all text is a language key, no static state that survives `Shutdown`.
- Language: keys `module.xtu.*` in `lang/en.lang` (reference) and `vi.lang`; run LangCheck `--strict` before finishing. A test scans the source for key
  literals and fails when one is missing from `en.lang`.
- The module must not edit `src/*`, `tests/*` or the old repo; host gaps go to the coordinator by mail.

## Layers

`XtuModule` (IModule) builds the real parts and an `XtuRuntime`; the runtime is what tests drive with fakes.
`XtuRuntime` (partial: `Runtime.cs` lifecycle, timers, monitor, apply, auto-switch; `Page.cs` page, values, hotkeys, tray, profile editing).
`ApplyService` -> `IXtuClient` (`XtuClient` = the apply sequence) -> `IXtuTuningPort` / `IXtuMonitorPort` (`Intel/IntelPorts.cs`).
Pure helpers: `Catalog` (list and active match), `ProfilesXml`, `Rules`, `MonitorSampler`, `AutoSwitcher`, `HotkeyPolicy`, `ProbeLogic`, `Fmt`.

## Behaviour to keep

- **Active profile** = first profile whose PL1, PL2, enable and window equal the hardware's within 0.01 (`XtuProfile.Matches`). XTU has no "active profile".
- **Profile list** = "Default (BIOS)" (from the control defaults, 90/115/56 when unreadable), then `Profiles.xml` (only profiles with all four ids), then profiles
  made in Rigbay; an XTU name wins over a custom one (ignoring case).
- **Reading state**: refresh first (`RefreshActiveValuesFromHardwareAccess`), then `GetControl(id).ActiveValue`. `GetTuningControlByID` returns 0 on this build.
- **Apply sequence** (tested in order): latch, validate, refresh, already-in-use check, locked and supported-value checks, discard, propose (49, 47, 48, 66), apply,
  refresh, read back. Failures after the first write-path call discard. "Already in use" writes nothing and asks nothing.
- **Monitor**: one thread, fixed slots at the demanded interval (host API 0.1.4: `XtuRuntime.MonitorDemand` = `host.Evaluate(ids, 1 s, Timeout.InfiniteTimeSpan)`: page/subscriber = Fast 1 s, `SubscribeBackground` (Watch) = Background at max(1 s, N), nothing = Idle = sleep on `_wake`; `MonitorWanted` is "not Idle"; during the warm-up the next read is at most 1 s away; a signal re-slots `next`), bus keeps the last value, connection reset on wake, `Withdraw` of all monitor ids after 3 failed reads in a row (PL1 and PL2 after 3 failed state reads), warm-up skip (after (re)connect XTU returns last run's values; skip until a probe value moves or 4 s), nothing
  published while warming or when XTU is unreachable; on failure retry in 5 s. cpu.clock is MHz (XTU gives GHz), `cpu.usage` is the mean of per-core utilisation.
- **Auto-switch**: `AutoSwitcher.Check` on power change, on the 5 s timer, and forced 4 s after resume; first sight of a power source counts as a change; off by
  default; the first check runs on the timer, never inside `Initialize`.
- **Hotkeys**: default unbound; a gesture without Ctrl or Alt, or one XTU's pairing uses (`uisettings.xml` `HotkeyPairingOn` + `xtu.hotkey.settings`), is refused and
  the previous one restored. Action ids `profile-<slug>`. Deleted profiles keep their hotkey registration (the host cannot unregister) but it finds nothing.
- **Page layout since the UI review (B2, host API `>=0.1.3 <0.2`, now `>=0.1.4 <0.2`)**: the profile `ChoiceRow` is `PageModel.Primary` with the limits in force as its `Line` (no "Active profile", "Limits in force" or "Last action" rows; notifications report changes); with 2 to 5 profiles of at most 20 characters (`XtuRuntime.NamesOnly`) the options are bare names and the `HardwareEffect` texts are literals listing every profile's limits (rebuilt on `LanguageChanged`), otherwise the options carry the limits; readings: four `Tile = Always`, PL1/PL2 `ChartRole.Reference`; auto-switch is one `ExpanderRow` with the toggle as `HeaderRow`; custom profiles are closed expanders with their limits as `Line` and Delete with `ConfirmKey`; group "XTU" = the two `InfoRow`s, hotkeys, Details (reload, throttling, version, restore, per-core). Tests: `PageTests.cs`.
- **Names on the page** go through `Rigbay.Abstractions.Text.Literal` (`=Name`, host task A8, merged). After a change to the profile list or a profile's values the module calls `IHostServices.RequestPageRefresh()` (`XtuRuntime.RequestPageRefresh`); the app redraw is task A7, so until it lands the open page keeps the old lists. The labels of the auto-switch choices (and of the profile choice when it is a drop-down) carry the values (`Name  ·  PL1 .. W / PL2 .. W / .. s`) because the host's approval text for a choice is its label.

## Recipes

**Add a metric.** Id in `XtuMetrics` (also add it to `Monitored`, a `MetricDescriptor` in `XtuMetrics.Descriptors` and a `metric.*` key in en and vi: the dashboard shows the label, and a test requires a descriptor for every id) (shared ids from `WellKnownMetrics` when one fits, else `xtu.<name>`), publish it in `MonitorSampler.Publish` with the right
unit (convert GHz to MHz), add a row in `Page.BuildPage` if it should show, add it to `docs/xtu/RECORDER-NOTES.md`, add a sampler test.

**Add a language key.** `en.lang` first, `vi.lang` the same placeholders, then `dotnet run --project src/Rigbay.LangCheck -- modules/xtu/Rigbay.Module.Xtu/lang --module xtu --source modules/xtu/Rigbay.Module.Xtu --strict`.

**Add a row.** In `Page.BuildPage`. A row that changes the hardware must carry a `HardwareEffect` with a `RevertDescriptionKey` (en and vi; no `SafeRevert` unless every revert is harmless, a button row also a `RevertButtonKey`; the host asks about a revert); a number that writes to hardware uses `ApplyMode.ApplyRevert`.
Do not add a row that writes anything but the four controls.

**Add a command.** In `XtuCommands.Register`; exit codes 0 ok, 1 failed, 2 not found or usage; keep `status` lines identical to `xtu-switcher --status`.

**Change the XTU contract use.** Calls only in `Intel/IntelPorts.cs`; keep the ports' interfaces stable so the logic and tests do not move. A new operation or type
goes into `Intel/Contracts.cs` written from the metadata (`tools/dump-contracts.ps1` prints names, namespaces, attribute values and member shapes of the installed XTU; write
your own declaration, never paste Intel code). The wire format is decided by CLR namespace, type and member names (alphabetical) and interface and method names, so
never rename or tidy anything in that file. A new read-only operation is proved by recording it again (`tools/record-wire.ps1 -OutDir <folder> -XtuDir <Drivers folder>`, elevated, read only,
copy the files to `Rigbay.Module.Xtu.Tests/wire/`) and extending `WireContractTests`; a write operation cannot be recorded without a write, so it needs the owner's approval
for that specific write. If an XTU update changes a contract the module fails at run time, not at build time; the fallback if XTU stops being usable over WCF from .NET 10
is design B of the spike (net48 helper process, `docs/spike/XTU-RUNTIME.md`).

## Gotchas (each cost time once)

- The data contract serializer refuses a non-public type without `[DataContract]`, so the structs and the enum in `Contracts.cs` are `public` (the service interfaces stay internal). `TuningResult` is `[Serializable]` with fields: its wire names are `__codes` and `<GeneralCode>k__BackingField`, so the field and the auto-property must keep those names. From a process that is not elevated a missing pipe and a refused pipe throw the same `EndpointNotFoundException`; only the inner exceptions tell them apart (`PipeCheck`).
- Writing `module.json` through a shell heredoc collapsed `\\` to `\` and made the JSON invalid; a test now parses it. Use the editor tool for files with backslashes.
- An elevated process does not inherit environment variables from the caller: pass the latch as a file, and everything else as command-line arguments.
- The real window is capped near 1100 px by the display, so a long page cannot be rendered in one PNG; render lower groups with a throw-away copy of the module that
  omits the upper ones (not committed).
- `xunit` only runs public test classes; public theory methods cannot take internal types as parameters (use strings and `Enum.Parse`).
- `ProfileEntry` rows and observables outlive `Shutdown` (the instance is reused); handlers are wired once in the constructor, and `_suppressChoice` / `_suppressAuto`
  stop programmatic sets from looking like user edits (setting the saved auto-switch values in `Start` once reset them to empty).
- `System.Security.Cryptography.Xml` and `.Pkcs` (transitive of the WCF packages) are pinned in the csproj to 10.0.14 (patched) with `ExcludeAssets="runtime"`; the build has 0 warnings and neither DLL ships. After any package change run `dotnet list modules/xtu/Rigbay.Xtu.slnx package --vulnerable --include-transitive` and bump the pins (`docs/xtu/README.md`, "Package audit"). Keep `modules/xtu` self-contained for extraction: only the Abstractions ProjectReference may point outside it; it has its own `Directory.Build.props`; `tools/live-read.ps1` reads nothing outside the module folder.

## Verified vs not verified (see `docs/xtu/README.md` for the record)

Verified on the real machine, read-only: probe, load through the real loader (no Intel file anywhere, since C-lite), status and monitor reads equal to the old app,
page in dark, light and Vietnamese, the confirmation dialog with the exact values (not answered). **Not verified: any write** (no apply of any profile), real hotkey
and tray registration, plug/unplug, resume, XTU stopped or updated. **The write-path contracts (`ProposeChange`, `ApplyChanges`, `DiscardChanges` and their types) are only checked against Intel's own serializer output, not against a real reply: the first approved apply is their proof.**
