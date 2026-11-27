<#
.SYNOPSIS
Runs the scroll fidelity probe in Windows Sandbox and copies its report here.

.DESCRIPTION
Builds perf/MarkdownRenderer.ScrollFidelityProbe on this machine, launches it
in Windows Sandbox with `winapp run --on sandbox`, waits for its JSON report
and pulls it to -OutputDirectory. The probe scrolls a long README-shaped
document in four ways (wheel storm, flings, thumb drag, random jumps) and
measures the share of each presented frame that is unrendered.

The sandbox is reused, never stopped. Compare two builds by running this once
per build with a different -Label and passing both reports to
eng/Compare-ScrollFidelityReports.ps1.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Label,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\scroll-probe'),
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [int] $CorpusKib = 768,
    [string] $Scenarios = 'wheel,fling,drag,jump',
    [switch] $NoSession,
    [int] $TimeoutMinutes = 12
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) {
    throw 'WinApp CLI 0.7 or newer is required.'
}

$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\perf\MarkdownRenderer.ScrollFidelityProbe\MarkdownRenderer.ScrollFidelityProbe.csproj'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$safeLabel = $Label -replace '[^A-Za-z0-9._-]', '-'
$guestRelative = "scroll-probe\$safeLabel-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).json"
$guestPath = "C:\WinApp\work\$guestRelative"

$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = "scroll-probe-$([guid]::NewGuid().ToString('N'))"
try {
    $appArguments = @('--out', $guestPath, '--label', $Label, '--kib', $CorpusKib, '--scenarios', $Scenarios)
    if ($NoSession) { $appArguments += '--no-session' }
    $runOutput = & winapp run $project --on sandbox --detach --json -c $Configuration --arch x64 -- @appArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "winapp run failed ($LASTEXITCODE): $($runOutput | Out-String)"
    }

    $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    $local = Join-Path $output ([IO.Path]::GetFileName($guestRelative))
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 10
        $null = & winapp target pull sandbox $guestRelative $local --json 2>&1
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $local)) {
            Write-Output $local
            return
        }
    }

    # A crashed probe leaves only its first-chance exception log.
    $exceptionLog = "$local.exceptions.log"
    $null = & winapp target pull sandbox "$guestRelative.exceptions.log" $exceptionLog --json 2>&1
    $detail = if (Test-Path -LiteralPath $exceptionLog) { " Exception log: $exceptionLog" } else { '' }
    throw "The probe did not write $guestPath within $TimeoutMinutes minutes.$detail"
}
finally {
    $null = & winapp ui yield --on sandbox 2>&1
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
}
