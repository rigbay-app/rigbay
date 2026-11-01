# WMI encodings decoded so far

Status words: **decoded** (read from the UI's code), **partial**, **not decoded**. None has been run on the hardware.
All values are for `root\WMI`. "R" read, "W" write, "?" unproven.

## AcerGamingFunction.GetGamingSysInfo (id 5, UInt32 in, UInt64 out), R?

| Input | Meaning | Output |
| --- | --- | --- |
| `1 \| (index << 8)` | health sensor `index` | status = bits 0..7 (0 ok); value = bits 8..23 |
| `2` | "battery boost" on/off | status = bits 0..7; flag = bits 40..47 (1 = on) |

Sensor index: 1 CPU temp, 2 CPU fan speed, 3 system temp, 4 system fan speed, 5 system fan speed external, 6 GPU1 fan speed,
7 system2 temp, 8 system2 fan speed, 9 GPU2 fan speed, 10 GPU1 temp, 11 GPU2 temp, 12 CPU fan rate, 13 system fan rate,
14 system2 fan rate, 15 system external fan rate, 16 GPU1 fan rate, 17 GPU2 fan rate. Units: guess (C, RPM, percent).
Status: decoded, **gate pending** (`docs/predatorsense/GATE-getgamingsysinfo.md`).

## AcerGenericMethod (SetFunction id 1: UInt64 in, UInt32 out; GetFunction id 2: UInt32 in, UInt64 out)

| Call | Input | Meaning | Status |
| --- | --- | --- | --- |
| SetFunction W | `7 \| (state << 16)` | Cool Booster off/on (name from UI) | decoded |
| GetFunction R? | `0x207` | Cool Booster state: byte 1 of the answer == 1 means on | decoded |
| GetFunction R? | `10` | AC adapter kind (only on machines with two adapters); bits 8.. give 1-AC, 2-AC, PD | partial |

## AcerGamingFunction.SetGamingMiscSetting (id 22, UInt64 in, UInt32 out), W

| Input | Meaning |
| --- | --- |
| `0x07`, `0x107`, `0x207` | CPU overclock level 0, 1, 2 |
| `0x05`, `0x105`, `0x205` | GPU overclock level 0, 1, 2 |
| `0x1005`, `0x1105`, `0x1205` | MaxQ mode 1, level 0, 1, 2 |
| `0x2005`, `0x2105`, `0x2205` | MaxQ mode 2, level 0, 1, 2 |
| `2 \| (mode << 8)` | GPU mode switch (hybrid/discrete); `mode` from the capability read |

Success check in the UI: low byte of the answer is 0. Meaning of "level" names (Normal/Faster/Turbo, Normal/Fast/Boosted/Extreme depending on model): UI enums.
Status: decoded, effect not observed. On this machine the registry says GPU OC is supported and CPU OC is not.

## AcerGamingFunction.GetGamingMiscSetting (id 23, UInt32 in, UInt64 out), R?

| Input | Meaning |
| --- | --- |
| `2` | GPU mode support: mode in bits 8..15; values 1, 2, 3 accepted |
| `9` | GPU mode capability bits: bits 8, 9, 10 (after an SMBIOS version check) |

## Not decoded

- `SetGamingFanBehavior` (14) / `GetGamingFanBehavior` (15): 64-bit packed fields, one per fan (this machine: 1 CPU, 2 GPU). Auto/Max/Custom
  select different bit patterns; shape seen, values not recorded.
- `SetGamingFanSpeed` (16), `GetGamingFanSpeed` (17), `SetGamingFanTable` (18), `GetGamingFanTable` (19).
- Operation mode (Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5 in the UI enum): sent to the Acer service as pipe command 53,
  service-to-WMI mapping unknown (needs the native service read).
- LED / RGB keyboard: `SetGamingLED`, `SetGamingRgbKb`, `SetGamingLEDBehavior`, `SetGamingLEDColor`, `SetGamingKBBacklight`; route (WMI or USB) for this model unknown.
- `GetGamingProfile`, `SetGamingProfile`, `GetGamingProfileSetting`, `SetGamingProfileSetting`.
