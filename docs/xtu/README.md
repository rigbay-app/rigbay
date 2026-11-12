# The XTU profiles module (`modules/xtu`, task C2)

Rigbay module `xtu`, group `power`: switch Intel XTU power-limit profiles (PL1, PL2, time window), switch them by power source, give
them hotkeys and tray items, and publish CPU readings on the metrics bus. It is the in-process .NET 10 design (design A) that spike C1
proved (`docs/spike/XTU-RUNTIME.md`). It ports the standalone app `xtu-profile-switcher`, which stays a working app and was not modified.

The folder is self-contained so it can move to its own repository (`module-xtu`): one project reference, `src/Rigbay.Abstractions`.

| Path | What |
| --- | --- |
| `modules/xtu/Rigbay.Module.Xtu/` | the module (`net10.0`, x64): code, `module.json`, `lang/en.lang`, `lang/vi.lang` |
| `modules/xtu/Rigbay.Module.Xtu.Tests/` | 190 unit tests, no hardware and no XTU (170 with fakes, 20 wire-compatibility tests against `wire/`) |
| `modules/xtu/Rigbay.Module.Xtu.Tests/wire/` | the recording of the real service's wire format (read-only, made once) the contract copies are checked against |
| `modules/xtu/tools/live-read.ps1` | the read-only live check below, as a script |
| `modules/xtu/tools/dump-contracts.ps1`, `record-wire.ps1` | print the contract metadata of the installed XTU; record the wire sample (needs XTU, run once, read-only) |
| `docs/xtu/RECORDER-NOTES.md` | XTU columns and meta of the old recorder, for the future recorder module |
| `docs/xtu/shots/` | the PNGs and text files of the live check |
| `.claude/skills/xtu-module/` | the module's own knowledge for the next agent (style of the old `xtu-switcher` skill) |

## Safety boundary (the part that must not move)

- **Only four controls are ever written:** PL2 (id 47), PL1 (48), PL2 enable (49), time window (66). The allow-list is `XtuIds`; the Intel
  port refuses any other id in `GetControl` and `Propose`, and the client only ever builds those four proposals (tested).
- **The old ranges and `Validate` are kept** (`Limits`, `Rules`): PL1 15 to 120 W, PL2 from PL1 to 150 W, window 1 to 128 s. A value outside
  them never reaches the service, and is refused before anyone is asked.
- **Every change is approved first.** Page rows carry a hardware effect, so the host shows the exact change and asks; hotkeys, tray items
  and `rigbay xtu apply` ask through `IHardwareApproval` with the exact values and the ones now in force. A declined or unanswerable
  request (headless run) writes nothing. The one thing that applies without a dialog is **auto-switch**, and only after the user turned it
  on, or chose its profile, through rows that were themselves confirmed by the host (they say so in their text).
- **A write latch** (`WriteLatch`): while the environment variable `RIGBAY_XTU_NO_WRITE` is `1` or `true`, or a file `no-write.flag`
  exists in the module's data folder, the apply fails before it asks anyone, and the Intel port refuses its three write-path methods
  (`Discard`, `Propose`, `ApplyPending`). The live check below ran with the file present (an elevated process does not inherit the caller's
  environment, which is why the file exists). The module logs which state it started in.
- **No Intel file is referenced, loaded, copied or committed** (since C-lite, task C4). The module declares its own copies of the few contract
  types it uses (`Intel/Contracts.cs`: names, namespaces and member shapes only) and builds the WCF client from them; the csproj has no
  `Reference`, `HintPath` or `XtuDir`, and `module.json` has no `probePaths` (`git ls-files modules | grep -i dll` is empty).
- Nothing is written at start: auto-switch is off by default, and its first check runs on the timer, not inside `Initialize`.

## Layers

```
XtuModule (IModule)  ->  XtuRuntime (page, hotkeys, tray, commands, timers, auto-switch, metrics)
                              |
                              +-- ApplyService   validate, read, approval (where the host has not asked), apply
                              +-- IXtuClient     ReadState, ReadMonitor, Apply      <- the interface the logic sees
                                     XtuClient   the proven apply sequence, the latch, validation   (plain C#)
                                        +-- IXtuTuningPort / IXtuMonitorPort
                                              Intel/IntelPorts.cs   the ONLY code that uses the XTU contracts and WCF
                                              Intel/Contracts.cs    the module's own copies of the contracts (interfaces, structs, enum)
```

