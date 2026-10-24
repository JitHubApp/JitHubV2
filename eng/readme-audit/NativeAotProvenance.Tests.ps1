$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'NativeAotProvenance.psm1'
$newProvenanceScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'New-NativeAotProvenance.ps1'
Import-Module $modulePath -Force

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$tempRoot = Join-Path $tempParent "jithub-native-aot-provenance-tests-$([Guid]::NewGuid().ToString('N'))"
$resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
if (-not $resolvedTempRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create NativeAOT provenance fixtures outside the temporary directory.'
}
New-Item -ItemType Directory -Path $resolvedTempRoot | Out-Null

$sourceCommitSha = '0123456789abcdef0123456789abcdef01234567'
$workflowRunId = '12345678901'
$workflowRepository = 'JitHubApp/JitHubV2'
$workflowRef = 'refs/heads/codex/markdown-renderer-1-0-finalize'

function New-ArtifactFixture([string]$Name) {
    $root = Join-Path $resolvedTempRoot $Name
    New-Item -ItemType Directory -Path $root | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'runtimes\win-x64') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $root 'JitHub.WinUI.exe'), [Text.Encoding]::UTF8.GetBytes('fixture native executable'))
    [IO.File]::WriteAllBytes((Join-Path $root 'runtimes\win-x64\worker.exe'), [Text.Encoding]::UTF8.GetBytes('fixture worker'))
    return $root
}

