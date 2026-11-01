Rigbay @VERSION@ (pre-release, Windows 11 x64)

Rigbay is a small Windows app that manages modules for your own machine: power limits,
fan and performance modes, monitoring, network fixes. Modules are installed from inside
the app. Source and issues: https://github.com/rigbay-app/rigbay (MIT license, see LICENSE).

INSTALL
  1. Extract this whole zip to a folder (not into Program Files yourself).
  2. Right-click Start, open "Terminal (Admin)" or "Windows PowerShell (Admin)".
  3. cd to the extracted folder and run:   powershell -ExecutionPolicy Bypass -File .\install.ps1
     Add -WhatIf first to see every step without changing anything.
  If .NET 10 or the Windows App Runtime 1.8 is missing, the script says which, shows the winget
  command and asks before running it.

WHAT IT CHANGES
  - copies the app to C:\Program Files\Rigbay
  - creates C:\ProgramData\Rigbay\modules (Administrators and SYSTEM only)
  - Start menu shortcut, a scheduled task "Rigbay" (start in the tray at logon; skip with -NoTask),
    and an entry in Settings > Apps > Installed apps
  Nothing else is touched.

UNSIGNED
  The files are not code-signed, so Windows SmartScreen may warn the first time. The SHA-256 of
  this zip is in the release notes on GitHub.

REMOVE
  Settings > Apps > Installed apps > Rigbay, or:   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
  Your modules and settings are kept unless you add -RemoveData.

The "modules" folder next to install.ps1 is empty. A module folder placed there (it must contain a
module.json) is copied to the modules folder by the installer, turned off, until you enable it in Rigbay.
