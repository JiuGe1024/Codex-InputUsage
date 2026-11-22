$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'outputs'
[void](New-Item -ItemType Directory -Path $output -Force)
$exe = Join-Path $output 'Codex-InputUsage.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll "/out:$exe" (Join-Path $root 'src\TokenBarLauncher.cs')
if ($LASTEXITCODE) { throw 'Launcher compilation failed.' }
Get-Item -LiteralPath $exe
