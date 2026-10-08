<#
.SYNOPSIS
    Helpers to run TwoTicks on the demo helper and take screenshots of it.

.DESCRIPTION
    Dot-source it, then:

        Start-Demo -Theme Dark          # starts artifacts\demo\app on the chats in demo
        Select-Chat 'Hiking crew'       # opens a chat
        Invoke-Element 'HeaderButton'   # clicks a button by its name or automation id
        Save-Window artifacts\demo\shots\info.png
        Move-DemoWindow; Save-Notification artifacts\demo\shots\notification.png
        Stop-Demo

    Take-Screenshots.ps1 uses these to take all of the README's screenshots.
#>

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class DemoWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, out RECT rect, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    // The smallest rectangle around the pixels that differ between two pictures of the same size.
    public static Rectangle Changed(Bitmap a, Bitmap b, int threshold) {
        var rect = new Rectangle(0, 0, a.Width, a.Height);
        BitmapData da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pa = new byte[da.Stride * a.Height];
        var pb = new byte[db.Stride * b.Height];
        Marshal.Copy(da.Scan0, pa, 0, pa.Length);
        Marshal.Copy(db.Scan0, pb, 0, pb.Length);
        a.UnlockBits(da);
        b.UnlockBits(db);
        int left = a.Width, top = a.Height, right = -1, bottom = -1;
        for (int y = 0; y < a.Height; y++) {
            for (int x = 0; x < a.Width; x++) {
                int i = y * da.Stride + x * 4;
                if (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]) > threshold) {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            }
        }
        return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
}
'@
# Real pixels, as DWM reports them, rather than sizes scaled for 100 %.
[DemoWin]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null

$script:Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# Starts the demo with a fresh data folder, in the light or the dark theme.
# -Link shows the screen for linking a phone instead of the chats.
function Start-Demo([ValidateSet('Light', 'Dark')][string]$Theme = 'Light', [switch]$Link) {
    Stop-Demo
    $data = Join-Path $script:Root 'artifacts\demo\data'
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $data | Out-Null
    # The window size fixes where Invoke-Point clicks.
    @{
        Notifications = $true; CloseToTray = $false; Theme = $Theme; ChatListWidth = 400
        Window = @{ X = 100; Y = 60; Width = 1400; Height = 900; Maximized = $false }
    } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $data 'settings.json')

    $env:TWOTICKS_DATA = $data
    $env:TWOTICKS_DEMO = Join-Path $PSScriptRoot 'demo'
    $env:TWOTICKS_DEMO_QR = if ($Link) { '1' } else { '' }
    $process = Start-Process (Join-Path $script:Root 'artifacts/demo/app/TwoTicks.exe') -PassThru
    Remove-Item Env:\TWOTICKS_DATA, Env:\TWOTICKS_DEMO, Env:\TWOTICKS_DEMO_QR -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 50 -and $process.MainWindowHandle -eq 0; $i++) {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }
    Start-Sleep -Seconds 2
}

# The running demo, also one started from another PowerShell session.
function Get-DemoProcess {
    $exe = (Resolve-Path (Join-Path $script:Root 'artifacts/demo/app/TwoTicks.exe')).Path
    Get-Process TwoTicks -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
}

function Stop-Demo {
    if ($p = Get-DemoProcess) {
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit()
    }
}

# The demo's window handle, once it has a window.
function Get-DemoHandle {
    for ($i = 0; $i -lt 50; $i++) {
        $p = Get-DemoProcess
        if ($p -and $p.MainWindowHandle -ne 0) { return $p.MainWindowHandle }
        Start-Sleep -Milliseconds 200
    }
    throw 'The demo has no window.'
}

function Get-DemoWindow { [System.Windows.Automation.AutomationElement]::FromHandle((Get-DemoHandle)) }

