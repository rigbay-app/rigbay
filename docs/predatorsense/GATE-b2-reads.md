# Approval requests: next firmware reads (candidates; B3 ran part of them)

**B3 update (see `FINDINGS.md` section 12):** the owner approved reading freely (gate `g-b3-reads`). Run once each, elevated: **R1** (`0x0201`, status 0, value 2857) and
**R2** (`0x207`, raw `0x2E1`, **status `0xE1`, odd, run stopped**), then sysinfo indexes 3 to 17 (all status 0). **R3, battery boost, `GetGamingMiscSetting(2)`/`(9)` and
`GetGamingLEDBehavior(1)` were NOT run** (held by the coordinator until R2's status is understood). The rest of this file is the original B2 text.

Original B2 status: **nothing here has been run, and no gate was opened for it.** B2 only decoded these. A gate holds its task until a person
answers, and these reads are optional follow-ups, so they are written down for the coordinator to open one at a time
(`csm gate open <task> "<question>" --options "approve,reject"`), each against its own task. Approval of one covers only that one call.

Why there are no gates for the fan, operation-mode and lighting "Get" methods: the Acer UI never calls `GetGamingFanBehavior`,
`GetGamingFanSpeed`, `GetGamingFanTable`, `GetGamingLEDGroup*`, `GetGamingRgbKb`, `GetGamingKBBacklight`,
`GetGamingProfileSetting`, and never sends the operation-mode read commands (`FINDINGS.md` 11.2 to 11.4). Their inputs cannot be traced
to the UI, so under the project rule they are not to be called at all. The candidates below are the traceable reads that
answer the same questions.

Common to all: root `root\WMI`, elevated administrator, run once through `tools/predatorsense-probe` after it is extended for
that exact call (the probe today only accepts `GetGamingSysInfo`, indexes 1 to 17). The first call (`0x0101`) returned status 0
and 73 and caused no visible problem; that is the only experience behind "looks like a read" (`FINDINGS.md` 11.1).

## R1: CPU fan speed (fan speed units, and a before/after check for any fan write)

- **Question (gate text, under 400 characters):** "Approve ONE call of root\WMI AcerGamingFunction.GetGamingSysInfo (id 5) with input 0x0201 (CPU fan speed), elevated? Same method and format as the 0x0101 call already done (status 0, value 73). The Acer UI polls this to draw fan speed. Reads only; I will not send any other input."
- Call: `GetGamingSysInfo`, `gmInput` = `0x0201` (`1 | (2 << 8)`).
- Parse (UI): status = low byte (0 ok); value = `(raw >> 8) & 0xFFFF`.
- Expect: a number; RPM if the unit guess is right (hundreds to a few thousand), or 0 if the fan is stopped. An error status is also possible if this model lacks the sensor.
- Why: settles the unit of fan "speed", gives the first live fan reading, and is the verification read for W1 and W4.
- Risk: same as the approved call. Worst case as in `GATE-getgamingsysinfo.md`.

## R2: Cool Booster state (prerequisite for the first fan write W1)

- **Question (under 400 characters):** "Approve ONE call of root\WMI AcerGenericMethod.GetFunction (id 2) with input 0x207, elevated? It is how the Acer UI reads the Cool Booster state (answer byte 1 equals 1 when on). A different method from the sensor call, so it needs its own approval. Reads only; no Set* call."
- Call: `AcerGenericMethod.GetFunction`, input `0x207`.
- Parse (UI, `get_coolboost_state`): status = low byte; Cool Booster on when byte 1 of the answer equals 1.
- Expect: byte 1 = 0 (the UI cache shows fan mode Auto).
- Risk: slightly higher than R1: a different method (class `AcerGenericMethod`), and the low byte 7 of the input is also the selector of the Cool Booster *write* (`SetFunction(7 | state << 16)`). The `0x200` bit is what the UI adds for a read. I cannot see the firmware side.

## R3: keyboard backlight (lighting read)

- **Question (under 400 characters):** "Approve ONE call of root\WMI AcerGenericMethod.GetFunction (id 2) with input 0x80184, elevated? The Acer UI builds it from the stored hotkey number 132 to read keyboard backlight brightness and on/off. Reads only; I will not send a Set* call or any other input."
- Call: `AcerGenericMethod.GetFunction`, input `0x80184` (`BK_Hotkey_Number 132 | 0x100 | 0x80000`; 132 is **O** from the registry on this machine).
- Parse (UI): low byte status; bits 32 to 39 brightness percent; bits 40 to 47 equal to 30 means backlight off.
- Risk: as R2. Only worth asking after R2 went well.

## Further traceable reads, not proposed now

`GetGamingSysInfo` other indexes (table in `GATE-getgamingsysinfo.md`: GPU1 temperature `0x0A01`, GPU fan speeds `0x0601` and `0x0901`, rates `0x0C01`, `0x1001`, `0x1101`),
`GetGamingMiscSetting(2)` and `(9)` (GPU mode), `GetGamingLEDBehavior(1)` (boost indicator, meaning only guessed), battery boost `GetGamingSysInfo(2)`.
Each needs its own approval, one at a time, after the results of R1 and R2 are looked at.
