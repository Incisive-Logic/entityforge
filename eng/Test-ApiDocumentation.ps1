param([string]$ReportPath = '', [switch]$RequireComplete, [string]$ApiDirectory = '', [string]$ConfigurationPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = if ($ApiDirectory) { $ApiDirectory } else { Join-Path $root 'artifacts/docs/api' }
if (!$ConfigurationPath) { $ConfigurationPath = Join-Path $root 'docfx.json' }
$config = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
$expectedAssemblies = @($config.metadata | ForEach-Object { $_.src } | ForEach-Object { $_.files } |
    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) } | Sort-Object -Unique)
if (!$expectedAssemblies.Count) { throw 'No configured API assemblies found.' }
$observedAssemblies = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if (!(Test-Path $directory)) { throw 'Run eng/Build-Docs.ps1 before checking API documentation.' }
$items = [Collections.Generic.List[object]]::new()
foreach ($file in Get-ChildItem $directory -Filter '*.yml') {
    if ($file.Name -eq 'toc.yml') { continue }
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $text = [regex]::Split($text, '(?m)^references:')[0]
    # Managed-reference YAML emits each documented symbol as a top-level item.
    foreach ($match in [regex]::Matches($text, '(?ms)^- uid: (?<uid>[^\r\n]+)\r?\n(?<body>.*?)(?=^- uid: |^references:|\z)')) {
        $body = $match.Groups['body'].Value
        foreach ($assemblyBlock in [regex]::Matches($body, '(?m)^  assemblies:\r?\n(?<names>(?:  - [^\r\n]+\r?\n)+)')) {
            foreach ($assembly in [regex]::Matches($assemblyBlock.Groups['names'].Value, '(?m)^  - (?<name>[^\r\n]+)')) {
                [void]$observedAssemblies.Add($assembly.Groups['name'].Value.Trim())
            }
        }
        if ($body -match '(?m)^  type: Namespace\s*$') { continue }
        $summary = [regex]::Match($body, '(?m)^  summary: (.+)$')
        $documented = $summary.Success -and $summary.Groups[1].Value.Trim() -notin @("''", '""', 'null')
        $items.Add([pscustomobject]@{uid=$match.Groups['uid'].Value;hasSummary=$documented})
    }
}
if (!$items.Count) { throw 'No API symbols found; metadata generation or YAML format needs investigation.' }
$missing = @($items | Where-Object { !$_.hasSummary })
$missingAssemblies = @($expectedAssemblies | Where-Object { !$observedAssemblies.Contains($_) })
$result = @{expectedAssemblies=$expectedAssemblies;missingAssemblies=$missingAssemblies;symbols=$items.Count;withSummary=$items.Count-$missing.Count;missingSummaries=$missing.Count;missing=@($missing.uid)}
if ($ReportPath) { $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath }
Write-Host "$($result.withSummary)/$($result.symbols) rendered API symbols have summaries; $($missing.Count) need documentation."
if ($missingAssemblies.Count) { throw ('Configured API assemblies are absent from metadata: ' + ($missingAssemblies -join ', ')) }
if ($RequireComplete -and $missing.Count) { throw 'Public API documentation is incomplete.' }
