# The PredatorSense module (task B6)

`modules/predatorsense/` is a Rigbay module for Acer Predator laptops. Current version 0.1.2 (Unreleased). It shows the firmware's temperatures and fan speeds and the **performance mode (read only)**, and
sets the fan mode to **Auto** or **Maximum**. Nothing else. It is built only from what `FINDINGS.md` sections 11 to 15 verified.
It keeps to its own folder (own solution `Rigbay.PredatorSense.sln`, own `Directory.Build.props`, `Rigbay.Abstractions` as the only
reference) so the owner can move it to a `module-predatorsense` repository; there, the project reference becomes the
`Rigbay.Abstractions` package (the comment in the csproj says how).

## What it does

| Part | Behaviour |
| --- | --- |
| Probe | Supported when the service `PSSvc` exists (registry key of the service, nothing is started) and the WMI class `root\WMI:AcerGamingFunction` exists (schema only, 6 s timeout). The first failing reason comes back as a key (`probe.no-service`, `probe.no-wmi`) or `probe.check-failed` with the error text; the probe never throws. No model is hard-wired; the model on the page is the firmware's `SystemManufacturer` and `SystemProductName` from the registry. |
| Readings | `GetGamingSysInfo` with input `1 \| (index << 8)` for index 1 CPU temperature, 3 system temperature, 10 GPU temperature, 2 CPU fan, 6 and 9 GPU fans. Value is `(raw >> 8) & 0xFFFF` when the status byte is 0. Metric ids and units: `cpu.temp`, `system.temp`, `gpu.temp` (degC), `fan.cpu.rpm`, `fan.gpu1.rpm`, `fan.gpu2.rpm` (rpm). |
| Performance mode (0.1.2, read only) | `GetGamingMiscSetting(0x0B)` is the current mode, `GetGamingMiscSetting(0x0A)` the modes the firmware has. Both answers: status in the low byte (non-zero = unknown), the value in bits 8 to 15 (FINDINGS 15.9; the real machine gave `0x0B0100` = Default and `0x0B3300` = bits 0, 1, 4, 5 = Quiet, Default, Extreme, Turbo). Mode ids Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5; any other id is "Unknown", never a guess. The supported list is read once when the module starts; the mode every 10 s (`PredatorSenseSettings.OpModePollSeconds`) **only while the page is visible or the metric `predatorsense.opmode` has a subscriber**, and at once when somebody starts to look; with nobody looking it is not read at all. A failed read, timeout, non-zero status or unknown id shows "Unknown" and withdraws the metric. The metric carries the id (unit none, descriptor `sensor.opmode`). There is no write. Since the UI review (0.1.2) the page draws the modes the laptop offers as a choice that is **never enabled**, with the current one chosen, "Read only. Change it in PredatorSense for now." under it and the why in its help glyph; nothing handles a change of its value and it has no hardware effect. When the current mode was not read, or is one the laptop did not list, the row's line says so. A change of the advertised list rebuilds the page. |
| Not available | A non-zero status, an error, a timeout, a temperature of 0, a fan at 0 that never turned, or a value outside 130 degC / 20000 rpm gives "not available": nothing is published for that sensor. Parts the machine does not have (no system fan here, a second GPU temperature) get no row. |
| Page | Since the UI review of November 2026 (`docs/ui-review/REVIEW-2026-11.md` PS-1, PS-2, DB-5, LC-1, LC-2; host API `>=0.1.3 <0.2`): the **fan mode is the page's main control** (`PageModel.Primary`, drawn as two segments under the page header) with one live line for what Rigbay set and what the fans did; the three temperatures are **tiles with their own fan** beside the number (`ReadingRow.Secondary`: "fan 3,711 rpm", "fans 2,870 · 2,960 rpm"); the fans are rows only (`Tile = Never`, no chart). Groups: **Readings** (the six reading rows; a host with the review's package B part 2 folds them into "All readings"), **Performance** (the read-only mode row), **Settings** (fan shortcut, restore Auto, **Details**: machine and poll interval). The dashboard line is the state of the fans when Rigbay set them ("Fans Maximum since 18:42"), else empty. |
| Fan mode | One choice row, the page's `Primary`, flagged `ChangesHardware`. The host shows the effect text and the exact change ("Fan mode: Auto -> Maximum"), asks, then sets the value; the module writes on a pool thread. The row never shows a mode Rigbay does not know: its options are Auto and Maximum and its value starts as `?`, which is not an option, so the host selects nothing (host API 0.1.3; until 0.1.2 a "Not set" option did this, see "Not set, set, revert"). After the write the module reads the fan speeds every 2 s for 10 s; the line under the choice says "Set to Maximum at 18:42 · CPU fan sped up: 2857 to 5555 rpm" (or that they fell back, stayed, or did not), "Not set by Rigbay yet" before the first change. It is a reading, not proof of the mode; the help glyph says so. The host's Revert puts the previous value back and is asked about like the change itself (see "Things to know"). |
| Last set | There is no firmware read of the **fan** mode (the performance mode above is a different thing and can be read). The fan-mode line says "Set to Maximum at 18:42" and its help that PredatorSense may change it without telling Rigbay. After a refused write the row goes back; after a write that did not answer in time the row stays on Maximum and says "not confirmed", because it may have landed. |
| Safety | When the module stops (turned off, host exit, fault) and Rigbay had set Maximum (also an unconfirmed one), it sends Auto, with a 2 s timeout so the 5 s Shutdown limit holds. Setting `restoreAutoOnExit`, on by default. After 5 failed polls in a row the sensors are withdrawn from the bus (their rows say "not available"), the dashboard line says "Readings not available", and fan changes are off until a poll succeeds again. |
| Settings | `pollSeconds` (1 to 30, default 2; the rate while the page is visible or a sensor metric has a subscriber, 10 s otherwise), `restoreAutoOnExit` (default true), in the module's settings file. |
| Hotkey | `toggle-fan`, **unbound by default** (that is how it is "off"). When the user binds a gesture, a press asks the host's approval service ("Set fan mode to Maximum" or "Fan mode: Maximum -> Auto"), and only on a yes writes. The result is announced once, with the speed-check line in it (`toast.fan-set`; a write the laptop refused, a stalled one, or one the fans did not follow is a warning): the page's own choice or Revert shows it in the window while the user looks (`ToastOrigin.Page`), the hotkey and the verb always give a Windows notification, and a failure always does (`docs/APP.md`, "Notifications"). A "no" is not announced. |
| Verbs | `rigbay predatorsense status` prints the model, six readings (or "not available"), the performance mode (a fresh read) and the last-set fan line; exit 0, or 1 when no sensor answered. `rigbay predatorsense fan auto\|max` asks for approval; no yes (or no window to ask in) means exit 1 and no call; a refused or unanswered write is exit 1; other arguments are exit 2. |
| Languages | `lang/en.lang`, `lang/vi.lang`, 94 keys each under `module.predatorsense.`; `Rigbay.LangCheck --strict` is clean (also the `name` and `description` overrides, so the enable row is translated). |

