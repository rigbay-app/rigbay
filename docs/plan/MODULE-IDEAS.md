# Module ideas, ranked (November 2026)

Task b84c2477 (M1), 2026-11-13. Candidates for "a hardware and system control bay for a gaming laptop", ranked for this owner: Acer Predator
Triton 500 SE (PT516-51s), i7-11800H, RTX 3060 Laptop, Windows 11, with XTU profiles, PredatorSense (fans, mode read), Steam access, Recorder and
Remote tunnel already installed. Background and the PowerToys comparison: `docs/research/POWERTOYS-2026-11.md`.

What the machine showed on 2026-11-13 (read only, this task): `nvml.dll` and `nvidia-smi.exe` are in `System32`; one active display, an external
1920x1080 monitor at 100 Hz on the NVIDIA GPU (50, 59, 60, 75, 100 Hz available); the Intel adapter reports the internal 2560x1600 panel at
165 Hz (not active at the time); battery `AP20BHU` at 100 %, on AC; power plan Balanced. Every firmware baseline in
`docs/predatorsense/FINDINGS.md` was also taken on AC.

How to read the table. **Value** is for this owner. **R/W**: what touches the machine. **Gate**: an owner approval needed before the work can
even be tested (a firmware read or write never run before, or a rule that writes without a click). **Size**: S under a day, M a few days, L a
week or more. Every write still goes through Rigbay's approval dialog and Revert (`docs/ABSTRACTIONS.md` 5.4); "Windows setting" means
`EffectKind.SystemSetting`.

## Ranking

