# spike/xtu: can the XTU module live in the .NET 10 host? (task C1)

Read-only. Nothing here writes to XTU: the only XTU calls are `Connect`, `RefreshActiveValuesFromHardwareAccess`,
`GetControl`, `GetDoRestoreUserValuesOnBoot` (tuning pipe) and `Connect(pid)`, `Start(pid)`, `GetValues`, `Stop(pid)`
(monitoring pipe). Intel's `XtuInterface.dll` / `XtuCommon.dll` are referenced and loaded from their install folder; no Intel
file is copied or committed. Results and the recommendation are in `docs/spike/XTU-RUNTIME.md`.

| Folder | What it is |
| --- | --- |
| `helper/XtuHelper.cs` | Design B: net48 console helper (C# 5, OS `csc.exe`), JSON lines on stdin/stdout. `build-helper.ps1` builds it. |
| `harness/` | Design B: .NET 10 client that plays the module (spawns the helper, measures latency, lifecycle tests). |
| `inproc/XtuModule/` | Design A: the XTU read as a .NET 10 class library (WCF client packages + Intel assemblies referenced in place). |
| `inproc/Runner/` | Design A: stand-in host with its own `AssemblyLoadContext`. |
| `inproc/CoreCheck/` | Design A through the real, unchanged `Rigbay.Core.ModuleLoadContext`. |
| `shared/WireProbe*.cs` | Offline check that the write-path types serialise identically on net48 and .NET 10 (no call to XTU). |
| `run-elevated.ps1` | Runs an exe elevated and leaves its console output in a file (the XTU pipes accept administrators only). |

## Reproduce

XTU must be running (`XTU3SERVICE`); this never starts or stops it.

```powershell
cd spike\xtu
.\build-helper.ps1                                          # design B helper -> out\XtuHelper.exe
dotnet build harness\XtuHarness.csproj -c Release           # design B harness -> out\harness
dotnet build inproc\XtuModule\XtuModule.csproj -c Release   # design A module  -> out\inproc\module
dotnet build inproc\Runner\Runner.csproj -c Release
dotnet build inproc\CoreCheck\CoreCheck.csproj -c Release

# B: status read + latency (elevated, because the XTU pipes are admin-only)
.\run-elevated.ps1 -Exe out\harness\XtuHarness.exe -Log out\B-run.txt -ProgramArgs "--n 100 --cold 5"
# A: own load context / the real Rigbay.Core loader (add --no-hook to see the failure without the module's resolver)
.\run-elevated.ps1 -Exe out\inproc\runner\Runner.exe -Log out\A-run.txt -ProgramArgs "--repeat 100"
.\run-elevated.ps1 -Exe out\inproc\corecheck\CoreCheck.exe -Log out\A-core.txt -ProgramArgs "--repeat 100"
```

Pass absolute paths in `-ProgramArgs`: an elevated process starts in `C:\Windows\System32`.
Offline wire check: `out\WireProbe48.exe dump` (built with `csc` from `shared\WireProbe.cs` + `WireProbeMain.cs`) against
`Runner.exe --wire dump`; compare the two outputs.