## The rule that decides what can reach the firmware

`Wmi/WmiAllowList.cs` is a fixed table:

| Call | Allowed |
| --- | --- |
| `GetGamingSysInfo` (UInt32 input) | the six indexes above, nothing else |
| `GetGamingMiscSetting` (UInt32 input) | `0x0B` (current mode) and `0x0A` (supported modes), nothing else; the module has no method that takes the input, only `ReadOperationMode()` and `ReadSupportedOperationModes()` |
| `SetGamingFanBehavior` (UInt64 input) | `0x01410019` (all fans Auto) and `0x02820019` (all fans Maximum), nothing else |

The two mode reads are checked in `GuardedAcerWmi` (they have no argument) and in `AcerWmi.ReadMisc`; the tests ask the allow-list about every input up to 0x20000, the mode-write values (`0x10B`, `0x20B`, `0x30B`, `0x40B`, `0x50B`, `0x105`), every one-bit change and large values, call the real `AcerWmi` with refused inputs (refused before a WMI object exists), and check by reflection that the interface has exactly four methods and the only `Set*` name on the list is the fan one. The fan write is checked three times: in `FanController` (it can only name a mode, never a number), in `GuardedAcerWmi` (the decorator the module
logic always talks through; it also lets one write through at a time), and inside `AcerWmi` itself. The unit tests refuse every
value below 0x40000, every one-bit change of the two values, the custom-speed value for all fans, and all other sensor indexes; a test
calls the real `AcerWmi` with refused values to show the refusal happens before any WMI object exists. Cool Booster, MaxQ, fan curves,
a performance-mode write (`SetGamingMiscSetting`), lighting and battery limits are not in the module and cannot be added without a new decoded encoding, an owner gate and a
new table entry.

## Layers

