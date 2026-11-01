param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'bin\tray-tests' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $OutputDirectory 'TraySpeedIconTests.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 ('/out:' + $testExe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll (Join-Path $repoRoot 'src\TraySpeedIcon.cs') (Join-Path $PSScriptRoot 'TraySpeedIconTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Tray icon tests compilation failed.' }
& $testExe $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Tray icon tests failed.' }
Write-Output ('Tray icon render fixtures: ' + (Join-Path $OutputDirectory 'tray-speed-icons.png'))
