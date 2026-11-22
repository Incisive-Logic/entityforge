#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/benchmarks/BenchmarkReport.psm1" -Force
$path = Join-Path ([IO.Path]::GetTempPath()) ('entityforge-report-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $path
$passed = 0
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $script:passed++ }
function Reject([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Require $rejected $Message
}
function Fixture {
    return @{
        schema = 15; status = 'completed'; storedDatasetFullyVerified = $true; profile = 'graph'; workload = 'point-lookup'
        size = 4; iterations = 4; concurrency = 1; warmup = 2; mode = 'fixed-request-count'; measurementSeconds = 0
        warmupSeconds = 0; seed = 39075; datasetShape = 'ring'; keyDistribution = 'uniform'; backgroundRoutes = $false
        requestedRdfQuads = 0; loadCheckpointEveryBatches = 0; reopenAfterLoad = $false; restoreAfterLoad = $false; verifyInChildProcess = $false
        verifiedRequests = 4; verifiedWarmupRequests = 2; generatedDatasetSha256 = ('a' * 64); assemblySha256 = ('b' * 64)
        loadedEntityForgeAssemblies = @(@{ name = 'EntityForge.Db.Hosting'; sha256 = ('b' * 64) })
        latencyMilliseconds = @(1,2,3,4); p50Milliseconds = 2; p95Milliseconds = 4; p99Milliseconds = 4
        seconds = 1; requestsPerSecond = 4; cpuSeconds = 0.1; allocatedBytes = 100; databaseBytes = 200; WorkingSet64 = 300; PeakWorkingSet64 = 400
    }
}
$expected = @{ profile='graph'; workload='point-lookup'; rows=4; iterations=4; clients=1; warmup=2 }
$rawPath = Join-Path $path 'raw.json'
try {
    Require ((Get-BenchmarkSamplingAssessment @{verifiedRequests=999;seconds=30.0}).status -eq 'short-observation') 'Small tail sample was not flagged.'
    Require ((Get-BenchmarkSamplingAssessment @{verifiedRequests=1000;seconds=29.9}).status -eq 'short-observation') 'Short time window was not flagged.'
    Require ((Get-BenchmarkSamplingAssessment @{verifiedRequests=1000;seconds=30.0}).status -eq 'sampling-floor-met') 'Sampling boundary was misclassified.'
    Reject { Get-BenchmarkSamplingAssessment @{verifiedRequests=1000;seconds=[double]::NaN} } 'Accepted nonfinite measurement time.'
    Write-BenchmarkJson $rawPath (Fixture)
    $r = Read-VerifiedQueryMeasurement $rawPath $expected
    Require ($r.verifiedRequests -eq 4) 'Valid fixture was rejected.'
    foreach ($mutation in @(
        @{ status='incomplete' }, @{ storedDatasetFullyVerified=$false }, @{ schema=14 }, @{ verifiedRequests=3 },
        @{ latencyMilliseconds=@(4,3,2,1) }, @{ latencyMilliseconds=@(1,2,3) }, @{ latencyMilliseconds=@(-1,2,3,4) },
        @{ p95Milliseconds=3 }, @{ requestsPerSecond=5 }, @{ seconds=0 }, @{ generatedDatasetSha256='bad' },
        @{ profile='rdf' }, @{ concurrency=2 }, @{ keyDistribution='hotspot' }, @{ warmupSeconds=1 },
        @{ backgroundRoutes=$true }, @{ cpuSeconds=-1 }, @{ verifiedWarmupRequests=1 },
        @{ loadedEntityForgeAssemblies=@(@{name='EntityForge.Db.Hosting';sha256='bad'}) }
    )) {
        $bad = Fixture
        foreach ($key in $mutation.Keys) { $bad[$key]=$mutation[$key] }
        Write-BenchmarkJson $rawPath $bad
        Reject { Read-VerifiedQueryMeasurement $rawPath $expected } "Accepted invalid fixture: $($mutation.Keys)"
    }
    $entity = Fixture
    $entity.schema = 16; $entity.profile = 'semantic'; $entity.workload = 'entity-replace'; $entity.deployment = 'embedded-api'
    $entity.mappedDatasetVerified = $true; $entity.keyDistribution = 'worker-striped'
    $entity.verifiedMutatingOperations=4; $entity.verifiedReadOperations=0
    $entity.operationContract = 'Save and verified read'; $entity.datasetVerificationContract = 'Mapped values'; $entity.timingContract = 'Whole operation'
    $entityExpected = @{ suite='entity-operations'; profile='semantic'; workload='entity-replace'; deployment='embedded-api'; rows=4; iterations=4; clients=1; warmup=2 }
    Write-BenchmarkJson $rawPath $entity
    Require ((Read-VerifiedQueryMeasurement $rawPath $entityExpected).profile -eq 'semantic') 'Semantic entity result rejected.'
    $entity.deployment = 'same-process-http'; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Relabelled HTTP as embedded.'
    $entity.deployment = 'embedded-api'; $entity.mappedDatasetVerified=$false; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted unverified mapped dataset.'
    $entity.mappedDatasetVerified = $true
    $entity.deployment = 'separate-process-http'; $entityExpected.deployment = 'separate-process-http'
    Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted absent server evidence.'
    $entity.serverProcessId = 123
    $entity.serverBefore = @{CpuSeconds=1;AllocatedBytes=100;WorkingSetBytes=500;PeakWorkingSetBytes=1000}
    $entity.serverAfter = @{CpuSeconds=2;AllocatedBytes=200;WorkingSetBytes=500;PeakWorkingSetBytes=1000}
    Write-BenchmarkJson $rawPath $entity
    Require ((Read-VerifiedQueryMeasurement $rawPath $entityExpected).serverProcessId -eq 123) 'Rejected separate server evidence.'
    $entity.serverAfter.CpuSeconds = 0
    Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted backwards server CPU interval.'
    $entity.deployment = 'distributed-loopback'; $entityExpected.deployment = 'distributed-loopback'; $entityExpected.suite = 'distributed-entities'
    $entity.topology = @{ownerCount=2;participantProcesses=1;participantTransport='HTTPS'}
    Write-BenchmarkJson $rawPath $entity
    Require ((Read-VerifiedQueryMeasurement $rawPath $entityExpected).deployment -eq 'distributed-loopback') 'Rejected distributed fixture.'
    $entity.topology.participantProcesses=2; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted invented distributed process topology.'
    $entity.deployment='distributed-processes'; $entityExpected.deployment='distributed-processes'; $entityExpected.suite='distributed-processes'
    $entity.topology.participantProcesses=3
    $entity.clusterBefore=@(); $entity.clusterAfter=@()
    $pidNumber=100
    foreach ($role in @('a','b','gateway')) {
        $pidNumber++
        $entity.clusterBefore += @{Role=$role;ProcessId=$pidNumber;Counters=@{CpuSeconds=1;AllocatedBytes=100;WorkingSetBytes=500;PeakWorkingSetBytes=1000}}
        $entity.clusterAfter += @{Role=$role;ProcessId=$pidNumber;Counters=@{CpuSeconds=2;AllocatedBytes=200;WorkingSetBytes=500;PeakWorkingSetBytes=1000}}
    }
    Write-BenchmarkJson $rawPath $entity
    Require ((Read-VerifiedQueryMeasurement $rawPath $entityExpected).clusterAfter.Count -eq 3) 'Rejected process cluster.'
    $entity.clusterAfter[2].ProcessId=101; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted reused cluster PID.'
    $entity.deployment='same-process-http'; $entityExpected.deployment='same-process-http'; $entityExpected.suite='native-mutations'
    $entity.mappedDatasetVerified=$false; $entity.nativeDatasetVerified=$true
    Write-BenchmarkJson $rawPath $entity
    Require ((Read-VerifiedQueryMeasurement $rawPath $entityExpected).nativeDatasetVerified) 'Rejected native dataset.'
    $entity.nativeDatasetVerified=$false; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted unverified native dataset.'
    $entity.nativeDatasetVerified=$true; $entity.loadSeconds=2; $entity.loadedRows=4; $entity.loadRowsPerSecond=2
    Write-BenchmarkJson $rawPath $entity
    Require ((Read-VerifiedQueryMeasurement $rawPath $entityExpected).loadRowsPerSecond -eq 2) 'Rejected ingestion observation.'
    $entity.loadRowsPerSecond=3; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted fabricated ingestion throughput.'
    $entity.loadRowsPerSecond='NaN'; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted nonnumeric ingestion throughput.'
    $entity.loadRowsPerSecond=2; $entity.verifiedMutatingOperations=3; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted incorrect write count.'
    $entity.verifiedMutatingOperations=4; $entity.workload='entity-hot-key'; $entityExpected.workload='entity-hot-key'
    $entityExpected.suite='entity-operations'; $entity.mappedDatasetVerified=$true; $entity.keyDistribution='single-hot-key'
    Write-BenchmarkJson $rawPath $entity
    Require ((Read-VerifiedQueryMeasurement $rawPath $entityExpected).keyDistribution -eq 'single-hot-key') 'Rejected hot-key contract.'
    $entity.keyDistribution='worker-striped'; Write-BenchmarkJson $rawPath $entity
    Reject { Read-VerifiedQueryMeasurement $rawPath $entityExpected } 'Accepted hot-key workload with disjoint key contract.'
    $life = Fixture
    $lifeExpected = $expected.Clone(); $lifeExpected.suite='native-lifecycle'
    $life.loadCheckpointEveryBatches=1; $life.reopenAfterLoad=$true; $life.restoreAfterLoad=$true; $life.verifyInChildProcess=$true
    $life.restoredBeforeVerification=$true; $life.reopenedBeforeVerification=$true; $life.backupSha256=('c'*64)
    $life.verifiedDatasetEntities=4; $life.childVerification=@{Verification=@{ProcessId=456;Entities=4}}
    $life.backupSeconds=1; $life.restoreSeconds=1; $life.reopenSeconds=1; $life.loadCheckpointSeconds=1
    Write-BenchmarkJson $rawPath $life
    Require ((Read-VerifiedQueryMeasurement $rawPath $lifeExpected).restoredBeforeVerification) 'Rejected verified lifecycle.'
    $life.childVerification.Verification.Entities=3; Write-BenchmarkJson $rawPath $life
    Reject { Read-VerifiedQueryMeasurement $rawPath $lifeExpected } 'Accepted incomplete restored dataset.'
    $trxPath = Join-Path $path 'tests.trx'
    $trx = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testId="1" outcome="Passed" testName="Native_query_benchmark_records_verified_results_and_environment (False)"/><UnitTestResult testId="2" outcome="Passed" testName="Native_query_benchmark_records_verified_results_and_environment (True)"/></Results></TestRun>'
    [IO.File]::WriteAllText($trxPath,$trx)
    Test-BenchmarkTrx $trxPath
    Require $true 'Valid TRX.'
    foreach ($bad in @($trx.Replace('outcome="Passed"','outcome="NotExecuted"'), $trx.Replace('testId="2"','testId="1"'), $trx.Replace('Native_query_benchmark','Other_query_benchmark'))) {
        [IO.File]::WriteAllText($trxPath,$bad)
        Reject { Test-BenchmarkTrx $trxPath } 'Accepted skipped, duplicate or unrelated tests.'
    }
    $run = [ordered]@{
        schema=1; suiteVersion='native-queries-v1'; status='completed'; commit='123'; dirty=$false; startedUtc='2026-01-01'; sdk='10.0.404'
        runtime='.NET 10'; os='test'; architecture='X64'; logicalProcessors=4
        environment=@{ classification='controlled'; machineId='test'; cpu='<script>alert(1)</script>|x ![tracking](https://invalid.example/image)' }
        environmentDescriptionSha256='hardware'; configuration=@{rows=4}; results=@(@{
            profile='graph';deployment='same-process-http';workload='point-lookup';repetition=1;verifiedRequests=4;requestsPerSecond=4.0;p50Milliseconds=2.0;p95Milliseconds=4.0;p99Milliseconds=4.0;cpuSeconds=0.1;allocatedBytes=100;databaseBytes=200;datasetSha256='dataset'
        }); attempts=@(@{id='point-lookup-1';status='completed';detail='verified'}); comparison=$null
    }
    $baseline = ($run | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable)
    $baseline.results[0].requestsPerSecond = 2.0
    $comparison = Get-BenchmarkComparison $run $baseline
    Require ($comparison.status -eq 'comparable-observations' -and $comparison.changes[0].throughputMedianChangePercent -eq 100) 'Incorrect median comparison.'
    $baseline.environment.classification = 'shared-ci'
    Require ((Get-BenchmarkComparison $run $baseline).status -eq 'not-comparable') 'Compared shared runners.'
    $baseline.environment.classification = 'controlled'
    $baseline.status = 'failed'
    Require ((Get-BenchmarkComparison $run $baseline).status -eq 'not-comparable') 'Compared failed run.'
    $baseline.status = 'completed'; $baseline.environmentDescriptionSha256 = 'different'
    Require ((Get-BenchmarkComparison $run $baseline).status -eq 'not-comparable') 'Compared different machines.'
    $baseline.environmentDescriptionSha256 = 'hardware'; $baseline.results[0].datasetSha256 = 'different'
    Require ((Get-BenchmarkComparison $run $baseline).status -eq 'not-comparable') 'Compared different datasets.'
    $baseline.results[0].datasetSha256 = 'dataset'; $baseline.configuration.rows = 5
    Require ((Get-BenchmarkComparison $run $baseline).status -eq 'not-comparable') 'Compared different workload configurations.'
    $baseline.configuration.rows = 4; $baseline.results[0].requestsPerSecond = -1
    Require ((Get-BenchmarkComparison $run $baseline).status -eq 'not-comparable') 'Compared invalid throughput.'
    $baseline.results[0].requestsPerSecond = 4; $baseline.results[0].deployment = 'embedded-api'
    Require ((Get-BenchmarkComparison $run $baseline).status -eq 'not-comparable') 'Compared different deployments.'
    $run.results[0].sampling = Get-BenchmarkSamplingAssessment @{verifiedRequests=4;seconds=1.0}
    Write-BenchmarkReport $path $run
    $md = Get-Content (Join-Path $path 'report.md') -Raw
    $html = Get-Content (Join-Path $path 'report.html') -Raw
    Require ($md.Contains('semantic | Not measured') -and $md.Contains('Separate server')) 'Coverage omissions disappeared.'
    Require (-not $md.Contains('<script>') -and -not $html.Contains('<script>')) 'Untrusted metadata was not escaped.'
    Require (-not $md.Contains('![tracking]') -and -not $html.Contains('<img')) 'Metadata injected Markdown images.'
    Require ($md.Contains('Sampling limitations') -and $html.Contains('short-observation')) 'Sampling limitations were omitted.'
    Require ((Get-Content (Join-Path $path 'results.json') -Raw | ConvertFrom-Json).results.Count -eq 1) 'Result serialization lost data.'
    $run.status='failed'; $run.results=@()
    Write-BenchmarkReport $path $run
    Require ((Get-Content (Join-Path $path 'report.md') -Raw).Contains('Status: failed')) 'Failed report not rendered.'
    $invalidPoints = @(
        @{ Suite='graphql'; Rows=1001; Clients=1 },
        @{ Suite='semantic-queries'; Rows=2001; Clients=1 },
        @{ Suite='distributed-processes'; Rows=10001; Clients=1; EtcdEndpoint='http://127.0.0.1:2379' },
        @{ Suite='distributed-processes'; Rows=7; Clients=1 },
        @{ Suite='native-queries'; Rows=1; Clients=2 }
    )
    foreach ($point in $invalidPoints) {
        $rejectedPath = Join-Path $path ('invalid-' + [Guid]::NewGuid().ToString('N'))
        Reject { & "$PSScriptRoot/Invoke-BenchmarkMatrix.ps1" @point -OutputDirectory $rejectedPath -ArtifactsPath (Join-Path $path 'unused-build') -NoBuild } 'Accepted incompatible matrix point.'
        Require (-not (Test-Path -LiteralPath $rejectedPath)) 'Allocated evidence before rejecting an invalid matrix point.'
    }
    Write-Host "$passed benchmark report checks passed. Synthetic fixtures are validation only, not performance measurements."
} finally {
    $resolved = [IO.Path]::GetFullPath($path)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