Unit tests replace the ports (`FakeTuningPort` records the calls in order and behaves like a small piece of hardware) or the client
(`FakeClient`) and the host services (`FakeHost`). The apply sequence, as the tests assert it: latch, validate, refresh, "already in use"
check, locked and supported-value checks, discard, propose (49, 47, 48, 66), apply, refresh, read back; every failure after the first
write-path call discards the pending proposals.

## The page

Operate-mode page of standard host rows (see `docs/UI-DIRECTION.md`); nothing custom. Since 0.1.1 it follows the UI review of November 2026
(`docs/ui-review/REVIEW-2026-11.md` XT-1 to XT-4, LC-1, LC-2, sketch 1) and uses host API 0.1.3 (`hostApi` `>=0.1.3 <0.2`). In order:

1. **Profile**, the page's main control (`PageModel.Primary`), drawn large under the page header. The active profile (same match rule as the old
   app: first profile whose four values equal the hardware's within 0.01) is the chosen option; values that match no profile choose none. The line
   under it says the limits in force (`PL1 45 W / PL2 60 W / 56 s`, or "Custom values: ...", or why XTU cannot be read). A hardware change: the host
   shows `A -> B`, asks, and offers Revert. With 2 to 5 profiles of at most 20 characters the host draws segments, so the options are the bare
   names and the approval text lists every profile's exact limits; with more or longer ones each option carries its limits (a drop-down). The rows
   "Active profile", "Limits in force" and "Last action" are gone: the notifications report every change.
2. **Readings**: CPU temperature, package power, usage and clock as the four tiles; PL1 and PL2 as dashed limits of the power chart
   (`ChartRole.Reference`), never tiles. A host with the review's package B part 2 folds these rows into "All readings".
3. **Switch by power source**: one row with the switch in its header (`ExpanderRow.HeaderRow`) and the power source now in its line ("Plugged in
   now · uses Balanced"); the AC and battery profiles inside.
4. **Your profiles**: one closed row per profile made in Rigbay with its limits in the line (the four values with Apply and Discard inside, and
   Delete, which asks "Delete Gaming?" first, `ButtonRow.ConfirmKey`), then "New profile" (name, PL1, PL2, PL2 enabled, time window, Create).
   The number rows carry the old ranges and cross-check PL1 against PL2.
5. **XTU**: the error row when XTU cannot be read and the warning row when Windows Fast Startup is on (each only while true), **Profile hotkeys**
   (one shortcut row per profile; a gesture without Ctrl or Alt, or one XTU's own hotkey pairing already uses, is refused and the old one comes
   back, with a notification), and **Details**: Reload the profiles from XTU, the four throttling ratios (rows with a sparkline, no tile, no
   chart), XTU version, restore after restart, per-core readings.

Notifications (`docs/APP.md`, "Notifications"): every apply is announced once. A profile chosen on the page, and the auto-switch settings changed
there (including turning it off), are shown in the window while the user looks at it; a hotkey, a tray item, `rigbay xtu apply` and the
auto-switch applying on a power change or after resume always give a Windows notification. A failure, a refusal, a busy service and a missing
auto-switch profile always do. "Already in use" is said for a request by the user and not for the automatic rule; a "no" in the approval is not announced.
6. **XTU**: version, restore after restart, and a switch for per-core readings.

Dashboard line: `Profile <name>` (or `Custom limits`, or `XTU not answering`) with CPU temperature and package power.

Rename is not offered: delete and create again (a rename would have to move the hotkey and tray item of the old name).

## Things to know

- **The monitor is read only while somebody looks.** The 1 Hz monitor thread reads XTU while the module's page is visible
  (`IHostServices.PageVisible`) or any monitor metric has a subscriber (`IMetricsBus.SubscriberCount`: the dashboard bay, a recorder, another
  module; the per-core ids count only while the per-core option is on). Otherwise it sleeps until `PageVisible.Changed` or `SubscribersChanged`
  wakes it, and reads nothing: no pipe traffic, no publishing. Going idle does not withdraw anything: the bus keeps the last sample of every id
  (`TryGetLatest`), now with an old timestamp, which is what the recorder's "older than about 2.5 s means XTU unavailable" rule expects (see
  `RECORDER-NOTES.md`; a recorder that only polls `TryGetLatest` without subscribing will see exactly that). After a pause the monitor
  connection is reset, so the warm-up skips whatever XTU returns first (not verified live whether that is needed: the reset is the safe choice).
  PL1 and PL2 are not monitor values; the 5 s state refresh keeps publishing them whatever is looked at.
