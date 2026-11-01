# XTU profiles module

Rigbay module `xtu` (group `power`): Intel XTU power-limit profiles, hotkeys, tray items, auto-switch by power source, CPU readings on the metrics bus,
and `rigbay xtu status | list | apply <name>`. In process on .NET 10, over Intel's own contract assemblies referenced in place (never copied).

- Module: `Rigbay.Module.Xtu/`. Tests (no hardware): `Rigbay.Module.Xtu.Tests/`. Read-only live check: `tools/live-read.ps1`.
- Full description, safety boundary, verification record and the live-apply test to ask the owner for: [`docs/xtu/README.md`](../../docs/xtu/README.md).
- Recorder hand-off (columns and meta of the old CSV recorder): [`docs/xtu/RECORDER-NOTES.md`](../../docs/xtu/RECORDER-NOTES.md).
- Building needs XTU installed (the Intel DLLs are compile references). Only four controls are ever written (PL1, PL2, PL2 enable, time window), after approval.
