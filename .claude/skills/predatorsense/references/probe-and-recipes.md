# Probe and recipes

## Probe (`tools/predatorsense-probe`)

```powershell
.\tools\predatorsense-probe\build.ps1                       # csc.exe from .NET Framework, no SDK
.\tools\predatorsense-probe\bin\predatorsense-probe.exe     # text; add --json for JSON
```

Exit code: 0 supported (service and `AcerGamingFunction` both present), 1 not supported, 2 bad arguments.
Default run calls no WMI method, opens no pipe, writes nothing. Run it from an elevated shell to also get the ACPI thermal zones.

Firmware reads are behind explicit flags, all together with `--allow-call`, elevated: `--sysinfo-index N` (1..17), `--r1` (sysinfo 2), `--r2` (`GetFunction(0x207)`),
`--r3` (`GetFunction(0x80184)`), `--battery-boost`, `--misc-get 2|9`, `--led-behavior-get`. An allowlist in `Probe.cs` (`Refusal()`) refuses anything else, every `Set*`, and a
repeated call; the run halts at the first exception or non-zero status. Do not run a flag without an answer from the owner (or the coordinator on the owner's words) for that call.
Run elevated like this and read the file afterwards (an elevated process cannot be stopped from a normal shell, so let it exit by itself; the file is UTF-16, so
use `Get-Content`/`Select-String`):

```powershell
$o = "$PWD\tools\predatorsense-probe\bin\out.txt"
Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList '-NoProfile','-Command',"& '$PWD\tools\predatorsense-probe\bin\predatorsense-probe.exe' --allow-call --r1 *> '$o'"
Get-Content $o
```

Gotchas seen in B3: build with `powershell -ExecutionPolicy Bypass -File build.ps1` (scripts are disabled by policy); passing a string array through `-File` joins it into one
string, so pass a comma string and split it; the probe prints the same registry cache every run, which makes a quick "did the app's record change" comparison.

To compare a reading with something else at the same moment, sample read-only sources right before and after the call: `\Thermal Zone Information(*)\High Precision Temperature`
(tenths of a kelvin, only the ACPI zone `TZ00`), `\Energy Meter(RAPL_Package0_PKG)\Power` (milliwatts), `\Processor Information(_Total)\% Processor Utility`, and `nvidia-smi
--query-gpu=temperature.gpu,...` for the GPU.

Any other WMI method needs the probe extended for that exact call and its own approval first.

## Reading what PredatorSense has recorded

`HKLM\SOFTWARE\OEM\PredatorSense` (`FanControl\CurrentFanMode`, `Overclock\CurrentOperationMode`, `Overclock\MaxQ_Mode`, ...) and
`C:\ProgramData\oem\PredatorSense\{HW_Support,Feature,PredatorSense}.ini`. Registry values are strings or numbers; the app's own records, possibly stale.

## Checklist before any new WMI call in code

1. Is the exact input traced to the Acer UI's code (helper name, line in the notes)? If not, stop.
2. Is it a Get or a Set? Sets need their own gate, with the previous value read first and an undo value.
3. Elevated? `root\WMI` instance access is denied otherwise.
4. Does the call go through a fault boundary with a short timeout in the host (WMI-ACPI calls can stall)?
5. Does the module re-read state after resume instead of trusting its own memory?
