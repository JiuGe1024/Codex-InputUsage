$ErrorActionPreference = 'Stop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'CodexInputUsageOverlay' -Force
    Write-Output 'Removed logon auto-start: CodexInputUsageOverlay'
} else {
    Write-Output 'Logon auto-start entry CodexInputUsageOverlay was not present.'
}
