param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$testRun = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path ([System.IO.Path]::GetTempPath()) ('FlowDesk-process-tests-' + [Guid]::NewGuid().ToString('N')) } else { [System.IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Path $testRun -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'These native-layout regression tests require 64-bit .NET Framework 4.8.' }
$testExe = Join-Path $testRun 'ProcessTrafficTests.exe'
& $compiler /nologo /platform:x64 /codepage:65001 ('/out:' + $testExe) /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll (Join-Path $sourceRoot 'src\Core.cs') (Join-Path $sourceRoot 'src\ProcessTraffic.cs') (Join-Path $PSScriptRoot 'ProcessTrafficRegressionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Process traffic regression tests did not compile.' }
Push-Location $testRun
try {
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Process traffic regression tests failed.' }
}
finally { Pop-Location }
Write-Output ('Test artifacts: ' + $testRun)
