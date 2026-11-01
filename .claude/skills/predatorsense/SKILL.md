---
name: predatorsense
description: Working knowledge of how Acer PredatorSense V3 controls an Acer Predator laptop (Triton 500 SE, PT516-51s) - service PSSvc, its named pipe, and the root\WMI AcerGamingFunction methods - and what is verified, not verified and forbidden. Use it before writing or changing anything for a Rigbay PredatorSense module, before calling any Acer WMI method, when adding a fan, performance-mode, lighting or sensor feature, or when writing the module's probe ("is this machine supported"). DRAFT from the B1 and B2 reading tasks; one firmware read (CPU temperature) has been run, no hardware write ever.
---

# PredatorSense: working knowledge (draft)

Source of truth for the evidence is `docs/predatorsense/FINDINGS.md` (tables, evidence tags C/O/G). This file is the short
version for people writing code. The first approval (CPU temperature, done) is `docs/predatorsense/GATE-getgamingsysinfo.md`;
the next candidate reads are in `docs/predatorsense/GATE-b2-reads.md`.

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
  10 GPU1 temp, 11 GPU2 temp (full list in `references/wmi-encodings.md`). Index 1 was read live: `0x0101` gave raw `0x4900`,
  status 0, value 73 (degrees C, consistent, unproven). Fan units are still a guess.
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
| Service, pipe names, WMI classes and method ids, registry and ini layout, command id lists | Any live firmware value except the CPU temperature below |
| **Live:** `GetGamingSysInfo(0x0101)` returns status 0, value 73 (once, elevated, owner-approved) | Fan speed/rate units; any other sensor index |
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

**Read one sensor (needs an approved gate).** Elevated: `predatorsense-probe.exe --allow-call --sysinfo-index 1`. Only index values
the owner approved (so far only 1). Start it with `Start-Process -Verb RunAs` writing to a file under `bin\` (ignored by git),
then read the file. The owner's UAC auto-elevates, no prompt. Paste the output into FINDINGS.md.

**Is a call safe to run?** Only if its exact input appears in the UI's code as a read and the owner approved that one call. "The UI
has the wrapper" is not enough: `WMIGetGamingFanGroupBehavior/Speed/Table` exist but nothing calls them.

**Decode a new call.** Find the UI helper (`CommonFunction`, `FanControlPage`, `OperationMode_Button`, `MonitorAndOCClass`,
`WMIFunction` in `PredatorSense.exe`) with REA `inspect-managed-members` or a metadata-only IL lister (parse metadata and IL
bytes with `System.Reflection.Metadata`; never load or run Acer code). Find who calls the `WMIFunction` wrapper first: no caller
means no traced input. The model-to-window mapping is `App.OnStartup` on `Feature.ini [MachineType] Type`. Write the encoding into
`references/wmi-encodings.md` with its status; open a gate before any run.

**Propose a write.** Fill in: exact call, value, expected effect, risk, how to undo, how the current state is read first.
Ask with `csm gate open`. One value per approval.
