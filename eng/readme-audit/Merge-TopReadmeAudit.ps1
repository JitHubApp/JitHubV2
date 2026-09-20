[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    [ValidateRange(1, 500)]
    [int]$ExpectedCount = 500,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$caseFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter result.json |
    Where-Object { $_.FullName -match '[\\/]cases[\\/]' })
$cases = @($caseFiles | ForEach-Object {
    Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
})

$failures = [Collections.Generic.List[string]]::new()
if ($cases.Count -ne $ExpectedCount) {
    $failures.Add("Found $($cases.Count) case results; expected $ExpectedCount.")
}

$duplicateRanks = @($cases | Group-Object rank | Where-Object Count -ne 1)
foreach ($duplicate in $duplicateRanks) {
    $failures.Add("Rank $($duplicate.Name) appeared $($duplicate.Count) times.")
}

$expectedRanks = [Collections.Generic.HashSet[int]]::new([int[]](1..$ExpectedCount))
foreach ($case in $cases) { [void]$expectedRanks.Remove([int]$case.rank) }
if ($expectedRanks.Count -ne 0) {
    $failures.Add("Missing ranks: $(@($expectedRanks | Sort-Object) -join ', ').")
}

$orderedCases = @($cases | Sort-Object { [int]$_.rank })
$failedCases = @($orderedCases | Where-Object status -ne 'passed')
foreach ($case in $failedCases) {
    $detail = @($case.failures) -join '; '
    $failures.Add("Rank $($case.rank) $($case.fullName): $detail")
}

function Get-Percentile([double[]]$SortedValues, [double]$Percentile) {
    if ($SortedValues.Count -eq 0) { return 0.0 }
    $position = [Math]::Clamp($Percentile, 0.0, 1.0) * ($SortedValues.Count - 1)
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    if ($lower -eq $upper) { return $SortedValues[$lower] }
    return $SortedValues[$lower] + (($SortedValues[$upper] - $SortedValues[$lower]) * ($position - $lower))
}

$firstRatios = [double[]]@($orderedCases |
    Where-Object { $null -ne $_.comparison } |
    ForEach-Object { [double]$_.comparison.nativeToBrowserFirstRenderRatio } |
    Where-Object { [double]::IsFinite($_) } |
    Sort-Object)
$fullRatios = [double[]]@($orderedCases |
    Where-Object { $null -ne $_.comparison } |
    ForEach-Object { [double]$_.comparison.nativeToBrowserFullPageRatio } |
    Where-Object { [double]::IsFinite($_) } |
    Sort-Object)

# GitHub intentionally serves some extensionless README candidates as source
# files instead of rich Markdown. Those cases still run the crash, clean-exit,
# and unavailable-content gates, but they have no browser/native rendering
# ratio. Require ratios for every case where the browser did render a README;
# do not make a legitimate source-view parity case fail consolidation.
$expectedComparisonCount = @($orderedCases | Where-Object {
    $null -eq $_.browser.readmeRendered -or $_.browser.readmeRendered -ne $false
}).Count

$firstP50 = Get-Percentile $firstRatios 0.50
$firstP95 = Get-Percentile $firstRatios 0.95
$fullP50 = Get-Percentile $fullRatios 0.50
$fullP95 = Get-Percentile $fullRatios 0.95
if ($firstRatios.Count -ne $expectedComparisonCount) {
    $failures.Add(
        "Only $($firstRatios.Count)/$expectedComparisonCount rendered README cases supplied a finite first-render ratio.")
}
if ($fullRatios.Count -ne $expectedComparisonCount) {
    $failures.Add(
        "Only $($fullRatios.Count)/$expectedComparisonCount rendered README cases supplied a finite full-page ratio.")
}
if ($firstP95 -gt 1.10) {
    $failures.Add(("Native first-render p95 was {0:P1} of Edge, above 110%." -f $firstP95))
}
if ($fullP95 -gt 1.10) {
    $failures.Add(("Native full-page p95 was {0:P1} of Edge, above 110%." -f $fullP95))
}

$summary = [ordered]@{
    schemaVersion = 1
    expectedCases = $ExpectedCount
    totalCases = $orderedCases.Count
    passedCases = $orderedCases.Count - $failedCases.Count
    failedCases = $failedCases.Count
    nativeFirstRenderRatioP50 = $firstP50
    nativeFirstRenderRatioP95 = $firstP95
    nativeFullPageRatioP50 = $fullP50
    nativeFullPageRatioP95 = $fullP95
    failures = @($failures)
    passed = $failures.Count -eq 0
    completedAtUtc = [DateTimeOffset]::UtcNow
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8

$markdown = [Collections.Generic.List[string]]::new()
$markdown.Add("# Consolidated top-$ExpectedCount README rendering audit")
$markdown.Add('')
$markdown.Add("- Result: **$(if ($summary.passed) { 'PASS' } else { 'FAIL' })**")
$markdown.Add("- Cases: $($summary.passedCases) passed, $($summary.failedCases) failed, $($summary.totalCases)/$ExpectedCount reported")
$markdown.Add(("- Native/Edge first-render ratio: p50 {0:F3}, p95 {1:F3}" -f $firstP50, $firstP95))
$markdown.Add(("- Native/Edge full-page ratio: p50 {0:F3}, p95 {1:F3}" -f $fullP50, $fullP95))
$markdown.Add('')
$markdown.Add('| Rank | Repository | Result | Text | Structure | Unavailable | First ratio | Full ratio |')
$markdown.Add('| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |')
foreach ($case in $orderedCases) {
    $text = if ($null -eq $case.comparison) { 'n/a' } else { '{0:P2}' -f [double]$case.comparison.textTokenCoverage }
    $structure = if ($null -eq $case.comparison) { 'n/a' } else { '{0:P2}' -f [double]$case.comparison.visualStructureScore }
    $unavailable = if ($null -eq $case.native) { 'n/a' } else { [string]$case.native.unavailableImages }
    $first = if ($null -eq $case.comparison) { 'n/a' } else { '{0:F3}' -f [double]$case.comparison.nativeToBrowserFirstRenderRatio }
    $full = if ($null -eq $case.comparison) { 'n/a' } else { '{0:F3}' -f [double]$case.comparison.nativeToBrowserFullPageRatio }
    $markdown.Add("| $($case.rank) | $($case.fullName) | $($case.status) | $text | $structure | $unavailable | $first | $full |")
}
if ($failures.Count -ne 0) {
    $markdown.Add('')
    $markdown.Add('## Failures')
    foreach ($failure in $failures) { $markdown.Add("- $failure") }
}
$markdown | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.md') -Encoding utf8

if (-not $summary.passed) {
    throw "Consolidated top-$ExpectedCount README audit failed. See '$OutputDirectory\summary.md'."
}

Write-Host "Consolidated top-$ExpectedCount README audit passed: '$OutputDirectory\summary.md'."
