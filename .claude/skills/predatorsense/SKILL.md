---
name: predatorsense
description: Working knowledge of how Acer PredatorSense V3 controls an Acer Predator laptop (Triton 500 SE, PT516-51s) - service PSSvc, its named pipe, and the root\WMI AcerGamingFunction methods - and what is verified, not verified and forbidden. Use it before writing or changing anything for a Rigbay PredatorSense module, before calling any Acer WMI method, when adding a fan, performance-mode, lighting or sensor feature, or when writing the module's probe ("is this machine supported"). DRAFT from the B1, B2 and B3 tasks; firmware READS only so far (sensors, one Cool Booster read), no hardware write ever.
---

# PredatorSense: working knowledge (draft)

Source of truth for the evidence is `docs/predatorsense/FINDINGS.md` (tables, evidence tags C/O/G). This file is the short
version for people writing code. The first approval (CPU temperature, done) is `docs/predatorsense/GATE-getgamingsysinfo.md`;
the candidate reads are in `docs/predatorsense/GATE-b2-reads.md`; B3's live results are `FINDINGS.md` section 12; the candidate
writes (none run) are in `docs/predatorsense/WRITE-PROPOSALS.md`.

Detail files (read only when needed):
- `references/wmi-encodings.md`: every input encoding decoded so far, with status.
- `references/probe-and-recipes.md`: running the probe, reading the cache, a "supported?" check, how to add a decoded call.

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
  is `0x01410019`) but have never been run; see the encodings file. The operation mode has no known direct WMI route.

## Verified / not verified

| Verified (read or observed) | Not verified |
| --- | --- |
| Service, pipe names, WMI classes and method ids, registry and ini layout, command id lists | Any live firmware value other than the reads in the next two rows |
| **Live:** `GetGamingSysInfo` indexes 1 to 17 all return status 0 (values above; elevated, owner-approved, read-only). GPU1 temp equals `nvidia-smi` | Fan speed unit (RPM by magnitude only); CPU `0x0101` against an independent CPU sensor; what the rate indexes mean |
| **Live:** `GetFunction(0x207)` returned status `0xE1` (undecoded) | `GetFunction(0x80184)`, battery boost, `GetGamingMiscSetting(2/9)`, `GetGamingLEDBehavior(1)`: never run |
| Encodings of CoolBooster, CPU/GPU OC level, MaxQ, GPU mode, sensor index, battery boost, AC mode, fan behaviour and fan speed writes (from UI code) | What those writes do on this machine (never run) |
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

**Propose a write.** Fill in: exact call, value, expected effect, risk, how to undo, how the current state is read first.
Ask with `csm gate open`. One value per approval. The filled-in candidates are in `docs/predatorsense/WRITE-PROPOSALS.md`. Rules that came out of B3:
- Start with the write that **restates the recorded state** (fan all-Auto `0x01410019`); it tests the call format and status at the lowest risk.
- For fan writes the only working check is **fan RPM** (`GetGamingSysInfo` 2, 6, 9). There is no firmware read of fan mode, operation mode, MaxQ level or colour,
  and the Cool Booster read answered `0xE1`. Say so in the proposal and keep each test to seconds, with an undo that does not need our code
  (the Acer app's Auto button).
- The operation mode has no known direct WMI route (pipe only); do not propose a value.
