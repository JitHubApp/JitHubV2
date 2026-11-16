[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PinnedRepositoryPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$nativeRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$requestedPinnedRepositoryPath = $PinnedRepositoryPath
. (Join-Path $nativeRoot 'Prepare-MermanSource.ps1')
$PinnedRepositoryPath = $requestedPinnedRepositoryPath
$script:MermanPreparationTestChecks = 0

function Assert-PreparationTest {
    param(
        [Parameter(Mandatory)] [bool] $Condition,
        [Parameter(Mandatory)] [string] $Message
    )

    if (-not $Condition) {
        throw "Merman preparation test failed: $Message"
    }
    $script:MermanPreparationTestChecks++
}

function Assert-PreparationThrows {
    param(
        [Parameter(Mandatory)] [scriptblock] $Action,
        [Parameter(Mandatory)] [string] $MessageFragment
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message.IndexOf($MessageFragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $script:MermanPreparationTestChecks++
            return
        }
        throw "Expected Merman preparation failure '$MessageFragment', got '$($_.Exception.Message)'."
    }

    throw "Merman preparation test expected failure containing '$MessageFragment'."
}

function Write-TestManifest {
    param(
        [Parameter(Mandatory)] [object] $Manifest,
        [Parameter(Mandatory)] [string] $Path
    )

    $json = ($Manifest | ConvertTo-Json -Depth 20) + "`n"
    [System.IO.File]::WriteAllText(
        $Path,
        $json,
        [System.Text.UTF8Encoding]::new($false))
}

function Write-TestProvenanceForManifest {
    param(
        [Parameter(Mandatory)] [object] $Provenance,
        [Parameter(Mandatory)] [object] $Manifest,
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [string] $ProvenancePath
    )

    $copy = $Provenance | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $copy.sourcePatches.manifestSha256 = Get-MermanFileRawSha256 -Path $ManifestPath -Description 'Offline test patch manifest'
    $patchSetEntries = @($Manifest.patches | ForEach-Object {
        [pscustomobject]@{
            RelativePath = [string]$_.path
            Sha256 = [string]$_.sha256
        }
    })
    $copy.sourcePatches.patchSetSha256 = Get-MermanPatchSetSha256 -Patches $patchSetEntries
    Write-TestManifest -Manifest $copy -Path $ProvenancePath
}

function Invoke-TestPreparation {
    param(
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [string] $PatchDirectory,
        [Parameter(Mandatory)] [string] $ProvenancePath,
        [Parameter(Mandatory)] [string] $CargoTomlPath,
        [Parameter(Mandatory)] [string] $PreparationRoot,
        [Parameter(Mandatory)] [string] $PinnedRepositoryPath
    )

    return Invoke-MermanSourcePreparation `
        -ManifestPath $ManifestPath `
        -PatchDirectory $PatchDirectory `
        -ProvenancePath $ProvenancePath `
        -CargoTomlPath $CargoTomlPath `
        -PreparationRoot $PreparationRoot `
        -PinnedRepositoryPath $PinnedRepositoryPath
}

$projectRoot = Split-Path -Parent $nativeRoot
$manifestPath = Join-Path $nativeRoot 'patches/MERMAN_SOURCE_PATCHES.json'
$patchDirectory = Join-Path $nativeRoot 'patches'
$provenancePath = Join-Path $projectRoot 'MERMAN_PROVENANCE.json'
$cargoTomlPath = Join-Path $nativeRoot 'Cargo.toml'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The tracked Merman patch manifest must exist before its offline preparation tests can run: $manifestPath"
}

