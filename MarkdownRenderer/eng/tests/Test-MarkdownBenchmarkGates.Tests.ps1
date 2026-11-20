# Self-test for Test-MarkdownBenchmarkGates.ps1. Each case writes synthetic
# BenchmarkDotNet full JSON reports and runs the gate in its own process.
#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Test-MarkdownBenchmarkGates.ps1'))
$pwsh = (Get-Process -Id $PID).Path
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "markdown-benchmark-gates-$([guid]::NewGuid().ToString('N'))"

$sourceLookup = 'MarkdownRenderer.Benchmarks.SourceMapBenchmarks.LookupAmongOneHundredThousandMappings'
$viewportSmall = 'MarkdownRenderer.Benchmarks.VerticalViewportIndexBenchmarks.LocateVisibleBand(BandCount: 100000)'
$viewportLarge = 'MarkdownRenderer.Benchmarks.VerticalViewportIndexBenchmarks.LocateVisibleBand(BandCount: 1000000)'
$parse = 'MarkdownRenderer.Benchmarks.MarkdownEngineBenchmarks.ParseWithoutCompletedCache(SourceBytes: 1048576)'

# A benchmark result: one mean per process launch, a p95 and an allocation.
function New-Result([string] $Name, [double[]] $LaunchMeans, [double] $P95 = 0, [double] $Allocated = 0) {
    [pscustomobject]@{
        Name = $Name
        LaunchMeans = $LaunchMeans
        P95 = $(if ($P95 -gt 0) { $P95 } else { ($LaunchMeans | Measure-Object -Maximum).Maximum * 1.02 })
        Allocated = $Allocated
    }
}

# The benchmarks every candidate run must contain; the parse benchmark gets
# the given launch means.
function New-Suite([double[]] $ParseLaunches, [double] $ParseAllocated = 31354323) {
    $launchCount = $ParseLaunches.Length
    @(
        New-Result $sourceLookup ([double[]] @(1..$launchCount | ForEach-Object { 65 }))
        New-Result $viewportSmall ([double[]] @(1..$launchCount | ForEach-Object { 11 }))
        New-Result $viewportLarge ([double[]] @(1..$launchCount | ForEach-Object { 14 }))
        New-Result $parse $ParseLaunches -Allocated $ParseAllocated
    )
}

# Writes one BenchmarkDotNet invocation: every launch gets three Workload/Result
# iterations whose mean is that launch's mean.
function Write-Run([string] $Directory, [object[]] $Results, [switch] $WithoutMeasurements) {
    $benchmarks = foreach ($result in $Results) {
        $measurements = [Collections.Generic.List[object]]::new()
        $all = [Collections.Generic.List[double]]::new()
        for ($launch = 0; $launch -lt $result.LaunchMeans.Length; $launch++) {
            $mean = $result.LaunchMeans[$launch]
            foreach ($offset in -0.01, 0, 0.01) {
                $perOperation = $mean * (1 + $offset)
                $all.Add($perOperation)
                $measurements.Add([ordered]@{
                    IterationMode = 'Workload'; IterationStage = 'Actual'; LaunchIndex = $launch + 1
                    IterationIndex = $measurements.Count + 1; Operations = 1000; Nanoseconds = $perOperation * 1000 + 50
                })
                $measurements.Add([ordered]@{
                    IterationMode = 'Workload'; IterationStage = 'Result'; LaunchIndex = $launch + 1
                    IterationIndex = $measurements.Count + 1; Operations = 1000; Nanoseconds = $perOperation * 1000
                })
            }
        }
        $benchmark = [ordered]@{
            FullName = $result.Name
            Statistics = [ordered]@{
                Mean = ($all | Measure-Object -Average).Average
                Percentiles = [ordered]@{ P95 = $result.P95 }
            }
            Memory = [ordered]@{ BytesAllocatedPerOperation = $result.Allocated }
        }
        if (-not $WithoutMeasurements) {
            $benchmark.Measurements = $measurements
        }
        $benchmark
    }

    $results = Join-Path $Directory 'results'
    [void] [IO.Directory]::CreateDirectory($results)
    $document = [ordered]@{ Title = 'synthetic'; Benchmarks = @($benchmarks) }
    [IO.File]::WriteAllText(
        (Join-Path $results 'Synthetic-report-full-compressed.json'),
        ($document | ConvertTo-Json -Depth 10 -Compress))
}

