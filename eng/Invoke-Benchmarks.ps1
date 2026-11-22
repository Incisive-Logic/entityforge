#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [ValidateSet('native-queries','native-mutations','entity-operations','semantic-queries','distributed-entities','distributed-processes','graphql','native-lifecycle')]
    [string[]]$Suite = @('native-queries','native-mutations','entity-operations','semantic-queries','graphql','native-lifecycle'),
    [ValidateSet('entity-insert','entity-replace','entity-delete-recreate','entity-balanced')]
    [string[]]$NativeWorkload = @('entity-insert','entity-replace','entity-delete-recreate','entity-balanced'),
    [ValidateSet('graphql-projection','graphql-paged')]
    [string[]]$GraphqlWorkload = @('graphql-projection','graphql-paged'),
    [ValidateSet('entity-find','entity-replace','entity-delete-recreate','entity-insert','entity-mixed','entity-balanced','entity-hot-key')]
    [string[]]$EntityWorkload = @('entity-find','entity-replace','entity-delete-recreate','entity-insert','entity-mixed','entity-balanced','entity-hot-key'),
    [ValidateSet('semantic-gql-point','semantic-gql-filter','semantic-gql-aggregate','semantic-sparql-point','semantic-sparql-filter','semantic-sparql-aggregate','semantic-gql-after-write','semantic-sparql-after-write')]
    [string[]]$SemanticWorkload = @('semantic-gql-point','semantic-gql-filter','semantic-gql-aggregate','semantic-sparql-point','semantic-sparql-filter','semantic-sparql-aggregate','semantic-gql-after-write','semantic-sparql-after-write'),
    [ValidateSet('point-lookup','two-hop','two-hop-join','two-hop-product','grouped-aggregate','incoming-scan')]
    [string[]]$Workload = @('point-lookup','two-hop','two-hop-join','two-hop-product','grouped-aggregate','incoming-scan'),
    [ValidateRange(1,1000000)][int]$Rows = 1000,
    [ValidateRange(1,100000)][int]$Iterations = 100,
    [ValidateRange(1,32)][int]$Clients = 1,
    [ValidateRange(0,10000)][int]$Warmup = 20,
    [ValidateRange(1,20)][int]$Repetitions = 3,
    [string]$EnvironmentDescription,
    [string]$Baseline,
    [string]$EtcdEndpoint,
    [switch]$NoBuild,
    [ValidateRange(30,7200)][int]$AttemptTimeoutSeconds = 1800
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/benchmarks/BenchmarkReport.psm1" -Force
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifacts = [IO.Path]::GetFullPath($ArtifactsPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; previous evidence is never overwritten.' }
if (@($Workload | Select-Object -Unique).Count -ne $Workload.Count) { throw 'Duplicate workloads are not allowed.' }
if ($Suite.Count -eq 0 -or ('native-queries' -in $Suite -and $Workload.Count -eq 0) -or ('entity-operations' -in $Suite -and $EntityWorkload.Count -eq 0) -or ('semantic-queries' -in $Suite -and $SemanticWorkload.Count -eq 0)) { throw 'Select at least one suite and one workload per selected suite.' }
if (@($Suite | Select-Object -Unique).Count -ne $Suite.Count -or @($EntityWorkload | Select-Object -Unique).Count -ne $EntityWorkload.Count) { throw 'Duplicate suites or entity workloads are not allowed.' }
if (('entity-operations' -in $Suite -or 'native-mutations' -in $Suite) -and $Rows -lt $Clients) { throw 'Entity workloads require Rows >= Clients.' }
if ('semantic-queries' -in $Suite -and ($Rows -lt $Clients -or $Rows -gt 2000)) { throw 'Semantic query workloads require Clients <= Rows <= 2000 within the bounded semantic projection.' }
if (@($SemanticWorkload | Select-Object -Unique).Count -ne $SemanticWorkload.Count) { throw 'Duplicate semantic workloads are not allowed.' }
if ('graphql' -in $Suite -and ($Rows -gt 1000 -or $Rows -lt $Clients -or $GraphqlWorkload.Count -eq 0)) { throw 'GraphQL measurements require Clients <= Rows <= 1000 and a selected workload.' }
if (@($GraphqlWorkload | Select-Object -Unique).Count -ne $GraphqlWorkload.Count) { throw 'Duplicate GraphQL workloads are not allowed.' }
if (('distributed-entities' -in $Suite -or 'distributed-processes' -in $Suite) -and (-not $EtcdEndpoint -or $Rows -gt 10000 -or $Rows -lt $Clients -or $EntityWorkload.Count -eq 0)) { throw 'Distributed measurements require an explicit isolated EtcdEndpoint and Clients <= Rows <= 10000.' }
if ('native-mutations' -in $Suite -and ($NativeWorkload.Count -eq 0 -or @($NativeWorkload | Select-Object -Unique).Count -ne $NativeWorkload.Count)) { throw 'Select distinct native mutation workloads.' }
$baselineReport = if ($Baseline) { Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json -AsHashtable } else { $null }
$description = if ($EnvironmentDescription) { Get-Content -LiteralPath $EnvironmentDescription -Raw | ConvertFrom-Json -AsHashtable } else {
    [ordered]@{ classification = 'unspecified'; cpu = 'not supplied'; memory = 'not supplied'; storage = 'not supplied'; backgroundLoad = 'not supplied'; topology = 'local fixtures; see each deployment and topology record' }
}
if ($description.classification -notin @('shared-ci','controlled','unspecified')) { throw 'Environment classification must be shared-ci, controlled or unspecified.' }
foreach ($field in @('cpu','memory','storage','backgroundLoad','topology')) {
    if (-not $description[$field]) { throw "Environment description requires $field." }
}
if ($description.classification -eq 'controlled' -and (-not $description['machineId'] -or -not $description['storageConfiguration'])) {
    throw 'Controlled comparisons require a stable non-secret machineId and storageConfiguration.'
}
$descriptionJson = $description | ConvertTo-Json -Depth 10 -Compress
$descriptionHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($descriptionJson))).ToLowerInvariant()

