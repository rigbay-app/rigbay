# Spike C1: can the XTU module live in the .NET 10 host?

Run on the author's machine on 2026-11-01 (Windows 11 10.0.26300, .NET SDK 10.0.403, runtime 10.0.14, XTU 7.14.2.135, service
`XTU3SERVICE` running). Read-only on XTU: no `ProposeChange`, `ApplyChanges`, `DiscardChanges` or any other control write exists
in the code, and none was called. XTU was neither started nor stopped. Everything below was run unless it says "not verified".
Code: `spike/xtu/` (see its `README.md` for the commands).

**Verdict: yes. Design A works.** The XTU module can stay in the .NET 10 host, in process, calling Intel's own contract
assemblies from the Intel folder through the .NET WCF client packages. It reads the same state as `xtu-switcher --status` with the
same latency as the net48 app. Two things have to be handled: the host's `ModuleLoadContext` blocks the WCF packages unless the module
adds a small resolver of its own (verified, no host change needed), and Intel's DLLs must be on the build machine. Design B (net48
helper process) also works and is the fallback. Design C is feasible for the contract, not worth doing at the wire level.

What was read, every time and by every design: `PL1=55 PL2=60 pl2enable=True window=56`, restore after reboot `True`. That equals
the output of the existing app on the same machine (`xtu-switcher --status`: `active: PL1=55 PL2=60 pl2enable=True window=56`,
matching profile `PL55-60W`).

## Results

