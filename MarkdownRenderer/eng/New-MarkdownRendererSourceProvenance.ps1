[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourcePath,
    [Parameter(Mandatory)][string] $RuntimeOutputPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedSourceCommitSha,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]{0,18}$')][string] $WorkflowRunId,
    [Parameter(Mandatory)][ValidatePattern('^[^/\s]+/[^/\s]+$')][string] $WorkflowRepository,
    [Parameter(Mandatory)][string] $WorkflowRef,
    [Parameter(Mandatory)][ValidateSet('reference', 'candidate')][string] $BuildRole
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-GitValue {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $lines = @(& git -C $SourceRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed for '$SourceRoot': $($lines -join ' ')"
    }
    return ($lines | ForEach-Object { [string]$_ }) -join "`n"
}

$SourceRoot = [IO.Path]::GetFullPath($SourcePath)
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeOutputPath)
if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container) -or
    -not (Test-Path -LiteralPath $RuntimeRoot -PathType Container)) {
    throw 'Source and runtime-output paths must be existing directories.'
}

$actualCommitSha = ([string](Invoke-GitValue -Arguments @('rev-parse', 'HEAD'))).Trim().ToLowerInvariant()
$sourceTreeSha = ([string](Invoke-GitValue -Arguments @('rev-parse', 'HEAD^{tree}'))).Trim().ToLowerInvariant()
$expectedCommitSha = $ExpectedSourceCommitSha.ToLowerInvariant()
if ($actualCommitSha -cne $expectedCommitSha) {
    throw "Checked-out source commit '$actualCommitSha' does not match expected commit '$expectedCommitSha'."
}
if ($actualCommitSha -notmatch '^[0-9a-f]{40}$' -or $sourceTreeSha -notmatch '^[0-9a-f]{40}$') {
    throw 'Git returned a malformed source commit or tree identity.'
}

$status = [string](Invoke-GitValue -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
if (-not [string]::IsNullOrEmpty($status)) {
    $firstStatusLine = ($status -split "`n", 2)[0]
    throw "The $BuildRole source checkout is not clean; first status entry: '$firstStatusLine'."
}
if ([string]::IsNullOrWhiteSpace($WorkflowRef)) {
    throw 'The workflow ref is required for auditable source provenance.'
}

$provenancePath = Join-Path $RuntimeRoot 'markdown-renderer-source-provenance.json'
if (Test-Path -LiteralPath $provenancePath) {
    throw "Source provenance is create-new only: '$provenancePath' already exists."
}
$provenance = [ordered]@{
    schemaVersion = 1
    providerName = 'MarkdownRenderer-Physical-Release-Source'
    buildRole = $BuildRole
    sourceCommitSha = $actualCommitSha
    sourceTreeSha = $sourceTreeSha
    workingTreeClean = $true
    workflowRunId = $WorkflowRunId
    workflowRepository = $WorkflowRepository.ToLowerInvariant()
    workflowRef = $WorkflowRef
}
$json = ($provenance | ConvertTo-Json -Depth 4) + "`n"
$bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
$stream = [IO.FileStream]::new(
    $provenancePath,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write,
    [IO.FileShare]::None,
    4096,
    [IO.FileOptions]::WriteThrough)
try {
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush($true)
}
finally {
    $stream.Dispose()
}

Write-Host "Wrote clean $BuildRole source provenance for $actualCommitSha."
