[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [ValidateRange(1, 500)]
    [int]$ExpectedCount = 500,

    [ValidateRange(1, 90)]
    [int]$MaximumAgeDays = 14
)

$ErrorActionPreference = 'Stop'
$SourcePath = [IO.Path]::GetFullPath($SourcePath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "The pinned README corpus does not exist: '$SourcePath'."
}

$manifest = Get-Content -LiteralPath $SourcePath -Raw | ConvertFrom-Json -Depth 16
if ([int]$manifest.schemaVersion -ne 2) {
    throw "The pinned README corpus has unsupported schema version '$($manifest.schemaVersion)'."
}
if ([int]$manifest.requestedCount -ne $ExpectedCount) {
    throw "The pinned README corpus requests $($manifest.requestedCount) repositories; expected $ExpectedCount."
}

$generatedAt = [DateTimeOffset]$manifest.generatedAtUtc
$now = [DateTimeOffset]::UtcNow
if ($generatedAt -gt $now.AddMinutes(5)) {
    throw "The pinned README corpus timestamp is in the future: $generatedAt."
}
if (($now - $generatedAt) -gt [TimeSpan]::FromDays($MaximumAgeDays)) {
    throw "The pinned README corpus is older than $MaximumAgeDays days: $generatedAt."
}

$repositories = @($manifest.repositories)
if ($repositories.Count -ne $ExpectedCount) {
    throw "The pinned README corpus contains $($repositories.Count) repositories; expected $ExpectedCount."
}

$ids = [Collections.Generic.HashSet[long]]::new()
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
for ($index = 0; $index -lt $repositories.Count; $index++) {
    $repository = $repositories[$index]
    $expectedRank = $index + 1
    if ([int]$repository.rank -ne $expectedRank) {
        throw "The pinned README corpus rank at index $index is '$($repository.rank)'; expected $expectedRank."
    }
    if ([long]$repository.id -le 0 -or -not $ids.Add([long]$repository.id)) {
        throw "The pinned README corpus has an invalid or duplicate repository id at rank $expectedRank."
    }
    $fullName = [string]$repository.fullName
    if ($fullName -notmatch '^[^/\s]+/[^/\s]+$' -or -not $names.Add($fullName)) {
        throw "The pinned README corpus has an invalid or duplicate repository name at rank $expectedRank."
    }
    if ([string]$repository.defaultBranch -eq '' -or
        [string]$repository.commitSha -notmatch '^[0-9a-f]{40}$' -or
        [string]$repository.url -notmatch '^https://github\.com/') {
        throw "The pinned README corpus has invalid repository metadata for '$fullName'."
    }

    $readme = $repository.readme
    if ($null -eq $readme) {
        throw "The pinned README corpus has no README metadata for '$fullName'."
    }
    if ([bool]$readme.available) {
        if ([long]$readme.byteSize -lt 0 -or
            [string]$readme.path -eq '' -or
            [string]$readme.sha -notmatch '^[0-9a-f]{40}$' -or
            [string]$readme.htmlUrl -notmatch '^https://github\.com/' -or
            [string]$readme.downloadUrl -notmatch '^https://raw\.githubusercontent\.com/') {
            throw "The pinned README corpus has invalid README metadata for '$fullName'."
        }
    }
    elseif ([long]$readme.byteSize -ne 0 -or
        [string]$readme.path -ne '' -or
        [string]$readme.sha -ne '') {
        throw "The pinned README corpus has inconsistent absent-README metadata for '$fullName'."
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$temporaryPath = "$OutputPath.$PID.tmp"
try {
    Copy-Item -LiteralPath $SourcePath -Destination $temporaryPath -Force
    Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force
}
finally {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Using validated top-$ExpectedCount README corpus from $generatedAt (SHA-256 $hash)."
