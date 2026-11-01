# predatorsense-write

The only tool in this repo that may write to the Acer firmware, and only the writes in the `Writes[]` allow-list of `Write.cs`, which are exactly the writes the
owner approved. Today: **one** entry, `W4a` = `root\WMI:AcerGamingFunction.SetGamingFanBehavior`, `gmInput` = `0x01410019` (all fans Auto). The value is a constant
in code; it cannot be given on the command line. `tools/predatorsense-probe` stays read-only.

Build: `powershell -ExecutionPolicy Bypass -File .\build.ps1` (.NET Framework `csc.exe`, no SDK). Needs an elevated process.

| Command | What it does |
| --- | --- |
| (no arguments) | prints the allow-list and usage |
| `--check <Class> <Method> <hex>` | says whether the allow-list would accept the call; touches no WMI |
| `--call W4a` | dry run: AC power, battery above 50 %, CPU load, schema type, `nvidia-smi` and three baseline passes of sysinfo 1, 2, 3, 6, 9, 10; **no write** |
| `--call W4a --execute` | the same, then the one write (15 s timeout), then sysinfo 2, 6, 9, 1, 10 every 2 s for 20 s, then `nvidia-smi` |
| `--log <path>` | log file (default `predatorsense-write.log` next to the exe) |

Every event is appended to the log with UTC and local time, the call, the input, the raw answer and the status. A logged `WRITE_ATTEMPT` blocks any further `--execute` on
that log (every write needs a new owner approval). Alerts: a non-zero status, a read failure, a fan outside baseline -30 % / +30 %, or 90 C or more. The tool never retries and
never makes a second write; on a write timeout it exits at once. Exit codes: 0 ok, 1 non-zero status or alert, 2 refused, 3 timeout, 4 precondition failed.

Result of the first run (B4): `docs/predatorsense/FINDINGS.md` section 13, raw log `docs/predatorsense/B4-w4a-run.txt`.

```powershell
$d = "$PWD\bin"
Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList '-NoProfile','-Command',"& '$d\predatorsense-write.exe' --call W4a *> '$d\dry.txt'"
Get-Content "$d\dry.txt"
```
