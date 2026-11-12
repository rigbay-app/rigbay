# Changelog

## 0.1.1 (Unreleased)

Needs a Rigbay host with host API 0.1.3 (`hostApi` `>=0.1.3 <0.2`); release it after the host that has it.

- **The profile is the page's main control** (UI review of November 2026, XT-1). It is drawn large under the page header, as segments when there are
  2 to 5 profiles of at most 20 characters, with the limits in force as its line ("PL1 45 W / PL2 60 W / 56 s", "Custom values: ..." when no profile
  matches, or why XTU cannot be read). The rows "Active profile", "Limits in force" and "Last action" are gone; every change is reported by a notification.
  With bare names in the options, the approval text lists the exact limits of every profile, so the question still says the values.
- **Readings** (XT-2): temperature, package power, usage and clock are the four tiles; PL1 and PL2 are dashed limits in the power chart, not tiles. The four
  throttling ratios moved to Details (rows only, no chart).
- **Switch by power source** (XT-3) is one row: the switch in its header, the power source now and the profile it uses in its line, the two choices inside.
- **Your profiles** (XT-4): closed rows with their limits in the line, "New profile" last, and Delete asks "Delete <name>?" first.
- **XTU** group: the two messages (only while true), the profile hotkeys, and Details (Reload, throttling, XTU version, restore after restart, per-core readings).
- **Shorter copy** (LC-2): descriptions only where they add a fact, at most about 70 characters; the explanation of the profile choice and of the throttling
  ratios is in their help glyph. Vietnamese (LC-1): "profile" is "cấu hình", "time window" is "khoảng thời gian", the module is "Cấu hình XTU".
- Since 0.1.0 (already in this folder): every hardware change, refusal and failure is announced (N1); the manifest declares a host API revision (R2).
