# Write proposals (task B3), with the result of the first test (task B4)

Status: **exactly one write has been run on this machine: W4a (all fans Auto), in B4, status 0 (section "W4a result" below).** Every other
candidate is still unrun. No write was run in B1, B2 or B3. The probe (`tools/predatorsense-probe`) refuses every `Set*` method in code. Writes go
only through `tools/predatorsense-write`, whose code allow-list holds the owner-approved calls (today: W4a only, and the log blocks a repeat).
The coordinator presents the candidates to the owner **one value at a time**; approval of one covers only that one call and value.
Evidence tags as in `FINDINGS.md` (C read from the UI's code, O observed, G guess). Section numbers refer to `FINDINGS.md`.

## What the B3 reads changed

- **Fan speed is readable and live**: `GetGamingSysInfo` indexes 2 (CPU), 6 (GPU 1), 9 (GPU 2) returned 2857, 3061, 3083 in Auto mode (12.2). This is the
  **only working before/after check for any fan write**. The unit is RPM by magnitude only (not proven).
- **The "rate" indexes (12, 16, 17) read 0 while the fans turn**, so they are no use as a check (12.2).
- **`GetFunction(0x207)` (Cool Booster state) answered status `0xE1`**, the UI turns that into "off", and nothing says what `0xE1` means (12.4). So there is
  **no proven read-back of Cool Booster**.
- **Still no firmware read of fan mode, fan speed setpoint, fan table, operation mode, MaxQ level or lighting colour** exists in the Acer UI (11.2 to 11.4).
  The Acer registry cache is the app's record, not a read of the firmware.

## Preconditions for every test

1. On AC power with the battery above 50 % (status code 2 at 100 % in the probe output), laptop on a hard flat surface, no heavy CPU/GPU load.
2. **Baseline** taken in the same minute with the read-only probe (elevated): `--sysinfo-index 1`, `2`, `3`, `6`, `9`, `10` and `nvidia-smi`. Idle Auto values seen in B3:
   CPU 63 to 64 C, system 44 C, GPU 53 C, CPU fan 2857, GPU fans 3061 and 3083.
3. **An undo that does not depend on this code**: the Acer PredatorSense app is installed; its fan page "Auto" button (and Cool Booster toggle) sets the same state
   through its own service. A reboot also ends a fan override, unless the firmware keeps it across reboot (**G, untested**).
4. Abort and undo at once if: any call returns a non-zero status, a fan RPM does not move in the expected direction within about 10 s, CPU or GPU temperature goes
   above 90 C, or the machine shows anything unexpected (display, keyboard, audio).
5. Every Set goes through the host's fault boundary with a timeout (a WMI-ACPI call can stall, see 12.5). One call, then read, then decide on the next.
6. The Acer service `PSSvc` keeps running and may re-apply its own cached mode on power or session events (**G**, section 5). A test outcome is only valid if the
   readings are taken within seconds of the write.

## The candidates

### W4a: fan mode, all fans Auto (**recommended first test**)

| Item | Value |
| --- | --- |
| Call | `root\WMI:AcerGamingFunction.SetGamingFanBehavior` (id 14), `gmInput` UInt64, out `gmOutput` UInt32 |
| Input | `0x01410019` |
| Decoding | low half `0x0019` = fans addressed: CPU `0x01`, GPU 1 `0x08`, GPU 2 `0x10` (this machine has no system fans); high half `0x0141` = 2-bit mode per slot, 1 = auto: slot 0 (`0x1`), slot 3 (`0x40`), slot 4 (`0x100`) (C, 11.2) |
| Expected effect | **none visible.** The Acer cache says the fans are already Auto (`CurrentFanMode` 0). It re-states the current state |
| Read-back | status of the Set itself (low byte of `gmOutput`, 0 = success, as the UI checks); then `GetGamingSysInfo` 2, 6, 9: RPM should stay in the baseline range. **No firmware read of the mode exists** |
| Undo | not needed if the recorded state is right. If fans were not in Auto before, this is the "back to normal" value |
| Worst case | the cache is stale and the firmware was in another mode, so this changes the mode to Auto (the safe, firmware-controlled state). A wrong mask would address the wrong fan; the mask is copied from the decoded table, never hand-built |
| Testable? | **Partly.** The call's own status is checked; the effect is "no change", visible only as RPM staying put. It cannot prove that the mode took effect |
| Why first | lowest risk of all: it only restates the recorded state and its undo is itself. It tests that the Set call format, the status and the timing work, before anything louder |

#### W4a result (B4, run 2026-11-01 18:42 local; gate `g-write-w4a`, owner answered "approve", once)

Run by `tools/predatorsense-write` (`--call W4a --execute`), elevated, on AC power at 100 %, CPU load 11 %. Raw log: `B4-w4a-run.txt`.

| Item | Result |
| --- | --- |
| Schema check before the write | `gmInput` of `SetGamingFanBehavior` is **UInt64** (read from the schema, no coercion needed) |
| The call | `SetGamingFanBehavior`, `gmInput` = `0x01410019`, once |
| Answer | `gmOutput` raw `0x0`, **status 0**, returned in **11 ms**. No exception, no stall |
| Baseline (3 passes, 2 s apart, the minute before) | CPU fan 2564 to 2586, GPU fan 1 2654, GPU fan 2 2663, CPU 57 to 61 C, system 44 to 46 C, GPU 52 C; `nvidia-smi` 52.69 C, 16.2 W, 22 % util, P8, fan "[N/A]" |
| After the write (11 passes, t = 0 to 20 s, every 2 s) | CPU fan 2564 or 2586, GPU fan 1 2654 or 2678, GPU fan 2 2663 (all inside the baseline's own jitter), CPU 56 to 65 C, GPU 52 then 51 C; `nvidia-smi` 51.53 C, 13.5 W, 0 %. **No alert fired** (the tool's alert range was baseline min -30 % / max +30 %, and 90 C) |
| Other calls | none. No second write, no retry, no other `Set*`. The run exited with code 0 |

- **Verified (O):** the call format works (class, method, UInt64 input) and the firmware **accepts it with status 0**; the call is fast; RPM and temperatures stayed in the
  baseline range for 20 s, as expected for a write that restates Auto.
- **Not verified:** that the firmware mode changed or was re-asserted (the mode cannot be read back, and nothing was expected to move); that the Acer cache's "Auto"
  was the true prior state (also unknown, but the RPM did not move); whether `PSSvc` re-applies its cached mode later; whether the setting survives a reboot or sleep;
  anything about W4b, W1, W2 (their calls were not run); what the owner heard or saw at the machine (not reported to this worker).
- **Fan RPM jitters by one step in Auto**: the values move in steps of about 22 to 24 (2564, 2586; 2654, 2678), so a pass can show a different number without any change.
  A real fan-mode effect (W4b) must be judged against that, i.e. as a jump of hundreds of RPM.
- `nvidia-smi` shows the GPU fan speed as `[N/A]` (the fans are driven by the EC, not the NVIDIA driver): the Acer sensors 6 and 9 are the only fan reading.
- **For W4b:** W4a returned status 0, so the order rule "only after W4a returned status 0" is met. W4b still needs its own owner approval and its own entry in the write tool's allow-list.

### W4b: fan mode, all fans Max

| Item | Value |
| --- | --- |
| Call | `SetGamingFanBehavior` (id 14) |
| Input | `0x02820019` (same mask, mode 2 = max in slots 0, 3, 4) |
| Expected effect | CPU and both GPU fans go to full speed; RPM rises well above baseline within seconds; loud |
| Read-back | `GetGamingSysInfo` 2, 6, 9 every 2 s for the duration; temperatures 1 and 10 should fall slowly. Status of the Set. **No mode read exists** |
| Undo | `SetGamingFanBehavior(0x01410019)` (W4a); fall back to the Acer app's "Auto" button, then to a reboot |
| Worst case | fans stay at Max (noise, bearing wear if left for long) if the Auto write is refused; no overheating risk from Max itself. A wrong mask changes a different fan's mode |
| Duration | at most 15 s, then W4a and confirm RPM returns towards the baseline |
| Testable? | **Yes for the effect** (RPM rises, readable), **no for the state** (no mode read) |
| Order | only after W4a returned status 0 |

### W1: Cool Booster on

| Item | Value |
| --- | --- |
| Call | `root\WMI:AcerGenericMethod.SetFunction` (id 1), `uiInput` UInt64, out `uiOutput` UInt32 |
| Input | `0x10007` = `7 \| (1 << 16)` (C, the UI's setter) |
| Expected effect | extra fan boost, louder; the name is from the UI ("Cool Booster"); what it does in firmware is **G** |
| Read-back | **the state read is unproven**: `GetFunction(0x207)` returned status `0xE1` (12.4), so it may never report "on". Use the fan RPM indexes 2, 6, 9 and the Set's own status (0 = success per the UI) |
| Undo | `SetFunction(0x7)` (state 0); then the Acer app's Cool Booster toggle; then a reboot |
| Worst case | fans louder and more worn if left on. Unknown: the firmware answered the matching read with an error code, so the feature may be absent, or the write may be refused or do something else under the same selector. The same method also carries other selectors, so a wrong low byte could hit another function: the input must be exactly `0x10007` |
| Duration | at most 15 s, then `0x7` and confirm RPM falls |
| Testable? | **Only through RPM.** If RPM does not move after a status-0 write, the result is "no visible effect", not "unsupported" |
| Order | after W4a and W4b, so a known RPM response already exists to compare with |

### W2: MaxQ mode 2, level 0

| Item | Value |
| --- | --- |
| Call | `AcerGamingFunction.SetGamingMiscSetting` (id 22), `gmInput` UInt64 |
| Input | `0x2005` ("MaxQ" mode 2, level 0; C) |
| Expected effect | GPU power profile: the Acer cache already records MaxQ mode 2 and GPU level 0 (`MaxQ_Mode` 2, `CurrentGPUMode` 0), so this should re-state the recorded state. Effect on a real change: GPU power limit and clocks under load |
| Read-back | **none for MaxQ.** `GetGamingMiscSetting(2)` and `(9)` read the GPU switch (hybrid/discrete) support, not MaxQ. Indirect: `nvidia-smi` power limit and clocks under a GPU load, which this task did not try |
| Undo | write the previous value, which **cannot be read**; the best guess is the same `0x2005` (the cache). Otherwise set it from the Acer app's overclock page |
| Worst case | medium: the GPU runs at a different power limit than the user expects (slower, or hotter at full power). Acer's own guard (stop overclocking below 30 % battery or on unplug) is not applied when calling WMI directly |
| Testable? | **Weak.** No state read, no easy effect check at idle |
| Order | last, and only if the owner wants MaxQ control; the first three tests teach more |

### W3: performance / operation mode (Quiet, Default, Render, Edit, Extreme, Turbo)

**Not proposed, not testable now.** The UI changes it only through service pipe command 53, and the service-to-WMI mapping is not decoded (11.3). The pipe is off limits
(it checks the caller is the genuine app, section 4). A guess that it is a combination of fan, misc and LED-behaviour writes is **G** and not a basis for a write.
Decoding needs a disassembly of `PSSvc.exe`, which has not been done. There is no firmware read of the mode either (the UI reads a registry value). The recorded mode is 4 (Extreme).

### Not proposed (no value, or too risky)

| Candidate | Why not |
| --- | --- |
| `SetGamingFanSpeed` (id 16), custom fan percentages | highest risk: a low percentage under load overheats. The sequence the UI uses around it (custom mode per fan, then the speed) is only partly traced (11.2). Needs a decoded sequence and a minimum percentage first |
| `SetGamingFanTable` (id 18) | payload not decoded; this model's UI does not use it |
| `SetGamingMiscSetting` GPU/CPU overclock levels (`0x05`, `0x105`, `0x205`, `0x07`, ...) | overclocking; no read-back of the level; CPU OC is not supported on this machine anyway |
| `SetGamingMiscSetting(2 \| (mode << 8))` GPU mode switch | a hybrid/discrete change, may need a reboot; no value approved |
| Lighting (`SetGamingRgbKb`, `SetGamingKBBacklight`, `SetGamingLEDBehavior`, `SetFunction` for backlight) | payloads not decoded; keyboard backlight state read (R3) not yet run |
| `SetGamingLEDBehavior` `0x10001`, `0x410001`, `1`, `0x400001` with `SetGamingMiscSetting(0x101)`/`(1)` | boost-related pair the UI writes together; meaning is a guess |
| `AcerBiosConfigurationTool.*` | BIOS settings with a password argument. Never |

## Suggested test order (each step needs its own owner approval)

| Step | Call | Value | Stop if |
| --- | --- | --- | --- |
| 0 | baseline reads (already-approved sensor reads) | n/a | any value odd |
| 1 | W4a `SetGamingFanBehavior` | `0x01410019` | non-zero status, or RPM leaves the baseline range. **DONE in B4: status 0, RPM unchanged** |
| 2 | W4b `SetGamingFanBehavior` for at most 15 s | `0x02820019` | RPM does not rise in 10 s, or temperature above 90 C; then undo with step 1 |
| 3 | step 1 again | `0x01410019` | RPM does not come back towards the baseline in 30 s: use the Acer app's Auto button |
| 4 | W1 `SetFunction` for at most 15 s | `0x10007` | non-zero status; then `0x7` |
| 5 | `SetFunction` | `0x7` | RPM does not fall: use the Acer app's Cool Booster toggle |
| 6 (optional) | W2 `SetGamingMiscSetting` | `0x2005` | non-zero status |

W3, custom speed, fan table, overclock levels and lighting: no steps until a decode and a separate approval.

## What is testable, given that the UI has no firmware read

| Candidate | State read-back | Effect check | Verdict |
| --- | --- | --- | --- |
| W4a fan Auto | none (cache only) | RPM unchanged | testable as a **format and status** test only. **Run in B4: status 0, RPM unchanged** |
| W4b fan Max | none | RPM indexes 2, 6, 9 rise | testable by **effect** |
| W1 Cool Booster | read answered `0xE1`: unproven | RPM, maybe | testable by effect, state unproven |
| W2 MaxQ | none | `nvidia-smi` under GPU load, not tried | weak |
| W3 operation mode | none | none known | **not testable** |
| Custom speed / table / OC / lighting | none | n/a | not proposed |

## Reads that need their own approval (not run, list for the coordinator)

Held by the coordinator after R2 (traced to the Acer UI's own read code, the owner's sentence did cover them, they are waiting for R2's `0xE1` to be understood):

| Call | Input | Why | Where traced |
| --- | --- | --- | --- |
| `AcerGenericMethod.GetFunction` R3 | `0x80184` (hotkey 132 from the registry) | keyboard backlight brightness / off; uses the same method that just answered `0xE1` | 11.4 |
| `GetGamingSysInfo` | `2` | battery boost flag, bits 40 to 47 | 3.3 |
| `GetGamingMiscSetting` | `2`, `9` | GPU mode support and capability | 3.3 |
| `GetGamingLEDBehavior` | `1` | boost indicator, meaning guessed | 11.5 |

Not covered by the owner's sentence, and **not traceable to any Acer UI read** (the UI never calls them), so they stay forbidden unless the owner approves a guess knowingly:

| Call (id) | What I would call, and why | Risk |
| --- | --- | --- |
| `GetGamingFanBehavior` (15), UInt32 in | the only possible read of the real fan mode; by symmetry with the write the input might be the fan mask (`0x19`) but that is a guess | the MOF marks every method read and write; an untraced input could act as a Set |
| `GetGamingFanSpeed` (17), UInt32 in | the fan set-point; the input might be the fan id (1 CPU, 4 and 5 GPU) | same |
| `GetGamingFanTable` (19), no input | fan curve; this model's UI does not use the table | same |
| `GetGamingProfileSetting` (9), `GetGamingRgbKb` (7), `GetGamingKBBacklight` (21), `GetGamingLEDColor` (13) | lighting and profile state | same |
| operation-mode reads (pipe 51, 52) | the service handlers exist; the pipe is off limits and the UI never sends them | not callable by a module |

If the owner wants the real fan mode read back, the honest route is to decode `PSSvc.exe` (what it sends for `get_operation_mode` and the fan getters) before guessing an input.