| | A. In process, .NET 10 | B. net48 helper process | C. Own contracts, no Intel DLLs |
| --- | --- | --- | --- |
| Works? | **Yes**, status and monitoring read, in a custom load context and through the real `Rigbay.Core.ModuleLoadContext` | **Yes**, end to end over stdin/stdout JSON lines | **Not run** (task says judge only). Feasible, see below |
| Exact errors met | `FileNotFoundException: Could not load file or assembly 'System.ServiceModel.Primitives, Version=10.0.0.0 ...'` with the unchanged host loader and no module resolver (fixed by the module, see A.2). Same type of error for `XtuInterface` if nothing resolves the Intel folder | none while elevated | n/a |
| Latency, status read (refresh + 4 x `GetControl` + restore flag) | first: 200 to 227 ms to create the channel and connect, then the first read 125 to 141 ms. Warm, n=100: p50 128 ms, p95 147 ms, min 93, max 236 | first: 169 to 239 ms. Warm n=50 / n=100: p50 119 / 125 ms, p95 143 / 154 ms, min 96 / 93, max 155 / 273 | same as A (same client stack) |
| Latency, monitoring `GetValues` (51 values) | first (Connect+Start included): 6 to 7 ms. Warm p50 0.21 to 0.23 ms | first: 10.6 to 12.4 ms. Warm p50 0.26 / 0.27 ms, p95 0.35 / 0.39 ms; at 1 Hz 0.8 to 1.8 ms | same as A |
| Cost of the process boundary | none | `ping` (no XTU call) p50 0.01 to 0.03 ms; spawn + first reply 137 to 187 ms once; a throw-away helper per call would cost 242 to 276 ms | none |
| Extra files shipped with the module | 5 DLLs, about 2.7 MB: `System.ServiceModel.Primitives` 2.4 MB, `.NetFramingBase` 199 KB, `.NetNamedPipe` 56 KB, `System.ServiceModel` (facade) 25 KB, `Microsoft.Extensions.ObjectPool` 22 KB, plus the module DLL and `deps.json`. A plain build copies 4 more (Cryptography.Pkcs, Cryptography.Xml, ServiceModel.Duplex, ServiceModel.Security) and 13 language folders; verified not needed | one 11.5 KB exe (`XtuHelper.exe`), nothing else. Intel DLLs not copied in either design | the same 5 DLLs as A, no Intel file at all |
| Memory | working set 26 to 58 MB after loading the stack and 100 calls (+32 MB inside the host) | helper 24 MB at start, 47 MB after 100 status + 100 monitor calls (separate process) | as A |
| Elevation | needs admin: not elevated, the call fails with `UnauthorizedAccessException: Access to the path is denied` (from `MemoryMappedFile.OpenExisting` inside `PipeSharedMemory.Open`). The host is elevated anyway | needs admin: not elevated, `EndpointNotFoundException` caused by `AddressAccessDeniedException` caused by `PipeException: ... Access is denied. (5, 0x5)`. The helper inherits the elevation of its parent (checked: `elevated: true`), so no second prompt | as A |
| Effect on the Abstractions contract | none | none (the helper is the module's private business) | none |
| Effect on the host (`Rigbay.Core`) | none required; one worthwhile fix, see A.2 | none | none |
| Needs Intel DLLs at build time | yes (compile references). A public CI runner has none, so release zips are built on the author's machine | yes (`csc /r:`), same limit | **no** |
| Isolation | a hung or crashing WCF call stays in the host (fault boundary, threadpool thread lost) | a hung or crashing call is contained; the host can kill the helper. Helper exits by itself when the host is hard-killed (verified) | as A |
| Effort, my estimate (to a finished module that also applies profiles) | about 0.5 to 1 day: the existing `XtuClient` and `MonitorService` port almost verbatim (the spike code is the skeleton) | about 2 days: protocol for every op, module-side client with timeouts, restart policy, Job Object, build of the helper | contracts: about 1 day on top of A plus one read-only live check. Raw protocol, no WCF: weeks, not recommended |

## Recommendation

**Use A for the XTU module.** Keep it in the .NET 10 host, built as an SDK-style project that references `XtuInterface.dll` and
`XtuCommon.dll` in place (`Private=false`) and the `System.ServiceModel.Primitives` and `System.ServiceModel.NetNamedPipe` packages (10.0.652802
resolved). It costs nothing at run time compared with the old app and has no process to manage.

1. Install the module resolver (A.2) in the module's first line of `Initialize`, behind a `NoInlining` method boundary.
2. Plan C-lite as a follow-up before the module goes into the registry: replace the Intel types by the module's own copies of the contracts
   (interface and member names, the plain data types). Then the module builds in public CI with no Intel file and loads nothing from
   `Program Files\Intel` except through the pipe. Section C says why that should work and what is unverified. This is a coordinator
   decision under worker rule 2 (identifiers are facts, no code bodies are copied).
3. Keep B as the fallback. Switch to it if a later XTU build makes Intel's assemblies fail to load on .NET 10, or if hung WCF calls turn
   out to be hard to contain with timeouts.

**What the Abstractions need: nothing.** Not for A, and not for B either: a helper process would be started in `Initialize` and killed
in `Shutdown` by the module itself, and `requires.admin: true` already covers it. If B is ever chosen, put the reusable parts (JSON-lines
client, Job Object with kill-on-close, call timeout) in a small shared library next to `Rigbay.Core`, not in `Abstractions`, because
helpers differ per module. Optional and non-breaking: let the manifest `requires` list say which foreign files a module reads in place, so
the install dialog can show "reads Intel XTU contract DLLs".

## A. In process on .NET 10

### A.1 What was built and run

`inproc/XtuModule` (net10.0, x64) references the Intel assemblies with `HintPath` into the Intel folder and `Private=false`, and
the two WCF client packages. It builds with 0 errors; the warnings are `CA1416` (platform analyser on `NetNamedPipeBinding`),
`CS8603` and the transitive `NU1902`/`NU1903` notices for `System.Security.Cryptography.Xml` and `.Pkcs` 10.0.0, which the minimal file set does not ship. No version-conflict warning (`MSB3277`) appeared.
Intel's assemblies reference `System.ServiceModel 4.0.0.0`; the package set contains a `System.ServiceModel.dll` facade that satisfies
it. At run time the Intel types come from `C:\Program Files\Intel\...\Drivers\XtuInterface.dll` and `XtuCommon.dll`, loaded in place (both 7.14.2.135).

The calls are the same as the old app: `ChannelFactory<IXtuTuning>` with `NetNamedPipeBinding` (64 MB quotas, `MaxItemsInObjectGraph`
raised), `Connect()`, `RefreshActiveValuesFromHardwareAccess()`, `GetControl(47|48|49|66)`, `GetDoRestoreUserValuesOnBoot()`; then
`ChannelFactory<IXtuMonitoring>`, `Connect(pid)`, `Start(pid)`, `GetValues()`, `Stop(pid)`. The monitoring `Connect/Start/Stop` register
and unregister this client with the service (the old app does the same); they are not tuning writes. The first `GetValues` after `Start`
returns the previous run's numbers (the same long decimal showed up in two runs), as the xtu skill says; the numbers here are for latency, not for sensor accuracy.
Where the time goes on a status read: `RefreshActiveValuesFromHardwareAccess` 94 to 164 ms (service side), four `GetControl` 6 to 8 ms warm
(28 to 39 ms the first time), the restore flag 0.3 ms.

### A.2 The host loader blocks the WCF packages (found by running the real `Rigbay.Core.ModuleLoadContext`)

`ModuleLoadContext.Load` hands every assembly whose name starts with `System.` to the host's default context and ignores the module's
copy. `System.ServiceModel.*` starts with `System.`, the host does not ship it, so the load fails with the `FileNotFoundException` quoted in the table.
(`CoreCheck.exe --no-hook`, exit code 1.) A custom load context without that rule (`Runner.exe`) worked at once, which is why the
rule was easy to miss.

Fix that needs no host change, verified through the unchanged loader (`CoreCheck.exe`, all reads OK): the module subscribes
to `AssemblyLoadContext.GetLoadContext(typeof(Module).Assembly).Resolving`. That event fires after the context's `Load` override and
the default context both failed. The handler returns `ctx.LoadFromAssemblyPath` for a DLL in the module's folder and, for `XtuInterface`,
`XtuCommon` and Intel's own dependencies, a DLL in the Intel folder. About 15 lines (`XtuReader.InstallResolver`). It must run before
the first method that touches a WCF or Intel type is compiled, hence the `NoInlining` split.

Recommended for the host (coordinator, not done here, `src/Rigbay.Core` is not mine): make the `System.` rule conditional, "default
context first if it actually has the assembly, else the module's folder". Otherwise every module that ships a `System.*` NuGet package
(`System.Management`, `System.ServiceProcess.ServiceController`, ...) hits the same wall, and the PredatorSense module likely will.

### A.3 Offline check of the write path

The write calls cannot be run (no writes allowed), so their data types were checked without the service: `SdkClientToken`, a list of
`XtuTuningProposal`, `TuningResult` (one, two and no codes), a list of `XtuTuningProposalResult`. The same source was compiled for net48
and .NET 10 and each side serialised them with `DataContractSerializer`. The XML is byte-identical on both (except the process id inside the default
token, which is the process's own), and each side reads the other's XML back correctly (`shared/WireProbe.cs`). `ProposeChange` and the other write operations over the real pipe are **not verified**.

## B. Out-of-process net48 helper

`helper/XtuHelper.cs`: C# 5, 11.5 KB exe built by `build-helper.ps1` with the OS `csc.exe` (no SDK), Intel DLLs referenced in place and
loaded by an `AssemblyResolve` hook as in the old app. Ops: `hello`, `status`, `monitor`, `ping`, `quit`; each reply carries `ok`, the time spent inside XTU (`ms`) and
`data` or `error`. `harness/` is the .NET 10 side.

Measured (elevated, `XtuHarness.exe`, two runs):

- Spawn to first reply 137 and 187 ms; first `status` (pipe connect included) 169 and 239 ms.
- Warm `status`: p50 125 / 119 ms; the helper's own time inside XTU is within 0.2 ms of the round trip. The process boundary adds 0.01 to 0.03 ms (`ping`).
- Warm `monitor`: p50 0.27 / 0.26 ms; five samples at 1 Hz took 0.8 to 1.8 ms each.
- A fresh helper per read (spawn + connect + status + exit): 242 to 276 ms (10 runs). Not a design to use for polling; keep one helper alive.
- Lifecycle: killing the helper makes the next call fail immediately (`IOException: The pipe is being closed.`), so the module can detect it and restart. Killing the host
  (`Process.Kill`, no cleanup) makes the helper exit by itself within 3 s because its stdin closes (checked from the process list). The helper unregisters its
  monitoring client on that path in the code; the registration being gone on the service side was not checked.
- Non-elevated: the exact error is in the table; the helper inherits elevation from the host.

Cost compared with A: one more executable to build, version and ship, a protocol to keep in step with the module, a restart policy, and
the same build-time need for Intel DLLs. Gain: isolation and no .NET Framework assembly inside the host process.

## C. Own implementation of the contract, no Intel DLLs (judged, not implemented)

Evidence from the Intel metadata (reflection-only load of `XtuInterface.dll` and `XtuCommon.dll`, plus the REA output) and from A:

- The service exposes plain WCF contracts: `IXtuTuning`, `IXtuMonitoring`, `IXtuServiceInfo`, `IXtuEvents`, `IXtuCore`, each marked `ServiceContract` with no
  namespace or name overrides and `OperationContract` per method; no `FaultContract`, no `ServiceKnownType`, no `DataContract` attributes. The data types are plain
  classes and structs (`XtuTuningControl`, `XtuTuningProposal`, `XtuTuningProposalResult`, `SdkClientToken`, `TuningResult`, enum `TuningErrorCode`) that WCF serialises by shape
  under their CLR namespaces (`IronCity.Core`, `IronCity.Common`).
- The service side wraps the standard named-pipe transport in its own transport element (`CustomNamedPipeTransportBindingElement`). By its members it only adds an allowed-users list
  to the pipe's access rules; the wire protocol is the standard WCF one. That matches what A showed: a client with the default `NetNamedPipeBinding` is accepted.
- The only awkward type is `TuningResult`: it is marked `Serializable`, so it travels as its private fields (`__codes` and the compiler-made backing field of `GeneralCode`, as
  `_x003C_GeneralCode_x003E_k__BackingField`). A copy with `DataMember` names set to those two names reproduces it; A.3 shows the exact XML.
- A already proves the hard part: the .NET 10 WCF client talks to this service, and the data types round-trip identically with the Framework serialiser.

So a module with its own copies of the interfaces and data types (names and namespaces equal to Intel's, `DataContract` set to match) and the same binding should work,
with no Intel file at build or run time. Raw framing without WCF (the .NET message-framing protocol with Windows-authenticated stream upgrade and binary XML) is feasible on paper
and gives nothing over the client packages, so it is not recommended.

Unverified: that the copies produce action names and a message shape the service accepts (needs one live read-only check), and whether an XTU update changes the contracts
(true for A and B too, which also bind to the installed version).

## Not verified

- Any write over the pipe (`ProposeChange`, `ApplyChanges`, `DiscardChanges`): forbidden here. Only the data types were compared offline (A.3).
- The real WinUI host process. `CoreCheck.exe` is a console process that uses the real `Rigbay.Core.ModuleLoadContext`; the WinUI app was not started with the XTU module.
- Behaviour with XTU stopped, restarted or updated (the service must not be stopped here), and after sleep/resume with a channel left open.
- Different XTU versions or machines; strict assembly-version matching of Intel's DLLs under .NET 10 (7.14.2.135 only).
- Design C beyond the metadata analysis.
- Timeout behaviour of a hung WCF call in A (the client timeouts were set, never triggered).
- Whether the monitoring client registration is cleared on the service after a hard kill of the helper.

## Files

`spike/xtu/` (all mine; `out/` is ignored by git). No Intel file is in the repository or next to any build output; Intel's DLLs are loaded from
`C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers` in place.
