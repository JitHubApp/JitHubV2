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

$fixtureStyleProfileId = 'fixture-github-readme'
$fixtureStyleProfileVersion = 7
$fixtureStyleProfileSha256 = 'a' * 64

function New-Case([int]$Rank, [bool]$IncludeSameByte = $true, [bool]$AbsentReadme = $false, [bool]$SourceView = $false, [double]$FirstRatio = 0.5, [double]$FullRatio = 0.6, [int]$UnavailableImages = 0, [bool]$IncludeVisualProfile = $true) {
    $readmeSha = if ($AbsentReadme) { '' } else { 'b' * 40 }
    $readmeSha256 = 'd' * 64
    $decodedTextSha256 = 'f' * 64
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
        # Complete matched-viewport evidence only; this deliberately does not
        # encode a numeric SSIM fidelity floor.
        sameByteTileSsimReference = 'same-byte-source-matched-viewport'
        sameByteTileSsimUnavailableReason = ''
        sameByteMeanTileSsim = 0.5
        sameByteMinimumTileSsim = 0.25
        sameByteTileCoverage = 1.0
        sameByteMatchedViewportCount = 1
        sameByteTotalViewportCount = 1
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
                    decodedTextSha256 = $decodedTextSha256
                }
                parser = [ordered]@{
                    name = 'marked'
                    version = '18.0.5'
                    license = 'MIT'
                    sha256 = '2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b'
                    emojiShortcodeSource = 'github/gemoji/db/emoji.json'
                    emojiShortcodeSourceCommit = 'fadaeaf1f1a9be82b321316a6c5502e43138b2f6'
                    emojiShortcodeSourceSha256 = 'b174ae2aeb321b52f64adb9ff412f966a7f338839d780784dd15dcad702c2dd6'
                    emojiShortcodeLicense = 'MIT'
                    emojiShortcodeLicenseSha256 = 'bab932cd28676c262d38eb7aedd7c366ebae15a07d823cc51854ec16ec045dba'
                    emojiShortcodeMapSha256 = '44980486df12fa536479d4a8c28e57b65654b373a263a894f79ccc7ceb335cf5'
                    emojiShortcodeExtensionSha256 = '89f772b065e6ba0114a9a8aaba19de071b20e2c44af065f83d09865c5bcb4d87'
                    emojiShortcodeAliasCount = 1913
                }
                styleProfile = if ($IncludeVisualProfile) { [ordered]@{
                    id = $fixtureStyleProfileId
                    version = $fixtureStyleProfileVersion
                    sha256 = $fixtureStyleProfileSha256
                    colorScheme = 'light'
                    edgeStylesheetLoaded = $true
                    edgeIntegrityAttributeMatched = $true
                    loopbackResponseCount = 1
                } } else { $null }
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
                    firstViewportImagesReadyBoundary = 'same source-available start through successful decode of every visible pinned image'
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
            readmeSourceDecodedTextSha256 = if ($AbsentReadme) { $null } else { $decodedTextSha256 }
            visualProfile = if ($IncludeVisualProfile -and -not $AbsentReadme -and -not $SourceView) { [ordered]@{
                id = $fixtureStyleProfileId
                version = $fixtureStyleProfileVersion
                cssSha256 = $fixtureStyleProfileSha256
            } } else { $null }
            firstViewportImagesReadyMs = 200
            sourceAvailableToFirstViewportImagesReadyMs = 100 * $FirstRatio
            fullTraversalMs = 200 * $FullRatio
            contentViewportWidth = 623
            contentViewportHeight = 700
            estimatedContentHeight = 700
            rasterizationScale = 1
            tiles = @([ordered]@{ index = 0; file = 'tile-0000.png' })
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
                imageRoutes = @()
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
            [Text.Encoding]::UTF8.GetBytes('fixture-shared-native-aot-app'))).ToLowerInvariant()
        $payloadFiles = [ordered]@{
            'JitHub.WinUI.exe' = $executableHash
            'e_sqlite3.dll' = 'b' * 64
            'runtimes/win-x64/MarkdownRenderer.Svg.Resvg.Worker.exe' = 'a' * 64
        }
        $payloadLines = @($payloadFiles.GetEnumerator() | ForEach-Object { "$($_.Key)`0$($_.Value)" })
        $payloadManifestHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($payloadLines -join "`n"))).ToLowerInvariant()
        $provenance = [ordered]@{
            schemaVersion = 1
            provenanceVerified = $true
            workingTreeClean = $true
            sourceCommitSha = '1' * 40
            configuration = 'Release'
            runtimeFlavor = 'native-aot'
            architecture = 'x64'
            runtimeIdentifier = 'win-x64'
            executableName = 'JitHub.WinUI.exe'
            executableSha256 = $executableHash
            workflowRunId = '123456789'
            workflowRepository = 'JitHubApp/JitHubV2'
            workflowRef = 'refs/heads/main'
            artifactFileSha256 = $payloadFiles
            artifactManifestSha256 = $payloadManifestHash
        }
        $provenanceBytes = [Text.UTF8Encoding]::new($false).GetBytes(($provenance | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
        $provenanceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($provenanceBytes)).ToLowerInvariant()
        $build = [ordered]@{
            schemaVersion = 2
            startRank = $startRank
            endRank = $endRank
            sourceCommitSha = '1' * 40
            configuration = 'Release'
            runtimeFlavor = 'native-aot'
            provenanceVerified = $true
            provenanceStatus = 'verified-canonical-workflow-artifact'
            provenanceSha256 = $provenanceHash
            provenanceWorkingTreeClean = $true
            workflowRunId = '123456789'
            workflowRepository = 'JitHubApp/JitHubV2'
            workflowRef = 'refs/heads/main'
            executableName = 'JitHub.WinUI.exe'
            executableSha256 = $executableHash
            dependencyAssemblySha256 = [ordered]@{
                'e_sqlite3.dll' = 'b' * 64
            }
            artifactFileSha256 = [ordered]@{
                'JitHub.WinUI.exe' = $executableHash
                'e_sqlite3.dll' = 'b' * 64
                'runtimes/win-x64/MarkdownRenderer.Svg.Resvg.Worker.exe' = 'a' * 64
                'native-aot-provenance.json' = $provenanceHash
            }
        }
        $build | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Root ("app-binary-ranks-{0}-{1}.json" -f $startRank, $endRank)) -Encoding utf8
        [IO.File]::WriteAllBytes((Join-Path $Root ("native-aot-provenance-ranks-{0}-{1}.json" -f $startRank, $endRank)), $provenanceBytes)
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
        if ($null -ne $case.browser.sameByteReplay.assets) {
            $case.browser.sameByteReplay.assets.assetUrlMapSha256 = Get-TestAssetMapSha256 $manifestPath
        }
        $case | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $casePath -Encoding utf8
        $output = Join-Path $resolvedTempRoot $Name
        $rejected = $false
        try { Invoke-Merge $Root $output 500 -RequireSameByte }
        catch {
            $summary = Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw | ConvertFrom-Json
            $rejected = (@($summary.failures) -join "`n") -match 'malformed or mismatched same-byte manifest artifact'
        }
        if (-not $rejected) { throw "The same-byte merge accepted an invalid manifest identity in '$Name'." }
    }
    finally {
        [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
        [IO.File]::WriteAllBytes($casePath, $caseBytes)
    }
}