function Add-Provenance([string]$Root) {
    $provenance = New-NativeAotProvenance `
        -InputPath $Root `
        -SourceCommitSha $sourceCommitSha `
        -WorkflowRunId $workflowRunId `
        -WorkflowRepository $workflowRepository `
        -WorkflowRef $workflowRef
    $path = Join-Path $Root 'native-aot-provenance.json'
    [IO.File]::WriteAllText($path, ($provenance | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    return $path
}

function Assert-Rejected([string]$Root, [string]$SidecarPath, [string]$Name, [scriptblock]$Mutation) {
    & $Mutation $Root $SidecarPath
    $rejected = $false
    try {
        $null = Test-NativeAotProvenance `
            -InputPath $Root `
            -ProvenancePath $SidecarPath `
            -ExpectedSourceCommitSha $sourceCommitSha `
            -ExpectedWorkflowRunId $workflowRunId `
            -ExpectedWorkflowRepository $workflowRepository `
            -ExpectedWorkflowRef $workflowRef
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw "NativeAOT provenance accepted the '$Name' mutation." }
}

try {
    $validRoot = New-ArtifactFixture 'valid'
    $validSidecar = Add-Provenance $validRoot
    $verified = Test-NativeAotProvenance `
        -InputPath $validRoot `
        -ProvenancePath $validSidecar `
        -ExpectedSourceCommitSha $sourceCommitSha `
        -ExpectedWorkflowRunId $workflowRunId `
        -ExpectedWorkflowRepository $workflowRepository `
        -ExpectedWorkflowRef $workflowRef
    if ($verified.Provenance.provenanceVerified -ne $true -or
        $verified.Provenance.workingTreeClean -ne $true -or
        @($verified.Provenance.artifactFileSha256.PSObject.Properties).Count -ne 2 -or
        $verified.Sha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'The valid canonical NativeAOT provenance fixture did not verify completely.'
    }

    $mismatchRoot = New-ArtifactFixture 'expected-identity-mismatch'
    $mismatchSidecar = Add-Provenance $mismatchRoot
    $rejectedWrongCommit = $false
    try {
        $null = Test-NativeAotProvenance -InputPath $mismatchRoot -ProvenancePath $mismatchSidecar `
            -ExpectedSourceCommitSha ('f' * 40)
    }
    catch { $rejectedWrongCommit = $true }
    if (-not $rejectedWrongCommit) { throw 'Provenance accepted an artifact from a different source commit.' }

    foreach ($mutation in @(
        @{ Name = 'modified executable'; Action = { param($root, $sidecar) [IO.File]::WriteAllText((Join-Path $root 'JitHub.WinUI.exe'), 'tampered') } },
        @{ Name = 'added artifact file'; Action = { param($root, $sidecar) [IO.File]::WriteAllText((Join-Path $root 'extra.dll'), 'unexpected') } },
        @{ Name = 'removed artifact file'; Action = { param($root, $sidecar) [IO.File]::Delete((Join-Path $root 'runtimes\win-x64\worker.exe')) } },
        @{ Name = 'tampered provenance commit'; Action = { param($root, $sidecar) $value = Get-Content -LiteralPath $sidecar -Raw | ConvertFrom-Json; $value.sourceCommitSha = 'f' * 40; $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $sidecar -Encoding utf8 } },
        @{ Name = 'tampered artifact hash'; Action = { param($root, $sidecar) $value = Get-Content -LiteralPath $sidecar -Raw | ConvertFrom-Json; $value.artifactFileSha256.'JitHub.WinUI.exe' = 'f' * 64; $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $sidecar -Encoding utf8 } }
    )) {
        $root = New-ArtifactFixture ([Guid]::NewGuid().ToString('N'))
        $sidecar = Add-Provenance $root
        Assert-Rejected $root $sidecar $mutation.Name $mutation.Action
    }

    $missingRoot = New-ArtifactFixture 'missing-sidecar'
    $missingRejected = $false
    try {
        $null = Test-NativeAotProvenance -InputPath $missingRoot `
            -ProvenancePath (Join-Path $missingRoot 'native-aot-provenance.json')
    }
    catch { $missingRejected = $true }
    if (-not $missingRejected) { throw 'The provenance verifier accepted a missing sidecar.' }

    $outsideRoot = New-ArtifactFixture 'outside-sidecar'
    $outsideSidecar = Join-Path $resolvedTempRoot 'outside.json'
    [IO.File]::WriteAllText($outsideSidecar, '{}')
    $outsideRejected = $false
    try { $null = Test-NativeAotProvenance -InputPath $outsideRoot -ProvenancePath $outsideSidecar }
    catch { $outsideRejected = $true }
    if (-not $outsideRejected) { throw 'The provenance verifier accepted an external sidecar path.' }

    # The publisher refuses to attach a commit label to a dirty checkout.
    $gitRoot = Join-Path $resolvedTempRoot 'source-repository'
    New-Item -ItemType Directory -Path $gitRoot | Out-Null
    & git -C $gitRoot init -q
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the provenance test Git repository.' }
    & git -C $gitRoot config user.email 'audit@example.invalid'
    & git -C $gitRoot config user.name 'Audit Test'
    [IO.File]::WriteAllText((Join-Path $gitRoot 'source.txt'), 'clean source')
    & git -C $gitRoot add source.txt
    & git -C $gitRoot commit -q -m 'fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit the provenance test Git repository.' }
    $gitCommit = ([string](& git -C $gitRoot rev-parse HEAD)).Trim()
    $publisherRoot = New-ArtifactFixture 'publisher'
    & $newProvenanceScript `
        -InputPath $publisherRoot `
        -RepositoryPath $gitRoot `
        -SourceCommitSha $gitCommit `
        -WorkflowRunId $workflowRunId `
        -WorkflowRepository $workflowRepository `
        -WorkflowRef $workflowRef | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $publisherRoot 'native-aot-provenance.json'))) {
        throw 'The publisher did not produce provenance from a clean source tree.'
    }

    [IO.File]::WriteAllText((Join-Path $gitRoot 'dirty.txt'), 'uncommitted')
    $dirtyRejected = $false
    try {
        & $newProvenanceScript `
            -InputPath $publisherRoot `
            -RepositoryPath $gitRoot `
            -SourceCommitSha $gitCommit `
            -WorkflowRunId $workflowRunId `
            -WorkflowRepository $workflowRepository `
            -WorkflowRef $workflowRef | Out-Null
    }
    catch { $dirtyRejected = $true }
    if (-not $dirtyRejected) { throw 'The publisher labeled a dirty source tree as verified provenance.' }

    Write-Host 'NativeAOT provenance tests passed: clean source binding, expected run identity, full payload verification, tamper rejection, missing/external sidecar rejection, and dirty-tree refusal.'
}
finally {
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        $actual = [IO.Path]::GetFullPath($resolvedTempRoot)
        if (-not $actual.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($actual) -notmatch '^jithub-native-aot-provenance-tests-[0-9a-f]{32}$') {
            throw 'Temporary NativeAOT provenance test path changed unexpectedly; preserving it.'
        }
        Remove-Item -LiteralPath $actual -Recurse -Force
    }
}
