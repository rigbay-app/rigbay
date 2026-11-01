# predatorsense-write

The only tool in this repo that may write to the Acer firmware, and only the writes in the `Writes[]` allow-list of `Write.cs`, which are exactly the writes the
owner approved. Today: **two** entries, both `root\WMI:AcerGamingFunction.SetGamingFanBehavior`: `W4a` = `gmInput` `0x01410019` (all fans Auto) and `W4b` = `0x02820019`
(all fans Max, run only together with W4a as its undo). The value is a constant in code; it cannot be given on the command line. `tools/predatorsense-probe` stays read-only.

Build: `powershell -ExecutionPolicy Bypass -File .\build.ps1` (.NET Framework `csc.exe`, no SDK). Needs an elevated process.

| Command | What it does |
| --- | --- |
| (no arguments) | prints the allow-list and usage |
| `--check <Class> <Method> <hex>` | says whether the allow-list would accept the call; touches no WMI |
| `--call W4a` | dry run: AC power, battery above 50 %, CPU load, schema type, `nvidia-smi` and three baseline passes of sysinfo 1, 2, 3, 6, 9, 10; **no write** |
| `--call W4a --execute` | the same, then the one write (15 s timeout), then sysinfo 2, 6, 9, 1, 10 every 2 s for 20 s, then `nvidia-smi` |
| `--call W4b --execute` | the same preconditions and baseline, then W4b (Max), sysinfo 2, 6, 9, 1, 10 every 2 s for 14 s (hard cap 15 s), then the undo W4a, then sysinfo every 2 s for up to 60 s until the fans are back in the baseline range. Abort rules end the window at once and run the undo: non-zero status or error of the Max write, a failed read, 90 C or more, no rise of 300 RPM over the baseline max within 10 s. Exit 5 = undo not confirmed, press Auto in the Acer app |
| `--log <path>` | log file (default `predatorsense-write.log` next to the exe) |
| `--undo-log <path>` | W4b only: the log of the undo write (default: the `--log` name with `-undo` before the extension). Each write attempt has its own log |

Every event is appended to the log with UTC and local time, the call, the input, the raw answer and the status. A logged `WRITE_ATTEMPT` blocks any further `--execute` on
that log (every write needs a new owner approval). Alerts: a non-zero status, a read failure, a fan outside baseline -30 % / +30 %, or 90 C or more. The tool never retries and
never makes a second write; on a write timeout it exits at once. Exit codes: 0 ok, 1 non-zero status or alert, 2 refused, 3 timeout, 4 precondition failed.

Result of the first run (B4): `docs/predatorsense/FINDINGS.md` section 13, raw log `docs/predatorsense/B4-w4a-run.txt`.
Result of the second run (B5, W4b then the W4a undo): section 14, raw logs `B5-w4b-run.txt` and `B5-w4b-undo-run.txt` in the same folder.

```powershell
$d = "$PWD\bin"
Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList '-NoProfile','-Command',"& '$d\predatorsense-write.exe' --call W4a *> '$d\dry.txt'"
Get-Content "$d\dry.txt"
```