function Get-TestAssetMapSha256([string]$ManifestFile) {
    $document = [System.Text.Json.JsonDocument]::Parse([string][IO.File]::ReadAllText($ManifestFile))
    try {
        $options = [System.Text.Json.JsonSerializerOptions]::new()
        $options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
        $json = [System.Text.Json.JsonSerializer]::Serialize($document.RootElement.GetProperty('assets'), $options)
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))).ToLowerInvariant()
    }
    finally { $document.Dispose() }
}

function Set-DuplicatePayloadAliasFixture([string]$Root, [int]$Rank) {
    $casePath = Join-Path $Root ("cases\{0:D3}-example-repository-{0}\result.json" -f $Rank)
    $case = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
    $manifestPath = Join-Path (Join-Path (Split-Path -Parent $casePath) 'browser') $case.browser.sameByteCorpus.manifest
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.assets = @(
        [ordered]@{ urlSha256 = 'a' * 64; sha256 = '1' * 64; byteSize = 10; mimeType = 'image/png' },
        [ordered]@{ urlSha256 = 'b' * 64; sha256 = '1' * 64; byteSize = 10; mimeType = 'image/png' },
        [ordered]@{ urlSha256 = 'c' * 64; sha256 = '2' * 64; byteSize = 15; mimeType = 'image/webp' }
    )
    $manifest.imageRoutes = @(
        [ordered]@{ index = 0; urlSha256 = 'a' * 64; sha256 = '1' * 64; mimeType = 'image/png' },
        [ordered]@{ index = 1; urlSha256 = 'b' * 64; sha256 = '1' * 64; mimeType = 'image/png' },
        [ordered]@{ index = 2; urlSha256 = 'c' * 64; sha256 = '2' * 64; mimeType = 'image/webp' }
    )
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $case.browser.sameByteCorpus.assetCount = 2
    $case.browser.sameByteCorpus.assetBytes = 25
    $case.browser.sameByteCorpus.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    $case.browser.sameByteReplay.assets.assetUrlMapSha256 = Get-TestAssetMapSha256 $manifestPath
    $case.browser.sameByteReplay.assets.expectedImageCount = 3
    $case.browser.sameByteReplay.assets.verifiedImageCount = 3
    $case.browser.sameByteReplay.assets.distinctExpectedUrlHashes = 3
    $case.browser.sameByteReplay.assets.distinctServedUrlHashes = 3
    $case.browser.sameByteReplay.semantic.imageCount = 3
    $case.browser.sameByteReplay.semantic.distinctImageCount = 3
    $case | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $casePath -Encoding utf8
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

function Assert-RejectedProvenanceMutation(
    [string]$Root,
    [string]$FileName,
    [scriptblock]$Mutation) {
    $provenancePath = Join-Path $Root $FileName
    $originalBytes = [IO.File]::ReadAllBytes($provenancePath)
    try {
        $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
        & $Mutation $provenance
        $provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8
        $output = Join-Path $resolvedTempRoot ("provenance-evidence-{0}" -f [Guid]::NewGuid().ToString('N'))
        $rejected = $false
        try { Invoke-Merge $Root $output 500 -RequireSameByte }
        catch {
            $summary = Get-Content -LiteralPath (Join-Path $output 'summary.json') -Raw | ConvertFrom-Json
            $rejected = (@($summary.failures) -join "`n") -match 'Release binary evidence'
        }
        if (-not $rejected) { throw "The same-byte merge accepted tampered provenance evidence in '$FileName'." }
    }
    finally {
        [IO.File]::WriteAllBytes($provenancePath, $originalBytes)
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
    # Nonrelease diagnostics intentionally continue to accept legacy cases
    # without either side of the style-profile identity contract.
    Write-Cases $baseline 2 { param($rank) New-Case $rank -AbsentReadme:($rank -eq 2) -IncludeVisualProfile:$false }
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
        @($positiveSummary.buildEvidence | Select-Object -ExpandProperty executableSha256 -Unique).Count -ne 1 -or
        @($positiveSummary.buildEvidence | Select-Object -ExpandProperty artifactManifestSha256 -Unique).Count -ne 1 -or
        @($positiveSummary.buildEvidence | Select-Object -ExpandProperty runtimeFlavor -Unique).Count -ne 1 -or
        @($positiveSummary.buildEvidence | Select-Object -ExpandProperty provenanceSha256 -Unique).Count -ne 1 -or
        @($positiveSummary.buildEvidence | Select-Object -ExpandProperty workflowRunId -Unique).Count -ne 1 -or
        @($positiveSummary.buildEvidence | Where-Object { $_.provenanceVerified -ne $true -or $_.provenanceStatus -cne 'verified-canonical-workflow-artifact' }).Count -ne 0 -or
        [string]$positiveSummary.buildEvidence[0].runtimeFlavor -cne 'native-aot' -or
        @($positiveSummary.buildEvidence | Select-Object -ExpandProperty sourceCommitSha -Unique).Count -ne 1) {
        throw 'The release merge did not prove one shared, provenance-qualified NativeAOT binary under one source commit and workflow run.'
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

    Assert-RejectedMutation $positive 2 'missing-native-style-profile' {
        param($case)
        $case.native.visualProfile = $null
    } 'native profile missing'
    Assert-RejectedMutation $positive 2 'missing-edge-style-profile' {
        param($case)
        $case.browser.sameByteReplay.styleProfile = $null
    } 'Edge profile missing'
    Assert-RejectedMutation $positive 2 'mismatched-style-profile-id' {
        param($case)
        $case.native.visualProfile.id = 'different-fixture-profile'
    } 'profile id'
    Assert-RejectedMutation $positive 2 'mismatched-style-profile-version' {
        param($case)
        $case.native.visualProfile.version++
    } 'profile version'
    Assert-RejectedMutation $positive 2 'mismatched-style-profile-css-sha256' {
        param($case)
        $case.native.visualProfile.cssSha256 = '0' * 64
    } 'profile CSS SHA-256'
    Assert-RejectedMutation $positive 2 'edge-style-profile-stylesheet-not-loaded' {
        param($case)
        $case.browser.sameByteReplay.styleProfile.edgeStylesheetLoaded = $false
    } 'Edge stylesheet not loaded'
    Assert-RejectedMutation $positive 2 'edge-style-profile-integrity-not-matched' {
        param($case)
        $case.browser.sameByteReplay.styleProfile.edgeIntegrityAttributeMatched = $false
    } 'Edge stylesheet integrity mismatch'
    Assert-RejectedMutation $positive 2 'edge-style-profile-loopback-response-missing' {
        param($case)
        $case.browser.sameByteReplay.styleProfile.loopbackResponseCount = 0
    } 'Edge stylesheet response missing'

    # Manifest rows are URL aliases; counters describe unique payloads. Two
    # different URLs may legitimately serve identical bytes and must count once.
    Set-DuplicatePayloadAliasFixture $positive 4
    $aliasOutput = Join-Path $resolvedTempRoot 'duplicate-payload-aliases-output'
    try { Invoke-Merge $positive $aliasOutput 500 -RequireSameByte }
    catch {
        $aliasSummary = Get-Content -LiteralPath (Join-Path $aliasOutput 'summary.json') -Raw | ConvertFrom-Json
        throw ("Valid duplicate-payload aliases were rejected: {0}" -f (@($aliasSummary.failures) -join '; '))
    }
    $aliasSummary = Get-Content -LiteralPath (Join-Path $aliasOutput 'summary.json') -Raw | ConvertFrom-Json
    if (-not $aliasSummary.passed -or $aliasSummary.sameByteCases -ne 499 -or $aliasSummary.sameByteExpectedCases -ne 499) {
        throw 'The same-byte merger did not accept URL aliases backed by the same unique payload bytes.'
    }
    Assert-RejectedManifestMutation $positive 4 'url-alias-counters-must-not-count-payloads' {
        param($case, $manifest)
        $case.browser.sameByteCorpus.assetCount = 3
        $case.browser.sameByteCorpus.assetBytes = 35
    }
    Assert-RejectedManifestMutation $positive 4 'duplicate-url-alias-hash' {
        param($case, $manifest)
        $manifest.assets[1].urlSha256 = $manifest.assets[0].urlSha256
    }
    Assert-RejectedManifestMutation $positive 4 'duplicate-image-route-index' {
        param($case, $manifest)
        $manifest.imageRoutes = @(
            [ordered]@{ index = 0; dataUri = $true },
            [ordered]@{ index = 0; dataUri = $true }
        )
    }
    Assert-RejectedManifestMutation $positive 4 'unbound-image-route-payload' {
        param($case, $manifest)
        $manifest.imageRoutes = @(
            [ordered]@{ index = 0; urlSha256 = 'a' * 64; sha256 = 'b' * 64; mimeType = 'image/png' }
        )
    }
    Assert-RejectedManifestMutation $positive 4 'conflicting-size-for-aliased-payload' {
        param($case, $manifest)
        $manifest.assets[1].byteSize = 11
    }
    Assert-RejectedManifestMutation $positive 4 'fractional-asset-byte-size' {
        param($case, $manifest)
        $manifest.assets[2].byteSize = 15.5
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
    Assert-RejectedMutation $positive 3 'missing-same-byte-visual-reference' {
        param($case)
        $case.comparison.PSObject.Properties.Remove('sameByteTileSsimReference')
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'wrong-same-byte-visual-reference' {
        param($case)
        $case.comparison.sameByteTileSsimReference = 'live-github-cross-style-diagnostic-absolute-offset'
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'same-byte-visual-reference-with-unavailable-reason' {
        param($case)
        $case.comparison.sameByteTileSsimUnavailableReason = 'source-tile-coverage-incomplete'
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'missing-same-byte-visual-coverage' {
        param($case)
        $case.comparison.PSObject.Properties.Remove('sameByteTileCoverage')
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'partial-same-byte-visual-coverage' {
        param($case)
        $case.comparison.sameByteTileCoverage = 0.5
        $case.comparison.sameByteMatchedViewportCount = 1
        $case.comparison.sameByteTotalViewportCount = 2
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'mismatched-same-byte-visual-counts' {
        param($case)
        $case.comparison.sameByteMatchedViewportCount = 2
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'same-byte-visual-count-omits-native-tile' {
        param($case)
        $case.native.tiles = @($case.native.tiles) + @([ordered]@{ index = 1; file = 'tile-0001.png' })
        # The comparison continues to claim complete 1/1 coverage, but the
        # actual native capture contains two viewports.
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'missing-same-byte-visual-count' {
        param($case)
        $case.comparison.PSObject.Properties.Remove('sameByteTotalViewportCount')
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'missing-same-byte-mean-ssim' {
        param($case)
        $case.comparison.PSObject.Properties.Remove('sameByteMeanTileSsim')
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'non-finite-same-byte-mean-ssim' {
        param($case)
        $case.comparison.sameByteMeanTileSsim = [double]::NaN
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'non-finite-same-byte-minimum-ssim' {
        param($case)
        $case.comparison.sameByteMinimumTileSsim = [double]::NaN
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'out-of-range-same-byte-mean-ssim' {
        param($case)
        $case.comparison.sameByteMeanTileSsim = 1.0001
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'out-of-range-same-byte-minimum-ssim' {
        param($case)
        $case.comparison.sameByteMinimumTileSsim = 1.0001
    } 'incomplete source-matched visual evidence'
    Assert-RejectedMutation $positive 3 'inverted-same-byte-ssim-range' {
        param($case)
        $case.comparison.sameByteMinimumTileSsim = 0.75
        $case.comparison.sameByteMeanTileSsim = 0.5
    } 'incomplete source-matched visual evidence'
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
    Assert-RejectedMutation $positive 3 'missing-source-available-native-timing' {
        param($case)
        $case.native.PSObject.Properties.Remove('sourceAvailableToFirstViewportImagesReadyMs')
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'mismatched-decoded-source-identity' {
        param($case)
        $case.browser.sameByteReplay.source.decodedTextSha256 = '0' * 64
    } 'incomplete or mismatched offline Edge replay evidence'
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
    Assert-RejectedMutation $positive 3 'wrong-emoji-shortcode-source-pin' {
        param($case)
        $case.browser.sameByteReplay.parser.emojiShortcodeSourceSha256 = '0' * 64
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'wrong-emoji-shortcode-license-pin' {
        param($case)
        $case.browser.sameByteReplay.parser.emojiShortcodeLicenseSha256 = '0' * 64
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'wrong-emoji-shortcode-map-pin' {
        param($case)
        $case.browser.sameByteReplay.parser.emojiShortcodeMapSha256 = '0' * 64
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'wrong-emoji-shortcode-extension-pin' {
        param($case)
        $case.browser.sameByteReplay.parser.emojiShortcodeExtensionSha256 = '0' * 64
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'wrong-emoji-shortcode-alias-count' {
        param($case)
        $case.browser.sameByteReplay.parser.emojiShortcodeAliasCount = 2
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
    Assert-RejectedMutation $positive 3 'source-image-route-count-mismatch' {
        param($case)
        $case.browser.sameByteReplay.assets.expectedImageCount = 1
        $case.browser.sameByteReplay.assets.verifiedImageCount = 1
        $case.browser.sameByteReplay.semantic.imageCount = 1
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 3 'source-semantic-image-count-mismatch' {
        param($case)
        $case.browser.sameByteReplay.semantic.imageCount = 1
    } 'incomplete or mismatched offline Edge replay evidence'
    Assert-RejectedMutation $positive 60 'offline-sentinel' {
        param($case)
        $case.native.sourceAvailableToFirstViewportImagesReadyMs = 111
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

    Assert-RejectedBuildEvidenceMutation $positive 'app-binary-ranks-26-50.json' {
        param($build)
        $build.runtimeFlavor = 'managed'
    } 'malformed, mismatched, or overlaps another shard'

    Assert-RejectedBuildEvidenceMutation $positive 'app-binary-ranks-26-50.json' {
        param($build)
        $build.executableSha256 = 'f' * 64
    } 'malformed, mismatched, or overlaps another shard'

    Assert-RejectedBuildEvidenceMutation $positive 'app-binary-ranks-26-50.json' {
        param($build)
        $build.artifactFileSha256.'runtimes/win-x64/MarkdownRenderer.Svg.Resvg.Worker.exe' = 'f' * 64
    } 'malformed, mismatched, or overlaps another shard'

    Assert-RejectedBuildEvidenceMutation $positive 'app-binary-ranks-26-50.json' {
        param($build)
        $build.dependencyAssemblySha256.'e_sqlite3.dll' = 'f' * 64
    } 'malformed, mismatched, or overlaps another shard'

    Assert-RejectedBuildEvidenceMutation $positive 'app-binary-ranks-26-50.json' {
        param($build)
        $build.artifactFileSha256.PSObject.Properties.Remove('runtimes/win-x64/MarkdownRenderer.Svg.Resvg.Worker.exe')
    } 'malformed, mismatched, or overlaps another shard'

    Assert-RejectedBuildEvidenceMutation $positive 'app-binary-ranks-26-50.json' {
        param($build)
        $build.provenanceVerified = $false
        $build.provenanceStatus = 'verified-artifact-local-diagnostic-only'
    } 'malformed, mismatched, or overlaps another shard'

    Assert-RejectedBuildEvidenceMutation $positive 'app-binary-ranks-26-50.json' {
        param($build)
        $build.workflowRunId = '987654321'
    } 'malformed, mismatched, or overlaps another shard'

    Assert-RejectedProvenanceMutation $positive 'native-aot-provenance-ranks-26-50.json' {
        param($provenance)
        $provenance.sourceCommitSha = 'f' * 40
    }

    $extraProvenancePath = Join-Path $positive 'native-aot-provenance-ranks-501-525.json'
    [IO.File]::WriteAllText($extraProvenancePath, '{}')
    try {
        $extraProvenanceOutput = Join-Path $resolvedTempRoot 'extra-provenance-output'
        $extraProvenanceRejected = $false
        try { Invoke-Merge $positive $extraProvenanceOutput 500 -RequireSameByte }
        catch {
            $extraProvenanceSummary = Get-Content -LiteralPath (Join-Path $extraProvenanceOutput 'summary.json') -Raw | ConvertFrom-Json
            $extraProvenanceRejected = (@($extraProvenanceSummary.failures) -join "`n") -match 'Unexpected per-shard NativeAOT provenance evidence'
        }
        if (-not $extraProvenanceRejected) { throw 'The same-byte merge accepted unpaired per-shard NativeAOT provenance.' }
    }
    finally {
        Remove-Item -LiteralPath $extraProvenancePath -Force
    }

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
        if ($rank -in @(2, 5, 8, 22, 26, 60)) { New-Case $rank -FirstRatio 1.11 -FullRatio 0.8 }
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
        $requiredSentinelRanks = @(2, 5, 8, 22, 26, 60, 203)
        $sentinelRejected = @($requiredSentinelRanks | Where-Object {
            $sentinelFailures -notmatch ("sentinel rank {0} " -f $_)
        }).Count -eq 0
    }
    if (-not $sentinelRejected) { throw 'The same-byte release merge did not enforce the newly added rank 2, 5, 8, and 22 plus existing performance sentinels.' }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    foreach ($requiredText in @(
        'same_byte_release:',
        'native_aot:',
        'needs: [corpus, native_aot]',
        'Verify-NativeAotArtifact.ps1',
        'Verify-NativeAotArtifact.Tests.ps1',
        'New-NativeAotProvenance.ps1',
        'NativeAotProvenance.Tests.ps1',
        'NativeAotProvenancePath = (Join-Path $env:RUNNER_TEMP',
        'SOURCE_WORKFLOW_RUN_ID: ${{ github.run_id }}',
        'SOURCE_WORKFLOW_REPOSITORY: ${{ github.repository }}',
        'SOURCE_WORKFLOW_REF: ${{ github.ref }}',
        'native-aot-provenance-ranks-*.json',
        'NativeAotArtifact = $true',
        'CaptureSameByteCorpus = $true',
        'RequireSameByteCorpus = $true',
        'github-emoji-shortcodes.test.mjs',
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

    Write-Host 'Top-500 merge tests passed: pinned corpus rank/repository/commit/README path/size/blob identity, GitHub emoji source/license/map/adapter provenance and tamper rejection, offline shortcode token-boundary and data-integrity tests, absent/source-view handling, positive 500-case offline text/structure metrics, one shared NativeAOT Release executable and full-artifact file/hash identity, aggregate screenshot quotas, timing-derived ratio consistency, bidirectional source-to-GitHub token coverage, GitHub/source table-structure regression, independent rendered-case fidelity floors, native README identity, replay qualification, changed-manifest rejection, mixed same-byte rejection, unavailable-image rejection, offline outlier sentinels, and artifact/workflow opt-in.'
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
