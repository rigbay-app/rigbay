---
name: predatorsense
description: Working knowledge of how Acer PredatorSense V3 controls an Acer Predator laptop (Triton 500 SE, PT516-51s) - service PSSvc, its named pipe, and the root\WMI AcerGamingFunction methods - and what is verified, not verified and forbidden. Use it before writing or changing anything for a Rigbay PredatorSense module, before calling any Acer WMI method, when adding a fan, performance-mode, lighting or sensor feature, or when writing the module's probe ("is this machine supported"). The Rigbay module itself is `modules/predatorsense/` (task B6, rules below). DRAFT from the B1 to B7 tasks; firmware reads (sensors, one Cool Booster read) and two fan-mode writes so far (all Auto, B4; all Max for 14 s then Auto again, B5; both status 0). B7 decoded the operation mode (performance modes) from the code, static only, and B9 re-checked it independently with Ghidra (same result); B10 ran the two mode reads once each (current mode = Default, supported = Quiet, Default, Extreme, Turbo) and module 0.1.2 shows the mode read only; no mode write has ever been run. B11 (2026-11-13) decoded the keyboard lighting (brightness, zone colour, effects, profile, what Acer re-applies) by static analysis of the UI, `PSSvc.exe` and `PSAdminAgent.exe`; nothing was called, and it corrected three earlier statements (backlight read input is `0x88401` not `0x80184`; pipe 13/14 are `SetGamingLED`/`GetGamingLED`; Acer calls the generic method class `APGeAction`).
---

# PredatorSense: working knowledge (draft)

Source of truth for the evidence is `docs/predatorsense/FINDINGS.md` (tables, evidence tags C/O/G). This file is the short
version for people writing code. The first approval (CPU temperature, done) is `docs/predatorsense/GATE-getgamingsysinfo.md`;
the candidate reads are in `docs/predatorsense/GATE-b2-reads.md`; B3's live results are `FINDINGS.md` section 12; the candidate
writes are in `docs/predatorsense/WRITE-PROPOSALS.md` (W4a and W4b have been run; results are `FINDINGS.md` sections 13 and 14).

Detail files (read only when needed):
- `references/wmi-encodings.md`: every input encoding decoded so far, with status.
- `references/probe-and-recipes.md`: running the probe, reading the cache, a "supported?" check, how to add a decoded call.

## The Rigbay module (B6): read this before touching `modules/predatorsense/`

`modules/predatorsense/` is the real module (own solution, `Rigbay.Abstractions` is its only reference; it moves to the
`module-predatorsense` repo later). How it works: `docs/predatorsense/MODULE.md`. The rules a change must keep:

- **Two writes exist, both `SetGamingFanBehavior`: Auto `0x01410019`, Max `0x02820019`.** They are a hard-coded table in
  `Wmi/WmiAllowList.cs`, checked in `GuardedAcerWmi` and again inside `AcerWmi`. Reads: `GetGamingSysInfo` indexes 1, 2, 3, 6, 9, 10
  and, since 0.1.2, `GetGamingMiscSetting` `0x0B` (current performance mode) and `0x0A` (which modes exist), through `IAcerWmi.ReadOperationMode()` / `ReadSupportedOperationModes()`
  which take no argument. Any new call (a performance-mode WRITE, Cool Booster, MaxQ, fan speed, lighting, battery) needs a decoded encoding, an owner gate, a table entry and
  tests that show every other value is still refused. Never widen the table to "make it work".
- **Never run a write from a test, a script or the app against the real machine as a check.** The fake (`FakeAcerWmi` in the tests)
  covers the write path. The tests call the real `AcerWmi` only with values that must be *refused* (before any WMI object exists).
