<#
.SYNOPSIS
    Checks that setup takes an install of the app's former name over.

.DESCRIPTION
    Up to version 0.5 the app was called WinWhatsApp. This installs that
    version from its release, gives it some data, a sign-in entry and a pinned
    shortcut, and has the old version running. Then it runs the new setup
    program the way the old version's updater does, and looks at what is left:
    the program and its entries under the new name, nothing under the old one,
    and the data where the new version looks for it.

    It installs, starts and uninstalls the app for the current user, and ends
    a running WinWhatsApp. That is for a machine nobody uses, which is where
    the build workflow runs it. On your own PC it would replace your install
    and move your data, so it refuses there unless told with -OnMyOwnPc.

.PARAMETER Setup
    The new setup program, as scripts\release.ps1 builds it.
#>
param(
    [Parameter(Mandatory)]
    [string] $Setup,
    [switch] $OnMyOwnPc
)

$ErrorActionPreference = 'Stop'

if (-not $env:CI -and -not $OnMyOwnPc) {
    throw 'This replaces your own install and moves its data. It is meant for the build machine; pass -OnMyOwnPc to run it here anyway.'
}

$formerRelease = 'https://github.com/rathlinus/WinWhatsApp/releases/download/v0.5.0/WinWhatsApp-0.5.0-Setup.exe'
$programs = Join-Path $env:LOCALAPPDATA 'Programs'
$former = Join-Path $programs 'WinWhatsApp'
$current = Join-Path $programs 'TwoTicks'
$formerData = Join-Path $env:LOCALAPPDATA 'WinWhatsApp'
$currentData = Join-Path $env:LOCALAPPDATA 'TwoTicks'
$startMenu = [Environment]::GetFolderPath('Programs')
$pinned = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9C4E7B2A-5D18-4F63-A0E9-3B7C1D6F8A42}_is1'
$silent = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'

$script:failed = 0
function Expect([string] $what, [bool] $holds) {
    if ($holds) {
        Write-Host "  ok    $what"
    }
    else {
        Write-Host "  FAIL  $what"
        $script:failed++
    }
}

function Target([string] $shortcut) {
    if (-not (Test-Path $shortcut)) {
        return ''
    }
    (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
}

function RunValue([string] $name) {
    (Get-ItemProperty $run -ErrorAction SilentlyContinue).$name
}

# Runs a setup program and waits for it alone. Start-Process -Wait would also
# wait for the app setup starts when it is done, which runs on.
function Install([string] $program, [string[]] $arguments) {
    $process = Start-Process $program -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(300000)) {
        $process.Kill()
        throw "$program did not finish in five minutes."
    }
}

function Stop-App {
    Get-Process TwoTicks, TwoTicks.Bridge, WinWhatsApp, WinWhatsApp.Bridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

Write-Host 'Installing version 0.5.0, under the former name...'
$old = Join-Path $env:TEMP 'WinWhatsApp-0.5.0-Setup.exe'
Invoke-WebRequest $formerRelease -OutFile $old -UseBasicParsing
Install $old ($silent + '/TASKS=autostart')
Expect 'the old version is installed' (Test-Path (Join-Path $former 'WinWhatsApp.exe'))
Expect 'it has its Start menu entry' (Test-Path (Join-Path $startMenu 'WinWhatsApp.lnk'))
Expect 'it starts at sign-in' ([bool](RunValue 'WinWhatsApp'))

# What a person who used it has: data, and a button pinned to the taskbar.
New-Item -ItemType Directory (Join-Path $formerData 'media') -Force | Out-Null
Set-Content (Join-Path $formerData 'media\photo.jpg') 'a picture'
Set-Content (Join-Path $formerData 'settings.json') '{ "Notifications": false }'
New-Item -ItemType Directory $pinned -Force | Out-Null
$pin = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $pinned 'WinWhatsApp.lnk'))
$pin.TargetPath = Join-Path $former 'WinWhatsApp.exe'
$pin.Save()

