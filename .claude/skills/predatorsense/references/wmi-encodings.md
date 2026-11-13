# WMI encodings decoded so far

Status words: **decoded** (read from the UI's code), **partial**, **not decoded**, **live** (run and seen). Calls run on the hardware so far
(all reads): `GetGamingSysInfo` indexes 1 to 17 and `AcerGenericMethod.GetFunction(0x207)`. No write ever. All values are for `root\WMI`.
"R" read, "W" write, "?" unproven. Parameter names: `AcerGamingFunction` `gmInput`/`gmOutput`, `AcerGenericMethod` `uiInput`/`uiOutput`; Get methods take UInt32.
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
**Live on this machine (B2, B3), all status 0:**

| Index | Raw | Value | Note |
| --- | --- | --- | --- |
| 1 CPU temp | `0x4900`, `0x4000`, `0x3F00`, `0x3F00` | 73, 64, 63, 63 | degrees C by analogy; ACPI `TZ00` stayed ~52 (different sensor, not the CPU) |
| 2 CPU fan speed | `0xB2900` | 2857 | RPM by magnitude |
| 3 system temp | `0x2C00` | 44 | degrees C |
| 6 / 9 GPU1 / GPU2 fan speed | `0xBF500` / `0xC0B00` | 3061 / 3083 | RPM by magnitude |
| 10 GPU1 temp | `0x3500` | 53 | **equals `nvidia-smi` (53.19, 53.41) at the same moment: degrees C** |
| 4, 5, 7, 8, 11 | `0x0` | 0 | hardware absent here; **status is still 0** |
| 12 to 17 (rates) | `0x0` | 0 | read 0 while fans ran at ~3000: not a usable state read |

Clubman's refresh code reads, per fan, speed index then rate index: CPU 2/12, system 1 4/13, system 2 8/14, GPU 1 6/16, GPU 2 9/17.
Input `2` (battery boost) has **not** been run. Details and the temperature cross-check: `docs/predatorsense/FINDINGS.md` 12.2, 12.3.

## AcerGenericMethod (SetFunction id 1: UInt64 in, UInt32 out; GetFunction id 2: UInt32 in, UInt64 out)

| Call | Input | Meaning | Status |
| --- | --- | --- | --- |
| SetFunction W | `7 \| (state << 16)` | Cool Booster off/on (name from UI) | decoded |
| GetFunction R? | `0x207` | Cool Booster state: UI says on only if low byte is 0 **and** byte 1 is 1; any other answer counts as "off" | decoded; **live: raw `0x2E1`, status byte `0xE1` (undecoded), UI would show "off"; read-back unproven** |
| GetFunction R? | `1 \| (BK_Hotkey_Number << 8) \| 0x80000` (here **`0x88401`**, hotkey 132 from `HKLM\SOFTWARE\OEM\PredatorSense\BK_Hotkey_Number`). **B11: this row said `0x80184` until 2026-11-13; that was wrong (hotkey number and the 1 swapped), do not run it** | keyboard backlight: bits 32..39 brightness percent, bits 40..47 == 30 is the UI's "off" flag (meaning open), bits 16..31 non-zero = on (agent) | decoded, traced read (UI and `PSAdminAgent.exe` agree), **not run** |
| SetFunction W | `2 \| (hotkey << 8) \| 0x80000 \| (percent << 32) \| (30 or 0) << 40` (here `0x88402 \| ...`) | the "Backlight timeout" checkbox of the Settings page (`PerKey=0`) | decoded, **not proposed** (the timeout byte's meaning is open) |

**Class name (B11):** the Acer service and agent call `GetFunction`/`SetFunction` on the class **`APGeAction`** (parameters `uiInput`/`uiOutput`; `GetFunction` input sent as UI4, `SetFunction` input as UI8) and listen to `APGeEvent`; no Acer binary contains the name `AcerGenericMethod`.
The probe and the B3 read used `AcerGenericMethod`. Whether those are two classes or one is open (W5-R0 settles it with a schema read). The method ids in the heading above are B1's, read from `AcerGenericMethod`.
| GetFunction R? | `10` | AC adapter kind (only on machines with two adapters); bits 8.. give 1-AC, 2-AC, PD | partial |

## AcerGamingFunction.SetGamingFanBehavior (id 14, UInt64 in, UInt32 out), W, run for all Auto (B4) and all Max (B5)

`input = lo | (hi << 16)`. Fan slots: 0 CPU, 1 and 2 system, 3 and 4 GPU (only fans that exist). `lo` bit `s` = fan in slot `s`
addressed (CPU `0x01`, sys1 `0x02`, sys2 `0x04`, GPU1 `0x08`, GPU2 `0x10`). `hi` bits `2s..2s+1` = mode of that fan:
**1 auto, 2 max, 3 custom**.

| This machine (mask `0x19`) | Value |
| --- | --- |
| all Auto | `0x01410019` (run once in B4: UInt64 input, status 0, fans unchanged) |
| all Max | `0x02820019` (run once in B5 for 14 s: status 0, fan speeds 2600 to 2700 -> 5555 / 5882 / 5900 RPM within 2 s; undone with the all-Auto value, status 0, back in 2 s) |
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

## AcerGamingFunction.GetGamingLED / SetGamingLED (ids 4 / 2); the UI calls these "LEDBehavior" (pipe 13/14)

**B11 correction:** the UI wrappers `WMISetGamingLEDBehavior` / `WMIGetGamingLEDBehavior` (pipe 13 / 14) become the WMI methods **`SetGamingLED` (2) / `GetGamingLED` (4)** in the service (and in the agent). The WMI methods `SetGamingLEDBehavior` (10), `GetGamingLEDBehavior` (11), `SetGamingLEDColor` (12) and `GetGamingLEDColor` (13) are the "LED group" pipe commands 20 to 23, not used by the Clubman keyboard page.

| Call | Input | Notes |
| --- | --- | --- |
| Get R? | `1` | UI checks status byte 0 and then `(raw >> 8) & 3 == 1` (fan-mode handlers) or `(raw >> 14) & 3 == 1` (one operation-mode branch). Meaning guessed: a boost indicator. |
| Set W | `0x10001`, `0x410001`, `1`, `0x400001` | written by the UI around Turbo + Max fan + AC + battery boost, together with `SetGamingMiscSetting(0x101)` / `(1)`. Meaning guessed. |
| Set W | **`8 \| (Z1 << 40) \| (Z2 << 41) \| (Z3 << 42) \| (Z4 << 43)`**, `Zn` = 1 when keyboard zone n is on | **keyboard zone on/off mask** (low byte 8 = "keyboard zones" selector). Three zones all on: `0x70000000008`; zone 1 only `0x10000000008`; none `0x8`. Old SMBIOS form (type 172 version below 2.86, this machine): UInt64. Decoded in B11, never run. |

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

## Operation mode (decoded in B7 by static analysis, re-checked in B9 with Ghidra: same values; **nothing run**)

UI enum Quiet 0, Default 1 (button/ini name "Normal"), Render 2, Edit 3, Extreme 4, Turbo 5. The UI sends the number as a `UInt32` in **service pipe command 53**
(reply: always `0xFFFFFFFF`, the UI ignores it) and caches it in `Overclock\CurrentOperationMode`. The UI never sends command 51 or 52. The pipe is off limits.
What `PSSvc.exe` does for 53 (this machine; B7's own decoder and B9's Ghidra agree on every value below; neither run anything):

1. if registry `NV_WhisperMode20_Support` = 1: admin-agent pipe command 35 with `1` for Quiet, else `0` (not WMI).
2. **`SetGamingMiscSetting` (id 22) `gmInput` = `(mode << 8) | 0x0B`** (`0x0B`, `0x10B`, `0x20B`, `0x30B`, `0x40B`, `0x50B`), then 30 ms.
3. GPU step with `level` = 0 (Quiet), 1 (Default, Render, Edit), 2 (Extreme), 3 (Turbo): NvAPI P-state offsets from `PredatorSense.ini` (this GPU: all 0 except Turbo core +100, memory +360;
   `SetPstates20`; the offset is absolute, `ini x 1000` kHz, and non-Turbo modes set it back to 0; a 100 ms sleep only after a failed try), then `SetGamingMiscSetting` `(level << 8) | 0x05` = `0x05`, `0x105`, `0x205`, `0x305` (when `Feature.ini [OverclockDetial]` `GPUOCWMIRule` and `GPUOCWMINewRule` are 1, as here).
4. No CPU OC, no power plan, no registry write (one unused repair write), no AC/battery check, and no check of either WMI answer in the service; the UI and `PSAdminAgent` do those (writes of the cache, Default reset on power-source change/low battery/resume).

Reads (service handlers for pipe 52 and 51; the UI never uses them): `GetGamingMiscSetting` (id 23, UInt32 in) `0x0B` = current mode, `0x0A` = capability. The service returns the raw 64-bit answer,
so the layout is unknown (B9: no Acer binary parses it; `PSAdminAgent` never reads `0x0A` or `0x0B`); **Run once each in B10 (read only, owner approved): `0x0B` gave `0x0B0100` (status 0, bits 8 to 15 = 1 = Default) and `0x0A` gave `0x0B3300` (status 0, bits 8 to 15 = 0x33 = Quiet, Default, Extreme, Turbo); layout G, one observation each (FINDINGS 15.9). Since module 0.1.2 these two reads are in the module's allow-list and run by the module (page row, metric `predatorsense.opmode`).** Other Misc selectors seen: `0x05` GPU OC level, `0x07` CPU OC level / CPU OC support read,
`0x09` discrete-GPU capability read, `0x02` GPU mode. UI Turbo branch (AC + boost + fan Max): `SetGamingLEDBehavior(0x10001)` + `SetGamingMiscSetting(0x101)`, undo `(1)` + `(1)`.
Turbo **key** with the UI closed (agent): `SetGamingFanBehavior(0x02AA001F)` all Max / `(0x0155001F)` all Auto, mask `0x1F` (all five slots).

## Keyboard lighting (this machine: WMI route, decoded in B11 by static analysis, **nothing run**)

Source: `docs/predatorsense/FINDINGS.md` section 16 (all tables, the Acer UI's action-by-action order, what is persisted and re-applied); the test plan is `WRITE-PROPOSALS.md` W5. This machine: 3 zones (`KeyBoardArea` = 3), SMBIOS type 172 version 2.83, which selects the **old forms** below
(`>= 2.86` selects a 10-byte backlight array and a 12-byte LED array; not used here).

| Call | Class.method (id) | Input | Notes |
| --- | --- | --- | --- |
| zone colour W | `AcerGamingFunction.SetGamingRgbKb` (6), UInt32 in | `zone \| (R << 8) \| (G << 16) \| (B << 24)`; `zone` = 1, 2, 4 for zone 1 to 3 (8 for a fourth); one bit per call | `R`, `G`, `B` = `floor(channel * ZoneColorAdjust)`, factors 1.0 on this machine (`HW_Support.ini`). Examples: zone 1 Red `0x0000FF01`; zone 1 `#00AEC7` `0xC7AE0001`. The service sleeps 30 ms after the call |
| brightness W | `AcerGamingFunction.SetGamingKBBacklight` (20), `byte[16]` in | `00 00 pct 00 00 00 00 00` + 8 tail bytes; `pct` = 0, 25, 50, 75, 100 (Acer slider level 1 to 5 = `(level - 1) * 25`) | all-zero = "close" (static, 0 %). The service always forwards 16 bytes of the request; Acer's UI sends 8, so the tail is uninitialised heap in Acer's own call. Send zeros (G) |
| effect W | same method, `byte[16]` | byte 0 mode: 1 Breathing, 2 Neon, 3 Wave, 4 Shifting, 5 Zoom, 6 Meteor, 7 Twinkling (0 = static); byte 1 speed 1..9; byte 2 `pct`; byte 3 = `8` for Wave only; byte 4 direction 1 or 2 (Wave, Shifting; which is left is G); bytes 5..7 R, G, B (Breathing, Shifting, Zoom, Meteor, Twinkling) | Twinkling is sent as a 9-byte array with byte 8 = 3 (old form). Neon and Wave carry no colour. The duration slider is stored by Acer and never sent |
| zone mask W | `AcerGamingFunction.SetGamingLED` (2) | `8 \| (zone mask << 40)` | see the LED section above |
| backlight read R | `APGeAction.GetFunction` | `0x88401` | see the `AcerGenericMethod` table above (class name!) |

Acer's order for a whole profile: all-zero `KB`, zone mask, `KB` static brightness, one `RgbKb` per enabled zone; for an effect: all-zero `KB`, then the one effect `KB`. A brightness slider move re-sends `KB` and every enabled zone's colour.
**No read of colour, zone mask, effect or the `KB` bytes exists** in any Acer binary (`GetGamingRgbKb` (7), `GetGamingKBBacklight` (21), `GetGamingLEDColor` (13), `GetGamingLEDBehavior` (11) are never sent), so those reply layouts are unknown.
Profiles live in `C:\ProgramData\oem\PredatorSense\ProfilePool\LightProfilePool\<name>\Main.xml` (Acer's record, not live state; read-only for us). After a resume or at agent start `PSAdminAgent.exe` re-applies the active Acer profile.

## Not decoded

- `GetGamingProfile` (3) / `SetGamingProfile` (1) / `GetGamingProfileSetting` (9) / `SetGamingProfileSetting` (8): the UI calls `GetGamingProfileConfiguration`
  only for LCD overdrive and the current group index; inputs are built from more state.
- `GetCPUOverclockingProfile` / `SetCPUOverclockingProfile`: byte structs; CPU OC is not supported on this machine.
- Fan table payload. (Lighting write payloads: decoded in B11, see above.) (The service's operation-mode mapping was decoded in B7, see above; the firmware semantics of selectors `0x0A`/`0x0B` are still unknown.)
