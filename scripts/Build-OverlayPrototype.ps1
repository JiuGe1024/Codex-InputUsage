$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'outputs'
[void](New-Item -ItemType Directory -Path $output -Force)
$exe = Join-Path $output 'Codex-InputUsage-Overlay.exe'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('UIAutomationClient','UIAutomationTypes','WindowsBase') | ForEach-Object { '/reference:' + (Join-Path $framework "WPF\$_.dll") }
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @references "/out:$exe" (Join-Path $root 'src/OverlayPrototype.cs')
if ($LASTEXITCODE) { throw 'Overlay compilation failed.' }
Get-Item -LiteralPath $exe
