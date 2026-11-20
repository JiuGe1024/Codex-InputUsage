$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'outputs'
[void](New-Item -ItemType Directory -Path $output -Force)
$exe = Join-Path $output 'Codex-InputUsage-Overlay.exe'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Core.dll'
) + (@('UIAutomationClient','UIAutomationTypes','WindowsBase') | ForEach-Object { '/reference:' + (Join-Path $framework "WPF\$_.dll") })
$sources = Get-ChildItem -Path (Join-Path $root 'src\overlay') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
if (-not $sources) { throw 'No C# source files found under src/overlay.' }
& (Join-Path $framework 'csc.exe') /nologo /utf8output /target:winexe /platform:x64 @references "/out:$exe" @sources
if ($LASTEXITCODE) { throw 'Overlay compilation failed.' }
Get-Item -LiteralPath $exe