- **The performance mode IS readable (0.1.2, B10 + P1), read only; the fan mode is not.** The page has a "Performance mode" row (current mode from `GetGamingMiscSetting(0x0B)`: status = low byte, id = bits 8 to 15, 0 Quiet, 1 Default, 2 Render, 3 Edit, 4 Extreme, 5 Turbo, anything else "Unknown"; options = the modes `0x0A` advertises, bit N = mode id N). Since the UI review (B2) it is one `ValueRow` (current mode, "Read only" description, "This laptop offers ..." as `Row.Line`, the why as `HelpKey`); a never-enabled `ChoiceRow` was tried and dropped (greyed note, no tooltip, the four Vietnamese labels overflow the row's segment width so the host shows a drop-down). The capability is read once at module start, the mode every 10 s only while the page is visible or the metric `predatorsense.opmode` has a subscriber, or at max(10 s, N) while a Watch-style background demand (host API 0.1.4, `SubscribeBackground`) exists (never otherwise), and at once when somebody starts to look. `PredatorSenseModule.SensorDemand`/`OpModeDemand` are `host.Evaluate`; `ApplyDemand` sets the `PollScheduler` (`Watched`, `BackgroundInterval`) and polls at once when reading starts or gets faster. The verbs carry `CommandInfo`: `status` ReadOnly, `fan` Writes/Hardware with a preview that names the mode only and `UndoArgs` = the mode Rigbay set before (or `auto` after a first `max`). No selector: changing it is a write nobody approved (`SetGamingMiscSetting((mode << 8) | 0x0B)`, W3 in `WRITE-PROPOSALS.md`). Only Default was ever seen live (one observation), so keep "Unknown" for what is not understood.
- **Page layout since the UI review (B2, host API `>=0.1.3 <0.2`):** the fan mode is `PageModel.Primary` (two segments; while nothing is set the value is the localised words "Not set" = not an option = nothing chosen, and the host prints it as is in the approval; `Row.Line` "Set to Maximum at 18:42 · CPU fan sped up ..." replaces the old "Last set" and "Speed check" rows); temperatures are `Tile = Always` with their fan as `ReadingRow.Secondary`; fans `Tile = Never`, `ChartGroupKey = ""`; groups Readings, Performance (read-only mode as text), Settings (shortcut, restore, Details: machine, poll interval). Tests: `PageTests.cs`.
- **No firmware read of the fan mode exists.** The page says what Rigbay set, never "current mode", and the fan mode chooses nothing (0.1.1 used a "Not set" option, 0.1.2 a value that is not an option) until Rigbay has set one in this run (E2E-10). The RPM check after a write
  is a reading (a jump of 300 rpm within 10 s), never proof. B5 saw about +3000 rpm within 2 s on Max.
- **Absent parts read status 0 and value 0.** A temperature of 0 is never a reading; a fan at 0 counts only after that fan has turned.
- **No write while the readings are not available** (the last poll got no answer, or 5 polls in a row; the sensors are then withdrawn from the bus). One exception, on purpose:
  putting the fans back to Auto when Rigbay set Maximum.
- **Host behaviour the module follows (A9, M1).** (1) Poll rate: `PollScheduler.Watched` = `PageVisible.Value` or any sensor metric with
  `SubscriberCount > 0`; fast = `pollSeconds` (default 2 s), slow = 10 s (`PredatorSenseSettings.IdlePollSeconds`); `PollSoon()` reads at once
  when somebody starts to look. (2) A sensor that had values and gets none for 5 polls in a row is `Withdraw`n; a new value publishes it
  again; absent parts are never withdrawn. There is no "stale readings" warning row any more. (3) Every sensor metric has a
  `MetricDescriptor` (`sensor.*` label keys, en and vi). (4) The fan row is a hardware row without `SafeRevert`: a Revert (either direction)
  is asked, text `effect.fan-mode-revert`. Keep all four when adding a sensor or a row; tests: `PollRateAndLabelTests`, `PollerWithdrawTests`.
- Every WMI call has a timeout and runs one at a time; a stalled call keeps the line until it returns, so calls never pile up.
- Reads send `UInt32`, the write sends `UInt64` after checking the schema type.

## Hard constraints

- **Never write to the hardware without the owner's approval for that exact call and value.** Fan, performance mode,
  overclock level, GPU mode, lighting firmware state, BIOS options, services, drivers: all writes. Open `csm gate`
  and wait. If unsure whether a call is a read, it is a write.
- **A name is not proof.** The MOF marks every `AcerGamingFunction` method read=true and write=true. `Get*` is a
  convention. Call only inputs traced to the Acer UI's own code, one at a time.
- **Never call `AcerBiosConfigurationTool`** (BIOS options, password argument).
- **Do not use the service pipe `\\.\pipe\predatorsense_service_namedpipe`.** `PSSvc` checks the caller's image path (B7: the service folder plus `PredatorSense.exe`, `PSAdminAgent.exe`, `PSAgent.exe`, `PSMobile.exe`,
  `PSLauncher.exe`, `PSSvc.exe`, or the `WindowsApps\AcerIncorporated.PredatorSenseV30_*` app, or its package family); there is no signature check. Talking to WMI
  directly from the elevated host is the route. Do not impersonate the Acer app or run from its folders.
