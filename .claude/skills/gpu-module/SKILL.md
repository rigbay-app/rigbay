---
name: gpu-module
description: Working knowledge of the Rigbay GPU module (modules/gpu, id gpu, .NET 8, in process, READ ONLY; NVIDIA GPU through NVML P/Invoke with a 15-name allow-list, nvml.dll loaded from System32 by full path; metrics gpu.usage, gpu.memctl.usage, gpu.power, gpu.power.limit, gpu.clock, gpu.mem.clock, gpu.mem.used, gpu.mem.total, gpu.nvml.temp, gpu.pstate, gpu.throttle; reads only while the page is visible or a reader subscribes; rigbay gpu status). Use it before reading or changing anything under modules/gpu, when adding a reading, a language key or a test, when touching the poll rate, the allow-list or the Optimus behaviour, and before any live check with the real GPU. Saves re-deriving the layers, the safety rules and the traps below.
---

# GPU module: codebase guide

Module id `gpu`, group `monitoring`, `modules/gpu/src/Rigbay.Gpu` (net8.0), tests in `modules/gpu/tests/Rigbay.Gpu.Tests` (125 at the last run, version 0.1.0
unreleased, host API `>=0.1.3 <0.2`, `requires.admin` false). Written by task G1 of job rigbay-13 (2026-11-13). This repository is PUBLIC.

## Hard constraints (check before you write code)

- **Read only, enforced in code.** `Nvml/NvmlAllowList.cs` is the ONLY file that spells an NVML function (15 names: init, shutdown, count, handle, name,
  utilization, power usage, enforced power limit, clock info, memory info, temperature, performance state, clocks event reasons, its older
  "throttle reasons" name, error string). `NvmlApi.Resolve` calls `NvmlAllowList.Require(name)` **before** it asks the DLL, and there is no `DllImport`
  (function pointers from `NativeLibrary.TryGetExport`), so a name that is not on the list cannot be bound. `INvml` has no method that sets, resets or
  clears (a test checks the 14 method names). `HygieneTests` scans the source (comment lines skipped) and the compiled assembly (UTF-8 and UTF-16 strings)
  for every `nvml` + capital-letter word and fails on one that is not on the list, or that appears outside `NvmlAllowList.cs`.
  **To add a reading:** add the name to `NvmlAllowList.Names`, a method to `INvml` and `NvmlApi`, the fake in `Fakes.cs`, the expected list in `HygieneTests.ExpectedNames`, the
  count in the manifest's `requires.services` text ("15 read-only NVML calls", a test compares it), and say in the README why it reads. Never add a `Set*`, `Reset*` or `Clear*`;
  those need an owner gate (`docs/plan/WORKER-RULES.md` rule 1).
- **`nvml.dll` by full path.** `Path.Combine(Environment.SystemDirectory, "nvml.dll")` and `NativeLibrary.TryLoad(path, assembly, DllImportSearchPath.System32, ...)`; no bare name, no `LoadLibrary`, no PATH. A test pins
  the exact lines. The library stays loaded for the life of the process; NVML itself is `Init` ed when the module is turned on (`GpuPoller.Open`) and `Shutdown` when it is turned off (`Close`).
  NVML counts initialisations: every successful `Init` needs one `Shutdown` (the probe and the module balance theirs, tests check the depth).
- **One thread in NVML at a time.** The module wraps whatever it is given in `SerialNvml` (one lock around every call) in `Probe` and `Initialize`; a test counts those two
  `new SerialNvml(` and `FakeNvml.MaxInside` proves no overlap under 8 threads. Polls are serialised again in `GpuPoller` (`_pollLock`); `Close` takes that lock, so a shutdown waits for a poll that runs.
- **Reads only while somebody looks** (Optimus). `GpuModule.IsWatched`: the page is visible, or any of the module's 11 metrics has a subscriber (Dashboard, recorder, tray tooltip, another module).
  Not watched: no timer fires, NVML is not called at all (a test: 0 read calls after 300 ms with the timer on). `PollScheduler` re-arms only while watched; `PollSoon` reads at once when somebody starts to look.
  **TODO (H1):** when the host's background metric demand (task H1, branch `feat/host-scenes-watch`, not on main on 2026-11-13) lands, subscribe through it so Watch can ask for the readings at a slow rate
  without the page open; today a subscriber means "fast" (the poll rate setting, 2 s). Until then a module that only wants an alert every minute still makes the GPU read every 2 s.
