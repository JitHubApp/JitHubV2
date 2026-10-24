[CmdletBinding()]
param(
    [string] $MatrixPath = 'docs/markdown-renderer/evidence/phase2-matrix.json',
    [string] $OutputRoot = 'MarkdownRenderer/artifacts/correctness/phase2-offline',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

# Runs each Phase 2 matrix row's offline filters against the current build and
# records per-row outcomes. It never marks a row passed: live checks remain a
# separate requirement, so rows report offline=PASS|FAIL and live=UNVERIFIED.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Set-Location $repositoryRoot

$matrix = Get-Content -LiteralPath $MatrixPath -Raw | ConvertFrom-Json -Depth 16
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$runRoot = [IO.Path]::GetFullPath((Join-Path $OutputRoot $runId))
New-Item -ItemType Directory -Force $runRoot | Out-Null
$head = (git rev-parse HEAD).Trim()
$dirty = [bool](git status --porcelain --untracked-files=no)

function Get-TrxCounts([string] $Path) {
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    [ordered]@{
        total = [int]$counters.total
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        notExecuted = [int]$counters.notExecuted
    }
}

$rows = [Collections.Generic.List[object]]::new()
foreach ($row in $matrix.rows) {
    $entries = [Collections.Generic.List[object]]::new()
    $offlinePassed = $true
    $index = 0
    foreach ($evidence in $row.offline) {
        $index++
        $projectPath = "MarkdownRenderer/$($evidence.project)/$($evidence.project).csproj"
        if ($evidence.filter -notmatch '^(|FullyQualifiedName~.*)$') {
            # Non-test evidence such as AOT smoke publication is recorded, not run here.
            $entries.Add([ordered]@{ project = $evidence.project; filter = $evidence.filter; outcome = 'NOT-RUN-HERE' })
            continue
        }
        $trxName = "$($row.id)-$index.trx"
        $arguments = @('test', $projectPath, '-c', $Configuration, '-p:Platform=x64', '--no-restore',
            '--logger', "trx;LogFileName=$trxName", '--results-directory', $runRoot)
        if (-not [string]::IsNullOrEmpty($evidence.filter)) {
            $arguments += @('--filter', $evidence.filter)
        }
        & dotnet @arguments *> (Join-Path $runRoot "$($row.id)-$index.log")
        $exitCode = $LASTEXITCODE
        $trxPath = Join-Path $runRoot $trxName
        $counts = if (Test-Path -LiteralPath $trxPath) { Get-TrxCounts $trxPath } else { $null }
        $passed = $exitCode -eq 0 -and $null -ne $counts -and $counts.total -gt 0 -and $counts.failed -eq 0
        if (-not $passed) { $offlinePassed = $false }
        $entries.Add([ordered]@{
            project = $evidence.project
            filter = $evidence.filter
            exitCode = $exitCode
            counts = $counts
            trx = $trxName
            outcome = if ($passed) { 'PASS' } elseif ($null -ne $counts -and $counts.total -eq 0) { 'NO-TESTS-MATCHED' } else { 'FAIL' }
        })
    }
    $rows.Add([ordered]@{
        id = $row.id
        offline = if ($offlinePassed) { 'PASS' } else { 'FAIL' }
        live = 'UNVERIFIED'
        evidence = $entries
    })
    Write-Host "$($row.id): offline=$(if ($offlinePassed) { 'PASS' } else { 'FAIL' }) live=UNVERIFIED"
}

$report = [ordered]@{
    schemaVersion = 1
    runId = $runId
    sourceRevision = $head
    uncommittedTrackedChanges = $dirty
    configuration = $Configuration
    matrix = $MatrixPath
    note = 'Offline evidence only. No row is a Phase 2 pass until its live checks run on this candidate.'
    rows = $rows
}
$reportPath = Join-Path $runRoot 'phase2-offline-evidence.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 10) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Report: $reportPath"
if (@($rows | Where-Object { $_.offline -ne 'PASS' }).Count -gt 0) { exit 1 }
