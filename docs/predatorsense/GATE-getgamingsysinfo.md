# Approval request: one firmware read, `GetGamingSysInfo`

Status: **approved by the owner (gate `g-sysinfo-0101`) for exactly one call, input `0x0101`; done once in task B2.** Result: status 0, raw
`0x4900`, value 73 (see `FINDINGS.md` 11.1). Everything below the first-approval table is still only a request: the table for later is
**not approved**, and each further index or method needs its own gate (candidates: `GATE-b2-reads.md`).

## The call

| Item | Value |
| --- | --- |
| WMI namespace | `root\WMI` |
| Class | `AcerGamingFunction` (class GUID `{7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56}`, MOF description "Version 2.81") |
| Method | `GetGamingSysInfo`, `WmiMethodId` 5 |
| In parameter | `gmInput`, UInt32 |
| Out parameter | `gmOutput`, UInt64 |
| Run as | elevated administrator (instance enumeration is "Access denied" for a normal user, checked) |

How the answer is read (from the Acer UI's own code): the low byte of `gmOutput` is a status (0 means success);
for a successful sensor read the value is in the next 16 bits (`(gmOutput >> 8) & 0xFFFF`).

## First approval: one input only

| Input (`gmInput`) | Meaning | Where it comes from |
| --- | --- | --- |
| `0x0101` | CPU temperature | the UI's helper builds the input as `1 OR (index << 8)`; sensor index 1 is `sCPU_Temperature` in the UI's own `System_Health_Information_Index` list |

That is the whole first request: **one call, input `0x0101`, once.**

## Table for later (not requested now)

Same formula, `gmInput = 1 | (index << 8)`. Only to be asked for after the first call has been looked at.

| Index | `gmInput` | Meaning (UI name) |
| --- | --- | --- |
| 1 | `0x0101` | CPU temperature |
| 2 | `0x0201` | CPU fan speed |
| 3 | `0x0301` | system temperature |
| 4 | `0x0401` | system fan speed |
| 5 | `0x0501` | system fan speed, external |
| 6 | `0x0601` | GPU 1 fan speed |
| 7 | `0x0701` | system 2 temperature |
| 8 | `0x0801` | system 2 fan speed |
| 9 | `0x0901` | GPU 2 fan speed |
| 10 | `0x0A01` | GPU 1 temperature |
| 11 | `0x0B01` | GPU 2 temperature |
| 12 | `0x0C01` | CPU fan rate |
| 13 | `0x0D01` | system fan rate |
| 14 | `0x0E01` | system 2 fan rate |
| 15 | `0x0F01` | system fan rate, external |
| 16 | `0x1001` | GPU 1 fan rate |
| 17 | `0x1101` | GPU 2 fan rate |

Evidence for the whole table: the helper `get_wmi_system_health_info` and the enum `System_Health_Information_Index`
in `PredatorSense.exe` (installed package, read in place; enum values read with a metadata-only load).
Not every index exists on every model; this machine has 1 CPU fan, 2 GPU fans and no separate system fan
(`HW_Support.ini`, `[FanSupport]`).

## Why I believe it is a read

Reasons, each with how sure I am:

1. The Acer UI calls it from its dashboard refresh code (`get_cpu_system_info_data`, `get_fan_info_data`) to draw
   temperatures and fan speeds. Sure of that (read in the UI's code). The polling interval I did not establish.
2. The UI does nothing else with the answer than show it. Sure (same code).
3. The service, when it relays this call, logs "input" and "output" and nothing more. This comes from log
   text in `PSSvc.exe`; I did **not** disassemble the native code, so this is weaker.
4. The method is named "Get", and the other half of the pair (`SetGamingProfile` and friends) is separate.
   This is only a naming convention. The MOF marks every method of the class, Get and Set alike, as
   read=true and write=true, so the MOF says nothing about side effects.

What I cannot see: the firmware (ACPI/EC) side of the method. That is why this is a request and not something I ran.

## Worst case if I am wrong

I cannot rule out that the firmware treats an input value as a command. The low byte of the input looks like a
function selector: the UI also calls the same method with input `2` to learn whether "battery boost" is on
(it reads a byte from bits 40..47 of the answer), and with `1 | (index << 8)` for sensors. So `0x01` means
"system health value, index in the next byte" as far as the UI shows; what the firmware does with it I cannot see.
The realistic bad outcomes, from most to least likely:

- an error status in the answer and nothing else (most likely; the UI itself relies on that status byte);
- a short stall of the WMI call;
- a changed firmware setting, if the method turns out to dispatch on the low byte. I have no evidence for this,
  and the UI sends the same low byte constantly in normal use.

I will not send any other input, any `Set*` method, or any call with a value I cannot trace to the UI's code.

## How it will be run if approved

`tools/predatorsense-probe`, elevated, with the explicit flags `--allow-call --sysinfo-index 1`. The probe refuses
any index outside 1..17 and calls no other method. Output is pasted into `FINDINGS.md`.
