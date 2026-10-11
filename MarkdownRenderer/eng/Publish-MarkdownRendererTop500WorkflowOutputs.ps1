[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SummaryPath,
    [Parameter(Mandatory)][string] $OutputPath,
    [Parameter(Mandatory)][string] $WorkflowEventName,
    [AllowEmptyString()][string] $SameByteRelease,
    [Parameter(Mandatory)][string] $WorkflowRunAttempt
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-RequiredJsonProperty {
    param(
        [Parameter(Mandatory)][object] $Value,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Label
    )

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Label is missing required JSON property '$Name'."
    }
    return $property.Value
}

function Assert-JsonTrue {
    param(
        [AllowNull()][object] $Value,
        [Parameter(Mandatory)][string] $Label
    )

    if ($Value -isnot [bool] -or -not $Value) {
        throw "$Label must be the JSON boolean true."
    }
}

function Get-JsonInt64 {
    param(
        [AllowNull()][object] $Value,
        [Parameter(Mandatory)][string] $Label
    )

    if ($Value -isnot [byte] -and $Value -isnot [sbyte] -and
        $Value -isnot [int16] -and $Value -isnot [uint16] -and
        $Value -isnot [int32] -and $Value -isnot [uint32] -and
        $Value -isnot [int64] -and $Value -isnot [uint64]) {
        throw "$Label must be a JSON integer."
    }
    return [int64]$Value
}

function Write-GitHubOutputs {
    param([Parameter(Mandatory)][Collections.IDictionary] $Values)

    $lines = [Collections.Generic.List[string]]::new()
    foreach ($name in $Values.Keys) {
        $value = [string]$Values[$name]
        if ($value.Contains("`r") -or $value.Contains("`n")) {
            throw "Refusing to write newline-containing GitHub output '$name'."
        }
        $lines.Add("$name=$value")
    }
    [IO.File]::AppendAllText(
        [IO.Path]::GetFullPath($OutputPath),
        ($lines -join [Environment]::NewLine) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false, $true))
}

$attempt = 0
if ($WorkflowRunAttempt -notmatch '^[1-9][0-9]{0,8}$' -or
    -not [int]::TryParse($WorkflowRunAttempt, [ref]$attempt) -or $attempt -lt 1) {
    throw 'Workflow run attempt must be a positive integer.'
}
if ($SameByteRelease -cnotin @('', 'true', 'false')) {
    throw 'Same-byte release input must be the lowercase string true or false.'
}
$isRelease = $WorkflowEventName -ceq 'workflow_call' -or
    ($WorkflowEventName -ceq 'workflow_dispatch' -and $SameByteRelease -ceq 'true')
$artifactName = 'readme-audit-consolidated'
if ($isRelease) { $artifactName += "-attempt-$attempt" }

$outputs = [ordered]@{
    consolidated_artifact_name = $artifactName
    passed = 'false'
    source_commit_sha = ''
    workflow_run_id = ''
    summary_sha256 = ''
    total_cases = ''
    passed_cases = ''
    same_byte_cases = ''
    same_byte_expected_cases = ''
}

$summaryFile = [IO.Path]::GetFullPath($SummaryPath)
$outputFile = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $outputFile -PathType Leaf)) {
    throw "GitHub output file does not exist: '$outputFile'."
}

if (-not (Test-Path -LiteralPath $summaryFile -PathType Leaf)) {
    Write-GitHubOutputs -Values $outputs
    Write-Host 'No consolidated summary exists; publishing a false release output.'
    return
}

