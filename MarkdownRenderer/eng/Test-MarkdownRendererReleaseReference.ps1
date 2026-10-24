[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RepositoryPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $CandidateCommitSha,
    [AllowEmptyString()][string] $ReferenceCommitSha
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-GitText {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $lines = @(& git -C $repository @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($lines -join ' ')"
    }
    return [string]::Join("`n", [string[]]@($lines | ForEach-Object { [string]$_ }))
}

$repository = [IO.Path]::GetFullPath($RepositoryPath)
if (-not (Test-Path -LiteralPath $repository -PathType Container)) {
    throw "Candidate repository directory does not exist: '$repository'."
}
$candidateSha = $CandidateCommitSha.Trim().ToLowerInvariant()
$referenceSha = ([string]$ReferenceCommitSha).Trim().ToLowerInvariant()
if ($referenceSha -notmatch '^[0-9a-f]{40}$') {
    throw 'Repository variable MARKDOWN_RENDERER_RELEASE_REFERENCE_SHA must be set to the approved full 40-character frozen-reference SHA.'
}
if ($candidateSha -ceq $referenceSha) {
    throw 'The frozen reference and candidate commits must differ.'
}

$actualCandidateSha = (Invoke-GitText -Arguments @('rev-parse', 'HEAD')).Trim().ToLowerInvariant()
if ($actualCandidateSha -cne $candidateSha) {
    throw "Candidate checkout '$actualCandidateSha' does not match the workflow commit '$candidateSha'."
}
$status = Invoke-GitText -Arguments @('status', '--porcelain=v1', '--untracked-files=all')
if (-not [string]::IsNullOrEmpty($status)) {
    throw "Candidate source checkout is not clean: '$(($status -split "`n", 2)[0])'."
}

$revision = $referenceSha + '^{commit}'
$resolvedReferenceSha = (Invoke-GitText -Arguments @('rev-parse', '--verify', $revision)).Trim().ToLowerInvariant()
if ($resolvedReferenceSha -cne $referenceSha) {
    throw 'The configured frozen-reference variable does not resolve to that exact commit object.'
}
$null = & git -C $repository merge-base --is-ancestor $referenceSha $candidateSha 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'The configured frozen-reference commit must be an ancestor of the candidate commit.'
}

$contractPath = 'MarkdownRenderer/MarkdownRenderer.PerformanceHarness/PerformanceMeasurementContract.cs'
$contractSource = Invoke-GitText -Arguments @('show', "${referenceSha}:$contractPath")
if ($contractSource -notmatch 'internal\s+const\s+int\s+SchemaVersion\s*=\s*12\s*;') {
    throw 'The configured frozen reference does not contain the required schema-12 measurement contract.'
}

Write-Output $referenceSha