```
PredatorSenseModule (IModule: Probe, Initialize, GetPageModel, Shutdown; page, texts, verbs and hotkey in partial files)
  |-- SensorPoller + PollScheduler   reads, decodes, publishes on the metrics bus, counts failed polls, withdraws sensors that stay unreadable
  |-- OperationModeReader            reads the performance mode (and the list of modes), publishes predatorsense.opmode; its own PollScheduler (10 s, idle = never)
  |-- FanController                  the only writer: one at a time, readings rule, RPM check, restore
  |-- GuardedAcerWmi : IAcerWmi      allow-list + one write at a time
  |       '-- AcerWmi                real System.Management (timeouts, one call in flight, UInt32 reads, UInt64 write)
  '-- MachineProbe over IMachineInfo (registry + WMI schema)
```

`IAcerWmi` has four members, `ReadSysInfo(index)`, `ReadOperationMode()`, `ReadSupportedOperationModes()` and `SetFanBehavior(value, timeout)`. The tests replace it with a fake that keeps a
record of every call, so "exactly one call with the exact value" is checked as a list.

## Evidence (2026-11-01, this machine, Windows 11, elevated host, no write)

- `dotnet build Rigbay.sln -c Release`: 0 warnings, 0 errors (module solution alone the same).
- `dotnet test`: module 89 passed, Core 230 passed (Release) at 0.1.0; at 0.1.1 the module has 121 tests (18 new ones in `FanModeStateTests`), the whole solution is green.
- `dotnet run --project src/Rigbay.LangCheck -- <lang> --module predatorsense --source <src> --strict`: ok, 76/76 in both languages at 0.1.0 and 93/93 at 0.1.2.
- The module folder (output of the build, no `Rigbay.Abstractions.dll` in it, `System.Management.dll` and `System.CodeDom.dll` next to the
  entry assembly) was loaded by the real app from a scratch modules folder (`tools/run-elevated.ps1`). Log: probe supported, "Readings are now
  Available", `enabled`, `stopped`. System.Management loaded through the A6 loader rule.
- PNGs of the app window (PrintWindow of its own HWND, each one looked at) in `modules/predatorsense/docs/evidence/`: `module-tall-dark`,
  `module-tall-light`, `module-vi-dark`, `dashboard-dark`, `dashboard-light`, `approval-dark`. (0.1.1 added the `fan-*` shots, see the end of this file.)
- Live readings by the verb `rigbay predatorsense status` (headless, exit 0) next to `nvidia-smi` taken just before and after
  (`modules/predatorsense/docs/evidence/status-*.txt`):

| Run | GPU temp, module | `nvidia-smi` before / after | CPU temp | System temp | CPU fan | GPU fans |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 57 | 56.34 / 57.81 | 82 | 44 | 3846 | 4347, 4340 |
| 2 | 56 | 56.06 / 56.44 | 82 | 44 | 3529 | 4109, 3906 |
| 3 | 57 | 56.69 / 58.28 | 85 | 44 | 3896 | 4347, 4340 |

  The GPU temperature agrees with `nvidia-smi` within 1.6 degC, as in B3. CPU temperature and fan speeds were higher than in the B3 to B5
  logs (CPU 57 to 75 degC, fans 2564 to 3083 rpm) because other workers were building and testing at the same time (CPU 82 to 87 degC in the
  runs above). This task wrote nothing, so the fans were in whatever mode they were in before (the firmware cannot be asked); the higher speeds
  follow the higher temperature. The system temperature (44) and the GPU fans running a little faster than the CPU fan match the earlier logs.
- The approval dialog was opened from the page (script `edit:row.fan-mode.header=max`) and **nobody answered it**; the log shows "approval
  asked" and no fan line, and no log of this task contains a fan write. The fan row stayed on Auto.

## Not verified

- **Any write by this module on the real machine** (0.1.0 and 0.1.1 alike). None was made, by design (not even Auto). The write path is verified against the fake only.
  The call format itself was verified live by B4 and B5 (status 0, RPM response) with `tools/predatorsense-write`, not by this module's code.
  The module's own real `AcerWmi.SetFanBehavior` has therefore never run.
- **0.1.1, in the real window:** that the open drop-down has no "Not set" entry after the first set, and that no Revert button is shown for the first change. Both follow from the host code (`ModuleEntry.RefreshPageOnce` disposes the old editors) and the unit tests; the PNGs show the closed box only. The set states were drawn over a fake firmware.
- The real `AcerWmi` timeout behaviour (a stalled call) was not provoked; it is covered by the fake and by reading the code.
- The "restore Auto on exit" path on real hardware, the hotkey on screen (a real key press), the verb `fan` live (a headless run is refused
  by the host because there is nobody to ask; that refusal is unit-tested, not run live on purpose).
