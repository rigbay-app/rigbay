# XTU profiles module

Rigbay module `xtu` (group `power`): Intel XTU power-limit profiles, hotkeys, tray items, auto-switch by power source, CPU readings on the metrics bus,
and `rigbay xtu status | list | apply <name>`. In process on .NET 10, over Intel's own contract assemblies referenced in place (never copied).

- Module: `Rigbay.Module.Xtu/`. Tests (no hardware): `Rigbay.Module.Xtu.Tests/`. Read-only live check: `tools/live-read.ps1`.
- Solution: `Rigbay.Xtu.slnx`. This folder is self-contained (own `Directory.Build.props`); its only dependency outside it is the `ProjectReference` to
  `Rigbay.Abstractions` (in a separate repository, replace it by the NuGet package).
- Full description, safety boundary, verification record and the live-apply test to ask the owner for: `docs/xtu/README.md` in the Rigbay repository.
  Recorder hand-off: `docs/xtu/RECORDER-NOTES.md`. Plan to remove the Intel build dependency: `docs/xtu/CLITE-PLAN.md`.

## Building

**Building needs Intel XTU installed on the machine**: `XtuInterface.dll` and `XtuCommon.dll` are compile references, read in place from
`%ProgramFiles%\Intel\Intel(R) Extreme Tuning Utility\Drivers` (override with `-p:XtuDir=<folder>`). They are never committed, copied or redistributed.
Because of that, **the public CI cannot build this module** until the contracts are mirrored in the module (design C-lite, see `docs/xtu/CLITE-PLAN.md`);
release zips are built on the author's machine.

```
dotnet build Rigbay.Xtu.slnx -c Release     # 0 warnings expected
dotnet test  Rigbay.Xtu.slnx -c Release
```

Only four controls are ever written (PL1, PL2, PL2 enable, time window), after approval.