- **A reading the GPU does not have is withdrawn, never 0.** `NVML_ERROR_NOT_SUPPORTED`: at once (and once, not every poll); any other failure: after 3 polls in a row (`GpuPoller.FailureLimit`), the last value stays for two polls. A performance state of 32 (NVML's "unknown")
  is treated as not supported. All metrics are withdrawn in `Shutdown`.
- **Rigbay rules** (`docs/ABSTRACTIONS.md` section 12): the constructor does nothing, `Probe` is read only (opens the library, `Init`, count, name, `Shutdown`), `Initialize` writes nothing, threads catch everything (`PollScheduler`), all text is a language key,
  `Shutdown` stops the timer, closes NVML and unhooks every handler (a test changes `PageVisible` afterwards and sees no read).
- **Manifest limits**: `requires` entries 1 to 200 characters, description up to 200, name up to 60. No `probePaths`: `nvml.dll` is a system file loaded by full path, not an assembly read from another folder.
- **Nothing about the owner's machine in the repository** beyond what the README states as "tried on" (model of the card, driver behaviour). No user name, paths of the owner's folders.

## Layout

| File | What |
| --- | --- |
| `Nvml/NvmlAllowList.cs` | the 15 names, `IsAllowed`, `Require` |
| `Nvml/INvml.cs` | `NvmlReturn` (all codes), `NvmlClock`, the read-only interface |
| `Nvml/NvmlApi.cs` | function-pointer binding over `INvmlExports`; `TryOpenSystemLibrary`; struct layouts; old/new reasons name fallback; `ErrorString` |
| `Nvml/SerialNvml.cs` | the one lock |
| `GpuPoller.cs` | `Open`/`Close`, `PollOnce` (publish, withdraw, miss counts, state Starting/Available/Degraded/Unavailable, reopen after 3 failed polls) |
| `PollScheduler.cs` | timer that runs only while `Watched`; next poll armed after the previous one ended |
| `GpuMetrics.cs` | the 11 metrics: ids, label keys, units, formats |
| `ClockReasons.cs` | the bit mask words, priority, "idle only when alone" |
| `GpuModule*.cs` | lifecycle and watching (`.cs`), state line and limit text (`.Texts.cs`), page (`.Page.cs`), `rigbay gpu status` (`.Commands.cs`) |
| `GpuProbe.cs`, `Dependencies.cs` | the probe; `Dependencies.Real()` is the only code that names `nvml.dll` |

## Decisions

- **Ids.** Load, power and clock use the shared `WellKnownMetrics` ids (`gpu.usage`, `gpu.power`, `gpu.clock`; the idea document said `gpu.load`, the code's vocabulary says `gpu.usage`). The rest are the module's: `gpu.memctl.usage`, `gpu.power.limit`, `gpu.mem.clock`, `gpu.mem.used`, `gpu.mem.total`, `gpu.pstate`, `gpu.throttle` (bit mask, for the recorder; no row shows it raw).
- **Temperature is NOT `gpu.temp`.** PredatorSense publishes `gpu.temp` from the laptop firmware; two sources on one id would alternate (newest sample wins) in the chart and the recorder. This module publishes `gpu.nvml.temp`, label "GPU core temperature", and keeps it out of its `PageSummary.Readings`
  (load and power only), so the Dashboard's one-tile-per-label rule never sees two temperatures. Cost: a PC with NVIDIA and no PredatorSense has no `gpu.temp` for generic consumers. If that matters, publish `gpu.temp` only when `GetMetricInfo("gpu.temp")` has another owner not present (not done; say so in the README).
- **Device.** The first NVIDIA GPU (index 0) only. A second GPU is not shown.
- **State line** `P0 · 72 W · power cap`: P-state, board power rounded to watts, at most two reasons (hardware power brake, hardware slowdown, thermal (both bits), power cap, sync boost, app clocks, display clocks; idle only when it is the only one). The "Clock limit" row names all of them or "Not limited".
- **Poll rate** setting 1 to 30 s, default 2 s, one number row. Not watched means not polled (idle interval is "never").
- **Logging.** First reading of a run (and after recovery): one Info line `GPU readings available: ...`; every poll: one Debug line `GPU read: ...` (set `logLevel` to `debug` in the data folder's `app.json` to see them).

## Live check recipe (own instance, never the owner's)

1. `tools\build.ps1` (refreshes `tools\out\app`), `dotnet test modules/gpu/tests/Rigbay.Gpu.Tests` (`-m:2 -nodeReuse:false`), `dotnet build-server shutdown`.
2. Stage `modules\gpu\src\Rigbay.Gpu\bin\Release\net8.0\*` (no pdb) into `tools\out\modules-gpu\gpu\`.
3. `tools\run-elevated.ps1 -Wait -Data tools\out\data-gpu -Modules tools\out\modules-gpu --shot <png> --exit-after 200 --allow-multi --shot-mode rtb --size 1240x1100 --script "enable:gpu;page:gpu;wait:30000"`.
   Run it as ONE blocking command in a background task of the harness: a `Start-Job` from a short tool call is killed when that call ends, and the elevated app dies with it before the shot.
4. The data folder is admin-only: change `logLevel` in `app.json` and read `logs\rigbay.log` through an elevated PowerShell (`Start-Process powershell -Verb RunAs`), copy to the scratchpad.
5. `nvidia-smi --query-gpu=utilization.gpu,power.draw,clocks.gr,clocks.mem,memory.used,temperature.gpu,pstate --format=csv` once, while the debug lines are being written; compare with the debug line before and after it.
6. No pipe command to the owner's Rigbay (`rigbay gpu status` from a shell goes through the pipe of whichever Rigbay owns it: do not use it; the script step `cmd:gpu status` runs in process but discards the output).

## Live result (2026-11-13, RTX 3060 Laptop, Windows 11, AC, external monitor on the NVIDIA GPU, own instance)

Module read at 11:41:54.596, one `nvidia-smi` query (the seven fields of the task plus a few more) at 11:41:54.675 to .761, GPU idle:

| | module (NVML) | nvidia-smi |
| --- | --- | --- |
| load / memory controller | 0 % / 100 % | 0 % / 100 % |
| power | 14.2 W | 14.18 W |
| graphics / memory clock | 210 / 405 MHz | 210 / 405 MHz |
| temperature | 52 | 52.19 |
| performance state, reasons | P8, idle | P8, 0x1 |
| memory used / total | 803 / 6144 MiB | 654 / 6144 MiB |

- Memory used differs by about 150 MiB: `nvmlDeviceGetMemoryInfo` (the v1 structure) probably counts driver-reserved memory in "used". Not checked. A v2 call would be a new name on the allow-list and a new owner-visible decision.
- The enforced power limit moved 65 to 90 W between reads while idle (`nvidia-smi` `power.limit` printed `[N/A]`).
- The first read of a run caught P0, 1425 MHz, 25 W, and two reads later P8, 210 MHz, 14 W. Cause not investigated (NVML waking the clocks, or the display).
- Memory controller load reads 100 % at idle on this setup in both tools (scan-out of the external monitor).
- Not done: battery, a sleeping GPU, load, throttle reasons other than idle, `tools\selftest.ps1` result is in the task mail.
- Screenshot: https://art.minhdevtree.tech/a/ciM4P6Ei1_7aqdhPWZU1YA

## Gotchas

- **Public test classes cannot take internal types** (`NvmlReturn` is internal): use `int` and cast in `[Theory]` data. CI builds the solution with `-warnaserror`, so the xunit analyzers (xUnit2013, xUnit2017, xUnit1031) are errors: `Assert.Empty`, `Assert.DoesNotContain`, `async Task` tests.
- **A lambda parameter named `_` hides the `out _` discard** inside it (`Parallel.For(0, 6, _ => api.Get(d, out _))` binds the parameter): name the parameter.
- **`Rigbay.sln` edits through Bash/Python lose backslashes** (`\t` became a TAB in `modules\gpu\tests`). Build such strings from `chr(92)`, then `git diff` the file.
- **The fake NVML library** (`FakeExports`) turns managed delegates into function pointers, so `NvmlApi`'s real marshalling (structs, `out` parameters, the 96-byte name buffer) is tested. Keep the delegate shapes in `NvmlApi` equal to NVIDIA's header.
- **Calling convention.** The delegates say `StdCall` as the header does on Windows; on x64 there is one convention and it is ignored. The module is not tested as a 32-bit process.
- **`PageVisible` is false while the module is off** and while the window is in the tray; the host drops the page's subscriptions then, so the module goes quiet by itself.
