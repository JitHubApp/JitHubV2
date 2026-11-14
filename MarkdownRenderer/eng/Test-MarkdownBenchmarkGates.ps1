[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CandidateDirectory,

    [Parameter(Mandatory = $false)]
    [string] $ReferenceDirectory,

    # MarkdownRenderer 1.0's public source-map lookup budget is 25 microseconds p95.
    [ValidateRange(1, [long]::MaxValue)]
    [long] $SourceLookupP95Nanoseconds = 25000,

    # Container-local viewport entry must remain comfortably below a microsecond
    # even for one million realized bands, and it must allocate nothing.
    [ValidateRange(1, [long]::MaxValue)]
    [long] $ViewportLookupP95Nanoseconds = 1000,

    # These are the release-plan regression budgets, not runner-specific timings.
    [ValidateRange(0, 100)]
    [double] $LatencyRegressionPercent = 5,

    [ValidateRange(0, 100)]
    [double] $AllocationRegressionPercent = 2
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-BenchmarkResults {
    param(
        [Parameter(Mandatory)]
        [string] $Directory,

        [Parameter(Mandatory)]
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "$Label benchmark directory '$Directory' does not exist."
    }

    $reports = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter '*-report-full-compressed.json' |
        Sort-Object FullName)
    if ($reports.Count -eq 0) {
        throw "$Label benchmark directory '$Directory' contains no BenchmarkDotNet JSON reports."
    }

    $results = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($report in $reports) {
        $document = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json -Depth 100
        if ($null -eq $document.Benchmarks) {
            throw "Benchmark report '$($report.FullName)' has no benchmark results."
        }

        foreach ($benchmark in @($document.Benchmarks)) {
            $fullName = [string] $benchmark.FullName
            if ([string]::IsNullOrWhiteSpace($fullName)) {
                throw "Benchmark report '$($report.FullName)' contains a result without a FullName."
            }
            if ($results.ContainsKey($fullName)) {
                throw "$Label benchmark '$fullName' appears in more than one JSON report."
            }
            if ($null -eq $benchmark.Statistics -or $null -eq $benchmark.Statistics.Percentiles) {
                throw "$Label benchmark '$fullName' has no latency statistics."
            }
            if ($null -eq $benchmark.Memory) {
                throw "$Label benchmark '$fullName' has no MemoryDiagnoser result."
            }

            $meanNanoseconds = [double] $benchmark.Statistics.Mean
            $p95Nanoseconds = [double] $benchmark.Statistics.Percentiles.P95
            $allocatedBytes = [double] $benchmark.Memory.BytesAllocatedPerOperation
            if (-not [double]::IsFinite($meanNanoseconds) -or $meanNanoseconds -le 0) {
                throw "$Label benchmark '$fullName' has an invalid mean latency: $meanNanoseconds."
            }
            if (-not [double]::IsFinite($p95Nanoseconds) -or $p95Nanoseconds -le 0) {
                throw "$Label benchmark '$fullName' has an invalid p95 latency: $p95Nanoseconds."
            }
            if (-not [double]::IsFinite($allocatedBytes) -or $allocatedBytes -lt 0) {
                throw "$Label benchmark '$fullName' has an invalid allocation result: $allocatedBytes."
            }

            $results.Add($fullName, [pscustomobject][ordered]@{
                FullName = $fullName
                MeanNanoseconds = $meanNanoseconds
                P95Nanoseconds = $p95Nanoseconds
                AllocatedBytesPerOperation = $allocatedBytes
                Report = $report.FullName
            })
        }
    }

    if ($results.Count -eq 0) {
        throw "$Label benchmark reports contain no results."
    }

    return $results
}

function Format-DeltaPercent {
    param(
        [Parameter(Mandatory)][double] $Candidate,
        [Parameter(Mandatory)][double] $Reference
    )

    if ($Reference -eq 0) {
        return $(if ($Candidate -eq 0) { '0.00%' } else { 'infinite' })
    }

    return '{0:+0.00%;-0.00%;0.00%}' -f (($Candidate / $Reference) - 1)
}

$candidateResults = Read-BenchmarkResults -Directory $CandidateDirectory -Label 'Candidate'
$sourceLookupName = 'MarkdownRenderer.Benchmarks.SourceMapBenchmarks.LookupAmongOneHundredThousandMappings'
if (-not $candidateResults.ContainsKey($sourceLookupName)) {
    throw "Required source-map benchmark '$sourceLookupName' was not executed."
}

$sourceLookup = $candidateResults[$sourceLookupName]
if ($sourceLookup.P95Nanoseconds -gt $SourceLookupP95Nanoseconds) {
    throw ('Source-map lookup p95 was {0:N2} ns; the release budget is {1:N0} ns.' -f
        $sourceLookup.P95Nanoseconds, $SourceLookupP95Nanoseconds)
}