| Rank | Module | What it does | Value | R/W | Gate | Admin | Risk | Size | Done well elsewhere |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **1 (top 3)** | **GPU readings** (NVML) | GPU load, power, clocks, memory, P-state and throttle reason on the metrics bus | high | read | none | no | low | M | HWiNFO, GPU-Z, Afterburner (none feed Rigbay's bus, recorder, alerts) |
| **2 (top 3)** | **Game scenes** | When a chosen game starts, run module verbs (XTU profile, recorder start, Steam access, tunnel); undo when it ends | high | writes through modules' own verbs | owner: approve each scene's automatic writes once | yes (process events) | medium | L | Vendor suites of other brands for their hardware; nothing in PowerToys |
| **3 (top 3)** | **Watch** | Alerts on any reading: threshold, duration, one notification per episode | medium to high | read | none | no | low | S to M | HWiNFO alarms (only its own sensors) |
| 4 | Power-source rules | On battery: internal panel to 60 Hz, Windows power mode to best efficiency; restore on AC | medium (high for anyone who unplugs) | Windows setting | owner: automatic switching | no | low to medium | M | Small single-purpose tools; Windows does not switch by power source |
| 5 | Network quality | Ping, jitter, loss to the gateway and a chosen host; Wi-Fi signal and link rate | medium | read (sends ICMP) | none | no | low | S to M | PingPlotter, WinMTR |
| 6 | Keyboard backlight (in PredatorSense) | Show and set brightness; off after idle or on battery | low to medium | firmware read and write | owner: the read `0x80184`, then the write | yes | low to medium | M | PredatorSense, Fn keys |
| 7 | Battery health | Design vs full-charge capacity, wear, cycles, charge or discharge rate in W | medium to low | read | none | partly (root\WMI) | low | S | `powercfg /batteryreport`, BatteryInfoView |
| 8 | Storage health | NVMe temperature, wear, power-on hours, free space | low to medium | read | none | yes | low | S | CrystalDiskInfo |
| 9 | CSM status | Agent sessions, sessions waiting for a person, quota left; a notification when one waits | medium (this owner only) | read | none | no | low | S | CSM's own app |
| 10 | GPU mode (in PredatorSense) | Show discrete or hybrid; switch it (after a restart) | medium to low here | firmware read and write | owner: read `GetGamingMiscSetting(2)`, then the write | yes | **high** | M to L | PredatorSense |
| 11 | Readings overlay | A small always-on-top, click-through box with chosen readings | medium | read | none | no | medium | M to L (host feature) | RTSS with Afterburner, NVIDIA app overlay, Xbox Game Bar (all with fps) |
| 12 | Audio output switch | Default playback device by hotkey or tray | low to medium | Windows setting | none | no | low | S to M | SoundSwitch, EarTrumpet, Windows quick settings |
| 13 | Startup apps | List startup entries, turn some off | low | Windows setting | none | yes for machine-wide entries | low to medium | M | Task Manager, Sysinternals Autoruns |

**Why this order.** The top three cost no firmware gate, use what Rigbay already has (the metrics bus and its history, the recorder, the
modules' verbs, notifications) and have no better home elsewhere. GPU readings come first because they are read only and fill the largest gap:
the GPU drives both the games and the monitor, and Rigbay sees only its temperature and fans. Scenes come second because they make the existing
modules work together, the thing a bay is for; they need one host addition and one owner decision. Watch comes third: small, and it gives a reason to keep
Rigbay in the tray. Power-source rules drop to fourth only because this machine has always been seen on AC with an external monitor.

## 1. GPU readings (NVML) — top 3

- **What.** Metrics `gpu.load` (%), `gpu.power` (W), `gpu.clock` and `gpu.mem.clock` (MHz), `gpu.mem.used` (bytes), `gpu.temp` (°C), the
  P-state and the throttle reason as the state line ("P0 · 72 W · power limit"). Four tiles, a chart; the recorder gets GPU power next to CPU
  package power, so a gaming session is seen whole.
- **How.** P/Invoke into `nvml.dll` (installed with the driver; present here): `nvmlInit_v2`, `nvmlDeviceGetHandleByIndex_v2`,
  `nvmlDeviceGetUtilizationRates`, `nvmlDeviceGetPowerUsage` (mW), `nvmlDeviceGetClockInfo`, `nvmlDeviceGetMemoryInfo`, `nvmlDeviceGetTemperature`,
  `nvmlDeviceGetPerformanceState`, `nvmlDeviceGetCurrentClocksEventReasons` (older drivers: `...ThrottleReasons`). **Read only**: an allow-list in
  code, no `nvmlDeviceSet*` anywhere, as the PredatorSense probe does with WMI. Probe: the DLL loads, init succeeds, one device or more. No admin.
- **Overlap.** PredatorSense already publishes a GPU temperature. Use the shared id and let the Dashboard's "one tile per label" rule keep one;
  or publish only what PredatorSense lacks. Decide in the module brief.
- **Risk.** On hybrid laptops, polling the NVIDIA GPU can keep it from sleeping and drain the battery (a known effect of monitoring tools; not
  measured here). Poll only while the page is open, a recorder runs or Watch asks (the `PageVisible` and `SubscriberCount` pattern), and on battery
  check first whether the GPU is active (how: to find out in the module task). A value the GPU does not report (`NVML_ERROR_NOT_SUPPORTED`, power
  on some laptop GPUs) is withdrawn, not shown as 0.
- **Size.** M (the reads are S; descriptors, page, language files, tests and the probe make it M).

## 2. Game scenes — top 3

- **What.** A scene = a game's executable name plus a list of steps; each step is an existing module verb with its arguments. Today's verbs
  already cover a useful scene: `xtu apply <profile>`, `recorder start` / `stop`, `steam-bypass on` / `off`, `tunnel on` / `off <name>`, and
  `predatorsense fan ...` once its writes are approved. When the game starts the steps run in order; when it ends the undo steps run (the value
  before the scene, recorded when it started). Page: one expander per scene with its switch; the state line "Running: <game> since 21:04".
- **How.** Process start and stop: WMI `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` events (admin; Rigbay is elevated) or a 5 s poll of
  the process list (simpler, a little CPU). **Host addition:** a module may invoke another module's verb through the host
  (`host.Commands.InvokeAsync(moduleId, verb, args)`), inside both modules' fault boundaries, with a list of each module's verbs for the editor.
- **Approval: the owner must decide.** A scene writes hardware with nobody at the window. DESIGN.md asks approval for each specific call and
  value. Proposal for the gate: the owner approves a scene once, as a list of exact steps and values; each run raises one notification
  (XTU's auto-switch already works this way); a step that is not in the approved list is refused; editing a scene asks again.
- **Risk.** Medium. Writes happen only through the modules' own verbs, which keep their own limits and allow-lists. Cases to design: the game
  crashes or Rigbay restarts mid-game (persist "applied", undo at the next start); two scenes overlap (last started wins, undo in reverse
  order); a verb fails (stop, notify, undo what ran).
- **Size.** L (host API, module, approval rule, tests).

## 3. Watch — top 3

- **What.** Rules over any metric: "at or above X for N seconds", with hysteresis, one notification per episode, a quiet period. Suggested
  rules from the metric catalogue (`GetAllMetricInfo`): CPU temperature 95 °C for 30 s; GPU throttling for thermal reasons (with module 1); CPU fan
  at 0 rpm while CPU is above 80 °C; tunnel handshake older than 180 s while the tunnel is on; Steam proxy errors rising. Page: the rules with
  their state (quiet, firing) and when each last fired.
- **How.** `IMetricsBus` reads, `host.Toast` (a tray balloon, since Rigbay is elevated). Read only, no admin, no approval. **Host addition:** a
  background subscription that does not count in `SubscriberCount` and accepts a slow rate (one sample per 10 s, say). Without it, Watch keeps
  every module at its fast poll rate all day and Rigbay's idle CPU goes up, the opposite of `docs/research/POWERTOYS-2026-11.md` section 1.
- **Risk.** Low; noise is the main one (one notification per episode, a quiet period).
- **Size.** S for the module, M with the host subscription kind.

## 4. Power-source rules

- **What.** Two columns, On battery and Plugged in: the internal panel's refresh rate (from the rates it lists) and the Windows power mode
  (best efficiency, balanced, best performance); later the keyboard backlight (6). Applied when the source changes.
- **How.** Source: `RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE)` or `GetSystemPowerStatus`. Refresh rate: `EnumDisplaySettings` to read,
  `ChangeDisplaySettingsEx` with `DM_DISPLAYFREQUENCY` to write (test with `CDS_TEST` first; only modes the panel lists; the internal panel only). Power
  mode: `PowerGetEffectiveOverlayScheme` / `PowerSetActiveOverlayScheme` in `powrprof.dll` are undocumented exports that community tools use; the
  documented alternative is the power plan (`PowerSetActiveScheme`). Both are Windows settings: approval rows; the automatic switch is the same
  owner question as scenes. Order it after XTU's own auto-switch on power change.
- **Risk.** Low to medium: a refresh rate the panel rejects (Windows reverts after its prompt; `CDS_TEST` avoids most of it); an undocumented
  export may change.
- **Size.** M. Elsewhere: Windows 11 has dynamic refresh rate on some panels, not a rule per power source; PowerToys PowerDisplay sets brightness
  and contrast, not the refresh rate.

## 5. Network quality

- **What.** Latency, jitter and loss to the default gateway and to one chosen host (a game server, a DNS resolver); Wi-Fi signal and link rate;
  which adapter is in use. On the bus, so the recorder and Watch use it.
- **How.** `System.Net.NetworkInformation.Ping`, `NetworkInterface` statistics, the WLAN API (`WlanQueryInterface`). It sends ICMP echo, so the
  manifest declares network use. Poll only while watched or recorded. No admin.
- **Risk.** Low; hosts that drop ICMP show "no reply", not an error. **Size.** S to M.

## 6. Keyboard backlight (an addition to PredatorSense)

- **How.** Read `AcerGenericMethod.GetFunction(0x80184)` (FINDINGS 11.4: bits 32 to 39 brightness in %, bits 40 to 47 equal to 30 means off).
  **Never run**: it was held in B3 after its sibling read `0x207` answered with status `0xE1`. Write through `SetGamingKBBacklight`, encoding not
  decoded. Two gates, then hardware rows with approval and Revert.
- **Value.** Fn keys already do it; the gain is automation (off after idle, off on battery).
- **Size.** M, mostly the gates and the decoding.

## 7. Battery health

- **How.** `root\WMI` `BatteryStaticData` (design capacity), `BatteryFullChargedCapacity`, `BatteryStatus` (charge and discharge rate in mW,
  voltage), `BatteryCycleCount`; or `IOCTL_BATTERY_QUERY_INFORMATION`. Read only.
- **Value.** Medium to low: the machine sits on AC, and FINDINGS found no charge-limit setting in PredatorSense v3 here, so wear is worth
  knowing; the discharge rate in W helps rule 4. **Size.** S.

## 8. Storage health

- **How.** `MSFT_StorageReliabilityCounter` (`root\Microsoft\Windows\Storage`): temperature, wear, power-on hours; free space from the volume.
  Read only, admin. Some drives report nothing. **Size.** S.

## 9. CSM status

- **How.** The `csm` command line with JSON output (sessions, notes, quota). Starting a process costs CPU: every few minutes in the tray,
  faster while the page is open. Rigbay runs elevated, so check that `csm` answers the same from an elevated caller. Not hardware; shows
  account data in a window. **Size.** S. Useful to this owner only; it should not go in the public registry list without a reason.

## 10. GPU mode (an addition to PredatorSense)

- **How.** Read `GetGamingMiscSetting(2)` (mode in bits 8 to 15) and the capability `GetGamingMiscSetting(9)` (bits 8 to 10), both held in B3 and
  never run; write `SetGamingMiscSetting(2 | mode << 8)` (FINDINGS 3.3). Takes effect after a restart.
- **Risk.** High: it changes the display path at boot; a wrong value could leave the internal panel dark until the change is undone, perhaps only
  from the BIOS or Acer's app. Not verified in any way. Lower value here because the external monitor already runs on the NVIDIA GPU.
- **Size.** M to L, behind two gates.

## 11. Readings overlay

- **How.** A topmost, layered, click-through tool window. A module cannot create windows (no UI types in the contract), so this is a host
  feature first. It does not show over exclusive full-screen games, only borderless ones, and it cannot show frame rate without hooking the game,
  which is out of scope.
- **Size.** M to L. The tools listed in the table do it better today, with frame rate.

## 12. Audio output switch

- **How.** Read endpoints with `IMMDeviceEnumerator` (documented); setting the default device needs `IPolicyConfig`, an undocumented COM interface
  community tools use. **Size.** S to M. Done well elsewhere; build it only if the owner asks.

## 13. Startup apps

- **How.** Run keys, the Startup folders and logon tasks; turning off through the `StartupApproved` keys, the way Task Manager does. **Size.** M.
  Done well by Task Manager and Autoruns.

## Not new modules

| Idea | Where it belongs |
| --- | --- |
| Cool Booster (W1), MaxQ / GPU power level (W2), operation mode writes (W3) | PredatorSense, per `docs/predatorsense/WRITE-PROPOSALS.md` |
| Fan curve editor | PredatorSense; needs a curve row kind, because a custom control (level 2) would not survive a split UI process (`POWERTOYS-2026-11.md` 1.7) |
| Keep awake, light/dark theme by time, external monitor brightness | PowerToys already has them (Awake, Light Switch, PowerDisplay); not worth a second copy |
| Soak test of PredatorSense's WMI polling | A test task, not a module: PowerToys reports that `System.Management` leaks on each WMI call, and PredatorSense polls through it |