- **No Acer files or decompiled code in the repo.** Reference the installed files in place; write facts (names, ids,
  numbers) in our own words and our own code.
- **Needs administrator.** WMI instance access under `root\WMI` is denied to normal users.
- Toolchain matches the rest of Rigbay's early code: C# 5 / `csc.exe` from .NET Framework, no SDK (the probe is built that way).

## Interface in one page

```
PredatorSense.exe (WPF) --pipe--> PSSvc.exe (native, LocalSystem) --WMI root\WMI--> WmiAcpi -> BIOS/EC
                                      |-- NvAPI (GPU clocks), Intel OC SDK -> XTU3SERVICE
                                      '-- admin agent pipe predatorsense_admin_agent_<session>
```

- Pipe frame (C): cmd id (2 bytes), arg count (1), per arg: length (4) + bytes. Reply: count (1), per arg length (4) + bytes.
- WMI (O): `root\WMI:AcerGamingFunction` GUID `{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}`, 25 methods (ids 1 to 25, all take/return
  one UInt32/UInt64 except KBBacklight and CPU overclock profile); `AcerGenericMethod` GUID `{1EA0BC57-521E-461B-A7A9-5D36F141A82A}`
  with `SetFunction` (1) and `GetFunction` (2).
- Answers: low byte is a status (0 = ok), payload above it (sensors: bits 8 to 23).
- Sensor read: `GetGamingSysInfo(1 | (index << 8))`, index 1 CPU temp, 2 CPU fan, 3 system temp, 4 system fan, 6 GPU1 fan,
  10 GPU1 temp, 11 GPU2 temp (full list in `references/wmi-encodings.md`). **All of indexes 1 to 17 were read live (B3), all status 0.**
  On this machine: CPU temp 63 to 73 C (varies with load), system temp 44, GPU1 temp 53 (equals `nvidia-smi` to 0.4 C, so temperatures are
  degrees C), CPU fan 2857, GPU fans 3061 and 3083 (RPM by magnitude, not proven). **Absent hardware (system fans, GPU 2 temp) answers status 0
  and value 0, not an error**; use `HW_Support.ini [FanSupport]` for what exists. **The "rate" indexes 12 to 17 read 0 while fans spin: do not use them.**
- **The CPU value of `0x0101` does not match the ACPI zone `TZ00`** (63 to 73 vs a flat 52). Different sensors; `TZ00` is not the CPU. Windows has
  no other CPU temperature source without a driver.
- **`AcerGenericMethod.GetFunction(0x207)` (Cool Booster state) answered status `0xE1` (raw `0x2E1`).** The UI makes this exact call at fan-page init and
  turns any non-zero status into "off". A non-zero status is not a failure of the call, it is an answer nobody has decoded. **No proven read-back of Cool Booster.**
  The matching write is `SetFunction(0x10007)`; judge it by fan RPM (sysinfo 2, 6, 9), not by this read.