$failureMessage = $null
try {
    $item = Get-Item -LiteralPath $summaryFile -Force
    if ($item.Length -le 0 -or $item.Length -gt 32MB -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The consolidated summary has an invalid size or file type.'
    }
    $bytes = [IO.File]::ReadAllBytes($summaryFile)
    $json = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    $summary = $json | ConvertFrom-Json -Depth 100
    if ($null -eq $summary) { throw 'The consolidated summary is empty.' }

    $summaryPassed = Get-RequiredJsonProperty -Value $summary -Name 'passed' -Label 'Summary'
    if ($summaryPassed -isnot [bool]) {
        throw 'Summary passed must be a JSON boolean, not a string or numeric value.'
    }
    $outputs.summary_sha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if ($summaryPassed) {
        $expectedCases = Get-JsonInt64 -Value (Get-RequiredJsonProperty $summary 'expectedCases' 'Summary') -Label 'Summary expectedCases'
        $totalCases = Get-JsonInt64 -Value (Get-RequiredJsonProperty $summary 'totalCases' 'Summary') -Label 'Summary totalCases'
        $passedCases = Get-JsonInt64 -Value (Get-RequiredJsonProperty $summary 'passedCases' 'Summary') -Label 'Summary passedCases'
        $failedCases = Get-JsonInt64 -Value (Get-RequiredJsonProperty $summary 'failedCases' 'Summary') -Label 'Summary failedCases'
        if ($expectedCases -ne 500 -or $totalCases -ne 500 -or $passedCases -ne 500 -or $failedCases -ne 0) {
            throw 'A passing consolidated summary must contain exactly 500 successful cases and zero failures.'
        }

        $sameByteCases = Get-JsonInt64 -Value (Get-RequiredJsonProperty $summary 'sameByteCases' 'Summary') -Label 'Summary sameByteCases'
        $sameByteExpectedCases = Get-JsonInt64 -Value (Get-RequiredJsonProperty $summary 'sameByteExpectedCases' 'Summary') -Label 'Summary sameByteExpectedCases'
        if ($isRelease) {
            Assert-JsonTrue -Value (Get-RequiredJsonProperty $summary 'sameByteRequired' 'Summary') -Label 'Summary sameByteRequired'
            if ($sameByteCases -ne 500 -or $sameByteExpectedCases -ne 500) {
                throw 'A passing same-byte release summary must contain 500 captures for 500 expected cases.'
            }
        }
        elseif ($summary.PSObject.Properties['sameByteRequired'] -and
            $summary.sameByteRequired -isnot [bool]) {
            throw 'Summary sameByteRequired must be a JSON boolean when present.'
        }

        $builds = @(Get-RequiredJsonProperty -Value $summary -Name 'buildEvidence' -Label 'Summary')
        if ($builds.Count -ne 20) { throw 'A passing full audit must contain exactly 20 NativeAOT build provenance shards.' }
        foreach ($build in $builds) {
            Assert-JsonTrue -Value (Get-RequiredJsonProperty $build 'provenanceVerified' 'Build evidence') -Label 'Build provenanceVerified'
        }
        $sourceCommit = [string](Get-RequiredJsonProperty $builds[0] 'sourceCommitSha' 'First build evidence')
        $workflowRunId = [string](Get-RequiredJsonProperty $builds[0] 'workflowRunId' 'First build evidence')
        if ($sourceCommit -notmatch '^[0-9a-fA-F]{40}$' -or $workflowRunId -notmatch '^[1-9][0-9]{0,18}$') {
            throw 'First build evidence must contain a full source commit SHA and positive workflow run ID.'
        }
        foreach ($build in $builds) {
            if ([string]$build.sourceCommitSha -ine $sourceCommit -or
                [string]$build.workflowRunId -cne $workflowRunId) {
                throw 'NativeAOT build provenance shards do not share one source commit and workflow run.'
            }
        }

        $culture = [Globalization.CultureInfo]::InvariantCulture
        $outputs.passed = 'true'
        $outputs.source_commit_sha = $sourceCommit.ToLowerInvariant()
        $outputs.workflow_run_id = $workflowRunId
        $outputs.total_cases = $totalCases.ToString($culture)
        $outputs.passed_cases = $passedCases.ToString($culture)
        $outputs.same_byte_cases = $sameByteCases.ToString($culture)
        $outputs.same_byte_expected_cases = $sameByteExpectedCases.ToString($culture)
    }
}
catch {
    $failureMessage = $_.Exception.Message
}

Write-GitHubOutputs -Values $outputs
if ($null -ne $failureMessage) {
    throw "Consolidated release output validation failed: $failureMessage"
}
