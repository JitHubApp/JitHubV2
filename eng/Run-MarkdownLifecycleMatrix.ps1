[CmdletBinding()]
param(
    [string]$OutputRoot = "",
    [string]$Repository = "JitHubApp/JitHubV2",
    [ValidateRange(30, 300)]
    [int]$CaseTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\markdown-security-lifecycle"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$appProject = Join-Path $repoRoot "JitHub.WinUI\JitHub.WinUI.csproj"
$automationProject = Join-Path $repoRoot "JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj"
$configurations = @("Debug", "Release")
$themes = "light,dark,highcontrast"
$expectedHostNames = @(
    "issue-body",
    "issue-comment",
    "issue-comment-form",
    "pull-request-body",
    "pull-request-comment",
    "pull-request-review",
    "pull-request-review-comment",
    "pull-request-review-reply-form",
    "pull-request-comment-form",
    "pull-request-compact-comment-form",
    "commit-body",
    "commit-comment",
    "commit-comment-form",
    "my-issues-body",
    "my-issues-comment",
    "my-pull-requests-body",
    "my-pull-requests-comment",
    "my-pull-requests-review",
    "my-pull-requests-review-comment",
    "repository-readme",
    "profile-overview-readme",
    "profile-readme"
)
$expectedHosts = $expectedHostNames.Count
# Twenty non-composer hosts run at all three viewports and text scales. The PR
# inline and compact composers partition the same nine real responsive states.
$expectedCases = ((20 * 3 * 3) + 9) * 3

function Get-SourceSnapshotHash {
    $roots = @(
        (Join-Path $repoRoot "JitHub.WinUI"),
        (Join-Path $repoRoot "JitHub.WinUI.Automation"),
        (Join-Path $repoRoot "JitHub.WinUI.Tests"),
        (Join-Path $repoRoot "MarkdownRenderer"),
        (Join-Path $repoRoot "eng")
    )
    $files = $roots |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File } |
        Where-Object {
            $_.FullName -notmatch "[\\/](bin|obj|artifacts)[\\/]" -and
            $_.Extension -notin @(".user", ".suo")
        } |
        Sort-Object FullName

    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($file in $files) {
            $relativePath = [System.IO.Path]::GetRelativePath($repoRoot, $file.FullName).Replace("\", "/")
            $hash.AppendData([System.Text.Encoding]::UTF8.GetBytes($relativePath))
            $stream = [System.IO.File]::OpenRead($file.FullName)
            try {
                $buffer = [byte[]]::new(131072)
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hash.AppendData($buffer, 0, $read)
                }
            }
            finally {
                $stream.Dispose()
            }
        }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Invoke-CheckedDotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-LifecycleAppProcessIds {
    param(
        [Parameter(Mandatory)][string]$RunOutput,
        [Parameter(Mandatory)][string]$AppPath
    )

    $ids = [System.Collections.Generic.HashSet[int]]::new()
    Get-ChildItem -LiteralPath (Join-Path $RunOutput ".runtime") -Recurse -Filter "app-ready.json" -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $signal = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                if ($signal.ProcessId -gt 0) {
                    [void]$ids.Add([int]$signal.ProcessId)
                }
            }
            catch {
                # A hard timeout may interrupt an atomic signal write. The runner tree
                # and executable-path sweep below remain authoritative cleanup gates.
            }
        }

    Get-CimInstance Win32_Process -Filter "Name = 'JitHub.WinUI.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            [System.IO.Path]::GetFullPath($_.ExecutablePath) -eq [System.IO.Path]::GetFullPath($AppPath)
        } |
        ForEach-Object { [void]$ids.Add([int]$_.ProcessId) }
    return @($ids)
}

function Stop-OwnedLifecycleProcesses {
    param(
        [Parameter(Mandatory)][string]$RunOutput,
        [Parameter(Mandatory)][string]$AppPath,
        [int]$RunnerProcessId = 0
    )

    if ($RunnerProcessId -gt 0 -and (Get-Process -Id $RunnerProcessId -ErrorAction SilentlyContinue)) {
        & taskkill.exe /PID $RunnerProcessId /T /F 2>$null | Out-Null
    }

    foreach ($processId in @(Get-LifecycleAppProcessIds -RunOutput $RunOutput -AppPath $AppPath)) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -ne $process -and $process.ProcessName -eq "JitHub.WinUI") {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            try { $process.WaitForExit(5000) | Out-Null } catch { }
        }
    }

    Start-Sleep -Milliseconds 150
}

