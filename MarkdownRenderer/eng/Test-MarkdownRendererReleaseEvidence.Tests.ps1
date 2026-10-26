$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$validator = Join-Path $PSScriptRoot 'Test-MarkdownRendererReleaseEvidence.ps1'
$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "markdown-renderer-release-join-$([guid]::NewGuid().ToString('N'))"))
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null

function Write-TestJson {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][object]$Value)
    [IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 30) + "`n"),
        [Text.UTF8Encoding]::new($false, $true))
}

function Copy-TestObject {
    param([Parameter(Mandatory)][object]$Value)
    return (($Value | ConvertTo-Json -Depth 30) | ConvertFrom-Json -Depth 30)
}

function Invoke-TestGit {
    param([Parameter(Mandatory)][string]$RepositoryPath, [Parameter(Mandatory)][string[]]$Arguments)
    $output = @(& git -C $RepositoryPath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $($output -join ' ')" }
    return ($output | ForEach-Object { [string]$_ }) -join "`n"
}

function Invoke-Validator {
    param(
        [Parameter(Mandatory)][string]$VerdictPath,
        [Parameter(Mandatory)][string]$OutputPath
    )
    $expectedArguments = @(
        '-CounterbalancedVerdictPath', $VerdictPath,
        '-OutputPath', $OutputPath,
        '-ExpectedCandidateCommitSha', $candidateSha,
        '-ExpectedReferenceCommitSha', $referenceSha,
        '-ExpectedWorkflowRunId', $runId,
        '-ExpectedWorkflowRepository', $repository,
        '-ExpectedWorkflowRef', $workflowRef,
        '-PhysicalJobResult', 'success')
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Process -Id $PID).Path
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('-NoLogo')
    $start.ArgumentList.Add('-NoProfile')
    $start.ArgumentList.Add('-File')
    $start.ArgumentList.Add($validator)
    foreach ($argument in $expectedArguments) { $start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not start the release evidence validator test process.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

function Assert-ValidatorRejects {
    param(
        [Parameter(Mandatory)][string] $VerdictPath,
        [Parameter(Mandatory)][string] $OutputPath,
        [Parameter(Mandatory)][string] $Label
    )
    $result = Invoke-Validator -VerdictPath $VerdictPath -OutputPath $OutputPath
    if ($result.ExitCode -eq 0) { throw "$Label was accepted by the joined-evidence validator." }
}

function Assert-PhysicalArtifactRetryContract {
    $workflowPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.github\workflows\markdown-renderer-physical-release.yml'))
    if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
        throw "Physical release workflow is missing: '$workflowPath'."
    }
    $workflow = [IO.File]::ReadAllText($workflowPath)
    $requiredSource = @(
        'artifact_name: ${{ steps.physical_artifact_name.outputs.name }}',
        'id: physical_artifact_name',
        '$artifactName = "markdown-renderer-counterbalanced-$env:GITHUB_RUN_ATTEMPT"',
        'name: ${{ steps.physical_artifact_name.outputs.name }}',
        'name: ${{ needs.physical.outputs.artifact_name }}',
        'path: ${{ runner.temp }}/markdown-renderer-release-${{ github.run_id }}-${{ github.run_attempt }}/**')
    foreach ($source in $requiredSource) {
        if (-not $workflow.Contains($source)) {
            throw "Physical artifact retry contract is missing source: '$source'."
        }
    }

    $nameStepIndex = $workflow.IndexOf('id: physical_artifact_name', [StringComparison]::Ordinal)
    $uploadStepIndex = $workflow.IndexOf('- name: Upload physical evidence', [StringComparison]::Ordinal)
    if ($nameStepIndex -lt 0 -or $uploadStepIndex -le $nameStepIndex) {
        throw 'The physical attempt-scoped artifact name must be published before upload consumes it.'
    }
    if ($workflow.Contains('name: markdown-renderer-counterbalanced-${{ github.run_attempt }}')) {
        throw 'The join must consume the successful physical job output instead of recomputing the current attempt name.'
    }
}

$candidateSha = '1' * 40
$referenceSha = '2' * 40
$runId = '123456789'
$repository = 'jithubapp/jithubv2'
$workflowRef = 'refs/heads/release-candidate'
$provenance = [ordered]@{
    workflowRunId = $runId
    workflowRepository = $repository
    workflowRef = $workflowRef
    reference = [ordered]@{
        buildRole = 'reference'
        sourceCommitSha = $referenceSha
        sourceTreeSha = 'A' * 40
        workingTreeClean = $true
        workflowRunId = $runId
        workflowRepository = $repository
        workflowRef = $workflowRef
        sha256 = 'C' * 64
    }
    candidate = [ordered]@{
        buildRole = 'candidate'
        sourceCommitSha = $candidateSha
        sourceTreeSha = 'B' * 40
        workingTreeClean = $true
        workflowRunId = $runId
        workflowRepository = $repository
        workflowRef = $workflowRef
        sha256 = 'D' * 64
    }
}
$physical = [ordered]@{
    schemaVersion = 3
    providerName = 'MarkdownRenderer-Counterbalanced-Performance'
    policy = 'schema12-r1-c1-c2-r2-abba-contrast-v3-source-provenance'
    runId = 'run-test'
    sequence = @('R1', 'C1', 'C2', 'R2')
    complete = $true
    passed = $true
    evidenceSetSha256 = 'E' * 64
    sourceProvenance = $provenance
    reports = @(
        [ordered]@{ role = 'R1'; reportSha256 = 'F' * 64; executableSha256 = '1' * 64 },
        [ordered]@{ role = 'C1'; reportSha256 = '2' * 64; executableSha256 = '2' * 64 },
        [ordered]@{ role = 'C2'; reportSha256 = '3' * 64; executableSha256 = '2' * 64 },
        [ordered]@{ role = 'R2'; reportSha256 = '4' * 64; executableSha256 = '1' * 64 })
}
try {
    Assert-PhysicalArtifactRetryContract

    $verdictPath = Join-Path $tempRoot 'physical.json'
    Write-TestJson -Path $verdictPath -Value $physical

    $passingOutput = Join-Path $tempRoot 'joined-pass.json'
    $passing = Invoke-Validator -VerdictPath $verdictPath -OutputPath $passingOutput
    if ($passing.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $passingOutput -PathType Leaf)) {
        throw "Valid joined evidence fixture was rejected: $($passing.Output)"
    }

    $mutatedPhysical = Copy-TestObject -Value $physical
    $mutatedPhysical.sourceProvenance.reference.sourceTreeSha = 'A' * 64
    Write-TestJson -Path $verdictPath -Value $mutatedPhysical
    $invalidTreeId = Invoke-Validator -VerdictPath $verdictPath -OutputPath (Join-Path $tempRoot 'joined-invalid-tree.json')
    if ($invalidTreeId.ExitCode -eq 0) { throw 'A 64-character digest was accepted as a Git SHA-1 tree object ID.' }

    $mutatedPhysical = Copy-TestObject -Value $physical
    $mutatedPhysical.sourceProvenance.candidate.workingTreeClean = $false
    Write-TestJson -Path $verdictPath -Value $mutatedPhysical
    $dirtySource = Invoke-Validator -VerdictPath $verdictPath -OutputPath (Join-Path $tempRoot 'joined-dirty-source.json')
    if ($dirtySource.ExitCode -eq 0) { throw 'A physical verdict from a dirty source tree was accepted.' }

    $mutations = @(
        @{ Label = 'String-valued physical complete=false'; Apply = { param($value) $value.complete = 'false' } }
        @{ Label = 'String-valued physical passed=false'; Apply = { param($value) $value.passed = 'false' } }
        @{ Label = 'String-valued reference workingTreeClean=false'; Apply = { param($value) $value.sourceProvenance.reference.workingTreeClean = 'false' } }
        @{ Label = 'String-valued candidate workingTreeClean=false'; Apply = { param($value) $value.sourceProvenance.candidate.workingTreeClean = 'false' } }
    )
    foreach ($mutation in $mutations) {
        $mutatedPhysical = Copy-TestObject -Value $physical
        & $mutation.Apply $mutatedPhysical
        Write-TestJson -Path $verdictPath -Value $mutatedPhysical
        Assert-ValidatorRejects -VerdictPath $verdictPath -OutputPath (Join-Path $tempRoot 'joined-bad-json-boolean.json') -Label $mutation.Label
    }

    $sourceRoot = Join-Path $tempRoot 'clean-source'
    $runtimeRoot = Join-Path $sourceRoot 'release-output'
    [IO.Directory]::CreateDirectory($sourceRoot) | Out-Null
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $sourceRoot '.gitignore'), "release-output/`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'fixture.txt'), 'clean source fixture', [Text.UTF8Encoding]::new($false))
    Invoke-TestGit -RepositoryPath $sourceRoot -Arguments @('init', '--quiet') | Out-Null
    Invoke-TestGit -RepositoryPath $sourceRoot -Arguments @('config', 'user.email', 'release-evidence-test@example.invalid') | Out-Null
    Invoke-TestGit -RepositoryPath $sourceRoot -Arguments @('config', 'user.name', 'Release Evidence Test') | Out-Null
    Invoke-TestGit -RepositoryPath $sourceRoot -Arguments @('add', '--', '.gitignore', 'fixture.txt') | Out-Null
    Invoke-TestGit -RepositoryPath $sourceRoot -Arguments @('commit', '--quiet', '-m', 'release evidence fixture') | Out-Null
    $sourceCommit = (Invoke-TestGit -RepositoryPath $sourceRoot -Arguments @('rev-parse', 'HEAD')).Trim()
    $provenanceScript = Join-Path $PSScriptRoot 'New-MarkdownRendererSourceProvenance.ps1'
    & $provenanceScript -SourcePath $sourceRoot -RuntimeOutputPath $runtimeRoot `
        -ExpectedSourceCommitSha $sourceCommit -WorkflowRunId $runId `
        -WorkflowRepository $repository -WorkflowRef $workflowRef -BuildRole candidate
    $sourceSidecar = Get-Content -LiteralPath (Join-Path $runtimeRoot 'markdown-renderer-source-provenance.json') -Raw | ConvertFrom-Json
    if ([string]$sourceSidecar.sourceCommitSha -cne $sourceCommit -or
        [string]$sourceSidecar.buildRole -cne 'candidate' -or
        -not [bool]$sourceSidecar.workingTreeClean) {
        throw 'The clean-checkout provenance writer did not record the actual committed source identity.'
    }

    $referenceValidator = Join-Path $PSScriptRoot 'Test-MarkdownRendererReleaseReference.ps1'
    $referenceFixtureRoot = Join-Path $tempRoot 'reference-contract-fixture'
    $referenceContractPath = Join-Path $referenceFixtureRoot 'MarkdownRenderer\MarkdownRenderer.PerformanceHarness'
    [IO.Directory]::CreateDirectory($referenceContractPath) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $referenceContractPath 'PerformanceMeasurementContract.cs'),
        'internal const int SchemaVersion = 12;' + "`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $referenceFixtureRoot 'source.txt'), 'reference source', [Text.UTF8Encoding]::new($false))
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('init', '--quiet') | Out-Null
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('config', 'user.email', 'release-evidence-test@example.invalid') | Out-Null
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('config', 'user.name', 'Release Evidence Test') | Out-Null
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('add', '--all') | Out-Null
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('commit', '--quiet', '-m', 'schema-12 frozen reference') | Out-Null
    $referenceCommit = (Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('rev-parse', 'HEAD')).Trim()
    [IO.File]::WriteAllText((Join-Path $referenceFixtureRoot 'source.txt'), 'candidate source', [Text.UTF8Encoding]::new($false))
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('add', '--all') | Out-Null
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('commit', '--quiet', '-m', 'schema-12 candidate') | Out-Null
    $candidateCommit = (Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('rev-parse', 'HEAD')).Trim()
    $validatedReference = [string](& $referenceValidator -RepositoryPath $referenceFixtureRoot `
        -CandidateCommitSha $candidateCommit -ReferenceCommitSha $referenceCommit)
    if ($validatedReference.Trim() -cne $referenceCommit) {
        throw 'The release-reference validator did not accept and normalize a clean schema-12 ancestor.'
    }

    $missingReferenceRejected = $false
    try {
        & $referenceValidator -RepositoryPath $referenceFixtureRoot `
            -CandidateCommitSha $candidateCommit -ReferenceCommitSha '' | Out-Null
    }
    catch { $missingReferenceRejected = $true }
    if (-not $missingReferenceRejected) { throw 'An unset release-reference variable was accepted.' }

    $shortReferenceRejected = $false
    try {
        & $referenceValidator -RepositoryPath $referenceFixtureRoot `
            -CandidateCommitSha $candidateCommit -ReferenceCommitSha 'deadbeef' | Out-Null
    }
    catch { $shortReferenceRejected = $true }
    if (-not $shortReferenceRejected) { throw 'A short release-reference value was accepted.' }

    $candidateBranch = (Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('branch', '--show-current')).Trim()
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('switch', '--orphan', 'unrelated-release-reference') | Out-Null
    [IO.File]::WriteAllText((Join-Path $referenceFixtureRoot 'unrelated.txt'), 'unrelated release reference', [Text.UTF8Encoding]::new($false))
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('add', '--', 'unrelated.txt') | Out-Null
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('commit', '--quiet', '-m', 'unrelated schema-12 commit') | Out-Null
    $unrelatedCommit = (Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('rev-parse', 'HEAD')).Trim()
    Invoke-TestGit -RepositoryPath $referenceFixtureRoot -Arguments @('switch', $candidateBranch) | Out-Null
    $unrelatedRejected = $false
    try {
        & $referenceValidator -RepositoryPath $referenceFixtureRoot `
            -CandidateCommitSha $candidateCommit -ReferenceCommitSha $unrelatedCommit | Out-Null
    }
    catch { $unrelatedRejected = $true }
    if (-not $unrelatedRejected) { throw 'A resolvable but non-ancestor release reference was accepted.' }

    $schema10Root = Join-Path $tempRoot 'schema10-reference-fixture'
    $schema10ContractPath = Join-Path $schema10Root 'MarkdownRenderer\MarkdownRenderer.PerformanceHarness'
    [IO.Directory]::CreateDirectory($schema10ContractPath) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $schema10ContractPath 'PerformanceMeasurementContract.cs'),
        'internal const int SchemaVersion = 10;' + "`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $schema10Root 'source.txt'), 'schema 10 fixture', [Text.UTF8Encoding]::new($false))
    Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('init', '--quiet') | Out-Null
    Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('config', 'user.email', 'release-evidence-test@example.invalid') | Out-Null
    Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('config', 'user.name', 'Release Evidence Test') | Out-Null
    Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('add', '--all') | Out-Null
    Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('commit', '--quiet', '-m', 'schema-10 frozen reference') | Out-Null
    $schema10Reference = (Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('rev-parse', 'HEAD')).Trim()
    [IO.File]::WriteAllText((Join-Path $schema10Root 'source.txt'), 'schema 10 candidate', [Text.UTF8Encoding]::new($false))
    Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('add', '--all') | Out-Null
    Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('commit', '--quiet', '-m', 'schema-10 candidate') | Out-Null
    $schema10Candidate = (Invoke-TestGit -RepositoryPath $schema10Root -Arguments @('rev-parse', 'HEAD')).Trim()
    $schema10Rejected = $false
    try {
        & $referenceValidator -RepositoryPath $schema10Root `
            -CandidateCommitSha $schema10Candidate -ReferenceCommitSha $schema10Reference | Out-Null
    }
    catch { $schema10Rejected = $true }
    if (-not $schema10Rejected) { throw 'A schema-10 frozen reference was accepted.' }

    Write-Host 'Release evidence tests passed: clean schema-12 provenance validated; missing/malformed, schema-10, unrelated, invalid-tree, dirty-source, and string-valued false booleans rejected.'
}
finally {
    $tempPath = [IO.Path]::GetFullPath($tempRoot)
    $temporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $tempPath.StartsWith($temporaryDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($tempPath) -notlike 'markdown-renderer-release-join-*') {
        throw "Refusing to remove unexpected test directory '$tempPath'."
    }
    Remove-Item -LiteralPath $tempPath -Recurse -Force
}
