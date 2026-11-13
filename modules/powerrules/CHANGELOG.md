# Changelog

## 0.1.0 (Unreleased)

First version. Not released: no live write has been run (see the README, "Not verified").

- Two columns, On battery and Plugged in, each with a refresh rate for the internal panel (from the rates it lists) and a Windows power mode (best efficiency, balanced, best performance).
- Automatic switching when the power source changes (and after resume), off by default, turned on with one question about exactly the two columns; an edited rule ends the approval; one notification per switch.
- The values before the first automatic change are remembered and put back when switching is turned off, when the module is turned off, or with the *Put back* row.
- *Change now*: the refresh rate and the power mode as rows with the host's question and Revert.
- The internal panel is found among the active display paths (output technology internal); with only an external monitor the rate step is skipped silently.
- Reads: `GetSystemPowerStatus`, `RegisterPowerSettingNotification`, `QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`, `EnumDisplaySettingsEx`, `PowerGetEffectiveOverlayScheme`.
  Writes: `ChangeDisplaySettingsEx` (`CDS_TEST` first, then this session only) and `PowerSetActiveOverlayScheme` (three overlay GUIDs).
- `rigbay power-rules status`.
- Needs Rigbay host API `>=0.1.3 <0.2`. English and Vietnamese.
