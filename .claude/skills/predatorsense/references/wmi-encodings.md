# WMI encodings decoded so far

Status words: **decoded** (read from the UI's code), **partial**, **not decoded**, **live** (run and seen). Only one call has ever
been run on the hardware: `GetGamingSysInfo(0x0101)`. All values are for `root\WMI`. "R" read, "W" write, "?" unproven.
This machine: 1 CPU fan, 0 system fans, 2 GPU fans; UI variant Clubman (`Feature.ini [MachineType] Type=8`).

Rule that came out of B2: **a UI wrapper with no caller is not a traced read.** `WMIGetGamingFanGroupBehavior/Speed/Table`,
`WMIGetGamingLEDGroupBehavior/Color`, `WMIGetGamingRgbKbSetting`, `WMIGetGamingKBBacklight`, `WMIGetGamingProfileSetting` and the
pipe commands `GetOperationMode(Capability)` (51, 52) are never called by the UI. Do not call the matching WMI methods.

## AcerGamingFunction.GetGamingSysInfo (id 5, UInt32 in, UInt64 out), R? , index 1 **live**

| Input | Meaning | Output |
| --- | --- | --- |
| `1 \| (index << 8)` | health sensor `index` | status = bits 0..7 (0 ok); value = bits 8..23 |
| `2` | "battery boost" on/off | status = bits 0..7; flag = bits 40..47 (1 = on) |

Sensor index: 1 CPU temp, 2 CPU fan speed, 3 system temp, 4 system fan speed, 5 system fan speed external, 6 GPU1 fan speed,
7 system2 temp, 8 system2 fan speed, 9 GPU2 fan speed, 10 GPU1 temp, 11 GPU2 temp, 12 CPU fan rate, 13 system fan rate,
14 system2 fan rate, 15 system external fan rate, 16 GPU1 fan rate, 17 GPU2 fan rate.
Live: `0x0101` -> raw `0x4900`, status 0, value 73 (degrees C, consistent with load, one sample, not proven). Other indexes: units
still guessed (C, RPM, percent). Clubman's refresh code reads, per fan, speed index then rate index: CPU 2/12, system 1 4/13,
system 2 8/14, GPU 1 6/16, GPU 2 9/17. Approved so far: only `0x0101`. Next candidates: `docs/predatorsense/GATE-b2-reads.md`.

## AcerGenericMethod (SetFunction id 1: UInt64 in, UInt32 out; GetFunction id 2: UInt32 in, UInt64 out)

| Call | Input | Meaning | Status |
| --- | --- | --- | --- |
| SetFunction W | `7 \| (state << 16)` | Cool Booster off/on (name from UI) | decoded |
| GetFunction R? | `0x207` | Cool Booster state: status low byte, then byte 1 of the answer == 1 means on | decoded, traced read |
| GetFunction R? | `BK_Hotkey_Number \| 0x100 \| 0x80000` (here `0x80184`, hotkey 132 from the registry) | keyboard backlight: bits 32..39 brightness percent, bits 40..47 == 30 means off | decoded, traced read |
| GetFunction R? | `10` | AC adapter kind (only on machines with two adapters); bits 8.. give 1-AC, 2-AC, PD | partial |

## AcerGamingFunction.SetGamingFanBehavior (id 14, UInt64 in, UInt32 out), W, never run

`input = lo | (hi << 16)`. Fan slots: 0 CPU, 1 and 2 system, 3 and 4 GPU (only fans that exist). `lo` bit `s` = fan in slot `s`
addressed (CPU `0x01`, sys1 `0x02`, sys2 `0x04`, GPU1 `0x08`, GPU2 `0x10`). `hi` bits `2s..2s+1` = mode of that fan:
**1 auto, 2 max, 3 custom**.

| This machine (mask `0x19`) | Value |
| --- | --- |
| all Auto | `0x01410019` |
| all Max | `0x02820019` |
| CPU custom / CPU auto (single fan) | `0x30001` / `0x10001` |
| GPU1 custom / auto | `0xC00008` / `0x400008` |
| GPU2 custom / auto | `0x3000010` / `0x1000010` |

Read-back: `GetGamingFanBehavior` (id 15, UInt32 in): **no UI caller, input untraceable, do not call.**

## AcerGamingFunction.SetGamingFanSpeed (id 16, UInt64 in, UInt32 out), W, never run; partial

`input = fanId | (percent << 8)`, fanId 1 CPU, 2 and 3 system, 4 and 5 GPU, percent 0..100 (UI scrollbar; lower bound not checked).
Used between per-fan custom/auto behaviour writes; exact sequence conditions not traced. `GetGamingFanSpeed` (id 17): no UI caller.

## Fan table (ids 18 and 19)

`SetGamingFanTable` is only used by the "Silent" mode of `FanControlPage` (sends `0`); Clubman has no Silent mode, so this model's UI
does not use the table. Payload **not decoded**. `GetGamingFanTable` (no input): no UI caller.

## AcerGamingFunction.GetGamingLEDBehavior / SetGamingLEDBehavior (ids 11 / 10)

| Call | Input | Notes |
| --- | --- | --- |
| Get R? | `1` | UI checks status byte 0 and then `(raw >> 8) & 3 == 1` (fan-mode handlers) or `(raw >> 14) & 3 == 1` (one operation-mode branch). Meaning guessed: a boost indicator. |
| Set W | `0x10001`, `0x410001`, `1`, `0x400001` | written by the UI around Turbo + Max fan + AC + battery boost, together with `SetGamingMiscSetting(0x101)` / `(1)`. Meaning guessed. |

## AcerGamingFunction.SetGamingMiscSetting (id 22, UInt64 in, UInt32 out), W

| Input | Meaning |
| --- | --- |
| `0x07`, `0x107`, `0x207` | CPU overclock level 0, 1, 2 |
| `0x05`, `0x105`, `0x205` | GPU overclock level 0, 1, 2 |
| `0x1005`, `0x1105`, `0x1205` | MaxQ mode 1, level 0, 1, 2 |
| `0x2005`, `0x2105`, `0x2205` | MaxQ mode 2, level 0, 1, 2 |
| `2 \| (mode << 8)` | GPU mode switch (hybrid/discrete); `mode` from the capability read |
| `0x01`, `0x101` | written by the UI with the LED behaviour pair above (battery-boost related; meaning guessed) |

Success check in the UI: low byte of the answer is 0. Meaning of "level" names (Normal/Faster/Turbo, Normal/Fast/Boosted/Extreme depending on model): UI enums.
Status: decoded, effect not observed. On this machine the registry says GPU OC is supported and CPU OC is not.

## AcerGamingFunction.GetGamingMiscSetting (id 23, UInt32 in, UInt64 out), R?

| Input | Meaning |
| --- | --- |
| `2` | GPU mode support: mode in bits 8..15; values 1, 2, 3 accepted |
| `9` | GPU mode capability bits: bits 8, 9, 10 (after an SMBIOS version check) |

## Operation mode

UI enum Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5. The UI sends the number as a `UInt32` in **service pipe command 53**
(reply: 32-bit status at offset 5 of a 9-byte frame) and caches it in `Overclock\CurrentOperationMode`. It never sends
command 51 or 52. The service-to-WMI mapping is **not decoded** (needs `PSSvc.exe` disassembly; the capability test uses an SMBIOS
type 172 table). **No direct WMI route is known**, and the pipe is off limits. Do not guess one.

## Lighting (this machine: WMI route)

The Clubman lighting page calls `SetGamingRgbKb` (6), `SetGamingKBBacklight` (20), `SetGamingLEDBehavior` (10) and
`AcerGenericMethod.SetFunction`; no `Get*` of colour or effect. Profiles are kept by the UI in XML under `ProfilePool`.
Payload encodings of the lighting writes: **not decoded** (not needed until a lighting write is proposed).

## Not decoded

- `GetGamingProfile` (3) / `SetGamingProfile` (1) / `GetGamingProfileSetting` (9) / `SetGamingProfileSetting` (8): the UI calls `GetGamingProfileConfiguration`
  only for LCD overdrive and the current group index; inputs are built from more state.
- `GetCPUOverclockingProfile` / `SetCPUOverclockingProfile`: byte structs; CPU OC is not supported on this machine.
- Fan table payload, lighting write payloads, the service's operation-mode mapping.
