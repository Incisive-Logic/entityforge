#Requires -Version 7.4
[CmdletBinding()]
param([string]$SchemaPath = '')
$ErrorActionPreference = 'Stop'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = Join-Path $temporaryRoot ('entityforge-sbom-' + [Guid]::NewGuid().ToString('N'))
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
try {
    New-Item -ItemType Directory -Force "$stage/licenses/runtime-packs/Microsoft.NETCore.App.Runtime.win-x64/10.0.13", "$stage/studio" | Out-Null
    @{ version = '1.2.3-test'; runtime = 'win-x64'; selfContained = $true; studioIncluded = $true } |
        ConvertTo-Json | Set-Content "$stage/release.json"
    @(@{ package = 'Example.Library/1.2.3'; license = 'MIT'; licenseType = 'expression' },
        @{ package = 'Example.Build/4.5.6'; license = 'LICENSE.txt'; licenseType = 'file' }) |
        ConvertTo-Json | Set-Content "$stage/licenses/packages.json"
    'Runtime notice fixture' | Set-Content "$stage/licenses/runtime-packs/Microsoft.NETCore.App.Runtime.win-x64/10.0.13/LICENSE.txt"
    '<html>Studio fixture</html>' | Set-Content "$stage/studio/index.html"
    [IO.File]::WriteAllBytes("$stage/payload.bin", [byte[]]@(0, 255, 42, 13, 10))
    & "$PSScriptRoot/New-PortableSbom.ps1" -PackageDirectory $stage
    $json = Get-Content "$stage/sbom.spdx.json" -Raw
    if ($SchemaPath) { Require (Test-Json -Json $json -SchemaFile $SchemaPath) 'SPDX schema validation failed.' }
    $document = $json | ConvertFrom-Json
    Require ($document.spdxVersion -eq 'SPDX-2.3' -and $document.dataLicense -eq 'CC0-1.0') 'Incorrect SPDX metadata identity.'
    Require ($document.files.Count -eq 5 -and $document.packages.Count -eq 4) 'Missing payload or component inventory.'
    Require (!$json.Contains($stage)) 'The SBOM leaked an absolute build path.'
    $identities = @($document.SPDXID) + @($document.files.SPDXID) + @($document.packages.SPDXID)
    Require (($identities | Select-Object -Unique).Count -eq $identities.Count) 'Duplicate SPDX element identity.'
    foreach ($relation in $document.relationships) {
        Require ($relation.spdxElementId -in $identities -and $relation.relatedSpdxElement -in $identities) 'Dangling SPDX relationship.'
    }
    foreach ($file in $document.files) {
        foreach ($checksum in $file.checksums) {
            $actual = (Get-FileHash -LiteralPath (Join-Path $stage $file.fileName) -Algorithm $checksum.algorithm).Hash.ToLowerInvariant()
            Require ($actual -ceq $checksum.checksumValue) "Incorrect checksum: $($file.fileName)"
        }
    }
    $root = $document.packages | Where-Object SPDXID -EQ 'SPDXRef-Package-EntityForge'
    Require ($root.filesAnalyzed -and $root.licenseDeclared -eq 'NOASSERTION') 'The distribution must not be assigned a blanket licence.'
    Require ($root.packageVerificationCode.packageVerificationCodeExcludedFiles -contains './sbom.spdx.json') 'Embedded SBOM must be excluded from its own verification code.'
    $library = $document.packages | Where-Object name -EQ 'Example.Library'
    $build = $document.packages | Where-Object name -EQ 'Example.Build'
    Require ($library.licenseDeclared -eq 'MIT' -and !$library.filesAnalyzed) 'Declared licence expression or metadata scope was lost.'
    Require ($build.licenseDeclared -eq 'NOASSERTION') 'A licence filename was misrepresented as an SPDX expression.'
    Require (@($document.relationships | Where-Object relationshipType -EQ 'DEPENDS_ON').Count -eq 0) 'Runtime dependency edges must not be guessed from restore metadata.'
    $original = (Get-FileHash "$stage/sbom.spdx.json").Hash
    $rejected = $false
    try { & "$PSScriptRoot/New-PortableSbom.ps1" -PackageDirectory $stage } catch { $rejected = $true }
    Require ($rejected -and (Get-FileHash "$stage/sbom.spdx.json").Hash -eq $original) 'Existing SBOM must not be overwritten.'
    @{ version = '1.2.3-test'; runtime = 'win-x64'; selfContained = 'true'; studioIncluded = $true } |
        ConvertTo-Json | Set-Content "$stage/release.json"
    $rejected = $false
    try { & "$PSScriptRoot/New-PortableSbom.ps1" -PackageDirectory $stage -OutputPath "$stage/invalid.spdx.json" } catch {
        $rejected = $_.Exception.Message -like '*release manifest*'
    }
    Require ($rejected -and !(Test-Path "$stage/invalid.spdx.json")) 'Invalid manifest types must be rejected before output.'
    Write-Host 'Portable SBOM checks passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($stage)
    if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^entityforge-sbom-[0-9a-f]{32}$') { throw 'Unexpected fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
