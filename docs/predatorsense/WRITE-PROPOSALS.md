# Write proposals (task B3), with the results of the first two tests (tasks B4 and B5)

Status: **three write calls have been run on this machine, all with status 0: W4a (all fans Auto) in B4 ("W4a result" below), then in B5 W4b (all fans Max, 14 s)
and W4a again as its undo ("W4b result" below).** Every other candidate (W1, W2) is still unrun. No write was run in B1, B2 or B3. The probe
(`tools/predatorsense-probe`) refuses every `Set*` method in code. Writes go only through `tools/predatorsense-write`, whose code allow-list holds the
owner-approved calls (today: W4a and W4b; each write attempt has its own log and the log blocks a repeat).
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

#### W4b result (B5, run 2026-11-01 20:21:44 to 20:22:07 local; gate `g-write-w4b`, owner answered "approve": two calls, once each)

Run by `tools/predatorsense-write` (`--call W4b --execute`), elevated, on AC power at 100 %, CPU load 24 % at the precondition check (other workers were building;
the threshold of 50 % was not touched, no precondition retry was needed). The tool sends W4b, reads 2, 6, 9, 1, 10 every 2 s for 14 s, then sends the undo W4a
by itself (its own log) and reads every 2 s until the three fans are inside the baseline range twice in a row. Raw logs: `B5-w4b-run.txt` (baseline, W4b, window)
and `B5-w4b-undo-run.txt` (W4a undo, recovery). Each holds exactly one `WRITE_ATTEMPT`.

| Item | Result |
| --- | --- |
| Schema check | `gmInput` is UInt64 (as in B4) |
| Baseline (3 passes, 2 s apart, 5 s before the write) | CPU fan 2586 to 2608, GPU fan 1 2631 to 2654, GPU fan 2 2693; CPU 62 to 75 C, system 40 to 41 C, GPU 50 C; `nvidia-smi` 50.84 C, 17.4 W, 24 %, P5 |
| W4b call | `SetGamingFanBehavior`, `gmInput` = `0x02820019`, once: `gmOutput` raw `0x0`, **status 0**, **13 ms**, no exception |
| Read right after (t = 0, about 16 ms after the answer) | CPU fan 2608, GPU 1 2631, GPU 2 2693: **still the baseline** |
| t = +2 s | CPU fan **5555**, GPU fan 1 **5882**, GPU fan 2 **5900** (about 2.1x, 2.2x, 2.2x the baseline) |
| t = +4 s to +14 s (6 more passes) | the same three numbers on every pass: 5555, 5882, 5900 (flat, no further rise, no jitter) |
| Temperatures during the 14 s | GPU (index 10) 50, 50, 50, 50, 50, 51, 49, 49 C. CPU (index 1) 62, 62, 62, 70, 76, 63, 58, 56 C |
| Why the window ended | the planned 14 s of reads were over (`max_window_ms` 14043); no abort rule fired (all statuses 0, rise seen at +2 s, highest temperature 76 C) |
| W4a undo, 13 ms after the last read | `SetGamingFanBehavior(0x01410019)`: raw `0x0`, **status 0**, **13 ms** |
| Recovery | +0 s (3 ms after the answer) 5555 / 5882 / 5900; **+2 s 2654 / 2631 / 2757**; +4 s 2884 / 2857 / 2929. Inside the baseline range (-30 %/+30 %: 1825..3390, 1841..3450, 1885..3500) at +2 s and +4 s, so "back" was declared at 4.0 s |
| After | `nvidia-smi` 49.34 C, 15.9 W, 13 %, P8; CPU 59 then 70 C, GPU 49 C. Exit code 0. No second write, no retry, no other `Set*` |

- **The answer to "does the write take effect": yes, in the fan speed.** After `SetGamingFanBehavior(0x02820019)` all three fans (CPU fan, GPU fan 1, GPU fan 2) went from about
  2600 to 2700 to 5555 to 5900 on the Acer sensor within 2 s, held that value for the whole 14 s, and the undo `0x01410019` brought them back to the Auto range within 2 s.
  The change is hundreds of RPM (about +2900 to +3200), far above the one-step jitter of Auto (22 to 24 RPM). This is the first write on this machine with a measured effect.
- **Verified (O):** the firmware accepts W4b with status 0 in 13 ms; the three fan speeds respond as the decoded table predicts (mode 2 in slots 0, 3, 4 = Max); the undo W4a
  is accepted with status 0 and the speeds return to the Auto range within 2 s; sensor reads keep working during Max; no temperature came near 90 C.
- **Not verified:**
  - **The mode itself**: there is still no read of the fan mode, so "all fans are in Max/Auto" is inferred from RPM only. After the undo the speeds (2654 to 2757, then 2857 to 2929) were
    in the Auto range but at +4 s about 200 to 300 above the pre-write baseline (CPU at 70 C then); whether that is normal Auto behaviour at that load, a still-settling fan or something else was not tested.
  - **When the rise starts**: the first read (about 16 ms after the answer) still showed the baseline and the next (+2 s) showed the full value, so the ramp time is somewhere in 0 to 2 s; not resolved.
  - **That 5555/5882/5900 is the fans' maximum** (it is flat for 12 s, which suggests a limit, but no other value was tried); that these numbers are RPM (unit still a working assumption, G+).
  - **Cooling effect**: the temperatures cannot be read as an effect of Max. The CPU value moved with load from other builds on this machine (62, 70, 76, then 56 C), and the GPU stayed at 49 to 51 C at idle.
  - **What the owner heard or felt** (not reported to this worker); noise level; any side effect other than RPM.
  - Whether `PSSvc` re-applies its own cached mode later (observed for 14 s of Max and 4 s after the undo only); survival across sleep or reboot; behaviour under load; W1, W2 (not run).
- **For a module:** a fan-mode write can be checked through sysinfo 2, 6, 9 with a threshold of a few hundred RPM, and a module should hold Max only briefly and always send the Auto value afterwards.
  The tool's rule "undo in its own log, always, even on an abort" worked as designed and was needed by no abort in this run.

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
| 2 | W4b `SetGamingFanBehavior` for at most 15 s | `0x02820019` | RPM does not rise in 10 s, or temperature above 90 C; then undo with step 1. **DONE in B5: status 0, RPM 2600 to 2700 -> 5555 to 5900 within 2 s, 14 s window** |
| 3 | step 1 again | `0x01410019` | RPM does not come back towards the baseline in 30 s: use the Acer app's Auto button. **DONE in B5: status 0, RPM back in the Auto range at +2 s** |
| 4 | W1 `SetFunction` for at most 15 s | `0x10007` | non-zero status; then `0x7` |
| 5 | `SetFunction` | `0x7` | RPM does not fall: use the Acer app's Cool Booster toggle |
| 6 (optional) | W2 `SetGamingMiscSetting` | `0x2005` | non-zero status |

W3, custom speed, fan table, overclock levels and lighting: no steps until a decode and a separate approval.

## What is testable, given that the UI has no firmware read

| Candidate | State read-back | Effect check | Verdict |
| --- | --- | --- | --- |
| W4a fan Auto | none (cache only) | RPM unchanged | testable as a **format and status** test only. **Run in B4: status 0, RPM unchanged** |
| W4b fan Max | none | RPM indexes 2, 6, 9 rise | testable by **effect**. **Run in B5: status 0, RPM rose from about 2600 to 2700 to 5555 to 5900 within 2 s, undo W4a brought it back** |
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