Write-Host 'Starting the old version, which the update has to end...'
$running = Start-Process (Join-Path $former 'WinWhatsApp.exe') -ArgumentList '--background' -PassThru
Start-Sleep -Seconds 8
Expect 'the old version runs' (-not $running.HasExited)

Write-Host 'Updating, the way the old version starts setup...'
$log = Join-Path $formerData 'update.log'
$arguments = $silent + '/NOCANCEL', '/NOCLOSEAPPLICATIONS', '/SP-', '/RELAUNCH=background', "/WAITPID=$($running.Id)", "/LOG=`"$log`""
$started = Get-Date
Install (Resolve-Path $Setup).Path $arguments
Write-Host ("  setup took {0:0} s" -f ((Get-Date) - $started).TotalSeconds)

Expect 'the old version was ended' $running.HasExited
Expect 'the new program is installed' (Test-Path (Join-Path $current 'TwoTicks.exe'))
Expect 'the old program folder is gone' (-not (Test-Path $former))
Expect 'the Start menu has the new entry' ((Target (Join-Path $startMenu 'TwoTicks.lnk')) -eq (Join-Path $current 'TwoTicks.exe'))
Expect 'the old Start menu entry is gone' (-not (Test-Path (Join-Path $startMenu 'WinWhatsApp.lnk')))
Expect 'it still starts at sign-in, under the new name' ((RunValue 'TwoTicks') -like "*$current\TwoTicks.exe*")
Expect 'the old sign-in entry is gone' (-not (RunValue 'WinWhatsApp'))
$entry = Get-ItemProperty $uninstall -ErrorAction SilentlyContinue
Expect 'Windows lists it as TwoTicks' ($entry.DisplayName -like 'TwoTicks*')
Expect 'in the new folder' ((Join-Path $entry.InstallLocation '') -eq (Join-Path $current ''))

Write-Host 'The new version, which setup started...'
Start-Sleep -Seconds 12
Expect 'the new version runs' ([bool](Get-Process TwoTicks -ErrorAction SilentlyContinue))
Expect 'the pinned button starts the new program' ((Target (Join-Path $pinned 'WinWhatsApp.lnk')) -eq (Join-Path $current 'TwoTicks.exe'))
Expect 'notifications are registered under the new name' (Test-Path 'HKCU:\Software\Classes\AppUserModelId\TwoTicks')
Expect 'and no longer under the old one' (-not (Test-Path 'HKCU:\Software\Classes\AppUserModelId\WinWhatsApp'))
$where = if (Test-Path $currentData) { 'the new folder' } else { 'the old folder still, as setup had its log open there' }
Write-Host "  its first start found the data in $where"

# The next start at the latest moves the data.
Stop-App
Start-Process (Join-Path $current 'TwoTicks.exe') -ArgumentList '--background'
Start-Sleep -Seconds 12
Expect 'the data is in the new folder' (Test-Path (Join-Path $currentData 'media\photo.jpg'))
Expect 'the old data folder is gone' (-not (Test-Path $formerData))
Expect 'the new version wrote its log there' (Test-Path (Join-Path $currentData 'app.log'))
Stop-App

Write-Host 'Uninstalling...'
Start-Process (Join-Path $current 'unins000.exe') -ArgumentList $silent -Wait
Start-Sleep -Seconds 3
Expect 'the program is gone' (-not (Test-Path (Join-Path $current 'TwoTicks.exe')))
Expect 'its Start menu entry is gone' (-not (Test-Path (Join-Path $startMenu 'TwoTicks.lnk')))
Expect 'its sign-in entry is gone' (-not (RunValue 'TwoTicks'))

Get-Content (Join-Path $currentData 'app.log') -ErrorAction SilentlyContinue | Select-Object -First 30 | ForEach-Object { Write-Host "  log: $_" }
if ($script:failed -gt 0) {
    throw "$($script:failed) expectations did not hold."
}
Write-Host 'Setup takes an install of the former name over.'