- This machine: 1 CPU fan, 2 GPU fans, no system fan; operation modes Quiet/Normal/Extreme/Turbo; no CPU OC, GPU OC via WMI;
  discrete-GPU switch supported; RGB keyboard. No battery charge limit exists in PredatorSense here.
- This machine's UI variant is **Clubman** (`Feature.ini [MachineType] Type=8`). Its lighting is **WMI** (`SetGamingRgbKb`,
  `SetGamingKBBacklight`, `SetGamingLED`, `APGeAction.SetFunction`/`GetFunction`), not the USB admin-agent route. Decoded in B11, **static only, never run**; the short version is the next bullet, the full tables are `FINDINGS.md` section 16 and the plan is `WRITE-PROPOSALS.md` W5.
- **Keyboard lighting (B11, static analysis; nothing was called).** 3 zones (`KeyBoardArea` = 3), SMBIOS type 172 version 2.83 (< 2.86) so Acer uses the old input forms. (1) **Zone colour:** `AcerGamingFunction.SetGamingRgbKb` (id 6), UInt32 `zone | R<<8 | G<<16 | B<<24`, `zone` = 1, 2, 4 (one bit per zone, only single bits are ever sent).
  (2) **Brightness and effects:** `SetGamingKBBacklight` (id 20), a `byte[16]`: byte 0 mode (0 static, 1 Breathing, 2 Neon, 3 Wave, 4 Shifting, 5 Zoom, 6 Meteor, 7 Twinkling), byte 1 speed 1 to 9, byte 2 brightness 0/25/50/75/100 (Acer slider level 1 to 5 = `(l-1)*25`), byte 3 = 8 for Wave only,
  byte 4 direction (Wave, Shifting), bytes 5 to 7 R, G, B; bytes 8 to 15 are heap garbage in Acer's service (send zeros: my choice, G). All-zero = "close" (off). (3) **Zone on/off mask:** `SetGamingLED` (id 2, the UI's "LEDBehavior" pipe 13), `8 | (zone mask << 40)`, three zones on `0x70000000008`.
  (4) **The only traced lighting read:** `APGeAction.GetFunction(0x88401)` (= `1 | 132<<8 | 0x80000`): status low byte, bits 32 to 39 brightness percent, bits 40 to 47 timeout flag (`30`: meaning open); never run. **No colour, zone or effect read exists in any Acer binary** (`GetGamingRgbKb`, `GetGamingKBBacklight`, `GetGamingLEDColor`, `GetGamingLEDBehavior` are never sent).
  (5) The service sleeps 30 ms after each WMI call; the UI fires colour and backlight calls without waiting. (6) **After a resume or at agent start `PSAdminAgent` re-applies the Acer profile** (here brightness 1 = off, `#00AEC7`) and undoes a Rigbay lighting write: re-apply after resume. Acer's profile is `ProfilePool\LightProfilePool\<name>\Main.xml`
  (read it, never write it). Acer's own record says the backlight is off now (`KBBackLight_State` 0, profile brightness 1), so a lighting test needs the owner to set a visible state first (W5 preconditions).
- **The UI never reads fan behaviour, fan speed, fan table, operation mode or lighting colour back from firmware.** It uses the
  registry cache plus `GetGamingSysInfo`. So those `Get*` inputs are untraceable to the UI: do not call them. (Exception found in B7: the **service** reads the mode with `GetGamingMiscSetting(0x0B)` and `(0x0A)`;
  both were run once each in B10 with the owner's approval and are now in the module's allow-list, read only.) The traceable reads are
  in `references/wmi-encodings.md` (sysinfo, Cool Booster `GetFunction(0x207)`, keyboard backlight `GetFunction(0x88401)`; **the backlight input was written `0x80184` before B11 and is wrong: do not run that**; Acer calls this method on the class `APGeAction`, and **W5-R0 (2026-11-13) showed `APGeAction` is a different class from `AcerGenericMethod`** (two GUIDs, same method shape); W5-R1 ran once: `APGeAction.GetFunction(0x88401)` status 0, raw `0x1E0000000000` = backlight off, `FINDINGS.md` 16.10).
