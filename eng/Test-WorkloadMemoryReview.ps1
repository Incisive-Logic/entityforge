$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('entityforge-memory-review-' + [Guid]::NewGuid().ToString('N') + '.json')
function Sample([double]$seconds, [long]$index, [long]$live) {
    return @{ Seconds = $seconds; LastBackgroundGen2 = $null; LastBlockingGen2 = @{
        Index = $index; HeapBytes = $live + 20; FragmentedBytes = 20; CommittedBytes = $live + 100
        Concurrent = $false; Compacted = $true; FinalizationPendingCount = 0
    } }
}
function Review($samples, [double]$start = 100, [double]$end = 86500, [string]$status = 'completed') {
    @{ schema = 6; status = $status; profile = 'graph'; resourceSampling = @{
        measurementStartedAtSampleSeconds = $start; measurementEndedAtSampleSeconds = $end; samples = $samples
    } } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fixture
    return (& "$PSScriptRoot/Review-WorkloadMemory.ps1" -InputPath $fixture | ConvertFrom-Json)
}
function Require([bool]$condition, [string]$message) { if (!$condition) { throw $message } }
function Reject([scriptblock]$action) {
    $rejected = $false
    try { $null = & $action } catch { $rejected = $true }
    Require $rejected 'Malformed or incomplete evidence was accepted.'
}
try {
    $samples = @((Sample 0 1 900), (Sample 100 1 900), (Sample 200 2 100),
        (Sample 300 2 100), (Sample 86400 3 111), (Sample 86500 3 111), (Sample 86600 4 9000))
    $review = Review $samples
    Require ($review.status -eq 'review-required') 'A full day should require review, never pass.'
    $blocking = $review.series[0]
    Require ($blocking.distinctCollectionsInWindow -eq 2) 'Repeated, setup or restart collections polluted the window.'
    Require ($blocking.firstToLastLiveHeapGrowthPercent -eq 11) 'Fragmentation-adjusted growth is incorrect.'
    Require ($blocking.exceedsTenPercent -eq $true) 'Growth above ten percent was not flagged.'
    Require ($review.series[1].distinctCollectionsInWindow -eq 0) 'Absent background collections were invented.'

    $exact = Review @((Sample 0 1 900), (Sample 100 1 900), (Sample 200 2 100),
        (Sample 86400 3 110), (Sample 86500 3 110))
    Require ($exact.series[0].exceedsTenPercent -eq $false) 'Exactly ten percent is not above the threshold.'

    $short = Review @((Sample 0 1 100), (Sample 101 2 110), (Sample 105 2 110), (Sample 110 3 120)) 100 105
    Require ($short.status -eq 'insufficient-duration-or-sampling') 'A short run was treated as a full day.'
    Require ($short.series[0].distinctCollectionsInWindow -eq 0) 'A collection interval crossing the boundary was accepted.'

    $stale = Review @((Sample 0 1 100), (Sample 100 1 100), (Sample 86500 1 100))
    Require ($null -eq $stale.series[0].firstToLastLiveHeapGrowthPercent) 'Repeated collections manufactured a growth estimate.'

    Reject { Review @((Sample 0 1 100), (Sample 100 1 101), (Sample 86500 2 110)) }
    Reject { Review @((Sample 0 2 100), (Sample 100 1 100), (Sample 86500 3 110)) }
    Reject { Review @((Sample 100 1 100), (Sample 0 2 100), (Sample 86500 3 110)) }
    Reject { Review $samples 100 86500 'incomplete' }
    Write-Output 'Passed: memory review boundaries, repeated collections, growth thresholds and invalid-evidence rejection.'
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Force }
}
