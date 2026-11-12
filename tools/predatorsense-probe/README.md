# predatorsense-probe

Read-only check of how Acer PredatorSense is set up on this machine. Build with `powershell -ExecutionPolicy Bypass -File .\build.ps1` (uses the .NET
Framework `csc.exe` that ships with Windows, no SDK; the bypass is for that one process only), run `bin\predatorsense-probe.exe` or add `--json`.

It reports the model, whether the `PSSvc` service and its pipes are there, whether the Acer gaming WMI class exists
(schema only), the settings PredatorSense itself recorded in the registry and `C:\ProgramData\oem\PredatorSense`,
and the plain Windows battery and thermal-zone readings. Exit code 0 means "this machine looks supported" (service
and WMI class both present), 1 means not, 2 means bad arguments or a refused call.

By default it calls **no WMI method**, opens no pipe, and writes nothing.

## Firmware reads (off by default)

A short list of firmware **reads** exists, each behind its own flag and only together with `--allow-call`. Each needs the owner's approval for that exact
call, and an elevated shell (instance access under `root\WMI` is denied to a normal user). Flags run in the order given, once each; the run halts at the
first exception or non-zero status.

| Flag | Call |
| --- | --- |
| `--sysinfo-index N` (1..17) | `AcerGamingFunction.GetGamingSysInfo`, input `1 \| (N << 8)` (sensors; 1 = CPU temperature) |
| `--r1` | `GetGamingSysInfo` `0x0201` (CPU fan speed), the same call as `--sysinfo-index 2` |
| `--r2` | `AcerGenericMethod.GetFunction` `0x207` (Cool Booster state) |
| `--r3` | `AcerGenericMethod.GetFunction` `0x80184` (keyboard backlight; hotkey number 132 is machine specific) |
| `--battery-boost` | `GetGamingSysInfo` `0x2` |
| `--misc-get 2\|9\|10\|11` | `AcerGamingFunction.GetGamingMiscSetting` `2` or `9` (GPU mode support / capability), `10` = `0x0A` (operation-mode capability), `11` = `0x0B` (current operation mode; B10, owner-approved 2026-11-12) |
| `--self-test-refusals` | no WMI, no `--allow-call`: checks that every other `GetGamingMiscSetting` input (0 to 0x1FF, the mode-write values, large values), every `Set*` name and a wrong class is refused |
| `--led-behavior-get` | `AcerGamingFunction.GetGamingLEDBehavior` `1` |

`Refusal()` in `Probe.cs` is the allowlist (exact class, method and input). Anything else, any method whose name starts with `Set`, a repeated call and a run
without `--allow-call` are refused before WMI is touched. There is no write path in this tool. Values come from the PredatorSense UI's own read code; see
`docs/predatorsense/FINDINGS.md`. Results and approvals so far: `FINDINGS.md` sections 11 and 12.

Run elevated, output to a file, and let the elevated process exit by itself:

```powershell
$o = "$PWD\bin\out.txt"
Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList '-NoProfile','-Command',"& '$PWD\bin\predatorsense-probe.exe' --allow-call --r1 *> '$o'"
Get-Content $o
```
