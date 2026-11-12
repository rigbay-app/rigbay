# PredatorSense interface findings (task B1)

Machine: Acer Predator PT516-51s (Triton 500 SE), board `Countryman_TLS`, BIOS Insyde V1.08, Windows 11.
Software read: Store package `AcerIncorporated.PredatorSenseV30` 3.0.3198.0 and the `PSSvc` service folder
`C:\Program Files\Acer\PredatorSense Service`. Everything was read in place. Nothing from Acer is copied into this
repo; names, ids and numbers below are facts, the wording is ours. Nothing in this study wrote to the hardware.
Sections 1 to 10 are the B1 reading (no WMI method called then). **Task B2 (section 11) made exactly one WMI method
call: `GetGamingSysInfo(0x0101)`, the owner-approved CPU temperature read.** **Task B3 (section 12) ran the owner-approved
follow-up reads: sysinfo indexes 2 to 17 and `AcerGenericMethod.GetFunction(0x207)`; it still wrote nothing.** **Task B4
(section 13) ran the first hardware write: `SetGamingFanBehavior(0x01410019)`, all fans Auto, status 0; B5 (section 14) added all Max for 14 s and the Auto undo.**
**Task B7 (section 15) is static only (nothing was called) and decodes the operation mode in `PSSvc.exe`.** **Task B9 (section 15.8) re-read the critical parts of it with Ghidra, also static only: it confirms B7's values and fixes four details (marked B9 in section 15).** Where
section 11, 12, 13, 14 or 15 corrects an earlier statement, the later section wins (the "nothing wrote" sentences above describe B1 to B3).

## How sure each statement is

| Tag | Meaning |
| --- | --- |
| **C** | confirmed by reading code or metadata of an installed file |
| **O** | observed on the running system (service state, pipe list, WMI schema, registry, ini files) |
| **G** | guess; not backed by anything I saw |

Tools: REA (`inspect-managed-members`) for the .NET assemblies; a small scratch tool of mine that lists IL and
reads enum constants with a metadata-only load (no Acer code is executed); plain string extraction for the native
`PSSvc.exe`. The native code itself was **not disassembled**, so claims about what the service does with a value
come from its log text and imports (tagged C only for those facts).

## 1. How the pieces fit

```
PredatorSense.exe (WPF, .NET, Store/"Centennial" package, full trust)
      |  named pipe  \\.\pipe\predatorsense_service_namedpipe      (C)(O)
      v
PSSvc.exe  (native, service "Predator Service", LocalSystem, start type Manual)   (O)
      |-- WMI, namespace root\WMI, class AcerGamingFunction / AcerGenericMethod   (C)(O)
      |-- NvAPI (GPU clocks, overclock P-states), Intel OC SDK -> "XTU3SERVICE"    (C: log text and imports)
      |-- admin agent pipe  \\.\pipe\predatorsense_admin_agent_<session id>        (C)(O: ..._1 exists)
      v
Acer WMI-ACPI driver (WmiAcpi, in the OS) -> BIOS/EC firmware                      (O: class provider = WmiProv)
```

- The UI (`PredatorSense.exe`, ~95 MB because of embedded assets) is a managed WPF app. **C**
- `PSSvc.exe` is native C++. Its `TsDotNetLib.dll` (managed, Acer) only holds the shared definitions: command ids,
  pipe names, registry paths. **C**
- Other native helpers in the service folder: `PSAgent.exe` (runs in the user session: OSD, toasts), `PSAdminAgent.exe`
  (user session, elevated: USB keyboard lighting, sound modes, sticky keys, Whisper mode), `PSLauncher.exe`,
  `PSMobile.exe`. **C** (command lists) / **O** (`PSAgent` and `PSAdminAgent` are running, session 1).
- A scheduled task `PredatorSense` and `PredatorSense UI` exist (state Ready). **O** What they launch was not read.

## 2. Q1: endpoints

| Endpoint | Value | Evidence |
| --- | --- | --- |
| Service name | `PSSvc`, display name "Predator Service", account LocalSystem, demand start | O (`sc qc`) |
| Service pipe | `\\.\pipe\predatorsense_service_namedpipe` | C (constant in `TsDotNetLib`), O (pipe is listed) |
| Admin agent pipe | `\\.\pipe\predatorsense_admin_agent_<session id>` | C, O (`..._1`) |
| WMI | namespace `root\WMI`; classes `AcerGamingFunction` (GUID `{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}`), `AcerGenericMethod` (`{1EA0BC57-521E-461B-A7A9-5D36F141A82A}`), `AcerGenericEvent`, `AcerBiosConfigurationTool` | O (class schema read) |
| Registry | `HKLM\SOFTWARE\OEM\PredatorSense` (+ `FanControl`, `Overclock`, `LightSetting`, `AdvanceSettings`, `KeyAssignment`, `GameSync`, `Mobile`, `UEIP`, `ColorRecord`) | C (constants), O (read) |
| Files | `C:\ProgramData\oem\PredatorSense\` : `HW_Support.ini`, `Feature.ini`, `PredatorSense.ini`, `ProfilePool\`, `BIOSLogo\` | O |
| COM, driver IOCTL | none seen. No Acer kernel driver is installed on this machine (the only Acer driver is the airplane-mode controller). | O |

### Pipe frame (C)

Request, client to service: 2-byte command id, then 1 byte argument count, then for each argument a 4-byte length
followed by the bytes. Reply: 1 byte argument count, then for each a 4-byte length and bytes. Example from the UI
code: a "get" with a UInt32 argument reads back 13 bytes (`1 + 4 + 8`, a UInt64 result); a "set" with a UInt64
argument reads back 9 bytes (`1 + 4 + 4`, a UInt32 result). Command ids are in section 3.

## 3. Q2: calls

Read/write column: **R** read, **W** writes machine state, **W?** I could not tell, treat as W. "How decided"
is by the name (Get/Set) and by what the UI does with the call. Names are not proof; see section 6.

### 3.1 Service pipe commands (what the UI sends to `PSSvc`)

Ids from `TsDotNetLib.ServiceCommand` (C). `Reg*`, `CreateProcess*` and the BIOS-logo commands are service chores.

| Id | Command | R/W | Notes |
| --- | --- | --- | --- |
| 9 / 10 | `WMIGetFunction` / `WMISetFunction` | R / W | relay to `AcerGenericMethod.GetFunction` / `SetFunction` |
| 11 / 12 | Set/Get GamingProfileConfiguration | W / R | relay to `SetGamingProfile` / `GetGamingProfile` |
| 13 / 14 | Set/Get GamingLEDBehavior | W / R | `SetGamingLED` / `GetGamingLED` |
| 15 | GetGamingSystemInformation | R? | `GetGamingSysInfo`. The sensor read. See `GATE-getgamingsysinfo.md` |
| 16 / 17 | Set/Get GamingRgbKbSetting | W / R | `SetGamingRgbKb` / `GetGamingRgbKb` |
| 18 / 19 | Set/Get GamingProfileSetting | W / R | |
| 20 to 23 | LED group behavior / colour, set and get | W / R | |
| 24 to 27 | Fan group behavior / speed, set and get | W / R | `SetGamingFanBehavior`, `SetGamingFanSpeed`, ... |
| 28 / 29 | Set/Get GamingFanTable | W / R | the fan curve table |
| 30 / 31 | Set/Get GamingKBBacklight | W / R | |
| 32 / 33 | Set/Get GamingMISCSetting | W / R | used for CPU/GPU overclock level, MaxQ, GPU mode (below) |
| 34 / 35 | Set/Get BIOS animation | W / R | |
| 36 to 38 | CPU frequency, max frequency, voltage | R | via Intel SDK, service side |
| 39 / 40 | CPUSetOCLevel / CPUSetSpecialOC | W | |
| 41 to 44, 46 | GPU frequency, max frequency, usage, count, idle state | R | via NvAPI, service side |
| 45 | GPUSetOCLevel | W | NvAPI P-state change or WMI, depending on `Feature.ini` rules |
| 47 | GetWLANState | R | |
| 48 to 50 | Apply / restore / get BIOS logo | W / W / R | copies a file to `\EFI\OEM` on the ESP |
| 51 / 52 / 53 | GetOperationModeCapability / GetOperationMode / SetOperationMode | R / R / W | the Quiet-Default-Extreme-Turbo style performance mode |

Admin-agent commands (`AdminAgentCommand`, C): USB keyboard colour and patterns (10 to 16, 31, 32, 34), key settings and
actuation points (17 to 24, for the optional USB gaming keyboard), sound modes (5 to 8), sticky key, Win/Menu key
(25), Whisper mode (35), hotspot ICS (29), delete file/folder (3, 4). Not read further; most are out of scope for
this laptop.

### 3.2 WMI methods (the layer a Rigbay module can talk to)

Schema read from `root\WMI:AcerGamingFunction` (O). All methods are "Implemented=True"; the MOF marks every one of them
read=true and write=true, so the MOF does not tell Get from Set.

| WmiMethodId | Method | In | Out | R/W |
| --- | --- | --- | --- | --- |
| 1 | `SetGamingProfile` | UInt64 | UInt32 | W |
| 2 | `SetGamingLED` | UInt64 | UInt32 | W |
| 3 | `GetGamingProfile` | UInt32 | UInt64 | R? |
| 4 | `GetGamingLED` | UInt32 | UInt64 | R? |
| 5 | `GetGamingSysInfo` | UInt32 | UInt64 | R? (sensors) |
| 6 / 7 | `SetGamingRgbKb` / `GetGamingRgbKb` | UInt32 / UInt32 (note: both take UInt32 in the schema) | UInt64 | W / R? |
| 8 / 9 | `SetGamingProfileSetting` / `GetGamingProfileSetting` | UInt64 / UInt32 | UInt32 / UInt64 | W / R? |
| 10 / 11 | `SetGamingLEDBehavior` / `GetGamingLEDBehavior` | UInt64 / UInt32 | UInt32 / UInt64 | W / R? |
| 12 / 13 | `SetGamingLEDColor` / `GetGamingLEDColor` | UInt64 / UInt32 | UInt32 / UInt64 | W / R? |
| 14 / 15 | `SetGamingFanBehavior` / `GetGamingFanBehavior` | UInt64 / UInt32 | UInt32 / UInt64 | W / R? |
| 16 / 17 | `SetGamingFanSpeed` / `GetGamingFanSpeed` | UInt64 / UInt32 | UInt32 / UInt64 | W / R? |
| 18 / 19 | `SetGamingFanTable` / `GetGamingFanTable` | UInt64 / (none) | UInt32 / UInt64 | W / R? |
| 20 / 21 | `SetGamingKBBacklight` / `GetGamingKBBacklight` | byte[] / (none) | UInt32 / byte[] + UInt8 | W / R? |
| 22 / 23 | `SetGamingMiscSetting` / `GetGamingMiscSetting` | UInt64 / UInt32 | UInt32 / UInt64 | W / R? |
| 24 / 25 | `SetCPUOverclockingProfile` / `GetCPUOverclockingProfile` | profile byte + struct bytes | code | W / R? |

`AcerGenericMethod`: id 1 `SetFunction` (UInt64 in, UInt32 out), id 2 `GetFunction` (UInt32 in, UInt64 out).
`AcerBiosConfigurationTool`: `SetBiosOptions` (takes a BIOS password argument), `GetBiosOptions`, `GetUSBDeviceFilterStatus`.
The service does query this class (its string table holds the class name and both method names, next to a
check named "BIOS MOF no uiInput"); what it asks is not decoded (**G**: a capability check, not a BIOS change). **Do not touch** (BIOS settings).

Instances can only be listed from an elevated process (normal user gets "Access denied"). **O**

### 3.3 Feature by feature

Argument encodings below were read from the UI's code (C). "Output" is the 64-bit (or 32-bit) answer; in every
"get" the UI checks the **low byte as status, 0 meaning success**.

**Sensors (temperature, fan speed): `GetGamingSysInfo`**

| Input | Output | R/W |
| --- | --- | --- |
| `1 \| (index << 8)` | status in bits 0 to 7, value in bits 8 to 23 | R? |
| index | 1 CPU temp, 2 CPU fan speed, 3 system temp, 4 system fan speed, 5 system fan speed external, 6 GPU1 fan speed, 7 system2 temp, 8 system2 fan speed, 9 GPU2 fan speed, 10 GPU1 temp, 11 GPU2 temp, 12 to 17 fan "rate" (CPU, system, system2, system external, GPU1, GPU2) | |

Unit of the values: CPU temperature is **O** (read live once, 73, consistent with degrees C; see section 11.1). Fan "speed" in
RPM and "rate" in percent stay **G** until those indexes get their own gate.
Input `2` on the same method returns whether "battery boost" is on (UI reads the byte at bits 40 to 47, 1 = on). C.

**Performance mode ("operation mode": Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5)**

UI → pipe command 53 with the mode number. **Decoded in B7 (section 15):** the service calls `SetGamingMiscSetting` with `(mode << 8) | 0x0B`, then a
GPU step (NvAPI offsets plus `SetGamingMiscSetting` `(level << 8) | 0x05`); the admin-agent lighting command is only for machine types 9 and 10, not for this machine.
B2 confirmed the UI has **no read** of the operation mode from firmware (section 11.3); the service has one (`GetGamingMiscSetting` input `0x0B`, section 15.5). The current value is cached at
`HKLM\...\PredatorSense\Overclock\CurrentOperationMode` (4 = Extreme in B1 to B5, **1 = Default on 2026-11-03**, O).
`Feature.ini` on this machine enables Quiet, Normal, Extreme, Turbo (O). Mapping of the 4 labels to the 6 numbers: Normal = Default (1); the mapping is in 15.1 (C).

**CPU and GPU overclock level (`SetGamingMiscSetting`, W)**

| What | Input values (hex) |
| --- | --- |
| CPU level 0 / 1 / 2 | `0x07` / `0x107` / `0x207` |
| GPU level 0 / 1 / 2 | `0x05` / `0x105` / `0x205` |
| "MaxQ" mode 1, level 0 / 1 / 2 | `0x1005` / `0x1105` / `0x1205` |
| "MaxQ" mode 2, level 0 / 1 / 2 | `0x2005` / `0x2105` / `0x2205` |
| GPU mode (discrete / hybrid switch) | `2 \| (mode << 8)`, mode from the capability read below |

Read-back of GPU mode support: `GetGamingMiscSetting(2)`, mode in bits 8 to 15 (values 1, 2, 3 accepted). Capability
bits: `GetGamingMiscSetting(9)`, bits 8, 9, 10 of the answer (guarded by an SMBIOS version check). On this machine the
service also has its own NVIDIA path (NvAPI P-states) and an Intel path (through the XTU service), selected by the
`[OverclockDetial]` rules in `Feature.ini` (O: all three "WMI" rules are 1, so the WMI path is the one used here; **G**).

**Fan mode: `SetGamingFanBehavior` (W), UI modes Auto 0, Max 1, Custom 2 (+ Silent 3 in an enum)**

The input is a 64-bit value built from two halves, the lower half one bit-field per fan, the upper half (shifted
left by 16) another; the number of fans is read from `HW_Support.ini` `[FanSupport]` (here CPU 1, GPU 2, System 0).
B2 finished the write encoding (section 11.2). Reading the mode back: the UI never calls `GetGamingFanBehavior`, so
its input is **not traceable**. The UI keeps `CurrentFanMode` in the registry (here 0 = Auto, O).

**Cool Booster (extra fan boost)**: set = `AcerGenericMethod.SetFunction` with `7 | (state << 16)` (W);
read = `GetFunction(0x207)`, byte 1 of the answer equals 1 when on (R?). C. Name matches the UI's "Cool Booster" toggle.

**Custom fan speeds, fan curve**: UI sends per-fan percentages (0 to 100, scrollbar) and an "auto" flag per fan;
service has `SetGamingFanSpeed` (16), `SetGamingFanTable` (18), and its own `set_oc_fan_table` routine that changes the
table when CPU/GPU overclock level changes (log text; skipped when operation mode is supported, so skipped here, C).
Speed write encoding decoded in section 11.2; the table payload is not (and this model's UI does not use the table).

**Keyboard lighting**: this machine reports RGB keyboard support and `LightingType` 1 (O). Two routes exist in the
code: WMI (`SetGamingRgbKb`, `SetGamingKBBacklight`, LED behavior/colour methods) and USB (admin agent). Which one
this model uses: **established in B2: WMI** (section 11.4). The UI variant for this machine is "Clubman"
(`Feature.ini` `[MachineType] Type=8`).

**Battery charge limit: not part of PredatorSense v3 on this machine.** I found no charge-limit string, command or
setting in the UI, `TsDotNetLib`, or any service binary (a search for charge/limit/calibration found only a
"stop overclocking when battery is below 30%" notice in `PSAgent.exe`), and Acer Care Center is not installed. The word
"battery boost" in the UI is a performance behaviour on battery (status read via `GetGamingSysInfo(2)`), not a charge cap.
Whether the firmware has a charge-limit setting reachable through the Misc/Profile methods: **G, no evidence**.

**Other**: LCD overdrive (service/UI helpers `set_lcd_over_drive`, registry `AdvanceSettings\LCDOverDrive`), POST/BIOS
animation, NVIDIA Whisper mode (admin agent 35, registry `NV_WhisperMode20_*`), Wi-Fi radio state read, AC adapter
type (`GetFunction(10)`: bits give 1-AC / 2-AC / PD, only on machines that report two adapters). Not studied further.

## 4. Q3: how the service reaches the firmware, privilege, caller check

- The service talks to the firmware only through the WMI classes above (COM `ConnectServer` to `ROOT\WMI`, C from log
  text and imports). WMI-ACPI is handled by the Windows `WmiAcpi` driver (running, O) and the BIOS; Acer ships no
  own kernel driver here. **O**
- Reaching those WMI methods needs an elevated process: instance listing is denied to a normal user. **O**. The service
  runs as LocalSystem. Whether a method call needs more than admin: not tested.
- **The pipe checks its caller.** The service impersonates the pipe client, reads the token, checks it is not an
  AppContainer process, takes the caller's process id and image path (and package family name for a packaged app),
  and refuses ("Not Allowed Client") unless it is the genuine PredatorSense app (package family
  `AcerIncorporated.PredatorSenseV30_48frkmn4z8aw4`, or the exe under `...\WindowsApps\AcerIncorporated.PredatorSenseV30_*\Win32\PredatorSense.exe`).
  C from log text and imports. A security descriptor string sits right next to the pipe-name prefix in the
service's string table: deny Guests and Anonymous, allow Authenticated Users read/write/execute, allow
Administrators everything (`D:(D;OICI;GA;;;BG)(D;OICI;GA;;;AN)(A;OICI;GRGWGX;;;AU)(A;OICI;GA;;;BA)`). That it is
the pipe's descriptor is **G** from its position (the service imports `ConvertStringSecurityDescriptorToSecurityDescriptorW`);
if so, any logged-in user may connect and the caller check above is the real gate. **Consequence: a Rigbay module should not
  plan to use the pipe.** It would have to pretend to be the Acer app, which this project will not do. The route for a
  module is WMI directly, from the elevated host.
- Calling WMI directly skips every safety check the Acer software may do (for example `PSAgent.exe` carries notices that
  overclocking is stopped when the battery is below 30% or the adapter is unplugged, C from its strings; where that rule is
  enforced I did not see). A module has to carry its own limits.

## 5. Q4: persistence and resets

- The UI and service record what was last set in the registry under `HKLM\SOFTWARE\OEM\PredatorSense` (O). Examples
  on this machine: `FanControl\CurrentFanMode=0`, `Overclock\CurrentOperationMode=4`, `Overclock\MaxQ_Mode=2`. These
  are the app's records, **not live firmware state**, and can be stale.
- The service handles power and session events (suspend, resume, logon, lock; C from log text), most likely to
  re-apply settings. That it re-applies, and which: **G**. *(B7, section 15.3: wrong for the service, whose event handlers only write log lines (C).
  The re-applying for the operation mode is done by `PSAdminAgent.exe` on power-source change, low-battery change and resume.)*
- Whether the firmware keeps fan or mode settings across reboot or sleep by itself: not tested (**G**). Do not rely on
  it; a module should re-apply or re-read after resume.
- Profiles and hotkey assignments live as XML/ini files under `C:\ProgramData\oem\PredatorSense\ProfilePool` (O, not read).

## 6. Q5: what can be read safely now

| Value | How | R status |
| --- | --- | --- |
| Model, board, BIOS | `Win32_ComputerSystem`, `Win32_BaseBoard`, `Win32_BIOS` | read-only OS data |
| Service present and running | service control manager | read-only |
| Pipes present | list `\\.\pipe\` | read-only (never opened) |
| WMI class present, method ids | class schema from `root\WMI` | read-only (no instance, no call) |
| Last recorded fan mode, performance mode, OC level, feature flags | registry and ini files above | read-only, may be stale |
| Number of fans, supported modes | `HW_Support.ini`, `Feature.ini` | read-only |
| Battery percent | `Win32_Battery` | read-only |
| ACPI thermal zone | `root\WMI MSAcpi_ThermalZoneTemperature` | read-only, needs admin (here: "Access denied" unelevated) |
| Live CPU temperature | `AcerGamingFunction.GetGamingSysInfo(0x0101)` | done once in B2 (gate approved), see 11.1 |
| Other temperatures, fan speed and rate | same method, other indexes | **each index needs its own gate** |
| Live GPU clocks and usage | NVIDIA NVML/NvAPI, not Acer | not studied |

Probe output on this machine (`tools/predatorsense-probe`, run unelevated, **no WMI method called**, trimmed):

```
elevated: False
identity:  system_model: Predator PT516-51s | board_product: Countryman_TLS | bios: Insyde Corp. V1.08
support:   service_present: True | service_pipe_present: True | gaming_wmi_class_present: True | supported: True
service:   PSSvc  Running  LocalSystem  Manual  "C:\Program Files\Acer\PredatorSense Service\PSSvc.exe"
pipes:     predatorsense_service_namedpipe: True | admin_agent_pipes: predatorsense_admin_agent_1
wmi_schema: root\wmi:AcerGamingFunction present, guid {7A4DDFE7-...}, 25 methods (ids 1..25)
            root\wmi:AcerGenericMethod  present, guid {1EA0BC57-...}, SetFunction=1, GetFunction=2
