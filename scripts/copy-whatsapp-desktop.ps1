<#
.SYNOPSIS
    Copies the icon and the sounds of the WhatsApp app from the Microsoft Store
    into src\TwoTicks.App\Assets: the icon to WhatsAppIcon, for the setting
    that shows it instead of the TwoTicks logo, and the sounds to
    WhatsAppSounds.

.DESCRIPTION
    Reads the sizes of the icon that WhatsApp ships in its package and writes
    from them the same files generate-assets.ps1 writes for the TwoTicks
    logo: AppIcon.png, AppIcon.ico, Tray.ico and TrayUnread.ico, the last with
    the same red dot for unread chats.

    The sounds are copied as they are: the tone for a new message
    (whatsapp_windows_pn_02.m4a), the ringtone and the hang-up sound of calls,
    and the ten alert tones WhatsApp lets you pick from (Alert-01 to Alert-10).

    WhatsApp has to be installed from the Microsoft Store. The copied files are
    checked in; run this only to take a newer version of them.
    Needs Windows PowerShell, which draws with WPF.
#>
[CmdletBinding()]
param(
    [string]$AssetsFolder
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

if (-not $AssetsFolder) {
    $AssetsFolder = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\TwoTicks.App\Assets'
}
$OutputFolder = Join-Path $AssetsFolder 'WhatsAppIcon'
$SoundsFolder = Join-Path $AssetsFolder 'WhatsAppSounds'

$package = Get-AppxPackage -Name '5319275A.WhatsAppDesktop' | Select-Object -First 1
if (-not $package) {
    throw 'WhatsApp from the Microsoft Store is not installed.'
}
$assets = Join-Path $package.InstallLocation 'Assets'

New-Item -ItemType Directory -Force $OutputFolder | Out-Null
$OutputFolder = (Resolve-Path $OutputFolder).Path

# The icon for the taskbar and Start: the "unplated" sizes have no tile behind them.
function Get-Source([int]$Size) {
    Join-Path $assets "AppList.targetsize-$($Size)_altform-unplated.png"
}

function Read-Bitmap([string]$Path) {
    $bitmap = New-Object System.Windows.Media.Imaging.BitmapImage
    $bitmap.BeginInit()
    $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $bitmap.UriSource = New-Object System.Uri $Path
    $bitmap.EndInit()
    return $bitmap
}

# The icon, with -Badge a red dot in the top right corner as in generate-assets.ps1.
function Get-IconPng([int]$Size, [switch]$Badge) {
    $source = Get-Source $Size
    if (-not $Badge) {
        return , [System.IO.File]::ReadAllBytes($source)
    }

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawImage((Read-Bitmap $source), (New-Object System.Windows.Rect 0, 0, $Size, $Size))
    $r = $Size * 0.2
    $center = New-Object System.Windows.Point ($Size - $r), $r
    $white = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Colors]::White)
    $red = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0xE5, 0x38, 0x3B))
    $dc.DrawEllipse($white, $null, $center, $r + $Size * 0.04, $r + $Size * 0.04)
    $dc.DrawEllipse($red, $null, $center, $r, $r)
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $Size, $Size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

# Writes an .ico with PNG-compressed entries, as generate-assets.ps1 does.
function Save-Ico([string]$Name, [int[]]$Sizes, [switch]$Badge) {
    $images = foreach ($size in $Sizes) {
        , (Get-IconPng $size -Badge:$Badge)
    }

    $ico = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $ico
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$Sizes.Count)
    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $dimension = if ($Sizes[$i] -ge 256) { 0 } else { $Sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $OutputFolder $Name), $ico.ToArray())
    $writer.Dispose()
}

Copy-Item (Get-Source 256) (Join-Path $OutputFolder 'AppIcon.png')
Save-Ico 'AppIcon.ico' @(16, 20, 24, 32, 40, 48, 64, 256)
Save-Ico 'Tray.ico' @(16, 20, 24, 32, 40, 48)
Save-Ico 'TrayUnread.ico' @(16, 20, 24, 32, 40, 48) -Badge

New-Item -ItemType Directory -Force $SoundsFolder | Out-Null
Copy-Item (Join-Path $package.InstallLocation 'Sounds\*') $SoundsFolder

Write-Host "Wrote the icon and the sounds of WhatsApp $($package.Version) to $AssetsFolder"
