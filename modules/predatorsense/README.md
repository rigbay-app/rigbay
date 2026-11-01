# PredatorSense module for Rigbay

Temperatures and fan speeds of an Acer Predator laptop, and the fan mode (Auto or Maximum). Windows, administrator, needs the Acer
PredatorSense service (`PSSvc`) and the firmware's `root\WMI:AcerGamingFunction`. Read `docs/predatorsense/MODULE.md` in the Rigbay
repository for how it works, what was verified and what was not.

```
dotnet build Rigbay.PredatorSense.sln -c Release      # output: src/Rigbay.PredatorSense/bin/Release/net8.0 (copy to <modules root>\predatorsense)
dotnet test  Rigbay.PredatorSense.sln                 # unit tests against a fake firmware; no hardware is touched
```

- Only two things ever reach the firmware: reading six sensor indexes, and writing one of two fan values (`Wmi/WmiAllowList.cs`).
- `docs/evidence/` has the screenshots of the page (dark, light, Vietnamese, the dashboard bay, the approval dialog) and three runs of
  `rigbay predatorsense status` next to `nvidia-smi`.
- When this folder becomes its own repository, replace the `ProjectReference` to `Rigbay.Abstractions` in the csproj with the NuGet package
  (the comment in the csproj shows the line).