$provenance = [System.IO.File]::ReadAllText($provenancePath, [System.Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json -ErrorAction Stop
$revision = [string](Get-MermanProperty $provenance 'revision')
$PinnedRepositoryPath = (Resolve-Path -LiteralPath $PinnedRepositoryPath).Path

$resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
$runId = [guid]::NewGuid().ToString('N')
$testRoot = Join-Path $resolvedTemp ('MarkdownRenderer-Merman-Preparation-' + $runId)
$markerPath = Join-Path $testRoot '.offline-test-owner'
$tempRootCreated = $false

try {
    $null = New-Item -ItemType Directory -Path $testRoot
    $tempRootCreated = $true
    [System.IO.File]::WriteAllText($markerPath, $runId, [System.Text.UTF8Encoding]::new($false))

    $manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json -ErrorAction Stop
    $outputRoot = Join-Path $testRoot 'prepared'

    # A semantically equivalent manifest with a different raw representation
    # is still a different reviewed artifact. The patch-set identity remains
    # self-consistent, but provenance must reject the manifest swap.
    $manifestSwapPath = Join-Path $testRoot 'manifest-swap.json'
    $manifestSwapText = [System.IO.File]::ReadAllText($manifestPath, [System.Text.UTF8Encoding]::new($false, $true)) + " `n"
    [System.IO.File]::WriteAllText($manifestSwapPath, $manifestSwapText, [System.Text.UTF8Encoding]::new($false))
    $manifestSwap = [System.IO.File]::ReadAllText($manifestSwapPath, [System.Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json -ErrorAction Stop
    $originalPatchSetEntries = @($manifest.patches | ForEach-Object {
        [pscustomobject]@{ RelativePath = [string]$_.path; Sha256 = [string]$_.sha256 }
    })
    $swappedPatchSetEntries = @($manifestSwap.patches | ForEach-Object {
        [pscustomobject]@{ RelativePath = [string]$_.path; Sha256 = [string]$_.sha256 }
    })
    Assert-PreparationTest -Condition (
        (Get-MermanPatchSetSha256 -Patches $originalPatchSetEntries) -ceq
        (Get-MermanPatchSetSha256 -Patches $swappedPatchSetEntries)) -Message 'swapped manifest retains a self-consistent patch-set digest'
    $unusedManifestSwapRoot = Join-Path $testRoot 'manifest-swap-output'
    Assert-PreparationThrows -Action {
        Invoke-TestPreparation -ManifestPath $manifestSwapPath -PatchDirectory $patchDirectory `
            -ProvenancePath $provenancePath -CargoTomlPath $cargoTomlPath -PreparationRoot $unusedManifestSwapRoot `
            -PinnedRepositoryPath $PinnedRepositoryPath
    } -MessageFragment 'raw SHA-256 mismatch'
    Assert-PreparationTest -Condition (-not (Test-Path -LiteralPath $unusedManifestSwapRoot)) -Message 'self-consistent manifest swap is rejected before cloning'

    $result = Invoke-TestPreparation `
        -ManifestPath $manifestPath `
        -PatchDirectory $patchDirectory `
        -ProvenancePath $provenancePath `
        -CargoTomlPath $cargoTomlPath `
        -PreparationRoot $outputRoot `
        -PinnedRepositoryPath $PinnedRepositoryPath

    Assert-PreparationTest -Condition ($result.SourceRoot.StartsWith($outputRoot, [System.StringComparison]::OrdinalIgnoreCase)) -Message 'fresh source root is under the isolated output directory'
    Assert-PreparationTest -Condition ([string]::Equals($result.BaseCommit, $revision, [System.StringComparison]::Ordinal)) -Message 'result commit matches provenance'
    Assert-PreparationTest -Condition ($result.CargoArguments.Count -eq 4) -Message 'two explicit Cargo --config key/value pairs are returned'
    Assert-PreparationTest -Condition ($result.CargoArguments[1].Contains('merman-core.path=')) -Message 'Cargo override names merman-core'
    Assert-PreparationTest -Condition ($result.CargoArguments[3].Contains('merman-render.path=')) -Message 'Cargo override names merman-render'
    $cargoEscapedSourceRoot = $result.SourceRoot.Replace('\', '\\')
    Assert-PreparationTest -Condition ($result.CargoArguments[1].Contains($cargoEscapedSourceRoot)) -Message 'Cargo override points into the prepared source'
    Assert-PreparationTest -Condition (-not [string]::Equals($result.SourceRoot, $PinnedRepositoryPath, [System.StringComparison]::OrdinalIgnoreCase)) -Message 'prepared source is independent from the supplied Cargo checkout'

    $reuse = Invoke-TestPreparation `
        -ManifestPath $manifestPath `
        -PatchDirectory $patchDirectory `
        -ProvenancePath $provenancePath `
        -CargoTomlPath $cargoTomlPath `
        -PreparationRoot $outputRoot `
        -PinnedRepositoryPath $PinnedRepositoryPath
    Assert-PreparationTest -Condition ([string]::Equals($reuse.SourceRoot, $result.SourceRoot, [System.StringComparison]::OrdinalIgnoreCase)) -Message 'valid cache reuse returns the same prepared source root'

    $wrongBasePath = Join-Path $testRoot 'wrong-base.json'
    $wrongBase = $manifest | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $wrongBase.baseCommit = ('0' + $wrongBase.baseCommit.Substring(1))
    Write-TestManifest -Manifest $wrongBase -Path $wrongBasePath
    $wrongBaseProvenancePath = Join-Path $testRoot 'wrong-base-provenance.json'
    Write-TestProvenanceForManifest -Provenance $provenance -Manifest $wrongBase -ManifestPath $wrongBasePath -ProvenancePath $wrongBaseProvenancePath
    $unusedWrongBaseRoot = Join-Path $testRoot 'wrong-base-output'
    Assert-PreparationThrows -Action {
        Invoke-TestPreparation -ManifestPath $wrongBasePath -PatchDirectory $patchDirectory `
            -ProvenancePath $wrongBaseProvenancePath -CargoTomlPath $cargoTomlPath -PreparationRoot $unusedWrongBaseRoot `
            -PinnedRepositoryPath $PinnedRepositoryPath
    } -MessageFragment 'match the exact Cargo repository'
    Assert-PreparationTest -Condition (-not (Test-Path -LiteralPath $unusedWrongBaseRoot)) -Message 'wrong-base manifest fails before creating an output directory'

    $badHashPath = Join-Path $testRoot 'bad-patch-hash.json'
    $badHash = $manifest | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $badHash.patches[0].sha256 = ('0' * 64)
    Write-TestManifest -Manifest $badHash -Path $badHashPath
    $badHashProvenancePath = Join-Path $testRoot 'bad-patch-hash-provenance.json'
    Write-TestProvenanceForManifest -Provenance $provenance -Manifest $badHash -ManifestPath $badHashPath -ProvenancePath $badHashProvenancePath
    $unusedBadHashRoot = Join-Path $testRoot 'bad-hash-output'
    Assert-PreparationThrows -Action {
        Invoke-TestPreparation -ManifestPath $badHashPath -PatchDirectory $patchDirectory `
            -ProvenancePath $badHashProvenancePath -CargoTomlPath $cargoTomlPath -PreparationRoot $unusedBadHashRoot `
            -PinnedRepositoryPath $PinnedRepositoryPath
    } -MessageFragment 'SHA-256 mismatch'
    Assert-PreparationTest -Condition (-not (Test-Path -LiteralPath $unusedBadHashRoot)) -Message 'bad patch digest fails before cloning'

    $traversalPath = Join-Path $testRoot 'patch-traversal.json'
    $traversal = $manifest | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $traversal.patches[0].path = '../' + $traversal.patches[0].path
    Write-TestManifest -Manifest $traversal -Path $traversalPath
    $traversalProvenancePath = Join-Path $testRoot 'patch-traversal-provenance.json'
    Write-TestProvenanceForManifest -Provenance $provenance -Manifest $traversal -ManifestPath $traversalPath -ProvenancePath $traversalProvenancePath
    $unusedTraversalRoot = Join-Path $testRoot 'traversal-output'
    Assert-PreparationThrows -Action {
        Invoke-TestPreparation -ManifestPath $traversalPath -PatchDirectory $patchDirectory `
            -ProvenancePath $traversalProvenancePath -CargoTomlPath $cargoTomlPath -PreparationRoot $unusedTraversalRoot `
            -PinnedRepositoryPath $PinnedRepositoryPath
    } -MessageFragment 'Unsafe, duplicate, or unsupported patch path'
    Assert-PreparationTest -Condition (-not (Test-Path -LiteralPath $unusedTraversalRoot)) -Message 'traversal manifest fails before cloning'

    $firstFile = $manifest.files[0]
    $preparedFile = Join-Path $result.SourceRoot ($firstFile.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    $originalBytes = [System.IO.File]::ReadAllBytes($preparedFile)
    try {
        $tamperedBytes = [System.Text.UTF8Encoding]::new($false).GetBytes("`n# offline cache tamper")
        $combined = [byte[]]::new($originalBytes.Length + $tamperedBytes.Length)
        [System.Buffer]::BlockCopy($originalBytes, 0, $combined, 0, $originalBytes.Length)
        [System.Buffer]::BlockCopy($tamperedBytes, 0, $combined, $originalBytes.Length, $tamperedBytes.Length)
        [System.IO.File]::WriteAllBytes($preparedFile, $combined)
        Assert-PreparationThrows -Action {
            Invoke-TestPreparation -ManifestPath $manifestPath -PatchDirectory $patchDirectory `
                -ProvenancePath $provenancePath -CargoTomlPath $cargoTomlPath -PreparationRoot $outputRoot `
                -PinnedRepositoryPath $PinnedRepositoryPath
        } -MessageFragment 'SHA-256 mismatch'
    }
    finally {
        [System.IO.File]::WriteAllBytes($preparedFile, $originalBytes)
    }

    # This mutation preserves the normalized source text but changes only its
    # line-ending bytes; the cache verifier must reject the compiled-byte drift.
    $originalText = $script:MermanUtf8Strict.GetString($originalBytes)
    $normalizedText = $originalText.Replace("`r`n", "`n").Replace("`r", "`n")
    Assert-PreparationTest -Condition $normalizedText.Contains("`n") -Message 'selected Rust fixture has line endings to mutate'
    $crlfText = $normalizedText.Replace("`n", "`r`n")
    $crlfBytes = $script:MermanUtf8NoBom.GetBytes($crlfText)
    $roundTrippedNormalizedText = $script:MermanUtf8Strict.GetString($crlfBytes).Replace("`r`n", "`n").Replace("`r", "`n")
    Assert-PreparationTest -Condition ($roundTrippedNormalizedText -ceq $normalizedText) -Message 'CRLF mutation preserves normalized source text'
    Assert-PreparationTest -Condition (
        (Get-MermanRawSha256FromBytes -Bytes $crlfBytes -Description 'Line-ending test source') -cne
        (Get-MermanRawSha256FromBytes -Bytes $originalBytes -Description 'Original test source')) -Message 'CRLF mutation changes exact source-byte digest'
    try {
        [System.IO.File]::WriteAllBytes($preparedFile, $crlfBytes)
        Assert-PreparationThrows -Action {
            Invoke-TestPreparation -ManifestPath $manifestPath -PatchDirectory $patchDirectory `
                -ProvenancePath $provenancePath -CargoTomlPath $cargoTomlPath -PreparationRoot $outputRoot `
                -PinnedRepositoryPath $PinnedRepositoryPath
        } -MessageFragment 'SHA-256 mismatch'
    }
    finally {
        [System.IO.File]::WriteAllBytes($preparedFile, $originalBytes)
    }

    $excludePath = Join-Path $result.SourceRoot '.git/info/exclude'
    $excludeOriginal = [System.IO.File]::ReadAllBytes($excludePath)
    $ignoredName = '.markdownrenderer-offline-' + $runId
    $ignoredPath = Join-Path $result.SourceRoot $ignoredName
    try {
        [System.IO.File]::AppendAllText($excludePath, "`n$ignoredName`n", [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($ignoredPath, 'unexpected ignored artifact', [System.Text.UTF8Encoding]::new($false))
        Assert-PreparationThrows -Action {
            Invoke-TestPreparation -ManifestPath $manifestPath -PatchDirectory $patchDirectory `
                -ProvenancePath $provenancePath -CargoTomlPath $cargoTomlPath -PreparationRoot $outputRoot `
                -PinnedRepositoryPath $PinnedRepositoryPath
        } -MessageFragment 'worktree/index/untracked/ignored entries'
    }
    finally {
        if (Test-Path -LiteralPath $ignoredPath) {
            Remove-Item -LiteralPath $ignoredPath -Force
        }
        [System.IO.File]::WriteAllBytes($excludePath, $excludeOriginal)
    }

    $recovered = Invoke-TestPreparation `
        -ManifestPath $manifestPath `
        -PatchDirectory $patchDirectory `
        -ProvenancePath $provenancePath `
        -CargoTomlPath $cargoTomlPath `
        -PreparationRoot $outputRoot `
        -PinnedRepositoryPath $PinnedRepositoryPath
    Assert-PreparationTest -Condition ([string]::Equals($recovered.SourceRoot, $result.SourceRoot, [System.StringComparison]::OrdinalIgnoreCase)) -Message 'cache becomes reusable after the exact test-only mutations are restored'

    # Schema 2 adds explicit, hash-pinned ordinary source files. Keep the real
    # manifest checks above intact, and exercise additions in this owned clone.
    $addedPatchDirectory = Join-Path $testRoot 'schema2-patches'
    $null = New-Item -ItemType Directory -Path $addedPatchDirectory
    foreach ($patch in $manifest.patches) {
        Copy-Item -LiteralPath (Join-Path $patchDirectory $patch.path) -Destination (Join-Path $addedPatchDirectory $patch.path)
    }
    $addedRelativePath = 'crates/merman-core/src/markdown_renderer_prepare_fixture.rs'
    $addedContent = "pub const PREPARATION_FIXTURE: bool = true;`n"
    $addedPatchName = '9999-offline-added-source.patch'
    $addedPatchPath = Join-Path $addedPatchDirectory $addedPatchName
    $addedPatch = @"
diff --git a/$addedRelativePath b/$addedRelativePath
new file mode 100644
--- /dev/null
+++ b/$addedRelativePath
@@ -0,0 +1 @@
+pub const PREPARATION_FIXTURE: bool = true;
"@ + "`n"
    [System.IO.File]::WriteAllText($addedPatchPath, $addedPatch, $script:MermanUtf8NoBom)
    $schema2 = $manifest | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $schema2.schemaVersion = 2
    $schema2.files = @($schema2.files | ForEach-Object {
        $kind = if ($_.PSObject.Properties['changeKind']) { $_.changeKind } else { 'modify' }
        [pscustomobject]@{ path = $_.path; changeKind = $kind; upstreamSha256 = $_.upstreamSha256; patchedSha256 = $_.patchedSha256 }
    }) + @([pscustomobject]@{
        path = $addedRelativePath; changeKind = 'add'; upstreamSha256 = $null
        patchedSha256 = Get-MermanRawSha256FromBytes -Bytes $script:MermanUtf8NoBom.GetBytes($addedContent) -Description 'Added source test fixture'
    })
    $schema2.patches = @($schema2.patches) + @([pscustomobject]@{
        path = $addedPatchName
        sha256 = Get-MermanFileRawSha256 -Path $addedPatchPath -Description 'Added source test patch'
    })
    $schema2ManifestPath = Join-Path $testRoot 'schema2-manifest.json'
    $schema2ProvenancePath = Join-Path $testRoot 'schema2-provenance.json'
    Write-TestManifest -Manifest $schema2 -Path $schema2ManifestPath
    Write-TestProvenanceForManifest -Provenance $provenance -Manifest $schema2 -ManifestPath $schema2ManifestPath -ProvenancePath $schema2ProvenancePath
    $schema2Output = Join-Path $testRoot 'schema2-prepared'
    $schema2Arguments = @{
        ManifestPath = $schema2ManifestPath; PatchDirectory = $addedPatchDirectory
        ProvenancePath = $schema2ProvenancePath; CargoTomlPath = $cargoTomlPath
        PreparationRoot = $schema2Output; PinnedRepositoryPath = $PinnedRepositoryPath
    }
    $withAddition = Invoke-TestPreparation @schema2Arguments
    $addedSourcePath = Join-Path $withAddition.SourceRoot $addedRelativePath
    Assert-PreparationTest -Condition ([System.IO.File]::ReadAllText($addedSourcePath) -ceq $addedContent) -Message 'schema 2 publishes the exact declared added source bytes'
    $addedReuse = Invoke-TestPreparation @schema2Arguments
    Assert-PreparationTest -Condition ($addedReuse.SourceRoot -ceq $withAddition.SourceRoot) -Message 'schema 2 revalidates and reuses its mixed modified/added cache'
    $inspectionRepository = Join-Path $testRoot 'patch-inspection-repository'
    $null = New-Item -ItemType Directory -Path $inspectionRepository
    $null = Invoke-MermanGitText -Repository $inspectionRepository -Arguments @('init')
    $nestedPatches = Join-Path $inspectionRepository 'nested/patches'
    $null = New-Item -ItemType Directory -Path $nestedPatches -Force
    foreach ($patch in $schema2.patches) {
        Copy-Item -LiteralPath (Join-Path $addedPatchDirectory $patch.path) -Destination (Join-Path $nestedPatches $patch.path)
    }
    $nestedArguments = $schema2Arguments.Clone()
    $nestedArguments.PatchDirectory = $nestedPatches
    $nestedReuse = Invoke-TestPreparation @nestedArguments
    Assert-PreparationTest -Condition ($nestedReuse.SourceRoot -ceq $withAddition.SourceRoot) -Message 'patch summary sees all additions when patch directory is nested inside a Git checkout'
    try {
        [System.IO.File]::AppendAllText($addedSourcePath, "// tampered`n", $script:MermanUtf8NoBom)
        Assert-PreparationThrows -Action { Invoke-TestPreparation @schema2Arguments } -MessageFragment 'SHA-256 mismatch'
    }
    finally {
        [System.IO.File]::WriteAllText($addedSourcePath, $addedContent, $script:MermanUtf8NoBom)
    }
    $heldAddedSource = Join-Path $testRoot 'held-added-source.rs'
    try {
        [System.IO.File]::Move($addedSourcePath, $heldAddedSource)
        Assert-PreparationThrows -Action { Invoke-TestPreparation @schema2Arguments } -MessageFragment 'worktree/index/untracked/ignored entries'
    }
    finally {
        if ([System.IO.File]::Exists($heldAddedSource)) { [System.IO.File]::Move($heldAddedSource, $addedSourcePath) }
    }
    $extraAddedSource = Join-Path $withAddition.SourceRoot 'crates/merman-core/src/undeclared_prepare_fixture.rs'
    try {
        [System.IO.File]::WriteAllText($extraAddedSource, '// undeclared', $script:MermanUtf8NoBom)
        Assert-PreparationThrows -Action { Invoke-TestPreparation @schema2Arguments } -MessageFragment 'worktree/index/untracked/ignored entries'
    }
    finally {
        if ([System.IO.File]::Exists($extraAddedSource)) { [System.IO.File]::Delete($extraAddedSource) }
    }
    try {
        $null = Invoke-MermanGitText -Repository $withAddition.SourceRoot -Arguments @('add', '--', $addedRelativePath)
        Assert-PreparationThrows -Action { Invoke-TestPreparation @schema2Arguments } -MessageFragment 'changed-file count'
    }
    finally {
        $null = Invoke-MermanGitText -Repository $withAddition.SourceRoot -Arguments @('update-index', '--force-remove', '--', $addedRelativePath)
    }

    function Assert-Schema2ManifestRejected {
        param([object] $Candidate, [string] $Name, [string] $MessageFragment)
        $candidatePath = Join-Path $testRoot ($Name + '.json')
        $candidateProvenance = Join-Path $testRoot ($Name + '-provenance.json')
        Write-TestManifest -Manifest $Candidate -Path $candidatePath
        Write-TestProvenanceForManifest -Provenance $provenance -Manifest $Candidate -ManifestPath $candidatePath -ProvenancePath $candidateProvenance
        Assert-PreparationThrows -Action {
            Invoke-TestPreparation -ManifestPath $candidatePath -PatchDirectory $addedPatchDirectory `
                -ProvenancePath $candidateProvenance -CargoTomlPath $cargoTomlPath -PreparationRoot $schema2Output `
                -PinnedRepositoryPath $PinnedRepositoryPath
        } -MessageFragment $MessageFragment
    }

    $badAddedHash = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $badAddedHash.files[-1].patchedSha256 = ('0' * 64)
    Assert-Schema2ManifestRejected -Candidate $badAddedHash -Name 'schema2-added-hash' -MessageFragment 'SHA-256 mismatch'
    Assert-PreparationTest -Condition ([System.IO.File]::ReadAllText($addedSourcePath) -ceq $addedContent) -Message 'cache rejection never replaces the original added source'
    $inventedBase = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $inventedBase.files[-1].upstreamSha256 = ('0' * 64)
    Assert-Schema2ManifestRejected -Candidate $inventedBase -Name 'schema2-invented-base' -MessageFragment 'upstreamSha256 as null'
    $badKind = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $badKind.files[-1].changeKind = 'ADD'
    Assert-Schema2ManifestRejected -Candidate $badKind -Name 'schema2-bad-kind' -MessageFragment 'changeKind'
    $stringSchema = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $stringSchema.schemaVersion = '2'
    Assert-Schema2ManifestRejected -Candidate $stringSchema -Name 'schema2-string-version' -MessageFragment 'must use schema 1 or 2'
    $floatSchema = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $floatSchema.schemaVersion = [double]2
    Assert-Schema2ManifestRejected -Candidate $floatSchema -Name 'schema2-float-version' -MessageFragment 'must use schema 1 or 2'
    $duplicateAddition = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $alias = $duplicateAddition.files[-1] | ConvertTo-Json | ConvertFrom-Json
    $alias.path = $alias.path.ToUpperInvariant()
    $duplicateAddition.files = @($duplicateAddition.files) + @($alias)
    Assert-Schema2ManifestRejected -Candidate $duplicateAddition -Name 'schema2-duplicate-add' -MessageFragment 'Duplicate Merman changed-file path'
    $traversalAddition = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $traversalAddition.files[-1].path = 'crates/merman-core/src/../escaped.rs'
    Assert-Schema2ManifestRejected -Candidate $traversalAddition -Name 'schema2-add-traversal' -MessageFragment 'Unsafe or unsupported Merman source path'
    $dotAddition = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $dotAddition.files[-1].path = 'crates/merman-core/src/./aliased.rs'
    Assert-Schema2ManifestRejected -Candidate $dotAddition -Name 'schema2-add-dot-segment' -MessageFragment 'Unsafe or unsupported Merman source path'
    $missingCreation = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $missingCreation.patches = @($missingCreation.patches | Where-Object { $_.path -cne $addedPatchName })
    Assert-Schema2ManifestRejected -Candidate $missingCreation -Name 'schema2-missing-creation' -MessageFragment 'exactly one reviewed ordinary-file patch'

    # Existing tracked paths cannot be reclassified as additions, even when
    # patch and manifest digests are internally consistent.
    $collision = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $collisionPath = [string]$manifest.files[0].path
    $collision.files = @($collision.files | Where-Object { $_.path -cne $collisionPath })
    $collision.files[-1].path = $collisionPath
    $collisionPatchName = '9998-offline-base-collision.patch'
    $collisionPatchPath = Join-Path $addedPatchDirectory $collisionPatchName
    [System.IO.File]::WriteAllText($collisionPatchPath, $addedPatch.Replace($addedRelativePath, $collisionPath), $script:MermanUtf8NoBom)
    $collision.patches[-1].path = $collisionPatchName
    $collision.patches[-1].sha256 = Get-MermanFileRawSha256 -Path $collisionPatchPath -Description 'Base collision test patch'
    Assert-Schema2ManifestRejected -Candidate $collision -Name 'schema2-base-collision' -MessageFragment 'must be absent from the pinned base tree'

    $newParent = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $newParentPath = 'crates/merman-core/src/undeclared_new_directory/fixture.rs'
    $newParent.files[-1].path = $newParentPath
    $newParentPatchName = '9996-offline-new-parent.patch'
    $newParentPatchPath = Join-Path $addedPatchDirectory $newParentPatchName
    [System.IO.File]::WriteAllText($newParentPatchPath, $addedPatch.Replace($addedRelativePath, $newParentPath), $script:MermanUtf8NoBom)
    $newParent.patches[-1].path = $newParentPatchName
    $newParent.patches[-1].sha256 = Get-MermanFileRawSha256 -Path $newParentPatchPath -Description 'Absent parent test patch'
    Assert-Schema2ManifestRejected -Candidate $newParent -Name 'schema2-absent-parent' -MessageFragment 'source parent must already be an ordinary directory'
    $newParentSet = @($newParent.patches | ForEach-Object { [pscustomobject]@{ RelativePath = $_.path; Sha256 = $_.sha256 } })
    Assert-PreparationTest -Condition (-not (Test-Path -LiteralPath (Join-Path $schema2Output (Get-MermanPatchSetSha256 -Patches $newParentSet)))) -Message 'failed added-parent admission publishes no final prepared root'

    $parentProbe = Join-Path $testRoot 'parent-probe'
    $parentTarget = Join-Path $testRoot 'parent-target'
    $null = New-Item -ItemType Directory -Path (Join-Path $parentProbe 'crates'), $parentTarget
    $parentLink = Join-Path $parentProbe 'crates/merman-core'
    try {
        $null = New-Item -ItemType Junction -Path $parentLink -Target $parentTarget
        Assert-PreparationThrows -Action {
            Get-MermanOrdinarySourcePath -SourceRoot $parentProbe -RelativePath 'crates/merman-core/fixture.rs'
        } -MessageFragment 'not a file or reparse point'
    }
    finally {
        if (Get-Item -LiteralPath $parentLink -Force -ErrorAction SilentlyContinue) {
            # Remove only this owned link, never its target or a recursive tree.
            [System.IO.Directory]::Delete($parentLink)
        }
    }

    $badMode = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $badModePatchName = '9997-offline-symlink-mode.patch'
    $badModePatchPath = Join-Path $addedPatchDirectory $badModePatchName
    [System.IO.File]::WriteAllText($badModePatchPath, $addedPatch.Replace('new file mode 100644', 'new file mode 120000'), $script:MermanUtf8NoBom)
    $badMode.patches[-1].path = $badModePatchName
    $badMode.patches[-1].sha256 = Get-MermanFileRawSha256 -Path $badModePatchPath -Description 'Symlink mode test patch'
    Assert-Schema2ManifestRejected -Candidate $badMode -Name 'schema2-bad-mode' -MessageFragment 'unsupported mode/type change'
    $executableMode = $schema2 | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $executablePatchName = '9995-offline-executable-mode.patch'
    $executablePatchPath = Join-Path $addedPatchDirectory $executablePatchName
    [System.IO.File]::WriteAllText($executablePatchPath, $addedPatch.Replace('new file mode 100644', 'new file mode 100755'), $script:MermanUtf8NoBom)
    $executableMode.patches[-1].path = $executablePatchName
    $executableMode.patches[-1].sha256 = Get-MermanFileRawSha256 -Path $executablePatchPath -Description 'Executable mode test patch'
    Assert-Schema2ManifestRejected -Candidate $executableMode -Name 'schema2-executable-mode' -MessageFragment 'unsupported mode/type change'
    $finalAdditionReuse = Invoke-TestPreparation @schema2Arguments
    Assert-PreparationTest -Condition ($finalAdditionReuse.SourceRoot -ceq $withAddition.SourceRoot) -Message 'schema 2 remains reusable after all exact test-only mutations are restored'

    $readOnlyFixture = Join-Path $testRoot 'readonly-cleanup-fixture.txt'
    [System.IO.File]::WriteAllText($readOnlyFixture, 'owned cleanup fixture', $script:MermanUtf8NoBom)
    [System.IO.File]::SetAttributes($readOnlyFixture, [System.IO.FileAttributes]::ReadOnly)
    Assert-PreparationTest -Condition (([System.IO.File]::GetAttributes($readOnlyFixture) -band [System.IO.FileAttributes]::ReadOnly) -ne 0) -Message 'owned test cleanup includes readonly Git-object-like files'
    $report = [pscustomobject]@{
        Result = 'PASS'
        Checks = $script:MermanPreparationTestChecks
        BaseCommit = $result.BaseCommit
        BaseTree = $result.BaseTree
        PatchSetSha256 = $result.PatchSetSha256
        PreparedSourceRoot = $result.SourceRoot
        OfflinePinnedSourcePath = $PinnedRepositoryPath
        Mutations = @('self-consistent manifest swap', 'wrong base', 'patch digest', 'path traversal', 'cached source edit', 'line-ending-only source mutation', 'ignored extra file', 'schema 2 added-source edit/missing/extra/index/hash/base/path/mode mutations')
        SharedCargoCheckoutWritten = $false
        TemporaryTestRootRemoved = $true
    }
}
finally {
    if ($tempRootCreated) {
        $resolvedRoot = [System.IO.Path]::GetFullPath($testRoot)
        $tempPrefix = $resolvedTemp.TrimEnd('\') + '\'
        $markerIsOwned = (Test-Path -LiteralPath $markerPath -PathType Leaf) -and
            ([System.IO.File]::ReadAllText($markerPath) -ceq $runId)
        if (-not $resolvedRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedRoot) -cne ('MarkdownRenderer-Merman-Preparation-' + $runId) -or
            -not $markerIsOwned) {
            throw "Refusing to remove an unverified Merman offline-test directory: $resolvedRoot"
        }
        # Git marks loose objects read-only. Remove that attribute only inside
        # this verified, fresh GUID-owned fixture. Never follow reparse points.
        $directories = [Collections.Generic.Stack[string]]::new()
        $directories.Push($resolvedRoot)
        while ($directories.Count -gt 0) {
            $directory = $directories.Pop()
            Assert-MermanDirectoryIsOrdinary -Path $directory
            foreach ($child in [System.IO.Directory]::EnumerateDirectories($directory)) {
                Assert-MermanDirectoryIsOrdinary -Path $child
                $directories.Push($child)
            }
            foreach ($file in [System.IO.Directory]::EnumerateFiles($directory)) {
                Assert-MermanFileIsOrdinary -Path $file
                $attributes = [System.IO.File]::GetAttributes($file)
                if (($attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                    [System.IO.File]::SetAttributes($file, $attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly))
                }
            }
        }
        [System.IO.Directory]::Delete($resolvedRoot, $true)
    }
}

# A PASS receipt is published only after the owned fixture cleanup succeeds.
Write-Output $report
