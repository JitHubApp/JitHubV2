[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$RepositoryPath,
    [Parameter(Mandatory = $true)][string]$SourceCommitSha,
    [Parameter(Mandatory = $true)][string]$WorkflowRunId,
    [Parameter(Mandatory = $true)][string]$WorkflowRepository,
    [Parameter(Mandatory = $true)][string]$WorkflowRef
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath($RepositoryPath)
$head = ([string](& git -C $repositoryRoot rev-parse HEAD)).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-fA-F]{40}$' -or
    -not [string]::Equals($head, $SourceCommitSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'NativeAOT provenance source commit does not match the checked-out build source.'
}

$workingTreeStatus = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not verify that the NativeAOT build source tree is clean.'
}
if ($workingTreeStatus.Count -ne 0) {
    throw "NativeAOT provenance cannot be issued from a dirty source tree: $($workingTreeStatus -join '; ')"
}

$modulePath = Join-Path $PSScriptRoot 'readme-audit\NativeAotProvenance.psm1'
Import-Module $modulePath -Force
$artifactRoot = [IO.Path]::GetFullPath($InputPath)
$provenance = New-NativeAotProvenance `
    -InputPath $artifactRoot `
    -SourceCommitSha $SourceCommitSha `
    -WorkflowRunId $WorkflowRunId `
    -WorkflowRepository $WorkflowRepository `
    -WorkflowRef $WorkflowRef

$sidecarPath = Join-Path $artifactRoot 'native-aot-provenance.json'
[IO.File]::WriteAllText(
    $sidecarPath,
    ($provenance | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$verified = Test-NativeAotProvenance `
    -InputPath $artifactRoot `
    -ProvenancePath $sidecarPath `
    -ExpectedSourceCommitSha $SourceCommitSha `
    -ExpectedWorkflowRunId $WorkflowRunId `
    -ExpectedWorkflowRepository $WorkflowRepository `
    -ExpectedWorkflowRef $WorkflowRef
Write-Host "Issued NativeAOT provenance $($verified.Sha256) for $WorkflowRepository@$WorkflowRef ($WorkflowRunId)."
