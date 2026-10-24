[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CounterbalancedVerdictPath,
    [Parameter(Mandatory)][string] $Top500SummaryPath,
    [Parameter(Mandatory)][string] $OutputPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCandidateCommitSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedReferenceCommitSha,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]{0,18}$')][string] $ExpectedWorkflowRunId,
    [Parameter(Mandatory)][ValidatePattern('^[^/\s]+/[^/\s]+$')][string] $ExpectedWorkflowRepository,
    [Parameter(Mandatory)][string] $ExpectedWorkflowRef,
    [Parameter(Mandatory)][string] $PhysicalJobResult,
    [Parameter(Mandatory)][string] $Top500JobResult,
    [Parameter(Mandatory)][string] $Top500WorkflowPassed,
    [Parameter(Mandatory)][string] $Top500WorkflowSourceCommitSha,
    [Parameter(Mandatory)][string] $Top500WorkflowRunId,
    [Parameter(Mandatory)][string] $Top500WorkflowSummarySha256,
    [Parameter(Mandatory)][string] $Top500WorkflowTotalCases,
    [Parameter(Mandatory)][string] $Top500WorkflowPassedCases,
    [Parameter(Mandatory)][string] $Top500WorkflowSameByteCases,
    [Parameter(Mandatory)][string] $Top500WorkflowSameByteExpectedCases
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-JsonEvidence {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Label)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Label is missing: '$resolved'."
    }
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Length -le 0 -or $item.Length -gt 32MB -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label has an invalid size or file type."
    }
    [byte[]]$bytes = [IO.File]::ReadAllBytes($resolved)
    $json = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    $value = $json | ConvertFrom-Json -Depth 100
    if ($null -eq $value) { throw "$Label is empty." }
    return [pscustomobject]@{
        Path = $resolved
        Value = $value
        Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
}

function Assert-Sha256 {
    param([Parameter(Mandatory)][string] $Value, [Parameter(Mandatory)][string] $Label)
    if ($Value -notmatch '^[0-9a-fA-F]{64}$') { throw "$Label is not a SHA-256 digest." }
}

function Assert-GitSha1ObjectId {
    param([Parameter(Mandatory)][string] $Value, [Parameter(Mandatory)][string] $Label)
    if ($Value -notmatch '^[0-9a-fA-F]{40}$') {
        throw "$Label must be a full 40-character Git SHA-1 object ID."
    }
}

function Assert-JsonTrue {
    param([AllowNull()][object] $Value, [Parameter(Mandatory)][string] $Label)
    if ($Value -isnot [bool] -or -not $Value) {
        throw "$Label must be the JSON boolean true."
    }
}

function Assert-Count {
    param(
        [Parameter(Mandatory)][string] $Value,
        [Parameter(Mandatory)][int] $Expected,
        [Parameter(Mandatory)][string] $Label
    )
    $parsed = 0
    if (-not [int]::TryParse($Value, [ref]$parsed) -or $parsed -ne $Expected) {
        throw "$Label '$Value' does not equal the required count $Expected."
    }
}

$candidateSha = $ExpectedCandidateCommitSha.ToLowerInvariant()
$referenceSha = $ExpectedReferenceCommitSha.ToLowerInvariant()
$repository = $ExpectedWorkflowRepository.ToLowerInvariant()
if ($candidateSha -ceq $referenceSha) { throw 'Reference and candidate commits must differ.' }
if ($PhysicalJobResult -cne 'success' -or $Top500JobResult -cne 'success') {
    throw "Both qualification jobs must succeed (physical='$PhysicalJobResult', top500='$Top500JobResult')."
}