# Clicks the first element with this name or automation id: a button, a menu item.
function Invoke-Element([string]$Name, [int]$Index = 0) {
    $condition = New-Object System.Windows.Automation.OrCondition `
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Name),
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $Name)
    $found = (Get-DemoWindow).FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($found.Count -le $Index) { throw "Nothing named '$Name' in the window." }
    $element = $found[$Index]
    foreach ($pattern in [System.Windows.Automation.InvokePattern]::Pattern, [System.Windows.Automation.SelectionItemPattern]::Pattern) {
        $p = $null
        if ($element.TryGetCurrentPattern($pattern, [ref]$p)) {
            if ($p -is [System.Windows.Automation.InvokePattern]) { $p.Invoke() } else { $p.Select() }
            Start-Sleep -Milliseconds 800
            return
        }
    }
    throw "'$Name' cannot be clicked."
}

# Brings the demo to the front, so its window buttons are not dimmed, and
# returns its handle. Windows allows that only right after some input, so
# this nudges the mouse first; a key press would make buttons show focus rings.
function Show-DemoWindow {
    $hwnd = Get-DemoHandle
    [DemoWin]::mouse_event(1, 1, 0, 0, [UIntPtr]::Zero)    # move by one pixel
    [DemoWin]::mouse_event(1, -1, 0, 0, [UIntPtr]::Zero)   # and back
    [DemoWin]::SetForegroundWindow($hwnd) | Out-Null
    $hwnd
}

# Waits until something with this name or automation id shows; false if it does not.
function Wait-Element([string]$Name, [int]$Seconds = 5) {
    $condition = New-Object System.Windows.Automation.OrCondition `
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Name),
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $Name)
    for ($i = 0; $i -lt $Seconds * 5; $i++) {
        if ((Get-DemoWindow).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)) { return $true }
        Start-Sleep -Milliseconds 200
    }
    $false
}

# Clicks a point of the window, in pixels from its top left corner, for what
# UI Automation cannot click, such as a photo in a chat.
function Invoke-Point([int]$X, [int]$Y) {
    $hwnd = Show-DemoWindow
    $r = New-Object DemoWin+RECT
    [DemoWin]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) | Out-Null
    [DemoWin]::SetCursorPos($r.Left + $X, $r.Top + $Y) | Out-Null
    [DemoWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)   # left down
    [DemoWin]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)   # left up
    Start-Sleep -Seconds 1
}

# Types into the field with this name, such as the search field.
function Set-Field([string]$Name, [string]$Text) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Name
    $field = (Get-DemoWindow).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    Start-Sleep -Seconds 1
}

# Opens a chat by the start of its name. The rows have no name of their own,
# so this finds the name's text and selects the row around it.
function Select-Chat([string]$Name) {
    $texts = (Get-DemoWindow).FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Text)))
    $text = $texts | Where-Object { $_.Current.Name.StartsWith($Name) } | Select-Object -First 1
    if (-not $text) { throw "No chat named '$Name'." }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $row = $text
    while ($row -and $row.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $row = $walker.GetParent($row) }
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 1
}

# Moves the window to the bottom right corner of the main screen, where
# Windows shows notifications.
function Move-DemoWindow {
    $hwnd = Get-DemoHandle
    $area = New-Object DemoWin+RECT
    [DemoWin]::SystemParametersInfo(0x30, 0, [ref]$area, 0) | Out-Null   # SPI_GETWORKAREA
    $w = New-Object DemoWin+RECT
    [DemoWin]::GetWindowRect($hwnd, [ref]$w) | Out-Null
    $r = New-Object DemoWin+RECT
    [DemoWin]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) | Out-Null
    # The window's rectangle has an invisible border around what shows.
    [DemoWin]::SetWindowPos($hwnd, [IntPtr]::Zero, ($w.Left + $area.Right - $r.Right), ($w.Top + $area.Bottom - $r.Bottom), 0, 0, 0x15) | Out-Null   # NOSIZE, NOZORDER, NOACTIVATE
    Start-Sleep -Milliseconds 500
}

# Switches Windows, not the app, to the light or the dark theme: notifications
# follow the theme of Windows. Returns the theme it had before.
function Set-WindowsTheme([ValidateSet('Light', 'Dark')][string]$Theme) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
    $before = if ((Get-ItemProperty $key).SystemUsesLightTheme -eq 0) { 'Dark' } else { 'Light' }
    Set-ItemProperty $key SystemUsesLightTheme ([int]($Theme -eq 'Light'))
    $result = [IntPtr]::Zero
    [DemoWin]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [IntPtr]::Zero, 'ImmersiveColorSet', 2, 5000, [ref]$result) | Out-Null   # WM_SETTINGCHANGE to every window
    Start-Sleep -Seconds 2
    $before
}

