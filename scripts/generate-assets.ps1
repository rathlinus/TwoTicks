<#
.SYNOPSIS
    Draws the WinWhatsApp logo and writes Logo.png, AppIcon.png and AppIcon.ico,
    the tray icons, and the two pictures of the setup wizard to packaging.

.DESCRIPTION
    The logo is a mix of two: the four tiles of the Windows logo, in the same
    arrangement as in the WinFFmpeg and WinCapture logos, and on them WhatsApp's
    speech bubble with the phone, in white. The shapes and the green are those
    of WhatsApp.svg on Wikimedia Commons; its soft grey shadow is left out.

    The tray icons are the logo without and with a small red dot for unread chats.
    The icon for macOS, packaging\macos\icon.png, is the logo on a white tile.

    The generated files are checked in; run this only to change the logo.
    Needs Windows PowerShell, which draws with WPF.
#>
[CmdletBinding()]
param(
    [string]$OutputFolder
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

if (-not $OutputFolder) {
    $OutputFolder = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\WinWhatsApp.App\Assets'
}

New-Item -ItemType Directory -Force $OutputFolder | Out-Null
$OutputFolder = (Resolve-Path $OutputFolder).Path

# The paths of WhatsApp.svg, in its own coordinates (a 175.216 by 175.552 view box).
$BubbleOutline = 'm12.966 161.238 10.439-38.114a73.42 73.42 0 0 1-9.821-36.772c.017-40.556 33.021-73.55 73.578-73.55 19.681.01 38.154 7.669 52.047 21.572s21.537 32.383 21.53 52.037c-.018 40.553-33.027 73.553-73.578 73.553h-.032c-12.313-.005-24.412-3.094-35.159-8.954z'
$BubbleInside = 'M87.184 25.227c-33.733 0-61.166 27.423-61.178 61.13a60.98 60.98 0 0 0 9.349 32.535l1.455 2.313-6.179 22.558 23.146-6.069 2.235 1.324c9.387 5.571 20.15 8.517 31.126 8.523h.023c33.707 0 61.14-27.426 61.153-61.135a60.75 60.75 0 0 0-17.895-43.251 60.75 60.75 0 0 0-43.235-17.928z'
$Phone = 'M68.772 55.603c-1.378-3.061-2.828-3.123-4.137-3.176l-3.524-.043c-1.226 0-3.218.46-4.902 2.3s-6.435 6.287-6.435 15.332 6.588 17.785 7.506 19.013 12.718 20.381 31.405 27.75c15.529 6.124 18.689 4.906 22.061 4.6s10.877-4.447 12.408-8.74 1.532-7.971 1.073-8.74-1.685-1.226-3.525-2.146-10.877-5.367-12.562-5.981-2.91-.919-4.137.921-4.746 5.979-5.819 7.206-2.144 1.381-3.984.462-7.76-2.861-14.784-9.124c-5.465-4.873-9.154-10.891-10.228-12.73s-.114-2.835.808-3.751c.825-.824 1.838-2.147 2.759-3.22s1.224-1.84 1.836-3.065.307-2.301-.153-3.22-4.032-10.011-5.666-13.647'

function Get-Color([string]$Hex) {
    [System.Windows.Media.Color]::FromRgb(
        [Convert]::ToByte($Hex.Substring(0, 2), 16), [Convert]::ToByte($Hex.Substring(2, 2), 16), [Convert]::ToByte($Hex.Substring(4, 2), 16))
}

function New-Brush([string]$Hex) {
    $brush = New-Object System.Windows.Media.SolidColorBrush (Get-Color $Hex)
    $brush.Freeze()
    return $brush
}

# Draws the logo, $IconSize pixels square, centred in a $Width by $Height
# picture. With -Badge, a red dot sits in the top right corner.
function New-IconBitmap([int]$Width, [int]$Height, [double]$IconSize, [switch]$Badge, [string]$Background) {
    $x = ($Width - $IconSize) / 2
    $y = ($Height - $IconSize) / 2
    $s = $IconSize

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    if ($Background) {
        $dc.DrawRectangle((New-Brush $Background), $null, (New-Object System.Windows.Rect 0, 0, $Width, $Height))
    }

    # Four tiles in the arrangement of the Windows logo, the same as in WinFFmpeg and WinCapture.
    $gap = $s * 0.06
    $tile = ($s - $gap) / 2
    $radius = $tile * 0.12
    $tiles = New-Object System.Windows.Media.GeometryGroup
    foreach ($row in 0, 1) {
        foreach ($column in 0, 1) {
            $rect = New-Object System.Windows.Rect ($x + $column * ($tile + $gap)), ($y + $row * ($tile + $gap)), $tile, $tile
            $tiles.Children.Add((New-Object System.Windows.Media.RectangleGeometry $rect, $radius, $radius))
        }
    }

    # The green of the original, a little lighter in the top left than in the bottom right.
    $green = New-Object System.Windows.Media.LinearGradientBrush (Get-Color '57D163'), (Get-Color '23B33A'),
        (New-Object System.Windows.Point $x, $y), (New-Object System.Windows.Point ($x + $s), ($y + $s))
    $green.MappingMode = [System.Windows.Media.BrushMappingMode]::Absolute
    $green.Freeze()
    $dc.DrawGeometry($green, $null, $tiles)

    # The bubble is only its outline, so the tiles show through it. Together with
    # the phone it fills the middle 80% of the tiles, like the shapes of WinCapture.
    $bubble = New-Object System.Windows.Media.GeometryGroup
    $bubble.FillRule = [System.Windows.Media.FillRule]::EvenOdd
    $bubble.Children.Add([System.Windows.Media.Geometry]::Parse($BubbleOutline))
    $bubble.Children.Add([System.Windows.Media.Geometry]::Parse($BubbleInside))
    $phone = [System.Windows.Media.Geometry]::Parse('F1 ' + $Phone)

    $bounds = $bubble.Bounds
    $scale = $s * 0.80 / [Math]::Max($bounds.Width, $bounds.Height)
    $offsetX = $x + ($s - $bounds.Width * $scale) / 2 - $bounds.X * $scale
    $offsetY = $y + ($s - $bounds.Height * $scale) / 2 - $bounds.Y * $scale
    $dc.PushTransform((New-Object System.Windows.Media.MatrixTransform $scale, 0, 0, $scale, $offsetX, $offsetY))
    $dc.DrawGeometry((New-Brush 'FFFFFF'), $null, $bubble)
    $dc.DrawGeometry((New-Brush 'FFFFFF'), $null, $phone)
    $dc.Pop()

    if ($Badge) {
        $r = $IconSize * 0.2
        $center = New-Object System.Windows.Point ($Width - $r - ($Width - $IconSize) / 2), ($r + ($Height - $IconSize) / 2)
        $dc.DrawEllipse((New-Brush 'FFFFFF'), $null, $center, $r + $IconSize * 0.04, $r + $IconSize * 0.04)
        $dc.DrawEllipse((New-Brush 'E5383B'), $null, $center, $r, $r)
    }
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $Width, $Height, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function ConvertTo-Png($Bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

function Save-Png([string]$Name, [int]$Size) {
    [System.IO.File]::WriteAllBytes((Join-Path $OutputFolder $Name), (ConvertTo-Png (New-IconBitmap $Size $Size $Size)))
}

# Writes an .ico with PNG-compressed entries, which Windows has supported since Vista.
function Save-Ico([string]$Name, [int[]]$Sizes, [switch]$Badge) {
    $images = foreach ($size in $Sizes) {
        , (ConvertTo-Png (New-IconBitmap $size $size $size -Badge:$Badge))
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

# The logo on its own, for the README.
Save-Png 'Logo.png' 256

# What Windows shows next to the app's notifications.
Save-Png 'AppIcon.png' 256

Save-Ico 'AppIcon.ico' @(16, 20, 24, 32, 40, 48, 64, 256)

# The notification area: the logo, and the logo with a dot while chats are unread.
Save-Ico 'Tray.ico' @(16, 20, 24, 32, 40, 48)
Save-Ico 'TrayUnread.ico' @(16, 20, 24, 32, 40, 48) -Badge

# The two pictures the setup wizard shows. Inno Setup wants bitmaps without
# transparency, so the logo is drawn onto a solid background. Both are twice the
# size the wizard uses at 100%, which keeps them sharp on scaled displays.
function Save-WizardBitmap([string]$Name, [int]$Width, [int]$Height, [double]$IconSize, [double]$IconTop, [string]$Background) {
    $full = New-IconBitmap $Width ([int]($IconSize + 2 * $IconTop)) $IconSize -Background $Background
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawRectangle((New-Brush $Background), $null, (New-Object System.Windows.Rect 0, 0, $Width, $Height))
    $dc.DrawImage($full, (New-Object System.Windows.Rect 0, 0, $full.PixelWidth, $full.PixelHeight))
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $Width, $Height, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.BmpBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create((New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bitmap, ([System.Windows.Media.PixelFormats]::Bgr24), $null, 0)))
    $stream = [System.IO.File]::Create((Join-Path $packagingFolder $Name))
    $encoder.Save($stream)
    $stream.Dispose()
}

$packagingFolder = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\packaging'
New-Item -ItemType Directory -Force $packagingFolder | Out-Null
$packagingFolder = (Resolve-Path $packagingFolder).Path
Save-WizardBitmap 'installer-side.bmp' 328 628 168 150 '202020'
Save-WizardBitmap 'installer-small.bmp' 110 110 94 8 'FFFFFF'

# The icon of the app on macOS: the logo on a white tile with round corners,
# in the size and with the margin macOS draws its icons in, so the Dock does
# not put a tile of its own around it.
function Save-MacIcon([string]$Path) {
    $canvas = 1024
    $tile = 824
    $margin = ($canvas - $tile) / 2
    $logoSize = 500
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $background = New-Object System.Windows.Media.LinearGradientBrush (Get-Color 'FFFFFF'), (Get-Color 'ECEFEC'),
        (New-Object System.Windows.Point 0, 0), (New-Object System.Windows.Point 0, 1)
    $background.Freeze()
    $radius = $tile * 0.2237
    $dc.DrawRoundedRectangle($background, $null, (New-Object System.Windows.Rect $margin, $margin, $tile, $tile), $radius, $radius)
    $logo = New-IconBitmap $logoSize $logoSize $logoSize
    $dc.DrawImage($logo, (New-Object System.Windows.Rect (($canvas - $logoSize) / 2), (($canvas - $logoSize) / 2), $logoSize, $logoSize))
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $canvas, $canvas, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    [System.IO.File]::WriteAllBytes($Path, (ConvertTo-Png $bitmap))
}

New-Item -ItemType Directory -Force (Join-Path $packagingFolder 'macos') | Out-Null
Save-MacIcon (Join-Path $packagingFolder 'macos\icon.png')

Write-Host "Wrote assets to $OutputFolder"