function Invoke-Tool([string]$Executable, [string[]]$Arguments, [string]$Log, [hashtable]$Overrides = @{}, [int]$Timeout = 1800) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    # Ambient benchmark settings must not silently alter the published experiment.
    foreach ($key in @($start.Environment.Keys)) {
        if ($key.StartsWith('ENTITYFORGE_QUERY_BENCHMARK', [StringComparison]::OrdinalIgnoreCase) -or $key.StartsWith('ENTITYFORGE_ENTITY_BENCHMARK', [StringComparison]::OrdinalIgnoreCase)) { $null = $start.Environment.Remove($key) }
    }
    foreach ($key in $Overrides.Keys) { $start.Environment[$key] = [string]$Overrides[$key] }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = [IO.File]::Create($Log)
    $stderr = [IO.File]::Create("$Log.stderr")
    try {
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($stderr)
        if (-not $process.WaitForExit($Timeout * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask))
            throw 'Process timed out. This attempt is incomplete; inspect owned fixture storage before rerunning.'
        }
        [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask))
        return $process.ExitCode
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $stdout.Dispose(); $stderr.Dispose(); $process.Dispose()
    }
}

function Assert-Capacity {
    foreach ($path in @($output,$artifacts,[IO.Path]::GetTempPath())) {
        $probe = $path
        while (-not (Test-Path -LiteralPath $probe)) { $probe = Split-Path -Parent $probe }
        $drive = [IO.DriveInfo]::GetDrives() | Where-Object {
            $probe.StartsWith($_.RootDirectory.FullName, [StringComparison]::OrdinalIgnoreCase)
        } | Sort-Object { $_.RootDirectory.FullName.Length } -Descending | Select-Object -First 1
        if ($null -eq $drive -or $drive.AvailableFreeSpace -lt 15GB) { throw 'Benchmark/build volume must retain at least 15 GiB free.' }
    }
}

