# Returns the repository version without building or requiring the .NET SDK.
param([string]$Version = '')
$ErrorActionPreference = 'Stop'
if (!$Version) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Version.props') -Raw
    $prefix = [string]$properties.Project.PropertyGroup.VersionPrefix
    $suffix = [string]$properties.Project.PropertyGroup.VersionSuffix
    $Version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
}
# Release versions must also be suitable for Docker tags and archive filenames.
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?$' -or $Version.Length -gt 128) {
    throw 'Use a SemVer version without build metadata, for example 0.8.0-preview.1.'
}
$Version
