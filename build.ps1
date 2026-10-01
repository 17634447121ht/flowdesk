$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path $compiler)) { throw '.NET Framework 4.8 compiler is required.' }
$wpf = Join-Path $framework 'WPF'
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap(48,48)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(18,28,45))
$down = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(114,229,206),4)
$up = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(179,166,255),4)
$down.StartCap = 'Round'; $down.EndCap = 'Round'; $up.StartCap = 'Round'; $up.EndCap = 'Round'
$graphics.DrawLine($down,16,11,16,36)
$graphics.DrawLine($down,9,29,16,36)
$graphics.DrawLine($down,23,29,16,36)
$graphics.DrawLine($up,33,12,33,37)
$graphics.DrawLine($up,26,19,33,12)
$graphics.DrawLine($up,40,19,33,12)
$stream = [System.IO.File]::Create((Join-Path $taskRoot 'FlowDesk.ico'))
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$icon.Save($stream)
$stream.Dispose(); $icon.Dispose(); $down.Dispose(); $up.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
$references = @('System.dll','System.Core.dll','System.Xml.dll','System.Drawing.dll','System.Windows.Forms.dll')
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/codepage:65001','/main:FlowDesk.Program',('/out:' + (Join-Path $taskRoot 'FlowDesk.exe')),('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest')),('/win32icon:' + (Join-Path $taskRoot 'FlowDesk.ico')))
foreach ($reference in $references) { $arguments += '/reference:' + $reference }
foreach ($reference in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) { $arguments += '/reference:' + (Join-Path $wpf $reference) }
$xaml = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Xaml') -Recurse -Filter System.Xaml.dll | Select-Object -First 1 -ExpandProperty FullName
if ($xaml) { $arguments += '/reference:' + $xaml }
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output ('Built: ' + (Join-Path $taskRoot 'FlowDesk.exe'))