cache:     fan.CurrentFanMode: 0 | oc.CurrentOperationMode: 4 | oc.MaxQ_Mode: 2 | oc.GPU_OC_Support: 1 | oc.CPU_OC_Support: 0
ini:       FanSupport CPU=1 GPU=2 System=0 | OperationModeSupport Quiet/Normal/Extreme/Turbo = 1 | MachineType 8
battery:   AP20BHU, 100 %, status code 2 (on AC)
thermal zones: Access denied (needs admin)
acer_sysinfo: not requested (no WMI method was called)
```

That was the B1 unelevated run. The elevated run with the one approved firmware read is in section 11.1.

## 7. Q6: risks and surprises

1. **Get methods are not proven to be reads.** The MOF flags every method read+write. `GetGamingSysInfo` takes a low-byte
   selector (1 = health value, 2 = battery boost); other Get methods may have selectors I have not seen. Treat every
   call whose input I cannot trace to the UI as a write. Worst case is described in `GATE-getgamingsysinfo.md`.
2. **Direct WMI skips Acer's guards** (section 4). Acer's software stops overclocking at low battery or on unplug;
   a module that writes `SetGamingMiscSetting` has to do the same.
3. **Overclock and fan inputs are packed bit fields.** A wrong bit position changes a different fan or setting.
   Do not hand-craft values; only send values copied from a decoded table.
4. **The same hardware is also driven by Intel XTU and NvAPI.** The service applies CPU settings through the XTU
   service and GPU clocks through NvAPI. Rigbay's XTU module and a PredatorSense module can fight each other over the
   same limits; also `CurrentCPUMode`/`CurrentGPUMode` in the registry will not reflect what Rigbay did.
5. **Running both PredatorSense and a Rigbay module:** the Acer UI may re-apply its own mode on resume or on a hotkey
   (Turbo key, `UICMD_SWITCH_TURBO_MODE`), undoing the module's setting. **G.**
6. **BIOS tool class** `AcerBiosConfigurationTool` can change BIOS options and accepts a password. Never call it.
7. `Reset`-style behaviour of the firmware after sleep or reboot is unknown (section 5).

## 8. Q7: what a Probe on another machine should check

In order, and the module says "not supported" with the first failing reason:

1. `Win32_ComputerSystem.Manufacturer` is Acer. (Model strings differ per product, so do not match a model list.)
2. Service `PSSvc` exists (it may be stopped; it is Manual start). Not required for direct WMI, but means PredatorSense is installed.
3. WMI class `root\WMI:AcerGamingFunction` exists and has `GetGamingSysInfo` with `WmiMethodId` 5 (schema only).
   This is the real capability test: if absent, no PredatorSense-style control through this route.
4. Optional detail: `HKLM\SOFTWARE\OEM\PredatorSense` and `C:\ProgramData\oem\PredatorSense\HW_Support.ini` / `Feature.ini` for fan count,
   supported operation modes, overclock support. These are written by PredatorSense; if PredatorSense was never run they
   may not exist, so absence is "unknown", not "unsupported".
5. Other Acer lines (Nitro, other Predator models) use other PredatorSense variants and may have different fan counts
   and mode lists. The UI has per-model pages (named after codenames); the model-to-page mapping was not read.

The probe implements 1 to 4.

## 9. Not verified

- Any live firmware value other than the one CPU temperature in 11.1 (that single value, input `0x0101`, once).
- Native service code: ~~what exactly it sends to WMI for operation mode~~ (done in B7, section 15, with my own decoder and no vetted disassembler); the fan table and lighting handlers are still not read; any rate limits.
- Any firmware **read** of fan behaviour, fan speed, fan table, operation mode or lighting colour: the UI has none (11.2 to 11.4), so their input encodings cannot be traced.
- The fan-table payload, and the exact conditions of the per-fan custom-speed sequence.
- Meaning of the `GetGamingLEDBehavior(1)` bits (11.5).
- Whether the security descriptor string I found is really the pipe's, and whether a non-genuine caller is refused at connect time or only after (I never opened the pipe).
- Units of fan "speed" and "rate" values (CPU temperature unit: consistent with degrees C, one sample, no second CPU-temperature source).
- What resets settings (sleep, reboot, Turbo key).

## 10. Proposed write operations (for the owner to approve one by one)

Nothing here has been run, in B1 or B2. The B2 read-decoding changed the picture in one important way: **the Acer UI
never reads fan behaviour, fan speed, fan table or operation mode back from firmware** (11.2, 11.3). So for those
writes there is no traceable "read the current state first". The only traceable before/after checks are
`GetFunction(0x207)` (Cool Booster state) and the `GetGamingSysInfo` fan speed/rate indexes. That drives the order below.

Suggested order: read gates R1 and R2 in `GATE-b2-reads.md` first; then W1, verified by R2 and R1.

| # | Call | Value | Expected effect | Risk | Undo | State of the evidence |
| --- | --- | --- | --- | --- | --- | --- |
| W1 | `AcerGenericMethod.SetFunction` (id 1) | `0x10007` (Cool Booster on) | fans boost to a higher speed | low to medium: louder, more wear if left on | `SetFunction(0x7)` | encoding C; meaning "Cool Booster" from the name only. **Read `GetFunction(0x207)` first (R2).** Verify with R1/R2 after. |
| W2 | `AcerGamingFunction.SetGamingMiscSetting` (id 22) | `0x2005` (MaxQ mode 2, level 0) | GPU power profile for MaxQ | medium: GPU performance/power change | write the previous value; **the current value has no traceable read** | encoding C; effect G. Do not run. |
| W3 | operation mode | see `WRITE-PROPOSALS.md` W3 (B7) | performance profile | medium to high | the previous mode value | **Superseded by B7 (section 15):** the service's WMI call is `SetGamingMiscSetting((mode << 8) \| 0x0B)`. Not run. The pipe stays off limits (section 4). |
| W4 | `SetGamingFanBehavior` (id 14) | Auto `0x01410019`; Max `0x02820019` (this machine: 1 CPU fan, 0 system, 2 GPU fans) | Auto: firmware control (the recorded state). Max: all three fans full | Max: loud, no overheating risk in itself; a wrong mask addresses the wrong fan | Auto value above | encoding C (11.2). No firmware read of the current mode exists; registry says Auto (stale-prone). Verify with `GetGamingSysInfo` fan speed/rate (needs its own gates). Higher risk than W1; ask after W1. |
| W5 | `SetGamingFanSpeed` (id 16) with `SetGamingFanBehavior` custom | `fanId \| (percent << 8)` after a custom-mode write | fixed fan speed | highest: a low percentage under load overheats | Auto value (W4) | encoding partly decoded (11.2); call sequence not fully traced. **No value proposed.** |
| W6 | `SetGamingFanTable` (id 18) | n/a | | highest | | payload **not decoded**; this model's UI does not use it. No value proposed. |

Charge limit: look for it outside PredatorSense (Acer Care Center, which is not installed) or accept it is not available.

## 11. B2: first live read, and decoding of the fan, operation-mode and lighting reads

Scope of B2: read-only. **One** WMI call was made in total (11.1). No `Set*` method, no pipe connection, no registry or
service change. Everything else here was read from the installed UI and service files in place with a metadata-only IL
lister (a scratch tool kept outside the repo; it parses the .NET metadata and IL bytes and executes none of Acer's code)
and from the service's string table.

### 11.1 The one live read

Approved by the owner as gate `g-sysinfo-0101`: `root\WMI:AcerGamingFunction.GetGamingSysInfo`, `gmInput` = `0x0101`
(CPU temperature), once, elevated. Run with `tools/predatorsense-probe` (`--allow-call --sysinfo-index 1`), elevated via
`Start-Process -Verb RunAs`, output captured to a file. The old gate `g-c1bd8ab12235` was void and covered nothing.

```
elevated: True
acer_sysinfo:
  cpu_temperature:
    input: 0x101
    raw: 0x4900
    status: 0
    value: 73
