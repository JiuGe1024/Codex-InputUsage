$ErrorActionPreference = 'Stop'

# Clean up any legacy HKCU Run entry if present
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -Force
    Write-Output 'Removed legacy HKCU Run entry: CodexInputUsageOverlay'
}

$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$configPath = Join-Path $codexHome 'config.toml'
if (Test-Path -LiteralPath $configPath) {
    $content = [IO.File]::ReadAllText($configPath, [Text.Encoding]::UTF8)
    $pattern = '(?ms)\r?\n?\[mcp_servers\.codex_input_usage\]\r?\n(?:(?!\[).*(?:\r?\n|$))*'
    $cleaned = [Text.RegularExpressions.Regex]::Replace($content, $pattern, '').TrimEnd() + "`r`n"
    if ($cleaned -ne $content) {
        [IO.File]::WriteAllText($configPath, $cleaned, (New-Object Text.UTF8Encoding($false)))
        Write-Output "Removed [mcp_servers.codex_input_usage] from $configPath"
    } else {
        Write-Output '[mcp_servers.codex_input_usage] was not present in config.toml.'
    }
}