- Fan write encodings are decoded (`SetGamingFanBehavior`: fan mask low half, 2-bit mode per fan in the high half; all-Auto here
  is `0x01410019`); see the encodings file. **`SetGamingFanBehavior(0x01410019)` was run once in B4: `gmInput` is UInt64, answer raw `0x0`
  (status 0) in 11 ms, fans and temperatures unchanged for 20 s.** **`SetGamingFanBehavior(0x02820019)` (all Max) was run once in B5 for 14 s: status 0 in 13 ms, fan speeds
  (sysinfo 2, 6, 9) went from about 2600 to 2700 to 5555 / 5882 / 5900 within 2 s and stayed there; the undo `0x01410019` (status 0) brought them back to the Auto range within 2 s. So a
  fan write has a measurable effect and the mask/mode table is right for modes 1 and 2. The first read ~16 ms after the write still shows the old RPM: wait about 2 s.** Custom values have never been run.
- **Operation mode (B7, static analysis, never run).** The UI sends service pipe command 53 with the mode (Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5). `PSSvc.exe` then (1) optionally tells the admin agent to set NVIDIA
  Whisper mode (command 35, on for Quiet only), (2) calls **`SetGamingMiscSetting` with `(mode << 8) | 0x0B`**, (3) does a GPU step: NvAPI offsets from `PredatorSense.ini` (this GPU: zero except Turbo core +100, memory +360) and
  `SetGamingMiscSetting` `(level << 8) | 0x05` with level 0 / 1 / 1 / 1 / 2 / 3 for modes 0 to 5. The service itself checks no AC, battery or GPU mode, does not re-apply on resume, writes no registry. The **UI** writes the cache
  `Overclock\CurrentOperationMode` on click (even if the service failed); **`PSAdminAgent`** resets the mode to Default (1) on power-source change, low battery and resume, and handles the Turbo key. There **is** a firmware read in the service,
  `GetGamingMiscSetting(0x0B)` (mode) and `(0x0A)` (capability), never used by the UI, layout unknown. **B9 (Ghidra, same values as B7):** no Acer code parses these answers (the service passes the raw 64-bit value to the pipe, `PSAdminAgent`
  never reads them), so a first read must log the raw value and interpret nothing. The service never checks the WMI answer and its pipe reply is always `0xFFFFFFFF`. A mode change is WMI call, then NvAPI P-state offset (absolute, `ini x 1000` kHz, **non-Turbo modes reset it to 0**), then
  WMI call 2; the 100 ms sleeps in the NvAPI part happen only after a failed try, the 30 ms sleep after each successful WMI call. Whisper (`NV_WhisperMode20_Support` = 1 here) goes through the admin agent. Details: `FINDINGS.md` sections 15 and 15.8; test plan: `WRITE-PROPOSALS.md` W3
  (**the reads W3-R1/R2 were run once each in B10, 2026-11-12: `0x0B` gave `0x0B0100` = status 0, mode byte 1 = Default as the cache; `0x0A` gave `0x0B3300` = status 0, mask bits 0, 1, 4, 5 = Quiet, Default, Extreme, Turbo, so no Render/Edit; both G for the layout, `FINDINGS.md` 15.9**). Rules: a direct write is only `SetGamingMiscSetting(0x10B/0x0B/0x40B/0x50B)` after its own owner gate; never write Acer's registry cache; a WMI-only Turbo is not Acer's Turbo (no NvAPI part); undo is always `0x10B`.
- **Set methods take UInt64 `gmInput`; the Get methods take UInt32** (the probe casts to `uint`). The Set answer is `gmOutput` UInt32, status = low byte, 0 = ok.
- **Fan RPM in Auto jitters by one step (about 22 to 24 RPM) and drifts with load**; use jumps of hundreds of RPM as the signal. `nvidia-smi` fan speed is `[N/A]` here, so sysinfo 2, 6, 9 are the only fan reading.