windows_thermal_zones:
  zone0_ACPI\ThermalZone\TZ00_0: 53.1
```

- **O**: status byte 0, payload `(raw >> 8) & 0xFFFF` = 73. The call returned at once with no error. The probe changes
  nothing; I did not look for firmware side effects beyond the answer.
- Plausibility check made right after, read-only: CPU load about 15 % (`Win32_Processor`; `% Processor Utility` 6 to 23 %),
  the Windows thermal-zone counter `TZ00` = 325 K = 51.9 C (the probe's sample: 53.1 C). 73 is believable as a CPU package
  temperature in degrees C on a laptop in Extreme mode with the probe, the Acer app and other sessions running. TZ00 is a
  different sensor (which one is unknown), so the two numbers are not expected to match. Verdict: not odd, no stop.
  **Unit degrees C: consistent (one sample); not proven.**
- The rest of that run is unchanged from B1 (service, pipes, WMI schema, cache, ini); the elevated run added the ACPI zone above.

### 11.2 Fan reads and the fan encodings

**Finding: the UI never reads fan behaviour, fan speed or the fan table back.** In `PredatorSense.exe` the wrappers
`WMIGetGamingFanGroupBehavior` (pipe 25), `WMIGetGamingFanGroupSpeed` (27) and `WMIGetGamingFanTable` (29) exist, but no
method in the program calls any of them (searched the IL of every method, also for the raw pipe send with those command
ids; the other hits for 25, 27, 29 are admin-agent commands on the other pipe, a separate numbering). The UI shows fan state from the registry cache
(`FanControl\CurrentFanMode`, `...Percentage`, `...CustomAuto`) and from `GetGamingSysInfo` (speed and rate indexes).
So the inputs of `GetGamingFanBehavior` (id 15, UInt32), `GetGamingFanSpeed` (id 17, UInt32) and `GetGamingFanTable`
(id 19, no input) are **not traceable to a UI read**, and under the project rule they are not to be called. By symmetry
with the writes below a speed read might take the fan id; that is **G** and stays unused.

What the UI does read for fans (traceable, each still needs its own gate): `GetGamingSysInfo` with `1 | (index << 8)`.
Clubman's refresh code asks, per fan, the speed index and the rate index: CPU fan 2 and 12, system fan 1 4 and 13,
system fan 2 8 and 14, GPU fan 1 6 and 16, GPU fan 2 9 and 17.

Write encodings, decoded from `Set_WMI_FanMode` (both `FanControlPage`, which Clubman uses, and `FanControlPage_Ghibli`; same
shape) and the per-fan custom code. **Never run in B2.** They are listed so that a later, approved write is not hand-crafted.

`SetGamingFanBehavior` (id 14, UInt64 in, UInt32 out), `input = lo | (hi << 16)`. Fan "slots": 0 CPU, 1 and 2 system fans,
3 and 4 GPU fans (only fans that exist; counts come from `HW_Support.ini [FanSupport]`).

| Part | Meaning |
| --- | --- |
| `lo` bit `s` | fan in slot `s` is addressed: CPU `0x01`, system 1 `0x02`, system 2 `0x04`, GPU 1 `0x08`, GPU 2 `0x10` |
| `hi` bits `2s..2s+1` | mode of the fan in slot `s`: **1 auto, 2 max, 3 custom** (0 not seen) |

This machine (1 CPU fan, 0 system, 2 GPU): `lo` = `0x19`. All fans Auto: `hi` = `0x141`, input **`0x01410019`**. All Max:
`hi` = `0x282`, input **`0x02820019`**. The UI sets Custom per fan, not for all at once. Per-fan values seen in the code:
CPU custom `0x30001`, CPU auto `0x10001`, GPU 1 custom `0xC00008`, GPU 1 auto `0x400008`, GPU 2 custom `0x3000010`, GPU 2 auto `0x1000010`.

`SetGamingFanSpeed` (id 16): `input = fanId | (percent << 8)`, fan ids **1 CPU, 2 and 3 system, 4 and 5 GPU**. Percent is the
UI scrollbar value (0 to 100 per B1; the minimum the UI allows was not checked). The UI brackets it with behaviour writes
for that one fan; what the code's `auto` flag adds to the custom value was not traced to the end.

`SetGamingFanTable` (id 18): the only caller on a fan page is the "Silent" mode of `FanControlPage`, which sends `0`.
Clubman's main window has no Silent mode, so this model's UI does not use the table; the payload format is not decoded.

Cool Booster: `get_coolboost_state` = `AcerGenericMethod.GetFunction(0x207)`; the code checks the low byte (status) and then
**byte 1 == 1 means on**. Set is `SetFunction(7 | (state << 16))`. This is the one fan-related state that has both a traced
read and a traced write.

### 11.3 Operation mode

- Service command 53 (`SetOperationMode`) is sent by `MonitorAndOCClass.set_operation_mode` with one `UInt32` (0 Quiet,
  1 Normal/Default, 2 Render, 3 Edit, 4 Extreme, 5 Turbo; numbers from `OperationModePage.GetOperationMode`). The reply is
  9 bytes; the UI reads a 32-bit status at offset 5.
- **Commands 51 (`GetOperationModeCapability`) and 52 (`GetOperationMode`) are never sent by the UI.** The UI learns the
  current mode from the registry `Overclock\CurrentOperationMode` (read in many places; here 4) and the supported modes from
  `Feature.ini [OperationModeSupport]`. So there is no UI read of the operation mode from firmware to trace.
- The service has handlers `get_operation_mode`, `get_operation_mode_capability`, `set_operation_mode`. Their log text has no
  WMI input/output lines (unlike the fan table and LED calls), and the capability test is tied to an SMBIOS type 172 table
  (`stSMBiosType172.pfunNum1[i]` in the string table). How they reach the firmware is **not decoded**; that needs
  disassembling `PSSvc.exe`, which B2 did not do.
- A Rigbay module cannot use command 53 (the pipe checks the caller, section 4). *(B7 update: the direct WMI route is now decoded, section 15.1: the service calls
  `SetGamingMiscSetting((mode << 8) | 0x0B)` and then does a GPU step. The guess in this bullet about "a combination of other calls" was only partly right: the main call is a new selector, `0x0B`.)*

### 11.4 UI variant and lighting route for this machine

- `Startup` reads `Feature.ini [MachineType] Type` into `_feature_type`; this machine has **8**. `App.OnStartup` maps it to a
  main window class: 1 Covini, 2 and 6 Ghibli (6 uses the Spyder lighting page), 3 Raptor, 4 Levante, 5 Defender, 7 QX60,
  **8 Clubman**, 9 XC90, 10 Evoque, otherwise the generic window. So this machine's UI is **Clubman**, with the pages
  `LightingPage_Clubman`, `OperationModePage`, `MonitoringPage` and `FanControlPage`.
- **Lighting route: WMI.** `LightingPage_Clubman` and `LightingDynamicUI_Clubman` call `SetGamingRgbKb` (id 6),
  `SetGamingKBBacklight` (id 20), `SetGamingLEDBehavior` (id 10) and `AcerGenericMethod.SetFunction`. I found no admin-agent
  or USB-lighting command in them, so the USB route in the admin agent is for other hardware here. Colours and effects are
  written, not read: the UI keeps lighting profiles in its own XML under `ProfilePool`, and **no `Get*` of RGB, zone colour
  or LED group colour is called anywhere**. The wrappers `WMIGetGamingRgbKbSetting`, `WMIGetGamingKBBacklight`,
  `WMIGetGamingLEDGroupBehavior/Color` and `WMIGetGamingProfileSetting` have no callers.
- The only traced lighting-related read is a field of `AcerGenericMethod.GetFunction`, used for the keyboard backlight:
  input = `BK_Hotkey_Number | (1 << 8) | 0x80000`. On this machine `BK_Hotkey_Number` is **132** (`0x84`, **O**, registry),
  so the input would be **`0x80184`**. Output: low byte status, **bits 32 to 39 = brightness percent**, and "backlight off"
  when **bits 40 to 47 equal 30**. Never run.
- Effects list and colour tables for the Clubman keyboard are not needed until a lighting write is approved.

### 11.5 The other traced reads (none run)

| Call | Input | How the UI parses it | Use |
| --- | --- | --- | --- |
| `GetGamingLEDBehavior` (id 11) | `1` | status = low byte (0 ok); then `(raw >> 8) & 3 == 1` in the fan-mode handlers and `(raw >> 14) & 3 == 1` in one operation-mode branch | a boost-related indicator. With Turbo + Max fan + AC + battery boost the UI writes `SetGamingLEDBehavior(0x10001)` (or `0x410001`) and `SetGamingMiscSetting(0x101)`; on the way back, if the read shows the flag, `SetGamingLEDBehavior(1)` (or `0x400001`) and `SetGamingMiscSetting(1)`. **The meaning is G.** |
| `GetFunction` (AcerGenericMethod id 2) | `0x207` | byte 1 of the answer == 1: Cool Booster on | traced, Cool Booster state |
| `GetFunction` | `0x80184` (this machine) | bits 32 to 39 brightness, bits 40 to 47 == 30 means off | traced, keyboard backlight |
| `GetGamingMiscSetting` (id 23) | `2` and `9` | GPU mode, GPU mode capability (see 3.3) | traced |
| `GetGamingProfile` (id 3) | an input built by the UI for LCD overdrive and the current group index | not decoded further | not needed now |
| `GetGamingSysInfo` | `1 \| (index << 8)` | `(raw >> 8) & 0xFFFF` if low byte 0 | traced, **confirmed live for index 1** |

Candidate next gates for these are written in `GATE-b2-reads.md` (not opened; see the note there).

## 12. B3: the follow-up reads (owner approval: "read all parameters freely"; coordinator scope applied)

Scope: **read-only, no `Set*` call, no pipe, no registry or service change.** The owner's gate `g-b3-reads` was read by the
coordinator as: R1, R2, R3, `GetGamingSysInfo` indexes 2 to 17, battery boost, `GetGamingMiscSetting(2)` and `(9)`,
`GetGamingLEDBehavior(1)`, each once, stop at the first odd status. After R2 answered with a non-zero status the coordinator
narrowed it: continue with sysinfo 3 to 17 and a few repeats of `0x0101`; **hold** R3 and every other call until R2 is explained.

Tool: `tools/predatorsense-probe`, extended with one flag per call (`--r1`, `--r2`, `--r3`, `--sysinfo-index N`, `--battery-boost`,
`--misc-get 2|9`, `--led-behavior-get`). The probe has an allowlist of exact class, method and input, refuses any `Set*` method, refuses
a repeated call, and halts the run at the first exception or non-zero status. All runs elevated through `Start-Process -Verb RunAs`,
output to a file under `tools/predatorsense-probe/bin/` (git-ignored). Refusal paths were tested without touching WMI (wrong flag
combination, duplicate call, out-of-range index).

### 12.1 What was run, and what was not

| Call | Input | Run? | Result |
| --- | --- | --- | --- |
| R1 `GetGamingSysInfo` | `0x0201` | yes, once | raw `0xB2900`, status 0, value 2857 |
| R2 `AcerGenericMethod.GetFunction` | `0x207` | yes, once | raw `0x2E1`, **status byte `0xE1` (225)**, byte 1 = `0x02`. **Odd: run stopped.** |
| R3 `AcerGenericMethod.GetFunction` | `0x80184` | **no (held by the coordinator)** | not run |
| `GetGamingSysInfo` indexes 3 to 17 | `1 \| (index << 8)` | yes, once each (15 calls) | all status 0, table below |
| `GetGamingSysInfo` battery boost | `2` | **no (held)** | not run |
| `GetGamingMiscSetting` | `2`, `9` | **no (held)** | not run |
| `GetGamingLEDBehavior` | `1` | **no (held)** | not run |
| `GetGamingSysInfo` | `0x0101` | yes, 3 more times (approved by the coordinator for the temperature cross-check) | 64, 63, 63 |

R1/R2 run (raw probe output, firmware part; the rest of the file is the B1 inventory, unchanged except the ACPI zone 52.1 C):

```
firmware_reads:
  cpu_fan_speed:
    call: AcerGamingFunction.GetGamingSysInfo(0x201)   raw: 0xB2900   status: 0   value: 2857
  r2_cool_booster_state:
    call: AcerGenericMethod.GetFunction(0x207)         raw: 0x2E1     status: 225
  stopped_at: r2_cool_booster_state (error or non-zero status; remaining calls NOT made)
