$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$configPath = Join-Path $codexHome 'config.toml'
if (-not ('CodexInputUsage.CodexCompanionConfig' -as [type])) {
    Add-Type -Path (Join-Path $root 'src\overlay\CodexCompanionConfig.cs')
}
[void][CodexInputUsage.CodexCompanionConfig]::Update($configPath, $null, $null, $false)
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay'
}
Write-Output 'Codex InputUsage companion is disabled.'
