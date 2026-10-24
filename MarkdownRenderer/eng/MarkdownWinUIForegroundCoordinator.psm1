Set-StrictMode -Version Latest

function Get-MarkdownHarnessRole {
    param(
        [Parameter(Mandatory)][string] $ExecutablePath,
        [Parameter(Mandatory)][string] $ReferenceExecutablePath,
        [Parameter(Mandatory)][string] $CandidateExecutablePath
    )

    $actualPath = [IO.Path]::GetFullPath($ExecutablePath)
    $referencePath = [IO.Path]::GetFullPath($ReferenceExecutablePath)
    $candidatePath = [IO.Path]::GetFullPath($CandidateExecutablePath)

    if ([string]::Equals($actualPath, $referencePath, [StringComparison]::OrdinalIgnoreCase)) {
        return 'Reference'
    }
    if ([string]::Equals($actualPath, $candidatePath, [StringComparison]::OrdinalIgnoreCase)) {
        return 'Candidate'
    }

    return $null
}

function Get-MarkdownExpectedHarnessRole {
    param([Parameter(Mandatory)][ValidateRange(0, 3)][int] $LaunchIndex)

    return @('Reference', 'Candidate', 'Candidate', 'Reference')[$LaunchIndex]
}

function Get-MarkdownHarnessPreflightDeadlineUtc {
    param(
        [Parameter(Mandatory)][UInt64] $EventCreatedFileTime,
        [Parameter(Mandatory)][ValidateRange(1, 30)][int] $TimeoutSeconds
    )

    if ($EventCreatedFileTime -eq 0 -or $EventCreatedFileTime -gt [Int64]::MaxValue) {
        throw 'The process-start event has an invalid TIME_CREATED value.'
    }

    $eventCreatedUtc = [DateTime]::FromFileTimeUtc([Int64]$EventCreatedFileTime)
    return [DateTimeOffset]::new($eventCreatedUtc).AddSeconds($TimeoutSeconds)
}

function New-MarkdownWinAppTitleBarClickArguments {
    param([Parameter(Mandatory)][ValidateRange(1, 2147483647)][int] $ProcessId)

    return @('ui', 'click', 'TitleBar', '-a', [string]$ProcessId)
}

function Test-MarkdownForegroundProcessId {
    param(
        [Parameter(Mandatory)][int] $ForegroundProcessId,
        [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int] $TargetProcessId
    )

    return $ForegroundProcessId -eq $TargetProcessId
}

function Test-MarkdownWinAppCliVersion {
    param([Parameter(Mandatory)][string] $VersionText)

    $version = [Version]::new(0, 0)
    return [Version]::TryParse($VersionText.Trim(), [ref]$version) -and
        $version.Major -eq 0 -and $version.Minor -eq 7
}

Export-ModuleMember -Function @(
    'Get-MarkdownHarnessRole',
    'Get-MarkdownExpectedHarnessRole',
    'Get-MarkdownHarnessPreflightDeadlineUtc',
    'New-MarkdownWinAppTitleBarClickArguments',
    'Test-MarkdownForegroundProcessId',
    'Test-MarkdownWinAppCliVersion')