# Writes one directory per run and returns their paths.
function Write-Side([string] $Case, [string] $Side, [object[][]] $Runs, [switch] $WithoutMeasurements) {
    $paths = for ($index = 0; $index -lt $Runs.Count; $index++) {
        $directory = Join-Path $tempRoot "$Case\$Side\round-$index"
        Write-Run $directory $Runs[$index] -WithoutMeasurements:$WithoutMeasurements
        $directory
    }
    return , [string[]] @($paths)
}

function Invoke-Gate([string[]] $Candidate, [string[]] $Reference, [hashtable] $Extra = @{}) {
    $parameters = @{ CandidateDirectory = $Candidate }
    if ($Reference.Count -gt 0) { $parameters.ReferenceDirectory = $Reference }
    foreach ($key in $Extra.Keys) { $parameters[$key] = $Extra[$key] }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $parameters -Depth 4 -Compress)))
    $command = "`$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encoded')) | ConvertFrom-Json -AsHashtable; " +
        "foreach (`$k in @(`$p.Keys)) { if (`$p[`$k] -is [Collections.IList]) { `$p[`$k] = [string[]] @(`$p[`$k]) } }; " +
        "try { & '$($script.Replace("'", "''"))' @p } catch { Write-Output `$_.Exception.Message; exit 1 }"
    $output = @(& $pwsh -NoLogo -NoProfile -Command $command 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

$failures = [Collections.Generic.List[string]]::new()
function Assert-Gate([string] $Name, $Result, [bool] $ShouldPass, [string] $Expected) {
    $ok = if ($ShouldPass) { $Result.ExitCode -eq 0 } else { $Result.ExitCode -ne 0 }
    if ($ok -and -not [string]::IsNullOrEmpty($Expected)) {
        $ok = $Result.Output.Contains($Expected, [StringComparison]::Ordinal)
    }
    if ($ok) {
        Write-Host "PASS $Name"
    }
    else {
        $failures.Add($Name)
        Write-Host "FAIL $Name (exit $($Result.ExitCode))"
        $Result.Output -split "`n" | Select-Object -Last 12 | ForEach-Object { Write-Host "    $_" }
    }
}

# One launch per side and round, as the CI comparison records them. Rounds 3 and 4
# ran while the runner was slow, which affects both sides of those rounds. In round
# 5 only the candidate run was disturbed, in round 6 only the reference run.
$referenceRounds = [double[]] @(60.0e6, 60.2e6, 72.0e6, 80.0e6, 60.4e6, 75.0e6, 59.0e6)
$candidateRounds = [double[]] @(61.0e6, 59.5e6, 73.0e6, 79.0e6, 75.5e6, 60.0e6, 59.4e6)
function Split-Rounds([double[]] $Launches, [int] $PerRun = 1) {
    $runs = [Collections.Generic.List[object[]]]::new()
    for ($start = 0; $start -lt $Launches.Length; $start += $PerRun) {
        $runs.Add([object[]] @(New-Suite ([double[]] $Launches[$start..($start + $PerRun - 1)])))
    }
    return , [object[][]] $runs.ToArray()
}
function Scale([double[]] $Values, [double] $Factor) { [double[]] @($Values | ForEach-Object { $_ * $Factor }) }

