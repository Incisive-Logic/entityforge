param([Parameter(Mandatory)][string]$InputPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Read bounded evidence only. This tool does not collect, force GC, or certify endurance.
$file = Get-Item -LiteralPath $InputPath
if ($file.Length -gt 32MB) { throw 'Workload report exceeds the 32 MiB review limit.' }
$report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
if ($report.schema -ne 6 -or $report.status -notin @('completed', 'completed-with-conflicts')) {
    throw 'A completed mixed-workload schema 6 report is required.'
}
function NonnegativeNumber($value, [string]$name) {
    if ($null -eq $value -or $value -is [string] -or $value -is [bool] -or
        ![double]::IsFinite([double]$value) -or [double]$value -lt 0) { throw "Invalid $name." }
    return [double]$value
}
$sampling = $report.resourceSampling
$start = NonnegativeNumber $sampling.measurementStartedAtSampleSeconds 'measurement start'
$end = NonnegativeNumber $sampling.measurementEndedAtSampleSeconds 'measurement end'
if ($end -le $start) { throw 'Measurement boundaries must be increasing.' }
$samples = @($sampling.samples)
if ($samples.Count -lt 2 -or $samples.Count -gt 4096) { throw 'Expected 2 to 4,096 resource samples.' }
$windowStart = [Math]::Max($start, $end - 86400)
$previousTime = $null
$states = @{}
foreach ($kind in @('LastBlockingGen2', 'LastBackgroundGen2')) {
    $states[$kind] = @{ Index = 0L; Signature = ''; Repeated = 0; Observations = [Collections.Generic.List[object]]::new() }
}
foreach ($sample in $samples) {
    $time = NonnegativeNumber $sample.Seconds 'sample time'
    if ($null -ne $previousTime -and $time -lt $previousTime) { throw 'Resource sample times decrease.' }
    foreach ($kind in @('LastBlockingGen2', 'LastBackgroundGen2')) {
        $collection = $sample.$kind
        $state = $states[$kind]
        if ($null -eq $collection) {
            if ($state.Index -ne 0) { throw 'A previously observed collection disappeared.' }
            continue
        }
        foreach ($field in @('Index', 'HeapBytes', 'FragmentedBytes', 'CommittedBytes', 'FinalizationPendingCount')) {
            $number = NonnegativeNumber $collection.$field $field
            if ([Math]::Truncate($number) -ne $number) { throw "Non-integral $field." }
        }
        if ($collection.Index -le 0 -or $collection.Index -lt $state.Index -or
            $collection.FragmentedBytes -gt $collection.HeapBytes) { throw 'Invalid full-GC observation.' }
        $signature = $collection | ConvertTo-Json -Compress
        if ($collection.Index -eq $state.Index) {
            if ($signature -ne $state.Signature) { throw 'The same collection index has inconsistent values.' }
            $state.Repeated++
            continue
        }
        $state.Index = $collection.Index
        $state.Signature = $signature
        # A new index is bracketed by consecutive sample times. Exclude the
        # initial inherited observation and intervals crossing either boundary.
        if ($null -ne $previousTime -and $previousTime -ge $windowStart -and $time -le $end) {
            $state.Observations.Add([pscustomobject]@{
                index = $collection.Index; observedAfterSeconds = $previousTime; observedBySeconds = $time
                liveHeapEstimateBytes = [decimal]$collection.HeapBytes - [decimal]$collection.FragmentedBytes
                heapBytes = $collection.HeapBytes; fragmentedBytes = $collection.FragmentedBytes
                committedBytes = $collection.CommittedBytes; pendingFinalizers = $collection.FinalizationPendingCount
            })
        }
    }
    $previousTime = $time
}
$coversWindow = [double]$samples[0].Seconds -le $windowStart -and [double]$samples[-1].Seconds -ge $end
$fullDay = $end - $start -ge 86400 -and $coversWindow
$series = foreach ($kind in @('LastBlockingGen2', 'LastBackgroundGen2')) {
    $state = $states[$kind]
    $observations = @($state.Observations.ToArray())
    $growth = $null
    if ($observations.Count -ge 2 -and $observations[0].liveHeapEstimateBytes -gt 0) {
        $growth = 100 * ($observations[-1].liveHeapEstimateBytes - $observations[0].liveHeapEstimateBytes) /
            $observations[0].liveHeapEstimateBytes
    }
    [pscustomobject]@{
        kind = $kind; distinctCollectionsInWindow = $observations.Count; repeatedObservationsIgnored = $state.Repeated
        firstToLastLiveHeapGrowthPercent = $growth
        exceedsTenPercent = if ($null -eq $growth) { $null } else { $growth -gt 10 }
        observations = $observations
    }
}
[ordered]@{
    schema = 1; status = if ($fullDay) { 'review-required' } else { 'insufficient-duration-or-sampling' }
    inputSha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    profile = $report.profile; measurementStartSeconds = $start; measurementEndSeconds = $end
    reviewWindowStartSeconds = $windowStart; reviewWindowEndSeconds = $end
    measurementSpansFullDay = ($end - $start -ge 86400)
    samplesBracketReviewWindow = $coversWindow; series = @($series)
    qualification = 'Observations only, never an endurance pass. Full-GC live-heap estimates are process-wide, including client and oracle. Collection times are bracketed by samples. First-to-last growth is a review signal, not a trend fit or proof of database-owned retained memory. Inspect workload/live-state stability, collection coverage, finalizers, storage and all other qualification evidence.'
} | ConvertTo-Json -Depth 8