- **Withdraw on failure.** Three monitor reads in a row that fail (retry every 5 s, so about 10 to 15 s) withdraw all monitor metrics
  (`IMetricsBus.Withdraw`): the readings on the page say "not available" and a recorder gets an `IsAvailable == false` notification. The first
  good sample after the warm-up publishes them again. Three failed state reads in a row withdraw `cpu.power.pl1` and `cpu.power.pl2` the same way.
- **Labels.** Every metric has a `MetricDescriptor` registered in `XtuRuntime.Start` (`XtuMetrics.Descriptors`, keys `metric.*` in en and vi, 22
  plus 32 per-core), so the dashboard shows "CPU temperature", not `cpu.temp`. The shared ids `cpu.temp`, `cpu.power`, `cpu.usage`, `cpu.clock` are
  also published by other sensor modules; the host keeps the first description it gets.
- **Revert is asked about.** The profile choice and the three auto-switch rows are hardware rows without `SafeRevert`: raising the limits back is a
  hardware change, so a Revert asks like the change did, with its own text (`effect.apply-profile-revert`, `effect.auto-switch-revert`,
  `effect.auto-choice-revert`, en and vi). The page has no button row that changes hardware, so no `RevertButtonKey`. The expander rows of your own
  profiles (`ApplyRevert` numbers) edit values stored in Rigbay, not XTU, and carry no `HardwareEffect`.

## Metrics (source `xtu`, about 1 Hz while looked at, warm-up skipped)