$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
$dirty = -not [string]::IsNullOrWhiteSpace((& git -C $root status --porcelain | Out-String))
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'The SDK required by global.json is unavailable.' }
$run = [ordered]@{
    schema = 1; suiteVersion = 'public-workloads-v7'; status = 'incomplete'; startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    commit = $commit; dirty = $dirty; sdk = $sdk; runtime = 'not measured'; controllerRuntime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    logicalProcessors = [Environment]::ProcessorCount; environment = $description; environmentDescriptionSha256 = $descriptionHash
    configuration = [ordered]@{ rows = $Rows; iterations = $Iterations; clients = $Clients; warmup = $Warmup; repetitions = $Repetitions; suites = $Suite; workloads = $Workload; entityWorkloads = $EntityWorkload; semanticWorkloads = $SemanticWorkload; graphqlWorkloads = $GraphqlWorkload; nativeWorkloads = $NativeWorkload; nativeSeed = 39075 }
    build = @{ status = 'pending'; detail = '' }; attempts = @(); results = @(); comparison = $null; sourceFiles = @(); evidence = @(); failure = $null
    ci = @{ repository = $env:GITHUB_REPOSITORY; runId = $env:GITHUB_RUN_ID; attempt = $env:GITHUB_RUN_ATTEMPT; event = $env:GITHUB_EVENT_NAME }
}
Assert-Capacity
$null = New-Item -ItemType Directory -Path $output
$sourceFiles = @(& git -C $root ls-files --cached --others --exclude-standard | Sort-Object -Unique)
foreach ($file in $sourceFiles) {
    $full = Join-Path $root $file
    if (Test-Path -LiteralPath $full -PathType Leaf) { $run.sourceFiles += @{ path = $file; sha256 = Get-BenchmarkHash $full } }
}
Write-BenchmarkReport $output $run $baselineReport
$project = 'tests/Db/EntityForge.Db.Hosting.Tests/EntityForge.Db.Hosting.Tests.csproj'
$testArgs = @($project,'-c','Release','--artifacts-path',$artifacts)
$assemblyInventory = @{}
try {
    if (-not $NoBuild) {
        $code = Invoke-Tool 'dotnet' (@('build') + $testArgs) (Join-Path $output 'build.log')
        if ($code -ne 0) { $run.build.status = 'failed'; throw "Build failed with exit code $code; see build.log." }
        $run.build.status = 'completed'
        if ('distributed-entities' -in $Suite -or 'distributed-processes' -in $Suite) {
            $code = Invoke-Tool 'dotnet' @('build','tests/Db/EntityForge.Db.Partition.Tests/EntityForge.Db.Partition.Tests.csproj','-c','Release','--artifacts-path',$artifacts) (Join-Path $output 'partition-build.log')
            if ($code -ne 0) { $run.build.status = 'failed'; throw 'Partition benchmark build failed.' }
        }
    } else { $run.build.status = 'not-run'; $run.build.detail = 'Caller supplied existing binaries. Loaded binary hashes are authoritative; source-to-binary correspondence is not established by this runner.' }
    $cases = @()
    if ('native-queries' -in $Suite) { foreach ($work in $Workload) { $cases += @{ suite='native-queries'; workload=$work } } }
    if ('entity-operations' -in $Suite) { foreach ($work in $EntityWorkload) { $cases += @{ suite='entity-operations'; workload=$work } } }
    if ('semantic-queries' -in $Suite) { foreach ($work in $SemanticWorkload) { $cases += @{ suite='semantic-queries'; workload=$work } } }
    if ('distributed-entities' -in $Suite) { foreach ($work in $EntityWorkload) { $cases += @{ suite='distributed-entities'; workload=$work } } }
    if ('distributed-processes' -in $Suite) { foreach ($work in $EntityWorkload) { $cases += @{ suite='distributed-processes'; workload=$work } } }
    if ('graphql' -in $Suite) { foreach ($work in $GraphqlWorkload) { $cases += @{ suite='graphql'; workload=$work } } }
    if ('native-lifecycle' -in $Suite) { $cases += @{ suite='native-lifecycle'; workload='two-hop' } }
    if ('native-mutations' -in $Suite) { foreach ($work in $NativeWorkload) { $cases += @{ suite='native-mutations'; workload=$work } } }
    foreach ($case in $cases) {
        $work = $case.workload
        $entity = $case.suite -notin @('native-queries','native-lifecycle')
        $nativeMutation = $case.suite -eq 'native-mutations'
        $semantic = $case.suite -eq 'semantic-queries'
        $distributed = $case.suite -in @('distributed-entities','distributed-processes')
        $processCluster = $case.suite -eq 'distributed-processes'
        $graphql = $case.suite -eq 'graphql'
        $lifecycle = $case.suite -eq 'native-lifecycle'
        for ($rep = 1; $rep -le $Repetitions; $rep++) {
            $id = if ($nativeMutation) { "native-$work-$rep" } elseif ($processCluster) { "cluster-$work-$rep" } elseif ($distributed) { "distributed-$work-$rep" } elseif ($lifecycle) { "lifecycle-$work-$rep" } else { "$work-$rep" }
            $dir = Join-Path $output $id
            $null = New-Item -ItemType Directory -Path $dir
            $attempt = @{ id = $id; status = 'incomplete'; detail = 'Started; no completed evidence yet.' }
            $run.attempts += $attempt
            Write-BenchmarkReport $output $run $baselineReport
            try {
                Assert-Capacity
                $environment = @{
                    ENTITYFORGE_QUERY_BENCHMARK = '1'; ENTITYFORGE_QUERY_BENCHMARK_REPORT_DIRECTORY = $dir
                    ENTITYFORGE_QUERY_BENCHMARK_WORKLOAD = $work; ENTITYFORGE_QUERY_BENCHMARK_ROWS = $Rows
                    ENTITYFORGE_QUERY_BENCHMARK_ITERATIONS = $Iterations; ENTITYFORGE_QUERY_BENCHMARK_CLIENTS = $Clients
                    ENTITYFORGE_QUERY_BENCHMARK_WARMUP = $Warmup
                }
                if ($entity) {
                    $environment = @{
                        ENTITYFORGE_ENTITY_BENCHMARK = '1'; ENTITYFORGE_ENTITY_BENCHMARK_REPORT_DIRECTORY = $dir
                        ENTITYFORGE_ENTITY_BENCHMARK_WORKLOAD = $work; ENTITYFORGE_ENTITY_BENCHMARK_ROWS = $Rows
                        ENTITYFORGE_ENTITY_BENCHMARK_ITERATIONS = $Iterations; ENTITYFORGE_ENTITY_BENCHMARK_CLIENTS = $Clients
                        ENTITYFORGE_ENTITY_BENCHMARK_WARMUP = $Warmup
                    }
                }
                if ($lifecycle) {
                    $environment.ENTITYFORGE_QUERY_BENCHMARK_LOAD_CHECKPOINT_EVERY_BATCHES = '1'
                    $environment.ENTITYFORGE_QUERY_BENCHMARK_REOPEN_AFTER_LOAD = '1'
                    $environment.ENTITYFORGE_QUERY_BENCHMARK_RESTORE_AFTER_LOAD = '1'
                    $environment.ENTITYFORGE_QUERY_BENCHMARK_VERIFY_IN_CHILD_PROCESS = '1'
                }
                 $method = if ($nativeMutation) { 'Native_mutation_benchmark_records_verified_operations' } elseif ($processCluster) { 'Distributed_process_benchmark_records_verified_operations' } elseif ($distributed) { 'Distributed_benchmark_records_verified_operations' } elseif ($graphql) { 'Graphql_benchmark_records_verified_operations' } elseif ($semantic) { 'Semantic_native_benchmark_records_verified_operations' } elseif ($entity) { 'Entity_benchmark_records_verified_operations' } else { 'Native_query_benchmark_records_verified_results_and_environment' }
                $expectedTests = if ($nativeMutation) { 4 } elseif ($distributed) { 3 } elseif ($graphql) { 6 } elseif ($entity -and -not $semantic) { 9 } else { 2 }
                $selectedTestArgs = $testArgs
                if ($distributed) {
                    $environment.ENTITYFORGE_TEST_ETCD_ENDPOINT = $EtcdEndpoint
                    $selectedTestArgs = @('tests/Db/EntityForge.Db.Partition.Tests/EntityForge.Db.Partition.Tests.csproj','-c','Release','--artifacts-path',$artifacts)
                }
                $argsList = @('test') + $selectedTestArgs + @('--no-build','--filter',"FullyQualifiedName~$method",'--logger','trx;LogFileName=tests.trx','--results-directory',$dir)
                $code = Invoke-Tool 'dotnet' $argsList (Join-Path $dir 'test.log') $environment $AttemptTimeoutSeconds
                if ($code -ne 0) { throw "Test process exited $code; see test.log and tests.trx." }
                Test-BenchmarkTrx (Join-Path $dir 'tests.trx') $expectedTests $method
                $verified = @()
                 $profiles = if ($nativeMutation) { @('graph','rdf') } elseif ($semantic) { @('semantic') } elseif ($entity) { @('graph','rdf','semantic') } else { @('graph','rdf') }
                $deployments = if ($processCluster) { @('distributed-processes') } elseif ($distributed) { @('distributed-loopback') } elseif ($nativeMutation -or $semantic -or $graphql) { @('same-process-http','separate-process-http') } elseif ($entity) { @('embedded-api','same-process-http','separate-process-http') } else { @('same-process-http') }
                foreach ($profile in $profiles) { foreach ($deployment in $deployments) {
                    $expected = @{ profile = $profile; deployment = $deployment; workload = $work; rows = $Rows; iterations = $Iterations; clients = $Clients; warmup = $Warmup; suite = $case.suite }
                    $filename = if ($entity) { "$profile-$deployment-$work.json" } else { "$profile-$work.json" }
                    $raw = Read-VerifiedQueryMeasurement (Join-Path $dir $filename) $expected
                    if ($run.runtime -ne 'not measured' -and $run.runtime -ne $raw.runtime) { throw 'Measured runtimes differ across attempts.' }
                    $run.runtime = $raw.runtime
                    foreach ($assembly in $raw.loadedEntityForgeAssemblies) {
                        if ($assemblyInventory.ContainsKey($assembly.name) -and $assemblyInventory[$assembly.name] -ne $assembly.sha256) {
                            throw "Binary changed between measurements: $($assembly.name)"
                        }
                        $assemblyInventory[$assembly.name] = $assembly.sha256
                    }
                    $label = if ($nativeMutation) { "native-$work" } elseif ($lifecycle) { "restore-verified-$work" } else { $work }
                    $verified += @{ profile = $profile; deployment = $deployment; workload = $label; repetition = $rep; verifiedRequests = $raw.verifiedRequests
                        requestsPerSecond = $raw.requestsPerSecond; p50Milliseconds = $raw.p50Milliseconds; p95Milliseconds = $raw.p95Milliseconds; p99Milliseconds = $raw.p99Milliseconds
                        cpuSeconds = $raw.cpuSeconds; allocatedBytes = $raw.allocatedBytes; databaseBytes = $raw.databaseBytes
                        workingSetBytes = $raw.WorkingSet64; peakWorkingSetBytes = $raw.PeakWorkingSet64
                        datasetSha256 = $raw.generatedDatasetSha256; assemblies = $raw.loadedEntityForgeAssemblies
                        runtime = $raw.runtime; raw = "$id/$filename"; sampling = (Get-BenchmarkSamplingAssessment $raw) }
                    if ($entity) { $verified[-1].topology = if ($raw.ContainsKey('topology')) { $raw.topology } else { $null }; $verified[-1].operationContract = $raw.operationContract; $verified[-1].timingContract = $raw.timingContract; $verified[-1].serverBefore = $raw.serverBefore; $verified[-1].serverAfter = $raw.serverAfter; $verified[-1].serverProcessId = $raw.serverProcessId }
                    if ($entity -and $raw.ContainsKey('loadSeconds')) { $verified[-1].loadSeconds = $raw.loadSeconds; $verified[-1].loadedRows = $raw.loadedRows; $verified[-1].loadContract = if ($raw.ContainsKey('loadContract')) { $raw.loadContract } else { 'Sequential mapped Save operations' } }
                    if ($processCluster) { $verified[-1].clusterBefore = $raw.clusterBefore; $verified[-1].clusterAfter = $raw.clusterAfter }
                    if ($distributed) { $verified[-1].measuredAbortedAttempts = $raw.measuredAbortedAttempts; $verified[-1].retryPolicy = $raw.retryPolicy }
                    if ($lifecycle) { $verified[-1].lifecycle = @{ backupSeconds=$raw.backupSeconds; restoreSeconds=$raw.restoreSeconds; reopenSeconds=$raw.reopenSeconds; checkpointSeconds=$raw.loadCheckpointSeconds; backupSha256=$raw.backupSha256; childVerification=$raw.childVerification } }
                } }
                $run.results += $verified
                $attempt.status = 'completed'; $attempt.detail = "$expectedTests fixtures passed; dataset checks and every measured operation verified. See each raw verification contract."
            } catch {
                $attempt.status = 'failed'; $attempt.detail = $_.Exception.Message
            }
            Write-BenchmarkReport $output $run $baselineReport
            # A failed run is retained and stops further allocation; no automatic best-of retries.
            if ($attempt.status -ne 'completed') { throw "Attempt $id failed. Retain this evidence before diagnosing or retrying." }
        }
    }
    foreach ($source in $run.sourceFiles) {
        $path = Join-Path $root $source.path
        if (-not (Test-Path -LiteralPath $path) -or (Get-BenchmarkHash $path) -ne $source.sha256) { throw 'Source files changed during the run; source identity is not stable.' }
    }
    $endFiles = @(& git -C $root ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    if (($sourceFiles -join "`n") -ne ($endFiles -join "`n")) { throw 'The source file set changed during the run.' }
    $run.status = 'completed'
} catch {
    $run.status = 'failed'
    $run.failure = $_.Exception.Message
    Write-Warning $_.Exception.Message
} finally {
    foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.Name -notin @('results.json','report.md','report.html') -and $_.Extension -ne '.pending' }) {
        $run.evidence += @{ path = [IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/'); sha256 = Get-BenchmarkHash $file.FullName }
    }
    Write-BenchmarkReport $output $run $baselineReport
    Write-Host "Reports: $output"
}
if ($run.status -ne 'completed') { exit 1 }
