[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    [ValidateRange(1, 500)]
    [int]$ExpectedCount = 500,

    [switch]$RequireSameByteCorpus,

    [string]$PinnedManifestPath = '',

    [ValidateSet('success', 'failure', 'cancelled', 'skipped')]
    [string]$ShardWorkflowResult = 'success',

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
# Artifact-volume limits are storage safety rails only; no screenshot capture,
# tile-coverage, or visual-fidelity requirement is waived by these values.
$screenshotShardQuotaBytes = 1073741824
$screenshotAggregateQuotaBytes = 8589934592
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
$caseDirectoryByRank = [Collections.Generic.Dictionary[int, string]]::new()
for ($index = 0; $index -lt $cases.Count; $index++) {
    [void]$caseDirectoryByRank.TryAdd(
        [int]$cases[$index].rank,
        $caseFiles[$index].DirectoryName)
}

$failures = [Collections.Generic.List[string]]::new()
if ($ShardWorkflowResult -cne 'success') {
    $failures.Add("At least one audit shard or required visual artifact upload did not complete successfully (needs.audit.result=$ShardWorkflowResult).")
}
$pinnedRepositoriesByRank = [Collections.Generic.Dictionary[int, object]]::new()
$pinnedManifestSha256 = ''
$pinnedManifestValid = $false
if ($RequireSameByteCorpus) {
    try {
        if ([string]::IsNullOrWhiteSpace($PinnedManifestPath) -or
            -not (Test-Path -LiteralPath $PinnedManifestPath -PathType Leaf)) {
            throw 'missing pinned corpus file'
        }
        $pinnedManifestItem = Get-Item -LiteralPath $PinnedManifestPath -ErrorAction Stop
        if ($pinnedManifestItem.Length -le 0 -or $pinnedManifestItem.Length -gt 16MB -or
            ($pinnedManifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'pinned corpus size or file type is invalid'
        }
        $pinnedManifest = Get-Content -LiteralPath $PinnedManifestPath -Raw | ConvertFrom-Json -Depth 16
        $pinnedRepositories = @($pinnedManifest.repositories)
        if ($pinnedManifest.schemaVersion -ne 2 -or
            [int]$pinnedManifest.requestedCount -ne $ExpectedCount -or
            $pinnedRepositories.Count -ne $ExpectedCount) {
            throw 'pinned corpus schema or case count is invalid'
        }

        $pinnedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        for ($index = 0; $index -lt $pinnedRepositories.Count; $index++) {
            $repository = $pinnedRepositories[$index]
            $expectedRank = $index + 1
            $fullName = [string]$repository.fullName
            $commitSha = [string]$repository.commitSha
            $readme = $repository.readme
            if ([int]$repository.rank -ne $expectedRank -or
                $fullName -notmatch '^[^/\s]+/[^/\s]+$' -or
                -not $pinnedNames.Add($fullName) -or
                $commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
                $null -eq $readme -or $readme.available -isnot [bool] -or
                $null -eq $readme.byteSize) {
                throw "pinned corpus identity is invalid at rank $expectedRank"
            }
            if ($readme.available) {
                $readmeByteSize = [double]$readme.byteSize
                if ([string]::IsNullOrWhiteSpace([string]$readme.path) -or
                    [string]$readme.sha -notmatch '^[0-9a-fA-F]{40}$' -or
                    -not [double]::IsFinite($readmeByteSize) -or
                    $readmeByteSize -lt 0 -or $readmeByteSize -gt 16MB -or
                    [Math]::Truncate($readmeByteSize) -ne $readmeByteSize) {
                    throw "pinned README identity is invalid at rank $expectedRank"
                }
            }
            elseif ([long]$readme.byteSize -ne 0 -or
                [string]$readme.path -ne '' -or [string]$readme.sha -ne '') {
                throw "pinned absent-README identity is inconsistent at rank $expectedRank"
            }

            [void]$pinnedRepositoriesByRank.Add($expectedRank, [pscustomobject]@{
                rank = $expectedRank
                fullName = $fullName
                commitSha = $commitSha.ToLowerInvariant()
                readmeAvailable = [bool]$readme.available
                readmePath = if ($readme.available) { [string]$readme.path } else { '' }
                readmeByteSize = [long]$readme.byteSize
                readmeSha = if ($readme.available) { ([string]$readme.sha).ToLowerInvariant() } else { '' }
            })
        }
        $pinnedManifestSha256 = (Get-FileHash -LiteralPath $PinnedManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $pinnedManifestValid = $true
    }
    catch {
        $pinnedRepositoriesByRank.Clear()
        $failures.Add('The pinned top-500 corpus manifest is missing, malformed, or has invalid repository/README identities.')
    }
}

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

function Get-AssetMapSha256([string]$ManifestFile) {
    $document = [System.Text.Json.JsonDocument]::Parse(
        [string][IO.File]::ReadAllText($ManifestFile))
    try {
        $options = [System.Text.Json.JsonSerializerOptions]::new()
        $options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
        $json = [System.Text.Json.JsonSerializer]::Serialize(
            $document.RootElement.GetProperty('assets'), $options)
        return [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData(
                [Text.Encoding]::UTF8.GetBytes($json))).ToLowerInvariant()
    }
    finally { $document.Dispose() }
}

function Test-FiniteAuditMetric([object]$Value, [double]$Minimum) {
    if ($null -eq $Value -or $Value -isnot [System.ValueType] -or $Value -is [bool]) {
        return $false
    }
    $number = [double]$Value
    return [double]::IsFinite($number) -and $number -ge $Minimum
}

function Get-PositiveFiniteRatio([object]$Numerator, [object]$Denominator) {
    if (-not (Test-FiniteAuditMetric -Value $Numerator -Minimum 0.0) -or
        -not (Test-FiniteAuditMetric -Value $Denominator -Minimum 0.0) -or
        [double]$Numerator -le 0 -or [double]$Denominator -le 0) {
        return $null
    }
    $ratio = [double]$Numerator / [double]$Denominator
    if ([double]::IsFinite($ratio) -and $ratio -gt 0) { return $ratio }
    return $null
}

function Get-ValidatedSemanticCount([object]$Value) {
    if ($null -eq $Value -or $Value -isnot [System.ValueType] -or $Value -is [bool]) {
        return $null
    }
    $number = [double]$Value
    if (-not [double]::IsFinite($number) -or $number -lt 0 -or $number -gt 200000 -or
        [Math]::Truncate($number) -ne $number) {
        return $null
    }
    return [int]$number
}

function Get-CountFidelity([int]$Expected, [int]$Actual) {
    if ($Expected -eq 0) { return [int]($Actual -eq 0) }
    $ratio = [double]$Actual / [double]$Expected
    if ($ratio -le 0) { return 0.0 }
    return [Math]::Min($ratio, 1.0 / $ratio)
}

function Get-CapturedGitHubSourceStructureScore($Case) {
    $sourceSemantic = $Case.browser.sameByteReplay.semantic
    $capturedSemantic = $Case.browser.semantic
    if ($null -eq $sourceSemantic -or $null -eq $capturedSemantic) { return $null }

    $headingsProperty = $capturedSemantic.PSObject.Properties['headings']
    if ($null -eq $headingsProperty -or $null -eq $headingsProperty.Value -or
        $headingsProperty.Value -is [string] -or
        $headingsProperty.Value -isnot [System.Collections.IList]) {
        return $null
    }

    $capturedCounts = @(
        @($headingsProperty.Value).Count,
        (Get-ValidatedSemanticCount $capturedSemantic.tables),
        (Get-ValidatedSemanticCount $capturedSemantic.taskCheckboxes),
        (Get-ValidatedSemanticCount $capturedSemantic.details))
    $sourceCounts = @(
        (Get-ValidatedSemanticCount $sourceSemantic.headingCount),
        (Get-ValidatedSemanticCount $sourceSemantic.tableCount),
        (Get-ValidatedSemanticCount $sourceSemantic.taskCheckboxCount),
        (Get-ValidatedSemanticCount $sourceSemantic.detailsCount))
    for ($index = 0; $index -lt $sourceCounts.Count; $index++) {
        if ($null -eq $capturedCounts[$index] -or $null -eq $sourceCounts[$index]) { return $null }
    }

    # Give each stable domain one normalized quarter so a table mismatch cannot
    # disappear among pages with many headings or other repeated elements.
    [double[]]$domainWeights = @(1, 1, 1, 1)
    $totalWeight = ($domainWeights | Measure-Object -Sum).Sum
    if ($null -eq $totalWeight -or -not [double]::IsFinite([double]$totalWeight) -or $totalWeight -le 0) {
        return $null
    }

    $score = 0.0
    for ($index = 0; $index -lt $domainWeights.Count; $index++) {
        $fidelity = Get-CountFidelity -Expected $capturedCounts[$index] -Actual $sourceCounts[$index]
        $score += $fidelity * ($domainWeights[$index] / [double]$totalWeight)
    }
    if ([double]::IsFinite($score)) { return $score }
    return $null
}

function Test-SourceReplayTileCoverage($Replay) {
    $extent = $Replay.renderedExtent
    $tiles = @($Replay.tiles)
    if ($null -eq $extent -or [int]$extent.width -le 0 -or
        [int]$extent.height -le 0 -or [int]$extent.height -gt 4194304 -or
        $tiles.Count -lt 1 -or $tiles.Count -gt 512) { return $false }

    [double]$coveredEnd = 0
    for ($index = 0; $index -lt $tiles.Count; $index++) {
        $tile = $tiles[$index]
        [double]$start = $tile.relativeY
        [double]$height = $tile.height
        [double]$end = $start + $height
        if ([int]$tile.index -ne $index -or
            [string]$tile.file -ne ('same-byte-source-tile-{0:D4}.png' -f $index) -or
            [double]$tile.width -ne [double]$extent.width -or
            -not [double]::IsFinite($start) -or -not [double]::IsFinite($height) -or
            $start -lt 0 -or $start -gt $coveredEnd -or
            $height -lt 1 -or $height -gt 8192 -or $end -gt [double]$extent.height) {
            return $false
        }
        $coveredEnd = [Math]::Max($coveredEnd, $end)
    }
    return $coveredEnd -eq [double]$extent.height
}

$buildEvidence = [Collections.Generic.List[object]]::new()
$screenshotEvidence = [Collections.Generic.List[object]]::new()
$screenshotEvidenceBytes = [long]0
if ($RequireSameByteCorpus) {
    $binaryEvidenceFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter 'app-binary-ranks-*.json' -ErrorAction SilentlyContinue)
    $coveredBuildRanks = [Collections.Generic.HashSet[int]]::new()
    $sourceCommitSha = ''
    if ($binaryEvidenceFiles.Count -eq 0) {
        $failures.Add('Same-byte release evidence is missing per-shard Release app binary fingerprints.')
    }
    foreach ($binaryFile in $binaryEvidenceFiles) {
        try {
            if ($binaryFile.Length -le 0 -or $binaryFile.Length -gt 4MB -or
                ($binaryFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'invalid file size or type'
            }
            $binary = Get-Content -LiteralPath $binaryFile.FullName -Raw | ConvertFrom-Json -Depth 8
            $startRank = [int]$binary.startRank
            $endRank = [int]$binary.endRank
            $expectedFileName = 'app-binary-ranks-{0}-{1}.json' -f $startRank, $endRank
            $assembliesProperty = $binary.PSObject.Properties['dependencyAssemblySha256']
            $assemblies = if ($null -eq $assembliesProperty) { $null } else { $assembliesProperty.Value }
            if ($binary.schemaVersion -ne 1 -or
                $startRank -lt 1 -or $endRank -lt $startRank -or $endRank -gt $ExpectedCount -or
                $binaryFile.Name -cne $expectedFileName -or
                [string]$binary.sourceCommitSha -notmatch '^[0-9a-fA-F]{40}$' -or
                [string]$binary.configuration -cne 'Release' -or
                [string]$binary.executableName -cne 'JitHub.WinUI.exe' -or
                [string]$binary.executableSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
                $null -eq $assemblies) {
                throw 'invalid Release build identity'
            }
            foreach ($requiredAssembly in @('JitHub.WinUI.dll', 'MarkdownRenderer.dll')) {
                $assemblyProperty = $assemblies.PSObject.Properties[$requiredAssembly]
                if ($null -eq $assemblyProperty -or
                    [string]$assemblyProperty.Value -notmatch '^[0-9a-fA-F]{64}$') {
                    throw "missing or invalid $requiredAssembly hash"
                }
            }
            foreach ($assemblyProperty in $assemblies.PSObject.Properties) {
                if ([string]$assemblyProperty.Value -notmatch '^[0-9a-fA-F]{64}$') {
                    throw 'invalid dependency assembly hash'
                }
            }
            if ([string]::IsNullOrWhiteSpace($sourceCommitSha)) {
                $sourceCommitSha = ([string]$binary.sourceCommitSha).ToLowerInvariant()
            }
            elseif ([string]$binary.sourceCommitSha -ine $sourceCommitSha) {
                throw 'Release shards were built from different source commits'
            }
            for ($rank = $startRank; $rank -le $endRank; $rank++) {
                if (-not $coveredBuildRanks.Add($rank)) {
                    throw "Release build rank $rank is covered by more than one shard"
                }
            }
            $buildEvidence.Add([pscustomobject]@{
                artifactFile = $binaryFile.Name
                startRank = $startRank
                endRank = $endRank
                sourceCommitSha = ([string]$binary.sourceCommitSha).ToLowerInvariant()
                configuration = [string]$binary.configuration
                executableSha256 = ([string]$binary.executableSha256).ToLowerInvariant()
                dependencyAssemblySha256 = $assemblies
            })
        }
        catch {
            $failures.Add("Release binary evidence '$($binaryFile.Name)' is malformed, mismatched, or overlaps another shard.")
        }
    }
    if ($coveredBuildRanks.Count -ne $ExpectedCount) {
        $failures.Add("Release binary evidence covers $($coveredBuildRanks.Count)/$ExpectedCount ranks.")
    }

    $screenshotEvidenceFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter 'screenshot-quota-ranks-*.json' -ErrorAction SilentlyContinue)
    $coveredScreenshotRanks = [Collections.Generic.HashSet[int]]::new()
    if ($screenshotEvidenceFiles.Count -eq 0) {
        $failures.Add('Same-byte release evidence is missing per-shard aggregate screenshot quota reports.')
    }
    foreach ($screenshotFile in $screenshotEvidenceFiles) {
        try {
            if ($screenshotFile.Length -le 0 -or $screenshotFile.Length -gt 1MB -or
                ($screenshotFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'invalid file size or type'
            }
            $quota = Get-Content -LiteralPath $screenshotFile.FullName -Raw | ConvertFrom-Json -Depth 4
            $startRank = [int]$quota.startRank
            $endRank = [int]$quota.endRank
            $expectedFileName = 'screenshot-quota-ranks-{0}-{1}.json' -f $startRank, $endRank
            $shardBytes = [long]$quota.screenshotBytes
            $screenshotCount = [int]$quota.screenshotCount
            $tileFailureCount = [int]$quota.caseTileValidationFailureCount
            $tileFailuresProperty = $quota.PSObject.Properties['caseTileValidationFailures']
            $tileFailures = if ($null -eq $tileFailuresProperty) { $null } else { @($tileFailuresProperty.Value) }
            $expectedShardStart = 1 + ([Math]::Floor(($startRank - 1) / 25) * 25)
            $expectedShardEnd = [Math]::Min($ExpectedCount, $expectedShardStart + 24)
            if ($quota.schemaVersion -ne 1 -or
                $startRank -lt 1 -or $endRank -lt $startRank -or $endRank -gt $ExpectedCount -or
                $startRank -ne $expectedShardStart -or $endRank -ne $expectedShardEnd -or
                $screenshotFile.Name -cne $expectedFileName -or
                $quota.status -cne 'passed' -or
                [long]$quota.shardQuotaBytes -ne $screenshotShardQuotaBytes -or
                $shardBytes -lt 0 -or $shardBytes -gt $screenshotShardQuotaBytes -or
                $screenshotCount -lt 1 -or $screenshotCount -gt 1000000 -or
                [string]$quota.caseTileValidationStatus -cne 'passed' -or
                $tileFailureCount -ne 0 -or $null -eq $tileFailuresProperty -or $tileFailures.Count -ne 0) {
                throw 'invalid or exceeded shard screenshot quota'
            }
            for ($rank = $startRank; $rank -le $endRank; $rank++) {
                if (-not $coveredScreenshotRanks.Add($rank)) {
                    throw "screenshot quota rank $rank is covered by more than one shard"
                }
            }
            $screenshotEvidenceBytes += $shardBytes
            $screenshotEvidence.Add([pscustomobject]@{
                artifactFile = $screenshotFile.Name
                startRank = $startRank
                endRank = $endRank
                screenshotCount = $screenshotCount
                screenshotBytes = $shardBytes
                shardQuotaBytes = $screenshotShardQuotaBytes
                caseTileValidationStatus = [string]$quota.caseTileValidationStatus
                caseTileValidationFailureCount = $tileFailureCount
                status = [string]$quota.status
            })
        }
        catch {
            $failures.Add("Screenshot quota evidence '$($screenshotFile.Name)' is malformed, failed, over quota, or overlaps another shard.")
        }
    }
    if ($coveredScreenshotRanks.Count -ne $ExpectedCount) {
        $failures.Add("Screenshot quota evidence covers $($coveredScreenshotRanks.Count)/$ExpectedCount ranks.")
    }
    if ($screenshotEvidenceBytes -gt $screenshotAggregateQuotaBytes) {
        $failures.Add(("Aggregate screenshot evidence was {0} bytes, above the {1}-byte release quota." -f
            $screenshotEvidenceBytes, $screenshotAggregateQuotaBytes))
    }
}

$sameByteCases = 0
$sameByteExpectedCases = 0
if ($RequireSameByteCorpus) {
    if (-not $pinnedManifestValid) {
        $failures.Add('Same-byte release identity cannot be qualified without a valid pinned corpus manifest.')
    }
    else {
        $sameByteExpectedCases = @($pinnedRepositoriesByRank.Values | Where-Object readmeAvailable).Count
    }
    $pinnedParserPath = Join-Path $PSScriptRoot 'vendor\marked-18.0.5.umd.js'
    $pinnedParserSha256 = '2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b'
    if (-not (Test-Path -LiteralPath $pinnedParserPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $pinnedParserPath -Algorithm SHA256).Hash -ne $pinnedParserSha256) {
        $failures.Add('The pinned source-bound Edge GFM parser is missing or changed.')
    }
    foreach ($case in $orderedCases) {
        $rank = [int]$case.rank
        if (-not $pinnedManifestValid -or -not $pinnedRepositoriesByRank.ContainsKey($rank)) {
            $failures.Add("Rank $rank cannot be matched to a pinned corpus identity.")
            continue
        }
        $expectedRepository = $pinnedRepositoriesByRank[$rank]
        $expectedReadmeSha = [string]$expectedRepository.readmeSha
        if ([string]$case.fullName -ine [string]$expectedRepository.fullName -or
            [string]$case.readmeSha -ine $expectedReadmeSha -or
            [string]$case.browser.readmeSha -ine $expectedReadmeSha) {
            $failures.Add("Rank $rank result identity does not match the pinned repository, commit, or README identity.")
        }

        if (-not $expectedRepository.readmeAvailable) {
            if ($null -ne $case.browser.sameByteCorpus -or
                $null -ne $case.browser.sameByteReplay -or
                $case.browser.readmeRendered -ne $false) {
                $failures.Add("Rank $rank is pinned without a README but its browser evidence does not prove an absent README.")
            }
            continue
        }

        $sameByte = $case.browser.sameByteCorpus
        $manifestPath = if ($null -eq $sameByte) { '' } else { [string]$sameByte.manifest }
        if ($null -eq $sameByte -or
            [string]$case.browser.readmeSha -ine $expectedReadmeSha -or
            $null -eq $sameByte.readmeBytes -or [long]$sameByte.readmeBytes -ne [long]$expectedRepository.readmeByteSize -or
            [string]$sameByte.manifestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            [string]$sameByte.readmeSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $manifestPath -notmatch '^same-byte-corpus-[0-9a-fA-F]{32}[\\/]manifest\.json$' -or
            $null -eq $sameByte.readmeBytes -or [long]$sameByte.readmeBytes -lt 0 -or [long]$sameByte.readmeBytes -gt 16MB -or
            $null -eq $sameByte.assetCount -or [int]$sameByte.assetCount -lt 0 -or [int]$sameByte.assetCount -gt 15000 -or
            $null -eq $sameByte.assetBytes -or [long]$sameByte.assetBytes -lt 0 -or [long]$sameByte.assetBytes -gt 256MB) {
            $failures.Add("Rank $($case.rank) is missing valid same-byte capture evidence for its pinned README.")
            continue
        }

        # The artifact deliberately omits source bytes, but retains its
        # privacy-safe manifest. Bind the case verdict to those uploaded bytes
        # rather than accepting a plausible-looking digest in result.json.
        $caseDirectory = ''
        if (-not $caseDirectoryByRank.TryGetValue([int]$case.rank, [ref]$caseDirectory)) {
            $failures.Add("Rank $($case.rank) has no case directory for its same-byte manifest.")
            continue
        }
        $manifestFile = Join-Path (Join-Path $caseDirectory 'browser') $manifestPath
        $manifestItem = Get-Item -LiteralPath $manifestFile -ErrorAction SilentlyContinue
        if ($null -eq $manifestItem -or $manifestItem.PSIsContainer -or
            $manifestItem.Length -le 0 -or $manifestItem.Length -gt 4MB -or
            ($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash -ne
                [string]$sameByte.manifestSha256) {
            $failures.Add("Rank $($case.rank) is missing its hash-matched same-byte manifest artifact.")
            continue
        }
        try {
            $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
            $assetMapSha256 = Get-AssetMapSha256 $manifestFile
            if ($manifest.schemaVersion -ne 2 -or $manifest.complete -ne $true -or
                [string]$manifest.repository.fullName -ine [string]$expectedRepository.fullName -or
                [string]$manifest.repository.commitSha -ine [string]$expectedRepository.commitSha -or
                [string]$manifest.repository.readmePath -cne [string]$expectedRepository.readmePath -or
                [string]$manifest.repository.readmeGitBlobSha1 -ine $expectedReadmeSha -or
                [string]$manifest.readme.file -ne 'readme.md' -or
                [long]$manifest.readme.byteSize -ne [long]$expectedRepository.readmeByteSize -or
                [long]$manifest.readme.byteSize -ne [long]$sameByte.readmeBytes -or
                [string]$manifest.readme.sha256 -ne [string]$sameByte.readmeSha256 -or
                @($manifest.assets).Count -ne [int]$sameByte.assetCount -or
                [long](($manifest.assets | Measure-Object -Property byteSize -Sum).Sum) -ne [long]$sameByte.assetBytes) {
                throw 'manifest identity mismatch'
            }
        }
        catch {
            $failures.Add("Rank $($case.rank) has a malformed or mismatched same-byte manifest artifact.")
            continue
        }
        if ([string]$case.native.readmeSourceSha256 -ne [string]$sameByte.readmeSha256) {
            $failures.Add("Rank $($case.rank) did not render the captured README source bytes in JitHub.")
        }

        $replay = $case.browser.sameByteReplay
        $expectedReplayStatus = if ($case.browser.readmeRendered -eq $false) { 'not-applicable' } else { 'passed' }
        $expectedReplaySchema = if ($expectedReplayStatus -eq 'passed') { 3 } else { 2 }
        if ($null -eq $replay -or $replay.schemaVersion -ne $expectedReplaySchema -or
            [string]$replay.status -ne $expectedReplayStatus -or
            [string]$manifest.browserRender.status -ne $expectedReplayStatus) {
            $failures.Add("Rank $($case.rank) is missing a qualified offline Edge replay.")
            continue
        }
        if ($expectedReplayStatus -eq 'not-applicable') {
            if ([string]$replay.reason -ne 'github-source-view' -or
                [string]$manifest.browserRender.reason -ne 'github-source-view') {
                $failures.Add("Rank $($case.rank) has an invalid source-view replay exemption.")
            }
        }
        else {
            $sourceSemantic = $replay.semantic
            if ($null -eq $sourceSemantic -or $sourceSemantic.complete -ne $true -or
                [string]$sourceSemantic.incompleteReason -ne '' -or
                [string]$sourceSemantic.tokenizationVersion -ne 'rune-l-n-mn-mc-han-nfc-simple-lower-invariant-v1' -or
                [string]$sourceSemantic.digestKeySha256 -notmatch '^[0-9a-fA-F]{64}$' -or
                -not (Test-FiniteAuditMetric -Value $sourceSemantic.sourceReplayToCapturedGitHubVisibleTextTokenCoverage -Minimum 0.985) -or
                -not (Test-FiniteAuditMetric -Value $sourceSemantic.capturedGitHubToSourceReplayVisibleTextTokenCoverage -Minimum 0.985) -or
                $null -eq $sourceSemantic.visibleTextTokenCount -or
                [long]$sourceSemantic.visibleTextTokenCount -lt 0 -or
                [long]$sourceSemantic.visibleTextTokenCount -gt 1000000 -or
                $null -eq $sourceSemantic.visibleTextTokenDigestCount -or
                [int]$sourceSemantic.visibleTextTokenDigestCount -lt 0 -or
                [int]$sourceSemantic.visibleTextTokenDigestCount -gt 20000 -or
                [int]$sourceSemantic.visibleTextTokenDigestCount -gt [long]$sourceSemantic.visibleTextTokenCount -or
                ([long]$sourceSemantic.visibleTextTokenCount -eq 0 -and [int]$sourceSemantic.visibleTextTokenDigestCount -ne 0) -or
                ([long]$sourceSemantic.visibleTextTokenCount -gt 0 -and [int]$sourceSemantic.visibleTextTokenDigestCount -eq 0) -or
                [double]$sourceSemantic.width -le 0 -or
                [double]$sourceSemantic.height -le 0 -or
                [int]$sourceSemantic.mediaCount -ne 0 -or
                [int]$sourceSemantic.distinctMediaCount -ne 0 -or
                $null -eq $case.comparison -or
                [string]$case.comparison.fidelityReference -ne 'same-byte-source') {
                $failures.Add("Rank $($case.rank) has incomplete source semantic fidelity evidence.")
            }
            $reportedSourceStructureScore = if ($null -eq $sourceSemantic) { $null } else { $sourceSemantic.capturedGitHubSourceStructureScore }
            $recomputedSourceStructureScore = Get-CapturedGitHubSourceStructureScore $case
            if (-not (Test-FiniteAuditMetric -Value $reportedSourceStructureScore -Minimum 0.95) -or
                $null -eq $recomputedSourceStructureScore -or
                -not [double]::IsFinite([double]$recomputedSourceStructureScore) -or
                [double]$recomputedSourceStructureScore -lt 0.95 -or
                [Math]::Abs([double]$reportedSourceStructureScore - [double]$recomputedSourceStructureScore) -gt 0.000001) {
                $failures.Add("Rank $($case.rank) captured GitHub/source stable-structure score is missing, mismatched, or below 95%.")
            }
            $htmlReplay = $case.browser.sameByteHtmlReplay
            $timing = $replay.timing
            $auditOnlyFrameWaitCount = Get-ValidatedSemanticCount -Value $timing.auditOnlyFrameWaitCount
            $maximumAuditOnlyFrameWaitCount = 512 * 17 * 3 + 2
            if ([string]$manifest.browserRender.file -ne 'rendered.html' -or
                [long]$manifest.browserRender.byteSize -le 0 -or
                [long]$manifest.browserRender.byteSize -gt 32MB -or
                [string]$manifest.browserRender.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
                $null -eq $htmlReplay -or [int]$htmlReplay.schemaVersion -ne 1 -or
                [string]$htmlReplay.status -ne 'passed' -or
                [string]$htmlReplay.renderedHtmlSha256 -ne [string]$manifest.browserRender.sha256 -or
                [string]$replay.source.readmeGitBlobSha1 -ine $expectedReadmeSha -or
                [string]$replay.source.readmeSha256 -ne [string]$sameByte.readmeSha256 -or
                [long]$replay.source.byteSize -ne [long]$sameByte.readmeBytes -or
                [string]$replay.parser.name -ne 'marked' -or
                [string]$replay.parser.version -ne '18.0.5' -or
                [string]$replay.parser.license -ne 'MIT' -or
                [string]$replay.parser.sha256 -ne $pinnedParserSha256 -or
                [int]$replay.viewport.width -ne [int]$case.native.contentViewportWidth -or
                [int]$replay.viewport.height -ne [int]$case.native.contentViewportHeight -or
                [int]$replay.viewport.edgeInnerWidth -ne [int]$case.native.contentViewportWidth -or
                [int]$replay.viewport.edgeInnerHeight -ne [int]$case.native.contentViewportHeight -or
                [int]$replay.renderedExtent.width -ne [int]$case.native.contentViewportWidth -or
                [int]$replay.renderedExtent.height -lt [int]$case.native.contentViewportHeight -or
                [int]$case.native.estimatedContentHeight -le 0 -or
                [Math]::Abs([double]$replay.viewport.deviceScaleFactor - [double]$case.native.rasterizationScale) -gt 0.000001 -or
                [Math]::Abs([double]$replay.viewport.edgeDeviceScaleFactor - [double]$case.native.rasterizationScale) -gt 0.000001 -or
                [string]$replay.viewport.colorScheme -ne 'light' -or
                [string]$replay.assets.assetUrlMapSha256 -ne $assetMapSha256 -or
                $null -eq $replay.assets -or
                [int]$replay.assets.expectedImageCount -lt 0 -or
                [int]$replay.assets.verifiedImageCount -ne [int]$replay.assets.expectedImageCount -or
                [int]$replay.assets.replayMissCount -ne 0 -or
                [int]$replay.assets.blockedExternalRequestCount -ne 0 -or
                [int]$replay.assets.distinctServedUrlHashes -ne [int]$replay.assets.distinctExpectedUrlHashes -or
                $null -eq $timing -or
                -not (Test-FiniteAuditMetric -Value $timing.firstViewportPaintMs -Minimum 0.0) -or
                [double]$timing.firstViewportPaintMs -le 0 -or
                -not (Test-FiniteAuditMetric -Value $timing.firstViewportImagesReadyMs -Minimum 0.0) -or
                [double]$timing.firstViewportImagesReadyMs -lt [double]$timing.firstViewportPaintMs -or
                -not (Test-FiniteAuditMetric -Value $timing.fullTraversalMs -Minimum 0.0) -or
                [double]$timing.fullTraversalMs -lt [double]$timing.firstViewportImagesReadyMs -or
                -not (Test-FiniteAuditMetric -Value $timing.chargedTraversalMs -Minimum 0.0) -or
                [double]$timing.chargedTraversalMs -le 0 -or
                [double]$timing.chargedTraversalMs -lt [double]$timing.firstViewportImagesReadyMs -or
                -not (Test-FiniteAuditMetric -Value $timing.auditOnlyFrameWaitMs -Minimum 0.0) -or
                [double]$timing.auditOnlyFrameWaitMs -le 0 -or
                $null -eq $auditOnlyFrameWaitCount -or
                $auditOnlyFrameWaitCount -lt 2 -or
                $auditOnlyFrameWaitCount -gt $maximumAuditOnlyFrameWaitCount -or
                [double]$timing.fullTraversalMs -lt [double]$timing.chargedTraversalMs -or
                [Math]::Abs([double]$timing.fullTraversalMs - [double]$timing.chargedTraversalMs -
                    [double]$timing.auditOnlyFrameWaitMs) -gt 0.001 -or
                [double]$case.native.firstViewportImagesReadyMs -le 0 -or
                -not (Test-SourceReplayTileCoverage $replay)) {
                $failures.Add("Rank $($case.rank) has incomplete or mismatched offline Edge replay evidence.")
            }
            $extentRatio = $case.comparison.sourceBoundLayoutExtentRatio
            $expectedExtentRatio = [double]$case.native.estimatedContentHeight / [double]$replay.renderedExtent.height
            if ($null -eq $extentRatio -or -not [double]::IsFinite([double]$extentRatio) -or
                [Math]::Abs([double]$extentRatio - $expectedExtentRatio) -gt 0.000001 -or
                [double]$extentRatio -lt 0.90 -or [double]$extentRatio -gt 1.10) {
                $failures.Add("Rank $($case.rank) is outside the source-bound 0.90–1.10 full-page extent gate or omitted its ratio.")
            }
            $timingRatioInputs = @(
                @{
                    Name = 'nativeToSameByteFirstRenderRatio'
                    Native = $case.native.firstViewportImagesReadyMs
                    Replay = $replay.timing.firstViewportImagesReadyMs
                },
                @{
                    Name = 'nativeToSameByteFullPageRatio'
                    Native = $case.native.fullTraversalMs
                    Replay = $replay.timing.chargedTraversalMs
                }
            )
            foreach ($ratioInput in $timingRatioInputs) {
                $metric = $ratioInput.Name
                $computedRatio = Get-PositiveFiniteRatio -Numerator $ratioInput.Native -Denominator $ratioInput.Replay
                $ratioProperty = if ($null -eq $case.comparison) { $null } else { $case.comparison.PSObject.Properties[$metric] }
                $reportedRatio = if ($null -eq $ratioProperty) { $null } else { $ratioProperty.Value }
                if ($null -eq $computedRatio) {
                    $failures.Add("Rank $($case.rank) is missing finite positive native or offline Edge timing needed to recompute $metric.")
                    if ($null -ne $ratioProperty) { $ratioProperty.Value = $null }
                    continue
                }
                if (-not (Test-FiniteAuditMetric -Value $reportedRatio -Minimum 0.0) -or [double]$reportedRatio -le 0) {
                    $failures.Add("Rank $($case.rank) did not provide a finite positive offline Edge $metric.")
                }
                elseif ([Math]::Abs([double]$reportedRatio - [double]$computedRatio) -gt 0.000001) {
                    $failures.Add("Rank $($case.rank) offline Edge $metric does not match its timing-derived ratio.")
                }
                # Use the validated timing calculation for the release gate and
                # summary. The stored ratio remains required evidence and must
                # agree within the same tolerance used for other serialized ratios.
                if ($null -ne $ratioProperty) { $ratioProperty.Value = $computedRatio }
            }
            foreach ($metric in @(
                @{ Name = 'textTokenCoverage'; Minimum = 0.985 },
                @{ Name = 'visualStructureScore'; Minimum = 0.95 }
            )) {
                $metricValue = if ($null -eq $case.comparison) { $null } else { $case.comparison.($metric.Name) }
                if (-not (Test-FiniteAuditMetric -Value $metricValue -Minimum $metric.Minimum)) {
                    $failures.Add(("Rank {0} rendered case has no finite comparison.{1} at or above {2:P1}." -f
                        $case.rank, $metric.Name, $metric.Minimum))
                }
            }
        }
        $sameByteCases++
    }

    if ($sameByteCases -ne $sameByteExpectedCases) {
        $failures.Add("Only $sameByteCases/$sameByteExpectedCases available README cases have same-byte capture evidence.")
    }

    # Keep known high-cost ranks visible in every release run. Aggregate p95
    # can hide a single slow page, so the full same-byte qualification also
    # checks these previously observed outliers individually against the same
    # 110% Edge ceiling.
    foreach ($sentinelRank in @(26, 60, 102, 149, 153, 201, 203, 295, 395, 476)) {
        $case = $orderedCases | Where-Object { [int]$_.rank -eq $sentinelRank } | Select-Object -First 1
        if ($null -eq $case) {
            $failures.Add("Same-byte release evidence is missing performance sentinel rank $sentinelRank.")
            continue
        }
        foreach ($metric in @('nativeToSameByteFirstRenderRatio', 'nativeToSameByteFullPageRatio')) {
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

$firstMetric = if ($RequireSameByteCorpus) { 'nativeToSameByteFirstRenderRatio' } else { 'nativeToBrowserFirstRenderRatio' }
$fullMetric = if ($RequireSameByteCorpus) { 'nativeToSameByteFullPageRatio' } else { 'nativeToBrowserFullPageRatio' }
$firstRatios = [double[]]@($orderedCases |
    Where-Object { $null -ne $_.comparison -and $null -ne $_.comparison.$firstMetric } |
    ForEach-Object { [double]$_.comparison.$firstMetric } |
    Where-Object { [double]::IsFinite($_) -and $_ -gt 0 } |
    Sort-Object)
$fullRatios = [double[]]@($orderedCases |
    Where-Object { $null -ne $_.comparison -and $null -ne $_.comparison.$fullMetric } |
    ForEach-Object { [double]$_.comparison.$fullMetric } |
    Where-Object { [double]::IsFinite($_) -and $_ -gt 0 } |
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
    shardWorkflowResult = $ShardWorkflowResult
    expectedCases = $ExpectedCount
    totalCases = $orderedCases.Count
    passedCases = $orderedCases.Count - $failedCases.Count
    failedCases = $failedCases.Count
    sameByteRequired = [bool]$RequireSameByteCorpus
    sameByteCases = $sameByteCases
    sameByteExpectedCases = if ($RequireSameByteCorpus) { $sameByteExpectedCases } else { 0 }
    pinnedCorpusSha256 = if ($RequireSameByteCorpus -and $pinnedManifestValid) { $pinnedManifestSha256 } else { $null }
    buildEvidence = @($buildEvidence | Sort-Object startRank)
    screenshotEvidenceBytes = if ($RequireSameByteCorpus) { $screenshotEvidenceBytes } else { $null }
    screenshotAggregateQuotaBytes = if ($RequireSameByteCorpus) { $screenshotAggregateQuotaBytes } else { $null }
    screenshotEvidence = @($screenshotEvidence | Sort-Object startRank)
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
if ($RequireSameByteCorpus -and $pinnedManifestValid) {
    $markdown.Add("- Pinned corpus SHA-256: $pinnedManifestSha256")
    $markdown.Add("- Aggregate screenshots: $screenshotEvidenceBytes / $screenshotAggregateQuotaBytes bytes across retained shard artifacts")
}
$comparisonScope = if ($RequireSameByteCorpus) { 'offline same-byte Edge' } else { 'live GitHub/Edge' }
$markdown.Add(("- Native/{0} first-render ratio: p50 {1:F3}, p95 {2:F3}" -f $comparisonScope, $firstP50, $firstP95))
$markdown.Add(("- Native/{0} full-page ratio: p50 {1:F3}, p95 {2:F3}" -f $comparisonScope, $fullP50, $fullP95))
$markdown.Add('')
if ($buildEvidence.Count -gt 0) {
    $markdown.Add('## Release app build identities')
    $markdown.Add('')
    $markdown.Add('| Ranks | Source commit | Executable SHA-256 | JitHub.WinUI.dll SHA-256 | MarkdownRenderer.dll SHA-256 |')
    $markdown.Add('| --- | --- | --- | --- | --- |')
    foreach ($build in @($buildEvidence | Sort-Object startRank)) {
        $appAssemblySha256 = [string]$build.dependencyAssemblySha256.'JitHub.WinUI.dll'
        $rendererAssemblySha256 = [string]$build.dependencyAssemblySha256.'MarkdownRenderer.dll'
        $markdown.Add(("| {0}-{1} | {2} | {3} | {4} | {5} |" -f
            $build.startRank, $build.endRank, $build.sourceCommitSha,
            $build.executableSha256, $appAssemblySha256, $rendererAssemblySha256))
    }
    $markdown.Add('')
}
$markdown.Add('| Rank | Repository | Result | Text | Native/source | GitHub/source | Unavailable | First ratio | Full ratio |')
$markdown.Add('| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |')
foreach ($case in $orderedCases) {
    $text = if ($null -eq $case.comparison) { 'n/a' } else { '{0:P2}' -f [double]$case.comparison.textTokenCoverage }
    $structure = if ($null -eq $case.comparison) { 'n/a' } else { '{0:P2}' -f [double]$case.comparison.visualStructureScore }
    $githubSourceStructureScore = if (Test-FiniteAuditMetric -Value $case.browser.sameByteReplay.semantic.capturedGitHubSourceStructureScore -Minimum 0.0) {
        '{0:P2}' -f [double]$case.browser.sameByteReplay.semantic.capturedGitHubSourceStructureScore
    } else { 'n/a' }
    $unavailable = if ($null -eq $case.native) { 'n/a' } else { [string]$case.native.unavailableImages }
    $first = if ($null -eq $case.comparison -or $null -eq $case.comparison.$firstMetric) { 'n/a' } else { '{0:F3}' -f [double]$case.comparison.$firstMetric }
    $full = if ($null -eq $case.comparison -or $null -eq $case.comparison.$fullMetric) { 'n/a' } else { '{0:F3}' -f [double]$case.comparison.$fullMetric }
    $markdown.Add("| $($case.rank) | $($case.fullName) | $($case.status) | $text | $structure | $githubSourceStructureScore | $unavailable | $first | $full |")
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