```

No exception and no hang on any call. I did not watch the fans, lighting or any other hardware state while the calls ran, so "no side effect" is **not verified**. What I can say: the registry cache (the app's own records, not firmware) was identical before and after (fan mode 0, operation mode 4, MaxQ mode 2, GPU level 0, keyboard colour 2), and the sensor values stayed in a normal range.

### 12.2 The sensor table (indexes 1 to 17, this machine: 1 CPU fan, 2 GPU fans, 1 GPU, no system fan)

Taken at about 18:16 to 18:18 local time, machine idle on AC (CPU 6 to 37 %, GPU 20 to 27 %), performance mode Extreme per the registry cache, fan mode Auto per the cache.

| Index | Input | Name | raw | status | value | What it looks like |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `0x0101` | CPU temperature | `0x4900`, `0x4000`, `0x3F00`, `0x3F00` | 0 | 73 (B2), 64, 63, 63 | degrees C, see 12.3 |
| 2 | `0x0201` | CPU fan speed | `0xB2900` | 0 | **2857** | RPM (magnitude only) |
| 3 | `0x0301` | system temperature | `0x2C00` | 0 | **44** | degrees C; a board/system sensor, cooler than TZ00 |
| 4 | `0x0401` | system fan speed | `0x0` | 0 | 0 | no such fan here |
| 5 | `0x0501` | system fan speed, external | `0x0` | 0 | 0 | no such fan here |
| 6 | `0x0601` | GPU 1 fan speed | `0xBF500` | 0 | **3061** | RPM |
| 7 | `0x0701` | system 2 temperature | `0x0` | 0 | 0 | absent |
| 8 | `0x0801` | system 2 fan speed | `0x0` | 0 | 0 | absent |
| 9 | `0x0901` | GPU 2 fan speed | `0xC0B00` | 0 | **3083** | RPM |
| 10 | `0x0A01` | GPU 1 temperature | `0x3500` | 0 | **53** | degrees C, **matches `nvidia-smi` (53.19 and 53.41 C at the same moment)** |
| 11 | `0x0B01` | GPU 2 temperature | `0x0` | 0 | 0 | absent (one GPU) |
| 12 | `0x0C01` | CPU fan rate | `0x0` | 0 | **0** | see below |
| 13 to 15 | `0x0D01` to `0x0F01` | system, system 2, external fan rate | `0x0` | 0 | 0 | absent fans |
| 16, 17 | `0x1001`, `0x1101` | GPU 1, GPU 2 fan rate | `0x0` | 0 | **0** | see below |

- **Absent hardware answers status 0 and value 0**, not an error. A module must not treat 0 as "stopped" for a fan the machine does not have;
  the fan count comes from `HW_Support.ini [FanSupport]` (CPU 1, GPU 2, System 0), which matches exactly which indexes returned non-zero.
- **Temperature unit: degrees C (O) for index 10**, because it equals `nvidia-smi` at the same moment to within 0.4. Index 1 and 3 use the same
  method and layout, so degrees C is the working assumption; index 1 is not independently confirmed (12.3).
- **Fan speed unit: RPM is the working assumption (G+).** The three values (2857, 3061, 3083) are in the range of laptop fans at moderate load;
  Windows has no fan counter set to compare with (the five "ACPI Fan" devices exist; I found no counter for them). Not proven.
- **The "rate" indexes (12 to 17) all returned 0 while the fans were turning at ~3000.** So "rate" is not a live percent of maximum on this machine in
  Auto mode. Possible reasons (all **G**): it is a commanded percent that is only non-zero in Custom mode, or it is not implemented on this model.
  The UI's fan page reads speed and rate together; it shows the speed. Do not use the rate indexes as a fan-state read-back.

### 12.3 CPU temperature cross-check

Question from B2: `0x0101` gave 73 while ACPI `TZ00` was ~52 C. Do they agree, and if not why?

Sources available **without a driver install**: the Windows thermal-zone counters (`Thermal Zone Information`; only one zone, `TZ00`, which is
the same ACPI source as `MSAcpi_ThermalZoneTemperature`), the RAPL package power counters (`Energy Meter`, in milliwatts; power, not temperature),
CPU utilisation/frequency counters, and `nvidia-smi` (GPU only). There is no second CPU temperature source: `Win32_TemperatureProbe` is empty,
and I found no counter set for the Intel Dynamic Tuning participant devices (they are present as PnP devices) or for the five "ACPI Fan" devices. Anything else (LibreHardwareMonitor, HWiNFO, XTU) needs a driver or touches
the Intel service; not done.

Samples taken in the 2 s before and after each probe call (so within about 2 s of the firmware read):

| Read | Acer `0x0101` | `TZ00` | package power (RAPL) | CPU utilisation | GPU (`nvidia-smi`) |
| --- | --- | --- | --- | --- | --- |
| B2 (earlier session) | **73** | 51.9 to 53.1 | n/a | ~15 % | n/a |
| B3 run 1, 18:17:54 | **64** | 52.1 / 52.1 | 12.2 / 9.3 W | 9 / 6 % | 52.6 / 52.4 |
| B3 run 2, 18:18:03 | **63** | 52.1 / 53.1 | 9.5 / 14.2 W | 6 / 12 % | 51.9 / 52.0 |
| B3 run 3, 18:18:12 | **63** | 52.1 / 52.1 | 24.3 / 15.6 W | 37 / 13 % | 51.8 / 51.8 |

**Verdict: they do not agree, and I cannot prove which is the CPU die temperature.** `0x0101` is 11 to 21 C above `TZ00`, and moves (73, 64, 63, 63
over a few minutes as load dropped) while `TZ00` stays within 1 C of 52. Likely reasons, most to least likely:

1. They are **different sensors**. `TZ00` is an ACPI thermal zone, which on most laptops is a board, skin or heat-pipe sensor with a slow response, and is
   what Windows uses for passive cooling; the EC value behind `0x0101` is probably a CPU package/die sensor that follows load quickly. The pattern (one
   fast, one slow and flat) fits that. **G**, but it is the only explanation that fits the data without assuming a faulty read.
2. The Acer value is an **EC-filtered or offset value** (some ECs report a margin or an averaged number). Not testable without disassembling the service.
3. The index-1 value is **not degrees C** at all. Unlikely: index 10 in the same method equals `nvidia-smi` in C to 0.4, and 63 to 73 is a normal idle-to-light-load
   package range for this CPU in Extreme mode.

What would settle it: a driver-based CPU package reader (needs an install, so it needs approval) or a short CPU load test showing `0x0101` climbing by tens of
degrees within seconds while `TZ00` lags. I did not run a load test (not asked, and it adds heat for no write-safety reason). **For a module: show `0x0101` as "CPU",
do not try to reconcile it with `TZ00`, and do not use `TZ00` as a CPU temperature.**

### 12.4 R2: `AcerGenericMethod.GetFunction(0x207)` answered status `0xE1`

- Raw `0x2E1`: low byte `0xE1` (225), byte 1 `0x02`. The UI's parse (`get_coolboost_state`, read again in B3) is: call `GetFunction(519)`;
  if `(raw & 0xFF) == 0` **and** `((raw >> 8) & 0xFF) == 1` then Cool Booster on, **otherwise off**. So for this answer the UI would say "off" with no error.
- **The UI makes this exact call itself.** `MainWindow_Clubman.initial_fan_page` (and every other variant) calls `get_coolboost_state` when the fan page is
  initialised, and shows the result on the Cool Booster toggle of `FanControlPage`. So `0x2E1` is almost certainly what the Acer UI also gets on this machine
  and silently turns into "off". **The UI cannot tell "off" from "not supported" or "error".** The UI's own cache has no Cool Booster value.
- The setter `set_coolboost_state` sends `SetFunction(7 | (state << 16))` and counts it a success when the low byte of the answer is 0. It is only called from
  the toggle's Checked and Unchecked events.
- What I could not establish: what `0xE1` means. No UI method compares an answer with 225, the service strings have no decoding for it, and the service logs only
  "WMI GetFunction input/output". Candidate readings, all **G**: (a) a "function not supported / invalid argument" code of the generic WMI method; (b) the answer's
  layout for this selector is different (status is not the low byte); (c) Cool Booster exists but its read needs another input on this model. Note that the
  answer echoes the input's byte 1 (`0x02`) in byte 1; that may or may not be meaningful.
- **Consequence: the Cool Booster read-back is unproven.** R3 (same method, a different input) and all other calls were not run, as instructed.
  W1 in `WRITE-PROPOSALS.md` says so.

### 12.5 Things this changes in earlier sections

- Section 3.3 / 6: `GetGamingSysInfo` fan speed indexes are **live and return plausible RPM**; the unit is still not independently proven. Rates read 0.
- Section 10: W1 (Cool Booster) loses its "read first" check; it can still be judged by fan RPM (indexes 2, 6, 9). The proposals are now in `WRITE-PROPOSALS.md`.
- Section 9 "Not verified": the sensor list there is now partly verified (12.2); R3, battery boost, `GetGamingMiscSetting`, `GetGamingLEDBehavior` remain unrun.
- A WMI **schema enumeration** from PowerShell (`ManagementClass.Methods` with `InParameters`/`OutParameters`) printed the first 12 methods (up to `SetGamingLEDBehavior`,
  id 10) and then produced nothing for 2 minutes; I stopped it. The next method to print would have been `GetGamingLEDBehavior` (id 11), but where exactly it hung is not
  established (output may have been buffered). The probe's own schema listing (names and ids only) works. That was a schema read, not a method call, but it is one more
  reason to give every WMI call in the host a timeout. Parameter names read before the stall: `AcerGenericMethod` uses `uiInput`/`uiOutput`, `AcerGamingFunction` uses
  `gmInput`/`gmOutput`; the Get methods take UInt32.

## 13. B4: the first hardware write (W4a, all fans Auto), owner gate `g-write-w4a`

Approval (owner, in the CSM app): exactly one call of `root\WMI:AcerGamingFunction.SetGamingFanBehavior` (id 14) with `gmInput` = `0x01410019`, once,
elevated. Nothing else: no repeat, no other value, no other `Set*`. Full proposal and the table of numbers: `WRITE-PROPOSALS.md`, "W4a result". Raw log:
`B4-w4a-run.txt` (this folder).

### 13.1 Tool

`tools/predatorsense-write` (C# 5, `csc.exe`, like the probe). Its write allow-list is a constant in code with **one entry** (W4a); the value cannot be passed on the
command line, only a key. Refusals were exercised without touching WMI (`--check`): the W4b value `0x02820019`, `SetFunction(0x10007)`, `GetGamingFanBehavior(0x19)` and
an unknown key are all refused. Every run appends timestamp, event, call, input, raw answer, status and elapsed time to a log. It refuses to write twice (the log
line `WRITE_ATTEMPT` blocks a second `--execute`; that guard was read, **not** exercised live, because testing it would risk a second write). Every WMI call runs on a thread with a
timeout (reads 10 s, the write 15 s); a timeout exits the process without a retry. The probe is unchanged and still read-only.

### 13.2 Sequence and result (2026-11-01, local 18:42:09 to 18:42:34)

| Step | Result |
| --- | --- |
| Preconditions | AC line on, battery 100 % (above 50 %), CPU load 11 % (limit 50 %); all checked by the tool before anything else |
| Schema | `gmInput` of `SetGamingFanBehavior` is UInt64 (checked, not coerced) |
| Baseline, same minute | `nvidia-smi` 52.69 C, 16.2 W, 22 %, P8; sysinfo 1, 2, 3, 6, 9, 10 three times, 2 s apart: CPU 57 to 61 C, CPU fan 2564 to 2586, system 44 to 46 C, GPU fan 1 2654, GPU fan 2 2663, GPU 52 C. All status 0 |
| **The write** | `SetGamingFanBehavior(0x01410019)`: `gmOutput` raw `0x0`, **status 0**, 11 ms, no exception |
| After, every 2 s for 20 s (11 passes of 2, 6, 9, 1, 10) | all status 0; CPU fan 2564 or 2586; GPU fans 2654 or 2678 and 2663; CPU 56 to 65 C; GPU 52 then 51 C. **No ALERT** (outside baseline -30 %/+30 %, or 90 C). `nvidia-smi` after: 51.53 C, 13.5 W, 0 % |
| Not done | any retry, W4b, W1, W2 or any other `Set*`; the elevated process exited by itself |

### 13.3 What this verifies, and what it does not

- **Verified (O):** the Set call format (class, method id 14, UInt64 `gmInput`) is accepted by the firmware with status 0 and returns at once. Writes can be made from an elevated
  .NET Framework process through `System.Management`, without the Acer pipe or the Acer app. The sensor reads (fan RPM, temperatures) keep working right after a write.
- **Verified (O):** restating Auto did not move the fans (RPM within one step of the baseline) or the temperatures during 20 s, which is the expected "no visible effect".
- **Not verified:** that the firmware mode was changed or re-asserted (the mode has no read-back, 11.2); that "Auto" was really the previous mode; whether `PSSvc` later re-applies its own cached
  mode; whether the state survives sleep or reboot; anything about the other fan values (Max, Custom), Cool Booster or MaxQ; what the owner saw or heard. A status of 0 means the method
  completed, not that the mode is what the module thinks.
- **Fan RPM in Auto jitters by one quantisation step** (about 22 to 24 RPM: 2564/2586, 2654/2678). It also drifts between sessions (B3 saw 2857, 3061, 3083 at a different load/temperature).
  Judge W4b by a jump of hundreds of RPM, not by a single step.
- `nvidia-smi` reports the fan speed as `[N/A]` on this machine, so sysinfo 2, 6, 9 are the only fan-speed reading.
- Section 12.5's remark that every WMI call should have a timeout stands; the write tool does that and nothing stalled in this run.

## 14. B5: the second hardware write (W4b, all fans Max for 14 s) and its undo (W4a), owner gate `g-write-w4b`

Approval (owner, in the CSM app): exactly two calls of `root\WMI:AcerGamingFunction.SetGamingFanBehavior` (id 14), once each, elevated, in this order: (1) `gmInput` =
`0x02820019` (all fans Max) for at most 15 s; (2) `gmInput` = `0x01410019` (all fans Auto) as the undo. No repeat, no other value, no other `Set*`. Abort rules: non-zero status,
no RPM rise within 10 s, any temperature of 90 C or more; then the undo at once. Full table of numbers: `WRITE-PROPOSALS.md`, "W4b result". Raw logs: `B5-w4b-run.txt` and
`B5-w4b-undo-run.txt` (this folder; one `WRITE_ATTEMPT` in each).

### 14.1 Tool change

`tools/predatorsense-write` got exactly one new allow-list entry, `W4b` (`0x02820019`), which carries `UndoKey = W4a`. `--call W4b --execute` does: preconditions (AC, battery above 50 %,
CPU load below 50 %, UInt64 schema, three baseline passes) -> the W4b write -> sysinfo 2, 6, 9, 1, 10 every 2 s for 14 s (3 s read timeout; a read that fails, times out or returns non-zero
status, a temperature of 90 C or more, or no fan rise of 300 RPM over the baseline maximum within 10 s ends the window at once) -> the undo W4a, which the tool sends itself and logs to a
**separate file** (opened and checked before the Max write, so the undo cannot be refused or fail to log afterwards) -> sysinfo every 2 s for up to 60 s until the three fans are inside the
baseline range (-30 %/+30 % of the baseline min/max) twice in a row; if they are not, or the undo status is not 0, the tool prints "press Auto in the Acer PredatorSense app" and exits with code 5.
The undo also runs when the Max write itself errors or times out (state unknown). Neither write is ever retried. Refusals were exercised with `--check` (no WMI): `0x02820018`, `0x03C30019`,
`SetFunction(0x10007)`, `GetGamingFanBehavior(0x19)`, `SetGamingMiscSetting(0x2005)` and an unknown key are refused; only the two approved values pass. The abort and exit-5 branches were
**not** exercised live (that would need a fault; the Max write ran only once).

### 14.2 Sequence and result (2026-11-01, local 20:21:44 to 20:22:07)

| Step | Result |
| --- | --- |
| Dry run first (`--call W4b`, no write, 20:21:29) | preconditions passed; baseline CPU fan 2608, GPU fans 2631 to 2654 and 2693 |
| Preconditions of the real run | AC on, battery 100 %, CPU load 24 % (limit 50 %, two other workers were active), UInt64 schema |
| Baseline of the real run (3 passes) | CPU fan 2586 to 2608, GPU 1 2631 to 2654, GPU 2 2693, CPU 62 to 75 C, system 40 to 41 C, GPU 50 C |
| **W4b** | raw `0x0`, status 0, 13 ms |
| Window t = 0 / +2 / +4 ... +14 s | t = 0 (about 16 ms after the answer): 2608 / 2631 / 2693 (baseline). From +2 s on, every pass: **5555 / 5882 / 5900** (CPU fan / GPU 1 / GPU 2). GPU 50 to 51 C then 49; CPU 62, 62, 62, 70, 76, 63, 58, 56 C |
| Abort rules | none fired; the window ended on its 14 s plan (14043 ms) |
| **W4a undo** | raw `0x0`, status 0, 13 ms |
| Recovery | +0 s 5555 / 5882 / 5900; **+2 s 2654 / 2631 / 2757**; +4 s 2884 / 2857 / 2929 (inside the baseline range, "back" at 4.0 s). `nvidia-smi` after: 49.3 C, 15.9 W, P8 |
| Not done | any retry, W1, W2 or another `Set*`; the elevated process exited by itself with code 0 |

### 14.3 What this verifies, and what it does not

- **Verified (O): the Set takes effect.** Max (`0x02820019`) raised all three fans from about 2600 to 2700 to 5555 / 5882 / 5900 on the Acer sensor (about +2900 to +3200 RPM, 2.1 to 2.2x), the value was flat
  for 12 s, and Auto (`0x01410019`) brought them back to the Auto range within 2 s. Both calls answered status 0 in 13 ms. The decoded table (low half = fan mask `0x19`, 2-bit mode per slot: 1 Auto, 2 Max) is
  therefore right for the fan mask and for modes 1 and 2 on this machine. The undo works without the Acer app.
- **Verified (O):** `GetGamingSysInfo` fan indexes 2, 6, 9 are a live, working check for a fan write; the temperatures stayed far below 90 C.
- **Not verified:** the fan **mode** (no read-back exists; it is inferred from RPM); the RPM unit (still a working assumption); that 5555 to 5900 is the fans' ceiling; the ramp time between 0 and 2 s;
  any cooling effect (CPU temperature followed load from other builds, so it says nothing about Max); noise or anything the owner saw or heard; whether `PSSvc` re-applies its mode later, or the state
  survives sleep or reboot; behaviour under load; Cool Booster (W1), MaxQ (W2), custom speeds. After the undo the fans were at 2857 to 2929 at +4 s, in the Auto range but 200 to 300 above the pre-write
  baseline (CPU 70 C then); whether Auto fully settled was not watched longer than that.
- The first read after a write (about 16 ms later) still shows the old RPM; a check must wait at least a second or two (2 s worked).

## 15. B7: the operation mode, decoded by static analysis (2026-11-03)

**Static only.** Nothing was called: no pipe connection, no WMI method, no PredatorSense action, no debugger or hook, no Acer DLL loaded into a process of
mine, no process started. What was read: the installed binaries as bytes, and state that Acer's software had already written (registry, ini files, PnP id).
Nothing Acer-made or decompiled is in the repo; below are names, ids, numbers and my own sentences.

### 15.0 Method, and the limit of it (read this first)

- **No native engine is installed.** `rea doctor` reports Hopper, Ghidra and IDA missing, there is no Java and no disassembler library. I did not install
  anything. Gate `g-c7f2ea3a5fd8` asks for a portable Ghidra 12.1.x plus a portable JDK 21 (or Hopper); **it was still unanswered when this section was written.** *(B9: the owner approved it on 2026-11-12 and B9 ran the independent Ghidra pass, section 15.8.)*
- So the native part was read with **my own small tools** (Python 3.13 standard library only, kept outside the repo): a PE reader (sections, imports, the
  `.pdata` function table), a string finder with cross-references (RIP-relative `lea`), and a decoder for the x86-64 subset MSVC emits, used to list the
  instructions of single functions with strings, imports and callees named. The managed UI was read with a metadata-only IL lister (also mine; it parses bytes and
  runs nothing).
- **Self-check of the decoder:** 1364 of the 1373 functions of `PSSvc.exe` decode to exactly their `.pdata` end with no unknown opcode (1537 of 1544 for
  `PSAdminAgent.exe`). The few that do not contain AVX (VEX) code or data; none is on a path below. That makes the instruction streams I quote consistent, but
  the decoder is not a vetted product. **A statement tagged C here means "I read that instruction sequence"; a Ghidra pass would be the independent check.**
- Function addresses below are RVAs inside `PSSvc.exe` (image base `0x140000000`) unless it says `PSAdminAgent`.

### 15.1 What the service does for command 53 (this machine) (C)

The pipe session loop (RVA `0xCB30`) reads a frame, then calls `table[command id]`; the table sits in `.rdata` and entry N is the handler of command N (checked
against the names of commands 9 to 53). Handler of 53 = RVA `0x1D260` (`kSvcCmdSetOperationMode`), of 52 = `0x1D110`, of 51 = `0x1CFC0`.

`set_operation_mode(mode)`, `mode` = the 32-bit argument (an argument longer than 4 bytes leaves `mode` at 0; **there is no range check**, any value is used):

| Step | What | Detail |
| --- | --- | --- |
| 1 | optional: admin agent, **Whisper mode** | only if `HKLM\SOFTWARE\OEM\PredatorSense\NV_WhisperMode20_Support` = 1 (here: 1). Opens `\\.\pipe\predatorsense_admin_agent_<active console session id>` and sends admin-agent command **35** (`kAdmAgtCmdSetNVWhisperMode`), one UInt32 argument = **1 if mode is 0 (Quiet), else 0**; reads the 9-byte reply, closes. Failures are logged only |
| 2 | **WMI call 1** | `root\WMI:AcerGamingFunction.SetGamingMiscSetting` (id 22), `gmInput` UInt64 = **`(mode << 8) \| 0x0B`**, output `gmOutput` UInt32 (wrapper RVA `0x24A00`: query `SELECT * FROM AcerGamingFunction`, input VARIANT type UI8, output coerced to UI4). **30 ms `Sleep` after a successful call** |
| 3 | level | `level` = 0 for mode 0; 1 for modes 1, 2, 3; 2 for mode 4; 3 for mode 5 (a mode above 5 gives 1). A failed WMI call 1 does **not** stop the handler: it logs "SetGamingMiscSettingBehavior is fail" and goes on |
| 4 | `gpu_set_oc_level(level)` (RVA `0x1D7A0`) | stores `level`; returns at once ("Not support GPU overclock") if the global GPU-OC flag is 0 (registry `Overclock\GPU_OC_Support` = 1 here). *(B9: the flag is an in-memory variable set once at service start, when this GPU's PnP id is found in the `[OC_GPU]` list of `PredatorSense.ini` (RVAs `0x14540`, `0x19020`); the registry value is a separate output of the same start-up code. It also skips the NvAPI part and WMI call 2. The runtime value was not observed; here the id is in the list, so it should be 1.)* |
| 4a | NvAPI path | if the NVIDIA flag (set in `write_gpu_name`; registry `Nvdia_GPU_Support` = 1 here) is 1: `nvidiaOC_set_oclevel(level)` (RVA `0x19CD0`): `NvAPI_Initialize`, `NvAPI_EnumPhysicalGPUs`, for each GPU `NvAPI_GPU_GetPstates20` (retry loop), **100 ms `Sleep`**, compute the shift between the current clock offset and the wanted one, `NvAPI_GPU_SetPstates20`, **100 ms `Sleep`**. Wanted core and memory offsets = ini values for that level x 1000 (kHz), see 15.2. Otherwise another vendor path (RVA `0x1A9F0`) is taken. *(B9, corrects two details: the 100 ms `Sleep` is only taken after a **failed** attempt of a try loop (up to 5 tries each for Get and Set), not after a successful call; and the value sent is the wanted offset itself (ini value x 1000), the current offset is only read and logged, so repeating a mode does not add up. It also needs `Intel_CPU_Support` = 1 in the registry (here 1) to take the ini branch.)* |
| 4b | gate check | `Operation_Mode_Support` is read from the registry (RVA `0x1A570`; the log says "skip oc fan table"; read-only). *(B9: read-only only because the value is 1 here. With any other value this function reads the fan table and, if its level differs from max(CPU level, GPU level) + 1, **writes** it with `SetGamingFanTable`. Not taken on this machine.)* |
| 4c | **WMI call 2** | only if `Feature.ini [OverclockDetial] GPUOCWMIRule` = 1 (here 1). Then, with `GPUOCWMINewRule` = 1 (here 1): `SetGamingMiscSetting` with **`(level << 8) \| 0x05`**, 30 ms `Sleep`. (With `GPUOCWMINewRule` = 0 the older rule uses the registry `Overclock\MaxQ_Mode` and the inputs `0x1005/0x1105/0x1205` (mode 1) or `0x2005/0x2105/0x2205` (mode 2); not taken here) |
| 5 | reply | 9 bytes, argument = **0xFFFFFFFF in every case** (the slot is initialised to -1 and never written). The UI discards it. **There is no success/failure signal to the caller** |

Not in this path: no CPU overclock call (`cpu_set_oc_level`, Intel SDK/XTU), no power-plan call (the service imports only `IsPwrSuspendAllowed` from
`POWRPROF`), no registry or ini **write** (the listed functions import `RegGetValueW`, `GetPrivateProfileStringW` and no writer), no check of AC, battery,
GPU mode or the mode value. The service has no `PowerSetActiveScheme`, and `PSAdminAgent` only reads the active scheme.

**The six modes on this machine** (service view; `Feature.ini [OperationModeSupport]` offers only Quiet, Normal, Extreme, Turbo, so the UI never sends 2 or 3 here):

| Mode (id) | Whisper cmd 35 arg | WMI call 1 (`SetGamingMiscSetting`) | level | NvAPI core / memory offset (this GPU, from the ini, 15.2) | WMI call 2 |
| --- | --- | --- | --- | --- | --- |
| Quiet (0) | 1 | `0x0B` | 0 | 0 / 0 | `0x05` |
| Default (1) ("Normal" in the ini and on the button) | 0 | `0x10B` | 1 | 0 / 0 | `0x105` |
| Render (2) | 0 | `0x20B` | 1 | 0 / 0 | `0x105` |
| Edit (3) | 0 | `0x30B` | 1 | 0 / 0 | `0x105` |
| Extreme (4) | 0 | `0x40B` | 2 | 0 / 0 | `0x205` |
| Turbo (5) | 0 | `0x50B` | 3 | **+100 / +360** | `0x305` |

Order inside the service: 1 (if enabled), WMI call 1, NvAPI, WMI call 2. Total time per command is roughly 30 + NvAPI work + 30 ms when every NvAPI call succeeds at once (100 ms more per failed attempt, B9), plus the pipe
and WMI latency; the UI then waits 2 s before refreshing (15.2).

**Selectors of `SetGamingMiscSetting`/`GetGamingMiscSetting`, as the service uses them** (low byte = selector, the next bits = value): `0x05` GPU OC level,
`0x07` CPU OC level, `0x0B` **operation mode**, `0x02` GPU mode (UI), `0x01`/`0x101` battery-boost pair (UI). Reads: `0x07` CPU OC support by BIOS
(`check_cpu_oc_by_bios_support`, value in bits 8 to 15), `0x09` discrete-GPU capability, `0x0A` **operation-mode capability**, `0x0B` **operation mode**.

### 15.2 What the Acer UI adds around it (Clubman window, `Feature.ini` type 8) (C)

`OperationMode_Button.OC_Mode_Checked` (the only caller of the UI's `set_operation_mode`; ignored while `App.initial_flag` is set):

1. UI display only: shows which parts are "boosted": Edit (3) = CPU, Render (2) = GPU, Extreme (4) and Turbo (5) = both, Quiet and Default = none. This is a label of
   the UI, not a call.
2. Starts a thread: `set_operation_mode(mode)` -> pipe 53 with one UInt32 (result discarded), `Sleep(1000)`, `Sleep(1000)`, then refresh of the page.
3. **Meanwhile, without waiting for the thread**: calls the main window's `Change_home_operation_mode_profile`, **writes `HKLM\...\Overclock\CurrentOperationMode` = mode** (so the
   cache is written whether or not the service succeeded), updates the GameSync ini of the active profile if one is active, and shows/hides the NVIDIA Whisper status when
   `NV_WhisperMode20_Support` = 1.
4. **Turbo branch (needs all of: mode 5, `FanControl\CurrentFanMode` = 1 (Max), AC power, "battery boost" on):** sets `FanControl\User_change` = 0, sends pipe 13 `SetGamingLEDBehavior(0x10001)`
   and pipe 32 `SetGamingMiscSetting(0x101)`, then posts message `0x8001` (wParam 11, lParam 1) to the `PSAgent` window (OSD). **Other modes:** reads pipe 14
   `GetGamingLEDBehavior(1)`; if status is 0 and `(raw >> 8) & 3 == 1` it undoes the pair with `SetGamingLEDBehavior(1)` and `SetGamingMiscSetting(1)` and posts `0x8001` (11, 0).
   (Variants with `0x410001`/`0x400001`, bit 14 instead of bit 8, are for machine type 3.) Meaning of the pair stays **G** (boost-related).
5. The earlier note "also tells the admin agent (command 34) to change the lighting accent" (3.3, 11.3) is **only for machine types 9 and 10** (XC90, Evoque); type 8 skips it (C).

Per-GPU offsets the service applies in 4a come from `C:\ProgramData\oem\PredatorSense\PredatorSense.ini` (Acer-written, **O**). Section named after the GPU PnP id; this
machine's GPU is `PCI\VEN_10DE&DEV_2520&SUBSYS_15321025` (**O**, `Get-PnpDevice`) and that section exists:

```
[PCI\VEN_10DE&DEV_2520&SUBSYS_15321025]   Quiet=0  Default=0  Extreme=0  Turbo=100
                                          RAM_Quiet=0  RAM_Default=0  RAM_Extreme=0  RAM_Turbo=360
