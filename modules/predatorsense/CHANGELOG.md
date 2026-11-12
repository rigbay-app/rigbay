# Changelog

## 0.1.2 (Unreleased)

Needs a Rigbay host with host API 0.1.3 (`hostApi` `>=0.1.3 <0.2`); release it after the host that has it.

- **The fan mode is the page's main control** (UI review of November 2026, PS-1, sketch 3): Auto and Maximum as two segments under the page header, nothing
  chosen while Rigbay has set nothing (the value `?` is not an option; the "Not set" option and the page rebuild after the first set are gone). One line under
  it replaces the rows "Last set by Rigbay" and "Speed check": "Not set by Rigbay yet", or "Set to Maximum at 18:42 · CPU fan sped up: 2857 to 5555 rpm".
- **Each temperature is a tile with its own fan** (PS-2): CPU with the CPU fan, GPU with its fans, system; the fans are rows only, out of the charts.
- **Performance mode, still read only:** the modes the laptop offers as a choice that is never enabled, the current one chosen, "Read only. Change it in
  PredatorSense for now." (the rows "Modes this laptop offers" and the note bar are gone; the why is in the help glyph). Nothing writes the mode.
- Groups: Readings, Performance, Settings (fan shortcut, restore Auto, Details with the machine and the poll interval). The dashboard line says the state of
  the fans when Rigbay set them ("Fans Maximum since 18:42") and nothing otherwise (DB-5). Shorter descriptions, explanations in help glyphs (LC-2).

- **Shows the performance mode, read only.** The Cooling and performance group has a "Performance mode" row (Quiet, Default, Extreme, Turbo; Render and Edit only if the laptop advertises them), read from the
  firmware with `GetGamingMiscSetting(0x0B)`, and a "Modes this laptop offers" row from `GetGamingMiscSetting(0x0A)`. A failed read, a non-zero status or an id nobody knows shows "Unknown".
  There is no selector: the page says "Change it in PredatorSense for now". Rigbay will offer a change only after a supervised test.
- New metric `predatorsense.opmode` (the mode id: Quiet 0, Default 1, Render 2, Edit 3, Extreme 4, Turbo 5), described for the dashboard. The supported modes are read once when the module starts; the current mode
  every 10 s, and only while the page is visible or the metric has a subscriber. `rigbay predatorsense status` prints the mode too.
- The allow-list gained exactly those two reads (`Wmi/WmiAllowList.cs`); tests show no other input of `GetGamingMiscSetting` and no `Set*` but the fan one can be sent.
- Group "Cooling" is now "Cooling and performance" (en, vi). New language keys in en and vi.

## 0.1.1

The Fan mode control no longer pretends to know the mode: it starts on "Not set" (E2E-10). Notifications for every change, refusal and failure (N1).

## 0.1.0

First version: temperatures and fan speeds from the firmware, and the fan mode Auto or Maximum, after you confirm.
