---
name: predatorsense
description: Working knowledge of how Acer PredatorSense V3 controls an Acer Predator laptop (Triton 500 SE, PT516-51s) - service PSSvc, its named pipe, and the root\WMI AcerGamingFunction methods - and what is verified, not verified and forbidden. Use it before writing or changing anything for a Rigbay PredatorSense module, before calling any Acer WMI method, when adding a fan, performance-mode, lighting or sensor feature, or when writing the module's probe ("is this machine supported"). The Rigbay module itself is `modules/predatorsense/` (task B6, rules below). DRAFT from the B1 to B6 tasks; firmware reads (sensors, one Cool Booster read) and two fan-mode writes so far (all Auto, B4; all Max for 14 s then Auto again, B5; both status 0).
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
  only. Any new call (Cool Booster, MaxQ, fan speed, lighting, battery) needs a decoded encoding, an owner gate, a table entry and
  tests that show every other value is still refused. Never widen the table to "make it work".
- **Never run a write from a test, a script or the app against the real machine as a check.** The fake (`FakeAcerWmi` in the tests)
  covers the write path. The tests call the real `AcerWmi` only with values that must be *refused* (before any WMI object exists).
- **No firmware read of the fan mode exists.** The page says "Last set by Rigbay", never "current mode", and the Fan mode box says "Not set" (an option that is not a value, writes nothing, goes away after the first set) until Rigbay has set one in this run (0.1.1, E2E-10). The RPM check after a write
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
- **Do not use the service pipe `\\.\pipe\predatorsense_service_namedpipe`.** `PSSvc` checks the caller is the genuine
  PredatorSense app (package family or exe path under `WindowsApps\AcerIncorporated.PredatorSenseV30_*`). Talking to WMI
  directly from the elevated host is the route. Do not impersonate the Acer app.
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
  `SetGamingKBBacklight`, `SetGamingLEDBehavior`, `AcerGenericMethod.SetFunction`), not the USB admin-agent route.
- **The UI never reads fan behaviour, fan speed, fan table, operation mode or lighting colour back from firmware.** It uses the
  registry cache plus `GetGamingSysInfo`. So those `Get*` inputs are untraceable: do not call them. The traceable reads are
  in `references/wmi-encodings.md` (sysinfo, Cool Booster `GetFunction(0x207)`, keyboard backlight `GetFunction(0x80184)`).
- Fan write encodings are decoded (`SetGamingFanBehavior`: fan mask low half, 2-bit mode per fan in the high half; all-Auto here
  is `0x01410019`); see the encodings file. **`SetGamingFanBehavior(0x01410019)` was run once in B4: `gmInput` is UInt64, answer raw `0x0`
  (status 0) in 11 ms, fans and temperatures unchanged for 20 s.** **`SetGamingFanBehavior(0x02820019)` (all Max) was run once in B5 for 14 s: status 0 in 13 ms, fan speeds
  (sysinfo 2, 6, 9) went from about 2600 to 2700 to 5555 / 5882 / 5900 within 2 s and stayed there; the undo `0x01410019` (status 0) brought them back to the Auto range within 2 s. So a
  fan write has a measurable effect and the mask/mode table is right for modes 1 and 2. The first read ~16 ms after the write still shows the old RPM: wait about 2 s.** Custom values have never been run. The operation mode has no known direct WMI route.
- **Set methods take UInt64 `gmInput`; the Get methods take UInt32** (the probe casts to `uint`). The Set answer is `gmOutput` UInt32, status = low byte, 0 = ok.
- **Fan RPM in Auto jitters by one step (about 22 to 24 RPM) and drifts with load**; use jumps of hundreds of RPM as the signal. `nvidia-smi` fan speed is `[N/A]` here, so sysinfo 2, 6, 9 are the only fan reading.

## Verified / not verified

