param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $env:TEMP ('FlowDesk-core-tests-' + [guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$testExe = Join-Path $OutputDirectory 'CoreRegressionTests.exe'
& $compiler /nologo /target:exe /codepage:65001 /optimize+ ('/out:' + $testExe) (Join-Path $taskRoot 'src\Core.cs') (Join-Path $PSScriptRoot 'CoreRegressionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Core regression test compilation failed.' }
& $testExe (Join-Path $OutputDirectory ('data-' + [guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE -ne 0) { throw 'Core regression tests failed.' }