## Verified / not verified

| Verified (read or observed) | Not verified |
| --- | --- |
| Service, pipe names, WMI classes and method ids, registry and ini layout, command id lists | Any live firmware value other than the reads in the next two rows |
| **Live (B10, owner-approved, read-only, once each):** `GetGamingMiscSetting(0x0B)` = `0x0B0100`, `GetGamingMiscSetting(0x0A)` = `0x0B3300`, both status 0, baseline sensors unchanged before and after, **no write made** | Further runs of these two reads need the owner's approval again |
| **Live:** `GetGamingSysInfo` indexes 1 to 17 all return status 0 (values above; elevated, owner-approved, read-only). GPU1 temp equals `nvidia-smi` | Fan speed unit (RPM by magnitude only); CPU `0x0101` against an independent CPU sensor; what the rate indexes mean |
| **Live:** `GetFunction(0x207)` returned status `0xE1` (undecoded; it went to the class `AcerGenericMethod`, Acer's code uses `APGeAction`) | `APGeAction.GetFunction(0x88401)` (backlight), battery boost, `GetGamingMiscSetting(2/9)`, `GetGamingLED(1)` (UI name `GetGamingLEDBehavior`): never run |
| Encodings of CoolBooster, CPU/GPU OC level, MaxQ, GPU mode, sensor index, battery boost, AC mode, fan behaviour and fan speed writes (from UI code) | What those writes do on this machine (never run), except the one below |
| **Live write (B4):** `SetGamingFanBehavior(0x01410019)` once, elevated: status 0, 11 ms, RPM 2564 to 2586 / 2654 to 2678 / 2663 and temperatures unchanged for 20 s | That the fan mode changed or was re-asserted (no read-back); whether `PSSvc` re-applies its mode; survival across sleep or reboot; Custom, Cool Booster, MaxQ |
| **Live write (B5):** `SetGamingFanBehavior(0x02820019)` (Max) then `(0x01410019)` (Auto), both status 0 in 13 ms; RPM 2600 to 2700 -> 5555 / 5882 / 5900 within 2 s, flat for 14 s, back in the Auto range 2 s after the undo | The mode itself (inferred from RPM only); ramp time under 2 s; RPM unit; cooling effect; what the owner heard; behaviour under load or across sleep/reboot |
| UI variant Clubman, lighting route WMI, the UI has no read of fan/mode/colour | Fan table payload |
| **Static (B11, UI + `PSSvc.exe` + `PSAdminAgent.exe`, Ghidra 12.1.4):** the lighting calls, their inputs and order, the zone model, the effect bytes, the profile files, what the agent re-applies; this machine's SMBIOS type 172 version 2.83 (old input forms) | What the firmware does with any of it; every lighting reply layout; whether the colours survive brightness 0 or an effect; the meaning of the timeout byte `30`; zone positions; the agent's type-8 branch to its end. Lighting calls run: only W5-R0 (schema) and W5-R1 (`GetFunction(0x88401)` once: status 0, backlight off), 2026-11-13 |
| **Static (B7, re-checked by B9 with Ghidra 12.1.4):** service operation-mode mapping (`(mode << 8) \| 0x0B`, GPU step, 30 ms after each WMI call, 100 ms only after a failed NvAPI try), who writes the cache, what the agent resets, the pipe caller check is an image-path check. Both decoders agree on all values | Anything about the firmware's behaviour per mode; reply layout of `GetGamingMiscSetting(0x0A/0x0B)` beyond one observation each (no Acer code parses it; B10 read `0x0B0100` and `0x0B3300` once, layout fits but is G; byte 2 = `0x0B` in B10 but `0x0C` in P1's live mode read, so not a constant, unexplained, ignored; R1 in a non-Default mode never seen); whether Render/Edit are accepted; runtime values of the service's in-memory flags; the managed UI part of section 15.2 and the agent's low-battery numbers were not re-read |
| Pipe caller check exists | Pipe ACL, behaviour of a refused connect |
| Charge limit is absent from PredatorSense v3 here | Charge limit via firmware Misc/Profile methods (no evidence) |
| | What resets settings (sleep, reboot, Turbo key); meaning of `GetGamingLEDBehavior(1)` bits |

