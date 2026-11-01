# PredatorSense interface findings (task B1)

Machine: Acer Predator PT516-51s (Triton 500 SE), board `Countryman_TLS`, BIOS Insyde V1.08, Windows 11.
Software read: Store package `AcerIncorporated.PredatorSenseV30` 3.0.3198.0 and the `PSSvc` service folder
`C:\Program Files\Acer\PredatorSense Service`. Everything was read in place. Nothing from Acer is copied into this
repo; names, ids and numbers below are facts, the wording is ours. Nothing in this study wrote to the hardware.
Sections 1 to 10 are the B1 reading (no WMI method called then). **Task B2 (section 11) made exactly one WMI method
call: `GetGamingSysInfo(0x0101)`, the owner-approved CPU temperature read.** **Task B3 (section 12) ran the owner-approved
follow-up reads: sysinfo indexes 2 to 17 and `AcerGenericMethod.GetFunction(0x207)`; it still wrote nothing.** Where
section 11 or 12 corrects an earlier statement, the later section wins.

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

UI → pipe command 53 with the mode number; also tells the admin agent (command 34) to change the lighting
accent. What the service then writes to firmware I could not recover without disassembling the native code: **not
decoded**. B2 confirmed the UI has **no read** of the operation mode from firmware (section 11.3). The current value is cached at `HKLM\...\PredatorSense\Overclock\CurrentOperationMode` (here 4 = Extreme, O).
`Feature.ini` on this machine enables Quiet, Normal, Extreme, Turbo (O). Mapping of the 4 labels to the 6 numbers: **G**.

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
  re-apply settings. That it re-applies, and which: **G**.
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
- Native service code: what exactly it sends to WMI for operation mode (get and set), the fan table, lighting; any rate limits.
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
| W3 | operation mode | n/a | performance profile | medium | previous mode | **No direct WMI route known.** The UI uses service command 53 only, and the pipe is off limits (section 4). The service's WMI mapping is not decoded. No value proposed. |
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
- A Rigbay module cannot use command 53 (the pipe checks the caller, section 4). **No direct WMI route for the operation
  mode is known.** If it exists it is probably a combination of other calls (the UI's mode handler also writes OC level and
  LED behaviour, see 11.5), but that is **G**.

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
