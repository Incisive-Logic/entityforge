param([string]$ArtifactsPath = '', [switch]$SkipMetadata, [string]$ArtifactsPivots = 'release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# These fixed directories contain generated DocFX output only. Removing the previous
# output prevents deleted or moved APIs from surviving a subsequent metadata build.
function Clear-DocumentationOutput([ValidateSet('api','site')][string]$Name) {
    $outputRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/docs'))
    $output = [IO.Path]::GetFullPath((Join-Path $outputRoot $Name))
    if ([IO.Path]::GetDirectoryName($output) -ne $outputRoot) { throw 'Invalid documentation output directory.' }
    if (Test-Path -LiteralPath $output) {
        if ((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to remove linked documentation output.' }
        Remove-Item -LiteralPath $output -Recurse -Force
    }
}
Push-Location $root
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Documentation tool restore failed.' }
    # Compile first, then read assemblies with their XML comments. This avoids design-time
    # project loading depending on stale obj trees or unresolved transitive references.
    if (!$SkipMetadata) {
        if (!$ArtifactsPath) { $ArtifactsPath = Join-Path $root 'artifacts/docs-build' }
        $ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
        & dotnet build EntityForge.Headless.slnx -c Release --artifacts-path $ArtifactsPath "-p:ArtifactsPivots=$ArtifactsPivots" -p:GenerateDocumentationFile=true -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Documentation input build failed.' }
        $config = Get-Content docfx.json -Raw | ConvertFrom-Json
        $assemblies = @($config.metadata[0].src[0].files | ForEach-Object {
            $name = [IO.Path]::GetFileNameWithoutExtension($_)
            "$name/$ArtifactsPivots/$name.dll"
        })
        foreach ($assembly in $assemblies) {
            $assemblyPath = Join-Path (Join-Path $ArtifactsPath 'bin') $assembly
            $xmlPath = [IO.Path]::ChangeExtension($assemblyPath, '.xml')
            if (!(Test-Path -LiteralPath $assemblyPath) -or !(Test-Path -LiteralPath $xmlPath)) {
                throw "Documentation requires both the assembly and its XML comments: $assemblyPath"
            }
        }
        $inputs = & "$PSScriptRoot/Stage-DocumentationInputs.ps1" -BuildBin (Join-Path $ArtifactsPath 'bin') `
            -AssemblyPaths $assemblies -Destination (Join-Path $ArtifactsPath 'docs-inputs')
        & dotnet run --project "$root/src/Tools/EntityForge.ApiDocumentation" -c Release -- $root $inputs.Directory
        if ($LASTEXITCODE -ne 0) { throw 'Generated API documentation supplementation failed.' }
        $config.metadata[0].src = @(@{ files = $inputs.Files; src = $inputs.Directory.Replace('\','/') })
        New-Item -ItemType Directory -Force "$root/artifacts/docs" | Out-Null
        $config | ConvertTo-Json -Depth 20 | Set-Content "$root/docfx.local.json"
        Clear-DocumentationOutput api
        & dotnet tool run docfx metadata docfx.local.json --warningsAsErrors
        if ($LASTEXITCODE -ne 0) { throw 'API metadata generation failed.' }
    }
    if ($SkipMetadata) {
        Copy-Item docfx.json docfx.local.json -Force
    }
    Clear-DocumentationOutput site
    & dotnet tool run docfx build docfx.local.json --warningsAsErrors
    if ($LASTEXITCODE -ne 0) { throw 'Documentation build failed.' }
    # DocFX's deployment manifest otherwise embeds the build machine's absolute path.
    $manifestPath = Join-Path $root 'artifacts/docs/site/manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
    $manifest.source_base_path = '.'
    $manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $manifestPath
    Write-Host "Documentation: $root/artifacts/docs/site/index.html"
}
finally { Pop-Location }