function Assert-DesktopLifecycleProcessesClosed {
    param(
        [Parameter(Mandatory)][string]$RunOutput,
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)][string]$RunnerAssembly
    )

    $ownedApps = @(Get-LifecycleAppProcessIds -RunOutput $RunOutput -AppPath $AppPath |
        Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    $ownedRunners = @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -like "*$RunnerAssembly*" -and
            $_.CommandLine -like "*markdown-host-lifecycle*"
        })
    if ($ownedApps.Count -gt 0 -or $ownedRunners.Count -gt 0) {
        throw "Lifecycle process cleanup failed. Apps=$($ownedApps -join ',') Runners=$($ownedRunners.ProcessId -join ',')."
    }
}

function Invoke-OneLifecycleCase {
    param(
        [Parameter(Mandatory)][string]$Configuration,
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)][string]$RunnerAssembly,
        [Parameter(Mandatory)][string]$RunOutput,
        [Parameter(Mandatory)][int]$Invocation,
        [Parameter(Mandatory)][bool]$Resume
    )

    Assert-DesktopLifecycleProcessesClosed -RunOutput $RunOutput -AppPath $AppPath -RunnerAssembly $RunnerAssembly
    $logDirectory = Join-Path $RunOutput "runner-logs"
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $stdout = Join-Path $logDirectory ("case-{0:D4}.stdout.log" -f $Invocation)
    $stderr = Join-Path $logDirectory ("case-{0:D4}.stderr.log" -f $Invocation)

    $previousResume = $env:JITHUB_AUTOMATION_MARKDOWN_RESUME
    $previousMaxCases = $env:JITHUB_AUTOMATION_MARKDOWN_MAX_CASES
    $env:JITHUB_AUTOMATION_MARKDOWN_RESUME = if ($Resume) { "1" } else { "0" }
    $env:JITHUB_AUTOMATION_MARKDOWN_MAX_CASES = "1"
    $runner = $null
    try {
        $arguments = @(
            $RunnerAssembly,
            "--probe=markdown-host-lifecycle",
            "--configuration=$Configuration",
            "--themes=$themes",
            "--repo=$Repository",
            "--app=$AppPath",
            "--out=$RunOutput")
        $runner = Start-Process dotnet -ArgumentList $arguments -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        if (-not $runner.WaitForExit($CaseTimeoutSeconds * 1000)) {
            Stop-OwnedLifecycleProcesses -RunOutput $RunOutput -AppPath $AppPath -RunnerProcessId $runner.Id
            throw "$Configuration lifecycle invocation $Invocation exceeded the $CaseTimeoutSeconds-second hard timeout. Logs: '$stdout', '$stderr'."
        }

        $runner.Refresh()
        if ($runner.ExitCode -ne 0) {
            throw "$Configuration lifecycle invocation $Invocation failed with exit code $($runner.ExitCode). Logs: '$stdout', '$stderr'."
        }
    }
    finally {
        if ($null -ne $runner) {
            Stop-OwnedLifecycleProcesses -RunOutput $RunOutput -AppPath $AppPath -RunnerProcessId $runner.Id
            $runner.Dispose()
        }
        $env:JITHUB_AUTOMATION_MARKDOWN_RESUME = $previousResume
        $env:JITHUB_AUTOMATION_MARKDOWN_MAX_CASES = $previousMaxCases
        Assert-DesktopLifecycleProcessesClosed -RunOutput $RunOutput -AppPath $AppPath -RunnerAssembly $RunnerAssembly
    }
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$sourceSnapshot = Get-SourceSnapshotHash
$runSummaries = @()

