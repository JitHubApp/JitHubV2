[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath,

    [ValidateSet('Any', 'Baseline', 'Candidate')]
    [string] $RequiredRegressionMode = 'Any'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:ScrollFrameTimeP95BudgetMilliseconds = 1000.0 / 120.0

if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
    throw "WinUI performance report '$ReportPath' does not exist."
}

[byte[]]$reportBytes = [IO.File]::ReadAllBytes($ReportPath)
$reportJson = [Text.UTF8Encoding]::new($false, $true).GetString($reportBytes)
$report = $reportJson | ConvertFrom-Json -Depth 100
if ($null -eq $report) {
    throw "WinUI performance report '$ReportPath' was empty."
}

function Get-RequiredValue {
    param(
        [Parameter(Mandatory)][object] $Root,
        [Parameter(Mandatory)][string] $Path
    )

    $value = $Root
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $value) {
            throw "Performance report is missing required value '$Path'."
        }

        $property = $value.PSObject.Properties[$segment]
        if ($null -eq $property) {
            throw "Performance report is missing required property '$Path'."
        }
        $value = $property.Value
    }

    return $value
}

function Test-FiniteNumber {
    param([object] $Value)

    try {
        $number = [double]$Value
        return [double]::IsFinite($number)
    }
    catch {
        return $false
    }
}

function Get-NearestRankPercentile {
    param(
        [Parameter(Mandatory)][object[]] $Values,
        [Parameter(Mandatory)][double] $Percentile
    )

    if ($Values.Count -eq 0) { return [double]::NaN }
    $sorted = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    $rank = [Math]::Clamp([int][Math]::Ceiling($Percentile * $sorted.Count) - 1, 0, $sorted.Count - 1)
    return [double]$sorted[$rank]
}

function Get-TwoValueCenter {
    param(
        [Parameter(Mandatory)][double] $First,
        [Parameter(Mandatory)][double] $Second
    )

    if (-not [double]::IsFinite($First) -or -not [double]::IsFinite($Second)) {
        return [double]::NaN
    }

    $sameSign = ($First -lt 0) -eq ($Second -lt 0)
    if ($sameSign) { return $First + (($Second - $First) / 2.0) }
    return ($First + $Second) / 2.0
}

function Get-TrueMedian {
    param([Parameter(Mandatory)][object[]] $Values)

    if ($Values.Count -eq 0) { return [double]::NaN }
    $sorted = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    if (@($sorted | Where-Object { -not [double]::IsFinite($_) }).Count -ne 0) {
        return [double]::NaN
    }

    $middle = [int][Math]::Floor($sorted.Count / 2.0)
    if (($sorted.Count % 2) -ne 0) { return [double]$sorted[$middle] }
    return Get-TwoValueCenter -First $sorted[$middle - 1] -Second $sorted[$middle]
}

function Get-HodgesLehmann {
    param([Parameter(Mandatory)][object[]] $Values)

    if ($Values.Count -eq 0) { return [double]::NaN }
    $samples = @($Values | ForEach-Object { [double]$_ })
    if (@($samples | Where-Object { -not [double]::IsFinite($_) }).Count -ne 0) {
        return [double]::NaN
    }

    $walshAverages = [Collections.Generic.List[double]]::new(
        [int]($samples.Count * ($samples.Count + 1) / 2))
    for ($left = 0; $left -lt $samples.Count; $left++) {
        for ($right = $left; $right -lt $samples.Count; $right++) {
            $walshAverages.Add((Get-TwoValueCenter -First $samples[$left] -Second $samples[$right]))
        }
    }
    return Get-TrueMedian -Values @($walshAverages)
}

function Get-RobustDispersionPercent {
    param([Parameter(Mandatory)][object[]] $Values)

    if ($Values.Count -eq 0) { return [double]::NaN }
    $samples = @($Values | ForEach-Object { [double]$_ })
    $location = Get-HodgesLehmann -Values $samples
    if (-not [double]::IsFinite($location)) { return [double]::NaN }
    $median = Get-TrueMedian -Values $samples
    $deviations = @($samples | ForEach-Object { [Math]::Abs($_ - $median) })
    $mad = Get-TrueMedian -Values $deviations
    if (-not [double]::IsFinite($mad)) { return [double]::NaN }
    if ([Math]::Abs($location) -le 1e-12) {
        if ($mad -le 1e-12) { return 0.0 }
        return [double]::MaxValue
    }
    return 1.4826 * $mad / [Math]::Abs($location) * 100.0
}

function Get-TheilSenStatistics {
    param([Parameter(Mandatory)][object[]] $Observations)

    if ($Observations.Count -lt 2) { return $null }
    $previousOrdinal = -1
    $ordinals = [Collections.Generic.List[int]]::new($Observations.Count)
    $values = [Collections.Generic.List[double]]::new($Observations.Count)
    foreach ($observation in $Observations) {
        if ($null -eq $observation -or
            $null -eq $observation.PSObject.Properties['GlobalOrdinal'] -or
            $null -eq $observation.PSObject.Properties['Value']) {
            return $null
        }
        $ordinal = [int]$observation.GlobalOrdinal
        $value = [double]$observation.Value
        if ($ordinal -lt 0 -or $ordinal -le $previousOrdinal -or
            -not [double]::IsFinite($value)) {
            return $null
        }
        $ordinals.Add($ordinal)
        $values.Add($value)
        $previousOrdinal = $ordinal
    }

    $ordinalSpan = [int64]$ordinals[$ordinals.Count - 1] - $ordinals[0]
    if ($ordinalSpan -le 0) { return $null }
    $slopes = [Collections.Generic.List[double]]::new(
        [int]($Observations.Count * ($Observations.Count - 1) / 2))
    for ($left = 0; $left -lt $values.Count; $left++) {
        for ($right = $left + 1; $right -lt $values.Count; $right++) {
            $ordinalDelta = [int64]$ordinals[$right] - $ordinals[$left]
            $slope = ($values[$right] - $values[$left]) / $ordinalDelta
            if (-not [double]::IsFinite($slope)) { return $null }
            $slopes.Add($slope)
        }
    }

    $theilSenSlope = Get-TrueMedian -Values @($slopes)
    $projectedDrift = $theilSenSlope * $ordinalSpan
    if (-not [double]::IsFinite($theilSenSlope) -or
        -not [double]::IsFinite($projectedDrift)) {
        return $null
    }
    return [pscustomobject]@{
        Slope = $theilSenSlope
        ProjectedDrift = $projectedDrift
    }
}

function Test-NearlyEqual {
    param([double] $Left, [double] $Right)

    if (-not [double]::IsFinite($Left) -or -not [double]::IsFinite($Right)) { return $false }
    $scale = [Math]::Max(1.0, [Math]::Max([Math]::Abs($Left), [Math]::Abs($Right)))
    return [Math]::Abs($Left - $Right) -le $scale * 1e-12
}

function Get-GrowthPercent {
    param([int64] $Final, [int64] $Baseline)

    if ($Baseline -le 0) {
        if ($Final -le $Baseline) { return 0.0 }
        return [double]::MaxValue
    }
    return [Math]::Max(0.0, (([double]$Final - [double]$Baseline) * 100.0 / [double]$Baseline))
}

function Test-Sha256 {
    param([object] $Value)
    return [string]$Value -match '^[0-9A-Fa-f]{64}$'
}

function Test-DeploymentIdentity {
    param([object] $Value)

    return [string]$Value -cmatch '^runtime-output-manifest-v1:[0-9A-F]{64}$'
}

function Test-PathWithinDirectory {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $DirectoryPath
    )

    $relativePath = [IO.Path]::GetRelativePath($DirectoryPath, $Path)
    return $relativePath.Length -eq 0 -or
        (-not [IO.Path]::IsPathRooted($relativePath) -and
         -not [string]::Equals($relativePath, '..', [StringComparison]::Ordinal) -and
         -not $relativePath.StartsWith(
             "..$([IO.Path]::DirectorySeparatorChar)",
             [StringComparison]::Ordinal) -and
         -not $relativePath.StartsWith(
             "..$([IO.Path]::AltDirectorySeparatorChar)",
             [StringComparison]::Ordinal))
}

