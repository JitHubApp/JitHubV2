<#
.SYNOPSIS
Runs the schema-10 WinUI performance harness in a counterbalanced R1,C1,C2,R2 order.

.DESCRIPTION
This is a release-evidence orchestrator, not a benchmark runner with retry logic. It:

* requires exact, different reference and candidate harness executables;
* captures each build's complete runtime-output manifest before starting, excluding
  only non-runtime PDB and XML files, and requires distinct manifest identities;
* retains the apphost, runtime configuration, and three managed DLL SHA-256 values
  as explicit human-reviewable evidence;
* creates one unique run directory and four create-new report paths;
* invokes each role exactly once, in R1,C1,C2,R2 order, without retries;
* validates every component as absolute-qualified baseline evidence with
   Test-MarkdownWinUIPerformanceGates.ps1;
* verifies role build identity, build-artifact stability, and machine stability; and
* writes a create-new aggregate verdict bound to every executable, gate, report,
  and gate-log SHA-256.

All four runs are baseline-mode component measurements. Cross-build regression is
decided only by the counterbalanced aggregate; requiring either middle run to pass
an R1-only comparison would reintroduce the linear time-order bias that the
R1,C1,C2,R2 design is intended to cancel. The aggregate gates only the ABBA
contrast ((C1 + C2) - (R1 + R2)) / 2 against the hybrid relative/absolute
allowance centered on (R1 + R2) / 2. Pair deltas, role spans, and the inferred
linear drift remain serialized diagnostics and never alter that verdict. Internal
stationarity and absolute qualification belong to the strict gate applied
independently to every component report.

The harness and gate are never retried. A failed or timed-out invocation stops the
sequence; already-created evidence remains in the unique run directory.

.PARAMETER ReferenceHarnessPath
Absolute path to the reference MarkdownRenderer.PerformanceHarness.exe.

.PARAMETER CandidateHarnessPath
Absolute path to the candidate MarkdownRenderer.PerformanceHarness.exe.

.PARAMETER OutputRoot
Absolute or relative parent directory beneath which a unique evidence directory is
created. Existing evidence is never overwritten.

.PARAMETER GateScriptPath
Path to the strict single-report schema-10 gate. Defaults to the sibling repository
script.

.PARAMETER RunTimeoutMinutes
Maximum wall-clock time for each harness process. A timeout terminates that process
tree and fails the sequence; it is not retried.