| Verified (read or observed) | Not verified |
| --- | --- |
| Service, pipe names, WMI classes and method ids, registry and ini layout, command id lists | Any live firmware value other than the reads in the next two rows |
| **Live:** `GetGamingSysInfo` indexes 1 to 17 all return status 0 (values above; elevated, owner-approved, read-only). GPU1 temp equals `nvidia-smi` | Fan speed unit (RPM by magnitude only); CPU `0x0101` against an independent CPU sensor; what the rate indexes mean |
| **Live:** `GetFunction(0x207)` returned status `0xE1` (undecoded) | `GetFunction(0x80184)`, battery boost, `GetGamingMiscSetting(2/9)`, `GetGamingLEDBehavior(1)`: never run |
| Encodings of CoolBooster, CPU/GPU OC level, MaxQ, GPU mode, sensor index, battery boost, AC mode, fan behaviour and fan speed writes (from UI code) | What those writes do on this machine (never run), except the one below |
| **Live write (B4):** `SetGamingFanBehavior(0x01410019)` once, elevated: status 0, 11 ms, RPM 2564 to 2586 / 2654 to 2678 / 2663 and temperatures unchanged for 20 s | That the fan mode changed or was re-asserted (no read-back); whether `PSSvc` re-applies its mode; survival across sleep or reboot; Custom, Cool Booster, MaxQ |
| **Live write (B5):** `SetGamingFanBehavior(0x02820019)` (Max) then `(0x01410019)` (Auto), both status 0 in 13 ms; RPM 2600 to 2700 -> 5555 / 5882 / 5900 within 2 s, flat for 14 s, back in the Auto range 2 s after the undo | The mode itself (inferred from RPM only); ramp time under 2 s; RPM unit; cooling effect; what the owner heard; behaviour under load or across sleep/reboot |
| UI variant Clubman, lighting route WMI, the UI has no read of fan/mode/colour | Operation-mode mapping in the service; fan table payload; lighting effect tables |
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
with one flag per approved call (`--sysinfo-index N`, `--r1`, `--r2`, `--r3`, `--battery-boost`, `--misc-get 2|9`, `--led-behavior-get`). The probe has an allowlist,
refuses `Set*`, repeats and unknown inputs, and halts at the first non-zero status. Start it with `Start-Process -Verb RunAs` writing to a file under `bin\`
(ignored by git; the file is UTF-16 when written by PowerShell redirection, so use `Get-Content`/`Select-String`, not `grep`), then read the file. The owner's UAC
auto-elevates, no prompt. Paste the output into FINDINGS.md. When a call returns a surprise, **stop and mail the coordinator**; do not run the next one.

**Is a call safe to run?** Only if its exact input appears in the UI's code as a read and the owner approved that one call. "The UI
has the wrapper" is not enough: `WMIGetGamingFanGroupBehavior/Speed/Table` exist but nothing calls them.

**Decode a new call.** Find the UI helper (`CommonFunction`, `FanControlPage`, `OperationMode_Button`, `MonitorAndOCClass`,
`WMIFunction` in `PredatorSense.exe`) with REA `inspect-managed-members` or a metadata-only IL lister (parse metadata and IL
bytes with `System.Reflection.Metadata`; never load or run Acer code). Find who calls the `WMIFunction` wrapper first: no caller
means no traced input. The model-to-window mapping is `App.OnStartup` on `Feature.ini [MachineType] Type`. Write the encoding into
`references/wmi-encodings.md` with its status; open a gate before any run.

**Run an approved write.** Only through `tools/predatorsense-write` (build with `build.ps1`; elevated). Its allow-list is a constant in `Write.cs` with the exact
class, method and input the owner approved (today W4a, and W4b which always runs with W4a as its undo, each write attempt in its own log); the probe stays read-only. A new write = a new gate, then a new allow-list entry, never a command-line value.
The tool checks AC power, battery above 50 %, CPU load, the UInt64 schema type, takes a three-pass baseline (sysinfo 1, 2, 3, 6, 9, 10 and `nvidia-smi`), makes the
one call on a timeout thread, then reads 2, 6, 9, 1, 10 every 2 s for 20 s and logs every step (`--call <key>` is a dry run; `--execute` writes; a logged
`WRITE_ATTEMPT` blocks a second write). Run it with `Start-Process -Verb RunAs -Wait` writing to a file. On a non-zero status, a timeout or an alert: no retry, no other write,
mail the coordinator; the undo is the Acer app's Auto button.

**Propose a write.** Fill in: exact call, value, expected effect, risk, how to undo, how the current state is read first.
Ask with `csm gate open`. One value per approval. The filled-in candidates are in `docs/predatorsense/WRITE-PROPOSALS.md`. Rules that came out of B3:
- Start with the write that **restates the recorded state** (fan all-Auto `0x01410019`); it tests the call format and status at the lowest risk.
- For fan writes the only working check is **fan RPM** (`GetGamingSysInfo` 2, 6, 9). There is no firmware read of fan mode, operation mode, MaxQ level or colour,
  and the Cool Booster read answered `0xE1`. Say so in the proposal and keep each test to seconds, with an undo that does not need our code
  (the Acer app's Auto button).
- The operation mode has no known direct WMI route (pipe only); do not propose a value.
