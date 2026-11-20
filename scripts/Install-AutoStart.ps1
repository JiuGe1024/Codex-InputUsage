$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'outputs\Codex-InputUsage-Overlay.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-OverlayPrototype.ps1')
}

# Remove any legacy HKCU Run registry entry so Windows never prompts for logon auto-start.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -Force
    Write-Output 'Removed legacy HKCU Run entry: CodexInputUsageOverlay'
}

$node = (Get-Command node -ErrorAction Stop).Source
$companion = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'codex-mcp-companion.mjs')).Path
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$configPath = Join-Path $codexHome 'config.toml'
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Codex config.toml not found at $configPath"
}

$content = [IO.File]::ReadAllText($configPath, [Text.Encoding]::UTF8)
# Strip any existing [mcp_servers.codex_input_usage] block cleanly
$pattern = '(?ms)\r?\n?\[mcp_servers\.codex_input_usage\]\r?\n(?:(?!\[).*(?:\r?\n|$))*'
$cleaned = [Text.RegularExpressions.Regex]::Replace($content, $pattern, '').TrimEnd()
$block = @"

[mcp_servers.codex_input_usage]
command = '$node'
args = ['$companion']
"@
$updated = $cleaned + "`r`n" + $block + "`r`n"
[IO.File]::WriteAllText($configPath, $updated, (New-Object Text.UTF8Encoding($false)))
Write-Output "Registered Codex native companion in $configPath -> $companion"
