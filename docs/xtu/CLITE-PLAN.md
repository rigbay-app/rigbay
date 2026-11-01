# C-lite plan: build the XTU module without Intel's DLLs (task C3, plan only, not implemented)

Design C-lite of `docs/spike/XTU-RUNTIME.md`: the module carries its own copies of the few contract types it uses (same names, same
namespaces, same member shapes), so it builds on a public CI runner with no Intel file, and still talks to the XTU service over the same
WCF named pipe. Only names and shapes are listed here (worker rule 2: identifiers are facts, no code bodies are copied).

## Why

Today `Rigbay.Module.Xtu.csproj` references `XtuInterface.dll` and `XtuCommon.dll` from `%ProgramFiles%\Intel\Intel(R) Extreme Tuning Utility\Drivers`
(`XtuDir`). Nothing builds without XTU installed, so the public CI cannot build or test the module and the extracted `module-xtu` repository
cannot have a green CI. The tests already run against fakes and never touch Intel types; only `Intel/IntelPorts.cs` does.

## What the module uses (XTU 7.14.2.135, assemblies `XtuInterface` and `XtuCommon`)

Everything below is used from `Intel/IntelPorts.cs`, nowhere else.

**`IronCity.Core.IXtuTuning`** (service contract, tuning pipe `net.pipe://localhost/ExtremeTuningUtility/Tuning`). Only these 7 of its 29 members:

| Member | Shape | Used for |
| --- | --- | --- |
| `Connect` | `bool Connect()` | open the session |
| `RefreshActiveValuesFromHardwareAccess` | `void ()` | refresh before every read |
| `GetControl` | `XtuTuningControl GetControl(uint id)` | read the four controls |
| `GetDoRestoreUserValuesOnBoot` | `bool ()` | "restore after reboot" line |
| `DiscardChanges` | `void (SdkClientToken clientToken, out List<?> changeList)` | drop pending proposals (write path) |
| `ProposeChange` | `TuningResult (List<XtuTuningProposal> proposals, SdkClientToken clientToken, out List<?> proposalDelta, out bool requiresReboot, bool isProfile)` | write path |
| `ApplyChanges` | `TuningResult (bool forceReboot, int tuningDelay, SdkClientToken clientToken)` | write path |

The element type of the two `List<?>` out parameters is not used by the module (it discards them); the C-lite copy must still use the exact
element type so the operation signature, and therefore the WCF message, is identical. Read it from the metadata when doing step 1.

**`IronCity.Core.IXtuMonitoring`** (monitoring pipe `.../Monitoring`). 4 of its 12 members: `bool Connect(int processId)`, `void Start(int processId)`,
`void Stop(int processId)`, `Dictionary<uint, decimal> GetValues()`.

**`IronCity.Core.XtuTuningControl`** (data contract). Read members: `ActiveValue` (decimal), `DefaultValue` (decimal), `ReadOnly` (bool), `Enabled` (bool),
`SupportedValues` (`List<decimal>`, used with `?.ToList()`). The type also has `Id`, `ProposedValue`, `BootValue`, `RequiresReboot`, `RealtimeOnly`,
`IsFavoredCore`: not read by the module, but a data contract copy must keep every member the service sends, or the serializer drops them silently
(harmless) or, for required members, fails (check in step 1).

**`IronCity.Core.XtuTuningProposal`** (data contract). Written members: `uint Id`, `decimal Value`.

**`IronCity.Common.SdkClientToken`**: static property `Default`; it is sent as an argument, so its data-contract fields (`int ProcessId` is one) must match.

**`IronCity.Common.TuningResult`**: read `GeneralCode` (`TuningErrorCode`) and `AllCodes` (`IReadOnlyDictionary<uint, TuningErrorCode>`, shape to confirm in step 1;
the module enumerates key/value pairs). The type is a data contract with more members and operators (`ContainsCode`, `SpecificResult`, `Merge`,
implicit bool); the module uses none of them.

**`IronCity.Common.TuningErrorCode`** (enum): `DidNotAttempt, Success, UnknownTuningFailure, InvalidProposal, ReadOnlyControl, DisabledControl,
ControlDoesNotExist, BlockedByISO, UndervoltProtection, UnsupportedValueRequested, IllegalVRAddress, TryAgain` (the module compares with `Success` and prints
the name; keep the order and values because the enum travels as an integer or by name depending on the attribute).

No other Intel type is touched. WCF itself comes from the `System.ServiceModel.*` NuGet packages, which already work without Intel files.

## What the copy must reproduce (not visible from the member list)

The wire format is decided by attributes and names that the list above does not show. Step 1 reads them from the metadata of the installed
DLLs (names and values only): `ServiceContract` Name/Namespace/SessionMode/CallbackContract, each `OperationContract` Name/Action/ReplyAction/IsOneWay,
`DataContract` Name/Namespace and each `DataMember` Name/Order/IsRequired, any `ServiceKnownType`, `KnownType`, `FaultContract`, and the enum `EnumMember` values.
A mismatch shows up as `ActionNotSupportedException`, a deserialisation error, or silently zeroed values, which is why the plan ends in a read-only live check.

## Steps and effort

1. Dump the contract metadata of the 7 + 4 operations and 6 types above (attributes, namespaces, element types of the two `out` lists). Script in `modules/xtu/tools/`, no Intel file stored. **1 to 2 h.**
2. Write `Rigbay.Module.Xtu/Intel/Contracts.cs` with the copies (namespaces `IronCity.Core` / `IronCity.Common` kept, internal visibility, only the members above, plus the data members needed for the wire). **2 to 3 h.**
3. Remove the two `Reference` items and `XtuDir` from the csproj; `probePaths` in `module.json` can stay for `Probe` (file existence check) or go. Check `ProbeLogic` still tests that the Intel folder exists (it is a hint that XTU is installed, not a load requirement any more). **1 h.**
4. Public CI job: `dotnet build` + `dotnet test` of the extracted repo on a hosted Windows runner (NuGet `Rigbay.Abstractions` instead of the ProjectReference). **1 to 2 h** (depends on how P1's extraction script and the registry pipeline end up).
5. Read-only live check on the real machine (`tools/live-read.ps1`, write latch ON): status and list must equal today's output (`PL1=55 PL2=60 pl2enable=True window=56`, profile `PL55-60W`), monitor values must flow. Run the monitoring pipe for 30 s. **1 h** plus an owner who is present, because it runs elevated.
6. Live apply (not part of C-lite): one approved apply after the copy is in place, because `ProposeChange` and `ApplyChanges` carry the most complex data contracts and a read-only check does not exercise them. The module's apply test in `docs/xtu/README.md` ("The live apply test to ask the owner for") is the one to run; needs the owner's approval for the specific write.

**Estimate: about 1 working day** for steps 1 to 5 (the spike put "contracts" at about a day on top of design A), plus the owner's approval and time for step 6.

## Risks

- The copy is only as good as the metadata reading: a wrong namespace or order breaks only the real service, never the fake-based tests. Step 5 is the only proof.
- An XTU update may change a contract. Today that breaks the build or the load; with C-lite it breaks only at run time, so `Probe` should also read the XTU version
  (`XtuInterface.dll` file version) and warn on a version newer than 7.14.2.135.
- Worker rule 2 and the coordinator: copying interface and type names, namespaces and attribute values into our own file is the decision to confirm before step 2.
  Method bodies and Intel's source are not copied; none exist in the metadata of the contract anyway.
- Fallback stays design B (net48 helper process) if XTU's assemblies stop loading on .NET 10.