`cpu.temp`, `cpu.power`, `cpu.usage` (mean of XTU's per-core utilisation), `cpu.clock` (highest core, MHz), `cpu.power.pl1`, `cpu.power.pl2`
(every 5 s), plus `xtu.cpu.temp.core-max`, `xtu.cpu.power.cores`, `xtu.igpu.power`, `xtu.cpu.uncore`, `xtu.cpu.active-cores`, `xtu.cpu.ref-clock`,
`xtu.memory.speed`, `xtu.igpu.clock|temp|volt`, `xtu.cpu.volt.cache`, `xtu.cpu.volt.system-agent`, `xtu.throttle.thermal|power|current|vr`, and
optionally `xtu.core<N>.temp|clock|volt|usage`. The brief named `cpu.freq`; the shared vocabulary (`WellKnownMetrics`) already has `cpu.clock`, so
that id is used. Full table and the recorder gaps: `RECORDER-NOTES.md`.

## Commands

`rigbay xtu status` prints the same lines as `xtu-switcher --status` (service, connected, active, matching profile, restore and Fast Startup,
one line per profile with `<== active`); `rigbay xtu list` prints the profile lines; `rigbay xtu apply <name>` asks for approval (headless:
refused, exit 1). Exit codes as the old app: 0 ok (also for "already in use" and for `status` with XTU unreachable), 1 failed or declined, 2 profile
not found or bad usage.

## Settings

`%LOCALAPPDATA%\Rigbay\modules\xtu\settings.json` (version 1): `customs[]` (name, pl1, pl2, pl2Enable, window), `autoSwitch`, `acProfile`,
`batteryProfile`, `publishPerCore`. Hotkey rebinding is kept by the host.

## Auto-switch

Power source from `GetSystemPowerStatus`. A change (power event plus a 5 s backup timer) applies the AC or battery profile; a manual change stays
until the source changes again; after resume from sleep the profile for the current source is applied again 4 s later (the firmware may have
put the limits back). A chosen profile that no longer exists is reported, not applied. Unknown power source: nothing.

## Build and test

```
dotnet build modules/xtu/Rigbay.Xtu.slnx -c Release          # builds anywhere (no XTU, no Intel file); 0 warnings
dotnet test  modules/xtu/Rigbay.Xtu.slnx -c Release
dotnet build Rigbay.sln -c Release                            # the module is part of the main solution too
dotnet run --project src/Rigbay.LangCheck -- modules/xtu/Rigbay.Module.Xtu/lang --module xtu --source modules/xtu/Rigbay.Module.Xtu --strict
```

Since C-lite (task C4) the module builds and its tests run on a machine that has no Intel XTU, for example a hosted `windows-latest` runner: the
contracts are the module's own and the tests use fakes and the recorded wire sample. At run time it still talks to the real XTU service over the
same named pipes. The output folder is the module: copy it to `<modules root>\xtu\`. It holds the module DLL, `module.json`, `lang\`, and the
WCF client packages (`System.ServiceModel.*`, about 2.7 MB); no Intel file.

### Why the copies are trustworthy without XTU: `wire/` and `WireContractTests`

Intel's contract types have no `[DataContract]`, so the wire format comes from CLR namespaces, type names, member names (alphabetical) and the
interface and method names (SOAP actions). A wrong copy would not show in the fake-based tests, only against the real service. So the real
service was recorded once, read-only, by `modules/xtu/tools/record-wire.ps1` (elevated, Windows PowerShell 5.1, XTU installed; no write-path call is
made anywhere in it) into three files under `modules/xtu/Rigbay.Module.Xtu.Tests/wire/`:

| File | Content | How it was made |
| --- | --- | --- |
| `live-messages.xml` | real request and reply bodies of the read-only calls: tuning `Connect`, `RefreshActiveValuesFromHardwareAccess`, `GetControl` 47, 48, 49, 66, `GetDoRestoreUserValuesOnBoot`; monitoring `Connect`, `Start`, `GetValues` (51 values), `Stop` | message inspector on a real session. The `SupportedValues` lists of 7994 (two controls) and 24 entries are cut to 6 (first 4, last 2, with a comment), the process id is replaced by 1234. Nothing else was changed; it has no machine name, user name or serial number (checked) |
| `service-contract.txt` | the WCF service description of the 11 mirrored operations: contract name and namespace, each action and reply action, wrapper and part names, order and types | computed from Intel's interfaces (`ContractDescription`), no service call |
| `type-samples.xml` | the data contract serializer's output of Intel's own types for fixed values: `XtuTuningControl`, `XtuTuningProposal` list, `SdkClientToken`, `TuningResult` (3 cases), `XtuTuningProposalResult` list, `TuningErrorCode`, `Dictionary<uint,decimal>` | Intel's types serialised in a throw-away process, no service call. This is the only evidence for the **write-path types**: a reply with them cannot be recorded without a write |

`WireContractTests` (20 tests, no XTU needed) check that: the copies' service description equals the recorded one line by line; each recorded
sample read by the copies and written back is identical (element names, namespaces, order, values); the copies built from the same values
serialise exactly like Intel's types; the copies' WCF formatters produce the recorded request bodies and understand the recorded real replies;
the write-path requests are composed of the recorded pieces in the order of the contract; the error code enum keeps names and values. A
mutation check (renaming the private `__codes` field) fails five of them. Two facts found while writing them are in the code comments:
the data types must be `public` (the .NET serializer refuses a non-public type that has no `[DataContract]`), and `TuningResult` is
`[Serializable]` with fields, so its wire names are `__codes` and `<GeneralCode>k__BackingField` (encoded).

To record again after an XTU update: run `tools/dump-contracts.ps1 -XtuDir <Drivers folder>` to read the metadata, update `Contracts.cs`, run `tools/record-wire.ps1 -OutDir <folder> -XtuDir <Drivers folder>`
elevated, look at the files, copy them over `wire/`, and run the tests.

## Verification (2026-11-01, this machine, Windows 11 10.0.26300, XTU 7.14.2.135, elevated, `no-write.flag` present, nothing written)

- `dotnet test` for the module: **148 passed**. LangCheck `--strict`: ok, 132 keys, English and Vietnamese complete.
- The real app (`Rigbay.App`, published from this branch) loaded the module from a folder with no Intel file in it; the loader found
  `XtuInterface.dll` and `XtuCommon.dll` through `probePaths` (no resolver code in the module), the probe said supported, the module enabled and
  stopped cleanly, no fault in `rigbay.log`; the log says `writes are switched OFF (no-write.flag)`.
- `rigbay xtu status` and `xtu-switcher --status` printed identical text (`shots/live-rigbay-xtu-status.txt`, `shots/live-xtu-switcher-status.txt`):
  `active: PL1=55 PL2=60 pl2enable=True window=56`, `matching profile: PL55-60W`, `restore after reboot: True   fast startup: Off`, four profiles.
- Monitor values next to the old app's: the old app recorded a CSV (`shots/live-old-app-monitor.csv`) while the module's page was rendered at
  20:35:07. Page: 78 °C, 36.5 W, 50 %, 3993 MHz. Old row 20:35:06.848: 78 °C, 36.513 W, 49.656 % (Windows counter), 3.993 GHz.
- PNGs of the app's own window (PrintWindow of its HWND, never the screen), all looked at: `shots/page-dark.png`, `page-light.png`,
  `page-lower-auto-profiles-dark.png`, `page-lower-about-dark.png` (the window is capped near 1100 px by the display, so the lower groups were
  rendered with a throw-away copy of the module that omits the upper ones), `dashboard-dark.png`, `approval-dark.png`, `approval-light.png`
  (the confirmation with the exact values; the script did not answer it, the app quit with the dialog open), `page-vi-dark.png`.
- After all of it `xtu-switcher --status` still read `PL1=55 PL2=60 pl2enable=True window=56`, and `rigbay.log` has no apply line.

## Verification of C-lite (task C4, 2026-11-01, same machine, XTU 7.14.2.135, elevated, `no-write.flag` present, nothing written)

The Intel references, `XtuDir` and `probePaths` were removed; the module now uses its own contract copies.

- `dotnet build modules/xtu/Rigbay.Xtu.slnx -c Release` and `dotnet build Rigbay.sln -c Release`: 0 warnings, 0 errors. `dotnet test` of the module:
  **190 passed** (the 161 of before, minus 1 test of the removed "DLLs missing" probe outcome, plus 4 for the new pipe outcomes of the probe, plus 6 for the service version, plus the 20
  wire tests).
- Read-only live check through the real host loader (`tools/live-read.ps1`, latch ON, the module laid out with no Intel file, no `probePaths`):
  `rigbay xtu status` printed `active: PL1=55 PL2=60 pl2enable=True window=56`, `matching profile: PL55-60W`, `restore after reboot: True   fast startup: Off`
  and the four profiles, the same as before and the same text as `xtu-switcher --status` run right after (`shots/clite-live-rigbay-xtu-status.txt`,
  `shots/clite-live-xtu-switcher-status.txt`; the old app's file differs only by a BOM and the final newline). The module page showed the readings
  (temperature, package power, usage, clock, PL1, PL2: `shots/clite-page-dark.png`), so the monitoring pipe (`Connect`, `Start`, `GetValues`) works with the
  copies too. The log says `writes are switched OFF (no-write.flag)`; no apply line.
- The wire recording (`wire/`, see above) was made during the same session.
- Build without Intel files: the csproj has no reference or path to the Intel folder; see `docs/xtu/CLITE-PLAN.md` ("Result") for the clean-clone
  build and the `git grep` check.
- Probe: the service and **both pipes**. `PipeCheck` opens a channel to each endpoint and closes it at once (no operation is called). Only "endpoint not found"
  that is not an access refusal counts as missing; from a process that is not elevated the answer is "unknown" and does not block the machine (a
  non-elevated client gets `EndpointNotFoundException` caused by an access refusal, the same text as a missing pipe, which is why the causes are
  looked at). The service version on the page is now read from the service's own image path in the Service Control Manager's registry entry, since
  no path into Intel's folder is built any more (`module.json` lists that registry read).

### Not verified

- **Any write.** No apply of any profile, no Revert, no auto-switch apply, not even the already-active profile. The apply sequence is verified
  with a fake service only; that it works against the real service rests on the standalone app's identical code and on A.3 of the spike (types
  serialise the same on net48 and .NET 10), not on a run of this module.
- `rigbay xtu apply` itself (its approval path is tested with fakes; a headless run was not made).
- Hotkeys and tray items on screen (registration and callbacks are tested against fakes; the real OS registration and the native tray menu were not
  exercised), refusing XTU's own combinations with a real `xtu.hotkey.settings`, plug and unplug, resume from sleep.
- XTU stopped, restarted, or updated while the module runs; the probe outcomes with a real stopped service (tested with fakes).
- High DPI above 100 % for the page (the dashboard shot was 144 dpi), a screen reader, keyboard use, real clicks.
- The page redraw after creating, editing or deleting a profile: the module calls `IHostServices.RequestPageRefresh()` (tested with a fake host), but the app side that redraws (task A7) was not on main when this was checked, so the redraw itself was not seen. The tray menu and the hotkey list were not rendered at all.
- **The write-path contracts against the real service** (C-lite): `DiscardChanges`, `ProposeChange`, `ApplyChanges` and the types `SdkClientToken`,
  `TuningResult`, `XtuTuningProposalResult`, `XtuTuningProposal` are checked against Intel's own serializer output and a composed request, never against a
  real reply (that needs a write). The first approved apply (the test below) is the proof; if the service rejects it, the first suspect is `Contracts.cs`.
- `SdkClientToken.Default` is the process id of the caller: read once from Intel's property in a throw-away process (it equalled that process's id), not
  from a service call.
- A new XTU version may change a contract; the module then fails at run time (a failed call), not at build time. The probe does not compare versions.
- A real GitHub-hosted runner build (a clean clone on this machine was built instead, with no path to the Intel folder; the runner itself was not used).

## Host dependencies (agreed with the coordinator; A8 landed, A7 pending)

1. **Literal text** (A8, in): profile names go through `Text.Literal` (`=Name`) in choices, tray items, hotkey names and the expanders of your own
   profiles. The page renders them correctly with the merged Core (`shots/page-dark.png`, `shots/approval-dark.png`); before A8 they showed as
   `module.xtu.=Name`. The App side for the navigation pane, tray menu and hotkey list is A7.
2. **`RequestPageRefresh`** (A8, in; the redraw is A7): the module calls it after Create, Delete, an edit of a custom profile and Reload. Until A7 the open
   page does not redraw by itself; turn the module off and on to see a new list.

## The live apply test to ask the owner for (not done)

Smallest useful test, reversible, inside the old app's own safe range, using profiles that already exist in XTU:

1. Start Rigbay with the module (no `no-write.flag`), note `xtu-switcher --status`: now `PL55-60W` (PL1 55, PL2 60, enable on, window 56).
2. In the module page choose **`PL35-40W`** (PL1 35, PL2 40, window 56, PL2 on). Expect the dialog `PL55-60W -> PL35-40W`, then Apply. Expect
   `Applied PL35-40W` and `xtu-switcher --status` showing `PL1=35 PL2=40 pl2enable=True window=56`, matching `PL35-40W`.
3. **Undo:** choose `PL55-60W` again (or press Revert next to the choice). Expect `PL1=55 PL2=60`. This is also the test of "revert".
4. If anything looks wrong: `xtu-switcher --apply PL55-60W` puts the previous profile back from the old app.

Lower values are the safe direction (less heat); the test does not go above the profile the machine already runs. A second test, only if the
first is fine: `rigbay xtu apply PL35-40W` from a command line while the app runs, then the hotkey path, then auto-switch on AC and battery.

## Package audit (task C3, 2026-11-01)

The module runs inside the elevated host, so vulnerable packages are fixed, not suppressed. The WCF client packages (`System.ServiceModel.Primitives`
and `.NetNamedPipe` 10.0.652802) pull `System.Security.Cryptography.Pkcs` and `.Xml` 10.0.0, which had 13 advisories (GHSA-2fmv-rv2c-59x7, GHSA-846f-f764-76mx
for Pkcs; GHSA-23rf-6693-g89p, -37gx-xxp4-5rgx, -6588-8gv4-xfgh, -7hqp-7c5m-4vq6, -8q5v-6pqq-x66h, -c399-9853-6jmj, -cvvh-rhrc-wg4q, -g8r8-53c2-pm3f, -mmjf-rqrv-855v,
-r955-8q5j-3h83, -w3x6-4m5h-cxqf for Xml; the highest fixed version among them is 10.0.13). Both are now pinned with explicit `PackageReference`s to **10.0.14**
(latest 10.x on nuget.org that day) in `Rigbay.Module.Xtu.csproj`, still with `ExcludeAssets="runtime"`: the named-pipe client never uses message signing, and
neither DLL is in the build output. Result: `dotnet build modules/xtu/Rigbay.Xtu.slnx` 0 warnings, 0 errors; `dotnet list package --vulnerable --include-transitive`
reports nothing for all three projects; 148 tests passed at that time. No vulnerable assembly is left that cannot be avoided (since C-lite no Intel DLL is
used at all). `WarningsNotAsErrors` for NU1901 to NU1904 stays so that a future advisory is a visible warning, not a failed build.
When the WCF packages are updated, drop the pins if the new version already depends on a patched version.
