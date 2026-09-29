[CmdletBinding()]
param(
    [ValidateRange(1, 500)]
    [int]$Count = 500,

    [ValidateRange(1, 500)]
    [int]$StartRank = 1,

    [string]$OutputRoot = "",

    [string]$ManifestPath = "",

    [switch]$Resume,

    [switch]$ReuseBrowserEvidence,

    [switch]$CaptureSameByteCorpus,

    [switch]$RasterDiagnostics,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$AppExecutablePath = "",

    [switch]$NativeAotArtifact,

    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $stamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $OutputRoot = Join-Path $repoRoot "artifacts\readme-top500\run-$stamp"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $OutputRoot "top-repositories.json"
}
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
$requiredManifestCount = $StartRank + $Count - 1
if (-not (Test-Path -LiteralPath $ManifestPath)) {
    & (Join-Path $repoRoot "eng\readme-audit\New-TopReadmeManifest.ps1") `
        -Count $requiredManifestCount `
        -OutputPath $ManifestPath
    if ($LASTEXITCODE -ne 0) { throw "README manifest generation failed." }
}

$appProject = Join-Path $repoRoot "JitHub.WinUI\JitHub.WinUI.csproj"
$automationProject = Join-Path $repoRoot "JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj"
if (-not [string]::IsNullOrWhiteSpace($AppExecutablePath) -and -not $SkipBuild) {
    throw "An explicit app executable requires -SkipBuild so the audit cannot silently replace it."
}
if ($NativeAotArtifact -and
    ($Configuration -ne 'Release' -or
     -not $SkipBuild -or
     [string]::IsNullOrWhiteSpace($AppExecutablePath))) {
    throw '-NativeAotArtifact requires an explicit prebuilt Release app and -SkipBuild.'
}
if (-not $SkipBuild) {
    $appBuildArguments = @(
        "build", $appProject,
        "-c", $Configuration,
        "-p:Platform=x64",
        "-p:GenerateAppxPackageOnBuild=false",
        "--disable-build-servers",
        "-m:1")
    if ($Configuration -eq "Release") {
        $appBuildArguments += "-p:SkipReleaseSecurityGate=true"
    }
    & dotnet @appBuildArguments
    if ($LASTEXITCODE -ne 0) { throw "JitHub build failed." }
    $automationBuildArguments = @(
        "build", $automationProject,
        "-c", $Configuration,
        "-p:Platform=x64",
        "--disable-build-servers",
        "-m:1")
    if ($Configuration -eq "Release") {
        $automationBuildArguments += @("-r", "win-x64")
    }
    & dotnet @automationBuildArguments
    if ($LASTEXITCODE -ne 0) { throw "README audit harness build failed." }
}