- The "Readings are not available" banner and the Unsupported bay on screen (no way to make this machine fail without breaking WMI); both are
  unit-tested at the page-model level.
- 150 % and 200 % DPI, high contrast, screen readers; a different Acer model; behaviour across sleep, reboot, or a resume where PSSvc re-applies its own mode.
- Whether the Acer firmware CPU sensor reads about 10 degC above the Windows thermal zone is quoted from FINDINGS 12.3; this run did not repeat it.

## Things to know

- **Revert is asked about, both ways (host rule since A9).** The fan row does not set `SafeRevert`: Auto -> Maximum is loud, so a Revert after
  "Auto" asks like any change (`effect.fan-mode-revert`: "Puts the fans back to the previous mode ...", with the exact current and previous value);
  Maximum -> Auto asks too, because the module does not claim one direction is harmless for the user. The page has no button row, so there is no
  `RevertButtonKey`. The app must call `RevertAsync` (a synchronous `Revert()` writes nothing and returns "must be confirmed").
- **Two poll speeds follow what is looked at.** The sensors are read every `pollSeconds` (default 2 s) while the module's page is visible
  (`IHostServices.PageVisible`) or any of the six sensor metrics has a subscriber (`IMetricsBus.SubscriberCount`; the dashboard bay, a recorder or
  another module), and every 10 s (`PredatorSenseSettings.IdlePollSeconds`, not a setting) otherwise. Both signals are followed with
  `PageVisible.Changed` and `SubscribersChanged`; when somebody starts to look, `PollScheduler.PollSoon()` reads at once. A host that does not
  send the signals leaves `PageVisible` true (`ConstantValue`), which means 2 s always, as before. One poll is about 70 ms of WMI time.
- **A sensor that cannot be read leaves the bus.** A sensor that had values and gets none for 5 polls in a row is withdrawn
  (`IMetricsBus.Withdraw`), so its `ReadingRow` says "not available" instead of showing an old number; the next value publishes it again. A part the
  machine does not have never published anything and is never withdrawn. The old "Readings are not available ... may be old" warning row is gone;
  the state line and the disabled fan row still say the sensors cannot be read.
- **Dashboard labels come from the module.** Each of the six metrics has a `MetricDescriptor` (label key `sensor.*`, unit), registered in
  `Initialize`: `cpu.temp` shows as "CPU temperature", `fan.cpu.rpm` as "CPU fan". `cpu.temp` is also published by the XTU module (a different
  sensor); the host keeps the first description it gets.
- A host crash while Maximum is set leaves the fans on Maximum (nothing can restore it). The undo is PredatorSense's Auto button, or a reboot (untested).
- **Fan row before anything is set (E2E-10, fixed in 0.1.1).** Until 0.1.0 the row showed Auto although nothing was written. Now it shows "Not set" (see "Not set, set, revert").

## The live write test the owner can approve afterwards

B4 and B5 proved the call format, status and RPM response with the write tool. What is still unproven is **this module's own `AcerWmi` write path
and its restore**. One session, elevated, on AC power at 50 % battery or more, CPU load low, one value at a time, each with its own approval:

1. In the app, set Fan mode to Maximum from the page (the host asks, then `SetGamingFanBehavior(0x02820019)`): expect "CPU fan sped up" within 10 s,
   "Last set by Rigbay: Maximum at hh:mm", status 0 in the log.
2. Switch the module off (or close the app) while Maximum is set, with `restoreAutoOnExit` on: expect `SetGamingFanBehavior(0x01410019)`,
   status 0 in the log, fans back to the Auto range within a few seconds. This is the restore-on-exit test; it covers the 2 s timeout path.
3. Turn the module on, bind the fan shortcut, press it twice (Maximum, then Auto), approve both: the hotkey path.
4. `rigbay predatorsense fan max` while the app runs (forwarded to it, the approval dialog appears), then `fan auto`.

Abort at once, and use PredatorSense's Auto button, if a status is not 0, no fan speeds rise within 10 s, or a temperature reaches 90 degC.

## Not set, set, revert (0.1.1, finding E2E-10)

**Since 0.1.2 (host API 0.1.3)** the "Not set" option and the page rebuild are gone: the value `?` is not an option, so the host selects neither
Auto nor Maximum, and the line says "Not set by Rigbay yet". A Revert of the first change asks "Fan mode: Maximum → ?" (the host prints a value
that is not an option as it is), writes nothing and the line says "No earlier mode to go back to, so nothing was changed". The table below is the
0.1.1 design, kept for the record.

