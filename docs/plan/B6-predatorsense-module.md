# Task B6: the PredatorSense module (read-only live; writes built but never run live)

Goal: a real Rigbay module, `modules/predatorsense/`, that shows this machine's Acer sensors and controls the fan mode, using only what is documented and tested in `docs/predatorsense/`. It will later move to its own repo `module-predatorsense` in the org (the owner's step), so keep it self-contained: its own folder, its own solution file, a `ProjectReference` to `src/Rigbay.Abstractions` only (no reference to Core or App).

Read first: `docs/plan/WORKER-RULES.md`, `docs/predatorsense/FINDINGS.md` (all of it, esp. 11 to 13), `docs/predatorsense/WRITE-PROPOSALS.md`, `docs/predatorsense/B4-w4a-run.txt`, `.claude/skills/predatorsense/SKILL.md`, `docs/ABSTRACTIONS.md` (the whole contract and "How to write a module"), `samples/Rigbay.SampleModule`, `docs/APP.md`, the existing probe `tools/predatorsense-probe` and write tool `tools/predatorsense-write` (reuse their facts, write clean module code, do not copy their command-line plumbing). Load the `impeccable` skill before you design the page.

## What the module does
1. **Probe**: supported only if the `PSSvc` service exists AND the WMI class `root\WMI:AcerGamingFunction` is present. Unsupported gives a plain reason (key + text), never an exception. No model name is hard-wired: the model shown comes from WMI/registry.
2. **Readings** (read-only, approved calls only): `GetGamingSysInfo` with input `1 | (index << 8)` for indexes 1 CPU temp, 3 system temp, 10 GPU temp, 2 CPU fan, 6 and 9 GPU fans (value = `(raw >> 8) & 0xFFFF` when the status byte is 0; a non-zero status or 0 on an absent part = "not available", never a crash). Poll every 2 s while the page is open or any subscriber exists; every 10 s otherwise; one call is about 11 ms; use the fault boundary timeout. Publish to the metrics bus with stable ids (`cpu.temp`, `gpu.temp`, `system.temp`, `fan.cpu.rpm`, `fan.gpu1.rpm`, `fan.gpu2.rpm`; units degC and rpm; source `predatorsense`). Note on the page that the Acer firmware CPU sensor can read about 10 C above the Windows thermal zone (different sensors; see FINDINGS 12).
3. **Page** (declarative rows, level 1): enable row; "Readings" group (live rows, one `Summary` for the dashboard bay: CPU temp + CPU fan); "Cooling" group with one choice row **Fan mode: Auto / Maximum**, flagged `ChangesHardware`, with the effect text ("Maximum runs all fans at full speed and is loud"), confirmation through the host approval service, read-back from the RPM readings after the write (RPM rising or returning toward baseline within 10 s, shown as a status line, NOT as proof of the mode), and a Revert. There is no firmware read of the mode: show "Last set by Rigbay: Maximum at 18:42" and say plainly that PredatorSense may change it. **Only these two writes exist**: `SetGamingFanBehavior` with `0x01410019` (Auto) and `0x02820019` (Max), using the same allow-list idea as `tools/predatorsense-write` (a hard-coded table, any other call refused in code, a timeout on every call, one write at a time). Do NOT add Cool Booster, MaxQ, fan curves, operation modes, lighting, battery limits (not decoded or not approved).
4. **Safety behaviour**: when the module stops (disabled, host exit, fault) and Rigbay had set Max, restore Auto (setting `restoreAutoOnExit`, default on). If a reading call fails 5 times in a row, publish "not available" and stop writes. Never write while the readings are not available.
5. **Settings** (per-module store): poll interval, `restoreAutoOnExit`. **Hotkey** (optional, off by default): toggle Max/Auto; a hotkey press goes through the same approval service.
6. **Verbs**: `rigbay predatorsense status` (prints readings), `rigbay predatorsense fan auto|max` (asks for approval; exit 1 if not confirmed).
7. **Language**: `lang/en.lang` and `lang/vi.lang`, keys `module.predatorsense.*`, `LangCheck` clean.
8. **Manifest** `module.json`: id `predatorsense`, group `cooling`, `requires` honest (admin, WMI root\WMI, service PSSvc read-only).

## Layers (testable without hardware)
`IAcerWmi` (interface: `ReadSysInfo(index)`, `SetFanBehavior(value)`) with the real implementation over System.Management and a fake for tests; the module logic only uses the interface. Unit tests for: probe outcomes, decoding of raw values, odd statuses, absent parts, polling and the metrics, the allow-list refusing everything else, approval declined = no call, approval accepted = exactly one call with the exact value, restore-on-exit, hotkey path, verbs, language keys.

The host loader rule for `System.*` packages (System.Management is one) is task A6 (branch `feat/loader-rule`); merge it (or `main` once it landed) before you build the real WMI layer.

## Live verification you MAY do (this machine, elevated, read-only)
Load the module in the real app (use the app scripts in `tools/`; give the app a modules folder containing your module output) and look at: the dashboard bay and the module page in dark and light (render the window's own HWND, never the real screen, and LOOK at the PNGs), the live readings against `nvidia-smi` and the earlier probe output, the Probe on this machine, the verb `status`. Reads only.

## Live verification you MUST NOT do
No write on the real hardware. Not even Auto. The fake WMI covers the write path. The owner approves live writes separately (task B5 for Max); tell the coordinator exactly which live write test you would want afterwards.

## Areas you own
`modules/predatorsense/`, `.claude/skills/predatorsense/` (update it), `docs/predatorsense/MODULE.md` (how the module works, in your words), and an entry in `Rigbay.sln` for the module if it builds without hardware. Do not edit `src/*` or `tests/*` (mail the coordinator).

Finish: `csm task done <id> --result "<one line>"`, `csm mail send job:rigbay-1 --type result --subject "B6 done" --body "<short>"`, last message begins `RESULT: done|failed|blocked` with verified / not verified.
