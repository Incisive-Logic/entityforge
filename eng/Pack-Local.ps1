param(
    [string]$Version = '',
    [Parameter(Mandatory = $true)][string]$ArtifactsPath
)
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-Version.ps1" -Version $Version
$root = Split-Path $PSScriptRoot -Parent
$artifacts = [IO.Path]::GetFullPath($ArtifactsPath)
$packages = Join-Path $artifacts 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null
# Select the complete DB runtime closure plus the neutral shared dependencies.
# UI/Studio applications, tests and prototypes are not part of the DB NuGet distribution.
$projects = @(Get-ChildItem "$root/src/Db" -Filter '*.csproj' -Recurse |
    Where-Object { $_.Name -notmatch '\.(Server|Prototypes\.)' })
foreach ($name in @('Query','Semantics','Values','Cqrs','Telemetry','Utility','Semantics.Protobuf')) {
    $projects += Get-Item "$root/src/Shared/EntityForge.$name/EntityForge.$name.csproj"
}
foreach ($project in $projects) {
    & dotnet pack $project.FullName -c Release --artifacts-path $artifacts -o $packages "-p:Version=$Version" "-p:PackageVersion=$Version" -p:GenerateDocumentationFile=true
    if ($LASTEXITCODE -ne 0) { throw "Local packing failed: $($project.Name)" }
}
Write-Host "Local packages: $packages. No package has been uploaded."
