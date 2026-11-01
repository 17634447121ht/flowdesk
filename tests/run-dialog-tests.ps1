param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $env:TEMP ('FlowDesk-dialog-tests-' + [guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$testExe = Join-Path $OutputDirectory 'DialogRegressionTests.exe'
$arguments = @('/nologo', '/target:exe', '/main:DialogRegressionTests', '/codepage:65001', '/optimize+', ('/out:' + $testExe))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')) {
    $arguments += '/reference:' + $reference
}
foreach ($reference in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll')) {
    $arguments += '/reference:' + (Join-Path (Join-Path $framework 'WPF') $reference)
}
$xaml = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Xaml') -Recurse -Filter System.Xaml.dll | Select-Object -First 1 -ExpandProperty FullName
if ($xaml) { $arguments += '/reference:' + $xaml }
foreach ($file in @('Core.cs', 'ProcessTraffic.cs', 'Dialogs.cs')) {
    $arguments += Join-Path (Join-Path $taskRoot 'src') $file
}
$arguments += Join-Path $PSScriptRoot 'DialogRegressionTests.cs'
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Dialog regression test compilation failed.' }
& $testExe (Join-Path $OutputDirectory 'renders')
if ($LASTEXITCODE -ne 0) { throw 'Dialog regression tests failed.' }
Write-Output ('Rendered dialog fixtures: ' + (Join-Path $OutputDirectory 'renders'))