function Resolve-ExistingReparsePoints {
    param([Parameter(Mandatory)][string] $Path)

    $pendingPath = [IO.Path]::GetFullPath($Path)
    $visitedPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $separators = [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    for ($redirectCount = 0; $redirectCount -lt 64; $redirectCount++) {
        if (-not $visitedPaths.Add($pendingPath)) {
            throw "A reparse-point cycle was detected while resolving '$Path'."
        }

        $root = [IO.Path]::GetPathRoot($pendingPath)
        if ([string]::IsNullOrEmpty($root)) {
            throw "The path must have a root: '$Path'."
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
                    throw "The reparse-point target could not be resolved for '$($entry.FullName)'."
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

    throw "Too many reparse-point redirects were encountered while resolving '$Path'."
}

function Test-PathWithinDeploymentRoot {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ExecutablePath
    )

    $deploymentRoot = [IO.Path]::GetDirectoryName(
        [IO.Path]::GetFullPath($ExecutablePath))
    if ([string]::IsNullOrWhiteSpace($deploymentRoot)) {
        throw 'The deployment executable must have a parent directory.'
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullDeploymentRoot = [IO.Path]::GetFullPath($deploymentRoot)
    if (Test-PathWithinDirectory `
            -Path $fullPath `
            -DirectoryPath $fullDeploymentRoot) {
        return $true
    }

    $physicalPath = Resolve-ExistingReparsePoints -Path $fullPath
    $physicalDeploymentRoot = Resolve-ExistingReparsePoints -Path $fullDeploymentRoot
    return Test-PathWithinDirectory `
        -Path $physicalPath `
        -DirectoryPath $physicalDeploymentRoot
}

function Get-DeploymentSnapshotDigest {
    param(
        [Parameter(Mandatory)][string] $DeploymentRoot,
        [Parameter(Mandatory)][string] $Policy
    )

    $paths = [Collections.Generic.List[string]]::new()
    $absolutePaths = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($DeploymentRoot)
    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Pop()
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($path)
            $fullPath = [IO.Path]::GetFullPath($path)
            $relativePath = [IO.Path]::GetRelativePath(
                $DeploymentRoot,
                $fullPath).Replace('\', '/')
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The runtime output contains unsupported reparse point '$relativePath'."
            }

            $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
            if ($isDirectory) {
                $pendingDirectories.Push($fullPath)
                continue
            }

            $extension = [IO.Path]::GetExtension($fullPath)
            if ([string]::Equals(
                    $extension,
                    '.pdb',
                    [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals(
                    $extension,
                    '.xml',
                    [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            if (-not $absolutePaths.TryAdd($relativePath, $fullPath)) {
                throw "The runtime-output manifest contains duplicate relative path '$relativePath'."
            }
            $paths.Add($relativePath)
        }
    }
    $paths.Sort([StringComparer]::Ordinal)

    $lines = [Collections.Generic.List[string]]::new($paths.Count + 1)
    $lines.Add($policy)
    foreach ($relativePath in $paths) {
        $stream = [IO.FileStream]::new(
            $absolutePaths[$relativePath],
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read,
            128 * 1024,
            [IO.FileOptions]::SequentialScan)
        try {
            $length = $stream.Length
            $sha256 = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData([IO.Stream]$stream))
            if ($stream.Length -ne $length) {
                throw "Runtime file '$relativePath' changed while it was being hashed."
            }
        }
        finally {
            $stream.Dispose()
        }
        $lines.Add(
            $relativePath + '|' +
            $length.ToString([Globalization.CultureInfo]::InvariantCulture) + '|' +
            $sha256)
    }

    $payload = [Text.UTF8Encoding]::new($false, $true).GetBytes($lines -join "`n")
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($payload))
}

function Get-DeploymentIdentity {
    param([Parameter(Mandatory)][string] $ExecutablePath)

    $policy = 'runtime-output-manifest-v1'
    $fullExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
    if (-not [IO.File]::Exists($fullExecutablePath)) {
        throw "The deployment executable '$fullExecutablePath' does not exist."
    }

    $deploymentRoot = [IO.Path]::GetDirectoryName($fullExecutablePath)
    if ([string]::IsNullOrWhiteSpace($deploymentRoot)) {
        throw 'The deployment executable must have a parent directory.'
    }

    $firstSnapshot = Get-DeploymentSnapshotDigest `
        -DeploymentRoot $deploymentRoot `
        -Policy $policy
    $secondSnapshot = Get-DeploymentSnapshotDigest `
        -DeploymentRoot $deploymentRoot `
        -Policy $policy
    if ($firstSnapshot -cne $secondSnapshot) {
        throw 'The runtime output changed while its deployment identity was being captured.'
    }

    return $policy + ':' + $secondSnapshot
}

function Test-CanonicalGuid {
    param([object] $Value)

    $parsed = [Guid]::Empty
    return [Guid]::TryParseExact([string]$Value, 'D', [ref]$parsed)
}

function Test-PowerMode {
    param([object] $Value)

    $text = [string]$Value
    return $text -ceq 'unsupported' -or
        [string]::Equals($text, '00000000-0000-0000-0000-000000000000', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($text, '961cc777-2547-4f9d-8174-7d86181b8a7a', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($text, 'ded574b5-45a0-4f42-8737-46345c09c238', [StringComparison]::OrdinalIgnoreCase)
}

function Test-PowerModePair {
    param(
        [object] $AcValue,
        [object] $DcValue
    )

    $acUnsupported = [string]$AcValue -ceq 'unsupported'
    $dcUnsupported = [string]$DcValue -ceq 'unsupported'
    return (Test-PowerMode $AcValue) -and
        (Test-PowerMode $DcValue) -and
        ($acUnsupported -eq $dcUnsupported)
}

function Test-EffectivePowerMode {
    param([object] $Value)

    return [string]$Value -cin @(
        'BatterySaver',
        'BetterBattery',
        'Balanced',
        'HighPerformance',
        'MaxPerformance',
        'GameMode',
        'MixedReality',
        'unsupported')
}

function Test-ReportTimestamps {
    param(
        [object] $StartedUtcText,
        [object] $CompletedUtcText
    )

    try {
        $started = [DateTimeOffset]::Parse(
            [string]$StartedUtcText,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
        $completed = [DateTimeOffset]::Parse(
            [string]$CompletedUtcText,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind)
        return $started -ne [DateTimeOffset]::MinValue -and
            $started.Offset -eq [TimeSpan]::Zero -and
            $completed.Offset -eq [TimeSpan]::Zero -and
            $completed -gt $started
    }
    catch {
        return $false
    }
}

function Get-ReportTimestampEvidence {
    param([Parameter(Mandatory)][byte[]] $Bytes)

    $json = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    $document = [Text.Json.JsonDocument]::Parse($json)
    try {
        $root = $document.RootElement
        return [pscustomobject]@{
            StartedUtc = [string]$root.GetProperty('startedUtc').GetString()
            CompletedUtc = [string]$root.GetProperty('completedUtc').GetString()
        }
    }
    finally {
        $document.Dispose()
    }
}

function Get-SingleBitAffinityIndex {
    param([object] $Value)

    $text = [string]$Value
    if (-not [Text.RegularExpressions.Regex]::IsMatch(
        $text,
        '^0x[0-9A-F]{16}$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
        return -1
    }

    try {
        $mask = [Convert]::ToUInt64($text.Substring(2), 16)
        if ([Numerics.BitOperations]::PopCount($mask) -ne 1) { return -1 }
        $index = [Numerics.BitOperations]::TrailingZeroCount($mask)
        if ($index -ge [IntPtr]::Size * 8) { return -1 }
        return $index
    }
    catch {
        return -1
    }
}

$script:ActiveProcessorTopology = $null
function Get-ActiveProcessorTopology {
    if ($null -ne $script:ActiveProcessorTopology) {
        return $script:ActiveProcessorTopology
    }

    if ($null -eq ('MarkdownRenderer.PerformanceGate.NativeProcessorTopology' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;

namespace MarkdownRenderer.PerformanceGate
{
    public static class NativeProcessorTopology
    {
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern ushort GetActiveProcessorGroupCount();

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern uint GetActiveProcessorCount(ushort groupNumber);
    }
}
'@
    }

    [int]$groupCount =
        [MarkdownRenderer.PerformanceGate.NativeProcessorTopology]::GetActiveProcessorGroupCount()
    if ($groupCount -le 0) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Windows returned no active processor groups (Win32 error $errorCode)."
    }

    [int[]]$activeCounts = [int[]]::new($groupCount)
    for ($group = 0; $group -lt $groupCount; $group++) {
        [uint32]$count =
            [MarkdownRenderer.PerformanceGate.NativeProcessorTopology]::GetActiveProcessorCount(
                [uint16]$group)
        if ($count -eq 0) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "Windows returned no active processors for group $group (Win32 error $errorCode)."
        }
        if ($count -gt ([IntPtr]::Size * 8)) {
            throw "Windows returned invalid active-processor count '$count' for group '$group'."
        }
        $activeCounts[$group] = [int]$count
    }

    $script:ActiveProcessorTopology = [pscustomobject]@{
        GroupCount = $groupCount
        ActiveCounts = $activeCounts
    }
    return $script:ActiveProcessorTopology
}

function Test-PinnedProcessorEvidence {
    param(
        [Parameter(Mandatory)][object] $AffinityMask,
        [Parameter(Mandatory)][int] $ProcessorGroup,
        [Parameter(Mandatory)][int] $ProcessorNumber,
        [Parameter(Mandatory)][object] $Priority,
        [Parameter(Mandatory)][string] $ExpectedPriority
    )

    $affinityIndex = Get-SingleBitAffinityIndex $AffinityMask
    $topology = Get-ActiveProcessorTopology
    return $affinityIndex -ge 0 -and
        $ProcessorGroup -ge 0 -and
        $ProcessorGroup -lt [int]$topology.GroupCount -and
        $affinityIndex -lt [int]$topology.ActiveCounts[$ProcessorGroup] -and
        $ProcessorNumber -eq $affinityIndex -and
        [string]$Priority -ceq $ExpectedPriority
}

function Read-PerformanceReportEvidence {
    param([Parameter(Mandatory)][string] $Path)

    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $json = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    $parsed = $json | ConvertFrom-Json -Depth 100
    if ($null -eq $parsed) {
        throw "Performance report '$Path' was empty."
    }

    $timestamps = Get-ReportTimestampEvidence -Bytes $bytes
    return [pscustomobject]@{
        Report = $parsed
        Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        Bytes = $bytes
        StartedUtc = $timestamps.StartedUtc
        CompletedUtc = $timestamps.CompletedUtc
    }
}

function Test-SameMachine {
    param(
        [Parameter(Mandatory)][object] $Candidate,
        [Parameter(Mandatory)][object] $Reference
    )

    return (
        [string]::Equals(
            [string]$Candidate.machineInstanceSha256,
            [string]$Reference.machineInstanceSha256,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Candidate.cpu, [string]$Reference.cpu, [StringComparison]::Ordinal) -and
        [int]$Candidate.logicalProcessorCount -eq [int]$Reference.logicalProcessorCount -and
        [string]::Equals(
            [string]$Candidate.osDescription,
            [string]$Reference.osDescription,
            [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Candidate.osVersion, [string]$Reference.osVersion, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Candidate.osArchitecture, [string]$Reference.osArchitecture, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Candidate.processArchitecture, [string]$Reference.processArchitecture, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Candidate.frameworkDescription, [string]$Reference.frameworkDescription, [StringComparison]::Ordinal) -and
        [Math]::Abs([double]$Candidate.dpiScale - [double]$Reference.dpiScale) -lt 0.01 -and
        [Math]::Abs([double]$Candidate.viewportWidthDips - [double]$Reference.viewportWidthDips) -lt 1.0 -and
        [Math]::Abs([double]$Candidate.viewportHeightDips - [double]$Reference.viewportHeightDips) -lt 1.0 -and
        [string]::Equals(
            [string]$Candidate.displayDeviceName,
            [string]$Reference.displayDeviceName,
            [StringComparison]::Ordinal) -and
        [Math]::Abs([double]$Candidate.configuredRefreshRateHz - [double]$Reference.configuredRefreshRateHz) -lt 0.5 -and
        [string]::Equals(
            [string]$Candidate.gpuAdapterIdentitySha256,
            [string]$Reference.gpuAdapterIdentitySha256,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            [string]$Candidate.gpuDriverVersion,
            [string]$Reference.gpuDriverVersion,
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$Candidate.displayAdapterIdentitySha256,
            [string]$Reference.displayAdapterIdentitySha256,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            [string]$Candidate.displayAdapterDriverVersion,
            [string]$Reference.displayAdapterDriverVersion,
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$Candidate.activePowerSchemeGuid,
            [string]$Reference.activePowerSchemeGuid,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            [string]$Candidate.userConfiguredAcPowerModeGuid,
            [string]$Reference.userConfiguredAcPowerModeGuid,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            [string]$Candidate.userConfiguredDcPowerModeGuid,
            [string]$Reference.userConfiguredDcPowerModeGuid,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            [string]$Candidate.powerSource,
            [string]$Reference.powerSource,
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$Candidate.energySaverState,
            [string]$Reference.energySaverState,
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$Candidate.effectivePowerMode,
            [string]$Reference.effectivePowerMode,
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$Candidate.processPriorityClass,
            [string]$Reference.processPriorityClass,
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$Candidate.processPowerThrottlingMode,
            [string]$Reference.processPowerThrottlingMode,
            [StringComparison]::Ordinal) -and
        [bool]$Candidate.gcServer -eq [bool]$Reference.gcServer -and
        [string]::Equals(
            [string]$Candidate.gcLatencyMode,
            [string]$Reference.gcLatencyMode,
            [StringComparison]::Ordinal))
}

function Test-SameRunEnvironment {
    param(
        [Parameter(Mandatory)][object] $Start,
        [Parameter(Mandatory)][object] $Completion
    )

    return (
        [string]::Equals([string]$Start.machineInstanceSha256, [string]$Completion.machineInstanceSha256, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Start.cpu, [string]$Completion.cpu, [StringComparison]::Ordinal) -and
        [int]$Start.logicalProcessorCount -eq [int]$Completion.logicalProcessorCount -and
        [string]::Equals([string]$Start.osDescription, [string]$Completion.osDescription, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.osVersion, [string]$Completion.osVersion, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.osArchitecture, [string]$Completion.osArchitecture, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.processArchitecture, [string]$Completion.processArchitecture, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.frameworkDescription, [string]$Completion.frameworkDescription, [StringComparison]::Ordinal) -and
        [Math]::Abs([double]$Start.dpiScale - [double]$Completion.dpiScale) -lt 0.01 -and
        [Math]::Abs([double]$Start.viewportWidthDips - [double]$Completion.viewportWidthDips) -lt 1.0 -and
        [Math]::Abs([double]$Start.viewportHeightDips - [double]$Completion.viewportHeightDips) -lt 1.0 -and
        [string]::Equals([string]$Start.displayDeviceName, [string]$Completion.displayDeviceName, [StringComparison]::Ordinal) -and
        [Math]::Abs([double]$Start.configuredRefreshRateHz - [double]$Completion.configuredRefreshRateHz) -lt 0.5 -and
        [string]::Equals([string]$Start.gpuAdapterIdentitySha256, [string]$Completion.gpuAdapterIdentitySha256, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Start.gpuAdapterLuid, [string]$Completion.gpuAdapterLuid, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Start.gpuDriverVersion, [string]$Completion.gpuDriverVersion, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.displayAdapterIdentitySha256, [string]$Completion.displayAdapterIdentitySha256, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Start.displayAdapterLuid, [string]$Completion.displayAdapterLuid, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Start.displayAdapterDriverVersion, [string]$Completion.displayAdapterDriverVersion, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.activePowerSchemeGuid, [string]$Completion.activePowerSchemeGuid, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Start.userConfiguredAcPowerModeGuid, [string]$Completion.userConfiguredAcPowerModeGuid, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Start.userConfiguredDcPowerModeGuid, [string]$Completion.userConfiguredDcPowerModeGuid, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Start.powerSource, [string]$Completion.powerSource, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.energySaverState, [string]$Completion.energySaverState, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.effectivePowerMode, [string]$Completion.effectivePowerMode, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.processPriorityClass, [string]$Completion.processPriorityClass, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Start.processPowerThrottlingMode, [string]$Completion.processPowerThrottlingMode, [StringComparison]::Ordinal) -and
        [bool]$Start.gcServer -eq [bool]$Completion.gcServer -and
        [string]::Equals([string]$Start.gcLatencyMode, [string]$Completion.gcLatencyMode, [StringComparison]::Ordinal))
}

function Test-EnvironmentSnapshotMetadata {
    param([Parameter(Mandatory)][object] $Machine)

    foreach ($name in @(
            'cpu', 'osDescription', 'osVersion', 'osArchitecture', 'processArchitecture',
            'frameworkDescription', 'displayDeviceName', 'gpuDriverVersion',
            'displayAdapterDriverVersion')) {
        if ([string]::IsNullOrWhiteSpace([string]$Machine.$name)) { return $false }
    }
    foreach ($name in @(
            'logicalProcessorCount', 'dpiScale', 'viewportWidthDips', 'viewportHeightDips',
            'configuredRefreshRateHz')) {
        if (-not (Test-FiniteNumber $Machine.$name) -or [double]$Machine.$name -le 0) {
            return $false
        }
    }

    return (
        (Test-Sha256 $Machine.machineInstanceSha256) -and
        (Test-Sha256 $Machine.gpuAdapterIdentitySha256) -and
        [string]$Machine.gpuAdapterLuid -match '^[0-9A-Fa-f]{8}:[0-9A-Fa-f]{8}$' -and
        (Test-Sha256 $Machine.displayAdapterIdentitySha256) -and
        [string]$Machine.displayAdapterLuid -match '^[0-9A-Fa-f]{8}:[0-9A-Fa-f]{8}$' -and
        (Test-CanonicalGuid $Machine.activePowerSchemeGuid) -and
        (Test-PowerModePair `
            $Machine.userConfiguredAcPowerModeGuid `
            $Machine.userConfiguredDcPowerModeGuid) -and
        (Test-EffectivePowerMode $Machine.effectivePowerMode) -and
        [string]$Machine.powerSource -cin @('AC', 'Battery') -and
        [string]$Machine.energySaverState -cin @('Off', 'On') -and
        [string]$Machine.processPriorityClass -cin
            @('Normal', 'Idle', 'High', 'RealTime', 'BelowNormal', 'AboveNormal') -and
        [string]$Machine.processPowerThrottlingMode -ceq
            'execution-speed=off;ignore-timer-resolution=off' -and
        $Machine.gcServer -is [bool] -and
        [string]$Machine.gcLatencyMode -cin
            @('Batch', 'Interactive', 'LowLatency', 'SustainedLowLatency', 'NoGCRegion'))
}

function Test-RuntimeConfigurationEvidence {
    param(
        [Parameter(Mandatory)][object] $RuntimeConfiguration,
        [Parameter(Mandatory)][string] $Label
    )

    if ([string]$RuntimeConfiguration.policy -cne
            'tiering-pgo-concurrent-gc-readytorun-disabled;dotnet-complus-overrides-unset-v1' -or
        $RuntimeConfiguration.tieredCompilationEnabled -isnot [bool] -or
        [bool]$RuntimeConfiguration.tieredCompilationEnabled -or
        $RuntimeConfiguration.tieredPgoEnabled -isnot [bool] -or
        [bool]$RuntimeConfiguration.tieredPgoEnabled -or
        $RuntimeConfiguration.concurrentGcEnabled -isnot [bool] -or
        [bool]$RuntimeConfiguration.concurrentGcEnabled -or
        $RuntimeConfiguration.readyToRunEnabled -isnot [bool] -or
        [bool]$RuntimeConfiguration.readyToRunEnabled -or
        @($RuntimeConfiguration.overrideEnvironmentVariables).Count -ne 0 -or
        $RuntimeConfiguration.passed -isnot [bool] -or
        -not [bool]$RuntimeConfiguration.passed) {
        Add-Failure "$Label does not satisfy the frozen runtime configuration policy."
        return $false
    }
    return $true
}

function Test-RuntimeConfigArtifact {
    param(
        [Parameter(Mandatory)][object] $Artifact,
        [Parameter(Mandatory)][string] $Label
    )

    $document = $null
    try {
        [byte[]]$bytes = [IO.File]::ReadAllBytes([string]$Artifact.path)
        $json = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        $document = [Text.Json.JsonDocument]::Parse($json)
        $properties = $document.RootElement.GetProperty('runtimeOptions').GetProperty('configProperties')
        foreach ($name in @(
                'System.Runtime.TieredCompilation',
                'System.Runtime.TieredPGO',
                'System.GC.Concurrent',
                'System.Runtime.ReadyToRun')) {
            $value = $properties.GetProperty($name)
            if ($value.ValueKind -ne [Text.Json.JsonValueKind]::False) {
                Add-Failure "$Label must explicitly set '$name' to false."
                return $false
            }
        }
    }
    catch {
        Add-Failure "$Label could not be verified: $($_.Exception.Message)"
        return $false
    }
    finally {
        if ($null -ne $document) { $document.Dispose() }
    }
    return $true
}

function Get-FirstViewportWilliamsCondition {
    param(
        [Parameter(Mandatory)][int] $Row,
        [Parameter(Mandatory)][int] $Position
    )

    $firstRow = @(0, 1, 5, 2, 4, 3)
    if ($Row -lt 0 -or $Row -ge 6 -or $Position -lt 0 -or $Position -ge 6) {
        return -1
    }
    return ($firstRow[$Position] + $Row) % 6
}

function Get-ValidatedFirstViewportEvidence {
    param(
        [Parameter(Mandatory)][object[]] $Results,
        [Parameter(Mandatory)][string] $Label,
        [Parameter(Mandatory)][DateTimeOffset] $ReportStartedUtc,
        [Parameter(Mandatory)][DateTimeOffset] $ReportCompletedUtc
    )

    $metrics = @{}
    $scheduledTrials = [Collections.Generic.List[object]]::new()
    $firstPin = $null
    if ($Results.Count -ne 6) {
        Add-Failure "$Label must contain exactly six scenarios in condition order."
        return [pscustomobject]@{ Metrics = $metrics; Pin = $null }
    }

    $scenarioSources = @(102400, 102400, 1048576, 1048576, 10485760, 10485760)
    $scenarioModes = @(
        'cache-disabled', 'cache-hit', 'cache-disabled',
        'cache-hit', 'cache-disabled', 'cache-hit')
    for ($conditionIndex = 0; $conditionIndex -lt 6; $conditionIndex++) {
        $result = $Results[$conditionIndex]
        $sourceBytes = $scenarioSources[$conditionIndex]
        $mode = $scenarioModes[$conditionIndex]
        if ($null -eq $result) {
            Add-Failure "$Label scenario $conditionIndex is null."
            continue
        }

        $expectedCorpus = if ($sourceBytes -eq 10 * 1024 * 1024) {
            'pathological-long-reference-token-v1'
        } else {
            'readme-mixed-v1'
        }
        $expectedBudget = if ($sourceBytes -eq 100 * 1024 -and $mode -eq 'cache-hit') { 75.0 }
            elseif ($sourceBytes -eq 100 * 1024) { 125.0 }
            elseif ($sourceBytes -eq 1024 * 1024) { 250.0 }
            else { 750.0 }
        $expectedCacheBudget = if ($mode -eq 'cache-hit') { [int64]$sourceBytes * 8L } else { 0L }
        if ([string]$result.corpus -cne $expectedCorpus -or
            [string]$result.mode -cne $mode -or
            [int]$result.sourceUtf16Bytes -ne $sourceBytes -or
            [int64]$result.parseCacheBudgetBytes -ne $expectedCacheBudget -or
            [string]$result.engineReuseMode -cne 'fresh-harness-owned-engine-per-trial' -or
            [string]$result.trialStartPolicy -cne
                'fresh-engine;optional-cache-prime;managed-full-collection;one-settling-presentation;recorded-presentations' -or
            [string]$result.schedulePolicy -cne 'six-condition-six-period-williams-square-v1' -or
            [string]$result.regressionEstimator -cne 'hodges-lehmann-of-six-trial-p95' -or
            [string]$result.stationarityPolicy -cne
                'mad-plus-theil-sen-projected-drift-plus-warmup-boundary-v1') {
            Add-Failure "$Label scenario $conditionIndex ($mode/$sourceBytes) violates the frozen protocol."
        }

        $pin = '{0}|{1}|{2}|{3}' -f
            [string]$result.measurementThreadAffinityMask,
            [int]$result.measurementProcessorGroup,
            [int]$result.measurementProcessorNumber,
            [string]$result.measurementThreadPriority
        if (-not (Test-PinnedProcessorEvidence `
                -AffinityMask $result.measurementThreadAffinityMask `
                -ProcessorGroup ([int]$result.measurementProcessorGroup) `
                -ProcessorNumber ([int]$result.measurementProcessorNumber) `
                -Priority $result.measurementThreadPriority `
                -ExpectedPriority 'Normal')) {
            Add-Failure "$Label scenario $conditionIndex has invalid Normal-priority pinned-thread evidence."
        }
        if ($null -eq $firstPin) { $firstPin = $pin }
        elseif ($pin -cne $firstPin) {
            Add-Failure "$Label first-viewport scenarios were not measured on one pinned thread."
        }

        $warmupP95 = [Collections.Generic.List[double]]::new(3)
        $measuredP95 = [Collections.Generic.List[double]]::new(6)
        $flattenedSamples = [Collections.Generic.List[double]]::new(600)
        $observations = [Collections.Generic.List[object]]::new(6)
        $phases = @(
            [pscustomobject]@{ Name = 'warmup'; Trials = @($result.warmupTrials); Count = 3; Offset = 0 },
            [pscustomobject]@{ Name = 'measured'; Trials = @($result.trials); Count = 6; Offset = 18 })
        foreach ($phase in $phases) {
            if ($phase.Trials.Count -ne $phase.Count) {
                Add-Failure "$Label scenario $conditionIndex must contain exactly $($phase.Count) $($phase.Name) trials."
                continue
            }

            for ($row = 0; $row -lt $phase.Count; $row++) {
                $trial = $phase.Trials[$row]
                if ($null -eq $trial) {
                    Add-Failure "$Label scenario $conditionIndex $($phase.Name) trial $row is null."
                    continue
                }
                $expectedPosition = -1
                for ($position = 0; $position -lt 6; $position++) {
                    if ((Get-FirstViewportWilliamsCondition -Row $row -Position $position) -eq
                            $conditionIndex) {
                        $expectedPosition = $position
                        break
                    }
                }
                $expectedGlobalOrdinal = $phase.Offset + $row * 6 + $expectedPosition
                if ([int]$trial.ordinal -ne $row -or
                    [int]$trial.globalOrdinal -ne $expectedGlobalOrdinal -or
                    [int]$trial.scheduleRow -ne $row -or
                    [int]$trial.schedulePosition -ne $expectedPosition) {
                    Add-Failure "$Label scenario $conditionIndex $($phase.Name) trial $row violates the Williams schedule."
                }

                $timestampsValid = $false
                try {
                    # Raw JSON validation above checks Offset == UTC before
                    # ConvertFrom-Json normalizes DateTime values to local time.
                    $trialStarted = [DateTimeOffset]$trial.startedUtc
                    $trialCompleted = [DateTimeOffset]$trial.completedUtc
                    $timestampsValid =
                        $trialStarted -ne [DateTimeOffset]::MinValue -and
                        $trialCompleted -ne [DateTimeOffset]::MinValue -and
                        $trialCompleted -gt $trialStarted -and
                        $trialStarted -gt $ReportStartedUtc -and
                        $trialCompleted -lt $ReportCompletedUtc
                    if ($timestampsValid) {
                        $scheduledTrials.Add([pscustomobject]@{
                            GlobalOrdinal = [int]$trial.globalOrdinal
                            StartedUtc = $trialStarted
                            CompletedUtc = $trialCompleted
                        })
                    }
                }
                catch {
                    $timestampsValid = $false
                }

                $trialSamples = @($trial.samplesMilliseconds)
                $samplesValid = $trialSamples.Count -eq 100 -and
                    @($trialSamples | Where-Object {
                        -not (Test-FiniteNumber $_) -or [double]$_ -lt 0
                    }).Count -eq 0
                $computedP95 = if ($samplesValid) {
                    Get-NearestRankPercentile -Values $trialSamples -Percentile 0.95
                } else {
                    [double]::NaN
                }
                $settlingValid = Test-FiniteNumber $trial.settlingElapsedMilliseconds
                $settlingValid = $settlingValid -and [double]$trial.settlingElapsedMilliseconds -ge 0
                $countersNonNegative =
                    [int]$trial.gen0Collections -ge 0 -and
                    [int]$trial.gen1Collections -ge 0 -and
                    [int]$trial.gen2Collections -ge 0 -and
                    [int]$trial.gen0Collections -ge [int]$trial.gen1Collections -and
                    [int]$trial.gen1Collections -ge [int]$trial.gen2Collections -and
                    [int64]$trial.processAllocatedBytes -ge 0
                $cacheProof = if ($mode -eq 'cache-disabled') {
                    [int]$trial.completedParseCountBeforePrime -eq 0 -and
                    [int]$trial.completedParseCountAfterPrime -eq 0 -and
                    [int]$trial.completedParseCountAfterTrial -eq 0 -and
                    [int64]$trial.sourceKeyHashCountBeforePrime -eq 0 -and
                    [int64]$trial.sourceKeyHashCountAfterPrime -eq 0 -and
                    [int64]$trial.sourceKeyHashCountAfterTrial -eq 101
                } else {
                    [int]$trial.completedParseCountBeforePrime -eq 0 -and
                    [int]$trial.completedParseCountAfterPrime -eq 1 -and
                    [int]$trial.completedParseCountAfterTrial -eq 1 -and
                    [int64]$trial.sourceKeyHashCountBeforePrime -eq 0 -and
                    [int64]$trial.sourceKeyHashCountAfterPrime -eq 1 -and
                    [int64]$trial.sourceKeyHashCountAfterTrial -eq 1
                }
                $computedComplete = $timestampsValid -and $samplesValid -and
                    $settlingValid -and [double]::IsFinite($computedP95) -and
                    $countersNonNegative -and $cacheProof
                if (-not $timestampsValid -or -not $samplesValid -or -not $settlingValid -or
                    -not $countersNonNegative -or
                    -not (Test-NearlyEqual ([double]$trial.p95Milliseconds) $computedP95) -or
                    [bool]$trial.cacheProofPassed -ne $cacheProof -or
                    -not [bool]$trial.cacheProofPassed -or
                    [bool]$trial.complete -ne $computedComplete -or
                    -not [bool]$trial.complete) {
                    Add-Failure "$Label scenario $conditionIndex $($phase.Name) trial $row is incomplete or not reproducible."
                }

                if ([double]::IsFinite($computedP95)) {
                    if ($phase.Name -eq 'warmup') {
                        $warmupP95.Add($computedP95)
                    } else {
                        $measuredP95.Add($computedP95)
                        $observations.Add([pscustomobject]@{
                            GlobalOrdinal = [int]$trial.globalOrdinal
                            Value = $computedP95
                        })
                    }
                }
                if ($phase.Name -eq 'measured' -and $samplesValid) {
                    foreach ($sample in $trialSamples) { $flattenedSamples.Add([double]$sample) }
                }
            }
        }

        $pooledSamples = @($result.samplesMilliseconds)
        $pooledValid = $pooledSamples.Count -eq 600 -and
            @($pooledSamples | Where-Object {
                -not (Test-FiniteNumber $_) -or [double]$_ -lt 0
            }).Count -eq 0
        $exactConcatenation = $pooledSamples.Count -eq $flattenedSamples.Count
        if ($exactConcatenation) {
            for ($sampleIndex = 0; $sampleIndex -lt $pooledSamples.Count; $sampleIndex++) {
                if ([double]$pooledSamples[$sampleIndex] -ne $flattenedSamples[$sampleIndex]) {
                    $exactConcatenation = $false
                    break
                }
            }
        }
        $computedPooledP95 = if ($pooledValid) {
            Get-NearestRankPercentile -Values $pooledSamples -Percentile 0.95
        } else {
            [double]::NaN
        }
        $derivedComplete = $warmupP95.Count -eq 3 -and $measuredP95.Count -eq 6
        if ($derivedComplete) {
            $regressionP95 = Get-HodgesLehmann -Values @($measuredP95)
            $dispersion = Get-RobustDispersionPercent -Values @($measuredP95)
            $theilSen = Get-TheilSenStatistics -Observations @($observations)
            $noiseFloor = if ($sourceBytes -eq 100 * 1024 -and $mode -eq 'cache-disabled') { 5.0 }
                elseif ($sourceBytes -eq 100 * 1024) { 3.0 }
                elseif ($sourceBytes -eq 1024 * 1024) { 5.0 }
                elseif ($mode -eq 'cache-disabled') { 3.0 }
                else { 1.0 }
            $allowance = [Math]::Max($noiseFloor, [Math]::Abs($regressionP95) * 0.05)
            $warmupCenter = Get-TwoValueCenter -First $warmupP95[1] -Second $warmupP95[2]
            $measuredCenter = Get-TwoValueCenter -First $measuredP95[0] -Second $measuredP95[1]
            $boundaryShift = [Math]::Abs($warmupCenter - $measuredCenter)
            $measuredEnvelopePassed = @($measuredP95 | Where-Object {
                [Math]::Abs([double]$_ - $regressionP95) -gt $allowance
            }).Count -eq 0
            $boundaryEndpoints = @(
                $warmupP95[1],
                $warmupP95[2],
                $measuredP95[0],
                $measuredP95[1]
            )
            $boundaryEndpointEnvelopePassed = @($boundaryEndpoints | Where-Object {
                [Math]::Abs([double]$_ - $regressionP95) -gt $allowance
            }).Count -eq 0
            $stationarity = $null -ne $theilSen -and
                [double]::IsFinite($dispersion) -and $dispersion -le 25.0 -and
                [double]::IsFinite($theilSen.Slope) -and
                [double]::IsFinite($theilSen.ProjectedDrift) -and
                [Math]::Abs([double]$theilSen.ProjectedDrift) -le $allowance -and
                [double]::IsFinite($boundaryShift) -and $boundaryShift -le $allowance -and
                $measuredEnvelopePassed -and $boundaryEndpointEnvelopePassed
            if (-not $pooledValid -or -not $exactConcatenation -or
                -not (Test-NearlyEqual ([double]$result.p95Milliseconds) $computedPooledP95) -or
                -not (Test-NearlyEqual ([double]$result.regressionP95Milliseconds) $regressionP95) -or
                -not (Test-NearlyEqual ([double]$result.regressionDispersionPercent) $dispersion) -or
                $null -eq $theilSen -or
                -not (Test-NearlyEqual `
                    ([double]$result.theilSenSlopeMillisecondsPerGlobalOrdinal) `
                    ([double]$theilSen.Slope)) -or
                -not (Test-NearlyEqual `
                    ([double]$result.projectedDriftMilliseconds) `
                    ([double]$theilSen.ProjectedDrift)) -or
                -not (Test-NearlyEqual `
                    ([double]$result.warmupBoundaryShiftMilliseconds) $boundaryShift) -or
                -not (Test-NearlyEqual `
                    ([double]$result.stationarityAllowanceMilliseconds) $allowance) -or
                -not [bool]$result.stationarityEvaluated -or
                [bool]$result.stationarityPassed -ne $stationarity -or
                -not $stationarity -or
                [double]$result.budgetMilliseconds -ne $expectedBudget -or
                $computedPooledP95 -gt $expectedBudget -or
                -not [bool]$result.passed) {
                Add-Failure "$Label scenario $conditionIndex aggregate, stationarity, or budget evidence is invalid."
            }

            $metric = "firstViewport.$mode.$sourceBytes.p95Ms.hodgesLehmann"
            $metrics[$metric] = [pscustomobject]@{
                Value = $regressionP95
                Trials = @($measuredP95)
                Dispersion = $dispersion
                Stationarity = $stationarity
            }
        } else {
            Add-Failure "$Label scenario $conditionIndex lacks complete warmup/measured statistics."
        }
    }

    $ordered = @($scheduledTrials | Sort-Object GlobalOrdinal)
    if ($ordered.Count -ne 54) {
        Add-Failure "$Label must contain 54 timestamped trials."
    } else {
        for ($globalOrdinal = 0; $globalOrdinal -lt 54; $globalOrdinal++) {
            if ([int]$ordered[$globalOrdinal].GlobalOrdinal -ne $globalOrdinal -or
                ($globalOrdinal -gt 0 -and
                 $ordered[$globalOrdinal].StartedUtc -le $ordered[$globalOrdinal - 1].CompletedUtc)) {
                Add-Failure "$Label trial timestamps do not follow the exact global schedule."
                break
            }
        }
    }

    return [pscustomobject]@{ Metrics = $metrics; Pin = $firstPin }
}

$requiredPaths = @(
    'schemaVersion', 'providerName', 'startedUtc', 'completedUtc', 'isReleaseEvidence',
    'passed', 'buildIdentity', 'buildArtifacts', 'runtimeConfiguration', 'completionMachine',
    'sampleRequirements', 'firstUsableViewport', 'scroll',
    'retainedMemory', 'lifecyclePlateau', 'sourceLookup', 'cancellation', 'regression', 'failures',
    'machine.machineInstanceSha256', 'machine.cpu', 'machine.logicalProcessorCount',
    'machine.osDescription', 'machine.osVersion',
    'machine.osArchitecture', 'machine.processArchitecture', 'machine.frameworkDescription',
    'machine.dpiScale', 'machine.viewportWidthDips', 'machine.viewportHeightDips',
    'machine.displayDeviceName',
    'machine.configuredRefreshRateHz', 'machine.observedRefreshRateHz', 'machine.isAtLeast120Hz',
    'machine.gpuAdapterLuid', 'machine.gpuAdapterIdentitySha256',
    'machine.gpuDriverVersion', 'machine.displayAdapterIdentitySha256',
    'machine.displayAdapterLuid', 'machine.displayAdapterDriverVersion',
    'machine.activePowerSchemeGuid',
    'machine.userConfiguredAcPowerModeGuid', 'machine.userConfiguredDcPowerModeGuid',
    'machine.powerSource', 'machine.energySaverState', 'machine.effectivePowerMode',
    'machine.processPriorityClass', 'machine.processPowerThrottlingMode',
    'machine.gcServer', 'machine.gcLatencyMode',
    'sampleRequirements.firstViewportIterationsRequired',
    'sampleRequirements.firstViewportTrialsRequired',
    'sampleRequirements.firstViewportWarmupTrialsRequired',
    'sampleRequirements.scrollFramesRequired',
    'sampleRequirements.scrollTrialsRequired', 'sampleRequirements.lifecycleCyclesRequired',
    'sampleRequirements.cancellationIterationsPerTrialRequired',
    'sampleRequirements.cancellationTrialsRequired', 'sampleRequirements.sourceLookupMappingCountRequired',
    'sampleRequirements.sourceLookupAbsoluteQueryCountRequired',
    'sampleRequirements.sourceLookupRegressionWarmupPassesRequired',
    'sampleRequirements.sourceLookupRegressionTrialsRequired',
    'sampleRequirements.sourceLookupRegressionBatchSizeRequired',
    'sampleRequirements.sourceLookupRegressionObservationCountRequired',
    'sampleRequirements.sourceLookupRegressionOperationCountRequired',
    'buildArtifacts.hashAlgorithm', 'buildArtifacts.executable',
    'buildArtifacts.performanceHarnessDll', 'buildArtifacts.markdownRendererDll',
    'buildArtifacts.markdownRendererCoreDll', 'buildArtifacts.runtimeConfig',
    'buildArtifacts.executable.fileName', 'buildArtifacts.executable.path',
    'buildArtifacts.executable.sha256', 'buildArtifacts.performanceHarnessDll.fileName',
    'buildArtifacts.performanceHarnessDll.path', 'buildArtifacts.performanceHarnessDll.sha256',
    'buildArtifacts.markdownRendererDll.fileName', 'buildArtifacts.markdownRendererDll.path',
    'buildArtifacts.markdownRendererDll.sha256', 'buildArtifacts.markdownRendererCoreDll.fileName',
    'buildArtifacts.markdownRendererCoreDll.path', 'buildArtifacts.markdownRendererCoreDll.sha256',
    'buildArtifacts.runtimeConfig.fileName', 'buildArtifacts.runtimeConfig.path',
    'buildArtifacts.runtimeConfig.sha256',
    'runtimeConfiguration.policy', 'runtimeConfiguration.tieredCompilationEnabled',
    'runtimeConfiguration.tieredPgoEnabled', 'runtimeConfiguration.concurrentGcEnabled',
    'runtimeConfiguration.readyToRunEnabled',
    'runtimeConfiguration.overrideEnvironmentVariables', 'runtimeConfiguration.passed',
    'scroll.corpus', 'scroll.sourceUtf16Bytes', 'scroll.stopwatchFrequency',
    'scroll.requestedFrames', 'scroll.measuredFrameIntervals', 'scroll.framesWithRendererWork',
    'scroll.regressionEstimator', 'scroll.trials',
    'scroll.regressionUiThreadWorkP95Milliseconds', 'scroll.regressionUiThreadWorkP99Milliseconds',
    'scroll.regressionFrameTimeP95Milliseconds',
    'scroll.regressionRendererOwnedAllocatedBytesPerFrameP95',
    'scroll.uiThreadWorkP95Milliseconds', 'scroll.uiThreadWorkP99Milliseconds',
    'scroll.scrollCallbackWorkP95Milliseconds', 'scroll.paintCallbackWorkP95Milliseconds',
    'scroll.uiThreadWorkP95BudgetMilliseconds', 'scroll.uiThreadWorkP99BudgetMilliseconds',
    'scroll.frameTimeP95Milliseconds', 'scroll.frameTimeP95BudgetMilliseconds',
    'scroll.framesOver16_7Percent', 'scroll.framesOver16_7PercentBudget',
    'scroll.maximumFrameStallMilliseconds', 'scroll.maximumFrameStallBudgetMilliseconds',
    'scroll.allocatedBytesPerFrameP95', 'scroll.maximumAllocatedBytesPerFrame',
    'scroll.rawAllocationScope', 'scroll.allocationGateBasis',
    'scroll.rendererOwnedAllocatedBytesPerFrameP95', 'scroll.maximumRendererOwnedAllocatedBytesPerFrame',
    'scroll.platformProjectionAllocatedBytesPerFrameP95', 'scroll.maximumPlatformProjectionAllocatedBytesPerFrame',
    'scroll.platformProjectionAllocationScope', 'scroll.scrollAllocatedBytesPerFrameP95',
    'scroll.paintAllocatedBytesPerFrameP95', 'scroll.scrollLazyLayoutAllocatedBytesP95',
    'scroll.scrollAdornerAllocatedBytesP95', 'scroll.scrollRealizationAllocatedBytesP95',
    'scroll.scrollHighlightingAllocatedBytesP95', 'scroll.paintSchedulingAllocatedBytesP95',
    'scroll.paintPlatformSessionAllocatedBytesP95', 'scroll.snapshotPaintAllocatedBytesP95',
    'scroll.interactivePaintAllocatedBytesP95', 'scroll.inlinePaintAllocatedBytesP95',
    'scroll.tablePaintAllocatedBytesP95', 'scroll.codePaintAllocatedBytesP95',
    'scroll.otherPaintAllocatedBytesP95', 'scroll.allocationBudgetBytesPerFrame',
    'scroll.lohAllocationPossible', 'scroll.rendererOwnedLohAllocationPossible',
    'scroll.maximumRealizedCodeActions', 'scroll.maximumOffscreenRealizedCodeActions',
    'scroll.gen2Collections', 'scroll.passed',
    'lifecyclePlateau.completedCycles', 'lifecyclePlateau.requiredCycles',
    'lifecyclePlateau.syntheticDeviceResets', 'lifecyclePlateau.deviceResetMode',
    'lifecyclePlateau.actualDeviceLossEvents', 'lifecyclePlateau.checkpoint80ManagedBytes',
    'lifecyclePlateau.checkpoint100ManagedBytes', 'lifecyclePlateau.checkpoint80PrivateBytes',
    'lifecyclePlateau.checkpoint100PrivateBytes', 'lifecyclePlateau.managedGrowthPercent',
    'lifecyclePlateau.privateGrowthPercent', 'lifecyclePlateau.growthPercent',
    'lifecyclePlateau.growthBudgetPercent', 'lifecyclePlateau.renderFailures', 'lifecyclePlateau.passed',
    'sourceLookup.mappingCount', 'sourceLookup.queryCount', 'sourceLookup.stopwatchFrequency',
    'sourceLookup.measurementThreadAffinityMask', 'sourceLookup.measurementProcessorGroup',
    'sourceLookup.measurementProcessorNumber', 'sourceLookup.measurementThreadPriority',
    'sourceLookup.samplesElapsedTicks', 'sourceLookup.p95Nanoseconds',
    'sourceLookup.p95BudgetNanoseconds', 'sourceLookup.allocatedBytes',
    'sourceLookup.regressionWarmupPasses', 'sourceLookup.regressionBatchSize',
    'sourceLookup.regressionObservationCount', 'sourceLookup.regressionOperationCount',
    'sourceLookup.regressionEstimator', 'sourceLookup.regressionTrialCount',
    'sourceLookup.regressionTrials',
    'sourceLookup.regressionP95Nanoseconds', 'sourceLookup.regressionAllocatedBytes',
    'sourceLookup.passed',
    'cancellation.requestedIterations', 'cancellation.iterationsPerTrial',
    'cancellation.warmupIterations',
    'cancellation.warmupElapsedMilliseconds', 'cancellation.warmupStaleCommits',
    'cancellation.observedCancellations', 'cancellation.trialStartPolicy',
    'cancellation.regressionEstimator', 'cancellation.trials',
    'cancellation.samplesMilliseconds', 'cancellation.p95Milliseconds',
    'cancellation.regressionP95Milliseconds',
    'cancellation.p95BudgetMilliseconds', 'cancellation.staleCommits', 'cancellation.passed',
    'regression.mode', 'regression.decisionPolicy', 'regression.referencePath', 'regression.referenceReportSha256',
    'regression.referenceBuildIdentity',
    'regression.referenceEligible', 'regression.machineComparable',
    'regression.comparedLatencyMetrics', 'regression.comparedAllocationMetrics',
    'regression.latencyRegressionBudgetPercent', 'regression.allocationRegressionBudgetPercent',
    'regression.comparisons', 'regression.passed'
)
foreach ($path in $requiredPaths) {
    $null = Get-RequiredValue -Root $report -Path $path
}

$failures = [Collections.Generic.List[string]]::new()
function Add-Failure([string] $Message) {
    $failures.Add($Message)
}

# The Windows-only release gate validates pin evidence against the topology that
# exists when the report is evaluated. An unavailable native probe is an
# infrastructure failure; accepting report-supplied topology would be forgeable.
$null = Get-ActiveProcessorTopology

$jsonObjectContracts = @{
    Report = @(
        'schemaVersion', 'providerName', 'startedUtc', 'completedUtc', 'isReleaseEvidence',
        'passed', 'buildIdentity', 'buildArtifacts', 'runtimeConfiguration', 'machine',
        'completionMachine', 'sampleRequirements',
        'firstUsableViewport', 'scroll', 'retainedMemory', 'lifecyclePlateau',
        'sourceLookup', 'cancellation', 'regression', 'failures')
    BuildArtifacts = @(
        'hashAlgorithm', 'executable', 'performanceHarnessDll', 'markdownRendererDll',
        'markdownRendererCoreDll', 'runtimeConfig')
    Artifact = @('fileName', 'path', 'sha256')
    RuntimeConfiguration = @(
        'policy', 'tieredCompilationEnabled', 'tieredPgoEnabled', 'concurrentGcEnabled',
        'readyToRunEnabled', 'overrideEnvironmentVariables', 'passed')
    Machine = @(
        'machineInstanceSha256', 'cpu', 'logicalProcessorCount', 'osDescription', 'osVersion',
        'osArchitecture', 'processArchitecture', 'frameworkDescription', 'dpiScale',
        'viewportWidthDips', 'viewportHeightDips', 'displayDeviceName', 'configuredRefreshRateHz',
        'observedRefreshRateHz', 'isAtLeast120Hz', 'gpuAdapterLuid',
        'gpuAdapterIdentitySha256', 'gpuDriverVersion', 'displayAdapterIdentitySha256',
        'displayAdapterLuid', 'displayAdapterDriverVersion', 'activePowerSchemeGuid',
        'userConfiguredAcPowerModeGuid', 'userConfiguredDcPowerModeGuid',
        'powerSource', 'energySaverState', 'effectivePowerMode',
        'processPriorityClass', 'processPowerThrottlingMode', 'gcServer', 'gcLatencyMode')
    SampleRequirements = @(
        'firstViewportIterationsRequired', 'firstViewportTrialsRequired',
        'firstViewportWarmupTrialsRequired', 'scrollFramesRequired', 'scrollTrialsRequired',
        'lifecycleCyclesRequired', 'cancellationIterationsPerTrialRequired',
        'cancellationTrialsRequired', 'sourceLookupMappingCountRequired',
        'sourceLookupAbsoluteQueryCountRequired', 'sourceLookupRegressionWarmupPassesRequired',
        'sourceLookupRegressionTrialsRequired', 'sourceLookupRegressionBatchSizeRequired',
        'sourceLookupRegressionObservationCountRequired',
        'sourceLookupRegressionOperationCountRequired')
    FirstViewport = @(
        'corpus', 'mode', 'sourceUtf16Bytes', 'parseCacheBudgetBytes', 'engineReuseMode',
        'trialStartPolicy', 'schedulePolicy', 'regressionEstimator', 'stationarityPolicy',
        'measurementThreadAffinityMask', 'measurementProcessorGroup',
        'measurementProcessorNumber', 'measurementThreadPriority', 'warmupTrials', 'trials',
        'samplesMilliseconds', 'p95Milliseconds', 'regressionP95Milliseconds',
        'regressionDispersionPercent', 'theilSenSlopeMillisecondsPerGlobalOrdinal',
        'projectedDriftMilliseconds', 'warmupBoundaryShiftMilliseconds',
        'stationarityAllowanceMilliseconds', 'stationarityEvaluated', 'stationarityPassed',
        'budgetMilliseconds', 'passed')
    FirstViewportTrial = @(
        'ordinal', 'globalOrdinal', 'scheduleRow', 'schedulePosition', 'startedUtc',
        'completedUtc', 'settlingElapsedMilliseconds', 'samplesMilliseconds',
        'p95Milliseconds', 'gen0Collections', 'gen1Collections', 'gen2Collections',
        'processAllocatedBytes', 'completedParseCountBeforePrime',
        'completedParseCountAfterPrime', 'completedParseCountAfterTrial',
        'sourceKeyHashCountBeforePrime', 'sourceKeyHashCountAfterPrime',
        'sourceKeyHashCountAfterTrial', 'cacheProofPassed', 'complete')
    Scroll = @(
        'corpus', 'sourceUtf16Bytes', 'stopwatchFrequency', 'requestedFrames',
        'measuredFrameIntervals', 'framesWithRendererWork', 'regressionEstimator', 'trials',
        'regressionUiThreadWorkP95Milliseconds', 'regressionUiThreadWorkP99Milliseconds',
        'regressionFrameTimeP95Milliseconds',
        'regressionRendererOwnedAllocatedBytesPerFrameP95', 'uiThreadWorkP95Milliseconds',
        'uiThreadWorkP99Milliseconds', 'scrollCallbackWorkP95Milliseconds',
        'paintCallbackWorkP95Milliseconds', 'uiThreadWorkP95BudgetMilliseconds',
        'uiThreadWorkP99BudgetMilliseconds', 'frameTimeP95Milliseconds',
        'frameTimeP95BudgetMilliseconds', 'framesOver16_7Percent',
        'framesOver16_7PercentBudget', 'maximumFrameStallMilliseconds',
        'maximumFrameStallBudgetMilliseconds', 'allocatedBytesPerFrameP95',
        'maximumAllocatedBytesPerFrame', 'rawAllocationScope', 'allocationGateBasis',
        'rendererOwnedAllocatedBytesPerFrameP95', 'maximumRendererOwnedAllocatedBytesPerFrame',
        'platformProjectionAllocatedBytesPerFrameP95',
        'maximumPlatformProjectionAllocatedBytesPerFrame', 'platformProjectionAllocationScope',
        'scrollAllocatedBytesPerFrameP95', 'paintAllocatedBytesPerFrameP95',
        'scrollLazyLayoutAllocatedBytesP95', 'scrollAdornerAllocatedBytesP95',
        'scrollRealizationAllocatedBytesP95', 'scrollHighlightingAllocatedBytesP95',
        'paintSchedulingAllocatedBytesP95', 'paintPlatformSessionAllocatedBytesP95',
        'snapshotPaintAllocatedBytesP95', 'interactivePaintAllocatedBytesP95',
        'inlinePaintAllocatedBytesP95', 'tablePaintAllocatedBytesP95',
        'codePaintAllocatedBytesP95', 'otherPaintAllocatedBytesP95',
        'allocationBudgetBytesPerFrame', 'lohAllocationPossible',
        'rendererOwnedLohAllocationPossible', 'maximumRealizedCodeActions',
        'maximumOffscreenRealizedCodeActions', 'gen2Collections', 'passed')
    ScrollTrial = @(
        'ordinal', 'firstLogicalFrameId', 'requestedFrames', 'measuredFrameIntervals',
        'framesWithRendererWork', 'uiThreadWorkElapsedTicks', 'frameIntervalElapsedTicks',
        'rendererOwnedAllocatedBytesPerFrame', 'uiThreadWorkP95Milliseconds',
        'uiThreadWorkP99Milliseconds', 'scrollCallbackWorkP95Milliseconds',
        'paintCallbackWorkP95Milliseconds', 'frameTimeP95Milliseconds',
        'framesOver16_7Percent', 'maximumFrameStallMilliseconds', 'allocatedBytesPerFrameP95',
        'maximumAllocatedBytesPerFrame', 'rendererOwnedAllocatedBytesPerFrameP95',
        'maximumRendererOwnedAllocatedBytesPerFrame',
        'platformProjectionAllocatedBytesPerFrameP95',
        'maximumPlatformProjectionAllocatedBytesPerFrame', 'scrollAllocatedBytesPerFrameP95',
        'paintAllocatedBytesPerFrameP95', 'scrollLazyLayoutAllocatedBytesP95',
        'scrollAdornerAllocatedBytesP95', 'scrollRealizationAllocatedBytesP95',
        'scrollHighlightingAllocatedBytesP95', 'paintSchedulingAllocatedBytesP95',
        'paintPlatformSessionAllocatedBytesP95', 'snapshotPaintAllocatedBytesP95',
        'interactivePaintAllocatedBytesP95', 'inlinePaintAllocatedBytesP95',
        'tablePaintAllocatedBytesP95', 'codePaintAllocatedBytesP95',
        'otherPaintAllocatedBytesP95', 'maximumRealizedCodeActions',
        'maximumOffscreenRealizedCodeActions', 'gen2Collections', 'complete')
    RetainedMemory = @(
        'corpus', 'sourceUtf16Bytes', 'managedDeltaBytes', 'privateDeltaBytes', 'budgetBytes', 'passed')
    Lifecycle = @(
        'completedCycles', 'requiredCycles', 'syntheticDeviceResets', 'deviceResetMode',
        'actualDeviceLossEvents', 'checkpoint80ManagedBytes', 'checkpoint100ManagedBytes',
        'checkpoint80PrivateBytes', 'checkpoint100PrivateBytes', 'managedGrowthPercent',
        'privateGrowthPercent', 'growthPercent', 'growthBudgetPercent', 'renderFailures', 'passed')
    SourceLookup = @(
        'mappingCount', 'queryCount', 'stopwatchFrequency', 'measurementThreadAffinityMask',
        'measurementProcessorGroup', 'measurementProcessorNumber', 'measurementThreadPriority',
        'samplesElapsedTicks', 'p95Nanoseconds', 'p95BudgetNanoseconds', 'allocatedBytes',
        'regressionWarmupPasses', 'regressionBatchSize', 'regressionObservationCount',
        'regressionOperationCount', 'regressionP95Nanoseconds', 'regressionAllocatedBytes',
        'regressionEstimator', 'regressionTrialCount', 'regressionTrials', 'passed')
    SourceLookupTrial = @(
        'ordinal', 'observationCount', 'operationCount', 'samplesElapsedTicks',
        'samplesNanosecondsPerLookup', 'p95Nanoseconds', 'allocatedBytes', 'complete')
    Cancellation = @(
        'requestedIterations', 'iterationsPerTrial', 'warmupIterations',
        'warmupElapsedMilliseconds', 'warmupStaleCommits', 'observedCancellations',
        'trialStartPolicy', 'regressionEstimator', 'trials', 'samplesMilliseconds',
        'p95Milliseconds', 'regressionP95Milliseconds', 'p95BudgetMilliseconds',
        'staleCommits', 'passed')
    CancellationTrial = @(
        'ordinal', 'warmupSourceSeed', 'warmupElapsedMilliseconds', 'warmupStaleCommits',
        'firstRecordedSourceSeed', 'requestedIterations', 'observedCancellations',
        'samplesMilliseconds', 'p95Milliseconds', 'staleCommits', 'complete')
    Regression = @(
        'mode', 'referencePath', 'referenceReportSha256', 'referenceBuildIdentity',
        'referenceEligible', 'machineComparable', 'comparedLatencyMetrics',
        'comparedAllocationMetrics', 'decisionPolicy', 'latencyRegressionBudgetPercent',
        'allocationRegressionBudgetPercent', 'comparisons', 'passed')
    RegressionComparison = @(
        'metric', 'kind', 'reference', 'candidate', 'absoluteDelta', 'deltaPercent',
        'budgetPercent', 'relativeAllowance', 'absoluteNoiseFloor', 'allowedAbsoluteDelta',
        'referenceDispersionPercent', 'candidateDispersionPercent', 'stabilityBudgetPercent',
        'decision', 'passed')
}

$jsonObjectChildren = @{
    Report = @{
        buildArtifacts = 'BuildArtifacts'; runtimeConfiguration = 'RuntimeConfiguration';
        machine = 'Machine'; completionMachine = 'Machine';
        sampleRequirements = 'SampleRequirements';
        scroll = 'Scroll'; lifecyclePlateau = 'Lifecycle'; sourceLookup = 'SourceLookup';
        cancellation = 'Cancellation'; regression = 'Regression'
    }
    BuildArtifacts = @{
        executable = 'Artifact'; performanceHarnessDll = 'Artifact';
        markdownRendererDll = 'Artifact'; markdownRendererCoreDll = 'Artifact';
        runtimeConfig = 'Artifact'
    }
}

$jsonObjectArrayChildren = @{
    Report = @{
        firstUsableViewport = 'FirstViewport'; retainedMemory = 'RetainedMemory'
    }
    FirstViewport = @{ warmupTrials = 'FirstViewportTrial'; trials = 'FirstViewportTrial' }
    Scroll = @{ trials = 'ScrollTrial' }
    SourceLookup = @{ regressionTrials = 'SourceLookupTrial' }
    Cancellation = @{ trials = 'CancellationTrial' }
    Regression = @{ comparisons = 'RegressionComparison' }
}

$jsonArrayProperties = @{
    Report = @('firstUsableViewport', 'retainedMemory', 'failures')
    RuntimeConfiguration = @('overrideEnvironmentVariables')
    FirstViewport = @('warmupTrials', 'trials', 'samplesMilliseconds')
    FirstViewportTrial = @('samplesMilliseconds')
    Scroll = @('trials')
    ScrollTrial = @(
        'uiThreadWorkElapsedTicks', 'frameIntervalElapsedTicks',
        'rendererOwnedAllocatedBytesPerFrame')
    SourceLookup = @('samplesElapsedTicks', 'regressionTrials')
    SourceLookupTrial = @('samplesElapsedTicks', 'samplesNanosecondsPerLookup')
    Cancellation = @('trials', 'samplesMilliseconds')
    CancellationTrial = @('samplesMilliseconds')
    Regression = @('comparisons')
}

$jsonPrimitiveArrayElementKinds = @{
    Report = @{ failures = 'String' }
    RuntimeConfiguration = @{ overrideEnvironmentVariables = 'String' }
    FirstViewport = @{ samplesMilliseconds = 'Number' }
    FirstViewportTrial = @{ samplesMilliseconds = 'Number' }
    ScrollTrial = @{
        uiThreadWorkElapsedTicks = 'Int64'
        frameIntervalElapsedTicks = 'Int64'
        rendererOwnedAllocatedBytesPerFrame = 'Int64'
    }
    SourceLookup = @{ samplesElapsedTicks = 'Int64' }
    SourceLookupTrial = @{
        samplesElapsedTicks = 'Int64'
        samplesNanosecondsPerLookup = 'Number'
    }
    Cancellation = @{ samplesMilliseconds = 'Number' }
    CancellationTrial = @{ samplesMilliseconds = 'Number' }
}

$jsonInt32Properties = @{
    Report = @('schemaVersion')
    Machine = @('logicalProcessorCount')
    SampleRequirements = @(
        'firstViewportIterationsRequired', 'firstViewportTrialsRequired',
        'firstViewportWarmupTrialsRequired', 'scrollFramesRequired', 'scrollTrialsRequired',
        'lifecycleCyclesRequired', 'cancellationIterationsPerTrialRequired',
        'cancellationTrialsRequired', 'sourceLookupMappingCountRequired',
        'sourceLookupAbsoluteQueryCountRequired', 'sourceLookupRegressionWarmupPassesRequired',
        'sourceLookupRegressionTrialsRequired', 'sourceLookupRegressionBatchSizeRequired',
        'sourceLookupRegressionObservationCountRequired',
        'sourceLookupRegressionOperationCountRequired')
    FirstViewport = @(
        'sourceUtf16Bytes', 'measurementProcessorGroup', 'measurementProcessorNumber')
    FirstViewportTrial = @(
        'ordinal', 'globalOrdinal', 'scheduleRow', 'schedulePosition', 'gen0Collections',
        'gen1Collections', 'gen2Collections', 'completedParseCountBeforePrime',
        'completedParseCountAfterPrime', 'completedParseCountAfterTrial')
    Scroll = @(
        'sourceUtf16Bytes', 'requestedFrames', 'measuredFrameIntervals',
        'framesWithRendererWork', 'gen2Collections')
    ScrollTrial = @(
        'ordinal', 'requestedFrames', 'measuredFrameIntervals',
        'framesWithRendererWork', 'gen2Collections')
    RetainedMemory = @('sourceUtf16Bytes')
    Lifecycle = @(
        'completedCycles', 'requiredCycles', 'syntheticDeviceResets',
        'actualDeviceLossEvents', 'renderFailures')
    SourceLookup = @(
        'mappingCount', 'queryCount', 'measurementProcessorGroup', 'measurementProcessorNumber',
        'regressionWarmupPasses', 'regressionBatchSize', 'regressionObservationCount',
        'regressionOperationCount', 'regressionTrialCount')
    SourceLookupTrial = @('ordinal', 'observationCount', 'operationCount')
    Cancellation = @(
        'requestedIterations', 'iterationsPerTrial', 'warmupIterations',
        'warmupStaleCommits', 'observedCancellations', 'staleCommits')
    CancellationTrial = @(
        'ordinal', 'warmupSourceSeed', 'warmupStaleCommits', 'firstRecordedSourceSeed',
        'requestedIterations', 'observedCancellations', 'staleCommits')
    Regression = @('comparedLatencyMetrics', 'comparedAllocationMetrics')
}

$jsonInt64Properties = @{
    FirstViewport = @('parseCacheBudgetBytes')
    FirstViewportTrial = @(
        'processAllocatedBytes', 'sourceKeyHashCountBeforePrime',
        'sourceKeyHashCountAfterPrime', 'sourceKeyHashCountAfterTrial')
    Scroll = @(
        'stopwatchFrequency', 'maximumAllocatedBytesPerFrame',
        'maximumRendererOwnedAllocatedBytesPerFrame',
        'maximumPlatformProjectionAllocatedBytesPerFrame', 'allocationBudgetBytesPerFrame',
        'maximumRealizedCodeActions', 'maximumOffscreenRealizedCodeActions')
    ScrollTrial = @(
        'firstLogicalFrameId', 'maximumAllocatedBytesPerFrame',
        'maximumRendererOwnedAllocatedBytesPerFrame',
        'maximumPlatformProjectionAllocatedBytesPerFrame', 'maximumRealizedCodeActions',
        'maximumOffscreenRealizedCodeActions')
    RetainedMemory = @('managedDeltaBytes', 'privateDeltaBytes', 'budgetBytes')
    Lifecycle = @(
        'checkpoint80ManagedBytes', 'checkpoint100ManagedBytes',
        'checkpoint80PrivateBytes', 'checkpoint100PrivateBytes')
    SourceLookup = @('stopwatchFrequency', 'allocatedBytes', 'regressionAllocatedBytes')
    SourceLookupTrial = @('allocatedBytes')
}

$jsonStringProperties = @{
    Report = @('providerName', 'startedUtc', 'completedUtc', 'buildIdentity')
    BuildArtifacts = @('hashAlgorithm')
    Artifact = @('fileName', 'path', 'sha256')
    RuntimeConfiguration = @('policy')
    Machine = @(
        'machineInstanceSha256', 'cpu', 'osDescription', 'osVersion', 'osArchitecture',
        'processArchitecture', 'frameworkDescription', 'displayDeviceName', 'gpuAdapterLuid',
        'gpuAdapterIdentitySha256', 'gpuDriverVersion', 'displayAdapterIdentitySha256',
        'displayAdapterLuid', 'displayAdapterDriverVersion', 'activePowerSchemeGuid',
        'userConfiguredAcPowerModeGuid', 'userConfiguredDcPowerModeGuid',
        'powerSource', 'energySaverState', 'effectivePowerMode',
        'processPriorityClass', 'processPowerThrottlingMode', 'gcLatencyMode')
    FirstViewport = @(
        'corpus', 'mode', 'engineReuseMode', 'trialStartPolicy', 'schedulePolicy',
        'regressionEstimator', 'stationarityPolicy', 'measurementThreadAffinityMask',
        'measurementThreadPriority')
    FirstViewportTrial = @('startedUtc', 'completedUtc')
    Scroll = @('corpus', 'regressionEstimator', 'rawAllocationScope', 'allocationGateBasis',
        'platformProjectionAllocationScope')
    RetainedMemory = @('corpus')
    Lifecycle = @('deviceResetMode')
    SourceLookup = @(
        'measurementThreadAffinityMask', 'measurementThreadPriority', 'regressionEstimator')
    Cancellation = @('trialStartPolicy', 'regressionEstimator')
    Regression = @(
        'mode', 'referencePath', 'referenceReportSha256', 'referenceBuildIdentity', 'decisionPolicy')
    RegressionComparison = @('metric', 'kind', 'decision')
}

$jsonBooleanProperties = @{
    Report = @('isReleaseEvidence', 'passed')
    Machine = @('isAtLeast120Hz', 'gcServer')
    RuntimeConfiguration = @(
        'tieredCompilationEnabled', 'tieredPgoEnabled', 'concurrentGcEnabled',
        'readyToRunEnabled', 'passed')
    FirstViewport = @('stationarityEvaluated', 'stationarityPassed', 'passed')
    FirstViewportTrial = @('cacheProofPassed', 'complete')
    Scroll = @('lohAllocationPossible', 'rendererOwnedLohAllocationPossible', 'passed')
    ScrollTrial = @('complete')
    RetainedMemory = @('passed')
    Lifecycle = @('passed')
    SourceLookup = @('passed')
    SourceLookupTrial = @('complete')
    Cancellation = @('passed')
    CancellationTrial = @('complete')
    Regression = @('referenceEligible', 'machineComparable', 'passed')
    RegressionComparison = @('passed')
}

function Test-StrictJsonObjectContract {
    param(
        [Parameter(Mandatory)][Text.Json.JsonElement] $Element,
        [Parameter(Mandatory)][string] $Contract,
        [Parameter(Mandatory)][string] $Label
    )

    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        Add-Failure "$Label must be a JSON object."
        return
    }

    [string[]]$expectedNames = $jsonObjectContracts[$Contract]
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $expectedNames) { $null = $expected.Add($name) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenIgnoreCase = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $objectChildren = $jsonObjectChildren[$Contract]
    $arrayChildren = $jsonObjectArrayChildren[$Contract]
    [string[]]$arrayNames = @($jsonArrayProperties[$Contract])
    [string[]]$stringNames = @($jsonStringProperties[$Contract])
    [string[]]$booleanNames = @($jsonBooleanProperties[$Contract])
    [string[]]$int32Names = @($jsonInt32Properties[$Contract])
    [string[]]$int64Names = @($jsonInt64Properties[$Contract])
    $primitiveArrayKinds = $jsonPrimitiveArrayElementKinds[$Contract]

    foreach ($property in $Element.EnumerateObject()) {
        $name = [string]$property.Name
        if (-not $seen.Add($name) -or -not $seenIgnoreCase.Add($name)) {
            Add-Failure "$Label contains duplicate or case-colliding JSON property '$name'."
            continue
        }
        if (-not $expected.Contains($name)) {
            Add-Failure "$Label contains unmapped JSON property '$name'."
            continue
        }

        $value = $property.Value
        if ($null -ne $objectChildren -and $objectChildren.ContainsKey($name)) {
            if ($value.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
                Add-Failure "$Label.$name must be a JSON object."
            }
            else {
                Test-StrictJsonObjectContract `
                    -Element $value `
                    -Contract ([string]$objectChildren[$name]) `
                    -Label "$Label.$name"
            }
            continue
        }
        if ($arrayNames -contains $name) {
            if ($value.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
                Add-Failure "$Label.$name must be a JSON array."
            }
            elseif ($null -ne $arrayChildren -and $arrayChildren.ContainsKey($name)) {
                $index = 0
                foreach ($item in $value.EnumerateArray()) {
                    Test-StrictJsonObjectContract `
                        -Element $item `
                        -Contract ([string]$arrayChildren[$name]) `
                        -Label "$Label.$name[$index]"
                    $index++
                }
            }
            elseif ($null -ne $primitiveArrayKinds -and
                    $primitiveArrayKinds.ContainsKey($name)) {
                $expectedKind = [string]$primitiveArrayKinds[$name]
                $index = 0
                foreach ($item in $value.EnumerateArray()) {
                    if ($expectedKind -eq 'String') {
                        if ($item.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                            Add-Failure "$Label.$name[$index] must be a JSON string."
                        }
                    }
                    elseif ($expectedKind -eq 'Int64') {
                        [int64]$arrayInteger64 = 0
                        if ($item.ValueKind -ne [Text.Json.JsonValueKind]::Number -or
                            -not $item.TryGetInt64([ref]$arrayInteger64)) {
                            Add-Failure "$Label.$name[$index] must be a signed 64-bit JSON integer."
                        }
                    }
                    elseif ($item.ValueKind -ne [Text.Json.JsonValueKind]::Number) {
                        Add-Failure "$Label.$name[$index] must be a JSON number."
                    }
                    $index++
                }
            }
            continue
        }

        $nullableRegressionReference = $Contract -eq 'Regression' -and
            $name -in @('referencePath', 'referenceReportSha256', 'referenceBuildIdentity')
        if ($stringNames -contains $name) {
            if ($value.ValueKind -ne [Text.Json.JsonValueKind]::String -and
                -not ($nullableRegressionReference -and
                      $value.ValueKind -eq [Text.Json.JsonValueKind]::Null)) {
                Add-Failure "$Label.$name must be a JSON string."
            }
            elseif ($Contract -in @('Report', 'FirstViewportTrial') -and
                    $name -in @('startedUtc', 'completedUtc')) {
                try {
                    $timestamp = [DateTimeOffset]::Parse(
                        [string]$value.GetString(),
                        [Globalization.CultureInfo]::InvariantCulture,
                        [Globalization.DateTimeStyles]::RoundtripKind)
                    if ($timestamp -eq [DateTimeOffset]::MinValue -or
                        $timestamp.Offset -ne [TimeSpan]::Zero) {
                        Add-Failure "$Label.$name must be a non-default UTC timestamp."
                    }
                }
                catch {
                    Add-Failure "$Label.$name must be a non-default UTC timestamp."
                }
            }
        }
        elseif ($booleanNames -contains $name) {
            if ($value.ValueKind -notin @(
                [Text.Json.JsonValueKind]::True,
                [Text.Json.JsonValueKind]::False)) {
                Add-Failure "$Label.$name must be a JSON Boolean."
            }
        }
        elseif ($int32Names -contains $name) {
            [int]$integer32 = 0
            if ($value.ValueKind -ne [Text.Json.JsonValueKind]::Number -or
                -not $value.TryGetInt32([ref]$integer32)) {
                Add-Failure "$Label.$name must be a signed 32-bit JSON integer."
            }
        }
        elseif ($int64Names -contains $name) {
            [int64]$integer64 = 0
            if ($value.ValueKind -ne [Text.Json.JsonValueKind]::Number -or
                -not $value.TryGetInt64([ref]$integer64)) {
                Add-Failure "$Label.$name must be a signed 64-bit JSON integer."
            }
        }
        elseif ($value.ValueKind -ne [Text.Json.JsonValueKind]::Number) {
            Add-Failure "$Label.$name must be a JSON number."
        }
    }

    foreach ($name in $expectedNames) {
        if (-not $seen.Contains($name)) {
            Add-Failure "$Label lacks required JSON property '$name'."
        }
    }
}

function Test-StrictPerformanceReportJson {
    param(
        [Parameter(Mandatory)][byte[]] $Bytes,
        [Parameter(Mandatory)][string] $Label
    )

    $document = $null
    try {
        $json = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
        $document = [Text.Json.JsonDocument]::Parse($json)
        Test-StrictJsonObjectContract -Element $document.RootElement -Contract 'Report' -Label $Label
    }
    catch {
        Add-Failure "$Label is not strict UTF-8 JSON: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) { $document.Dispose() }
    }
}

Test-StrictPerformanceReportJson -Bytes $reportBytes -Label 'Report'
$candidateReportTimestamps = Get-ReportTimestampEvidence -Bytes $reportBytes

function Get-ValidatedScrollEvidence {
    param(
        [Parameter(Mandatory)][object] $Scroll,
        [Parameter(Mandatory)][string] $Label
    )

    $rootNumericNames = @(
        'regressionUiThreadWorkP95Milliseconds', 'regressionUiThreadWorkP99Milliseconds',
        'regressionFrameTimeP95Milliseconds', 'regressionRendererOwnedAllocatedBytesPerFrameP95',
        'uiThreadWorkP95Milliseconds', 'uiThreadWorkP99Milliseconds',
        'scrollCallbackWorkP95Milliseconds', 'paintCallbackWorkP95Milliseconds',
        'frameTimeP95Milliseconds', 'framesOver16_7Percent', 'maximumFrameStallMilliseconds',
        'allocatedBytesPerFrameP95', 'maximumAllocatedBytesPerFrame',
        'rendererOwnedAllocatedBytesPerFrameP95', 'maximumRendererOwnedAllocatedBytesPerFrame',
        'platformProjectionAllocatedBytesPerFrameP95', 'maximumPlatformProjectionAllocatedBytesPerFrame',
        'scrollAllocatedBytesPerFrameP95', 'paintAllocatedBytesPerFrameP95',
        'scrollLazyLayoutAllocatedBytesP95', 'scrollAdornerAllocatedBytesP95',
        'scrollRealizationAllocatedBytesP95', 'scrollHighlightingAllocatedBytesP95',
        'paintSchedulingAllocatedBytesP95', 'paintPlatformSessionAllocatedBytesP95',
        'snapshotPaintAllocatedBytesP95', 'interactivePaintAllocatedBytesP95',
        'inlinePaintAllocatedBytesP95', 'tablePaintAllocatedBytesP95', 'codePaintAllocatedBytesP95',
        'otherPaintAllocatedBytesP95', 'maximumRealizedCodeActions',
        'maximumOffscreenRealizedCodeActions', 'gen2Collections')
    $rootNames = @(
        'stopwatchFrequency', 'requestedFrames', 'measuredFrameIntervals', 'framesWithRendererWork',
        'regressionEstimator', 'trials', 'rendererOwnedLohAllocationPossible',
        'uiThreadWorkP95BudgetMilliseconds', 'uiThreadWorkP99BudgetMilliseconds',
        'frameTimeP95BudgetMilliseconds', 'framesOver16_7PercentBudget',
        'maximumFrameStallBudgetMilliseconds', 'allocationBudgetBytesPerFrame',
        'rawAllocationScope', 'allocationGateBasis', 'platformProjectionAllocationScope', 'passed') +
        $rootNumericNames
    foreach ($name in $rootNames) {
        if ($null -eq $Scroll.PSObject.Properties[$name]) {
            Add-Failure "$Label lacks '$name'."
            return $null
        }
    }

    foreach ($name in $rootNumericNames) {
        if (-not (Test-FiniteNumber $Scroll.$name) -or [double]$Scroll.$name -lt 0) {
            Add-Failure "$Label metric '$name' is missing, non-finite, or negative."
        }
    }
    if ([string]$Scroll.allocationGateBasis -cne 'renderer-owned-managed' -or
        [string]$Scroll.rawAllocationScope -cne
            'all managed allocations observed on the UI thread inside renderer scroll and paint callbacks' -or
        [string]$Scroll.platformProjectionAllocationScope -cne
            'managed projection allocations measured around WinUI and Win2D calls; retained separately from renderer-owned allocations') {
        Add-Failure "$Label allocation scopes or gate basis are invalid."
    }

    $frequency = [double]$Scroll.stopwatchFrequency
    $trials = @($Scroll.trials)
    if (-not (Test-FiniteNumber $frequency) -or
        [int64]$frequency -ne [Diagnostics.Stopwatch]::Frequency -or
        [int]$Scroll.requestedFrames -ne 12000 -or
        [int]$Scroll.measuredFrameIntervals -ne 12000 -or
        [int]$Scroll.framesWithRendererWork -ne 12000 -or
        [string]$Scroll.regressionEstimator -ne 'hodges-lehmann-of-five-trial-p95' -or
        $trials.Count -ne 5) {
        Add-Failure "$Label does not use the frozen five-trial raw-sample protocol."
        return $null
    }

    $trialNumericNames = @(
        'uiThreadWorkP95Milliseconds', 'uiThreadWorkP99Milliseconds',
        'scrollCallbackWorkP95Milliseconds', 'paintCallbackWorkP95Milliseconds',
        'frameTimeP95Milliseconds', 'framesOver16_7Percent', 'maximumFrameStallMilliseconds',
        'allocatedBytesPerFrameP95', 'maximumAllocatedBytesPerFrame',
        'rendererOwnedAllocatedBytesPerFrameP95', 'maximumRendererOwnedAllocatedBytesPerFrame',
        'platformProjectionAllocatedBytesPerFrameP95', 'maximumPlatformProjectionAllocatedBytesPerFrame',
        'scrollAllocatedBytesPerFrameP95', 'paintAllocatedBytesPerFrameP95',
        'scrollLazyLayoutAllocatedBytesP95', 'scrollAdornerAllocatedBytesP95',
        'scrollRealizationAllocatedBytesP95', 'scrollHighlightingAllocatedBytesP95',
        'paintSchedulingAllocatedBytesP95', 'paintPlatformSessionAllocatedBytesP95',
        'snapshotPaintAllocatedBytesP95', 'interactivePaintAllocatedBytesP95',
        'inlinePaintAllocatedBytesP95', 'tablePaintAllocatedBytesP95',
        'codePaintAllocatedBytesP95', 'otherPaintAllocatedBytesP95',
        'maximumRealizedCodeActions', 'maximumOffscreenRealizedCodeActions', 'gen2Collections')
    $allUiTicks = [Collections.Generic.List[int64]]::new(12000)
    $allFrameTicks = [Collections.Generic.List[int64]]::new(12000)
    $allRendererAllocations = [Collections.Generic.List[int64]]::new(12000)
    $trialUiP95 = @()
    $trialUiP99 = @()
    $trialFrameP95 = @()
    $trialAllocationP95 = @()
    $previousLastFrameId = 0L
    $millisecondsPerTick = 1000.0 / $frequency

    for ($trialIndex = 0; $trialIndex -lt 5; $trialIndex++) {
        $trial = $trials[$trialIndex]
        $requiredNames = @(
            'ordinal', 'firstLogicalFrameId', 'requestedFrames', 'measuredFrameIntervals',
            'framesWithRendererWork', 'uiThreadWorkElapsedTicks', 'frameIntervalElapsedTicks',
            'rendererOwnedAllocatedBytesPerFrame', 'complete') + $trialNumericNames
        $missing = $false
        foreach ($name in $requiredNames) {
            if ($null -eq $trial.PSObject.Properties[$name]) {
                Add-Failure "$Label trial $trialIndex lacks '$name'."
                $missing = $true
            }
        }
        if ($missing) { continue }

        foreach ($name in $trialNumericNames) {
            if (-not (Test-FiniteNumber $trial.$name) -or [double]$trial.$name -lt 0) {
                Add-Failure "$Label trial $trialIndex metric '$name' is invalid."
            }
        }

        $firstFrameId = [int64]$trial.firstLogicalFrameId
        if ([int]$trial.ordinal -ne $trialIndex -or
            [int]$trial.requestedFrames -ne 2400 -or
            [int]$trial.measuredFrameIntervals -ne 2400 -or
            [int]$trial.framesWithRendererWork -ne 2400 -or
            -not [bool]$trial.complete -or
            $firstFrameId -le $previousLastFrameId -or
            $firstFrameId -gt [int64]::MaxValue - 2399L -or
            [int64]$trial.maximumOffscreenRealizedCodeActions -ne 0 -or
            [int]$trial.gen2Collections -ne 0) {
            Add-Failure "$Label trial $trialIndex is incomplete, out of order, or violates release invariants."
        }
        if ($firstFrameId -le [int64]::MaxValue - 2399L) {
            $previousLastFrameId = $firstFrameId + 2399L
        }

        $uiTicks = @($trial.uiThreadWorkElapsedTicks)
        $frameTicks = @($trial.frameIntervalElapsedTicks)
        $rendererAllocations = @($trial.rendererOwnedAllocatedBytesPerFrame)
        $rawValid = $uiTicks.Count -eq 2400 -and
            $frameTicks.Count -eq 2400 -and
            $rendererAllocations.Count -eq 2400
        if (-not $rawValid) {
            Add-Failure "$Label trial $trialIndex must contain exactly 2,400 samples in each raw array."
            continue
        }
        for ($sampleIndex = 0; $sampleIndex -lt 2400; $sampleIndex++) {
            $uiValue = $uiTicks[$sampleIndex]
            $frameValue = $frameTicks[$sampleIndex]
            $allocationValue = $rendererAllocations[$sampleIndex]
            if (-not (Test-FiniteNumber $uiValue) -or [double]$uiValue -lt 0 -or
                [double]$uiValue -gt [int64]::MaxValue -or
                [double]$uiValue -ne [double][int64]$uiValue -or
                -not (Test-FiniteNumber $frameValue) -or [double]$frameValue -le 0 -or
                [double]$frameValue -gt [int64]::MaxValue -or
                [double]$frameValue -ne [double][int64]$frameValue -or
                -not (Test-FiniteNumber $allocationValue) -or [double]$allocationValue -lt 0 -or
                [double]$allocationValue -gt [int64]::MaxValue -or
                [double]$allocationValue -ne [double][int64]$allocationValue) {
                Add-Failure "$Label trial $trialIndex has an invalid raw sample at index $sampleIndex."
                $rawValid = $false
                break
            }
        }
        if (-not $rawValid) { continue }

        $computedUiP95 = (Get-NearestRankPercentile -Values $uiTicks -Percentile 0.95) * $millisecondsPerTick
        $computedUiP99 = (Get-NearestRankPercentile -Values $uiTicks -Percentile 0.99) * $millisecondsPerTick
        $computedFrameP95 = (Get-NearestRankPercentile -Values $frameTicks -Percentile 0.95) * $millisecondsPerTick
        $computedFramesOver = @($frameTicks | Where-Object {
            [double]$_ * $millisecondsPerTick -gt 16.7
        }).Count * 100.0 / 2400.0
        $computedMaximumStall = ([double]($frameTicks | Measure-Object -Maximum).Maximum) * $millisecondsPerTick
        $computedAllocationP95 = Get-NearestRankPercentile -Values $rendererAllocations -Percentile 0.95
        $computedMaximumAllocation = [int64]($rendererAllocations | Measure-Object -Maximum).Maximum
        $computedTrialValues = @{
            uiThreadWorkP95Milliseconds = $computedUiP95
            uiThreadWorkP99Milliseconds = $computedUiP99
            frameTimeP95Milliseconds = $computedFrameP95
            framesOver16_7Percent = $computedFramesOver
            maximumFrameStallMilliseconds = $computedMaximumStall
            rendererOwnedAllocatedBytesPerFrameP95 = $computedAllocationP95
        }
        foreach ($name in $computedTrialValues.Keys) {
            if (-not (Test-NearlyEqual ([double]$trial.$name) ([double]$computedTrialValues[$name]))) {
                Add-Failure "$Label trial $trialIndex metric '$name' is not reproducible from raw evidence."
            }
        }
        if ([int64]$trial.maximumRendererOwnedAllocatedBytesPerFrame -ne $computedMaximumAllocation) {
            Add-Failure "$Label trial $trialIndex maximum renderer allocation is not reproducible."
        }

        $trialUiP95 += $computedUiP95
        $trialUiP99 += $computedUiP99
        $trialFrameP95 += $computedFrameP95
        $trialAllocationP95 += $computedAllocationP95
        $allUiTicks.AddRange([int64[]]$uiTicks)
        $allFrameTicks.AddRange([int64[]]$frameTicks)
        $allRendererAllocations.AddRange([int64[]]$rendererAllocations)
    }

    if ($trialUiP95.Count -ne 5 -or $allUiTicks.Count -ne 12000 -or
        $allFrameTicks.Count -ne 12000 -or $allRendererAllocations.Count -ne 12000) {
        Add-Failure "$Label does not contain five independently reproducible raw trials."
        return $null
    }

    $pooledUiP95 = (Get-NearestRankPercentile -Values @($allUiTicks) -Percentile 0.95) * $millisecondsPerTick
    $pooledUiP99 = (Get-NearestRankPercentile -Values @($allUiTicks) -Percentile 0.99) * $millisecondsPerTick
    $pooledFrameP95 = (Get-NearestRankPercentile -Values @($allFrameTicks) -Percentile 0.95) * $millisecondsPerTick
    $pooledFramesOver = @($allFrameTicks | Where-Object {
        [double]$_ * $millisecondsPerTick -gt 16.7
    }).Count * 100.0 / 12000.0
    $pooledMaximumStall = ([double]($allFrameTicks | Measure-Object -Maximum).Maximum) * $millisecondsPerTick
    $pooledAllocationP95 = Get-NearestRankPercentile -Values @($allRendererAllocations) -Percentile 0.95
    $pooledMaximumAllocation = [int64]($allRendererAllocations | Measure-Object -Maximum).Maximum
    $aggregateValues = @{
        uiThreadWorkP95Milliseconds = $pooledUiP95
        uiThreadWorkP99Milliseconds = $pooledUiP99
        frameTimeP95Milliseconds = $pooledFrameP95
        framesOver16_7Percent = $pooledFramesOver
        maximumFrameStallMilliseconds = $pooledMaximumStall
        rendererOwnedAllocatedBytesPerFrameP95 = $pooledAllocationP95
    }
    foreach ($name in $aggregateValues.Keys) {
        if (-not (Test-NearlyEqual ([double]$Scroll.$name) ([double]$aggregateValues[$name]))) {
            Add-Failure "$Label pooled metric '$name' is not reproducible from raw evidence."
        }
    }
    if ([int64]$Scroll.maximumRendererOwnedAllocatedBytesPerFrame -ne $pooledMaximumAllocation -or
        [bool]$Scroll.rendererOwnedLohAllocationPossible -ne ($pooledMaximumAllocation -ge 85000)) {
        Add-Failure "$Label pooled maximum allocation or LOH flag is not reproducible."
    }

    $regressionValues = @{
        regressionUiThreadWorkP95Milliseconds = Get-HodgesLehmann -Values $trialUiP95
        regressionUiThreadWorkP99Milliseconds = Get-HodgesLehmann -Values $trialUiP99
        regressionFrameTimeP95Milliseconds = Get-HodgesLehmann -Values $trialFrameP95
        regressionRendererOwnedAllocatedBytesPerFrameP95 = Get-HodgesLehmann -Values $trialAllocationP95
    }
    foreach ($name in $regressionValues.Keys) {
        if (-not (Test-NearlyEqual ([double]$Scroll.$name) ([double]$regressionValues[$name]))) {
            Add-Failure "$Label Hodges-Lehmann estimator '$name' is not reproducible from raw evidence."
        }
    }

    $dispersions = @{
        uiP95 = Get-RobustDispersionPercent -Values $trialUiP95
        uiP99 = Get-RobustDispersionPercent -Values $trialUiP99
        frameP95 = Get-RobustDispersionPercent -Values $trialFrameP95
        allocationP95 = Get-RobustDispersionPercent -Values $trialAllocationP95
    }
    if ([double]$dispersions.uiP95 -gt 25 -or [double]$dispersions.uiP99 -gt 25 -or
        [double]$dispersions.frameP95 -gt 35 -or [double]$dispersions.allocationP95 -gt 25) {
        Add-Failure "$Label trial dispersion exceeds the frozen stability caps."
    }
    if ([double]$Scroll.uiThreadWorkP95BudgetMilliseconds -ne 4 -or
        [double]$Scroll.uiThreadWorkP99BudgetMilliseconds -ne 6 -or
        [double]$Scroll.frameTimeP95BudgetMilliseconds -ne
            $script:ScrollFrameTimeP95BudgetMilliseconds -or
        [double]$Scroll.framesOver16_7PercentBudget -ne 1 -or
        [double]$Scroll.maximumFrameStallBudgetMilliseconds -ne 50 -or
        [int64]$Scroll.allocationBudgetBytesPerFrame -ne 4096 -or
        $pooledUiP95 -gt 4 -or $pooledUiP99 -gt 6 -or
        $pooledFrameP95 -gt $script:ScrollFrameTimeP95BudgetMilliseconds -or
        $pooledFramesOver -ge 1 -or
        $pooledMaximumStall -gt 50 -or $pooledMaximumAllocation -gt 4096 -or
        $pooledMaximumAllocation -ge 85000 -or
        [int64]$Scroll.maximumOffscreenRealizedCodeActions -ne 0 -or
        [int]$Scroll.gen2Collections -ne 0 -or -not [bool]$Scroll.passed) {
        Add-Failure "$Label failed a frozen absolute budget or release invariant."
    }

    return [pscustomobject]@{
        TrialUiP95 = $trialUiP95
        TrialUiP99 = $trialUiP99
        TrialFrameP95 = $trialFrameP95
        TrialAllocationP95 = $trialAllocationP95
        RegressionUiP95 = [double]$regressionValues.regressionUiThreadWorkP95Milliseconds
        RegressionUiP99 = [double]$regressionValues.regressionUiThreadWorkP99Milliseconds
        RegressionFrameP95 = [double]$regressionValues.regressionFrameTimeP95Milliseconds
        RegressionAllocationP95 = [double]$regressionValues.regressionRendererOwnedAllocatedBytesPerFrameP95
        UiP95Dispersion = [double]$dispersions.uiP95
        UiP99Dispersion = [double]$dispersions.uiP99
        FrameP95Dispersion = [double]$dispersions.frameP95
        AllocationP95Dispersion = [double]$dispersions.allocationP95
        PooledUiP95 = $pooledUiP95
        PooledUiP99 = $pooledUiP99
        PooledFrameP95 = $pooledFrameP95
        PooledFramesOver = $pooledFramesOver
        PooledMaximumStall = $pooledMaximumStall
        PooledAllocationP95 = $pooledAllocationP95
        PooledMaximumAllocation = $pooledMaximumAllocation
        ObservedRefreshRateHz = $frequency / (
            Get-NearestRankPercentile -Values @($allFrameTicks) -Percentile 0.50)
    }
}

function Get-ValidatedCancellationEvidence {
    param(
        [Parameter(Mandatory)][object] $Cancellation,
        [Parameter(Mandatory)][string] $Label
    )

    $rootNames = @(
        'requestedIterations', 'iterationsPerTrial', 'warmupIterations',
        'warmupElapsedMilliseconds', 'warmupStaleCommits', 'observedCancellations',
        'trialStartPolicy', 'regressionEstimator', 'trials', 'samplesMilliseconds',
        'p95Milliseconds', 'regressionP95Milliseconds', 'p95BudgetMilliseconds',
        'staleCommits', 'passed')
    foreach ($name in $rootNames) {
        if ($null -eq $Cancellation.PSObject.Properties[$name]) {
            Add-Failure "$Label lacks '$name'."
            return $null
        }
    }

    $trials = @($Cancellation.trials)
    $pooledSamples = @($Cancellation.samplesMilliseconds)
    if ([int]$Cancellation.iterationsPerTrial -ne 40 -or
        [int]$Cancellation.requestedIterations -ne 200 -or
        [int]$Cancellation.observedCancellations -ne 200 -or
        [int]$Cancellation.warmupIterations -ne 5 -or
        [string]$Cancellation.trialStartPolicy -ne
            'managed-full-collection-then-one-distinct-warmup-per-trial' -or
        [string]$Cancellation.regressionEstimator -ne 'hodges-lehmann-of-five-trial-p95' -or
        $trials.Count -ne 5 -or $pooledSamples.Count -ne 200) {
        Add-Failure "$Label does not use the frozen five-by-forty cancellation protocol."
        return $null
    }

    $flattenedSamples = [Collections.Generic.List[double]]::new(200)
    $trialP95Values = @()
    $warmupSamples = @()
    $computedWarmupStaleCommits = 0L
    $computedStaleCommits = 0L
    for ($trialIndex = 0; $trialIndex -lt 5; $trialIndex++) {
        $trial = $trials[$trialIndex]
        $trialNames = @(
            'ordinal', 'warmupSourceSeed', 'warmupElapsedMilliseconds', 'warmupStaleCommits',
            'firstRecordedSourceSeed', 'requestedIterations', 'observedCancellations',
            'samplesMilliseconds', 'p95Milliseconds', 'staleCommits', 'complete')
        $missing = $false
        foreach ($name in $trialNames) {
            if ($null -eq $trial.PSObject.Properties[$name]) {
                Add-Failure "$Label trial $trialIndex lacks '$name'."
                $missing = $true
            }
        }
        if ($missing) { continue }

        $samples = @($trial.samplesMilliseconds)
        $expectedWarmupSeed = 4999 - $trialIndex
        $expectedFirstRecordedSeed = 5000 + $trialIndex * 40
        if ([int]$trial.ordinal -ne $trialIndex -or
            [int]$trial.warmupSourceSeed -ne $expectedWarmupSeed -or
            [int]$trial.firstRecordedSourceSeed -ne $expectedFirstRecordedSeed -or
            [int]$trial.requestedIterations -ne 40 -or
            [int]$trial.observedCancellations -ne 40 -or
            $samples.Count -ne 40 -or
            -not (Test-FiniteNumber $trial.warmupElapsedMilliseconds) -or
            [double]$trial.warmupElapsedMilliseconds -lt 0 -or
            [double]$trial.warmupElapsedMilliseconds -gt 16 -or
            [int]$trial.warmupStaleCommits -ne 0 -or
            [int]$trial.staleCommits -ne 0 -or
            -not [bool]$trial.complete) {
            Add-Failure "$Label trial $trialIndex violates its frozen plan, population, warmup, or stale-publication contract."
        }

        $samplesValid = $samples.Count -eq 40
        foreach ($sample in $samples) {
            if (-not (Test-FiniteNumber $sample) -or [double]$sample -lt 0) {
                Add-Failure "$Label trial $trialIndex contains an invalid sample."
                $samplesValid = $false
            }
            else {
                $flattenedSamples.Add([double]$sample)
            }
        }
        if ($samplesValid) {
            $trialP95 = Get-NearestRankPercentile -Values $samples -Percentile 0.95
            if (-not (Test-NearlyEqual ([double]$trial.p95Milliseconds) $trialP95) -or
                $trialP95 -gt 16) {
                Add-Failure "$Label trial $trialIndex p95 is not reproducible or exceeds 16 ms."
            }
            $trialP95Values += $trialP95
        }
        $warmupSamples += [double]$trial.warmupElapsedMilliseconds
        $computedWarmupStaleCommits += [int64]$trial.warmupStaleCommits
        $computedStaleCommits += [int64]$trial.staleCommits
    }

    if ($trialP95Values.Count -ne 5 -or $flattenedSamples.Count -ne 200) {
        Add-Failure "$Label does not contain five independently reproducible trial populations."
        return $null
    }
    foreach ($sample in $pooledSamples) {
        if (-not (Test-FiniteNumber $sample) -or [double]$sample -lt 0) {
            Add-Failure "$Label pooled sample population contains an invalid value."
            return $null
        }
    }
    for ($sampleIndex = 0; $sampleIndex -lt 200; $sampleIndex++) {
        if ([double]$pooledSamples[$sampleIndex] -ne $flattenedSamples[$sampleIndex]) {
            Add-Failure "$Label pooled samples are not the exact ordered trial concatenation."
            break
        }
    }

    $computedWarmupP95 = Get-NearestRankPercentile -Values $warmupSamples -Percentile 0.95
    $computedPooledP95 = Get-NearestRankPercentile -Values $pooledSamples -Percentile 0.95
    $computedRegressionP95 = Get-HodgesLehmann -Values $trialP95Values
    $computedDispersion = Get-RobustDispersionPercent -Values $trialP95Values
    if (-not (Test-NearlyEqual ([double]$Cancellation.warmupElapsedMilliseconds) $computedWarmupP95) -or
        -not (Test-NearlyEqual ([double]$Cancellation.p95Milliseconds) $computedPooledP95) -or
        -not (Test-NearlyEqual ([double]$Cancellation.regressionP95Milliseconds) $computedRegressionP95) -or
        [int64]$Cancellation.warmupStaleCommits -ne $computedWarmupStaleCommits -or
        [int64]$Cancellation.staleCommits -ne $computedStaleCommits) {
        Add-Failure "$Label aggregate warmup, samples, p95, Hodges-Lehmann estimate, or stale counts are not reproducible."
    }
    if ($computedWarmupP95 -gt 16 -or $computedPooledP95 -gt 16 -or
        [double]$Cancellation.p95BudgetMilliseconds -ne 16 -or
        $computedWarmupStaleCommits -ne 0 -or $computedStaleCommits -ne 0 -or
        $computedDispersion -gt 25 -or -not [bool]$Cancellation.passed) {
        Add-Failure "$Label exceeded its absolute budget, dispersion cap, or stale-publication gate."
    }

    return [pscustomobject]@{
        TrialP95 = $trialP95Values
        RegressionP95 = $computedRegressionP95
        Dispersion = $computedDispersion
        PooledP95 = $computedPooledP95
        WarmupP95 = $computedWarmupP95
    }
}

if ([int]$report.schemaVersion -ne 10) { Add-Failure 'Schema version must be 10.' }
if ([string]$report.providerName -ne 'MarkdownRenderer-Performance') { Add-Failure 'Unexpected provider name.' }
if (-not [bool]$report.isReleaseEvidence) { Add-Failure 'Quick/non-release reports cannot satisfy release gates.' }
if (-not (Test-ReportTimestamps `
        -StartedUtcText $candidateReportTimestamps.StartedUtc `
        -CompletedUtcText $candidateReportTimestamps.CompletedUtc)) {
    Add-Failure 'Report timestamps are missing, invalid, or non-increasing.'
}
if (-not (Test-DeploymentIdentity $report.buildIdentity)) {
    Add-Failure 'Build identity must use runtime-output-manifest-v1 with an uppercase SHA-256.'
}
if (@($report.failures).Count -ne 0) { Add-Failure 'The harness reported one or more failures.' }

if ([string]$report.buildArtifacts.hashAlgorithm -ne 'SHA-256') {
    Add-Failure 'Build artifact hash algorithm must be SHA-256.'
}
$artifactExpectations = @(
    @('executable', 'MarkdownRenderer.PerformanceHarness.exe'),
    @('performanceHarnessDll', 'MarkdownRenderer.PerformanceHarness.dll'),
    @('markdownRendererDll', 'MarkdownRenderer.dll'),
    @('markdownRendererCoreDll', 'MarkdownRenderer.Core.dll'),
    @('runtimeConfig', 'MarkdownRenderer.PerformanceHarness.runtimeconfig.json')
)
foreach ($expectation in $artifactExpectations) {
    $artifact = $report.buildArtifacts.($expectation[0])
    if ($null -eq $artifact) {
        Add-Failure "Build artifact '$($expectation[0])' is missing."
        continue
    }
    foreach ($name in @('fileName', 'path', 'sha256')) {
        if ($null -eq $artifact.PSObject.Properties[$name]) {
            Add-Failure "Build artifact '$($expectation[0])' lacks '$name'."
        }
    }
    if ([string]$artifact.fileName -ne $expectation[1] -or
        -not [IO.Path]::IsPathFullyQualified([string]$artifact.path) -or
        [string]::Compare(
            [IO.Path]::GetFileName([string]$artifact.path),
            $expectation[1],
            [StringComparison]::OrdinalIgnoreCase) -ne 0 -or
        -not (Test-Path -LiteralPath ([string]$artifact.path) -PathType Leaf) -or
        -not (Test-Sha256 $artifact.sha256)) {
        Add-Failure "Build artifact '$($expectation[0])' metadata is invalid."
        continue
    }
    $actualHash = (Get-FileHash -LiteralPath ([string]$artifact.path) -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, [string]$artifact.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure "Build artifact '$($expectation[0])' SHA-256 does not match its file."
    }
}
$currentExecutablePath = [string]$report.buildArtifacts.executable.path
try {
    if (Test-PathWithinDeploymentRoot `
            -Path $ReportPath `
            -ExecutablePath $currentExecutablePath) {
        Add-Failure 'The current report path must be outside the executable deployment root.'
    }
    $boundReferencePath = [string]$report.regression.referencePath
    if (-not [string]::IsNullOrWhiteSpace($boundReferencePath) -and
        (Test-PathWithinDeploymentRoot `
            -Path $boundReferencePath `
            -ExecutablePath $currentExecutablePath)) {
        Add-Failure 'The reference report path must be outside the executable deployment root.'
    }

    $currentDeploymentIdentity = Get-DeploymentIdentity `
        -ExecutablePath $currentExecutablePath
    if ([string]$report.buildIdentity -cne $currentDeploymentIdentity) {
        Add-Failure 'The current runtime output does not match the captured deployment identity.'
    }
}
catch {
    Add-Failure "The current runtime output identity could not be verified: $($_.Exception.Message)"
}
if (Test-RuntimeConfigurationEvidence `
        -RuntimeConfiguration $report.runtimeConfiguration `
        -Label 'Runtime configuration evidence') {
    $null = Test-RuntimeConfigArtifact `
        -Artifact $report.buildArtifacts.runtimeConfig `
        -Label 'Runtime configuration artifact'
}

$stringMetadata = @(
    'cpu', 'osDescription', 'osVersion', 'osArchitecture', 'processArchitecture',
    'frameworkDescription', 'displayDeviceName')
foreach ($name in $stringMetadata) {
    if ([string]::IsNullOrWhiteSpace([string]$report.machine.$name)) {
        Add-Failure "Machine metadata '$name' is empty."
    }
}
foreach ($name in @('logicalProcessorCount', 'dpiScale', 'viewportWidthDips', 'viewportHeightDips', 'configuredRefreshRateHz', 'observedRefreshRateHz')) {
    $value = $report.machine.$name
    if (-not (Test-FiniteNumber $value) -or [double]$value -le 0) {
        Add-Failure "Machine metadata '$name' is not a finite positive value."
    }
}
if (-not [bool]$report.machine.isAtLeast120Hz -or
    [double]$report.machine.configuredRefreshRateHz -lt 119 -or
    [double]$report.machine.observedRefreshRateHz -lt
        [Math]::Max(115, [double]$report.machine.configuredRefreshRateHz * 0.95)) {
    Add-Failure 'The active display lacks configured and observed 120 Hz evidence.'
}
if (-not (Test-Sha256 $report.machine.machineInstanceSha256) -or
    [string]$report.machine.gpuAdapterLuid -notmatch '^[0-9A-Fa-f]{8}:[0-9A-Fa-f]{8}$' -or
    -not (Test-Sha256 $report.machine.gpuAdapterIdentitySha256) -or
    [string]::IsNullOrWhiteSpace([string]$report.machine.gpuDriverVersion) -or
    -not (Test-Sha256 $report.machine.displayAdapterIdentitySha256) -or
    [string]$report.machine.displayAdapterLuid -notmatch '^[0-9A-Fa-f]{8}:[0-9A-Fa-f]{8}$' -or
    [string]::IsNullOrWhiteSpace([string]$report.machine.displayAdapterDriverVersion) -or
    -not (Test-CanonicalGuid $report.machine.activePowerSchemeGuid) -or
    -not (Test-PowerModePair `
        $report.machine.userConfiguredAcPowerModeGuid `
        $report.machine.userConfiguredDcPowerModeGuid) -or
    [string]$report.machine.powerSource -cne 'AC' -or
    [string]$report.machine.energySaverState -cne 'Off' -or
    -not (Test-EffectivePowerMode $report.machine.effectivePowerMode) -or
    [string]$report.machine.processPriorityClass -cnotin
        @('Normal', 'Idle', 'High', 'RealTime', 'BelowNormal', 'AboveNormal') -or
    [string]$report.machine.processPowerThrottlingMode -cne
        'execution-speed=off;ignore-timer-resolution=off' -or
    $report.machine.gcServer -isnot [bool] -or
    [string]$report.machine.gcLatencyMode -cnotin
        @('Batch', 'Interactive', 'LowLatency', 'SustainedLowLatency', 'NoGCRegion')) {
    Add-Failure 'Machine fingerprint, GPU, power, priority, or GC metadata is invalid.'
}
if (-not (Test-EnvironmentSnapshotMetadata $report.completionMachine) -or
    -not (Test-SameRunEnvironment -Start $report.machine -Completion $report.completionMachine)) {
    Add-Failure 'The completion environment is incomplete or differs from the measurement start.'
}

if ([int]$report.sampleRequirements.firstViewportIterationsRequired -ne 100 -or
    [int]$report.sampleRequirements.firstViewportTrialsRequired -ne 6 -or
    [int]$report.sampleRequirements.firstViewportWarmupTrialsRequired -ne 3 -or
    [int]$report.sampleRequirements.scrollFramesRequired -ne 2400 -or
    [int]$report.sampleRequirements.scrollTrialsRequired -ne 5 -or
    [int]$report.sampleRequirements.lifecycleCyclesRequired -ne 100 -or
    [int]$report.sampleRequirements.cancellationIterationsPerTrialRequired -ne 40 -or
    [int]$report.sampleRequirements.cancellationTrialsRequired -ne 5 -or
    [int]$report.sampleRequirements.sourceLookupMappingCountRequired -ne 100000 -or
    [int]$report.sampleRequirements.sourceLookupAbsoluteQueryCountRequired -ne 20000 -or
    [int]$report.sampleRequirements.sourceLookupRegressionWarmupPassesRequired -ne 8 -or
    [int]$report.sampleRequirements.sourceLookupRegressionTrialsRequired -ne 5 -or
    [int]$report.sampleRequirements.sourceLookupRegressionBatchSizeRequired -ne 4096 -or
    [int]$report.sampleRequirements.sourceLookupRegressionObservationCountRequired -ne 128 -or
    [int]$report.sampleRequirements.sourceLookupRegressionOperationCountRequired -ne 524288) {
    Add-Failure 'Release sample requirements do not match the frozen gate.'
}

$expectedSizes = @((100 * 1024), (1024 * 1024), (10 * 1024 * 1024))
$candidateFirstViewport = Get-ValidatedFirstViewportEvidence `
    -Results @($report.firstUsableViewport) `
    -Label 'First-viewport evidence' `
    -ReportStartedUtc ([DateTimeOffset]$report.startedUtc) `
    -ReportCompletedUtc ([DateTimeOffset]$report.completedUtc)
$candidateFirstViewportEvidence = $candidateFirstViewport.Metrics

$scroll = $report.scroll
if ([string]$scroll.corpus -ne 'adversarial-nested-table-code-v1' -or
    [int]$scroll.sourceUtf16Bytes -ne 1024 * 1024) {
    Add-Failure 'Warm-scroll evidence did not use the frozen 1 MiB adversarial nested-list/table/code corpus.'
}
$scrollNumericNames = @(
    'regressionUiThreadWorkP95Milliseconds', 'regressionUiThreadWorkP99Milliseconds',
    'regressionFrameTimeP95Milliseconds', 'regressionRendererOwnedAllocatedBytesPerFrameP95',
    'uiThreadWorkP95Milliseconds', 'uiThreadWorkP99Milliseconds', 'scrollCallbackWorkP95Milliseconds',
    'paintCallbackWorkP95Milliseconds', 'frameTimeP95Milliseconds', 'framesOver16_7Percent',
    'maximumFrameStallMilliseconds', 'allocatedBytesPerFrameP95', 'maximumAllocatedBytesPerFrame',
    'rendererOwnedAllocatedBytesPerFrameP95', 'maximumRendererOwnedAllocatedBytesPerFrame',
    'platformProjectionAllocatedBytesPerFrameP95', 'maximumPlatformProjectionAllocatedBytesPerFrame',
    'scrollAllocatedBytesPerFrameP95', 'paintAllocatedBytesPerFrameP95',
    'scrollLazyLayoutAllocatedBytesP95', 'scrollAdornerAllocatedBytesP95',
    'scrollRealizationAllocatedBytesP95', 'scrollHighlightingAllocatedBytesP95',
    'paintSchedulingAllocatedBytesP95', 'paintPlatformSessionAllocatedBytesP95',
    'snapshotPaintAllocatedBytesP95', 'interactivePaintAllocatedBytesP95',
    'inlinePaintAllocatedBytesP95', 'tablePaintAllocatedBytesP95', 'codePaintAllocatedBytesP95',
    'otherPaintAllocatedBytesP95', 'maximumRealizedCodeActions', 'maximumOffscreenRealizedCodeActions',
    'gen2Collections'
)
foreach ($name in $scrollNumericNames) {
    if (-not (Test-FiniteNumber $scroll.$name) -or [double]$scroll.$name -lt 0) {
        Add-Failure "Scroll metric '$name' is missing, non-finite, or negative."
    }
}
$candidateScrollEvidence = Get-ValidatedScrollEvidence -Scroll $scroll -Label 'Warm-scroll evidence'
if ($null -ne $candidateScrollEvidence) {
    if (-not (Test-NearlyEqual `
            ([double]$report.machine.observedRefreshRateHz) `
            ([double]$candidateScrollEvidence.ObservedRefreshRateHz))) {
        Add-Failure 'Observed refresh rate is not reproducible from candidate raw frame evidence.'
    }
    if ($candidateScrollEvidence.PooledUiP95 -gt 4 -or
        $candidateScrollEvidence.PooledUiP99 -gt 6) {
        Add-Failure 'UI-thread p95/p99 work exceeded 4/6 ms.'
    }
    if ($candidateScrollEvidence.PooledFrameP95 -gt
        $script:ScrollFrameTimeP95BudgetMilliseconds) {
        Add-Failure 'Frame-time p95 exceeded the exact 120 Hz interval.'
    }
    if ($candidateScrollEvidence.PooledFramesOver -ge 1) {
        Add-Failure 'At least 1% of warm-scroll frames exceeded 16.7 ms.'
    }
    if ($candidateScrollEvidence.PooledMaximumStall -gt 50) {
        Add-Failure 'A renderer scroll stall exceeded 50 ms.'
    }
    if ($candidateScrollEvidence.PooledMaximumAllocation -gt 4096) {
        Add-Failure 'Renderer-owned allocation exceeded 4 KiB per frame.'
    }
}
if ([double]$scroll.uiThreadWorkP95BudgetMilliseconds -ne 4 -or
    [double]$scroll.uiThreadWorkP99BudgetMilliseconds -ne 6 -or
    [double]$scroll.frameTimeP95BudgetMilliseconds -ne
        $script:ScrollFrameTimeP95BudgetMilliseconds -or
    [double]$scroll.framesOver16_7PercentBudget -ne 1 -or
    [double]$scroll.maximumFrameStallBudgetMilliseconds -ne 50) {
    Add-Failure 'Serialized warm-scroll budgets do not match the frozen gate.'
}
if ([string]$scroll.allocationGateBasis -ne 'renderer-owned-managed' -or
    [string]::IsNullOrWhiteSpace([string]$scroll.rawAllocationScope) -or
    [string]::IsNullOrWhiteSpace([string]$scroll.platformProjectionAllocationScope)) { Add-Failure 'Allocation scopes/basis are incomplete.' }
if ([int64]$scroll.allocationBudgetBytesPerFrame -ne 4096 -or
    [int64]$scroll.maximumRendererOwnedAllocatedBytesPerFrame -gt 4096 -or
    [bool]$scroll.rendererOwnedLohAllocationPossible) { Add-Failure 'Renderer-owned allocation/LOH gate failed.' }
if ([int64]$scroll.maximumOffscreenRealizedCodeActions -ne 0) { Add-Failure 'Off-screen code actions were realized.' }
if ([int]$scroll.gen2Collections -ne 0) { Add-Failure 'A Gen2 collection occurred in the warm-scroll trace.' }
if (-not [bool]$scroll.passed) { Add-Failure 'The harness marked warm scroll as failed.' }

if (@($report.retainedMemory).Count -ne 3) { Add-Failure 'Retained-memory evidence must contain exactly three scenarios.' }
foreach ($sourceBytes in $expectedSizes) {
    $matches = @($report.retainedMemory | Where-Object { [int]$_.sourceUtf16Bytes -eq $sourceBytes })
    if ($matches.Count -ne 1) { Add-Failure "Retained-memory scenario $sourceBytes is missing or duplicated."; continue }
    $result = $matches[0]
    foreach ($name in @('corpus', 'sourceUtf16Bytes', 'managedDeltaBytes', 'privateDeltaBytes', 'budgetBytes', 'passed')) {
        if ($null -eq $result.PSObject.Properties[$name]) { Add-Failure "Retained-memory scenario $sourceBytes lacks '$name'." }
    }
    $expectedCorpus = if ($sourceBytes -eq 10 * 1024 * 1024) {
        'pathological-long-reference-token-v1'
    } else {
        'readme-mixed-v1'
    }
    $budget = [int64]$sourceBytes * 8 + 32L * 1024 * 1024
    if ([string]$result.corpus -ne $expectedCorpus -or
        [int64]$result.budgetBytes -ne $budget -or [int64]$result.managedDeltaBytes -lt 0 -or
        [int64]$result.privateDeltaBytes -lt 0 -or [int64]$result.managedDeltaBytes -gt $budget -or
        [int64]$result.privateDeltaBytes -gt $budget -or -not [bool]$result.passed) {
        Add-Failure "Retained-memory scenario $sourceBytes failed its 8x source + 32 MiB cap."
    }
}

$life = $report.lifecyclePlateau
foreach ($name in @('managedGrowthPercent', 'privateGrowthPercent', 'growthPercent')) {
    if (-not (Test-FiniteNumber $life.$name) -or [double]$life.$name -lt 0) { Add-Failure "Lifecycle metric '$name' is invalid." }
}
$computedManagedGrowth = Get-GrowthPercent `
    -Final ([int64]$life.checkpoint100ManagedBytes) `
    -Baseline ([int64]$life.checkpoint80ManagedBytes)
$computedPrivateGrowth = Get-GrowthPercent `
    -Final ([int64]$life.checkpoint100PrivateBytes) `
    -Baseline ([int64]$life.checkpoint80PrivateBytes)
$computedGrowth = [Math]::Max($computedManagedGrowth, $computedPrivateGrowth)
if ([int]$life.requiredCycles -ne 100 -or [int]$life.completedCycles -ne 100 -or
    [int]$life.syntheticDeviceResets -ne 4 -or
    [string]$life.deviceResetMode -cne 'snapshot-release-and-canvas-device-trim' -or
    [int]$life.actualDeviceLossEvents -lt 0 -or
    [int64]$life.checkpoint80ManagedBytes -le 0 -or [int64]$life.checkpoint100ManagedBytes -le 0 -or
    [int64]$life.checkpoint80PrivateBytes -le 0 -or [int64]$life.checkpoint100PrivateBytes -le 0 -or
    -not (Test-NearlyEqual ([double]$life.managedGrowthPercent) $computedManagedGrowth) -or
    -not (Test-NearlyEqual ([double]$life.privateGrowthPercent) $computedPrivateGrowth) -or
    -not (Test-NearlyEqual ([double]$life.growthPercent) $computedGrowth) -or
    [double]$life.growthBudgetPercent -ne 5 -or [double]$life.growthPercent -gt 5 -or
    [int]$life.renderFailures -ne 0 -or -not [bool]$life.passed) {
    Add-Failure 'The 100-cycle retained-memory/device-reset plateau gate failed.'
}

$lookup = $report.sourceLookup
$absoluteLookupTicks = @($lookup.samplesElapsedTicks)
$regressionLookupTrials = @($lookup.regressionTrials)
$lookupFrequency = [double]$lookup.stopwatchFrequency
if (-not (Test-PinnedProcessorEvidence `
        -AffinityMask $lookup.measurementThreadAffinityMask `
        -ProcessorGroup ([int]$lookup.measurementProcessorGroup) `
        -ProcessorNumber ([int]$lookup.measurementProcessorNumber) `
        -Priority $lookup.measurementThreadPriority `
        -ExpectedPriority 'Highest')) {
    Add-Failure 'Source-lookup execution metadata is invalid or does not identify one pinned highest-priority processor.'
}
if ([int]$lookup.mappingCount -ne 100000 -or [int]$lookup.queryCount -ne 20000 -or
    -not (Test-FiniteNumber $lookupFrequency) -or
    [int64]$lookupFrequency -ne [Diagnostics.Stopwatch]::Frequency -or
    $absoluteLookupTicks.Count -ne 20000 -or
    -not (Test-FiniteNumber $lookup.p95Nanoseconds) -or [double]$lookup.p95Nanoseconds -le 0 -or
    [double]$lookup.p95Nanoseconds -gt 25000 -or [double]$lookup.p95BudgetNanoseconds -ne 25000 -or
    [int64]$lookup.allocatedBytes -ne 0 -or
    [int]$lookup.regressionWarmupPasses -ne 8 -or
    [int]$lookup.regressionBatchSize -ne 4096 -or
    [int]$lookup.regressionObservationCount -ne 128 -or
    [int]$lookup.regressionOperationCount -ne 524288 -or
    [int]$lookup.regressionOperationCount -ne
        ([int]$lookup.regressionBatchSize * [int]$lookup.regressionObservationCount) -or
    [string]$lookup.regressionEstimator -ne 'hodges-lehmann-of-five-trial-p95' -or
    [int]$lookup.regressionTrialCount -ne 5 -or
    $regressionLookupTrials.Count -ne 5 -or
    -not (Test-FiniteNumber $lookup.regressionP95Nanoseconds) -or
    [double]$lookup.regressionP95Nanoseconds -le 0 -or
    [int64]$lookup.regressionAllocatedBytes -ne 0 -or -not [bool]$lookup.passed) {
    Add-Failure 'The 100,000-entry source-map lookup gate failed.'
}
foreach ($ticks in $absoluteLookupTicks) {
    if (-not (Test-FiniteNumber $ticks) -or [double]$ticks -lt 0 -or
        [double]$ticks -gt [int64]::MaxValue -or
        [double]$ticks -ne [double][int64]$ticks) {
        Add-Failure 'Absolute source-lookup evidence contains an invalid elapsed-tick sample.'
    }
}
if ($absoluteLookupTicks.Count -eq 20000 -and $lookupFrequency -gt 0) {
    $absoluteP95Ticks = Get-NearestRankPercentile -Values $absoluteLookupTicks -Percentile 0.95
    $computedAbsoluteP95 = $absoluteP95Ticks * 1000000000.0 / $lookupFrequency
    if (-not (Test-NearlyEqual ([double]$lookup.p95Nanoseconds) $computedAbsoluteP95)) {
        Add-Failure 'Absolute source-lookup p95 does not match its serialized elapsed ticks.'
    }
}
if ($null -ne $lookup.PSObject.Properties['regressionSamplesElapsedTicks'] -or
    $null -ne $lookup.PSObject.Properties['regressionSamplesNanosecondsPerLookup']) {
    Add-Failure 'Schema-10 source-lookup evidence must not contain legacy flat regression sample arrays.'
}
$candidateLookupEvidence = $null
$computedRegressionTrialP95 = @()
$computedRegressionAllocated = 0L
if ($regressionLookupTrials.Count -eq 5 -and $lookupFrequency -gt 0 -and
    [int]$lookup.regressionBatchSize -eq 4096) {
    for ($trialIndex = 0; $trialIndex -lt 5; $trialIndex++) {
        $trial = $regressionLookupTrials[$trialIndex]
        $trialComplete = $true
        foreach ($name in @(
            'ordinal', 'observationCount', 'operationCount', 'samplesElapsedTicks',
            'samplesNanosecondsPerLookup', 'p95Nanoseconds', 'allocatedBytes', 'complete')) {
            if ($null -eq $trial.PSObject.Properties[$name]) {
                Add-Failure "Source-lookup regression trial $trialIndex lacks '$name'."
                $trialComplete = $false
            }
        }
        if (-not $trialComplete) { continue }

        $trialTicks = @($trial.samplesElapsedTicks)
        $trialSamples = @($trial.samplesNanosecondsPerLookup)
        if ([int]$trial.ordinal -ne $trialIndex -or
            [int]$trial.observationCount -ne 128 -or
            [int]$trial.operationCount -ne 524288 -or
            [int]$trial.operationCount -ne ([int]$lookup.regressionBatchSize * [int]$trial.observationCount) -or
            $trialTicks.Count -ne 128 -or $trialSamples.Count -ne 128 -or
            [int64]$trial.allocatedBytes -ne 0 -or -not [bool]$trial.complete) {
            Add-Failure "Source-lookup regression trial $trialIndex does not match the frozen population."
            continue
        }

        for ($observation = 0; $observation -lt 128; $observation++) {
            $rawTicks = $trialTicks[$observation]
            $ticks = [int64]$rawTicks
            $sample = [double]$trialSamples[$observation]
            $computedSample = $ticks * 1000000000.0 / $lookupFrequency / 4096.0
            if (-not (Test-FiniteNumber $rawTicks) -or [double]$rawTicks -le 0 -or
                [double]$rawTicks -gt [int64]::MaxValue -or
                [double]$rawTicks -ne [double]$ticks -or
                -not (Test-FiniteNumber $sample) -or $sample -le 0 -or
                -not (Test-NearlyEqual $sample $computedSample)) {
                Add-Failure "Source-lookup regression trial $trialIndex observation $observation is not reproducible."
            }
        }

        $trialP95 = Get-NearestRankPercentile -Values $trialSamples -Percentile 0.95
        if (-not (Test-FiniteNumber $trial.p95Nanoseconds) -or
            [double]$trial.p95Nanoseconds -le 0 -or
            -not (Test-NearlyEqual ([double]$trial.p95Nanoseconds) $trialP95)) {
            Add-Failure "Source-lookup regression trial $trialIndex p95 is not reproducible."
        }
        $computedRegressionTrialP95 += $trialP95
        $computedRegressionAllocated += [int64]$trial.allocatedBytes
    }
}
if ($computedRegressionTrialP95.Count -eq 5) {
    $computedRegressionP95 = Get-HodgesLehmann -Values $computedRegressionTrialP95
    if (-not (Test-NearlyEqual ([double]$lookup.regressionP95Nanoseconds) $computedRegressionP95)) {
        Add-Failure 'Source-lookup regression p95 is not the Hodges-Lehmann estimate of five trial p95 values.'
    }
    $candidateLookupDispersion = Get-RobustDispersionPercent -Values $computedRegressionTrialP95
    if (-not (Test-FiniteNumber $candidateLookupDispersion) -or $candidateLookupDispersion -gt 20) {
        Add-Failure 'Source-lookup trial p95 dispersion exceeds 20%.'
    }
    if ([int64]$lookup.regressionAllocatedBytes -ne $computedRegressionAllocated) {
        Add-Failure 'Source-lookup regression allocation total does not equal the five trial totals.'
    }
    $candidateLookupEvidence = [pscustomobject]@{
        Value = $computedRegressionP95
        Trials = $computedRegressionTrialP95
        Dispersion = $candidateLookupDispersion
    }
}

$cancellation = $report.cancellation
$candidateCancellationEvidence =
    Get-ValidatedCancellationEvidence -Cancellation $cancellation -Label 'Cancellation evidence'

$regression = $report.regression
if ([double]$regression.latencyRegressionBudgetPercent -ne 5 -or
    [double]$regression.allocationRegressionBudgetPercent -ne 2 -or
    [string]$regression.decisionPolicy -ne
        'max-relative-percent-or-absolute-noise-floor-with-robust-dispersion' -or
    -not [bool]$regression.referenceEligible -or -not [bool]$regression.machineComparable -or
    -not [bool]$regression.passed) {
    Add-Failure 'Regression evidence is ineligible, incomparable, or failed the +5%/+2% policy.'
}
$mode = [string]$regression.mode
if ($RequiredRegressionMode -ne 'Any' -and $mode -ne $RequiredRegressionMode.ToLowerInvariant()) {
    Add-Failure "Regression mode '$mode' does not match required mode '$RequiredRegressionMode'."
}
if ($mode -eq 'baseline') {
    if ([int]$regression.comparedLatencyMetrics -ne 0 -or [int]$regression.comparedAllocationMetrics -ne 0 -or
        @($regression.comparisons).Count -ne 0) { Add-Failure 'A baseline report unexpectedly contains candidate comparisons.' }
    if (-not [string]::IsNullOrEmpty([string]$regression.referencePath) -or
        -not [string]::IsNullOrEmpty([string]$regression.referenceReportSha256) -or
        -not [string]::IsNullOrEmpty([string]$regression.referenceBuildIdentity)) {
        Add-Failure 'A baseline report unexpectedly binds a reference report.'
    }
}
elseif ($mode -eq 'candidate') {
    $referencePath = [string]$regression.referencePath
    $referenceReport = $null
    $expectedReferences = @{}
    $referenceTrialPopulations = @{}
    $referenceDispersions = @{}
    $referenceStationarity = @{}
    $referenceReportTimestamps = $null
    if (-not [IO.Path]::IsPathFullyQualified($referencePath) -or
        -not (Test-Path -LiteralPath $referencePath -PathType Leaf) -or
        -not (Test-Sha256 $regression.referenceReportSha256)) {
        Add-Failure 'Candidate reference-report binding is missing or invalid.'
    }
    else {
        try {
            $referenceEvidence = Read-PerformanceReportEvidence -Path $referencePath
            Test-StrictPerformanceReportJson `
                -Bytes $referenceEvidence.Bytes `
                -Label 'Reference report'
            if (-not [string]::Equals(
                [string]$referenceEvidence.Sha256,
                [string]$regression.referenceReportSha256,
                [StringComparison]::OrdinalIgnoreCase)) {
                Add-Failure 'Candidate reference-report SHA-256 does not match the parsed reference bytes.'
            }
            else {
                $referenceReport = $referenceEvidence.Report
                $referenceReportTimestamps = [pscustomobject]@{
                    StartedUtc = $referenceEvidence.StartedUtc
                    CompletedUtc = $referenceEvidence.CompletedUtc
                }
            }
        }
        catch {
            Add-Failure "Candidate reference report could not be read: $($_.Exception.Message)"
        }
    }

    if ($null -ne $referenceReport) {
        $referenceRequiredPaths = @(
            'schemaVersion', 'providerName', 'startedUtc', 'completedUtc', 'isReleaseEvidence',
            'passed', 'buildIdentity', 'runtimeConfiguration', 'completionMachine', 'failures',
            'buildArtifacts.hashAlgorithm', 'buildArtifacts.executable',
            'buildArtifacts.performanceHarnessDll', 'buildArtifacts.markdownRendererDll',
            'buildArtifacts.markdownRendererCoreDll', 'buildArtifacts.runtimeConfig',
            'buildArtifacts.executable.fileName', 'buildArtifacts.executable.path',
            'buildArtifacts.executable.sha256', 'buildArtifacts.performanceHarnessDll.fileName',
            'buildArtifacts.performanceHarnessDll.path', 'buildArtifacts.performanceHarnessDll.sha256',
            'buildArtifacts.markdownRendererDll.fileName', 'buildArtifacts.markdownRendererDll.path',
            'buildArtifacts.markdownRendererDll.sha256', 'buildArtifacts.markdownRendererCoreDll.fileName',
            'buildArtifacts.markdownRendererCoreDll.path', 'buildArtifacts.markdownRendererCoreDll.sha256',
            'buildArtifacts.runtimeConfig.fileName', 'buildArtifacts.runtimeConfig.path',
            'buildArtifacts.runtimeConfig.sha256',
            'runtimeConfiguration.policy', 'runtimeConfiguration.tieredCompilationEnabled',
            'runtimeConfiguration.tieredPgoEnabled', 'runtimeConfiguration.concurrentGcEnabled',
            'runtimeConfiguration.readyToRunEnabled',
            'runtimeConfiguration.overrideEnvironmentVariables', 'runtimeConfiguration.passed',
            'firstUsableViewport', 'scroll', 'retainedMemory', 'lifecyclePlateau',
            'sourceLookup', 'cancellation', 'sampleRequirements',
            'machine.machineInstanceSha256', 'machine.cpu', 'machine.logicalProcessorCount',
            'machine.osDescription', 'machine.osVersion',
            'machine.osArchitecture', 'machine.processArchitecture', 'machine.frameworkDescription',
            'machine.dpiScale', 'machine.viewportWidthDips', 'machine.viewportHeightDips',
            'machine.displayDeviceName',
            'machine.configuredRefreshRateHz', 'machine.observedRefreshRateHz', 'machine.isAtLeast120Hz',
            'machine.gpuAdapterLuid', 'machine.gpuAdapterIdentitySha256',
            'machine.gpuDriverVersion', 'machine.displayAdapterIdentitySha256',
            'machine.displayAdapterLuid', 'machine.displayAdapterDriverVersion',
            'machine.activePowerSchemeGuid',
            'machine.userConfiguredAcPowerModeGuid', 'machine.userConfiguredDcPowerModeGuid',
            'machine.powerSource', 'machine.energySaverState', 'machine.effectivePowerMode',
            'machine.processPriorityClass', 'machine.processPowerThrottlingMode',
            'machine.gcServer', 'machine.gcLatencyMode',
            'regression.mode', 'regression.decisionPolicy', 'regression.referencePath', 'regression.referenceReportSha256',
            'regression.referenceBuildIdentity', 'regression.referenceEligible',
            'regression.machineComparable', 'regression.comparedLatencyMetrics',
            'regression.comparedAllocationMetrics', 'regression.latencyRegressionBudgetPercent',
            'regression.allocationRegressionBudgetPercent', 'regression.comparisons', 'regression.passed',
            'sampleRequirements.firstViewportIterationsRequired',
            'sampleRequirements.firstViewportTrialsRequired',
            'sampleRequirements.firstViewportWarmupTrialsRequired',
            'sampleRequirements.scrollFramesRequired', 'sampleRequirements.scrollTrialsRequired',
            'sampleRequirements.cancellationIterationsPerTrialRequired',
            'sampleRequirements.cancellationTrialsRequired',
            'sampleRequirements.sourceLookupRegressionWarmupPassesRequired',
            'sampleRequirements.sourceLookupRegressionTrialsRequired',
            'sampleRequirements.sourceLookupRegressionBatchSizeRequired',
            'sampleRequirements.sourceLookupRegressionObservationCountRequired',
            'sampleRequirements.sourceLookupRegressionOperationCountRequired',
            'lifecyclePlateau.completedCycles', 'lifecyclePlateau.requiredCycles',
            'lifecyclePlateau.syntheticDeviceResets', 'lifecyclePlateau.deviceResetMode',
            'lifecyclePlateau.actualDeviceLossEvents', 'lifecyclePlateau.checkpoint80ManagedBytes',
            'lifecyclePlateau.checkpoint100ManagedBytes', 'lifecyclePlateau.checkpoint80PrivateBytes',
            'lifecyclePlateau.checkpoint100PrivateBytes', 'lifecyclePlateau.managedGrowthPercent',
            'lifecyclePlateau.privateGrowthPercent', 'lifecyclePlateau.growthPercent',
            'lifecyclePlateau.growthBudgetPercent', 'lifecyclePlateau.renderFailures',
            'lifecyclePlateau.passed',
            'scroll.corpus', 'scroll.sourceUtf16Bytes', 'scroll.stopwatchFrequency',
            'scroll.regressionEstimator', 'scroll.trials',
            'scroll.regressionUiThreadWorkP95Milliseconds',
            'scroll.regressionUiThreadWorkP99Milliseconds',
            'scroll.regressionFrameTimeP95Milliseconds',
            'scroll.regressionRendererOwnedAllocatedBytesPerFrameP95',
            'sourceLookup.mappingCount', 'sourceLookup.queryCount',
            'sourceLookup.stopwatchFrequency', 'sourceLookup.measurementThreadAffinityMask',
            'sourceLookup.measurementProcessorGroup', 'sourceLookup.measurementProcessorNumber',
            'sourceLookup.measurementThreadPriority', 'sourceLookup.samplesElapsedTicks',
            'sourceLookup.p95Nanoseconds', 'sourceLookup.p95BudgetNanoseconds',
            'sourceLookup.allocatedBytes', 'sourceLookup.regressionWarmupPasses',
            'sourceLookup.regressionBatchSize', 'sourceLookup.regressionObservationCount',
            'sourceLookup.regressionOperationCount', 'sourceLookup.regressionEstimator',
            'sourceLookup.regressionTrialCount', 'sourceLookup.regressionTrials',
            'sourceLookup.regressionP95Nanoseconds', 'sourceLookup.regressionAllocatedBytes',
            'sourceLookup.passed',
            'cancellation.requestedIterations', 'cancellation.iterationsPerTrial',
            'cancellation.warmupIterations', 'cancellation.warmupElapsedMilliseconds',
            'cancellation.warmupStaleCommits', 'cancellation.observedCancellations',
            'cancellation.trialStartPolicy', 'cancellation.regressionEstimator',
            'cancellation.trials', 'cancellation.samplesMilliseconds',
            'cancellation.p95Milliseconds', 'cancellation.regressionP95Milliseconds',
            'cancellation.p95BudgetMilliseconds', 'cancellation.staleCommits', 'cancellation.passed'
        )
        try {
            foreach ($path in $referenceRequiredPaths) {
                $null = Get-RequiredValue -Root $referenceReport -Path $path
            }
        }
        catch {
            Add-Failure "Candidate reference report is structurally incomplete: $($_.Exception.Message)"
            $referenceReport = $null
        }
    }

    if ($null -ne $referenceReport) {
        if ([int]$referenceReport.schemaVersion -ne 10 -or
            [string]$referenceReport.providerName -ne 'MarkdownRenderer-Performance' -or
            -not (Test-ReportTimestamps `
                -StartedUtcText $referenceReportTimestamps.StartedUtc `
                -CompletedUtcText $referenceReportTimestamps.CompletedUtc) -or
            -not [bool]$referenceReport.isReleaseEvidence -or
            -not [bool]$referenceReport.passed -or
            @($referenceReport.failures).Count -ne 0 -or
            -not (Test-DeploymentIdentity $referenceReport.buildIdentity) -or
            [string]$referenceReport.regression.mode -ne 'baseline' -or
            -not [bool]$referenceReport.regression.referenceEligible -or
            -not [bool]$referenceReport.regression.machineComparable -or
            -not [bool]$referenceReport.regression.passed -or
            [string]$referenceReport.regression.decisionPolicy -ne
                'max-relative-percent-or-absolute-noise-floor-with-robust-dispersion' -or
            [int]$referenceReport.regression.comparedLatencyMetrics -ne 0 -or
            [int]$referenceReport.regression.comparedAllocationMetrics -ne 0 -or
            [double]$referenceReport.regression.latencyRegressionBudgetPercent -ne 5 -or
            [double]$referenceReport.regression.allocationRegressionBudgetPercent -ne 2 -or
            @($referenceReport.regression.comparisons).Count -ne 0 -or
            -not [string]::IsNullOrEmpty([string]$referenceReport.regression.referencePath) -or
            -not [string]::IsNullOrEmpty([string]$referenceReport.regression.referenceReportSha256) -or
            -not [string]::IsNullOrEmpty([string]$referenceReport.regression.referenceBuildIdentity)) {
            Add-Failure 'Candidate reference is not an accepted schema-10 release baseline.'
        }
        if ([string]::IsNullOrWhiteSpace([string]$regression.referenceBuildIdentity) -or
            -not [string]::Equals(
                [string]$regression.referenceBuildIdentity,
                [string]$referenceReport.buildIdentity,
                [StringComparison]::Ordinal)) {
            Add-Failure 'Candidate referenceBuildIdentity does not bind the referenced baseline.'
        }

        if ([string]$referenceReport.buildArtifacts.hashAlgorithm -ne 'SHA-256') {
            Add-Failure 'Reference build artifact hash algorithm must be SHA-256.'
        }
        foreach ($expectation in $artifactExpectations) {
            $referenceArtifact = $referenceReport.buildArtifacts.($expectation[0])
            if ($null -eq $referenceArtifact -or
                [string]$referenceArtifact.fileName -ne $expectation[1] -or
                -not [IO.Path]::IsPathFullyQualified([string]$referenceArtifact.path) -or
                [string]::Compare(
                    [IO.Path]::GetFileName([string]$referenceArtifact.path),
                    $expectation[1],
                    [StringComparison]::OrdinalIgnoreCase) -ne 0 -or
                -not (Test-Sha256 $referenceArtifact.sha256)) {
                Add-Failure "Reference build artifact '$($expectation[0])' metadata is invalid."
            }
        }
        # Historical references retain hashes and derived runtime evidence;
        # their build output need not remain present on the gate host.
        $null = Test-RuntimeConfigurationEvidence `
            -RuntimeConfiguration $referenceReport.runtimeConfiguration `
            -Label 'Reference runtime configuration evidence'
        $sameBuildIdentity = [string]::Equals(
            [string]$report.buildIdentity,
            [string]$referenceReport.buildIdentity,
            [StringComparison]::Ordinal)
        foreach ($expectation in $artifactExpectations) {
            $candidateHash = [string]$report.buildArtifacts.($expectation[0]).sha256
            $referenceHash = [string]$referenceReport.buildArtifacts.($expectation[0]).sha256
            if (-not [string]::Equals(
                $candidateHash,
                $referenceHash,
                [StringComparison]::OrdinalIgnoreCase)) {
                if ($sameBuildIdentity) {
                    Add-Failure (
                        "Candidate and reference share a build identity but '$($expectation[0])' hashes differ.")
                }
            }
        }

        foreach ($name in $stringMetadata) {
            if ([string]::IsNullOrWhiteSpace([string]$referenceReport.machine.$name)) {
                Add-Failure "Reference machine metadata '$name' is empty."
            }
        }
        foreach ($name in @('logicalProcessorCount', 'dpiScale', 'viewportWidthDips', 'viewportHeightDips', 'configuredRefreshRateHz', 'observedRefreshRateHz')) {
            $value = $referenceReport.machine.$name
            if (-not (Test-FiniteNumber $value) -or [double]$value -le 0) {
                Add-Failure "Reference machine metadata '$name' is invalid."
            }
        }
        if (-not [bool]$referenceReport.machine.isAtLeast120Hz -or
            [double]$referenceReport.machine.configuredRefreshRateHz -lt 119 -or
            [double]$referenceReport.machine.observedRefreshRateHz -lt
                [Math]::Max(115, [double]$referenceReport.machine.configuredRefreshRateHz * 0.95) -or
            -not (Test-SameMachine -Candidate $report.machine -Reference $referenceReport.machine)) {
            Add-Failure 'Candidate and reference machine metadata are not comparable under the frozen tolerances.'
        }
        if (-not (Test-Sha256 $referenceReport.machine.machineInstanceSha256) -or
            [string]$referenceReport.machine.gpuAdapterLuid -notmatch '^[0-9A-Fa-f]{8}:[0-9A-Fa-f]{8}$' -or
            -not (Test-Sha256 $referenceReport.machine.gpuAdapterIdentitySha256) -or
            [string]::IsNullOrWhiteSpace([string]$referenceReport.machine.gpuDriverVersion) -or
            -not (Test-Sha256 $referenceReport.machine.displayAdapterIdentitySha256) -or
            [string]$referenceReport.machine.displayAdapterLuid -notmatch '^[0-9A-Fa-f]{8}:[0-9A-Fa-f]{8}$' -or
            [string]::IsNullOrWhiteSpace([string]$referenceReport.machine.displayAdapterDriverVersion) -or
            -not (Test-CanonicalGuid $referenceReport.machine.activePowerSchemeGuid) -or
            -not (Test-PowerModePair `
                $referenceReport.machine.userConfiguredAcPowerModeGuid `
                $referenceReport.machine.userConfiguredDcPowerModeGuid) -or
            [string]$referenceReport.machine.powerSource -cne 'AC' -or
            [string]$referenceReport.machine.energySaverState -cne 'Off' -or
            -not (Test-EffectivePowerMode $referenceReport.machine.effectivePowerMode) -or
            [string]$referenceReport.machine.processPriorityClass -cnotin
                @('Normal', 'Idle', 'High', 'RealTime', 'BelowNormal', 'AboveNormal') -or
            [string]$referenceReport.machine.processPowerThrottlingMode -cne
                'execution-speed=off;ignore-timer-resolution=off' -or
            $referenceReport.machine.gcServer -isnot [bool] -or
            [string]$referenceReport.machine.gcLatencyMode -cnotin
                @('Batch', 'Interactive', 'LowLatency', 'SustainedLowLatency', 'NoGCRegion')) {
            Add-Failure 'Reference machine fingerprint, GPU, power, priority, or GC metadata is invalid.'
        }
        if (-not (Test-EnvironmentSnapshotMetadata $referenceReport.completionMachine) -or
            -not (Test-SameRunEnvironment `
                -Start $referenceReport.machine `
                -Completion $referenceReport.completionMachine)) {
            Add-Failure 'The reference completion environment is incomplete or differs from measurement start.'
        }

        $referenceRequirements = $referenceReport.sampleRequirements
        if ([int]$referenceRequirements.firstViewportIterationsRequired -ne 100 -or
            [int]$referenceRequirements.firstViewportTrialsRequired -ne 6 -or
            [int]$referenceRequirements.firstViewportWarmupTrialsRequired -ne 3 -or
            [int]$referenceRequirements.scrollFramesRequired -ne 2400 -or
            [int]$referenceRequirements.scrollTrialsRequired -ne 5 -or
            [int]$referenceRequirements.lifecycleCyclesRequired -ne 100 -or
            [int]$referenceRequirements.cancellationIterationsPerTrialRequired -ne 40 -or
            [int]$referenceRequirements.cancellationTrialsRequired -ne 5 -or
            [int]$referenceRequirements.sourceLookupMappingCountRequired -ne 100000 -or
            [int]$referenceRequirements.sourceLookupAbsoluteQueryCountRequired -ne 20000 -or
            [int]$referenceRequirements.sourceLookupRegressionWarmupPassesRequired -ne 8 -or
            [int]$referenceRequirements.sourceLookupRegressionTrialsRequired -ne 5 -or
            [int]$referenceRequirements.sourceLookupRegressionBatchSizeRequired -ne 4096 -or
            [int]$referenceRequirements.sourceLookupRegressionObservationCountRequired -ne 128 -or
            [int]$referenceRequirements.sourceLookupRegressionOperationCountRequired -ne 524288) {
            Add-Failure 'Candidate reference baseline does not use the frozen regression populations.'
        }

        if (@($referenceReport.retainedMemory).Count -ne 3) {
            Add-Failure 'Reference retained-memory evidence must contain exactly three scenarios.'
        }
        foreach ($sourceBytes in $expectedSizes) {
            $referenceMemoryMatches = @($referenceReport.retainedMemory | Where-Object {
                [int]$_.sourceUtf16Bytes -eq $sourceBytes
            })
            if ($referenceMemoryMatches.Count -ne 1) {
                Add-Failure "Reference retained-memory scenario $sourceBytes is missing or duplicated."
                continue
            }
            $referenceMemory = $referenceMemoryMatches[0]
            $referenceMemoryComplete = $true
            foreach ($name in @(
                'corpus', 'sourceUtf16Bytes', 'managedDeltaBytes', 'privateDeltaBytes',
                'budgetBytes', 'passed')) {
                if ($null -eq $referenceMemory.PSObject.Properties[$name]) {
                    Add-Failure "Reference retained-memory scenario $sourceBytes lacks '$name'."
                    $referenceMemoryComplete = $false
                }
            }
            if (-not $referenceMemoryComplete) { continue }
            $expectedReferenceMemoryCorpus = if ($sourceBytes -eq 10 * 1024 * 1024) {
                'pathological-long-reference-token-v1'
            } else {
                'readme-mixed-v1'
            }
            $referenceMemoryBudget = [int64]$sourceBytes * 8 + 32L * 1024 * 1024
            if ([string]$referenceMemory.corpus -ne $expectedReferenceMemoryCorpus -or
                [int64]$referenceMemory.budgetBytes -ne $referenceMemoryBudget -or
                [int64]$referenceMemory.managedDeltaBytes -lt 0 -or
                [int64]$referenceMemory.privateDeltaBytes -lt 0 -or
                [int64]$referenceMemory.managedDeltaBytes -gt $referenceMemoryBudget -or
                [int64]$referenceMemory.privateDeltaBytes -gt $referenceMemoryBudget -or
                -not [bool]$referenceMemory.passed) {
                Add-Failure "Reference retained-memory scenario $sourceBytes failed its frozen cap."
            }
        }

        $referenceLife = $referenceReport.lifecyclePlateau
        $referenceManagedGrowth = Get-GrowthPercent `
            -Final ([int64]$referenceLife.checkpoint100ManagedBytes) `
            -Baseline ([int64]$referenceLife.checkpoint80ManagedBytes)
        $referencePrivateGrowth = Get-GrowthPercent `
            -Final ([int64]$referenceLife.checkpoint100PrivateBytes) `
            -Baseline ([int64]$referenceLife.checkpoint80PrivateBytes)
        $referenceGrowth = [Math]::Max($referenceManagedGrowth, $referencePrivateGrowth)
        if ([int]$referenceLife.requiredCycles -ne 100 -or
            [int]$referenceLife.completedCycles -ne 100 -or
            [int]$referenceLife.syntheticDeviceResets -ne 4 -or
            [string]$referenceLife.deviceResetMode -cne
                'snapshot-release-and-canvas-device-trim' -or
            [int]$referenceLife.actualDeviceLossEvents -lt 0 -or
            [int64]$referenceLife.checkpoint80ManagedBytes -le 0 -or
            [int64]$referenceLife.checkpoint100ManagedBytes -le 0 -or
            [int64]$referenceLife.checkpoint80PrivateBytes -le 0 -or
            [int64]$referenceLife.checkpoint100PrivateBytes -le 0 -or
            -not (Test-NearlyEqual ([double]$referenceLife.managedGrowthPercent) $referenceManagedGrowth) -or
            -not (Test-NearlyEqual ([double]$referenceLife.privateGrowthPercent) $referencePrivateGrowth) -or
            -not (Test-NearlyEqual ([double]$referenceLife.growthPercent) $referenceGrowth) -or
            [double]$referenceLife.growthBudgetPercent -ne 5 -or
            $referenceGrowth -gt 5 -or
            [int]$referenceLife.renderFailures -ne 0 -or
            -not [bool]$referenceLife.passed) {
            Add-Failure 'Reference lifecycle evidence failed its frozen absolute gate.'
        }

        $referenceFirstViewport = Get-ValidatedFirstViewportEvidence `
            -Results @($referenceReport.firstUsableViewport) `
            -Label 'Reference first-viewport evidence' `
            -ReportStartedUtc ([DateTimeOffset]$referenceReport.startedUtc) `
            -ReportCompletedUtc ([DateTimeOffset]$referenceReport.completedUtc)
        if ($null -eq $candidateFirstViewport.Pin -or
            $null -eq $referenceFirstViewport.Pin -or
            [string]$candidateFirstViewport.Pin -cne [string]$referenceFirstViewport.Pin) {
            Add-Failure 'Candidate and reference first-viewport pinned-thread evidence is not comparable.'
        }
        foreach ($metric in $referenceFirstViewport.Metrics.Keys) {
            $expectedReferences[$metric] = $referenceFirstViewport.Metrics[$metric].Value
            $referenceTrialPopulations[$metric] = $referenceFirstViewport.Metrics[$metric].Trials
            $referenceDispersions[$metric] = $referenceFirstViewport.Metrics[$metric].Dispersion
            $referenceStationarity[$metric] = $referenceFirstViewport.Metrics[$metric].Stationarity
        }

        $referenceScroll = $referenceReport.scroll
        if ([string]$referenceScroll.corpus -ne [string]$scroll.corpus -or
            [int]$referenceScroll.sourceUtf16Bytes -ne [int]$scroll.sourceUtf16Bytes) {
            Add-Failure 'Reference warm-scroll corpus is incompatible.'
        }
        $referenceScrollEvidence = Get-ValidatedScrollEvidence `
            -Scroll $referenceScroll `
            -Label 'Reference warm-scroll evidence'
        if ($null -ne $referenceScrollEvidence) {
            if (-not (Test-NearlyEqual `
                    ([double]$referenceReport.machine.observedRefreshRateHz) `
                    ([double]$referenceScrollEvidence.ObservedRefreshRateHz))) {
                Add-Failure 'Observed refresh rate is not reproducible from reference raw frame evidence.'
            }
            $referenceScrollMetrics = @{
                'scroll.uiThreadWorkP95Ms.hodgesLehmann' = [pscustomobject]@{
                    Value = $referenceScrollEvidence.RegressionUiP95
                    Trials = $referenceScrollEvidence.TrialUiP95
                    Dispersion = $referenceScrollEvidence.UiP95Dispersion
                }
                'scroll.uiThreadWorkP99Ms.hodgesLehmann' = [pscustomobject]@{
                    Value = $referenceScrollEvidence.RegressionUiP99
                    Trials = $referenceScrollEvidence.TrialUiP99
                    Dispersion = $referenceScrollEvidence.UiP99Dispersion
                }
                'scroll.frameTimeP95Ms.hodgesLehmann' = [pscustomobject]@{
                    Value = $referenceScrollEvidence.RegressionFrameP95
                    Trials = $referenceScrollEvidence.TrialFrameP95
                    Dispersion = $referenceScrollEvidence.FrameP95Dispersion
                }
                'scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann' = [pscustomobject]@{
                    Value = $referenceScrollEvidence.RegressionAllocationP95
                    Trials = $referenceScrollEvidence.TrialAllocationP95
                    Dispersion = $referenceScrollEvidence.AllocationP95Dispersion
                }
            }
            foreach ($metric in $referenceScrollMetrics.Keys) {
                $expectedReferences[$metric] = [double]$referenceScrollMetrics[$metric].Value
                $referenceTrialPopulations[$metric] = @($referenceScrollMetrics[$metric].Trials)
                $referenceDispersions[$metric] = [double]$referenceScrollMetrics[$metric].Dispersion
            }
        }

        $referenceLookup = $referenceReport.sourceLookup
        $referenceAbsoluteLookupTicks = @($referenceLookup.samplesElapsedTicks)
        $referenceLookupTrials = @($referenceLookup.regressionTrials)
        $referenceLookupFrequency = [double]$referenceLookup.stopwatchFrequency
        if (-not (Test-PinnedProcessorEvidence `
                -AffinityMask $referenceLookup.measurementThreadAffinityMask `
                -ProcessorGroup ([int]$referenceLookup.measurementProcessorGroup) `
                -ProcessorNumber ([int]$referenceLookup.measurementProcessorNumber) `
                -Priority $referenceLookup.measurementThreadPriority `
                -ExpectedPriority 'Highest')) {
            Add-Failure 'Reference source-lookup execution metadata is invalid.'
        }
        if ([string]$lookup.measurementThreadAffinityMask -cne
                [string]$referenceLookup.measurementThreadAffinityMask -or
            [int]$lookup.measurementProcessorGroup -ne
                [int]$referenceLookup.measurementProcessorGroup -or
            [int]$lookup.measurementProcessorNumber -ne
                [int]$referenceLookup.measurementProcessorNumber -or
            [string]$lookup.measurementThreadPriority -cne
                [string]$referenceLookup.measurementThreadPriority) {
            Add-Failure 'Candidate and reference source-lookup execution metadata do not match.'
        }
        if ([int]$referenceLookup.mappingCount -ne 100000 -or
            [int]$referenceLookup.queryCount -ne 20000 -or
            -not (Test-FiniteNumber $referenceLookupFrequency) -or
            [int64]$referenceLookupFrequency -ne [Diagnostics.Stopwatch]::Frequency -or
            $referenceAbsoluteLookupTicks.Count -ne 20000 -or
            -not (Test-FiniteNumber $referenceLookup.p95Nanoseconds) -or
            [double]$referenceLookup.p95Nanoseconds -le 0 -or
            [double]$referenceLookup.p95Nanoseconds -gt 25000 -or
            [double]$referenceLookup.p95BudgetNanoseconds -ne 25000 -or
            [int64]$referenceLookup.allocatedBytes -ne 0 -or
            [int]$referenceLookup.regressionWarmupPasses -ne 8 -or
            [int]$referenceLookup.regressionBatchSize -ne 4096 -or
            [int]$referenceLookup.regressionObservationCount -ne 128 -or
            [int]$referenceLookup.regressionOperationCount -ne 524288 -or
            [int]$referenceLookup.regressionOperationCount -ne
                ([int]$referenceLookup.regressionBatchSize * [int]$referenceLookup.regressionObservationCount) -or
            [string]$referenceLookup.regressionEstimator -ne 'hodges-lehmann-of-five-trial-p95' -or
            [int]$referenceLookup.regressionTrialCount -ne 5 -or
            $referenceLookupTrials.Count -ne 5 -or
            [int64]$referenceLookup.regressionAllocatedBytes -ne 0 -or
            -not [bool]$referenceLookup.passed) {
            Add-Failure 'Reference source-lookup regression evidence is incomplete.'
        }
        foreach ($ticks in $referenceAbsoluteLookupTicks) {
            if (-not (Test-FiniteNumber $ticks) -or [double]$ticks -lt 0 -or
                [double]$ticks -gt [int64]::MaxValue -or
                [double]$ticks -ne [double][int64]$ticks) {
                Add-Failure 'Reference absolute source-lookup evidence contains an invalid elapsed-tick sample.'
            }
        }
        if ($referenceAbsoluteLookupTicks.Count -eq 20000 -and $referenceLookupFrequency -gt 0) {
            $referenceAbsoluteP95Ticks =
                Get-NearestRankPercentile -Values $referenceAbsoluteLookupTicks -Percentile 0.95
            $computedReferenceAbsoluteP95 =
                $referenceAbsoluteP95Ticks * 1000000000.0 / $referenceLookupFrequency
            if (-not (Test-NearlyEqual ([double]$referenceLookup.p95Nanoseconds) $computedReferenceAbsoluteP95)) {
                Add-Failure 'Reference absolute source-lookup p95 is not reproducible from elapsed ticks.'
            }
        }
        if ($null -ne $referenceLookup.PSObject.Properties['regressionSamplesElapsedTicks'] -or
            $null -ne $referenceLookup.PSObject.Properties['regressionSamplesNanosecondsPerLookup']) {
            Add-Failure 'Schema-10 reference source-lookup evidence contains legacy flat regression sample arrays.'
        }
        $computedReferenceTrialP95 = @()
        $computedReferenceAllocated = 0L
        if ($referenceLookupTrials.Count -eq 5 -and $referenceLookupFrequency -gt 0 -and
            [int]$referenceLookup.regressionBatchSize -eq 4096) {
            for ($trialIndex = 0; $trialIndex -lt 5; $trialIndex++) {
                $referenceTrial = $referenceLookupTrials[$trialIndex]
                $referenceTrialComplete = $true
                foreach ($name in @(
                    'ordinal', 'observationCount', 'operationCount', 'samplesElapsedTicks',
                    'samplesNanosecondsPerLookup', 'p95Nanoseconds', 'allocatedBytes', 'complete')) {
                    if ($null -eq $referenceTrial.PSObject.Properties[$name]) {
                        Add-Failure "Reference source-lookup regression trial $trialIndex lacks '$name'."
                        $referenceTrialComplete = $false
                    }
                }
                if (-not $referenceTrialComplete) { continue }

                $referenceTicks = @($referenceTrial.samplesElapsedTicks)
                $referenceSamples = @($referenceTrial.samplesNanosecondsPerLookup)
                if ([int]$referenceTrial.ordinal -ne $trialIndex -or
                    [int]$referenceTrial.observationCount -ne 128 -or
                    [int]$referenceTrial.operationCount -ne 524288 -or
                    [int]$referenceTrial.operationCount -ne
                        ([int]$referenceLookup.regressionBatchSize * [int]$referenceTrial.observationCount) -or
                    $referenceTicks.Count -ne 128 -or $referenceSamples.Count -ne 128 -or
                    [int64]$referenceTrial.allocatedBytes -ne 0 -or -not [bool]$referenceTrial.complete) {
                    Add-Failure "Reference source-lookup regression trial $trialIndex does not match the frozen population."
                    continue
                }

                for ($observation = 0; $observation -lt 128; $observation++) {
                    $rawTicks = $referenceTicks[$observation]
                    $ticks = [int64]$rawTicks
                    $sample = [double]$referenceSamples[$observation]
                    $computedSample = $ticks * 1000000000.0 / $referenceLookupFrequency / 4096.0
                    if (-not (Test-FiniteNumber $rawTicks) -or [double]$rawTicks -le 0 -or
                        [double]$rawTicks -gt [int64]::MaxValue -or
                        [double]$rawTicks -ne [double]$ticks -or
                        -not (Test-FiniteNumber $sample) -or $sample -le 0 -or
                        -not (Test-NearlyEqual $sample $computedSample)) {
                        Add-Failure (
                            "Reference source-lookup regression trial $trialIndex observation $observation is not reproducible.")
                    }
                }

                $referenceTrialP95 = Get-NearestRankPercentile -Values $referenceSamples -Percentile 0.95
                if (-not (Test-FiniteNumber $referenceTrial.p95Nanoseconds) -or
                    [double]$referenceTrial.p95Nanoseconds -le 0 -or
                    -not (Test-NearlyEqual ([double]$referenceTrial.p95Nanoseconds) $referenceTrialP95)) {
                    Add-Failure "Reference source-lookup regression trial $trialIndex p95 is not reproducible."
                }
                $computedReferenceTrialP95 += $referenceTrialP95
                $computedReferenceAllocated += [int64]$referenceTrial.allocatedBytes
            }
        }
        if ($computedReferenceTrialP95.Count -eq 5) {
            $computedReferenceLookupP95 = Get-HodgesLehmann -Values $computedReferenceTrialP95
            if (-not (Test-NearlyEqual ([double]$referenceLookup.regressionP95Nanoseconds) $computedReferenceLookupP95)) {
                Add-Failure 'Reference source-lookup p95 is not the Hodges-Lehmann estimate of its five trial p95 values.'
            }
            $referenceLookupDispersion =
                Get-RobustDispersionPercent -Values $computedReferenceTrialP95
            if (-not (Test-FiniteNumber $referenceLookupDispersion) -or
                $referenceLookupDispersion -gt 20) {
                Add-Failure 'Reference source-lookup trial p95 dispersion exceeds 20%.'
            }
            if ([int64]$referenceLookup.regressionAllocatedBytes -ne $computedReferenceAllocated) {
                Add-Failure 'Reference source-lookup allocation total does not equal its five trial totals.'
            }
            $metric = 'sourceLookup.regressionP95Ns.hodgesLehmann'
            $expectedReferences[$metric] = $computedReferenceLookupP95
            $referenceTrialPopulations[$metric] = $computedReferenceTrialP95
            $referenceDispersions[$metric] = $referenceLookupDispersion
        }

        $referenceCancellationEvidence = Get-ValidatedCancellationEvidence `
            -Cancellation $referenceReport.cancellation `
            -Label 'Reference cancellation evidence'
        if ($null -ne $referenceCancellationEvidence) {
            $metric = 'cancellation.p95Ms.hodgesLehmann'
            $expectedReferences[$metric] = $referenceCancellationEvidence.RegressionP95
            $referenceTrialPopulations[$metric] = $referenceCancellationEvidence.TrialP95
            $referenceDispersions[$metric] = $referenceCancellationEvidence.Dispersion
        }
    }

    if ([int]$regression.comparedLatencyMetrics -ne 11 -or [int]$regression.comparedAllocationMetrics -ne 1 -or
        @($regression.comparisons).Count -ne 12) { Add-Failure 'Candidate regression evidence is incomplete.' }
    $expectedCandidates = @{}
    $candidateTrialPopulations = @{}
    $candidateDispersions = @{}
    $candidateStationarity = @{}
    foreach ($metric in $candidateFirstViewportEvidence.Keys) {
        $expectedCandidates[$metric] = $candidateFirstViewportEvidence[$metric].Value
        $candidateTrialPopulations[$metric] = $candidateFirstViewportEvidence[$metric].Trials
        $candidateDispersions[$metric] = $candidateFirstViewportEvidence[$metric].Dispersion
        $candidateStationarity[$metric] = $candidateFirstViewportEvidence[$metric].Stationarity
    }
    if ($null -ne $candidateScrollEvidence) {
        $candidateScrollMetrics = @{
            'scroll.uiThreadWorkP95Ms.hodgesLehmann' = [pscustomobject]@{
                Value = $candidateScrollEvidence.RegressionUiP95
                Trials = $candidateScrollEvidence.TrialUiP95
                Dispersion = $candidateScrollEvidence.UiP95Dispersion
            }
            'scroll.uiThreadWorkP99Ms.hodgesLehmann' = [pscustomobject]@{
                Value = $candidateScrollEvidence.RegressionUiP99
                Trials = $candidateScrollEvidence.TrialUiP99
                Dispersion = $candidateScrollEvidence.UiP99Dispersion
            }
            'scroll.frameTimeP95Ms.hodgesLehmann' = [pscustomobject]@{
                Value = $candidateScrollEvidence.RegressionFrameP95
                Trials = $candidateScrollEvidence.TrialFrameP95
                Dispersion = $candidateScrollEvidence.FrameP95Dispersion
            }
            'scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann' = [pscustomobject]@{
                Value = $candidateScrollEvidence.RegressionAllocationP95
                Trials = $candidateScrollEvidence.TrialAllocationP95
                Dispersion = $candidateScrollEvidence.AllocationP95Dispersion
            }
        }
        foreach ($metric in $candidateScrollMetrics.Keys) {
            $expectedCandidates[$metric] = $candidateScrollMetrics[$metric].Value
            $candidateTrialPopulations[$metric] = $candidateScrollMetrics[$metric].Trials
            $candidateDispersions[$metric] = $candidateScrollMetrics[$metric].Dispersion
        }
    }
    if ($null -ne $candidateLookupEvidence) {
        $metric = 'sourceLookup.regressionP95Ns.hodgesLehmann'
        $expectedCandidates[$metric] = $candidateLookupEvidence.Value
        $candidateTrialPopulations[$metric] = $candidateLookupEvidence.Trials
        $candidateDispersions[$metric] = $candidateLookupEvidence.Dispersion
    }
    if ($null -ne $candidateCancellationEvidence) {
        $metric = 'cancellation.p95Ms.hodgesLehmann'
        $expectedCandidates[$metric] = $candidateCancellationEvidence.RegressionP95
        $candidateTrialPopulations[$metric] = $candidateCancellationEvidence.TrialP95
        $candidateDispersions[$metric] = $candidateCancellationEvidence.Dispersion
    }

    $allocationMetric = 'scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann'
    $absoluteNoiseFloors = @{
        'firstViewport.cache-disabled.102400.p95Ms.hodgesLehmann' = 5.0
        'firstViewport.cache-hit.102400.p95Ms.hodgesLehmann' = 3.0
        'firstViewport.cache-disabled.1048576.p95Ms.hodgesLehmann' = 5.0
        'firstViewport.cache-hit.1048576.p95Ms.hodgesLehmann' = 5.0
        'firstViewport.cache-disabled.10485760.p95Ms.hodgesLehmann' = 3.0
        'firstViewport.cache-hit.10485760.p95Ms.hodgesLehmann' = 1.0
        'scroll.uiThreadWorkP95Ms.hodgesLehmann' = 0.05
        'scroll.uiThreadWorkP99Ms.hodgesLehmann' = 0.10
        'scroll.frameTimeP95Ms.hodgesLehmann' = 2.0
        'sourceLookup.regressionP95Ns.hodgesLehmann' = 30.0
        'cancellation.p95Ms.hodgesLehmann' = 0.05
        'scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann' = 64.0
    }
    $stabilityBudgets = @{
        'firstViewport.cache-disabled.102400.p95Ms.hodgesLehmann' = 25.0
        'firstViewport.cache-hit.102400.p95Ms.hodgesLehmann' = 25.0
        'firstViewport.cache-disabled.1048576.p95Ms.hodgesLehmann' = 25.0
        'firstViewport.cache-hit.1048576.p95Ms.hodgesLehmann' = 25.0
        'firstViewport.cache-disabled.10485760.p95Ms.hodgesLehmann' = 25.0
        'firstViewport.cache-hit.10485760.p95Ms.hodgesLehmann' = 25.0
        'scroll.uiThreadWorkP95Ms.hodgesLehmann' = 25.0
        'scroll.uiThreadWorkP99Ms.hodgesLehmann' = 25.0
        'scroll.frameTimeP95Ms.hodgesLehmann' = 35.0
        'sourceLookup.regressionP95Ns.hodgesLehmann' = 20.0
        'cancellation.p95Ms.hodgesLehmann' = 25.0
        'scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann' = 25.0
    }
    $expectedKinds = @{}
    foreach ($metric in $absoluteNoiseFloors.Keys) {
        $expectedKinds[$metric] = if ($metric -eq $allocationMetric) { 'allocation' } else { 'latency' }
    }
    if ($expectedReferences.Count -ne $expectedCandidates.Count) {
        Add-Failure 'Candidate reference baseline did not yield all 12 reproducible regression estimators.'
    }
    $comparisons = @($regression.comparisons)
    $comparisonNames = @($comparisons | ForEach-Object { [string]$_.metric })
    if (@($comparisonNames | Select-Object -Unique).Count -ne $comparisonNames.Count -or
        $comparisonNames.Count -ne $expectedCandidates.Count) {
        Add-Failure 'Candidate regression comparisons are missing or duplicated.'
    }
    foreach ($comparison in $comparisons) {
        $comparisonHasRequiredFields = $true
        foreach ($name in @(
            'metric', 'kind', 'reference', 'candidate', 'absoluteDelta', 'deltaPercent',
            'budgetPercent', 'relativeAllowance', 'absoluteNoiseFloor', 'allowedAbsoluteDelta',
            'referenceDispersionPercent', 'candidateDispersionPercent', 'stabilityBudgetPercent',
            'decision', 'passed')) {
            if ($null -eq $comparison.PSObject.Properties[$name]) {
                Add-Failure "Regression comparison lacks '$name'."
                $comparisonHasRequiredFields = $false
            }
        }
        if (-not $comparisonHasRequiredFields) { continue }
        $metric = [string]$comparison.metric
        if (-not $expectedCandidates.ContainsKey($metric) -or
            -not $expectedReferences.ContainsKey($metric) -or
            -not $candidateTrialPopulations.ContainsKey($metric) -or
            -not $referenceTrialPopulations.ContainsKey($metric) -or
            -not $expectedKinds.ContainsKey($metric)) {
            Add-Failure "Regression comparison '$metric' is not a frozen schema-10 metric."
            continue
        }

        $reference = [double]$expectedReferences[$metric]
        $candidate = [double]$expectedCandidates[$metric]
        if (-not (Test-NearlyEqual ([double]$comparison.candidate) $candidate)) {
            Add-Failure "Regression comparison '$metric' does not bind the report's measured estimator."
        }
        if (-not (Test-NearlyEqual ([double]$comparison.reference) $reference)) {
            Add-Failure "Regression comparison '$metric' does not bind the recomputed reference estimator."
        }
        if ([string]$comparison.kind -ne [string]$expectedKinds[$metric]) {
            Add-Failure "Regression comparison '$metric' has the wrong metric kind."
        }
        $expectedBudget = if ([string]$expectedKinds[$metric] -eq 'allocation') { 2.0 } else { 5.0 }
        $computedDelta = if ($reference -eq 0) {
            if ($candidate -eq 0) { 0.0 } else { [double]::MaxValue }
        } else {
            (($candidate / $reference) - 1.0) * 100.0
        }
        $absoluteDelta = $candidate - $reference
        $relativeAllowance = [Math]::Abs($reference) * $expectedBudget / 100.0
        $absoluteNoiseFloor = [double]$absoluteNoiseFloors[$metric]
        $allowedAbsoluteDelta = [Math]::Max($relativeAllowance, $absoluteNoiseFloor)
        $referenceDispersion =
            Get-RobustDispersionPercent -Values @($referenceTrialPopulations[$metric])
        $candidateDispersion =
            Get-RobustDispersionPercent -Values @($candidateTrialPopulations[$metric])
        $stabilityBudget = [double]$stabilityBudgets[$metric]
        $stationary = (-not $candidateStationarity.ContainsKey($metric) -or
                [bool]$candidateStationarity[$metric]) -and
            (-not $referenceStationarity.ContainsKey($metric) -or
                [bool]$referenceStationarity[$metric])
        $stable = $stationary -and
            $referenceDispersion -le $stabilityBudget -and
            $candidateDispersion -le $stabilityBudget
        $withinAllowance = $stable -and
            ($absoluteDelta -le $allowedAbsoluteDelta -or
             (Test-NearlyEqual $absoluteDelta $allowedAbsoluteDelta))
        $expectedDecision = if (-not $stable) {
            'inconclusive'
        } elseif ($withinAllowance) {
            'pass'
        } else {
            'regression'
        }

        $computedFields = @{
            absoluteDelta = $absoluteDelta
            deltaPercent = $computedDelta
            budgetPercent = $expectedBudget
            relativeAllowance = $relativeAllowance
            absoluteNoiseFloor = $absoluteNoiseFloor
            allowedAbsoluteDelta = $allowedAbsoluteDelta
            referenceDispersionPercent = $referenceDispersion
            candidateDispersionPercent = $candidateDispersion
            stabilityBudgetPercent = $stabilityBudget
        }
        foreach ($name in $computedFields.Keys) {
            if (-not (Test-NearlyEqual ([double]$comparison.$name) ([double]$computedFields[$name]))) {
                Add-Failure "Regression comparison '$metric' field '$name' is not reproducible."
            }
        }
        if ([string]$comparison.decision -cne $expectedDecision -or
            [bool]$comparison.passed -ne $withinAllowance -or
            -not $withinAllowance) {
            Add-Failure "Regression comparison '$metric' has the wrong hybrid-allowance decision or failed."
        }
    }
}
else {
    Add-Failure "Regression mode '$mode' is not release-gating."
}

if (-not [bool]$report.passed) { Add-Failure 'The harness did not mark the full report as passed.' }

if ($failures.Count -gt 0) {
    throw "WinUI performance gate failed:`n - $($failures -join "`n - ")"
}

Write-Host (
    'WinUI performance gates passed: schema-10 ordered-stationarity evidence for six first-viewports, five 2,400-frame scroll trials, ' +
    'batched source lookup, five 40-sample cancellation trials, 4 KiB renderer allocation, retained memory/plateau, runtime configuration, hashes, and hybrid regression policy.')