## Gotchas

- Registry `HKLM\SOFTWARE\OEM\PredatorSense\...` holds what the app last set, **not** live state. Treat as a hint.
- The Acer service re-applies settings on power/session events (likely). A Rigbay write may be undone by it, or undo the
  user's Acer setting. Decide whether the module requires PredatorSense to be stopped. Stopping a service needs approval.
- Intel XTU and NvAPI are driven by the same service; a Rigbay XTU module competes with PredatorSense for CPU limits.
- Packed bit fields (fan behavior, LED group): one wrong bit changes another fan. Send only values copied from a decoded table.
- `GetCPUOverclockingProfile` / `SetCPUOverclockingProfile` take byte structs; not decoded; CPU OC is not supported here anyway.
- Acer's own guards (overclock stopped at low battery) are not enforced when calling WMI directly.

## Recipes

**Is this machine supported (module Probe).** Manufacturer is Acer; class `root\WMI:AcerGamingFunction` exists and has
`GetGamingSysInfo` with `WmiMethodId` 5 (schema only, `ManagementClass.Methods`, no instance, no call). Optional detail from
`HW_Support.ini` / `Feature.ini` if present; absence of those files means "unknown", not "unsupported".

**Look at the machine without touching it.** Build and run `tools/predatorsense-probe` (`build.ps1`, then `bin\predatorsense-probe.exe`, `--json`).
It calls no WMI method by default.

