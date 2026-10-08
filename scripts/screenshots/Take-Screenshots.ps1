<#
.SYNOPSIS
    Takes the README's screenshots, in the light and the dark theme.

.DESCRIPTION
    Copies the app from artifacts\app to artifacts\demo\app, puts the demo
    helper in place of the real one, runs it on the made-up chats in
    scripts\screenshots\demo and saves every view in both themes to
    docs\screenshots as <theme>-<name>.webp.

    It never touches your own TwoTicks or its data, and runs beside it.
    Build the app first with scripts\build.ps1. Needs Go, and Python with
    Pillow for the WebP files. Keep your hands off the mouse while it runs:
    opening a photo is a real click. For the notification it switches Windows
    to the light and the dark theme and back, and needs Do not disturb off.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Capture.ps1')

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$App = Join-Path $Root 'artifacts\demo\app'
$Shots = Join-Path $Root 'artifacts\demo\shots'
$Output = Join-Path $Root 'docs\screenshots'

if (-not (Test-Path (Join-Path $Root 'artifacts\app\TwoTicks.exe'))) {
    throw 'Build the app first: scripts\build.ps1'
}

Write-Host 'Preparing the demo app...'
Stop-Demo
Remove-Item -Recurse -Force $App, $Shots -ErrorAction SilentlyContinue
Copy-Item -Recurse (Join-Path $Root 'artifacts\app') $App
go build -C (Join-Path $PSScriptRoot 'demo-bridge') -o (Join-Path $App 'TwoTicks.Bridge.exe') .
if ($LASTEXITCODE -ne 0) { throw 'Building the demo helper failed.' }
New-Item -ItemType Directory -Force $Shots | Out-Null

foreach ($theme in 'Light', 'Dark') {
    $t = $theme.ToLowerInvariant()
    Write-Host "Taking the $t screenshots..."

    Start-Demo -Theme $theme
    Select-Chat 'Hiking crew'; Start-Sleep 2
    Save-Window "$Shots\$t-chat.png" | Out-Null

    Invoke-Element 'HeaderButton'; Start-Sleep 2
    Save-Window "$Shots\$t-group-info.png" | Out-Null
    Invoke-Element 'Close'                       # the info's close button comes first

    # The photo of the lake. A click can get lost while the window comes to the front.
    for ($try = 0; $try -lt 3 -and -not (Wait-Element 'Zoom in' 1); $try++) { Invoke-Point 765 467 }
    if (-not (Wait-Element 'Zoom in' 3)) { throw 'The photo viewer did not open.' }
    Start-Sleep 1
    Save-Window "$Shots\$t-viewer.png" | Out-Null
    Invoke-Element 'Close'                       # the viewer's

    Select-Chat 'Mia'; Start-Sleep 2
    Save-Window "$Shots\$t-media.png" | Out-Null

    Select-Chat 'Noah'; Start-Sleep 2
    Save-Window "$Shots\$t-files.png" | Out-Null

    Invoke-Element 'Menu'; Invoke-Element 'Settings'; Start-Sleep 1
    Save-Window "$Shots\$t-settings.png" | Out-Null
    Invoke-Element 'Done'

    Set-Field 'Search or start a new chat' 'pizza'; Start-Sleep 1
    Save-Window "$Shots\$t-search.png" | Out-Null

    # A notification over the corner of the window. Notifications follow the
    # theme of Windows rather than the app's, so Windows switches for a moment.
    $windowsTheme = Set-WindowsTheme $theme
    try {
        Start-Demo -Theme $theme
        Select-Chat 'Mia'; Start-Sleep 2
        Move-DemoWindow
        Save-Notification "$Shots\$t-notification.png" | Out-Null
    }
    finally {
        Set-WindowsTheme $windowsTheme | Out-Null
    }

    Start-Demo -Theme $theme -Link
    Save-Window "$Shots\$t-link.png" | Out-Null
}
Stop-Demo

Write-Host 'Saving them as WebP...'
New-Item -ItemType Directory -Force $Output | Out-Null
$convert = @'
import pathlib, sys
from PIL import Image
for png in pathlib.Path(sys.argv[1]).glob("*.png"):
    Image.open(png).save(pathlib.Path(sys.argv[2]) / (png.stem + ".webp"), quality=90, method=6)
'@
# From a file: Windows PowerShell drops the quotes inside a -c argument.
$script = Join-Path $Shots 'to-webp.py'
Set-Content -Encoding ascii $script $convert
python $script $Shots $Output
if ($LASTEXITCODE -ne 0) { throw 'Converting to WebP failed. Is Pillow installed (pip install pillow)?' }
Get-ChildItem $Output -Filter *.webp | ForEach-Object { '{0,-24} {1,6} KB' -f $_.Name, [math]::Round($_.Length / 1KB) }
