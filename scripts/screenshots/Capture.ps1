<#
.SYNOPSIS
    Helpers to run WinWhatsApp on the demo helper and take screenshots of it.

.DESCRIPTION
    Dot-source it, then:

        Start-Demo                      # starts artifacts\demo\app on artifacts\demo\data
        Invoke-Element 'Hiking crew'    # clicks a chat, a button, anything with that name
        Save-Window docs\screenshots\chats.png
        Stop-Demo

    See README.md next to this script for the whole procedure.
#>

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DemoWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
}
'@
# Real pixels, as DWM reports them, rather than sizes scaled for 100 %.
[DemoWin]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null

$script:Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function Start-Demo {
    Stop-Demo
    $env:WINWHATSAPP_DATA = Join-Path $script:Root 'artifacts\demo\data'
    $process = Start-Process (Join-Path $script:Root 'artifacts/demo/app/WinWhatsApp.exe') -PassThru
    Remove-Item Env:\WINWHATSAPP_DATA
    for ($i = 0; $i -lt 50 -and $process.MainWindowHandle -eq 0; $i++) {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }
    Start-Sleep -Seconds 2
}

# The running demo, also one started from another PowerShell session.
function Get-DemoProcess {
    $exe = (Resolve-Path (Join-Path $script:Root 'artifacts/demo/app/WinWhatsApp.exe')).Path
    Get-Process WinWhatsApp -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
}

function Stop-Demo {
    if ($p = Get-DemoProcess) {
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit()
    }
}

function Get-DemoWindow { [System.Windows.Automation.AutomationElement]::FromHandle((Get-DemoProcess).MainWindowHandle) }

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

# Clicks a point of the window, in pixels from its top left corner, for what
# UI Automation cannot click, such as a photo in a chat.
function Invoke-Point([int]$X, [int]$Y) {
    $hwnd = (Get-DemoProcess).MainWindowHandle
    [DemoWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [DemoWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [DemoWin]::SetForegroundWindow($hwnd) | Out-Null
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

# Saves the window, without the shadow around it, as a PNG. Works while
# other windows cover it.
function Save-Window([string]$Path) {
    $hwnd = (Get-DemoProcess).MainWindowHandle
    # Windows lets a program bring another to the front only right after a key
    # press, so press Alt first. Without it the window buttons show dimmed.
    [DemoWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [DemoWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [DemoWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 500
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
    $full = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $script:Root $Path }
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    $bitmap.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $whole.Dispose()
    $full
}
