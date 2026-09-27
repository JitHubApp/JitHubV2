[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    [ValidateRange(1, 500)]
    [int]$ExpectedCount = 500,

    [switch]$RequireSameByteCorpus,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
if ($RequireSameByteCorpus -and $ExpectedCount -ne 500) {
    throw "A same-byte release verdict requires all 500 pinned repository cases."
}
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

# A complete result set also has to prove that every native process exited
# cleanly and every image resolved. Do not let a malformed or hand-assembled
# case file report `passed` while omitting those release invariants.
foreach ($case in $orderedCases) {
    $rank = [int]$case.rank
    if ($null -eq $case.native) {
        $failures.Add("Rank $rank has no native result.")
        continue
    }
    if ($case.native.cleanExit -ne $true) {
        $failures.Add("Rank $rank did not record a clean native process exit.")
    }
    if ($null -eq $case.native.unavailableImages -or [int]$case.native.unavailableImages -ne 0) {
        $failures.Add("Rank $rank did not record zero unavailable native images.")
    }
    if ($null -eq $case.native.loadingImagesAfterTraversal -or [int]$case.native.loadingImagesAfterTraversal -ne 0) {
        $failures.Add("Rank $rank did not record zero loading native images after traversal.")
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$case.native.renderFailure)) {
        $failures.Add("Rank $rank recorded a native render exception.")
    }
}

$sameByteCases = 0
if ($RequireSameByteCorpus) {
    foreach ($case in $orderedCases) {
        if ([string]::IsNullOrWhiteSpace([string]$case.readmeSha)) {
            if ($null -ne $case.browser.sameByteCorpus) {
                $failures.Add("Rank $($case.rank) has same-byte evidence despite having no pinned README.")
            }
            continue
        }

        $sameByte = $case.browser.sameByteCorpus
        $manifestPath = if ($null -eq $sameByte) { '' } else { [string]$sameByte.manifest }
        if ($null -eq $sameByte -or
            [string]$case.browser.readmeSha -ne [string]$case.readmeSha -or
            [string]$sameByte.manifestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $manifestPath -notmatch '^same-byte-corpus-[0-9a-fA-F]{32}[\\/]manifest\.json$' -or
            $null -eq $sameByte.readmeBytes -or [long]$sameByte.readmeBytes -lt 0 -or [long]$sameByte.readmeBytes -gt 16MB -or
            $null -eq $sameByte.assetCount -or [int]$sameByte.assetCount -lt 0 -or [int]$sameByte.assetCount -gt 15000 -or
            $null -eq $sameByte.assetBytes -or [long]$sameByte.assetBytes -lt 0 -or [long]$sameByte.assetBytes -gt 256MB) {
            $failures.Add("Rank $($case.rank) is missing valid same-byte capture evidence for its pinned README.")
            continue
        }
        $sameByteCases++
    }

    $expectedSameByteCases = @($orderedCases | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.readmeSha) }).Count
    if ($sameByteCases -ne $expectedSameByteCases) {
        $failures.Add("Only $sameByteCases/$expectedSameByteCases available README cases have same-byte capture evidence.")
    }

    # Keep known high-cost ranks visible in every release run. Aggregate p95
    # can hide a single slow page, so the full same-byte qualification also
    # checks these previously observed outliers individually against the same
    # 110% Edge ceiling.
    foreach ($sentinelRank in @(60, 203)) {
        $case = $orderedCases | Where-Object { [int]$_.rank -eq $sentinelRank } | Select-Object -First 1
        if ($null -eq $case) {
            $failures.Add("Same-byte release evidence is missing performance sentinel rank $sentinelRank.")
            continue
        }
        foreach ($metric in @('nativeToBrowserFirstRenderRatio', 'nativeToBrowserFullPageRatio')) {
            $ratio = $case.comparison.$metric
            if ($null -eq $ratio -or -not [double]::IsFinite([double]$ratio) -or [double]$ratio -gt 1.10) {
                $failures.Add(("Same-byte release sentinel rank {0} {1} was {2}, above 110% of Edge or missing." -f
                    $sentinelRank, $metric, $ratio))
            }
        }
    }
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
    sameByteRequired = [bool]$RequireSameByteCorpus
    sameByteCases = $sameByteCases
    sameByteExpectedCases = if ($RequireSameByteCorpus) { @($orderedCases | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.readmeSha) }).Count } else { 0 }
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
$markdown.Add("- Same-byte captures: $($summary.sameByteCases)/$($summary.sameByteExpectedCases) required README cases")
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
