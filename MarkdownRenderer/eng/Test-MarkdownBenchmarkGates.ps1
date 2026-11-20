# Applies the MarkdownRenderer BenchmarkDotNet gates.
#
# Absolute budgets are checked in every candidate round.
#
# The optional cross-revision comparison is paired. Shared GitHub runners change
# speed in regimes lasting minutes, and separate process launches of one build land
# several percent apart, so one reference run compared with one candidate run
# measured minutes later cannot resolve a 5% budget (identical code exceeded it in
# 39 of 60 such comparisons). Invoke-MarkdownBenchmarkComparison.ps1 therefore
# measures the reference and the candidate back to back in several rounds.
# ReferenceDirectory[i] and CandidateDirectory[i] hold round i of each side.
#
# Each round gives one log ratio per benchmark, ln(candidate mean / reference mean).
# The latency estimate is the one-sample Hodges-Lehmann location of those round
# ratios (the median of all pairwise round averages), as in the performance
# harness. The budget and the decision rule are unchanged: the gate fails when the
# estimated ratio exceeds 1 + LatencyRegressionPercent/100. With one round the
# estimate is exactly the former single-run ratio.
[CmdletBinding()]
param(
    # Candidate BenchmarkDotNet artifact directories, one per round.
    [Parameter(Mandatory)]
    [string[]] $CandidateDirectory,

    # Reference BenchmarkDotNet artifact directories, one per round and paired with
    # CandidateDirectory by position. Without them, only the absolute budgets are checked.
    [Parameter(Mandatory = $false)]
    [string[]] $ReferenceDirectory = @(),

    # Optional Markdown file that receives the comparison table (for example the
    # GitHub step summary).
    [string] $SummaryPath,

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

function Read-BenchmarkRun {
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
                throw "$Label benchmark '$fullName' appears in more than one JSON report in '$Directory'."
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

            $launchMeans = @(Get-LaunchMeans -Benchmark $benchmark -FullName $fullName -Label $Label)

            $results.Add($fullName, [pscustomobject][ordered]@{
                FullName = $fullName
                MeanNanoseconds = $meanNanoseconds
                P95Nanoseconds = $p95Nanoseconds
                AllocatedBytesPerOperation = $allocatedBytes
                LaunchMeans = $launchMeans
                Report = $report.FullName
            })
        }
    }

    if ($results.Count -eq 0) {
        throw "$Label benchmark reports in '$Directory' contain no results."
    }

    return $results
}

# BenchmarkDotNet's Statistics.Mean is the mean of the Workload/Result measurements
# (actual iterations minus overhead, upper outliers removed), pooled over launches.
# The same measurements grouped by LaunchIndex give one mean per process launch.
function Get-LaunchMeans {
    param(
        [Parameter(Mandatory)] $Benchmark,
        [Parameter(Mandatory)][string] $FullName,
        [Parameter(Mandatory)][string] $Label
    )

    if ($null -eq $Benchmark.PSObject.Properties['Measurements'] -or $null -eq $Benchmark.Measurements) {
        throw "$Label benchmark '$FullName' has no raw measurements; export the full JSON report."
    }

    $results = @($Benchmark.Measurements | Where-Object {
        $_.IterationMode -eq 'Workload' -and $_.IterationStage -eq 'Result'
    })
    if ($results.Count -eq 0) {
        throw "$Label benchmark '$FullName' has no Workload/Result measurements."
    }

    $means = foreach ($launch in $results | Group-Object { [int] $_.LaunchIndex } | Sort-Object { [int] $_.Name }) {
        $sum = 0.0
        foreach ($measurement in $launch.Group) {
            $operations = [double] $measurement.Operations
            $nanoseconds = [double] $measurement.Nanoseconds
            if (-not [double]::IsFinite($operations) -or $operations -le 0 -or
                -not [double]::IsFinite($nanoseconds) -or $nanoseconds -le 0) {
                throw "$Label benchmark '$FullName' launch $($launch.Name) has an invalid measurement."
            }
            $sum += $nanoseconds / $operations
        }
        $sum / $launch.Group.Count
    }

    return @($means)
}

