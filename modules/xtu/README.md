# XTU profiles module

Rigbay module `xtu` (group `power`): Intel XTU power-limit profiles, hotkeys, tray items, auto-switch by power source, CPU readings on the metrics bus,
and `rigbay xtu status | list | apply <name>`. In process on .NET 10; it talks to the XTU service over WCF named pipes with its **own copies** of the
few contract types it uses (`Rigbay.Module.Xtu/Intel/Contracts.cs`), so no Intel file is needed to build, test or run it.

- Module: `Rigbay.Module.Xtu/`. Tests (no hardware, no XTU): `Rigbay.Module.Xtu.Tests/`. Read-only live check: `tools/live-read.ps1`.
- Solution: `Rigbay.Xtu.slnx`. This folder is self-contained (own `Directory.Build.props`); its only dependency outside it is the `ProjectReference` to
  `Rigbay.Abstractions` (in a separate repository, replace it by the NuGet package).
- Full description, safety boundary, verification record and the live-apply test to ask the owner for: `docs/xtu/README.md` in the Rigbay repository.
  Recorder hand-off: `docs/xtu/RECORDER-NOTES.md`. How the Intel build dependency was removed: `docs/xtu/CLITE-PLAN.md`.

## Building

**It builds anywhere**, with .NET 10 and no Intel XTU installed (for example on GitHub's `windows-latest` runner). Nothing of Intel's is referenced,
copied or redistributed: the module declares its own interface and data types with the same names, namespaces and member shapes as the service uses
on the wire (shapes only, written from member lists), and builds the WCF client from them (`System.ServiceModel.*` packages).

```
dotnet build Rigbay.Xtu.slnx -c Release     # 0 warnings expected
dotnet test  Rigbay.Xtu.slnx -c Release     # no XTU needed
```

The tests compare the copies with a recording of the real service made once, read-only (`Rigbay.Module.Xtu.Tests/wire/`, made by `tools/record-wire.ps1`;
`tools/dump-contracts.ps1` prints the contract metadata of an installed XTU). At run time the module still talks to the real service, so it only works
on a machine that has the XTU service (`XTU3SERVICE`) running; the probe checks the service and its two pipes.

Only four controls are ever written (PL1, PL2, PL2 enable, time window), after approval.

Version 0.1.2 (Unreleased). Needs Rigbay 0.1.5 or newer (host API 0.1.4); see `CHANGELOG.md`.

**For other modules (host API 0.1.4).** `rigbay xtu status` and `list` are declared read-only. `rigbay xtu apply <profile>` is declared as a write of the CPU power limits (hardware); its preview is the profile's own values ("Gaming: PL1 45 W · PL2 65 W · 28 s", from the profile list, never from what is in force now), and a run that changed something reports the profile that was in force before, so the host can put it back with `apply <that profile>`. When the old values matched no profile there is nothing to report and that step is not undone. The monitor (temperature, power, clock, usage) is also read when another module such as Watch asks for it in the background, at the asked interval but never faster than once a second; with nobody asking it is still not read at all.
