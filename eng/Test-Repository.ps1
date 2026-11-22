param([string]$ReportPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$files = @(& git -C $root ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate repository files.' }
$issues = [Collections.Generic.List[object]]::new()
foreach ($relative in $files) {
    $file = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { continue }
    if ($relative -match '\.md$') {
        $text = Get-Content -LiteralPath $file -Raw
        if ($text -match '(?i)(?:[A-Z]:[\\/](?:Git|Users|EntityForge\.Reports)[\\/]|github\.com/[^/\s]+/[^/\s]*-development)') {
            $issues.Add(@{file=$relative; problem='Private or machine-specific location in public documentation'})
        }
        foreach ($match in [regex]::Matches($text, '\[[^\]]*\]\(([^\s)#]+)(?:#[^)]*)?\)')) {
            $target = $match.Groups[1].Value
            if ($target -match '^(?:[a-z]+:|#|/)') { continue }
            # The portable README is copied to the archive root beside docs/.
            # Validate its links in that published layout, not its template folder.
            $linkRoot = if ($relative -eq 'deploy/portable/README.md') { $root } else { Split-Path $file -Parent }
            $resolved = Join-Path $linkRoot ([Uri]::UnescapeDataString($target))
            if (!(Test-Path -LiteralPath $resolved)) { $issues.Add(@{file=$relative;problem='Missing link target';target=$target}) }
        }
    }
    if ($relative -match '\.csproj$') {
        if ($relative -notmatch '^(src|tests)/') {
            $issues.Add(@{file=$relative;problem='Project outside src or tests'})
        }
        [xml]$project = Get-Content -LiteralPath $file -Raw
        foreach ($framework in @($project.SelectNodes('//TargetFramework | //TargetFrameworks') | ForEach-Object { $_.InnerText -split ';' })) {
            if ($framework -notmatch '^net10\.0(?:-[a-z0-9.]+)?$') {
                $issues.Add(@{file=$relative;problem='Project must target the .NET 10 baseline';target=$framework})
            }
        }
        foreach ($reference in $project.Project.ItemGroup.ProjectReference) {
            if (!$reference.Include) { continue }
            if ($reference.Include -match '\$\(') { $issues.Add(@{file=$relative;problem='Property-based project reference requires review';target=$reference.Include}); continue }
            $target = [IO.Path]::GetFullPath((Join-Path (Split-Path $file -Parent) $reference.Include))
            if (!$target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $target)) {
                $issues.Add(@{file=$relative;problem='External or missing project reference';target=$reference.Include})
            }
        }
    }
    if ($relative -match '\.slnx?$') {
        $solutionText = Get-Content -LiteralPath $file -Raw
        foreach ($match in [regex]::Matches($solutionText, '"([^"\r\n]+\.csproj)"')) {
            $target = [IO.Path]::GetFullPath((Join-Path (Split-Path $file -Parent) $match.Groups[1].Value))
            if (!$target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $target)) {
                $issues.Add(@{file=$relative;problem='External or missing solution project';target=$match.Groups[1].Value})
            }
        }
    }
}
$result = @{files=$files.Count; issues=@($issues.ToArray())}
if ($ReportPath) { $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath }
if ($issues.Count) { $issues | ConvertTo-Json -Depth 5 | Write-Host; throw "$($issues.Count) repository checks failed." }
Write-Host "Checked $($files.Count) candidate files: documentation links, solution paths and project boundaries passed."