$physicalEvidence = Read-JsonEvidence -Path $CounterbalancedVerdictPath -Label 'Counterbalanced verdict'
$physical = $physicalEvidence.Value
Assert-JsonTrue -Value $physical.complete -Label 'Counterbalanced complete'
Assert-JsonTrue -Value $physical.passed -Label 'Counterbalanced passed'
if ([int]$physical.schemaVersion -ne 3 -or
    [string]$physical.providerName -cne 'MarkdownRenderer-Counterbalanced-Performance' -or
    [string]$physical.policy -cne 'schema12-r1-c1-c2-r2-abba-contrast-v3-source-provenance' -or
    (@($physical.sequence) -join ',') -cne 'R1,C1,C2,R2') {
    throw 'The physical evidence is incomplete, nonpassing, or does not use the frozen schema-3 R1,C1,C2,R2 policy.'
}
$source = $physical.sourceProvenance
if ($null -eq $source) { throw 'Counterbalanced source provenance is missing.' }
Assert-JsonTrue -Value $source.reference.workingTreeClean -Label 'Reference source workingTreeClean'
Assert-JsonTrue -Value $source.candidate.workingTreeClean -Label 'Candidate source workingTreeClean'
if ([string]$source.workflowRunId -cne $ExpectedWorkflowRunId -or
    [string]$source.workflowRepository -ine $repository -or
    [string]$source.workflowRef -cne $ExpectedWorkflowRef -or
    [string]$source.reference.buildRole -cne 'reference' -or
    [string]$source.candidate.buildRole -cne 'candidate' -or
    [string]$source.reference.sourceCommitSha -ine $referenceSha -or
    [string]$source.candidate.sourceCommitSha -ine $candidateSha -or
    [string]$source.reference.workflowRunId -cne $ExpectedWorkflowRunId -or
    [string]$source.candidate.workflowRunId -cne $ExpectedWorkflowRunId -or
    [string]$source.reference.workflowRepository -ine $repository -or
    [string]$source.candidate.workflowRepository -ine $repository -or
    [string]$source.reference.workflowRef -cne $ExpectedWorkflowRef -or
    [string]$source.candidate.workflowRef -cne $ExpectedWorkflowRef) {
    throw 'Physical reference/candidate source provenance does not match this clean Actions checkout and workflow run.'
}
Assert-GitSha1ObjectId -Value ([string]$source.reference.sourceTreeSha) -Label 'Reference source tree'
Assert-GitSha1ObjectId -Value ([string]$source.candidate.sourceTreeSha) -Label 'Candidate source tree'
Assert-Sha256 -Value ([string]$source.reference.sha256) -Label 'Reference provenance sidecar'
Assert-Sha256 -Value ([string]$source.candidate.sha256) -Label 'Candidate provenance sidecar'
Assert-Sha256 -Value ([string]$physical.evidenceSetSha256) -Label 'Counterbalanced evidence set'
$reportRoles = @($physical.reports | ForEach-Object { [string]$_.role })
if (($reportRoles -join ',') -cne 'R1,C1,C2,R2') { throw 'Counterbalanced report roles are missing, duplicated, or out of order.' }
foreach ($report in @($physical.reports)) {
    Assert-Sha256 -Value ([string]$report.reportSha256) -Label "Role $($report.role) report"
    Assert-Sha256 -Value ([string]$report.executableSha256) -Label "Role $($report.role) executable"
}

$topEvidence = Read-JsonEvidence -Path $Top500SummaryPath -Label 'Consolidated top-500 summary'
$top = $topEvidence.Value
Assert-JsonTrue -Value $top.passed -Label 'Top-500 summary passed'
Assert-JsonTrue -Value $top.sameByteRequired -Label 'Top-500 sameByteRequired'
Assert-Sha256 -Value $Top500WorkflowSummarySha256 -Label 'Reusable top-500 summary output'
if ($topEvidence.Sha256 -cne $Top500WorkflowSummarySha256.ToLowerInvariant()) {
    throw 'The downloaded top-500 summary bytes differ from the reusable workflow output digest.'
}
if ($Top500WorkflowPassed -cne 'true' -or
    [int]$top.expectedCases -ne 500 -or
    [int]$top.totalCases -ne 500 -or
    [int]$top.passedCases -ne 500 -or
    [int]$top.failedCases -ne 0 -or
    [int]$top.sameByteCases -ne 500 -or
    [int]$top.sameByteExpectedCases -ne 500 -or
    [int]$top.sameByteCases -ne [int]$top.sameByteExpectedCases -or
    [string]$Top500WorkflowSourceCommitSha -ine $candidateSha -or
    [string]$Top500WorkflowRunId -cne $ExpectedWorkflowRunId) {
    throw 'The same-byte top-500 summary or reusable workflow outputs do not pass the full current-commit release conditions.'
}
Assert-Count -Value $Top500WorkflowTotalCases -Expected 500 -Label 'Reusable workflow total cases'
Assert-Count -Value $Top500WorkflowPassedCases -Expected 500 -Label 'Reusable workflow passed cases'
Assert-Count -Value $Top500WorkflowSameByteCases -Expected ([int]$top.sameByteCases) -Label 'Reusable workflow same-byte cases'
Assert-Count -Value $Top500WorkflowSameByteExpectedCases -Expected ([int]$top.sameByteExpectedCases) -Label 'Reusable workflow required same-byte cases'
if ([int]$top.sameByteCases -ne [int]$top.sameByteExpectedCases) {
    throw 'The top-500 summary is missing required same-byte captures.'
}
Assert-Sha256 -Value ([string]$top.pinnedCorpusSha256) -Label 'Pinned top-500 corpus'
$firstP95 = [double]$top.nativeFirstRenderRatioP95
$fullP95 = [double]$top.nativeFullPageRatioP95
if ([double]::IsNaN($firstP95) -or [double]::IsInfinity($firstP95) -or
    [double]::IsNaN($fullP95) -or [double]::IsInfinity($fullP95) -or
    $firstP95 -lt 0 -or $firstP95 -gt 1.10 -or $fullP95 -lt 0 -or $fullP95 -gt 1.10) {
    throw 'Top-500 aggregate first-render or full-page p95 exceeds its 110% release ceiling.'
}
$builds = @($top.buildEvidence | Sort-Object { [int]$_.startRank })
if ($builds.Count -ne 20) { throw "Expected 20 NativeAOT provenance shards; found $($builds.Count)." }
$nextRank = 1
foreach ($build in $builds) {
    Assert-JsonTrue -Value $build.provenanceVerified -Label "Top-500 ranks $([int]$build.startRank)-$([int]$build.endRank) provenanceVerified"
    $startRank = [int]$build.startRank
    $endRank = [int]$build.endRank
    if ($startRank -ne $nextRank -or $endRank -lt $startRank -or
        $endRank -gt 500 -or ($endRank - $startRank + 1) -gt 25 -or
        [string]$build.sourceCommitSha -ine $candidateSha -or
        [string]$build.runtimeFlavor -cne 'native-aot' -or
        [string]$build.workflowRunId -cne $ExpectedWorkflowRunId -or
        [string]$build.workflowRepository -ine $repository -or
        [string]$build.workflowRef -cne $ExpectedWorkflowRef) {
        throw "Top-500 NativeAOT build/provenance evidence is invalid at ranks $startRank-$endRank."
    }
    Assert-Sha256 -Value ([string]$build.provenanceSha256) -Label "Top-500 ranks $startRank-$endRank provenance"
    Assert-Sha256 -Value ([string]$build.executableSha256) -Label "Top-500 ranks $startRank-$endRank executable"
    Assert-Sha256 -Value ([string]$build.artifactManifestSha256) -Label "Top-500 ranks $startRank-$endRank artifact manifest"
    $nextRank = $endRank + 1
}
if ($nextRank -ne 501) { throw 'NativeAOT source provenance does not cover ranks 1 through 500 exactly.' }

