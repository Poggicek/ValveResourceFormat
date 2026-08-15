<#
.SYNOPSIS
    Fetches the Mesa lavapipe CPU Vulkan driver into this directory.

.DESCRIPTION
    The golden suite's Vulkan run needs a Vulkan device that is not the machine's display adapter.
    This downloads one, verifies it, and drops two files next to this script. Nothing is installed:
    no registry key, no system directory, no service. Deleting the two files undoes it.

    See README.md in this directory for provenance and licence.

.PARAMETER Force
    Re-download even when the files are already present and match.
#>

[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Release = '26.2.0'
$Asset = "mesa3d-$Release-release-msvc.7z"
$Url = "https://github.com/pal1000/mesa-dist-win/releases/download/$Release/$Asset"

# From README.md. A changed hash means the upstream asset was replaced; do not paper over it.
$ArchiveSha256 = 'DCB2719EF346DAB5B609FCB193A5F13CFC4B0502E3F4DE1AD43D349477402F47'
$DriverSha256 = 'A53822223ACB84B084B77758102FBB7345ECDA19480A2220627DF5A2B6C0E463'

$Here = $PSScriptRoot
$Driver = Join-Path $Here 'vulkan_lvp.dll'
$Manifest = Join-Path $Here 'lvp_icd.x86_64.json'

if (-not $Force -and (Test-Path $Driver) -and (Test-Path $Manifest)) {
    if ((Get-FileHash $Driver -Algorithm SHA256).Hash -eq $DriverSha256) {
        Write-Host "lavapipe $Release is already here. Use -Force to re-download."
        exit 0
    }
}

$SevenZip = (Get-Command 7z -ErrorAction SilentlyContinue)?.Source
if (-not $SevenZip) {
    $Candidate = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
    if (Test-Path $Candidate) { $SevenZip = $Candidate }
}

if (-not $SevenZip) {
    throw "7-Zip is required to unpack $Asset and was not found on PATH or in '$env:ProgramFiles\7-Zip'."
}

$Staging = Join-Path ([System.IO.Path]::GetTempPath()) ("lavapipe-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Staging | Out-Null

try {
    $Archive = Join-Path $Staging $Asset

    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Archive

    $actual = (Get-FileHash $Archive -Algorithm SHA256).Hash
    if ($actual -ne $ArchiveSha256) {
        throw "$Asset hashed $actual, expected $ArchiveSha256. The upstream asset is not the one this was pinned to."
    }

    & $SevenZip e $Archive "-o$Staging" 'x64/vulkan_lvp.dll' 'x64/lvp_icd.x86_64.json' -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed to extract the x64 lavapipe files (exit $LASTEXITCODE)." }

    Copy-Item (Join-Path $Staging 'vulkan_lvp.dll') $Driver -Force
    Copy-Item (Join-Path $Staging 'lvp_icd.x86_64.json') $Manifest -Force

    $actual = (Get-FileHash $Driver -Algorithm SHA256).Hash
    if ($actual -ne $DriverSha256) {
        throw "vulkan_lvp.dll hashed $actual, expected $DriverSha256."
    }

    Write-Host "lavapipe $Release is in $Here. Nothing was installed; delete the two files to remove it."
}
finally {
    Remove-Item -Recurse -Force $Staging -ErrorAction SilentlyContinue
}
