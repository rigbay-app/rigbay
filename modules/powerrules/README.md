# Power-source rules

Rigbay module `power-rules` (group `power`, name "Power-source rules", vi "Theo nguồn điện"): when the PC goes **on battery** or is **plugged in**, it changes the
**internal panel's refresh rate** and the **Windows power mode** for you, the way you chose, and puts them back afterwards.

Status: **0.1.0, unreleased, written and tested on fakes, reads checked on one machine, no live write has ever been run.** The first live write needs the owner's gate (plan below).

## What it does

- **Two columns, On battery and Plugged in**, each with *Refresh rate* (leave as is, or one of the rates the internal panel lists at its current resolution) and *Power mode* (leave as is, Best efficiency, Balanced, Best performance).
- **Follow the power source** (off by default): turning it on asks **once** about exactly these two columns. Then, a few seconds after the source changes (and a few seconds after the PC wakes up), the column for the new source is applied, with **one notification** per switch.
  Changing any value turns it off again and asks again; a settings file edited by hand does not carry the approval to other rules (the approval is a SHA-256 of the two columns).
- **Values before are remembered**: the first automatic change writes down what it replaced. Turning the switch off, turning the module off or closing Rigbay puts those values back; the page also has a *Put back* button while something is remembered.
- **Change now** (two rows with the host's usual question and Revert): the refresh rate (only a rate the panel lists) and the power mode.
- **No internal panel in use** (the lid is closed, or only an external monitor is on) is not an error: the refresh-rate step is skipped silently and the power mode still switches.
- `rigbay power-rules status` prints the power source, the panel, the power mode and the rules (reads only).
- English and Vietnamese.

## How it works, and what it will never do

| Part | Calls | Safety |
|---|---|---|
| Power source | `RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE)` on a message-only window of its own thread; one `GetSystemPowerStatus` read after the settle delay | No polling while the notification works. If Windows refuses it, a 30 s read is the fallback (and the log says so). |
| Internal panel | `QueryDisplayConfig` (active paths), `DisplayConfigGetDeviceInfo`, `EnumDisplaySettingsEx` | Reads only. The panel is the active path whose output technology is internal (`0x80000000`, or embedded DisplayPort or UDI). |
| Refresh rate (write) | `ChangeDisplaySettingsEx`, `DM_DISPLAYFREQUENCY`, first with `CDS_TEST`, then with no flag | Internal panel only; only a rate Windows lists for it at the current resolution (re-checked inside the call); 0 and 1 are never sent; no `CDS_UPDATEREGISTRY` (this session only); read back; a change that did not hold is reported and not remembered. |
| Power mode | `PowerGetEffectiveOverlayScheme`, `PowerSetActiveOverlayScheme` in `powrprof.dll` (undocumented exports, bound by name from System32) | The three known overlay GUIDs only; the native layer takes a mode, not a GUID. A missing export means "unknown" and the page says so. |

The module declares no registry, no network, no service and no administrator right in its manifest, and a test scans the source for registry, process, file, `powercfg` and power-plan calls (none) and for any native function that is not on the allow-list.

### Order with XTU's auto-switch

XTU's own auto-switch reacts to the same change at once, again on a 5 s timer, and 4 s after resume. This module waits **5 s** after a change of source and **8 s** after resume, so Windows has swapped its own per-source power mode and XTU has applied its profile before this module touches anything. The two share no resource.
Open risk (not verified): a change of the Windows power mode might make Intel's tuning re-apply its own power limits and undo an XTU profile. The live plan reads XTU's PL1 and PL2 before and after the mode change.

## Using it

```
rigbay power-rules status
```

Build and test:

```
dotnet build Rigbay.PowerRules.slnx -c Release       # 0 warnings expected
dotnet test  Rigbay.PowerRules.slnx -c Release       # 76 tests
dotnet run --project ../../src/Rigbay.LangCheck -c Release -- src/Rigbay.PowerRules/lang --module power-rules --source src/Rigbay.PowerRules --strict
```

Copy `src/Rigbay.PowerRules/bin/Release/net8.0/` (`module.json`, the icon files, `lang/`, `Rigbay.PowerRules.dll`; no PDB) to `<modules root>\power-rules\`. Notes for changing it: `.claude/skills/powerrules-module/` in the Rigbay repository.

## What was verified, and what was not

**Verified by tests (76, all on fakes for every Windows call):** probe (no battery); first-run defaults; nothing written or armed in `Initialize`; the approval question (exact text of both columns, no answer = off, an edit while it is open drops the answer, nothing to switch asks nothing); approval ends when a rule changes; a hand-edited settings file does not carry the approval; approval honoured after a restart and run on the timer, not in `Initialize`; switch on battery (test, apply, mode, one notification, value before remembered); plug in (restored, remembered values forgotten); same source twice does nothing; a burst of changes runs once; resume forces a re-apply after 8 s; unknown source (UPS) does nothing; **`CDS_TEST` refused (the real call is never made)**; a rate the panel does not list is never sent; Windows accepts but the display does not keep the rate; failing real call; **internal panel not active (silent skip, the mode still switches)**; **undocumented export missing (mode never set, page says so)**; mode call that does not hold; a mode that is none of the three; turning off and closing put the values back (only while approved); the *Put back* row; manual rate and mode from the page, including a refused rate putting the row back; a reader that throws; slow-poll fallback when the notification is refused; no polling otherwise; the status verb changes nothing; the source is scanned: native allow-list, one call site per write, test before apply, no `CDS_UPDATEREGISTRY` and friends; manifest, icons, language files (LangCheck strict: 75 keys, en and vi). The suite ran 15 times in a row without a failure.

**Verified live on the author's machine (2026-11-13, reads only, own Rigbay instance with its own data folder, no write and no `CDS_TEST` call):** Acer Predator Triton 500 SE, Windows 11, on AC, the external 1920x1080 monitor (HDMI) is the only active display.
- The module loads, starts, stops cleanly and the page draws (screenshots in the published page).
- Power source read: Plugged in. The battery is detected (the probe says supported).
- `RegisterPowerSettingNotification` was accepted (the message-only window starts and stops cleanly).
- `powrprof.dll` has both undocumented exports and `PowerGetEffectiveOverlayScheme` returned the Balanced overlay (the power plan is Balanced).
- Panel: with the external monitor on, no internal path is active, so the module reports "Not in use". A separate read-only dump of `QueryDisplayConfig` with all paths shows the internal panel's paths with output technology **`0x80000000` (internal)**, `active=False`: the value the code looks for is the one this machine reports, and the active-only query is why it is skipped.

**Not verified (needs the owner's gate or another machine):**
- Reading the panel when the internal panel **is** active (its rates at 2560x1600, whether 60 and 165 are listed), because no live run had it active.
- `CDS_TEST` and the real `ChangeDisplaySettingsEx` call: not run at all. Whether a change without `CDS_UPDATEREGISTRY` survives lock, sleep and a display change (docking, plugging the external monitor); the resume path re-applies, but that is a design, not a result.
- `PowerSetActiveOverlayScheme`: not run. Windows 11 keeps a power mode for battery and one for plugged in; it probably swaps them by itself at the change, and whether this call writes the slot of the current source is unknown.
- That the notification **fires** on a real unplug (it was only seen to be accepted).
- That an overlay change does not disturb XTU's limits (above).
- Any other machine, panel (variable refresh, 120 Hz and 240 Hz panels), Windows 10 (no overlay modes), an eGPU or a dock.

## Proposed live-test plan (for the owner's gate; nothing here has been run)

One gate per step with the exact call and value; the owner decides each. Needs the internal panel to be in use (external monitor unplugged, or "extend" with the internal panel on), the PC on AC, the author's own Rigbay instance. A black or wrong screen is handled by Win+Ctrl+Shift+B (restarts the graphics driver), the Settings app (System, Display, Advanced display) or the undo step below.

| Step | Exact call | Expect | Undo |
|---|---|---|---|
| R0 (read, no gate) | `rigbay power-rules status` with the internal panel in use | panel `2560 × 1600 · 165 Hz`; the Refresh rate row lists the panel's rates (60, 165 and others it lists); power mode Balanced | none |
| W1 test only | `ChangeDisplaySettingsEx("\\.\DISPLAYn", mode{width 2560, height 1600, frequency 60, fields DM_POSITION\|DM_DISPLAYORIENTATION\|DM_BITSPERPEL\|DM_PELSWIDTH\|DM_PELSHEIGHT\|DM_DISPLAYFLAGS\|DM_DISPLAYFREQUENCY}, NULL, CDS_TEST, NULL)` | returns 0 (`DISP_CHANGE_SUCCESSFUL`); the screen does not change | none (nothing changed) |
| W2 set 60 Hz | the same call with flags 0 (from the page: *Change now, Refresh rate, 60 Hz*, the host asks first) | `EnumDisplaySettingsEx(ENUM_CURRENT_SETTINGS)` and Settings show 60 Hz; the screen blinks once | choose 165 Hz on the page (Revert), same call with 165 |
| W3 durability | with W2 in effect: lock and unlock; sleep and wake; plug and unplug the external monitor | note which of these put the rate back to 165. If one does, report it: adding `CDS_UPDATEREGISTRY` (a persistent change) is a separate decision for the owner | W2's undo |
| W4 read both modes | unplug the charger, read the power mode (status), plug in, read again | shows whether Windows swaps its own mode per source | none |
| W5 power mode | `PowerSetActiveOverlayScheme(961cc777-2547-4f9d-8174-7d86181b8a7a)` (Best efficiency) from the page; read back; read XTU's PL1 and PL2 on the XTU page before and after | Settings shows Best efficiency; PL1 and PL2 unchanged | `PowerSetActiveOverlayScheme(00000000-0000-0000-0000-000000000000)` (Balanced) |
| W6 the rules | the owner approves *Battery: 60 Hz, Best efficiency; Plugged in: 165 Hz, Balanced*; unplug; wait 5 s; plug in; turn the switch off | one notification at each switch; 60 Hz and Best efficiency on battery; 165 Hz and Balanced back; XTU's profile applied by XTU as before; turning off with a remembered value puts it back | turn the switch off; *Put back*; or W2/W5 undo |

## License

MIT.