$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($output)
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
if (Test-Path -LiteralPath $output) { throw "Qualification verdict is create-new only: '$output'." }
$joined = [ordered]@{
    schemaVersion = 1
    providerName = 'MarkdownRenderer-Joined-Release-Qualification'
    policy = 'clean-source-physical-abba-plus-same-run-nativeaot-same-byte-top500-v1'
    complete = $true
    passed = $true
    candidateCommitSha = $candidateSha
    referenceCommitSha = $referenceSha
    workflow = [ordered]@{
        runId = $ExpectedWorkflowRunId
        repository = $repository
        ref = $ExpectedWorkflowRef
    }
    runnerAssurance = [ordered]@{
        physicalRunnerAttestation = 'operator-configured'
        activeWindowsConsoleVerified = $true
        bareMetalIndependentlyAttested = $false
    }
    physical = [ordered]@{
        verdictSha256 = $physicalEvidence.Sha256
        evidenceSetSha256 = [string]$physical.evidenceSetSha256
        runId = [string]$physical.runId
        policy = [string]$physical.policy
        sequence = @($physical.sequence)
        sourceTreeSha = [string]$source.candidate.sourceTreeSha
    }
    top500 = [ordered]@{
        summarySha256 = $topEvidence.Sha256
        pinnedCorpusSha256 = [string]$top.pinnedCorpusSha256
        expectedCases = [int]$top.expectedCases
        passedCases = [int]$top.passedCases
        sameByteCases = [int]$top.sameByteCases
        sameByteExpectedCases = [int]$top.sameByteExpectedCases
        nativeFirstRenderRatioP95 = $firstP95
        nativeFullPageRatioP95 = $fullP95
        sourceCommits = @($builds | ForEach-Object { [string]$_.sourceCommitSha } | Select-Object -Unique)
    }
    createdAtUtc = [DateTimeOffset]::UtcNow
}
$jsonBytes = [Text.UTF8Encoding]::new($false, $true).GetBytes((($joined | ConvertTo-Json -Depth 12) + "`n"))
$stream = [IO.FileStream]::new($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
try {
    $stream.Write($jsonBytes, 0, $jsonBytes.Length)
    $stream.Flush($true)
}
finally {
    $stream.Dispose()
}
Write-Host "Joined physical R1,C1,C2,R2 and same-run same-byte top-500 qualification passed: $output"
Write-Output $output
