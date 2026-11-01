param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'bin\taskbar-tests' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$testExe = Join-Path $OutputDirectory 'TaskbarRegressionTests.exe'
$compileArgs = @('/nologo','/target:exe','/platform:x64','/codepage:65001','/main:TaskbarRegressionTests',('/out:' + $testExe))
foreach ($reference in @('System.dll','System.Core.dll','System.Xml.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
    $compileArgs += '/reference:' + $reference
}
foreach ($reference in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) {
    $compileArgs += '/reference:' + (Join-Path $framework ('WPF\' + $reference))
}
$xaml = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Xaml') -Recurse -Filter System.Xaml.dll | Select-Object -First 1 -ExpandProperty FullName
if ($xaml) { $compileArgs += '/reference:' + $xaml }
$compileArgs += @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$compileArgs += Join-Path $PSScriptRoot 'TaskbarRegressionTests.cs'
& (Join-Path $framework 'csc.exe') @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Taskbar tests compilation failed.' }
& $testExe (Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE -ne 0) { throw 'Taskbar tests failed.' }
