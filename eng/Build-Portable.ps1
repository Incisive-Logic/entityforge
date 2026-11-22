#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Version = '',
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BuildArtifactsDirectory = '',
    [ValidateSet('win-x64', 'linux-x64')][string[]]$Runtime = @('win-x64', 'linux-x64')
)
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-Version.ps1" -Version $Version
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; existing builds are never overwritten.' }
New-Item -ItemType Directory -Path $output | Out-Null
$buildRoot = if ($BuildArtifactsDirectory) { [IO.Path]::GetFullPath($BuildArtifactsDirectory) } else { $output }
$artifacts = Join-Path $buildRoot 'build'
# A publish directory contains fingerprinted assets which incremental publishing does
# not remove. Keep compilation cached, but start each distribution with fresh assets.
$browser = Join-Path $output 'browser'
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location $root
try {
    Invoke-DotNet @('publish', 'src/Studio/EntityForge.Db.Studio.Browser/EntityForge.Db.Studio.Browser.csproj', '-c', 'Release', '--artifacts-path', $artifacts, '-o', $browser, "-p:Version=$Version", '-p:ILLinkTreatWarningsAsErrors=true')
    if (!(Test-Path "$browser/wwwroot/index.html")) { throw 'Studio publish output is missing.' }
    $archives = @()
    foreach ($rid in ($Runtime | Select-Object -Unique)) {
        $name = "entityforge-$Version-$rid"
        $stage = Join-Path $output $name
        # Separate restore/intermediate directories prevent RID-specific assets from being reused.
        $serverArtifacts = Join-Path $buildRoot "build-$rid"
        Invoke-DotNet @('publish', 'src/Db/EntityForge.Db.Server/EntityForge.Db.Server.csproj', '-c', 'Release', '-r', $rid, '--self-contained', 'true', '--artifacts-path', $serverArtifacts, '-o', $stage, "-p:Version=$Version", '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
        Copy-Item -LiteralPath "$browser/wwwroot" -Destination "$stage/studio" -Recurse
        Copy-Item -LiteralPath "$root/docs" -Destination "$stage/docs" -Recurse
        foreach ($file in @('License.md', 'LICENSING.md', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'SECURITY.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md')) {
            Copy-Item -LiteralPath "$root/$file" -Destination $stage
        }
        Copy-Item -LiteralPath "$root/deploy/portable/README.md" -Destination "$stage/README.md"
        New-Item -ItemType Directory -Force "$stage/licenses" | Out-Null
        Get-ChildItem -LiteralPath "$root/deploy/portable/licenses" | Copy-Item -Destination "$stage/licenses" -Recurse
        $inventory = @{}
        foreach ($assetsRoot in @($artifacts, $serverArtifacts)) {
            foreach ($assetsFile in Get-ChildItem -LiteralPath $assetsRoot -Filter project.assets.json -Recurse) {
                $assets = Get-Content -LiteralPath $assetsFile.FullName -Raw | ConvertFrom-Json -AsHashtable
                foreach ($key in $assets.libraries.Keys) {
                    $library = $assets.libraries[$key]
                    if ($library.type -ne 'package' -or $inventory.ContainsKey($key)) { continue }
                    $package = $null
                    foreach ($folder in $assets.packageFolders.Keys) {
                        $candidate = Join-Path $folder $library.path
                        if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
                    }
                    if (!$package) { throw "Restored package missing: $key" }
                    $legal = Join-Path "$stage/licenses" $library.path
                    New-Item -ItemType Directory -Force $legal | Out-Null
                    $nuspec = Get-ChildItem -LiteralPath $package -Filter *.nuspec | Select-Object -First 1
                    [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
                    $license = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
                    Copy-Item -LiteralPath $nuspec.FullName -Destination $legal
                    $notices = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { $_.Name -match '(?i)license|licence|notice|copyright|copying|ofl' })
                    foreach ($notice in $notices) {
                        $relative = [IO.Path]::GetRelativePath($package, $notice.FullName)
                        $destination = Join-Path $legal $relative
                        New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
                        Copy-Item -LiteralPath $notice.FullName -Destination $destination
                    }
                    if ($license.type -eq 'expression' -and !(Test-Path -LiteralPath "$stage/licenses/$($license.InnerText).txt")) {
                        throw "Add and review licence expression text before packaging: $key ($($license.InnerText))"
                    }
                    $inventory[$key] = [ordered]@{ package = $key; license = $license.InnerText; licenseType = $license.type; bundledNoticeFiles = $notices.Count }
                }
            }
        }
        # Runtime packs are SDK downloads, not ordinary entries in assets.libraries.
        $dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
        $runtimePacks = @{}
        $browserPacksJson = & dotnet msbuild src/Studio/EntityForge.Db.Studio.Browser/EntityForge.Db.Studio.Browser.csproj -t:ResolveFrameworkReferences "-p:ArtifactsPath=$artifacts" -getItem:ResolvedRuntimePack -verbosity:quiet
        if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve browser runtime notices.' }
        $browserPacks = ($browserPacksJson -join "`n") | ConvertFrom-Json
        foreach ($pack in $browserPacks.Items.ResolvedRuntimePack) {
            if (!(Test-Path -LiteralPath $pack.PackageDirectory)) { throw "Runtime pack missing: $($pack.Identity)" }
            $runtimePacks[$pack.PackageDirectory] = "$($pack.Identity)/$($pack.NuGetPackageVersion)"
        }
        foreach ($assetsRoot in @($artifacts, $serverArtifacts)) {
            foreach ($assetsFile in Get-ChildItem -LiteralPath $assetsRoot -Filter project.assets.json -Recurse) {
                $assets = Get-Content -LiteralPath $assetsFile.FullName -Raw | ConvertFrom-Json -AsHashtable
                foreach ($framework in $assets.project.frameworks.Values) {
                    foreach ($download in $framework.downloadDependencies) {
                        $packVersion = $download.version.Trim('[', ']').Split(',')[0].Trim()
                        $pack = Join-Path "$dotnetRoot/packs" "$($download.name)/$packVersion"
                        if (!(Test-Path -LiteralPath $pack)) {
                            foreach ($folder in $assets.packageFolders.Keys) {
                                $candidate = Join-Path $folder "$($download.name.ToLowerInvariant())/$packVersion"
                                if (Test-Path -LiteralPath $candidate) { $pack = $candidate; break }
                            }
                        }
                        if (Test-Path -LiteralPath $pack) { $runtimePacks[$pack] = "$($download.name)/$packVersion" }
                    }
                }
            }
        }
        foreach ($pack in $runtimePacks.Keys) {
            $destination = Join-Path "$stage/licenses/runtime-packs" $runtimePacks[$pack]
            New-Item -ItemType Directory -Force $destination | Out-Null
            Get-ChildItem -LiteralPath $pack -File | Where-Object Name -match '(?i)license|licence|notice' | Copy-Item -Destination $destination
        }
        # The inventory deliberately includes the complete restore closure, including build tools.
        $inventory.Values | Sort-Object { $_.package } | ConvertTo-Json -Depth 5 | Set-Content "$stage/licenses/packages.json" -Encoding utf8
        $runtimeConfig = Get-Content -LiteralPath "$stage/EntityForge.Db.Server.runtimeconfig.json" -Raw | ConvertFrom-Json
        $includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
        foreach ($frameworkName in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
            if (!($includedFrameworks | Where-Object { $_.name -eq $frameworkName -and $_.version })) {
                throw "Self-contained package is missing runtime identity: $frameworkName"
            }
        }
        [ordered]@{
            version = $Version
            runtime = $rid
            selfContained = $true
            studioIncluded = $true
            sdk = (& dotnet --version)
            includedFrameworks = @($includedFrameworks | Select-Object name, version)
            runtimePacks = @($runtimePacks.Values | Sort-Object -Unique)
            builtUtc = [DateTime]::UtcNow.ToString('O')
        } | ConvertTo-Json -Depth 5 | Set-Content "$stage/release.json" -Encoding utf8
        & "$PSScriptRoot/New-PortableSbom.ps1" -PackageDirectory $stage
        if ($rid -eq 'win-x64') {
            $archive = Join-Path $output "$name.zip"
            [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
        } else {
            $archive = Join-Path $output "$name.tar.gz"
            # Set explicit Unix modes even when the build host is Windows.
            $stream = [IO.File]::Create($archive)
            $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Optimal)
            $writer = [System.Formats.Tar.TarWriter]::new($gzip)
            try {
                foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
                    $relative = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\', '/')
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $relative)
                    $entry.Mode = if ($relative -eq "$name/EntityForge.Db.Server") { 493 } else { 420 }
                    $entry.DataStream = [IO.File]::OpenRead($file.FullName)
                    try { $writer.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
                }
            } finally { $writer.Dispose(); $gzip.Dispose(); $stream.Dispose() }
        }
        $archives += $archive
    }
    $archives | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_) } | Set-Content "$output/SHA256SUMS" -Encoding ascii
    Write-Host "Portable packages created in $output. No files were uploaded."
}
finally { Pop-Location }