# This lookup benchmark's established allocation baseline is zero. Requiring zero avoids
# manufacturing a byte allowance and catches boxing or temporary collection regressions.
if ($sourceLookup.AllocatedBytesPerOperation -ne 0) {
    throw ('Source-map lookup allocated {0:N2} bytes/op; the established baseline is zero.' -f
        $sourceLookup.AllocatedBytesPerOperation)
}

Write-Host ('Source-map budget passed: p95 {0:N2} ns <= {1:N0} ns; {2:N0} bytes/op.' -f
    $sourceLookup.P95Nanoseconds,
    $SourceLookupP95Nanoseconds,
    $sourceLookup.AllocatedBytesPerOperation)

$viewportLookupPrefix = 'MarkdownRenderer.Benchmarks.VerticalViewportIndexBenchmarks.LocateVisibleBand('
$viewportLookups = @($candidateResults.Values | Where-Object {
    $_.FullName.StartsWith($viewportLookupPrefix, [StringComparison]::Ordinal)
})
if ($viewportLookups.Count -ne 2) {
    throw "Required viewport-index benchmarks were not both executed (found $($viewportLookups.Count), expected 2)."
}
foreach ($viewportLookup in $viewportLookups) {
    if ($viewportLookup.P95Nanoseconds -gt $ViewportLookupP95Nanoseconds) {
        throw ("Viewport-index lookup '$($viewportLookup.FullName)' p95 was " +
            "$($viewportLookup.P95Nanoseconds.ToString('N2')) ns; the release budget is " +
            "$($ViewportLookupP95Nanoseconds.ToString('N0')) ns.")
    }
    if ($viewportLookup.AllocatedBytesPerOperation -ne 0) {
        throw "Viewport-index lookup '$($viewportLookup.FullName)' allocated $($viewportLookup.AllocatedBytesPerOperation) bytes/op."
    }
}
Write-Host (
    "Viewport-index budgets passed for 100,000 and 1,000,000 bands: p95 <= " +
    "$($ViewportLookupP95Nanoseconds.ToString('N0')) ns and zero bytes/op.")

if ([string]::IsNullOrWhiteSpace($ReferenceDirectory)) {
    Write-Host 'No same-machine reference result was supplied; cross-revision regression comparison was not run.'
    exit 0
}

$referenceResults = Read-BenchmarkResults -Directory $ReferenceDirectory -Label 'Reference'
$latencyMultiplier = 1 + ($LatencyRegressionPercent / 100)
$allocationMultiplier = 1 + ($AllocationRegressionPercent / 100)

$failures = [Collections.Generic.List[string]]::new()
foreach ($referenceEntry in $referenceResults.GetEnumerator() | Sort-Object Key) {
    $name = $referenceEntry.Key
    $reference = $referenceEntry.Value
    if (-not $candidateResults.ContainsKey($name)) {
        $failures.Add("Candidate did not execute reference benchmark '$name'.")
        continue
    }

    $candidate = $candidateResults[$name]
    $maximumMean = $reference.MeanNanoseconds * $latencyMultiplier
    if ($candidate.MeanNanoseconds -gt $maximumMean) {
        $delta = Format-DeltaPercent -Candidate $candidate.MeanNanoseconds -Reference $reference.MeanNanoseconds
        $failures.Add(
            "Latency regression for '$name': candidate $($candidate.MeanNanoseconds.ToString('N2')) ns, " +
            "reference $($reference.MeanNanoseconds.ToString('N2')) ns ($delta; allowed +$LatencyRegressionPercent%).")
    }

    $maximumAllocation = if ($reference.AllocatedBytesPerOperation -eq 0) {
        0
    }
    else {
        $reference.AllocatedBytesPerOperation * $allocationMultiplier
    }
    if ($candidate.AllocatedBytesPerOperation -gt $maximumAllocation) {
        $delta = Format-DeltaPercent `
            -Candidate $candidate.AllocatedBytesPerOperation `
            -Reference $reference.AllocatedBytesPerOperation
        $failures.Add(
            "Allocation regression for '$name': candidate $($candidate.AllocatedBytesPerOperation.ToString('N2')) bytes/op, " +
            "reference $($reference.AllocatedBytesPerOperation.ToString('N2')) bytes/op ($delta; allowed +$AllocationRegressionPercent%).")
    }
}

if ($failures.Count -gt 0) {
    throw "Benchmark regression gate failed:`n - $($failures -join "`n - ")"
}

Write-Host (
    "Benchmark regression gate passed for $($referenceResults.Count) reference benchmarks " +
    "(latency <= +$LatencyRegressionPercent%; allocation <= +$AllocationRegressionPercent%).")
