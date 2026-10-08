<#
.SYNOPSIS
    Builds TwoTicks: the app and the WhatsApp helper next to it.

.DESCRIPTION
    Runs the tests, builds the Go helper and publishes the app to artifacts\app.
    The folder runs as it is, without .NET or anything else installed.

    Needs the .NET 10 SDK and Go.

.PARAMETER Version
    The version to stamp into the programs, such as 1.2.0. Leave it out for a
    development build, which gets the version in Directory.Build.props.

.PARAMETER SkipTests
    Publish without running the tests first.

.PARAMETER Run
    Start the freshly built TwoTicks afterwards, replacing a running one.
#>
[CmdletBinding()]
param(
    [version]$Version,

    [switch]$SkipTests,

    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Output = Join-Path $RepoRoot 'artifacts\app'

# Runs a native command and fails the script when it fails.
function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$([System.IO.Path]::GetFileName($Command)) $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    throw 'Go was not found. Install it with "winget install GoLang.Go".'
}

if (-not $SkipTests) {
    Write-Host 'Running the tests...'
    Invoke-Native dotnet @('test', (Join-Path $RepoRoot 'tests\TwoTicks.Core.Tests'), '--nologo', '-v:q')
    Push-Location (Join-Path $RepoRoot 'src\TwoTicks.Bridge')
    try {
        Invoke-Native go @('vet', './...')
    }
    finally {
        Pop-Location
    }
}

# A running TwoTicks keeps its files locked.
$running = Get-Process TwoTicks, TwoTicks.Bridge -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping the running TwoTicks...'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

$versionArguments = @()
if ($Version) {
    $versionArguments = @("-p:Version=$Version")
}

# Publishing does not remove files an earlier build left there.
if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}

# The project builds the helper with Go before it compiles; see BuildBridge in
# TwoTicks.App.csproj. Forcing it here keeps a release from reusing a helper
# built with another version.
$bridge = Join-Path $RepoRoot 'artifacts\bridge\TwoTicks.Bridge.exe'
if (Test-Path $bridge) {
    Remove-Item $bridge -Force
}

Write-Host 'Publishing TwoTicks...'
Invoke-Native dotnet (@(
    'publish', (Join-Path $RepoRoot 'src\TwoTicks.App\TwoTicks.App.csproj'),
    '-c', 'Release', '-p:Platform=x64', '-o', $Output, '--nologo', '-v:q') + $versionArguments)

$exe = Join-Path $Output 'TwoTicks.exe'
Write-Host "Built: $exe"

if ($Run) {
    Start-Process $exe
}