The firmware cannot say which fan mode it is in, so a control that opens on "Auto" suggests a fact nobody knows. The `ChoiceRow` contract has no
placeholder text (`docs/ABSTRACTIONS.md` 5.2, and the option list is fixed per row), so the module does it with an option and a page rebuild:

| State | What the page shows | What happens |
| --- | --- | --- |
| **Not set** (the module started and Rigbay has set nothing in this run) | Options **Not set**, Auto, Maximum; the box shows "Not set"; "Last set by Rigbay: Nothing yet" | Nothing was written. "Not set" is a state, not a value: choosing it writes nothing. Choosing **Auto** is a real request (it used to be a silent no-op, because the box already said Auto); it asks like every change and writes Auto. |
| **Set** (Rigbay set Auto or Maximum, or a Maximum that may have landed) | Options Auto, Maximum only; the box shows the mode; "Last set by Rigbay: Maximum at 18:42" | On the first set the module calls `RequestPageRefresh()` once; the rebuilt page has no "Not set" option. A refused first write leaves the row on "Not set". |
| **Revert** of the first change | the host would put "Not set" back, which is not a value: the module writes nothing, shows the row on what Rigbay really set and the speed-check line says "No earlier mode to go back to, so nothing was changed" | After the page is rebuilt the host holds no previous value for the new row, so no Revert is offered for that first change. A Revert of a later change (Auto <-> Maximum) is asked about and written as before. |
| **Module restart** (disable, enable; same app run) | what Rigbay set is kept (`LastSet` is carried), so no "Not set" | A new app run starts at "Not set" again, because PredatorSense or a reboot may have changed the mode meanwhile. |
| **Language switch** | the state stays; "Not set" and "Chưa đặt" are in `fan.not-set` | Nothing is written. |

Every write is still guarded by the host's approval question (the row keeps `ChangesHardware`) and by the allow-list; nothing new can reach the firmware.
The hotkey and the `fan` verb already said "Set fan mode to Maximum" when nothing was set; they are unchanged.

Evidence for 0.1.1 (`modules/predatorsense/docs/evidence/`, PrintWindow of the app's own HWND, each one looked at): `fan-not-set-dark`, `fan-not-set-light`,
`fan-not-set-vi-dark` are the **real module** with live reads only (no write; the module was enabled and left on "Not set"). `fan-set-auto-dark-fake-firmware`
and `fan-set-max-light-fake-firmware` are the same module code over an in-memory firmware in a scratch project (never committed), because this task may not write to
the hardware; the app asked and approved by script (`approve:true`), the page was rebuilt without "Not set" and showed the mode.

## Performance mode, read only (0.1.2, task P1)

Evidence (2026-11-12/13, this machine): `dotnet build Rigbay.sln -c Release` 0 warnings, 0 errors; the module has 184 tests (63 new, all green; the whole solution is green: Core 685, XTU 208, SteamBypass 269, Recorder 146, Tunnel 117, LangCheck 19); `tools/ci/langcheck.ps1` strict: predatorsense 93/93 in en and vi (its one reported problem is the tunnel folder missing from the script's list, not this module).
**One live run of the module's own read path** (real `AcerWmi`, the module build copied to a scratch modules folder, the app elevated with its own data folder, the page opened, then the app quit by itself; no fan write, nothing else called): the module read `GetGamingMiscSetting(0x0A)` at start and `(0x0B)` when the page opened; the log says `Performance mode read: raw 0xC0100 -> Default`, and the page shows "Performance mode: Default" and "Modes this laptop offers: Quiet, Default, Extreme, Turbo" (`docs/evidence/opmode-real-machine-dark.png`, PrintWindow of the app's own HWND). The mode byte is 1 as in B10 and as in the Acer cache; **byte 2 was `0x0C` this time, it was `0x0B` in B10** (so it is not a constant; the module ignores it).
Screenshots over an in-memory firmware (scratch project, not committed, no hardware): `docs/evidence/opmode-dark.png`, `opmode-light.png`, `opmode-vi-dark.png`. What is **not** verified:
the mode in any state but Default (two observations of Default, so "byte 1 = the mode" is a match with the Acer cache, not yet a proof; the first reading in another mode needs a mode change, which is a write), whether the
mask is bit-per-mode-id, what byte 2 means (`0x0B` in both B10 answers, `0x0C` in the one live mode read of P1; ignored), and the page in the real host window beyond the screenshots of the page over the fake firmware in `docs/evidence/`.
The tray line and a Dashboard tile are not part of this version (the metric and its descriptor are there so the Dashboard can show it).
