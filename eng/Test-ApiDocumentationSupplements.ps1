param([Parameter(Mandatory)][string]$ArtifactsPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$parent = [IO.Path]::GetFullPath($ArtifactsPath)
$scratch = Join-Path $parent ('api-doc-test-' + [guid]::NewGuid().ToString('N'))
if (![IO.Path]::GetFullPath($scratch).StartsWith($parent.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test scratch directory must be inside the supplied artifacts directory.'
}
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><GenerateDocumentationFile>true</GenerateDocumentationFile><NoWarn>1591</NoWarn><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Fixture.cs" /></ItemGroup>
</Project>
'@ | Set-Content (Join-Path $scratch 'Fixture.csproj')
    @'
namespace Example;
/// <summary>Authored contract that must remain unchanged.</summary>
public record Entry(int Number);
public class Service { public void Undocumented() {} }
public record Custom(int Number)
{
    public override string ToString() => "custom";
}
'@ | Set-Content (Join-Path $scratch 'Fixture.cs')
    @{ metadata = @(@{ src = @(@{ files = @('Fixture.csproj') }) }) } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $scratch 'docfx.json')
    $staged = Join-Path $scratch 'staged'
    & dotnet build (Join-Path $scratch 'Fixture.csproj') -c Release -o $staged -v quiet
    if ($LASTEXITCODE) { throw 'Fixture compilation failed.' }
    $xmlPath = Join-Path $staged 'Fixture.xml'
    $before = [xml](Get-Content $xmlPath -Raw)
    & dotnet run --project "$root/src/Tools/EntityForge.ApiDocumentation" -c Release -- $scratch $staged
    if ($LASTEXITCODE) { throw 'Supplement generation failed.' }
    $after = [xml](Get-Content $xmlPath -Raw)
    foreach ($entry in $before.doc.members.member) {
        $retained = @($after.doc.members.member | Where-Object name -EQ $entry.name)
        if ($retained.Count -ne 1 -or $retained[0].OuterXml -cne $entry.OuterXml) { throw "Authored XML changed: $($entry.name)" }
    }
    foreach ($id in @('M:Example.Entry.ToString', 'M:Example.Entry.Equals(System.Object)', 'P:Example.Entry.EqualityContract', 'M:Example.Entry.#ctor(Example.Entry)')) {
        if (!($after.doc.members.member | Where-Object name -EQ $id)) { throw "Expected generated description: $id" }
    }
    foreach ($id in @('M:Example.Service.Undocumented', 'M:Example.Custom.ToString')) {
        if ($after.doc.members.member | Where-Object name -EQ $id) { throw "Must not fabricate an authored contract: $id" }
    }
    $hash = (Get-FileHash $xmlPath).Hash
    & dotnet run --project "$root/src/Tools/EntityForge.ApiDocumentation" -c Release -- $scratch $staged
    if ($LASTEXITCODE -or (Get-FileHash $xmlPath).Hash -ne $hash) { throw 'Supplement generation is not idempotent.' }
    Write-Host 'API supplement fixture checks passed: authored XML preserved, generated contracts covered, authored gaps retained, repeat run unchanged.'
}
finally {
    # Only the freshly allocated, validated test-owned directory is removed.
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
