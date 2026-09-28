$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Merge-TopReadmeAudit.ps1'
$workflowPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.github\workflows\markdown-readme-top500.yml'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "jithub-top500-merge-tests-$([Guid]::NewGuid().ToString('N'))"
$resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
$screenshotEvidenceScript = Join-Path $PSScriptRoot 'Get-TopReadmeScreenshotEvidence.ps1'
$expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedTempRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create merge fixtures outside the temporary directory.'
}
New-Item -ItemType Directory -Path $resolvedTempRoot | Out-Null

function New-Case([int]$Rank, [bool]$IncludeSameByte = $true, [bool]$AbsentReadme = $false, [bool]$SourceView = $false, [double]$FirstRatio = 0.5, [double]$FullRatio = 0.6, [int]$UnavailableImages = 0) {
    $readmeSha = if ($AbsentReadme) { '' } else { 'b' * 40 }
    $readmeSha256 = 'd' * 64
    $renderedSha256 = 'e' * 64
    $assetMapSha256 = '4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    $sameByte = $null
    if ($IncludeSameByte -and -not $AbsentReadme) {
        $sameByte = [ordered]@{
            manifest = ('same-byte-corpus-' + ('c' * 32) + '/manifest.json')
            manifestSha256 = ''
            readmeSha256 = $readmeSha256
            readmeBytes = 10
            assetCount = 0
            assetBytes = 0
        }
    }
    $comparison = if ($AbsentReadme -or $SourceView) { $null } else { [ordered]@{
        fidelityReference = 'same-byte-source'
        textTokenCoverage = 1.0
        visualStructureScore = 1.0
        nativeToBrowserFirstRenderRatio = $FirstRatio
        nativeToBrowserFullPageRatio = $FullRatio
        nativeToSameByteFirstRenderRatio = $FirstRatio
        nativeToSameByteFullPageRatio = $FullRatio
        sourceBoundLayoutExtentRatio = 1.0
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
            readmeRendered = -not ($AbsentReadme -or $SourceView)
            sameByteCorpus = $sameByte
            sameByteReplay = if ($AbsentReadme) { $null } elseif ($SourceView) { [ordered]@{
                schemaVersion = 2
                status = 'not-applicable'
                reason = 'github-source-view'
            } } else { [ordered]@{
                schemaVersion = 3
                status = 'passed'
                source = [ordered]@{
                    readmeGitBlobSha1 = $readmeSha
                    readmeSha256 = $readmeSha256
                    byteSize = 10
                }
                parser = [ordered]@{
                    name = 'marked'
                    version = '18.0.5'
                    license = 'MIT'
                    sha256 = '2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b'
                }
                viewport = [ordered]@{
                    width = 623
                    height = 700
                    deviceScaleFactor = 1
                    colorScheme = 'light'
                    edgeInnerWidth = 623
                    edgeInnerHeight = 700
                    edgeDeviceScaleFactor = 1
                }
                renderedExtent = [ordered]@{ width = 623; height = 700 }
                assets = [ordered]@{
                    assetUrlMapSha256 = $assetMapSha256
                    expectedImageCount = 0
                    verifiedImageCount = 0
                    distinctExpectedUrlHashes = 0
                    distinctServedUrlHashes = 0
                    replayMissCount = 0
                    blockedExternalRequestCount = 0
                }
                timing = [ordered]@{
                    firstViewportPaintMs = 20
                    firstViewportImagesReadyMs = 100
                    fullTraversalMs = 210
                    chargedTraversalMs = 200
                    auditOnlyFrameWaitMs = 10
                    auditOnlyFrameWaitCount = 2
                }
                semantic = [ordered]@{
                    complete = $true
                    incompleteReason = ''
                    tokenizationVersion = 'rune-l-n-mn-mc-han-nfc-simple-lower-invariant-v1'
                    digestKeySha256 = 'f' * 64
                    visibleTextTokenCount = 3
                    visibleTextTokenDigestCount = 2
                    sourceReplayToCapturedGitHubVisibleTextTokenCoverage = 1.0
                    capturedGitHubToSourceReplayVisibleTextTokenCoverage = 1.0
                    capturedGitHubSourceStructureScore = 1.0
                    width = 623
                    height = 700
                    headingCount = 1
                    distinctLinkCount = 0
                    imageCount = 0
                    distinctImageCount = 0
                    mediaCount = 0
                    distinctMediaCount = 0
                    tableCount = 0
                    codeBlockCount = 0
                    taskCheckboxCount = 0
                    detailsCount = 0
                    visibleMermaidSourceCount = 0
                }
                tiles = @([ordered]@{ index = 0; relativeY = 0; width = 623; height = 700; file = 'same-byte-source-tile-0000.png' })
            } }
            sameByteHtmlReplay = if ($AbsentReadme -or $SourceView) { $null } else { [ordered]@{
                schemaVersion = 1
                status = 'passed'
                renderedHtmlSha256 = $renderedSha256
            } }
            semantic = [ordered]@{
                unavailableImages = 0
                headings = @([ordered]@{ level = 1; text = '' })
                tables = 0
                taskCheckboxes = 0
                details = 0
            }
        }
        native = [ordered]@{
            cleanExit = $true
            unavailableImages = $UnavailableImages
            loadingImagesAfterTraversal = 0
            renderFailure = $null
            readmeSourceSha256 = if ($AbsentReadme) { $null } else { $readmeSha256 }
            firstViewportImagesReadyMs = 100 * $FirstRatio
            fullTraversalMs = 200 * $FullRatio
            contentViewportWidth = 623
            contentViewportHeight = 700
            estimatedContentHeight = 700
            rasterizationScale = 1
        }
        comparison = $comparison
    }
}

