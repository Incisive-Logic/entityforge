#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [ValidateRange(1,1000000)][int[]]$Rows = @(1000,10000,100000),
    [ValidateRange(1,32)][int[]]$Clients = @(1,4,16),
    [string[]]$Suite = @('native-queries'),
    [string[]]$Workload = @('point-lookup','two-hop','grouped-aggregate'),
    [string[]]$EntityWorkload = @('entity-find','entity-balanced','entity-hot-key'),
    [string[]]$SemanticWorkload = @('semantic-gql-point','semantic-gql-after-write','semantic-sparql-point','semantic-sparql-after-write'),
    [string[]]$NativeWorkload = @('entity-insert','entity-replace','entity-delete-recreate','entity-balanced'),
    [string[]]$GraphqlWorkload = @('graphql-projection','graphql-paged'),
    [ValidateRange(1,100000)][int]$Iterations = 100,
    [ValidateRange(0,10000)][int]$Warmup = 20,
    [ValidateRange(1,20)][int]$Repetitions = 3,
    [string]$EnvironmentDescription,
    [string]$EtcdEndpoint,
    [switch]$NoBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/benchmarks/BenchmarkReport.psm1" -Force
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new matrix output directory.' }
if (-not $Rows.Count -or -not $Clients.Count -or @($Rows | Select-Object -Unique).Count -ne $Rows.Count -or @($Clients | Select-Object -Unique).Count -ne $Clients.Count) { throw 'Nonempty distinct scale/concurrency points required.' }
if (@($Suite | Where-Object { $_ -notin @('native-queries','native-mutations','entity-operations','semantic-queries','distributed-entities','distributed-processes','graphql','native-lifecycle') }).Count) { throw 'Unknown suite.' }
foreach ($size in $Rows) { foreach ($clientCount in $Clients) {
    if ($size -lt $clientCount) { throw 'Every matrix point requires Rows >= Clients.' }
    if (('graphql' -in $Suite -and $size -gt 1000) -or ('semantic-queries' -in $Suite -and $size -gt 2000) -or (@($Suite | Where-Object { $_ -like 'distributed-*' }).Count -gt 0 -and ($size -gt 10000 -or -not $EtcdEndpoint))) { throw 'Matrix exceeds a selected suite limit; select compatible suites explicitly. No silent omissions.' }
} }
$null = New-Item -ItemType Directory -Path $output
$matrix = @{ schema=1; status='incomplete'; points=@(); failure=$null }
function Save-Index {
    Write-BenchmarkJson (Join-Path $output 'matrix.json') $matrix
    $lines = @('# Scale and concurrency matrix','','Each point retains its own verified measurements, environment, source/binary identity and failures. Compare only like configurations. Large point counts are opt-in; elapsed time and storage requirements grow with dataset and write count.','',"Status: $($matrix.status)",'','| Rows | Clients | Status | Reports |','| ---: | ---: | --- | --- |')
    foreach ($point in $matrix.points) { $lines += "| $($point.rows) | $($point.clients) | $($point.status) | [Markdown]($($point.directory)/report.md), [HTML]($($point.directory)/report.html), [JSON]($($point.directory)/results.json) |" }
    [IO.File]::WriteAllText((Join-Path $output 'matrix.md'), ($lines -join "`n"))
    $links = foreach ($point in $matrix.points) { '<li>Rows ' + $point.rows + ', clients ' + $point.clients + ': ' + ([Net.WebUtility]::HtmlEncode($point.status)) + ' <a href="' + $point.directory + '/report.html">Report</a></li>' }
    [IO.File]::WriteAllText((Join-Path $output 'matrix.html'), '<!doctype html><meta charset="utf-8"><title>Benchmark matrix</title><h1>Scale and concurrency matrix</h1><p>' + ([Net.WebUtility]::HtmlEncode($matrix.status)) + '</p><ul>' + ($links -join '') + '</ul><a href="matrix.json">Raw matrix</a>')
}
Save-Index
try {
    $first = $true
    $sourceIdentity = $null
    $binaryIdentity = @{}
    foreach ($size in $Rows) { foreach ($clientCount in $Clients) {
        $point = @{ rows=$size; clients=$clientCount; status='incomplete'; directory="rows-$size-clients-$clientCount" }
        $matrix.points += $point
        Save-Index
        $options = @{ OutputDirectory=(Join-Path $output $point.directory); ArtifactsPath=$ArtifactsPath; Rows=$size; Clients=$clientCount; Suite=$Suite; Workload=$Workload; EntityWorkload=$EntityWorkload; Iterations=$Iterations; Warmup=$Warmup; Repetitions=$Repetitions; NoBuild=($NoBuild -or -not $first) }
        $options.SemanticWorkload = $SemanticWorkload
        $options.NativeWorkload = $NativeWorkload
        $options.GraphqlWorkload = $GraphqlWorkload
        if ($EnvironmentDescription) { $options.EnvironmentDescription = $EnvironmentDescription }
        if ($EtcdEndpoint) { $options.EtcdEndpoint = $EtcdEndpoint }
        & "$PSScriptRoot/Invoke-Benchmarks.ps1" @options
        $result = Get-Content (Join-Path $output "$($point.directory)/results.json") -Raw | ConvertFrom-Json
        $point.status = $result.status
        $identity = $result.sourceFiles | ConvertTo-Json -Depth 5 -Compress
        if ($null -eq $sourceIdentity) { $sourceIdentity = $identity }
        elseif ($sourceIdentity -ne $identity) { $point.status='failed'; throw 'Source changed between matrix points.' }
        foreach ($measurement in $result.results) { foreach ($assembly in $measurement.assemblies) {
            if ($binaryIdentity.ContainsKey($assembly.name) -and $binaryIdentity[$assembly.name] -ne $assembly.sha256) { $point.status='failed'; throw 'Binary changed between matrix points.' }
            $binaryIdentity[$assembly.name] = $assembly.sha256
        } }
        Save-Index
        if ($point.status -ne 'completed') { throw 'Matrix point failed; later points were not allocated.' }
        $first = $false
    } }
    $matrix.status = 'completed'
} catch {
    $matrix.status = 'failed'; $matrix.failure = $_.Exception.Message
    throw
} finally { Save-Index }
