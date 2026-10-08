<#
.SYNOPSIS
    Builds what a release consists of.

.DESCRIPTION
    Builds TwoTicks with the given version and collects the files to publish
    in artifacts\release:

        TwoTicks-<version>-Setup.exe  the setup program, which is what most people want
        TwoTicks-<version>-x64.zip    the app folder, which runs after unpacking
                                         without installing anything

    Compiling the setup program needs Inno Setup.

.PARAMETER Version
    The release version, such as 1.2.0.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [version]$Version
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts'

# Finds ISCC.exe, the Inno Setup compiler, wherever its installer put it.
function Find-InnoSetup {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup *\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup *\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup *\ISCC.exe')
    )

    foreach ($pattern in $candidates) {
        $tool = Get-ChildItem $pattern -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }

    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw 'Inno Setup was not found. Install it with "winget install JRSoftware.InnoSetup".'
}

# Looked up first, so a missing compiler is reported before the long build and not after it.
$innoSetup = Find-InnoSetup

& (Join-Path $PSScriptRoot 'build.ps1') -Version $Version -SkipTests

$release = Join-Path $Artifacts 'release'
if (Test-Path $release) { Remove-Item $release -Recurse -Force }

# A copy without the debug symbols, which nobody running the program needs.
$app = Join-Path $Artifacts 'app'
$shortVersion = "$($Version.Major).$($Version.Minor).$([Math]::Max($Version.Build, 0))"
$bundle = Join-Path $release 'TwoTicks'
New-Item -ItemType Directory -Force $release | Out-Null
Copy-Item $app $bundle -Recurse
Get-ChildItem $bundle -Recurse -Filter '*.pdb' | Remove-Item -Force

Write-Host 'Packing the zip...'
Compress-Archive -Path $bundle -DestinationPath (Join-Path $release "TwoTicks-$shortVersion-x64.zip")
Remove-Item $bundle -Recurse -Force

Write-Host 'Compiling the setup program...'
& $innoSetup '/Qp' "/DAppVersion=$shortVersion" "/DSourceDir=$app" "/DOutputDir=$release" (Join-Path $RepoRoot 'packaging\TwoTicks.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Write-Host 'Release files:'
Get-ChildItem $release | ForEach-Object { Write-Host "  $($_.FullName)" }
