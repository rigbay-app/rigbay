# E2E1: the other half of the admin-only folder check. Run from a NORMAL (not elevated) PowerShell after hooks.ps1 left the H6 folders
# (<Root>\hooks\h6-acl\Rigbay\modules and modules.install, made by the app itself). A program running as this user without elevation must not be
# able to plant a module, change the cached list, or swap what was verified (docs/INSTALL.md section 3, section 9).
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root, [string]$Out)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = Join-Path $Root 'evidence' }
$report = Join-Path $Out 'report-acl-user.txt'
Remove-Item $report -ErrorAction SilentlyContinue
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$pass = 0; $fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:pass++ } else { $script:fail++ }
    $line = '{0}  {1}{2}' -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { '  -- ' + $Detail } else { '' })
    Write-Host $line; Add-Content -LiteralPath $report -Value $line -Encoding UTF8
}
Check 'this shell is NOT elevated (an elevated shell would prove nothing)' (-not $isAdmin)
$parent = Join-Path $Root 'hooks\h6-acl\Rigbay'
$mods = Join-Path $parent 'modules'
$inst = Join-Path $parent 'modules.install'
if (-not (Test-Path -LiteralPath $parent)) { Write-Host "no H6 folders at $parent (run hooks first)"; exit 2 }

function Denied([scriptblock]$Try) { try { & $Try; $false } catch { $true } }
Check 'cannot list the modules folder' (Denied { Get-ChildItem -LiteralPath $mods -ErrorAction Stop | Out-Null })
Check 'cannot plant a module folder in modules\' (Denied { New-Item -ItemType Directory -Path (Join-Path $mods 'planted') -ErrorAction Stop | Out-Null })
Check 'cannot write a file in modules\' (Denied { Set-Content -LiteralPath (Join-Path $mods 'planted.txt') -Value x -ErrorAction Stop })
Check 'cannot edit the pending list' (Denied { Add-Content -LiteralPath (Join-Path $inst 'pending.json') -Value ' ' -ErrorAction Stop })
Check 'cannot replace the cached registry' (Denied { Set-Content -LiteralPath (Join-Path $inst 'registry\registry.json') -Value '{}' -ErrorAction Stop })
Check 'cannot swap a verified, parked module file' (Denied { Set-Content -LiteralPath (Join-Path $inst 'ready\recorder\Rigbay.Recorder.dll') -Value x -ErrorAction Stop })
Check 'cannot create anything in the Rigbay parent folder either' (Denied { New-Item -ItemType Directory -Path (Join-Path $parent 'planted') -ErrorAction Stop | Out-Null })
Check 'cannot delete the modules folder' (Denied { Remove-Item -LiteralPath $mods -Recurse -Force -ErrorAction Stop })
$h6data = Join-Path $Root 'hooks\h6-acl\data'
Check 'control: the data folder next to it (not hardened, it plays the role of %LOCALAPPDATA%\Rigbay) is writable by this user' (-not (Denied { Add-Content -LiteralPath (Join-Path $h6data 'logs\rigbay.log') -Value ' ' -ErrorAction Stop }))
"== E2E1 non-elevated ACL check: $pass passed, $fail failed"
Add-Content -LiteralPath $report -Value "== $pass passed, $fail failed" -Encoding UTF8
exit [int]($fail -gt 0)