.OUTPUTS
The absolute path of counterbalanced-verdict.json after a complete passing run.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReferenceHarnessPath,

    [Parameter(Mandatory)]
    [string] $CandidateHarnessPath,

    [Parameter(Mandatory)]
    [string] $OutputRoot,

    [string] $GateScriptPath = (Join-Path $PSScriptRoot 'Test-MarkdownWinUIPerformanceGates.ps1'),

    [ValidateRange(1, 240)]
    [int] $RunTimeoutMinutes = 45
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:AggregateSchemaVersion = 2
$script:AggregateProviderName = 'MarkdownRenderer-Counterbalanced-Performance'
$script:AggregatePolicy = 'schema10-r1-c1-c2-r2-abba-contrast-v2'
$script:RuntimeOutputManifestPolicy = 'runtime-output-manifest-v1'
$script:Utf8NoBom = [Text.UTF8Encoding]::new($false, $true)

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Label,
        [string] $RequiredExtension
    )

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw "$Label path must be absolute: '$Path'."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label file does not exist: '$Path'."
    }

    $resolved = (Get-Item -LiteralPath $Path -Force).FullName
    if (-not [string]::IsNullOrEmpty($RequiredExtension) -and
        -not [string]::Equals(
            [IO.Path]::GetExtension($resolved),
            $RequiredExtension,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be a '$RequiredExtension' file: '$resolved'."
    }

    return $resolved
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-ExpectedBuildArtifactSet {
    param(
        [Parameter(Mandatory)][string] $ExecutablePath,
        [Parameter(Mandatory)][string] $Label
    )

    if (-not [string]::Equals(
        [IO.Path]::GetFileName($ExecutablePath),
        'MarkdownRenderer.PerformanceHarness.exe',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label executable must be named 'MarkdownRenderer.PerformanceHarness.exe'."
    }

    $directory = [IO.Path]::GetDirectoryName($ExecutablePath)
    $definitions = @(
        [pscustomobject]@{ Name = 'executable'; FileName = 'MarkdownRenderer.PerformanceHarness.exe'; Path = $ExecutablePath },
        [pscustomobject]@{ Name = 'runtimeConfig'; FileName = 'MarkdownRenderer.PerformanceHarness.runtimeconfig.json'; Path = ([IO.Path]::ChangeExtension($ExecutablePath, '.runtimeconfig.json')) },
        [pscustomobject]@{ Name = 'performanceHarnessDll'; FileName = 'MarkdownRenderer.PerformanceHarness.dll'; Path = (Join-Path $directory 'MarkdownRenderer.PerformanceHarness.dll') },
        [pscustomobject]@{ Name = 'markdownRendererDll'; FileName = 'MarkdownRenderer.dll'; Path = (Join-Path $directory 'MarkdownRenderer.dll') },
        [pscustomobject]@{ Name = 'markdownRendererCoreDll'; FileName = 'MarkdownRenderer.Core.dll'; Path = (Join-Path $directory 'MarkdownRenderer.Core.dll') })

    $result = [ordered]@{}
    foreach ($definition in $definitions) {
        $requiredExtension = if ($definition.Name -eq 'executable') {
            '.exe'
        }
        elseif ($definition.Name -eq 'runtimeConfig') {
            '.json'
        }
        else {
            '.dll'
        }
        $path = Resolve-RequiredFile `
            -Path $definition.Path `
            -Label "$Label artifact '$($definition.Name)'" `
            -RequiredExtension $requiredExtension
        $result[$definition.Name] = [ordered]@{
            fileName = $definition.FileName
            path = $path
            sha256 = Get-Sha256 -Path $path
        }
    }

    return $result
}

function Get-RuntimeOutputManifest {
    param(
        [Parameter(Mandatory)][string] $ExecutablePath,
        [Parameter(Mandatory)][string] $Label
    )

    $directoryPath = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ExecutablePath))
    if ([string]::IsNullOrWhiteSpace($directoryPath) -or
        -not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
        throw "$Label runtime-output directory does not exist: '$directoryPath'."
    }
    $directoryPath = (Get-Item -LiteralPath $directoryPath -Force).FullName

    $captureSnapshot = {
        param(
            [Parameter(Mandatory)][string] $RootPath,
            [Parameter(Mandatory)][string] $SnapshotLabel
        )

        $pathsByRelativePath =
            [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        $relativePaths = [Collections.Generic.List[string]]::new()
        $pendingDirectories = [Collections.Generic.Stack[string]]::new()
        $pendingDirectories.Push($RootPath)
        while ($pendingDirectories.Count -gt 0) {
            $currentDirectory = $pendingDirectories.Pop()
            foreach ($entryPath in [IO.Directory]::EnumerateFileSystemEntries($currentDirectory)) {
                $attributes = [IO.File]::GetAttributes($entryPath)
                $fullPath = [IO.Path]::GetFullPath($entryPath)
                $relativePath =
                    [IO.Path]::GetRelativePath($RootPath, $fullPath).Replace('\', '/')
                $isDirectory =
                    ($attributes -band [IO.FileAttributes]::Directory) -ne 0
                $extension = [IO.Path]::GetExtension($fullPath)
                if (-not $isDirectory -and
                    ([string]::Equals(
                            $extension,
                            '.pdb',
                            [StringComparison]::OrdinalIgnoreCase) -or
                     [string]::Equals(
                            $extension,
                            '.xml',
                            [StringComparison]::OrdinalIgnoreCase))) {
                    continue
                }
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "$SnapshotLabel runtime output contains unsupported reparse point '$relativePath'."
                }

                if ($isDirectory) {
                    $pendingDirectories.Push($fullPath)
                    continue
                }

                if (-not $pathsByRelativePath.TryAdd($relativePath, $fullPath)) {
                    throw "$SnapshotLabel runtime-output manifest contains duplicate relative path '$relativePath'."
                }
                $relativePaths.Add($relativePath)
            }
        }
        $relativePaths.Sort([StringComparer]::Ordinal)

        $canonicalLines = [Collections.Generic.List[string]]::new()
        $canonicalLines.Add($script:RuntimeOutputManifestPolicy)
        $files = [Collections.Generic.List[object]]::new()
        foreach ($relativePath in $relativePaths) {
            $fullPath = $pathsByRelativePath[$relativePath]
            $stream = [IO.FileStream]::new(
                $fullPath,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::Read,
                131072,
                [IO.FileOptions]::SequentialScan)
            try {
                [int64]$byteLength = $stream.Length
                $sha256 = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($stream))
                if ($stream.Length -ne $byteLength) {
                    throw "$SnapshotLabel runtime file '$relativePath' changed while it was being hashed."
                }
            }
            finally {
                $stream.Dispose()
            }

            $canonicalLines.Add([string]::Concat(
                $relativePath,
                '|',
                $byteLength.ToString([Globalization.CultureInfo]::InvariantCulture),
                '|',
                $sha256))
            $files.Add([ordered]@{
                relativePath = $relativePath
                byteLength = $byteLength
                sha256 = $sha256
            })
        }

        $canonicalPayload = $script:Utf8NoBom.GetBytes(($canonicalLines -join "`n"))
        return [pscustomobject]@{
            sha256 = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($canonicalPayload))
            files = @($files)
        }
    }

    $first = & $captureSnapshot $directoryPath $Label
    $second = & $captureSnapshot $directoryPath $Label
    if ([string]$first.sha256 -cne [string]$second.sha256) {
        throw "$Label runtime output changed while its deployment identity was being captured."
    }

    $manifestSha256 = [string]$second.sha256
    return [pscustomobject]@{
        policy = $script:RuntimeOutputManifestPolicy
        rootPath = $directoryPath
        identity = [string]::Concat(
            $script:RuntimeOutputManifestPolicy,
            ':',
            $manifestSha256)
        sha256 = $manifestSha256
        fileCount = $second.files.Count
        files = @($second.files)
    }
}

function Assert-RuntimeOutputManifestUnchanged {
    param(
        [Parameter(Mandatory)][object] $ExpectedManifest,
        [Parameter(Mandatory)][string] $ExecutablePath,
        [Parameter(Mandatory)][string] $Label
    )

    $actualManifest = Get-RuntimeOutputManifest `
        -ExecutablePath $ExecutablePath `
        -Label $Label
    if ([string]$actualManifest.identity -cne [string]$ExpectedManifest.identity) {
        throw "$Label runtime output changed during the counterbalanced sequence. Expected identity $($ExpectedManifest.identity); observed $($actualManifest.identity)."
    }
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ExpectedSha256,
        [Parameter(Mandatory)][string] $Label
    )

    $actual = Get-Sha256 -Path $Path
    if (-not [string]::Equals($actual, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label changed during the counterbalanced sequence. Expected SHA-256 $ExpectedSha256; observed $actual."
    }
}

function Assert-BuildArtifactSetUnchanged {
    param(
        [Parameter(Mandatory)][Collections.IDictionary] $ArtifactSet,
        [Parameter(Mandatory)][string] $Label
    )

    foreach ($name in @('executable', 'runtimeConfig', 'performanceHarnessDll', 'markdownRendererDll', 'markdownRendererCoreDll')) {
        Assert-FileHash `
            -Path ([string]$ArtifactSet[$name].path) `
            -ExpectedSha256 ([string]$ArtifactSet[$name].sha256) `
            -Label "$Label artifact '$name'"
    }
}

function Write-NewUtf8File {
    param(
        [Parameter(Mandatory)][string] $Path,
        [AllowEmptyString()][Parameter(Mandatory)][string] $Content
    )

    $parent = [IO.Path]::GetDirectoryName($Path)
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "Create-new output '$Path' must have a parent directory."
    }

    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $bytes = $script:Utf8NoBom.GetBytes($Content)
    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-ProcessOnce {
    param(
        [Parameter(Mandatory)][string] $ExecutablePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $WorkingDirectory,
        [Parameter(Mandatory)][string] $StdOutPath,
        [Parameter(Mandatory)][string] $StdErrPath,
        [Parameter(Mandatory)][int] $TimeoutMinutes
    )

    foreach ($outputPath in @($StdOutPath, $StdErrPath)) {
        if (Test-Path -LiteralPath $outputPath) {
            throw "Process output '$outputPath' already exists; evidence is create-new only."
        }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $startedUtc = [DateTimeOffset]::UtcNow
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    try {
        if (-not $process.Start()) {
            throw "Process '$ExecutablePath' did not start."
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timeout = [TimeSpan]::FromMinutes($TimeoutMinutes)
        while (-not $process.WaitForExit(1000)) {
            if ($timer.Elapsed -lt $timeout) {
                continue
            }

            $timedOut = $true
            try {
                $process.Kill($true)
            }
            catch {
                # Preserve the original timeout classification even if the process
                # exits between WaitForExit and Kill.
            }
            $process.WaitForExit()
            break
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        Write-NewUtf8File -Path $StdOutPath -Content $stdout
        Write-NewUtf8File -Path $StdErrPath -Content $stderr

        return [pscustomobject]@{
            StartedUtc = $startedUtc
            CompletedUtc = [DateTimeOffset]::UtcNow
            ElapsedMilliseconds = [Math]::Round($timer.Elapsed.TotalMilliseconds, 3)
            ExitCode = if ($timedOut) { $null } else { $process.ExitCode }
            TimedOut = $timedOut
            StdOutSha256 = Get-Sha256 -Path $StdOutPath
            StdErrSha256 = Get-Sha256 -Path $StdErrPath
        }
    }
    finally {
        $timer.Stop()
        $process.Dispose()
    }
}

function Read-JsonEvidence {
    param([Parameter(Mandatory)][string] $Path)

    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $json = $script:Utf8NoBom.GetString($bytes)
    $value = $json | ConvertFrom-Json -Depth 100
    if ($null -eq $value) {
        throw "Evidence '$Path' was empty."
    }

    return [pscustomobject]@{
        Value = $value
        Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    }
}

function Invoke-StrictReportGate {
    param(
        [Parameter(Mandatory)][string] $PowerShellPath,
        [Parameter(Mandatory)][string] $GatePath,
        [Parameter(Mandatory)][string] $ReportPath,
        [Parameter(Mandatory)][ValidateSet('Baseline')][string] $Mode,
        [Parameter(Mandatory)][string] $LogStem
    )

    $result = Invoke-ProcessOnce `
        -ExecutablePath $PowerShellPath `
        -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive',
            '-File', $GatePath,
            '-ReportPath', $ReportPath,
            '-RequiredRegressionMode', $Mode) `
        -WorkingDirectory ([IO.Path]::GetDirectoryName($GatePath)) `
        -StdOutPath "$LogStem.stdout.log" `
        -StdErrPath "$LogStem.stderr.log" `
        -TimeoutMinutes 5

    return $result
}

function Test-PathEqual {
    param([string] $Left, [string] $Right)

    return [string]::Equals(
        [IO.Path]::GetFullPath($Left),
        [IO.Path]::GetFullPath($Right),
        [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathWithinDirectory {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $DirectoryPath
    )

    $isContained = {
        param(
            [Parameter(Mandatory)][string] $CandidatePath,
            [Parameter(Mandatory)][string] $CandidateDirectory
        )

        $relativePath = [IO.Path]::GetRelativePath($CandidateDirectory, $CandidatePath)
        return -not [IO.Path]::IsPathRooted($relativePath) -and
            -not [string]::Equals($relativePath, '..', [StringComparison]::Ordinal) -and
            -not $relativePath.StartsWith(
                "..$([IO.Path]::DirectorySeparatorChar)",
                [StringComparison]::Ordinal) -and
            -not $relativePath.StartsWith(
                "..$([IO.Path]::AltDirectorySeparatorChar)",
                [StringComparison]::Ordinal)
    }
    $resolveExistingReparsePoints = {
        param([Parameter(Mandatory)][string] $CandidatePath)

        $pendingPath = [IO.Path]::GetFullPath($CandidatePath)
        $visitedPaths = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        $separators = [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        for ($redirectCount = 0; $redirectCount -lt 64; $redirectCount++) {
            if (-not $visitedPaths.Add($pendingPath)) {
                throw "Reparse-point cycle detected while resolving '$CandidatePath'."
            }

            $root = [IO.Path]::GetPathRoot($pendingPath)
            if ([string]::IsNullOrEmpty($root)) {
                throw "Path must have a root: '$CandidatePath'."
            }

            $current = $root
            $segments = $pendingPath.Substring($root.Length).Split(
                $separators,
                [StringSplitOptions]::RemoveEmptyEntries)
            $redirected = $false
            for ($index = 0; $index -lt $segments.Length; $index++) {
                $candidate = [IO.Path]::Combine($current, $segments[$index])
                $entry = if ([IO.Directory]::Exists($candidate)) {
                    [IO.DirectoryInfo]::new($candidate)
                }
                elseif ([IO.File]::Exists($candidate)) {
                    [IO.FileInfo]::new($candidate)
                }
                else {
                    $null
                }

                if ($null -eq $entry) {
                    for (; $index -lt $segments.Length; $index++) {
                        $current = [IO.Path]::Combine($current, $segments[$index])
                    }
                    return [IO.Path]::GetFullPath($current)
                }

                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $target = $entry.ResolveLinkTarget($false)
                    if ($null -eq $target) {
                        throw "Reparse-point target could not be resolved for '$($entry.FullName)'."
                    }
                    $pendingPath = $target.FullName
                    for ($remainder = $index + 1;
                         $remainder -lt $segments.Length;
                         $remainder++) {
                        $pendingPath = [IO.Path]::Combine(
                            $pendingPath,
                            $segments[$remainder])
                    }
                    $pendingPath = [IO.Path]::GetFullPath($pendingPath)
                    $redirected = $true
                    break
                }

                $current = $entry.FullName
            }

            if (-not $redirected) {
                return [IO.Path]::GetFullPath($current)
            }
        }

        throw "Too many reparse-point redirects while resolving '$CandidatePath'."
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullDirectory = [IO.Path]::GetFullPath($DirectoryPath)
    if (& $isContained $fullPath $fullDirectory) {
        return $true
    }

    $physicalPath = & $resolveExistingReparsePoints $fullPath
    $physicalDirectory = & $resolveExistingReparsePoints $fullDirectory
    return & $isContained $physicalPath $physicalDirectory
}

function Assert-ReportRoleBinding {
    param(
        [Parameter(Mandatory)][object] $Report,
        [Parameter(Mandatory)][ValidateSet('Baseline')][string] $Mode,
        [Parameter(Mandatory)][Collections.IDictionary] $ExpectedArtifactSet,
        [Parameter(Mandatory)][string] $ExpectedBuildIdentity
    )

    $expectedMode = $Mode.ToLowerInvariant()
    if ([int]$Report.schemaVersion -ne 10 -or
        [string]$Report.providerName -cne 'MarkdownRenderer-Performance' -or
        -not [bool]$Report.isReleaseEvidence -or
        -not [bool]$Report.passed -or
        [string]$Report.regression.mode -cne $expectedMode) {
        throw "Generated $Mode report did not retain its gated schema-10 role contract."
    }

    if ([string]$Report.buildIdentity -cne $ExpectedBuildIdentity) {
        throw "Generated $Mode report build identity does not match the requested runtime output. Expected '$ExpectedBuildIdentity'; observed '$($Report.buildIdentity)'."
    }

    foreach ($name in @('executable', 'runtimeConfig', 'performanceHarnessDll', 'markdownRendererDll', 'markdownRendererCoreDll')) {
        $actual = $Report.buildArtifacts.$name
        $expected = $ExpectedArtifactSet[$name]
        if ([string]$actual.fileName -cne [string]$expected.fileName -or
            -not (Test-PathEqual -Left ([string]$actual.path) -Right ([string]$expected.path)) -or
            -not [string]::Equals(
                [string]$actual.sha256,
                [string]$expected.sha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Generated $Mode report is not bound to requested build artifact '$name'."
        }
    }

    if (-not [string]::IsNullOrEmpty([string]$Report.regression.referencePath) -or
        -not [string]::IsNullOrEmpty([string]$Report.regression.referenceReportSha256)) {
        throw 'A generated baseline component unexpectedly bound a reference report.'
    }
}

function Get-ArtifactHashSet {
    param([Parameter(Mandatory)][object] $Report)

    $result = [ordered]@{}
    foreach ($name in @('executable', 'runtimeConfig', 'performanceHarnessDll', 'markdownRendererDll', 'markdownRendererCoreDll')) {
        $artifact = $Report.buildArtifacts.$name
        $result[$name] = [ordered]@{
            fileName = [string]$artifact.fileName
            path = [IO.Path]::GetFullPath([string]$artifact.path)
            sha256 = ([string]$artifact.sha256).ToUpperInvariant()
        }
    }
    return $result
}

function Test-ArtifactHashSetEqual {
    param(
        [Parameter(Mandatory)][Collections.IDictionary] $Left,
        [Parameter(Mandatory)][Collections.IDictionary] $Right
    )

    foreach ($name in @('executable', 'runtimeConfig', 'performanceHarnessDll', 'markdownRendererDll', 'markdownRendererCoreDll')) {
        if (-not [string]::Equals(
            [string]$Left[$name].sha256,
            [string]$Right[$name].sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    return $true
}

function Test-StableMachineEnvironment {
    param(
        [Parameter(Mandatory)][object] $Left,
        [Parameter(Mandatory)][object] $Right
    )

    $exactFields = @(
        'machineInstanceSha256', 'cpu', 'logicalProcessorCount', 'osDescription',
        'osVersion', 'osArchitecture', 'processArchitecture', 'frameworkDescription',
        'displayDeviceName', 'gpuAdapterIdentitySha256', 'gpuDriverVersion',
        'displayAdapterIdentitySha256', 'displayAdapterDriverVersion',
        'activePowerSchemeGuid', 'effectivePowerMode',
        'userConfiguredAcPowerModeGuid', 'userConfiguredDcPowerModeGuid', 'powerSource',
        'energySaverState', 'processPriorityClass', 'processPowerThrottlingMode',
        'gcServer', 'gcLatencyMode')
    foreach ($name in $exactFields) {
        if (-not [string]::Equals(
            [string]$Left.$name,
            [string]$Right.$name,
            [StringComparison]::Ordinal)) {
            return $false
        }
    }

    return [Math]::Abs([double]$Left.dpiScale - [double]$Right.dpiScale) -lt 0.01 -and
        [Math]::Abs([double]$Left.viewportWidthDips - [double]$Right.viewportWidthDips) -lt 1.0 -and
        [Math]::Abs([double]$Left.viewportHeightDips - [double]$Right.viewportHeightDips) -lt 1.0 -and
        [Math]::Abs([double]$Left.configuredRefreshRateHz - [double]$Right.configuredRefreshRateHz) -lt 0.5
}

function Test-StableSourceLookupEnvironment {
    param(
        [Parameter(Mandatory)][object] $Left,
        [Parameter(Mandatory)][object] $Right
    )

    return [int64]$Left.stopwatchFrequency -eq [int64]$Right.stopwatchFrequency -and
        [string]::Equals(
            [string]$Left.measurementThreadAffinityMask,
            [string]$Right.measurementThreadAffinityMask,
            [StringComparison]::Ordinal) -and
        [int]$Left.measurementProcessorGroup -eq [int]$Right.measurementProcessorGroup -and
        [int]$Left.measurementProcessorNumber -eq [int]$Right.measurementProcessorNumber -and
        [string]::Equals(
            [string]$Left.measurementThreadPriority,
            [string]$Right.measurementThreadPriority,
            [StringComparison]::Ordinal)
}

function Get-HodgesLehmann {
    param([Parameter(Mandatory)][double[]] $Values)

    if ($Values.Count -eq 0 -or @($Values | Where-Object { -not [double]::IsFinite($_) }).Count -ne 0) {
        throw 'Hodges-Lehmann input must contain finite values.'
    }

    $walsh = [Collections.Generic.List[double]]::new()
    for ($left = 0; $left -lt $Values.Count; $left++) {
        for ($right = $left; $right -lt $Values.Count; $right++) {
            $walsh.Add(($Values[$left] + $Values[$right]) / 2.0)
        }
    }
    $sorted = @($walsh | Sort-Object)
    $middle = [Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-RoleSpreadPercent {
    param(
        [Parameter(Mandatory)][double[]] $Values,
        [Parameter(Mandatory)][double] $Center
    )

    $spread = ([double]($Values | Measure-Object -Maximum).Maximum) -
        ([double]($Values | Measure-Object -Minimum).Minimum)
    if ([Math]::Abs($Center) -le 1e-12) {
        return $(if ([Math]::Abs($spread) -le 1e-12) { 0.0 } else { [double]::MaxValue })
    }
    return [Math]::Abs($spread) / [Math]::Abs($Center) * 100.0
}

function Get-DeltaPercent {
    param([double] $Candidate, [double] $Reference)

    if ([Math]::Abs($Reference) -le 1e-12) {
        return $(if ([Math]::Abs($Candidate) -le 1e-12) { 0.0 } else { [double]::MaxValue })
    }
    return (($Candidate / $Reference) - 1.0) * 100.0
}

function Test-LessThanOrNearlyEqual {
    param([double] $Value, [double] $Limit)

    if (-not [double]::IsFinite($Value) -or -not [double]::IsFinite($Limit)) {
        return $false
    }
    $scale = [Math]::Max(1.0, [Math]::Max([Math]::Abs($Value), [Math]::Abs($Limit)))
    return $Value -le $Limit -or [Math]::Abs($Value - $Limit) -le $scale * 1e-12
}

function Get-MetricValue {
    param(
        [Parameter(Mandatory)][object] $Report,
        [Parameter(Mandatory)][string] $Metric
    )

    if ($Metric -match '^firstViewport\.(cache-disabled|cache-hit)\.(\d+)\.p95Ms\.hodgesLehmann$') {
        $mode = $Matches[1]
        $size = [int]$Matches[2]
        $matches = @($Report.firstUsableViewport | Where-Object {
            [string]$_.mode -ceq $mode -and [int]$_.sourceUtf16Bytes -eq $size
        })
        if ($matches.Count -ne 1) {
            throw "Metric '$Metric' did not resolve to one first-viewport result."
        }
        return [double]$matches[0].regressionP95Milliseconds
    }

    switch ($Metric) {
        'scroll.uiThreadWorkP95Ms.hodgesLehmann' {
            return [double]$Report.scroll.regressionUiThreadWorkP95Milliseconds
        }
        'scroll.uiThreadWorkP99Ms.hodgesLehmann' {
            return [double]$Report.scroll.regressionUiThreadWorkP99Milliseconds
        }
        'scroll.frameTimeP95Ms.hodgesLehmann' {
            return [double]$Report.scroll.regressionFrameTimeP95Milliseconds
        }
        'scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann' {
            return [double]$Report.scroll.regressionRendererOwnedAllocatedBytesPerFrameP95
        }
        'sourceLookup.regressionP95Ns.hodgesLehmann' {
            return [double]$Report.sourceLookup.regressionP95Nanoseconds
        }
        'cancellation.p95Ms.hodgesLehmann' {
            return [double]$Report.cancellation.regressionP95Milliseconds
        }
        default {
            throw "Unknown frozen aggregate metric '$Metric'."
        }
    }
}

function Get-MetricDefinitions {
    return @(
        [pscustomobject]@{ Name = 'firstViewport.cache-disabled.102400.p95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 5.0 },
        [pscustomobject]@{ Name = 'firstViewport.cache-hit.102400.p95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 3.0 },
        [pscustomobject]@{ Name = 'firstViewport.cache-disabled.1048576.p95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 5.0 },
        [pscustomobject]@{ Name = 'firstViewport.cache-hit.1048576.p95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 5.0 },
        [pscustomobject]@{ Name = 'firstViewport.cache-disabled.10485760.p95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 3.0 },
        [pscustomobject]@{ Name = 'firstViewport.cache-hit.10485760.p95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 1.0 },
        [pscustomobject]@{ Name = 'scroll.uiThreadWorkP95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 0.05 },
        [pscustomobject]@{ Name = 'scroll.uiThreadWorkP99Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 0.10 },
        [pscustomobject]@{ Name = 'scroll.frameTimeP95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 2.0 },
        [pscustomobject]@{ Name = 'sourceLookup.regressionP95Ns.hodgesLehmann'; Kind = 'latency'; Unit = 'ns'; Floor = 30.0 },
        [pscustomobject]@{ Name = 'cancellation.p95Ms.hodgesLehmann'; Kind = 'latency'; Unit = 'ms'; Floor = 0.05 },
        [pscustomobject]@{ Name = 'scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann'; Kind = 'allocation'; Unit = 'bytes/frame'; Floor = 64.0 }
    )
}

function Get-AggregateMetricVerdicts {
    param([Parameter(Mandatory)][Collections.IDictionary] $ReportsByRole)

    $results = [Collections.Generic.List[object]]::new()
    foreach ($definition in Get-MetricDefinitions) {
        $r1 = Get-MetricValue -Report $ReportsByRole['R1'] -Metric $definition.Name
        $r2 = Get-MetricValue -Report $ReportsByRole['R2'] -Metric $definition.Name
        $c1 = Get-MetricValue -Report $ReportsByRole['C1'] -Metric $definition.Name
        $c2 = Get-MetricValue -Report $ReportsByRole['C2'] -Metric $definition.Name
        $allValues = @($r1, $r2, $c1, $c2)
        if (@($allValues | Where-Object { -not [double]::IsFinite($_) -or $_ -lt 0 }).Count -ne 0) {
            throw "Aggregate metric '$($definition.Name)' contains a non-finite or negative role value."
        }

        $referenceValues = [double[]]@($r1, $r2)
        $candidateValues = [double[]]@($c1, $c2)
        $referenceCenter = Get-HodgesLehmann -Values $referenceValues
        $candidateCenter = Get-HodgesLehmann -Values $candidateValues
        $abbaContrastEffect = (($c1 + $c2) - ($r1 + $r2)) / 2.0
        $pairedDeltas = [double[]]@(($c1 - $r1), ($c2 - $r2))
        $pairedDeltaCenter = Get-HodgesLehmann -Values $pairedDeltas
        $pairedDeltaSpread = [Math]::Abs($pairedDeltas[0] - $pairedDeltas[1])
        $linearDriftEstimate = ($pairedDeltas[0] - $pairedDeltas[1]) / 2.0
        $budgetPercent = if ($definition.Kind -eq 'allocation') { 2.0 } else { 5.0 }
        $relativeAllowance = [Math]::Abs($referenceCenter) * $budgetPercent / 100.0
        $allowedDelta = [Math]::Max($relativeAllowance, [double]$definition.Floor)
        $referenceSpan = [Math]::Abs($r2 - $r1)
        $candidateSpan = [Math]::Abs($c2 - $c1)
        $referenceSpread = Get-RoleSpreadPercent -Values $referenceValues -Center $referenceCenter
        $candidateSpread = Get-RoleSpreadPercent -Values $candidateValues -Center $candidateCenter
        # This is the sole cross-run relative gate. The ABBA contrast cancels an
        # additive linear time trend exactly. Four observations cannot identify
        # higher-order drift separately from treatment, so pair/role disagreement
        # is evidence for review rather than an allowance-derived hidden gate.
        $withinAllowance = Test-LessThanOrNearlyEqual `
            -Value $abbaContrastEffect `
            -Limit $allowedDelta
        $decision = if ($withinAllowance) {
            'pass'
        }
        else {
            'regression'
        }

        $results.Add([ordered]@{
            metric = $definition.Name
            kind = $definition.Kind
            unit = $definition.Unit
            referenceValues = [ordered]@{ R1 = $r1; R2 = $r2 }
            candidateValues = [ordered]@{ C1 = $c1; C2 = $c2 }
            referenceHodgesLehmannCenter = $referenceCenter
            candidateHodgesLehmannCenter = $candidateCenter
            abbaContrastEffect = $abbaContrastEffect
            roleCenterAbsoluteDelta = $candidateCenter - $referenceCenter
            roleCenterDeltaPercent = Get-DeltaPercent -Candidate $candidateCenter -Reference $referenceCenter
            pairedDeltas = @(
                [ordered]@{
                    referenceRole = 'R1'
                    candidateRole = 'C1'
                    absoluteDelta = $pairedDeltas[0]
                    deltaPercent = Get-DeltaPercent -Candidate $c1 -Reference $r1
                },
                [ordered]@{
                    referenceRole = 'R2'
                    candidateRole = 'C2'
                    absoluteDelta = $pairedDeltas[1]
                    deltaPercent = Get-DeltaPercent -Candidate $c2 -Reference $r2
                })
            pairedDeltaHodgesLehmannCenter = $pairedDeltaCenter
            pairedDeltaSpread = $pairedDeltaSpread
            linearDriftEstimatePerRun = $linearDriftEstimate
            referenceRoleSpan = $referenceSpan
            candidateRoleSpan = $candidateSpan
            referenceRoleSpreadPercent = $referenceSpread
            candidateRoleSpreadPercent = $candidateSpread
            diagnosticsAreNonGating = $true
            budgetPercent = $budgetPercent
            relativeAllowance = $relativeAllowance
            absoluteNoiseFloor = [double]$definition.Floor
            allowedAbsoluteDelta = $allowedDelta
            decision = $decision
            passed = $decision -eq 'pass'
        })
    }

    return @($results)
}

function Get-EvidenceSetSha256 {
    param(
        [Parameter(Mandatory)][Collections.IDictionary] $ReferenceArtifactSet,
        [Parameter(Mandatory)][Collections.IDictionary] $CandidateArtifactSet,
        [Parameter(Mandatory)][string] $ReferenceBuildIdentity,
        [Parameter(Mandatory)][string] $CandidateBuildIdentity,
        [Parameter(Mandatory)][string] $OrchestratorPath,
        [Parameter(Mandatory)][string] $OrchestratorSha256,
        [Parameter(Mandatory)][string] $GatePath,
        [Parameter(Mandatory)][string] $GateSha256,
        [Parameter(Mandatory)][object[]] $RunRecords
    )

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("reference-runtime-output-manifest:$ReferenceBuildIdentity")
    $lines.Add("candidate-runtime-output-manifest:$CandidateBuildIdentity")
    foreach ($name in @('executable', 'runtimeConfig', 'performanceHarnessDll', 'markdownRendererDll', 'markdownRendererCoreDll')) {
        $lines.Add("reference-${name}:$($ReferenceArtifactSet[$name].path)|$($ReferenceArtifactSet[$name].sha256)")
        $lines.Add("candidate-${name}:$($CandidateArtifactSet[$name].path)|$($CandidateArtifactSet[$name].sha256)")
    }
    $lines.Add("orchestrator:${OrchestratorPath}|$OrchestratorSha256")
    $lines.Add("strict-gate:${GatePath}|$GateSha256")
    foreach ($record in $RunRecords) {
        $lines.Add("$($record.role)-report:$($record.reportPath)|$($record.reportSha256)")
        $lines.Add("$($record.role)-harness-stdout:$($record.harness.stdoutPath)|$($record.harness.stdoutSha256)")
        $lines.Add("$($record.role)-harness-stderr:$($record.harness.stderrPath)|$($record.harness.stderrSha256)")
        $lines.Add("$($record.role)-gate-stdout:$($record.gate.stdoutPath)|$($record.gate.stdoutSha256)")
        $lines.Add("$($record.role)-gate-stderr:$($record.gate.stderrPath)|$($record.gate.stderrSha256)")
    }
    $payload = $script:Utf8NoBom.GetBytes(($lines -join "`n"))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload))
}

$referenceHarness = Resolve-RequiredFile `
    -Path $ReferenceHarnessPath -Label 'Reference harness' -RequiredExtension '.exe'
$candidateHarness = Resolve-RequiredFile `
    -Path $CandidateHarnessPath -Label 'Candidate harness' -RequiredExtension '.exe'
$gateScript = Resolve-RequiredFile `
    -Path ([IO.Path]::GetFullPath($GateScriptPath)) -Label 'Strict performance gate' -RequiredExtension '.ps1'
$orchestratorScript = Resolve-RequiredFile `
    -Path $PSCommandPath -Label 'Counterbalanced orchestrator' -RequiredExtension '.ps1'

if (Test-PathEqual -Left $referenceHarness -Right $candidateHarness) {
    throw 'Reference and candidate harnesses must be different files.'
}

$fullOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$referenceDeploymentRoot = [IO.Path]::GetDirectoryName($referenceHarness)
$candidateDeploymentRoot = [IO.Path]::GetDirectoryName($candidateHarness)
if ((Test-PathWithinDirectory `
        -Path $fullOutputRoot `
        -DirectoryPath $referenceDeploymentRoot) -or
    (Test-PathWithinDirectory `
        -Path $fullOutputRoot `
        -DirectoryPath $candidateDeploymentRoot)) {
    throw 'Output root must be outside both reference and candidate runtime-output directories.'
}

$referenceArtifactSet = Get-ExpectedBuildArtifactSet `
    -ExecutablePath $referenceHarness -Label 'Reference build'
$candidateArtifactSet = Get-ExpectedBuildArtifactSet `
    -ExecutablePath $candidateHarness -Label 'Candidate build'
$referenceRuntimeOutputManifest = Get-RuntimeOutputManifest `
    -ExecutablePath $referenceHarness -Label 'Reference build'
$candidateRuntimeOutputManifest = Get-RuntimeOutputManifest `
    -ExecutablePath $candidateHarness -Label 'Candidate build'
if ([string]$referenceRuntimeOutputManifest.identity -ceq
    [string]$candidateRuntimeOutputManifest.identity) {
    throw 'Reference and candidate harnesses must be distinct builds with different runtime-output manifest identities.'
}
$referenceExecutableSha256 = [string]$referenceArtifactSet.executable.sha256
$candidateExecutableSha256 = [string]$candidateArtifactSet.executable.sha256
$gateSha256 = Get-Sha256 -Path $gateScript
$orchestratorSha256 = Get-Sha256 -Path $orchestratorScript

$powerShellPath = Resolve-RequiredFile `
    -Path (Join-Path $PSHOME 'pwsh.exe') -Label 'PowerShell host' -RequiredExtension '.exe'
if (Test-Path -LiteralPath $fullOutputRoot -PathType Leaf) {
    throw "Output root '$fullOutputRoot' is a file."
}
[IO.Directory]::CreateDirectory($fullOutputRoot) | Out-Null
$runId = 'run-{0}-{1}' -f `
    [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssfff'),
    ([Guid]::NewGuid().ToString('N').Substring(0, 12))
$runDirectory = Join-Path $fullOutputRoot $runId
if (Test-Path -LiteralPath $runDirectory) {
    throw "Unique counterbalanced run directory unexpectedly exists: '$runDirectory'."
}
New-Item -Path $runDirectory -ItemType Directory -ErrorAction Stop | Out-Null

$r1Path = Join-Path $runDirectory 'R1.reference.json'
$plan = @(
    [pscustomobject]@{
        Ordinal = 1; Role = 'R1'; BuildRole = 'reference'; GateMode = 'Baseline'
        ExecutablePath = $referenceHarness; ExecutableSha256 = $referenceExecutableSha256
        ArtifactSet = $referenceArtifactSet
        ExpectedBuildIdentity = [string]$referenceRuntimeOutputManifest.identity
        ReportPath = $r1Path; Arguments = @('--output', $r1Path, '--baseline')
    },
    [pscustomobject]@{
        Ordinal = 2; Role = 'C1'; BuildRole = 'candidate'; GateMode = 'Baseline'
        ExecutablePath = $candidateHarness; ExecutableSha256 = $candidateExecutableSha256
        ArtifactSet = $candidateArtifactSet
        ExpectedBuildIdentity = [string]$candidateRuntimeOutputManifest.identity
        ReportPath = (Join-Path $runDirectory 'C1.candidate.json')
        Arguments = @('--output', (Join-Path $runDirectory 'C1.candidate.json'), '--baseline')
    },
    [pscustomobject]@{
        Ordinal = 3; Role = 'C2'; BuildRole = 'candidate'; GateMode = 'Baseline'
        ExecutablePath = $candidateHarness; ExecutableSha256 = $candidateExecutableSha256
        ArtifactSet = $candidateArtifactSet
        ExpectedBuildIdentity = [string]$candidateRuntimeOutputManifest.identity
        ReportPath = (Join-Path $runDirectory 'C2.candidate.json')
        Arguments = @('--output', (Join-Path $runDirectory 'C2.candidate.json'), '--baseline')
    },
    [pscustomobject]@{
        Ordinal = 4; Role = 'R2'; BuildRole = 'reference'; GateMode = 'Baseline'
        ExecutablePath = $referenceHarness; ExecutableSha256 = $referenceExecutableSha256
        ArtifactSet = $referenceArtifactSet
        ExpectedBuildIdentity = [string]$referenceRuntimeOutputManifest.identity
        ReportPath = (Join-Path $runDirectory 'R2.reference.json')
        Arguments = @('--output', (Join-Path $runDirectory 'R2.reference.json'), '--baseline')
    })

$sequenceStartedUtc = [DateTimeOffset]::UtcNow
$runRecords = [Collections.Generic.List[object]]::new()
$reportsByRole = [ordered]@{}
$verdictPath = Join-Path $runDirectory 'counterbalanced-verdict.json'
$failure = $null

try {
    foreach ($run in $plan) {
        Assert-RuntimeOutputManifestUnchanged `
            -ExpectedManifest $referenceRuntimeOutputManifest `
            -ExecutablePath $referenceHarness `
            -Label 'Reference build'
        Assert-RuntimeOutputManifestUnchanged `
            -ExpectedManifest $candidateRuntimeOutputManifest `
            -ExecutablePath $candidateHarness `
            -Label 'Candidate build'
        Assert-BuildArtifactSetUnchanged -ArtifactSet $referenceArtifactSet -Label 'Reference build'
        Assert-BuildArtifactSetUnchanged -ArtifactSet $candidateArtifactSet -Label 'Candidate build'
        Assert-FileHash -Path $orchestratorScript -ExpectedSha256 $orchestratorSha256 -Label 'Counterbalanced orchestrator'
        Assert-FileHash -Path $gateScript -ExpectedSha256 $gateSha256 -Label 'Strict performance gate'
        if (Test-Path -LiteralPath $run.ReportPath) {
            throw "Role $($run.Role) report path already exists; evidence is create-new only."
        }

        $logStem = Join-Path $runDirectory $run.Role
        Write-Host "[$($run.Ordinal)/4] Running $($run.Role) ($($run.BuildRole)) exactly once..."
        $processResult = Invoke-ProcessOnce `
            -ExecutablePath $run.ExecutablePath `
            -Arguments $run.Arguments `
            -WorkingDirectory ([IO.Path]::GetDirectoryName($run.ExecutablePath)) `
            -StdOutPath "$logStem.harness.stdout.log" `
            -StdErrPath "$logStem.harness.stderr.log" `
            -TimeoutMinutes $RunTimeoutMinutes
        if ($processResult.TimedOut) {
            throw "Role $($run.Role) exceeded the $RunTimeoutMinutes minute timeout."
        }
        if ([int]$processResult.ExitCode -ne 0) {
            throw "Role $($run.Role) harness exited with code $($processResult.ExitCode)."
        }
        if (-not (Test-Path -LiteralPath $run.ReportPath -PathType Leaf)) {
            throw "Role $($run.Role) exited successfully without creating its report."
        }

        Write-Host "[$($run.Ordinal)/4] Strictly validating $($run.Role)..."
        $gateResult = Invoke-StrictReportGate `
            -PowerShellPath $powerShellPath `
            -GatePath $gateScript `
            -ReportPath $run.ReportPath `
            -Mode $run.GateMode `
            -LogStem "$logStem.gate"
        if ($gateResult.TimedOut) {
            throw "Strict gate timed out for role $($run.Role)."
        }
        if ([int]$gateResult.ExitCode -ne 0) {
            throw "Strict gate failed for role $($run.Role) with exit code $($gateResult.ExitCode)."
        }

        $reportEvidence = Read-JsonEvidence -Path $run.ReportPath
        Assert-ReportRoleBinding `
            -Report $reportEvidence.Value `
            -Mode $run.GateMode `
            -ExpectedArtifactSet $run.ArtifactSet `
            -ExpectedBuildIdentity $run.ExpectedBuildIdentity

        $reportsByRole[$run.Role] = $reportEvidence.Value
        $runRecords.Add([ordered]@{
            ordinal = $run.Ordinal
            role = $run.Role
            buildRole = $run.BuildRole
            expectedRegressionMode = $run.GateMode.ToLowerInvariant()
            executablePath = $run.ExecutablePath
            executableSha256 = $run.ExecutableSha256
            buildIdentity = [string]$reportEvidence.Value.buildIdentity
            buildArtifactHashes = Get-ArtifactHashSet -Report $reportEvidence.Value
            reportPath = $run.ReportPath
            reportSha256 = $reportEvidence.Sha256
            harness = [ordered]@{
                startedUtc = $processResult.StartedUtc
                completedUtc = $processResult.CompletedUtc
                elapsedMilliseconds = $processResult.ElapsedMilliseconds
                exitCode = $processResult.ExitCode
                timedOut = $processResult.TimedOut
                stdoutPath = "$logStem.harness.stdout.log"
                stdoutSha256 = $processResult.StdOutSha256
                stderrPath = "$logStem.harness.stderr.log"
                stderrSha256 = $processResult.StdErrSha256
            }
            gate = [ordered]@{
                exitCode = $gateResult.ExitCode
                timedOut = $gateResult.TimedOut
                stdoutPath = "$logStem.gate.stdout.log"
                stdoutSha256 = $gateResult.StdOutSha256
                stderrPath = "$logStem.gate.stderr.log"
                stderrSha256 = $gateResult.StdErrSha256
                passed = $true
            }
        })
    }

    Assert-RuntimeOutputManifestUnchanged `
        -ExpectedManifest $referenceRuntimeOutputManifest `
        -ExecutablePath $referenceHarness `
        -Label 'Reference build'
    Assert-RuntimeOutputManifestUnchanged `
        -ExpectedManifest $candidateRuntimeOutputManifest `
        -ExecutablePath $candidateHarness `
        -Label 'Candidate build'
    Assert-BuildArtifactSetUnchanged -ArtifactSet $referenceArtifactSet -Label 'Reference build'
    Assert-BuildArtifactSetUnchanged -ArtifactSet $candidateArtifactSet -Label 'Candidate build'
    Assert-FileHash -Path $orchestratorScript -ExpectedSha256 $orchestratorSha256 -Label 'Counterbalanced orchestrator'
    Assert-FileHash -Path $gateScript -ExpectedSha256 $gateSha256 -Label 'Strict performance gate'
    foreach ($record in $runRecords) {
        Assert-FileHash -Path $record.reportPath -ExpectedSha256 $record.reportSha256 -Label "Role $($record.role) report"
    }

    if ($runRecords.Count -ne 4 -or
        ($runRecords.role -join ',') -cne 'R1,C1,C2,R2') {
        throw 'Counterbalanced sequence did not complete in the frozen R1,C1,C2,R2 order.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$runRecords[0].buildIdentity) -or
        [string]$runRecords[0].buildIdentity -cne [string]$runRecords[3].buildIdentity -or
        [string]::IsNullOrWhiteSpace([string]$runRecords[1].buildIdentity) -or
        [string]$runRecords[1].buildIdentity -cne [string]$runRecords[2].buildIdentity) {
        throw 'Repeated reports for one build role do not have a stable build identity.'
    }
    if (-not (Test-ArtifactHashSetEqual `
        -Left $runRecords[0].buildArtifactHashes -Right $runRecords[3].buildArtifactHashes) -or
        -not (Test-ArtifactHashSetEqual `
            -Left $runRecords[1].buildArtifactHashes -Right $runRecords[2].buildArtifactHashes)) {
        throw 'Build artifact hashes changed between repeated measurements of one role.'
    }
    foreach ($role in @('C1', 'C2', 'R2')) {
        if (-not (Test-StableMachineEnvironment `
            -Left $reportsByRole['R1'].machine -Right $reportsByRole[$role].machine)) {
            throw "Machine environment changed between R1 and $role."
        }
        if (-not (Test-StableSourceLookupEnvironment `
            -Left $reportsByRole['R1'].sourceLookup -Right $reportsByRole[$role].sourceLookup)) {
            throw "Source-lookup execution environment changed between R1 and $role."
        }
    }

    $metrics = @(Get-AggregateMetricVerdicts -ReportsByRole $reportsByRole)
    $metricFailures = @($metrics | Where-Object { -not [bool]$_.passed })
    $aggregatePassed = $metricFailures.Count -eq 0
    $aggregateFailures = @($metricFailures | ForEach-Object {
        "Metric '$($_.metric)' produced '$($_.decision)' in the counterbalanced aggregate."
    })
    $evidenceSetSha256 = Get-EvidenceSetSha256 `
        -ReferenceArtifactSet $referenceArtifactSet `
        -CandidateArtifactSet $candidateArtifactSet `
        -ReferenceBuildIdentity ([string]$referenceRuntimeOutputManifest.identity) `
        -CandidateBuildIdentity ([string]$candidateRuntimeOutputManifest.identity) `
        -OrchestratorPath $orchestratorScript `
        -OrchestratorSha256 $orchestratorSha256 `
        -GatePath $gateScript `
        -GateSha256 $gateSha256 `
        -RunRecords @($runRecords)

    $verdict = [ordered]@{
        schemaVersion = $script:AggregateSchemaVersion
        providerName = $script:AggregateProviderName
        policy = $script:AggregatePolicy
        runId = $runId
        sequence = @('R1', 'C1', 'C2', 'R2')
        startedUtc = $sequenceStartedUtc
        completedUtc = [DateTimeOffset]::UtcNow
        complete = $true
        passed = $aggregatePassed
        evidenceSetSha256 = $evidenceSetSha256
        orchestrator = [ordered]@{ path = $orchestratorScript; sha256 = $orchestratorSha256 }
        strictGate = [ordered]@{ path = $gateScript; sha256 = $gateSha256 }
        builds = [ordered]@{
            reference = [ordered]@{ artifacts = $referenceArtifactSet }
            candidate = [ordered]@{ artifacts = $candidateArtifactSet }
        }
        reports = @($runRecords)
        metrics = $metrics
        failures = $aggregateFailures
    }
    Write-NewUtf8File `
        -Path $verdictPath `
        -Content (($verdict | ConvertTo-Json -Depth 20) + "`n")

    if (-not $aggregatePassed) {
        throw "Counterbalanced performance verdict failed. See '$verdictPath'."
    }
}
catch {
    $failure = $_
    throw
}
finally {
    if ($null -ne $failure -and -not (Test-Path -LiteralPath $verdictPath)) {
        $failureVerdict = [ordered]@{
            schemaVersion = $script:AggregateSchemaVersion
            providerName = $script:AggregateProviderName
            policy = $script:AggregatePolicy
            runId = $runId
            sequence = @('R1', 'C1', 'C2', 'R2')
            startedUtc = $sequenceStartedUtc
            completedUtc = [DateTimeOffset]::UtcNow
            complete = $false
            passed = $false
            orchestrator = [ordered]@{ path = $orchestratorScript; sha256 = $orchestratorSha256 }
            strictGate = [ordered]@{ path = $gateScript; sha256 = $gateSha256 }
            builds = [ordered]@{
                reference = [ordered]@{ artifacts = $referenceArtifactSet }
                candidate = [ordered]@{ artifacts = $candidateArtifactSet }
            }
            reports = @($runRecords)
            metrics = @()
            failures = @($failure.Exception.Message)
        }
        try {
            Write-NewUtf8File `
                -Path $verdictPath `
                -Content (($failureVerdict | ConvertTo-Json -Depth 20) + "`n")
        }
        catch {
            Write-Warning "Could not write partial counterbalanced verdict: $($_.Exception.Message)"
        }
    }
}

Write-Host "Counterbalanced WinUI performance evidence passed: $verdictPath"
Write-Output $verdictPath
