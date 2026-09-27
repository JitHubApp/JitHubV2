$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Merge-TopReadmeAudit.ps1'
$workflowPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.github\workflows\markdown-readme-top500.yml'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "jithub-top500-merge-tests-$([Guid]::NewGuid().ToString('N'))"
$resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
$expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedTempRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create merge fixtures outside the temporary directory.'
}
New-Item -ItemType Directory -Path $resolvedTempRoot | Out-Null

function New-Case([int]$Rank, [bool]$IncludeSameByte = $true, [bool]$AbsentReadme = $false, [double]$FirstRatio = 0.5, [double]$FullRatio = 0.6, [int]$UnavailableImages = 0) {
    $readmeSha = if ($AbsentReadme) { '' } else { 'b' * 40 }
    $sameByte = $null
    if ($IncludeSameByte -and -not $AbsentReadme) {
        $sameByte = [ordered]@{
            manifest = ('same-byte-corpus-' + ('c' * 32) + '/manifest.json')
            manifestSha256 = 'a' * 64
            readmeBytes = 10
            assetCount = 1
            assetBytes = 16
        }
    }
    $comparison = if ($AbsentReadme) { $null } else { [ordered]@{
        nativeToBrowserFirstRenderRatio = $FirstRatio
        nativeToBrowserFullPageRatio = $FullRatio
    } }
    return [ordered]@{
        schemaVersion = 4
        rank = $Rank
        fullName = "example/repository-$Rank"
        readmeSha = $readmeSha
        status = 'passed'
        failures = @()
        browser = [ordered]@{
            readmeSha = $readmeSha
            readmeRendered = -not $AbsentReadme
            sameByteCorpus = $sameByte
            semantic = [ordered]@{ unavailableImages = 0 }
        }
        native = [ordered]@{
            cleanExit = $true
            unavailableImages = $UnavailableImages
            loadingImagesAfterTraversal = 0
            renderFailure = $null
        }
        comparison = $comparison
    }
}

function Write-Cases([string]$Root, [int]$Count, [scriptblock]$Factory) {
    for ($rank = 1; $rank -le $Count; $rank++) {
        $case = & $Factory $rank
        $caseDirectory = Join-Path $Root ("cases\{0:D3}-example-repository-{0}" -f $rank)
        New-Item -ItemType Directory -Force -Path $caseDirectory | Out-Null
        $case | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $caseDirectory 'result.json') -Encoding utf8
    }
}

function Invoke-Merge([string]$Root, [string]$Output, [int]$Count, [switch]$RequireSameByte) {
    $arguments = @{
        EvidenceRoot = $Root
        OutputDirectory = $Output
        ExpectedCount = $Count
    }
    if ($RequireSameByte) { $arguments.RequireSameByteCorpus = $true }
    & $scriptPath @arguments | Out-Null
}

try {
    $baseline = Join-Path $resolvedTempRoot 'baseline'
    Write-Cases $baseline 2 { param($rank) New-Case $rank -AbsentReadme:($rank -eq 2) }
    Invoke-Merge $baseline (Join-Path $resolvedTempRoot 'baseline-output') 2

    $positive = Join-Path $resolvedTempRoot 'positive-same-byte'
    Write-Cases $positive 500 { param($rank) New-Case $rank -AbsentReadme:($rank -eq 17) }
    Invoke-Merge $positive (Join-Path $resolvedTempRoot 'positive-same-byte-output') 500 -RequireSameByte
    $positiveSummary = Get-Content -LiteralPath (Join-Path $resolvedTempRoot 'positive-same-byte-output\summary.json') -Raw | ConvertFrom-Json
    if (-not $positiveSummary.passed -or $positiveSummary.sameByteCases -ne 499 -or $positiveSummary.sameByteExpectedCases -ne 499) {
        throw 'The complete same-byte release merge did not pass with one legitimate absent-README case.'
    }

    $mixed = Join-Path $resolvedTempRoot 'mixed'
    Write-Cases $mixed 500 { param($rank) New-Case $rank -IncludeSameByte:($rank -ne 2) }
    $mixedRejected = $false
    try {
        Invoke-Merge $mixed (Join-Path $resolvedTempRoot 'mixed-output') 500 -RequireSameByte
    }
    catch {
        $mixedSummary = Get-Content -LiteralPath (Join-Path $resolvedTempRoot 'mixed-output\summary.json') -Raw | ConvertFrom-Json
        $mixedRejected = (@($mixedSummary.failures) -join "`n") -match 'missing valid same-byte capture evidence'
    }
    if (-not $mixedRejected) { throw 'The release merge did not reject missing/mixed same-byte case evidence.' }

    $unclean = Join-Path $resolvedTempRoot 'unclean'
    Write-Cases $unclean 2 { param($rank) New-Case $rank -UnavailableImages 1 }
    $uncleanRejected = $false
    try { Invoke-Merge $unclean (Join-Path $resolvedTempRoot 'unclean-output') 2 }
    catch {
        $uncleanSummary = Get-Content -LiteralPath (Join-Path $resolvedTempRoot 'unclean-output\summary.json') -Raw | ConvertFrom-Json
        $uncleanRejected = (@($uncleanSummary.failures) -join "`n") -match 'zero unavailable native images'
    }
    if (-not $uncleanRejected) { throw 'The merge did not reject a passing case with unavailable native images.' }

    $sentinel = Join-Path $resolvedTempRoot 'sentinel'
    Write-Cases $sentinel 500 {
        param($rank)
        if ($rank -eq 60) { New-Case $rank -FirstRatio 1.11 -FullRatio 0.8 }
        elseif ($rank -eq 203) { New-Case $rank -FirstRatio 0.8 -FullRatio 1.11 }
        else { New-Case $rank }
    }
    $sentinelRejected = $false
    try {
        Invoke-Merge $sentinel (Join-Path $resolvedTempRoot 'sentinel-output') 500 -RequireSameByte
    }
    catch {
        $sentinelSummary = Get-Content -LiteralPath (Join-Path $resolvedTempRoot 'sentinel-output\summary.json') -Raw | ConvertFrom-Json
        $sentinelFailures = @($sentinelSummary.failures) -join "`n"
        $sentinelRejected = $sentinelFailures -match 'sentinel rank 60' -and $sentinelFailures -match 'sentinel rank 203'
    }
    if (-not $sentinelRejected) { throw 'The same-byte release merge did not enforce both performance sentinels.' }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    foreach ($requiredText in @(
        'same_byte_release:',
        'CaptureSameByteCorpus = $true',
        'RequireSameByteCorpus = $true',
        '!${{ runner.temp }}/readme-audit/cases/**/browser/same-byte-corpus-*/readme.md',
        '!${{ runner.temp }}/readme-audit/cases/**/browser/same-byte-corpus-*/assets/**')) {
        if (-not $workflow.Contains($requiredText)) {
            throw "The top-500 workflow is missing its same-byte release contract: $requiredText"
        }
    }

    Write-Host 'Top-500 merge tests passed: absent-README handling, a positive 500-case same-byte verdict, mixed same-byte rejection, unavailable-image rejection, outlier sentinels, and artifact/workflow opt-in.'
}
finally {
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        $actual = [IO.Path]::GetFullPath($resolvedTempRoot)
        if (-not $actual.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Temporary test path changed unexpectedly; preserving it for inspection.'
        }
        Remove-Item -LiteralPath $actual -Recurse -Force
    }
}