# A part of the screen, in pixels.
function Copy-Screen([System.Drawing.Rectangle]$Rect) {
    $bitmap = New-Object System.Drawing.Bitmap $Rect.Width, $Rect.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($Rect.Location, [System.Drawing.Point]::Empty, $Rect.Size)
    $graphics.Dispose()
    $bitmap
}

# Has the demo helper send the incoming message of demo.json, waits for its
# notification and saves the window with the notification over its corner.
# Move the window to where notifications show first, with Move-DemoWindow.
function Save-Notification([string]$Path) {
    $hwnd = Show-DemoWindow
    Start-Sleep -Milliseconds 500
    $r = New-Object DemoWin+RECT
    [DemoWin]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) | Out-Null
    # The corner the notification shows over. Nothing else in it moves.
    $corner = New-Object System.Drawing.Rectangle ($r.Right - 640), ($r.Bottom - 480), 640, 480
    $before = Copy-Screen $corner
    New-Item -ItemType File -Force (Join-Path $script:Root 'artifacts\demo\data\demo-incoming') | Out-Null
    $box = [System.Drawing.Rectangle]::Empty
    for ($i = 0; $i -lt 20 -and $box.IsEmpty; $i++) {
        Start-Sleep -Milliseconds 250
        $after = Copy-Screen $corner
        $box = [DemoWin]::Changed($before, $after, 24)
    }
    if ($box.IsEmpty) { throw 'No notification showed. Is Do not disturb on?' }
    Start-Sleep -Milliseconds 1500                     # until it has slid in
    $after = Copy-Screen $corner
    $box = [DemoWin]::Changed($before, $after, 24)
    $box.Inflate(32, 32)                               # with its shadow
    $box.Intersect((New-Object System.Drawing.Rectangle 0, 0, $corner.Width, $corner.Height))

    $window = Get-WindowBitmap $hwnd
    $graphics = [System.Drawing.Graphics]::FromImage($window)
    $at = New-Object System.Drawing.Rectangle ($window.Width - $corner.Width + $box.X), ($window.Height - $corner.Height + $box.Y), $box.Width, $box.Height
    $graphics.DrawImage($after, $at, $box, [System.Drawing.GraphicsUnit]::Pixel)
    $graphics.Dispose()
    $full = Save-Bitmap $window $Path
    $window.Dispose(); $before.Dispose(); $after.Dispose()

    # Takes it off the screen and out of the notification centre. Only the demo
    # chat's: your own TwoTicks shows its notifications under the same name.
    $history = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]::History
    $history.RemoveGroup('120363000000000001@g.us', 'TwoTicks')
    $full
}

# The window, without the shadow around it. Works while other windows cover it.
function Get-WindowBitmap([IntPtr]$hwnd) {
    $w = New-Object DemoWin+RECT
    [DemoWin]::GetWindowRect($hwnd, [ref]$w) | Out-Null
    $r = New-Object DemoWin+RECT
    [DemoWin]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) | Out-Null   # DWMWA_EXTENDED_FRAME_BOUNDS
    $whole = New-Object System.Drawing.Bitmap ($w.Right - $w.Left), ($w.Bottom - $w.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($whole)
    $hdc = $graphics.GetHdc()
    [DemoWin]::PrintWindow($hwnd, $hdc, 2) | Out-Null                   # PW_RENDERFULLCONTENT
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    $visible = New-Object System.Drawing.Rectangle ($r.Left - $w.Left), ($r.Top - $w.Top), ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $bitmap = $whole.Clone($visible, $whole.PixelFormat)
    $whole.Dispose()
    $bitmap
}

function Save-Bitmap([System.Drawing.Bitmap]$Bitmap, [string]$Path) {
    $full = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $script:Root $Path }
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    $Bitmap.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $full
}

# Saves the window, without the shadow around it, as a PNG. Works while
# other windows cover it.
function Save-Window([string]$Path) {
    $hwnd = Show-DemoWindow
    Start-Sleep -Milliseconds 500
    $bitmap = Get-WindowBitmap $hwnd
    $full = Save-Bitmap $bitmap $Path
    $bitmap.Dispose()
    $full
}
