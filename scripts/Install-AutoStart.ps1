$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'outputs\Codex-InputUsage-Overlay.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-OverlayPrototype.ps1')
}
$resolved = (Resolve-Path -LiteralPath $exe).Path
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -Value "`"$resolved`"" -PropertyType String -Force | Out-Null
Write-Output "Installed logon auto-start: CodexInputUsageOverlay -> `"$resolved`""
