param(
    [Parameter(Mandatory)][string]$BuildBin,
    [Parameter(Mandatory)][string[]]$AssemblyPaths,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$inputs = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$mainFiles = [Collections.Generic.List[string]]::new()
foreach ($relative in $AssemblyPaths) {
    $assembly = Join-Path $BuildBin $relative
    $xml = [IO.Path]::ChangeExtension($assembly, '.xml')
    if (!(Test-Path -LiteralPath $assembly) -or !(Test-Path -LiteralPath $xml)) {
        throw "Documentation requires assembly and XML comments: $assembly"
    }
    $mainFiles.Add([IO.Path]::GetFileName($assembly))
    foreach ($file in Get-ChildItem -LiteralPath (Split-Path $assembly -Parent) -File) {
        if ($file.Extension -notin @('.dll','.xml') -and !$file.Name.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if ($inputs.ContainsKey($file.Name)) {
            if ($inputs[$file.Name].Hash -ne $hash) {
                throw "Conflicting documentation dependency $($file.Name): $($inputs[$file.Name].Path) and $($file.FullName)"
            }
        } else {
            $inputs.Add($file.Name, @{Path=$file.FullName;Hash=$hash})
        }
    }
}
# Portable PDB Source Link mappings can become cross-assembly reference URLs
# even with DocFX Git features disabled. Metadata needs assemblies and XML, not PDBs.
# Retain dependency manifests so DocFX can resolve restored NuGet assemblies.
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
if (Get-ChildItem -LiteralPath $destinationRoot -Filter '*.pdb' -File) {
    throw "Documentation input directory must not contain PDB files: $destinationRoot"
}
foreach ($old in Get-ChildItem -LiteralPath $destinationRoot -File) {
    if (($old.Extension -in @('.dll','.xml') -or $old.Name.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase)) -and !$inputs.ContainsKey($old.Name)) {
        $resolved = [IO.Path]::GetFullPath($old.FullName)
        if (![String]::Equals([IO.Path]::GetDirectoryName($resolved), $destinationRoot.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Stale documentation input is outside the designated directory.'
        }
        Remove-Item -LiteralPath $resolved
    }
}
foreach ($entry in $inputs.GetEnumerator()) {
    $target = Join-Path $destinationRoot $entry.Key
    if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.Value.Hash) {
        Copy-Item -LiteralPath $entry.Value.Path -Destination $target -Force
    }
}
[pscustomobject]@{Directory=$destinationRoot;Files=@($mainFiles);DependencyFiles=$inputs.Count}