$appPath = if ([string]::IsNullOrWhiteSpace($AppExecutablePath)) {
    Join-Path $repoRoot "JitHub.WinUI\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\JitHub.WinUI.exe"
} else {
    [System.IO.Path]::GetFullPath($AppExecutablePath)
}
$runnerRidSegment = if ($Configuration -eq "Release") { "\win-x64" } else { "" }
$runnerPath = Join-Path $repoRoot "JitHub.WinUI.Automation\bin\x64\$Configuration\net10.0-windows10.0.19041.0$runnerRidSegment\JitHub.WinUI.Automation.dll"
$browserScript = Join-Path $repoRoot "eng\readme-audit\browser-oracle.mjs"
foreach ($requiredPath in @($appPath, $runnerPath, $browserScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required audit artifact was not found: '$requiredPath'."
    }
}
$sourceCommitSha = [string]$env:GITHUB_SHA
if ($sourceCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    $sourceCommitSha = [string]((& git -C $repoRoot rev-parse HEAD) | Out-String).Trim()
}
if ($sourceCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Could not identify the source commit used for the README audit build."
}
$dependencyAssemblySha256 = [ordered]@{}
$appDirectory = Split-Path -Parent $appPath
foreach ($assembly in @(Get-ChildItem -LiteralPath $appDirectory -File -Filter '*.dll' | Sort-Object Name)) {
    $dependencyAssemblySha256[$assembly.Name] =
        (Get-FileHash -LiteralPath $assembly.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($NativeAotArtifact) {
    if ([System.IO.Path]::GetFileName($appPath) -ne 'JitHub.WinUI.exe') {
        throw "The explicit NativeAOT executable has an unexpected name: '$appPath'."
    }
    & (Join-Path $repoRoot 'eng\Verify-NativeAotArtifact.ps1') `
        -InputPath $appDirectory -Architecture x64
    if (-not $?) { throw "NativeAOT artifact verification failed: '$appDirectory'." }
}
else {
    foreach ($assemblyName in @('JitHub.WinUI.dll', 'MarkdownRenderer.dll')) {
        if (-not $dependencyAssemblySha256.Contains($assemblyName)) {
            throw "Required Release app assembly was not found beside '$appPath': '$assemblyName'."
        }
    }
}
$lastRank = $StartRank + $Count - 1
$appBinaryEvidence = [ordered]@{
    schemaVersion = 1
    startRank = $StartRank
    endRank = $lastRank
    sourceCommitSha = $sourceCommitSha.ToLowerInvariant()
    configuration = $Configuration
    runtimeFlavor = if ($NativeAotArtifact) { 'native-aot' } else { 'managed' }
    executableName = [System.IO.Path]::GetFileName($appPath)
    executableSha256 = (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash.ToLowerInvariant()
    dependencyAssemblySha256 = $dependencyAssemblySha256
}
[System.IO.File]::WriteAllText(
    (Join-Path $OutputRoot ("app-binary-ranks-{0}-{1}.json" -f $StartRank, $lastRank)),
    (($appBinaryEvidence | ConvertTo-Json -Depth 4) + [Environment]::NewLine),
    [System.Text.UTF8Encoding]::new($false))

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is required for authenticated corpus rendering."
}
$auditToken = (& gh auth token).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($auditToken)) {
    throw "Could not obtain the authenticated GitHub token for the isolated audit processes."
}

$tokenBytes = [System.Text.Encoding]::UTF8.GetBytes($auditToken)
$tokenHashBytes = [System.Security.Cryptography.SHA256]::HashData($tokenBytes)
$tokenHash = [Convert]::ToHexString($tokenHashBytes)
# A 60-bit positive identifier is stable for the token, does not disclose it,
# and preserves the production cache's per-account isolation. Audit data roots
# are unique per case, so this identity never enters a user's normal cache.
$auditAccountId = [Convert]::ToInt64($tokenHash.Substring(0, 15), 16).ToString(
    [System.Globalization.CultureInfo]::InvariantCulture)
[Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
[Array]::Clear($tokenHashBytes, 0, $tokenHashBytes.Length)

$previousToken = $env:JITHUB_README_AUDIT_GITHUB_TOKEN
$previousAccountId = $env:JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID
$previousRasterDiagnostics = $env:JITHUB_README_AUDIT_RASTER_DIAGNOSTICS
$runnerProcess = $null

function Stop-ReadmeAuditProcessTree {
    param([System.Diagnostics.Process]$Process)

    # Snapshot descendants before killing the root: WaitForExit/HasExited on
    # that root do not prove that its Node, Edge, and JitHub children exited.
    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop |
        Select-Object ProcessId, ParentProcessId, CreationDate)
    $treeIds = [System.Collections.Generic.HashSet[int]]::new()
    [void]$treeIds.Add($Process.Id)
    do {
        $added = $false
        foreach ($entry in $processes) {
            if ($treeIds.Contains([int]$entry.ParentProcessId) -and
                $treeIds.Add([int]$entry.ProcessId)) {
                $added = $true
            }
        }
    } while ($added)
    $tree = @($processes | Where-Object { $treeIds.Contains([int]$_.ProcessId) })

    if (-not $Process.HasExited) {
        try {
            $Process.Kill($true)
        }
        catch {
            if (-not $Process.HasExited) { throw }
        }
    }

    # A root that crashed before the watchdog fired may leave inherited pipe
    # handles open in descendants. Kill only PIDs whose creation time still
    # matches our pre-kill snapshot, never an unrelated recycled PID.
    foreach ($entry in $tree) {
        if ([int]$entry.ProcessId -eq $Process.Id) { continue }
        $live = Get-CimInstance Win32_Process -Filter "ProcessId = $($entry.ProcessId)" -ErrorAction Stop
        if ($null -ne $live -and $live.CreationDate -eq $entry.CreationDate) {
            try {
                $child = [System.Diagnostics.Process]::GetProcessById([int]$entry.ProcessId)
                try { $child.Kill($true) } finally { $child.Dispose() }
            }
            catch [System.ArgumentException] {
                # The child exited between identity validation and the kill.
            }
            catch [System.InvalidOperationException] {
                # The child exited between identity validation and the kill.
            }
        }
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $remaining = @($tree | Where-Object {
            $live = Get-CimInstance Win32_Process -Filter "ProcessId = $($_.ProcessId)" -ErrorAction Stop
            $null -ne $live -and $live.CreationDate -eq $_.CreationDate
        })
        if ($remaining.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "README audit watchdog could not verify exit of all $($tree.Count) process-tree members."
}

function Write-ReadmeAuditWatchdogFailure {
    param(
        [Parameter(Mandatory)][string]$TimeoutKind,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][string]$Case,
        [AllowEmptyString()][string]$Progress = "",
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][bool]$ProcessTreeTerminated
    )

    $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss-fff")
    $suffix = [Guid]::NewGuid().ToString("N")
    $artifactPath = Join-Path $OutputRoot "watchdog-failure-$timestamp-$suffix.json"
    $artifact = [ordered]@{
        schemaVersion = 1
        status = "failed"
        failureCategory = "infrastructure-timeout"
        timeoutKind = $TimeoutKind
        timeoutSeconds = $TimeoutSeconds
        case = $Case
        lastProgress = $Progress
        detectedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        runnerProcessId = $ProcessId
        processTreeTerminated = $ProcessTreeTerminated
        performanceGateEvaluated = $false
    }
    $json = $artifact | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText(
        $artifactPath,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    return $artifactPath
}

function Stop-ReadmeAuditForWatchdog {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$TimeoutKind,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][string]$Case,
        [AllowEmptyString()][string]$Progress = ""
    )

    $terminated = $false
    try {
        $terminated = Stop-ReadmeAuditProcessTree -Process $Process
    }
    finally {
        $artifactPath = Write-ReadmeAuditWatchdogFailure `
            -TimeoutKind $TimeoutKind `
            -TimeoutSeconds $TimeoutSeconds `
            -Case $Case `
            -Progress $Progress `
            -ProcessId $Process.Id `
            -ProcessTreeTerminated $terminated
        Write-Host "README audit watchdog failure evidence: '$artifactPath'." -ForegroundColor Yellow
    }
}

try {
    $env:JITHUB_README_AUDIT_GITHUB_TOKEN = $auditToken
    $env:JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID = $auditAccountId
    $env:JITHUB_README_AUDIT_RASTER_DIAGNOSTICS = if ($RasterDiagnostics) { "1" } else { $null }
    $arguments = @(
        $runnerPath,
        "--probe=readme-production-audit",
        "--app=$appPath",
        "--out=$OutputRoot",
        "--manifest=$ManifestPath",
        "--browser-script=$browserScript",
        "--start-rank=$StartRank",
        "--count=$Count")
    if ($Resume) { $arguments += "--resume" }
    if ($ReuseBrowserEvidence) { $arguments += "--reuse-browser-evidence" }
    if ($CaptureSameByteCorpus) { $arguments += "--capture-same-byte-corpus" }

    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($dotnetPath)
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) {
        $startInfo.ArgumentList.Add([string]$argument)
    }

    $runnerProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $runnerProcess) {
        throw "Could not start the isolated README audit runner."
    }

    # UIA provider calls may block the managed runner beyond its own traversal
    # timeout. Watch the child process externally so a provider hang cannot pin
    # the wrapper forever or leave its JitHub/Edge descendants running.
    $caseWatchdog = [TimeSpan]::FromMinutes(30)
    $nativeTraversalInactivityWatchdog = [TimeSpan]::FromSeconds(90)
    $currentCase = "starting"
    $caseStartedUtc = [DateTimeOffset]::UtcNow
    $nativeTraversalStarted = $false
    $lastNativeProgressUtc = $null
    $lastNativeProgressStage = ""
    $stdoutTask = $runnerProcess.StandardOutput.ReadLineAsync()
    $stderrTask = $runnerProcess.StandardError.ReadLineAsync()
    $stdoutOpen = $true
    $stderrOpen = $true
    $rootExitObservedUtc = $null

    while (-not ($runnerProcess.HasExited -and -not $stdoutOpen -and -not $stderrOpen)) {
        $readOutput = $false
        if ($stdoutOpen -and $stdoutTask.IsCompleted) {
            $line = $stdoutTask.GetAwaiter().GetResult()
            if ($null -eq $line) {
                $stdoutOpen = $false
            }
            else {
                Write-Host $line
                $readOutput = $true
                if ($line -match '^README audit (\d+)/\d+: (.+)$') {
                    $currentCase = "rank $($Matches[1]): $($Matches[2])"
                    $caseStartedUtc = [DateTimeOffset]::UtcNow
                    $nativeTraversalStarted = $false
                    $lastNativeProgressUtc = $null
                    $lastNativeProgressStage = ""
                }
                if ($line -match '^README native audit stage: (.+)\.$') {
                    $nativeTraversalStarted = $true
                    $lastNativeProgressUtc = [DateTimeOffset]::UtcNow
                    $lastNativeProgressStage = $Matches[1]
                }
                elseif ($line -match '^README native traversal starting\.') {
                    $nativeTraversalStarted = $true
                    $lastNativeProgressUtc = [DateTimeOffset]::UtcNow
                    $lastNativeProgressStage = "native traversal starting"
                }
                elseif ($line -match '^README native traversal (?:viewport|revisit|movement) ') {
                    $nativeTraversalStarted = $true
                    $lastNativeProgressUtc = [DateTimeOffset]::UtcNow
                    $lastNativeProgressStage = $line
                }
                elseif ($line -match '^README native traversal complete:') {
                    $nativeTraversalStarted = $true
                    $lastNativeProgressUtc = [DateTimeOffset]::UtcNow
                    $lastNativeProgressStage = "native traversal complete"
                }
                elseif ($line -match '^README native audit complete\.$') {
                    $nativeTraversalStarted = $false
                    $lastNativeProgressUtc = $null
                    $lastNativeProgressStage = ""
                }
                elseif ($line -match '^README audit .+: (?:passed|failed);') {
                    $nativeTraversalStarted = $false
                    $lastNativeProgressUtc = $null
                    $lastNativeProgressStage = ""
                }
            }
            if ($stdoutOpen) {
                $stdoutTask = $runnerProcess.StandardOutput.ReadLineAsync()
            }
        }

        if ($stderrOpen -and $stderrTask.IsCompleted) {
            $line = $stderrTask.GetAwaiter().GetResult()
            if ($null -eq $line) {
                $stderrOpen = $false
            }
            else {
                Write-Host $line -ForegroundColor DarkGray
                $readOutput = $true
            }
            if ($stderrOpen) {
                $stderrTask = $runnerProcess.StandardError.ReadLineAsync()
            }
        }

        $nowUtc = [DateTimeOffset]::UtcNow
        if ($runnerProcess.HasExited) {
            if ($null -eq $rootExitObservedUtc) { $rootExitObservedUtc = $nowUtc }
            if (($stdoutOpen -or $stderrOpen) -and
                ($nowUtc - $rootExitObservedUtc).TotalSeconds -ge 5) {
                Stop-ReadmeAuditForWatchdog `
                    -Process $runnerProcess `
                    -TimeoutKind "post-exit-pipe-drain" `
                    -TimeoutSeconds 5 `
                    -Case $currentCase
                throw "README audit runner exited while a descendant held an output pipe open at '$currentCase'."
            }
        }
        else {
            if (($nowUtc - $caseStartedUtc).CompareTo($caseWatchdog) -ge 0) {
                Stop-ReadmeAuditForWatchdog `
                    -Process $runnerProcess `
                    -TimeoutKind "per-case-deadline" `
                    -TimeoutSeconds ([int]$caseWatchdog.TotalSeconds) `
                    -Case $currentCase `
                    -Progress $lastNativeProgressStage
                throw "README audit exceeded its 30-minute per-case watchdog at '$currentCase'; the audit process tree was terminated."
            }
            if ($nativeTraversalStarted -and $null -ne $lastNativeProgressUtc -and
                ($nowUtc - $lastNativeProgressUtc).TotalSeconds -ge 90) {
                Stop-ReadmeAuditForWatchdog `
                    -Process $runnerProcess `
                    -TimeoutKind "native-traversal-inactivity" `
                    -TimeoutSeconds ([int]$nativeTraversalInactivityWatchdog.TotalSeconds) `
                    -Case $currentCase `
                    -Progress $lastNativeProgressStage
                throw "README native traversal made no progress for 90 seconds at '$currentCase' (last stage: '$lastNativeProgressStage'); the audit process tree was terminated."
            }
        }

        if (-not $readOutput) {
            Start-Sleep -Milliseconds 200
        }
    }

    $runnerProcess.WaitForExit()
    if ($runnerProcess.ExitCode -ne 0) {
        throw "Top README audit failed. See '$OutputRoot\summary.md'."
    }
}
finally {
    if ($null -ne $runnerProcess) {
        try {
            if (-not $runnerProcess.HasExited) {
                Stop-ReadmeAuditProcessTree -Process $runnerProcess | Out-Null
            }
        }
        finally {
            $runnerProcess.Dispose()
        }
    }
    $env:JITHUB_README_AUDIT_GITHUB_TOKEN = $previousToken
    $env:JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID = $previousAccountId
    $env:JITHUB_README_AUDIT_RASTER_DIAGNOSTICS = $previousRasterDiagnostics
    $auditToken = $null
    $auditAccountId = $null
}

Write-Host "Top README audit passed. Report: '$OutputRoot\summary.md'."