try {
    $candidate = Write-Side 'absolute-only' 'candidate' (Split-Rounds $candidateRounds)
    Assert-Gate 'Absolute budgets pass without a reference' (Invoke-Gate $candidate @()) $true 'comparison was not run'

    $reference = Write-Side 'same-code' 'reference' (Split-Rounds $referenceRounds)
    $candidate = Write-Side 'same-code' 'candidate' (Split-Rounds $candidateRounds)
    Assert-Gate 'Same code with shared slow rounds and one disturbed run per side passes' (Invoke-Gate $candidate $reference) $true 'regression gate passed'

    $candidate = Write-Side 'slow-ten' 'candidate' (Split-Rounds (Scale $candidateRounds 1.10))
    Assert-Gate 'A 10% slowdown in every round fails' (Invoke-Gate $candidate $reference) $false 'Latency regression for ''MarkdownRenderer.Benchmarks.MarkdownEngineBenchmarks.ParseWithoutCompletedCache(SourceBytes: 1048576)'''

    # The decision threshold is unchanged: a clean 4% shift passes and a clean 6% shift fails.
    $flat = [double[]] @(1..5 | ForEach-Object { 60e6 })
    $reference = Write-Side 'threshold' 'reference' (Split-Rounds $flat)
    $candidate = Write-Side 'threshold-4' 'candidate' (Split-Rounds (Scale $flat 1.04))
    Assert-Gate 'A 4% shift is within the 5% budget' (Invoke-Gate $candidate $reference) $true 'regression gate passed'
    $candidate = Write-Side 'threshold-6' 'candidate' (Split-Rounds (Scale $flat 1.06))
    Assert-Gate 'A 6% shift exceeds the 5% budget' (Invoke-Gate $candidate $reference) $false 'ratio 1.0600 (+6.00%; allowed +5%)'

    # One round is exactly the former single-run comparison.
    $reference = Write-Side 'single' 'reference' (Split-Rounds ([double[]] @(62832365.0)))
    $candidate = Write-Side 'single' 'candidate' (Split-Rounds ([double[]] @(78798218.57)))
    Assert-Gate 'One round keeps the single-run ratio' (Invoke-Gate $candidate $reference) $false '(+25.41%; allowed +5%)'

    # Rounds are paired, so a missing round cannot shrink the comparison silently.
    $reference = Write-Side 'rounds' 'reference' (Split-Rounds $referenceRounds)
    $candidate = Write-Side 'rounds' 'candidate' (Split-Rounds ([double[]] $candidateRounds[0..5]))
    Assert-Gate 'A missing candidate round fails' (Invoke-Gate $candidate $reference) $false 'needs one reference run per candidate run'

    $runs = Split-Rounds $candidateRounds
    $runs[1] = [object[]] @($runs[1] | Where-Object { $_.Name -ne $parse })
    $candidate = Write-Side 'missing-benchmark' 'candidate' $runs
    Assert-Gate 'A benchmark missing from one candidate round fails' (Invoke-Gate $candidate $reference) $false 'executed a different benchmark set'

    $runs = Split-Rounds $candidateRounds
    for ($index = 0; $index -lt $runs.Count; $index++) {
        $runs[$index] = [object[]] @($runs[$index] | Where-Object { $_.Name -ne $parse })
    }
    $candidate = Write-Side 'missing-everywhere' 'candidate' $runs
    Assert-Gate 'A reference benchmark the candidate never ran fails' (Invoke-Gate $candidate $reference) $false 'Candidate did not execute reference benchmark'

    # Allocation: one candidate round above budget fails even when the others are fine.
    $runs = Split-Rounds $candidateRounds
    $runs[1] = New-Suite ([double[]] @($candidateRounds[1])) -ParseAllocated (31354323 * 1.03)
    $candidate = Write-Side 'allocation' 'candidate' $runs
    Assert-Gate 'An allocation regression in one round fails' (Invoke-Gate $candidate $reference) $false 'Allocation regression for'

    # Absolute budgets apply to every candidate round, not only the first.
    $runs = Split-Rounds $candidateRounds
    $runs[1][0] = New-Result $sourceLookup ([double[]] @(65)) -P95 30000
    $candidate = Write-Side 'absolute-second' 'candidate' $runs
    Assert-Gate 'A source-lookup budget miss in a later round fails' (Invoke-Gate $candidate $reference) $false 'the release budget is 25,000 ns'

    $candidate = Write-Side 'no-measurements' 'candidate' (Split-Rounds $candidateRounds) -WithoutMeasurements
    Assert-Gate 'Reports without raw measurements are rejected' (Invoke-Gate $candidate $reference) $false 'has no raw measurements'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) benchmark gate self-test case(s) failed: $($failures -join '; ')"
}
Write-Host 'Benchmark gate self-tests passed.'
