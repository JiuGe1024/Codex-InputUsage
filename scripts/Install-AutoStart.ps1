$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'outputs\Codex-InputUsage-Overlay.exe'
$companion = Join-Path $PSScriptRoot 'codex-mcp-companion.mjs'
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$configPath = Join-Path $codexHome 'config.toml'
if (-not ('CodexInputUsage.CodexCompanionConfig' -as [type])) {
    Add-Type -Path (Join-Path $root 'src\overlay\CodexCompanionConfig.cs')
}
$node = [CodexInputUsage.CodexCompanionConfig]::FindNodeExecutable()
if ([CodexInputUsage.CodexCompanionConfig]::EnableProblem($configPath, $companion, $node)) {
    throw 'Automatic startup requires config.toml, node.exe and the repository companion script.'
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'Build-OverlayPrototype.ps1')
}
if (-not [CodexInputUsage.CodexCompanionConfig]::Update($configPath, $companion, $node, $true)) {
    throw 'Companion startup was not registered because a required file is missing.'
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay'
}
Write-Output 'Registered the Codex InputUsage companion in config.toml.'