**Read through the probe (needs an approved gate or an explicit owner/coordinator approval).** Elevated: `predatorsense-probe.exe --allow-call <flag>...`
with one flag per approved call (`--sysinfo-index N`, `--r1`, `--r2`, `--kb-backlight`, `--battery-boost`, `--misc-get 2|9|10|11`, `--led-get`; **B11 replaced the old `--r3` (wrong input `0x80184`) and `--led-behavior-get` (wrong method id 11) by `--kb-backlight` (`APGeAction.GetFunction(0x88401)`) and `--led-get` (`GetGamingLED(1)`); the old two are refused, see `FINDINGS.md` 16.1 and 16.9. The new offline refusal checks passed on 2026-11-13 before W5-R1; run `--self-test-refusals` before every new read**). The probe has an allowlist,
refuses `Set*`, repeats and unknown inputs, and halts at the first non-zero status. Start it with `Start-Process -Verb RunAs` writing to a file under `bin\`
(ignored by git; the file is UTF-16 when written by PowerShell redirection, so use `Get-Content`/`Select-String`, not `grep`), then read the file. The owner's UAC
auto-elevates, no prompt. Paste the output into FINDINGS.md. When a call returns a surprise, **stop and mail the coordinator**; do not run the next one.

**Is a call safe to run?** Only if its exact input appears in the UI's code as a read and the owner approved that one call. "The UI
has the wrapper" is not enough: `WMIGetGamingFanGroupBehavior/Speed/Table` exist but nothing calls them.

**Decode a new call.** Find the UI helper (`CommonFunction`, `FanControlPage`, `OperationMode_Button`, `MonitorAndOCClass`,
`WMIFunction` in `PredatorSense.exe`) with REA `inspect-managed-members` or a metadata-only IL lister (parse metadata and IL
bytes with `System.Reflection.Metadata`; never load or run Acer code). Find who calls the `WMIFunction` wrapper first: no caller
means no traced input. The model-to-window mapping is `App.OnStartup` on `Feature.ini [MachineType] Type`.
**Native service code (`PSSvc.exe`, `PSAdminAgent.exe`).** With Ghidra (B9; the owner approved a portable install on 2026-11-12; ask again, with a `csm gate`, before installing anything else): portable Ghidra 12.1.x zip plus a portable JDK 21 zip, SHA-256 checked against the publisher,
extracted into a new folder outside the repo (for example `D:\Documents\Projects\_tools\ghidra-scratch`), `JAVA_HOME`, `GHIDRA_INSTALL_DIR`, `GHIDRA_MAXMEM=4G` set only in a batch file that wraps `support\analyzeHeadless.bat`. Import the Acer exe in place (`-import <path>`; about 40 s for `PSSvc.exe`),
then run small GhidraScripts with `-process <name> -noanalysis -readOnly -postScript` (Java scripts compile with the JDK; no Python needed): string cross-references, decompile by address, a listing dump with strings and callees, who-calls-whom, a qword dump of the handler table. Traps: PowerShell blocks `.ps1` here (use `.bat` and `cmd /c`);
a `|` in a batch argument breaks cmd (pass regexes through a file); the decompiler hides the variadic WMI argument lists (read the listing: the names `gmInput`/`gmOutput` and the method name are loaded just before the exec-helper call) and shows void helpers as returning values; `reg query | head` can cut a value you need.
What to look for first: the log strings name every handler (`treadstone::TsClientCommandProcessor::<name>`), the pipe loop (`0xCB30`) calls `table[command id]` at `0x69AD0` (entry N = command N, highest 0x35), the WMI exec helper is `0x238B0` and its callers list every WMI method the service can call.
Without a disassembler (B7's way): read the PE as bytes, take the function list from `.pdata`, find RIP-relative `lea` references to the log strings and decode single functions with a small x86-64 decoder of your own; B9's pass shows that this gave the right answer for the operation mode. Never run or load the Acer files.
Delete the Ghidra project and the extracted tools when done unless the owner said to keep them. Write the encoding into `references/wmi-encodings.md` with its status; open a gate before any run.

**Run an approved write.** Only through `tools/predatorsense-write` (build with `build.ps1`; elevated). Its allow-list is a constant in `Write.cs` with the exact
class, method and input the owner approved (today W4a, and W4b which always runs with W4a as its undo, each write attempt in its own log); the probe stays read-only. A new write = a new gate, then a new allow-list entry, never a command-line value.
The tool checks AC power, battery above 50 %, CPU load, the UInt64 schema type, takes a three-pass baseline (sysinfo 1, 2, 3, 6, 9, 10 and `nvidia-smi`), makes the
one call on a timeout thread, then reads 2, 6, 9, 1, 10 every 2 s for 20 s and logs every step (`--call <key>` is a dry run; `--execute` writes; a logged
`WRITE_ATTEMPT` blocks a second write). Run it with `Start-Process -Verb RunAs -Wait` writing to a file. On a non-zero status, a timeout or an alert: no retry, no other write,
mail the coordinator; the undo is the Acer app's Auto button.

**Propose a write.** Fill in: exact call, value, expected effect, risk, how to undo, how the current state is read first.
Ask with `csm gate open`. One value per approval. The filled-in candidates are in `docs/predatorsense/WRITE-PROPOSALS.md`. Rules that came out of B3:
- Start with the write that **restates the recorded state** (fan all-Auto `0x01410019`); it tests the call format and status at the lowest risk.
- For fan writes the only working check is **fan RPM** (`GetGamingSysInfo` 2, 6, 9). There is no firmware read of fan mode, MaxQ level or colour (the operation mode can be read, B10),
  and the Cool Booster read answered `0xE1`. Say so in the proposal and keep each test to seconds, with an undo that does not need our code
  (the Acer app's Auto button).
- The operation mode has a decoded WMI route since B7 (`SetGamingMiscSetting((mode << 8) | 0x0B)`), proposed in `WRITE-PROPOSALS.md` W3 as W3a to W3d with reads W3-R1/R2 first (both done once in B10; R1 can be read before/after a step, but W3a restates Default so only W3b can show an R1 change). Propose values only from that table; it is code evidence plus two reads, not a write run.