```

The service reads the keys `Quiet`, `Default`, `Extreme`, `Turbo` (core) and `RAM_Quiet`, ... `RAM_Turbo` (memory) into level slots 0 to 3 (C, the stores of RVA `0x14540`).
The unit is "value x 1000 = kHz" in the NvAPI call (C); what that means in MHz as GPU tools show it is **G** (core +100 MHz looks right; memory +360 is the NvAPI-side number).

### 15.3 Other writers of the mode (the service is not the only one) (C unless said)

Command 53 is also sent by **`PSAdminAgent.exe`** (user session, elevated, runs from the service folder). The service's caller check is a path check (15.6), and the agent's path is on its list.

| Trigger | What the agent does |
| --- | --- |
| **Turbo key** (`Press_Turbo_key_Operating_Mode`, `Turbo_mode_switch_Operating_Mode`) | acts only if (`Operation_Mode_Support` or the GPU-OC flag is set), both lock flags are clear, the AC status check passes **and** battery boost reads on; it then sets the locks and calls the switch. Mode = 5 when the key turns Turbo on, 1 when off. **UI running:** posts window message `0x8012` (wParam = mode) to the UI, which then does its own pipe 53. **UI not running:** (if the GPU-OC flag is set) sends pipe 53 itself, writes `Overclock\CurrentOperationMode`, `ProfilePool\currentsetting.ini [OperationMode] currentoperationmode`, and then **`SetGamingFanBehavior`: `0x02AA001F` (all fans Max) when Turbo goes on, `0x0155001F` (all Auto) when it goes off** (mask `0x1F` = all five fan slots, 2-bit mode 2 or 1 per slot), writes `FanControl\CurrentFanMode` = 1/0 and the ini, and posts the OSD message to `PSAgent` |
| **Power source change** (AC/DC/PD), **low-battery state change**, **resume / Modern Standby** (`DoOCFunction`; the resume handler and the message loop start `Reset_all_settings` (RVA `0x434A0` in the agent) as a function pointer, and it reads registry `Force_reset`, here 1; how that value gates it was not traced) | calls **`reset_Operation_Mode`** (RVA `0x50500` in the agent): reads AC status and the battery-boost flag (`GetGamingSysInfo` input 2 path). If the flag reads 0 it writes `CurrentOperationMode` = 1. **If AC and boost are on it re-sends the cached mode, otherwise it sends pipe 53 with 1 (Default).** `Reset_all_settings` also touches `CurrentCPUMode`, `CurrentGPUMode`, `CurrentFanMode` (back to Auto) and the GameSync `ActiveIndex` (value names read in the code; exact values written **G**) |
| GameSync profile apply ("Do set GameSync Profile"), "Do restore settings" | call `reset_Operation_Mode` too |

**The service itself does not react to power or session events**: `OnPowerEvent` and `OnSessionChange` (RVAs `0xA480`, `0xA5A0`) only write log lines (C). The re-applying
that FINDINGS 5 guessed at is done by the agent, not the service. The UI on its side only greys out the buttons on DC, USB-PD, or AC without battery boost
(`Disabled_OverClocking`: shows Default **without sending anything** and without writing the cache); on AC with boost it restores `BeforeUnpluggedOperationMode` in the UI (`Enabled_OverClocking`).
The user-facing texts in `PSAgent.exe` say overclocking is stopped below 30 % battery and is available only with the adapter plugged in and at least 40 % battery left (strings, C);
the comparison constants in `PSAdminAgent` were not decoded.

### 15.4 What is persisted, and where (O for values, C for who writes)

| Place | Written by | Value now (2026-11-03) |
| --- | --- | --- |
| `HKLM\SOFTWARE\OEM\PredatorSense\Overclock\CurrentOperationMode` | UI on every click; agent on Turbo key / reset | **1** (it was 4 during B1 to B5) |
| `...\Overclock\CurrentCPUMode`, `CurrentGPUMode` | UI overclock pages, agent resets | 0, 0 |
| `...\FanControl\CurrentFanMode`, `User_change` | UI, agent | 0, 0 |
| `...\PredatorSense` flags the mode path reads: `Operation_Mode_Support`, `NV_WhisperMode20_Support` / `_Status`, `Nvdia_GPU_Support`, `Force_reset`; `...\Overclock`: `GPU_OC_Support`, `CPU_OC_Support`, `CPU_PL_Support`, `MaxQ_Mode` | service start-up detection (`Operation_Mode_Support` comes from an SMBIOS-based check, RVA `0x1D670`, run at start) | 1; 1 / 0; 1; 1; 1, 0, 0, 2 |
| `C:\ProgramData\oem\PredatorSense\ProfilePool\currentsetting.ini` `[OperationMode] currentoperationmode` | agent | **1**, file last written **2026-11-02 21:11** (UTF-16) |
| `PredatorSense.ini`, `Feature.ini`, `HW_Support.ini` | not written by a mode change; read by the service | see 15.2 and `Feature.ini` (Quiet/Normal/Extreme/Turbo = 1, `PowerLimit` = 0, `GPUOCWMIRule` = 1, `GPUOCWMINewRule` = 1) |

The service writes none of them in the mode path. The cache changed from 4 to 1 between the B5 run (2026-11-01 20:22) and 2026-11-02 21:11 without anything of mine touching it. That all
cache values now equal the reset values (mode 1, fan 0, CPU 0, GPU 0) fits the agent's reset on resume/start (**G**, not proven; I do not know who triggered it).

### 15.5 How a mode can be read

| Source | Exists? | Notes |
| --- | --- | --- |
| **Firmware: `GetGamingMiscSetting` (id 23) with input `0x0B`** | **yes in the service** (pipe 52 handler `0x1D110`), never used by the UI | C: the service sends exactly `0x0B` and returns the raw 64-bit answer to the pipe caller without parsing it (so the layout is unknown to me). By analogy with the selectors it parses itself (`0x07`: value in bits 8 to 15; the GPU-mode read in 3.3) the mode is probably in bits 8 to 15 with the status in the low byte (**G**). **Run once in B10 (2026-11-12): answer `0x0B0100`, status 0, byte 1 = 1 = Default, matching the cache (section 15.9)** |
| **Firmware: `GetGamingMiscSetting` input `0x0A`** | yes (pipe 51 handler `0x1CFC0`), never used by the UI | "operation-mode capability"; layout unknown. **Run once in B10 (2026-11-12): answer `0x0B3300`, status 0, byte 1 = `0x33`, bits 0, 1, 4, 5 set = Quiet, Default, Extreme, Turbo (section 15.9)** |
| Registry `Overclock\CurrentOperationMode` | yes | the UI's and agent's record, written on click even if the service failed; **not** updated by the UI's unplug fallback; not live state |
| `ProfilePool\currentsetting.ini` | yes | agent's record |
| NvAPI P-state offsets | yes, but it needs NvAPI, not WMI | would show only the GPU part; 0 in every mode except Turbo here |
| Windows power plan | no | the mode does not touch it |

So a **firmware read exists** (`0x0B`), but it is untraced in the UI. Under the project rule (a call is only run if its input is traced and the owner approves) it needs its own gate; it is traced
to the **service's own handler**, not to the UI. The risk is the usual one: the MOF flags every method read and write, but the service itself sends this input as a "get".

### 15.6 Dangerous parts, and what a direct WMI call would skip

- **What the firmware does for selector `0x0B` is not visible in any Acer binary** (it is the BIOS/EC). The Acer UI calls Extreme and Turbo "overclocking"; the safe assumption is higher CPU
  and GPU power, hotter and louder. The CPU/GPU split shown by the UI (15.2 item 1) is only a label.
- **Turbo adds an NvAPI offset: GPU core +100, memory +360** (this machine's ini), applied by the service through `SetPstates20`. A direct WMI `0x50B` does **not** do this; Acer's Turbo = WMI + NvAPI + WMI.
- **Fan overrides**: the mode path of the service touches no fan setting. Fans change through (a) the Turbo **key** when the UI is closed (`0x02AA001F` all Max / `0x0155001F` all Auto,
  mask `0x1F`), (b) the UI Turbo branch pair `0x10001` + `0x101` (meaning guessed), (c) the firmware itself.
- **Power limits:** no CPU power-limit write in this path; this machine has `PowerLimit` = 0 and `CPU_PL_Support` = 0 in the Acer files. Whether the firmware changes power limits for a mode is unknown.
- **Acer's guards are not in the WMI call**: the AC/battery rules (30 %, 40 %) live in the agent and UI (15.3); a direct `SetGamingMiscSetting` has none of them. The agent may also reset the mode to Default
  after a power-source change or resume and so undo a Rigbay-set mode (C that it can, **G** how often).
- **Enforced by the service, not reproducible with direct WMI:** (1) the **caller check on the pipe**, which only matters for the pipe: the check is `IsAllowedWin32App`/`IsAllowedUwpApp` (RVAs `0xC7A0`, `0xC4A0`),
  taking the client's process id with `GetNamedPipeClientProcessId`, its image path with `QueryFullProcessImageNameW`, and comparing with the service folder + one of six file names
  (`PredatorSense.exe`, `PSAdminAgent.exe`, `PSAgent.exe`, `PSMobile.exe`, `PSLauncher.exe`, `PSSvc.exe`) or the `WindowsApps\AcerIncorporated.PredatorSenseV30_*\Win32\PredatorSense.exe` path, or the UWP package family
  `AcerIncorporated.PredatorSenseV30_48frkmn4z8aw4`. There is **no signature or hash check** (no `WinVerifyTrust`/crypt import). It is a path gate; Rigbay still must not use the pipe.
  (2) NvAPI offsets and NVIDIA Whisper mode (admin agent) are not WMI. **(3) I found no firmware handshake, token or sequence number in the WMI calls themselves:** the service builds the input from the mode, calls the method and sleeps 30 ms. What the firmware accepts is untested.

### 15.7 What I could not decode or verify

- The firmware semantics of selectors `0x0A`/`0x0B` (reply layouts, what each mode changes in BIOS/EC), and whether Render (2), Edit (3) or a value above 5 is accepted by this firmware.
- The SMBIOS (type 172) capability parse in RVA `0x1D670`: purpose and registry output read, parsing not decoded. *(B9: it reads the raw SMBIOS tables through the read-only WMI class `MSSMBios_RawSMBiosTables`, parses the type 172 list in `0x25850` and returns 1 when the entry with number 0x0F has value 1; the byte layout of the table was not decoded.)*
- `PSAdminAgent`: the comparison constants for the low-battery rule, the source of its "low battery" flag (a `GetGamingSysInfo`-style read, RVA `0xA1C0` there), what `Reset_all_settings` writes exactly, the Turbo-key event decoder, and what the UI does on message `0x8012` besides the usual pipe 53.
- Other families of profile keys in RVA `0x14540` (`Normal/Faster/Boosted/Turbo`, XTU `.xtu` files, CPU power-limit profiles): they are for other models or CPU overclock, which is not supported here; the mode handler does not reach them on this machine.
- Nine functions of `PSSvc.exe` (AVX code): not read; none is on the paths above. The decoder is my own (15.0).
- Whether the firmware keeps a mode across reboot, sleep or unplug. Nothing was run, so every effect statement above is about code, not about observed behaviour.

### 15.8 Independent check with Ghidra (task B9, 2026-11-12; static only)

**Verdict: B7's decode of the operation mode is confirmed.** Every value, the order of the calls, the level mapping and the "nothing else happens" list were found again by a different tool. Four details of
section 15 were wrong or too loose and are fixed in place (marked *B9*): the GPU-OC gate (15.1 row 4), the 100 ms sleeps and the absolute NvAPI offset (row 4a), the fan-table side of the gate check (row 4b), the total time.
Nothing was called: no pipe, no WMI method, no PredatorSense action, no debugger, no process of Acer's started, no Acer DLL loaded into a process of mine (Ghidra reads the file as bytes).

**Tool and install (approved by the owner on 2026-11-12, in chat).** Portable Ghidra 12.1.4 (official NSA GitHub release zip `ghidra_12.1.4_PUBLIC_20260921.zip`) and portable Temurin JDK 21.0.13+9 (zip), both extracted into a new folder
`D:\Documents\Projects\_tools\ghidra-scratch` outside the repo; `JAVA_HOME` and `GHIDRA_INSTALL_DIR` were set inside the batch file that ran each step only; heap capped at 4 GB (`GHIDRA_MAXMEM=4G`); no installer, PATH, registry, service or UAC change.
SHA-256 checked against the publishers' own values, both **matched**: Ghidra `ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db` (the release notes and GitHub's asset digest give the same string); JDK `a4dbaa05fca4bd89d84584e58c459bd732efebb1722af80ab13c6516832004f9` (Adoptium's
API checksum for that file). Files analysed, in place: `PSSvc.exe` (SHA-256 `55c9077f31ef0cbf4826762b2132229f924733dd016a64cc9d3f52b27348ea1d`) and `PSAdminAgent.exe` (`7c4d6bc1627bccb461ab7836f3a9fdf9364ebac29e92cd2eaf483c1be994d70e`), both from `C:\Program Files\Acer\PredatorSense Service`. Headless auto-analysis of `PSSvc.exe` took 41 s; Ghidra lists 1402 functions in it
(B7's decoder counted 1373 in `.pdata`; the two counts are not the same thing). I used the decompiler and a disassembly dump through small scripts of mine (string cross-references, decompile, listing, who-calls-whom); they and every output stay in the scratch folder. No Acer code or listing is in the repo.

**Order of work.** I wrote my own reading of the handler into a scratch note before I opened section 15, and compared afterwards. One slip of mine in that reading: I cut a `reg query` listing after 30 lines and first believed
`NV_WhisperMode20_Support` was absent; the full listing shows it is 1, as B7 said.

**My reading in short (C unless noted; RVAs inside `PSSvc.exe`).** Pipe loop `0xCB30`: a command id above 0x35 is dropped; otherwise it calls entry N of a pointer table at `0x69AD0`; entry 53 is at `0x69C78` and holds `0x1D260`. The caller check runs for every received frame before the dispatch.
Handler `0x1D260`: (1) mode = first 4-byte argument; (2) if `NV_WhisperMode20_Support` = 1, admin-agent pipe command 0x23 (= 35) with one UInt32 = (mode == 0); (3) `SetGamingMiscSetting` with `(mode << 8) | 0x0B`, output only logged; (4) `level` = 0, 2, 3 for modes 0, 4, 5, else 1; (5) `0x1D7A0(level)`;
(6) a reply of one 4-byte argument that is always `0xFFFFFFFF`. `0x1D7A0`: gate flag, in-memory level, NvAPI Get/Set P-states with ini offsets x 1000, the `Operation_Mode_Support` check, then `SetGamingMiscSetting((level << 8) | 5)` under the two `Feature.ini` rules.

#### 15.8.1 Every B7 claim, one by one

Verdicts: **agree**, **agree, detail corrected**, **could not check** (and why). Evidence is a function address (RVA) and a tag.

| # | B7 claim (section) | Verdict | Evidence (Ghidra) |
| --- | --- | --- | --- |
| 1 | The pipe loop `0xCB30` calls `table[command id]`; 53 = `0x1D260`, 52 = `0x1D110`, 51 = `0x1CFC0` (15.1) | **agree** | Loop `0xCB30` rejects ids above 0x35 and calls through a table at `0x69AD0` (8-byte entries): entry 53 (`0x69C78`) = `0x1D260`, 52 = `0x1D110`, 51 = `0x1CFC0`; the entry for id 32 is the handler that logs `kSvcCmdWMISetGamingMiscSetting`. C. So 53 is the highest command the loop accepts |
| 2 | `mode` = the 32-bit argument, a longer argument leaves it 0, no range check (15.1) | **agree** | `0x1D260`: the length field before the argument is tested against 5; the value goes into the shift and the level test unchanged. C |
| 3 | Step 1: Whisper mode via the admin agent, only with `NV_WhisperMode20_Support` = 1 (here 1), command 35, UInt32 = 1 only for mode 0, 9-byte reply read, failures only logged (15.1) | **agree** | `0x1D260`: registry value read (DWORD), pipe name `predatorsense_admin_agent_` + active console session id, send of id 0x23 with one 4-byte argument = (mode == 0), `ReadFile` of 9 bytes, nothing checked. It runs **before** the WMI call. C. The registry value is 1 on this machine (O, re-read by B9) |
| 4 | Step 2: `AcerGamingFunction.SetGamingMiscSetting` (id 22), `gmInput` UInt64 = `(mode << 8) \| 0x0B`, output `gmOutput` UInt32, wrapper `0x24A00`, query `SELECT * FROM AcerGamingFunction`, UI8 in, UI4 out, 30 ms sleep after a success (15.1) | **agree** | Input built at `0x1D54C` (`shl 8`, `or 0xB`), call at `0x1D59D` into `0x24A00`; `0x24A00` passes the names `AcerGamingFunction`, `SetGamingMiscSetting`, `gmInput`, `gmOutput` and the query to the generic exec helper `0x238B0` (COM `ExecMethod` on the first instance), `VariantChangeType` to type 0x15 in and 0x13 out, `Sleep(0x1E)` on the success path only. C |
| 5 | A failed call 1 does not stop the handler; the output is not interpreted (15.1) | **agree** | `0x1D5A2` to `0x1D5ED`: the result only chooses between two log lines; no branch on `gmOutput` anywhere in `0x1D260` or `0x1D7A0`. C. So "status 0 = ok" for Misc calls is B7's and the UI's convention, not something the service checks: **G** for this call |
| 6 | Level: 0 for mode 0, 1 for 1 to 3, 2 for 4, 3 for 5, 1 above 5 (15.1) | **agree** | `0x1D5FB` to `0x1D616`. C |
| 7 | Step 4: `gpu_set_oc_level` returns at once when the global GPU-OC flag is 0, the flag being registry `GPU_OC_Support` (15.1) | **agree, detail corrected** | `0x1D7A0` tests an in-memory variable, not the registry. It is set once at service start (`0x14540`, store at `0x17199`) when the GPU's PnP id is found in the `[OC_GPU]` list of `PredatorSense.ini` (finder `0x19020`); the registry `Overclock\GPU_OC_Support` is a separate output of the same start-up code (written at `0x1832A` and `0x183BE`). When the variable is 0 the NvAPI part and WMI call 2 are skipped too. The runtime value cannot be seen statically: the id is in the list here (O: this GPU is `DEV_2520`, the list has it), so it should be 1. C for the code, G for the runtime value |
| 8 | Step 4a: NvAPI path when the NVIDIA flag is 1; Initialize, EnumPhysicalGPUs, Get/Set P-states 20, retry loops, 100 ms sleeps, shift computed from the current offset (15.1) | **agree, detail corrected** | `0x19CD0` (called from `0x1D861` when the flag at `0x82E64` is 1; set at `0x19AC9` by the start-up probe `0x19890` that enumerates NvAPI GPUs; registry `Nvdia_GPU_Support` = 1 here, O). Per physical GPU: Get P-states (try loop, 5 tries), then Set P-states (try loop, 5 tries). NvAPI function ids called through the query-interface thunk: `0xE5AC921F` (enumerate GPUs), `0x6FF81213` (Get P-states 20), `0x0F4DAE6B` (Set P-states 20), plus `0x2D43FB31` before each Get and Set (purpose not identified); the names are the public NvAPI table's as I remember it, **G**. **Corrections:** the 100 ms sleep (`0x19F96`, `0x1A1C8`) is taken only after a failed try, not after a success; the Set structure carries `ini value x 1000` as the wanted offset (one P-state, a core and a memory entry), the "current" clock is read and only logged, so there is no accumulation. The structure's field names are not confirmed (read as offsets). C |
| 9 | Ini keys `Quiet/Default/Extreme/Turbo` (core) and `RAM_*` (memory) fill level slots 0 to 3; unit x 1000 = kHz; this GPU: 0/0/0/+100 and 0/0/0/+360 (15.2) | **agree** | Stores in `0x14540`: core slots 0 to 3 at `0x17664`, `0x17714`, `0x177C4`, `0x17874`; memory slots at `0x17924`, `0x179D4`, `0x17A84`, `0x18057`; factor 1000.0 at `0x74928` read by `0x19CD0`. This key family is the branch for a machine type above 7 (type 8 here, O); machine types up to 7 use other key names (`Normal`, `Faster`, `Turbo`, with a separate branch for types 4 and 5). The branch also needs `Intel_CPU_Support` = 1 (here 1, O). The `PredatorSense.ini` section of this GPU (O): `Quiet=0 Default=0 Extreme=0 Turbo=100`, `RAM_*` = 0, 0, 0, 360. C |
| 10 | Step 4b: the `Operation_Mode_Support` check is read-only; the log says "skip oc fan table" (15.1) | **agree, detail corrected** | `0x1A570` returns at once when the value is 1 (here 1, O; the log line is written on that path). With any other value it reads `GetGamingFanTable` and, if the level differs from max(CPU, GPU level) + 1, **calls `SetGamingFanTable`** (RVA `0x24250`). Not taken here; a machine where the value is not 1 would write a fan table in the same command. C |
| 11 | Step 4c: WMI call 2 with `GPUOCWMIRule` = 1 and `GPUOCWMINewRule` = 1 sends `(level << 8) \| 0x05`; the older rule (`NewRule` = 0) uses `MaxQ_Mode` and the inputs `0x1005..0x1205` / `0x2005..0x2205` (15.1) | **agree** | `0x1D7A0`: the `Feature.ini [OverclockDetial]` keys are read at call time; with both 1 the input is `0x05`, `0x105`, `0x205`, `0x305` for levels 0 to 3; with `NewRule` = 0, `MaxQ_Mode` 1 gives `0x1005/0x1105/0x1205` and 2 gives `0x2005/0x2105/0x2205`; a `NewRule` other than 0 or 1 returns. Extra: in the `MaxQ_Mode` branches level 3 has no case and would send input 0 (not taken here; `Feature.ini` is 1 and 1, O). Same wrapper, output only logged, 30 ms sleep. C |
| 12 | The reply is 9 bytes with `0xFFFFFFFF` in every case (15.1) | **agree** | The reply slot is set to -1 at `0x1D2BA` and never written again; sent by `0xD140` with count 1 and size 4. C |
| 13 | Not in the path: no CPU OC call, no power-plan call, no registry or ini write, no check of AC, battery, GPU mode or the mode value (15.1) | **agree, with one edge** | The callee lists of `0x1D260`, `0x1D7A0`, `0x1A570`, `0x19CD0` hold no power or battery function; `PSSvc.exe` imports only `IsPwrSuspendAllowed` from `POWRPROF`, no `GetSystemPowerStatus`, and the strings `PowerSetActiveScheme` and `CurrentOperationMode` are not in the file (C). The registry helper `0xB9E0` is a DWORD **writer**; the handler, `0x1D7A0` and `0x1A570` call it only as a repair when a value exists but is not a DWORD (it writes 0). Not taken here: all three values are `REG_DWORD` (O). Otherwise only reads |
| 14 | Selectors as the service uses them: `0x05`, `0x07`, `0x0B`, `0x09`, `0x0A` (15.1) | **agree** | `0x1D110` sends `GetGamingMiscSetting(0x0B)` and `0x1CFC0` sends `(0x0A)`; start-up `0xBC40` reads `(9)` and tests bit `0x400`; `0x184E0` reads `(7)` and uses the byte next to the status for the CPU-OC answer. C |
| 15 | The service has a firmware read of the mode (`0x0B`), never used by the UI, layout unknown (15.5) | **agree** | `0x1D110` returns the raw 64-bit answer to the pipe caller (the 8-byte reply holds `0xFFFFFFFF` when the call failed) and does nothing with it. See 15.8.2 item 4. C |
| 16 | The caller check is a path check with no signature check (15.6) | **agree; the list of file names not rechecked** | `0xC4A0` and `0xC7A0` (strings for the UWP package family and the `WindowsApps` path, `GetNamedPipeClientProcessId`, `QueryFullProcessImageNameW`, the service's own folder by `GetModuleFileNameW`); no `WinVerifyTrust` or crypto import. The check runs per received frame (`0xCB30`), before the dispatch. I did not enumerate the six file names. C |
| 17 | The service does not react to power or session events (15.3) | **could not check** | `0xA480` and `0xA5A0` were not opened. Not needed for the mode path |
| 18 | `PSAdminAgent` is what resets the mode on power-source change, low battery, resume (15.3) | **agree for the reset function and its triggers** | `PSAdminAgent.exe` `0x50500` (`reset_Operation_Mode`): the battery-boost and AC helpers are called; if boost reads false it writes `CurrentOperationMode` = 1; with AC and boost it re-sends the cached value, otherwise a value that starts as 1, in both cases as service command 0x35. Callers: `0x9A50` (low-percentage check), `0x9CB0` (power-source change), `0xEC90` (GameSync apply), `0x11780` (restore), `0x434A0` (`Reset_all_settings`). The log strings `ModernStandby Do_Reset_all_settings` and `PBT_APMRESUMEAUTOMATIC` sit in the callers of `0x434A0`. C. The Turbo-key function (`0x4EE40`) also sends 0x35 and the fan values `0x02AA001F` and `0x0155001F` occur as immediates in the file; I did not trace each use |
| 19 | The agent's numbers for the low-battery rule, `Force_reset` gating, what `Reset_all_settings` writes (15.7) | **could not check** | not opened (outside the five targets of B9) |
| 20 | The UI side of 15.2 (OperationMode_Button, cache write, Turbo branch) | **could not check** | managed code, not a Ghidra target; B7's IL reading stands unchanged |
| 21 | 15.4 values and who writes them | **agree for the service** | the service writes none of them in the mode path (item 13); registry and ini values re-read by B9 are as in 15.2 and 15.4 (O) |
| 22 | The decoder is home-made and not a proven tool (15.0) | n/a | on the five targets Ghidra's output gives the same immediates, strings and call order, so the decoder's reading of this path held up |

#### 15.8.2 The five targets of B9

1. **Command 53 handler: WMI calls, method ids, values, order, delays, errors.** Two `SetGamingMiscSetting` (id 22) calls, both through `0x24A00`: `(mode << 8) | 0x0B`, then `(level << 8) | 0x05` (`NewRule` = 1). Between them an NvAPI step. The admin-agent Whisper command comes first when enabled. A 30 ms
   `Sleep` follows each successful WMI call. No error is handled: neither WMI output is compared with anything, and the reply to the pipe caller is the constant `0xFFFFFFFF`. No other `AcerGamingFunction` or `AcerGenericMethod` method is called from this path. (A start-up routine elsewhere reads `GetMisc(9)` and `(7)` and the SMBIOS table.)
   Per mode on this machine: Quiet `0x0B` then `0x05`; Default, Render, Edit `0x10B`, `0x20B`, `0x30B` then `0x105`; Extreme `0x40B` then `0x205`; Turbo `0x50B`, NvAPI core +100 and memory +360, then `0x305`. Identical to the table in 15.1.
2. **GPU step.** Read: `Feature.ini [OverclockDetial] GPUOCWMIRule` and `GPUOCWMINewRule` (both 1); the `PredatorSense.ini` section of this GPU's PnP id; registry `Intel_CPU_Support`, `Nvdia_GPU_Support`, `Operation_Mode_Support`, and `Overclock\MaxQ_Mode` (old rule only). NvAPI: Get then Set P-states 20 per GPU with the ini offsets x 1000. WMI: `(level << 8) | 0x05`.
   Level per mode 0 to 5: 0, 1, 1, 1, 2, 3. Confirmed.
3. **What the service persists, reads back, checks, triggers.** It persists nothing in this path (item 13; one latent repair write, not taken). It reads back nothing: no `Get` call after the writes, no NvAPI read-back beyond the Get used to build the Set. Checks: only the config switches above; no AC, battery, GPU-mode or value check.
   Triggers: the admin agent's Whisper command (when enabled) and nothing else: no power plan, no `PSAdminAgent` start, no fan write (except the old-rule fan table on a machine whose `Operation_Mode_Support` is not 1).
4. **Layout of the `GetGamingMiscSetting` reply for selector `0x0B`.** **No code parses it.** In `PSSvc.exe` the only reader of `0x0B` is the pipe-52 handler `0x1D110`, which hands the raw 64-bit value (or `0xFFFFFFFF` on failure) to its caller. The UI never sends command 52 (B2, B7).
   `PSAdminAgent.exe` never sends command 51 or 52 either: its calls to the service use ids 5, 28, 39, 40, 45 and 53 (read as constants at the call sites). The agent calls WMI itself; its `GetGamingMiscSetting` calls use the selectors 3 and 6 as constants, and four more call sites pass a variable
   that I did not resolve (all in the battery-boost and reset functions); there is no constant `0x0A` or `0x0B`. So the layout of `0x0B` (and of `0x0A`) is **unknown from any Acer binary**, and the guess "mode in bits 8 to 15, status in the low byte" stays **G**. What is code-backed about the answer: it is a 64-bit value, and the service itself does not look inside.
5. **What makes a direct WMI call differ from the service.** (a) **No caller check applies to WMI**: the check is on the pipe only, and there is no signature, token or sequence in the WMI calls; I found no handshake or capability call before the Set. The wrapper opens COM and connects to `root\WMI` for every call and sleeps 30 ms after it.
   (b) The service's other parts that a direct call lacks: the **Whisper command** (this machine has it enabled; Quiet turns it on, every other mode turns it off), the **NvAPI offsets** (Turbo +100 / +360; the other modes **reset them to 0**, so a WMI-only change after an Acer Turbo leaves the GPU offset where it was), WMI call 2 (GPU level), the
   cache write and the OSD by UI and agent, Acer's AC and battery guards, and, on a machine whose `Operation_Mode_Support` is not 1, a fan-table write. (c) The **agent can undo** a direct mode (item 18). (d) The service gives no success signal to anybody, so for a direct call the WMI `gmOutput` is the only signal, and nothing in Acer's code says what it holds on success: **G**.
   (e) New from the agent's log text: the agent also writes `SetGamingMiscSetting(0x101)` under the log line "Enable Turbo Key" (constant 257 in the same functions) and has a matching "Disable Turbo Key" write (value not resolved). So the pair the UI writes in its Turbo branch (15.2 item 4) is most likely the Turbo-key enable and disable
   setting, not a mode: **G** for the exact mapping, C for the log text and the constant.

#### 15.8.3 What W3 becomes

The values in W3 stand unchanged; two independent readings of the code agree on all of them, so W3 is now backed by two decoders. See the update at the top of W3 in `WRITE-PROPOSALS.md`. In short: the reads **W3-R1 (`0x0B`) and W3-R2 (`0x0A`) may now be proposed first**, then W3a (`0x10B`), W3b (`0x0B`),
W3c (`0x40B`), W3d (`0x50B`); each still needs its own owner gate, and Ghidra does not change what the firmware does with them (it is not visible in any binary). Nothing is proposed that is not in the code. New for the gate text: the Whisper command, the NvAPI reset in the non-Turbo modes, and the fact that no success signal exists on the service side.

#### 15.8.4 What Ghidra did not decode (list for the next task)

- Firmware behaviour: what the BIOS/EC does for `0x0B` and for `0x05` (not in any Acer binary), the reply layouts of selectors `0x0A` and `0x0B`, whether Render and Edit are accepted, whether the firmware keeps a mode across sleep or reboot.
- The runtime values of the in-memory flags (GPU-OC flag, NVIDIA flag): static analysis shows how they are set, not that they were set on this boot.
- The NvAPI structure's field names (read as offsets), the purpose of NvAPI id `0x2D43FB31`, the non-NVIDIA path `0x1A9F0`, the `MaxQ_Mode` branches (not taken here).
- The byte layout of the SMBIOS type 172 table parse (`0x25850`); only its result (entry 0x0F = 1 gives `Operation_Mode_Support` = 1) was read.
- `PSAdminAgent`: the Turbo-key decoder, the low-battery constants, `Force_reset` gating, what `Reset_all_settings` writes, the four unresolved `GetGamingMiscSetting` selectors, which use each `0x02AA001F` / `0x0155001F` immediate has.
- The six file names of the caller check, `OnPowerEvent` and `OnSessionChange` (B7 15.3), and the whole managed UI (15.2): not part of this pass.
- Two decompiler caveats, both checked in the listing: it drops the variadic WMI argument lists (names come from the disassembly), and it shows the void registry writer `0xB9E0` as if it returned a value.

#### 15.8.5 Kept or deleted

**Deleted, nothing kept:** the Ghidra project, my scripts and outputs, the extracted Ghidra and JDK folders, the downloaded zips (`D:\Documents\Projects\_tools\ghidra-scratch`, and the then empty `_tools`), and the per-user settings folder Ghidra created under `%APPDATA%\ghidra` during the run. The SHA-256 values above are the record. A later pass has to download and verify again.

### 15.9 First reads (task B10, 2026-11-12): `GetGamingMiscSetting(0x0B)` and `(0x0A)`, run once each

**Approval.** The owner, in chat on 2026-11-12, answering "do you agree that I open a gate for these two read-only calls?" with "ok, do everything you can". Scope applied literally: exactly these two calls, once each, elevated, in this order, nothing else
(no `Set*`, no other input, no retry loop). **No write was made**, no pipe was opened, no registry value was written, no service was touched.

**Tool.** `tools/predatorsense-probe` got two more inputs on its code allow-list (`--misc-get 11` = `0x0B`, `--misc-get 10` = `0x0A`; the list for `GetGamingMiscSetting` is now exactly 2, 9, 0x0A, 0x0B) and an offline `--self-test-refusals` that
asks the allow-list about every input 0 to 0x1FF, the mode-write values (`0x10B`, `0x20B`, `0x40B`, `0x50B`, `0x105`), large values, four `Set*` names and the wrong class, without touching WMI: it passed. A repeated call and an input of 12 were also refused on the command line. Build with `build.ps1`: clean.
Each call ran in its own elevated process (`Start-Process -Verb RunAs`, output to a file, the process exited by itself, none left running), so there is one log per call. Compact log: `B10-w3-reads-run.txt`.

**Baseline** (only reads done before: `GetGamingSysInfo` 1, 2, 3, 6, 9, 10, as in B4/B5, plus the registry cache and the power state), taken 12 s before R1 and 11 s after R2:

| | before (23:43:57) | after (23:44:28) |
| --- | --- | --- |
| CPU temperature (1) | 74 C | 62 C |
| CPU fan (2) | 2884 | 2912 |
| system temperature (3) | 38 C | 38 C |
| GPU1 fan (6) | 3061 | 3030 |
| GPU2 fan (9) | 3043 | 3043 |
| GPU1 temperature (10) | 49 C | 48 C |
| cache `CurrentOperationMode` | 1 | 1 |
| AC / battery | online, 100 % (Windows status 2) | online, 100 % |

Nothing moved beyond the normal jitter of Auto fans and a CPU temperature that swings at idle (62 to 75 in B5). Every status was 0. All fan values are in the Auto range of B4/B5 (about 2600 to 3100 RPM).
Not taken this time: `nvidia-smi` and the RAPL counter (the brief limited the baseline to reads already done).

**The two answers** (raw 64-bit value; status = low byte; each call returned within 0.3 s of process time, no error, no exception):

| Call | Input | Raw answer | Status | Byte 1 (bits 8 to 15) | Byte 2 (bits 16 to 23) | Bits 24 to 63 |
| --- | --- | --- | --- | --- | --- | --- |
| W3-R1 (23:44:10 local) | `0x0B` | **`0x0B0100`** | 0 | `0x01` = 1 | `0x0B` | 0 |
| W3-R2 (23:44:16 local) | `0x0A` | **`0x0B3300`** | 0 | `0x33` = 0b00110011 = 51 | `0x0B` | 0 |

**Reading.**

- **R1 contains the mode (G, one observation).** Status 0 and byte 1 = 1, which is the mode id of Default in the service's mapping (Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5, section 15.1) and equals the Acer cache `CurrentOperationMode` = 1 (O). So the guess "status in the low byte, mode in bits 8 to 15"
  (the layout of the other Misc reads) held for this call. What one reading cannot show: that byte 1 changes when the mode changes (it equals the cache, and 1 could also be a constant). It is a match, not yet a proof; a read taken in another mode would be one (see below).
- **R2 lists the supported modes as a bit mask (G, fairly strong).** Byte 1 = `0x33`: bits 0, 1, 4 and 5 are set, bits 2, 3, 6 and 7 are clear. With bit N meaning "mode id N is available" that is **Quiet, Default, Extreme, Turbo present; Render and Edit absent**, which is exactly what `Feature.ini [OperationModeSupport]` offers
  (Quiet, Normal, Extreme, Turbo = 1) and what the UI shows on this machine (O). The firmware's own capability word agrees with the Acer file, and it answers the open question of 15.7 and `WRITE-PROPOSALS.md` ("does the firmware accept Render and Edit?"):
  **the firmware does not advertise them, so `0x20B` and `0x30B` stay not proposed, now with a second source.** The mapping of bits to modes is an inference (no Acer code parses this value, 15.8.2 item 4); it fits all four set bits and both clear ones, but it remains **G**.
- **Byte 2 = `0x0B` in both answers (unexplained).** It is not an echo of the input (R2 was sent `0x0A`). It could be a constant field, a version or a count (11?); no code says. Not used for anything.
- **Bits 24 to 63 are 0 in both**; no further field is visible.
- The status byte is 0 in both, the same convention as the other reads; the `0xE1` seen once from `GetFunction(0x207)` (section 11) did not recur here.

**Can W3a be read back?** Yes: the read works and is cheap, so W3-R1 before and after is now part of the W3a plan (each run needs the owner's approval again, or the owner may allow repeating that one read). But W3a (`0x10B`, Default) restates the current mode, so R1 should read `0x0B0100` both times:
it can show that the call did not disturb anything and that the mode stayed 1, **not that the write took effect.** The first step that can show an effect through R1 is W3b (`0x0B`, Quiet): if the layout holds, byte 1 goes to 0 (answer `0x0B0000`) and returns to 1 after the undo `0x10B`.
That is a prediction (G). If byte 1 stays 1 after W3b, either the layout guess is wrong or the firmware did not take the value, and the fan and temperature sensors decide. An unchanged reading after W3a is no objection to W3b.

**Still not known.** What the firmware does for each mode (fans, power limits); whether it keeps a mode across reboot or sleep; what byte 2 means; how R1 answers in a non-Default mode (needs a mode change, which is a write); whether the mask is bit-per-mode-id or something that merely looks like it.
**Tags:** raw answers, statuses and baseline values O; "R1 holds the mode" G (one match); "R2 is a mode mask" G.
