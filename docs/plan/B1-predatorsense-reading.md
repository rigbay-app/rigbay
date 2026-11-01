# Task B1: PredatorSense interface reading (read-only)

Track B1: read how Acer PredatorSense really controls the hardware on this machine (READ-ONLY).

## Goal
Understand the real interface between the installed PredatorSense V3 and the hardware, so a Rigbay module can do the same things later. Features to look for: fan modes and fan curves, performance / turbo mode, keyboard lighting, battery charge limit, and whatever else exists (GPU mode, power plans, sensors/temperatures, etc).

## What is installed (verified by the coordinator)
- Store package `AcerIncorporated.PredatorSenseV30` 3.0.3198.0, at `C:\Program Files\WindowsApps\AcerIncorporated.PredatorSenseV30_3.0.3198.0_x64__48frkmn4z8aw4` (use `Get-AppxPackage` to get the path; reading may need admin).
- Windows service `PSSvc` = "Predator Service", `C:\Program Files\Acer\PredatorSense Service\PSSvc.exe`, start mode Manual, currently running. Look also for other Acer services/drivers/WMI providers/scheduled tasks that PredatorSense uses (`Get-Service`, `Get-CimInstance Win32_SystemDriver`, `Get-WmiObject -Namespace root\wmi -List`, `root\WMI` classes with Acer/Predator/ACPI names, etc.). Listing and querying these is read-only.

## Tool: REA (reverse-engineering helper, https://github.com/morluto/rea)
A working copy with node_modules installed from an earlier session: `<scratch>/rea-tool` (package `rea-agents`; bins in `node_modules\.bin`: `rea`, `rea-agents`). It was used before to read Intel XTU's service contracts; its earlier JSON output is in `out\` there (shows the style of result). Read its package README (`node_modules\rea-agents\README.md`) and `skills\` folder first. If that folder is gone, `npm install rea-agents` into a NEW folder outside this repo (installing an npm package into a scratch folder is allowed; do not install anything system-wide). Keep REA output and any extracted/decompiled material OUTSIDE the repo (use your scratchpad or a temp folder); only your own written findings go into the repo.
The app is probably .NET (UWP/WinUI/WPF or Electron) and the service probably .NET or native; REA can inspect .NET assemblies, JavaScript/Electron and native binaries. Find out which.

## Questions the findings must answer, with evidence
1. How does the UI talk to the service: named pipe, local socket/HTTP, WMI class, COM, driver IOCTL, Windows ACPI method via a driver, shared file? Give the exact endpoint names (pipe name, WMI namespace/class/method, device path, IOCTL codes...).
2. The exact calls for each feature: name, argument types, ids, value ranges, what they return. Mark each call `read` or `write` (write = changes machine state). Say how you decided.
3. How the service talks to the firmware/EC (WMI `AcpiTest`-style method? vendor DLL? driver?). What privilege is needed (admin, SYSTEM, signed caller)? Does the service check the caller?
4. How fan curves, performance mode, charge limit, lighting are persisted (service config, EC, registry, files) and what resets them (reboot, sleep, mode change).
5. What can be READ safely right now (fan RPM, temperatures, current mode, battery limit state, ...), and which calls give it.
6. Anything risky or surprising (calls that look like reads but change state, limits enforced by firmware, values that can overheat/overdrive hardware).
7. How a different machine would differ (what a Probe should check so the module says "this machine is not supported": service present? WMI class present? model string?).

## Deliverables, in this order (stop after each numbered item if you hit a gate)
1. `docs/predatorsense/FINDINGS.md` in your worktree: the real interface, in your own words, with a table per feature (call, args, range, read/write, evidence = which installed file/class you saw it in). State clearly what is confirmed by reading code, what by observing the running system, and what is only a guess.
2. A **read-only probe tool** under `tools/predatorsense-probe/` that only reads state and prints it (and `--json`). It must be buildable on this machine WITHOUT a .NET SDK (the .NET Framework `csc.exe` at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, or PowerShell, or Node). Include a `build.ps1` or `run.ps1`, and a README line. Run it and paste the real output (trimmed) in FINDINGS.md. If the only way to read a value is a call you cannot prove is read-only, do not run it: put it in FINDINGS.md as "needs approval" and open a gate.
3. A draft project skill under `.claude/skills/predatorsense/SKILL.md` (+ `references/` if useful) in the style of `D:\Documents\Projects\driver-setup\xtu-switcher\.claude\skills\xtu-switcher\SKILL.md`: hard constraints, interface facts, gotchas, verified vs not verified, recipes.
4. Do NOT write to the hardware. Write paths come only after the user approves, one by one. In FINDINGS.md end with a proposed list of write operations (exact call, value, expected effect, risk, how to undo) so the coordinator can ask the user.

## Areas you own (touch nothing else)
`docs/predatorsense/`, `tools/predatorsense-probe/`, `.claude/skills/predatorsense/`.
Commit on your branch in small steps. Read `csm note` before overwriting it; update it at checkpoints (in Vietnamese).
