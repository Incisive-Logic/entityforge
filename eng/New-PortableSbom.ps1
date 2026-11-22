#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$stage = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $stage 'release.json') -Raw | ConvertFrom-Json
$inventory = @(Get-Content -LiteralPath (Join-Path $stage 'licenses/packages.json') -Raw | ConvertFrom-Json)
if ($manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
    $manifest.runtime -notin @('win-x64', 'linux-x64') -or
    $manifest.selfContained -isnot [bool] -or $manifest.studioIncluded -isnot [bool] -or
    !$manifest.selfContained -or !$manifest.studioIncluded) {
    throw 'A portable DB and Studio release manifest is required.'
}
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $stage 'sbom.spdx.json' }
if (Test-Path -LiteralPath $output) { throw 'The SBOM destination already exists.' }
$entries = @(Get-Item -LiteralPath $stage) + @(Get-ChildItem -LiteralPath $stage -Recurse -Force)
if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'Package inventory cannot traverse symbolic links or other reparse points.'
}
[string[]]$paths = @($entries | Where-Object { !$_.PSIsContainer } | ForEach-Object FullName)
[Array]::Sort($paths, [StringComparer]::Ordinal)
$files = [Collections.Generic.List[object]]::new()
$relationships = [Collections.Generic.List[object]]::new()
$sha1Values = [Collections.Generic.List[string]]::new()
$rootId = 'SPDXRef-Package-EntityForge'
foreach ($path in $paths) {
    $relative = './' + [IO.Path]::GetRelativePath($stage, $path).Replace('\', '/')
    $sha1 = (Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash.ToLowerInvariant()
    $sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $id = 'SPDXRef-File-' + $files.Count.ToString('D6', [Globalization.CultureInfo]::InvariantCulture)
    $files.Add([ordered]@{
        SPDXID = $id; fileName = $relative
        checksums = @(@{ algorithm = 'SHA1'; checksumValue = $sha1 }, @{ algorithm = 'SHA256'; checksumValue = $sha256 })
        licenseConcluded = 'NOASSERTION'; licenseInfoInFiles = @('NOASSERTION'); copyrightText = 'NOASSERTION'
    })
    $sha1Values.Add($sha1)
    $relationships.Add(@{ spdxElementId = $rootId; relationshipType = 'CONTAINS'; relatedSpdxElement = $id })
}
# SPDX 2.3 specifies SHA-1 for this aggregate verification code. Each file also
# carries a SHA-256 digest; the verification code is not a digital signature.
$sha1Values.Sort([StringComparer]::Ordinal)
$verification = [ordered]@{
    packageVerificationCodeValue = [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData(
        [Text.Encoding]::UTF8.GetBytes([string]::Join('', $sha1Values.ToArray())))).ToLowerInvariant()
}
$outputRelative = [IO.Path]::GetRelativePath($stage, $output).Replace('\', '/')
if (!$outputRelative.StartsWith('../', [StringComparison]::Ordinal) -and ![IO.Path]::IsPathRooted($outputRelative)) {
    $verification.packageVerificationCodeExcludedFiles = @('./' + $outputRelative)
}
$packages = [Collections.Generic.List[object]]::new()
$packages.Add([ordered]@{
    SPDXID = $rootId; name = 'EntityForge DB and Studio'; versionInfo = $manifest.version
    downloadLocation = 'NOASSERTION'; filesAnalyzed = $true; packageVerificationCode = $verification
    licenseConcluded = 'NOASSERTION'; licenseDeclared = 'NOASSERTION'; licenseInfoFromFiles = @('NOASSERTION')
    copyrightText = 'NOASSERTION'
    description = "Self-contained $($manifest.runtime) distribution. File hashes describe the staged payload. Component licences remain in the bundled notices; no bundle-wide licence is inferred."
})
$components = @{}
foreach ($item in $inventory) {
    if ($item.package -notmatch '^([^/]+)/([^/]+)$') { throw "Invalid restored package identity: $($item.package)" }
    $components[$item.package.ToLowerInvariant()] = @{
        name = $Matches[1]; version = $Matches[2]
        declared = if ($item.licenseType -eq 'expression' -and $item.license) { $item.license } else { 'NOASSERTION' }
        source = 'Listed in licenses/packages.json: complete recorded restore inventory, including build tools. Runtime inclusion and direct dependency edges are not inferred.'
    }
}
$runtimeNotices = Join-Path $stage 'licenses/runtime-packs'
if (Test-Path -LiteralPath $runtimeNotices) {
    foreach ($pack in Get-ChildItem -LiteralPath $runtimeNotices -Directory) {
        foreach ($version in Get-ChildItem -LiteralPath $pack.FullName -Directory) {
            $key = ($pack.Name + '/' + $version.Name).ToLowerInvariant()
            if (!$components.ContainsKey($key)) {
                $components[$key] = @{
                    name = $pack.Name; version = $version.Name; declared = 'NOASSERTION'
                    source = 'Listed in bundled runtime-pack notices. The inventory does not map individual runtime-pack files to distribution files.'
                }
            }
        }
    }
}
[string[]]$keys = @($components.Keys)
[Array]::Sort($keys, [StringComparer]::Ordinal)
foreach ($key in $keys) {
    $component = $components[$key]
    $id = 'SPDXRef-Package-' + $packages.Count.ToString('D6', [Globalization.CultureInfo]::InvariantCulture)
    $packages.Add([ordered]@{
        SPDXID = $id; name = $component.name; versionInfo = $component.version
        downloadLocation = 'NOASSERTION'; filesAnalyzed = $false; licenseConcluded = 'NOASSERTION'
        licenseDeclared = $component.declared; copyrightText = 'NOASSERTION'; sourceInfo = $component.source
        externalRefs = @(@{
            referenceCategory = 'PACKAGE-MANAGER'; referenceType = 'purl'
            referenceLocator = 'pkg:nuget/' + [Uri]::EscapeDataString($component.name) + '@' + [Uri]::EscapeDataString($component.version)
        })
    })
    $relationships.Add(@{
        spdxElementId = $id; relationshipType = 'OTHER'; relatedSpdxElement = $rootId
        comment = $component.source
    })
}
$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'; dataLicense = 'CC0-1.0'; SPDXID = 'SPDXRef-DOCUMENT'
    name = "entityforge-$($manifest.version)-$($manifest.runtime)"
    documentNamespace = 'https://spdx.org/spdxdocs/entityforge-' + [Guid]::NewGuid().ToString('D')
    creationInfo = @{
        created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
        creators = @('Tool: EntityForge-Portable-SBOM-1')
        comment = 'Payload checksums and recorded dependency metadata. No source-level licence conclusions, vulnerability verdict, complete runtime dependency graph, or container base-image inventory is asserted.'
    }
    documentDescribes = @($rootId); packages = $packages.ToArray(); files = $files.ToArray(); relationships = $relationships.ToArray()
}
$json = $document | ConvertTo-Json -Depth 12
New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream.Write($bytes)
    $stream.Flush($true)
} finally { $stream.Dispose() }
Write-Host "SPDX inventory: $output ($($files.Count) files, $($packages.Count) package records)."
