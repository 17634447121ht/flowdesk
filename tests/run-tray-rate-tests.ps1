param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'bin\tray-rate-tests' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $OutputDirectory 'TrayRateDisplayTests.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 ('/out:' + $testExe) (Join-Path $repoRoot 'src\TrayRateDisplay.cs') (Join-Path $PSScriptRoot 'TrayRateDisplayTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Tray rate tests compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Tray rate tests failed.' }
