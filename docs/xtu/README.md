# The XTU profiles module (`modules/xtu`, task C2)

Rigbay module `xtu`, group `power`: switch Intel XTU power-limit profiles (PL1, PL2, time window), switch them by power source, give
them hotkeys and tray items, and publish CPU readings on the metrics bus. It is the in-process .NET 10 design (design A) that spike C1
proved (`docs/spike/XTU-RUNTIME.md`). It ports the standalone app `xtu-profile-switcher`, which stays a working app and was not modified.

The folder is self-contained so it can move to its own repository (`module-xtu`): one project reference, `src/Rigbay.Abstractions`.

| Path | What |
| --- | --- |
| `modules/xtu/Rigbay.Module.Xtu/` | the module (`net10.0`, x64): code, `module.json`, `lang/en.lang`, `lang/vi.lang` |
| `modules/xtu/Rigbay.Module.Xtu.Tests/` | 148 unit tests, no hardware |
| `modules/xtu/tools/live-read.ps1` | the read-only live check below, as a script |
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
- Intel's DLLs are referenced in place (`HintPath` to the Intel folder, `Private=false`) and loaded from there through the host's
  `probePaths` rule. None is copied or committed (`git ls-files modules | grep -i dll` is empty).
- Nothing is written at start: auto-switch is off by default, and its first check runs on the timer, not inside `Initialize`.

## Layers

```
XtuModule (IModule)  ->  XtuRuntime (page, hotkeys, tray, commands, timers, auto-switch, metrics)
                              |
                              +-- ApplyService   validate, read, approval (where the host has not asked), apply
                              +-- IXtuClient     ReadState, ReadMonitor, Apply      <- the interface the logic sees
                                     XtuClient   the proven apply sequence, the latch, validation   (plain C#)
                                        +-- IXtuTuningPort / IXtuMonitorPort
                                              Intel/IntelPorts.cs   the ONLY code that touches Intel's assemblies and WCF
```

Unit tests replace the ports (`FakeTuningPort` records the calls in order and behaves like a small piece of hardware) or the client
(`FakeClient`) and the host services (`FakeHost`). The apply sequence, as the tests assert it: latch, validate, refresh, "already in use"
check, locked and supported-value checks, discard, propose (49, 47, 48, 66), apply, refresh, read back; every failure after the first
write-path call discards the pending proposals.

## The page

Operate-mode page of standard host rows (see `docs/UI-DIRECTION.md`); nothing custom. In order:

1. **Power limits now**: error row when XTU cannot be read, warning row when Windows Fast Startup is on, the active profile (same match
   rule as the old app: first profile whose four values equal the hardware's within 0.01), the limits in force, the **Profile** choice
   (a hardware change: the host shows `A -> B` with the exact values, asks, and offers Revert), the last action, and a Reload button.
2. **Readings**: CPU temperature, package power, usage, clock, PL1, PL2, and a Throttling expander (thermal, power, current, VR).
3. **Switch by power source**: the switch, a profile for AC, a profile for battery, the power source now.
4. **Your profiles**: "New profile" (name, PL1, PL2, PL2 enabled, window, Create) and one expander per profile made in Rigbay (the four values
   with Apply and Discard, and Delete). The number rows carry the old ranges and cross-check PL1 against PL2.
5. **Hotkeys**: one shortcut row per profile. A gesture without Ctrl or Alt, or one XTU's own hotkey pairing already uses, is refused and the
   old one comes back (with a toast).
6. **XTU**: version, restore after restart, and a switch for per-core readings.

Dashboard line: `Profile <name>` (or `Custom limits`, or `XTU not answering`) with CPU temperature and package power.

Rename is not offered: delete and create again (a rename would have to move the hotkey and tray item of the old name).

## Metrics (source `xtu`, about 1 Hz, warm-up skipped)

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
dotnet build modules/xtu/Rigbay.Xtu.slnx -c Release          # needs XTU installed: the Intel DLLs are compile references; 0 warnings
dotnet test  modules/xtu/Rigbay.Xtu.slnx -c Release
dotnet run --project src/Rigbay.LangCheck -- modules/xtu/Rigbay.Module.Xtu/lang --module xtu --source modules/xtu/Rigbay.Module.Xtu --strict
```

`XtuDir` (an MSBuild property) points at the Intel folder; the default is `%ProgramFiles%\Intel\Intel(R) Extreme Tuning Utility\Drivers`. A
public CI runner has no Intel files, so release zips are built on the author's machine until the "C-lite" follow-up of the spike (own copies of the
contracts) is decided. The output folder is the module: copy it to `<modules root>\xtu\`. It holds the module DLL, `module.json`, `lang\`, and the
WCF client packages (`System.ServiceModel.*`, about 2.7 MB); no Intel file.

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
- A module repo build on a machine without XTU (needs the Intel DLLs).

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
reports nothing for all three projects; 148 tests pass. No vulnerable assembly is left that cannot be avoided (the Intel DLLs are not NuGet packages and
are not scanned by NuGet audit). `WarningsNotAsErrors` for NU1901 to NU1904 stays so that a future advisory is a visible warning, not a failed build.
When the WCF packages are updated, drop the pins if the new version already depends on a patched version.
