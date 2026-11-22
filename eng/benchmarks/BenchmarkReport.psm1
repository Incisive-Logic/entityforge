Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-BenchmarkHash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-BenchmarkJson([string]$Path, $Value) {
    $pending = "$Path.pending"
    [IO.File]::WriteAllText($pending, ($Value | ConvertTo-Json -Depth 70), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($pending, $Path, $true)
}

function ConvertTo-BenchmarkText($Value) {
    $text = [Net.WebUtility]::HtmlEncode([string]$Value).Replace('|', '&#124;').Replace("`r", ' ').Replace("`n", ' ')
    foreach ($code in @(33,35,42,91,92,93,95,96)) { $text = $text.Replace([string][char]$code, "&#$code;") }
    return $text
}

function Get-BenchmarkNumber($Value, [string]$Name, [bool]$Positive = $false) {
    if ($Value -isnot [ValueType] -or $Value -is [bool]) { throw "Invalid numeric field: $Name" }
    $number = [double]$Value
    if (-not [double]::IsFinite($number) -or $number -lt 0 -or ($Positive -and $number -eq 0)) {
        throw "Invalid numeric field: $Name"
    }
    return $number
}

function Get-BenchmarkSamplingAssessment($Measurement) {
    $count = Get-BenchmarkNumber $Measurement.verifiedRequests 'sample count' $true
    $seconds = Get-BenchmarkNumber $Measurement.seconds 'measurement seconds' $true
    $notes = @()
    if ($count -lt 1000) { $notes += 'Fewer than 1,000 operations: p99 describes fewer than ten upper-tail observations.' }
    if ($seconds -lt 30) { $notes += 'Measured window below 30 seconds: startup, scheduling and GC variation may dominate.' }
    return @{ operations=$count; seconds=$seconds; status=$(if ($notes.Count) { 'short-observation' } else { 'sampling-floor-met' }); notes=$notes }
}

function Read-VerifiedQueryMeasurement([string]$Path, $Expected) {
    $r = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    $entity = $Expected.ContainsKey('suite') -and $Expected.suite -in @('native-mutations','entity-operations','semantic-queries','distributed-entities','distributed-processes','graphql')
    $lifecycle = $Expected.ContainsKey('suite') -and $Expected.suite -eq 'native-lifecycle'
    if ($r.status -ne 'completed' -or ($entity -and ($r.schema -ne 16 -or (($Expected.suite -eq 'native-mutations' -and $r.nativeDatasetVerified -ne $true) -or ($Expected.suite -ne 'native-mutations' -and $r.mappedDatasetVerified -ne $true)))) -or
        (-not $entity -and ($r.schema -ne 15 -or $r.storedDatasetFullyVerified -ne $true))) {
        throw 'Missing completed result with the required dataset verification.'
    }
    foreach ($pair in @(@('profile','profile'), @('workload','workload'), @('size','rows'),
        @('iterations','iterations'), @('concurrency','clients'), @('warmup','warmup'))) {
        if ($r[$pair[0]] -ne $Expected[$pair[1]]) { throw "Workload mismatch: $($pair[0])" }
    }
    if ($entity) {
        $writes = switch ($Expected.workload) {
            'entity-mixed' { [Math]::Ceiling($Expected.iterations / 5.0) }
            'entity-balanced' { [Math]::Ceiling($Expected.iterations / 2.0) }
            { $_ -in @('entity-insert','entity-replace','entity-delete-recreate','entity-hot-key','semantic-gql-after-write','semantic-sparql-after-write') } { $Expected.iterations }
            default { 0 }
        }
        if ($r.verifiedMutatingOperations -ne $writes -or $r.verifiedReadOperations -ne $Expected.iterations - $writes) { throw 'Incorrect verified read/write operation counts.' }
        if ($r.ContainsKey('loadSeconds')) {
            $null = Get-BenchmarkNumber $r.loadSeconds 'load seconds' $true
            if ($r.loadedRows -ne $Expected.rows) { throw 'Incorrect ingestion row count.' }
            if ($r.ContainsKey('loadRowsPerSecond')) { $null = Get-BenchmarkNumber $r.loadRowsPerSecond 'ingestion throughput' $true }
            if ($r.ContainsKey('loadRowsPerSecond') -and [Math]::Abs($r.loadRowsPerSecond - $r.loadedRows / $r.loadSeconds) -gt 0.000001 * [Math]::Max(1,$r.loadRowsPerSecond)) { throw 'Incorrect ingestion throughput.' }
        }
        if ($r.deployment -ne $Expected.deployment -or $r.mode -ne 'fixed-request-count' -or $r.keyDistribution -ne $(if ($Expected.workload -eq 'entity-hot-key') { 'single-hot-key' } else { 'worker-striped' }) -or
            -not $r.operationContract -or -not $r.datasetVerificationContract -or -not $r.timingContract) { throw 'Unexpected entity operation configuration.' }
        if ($r.deployment -eq 'separate-process-http') {
            if ($r.serverProcessId -le 0 -or $null -eq $r.serverBefore -or $null -eq $r.serverAfter) { throw 'Missing separate-server process evidence.' }
            foreach ($observation in @($r.serverBefore,$r.serverAfter)) {
                foreach ($field in @('CpuSeconds','AllocatedBytes','WorkingSetBytes','PeakWorkingSetBytes')) { $null = Get-BenchmarkNumber $observation[$field] "server $field" }
            }
            if ($r.serverAfter.CpuSeconds -lt $r.serverBefore.CpuSeconds -or $r.serverAfter.AllocatedBytes -lt $r.serverBefore.AllocatedBytes) { throw 'Invalid server resource interval.' }
        }
        if ($r.deployment -eq 'distributed-processes') {
            if ($r.topology.ownerCount -ne 2 -or $r.topology.participantProcesses -ne 3 -or $r.topology.participantTransport -ne 'HTTPS') { throw 'Invalid process cluster topology.' }
            if (@($r.clusterBefore).Count -ne 3 -or @($r.clusterAfter).Count -ne 3 -or @($r.clusterAfter.ProcessId | Select-Object -Unique).Count -ne 3) { throw 'Missing distinct cluster processes.' }
            foreach ($role in @('a','b','gateway')) {
                $before = @($r.clusterBefore | Where-Object Role -eq $role); $after = @($r.clusterAfter | Where-Object Role -eq $role)
                if ($before.Count -ne 1 -or $after.Count -ne 1 -or $before[0].ProcessId -ne $after[0].ProcessId -or $after[0].ProcessId -le 0) { throw 'Cluster process association changed.' }
                foreach ($observation in @($before[0].Counters,$after[0].Counters)) {
                    foreach ($field in @('CpuSeconds','AllocatedBytes','WorkingSetBytes','PeakWorkingSetBytes')) { $null = Get-BenchmarkNumber $observation[$field] "cluster $field" }
                }
                if ($after[0].Counters.CpuSeconds -lt $before[0].Counters.CpuSeconds -or $after[0].Counters.AllocatedBytes -lt $before[0].Counters.AllocatedBytes) { throw 'Invalid cluster resource interval.' }
            }
        }
        if ($r.deployment -eq 'distributed-loopback' -and ($r.topology.ownerCount -ne 2 -or $r.topology.participantProcesses -ne 1 -or $r.topology.participantTransport -ne 'HTTPS')) { throw 'Missing or inconsistent distributed fixture topology.' }
    } elseif ($r.mode -ne 'fixed-request-count' -or $r.measurementSeconds -ne 0 -or
        $r.warmupSeconds -ne 0 -or $r.seed -ne 39075 -or $r.datasetShape -ne 'ring' -or
        $r.backgroundRoutes -ne $false -or $r.requestedRdfQuads -ne 0 -or
        $r.loadCheckpointEveryBatches -ne [int]$lifecycle -or $r.reopenAfterLoad -ne $lifecycle -or
        $r.restoreAfterLoad -ne $lifecycle -or $r.verifyInChildProcess -ne $lifecycle -or
        $r.keyDistribution -notin @('uniform', 'not-applicable')) {
        throw 'Unexpected workload configuration; cannot compare this run with the suite contract.'
    }
    if ($lifecycle) {
        if ($r.restoredBeforeVerification -ne $true -or $r.reopenedBeforeVerification -ne $true -or $r.backupSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            $r.childVerification.Verification.ProcessId -le 0 -or $r.childVerification.Verification.Entities -ne $r.verifiedDatasetEntities) { throw 'Missing verified backup/restore/fresh-process evidence.' }
        foreach ($field in @('backupSeconds','restoreSeconds','reopenSeconds','loadCheckpointSeconds')) { $null = Get-BenchmarkNumber $r[$field] $field $true }
    }
    if ($r.verifiedRequests -ne $Expected.iterations -or $r.verifiedWarmupRequests -ne $Expected.warmup) {
        throw 'Not every requested operation was verified.'
    }
    if ($r.generatedDatasetSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $r.assemblySha256 -notmatch '^[a-fA-F0-9]{64}$' -or @($r.loadedEntityForgeAssemblies).Count -eq 0) {
        throw 'Missing dataset or binary identity.'
    }
    $names = @{}
    foreach ($assembly in $r.loadedEntityForgeAssemblies) {
        if ($assembly.name -notlike 'EntityForge.*' -or $assembly.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $names.ContainsKey($assembly.name)) {
            throw 'Invalid loaded assembly inventory.'
        }
        $names[$assembly.name] = $assembly.sha256
    }
    if ($names['EntityForge.Db.Hosting'] -ne $r.assemblySha256) { throw 'Hosting assembly identities disagree.' }
    $samples = @($r.latencyMilliseconds)
    if ($samples.Count -ne $Expected.iterations) { throw 'Missing raw latency samples.' }
    $previous = -1.0
    foreach ($sample in $samples) {
        $n = Get-BenchmarkNumber $sample 'latency'
        if ($n -lt $previous) { throw 'Latency samples are not sorted.' }
        $previous = $n
    }
    foreach ($percentile in @(@('p50Milliseconds', .50), @('p95Milliseconds', .95), @('p99Milliseconds', .99))) {
        $actual = Get-BenchmarkNumber $r[$percentile[0]] $percentile[0]
        $expectedValue = [double]$samples[[Math]::Max(0, [int][Math]::Ceiling($samples.Count * $percentile[1]) - 1)]
        if ([Math]::Abs($actual - $expectedValue) -gt 0.000001) { throw "Invalid $($percentile[0])" }
    }
    $seconds = Get-BenchmarkNumber $r.seconds 'seconds' $true
    $throughput = Get-BenchmarkNumber $r.requestsPerSecond 'requestsPerSecond' $true
    if ([Math]::Abs($throughput - $r.verifiedRequests / $seconds) -gt [Math]::Max(0.000001, $throughput * 0.000001)) {
        throw 'Throughput does not match the verified request count and elapsed time.'
    }
    foreach ($field in @('cpuSeconds','allocatedBytes','databaseBytes','WorkingSet64','PeakWorkingSet64')) {
        $null = Get-BenchmarkNumber $r[$field] $field
    }
    return $r
}

function Test-BenchmarkTrx([string]$Path, [int]$ExpectedCount = 2, [string]$Method = 'Native_query_benchmark_records_verified_results_and_environment') {
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $results = @($trx.SelectNodes('//t:UnitTestResult', $ns))
    if ($results.Count -ne $ExpectedCount) { throw "Expected exactly $ExpectedCount benchmark test cases." }
    foreach ($result in $results) {
        if ($result.outcome -ne 'Passed' -or $result.testName -notlike "$Method*") {
            throw 'Benchmark tests failed, were skipped, or did not match the selected fixture.'
        }
    }
    if (@($results | ForEach-Object testId | Select-Object -Unique).Count -ne $ExpectedCount) {
        throw 'Duplicate test result identities.'
    }
}

function Get-BenchmarkComparison($Current, $Baseline) {
    if ($null -eq $Baseline) { return @{ status = 'not-requested'; reason = 'No baseline supplied.'; changes = @() } }
    $reason = $null
    if ($Current.schema -ne $Baseline.schema -or $Current.suiteVersion -ne $Baseline.suiteVersion) { $reason = 'Different report or workload contract.' }
    elseif ($Current.status -ne 'completed' -or $Baseline.status -ne 'completed') { $reason = 'A run is failed or incomplete.' }
    elseif ($Current.environment.classification -ne 'controlled' -or $Baseline.environment.classification -ne 'controlled') { $reason = 'Shared or unspecified hardware is not a controlled comparison.' }
    elseif (@($Current.results).Count -ne @($Baseline.results).Count) { $reason = 'Result coverage differs.' }
    elseif ($Current.environmentDescriptionSha256 -ne $Baseline.environmentDescriptionSha256 -or
        $Current.runtime -ne $Baseline.runtime -or $Current.os -ne $Baseline.os -or
        $Current.architecture -ne $Baseline.architecture -or $Current.logicalProcessors -ne $Baseline.logicalProcessors -or
        ($Current.configuration | ConvertTo-Json -Compress) -ne ($Baseline.configuration | ConvertTo-Json -Compress)) { $reason = 'Hardware, runtime or workload configuration differs.' }
    if ($reason) { return @{ status = 'not-comparable'; reason = $reason; changes = @() } }
    foreach ($report in @($Current,$Baseline)) {
        $keys = @{}
        foreach ($result in $report.results) {
            $key = "$($result.profile)/$($result.deployment)/$($result.workload)/$($result.repetition)"
            if ($keys.ContainsKey($key)) { return @{ status = 'not-comparable'; reason = 'Duplicate repetition identity.'; changes = @() } }
            $keys[$key] = $true
            try { $null = Get-BenchmarkNumber $result.requestsPerSecond 'baseline throughput' $true }
            catch { return @{ status = 'not-comparable'; reason = 'Invalid throughput observation.'; changes = @() } }
        }
    }
    $changes = @()
    foreach ($group in @($Current.results | Group-Object profile,deployment,workload)) {
        $first = $group.Group[0]
        $prior = @($Baseline.results | Where-Object { $_.profile -eq $first.profile -and $_.deployment -eq $first.deployment -and $_.workload -eq $first.workload })
        if ($prior.Count -ne $group.Count -or @($prior | Where-Object { $_.datasetSha256 -ne $first.datasetSha256 }).Count) {
            return @{ status = 'not-comparable'; reason = 'Dataset or repetition coverage differs.'; changes = @() }
        }
        $now = @($group.Group.requestsPerSecond | Sort-Object)
        $then = @($prior.requestsPerSecond | Sort-Object)
        $medianNow = ($now[[int][Math]::Floor(($now.Count - 1) / 2)] + $now[[int][Math]::Floor($now.Count / 2)]) / 2
        $medianThen = ($then[[int][Math]::Floor(($then.Count - 1) / 2)] + $then[[int][Math]::Floor($then.Count / 2)]) / 2
        if ($medianThen -le 0) { throw 'Baseline has invalid throughput.' }
        $changes += @{ profile = $first.profile; deployment = $first.deployment; workload = $first.workload; throughputMedianChangePercent = 100 * ($medianNow / $medianThen - 1) }
    }
    return @{ status = 'comparable-observations'; reason = 'Median of independent repetitions; no significance test or automatic performance verdict.'; changes = $changes }
}

function Write-BenchmarkReport([string]$Directory, $Run, $Baseline = $null) {
    $Run.comparison = Get-BenchmarkComparison $Run $Baseline
    Write-BenchmarkJson (Join-Path $Directory 'results.json') $Run
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# EntityForge performance run')
    $lines.Add('')
    $lines.Add("**Status: $(ConvertTo-BenchmarkText $Run.status)** — correctness-checked observations, not a hardware-independent guarantee.")
    $lines.Add('')
    $lines.Add("Commit: $(ConvertTo-BenchmarkText $Run.commit); dirty tree: $($Run.dirty); suite: $($Run.suiteVersion).")
    $lines.Add("Started: $($Run.startedUtc). SDK: $(ConvertTo-BenchmarkText $Run.sdk).")
    $lines.Add("Environment: $(ConvertTo-BenchmarkText $Run.environment.classification); OS: $(ConvertTo-BenchmarkText $Run.os); architecture: $($Run.architecture); logical processors: $($Run.logicalProcessors).")
    $lines.Add("Hardware/storage/load description: $(ConvertTo-BenchmarkText ($Run.environment | ConvertTo-Json -Compress)).")
    $lines.Add('')
    $lines.Add("Configuration: $(ConvertTo-BenchmarkText ($Run.configuration | ConvertTo-Json -Compress)).")
    if ($Run.Contains('build')) { $lines.Add("Build: $(ConvertTo-BenchmarkText $Run.build.status). $(ConvertTo-BenchmarkText $Run.build.detail)") }
    if ($Run.Contains('failure') -and $Run.failure) { $lines.Add("Failure: $(ConvertTo-BenchmarkText $Run.failure)") }
    $lines.Add('')
    $lines.Add('## Measurement boundary')
    $lines.Add('')
    $lines.Add('Results distinguish embedded API, same-process HTTP, separate-process HTTP and distributed-loopback fixtures. Main-table CPU and allocations belong to the test/client process: they include the server only for same-process fixtures. Separate-server counters are shown separately. Distributed-loopback includes a gateway and two HTTPS participants in one process, with external etcd excluded from resource totals and a same-host test clock; it is not a cross-machine measurement. Embedded storage is opened through hosting with an idle listener. Throughput counts whole verified application operations, including response parsing and assertions, not individual requests or isolated commits. Loading and final checks are excluded. Closed-loop warmed workloads; no cold-cache or open-loop overload claim. Percentiles are exact nearest rank per repetition; do not average them.')
    $lines.Add('')
    $lines.Add('## Results by repetition')
    $lines.Add('')
    $lines.Add('| Profile | Deployment | Workload | Repeat | Verified operations | Operations/s | p50 ms | p95 ms | p99 ms | CPU s | Allocated bytes | Database bytes |')
    $lines.Add('| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
    foreach ($r in $Run.results) {
        $format = '| {0} | {1} | {2} | {3} | {4} | {5:F2} | {6:F3} | {7:F3} | {8:F3} | {9:F3} | {10} | {11} |'
        $lines.Add([string]::Format([Globalization.CultureInfo]::InvariantCulture, $format, [object[]]@($r.profile,$r.deployment,$r.workload,$r.repetition,$r.verifiedRequests,$r.requestsPerSecond,$r.p50Milliseconds,$r.p95Milliseconds,$r.p99Milliseconds,$r.cpuSeconds,$r.allocatedBytes,$r.databaseBytes)))
    }
    $lines.Add('')
    $sampling = @($Run.results | Where-Object { $_.ContainsKey('sampling') })
    if ($sampling.Count) {
        $lines.Add('## Sampling limitations')
        $lines.Add('')
        $lines.Add('A 30-second window and 1,000 operations are reporting floors, not statistical confidence, endurance or release approval. Closed-loop workloads do not measure overload latency. Short observations remain visible and are not failures of correctness.')
        $lines.Add('')
        $lines.Add('| Profile/deployment/workload/repeat | Seconds | Assessment | Notes |')
        $lines.Add('| --- | ---: | --- | --- |')
        foreach ($r in $sampling) {
            $label = ConvertTo-BenchmarkText "$($r.profile)/$($r.deployment)/$($r.workload)/$($r.repetition)"
            $lines.Add("| $label | $($r.sampling.seconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture)) | $($r.sampling.status) | $(ConvertTo-BenchmarkText ($r.sampling.notes -join ' ')) |")
        }
        $lines.Add('')
    }
    $loads = @($Run.results | Where-Object { $_.ContainsKey('loadSeconds') })
    if ($loads.Count) {
        $lines.Add('## Ingestion observations')
        $lines.Add('')
        $lines.Add('Initial loading only; dataset checks run afterwards and must pass. Separate from warmed operations. Includes the stated commit/HTTP path, excludes database startup. Rows are logical items, not physical records. No isolated bulk-API claim.')
        $lines.Add('| Profile/deployment/workload/repeat | Rows | Load seconds | Rows/s | Method |')
        $lines.Add('| --- | ---: | ---: | ---: | --- |')
        foreach ($r in $loads) { $lines.Add("| $($r.profile)/$($r.deployment)/$($r.workload)/$($r.repetition) | $($r.loadedRows) | $($r.loadSeconds) | $($r.loadedRows / $r.loadSeconds) | $(ConvertTo-BenchmarkText $r.loadContract) |") }
        $lines.Add('')
    }
    $distributed = @($Run.results | Where-Object { $_.deployment -in @('distributed-loopback','distributed-processes') })
    if ($distributed.Count) {
        $lines.Add('## Distributed write retries')
        $lines.Add('')
        $lines.Add('Only durably aborted writes retry, with a new operation identity, at most sixteen attempts and 1-128 ms exponential delay. All retry time is included in latency. Uncertain outcomes and transport failures fail the run. Participant traffic uses HTTPS to both owners; placement uses etcd. Transaction authority is local-file for distributed-loopback and external-etcd for distributed-processes.')
        $lines.Add('| Profile/workload/repeat | Measured aborted attempts |')
        $lines.Add('| --- | ---: |')
        foreach ($r in $distributed) { $lines.Add("| $($r.profile)/$($r.workload)/$($r.repetition) | $($r.measuredAbortedAttempts) |") }
        $lines.Add('')
    }
    $clusters = @($Run.results | Where-Object deployment -eq 'distributed-processes')
    if ($clusters.Count) {
        $lines.Add('## Separate owner and gateway processes')
        $lines.Add('')
        $lines.Add('Two owner processes and one gateway process on this host. Both placement and decision authority use external etcd, excluded from these counters. This is not a multi-machine result. Resource intervals include observation overhead; working-set peaks cover each process lifetime.')
        $lines.Add('| Profile/workload/repeat | Role | PID | CPU s | Allocated bytes | Peak working set bytes |')
        $lines.Add('| --- | --- | ---: | ---: | ---: | ---: |')
        foreach ($r in $clusters) { foreach ($after in $r.clusterAfter) {
            $before = @($r.clusterBefore | Where-Object Role -eq $after.Role)[0]
            $lines.Add("| $($r.profile)/$($r.workload)/$($r.repetition) | $($after.Role) | $($after.ProcessId) | $($after.Counters.CpuSeconds - $before.Counters.CpuSeconds) | $($after.Counters.AllocatedBytes - $before.Counters.AllocatedBytes) | $($after.Counters.PeakWorkingSetBytes) |")
        } }
        $lines.Add('')
    }
    $servers = @($Run.results | Where-Object { $_.deployment -eq 'separate-process-http' })
    if ($servers.Count) {
        $lines.Add('## Separate-server observations')
        $lines.Add('')
        $lines.Add('Server counters bracket the measured window and include observation overhead. Peak working set is a process-lifetime high-water mark, not a measurement-window peak.')
        $lines.Add('| Profile/workload/repeat | Server CPU s | Server allocated bytes | Server peak working set bytes |')
        $lines.Add('| --- | ---: | ---: | ---: |')
        foreach ($r in $servers) {
            $lines.Add("| $($r.profile)/$($r.workload)/$($r.repetition) | $($r.serverAfter.CpuSeconds - $r.serverBefore.CpuSeconds) | $($r.serverAfter.AllocatedBytes - $r.serverBefore.AllocatedBytes) | $($r.serverAfter.PeakWorkingSetBytes) |")
        }
        $lines.Add('')
    }
    $lifecycle = @($Run.results | Where-Object { $_.ContainsKey('lifecycle') })
    if ($lifecycle.Count) {
        $lines.Add('## Lifecycle phases')
        $lines.Add('')
        $lines.Add('Query latency is measured after restore and fresh-process dataset verification. These phase durations are separate observations, not crash-recovery or failover timings.')
        $lines.Add('| Profile/repeat | Checkpoint s | Backup s | Restore s | Reopen s |')
        $lines.Add('| --- | ---: | ---: | ---: | ---: |')
        foreach ($r in $lifecycle) {
            $lines.Add("| $($r.profile)/$($r.repetition) | $($r.lifecycle.checkpointSeconds) | $($r.lifecycle.backupSeconds) | $($r.lifecycle.restoreSeconds) | $($r.lifecycle.reopenSeconds) |")
        }
        $lines.Add('')
    }
    $lines.Add('## Repetition variation')
    $lines.Add('')
    $lines.Add('| Profile/deployment/workload | Repetitions | Minimum operations/s | Maximum operations/s |')
    $lines.Add('| --- | ---: | ---: | ---: |')
    foreach ($g in @($Run.results | Group-Object profile,deployment,workload)) {
        $m = $g.Group.requestsPerSecond | Measure-Object -Minimum -Maximum
        $lines.Add("| $(ConvertTo-BenchmarkText $g.Name) | $($g.Count) | $($m.Minimum) | $($m.Maximum) |")
    }
    $lines.Add('')
    $lines.Add('## Coverage and omissions')
    $lines.Add('')
    $lines.Add('| Profile | Embedded | Same-process HTTP | Separate server | Local distributed fixture | Multi-machine |')
    $lines.Add('| --- | --- | --- | --- | --- | --- |')
    foreach ($profile in @('graph','rdf','semantic')) {
        $embedded = if (@($Run.results | Where-Object { $_.profile -eq $profile -and $_.deployment -eq 'embedded-api' }).Count) { 'Mapped entity operations; see results' } else { 'Not measured in this run' }
        $http = if (@($Run.results | Where-Object { $_.profile -eq $profile -and $_.deployment -eq 'same-process-http' }).Count) { 'See individual results and attempts' } else { 'Not measured in this run' }
        $separate = if (@($Run.results | Where-Object { $_.profile -eq $profile -and $_.deployment -eq 'separate-process-http' }).Count) { 'See results' } else { 'Not measured' }
        $distributed = if (@($Run.results | Where-Object { $_.profile -eq $profile -and $_.deployment -in @('distributed-loopback','distributed-processes') }).Count) { 'Two HTTPS owners; see topology/process records' } else { 'Not measured' }
        $lines.Add("| $profile | $embedded | $http | $separate | $distributed | Not measured |")
    }
    $lines.Add('')
    $lines.Add('Selected suites cover mapped reads/writes/inserts/mixed operations, Graph/RDF native reads, asserted Semantic GQL/SPARQL reads, GraphQL projection/pagination and Graph/RDF checkpoint/backup/restore verification. Missing suites are not passes. Native GQL/SPARQL writes and mapped hot-key overwrites are selected explicitly. Ingestion timings describe the stated loading method, not an unmeasured bulk API. No inference, crash/failover, Semantic backup/restore or cross-machine performance claim.')
    $lines.Add('')
    $lines.Add('## Attempts and evidence')
    $lines.Add('')
    foreach ($attempt in $Run.attempts) {
        $lines.Add("- $(ConvertTo-BenchmarkText $attempt.id): **$(ConvertTo-BenchmarkText $attempt.status)**. $(ConvertTo-BenchmarkText $attempt.detail) [Raw evidence]($($attempt.id)/)")
    }
    $lines.Add('')
    $lines.Add('## Baseline comparison')
    $lines.Add('')
    $lines.Add("$(ConvertTo-BenchmarkText $Run.comparison.status): $(ConvertTo-BenchmarkText $Run.comparison.reason)")
    foreach ($change in $Run.comparison.changes) {
        $lines.Add("- $($change.profile)/$($change.deployment)/$($change.workload): throughput median change $([Math]::Round($change.throughputMedianChangePercent, 2))%.")
    }
    $lines.Add('')
    $lines.Add('[Machine-readable results](results.json) contain configuration, binary/dataset identities, resource observations references and evidence hashes. Preserve the whole directory. The supplied environment description is operator-declared, not independently attested. AI assisted development of the suite; measurements come from actual executions.')
    $markdown = ($lines -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $Directory 'report.md'), $markdown, [Text.UTF8Encoding]::new($false))
    # Render only the small Markdown subset generated above, never arbitrary HTML from metadata.
    $body = [Text.StringBuilder]::new()
    $inTable = $false
    foreach ($line in $lines) {
        if ($line.StartsWith('|')) {
            if ($line -match '^\|[\s:|\-]+$') { continue }
            $tag = if ($inTable) { 'td' } else { 'th' }
            if (-not $inTable) { $null = $body.Append('<div class="scroll"><table>'); $inTable = $true }
            $null = $body.Append('<tr>')
            foreach ($cell in $line.Trim('|').Split('|')) {
                $safe = [Net.WebUtility]::HtmlEncode([Net.WebUtility]::HtmlDecode($cell.Trim()))
                $null = $body.Append("<$tag>$safe</$tag>")
            }
            $null = $body.Append('</tr>')
            continue
        }
        if ($inTable) { $null = $body.Append('</table></div>'); $inTable = $false }
        $safe = [Net.WebUtility]::HtmlEncode([Net.WebUtility]::HtmlDecode($line))
        if ($line.StartsWith('## ')) { $null = $body.Append('<h2>' + $safe.Substring(3) + '</h2>') }
        elseif ($line.StartsWith('# ')) { $null = $body.Append('<h1>' + $safe.Substring(2) + '</h1>') }
        elseif ($line) { $null = $body.Append('<p>' + $safe + '</p>') }
    }
    if ($inTable) { $null = $body.Append('</table></div>') }
    $html = '<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>EntityForge performance run</title><style>body{background:#15191f;color:#e3e8ef;font:15px system-ui;margin:2rem;line-height:1.6}p{overflow-wrap:anywhere}a{color:#7bc9ff}h2{margin-top:2rem}.scroll{overflow:auto}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}th,td{padding:.6rem;text-align:left;border-bottom:1px solid #394351}th{color:#8ed7d0;background:#202832}tr:nth-child(even){background:#1b222b}</style><p><a href="report.md">Markdown</a> · <a href="results.json">Raw results</a></p>' + $body.ToString() + '</html>'
    [IO.File]::WriteAllText((Join-Path $Directory 'report.html'), $html, [Text.UTF8Encoding]::new($false))
}

Export-ModuleMember -Function Get-BenchmarkHash,Write-BenchmarkJson,Read-VerifiedQueryMeasurement,Test-BenchmarkTrx,Write-BenchmarkReport,Get-BenchmarkComparison,Get-BenchmarkSamplingAssessment
