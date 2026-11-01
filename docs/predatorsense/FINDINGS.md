# PredatorSense interface findings (task B1)

Machine: Acer Predator PT516-51s (Triton 500 SE), board `Countryman_TLS`, BIOS Insyde V1.08, Windows 11.
Software read: Store package `AcerIncorporated.PredatorSenseV30` 3.0.3198.0 and the `PSSvc` service folder
`C:\Program Files\Acer\PredatorSense Service`. Everything was read in place. Nothing from Acer is copied into this
repo; names, ids and numbers below are facts, the wording is ours. Nothing in this study wrote to the hardware, and
**no WMI method was called**.

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

Unit of the values: **G** (temperatures in degrees C, "speed" in RPM, "rate" in percent). Not read yet; needs the gate.
Input `2` on the same method returns whether "battery boost" is on (UI reads the byte at bits 40 to 47, 1 = on). C.

**Performance mode ("operation mode": Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5)**

UI → pipe command 53 with the mode number; also tells the admin agent (command 34) to change the lighting
accent. What the service then writes to firmware I could not recover without disassembling the native code: **not
decoded**. The current value is cached at `HKLM\...\PredatorSense\Overclock\CurrentOperationMode` (here 4 = Extreme, O).
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
I decoded the shape (CPU fan sets bit 0 or 1, other fans use bit pairs from position 2 or 3 upwards) but did **not**
finish it, so no value is given here. Reading mode back: `GetGamingFanBehavior`, input not decoded. **Not decoded.**
The UI also keeps `CurrentFanMode` in the registry (here 0 = Auto, O).

**Cool Booster (extra fan boost)**: set = `AcerGenericMethod.SetFunction` with `7 | (state << 16)` (W);
read = `GetFunction(0x207)`, byte 1 of the answer equals 1 when on (R?). C. Name matches the UI's "Cool Booster" toggle.

**Custom fan speeds, fan curve**: UI sends per-fan percentages (0 to 100, scrollbar) and an "auto" flag per fan;
service has `SetGamingFanSpeed` (16), `SetGamingFanTable` (18), and its own `set_oc_fan_table` routine that changes the
table when CPU/GPU overclock level changes (log text; skipped when operation mode is supported, so skipped here, C).
Encodings **not decoded**.

**Keyboard lighting**: this machine reports RGB keyboard support and `LightingType` 1 (O). Two routes exist in the
code: WMI (`SetGamingRgbKb`, `SetGamingKBBacklight`, LED behavior/colour methods) and USB (admin agent). Which one
this model uses: **not established**. Effects list (breathing, neon, wave, shifting, zoom, meteor, twinkling in
one UI variant) exists per model variant; the variant for this machine was not identified.

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
| Live CPU/GPU/system temperature, fan speed | `AcerGamingFunction.GetGamingSysInfo` | **needs approval**, gate open |
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

Not yet done: the elevated run (thermal zones), and the firmware sensor read, which waits on gate `g-8fdcf5880b83`.

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

- Any live value from the firmware (gate open, see `GATE-getgamingsysinfo.md`).
- Native service code: what exactly it sends to WMI for operation mode, fan mode, lighting; any rate limits.
- Complete fan-behavior, fan-speed, fan-table and LED encodings.
- Whether the security descriptor string I found is really the pipe's, and whether a non-genuine caller is refused at connect time or only after (I never opened the pipe).
- Units of sensor values; whether "rate" is percent.
- Which UI variant and lighting route this model uses.
- What resets settings (sleep, reboot, Turbo key).

## 10. Proposed write operations (for the owner to approve one by one)

Nothing here has been run. None is ready before the read gate is answered, because the first thing to do with
any of them is to read the current state and record it so the write can be undone.

| # | Call | Value | Expected effect | Risk | Undo | State of the evidence |
| --- | --- | --- | --- | --- | --- | --- |
| W1 | `AcerGenericMethod.SetFunction` (id 1) | `0x10007` (Cool Booster on) | fans boost to a higher speed | low to medium: louder, more wear if left on | `SetFunction(0x7)` | encoding read from code (C); the meaning "Cool Booster" is from the name only. Read `GetFunction(0x207)` first. |
| W2 | `AcerGamingFunction.SetGamingMiscSetting` (id 22) | `0x2005` (MaxQ mode 2, level 0) | GPU power profile for MaxQ | medium: GPU performance/power change | write the previous value; the current value is unknown (no decoded read) | encoding C; effect G. Do not run before a decoded read exists. |
| W3 | operation mode (pipe 53) | e.g. Default | performance profile | medium | previous mode | **not decoded**: needs the service's WMI mapping first. |
| W4 | fan mode / custom speeds / fan table | n/a | | highest: can overheat | | **not decoded**; no value proposed. |

Recommendation: after the read gate, decode the fan and operation-mode reads (`GetGamingFanBehavior`,
`GetGamingFanSpeed`, `GetGamingFanTable`, operation mode) from the UI/service, keeping each one behind its own
gate, before proposing any fan or mode write. Charge limit: look for it outside PredatorSense (Acer Care Center, which
is not installed) or accept it is not available.
