# Probe and recipes

## Probe (`tools/predatorsense-probe`)

```powershell
.\tools\predatorsense-probe\build.ps1                       # csc.exe from .NET Framework, no SDK
.\tools\predatorsense-probe\bin\predatorsense-probe.exe     # text; add --json for JSON
```

Exit code: 0 supported (service and `AcerGamingFunction` both present), 1 not supported, 2 bad arguments.
Default run calls no WMI method, opens no pipe, writes nothing. Run it from an elevated shell to also get the ACPI thermal zones.

The one firmware read behind a flag: `--allow-call --sysinfo-index N` (N 1..17, one per run, elevated). Do not run it without an
answered gate for that index. Only index 1 was ever approved and run (`0x0101`, raw `0x4900`, value 73). Run it elevated like this and
read the file afterwards (an elevated process cannot be stopped from a normal shell, so let it exit by itself):

```powershell
$o = "$PWD\tools\predatorsense-probe\bin\out.txt"
Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList '-NoProfile','-Command',"& '$PWD\tools\predatorsense-probe\bin\predatorsense-probe.exe' --allow-call --sysinfo-index 1 *> '$o'"
Get-Content $o
```

Any other WMI method (Cool Booster read, backlight read, ...) needs the probe extended for that exact call and its own approval first.

## Reading what PredatorSense has recorded

`HKLM\SOFTWARE\OEM\PredatorSense` (`FanControl\CurrentFanMode`, `Overclock\CurrentOperationMode`, `Overclock\MaxQ_Mode`, ...) and
`C:\ProgramData\oem\PredatorSense\{HW_Support,Feature,PredatorSense}.ini`. Registry values are strings or numbers; the app's own records, possibly stale.

## Checklist before any new WMI call in code

1. Is the exact input traced to the Acer UI's code (helper name, line in the notes)? If not, stop.
2. Is it a Get or a Set? Sets need their own gate, with the previous value read first and an undo value.
3. Elevated? `root\WMI` instance access is denied otherwise.
4. Does the call go through a fault boundary with a short timeout in the host (WMI-ACPI calls can stall)?
5. Does the module re-read state after resume instead of trusting its own memory?