foreach ($configuration in $configurations) {
    Write-Host "Building current-source $configuration app and automation harness..."
    Invoke-CheckedDotNet @(
        "build", $appProject, "-c", $configuration,
        "--no-restore", "--disable-build-servers", "-m:1")
    Invoke-CheckedDotNet @(
        "build", $automationProject, "-c", $configuration,
        "--no-restore", "--disable-build-servers", "-m:1")

    $appPath = Join-Path $repoRoot (
        "JitHub.WinUI\bin\x64\$configuration\net10.0-windows10.0.26100.0\win-x64\JitHub.WinUI.exe")
    if (-not (Test-Path -LiteralPath $appPath)) {
        throw "Current-source $configuration executable was not produced at '$appPath'."
    }

    $runnerAssembly = Join-Path $repoRoot (
        "JitHub.WinUI.Automation\bin\$configuration\net10.0-windows10.0.19041.0\JitHub.WinUI.Automation.dll")
    if (-not (Test-Path -LiteralPath $runnerAssembly)) {
        throw "Current-source $configuration automation assembly was not produced at '$runnerAssembly'."
    }

    $runOutput = Join-Path $OutputRoot $configuration.ToLowerInvariant()
    New-Item -ItemType Directory -Force -Path $runOutput | Out-Null
    $manifestPath = Join-Path $runOutput "markdown-lifecycle-manifest.json"
    if (Test-Path -LiteralPath $manifestPath) {
        throw "Output '$runOutput' already contains a lifecycle manifest. Use a fresh OutputRoot for current-source evidence."
    }

    Write-Host "Running $configuration Markdown lifecycle matrix as one bounded process per case..."
    $invocation = 0
    $maximumInvocations = $expectedCases + 3
    do {
        $invocation++
        Invoke-OneLifecycleCase `
            -Configuration $configuration `
            -AppPath $appPath `
            -RunnerAssembly $runnerAssembly `
            -RunOutput $runOutput `
            -Invocation $invocation `
            -Resume ($invocation -gt 1)

        if (-not (Test-Path -LiteralPath $manifestPath)) {
            throw "$configuration lifecycle invocation $invocation did not persist a manifest."
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $passed = @($manifest.cases | Where-Object { $_.status -eq "passed" -and $_.cleanClose }).Count
        Write-Host "$configuration lifecycle progress: $passed/$expectedCases cases; completed=$($manifest.completed)."
        if ($invocation -ge $maximumInvocations -and -not $manifest.completed) {
            throw "$configuration lifecycle matrix did not complete within $maximumInvocations bounded invocations."
        }
    }
    until ($manifest.completed)

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (-not $manifest.completed -or
        $manifest.version -ne 5 -or
        $manifest.runScope -ne "full-matrix" -or
        -not [string]::IsNullOrWhiteSpace([string]$manifest.requestedTarget) -or
        -not $manifest.requiresSupplementalCases -or
        $manifest.expectedHostCount -ne $expectedHosts -or
        $manifest.expectedCaseCount -ne $expectedCases -or
        @($manifest.hosts | Where-Object { $_ -notin $expectedHostNames }).Count -ne 0 -or
        @($expectedHostNames | Where-Object { $_ -notin $manifest.hosts }).Count -ne 0 -or
        @($manifest.cases).Count -ne $expectedCases -or
        @($manifest.cases | Where-Object {
            $_.status -ne "passed" -or
            -not $_.cleanClose -or
            -not $_.hostReady -or
            -not $_.realHostComposition -or
            -not $_.hostUnloadOnClose -or
            -not $_.selection -or
            -not $_.pointerDragSelection -or
            -not $_.ctrlC -or
            -not $_.contextCopy -or
            -not $_.keyboardLinkFocus -or
            -not $_.internalRepositoryRoute -or
            -not $_.internalUserRoute -or
            -not $_.externalBrowserRoute -or
            -not $_.inlineSvg -or
            -not $_.remoteImageNotice -or
            -not $_.relayout -or
            -not $_.repeatedRelayout -or
            $_.relayoutCycles -lt 6 -or
            -not $_.scroll -or
            -not $_.memoryBudget -or
            -not $_.retainedMemoryBudget -or
            [string]::IsNullOrWhiteSpace([string]$_.screenshot) -or
            -not (Test-Path -LiteralPath (Join-Path $runOutput ([string]$_.screenshot))) -or
            $_.unhandledLogCount -ne 0 -or
            $_.exitCode -ne 0
        }).Count -ne 0 -or
        $manifest.resourceMapAbsentCase.status -ne "passed" -or
        -not $manifest.resourceMapAbsentCase.cleanClose -or
        -not $manifest.resourceMapAbsentCase.realHostComposition -or
        -not $manifest.resourceMapAbsentCase.hostUnloadOnClose -or
        -not $manifest.resourceMapAbsentCase.pointerDragSelection -or
        -not $manifest.resourceMapAbsentCase.internalRepositoryRoute -or
        -not $manifest.resourceMapAbsentCase.internalUserRoute -or
        -not $manifest.resourceMapAbsentCase.externalBrowserRoute -or
        -not $manifest.resourceMapAbsentCase.repeatedRelayout -or
        -not $manifest.resourceMapAbsentCase.retainedMemoryBudget -or
        -not $manifest.resourceMapAbsentCase.memoryBudget -or
        $manifest.securityPolicyCase.status -ne "passed" -or
        -not $manifest.securityPolicyCase.cleanClose -or
        -not $manifest.securityPolicyCase.realHostComposition -or
        -not $manifest.securityPolicyCase.hostUnloadOnClose -or
        -not $manifest.securityPolicyCase.pointerDragSelection -or
        -not $manifest.securityPolicyCase.internalRepositoryRoute -or
        -not $manifest.securityPolicyCase.internalUserRoute -or
        -not $manifest.securityPolicyCase.externalBrowserRoute -or
        -not $manifest.securityPolicyCase.repeatedRelayout -or
        -not $manifest.securityPolicyCase.retainedMemoryBudget -or
        -not $manifest.securityPolicyCase.hostileSvgBudget -or
        -not $manifest.securityPolicyCase.oversizedSvgBudget -or
        -not $manifest.securityPolicyCase.redirectPolicyFixture -or
        -not $manifest.securityPolicyCase.remoteImagePolicy -or
        -not $manifest.securityPolicyCase.memoryBudget) {
        throw "$configuration lifecycle manifest failed completeness or clean-close validation."
    }

    $runSummaries += [pscustomobject]@{
        configuration = $configuration
        manifest = [System.IO.Path]::GetRelativePath($OutputRoot, $manifestPath).Replace("\", "/")
        appPath = $manifest.appPath
        appSha256 = $manifest.appSha256
        appLastWriteUtc = $manifest.appLastWriteUtc
        appAssemblyPath = $manifest.appAssemblyPath
        appAssemblySha256 = $manifest.appAssemblySha256
        appAssemblyLastWriteUtc = $manifest.appAssemblyLastWriteUtc
        automationAssemblyPath = $manifest.automationAssemblyPath
        automationAssemblySha256 = $manifest.automationAssemblySha256
        automationAssemblyLastWriteUtc = $manifest.automationAssemblyLastWriteUtc
        hostCount = $manifest.expectedHostCount
        caseCount = @($manifest.cases).Count
        resourceMapAbsent = $manifest.resourceMapAbsentCase.status
        securityPolicy = $manifest.securityPolicyCase.status
        boundedInvocationCount = $invocation
        completed = $manifest.completed
    }
}

$endingSourceSnapshot = Get-SourceSnapshotHash
if ($endingSourceSnapshot -ne $sourceSnapshot) {
    throw "Source changed while the lifecycle matrix was running. Rebuild and rerun so evidence matches one source snapshot."
}

$combinedManifest = [ordered]@{
    version = 5
    runScope = "full-matrix"
    requiresSupplementalCases = $true
    sourceSnapshotSha256 = $sourceSnapshot
    expectedConfigurations = $configurations
    expectedHostCount = $expectedHosts
    expectedCasesPerConfiguration = $expectedCases
    expectedThemes = @("light", "dark", "highcontrast")
    expectedTextScalePercents = @(100, 150, 200)
    expectedViewports = @("wide:1366x900", "snapped:760x650", "compact:640x600")
    perCaseHardTimeoutSeconds = $CaseTimeoutSeconds
    oneRunnerProcessPerCase = $true
    supplementalCasesPerConfiguration = @("resource-map-absent", "security-policy")
    completed = $true
    runs = $runSummaries
}
$combinedPath = Join-Path $OutputRoot "markdown-lifecycle-combined-manifest.json"
[System.IO.File]::WriteAllText(
    $combinedPath,
    ($combinedManifest | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Markdown lifecycle matrix complete: $combinedPath"
