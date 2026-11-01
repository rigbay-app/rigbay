# predatorsense-probe

Read-only check of how Acer PredatorSense is set up on this machine. Build with `.\build.ps1` (uses the .NET
Framework `csc.exe` that ships with Windows, no SDK), run `bin\predatorsense-probe.exe` or add `--json`.

It reports the model, whether the `PSSvc` service and its pipes are there, whether the Acer gaming WMI class exists
(schema only), the settings PredatorSense itself recorded in the registry and `C:\ProgramData\oem\PredatorSense`,
and the plain Windows battery and thermal-zone readings. Exit code 0 means "this machine looks supported" (service
and WMI class both present), 1 means not, 2 means bad arguments.

By default it calls **no WMI method**, opens no pipe, and writes nothing. One firmware read, `GetGamingSysInfo` for a
single sensor, exists behind `--allow-call --sysinfo-index N`; it needs the owner's approval first
(see `docs/predatorsense/GATE-getgamingsysinfo.md`) and an elevated shell. Run it elevated for the thermal-zone readings too.
