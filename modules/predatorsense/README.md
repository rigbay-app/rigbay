# PredatorSense module for Rigbay

Temperatures, fan speeds and the performance mode (read only) of an Acer Predator laptop, and the fan mode (Auto or Maximum). Windows, administrator, needs the Acer
PredatorSense service (`PSSvc`) and the firmware's `root\WMI:AcerGamingFunction`. Read `docs/predatorsense/MODULE.md` in the Rigbay
repository for how it works, what was verified and what was not.

```
dotnet build Rigbay.PredatorSense.sln -c Release      # output: src/Rigbay.PredatorSense/bin/Release/net8.0 (copy to <modules root>\predatorsense)
dotnet test  Rigbay.PredatorSense.sln                 # unit tests against a fake firmware; no hardware is touched
```

- Only these ever reach the firmware: reading six sensor indexes, two read-only performance-mode reads (`GetGamingMiscSetting` 0x0A and 0x0B), and writing one of two fan values (`Wmi/WmiAllowList.cs`). Version 0.1.3 (Unreleased). Needs Rigbay 0.1.5 or newer (host API 0.1.4); see `CHANGELOG.md`.

**For other modules (host API 0.1.4).** `rigbay predatorsense status` is declared read-only. `rigbay predatorsense fan auto|max` is declared as a hardware write (the same effect text the page asks with); its preview is the mode the argument names ("Fan mode: Maximum (all fans at full speed)"), never the state of the fans. A run that succeeded and changed the mode reports how to put it back: `max` reports `auto`, and `auto` after Rigbay had set Maximum reports `max`. `auto` when Rigbay had set nothing reports nothing (there is no read of the firmware's fan mode, so nothing is known to restore); `max` when Rigbay had set nothing reports `auto`, which is what the module itself does when it stops. The sensors are read at least as often as another module such as Watch asks for in the background (never faster than the poll rate, never slower than every 10 s), and the performance mode, which is never read while nobody looks, is also read when a background reader asks for `predatorsense.opmode` (at the asked interval, not faster than every 10 s).
- `docs/evidence/` has the screenshots of the page (dark, light, Vietnamese, the dashboard bay, the approval dialog) and three runs of
  `rigbay predatorsense status` next to `nvidia-smi`.
- When this folder becomes its own repository, replace the `ProjectReference` to `Rigbay.Abstractions` in the csproj with the NuGet package
  (the comment in the csproj shows the line).