function Write-Cases([string]$Root, [int]$Count, [scriptblock]$Factory) {
    $pinnedRepositories = [Collections.Generic.List[object]]::new()
    for ($rank = 1; $rank -le $Count; $rank++) {
        $case = & $Factory $rank
        $hasReadme = -not [string]::IsNullOrWhiteSpace([string]$case.readmeSha)
        $pinnedRepositories.Add([ordered]@{
            rank = $rank
            id = $rank
            fullName = $case.fullName
            defaultBranch = 'main'
            commitSha = 'a' * 40
            url = "https://github.com/$($case.fullName)"
            pushedAtUtc = '2026-01-01T00:00:00Z'
            readme = [ordered]@{
                available = $hasReadme
                byteSize = if ($hasReadme) { 10 } else { 0 }
                path = if ($hasReadme) { 'README.md' } else { '' }
                sha = if ($hasReadme) { $case.readmeSha } else { '' }
                htmlUrl = if ($hasReadme) { "https://github.com/$($case.fullName)/blob/main/README.md" } else { '' }
                downloadUrl = if ($hasReadme) { "https://raw.githubusercontent.com/$($case.fullName)/main/README.md" } else { '' }
            }
        })
        $caseDirectory = Join-Path $Root ("cases\{0:D3}-example-repository-{0}" -f $rank)
        New-Item -ItemType Directory -Force -Path $caseDirectory | Out-Null
        if ($null -ne $case.browser.sameByteCorpus) {
            $manifestPath = Join-Path (Join-Path $caseDirectory 'browser') $case.browser.sameByteCorpus.manifest
            $manifestDirectory = Split-Path -Parent $manifestPath
            New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null
            $manifest = [ordered]@{
                schemaVersion = 2
                complete = $true
                repository = [ordered]@{
                    fullName = $case.fullName
                    commitSha = 'a' * 40
                    readmePath = 'README.md'
                    readmeGitBlobSha1 = $case.readmeSha
                }
                readme = [ordered]@{ file = 'readme.md'; byteSize = 10; sha256 = 'd' * 64 }
                browserRender = if ($case.browser.readmeRendered -eq $false) { [ordered]@{
                    status = 'not-applicable'
                    reason = 'github-source-view'
                } } else { [ordered]@{
                    status = 'passed'
                    file = 'rendered.html'
                    byteSize = 20
                    sha256 = 'e' * 64
                } }
                assets = @()
            }
            $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
            $case.browser.sameByteCorpus.manifestSha256 =
                (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        }
        $case | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $caseDirectory 'result.json') -Encoding utf8
    }

    $pinnedManifest = [ordered]@{
        schemaVersion = 2
        generatedAtUtc = '2026-01-01T00:00:00Z'
        source = 'GitHub REST API'
        query = 'fixture'
        requestedCount = $Count
        repositories = @($pinnedRepositories)
    }
    $pinnedManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Root 'top-repositories.json') -Encoding utf8

    for ($startRank = 1; $startRank -le $Count; $startRank += 25) {
        $endRank = [Math]::Min($Count, $startRank + 24)
        $executableHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes("fixture-app-$startRank"))).ToLowerInvariant()
        $appAssemblyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes("fixture-app-assembly-$startRank"))).ToLowerInvariant()
        $rendererAssemblyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes("fixture-renderer-assembly-$startRank"))).ToLowerInvariant()
        $build = [ordered]@{
            schemaVersion = 1
            startRank = $startRank
            endRank = $endRank
            sourceCommitSha = '1' * 40
            configuration = 'Release'
            executableName = 'JitHub.WinUI.exe'
            executableSha256 = $executableHash
            dependencyAssemblySha256 = [ordered]@{
                'JitHub.WinUI.dll' = $appAssemblyHash
                'MarkdownRenderer.dll' = $rendererAssemblyHash
            }
        }
        $build | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Root ("app-binary-ranks-{0}-{1}.json" -f $startRank, $endRank)) -Encoding utf8
        $screenshotQuota = [ordered]@{
            schemaVersion = 1
            startRank = $startRank
            endRank = $endRank
            status = 'passed'
            screenshotCount = 2
            screenshotBytes = 100
            shardQuotaBytes = 1073741824
            caseTileValidationStatus = 'passed'
            caseTileValidationFailureCount = 0
            caseTileValidationFailures = @()
        }
        $screenshotQuota | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Root ("screenshot-quota-ranks-{0}-{1}.json" -f $startRank, $endRank)) -Encoding utf8
    }
}

