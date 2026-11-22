#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [ValidatePattern('^[a-z0-9][a-z0-9._/-]*:[a-zA-Z0-9_][a-zA-Z0-9_.-]*$')][string]$ImageTag
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $package 'release.json') -Raw | ConvertFrom-Json
if ($manifest.runtime -ne 'linux-x64' -or $manifest.selfContained -ne $true -or $manifest.studioIncluded -ne $true) {
    throw 'Use a self-contained linux-x64 package with Studio produced by Build-Portable.ps1.'
}
if ($manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'The package version is invalid.' }
if (!$ImageTag) { $ImageTag = "entityforge:$($manifest.version)" }
foreach ($file in @('EntityForge.Db.Server', 'studio/index.html', 'License.md', 'THIRD-PARTY-NOTICES.md', 'licenses/packages.json', 'sbom.spdx.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $file) -PathType Leaf)) { throw "Required package file is missing: $file" }
}
# Release images refresh both the base and its OS security packages. Reusing a
# cached apt layer could otherwise preserve vulnerabilities after fixes ship.
& docker build --pull --no-cache --platform linux/amd64 --file "$root/deploy/portable/Dockerfile" --build-arg "VERSION=$($manifest.version)" --tag $ImageTag $package
if ($LASTEXITCODE -ne 0) { throw 'Local container build failed.' }
Write-Host "Built local image $ImageTag from package $($manifest.version). No image was pushed."
