# Runs the MarkdownRenderer BenchmarkDotNet suite for the CI benchmark gates.
#
# With a reference build, every round runs each benchmark method once for the
# reference and once for the candidate, back to back, alternating which side goes
# first. Shared GitHub runners change speed in regimes lasting minutes (the same
# build measured 10 minutes apart can differ by 20-25%), so the two sides of a
# comparison must be measured seconds apart rather than a whole suite apart.
# BenchmarkDotNet's in-process toolchain makes that affordable: it runs the
# already-built benchmark assembly without generating and building a separate
# project for every invocation.
#
# Every invocation is a fresh process, and BenchmarkDotNet's pilot stage sizes
# iterations from the first timings. If those come from not-yet-tiered code, the
# launch measures partly unoptimized code (the 100 KiB parse then ran 2-5x slow).
# DOTNET_TC_CallCountingDelayMs=0 starts tier-up as soon as a method reaches its
# call count instead of after a 100 ms quiet period; the steady-state code,
# including dynamic PGO, is unchanged. Both sides run with the same settings.
#
# Output layout, read by Test-MarkdownBenchmarkGates.ps1:
#   <OutputDirectory>\candidate\round-01\<method>\results\*-report-full-compressed.json
#   <OutputDirectory>\reference\round-01\<method>\results\*-report-full-compressed.json
[CmdletBinding()]
param(
    # The built candidate MarkdownRenderer.Benchmarks.dll.
    [Parameter(Mandatory)]
    [string] $CandidateAssembly,

    # The built reference MarkdownRenderer.Benchmarks.dll. Without it the
    # candidate runs alone, for the absolute budgets only.
    [string] $ReferenceAssembly,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    # Paired rounds. Each round contributes one candidate/reference ratio per benchmark.
    [ValidateRange(1, 100)]
    [int] $Rounds = 10,

    # Warmup iterations per benchmark case and round.
    [ValidateRange(1, 100)]
    [int] $WarmupCount = 5,

    # Measured iterations per benchmark case and round.
    [ValidateRange(1, 100)]
    [int] $IterationCount = 5,

    # Target duration of one iteration in milliseconds.
    [ValidateRange(10, 10000)]
    [int] $IterationTime = 500
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-Assembly([string] $Path, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label benchmark assembly '$Path' does not exist."
    }
    return [IO.Path]::GetFullPath($Path)
}

function Get-BenchmarkMethods([string] $Assembly, [string] $Label) {
    $output = @(& dotnet $Assembly --list flat 2>&1 | ForEach-Object { "$_".Trim() })
    if ($LASTEXITCODE -ne 0) {
        throw "$Label benchmark assembly could not list its benchmarks (exit $LASTEXITCODE):`n$($output -join "`n")"
    }
    $methods = @($output | Where-Object { $_ -match '^[A-Za-z_][\w.]*\.[A-Za-z_]\w*$' } | Sort-Object -Unique)
    if ($methods.Count -eq 0) {
        throw "$Label benchmark assembly listed no benchmarks:`n$($output -join "`n")"
    }
    return $methods
}

function Invoke-BenchmarkMethod([string] $Assembly, [string] $Method, [string] $Artifacts, [string] $Label) {
    [void] [IO.Directory]::CreateDirectory($Artifacts)
    & dotnet $Assembly `
        --inProcess `
        --job Short `
        --warmupCount $WarmupCount `
        --iterationCount $IterationCount `
        --iterationTime $IterationTime `
        --exporters JSON `
        --artifacts $Artifacts `
        --filter $Method
    if ($LASTEXITCODE -ne 0) {
        throw "$Label benchmark '$Method' failed with exit code $LASTEXITCODE."
    }
    $reports = @(Get-ChildItem -LiteralPath $Artifacts -Recurse -File -Filter '*-report-full-compressed.json')
    if ($reports.Count -eq 0) {
        throw "$Label benchmark '$Method' produced no JSON report in '$Artifacts'."
    }
}

# The gate reads every round directory under the output, so a report left from an
# earlier run would be compared as if it were this run's. Start from an empty folder.
if ((Test-Path -LiteralPath $OutputDirectory) -and
    @(Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1).Count -ne 0) {
    throw "Benchmark output directory '$OutputDirectory' is not empty; pass a fresh directory so no earlier report enters the comparison."
}
[void] [IO.Directory]::CreateDirectory($OutputDirectory)

$candidate = Resolve-Assembly $CandidateAssembly 'Candidate'
$candidateMethods = Get-BenchmarkMethods $candidate 'Candidate'
$reference = $null
$referenceMethods = @()
if (-not [string]::IsNullOrWhiteSpace($ReferenceAssembly)) {
    $reference = Resolve-Assembly $ReferenceAssembly 'Reference'
    $referenceMethods = Get-BenchmarkMethods $reference 'Reference'
}
else {
    # Without a reference there is nothing to pair, so one candidate round is enough
    # for the absolute budgets.
    $Rounds = 1
}

# Every method either side defines runs in every round. A method the candidate no
# longer defines is left unmeasured on that side, and the gate reports it as missing.
$methods = @(@($candidateMethods) + @($referenceMethods) | Sort-Object -Unique)
Write-Host "Benchmark methods: $($methods -join ', ')"
Write-Host "Rounds: $Rounds; reference: $(if ($null -ne $reference) { $reference } else { 'none' })"

$previousDelay = $env:DOTNET_TC_CallCountingDelayMs
$env:DOTNET_TC_CallCountingDelayMs = '0'
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        $roundName = 'round-{0:00}' -f $round
        for ($index = 0; $index -lt $methods.Count; $index++) {
            $method = $methods[$index]
            $methodFolder = '{0:00}-{1}' -f ($index + 1), $method.Substring($method.LastIndexOf('.', $method.LastIndexOf('.') - 1) + 1)
            $sides = [Collections.Generic.List[object]]::new()
            if ($candidateMethods -contains $method) {
                $sides.Add([pscustomobject]@{ Label = 'Candidate'; Assembly = $candidate; Folder = 'candidate' })
            }
            if ($null -ne $reference -and $referenceMethods -contains $method) {
                $sides.Add([pscustomobject]@{ Label = 'Reference'; Assembly = $reference; Folder = 'reference' })
            }
            # Alternate the order across rounds and methods so neither side is
            # systematically measured first.
            if ((($round + $index) % 2) -eq 1) {
                $sides.Reverse()
            }
            foreach ($side in $sides) {
                Write-Host "::group::$roundName $($side.Label) $method"
                Invoke-BenchmarkMethod $side.Assembly $method (Join-Path $OutputDirectory "$($side.Folder)\$roundName\$methodFolder") $side.Label
                Write-Host '::endgroup::'
            }
        }
    }
}
finally {
    $env:DOTNET_TC_CallCountingDelayMs = $previousDelay
}