function Read-BenchmarkSide {
    param(
        [Parameter(Mandatory)]
        [string[]] $Directories,

        [Parameter(Mandatory)]
        [string] $Label
    )

    $runs = @(foreach ($directory in $Directories) { , (Read-BenchmarkRun -Directory $directory -Label $Label) })

    # Every round of one side must have executed the same benchmarks, otherwise a
    # benchmark missing from one round would be compared on fewer rounds.
    $expected = @($runs[0].Keys | Sort-Object)
    for ($index = 1; $index -lt $runs.Count; $index++) {
        $actual = @($runs[$index].Keys | Sort-Object)
        $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual -CaseSensitive)
        if ($difference.Count -gt 0) {
            throw ("$Label run '$($Directories[$index])' executed a different benchmark set from " +
                "'$($Directories[0])': $(@($difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join '; ').")
        }
    }

    return , $runs
}

function Get-Median {
    param([Parameter(Mandatory)][double[]] $Values)

    $sorted = [double[]] $Values.Clone()
    [Array]::Sort($sorted)
    $middle = [int] [Math]::Floor($sorted.Length / 2)
    if ($sorted.Length % 2 -eq 1) {
        return $sorted[$middle]
    }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

# Mean of the log launch means of one run (a single value for one launch).
function Get-LogMean {
    param([Parameter(Mandatory)][double[]] $Values)

    $sum = 0.0
    foreach ($value in $Values) {
        $sum += [Math]::Log($value)
    }
    return $sum / $Values.Length
}

# One-sample Hodges-Lehmann location: the median of the Walsh averages
# (x_i + x_j) / 2 for every i <= j.
function Get-HodgesLehmannLocation {
    param([Parameter(Mandatory)][double[]] $Values)

    $averages = [Collections.Generic.List[double]]::new()
    for ($i = 0; $i -lt $Values.Length; $i++) {
        for ($j = $i; $j -lt $Values.Length; $j++) {
            $averages.Add(($Values[$i] + $Values[$j]) / 2)
        }
    }
    return Get-Median -Values $averages.ToArray()
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

$candidateRuns = Read-BenchmarkSide -Directories $CandidateDirectory -Label 'Candidate'

$sourceLookupName = 'MarkdownRenderer.Benchmarks.SourceMapBenchmarks.LookupAmongOneHundredThousandMappings'
$viewportLookupPrefix = 'MarkdownRenderer.Benchmarks.VerticalViewportIndexBenchmarks.LocateVisibleBand('
for ($runIndex = 0; $runIndex -lt $candidateRuns.Count; $runIndex++) {
    $candidateResults = $candidateRuns[$runIndex]
    $runLabel = "candidate run '$($CandidateDirectory[$runIndex])'"
    if (-not $candidateResults.ContainsKey($sourceLookupName)) {
        throw "Required source-map benchmark '$sourceLookupName' was not executed in $runLabel."
    }

    $sourceLookup = $candidateResults[$sourceLookupName]
    if ($sourceLookup.P95Nanoseconds -gt $SourceLookupP95Nanoseconds) {
        throw ('Source-map lookup p95 was {0:N2} ns in {1}; the release budget is {2:N0} ns.' -f
            $sourceLookup.P95Nanoseconds, $runLabel, $SourceLookupP95Nanoseconds)
    }

    # This lookup benchmark's established allocation baseline is zero. Requiring zero avoids
    # manufacturing a byte allowance and catches boxing or temporary collection regressions.
    if ($sourceLookup.AllocatedBytesPerOperation -ne 0) {
        throw ('Source-map lookup allocated {0:N2} bytes/op in {1}; the established baseline is zero.' -f
            $sourceLookup.AllocatedBytesPerOperation, $runLabel)
    }

    Write-Host ('Source-map budget passed in {0}: p95 {1:N2} ns <= {2:N0} ns; {3:N0} bytes/op.' -f
        $runLabel,
        $sourceLookup.P95Nanoseconds,
        $SourceLookupP95Nanoseconds,
        $sourceLookup.AllocatedBytesPerOperation)

    $viewportLookups = @($candidateResults.Values | Where-Object {
        $_.FullName.StartsWith($viewportLookupPrefix, [StringComparison]::Ordinal)
    })
    if ($viewportLookups.Count -ne 2) {
        throw "Required viewport-index benchmarks were not both executed in $runLabel (found $($viewportLookups.Count), expected 2)."
    }
    foreach ($viewportLookup in $viewportLookups) {
        if ($viewportLookup.P95Nanoseconds -gt $ViewportLookupP95Nanoseconds) {
            throw ("Viewport-index lookup '$($viewportLookup.FullName)' p95 was " +
                "$($viewportLookup.P95Nanoseconds.ToString('N2')) ns in $runLabel; the release budget is " +
                "$($ViewportLookupP95Nanoseconds.ToString('N0')) ns.")
        }
        if ($viewportLookup.AllocatedBytesPerOperation -ne 0) {
            throw "Viewport-index lookup '$($viewportLookup.FullName)' allocated $($viewportLookup.AllocatedBytesPerOperation) bytes/op in $runLabel."
        }
    }
    Write-Host (
        "Viewport-index budgets passed in $runLabel for 100,000 and 1,000,000 bands: p95 <= " +
        "$($ViewportLookupP95Nanoseconds.ToString('N0')) ns and zero bytes/op.")
}

if ($ReferenceDirectory.Count -eq 0) {
    Write-Host 'No same-machine reference result was supplied; cross-revision regression comparison was not run.'
    exit 0
}

if ($ReferenceDirectory.Count -ne $CandidateDirectory.Count) {
    throw ("The paired comparison needs one reference run per candidate run; got " +
        "$($ReferenceDirectory.Count) reference and $($CandidateDirectory.Count) candidate runs.")
}

$referenceRuns = Read-BenchmarkSide -Directories $ReferenceDirectory -Label 'Reference'
$latencyMultiplier = 1 + ($LatencyRegressionPercent / 100)
$allocationMultiplier = 1 + ($AllocationRegressionPercent / 100)

$failures = [Collections.Generic.List[string]]::new()
$rows = [Collections.Generic.List[object]]::new()
foreach ($name in @($referenceRuns[0].Keys | Sort-Object)) {
    $missing = @(for ($index = 0; $index -lt $candidateRuns.Count; $index++) {
        if (-not $candidateRuns[$index].ContainsKey($name)) { $CandidateDirectory[$index] }
    })
    if ($missing.Count -gt 0) {
        $failures.Add("Candidate did not execute reference benchmark '$name' in: $($missing -join ', ').")
        continue
    }

    # One log ratio per round, from the reference and candidate runs measured together.
    $roundLogRatios = [double[]] @(for ($index = 0; $index -lt $referenceRuns.Count; $index++) {
        (Get-LogMean -Values $candidateRuns[$index][$name].LaunchMeans) -
            (Get-LogMean -Values $referenceRuns[$index][$name].LaunchMeans)
    })
    $ratio = [Math]::Exp((Get-HodgesLehmannLocation -Values $roundLogRatios))
    $roundRatios = [double[]] @($roundLogRatios | ForEach-Object { [Math]::Exp($_) })
    $lowestRound = ($roundRatios | Measure-Object -Minimum).Minimum
    $highestRound = ($roundRatios | Measure-Object -Maximum).Maximum
    $referenceMedian = Get-Median -Values ([double[]] @($referenceRuns | ForEach-Object { $_[$name].LaunchMeans }))
    $candidateMedian = Get-Median -Values ([double[]] @($candidateRuns | ForEach-Object { $_[$name].LaunchMeans }))
    $latencyPassed = $ratio -le $latencyMultiplier
    if (-not $latencyPassed) {
        $failures.Add(
            "Latency regression for '$name': Hodges-Lehmann candidate/reference ratio $($ratio.ToString('F4')) " +
            "($('{0:+0.00%;-0.00%;0.00%}' -f ($ratio - 1)); allowed +$LatencyRegressionPercent%) over " +
            "$($roundRatios.Length) paired rounds (round ratios $($lowestRound.ToString('F3'))-$($highestRound.ToString('F3'))); " +
            "median means: candidate $($candidateMedian.ToString('N2')) ns, reference $($referenceMedian.ToString('N2')) ns.")
    }

    # Allocation is close to deterministic, so it is compared conservatively: the
    # largest candidate result must stay within budget of the smallest reference result.
    $referenceAllocation = (@($referenceRuns | ForEach-Object { $_[$name].AllocatedBytesPerOperation }) | Measure-Object -Minimum).Minimum
    $candidateAllocation = (@($candidateRuns | ForEach-Object { $_[$name].AllocatedBytesPerOperation }) | Measure-Object -Maximum).Maximum
    $maximumAllocation = if ($referenceAllocation -eq 0) {
        0
    }
    else {
        $referenceAllocation * $allocationMultiplier
    }
    $allocationPassed = $candidateAllocation -le $maximumAllocation
    if (-not $allocationPassed) {
        $delta = Format-DeltaPercent -Candidate $candidateAllocation -Reference $referenceAllocation
        $failures.Add(
            "Allocation regression for '$name': candidate $($candidateAllocation.ToString('N2')) bytes/op, " +
            "reference $($referenceAllocation.ToString('N2')) bytes/op ($delta; allowed +$AllocationRegressionPercent%).")
    }

    $rows.Add([pscustomobject][ordered]@{
        Benchmark = $name.Replace('MarkdownRenderer.Benchmarks.', '')
        Rounds = $roundRatios.Length
        RoundRange = "$($lowestRound.ToString('F3'))-$($highestRound.ToString('F3'))"
        ReferenceMedian = $referenceMedian
        CandidateMedian = $candidateMedian
        Ratio = $ratio
        ReferenceAllocation = $referenceAllocation
        CandidateAllocation = $candidateAllocation
        Passed = $latencyPassed -and $allocationPassed
    })
}

foreach ($row in $rows) {
    Write-Host ('{0} {1}: ratio {2:F4} over {3} rounds (round ratios {4}); median {5:N2} -> {6:N2} ns; {7:N0} -> {8:N0} bytes/op.' -f
        $(if ($row.Passed) { 'PASS' } else { 'FAIL' }),
        $row.Benchmark, $row.Ratio, $row.Rounds, $row.RoundRange, $row.ReferenceMedian, $row.CandidateMedian,
        $row.ReferenceAllocation, $row.CandidateAllocation)
}

if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('### MarkdownRenderer benchmark regression gate')
    $lines.Add('')
    $lines.Add("Latency: Hodges-Lehmann location of paired per-round candidate/reference ratios, budget <= +$LatencyRegressionPercent%. " +
        "Allocation: largest candidate vs smallest reference, budget <= +$AllocationRegressionPercent%.")
    $lines.Add('')
    $lines.Add('| Benchmark | Rounds | Round ratios | Reference median (ns) | Candidate median (ns) | Latency ratio | Allocation (ref -> cand B/op) | Result |')
    $lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |')
    foreach ($row in $rows) {
        $lines.Add(('| `{0}` | {1} | {2} | {3:N2} | {4:N2} | {5:F4} | {6:N0} -> {7:N0} | {8} |' -f
            $row.Benchmark, $row.Rounds, $row.RoundRange, $row.ReferenceMedian, $row.CandidateMedian, $row.Ratio,
            $row.ReferenceAllocation, $row.CandidateAllocation, $(if ($row.Passed) { 'pass' } else { '**fail**' })))
    }
    Add-Content -LiteralPath $SummaryPath -Value $lines
}

if ($failures.Count -gt 0) {
    throw "Benchmark regression gate failed:`n - $($failures -join "`n - ")"
}

Write-Host (
    "Benchmark regression gate passed for $($rows.Count) reference benchmarks " +
    "(latency Hodges-Lehmann ratio <= +$LatencyRegressionPercent%; allocation <= +$AllocationRegressionPercent%).")