function Invoke-Merge([string]$Root, [string]$Output, [int]$Count, [switch]$RequireSameByte, [string]$PinnedPath = '') {
    $arguments = @{
        EvidenceRoot = $Root
        OutputDirectory = $Output
        ExpectedCount = $Count
        PinnedManifestPath = if ([string]::IsNullOrWhiteSpace($PinnedPath)) { Join-Path $Root 'top-repositories.json' } else { $PinnedPath }
    }
    if ($RequireSameByte) { $arguments.RequireSameByteCorpus = $true }
    & $scriptPath @arguments | Out-Null
}

function Assert-RejectedMutation(
    [string]$Root,
    [int]$Rank,
    [string]$Name,
    [scriptblock]$Mutation,
    [string]$ExpectedFailure) {
    $casePath = Join-Path $Root ("cases\{0:D3}-example-repository-{0}\result.json" -f $Rank)
    $original = Get-Content -LiteralPath $casePath -Raw
    try {
        $case = $original | ConvertFrom-Json
        & $Mutation $case
        $case | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $casePath -Encoding utf8
        $output = Join-Path $resolvedTempRoot $Name
        $rejected = $false
        try { Invoke-Merge $Root $output 500 -RequireSameByte }
        catch {
            $summary = Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw | ConvertFrom-Json
            $rejected = (@($summary.failures) -join "`n") -match $ExpectedFailure
        }
        if (-not $rejected) { throw "Same-byte merge accepted or misclassified mutation $Name." }
    }
    finally {
        Set-Content -LiteralPath $casePath -Value $original -NoNewline -Encoding utf8
    }
}

function Assert-RejectedManifestMutation(
    [string]$Root,
    [int]$Rank,
    [string]$Name,
    [scriptblock]$Mutation) {
    $casePath = Join-Path $Root ("cases\{0:D3}-example-repository-{0}\result.json" -f $Rank)
    $caseBytes = [IO.File]::ReadAllBytes($casePath)
    $case = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
    $manifestPath = Join-Path (Join-Path (Split-Path -Parent $casePath) 'browser') $case.browser.sameByteCorpus.manifest
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        & $Mutation $case $manifest
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        $case.browser.sameByteCorpus.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        $case | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $casePath -Encoding utf8
        $output = Join-Path $resolvedTempRoot $Name
        $rejected = $false
        try { Invoke-Merge $Root $output 500 -RequireSameByte }
        catch {
            $summary = Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw | ConvertFrom-Json
            $rejected = (@($summary.failures) -join "`n") -match 'malformed or mismatched same-byte manifest artifact'
        }
        if (-not $rejected) { throw "The same-byte merge accepted an invalid pinned README manifest identity in '$Name'." }
    }
    finally {
        [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
        [IO.File]::WriteAllBytes($casePath, $caseBytes)
    }
}

function Assert-RejectedBuildEvidenceMutation(
    [string]$Root,
    [string]$FileName,
    [scriptblock]$Mutation,
    [string]$ExpectedFailure) {
    $buildPath = Join-Path $Root $FileName
    $originalBytes = [IO.File]::ReadAllBytes($buildPath)
    try {
        $build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
        & $Mutation $build
        $build | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $buildPath -Encoding utf8
        $output = Join-Path $resolvedTempRoot ("build-evidence-{0}" -f [Guid]::NewGuid().ToString('N'))
        $rejected = $false
        try { Invoke-Merge $Root $output 500 -RequireSameByte }
        catch {
            $summary = Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw | ConvertFrom-Json
            $rejected = (@($summary.failures) -join "`n") -match $ExpectedFailure
        }
        if (-not $rejected) { throw "The same-byte merge accepted invalid build evidence in '$FileName'." }
    }
    finally {
        [IO.File]::WriteAllBytes($buildPath, $originalBytes)
    }
}

