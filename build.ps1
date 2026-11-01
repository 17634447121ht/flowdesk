param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$staging = Join-Path $taskRoot ('bin\build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
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
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001','/main:FlowDesk.Program',('/out:' + (Join-Path $staging 'FlowDesk.exe')),('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest')),('/win32icon:' + (Join-Path $taskRoot 'FlowDesk.ico')))
foreach ($reference in $references) { $arguments += '/reference:' + $reference }
foreach ($reference in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) { $arguments += '/reference:' + (Join-Path $wpf $reference) }
$xaml = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Xaml') -Recurse -Filter System.Xaml.dll | Select-Object -First 1 -ExpandProperty FullName
if ($xaml) { $arguments += '/reference:' + $xaml }
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
$target = Join-Path $destination 'FlowDesk.exe'
try {
    if (Test-Path -LiteralPath $target) {
        [IO.File]::Replace((Join-Path $staging 'FlowDesk.exe'), $target, ($target + '.previous'), $true)
    } else {
        [IO.File]::Move((Join-Path $staging 'FlowDesk.exe'), $target)
    }
    $configSource = Join-Path $taskRoot 'FlowDesk.exe.config'
    $configTarget = Join-Path $destination 'FlowDesk.exe.config'
    if ($configSource -ne $configTarget) { Copy-Item -LiteralPath $configSource -Destination $configTarget -Force }
    # Keep the early preview filename usable when updating an existing folder.
    $legacyTarget = Join-Path $destination 'FlowDesk-updated.exe'
    if (Test-Path -LiteralPath $legacyTarget) {
        $legacyStaged = Join-Path $staging 'FlowDesk-updated.exe'
        Copy-Item -LiteralPath $target -Destination $legacyStaged
        [IO.File]::Replace($legacyStaged, $legacyTarget, ($legacyTarget + '.previous'), $true)
        Copy-Item -LiteralPath $configSource -Destination ($legacyTarget + '.config') -Force
    }
} catch {
    throw ('编译已完成，但无法更新程序。请先从托盘退出 FlowDesk 后重试。暂存目录：' + $staging + '。原因：' + $_.Exception.Message)
}
Write-Output ('Built: ' + $target)
