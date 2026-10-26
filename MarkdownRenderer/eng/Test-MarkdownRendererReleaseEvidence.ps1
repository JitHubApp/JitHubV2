[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CounterbalancedVerdictPath,
    [Parameter(Mandatory)][string] $OutputPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCandidateCommitSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedReferenceCommitSha,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]{0,18}$')][string] $ExpectedWorkflowRunId,
    [Parameter(Mandatory)][ValidatePattern('^[^/\s]+/[^/\s]+$')][string] $ExpectedWorkflowRepository,
    [Parameter(Mandatory)][string] $ExpectedWorkflowRef,
    [Parameter(Mandatory)][string] $PhysicalJobResult
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

$candidateSha = $ExpectedCandidateCommitSha.ToLowerInvariant()
$referenceSha = $ExpectedReferenceCommitSha.ToLowerInvariant()
$repository = $ExpectedWorkflowRepository.ToLowerInvariant()
if ($candidateSha -ceq $referenceSha) { throw 'Reference and candidate commits must differ.' }
if ($PhysicalJobResult -cne 'success') {
    throw "The physical qualification job must succeed (physical='$PhysicalJobResult')."
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

$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($output)
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
if (Test-Path -LiteralPath $output) { throw "Qualification verdict is create-new only: '$output'." }
$joined = [ordered]@{
    schemaVersion = 1
    providerName = 'MarkdownRenderer-Joined-Release-Qualification'
    policy = 'clean-source-physical-abba-v2'
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
Write-Host "Physical R1,C1,C2,R2 release qualification passed: $output"
Write-Output $output