try {
    . $screenshotEvidenceScript
    $screenshotFilterRoot = Join-Path $resolvedTempRoot 'screenshot-path-filter'
    $sourceAsset = Join-Path $screenshotFilterRoot 'cases\001-fixture\browser\same-byte-corpus-fixture\assets\large-source-image.png'
    $liveEdgeTile = Join-Path $screenshotFilterRoot 'cases\001-fixture\browser\tile-0000.png'
    $htmlEdgeTile = Join-Path $screenshotFilterRoot 'cases\001-fixture\browser\same-byte-tile-0000.png'
    $sourceEdgeTile = Join-Path $screenshotFilterRoot 'cases\001-fixture\browser\source-replay\same-byte-source-tile-0000.png'
    $nativeTile = Join-Path $screenshotFilterRoot 'cases\001-fixture\native\tile-0000.png'
    foreach ($path in @($sourceAsset, $liveEdgeTile, $htmlEdgeTile, $sourceEdgeTile, $nativeTile)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    }
    [IO.File]::WriteAllBytes($sourceAsset, [byte[]](1..100))
    [IO.File]::WriteAllBytes($liveEdgeTile, [byte[]](1..5))
    [IO.File]::WriteAllBytes($htmlEdgeTile, [byte[]](1..4))
    [IO.File]::WriteAllBytes($sourceEdgeTile, [byte[]](1..3))
    [IO.File]::WriteAllBytes($nativeTile, [byte[]](1..6))
    $screenshotFixtureCase = [ordered]@{
        schemaVersion = 4
        rank = 1
        readmeSha = 'b' * 40
        browser = [ordered]@{
            readmeRendered = $true
            tiles = @([ordered]@{ index = 0; file = 'tile-0000.png' })
            sameByteHtmlReplay = [ordered]@{ tiles = @([ordered]@{ index = 0; file = 'same-byte-tile-0000.png' }) }
            sameByteReplay = [ordered]@{ tiles = @([ordered]@{ index = 0; file = 'same-byte-source-tile-0000.png' }) }
        }
        native = [ordered]@{ tiles = @([ordered]@{ index = 0; file = 'tile-0000.png' }) }
    }
    $caseDirectory = Join-Path $screenshotFilterRoot 'cases\001-fixture'
    $screenshotFixtureCase | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $caseDirectory 'result.json') -Encoding utf8
    $screenshotPathEvidence = Get-TopReadmeScreenshotEvidence -AuditRoot $screenshotFilterRoot -RequireSameByteTiles
    if ($screenshotPathEvidence.screenshotCount -ne 4 -or $screenshotPathEvidence.screenshotBytes -ne 18 -or
        $screenshotPathEvidence.caseTileValidationStatus -ne 'passed') {
        throw 'Screenshot quota measurement counted captured source assets or missed an expected nonempty browser/native tile file.'
    }
    [IO.File]::Delete($sourceEdgeTile)
    $missingTileEvidence = Get-TopReadmeScreenshotEvidence -AuditRoot $screenshotFilterRoot -RequireSameByteTiles
    if ($missingTileEvidence.caseTileValidationStatus -ne 'failed' -or
        $missingTileEvidence.caseTileValidationFailureCount -lt 1) {
        throw 'Screenshot evidence validation accepted a result whose source-bound Edge tile file was missing.'
    }

    $baseline = Join-Path $resolvedTempRoot 'baseline'
    Write-Cases $baseline 2 { param($rank) New-Case $rank -AbsentReadme:($rank -eq 2) }
    Invoke-Merge $baseline (Join-Path $resolvedTempRoot 'baseline-output') 2

    $positive = Join-Path $resolvedTempRoot 'positive-same-byte'
    Write-Cases $positive 500 { param($rank) New-Case $rank -AbsentReadme:($rank -eq 17) -SourceView:($rank -eq 18) }
    $positiveOutput = Join-Path $resolvedTempRoot 'positive-same-byte-output'
    try { Invoke-Merge $positive $positiveOutput 500 -RequireSameByte }
    catch {
        $failedSummary = Get-Content -LiteralPath (Join-Path $positiveOutput 'summary.json') -Raw | ConvertFrom-Json
        throw ("Positive same-byte merge fixture failed: {0}" -f (@($failedSummary.failures) -join '; '))
    }
    $positiveSummary = Get-Content -LiteralPath (Join-Path $positiveOutput 'summary.json') -Raw | ConvertFrom-Json
    if (-not $positiveSummary.passed -or $positiveSummary.sameByteCases -ne 499 -or $positiveSummary.sameByteExpectedCases -ne 499) {
        throw 'The complete same-byte release merge did not pass with legitimate absent-README and source-view cases.'
    }
    if (@($positiveSummary.buildEvidence).Count -ne 20 -or
        @($positiveSummary.buildEvidence | Select-Object -ExpandProperty executableSha256 -Unique).Count -ne 20 -or
        @($positiveSummary.buildEvidence | Select-Object -ExpandProperty sourceCommitSha -Unique).Count -ne 1) {
        throw 'The release merge did not preserve distinct per-shard binary hashes under one source commit.'
    }
    if ($positiveSummary.screenshotEvidenceBytes -ne 2000 -or
        $positiveSummary.screenshotAggregateQuotaBytes -ne 8589934592 -or
        @($positiveSummary.screenshotEvidence).Count -ne 20) {
        throw 'The release merge did not validate and report all shard screenshot quota evidence.'
    }
    if ([string]$positiveSummary.pinnedCorpusSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'The release summary did not retain the validated pinned corpus digest.'
    }
    if ([Math]::Abs([double]$positiveSummary.nativeFirstRenderRatioP50 - 0.5) -gt 0.000001 -or
        [Math]::Abs([double]$positiveSummary.nativeFullPageRatioP50 - 0.6) -gt 0.000001) {
        throw 'The complete same-byte release summary did not use ratios recomputed from the fixture timing fields.'
    }
    $positiveMarkdown = Get-Content -LiteralPath (Join-Path $resolvedTempRoot 'positive-same-byte-output\summary.md') -Raw
    if (-not $positiveMarkdown.Contains('| Native/source | GitHub/source |')) {
        throw 'The same-byte summary does not distinguish native/source from GitHub/source structural fidelity.'
    }

    Assert-RejectedMutation $positive 2 'wrong-native-readme' {
        param($case)
        $case.native.readmeSourceSha256 = '0' * 64
    } 'did not render the captured README source bytes'
    Assert-RejectedMutation $positive 2 'claimed-absent-pinned-readme' {
        param($case)
        $case.readmeSha = ''
        $case.browser.readmeSha = ''
        $case.browser.readmeRendered = $false
        $case.browser.sameByteCorpus = $null
        $case.browser.sameByteReplay = $null
        $case.comparison = $null
    } 'result identity does not match the pinned repository, commit, or README identity'
    Assert-RejectedMutation $positive 2 'wrong-pinned-repository-name' {
        param($case)
        $case.fullName = 'example/not-the-pinned-repository'
    } 'result identity does not match the pinned repository, commit, or README identity'
    Assert-RejectedMutation $positive 2 'wrong-pinned-readme-blob' {
        param($case)
        $case.readmeSha = 'c' * 40
        $case.browser.readmeSha = 'c' * 40
    } 'result identity does not match the pinned repository, commit, or README identity'
    Assert-RejectedMutation $positive 2 'rank-outside-pinned-corpus' {
        param($case)
        $case.rank = 501
    } 'Rank 501 cannot be matched to a pinned corpus identity'
    Assert-RejectedMutation $positive 3 'missing-offline-replay' {
        param($case)
        $case.browser.sameByteReplay = $null
    } 'missing a qualified offline Edge replay'
    Assert-RejectedMutation $positive 3 'timing-only-schema-two-replay' {
        param($case)
        $case.browser.sameByteReplay.schemaVersion = 2
        $case.browser.sameByteReplay.PSObject.Properties.Remove('semantic')
        $case.comparison.fidelityReference = 'live-github-diagnostic'
    } 'missing a qualified offline Edge replay'
    Assert-RejectedMutation $positive 3 'incomplete-source-semantic-proof' {
        param($case)
        $case.browser.sameByteReplay.semantic.complete = $false
    } 'incomplete source semantic fidelity evidence'
    Assert-RejectedMutation $positive 3 'unknown-source-tokenization' {
        param($case)
        $case.browser.sameByteReplay.semantic.tokenizationVersion = 'unknown'
    } 'incomplete source semantic fidelity evidence'
    Assert-RejectedMutation $positive 3 'live-only-source-comparison' {
        param($case)
        $case.comparison.fidelityReference = 'live-github-diagnostic'
    } 'incomplete source semantic fidelity evidence'
    Assert-RejectedMutation $positive 3 'source-token-under-covers-captured-github-article' {
        param($case)
        $case.browser.sameByteReplay.semantic.sourceReplayToCapturedGitHubVisibleTextTokenCoverage = 0.9849
    } 'incomplete source semantic fidelity evidence'
    Assert-RejectedMutation $positive 3 'captured-github-article-under-covers-source-tokens' {
        param($case)
        $case.browser.sameByteReplay.semantic.capturedGitHubToSourceReplayVisibleTextTokenCoverage = 0.9849
    } 'incomplete source semantic fidelity evidence'
    Assert-RejectedMutation $positive 3 'github-table-source-paragraph-regression' {
        param($case)
        # Captured GitHub has one authored table; the source replay and native
        # renderer both have a paragraph. Text fidelity and native/source
        # structure still claim a pass, so the separate GitHub/source score
        # must reject this fixture.
        $case.browser.semantic.tables = 1
        $case.browser.sameByteReplay.semantic.tableCount = 0
        $case.browser.sameByteReplay.semantic.capturedGitHubSourceStructureScore = 0.75
        $case.comparison.textTokenCoverage = 1.0
        $case.comparison.visualStructureScore = 1.0
    } 'captured GitHub/source stable-structure score'
    Assert-RejectedMutation $positive 3 'below-captured-github-source-structure-floor' {
        param($case)
        $case.browser.sameByteReplay.semantic.capturedGitHubSourceStructureScore = 0.9499
    } 'captured GitHub/source stable-structure score'
    Assert-RejectedMutation $positive 3 'missing-captured-github-source-structure-score' {
        param($case)
        $case.browser.sameByteReplay.semantic.PSObject.Properties.Remove('capturedGitHubSourceStructureScore')
    } 'captured GitHub/source stable-structure score'
    Assert-RejectedMutation $positive 3 'non-finite-captured-github-source-structure-score' {
        param($case)
        $case.browser.sameByteReplay.semantic.capturedGitHubSourceStructureScore = [double]::NaN
    } 'captured GitHub/source stable-structure score'
    Assert-RejectedMutation $positive 3 'below-source-text-token-coverage-floor' {
        param($case)
        $case.comparison.textTokenCoverage = 0.9849
    } 'finite comparison.textTokenCoverage at or above 98.5%'
    Assert-RejectedMutation $positive 3 'below-visual-structure-floor' {
        param($case)
        $case.comparison.visualStructureScore = 0.9499
    } 'finite comparison.visualStructureScore at or above 95.0%'
    Assert-RejectedMutation $positive 3 'inconsistent-first-render-ratio' {
        param($case)
        $case.comparison.nativeToSameByteFirstRenderRatio = 0.51
    } 'does not match its timing-derived ratio'
    Assert-RejectedMutation $positive 3 'missing-native-full-traversal-timing' {
        param($case)
        $case.native.PSObject.Properties.Remove('fullTraversalMs')
    } 'missing finite positive native or offline Edge timing needed to recompute nativeToSameByteFullPageRatio'
    Assert-RejectedMutation $positive 3 'non-finite-edge-first-ready-timing' {
        param($case)
        $case.browser.sameByteReplay.timing.firstViewportImagesReadyMs = [double]::NaN
    } 'missing finite positive native or offline Edge timing needed to recompute nativeToSameByteFirstRenderRatio'
    Assert-RejectedMutation $positive 3 'missing-edge-charged-traversal-timing' {
        param($case)
        $case.browser.sameByteReplay.timing.PSObject.Properties.Remove('chargedTraversalMs')
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'inconsistent-edge-audit-frame-timing' {
        param($case)
        $case.browser.sameByteReplay.timing.auditOnlyFrameWaitMs = 5
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'missing-stored-full-page-ratio' {
        param($case)
        $case.comparison.PSObject.Properties.Remove('nativeToSameByteFullPageRatio')
    } 'did not provide a finite positive offline Edge nativeToSameByteFullPageRatio'
    Assert-RejectedMutation $positive 3 'non-finite-stored-first-render-ratio' {
        param($case)
        $case.comparison.nativeToSameByteFirstRenderRatio = [double]::NaN
    } 'did not provide a finite positive offline Edge nativeToSameByteFirstRenderRatio'
    Assert-RejectedMutation $positive 3 'missing-source-text-token-coverage' {
        param($case)
        $case.comparison.PSObject.Properties.Remove('textTokenCoverage')
    } 'finite comparison.textTokenCoverage at or above 98.5%'
    Assert-RejectedMutation $positive 3 'non-finite-visual-structure-score' {
        param($case)
        $case.comparison.visualStructureScore = [double]::NaN
    } 'finite comparison.visualStructureScore at or above 95.0%'
    Assert-RejectedMutation $positive 18 'false-source-exemption' {
        param($case)
        $case.browser.sameByteReplay.reason = 'unverified'
    } 'invalid source-view replay exemption'
    Assert-RejectedMutation $positive 3 'wrong-source-parser' {
        param($case)
        $case.browser.sameByteReplay.parser.sha256 = '0' * 64
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'wrong-native-viewport' {
        param($case)
        $case.browser.sameByteReplay.viewport.width = 1000
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'incomplete-edge-tiles' {
        param($case)
        $case.browser.sameByteReplay.renderedExtent.height = 701
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'mismatched-page-extent' {
        param($case)
        $case.native.estimatedContentHeight = 850
        $case.comparison.sourceBoundLayoutExtentRatio = 850.0 / 700.0
    } 'outside the source-bound 0.90–1.10 full-page extent gate'
    Assert-RejectedMutation $positive 3 'unverified-source-image' {
        param($case)
        $case.browser.sameByteReplay.assets.expectedImageCount = 1
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 60 'offline-sentinel' {
        param($case)
        $case.native.firstViewportImagesReadyMs = 111
        $case.comparison.nativeToSameByteFirstRenderRatio = 1.11
        $case.comparison.nativeToBrowserFirstRenderRatio = 0.5
    } 'sentinel rank 60'

    Assert-RejectedManifestMutation $positive 4 'wrong-pinned-commit-output' {
        param($case, $manifest)
        $manifest.repository.commitSha = 'f' * 40
    }
    Assert-RejectedManifestMutation $positive 4 'wrong-pinned-readme-path-output' {
        param($case, $manifest)
        $manifest.repository.readmePath = 'docs/OTHER.md'
    }
    Assert-RejectedManifestMutation $positive 4 'wrong-pinned-readme-byte-size-output' {
        param($case, $manifest)
        $manifest.readme.byteSize = 11
    }

    $quotaPath = Join-Path $positive 'screenshot-quota-ranks-1-25.json'
    $quotaBytes = [IO.File]::ReadAllBytes($quotaPath)
    try {
        $quota = Get-Content -LiteralPath $quotaPath -Raw | ConvertFrom-Json
        $quota.screenshotBytes = 1073741825
        $quota | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $quotaPath -Encoding utf8
        $quotaOutput = Join-Path $resolvedTempRoot 'over-shard-screenshot-quota-output'
        $quotaRejected = $false
        try { Invoke-Merge $positive $quotaOutput 500 -RequireSameByte }
        catch {
            $quotaSummary = Get-Content -LiteralPath (Join-Path $quotaOutput 'summary.json') -Raw | ConvertFrom-Json
            $quotaRejected = (@($quotaSummary.failures) -join "`n") -match 'Screenshot quota evidence'
        }
        if (-not $quotaRejected) { throw 'The same-byte merge accepted a shard that exceeded its screenshot evidence quota.' }
    }
    finally {
        [IO.File]::WriteAllBytes($quotaPath, $quotaBytes)
    }

    $aggregateQuotaOriginals = [Collections.Generic.List[object]]::new()
    foreach ($aggregateQuotaPath in @(Get-ChildItem -LiteralPath $positive -File -Filter 'screenshot-quota-ranks-*.json')) {
        $aggregateQuotaOriginals.Add([pscustomobject]@{
            path = $aggregateQuotaPath.FullName
            bytes = [IO.File]::ReadAllBytes($aggregateQuotaPath.FullName)
        })
        $aggregateQuota = Get-Content -LiteralPath $aggregateQuotaPath.FullName -Raw | ConvertFrom-Json
        $aggregateQuota.screenshotBytes = 450MB
        $aggregateQuota | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $aggregateQuotaPath.FullName -Encoding utf8
    }
    try {
        $aggregateQuotaOutput = Join-Path $resolvedTempRoot 'over-aggregate-screenshot-quota-output'
        $aggregateQuotaRejected = $false
        try { Invoke-Merge $positive $aggregateQuotaOutput 500 -RequireSameByte }
        catch {
            $aggregateQuotaSummary = Get-Content -LiteralPath (Join-Path $aggregateQuotaOutput 'summary.json') -Raw | ConvertFrom-Json
            $aggregateQuotaRejected = (@($aggregateQuotaSummary.failures) -join "`n") -match 'Aggregate screenshot evidence'
        }
        if (-not $aggregateQuotaRejected) { throw 'The same-byte merge accepted shard screenshots above the run-wide quota.' }
    }
    finally {
        foreach ($aggregateQuotaOriginal in $aggregateQuotaOriginals) {
            [IO.File]::WriteAllBytes([string]$aggregateQuotaOriginal.path, [byte[]]$aggregateQuotaOriginal.bytes)
        }
    }

    $missingQuotaPath = Join-Path $positive 'screenshot-quota-ranks-1-25.json'
    $missingQuotaBytes = [IO.File]::ReadAllBytes($missingQuotaPath)
    try {
        Remove-Item -LiteralPath $missingQuotaPath -Force
        $missingQuotaOutput = Join-Path $resolvedTempRoot 'missing-screenshot-quota-output'
        $missingQuotaRejected = $false
        try { Invoke-Merge $positive $missingQuotaOutput 500 -RequireSameByte }
        catch {
            $missingQuotaSummary = Get-Content -LiteralPath (Join-Path $missingQuotaOutput 'summary.json') -Raw | ConvertFrom-Json
            $missingQuotaRejected = (@($missingQuotaSummary.failures) -join "`n") -match 'Screenshot quota evidence covers 475/500 ranks'
        }
        if (-not $missingQuotaRejected) { throw 'The same-byte merge accepted missing per-shard screenshot quota evidence.' }
    }
    finally {
        [IO.File]::WriteAllBytes($missingQuotaPath, $missingQuotaBytes)
    }

    Assert-RejectedBuildEvidenceMutation $positive 'app-binary-ranks-26-50.json' {
        param($build)
        $build.sourceCommitSha = 'f' * 40
    } 'malformed, mismatched, or overlaps another shard'

    $missingBuildPath = Join-Path $positive 'app-binary-ranks-1-25.json'
    $missingBuildBytes = [IO.File]::ReadAllBytes($missingBuildPath)
    try {
        Remove-Item -LiteralPath $missingBuildPath -Force
        $missingBuildOutput = Join-Path $resolvedTempRoot 'missing-build-evidence-output'
        $missingBuildRejected = $false
        try { Invoke-Merge $positive $missingBuildOutput 500 -RequireSameByte }
        catch {
            $missingBuildSummary = Get-Content -LiteralPath (Join-Path $missingBuildOutput 'summary.json') -Raw | ConvertFrom-Json
            $missingBuildRejected = (@($missingBuildSummary.failures) -join "`n") -match 'Release binary evidence covers 475/500 ranks'
        }
        if (-not $missingBuildRejected) { throw 'The same-byte merge accepted missing per-shard binary evidence.' }
    }
    finally {
        [IO.File]::WriteAllBytes($missingBuildPath, $missingBuildBytes)
    }

    $missingCorpusOutput = Join-Path $resolvedTempRoot 'missing-pinned-corpus-output'
    $missingCorpusRejected = $false
    try { Invoke-Merge $positive $missingCorpusOutput 500 -RequireSameByte -PinnedPath (Join-Path $positive 'missing-corpus.json') }
    catch {
        $missingCorpusSummary = Get-Content -LiteralPath (Join-Path $missingCorpusOutput 'summary.json') -Raw | ConvertFrom-Json
        $missingCorpusRejected = (@($missingCorpusSummary.failures) -join "`n") -match 'pinned top-500 corpus manifest is missing'
    }
    if (-not $missingCorpusRejected) { throw 'The same-byte merge accepted missing pinned corpus identity.' }

    $tamperedManifest = Join-Path $positive 'cases\002-example-repository-2\browser\same-byte-corpus-cccccccccccccccccccccccccccccccc\manifest.json'
    Add-Content -LiteralPath $tamperedManifest -Value 'tampered'
    $tamperedRejected = $false
    try { Invoke-Merge $positive (Join-Path $resolvedTempRoot 'tampered-output') 500 -RequireSameByte }
    catch {
        $tamperedSummary = Get-Content -LiteralPath (Join-Path $resolvedTempRoot 'tampered-output\summary.json') -Raw | ConvertFrom-Json
        $tamperedRejected = (@($tamperedSummary.failures) -join "`n") -match 'hash-matched same-byte manifest'
    }
    if (-not $tamperedRejected) { throw 'The merge did not reject changed same-byte manifest artifact bytes.' }

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
        if ($rank -eq 26) { New-Case $rank -FirstRatio 1.11 -FullRatio 0.8 }
        elseif ($rank -eq 60) { New-Case $rank -FirstRatio 1.11 -FullRatio 0.8 }
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
        $sentinelRejected = $sentinelFailures -match 'sentinel rank 26' -and
            $sentinelFailures -match 'sentinel rank 60' -and
            $sentinelFailures -match 'sentinel rank 203'
    }
    if (-not $sentinelRejected) { throw 'The same-byte release merge did not enforce the newly added rank 26 and existing performance sentinels.' }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    foreach ($requiredText in @(
        'same_byte_release:',
        'CaptureSameByteCorpus = $true',
        'RequireSameByteCorpus = $true',
        'PinnedManifestPath = (Join-Path $env:RUNNER_TEMP',
        'ShardWorkflowResult = $env:AUDIT_SHARDS_RESULT',
        '${{ runner.temp }}/readme-audit/app-binary-ranks-*.json',
        'screenshot-quota-ranks-*.json',
        'Get-TopReadmeScreenshotEvidence -AuditRoot $output',
        'Get-TopReadmeScreenshotEvidence.ps1',
        'readme-audit-evidence-',
        'readme-audit-visual-',
        'global.json',
        'Directory.Build.props',
        'Directory.Build.targets',
        'NuGet.config',
        '!${{ runner.temp }}/readme-audit/cases/**/browser/same-byte-corpus-*/readme.md',
        '!${{ runner.temp }}/readme-audit/cases/**/browser/same-byte-corpus-*/rendered.html',
        '!${{ runner.temp }}/readme-audit/cases/**/browser/same-byte-corpus-*/assets/**')) {
        if (-not $workflow.Contains($requiredText)) {
            throw "The top-500 workflow is missing its same-byte release contract: $requiredText"
        }
    }

    Write-Host 'Top-500 merge tests passed: pinned corpus rank/repository/commit/README path/size/blob identity, absent/source-view handling, positive 500-case offline text/structure metrics, per-shard Release app hash provenance, aggregate screenshot quotas, timing-derived ratio consistency, bidirectional source-to-GitHub token coverage, GitHub/source table-structure regression, independent rendered-case fidelity floors, native README identity, replay qualification, changed-manifest rejection, mixed same-byte rejection, unavailable-image rejection, offline outlier sentinels, and artifact/workflow opt-in.'
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
