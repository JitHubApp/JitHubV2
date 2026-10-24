[CmdletBinding()]
param(
    [string] $CorpusManifestPath,
    [string] $OutputDirectory,
    [ValidateRange(1, 8)][int] $MaxConcurrency = 4,
    [ValidateRange(1, 5)][int] $MaxAttempts = 3,
    [ValidateRange(1, 8388608)][int] $MaxReadmeBytes = 8388608,
    [ValidateRange(0, 60)][int] $MaxRetryWaitSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:MarkdownRendererTop500CorpusSha256 = '615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72'
$script:MarkdownRendererTop500CorpusRelativePath = 'artifacts/readme-top500/corpus-36360809497/top-repositories.json'
$script:MarkdownRendererTop500ExpectedCount = 500

function ConvertTo-MarkdownRendererHex {
    param([Parameter(Mandatory)][byte[]] $Bytes)
    return [BitConverter]::ToString($Bytes).Replace('-', '').ToLowerInvariant()
}

function Get-MarkdownRendererSha256 {
    param([Parameter(Mandatory)][byte[]] $Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ConvertTo-MarkdownRendererHex -Bytes $algorithm.ComputeHash($Bytes) }
    finally { $algorithm.Dispose() }
}

function Get-MarkdownRendererGitBlobSha1 {
    param([Parameter(Mandatory)][byte[]] $Bytes)
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($Bytes.Length)`0")
    $inputBytes = [byte[]]::new($header.Length + $Bytes.Length)
    [Array]::Copy($header, 0, $inputBytes, 0, $header.Length)
    [Array]::Copy($Bytes, 0, $inputBytes, $header.Length, $Bytes.Length)
    $algorithm = [Security.Cryptography.SHA1]::Create()
    try { return ConvertTo-MarkdownRendererHex -Bytes $algorithm.ComputeHash($inputBytes) }
    finally { $algorithm.Dispose() }
}

function Get-MarkdownRendererSymlinkProofSha256 {
    param([Parameter(Mandatory)][object] $Proof)
    $chain = [Collections.Generic.List[object]]::new()
    foreach ($link in @($Proof.linkChain)) {
        $chain.Add([pscustomobject][ordered]@{
            linkPath = [string]$link.linkPath
            linkMode = [string]$link.linkMode
            linkType = [string]$link.linkType
            linkGitBlobSha1 = [string]$link.linkGitBlobSha1
            fetchedLinkGitBlobSha1 = [string]$link.fetchedLinkGitBlobSha1
            linkParentTreeSha = [string]$link.linkParentTreeSha
            targetText = [string]$link.targetText
            normalizedTargetPath = [string]$link.normalizedTargetPath
            targetMode = [string]$link.targetMode
            targetType = [string]$link.targetType
            targetGitObjectSha = [string]$link.targetGitObjectSha
            targetParentTreeSha = [string]$link.targetParentTreeSha
        })
    }
    $payload = [pscustomobject][ordered]@{
        schemaVersion = [int]$Proof.schemaVersion
        resolver = [string]$Proof.resolver
        commitSha = [string]$Proof.commitSha
        rootTreeSha = [string]$Proof.rootTreeSha
        linkChain = @($chain)
        finalTargetPath = [string]$Proof.finalTargetPath
        finalTreeEntrySha = [string]$Proof.finalTreeEntrySha
        finalContentGitBlobSha1 = [string]$Proof.finalContentGitBlobSha1
        finalContentByteSize = [long]$Proof.finalContentByteSize
    }
    $json = ConvertTo-Json -InputObject $payload -Depth 12 -Compress
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    return Get-MarkdownRendererSha256 -Bytes $bytes
}

function ConvertTo-MarkdownRendererBoundedSeconds {
    param([Parameter(Mandatory)][double] $Seconds)
    if ([double]::IsNaN($Seconds) -or [double]::IsInfinity($Seconds)) { return [int]::MaxValue }
    return [int][Math]::Min([int]::MaxValue, [Math]::Max(0, [Math]::Ceiling($Seconds)))
}

function Assert-MarkdownRendererNoReparsePath {
    param([Parameter(Mandatory)][string] $Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrEmpty($root)) { throw 'path-is-not-absolute' }
    $relative = $fullPath.Substring($root.Length).TrimStart([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    $segments = $relative.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)
    $current = $root
    foreach ($segment in $segments) {
        $current = [IO.Path]::Combine($current, $segment)
        try {
            $attributes = [IO.File]::GetAttributes($current)
        }
        catch {
            $cause = if ($null -ne $_.Exception.InnerException) { $_.Exception.InnerException } else { $_.Exception }
            if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { break }
            throw
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'reparse-point-path-rejected'
        }
    }
    return $fullPath
}

function Read-MarkdownRendererFileBytesBounded {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][ValidateRange(1, 8388608)][int] $MaxBytes)
    $fullPath = Assert-MarkdownRendererNoReparsePath -Path $Path
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -gt $MaxBytes) { throw 'file-over-budget' }
        if ($stream.Length -gt [int]::MaxValue) { throw 'file-over-budget' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw 'file-changed-during-read' }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) { throw 'file-changed-during-read' }
        return $bytes
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Get-MarkdownRendererReadmeIdentity {
    param([Parameter(Mandatory)][object] $Repository)

    $parts = ([string]$Repository.fullName).Split([char[]]@('/'), 2)
    $owner = $parts[0]
    $name = $parts[1]
    $readmePath = [string]$Repository.readme.path
    $commit = ([string]$Repository.commitSha).ToLowerInvariant()
    $pathSegments = $readmePath.Split('/')
    foreach ($segment in $pathSegments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -ceq '.' -or $segment -ceq '..') {
            throw "Rank $($Repository.rank) has an unsafe README path."
        }
    }

    $escapedPath = ($pathSegments | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $expectedHtmlUrl = "https://github.com/$owner/$name/blob/$commit/$escapedPath"
    $expectedDownloadUrl = "https://raw.githubusercontent.com/$owner/$name/$commit/$escapedPath"
    if ([string]$Repository.url -cne "https://github.com/$owner/$name" -or
        [string]$Repository.readme.htmlUrl -cne $expectedHtmlUrl -or
        [string]$Repository.readme.downloadUrl -cne $expectedDownloadUrl) {
        throw "Rank $($Repository.rank) has a URL that does not bind to its pinned repository, commit, and README path."
    }

    return [pscustomobject][ordered]@{
        rank = [int]$Repository.rank
        repositoryId = [long]$Repository.id
        fullName = [string]$Repository.fullName
        commitSha = $commit
        defaultBranch = [string]$Repository.defaultBranch
        readmePath = $readmePath
        expectedByteSize = [long]$Repository.readme.byteSize
        expectedGitBlobSha1 = ([string]$Repository.readme.sha).ToLowerInvariant()
        htmlUrl = $expectedHtmlUrl
        downloadUrl = $expectedDownloadUrl
    }
}

function Read-MarkdownRendererPinnedCorpus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string] $ExpectedSha256,
        [Parameter(Mandatory)][ValidateRange(1, 500)][int] $ExpectedCount,
        [ValidateRange(1, 8388608)][int] $MaxReadmeBytes = 8388608
    )

    $fullPath = Assert-MarkdownRendererNoReparsePath -Path $Path
    if (-not [IO.File]::Exists($fullPath)) { throw "Frozen README corpus manifest is missing: '$fullPath'." }
    $manifestBytes = Read-MarkdownRendererFileBytesBounded -Path $fullPath -MaxBytes 8388608
    $actualSha256 = Get-MarkdownRendererSha256 -Bytes $manifestBytes
    if ($actualSha256 -cne $ExpectedSha256.ToLowerInvariant()) {
        throw "Frozen README corpus SHA-256 mismatch: expected $ExpectedSha256, got $actualSha256."
    }

    $json = [Text.Encoding]::UTF8.GetString($manifestBytes)
    $manifest = ConvertFrom-Json -InputObject $json
    if ([int]$manifest.schemaVersion -ne 2 -or [int]$manifest.requestedCount -ne $ExpectedCount) {
        throw 'Frozen README corpus has an unexpected schema version or requested count.'
    }
    $repositories = @($manifest.repositories)
    if ($repositories.Count -ne $ExpectedCount) { throw "Frozen README corpus must contain exactly $ExpectedCount entries." }

    $seenIds = [Collections.Generic.HashSet[long]]::new()
    $seenNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $seenRanks = [Collections.Generic.HashSet[int]]::new()
    $identities = [Collections.Generic.List[object]]::new()
    foreach ($repository in $repositories) {
        $rank = [int]$repository.rank
        if ($rank -lt 1 -or $rank -gt $ExpectedCount -or -not $seenRanks.Add($rank)) {
            throw "Frozen README corpus contains a missing, duplicate, or out-of-range rank '$rank'."
        }
        if ([long]$repository.id -le 0 -or -not $seenIds.Add([long]$repository.id)) {
            throw "Frozen README corpus contains a missing or duplicate repository ID at rank $rank."
        }
        $fullName = [string]$repository.fullName
        if ($fullName -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or -not $seenNames.Add($fullName)) {
            throw "Frozen README corpus contains an invalid or duplicate repository name at rank $rank."
        }
        $commit = [string]$repository.commitSha
        if ($commit -notmatch '^[0-9a-fA-F]{40}$' -or [string]::IsNullOrWhiteSpace([string]$repository.defaultBranch)) {
            throw "Frozen README corpus contains an invalid immutable commit or branch at rank $rank."
        }
        if ($null -eq $repository.readme -or $repository.readme.available -isnot [bool] -or -not $repository.readme.available) {
            throw "Frozen README corpus does not declare an available README at rank $rank."
        }
        $path = [string]$repository.readme.path
        if ($path -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$') {
            throw "Frozen README corpus contains an unsafe or unsupported path at rank $rank."
        }
        $size = [long]$repository.readme.byteSize
        if ($size -lt 1 -or $size -gt $MaxReadmeBytes -or [double]$repository.readme.byteSize -ne [double]$size) {
            throw "Frozen README corpus contains an invalid or over-budget byte size at rank $rank."
        }
        if ([string]$repository.readme.sha -notmatch '^[0-9a-fA-F]{40}$') {
            throw "Frozen README corpus contains an invalid Git blob SHA-1 at rank $rank."
        }
        $identities.Add((Get-MarkdownRendererReadmeIdentity -Repository $repository))
    }
    for ($rank = 1; $rank -le $ExpectedCount; $rank++) {
        if (-not $seenRanks.Contains($rank)) { throw "Frozen README corpus is missing rank $rank." }
    }

    $ordered = @($identities | Sort-Object rank)
    return [pscustomobject]@{
        Path = $fullPath
        Sha256 = $actualSha256
        SchemaVersion = [int]$manifest.schemaVersion
        GeneratedAtUtc = [string]$manifest.generatedAtUtc
        RequestedCount = $ExpectedCount
        Identities = $ordered
    }
}

function Get-MarkdownRendererGitBlobObjectPath {
    param(
        [Parameter(Mandatory)][string] $ObjectDirectory,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $GitBlobSha1
    )
    $prefix = $GitBlobSha1.Substring(0, 2)
    return [IO.Path]::GetFullPath((Join-Path $ObjectDirectory (Join-Path $prefix "$GitBlobSha1.md")))
}

function Test-MarkdownRendererGitBlobObject {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][ValidateRange(1, 8388608)][long] $ExpectedByteSize,
        [Parameter(Mandatory)][string] $ExpectedGitBlobSha1
    )
    try {
        $fullPath = Assert-MarkdownRendererNoReparsePath -Path $Path
        if (-not [IO.File]::Exists($fullPath)) {
            return [pscustomobject]@{ Exists = $false; Valid = $false; Sha256 = $null; Reason = $null }
        }
        $stream = [IO.FileStream]::new($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($stream.Length -ne $ExpectedByteSize) {
                return [pscustomobject]@{ Exists = $true; Valid = $false; Sha256 = $null; Reason = 'byte-size-mismatch' }
            }
            $bytes = [byte[]]::new([int]$ExpectedByteSize)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                if ($read -le 0) { return [pscustomobject]@{ Exists = $true; Valid = $false; Sha256 = $null; Reason = 'read-error' } }
                $offset += $read
            }
            if ($stream.ReadByte() -ne -1) { return [pscustomobject]@{ Exists = $true; Valid = $false; Sha256 = $null; Reason = 'byte-size-mismatch' } }
        }
        finally { $stream.Dispose() }
        if ($bytes.Length -ne $ExpectedByteSize) {
            return [pscustomobject]@{ Exists = $true; Valid = $false; Sha256 = $null; Reason = 'byte-size-mismatch' }
        }
        $actualBlob = Get-MarkdownRendererGitBlobSha1 -Bytes $bytes
        if ($actualBlob -cne $ExpectedGitBlobSha1.ToLowerInvariant()) {
            return [pscustomobject]@{ Exists = $true; Valid = $false; Sha256 = (Get-MarkdownRendererSha256 -Bytes $bytes); Reason = 'git-blob-sha1-mismatch' }
        }
        return [pscustomobject]@{ Exists = $true; Valid = $true; Sha256 = (Get-MarkdownRendererSha256 -Bytes $bytes); Reason = $null }
    }
    catch {
        $cause = if ($null -ne $_.Exception.InnerException) { $_.Exception.InnerException } else { $_.Exception }
        $reason = if ([string]$cause.Message -ceq 'reparse-point-path-rejected') { 'reparse-point-path-rejected' } else { 'read-error' }
        return [pscustomobject]@{ Exists = $true; Valid = $false; Sha256 = $null; Reason = $reason }
    }
}

function Get-MarkdownRendererHttpRateLimitState {
    param([Parameter(Mandatory)][object] $Response, [Parameter(Mandatory)][int] $StatusCode)
    $rateLimited = ($StatusCode -eq 429)
    $retryAfterSeconds = $null
    $retryAfter = $Response.Headers.RetryAfter
    if ($null -ne $retryAfter) {
        if ($null -ne $retryAfter.Delta) { $retryAfterSeconds = ConvertTo-MarkdownRendererBoundedSeconds -Seconds $retryAfter.Delta.TotalSeconds }
        elseif ($null -ne $retryAfter.Date) { $retryAfterSeconds = ConvertTo-MarkdownRendererBoundedSeconds -Seconds ($retryAfter.Date.UtcDateTime - [DateTime]::UtcNow).TotalSeconds }
    }
    if ($StatusCode -eq 403 -and $null -ne $retryAfterSeconds) { $rateLimited = $true }
    $remainingValues = $null
    $resetValues = $null
    if ($Response.Headers.TryGetValues('X-RateLimit-Remaining', [ref]$remainingValues) -and
        [string]$remainingValues[0] -eq '0') {
        $rateLimited = $true
        if ($Response.Headers.TryGetValues('X-RateLimit-Reset', [ref]$resetValues)) {
            $resetEpoch = 0L
            if ([long]::TryParse([string]$resetValues[0], [ref]$resetEpoch)) {
                $retryAfterSeconds = [Math]::Max(0, [int][Math]::Min([int]::MaxValue, $resetEpoch - [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()))
            }
        }
    }
    return [pscustomobject]@{ RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds }
}

function Invoke-MarkdownRendererBoundedGet {
    param(
        [Parameter(Mandatory)][string] $Uri,
        [Parameter(Mandatory)][AllowNull()][object] $HttpClient,
        [Parameter(Mandatory)][ValidateRange(1, 8388608)][int] $MaxBytes,
        [Parameter(Mandatory)][ValidateSet('api.github.com', 'raw.githubusercontent.com')][string] $AllowedHost,
        [AllowNull()][scriptblock] $RequestOverride,
        [ValidateRange(1, 5)][int] $MaxAttempts = 3,
        [ValidateRange(0, 60)][int] $MaxRetryWaitSeconds = 30
    )
    $parsed = $null
    if (-not [Uri]::TryCreate($Uri, [UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -cne 'https' -or $parsed.Host -cne $AllowedHost -or $parsed.Port -ne 443 -or
        -not [string]::IsNullOrEmpty($parsed.UserInfo) -or -not [string]::IsNullOrEmpty($parsed.Fragment) -or
        -not [string]::IsNullOrEmpty($parsed.Query)) {
        throw 'request-uri-outside-allowlist'
    }
    $lastStatus = 0
    $lastRateLimited = $false
    $lastRetryAfter = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $response = $null
        $fake = $null
        $status = 0
        $rateLimited = $false
        $retryAfterSeconds = $null
        $json = $null
        $location = $null
        try {
            if ($null -ne $RequestOverride) {
                $fake = & $RequestOverride $Uri $attempt
                $status = [int]$fake.StatusCode
                $json = $fake.Json
                $location = $fake.Location
                $rateLimited = ($status -eq 429)
                if ($null -ne $fake.PSObject.Properties['RateLimited']) { $rateLimited = $rateLimited -or [bool]$fake.RateLimited }
                if ($null -ne $fake.PSObject.Properties['RetryAfterSeconds']) {
                    $parsedRetry = 0
                    if ($null -ne $fake.RetryAfterSeconds -and [int]::TryParse([string]$fake.RetryAfterSeconds, [ref]$parsedRetry)) {
                        $retryAfterSeconds = [Math]::Max(0, $parsedRetry)
                    }
                }
            }
            else {
                $response = $HttpClient.GetAsync($parsed, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                $status = [int]$response.StatusCode
                $limitState = Get-MarkdownRendererHttpRateLimitState -Response $response -StatusCode $status
                $rateLimited = [bool]$limitState.RateLimited
                $retryAfterSeconds = $limitState.RetryAfterSeconds
            }

            if ($status -ge 300 -and $status -lt 400 -or $null -ne $location) {
                return [pscustomobject]@{ Success = $false; StatusCode = $status; Bytes = $null; Reason = 'redirect-rejected'; Json = $null; RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds; Attempts = $attempt }
            }
            if ($status -eq 200) {
                [byte[]]$bytes = @()
                if ($null -ne $fake) {
                    if ($null -ne $fake.Bytes) { $bytes = [byte[]]$fake.Bytes }
                    if ($bytes.LongLength -gt $MaxBytes) {
                        return [pscustomobject]@{ Success = $false; StatusCode = $status; Bytes = $null; Reason = 'response-over-budget'; Json = $null; RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds; Attempts = $attempt }
                    }
                }
                else {
                    $contentLength = $response.Content.Headers.ContentLength
                    if ($null -ne $contentLength -and $contentLength -gt $MaxBytes) {
                        return [pscustomobject]@{ Success = $false; StatusCode = $status; Bytes = $null; Reason = 'response-over-budget'; Json = $null; RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds; Attempts = $attempt }
                    }
                    $contentStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    $memory = [IO.MemoryStream]::new([Math]::Min($MaxBytes, 65536))
                    try {
                        $buffer = [byte[]]::new(8192)
                        while (($read = $contentStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            if (($memory.Length + $read) -gt $MaxBytes) {
                                return [pscustomobject]@{ Success = $false; StatusCode = $status; Bytes = $null; Reason = 'response-over-budget'; Json = $null; RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds; Attempts = $attempt }
                            }
                            $memory.Write($buffer, 0, $read)
                        }
                        $bytes = $memory.ToArray()
                    }
                    finally { $memory.Dispose(); $contentStream.Dispose() }
                }
                return [pscustomobject]@{ Success = $true; StatusCode = $status; Bytes = [byte[]]$bytes; Reason = $null; Json = $json; RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds; Attempts = $attempt }
            }
        }
        catch {
            $status = 0
            $rateLimited = $false
            $retryAfterSeconds = $null
        }
        finally { if ($null -ne $response) { $response.Dispose() } }

        $lastStatus = $status
        $lastRateLimited = $rateLimited
        $lastRetryAfter = $retryAfterSeconds
        $retryable = ($status -eq 0 -or $status -eq 408 -or $status -eq 429 -or $status -ge 500 -or $rateLimited)
        if (-not $retryable) {
            return [pscustomobject]@{ Success = $false; StatusCode = $status; Bytes = $null; Reason = if ($rateLimited) { 'rate-limit' } else { 'http-failure' }; Json = $json; RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds; Attempts = $attempt }
        }
        if ($attempt -ge $MaxAttempts) {
            return [pscustomobject]@{ Success = $false; StatusCode = $status; Bytes = $null; Reason = if ($rateLimited) { 'rate-limit' } else { 'http-failure' }; Json = $null; RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds; Attempts = $attempt }
        }
        $delay = Get-MarkdownRendererRetryDelay -Attempt $attempt -RetryAfterSeconds $retryAfterSeconds -MaxRetryWaitSeconds $MaxRetryWaitSeconds
        if ($delay -gt $MaxRetryWaitSeconds) {
            return [pscustomobject]@{ Success = $false; StatusCode = $status; Bytes = $null; Reason = if ($rateLimited) { 'rate-limit-deferred' } else { 'retry-deferred' }; Json = $null; RateLimited = $rateLimited; RetryAfterSeconds = $retryAfterSeconds; Attempts = $attempt }
        }
        if ($delay -gt 0) { Start-Sleep -Seconds $delay }
    }
    return [pscustomobject]@{ Success = $false; StatusCode = $lastStatus; Bytes = $null; Reason = if ($lastRateLimited) { 'rate-limit' } else { 'http-failure' }; Json = $null; RateLimited = $lastRateLimited; RetryAfterSeconds = $lastRetryAfter; Attempts = $MaxAttempts }
}

function Get-MarkdownRendererGitHubTreeEntry {
    param(
        [Parameter(Mandatory)][object] $Identity,
        [Parameter(Mandatory)][string] $RepositoryPath,
        [Parameter(Mandatory)][AllowNull()][object] $HttpClient,
        [Parameter(Mandatory)][int] $MaxBytes,
        [Parameter(Mandatory)][hashtable] $TreeCache,
        [AllowNull()][scriptblock] $ApiRequestOverride,
        [ValidateRange(1, 5)][int] $MaxAttempts = 3,
        [ValidateRange(0, 60)][int] $MaxRetryWaitSeconds = 30
    )

    $parts = ([string]$Identity.fullName).Split([char[]]@('/'), 2)
    $owner = [Uri]::EscapeDataString($parts[0])
    $repository = [Uri]::EscapeDataString($parts[1])
    $commit = ([string]$Identity.commitSha).ToLowerInvariant()
    $commitUri = "https://api.github.com/repos/$owner/$repository/git/commits/$commit"
    $commitResponse = Invoke-MarkdownRendererBoundedGet -Uri $commitUri -HttpClient $HttpClient -MaxBytes $MaxBytes -AllowedHost 'api.github.com' -RequestOverride $ApiRequestOverride -MaxAttempts $MaxAttempts -MaxRetryWaitSeconds $MaxRetryWaitSeconds
    if (-not $commitResponse.Success) {
        throw $(if ($commitResponse.Reason -ceq 'response-over-budget') { 'git-api-response-over-budget' } elseif ($commitResponse.Reason -in @('rate-limit', 'rate-limit-deferred')) { 'git-api-rate-limit-deferred' } elseif ($commitResponse.Reason -ceq 'retry-deferred') { 'git-api-retry-deferred' } else { 'git-api-http-failure' })
    }
    $commitObject = if ($null -ne $commitResponse.Json) { $commitResponse.Json } else { ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString($commitResponse.Bytes)) }
    if ([string]$commitObject.sha -cne $commit -or [string]$commitObject.tree.sha -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'git-api-commit-proof-invalid'
    }
    $rootTreeSha = ([string]$commitObject.tree.sha).ToLowerInvariant()
    $currentTreeSha = $rootTreeSha
    $segments = $RepositoryPath.Split('/')
    for ($index = 0; $index -lt $segments.Length; $index++) {
        if (-not $TreeCache.ContainsKey($currentTreeSha)) {
            $treeUri = "https://api.github.com/repos/$owner/$repository/git/trees/$currentTreeSha"
            $treeResponse = Invoke-MarkdownRendererBoundedGet -Uri $treeUri -HttpClient $HttpClient -MaxBytes $MaxBytes -AllowedHost 'api.github.com' -RequestOverride $ApiRequestOverride -MaxAttempts $MaxAttempts -MaxRetryWaitSeconds $MaxRetryWaitSeconds
            if (-not $treeResponse.Success) {
                throw $(if ($treeResponse.Reason -ceq 'response-over-budget') { 'git-api-response-over-budget' } elseif ($treeResponse.Reason -in @('rate-limit', 'rate-limit-deferred')) { 'git-api-rate-limit-deferred' } elseif ($treeResponse.Reason -ceq 'retry-deferred') { 'git-api-retry-deferred' } else { 'git-api-http-failure' })
            }
            $treeObject = if ($null -ne $treeResponse.Json) { $treeResponse.Json } else { ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString($treeResponse.Bytes)) }
            if ([string]$treeObject.sha -cne $currentTreeSha -or [bool]$treeObject.truncated) { throw 'git-api-tree-proof-invalid' }
            $TreeCache[$currentTreeSha] = @($treeObject.tree)
        }
        $matches = @($TreeCache[$currentTreeSha] | Where-Object { [string]$_.path -ceq [string]$segments[$index] })
        if ($matches.Count -ne 1) { throw 'git-tree-path-entry-missing-or-ambiguous' }
        $entry = $matches[0]
        if ([string]$entry.sha -notmatch '^[0-9a-fA-F]{40}$') { throw 'git-tree-entry-hash-invalid' }
        if ($index -lt ($segments.Length - 1)) {
            if ([string]$entry.mode -cne '040000' -or [string]$entry.type -cne 'tree') {
                throw 'git-tree-parent-is-not-a-tree'
            }
            $currentTreeSha = ([string]$entry.sha).ToLowerInvariant()
            continue
        }
        return [pscustomobject]@{
            Entry = $entry
            RootTreeSha = $rootTreeSha
            ParentTreeSha = $currentTreeSha
            FullPath = $RepositoryPath
        }
    }
    throw 'git-tree-path-entry-missing'
}

function Resolve-MarkdownRendererSymlinkTargetPath {
    param([Parameter(Mandatory)][string] $LinkPath, [Parameter(Mandatory)][byte[]] $LinkBytes)
    try { $target = [Text.UTF8Encoding]::new($false, $true).GetString($LinkBytes) }
    catch { throw 'symlink-target-invalid-utf8' }
    if ([string]::IsNullOrWhiteSpace($target) -or $target.Contains([char]0) -or
        $target.Contains('\') -or $target.StartsWith('/') -or $target -match '[\r\n?#:]') {
        throw 'symlink-target-invalid'
    }
    $stack = [Collections.Generic.List[string]]::new()
    $linkSegments = $LinkPath.Split('/')
    for ($index = 0; $index -lt ($linkSegments.Length - 1); $index++) { $stack.Add($linkSegments[$index]) }
    $targetSegments = $target.Split('/')
    foreach ($segment in $targetSegments) {
        if ([string]::IsNullOrEmpty($segment)) { throw 'symlink-target-invalid' }
        if ($segment -ceq '.') { continue }
        if ($segment -ceq '..') {
            if ($stack.Count -eq 0) { throw 'symlink-target-escapes-repository' }
            $stack.RemoveAt($stack.Count - 1)
            continue
        }
        if ($segment -notmatch '^[\p{L}\p{N}._ -]+$') { throw 'symlink-target-invalid' }
        $stack.Add($segment)
    }
    if ($stack.Count -eq 0) { throw 'symlink-target-invalid' }
    return ($stack -join '/')
}

function Resolve-MarkdownRendererPinnedReadmeSymlink {
    param(
        [Parameter(Mandatory)][object] $Identity,
        [Parameter(Mandatory)][byte[]] $InitialLinkBytes,
        [Parameter(Mandatory)][AllowNull()][object] $HttpClient,
        [Parameter(Mandatory)][int] $MaxReadmeBytes,
        [ValidateRange(1, 4)][int] $MaxLinkDepth = 4,
        [AllowNull()][scriptblock] $ApiRequestOverride,
        [AllowNull()][scriptblock] $RawRequestOverride,
        [ValidateRange(1, 5)][int] $MaxAttempts = 3,
        [ValidateRange(0, 60)][int] $MaxRetryWaitSeconds = 30
    )
    try {
        $treeCache = @{}
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $proofs = [Collections.Generic.List[object]]::new()
        $linkPath = [string]$Identity.readmePath
        $linkBytes = [byte[]]$InitialLinkBytes
        $parts = ([string]$Identity.fullName).Split([char[]]@('/'), 2)
        $owner = $parts[0]
        $repository = $parts[1]
        $commit = ([string]$Identity.commitSha).ToLowerInvariant()
        $rootTreeSha = $null

        for ($depth = 0; $depth -lt $MaxLinkDepth; $depth++) {
            if (-not $seen.Add($linkPath)) { throw 'symlink-loop' }
            $linkProof = Get-MarkdownRendererGitHubTreeEntry -Identity $Identity -RepositoryPath $linkPath -HttpClient $HttpClient -MaxBytes 8388608 -TreeCache $treeCache -ApiRequestOverride $ApiRequestOverride -MaxAttempts $MaxAttempts -MaxRetryWaitSeconds $MaxRetryWaitSeconds
            if ($null -eq $rootTreeSha) { $rootTreeSha = [string]$linkProof.RootTreeSha }
            elseif ([string]$rootTreeSha -cne [string]$linkProof.RootTreeSha) { throw 'git-api-root-tree-changed' }
            $linkEntry = $linkProof.Entry
            if ([string]$linkEntry.mode -cne '120000' -or [string]$linkEntry.type -cne 'blob') { throw 'manifest-path-is-not-git-symlink' }
            $actualLinkBlob = Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes
            if ($actualLinkBlob -cne ([string]$linkEntry.sha).ToLowerInvariant()) { throw 'symlink-blob-proof-mismatch' }

            $targetPath = Resolve-MarkdownRendererSymlinkTargetPath -LinkPath $linkPath -LinkBytes $linkBytes
            if ($seen.Contains($targetPath)) { throw 'symlink-loop' }
            $targetProof = Get-MarkdownRendererGitHubTreeEntry -Identity $Identity -RepositoryPath $targetPath -HttpClient $HttpClient -MaxBytes 8388608 -TreeCache $treeCache -ApiRequestOverride $ApiRequestOverride -MaxAttempts $MaxAttempts -MaxRetryWaitSeconds $MaxRetryWaitSeconds
            if ([string]$rootTreeSha -cne [string]$targetProof.RootTreeSha) { throw 'git-api-root-tree-changed' }
            $targetEntry = $targetProof.Entry
            $proofs.Add([pscustomobject][ordered]@{
                linkPath = $linkPath
                linkMode = [string]$linkEntry.mode
                linkType = [string]$linkEntry.type
                linkGitBlobSha1 = ([string]$linkEntry.sha).ToLowerInvariant()
                fetchedLinkGitBlobSha1 = $actualLinkBlob
                linkParentTreeSha = ([string]$linkProof.ParentTreeSha).ToLowerInvariant()
                targetText = [Text.UTF8Encoding]::new($false, $true).GetString($linkBytes)
                normalizedTargetPath = $targetPath
                targetMode = [string]$targetEntry.mode
                targetType = [string]$targetEntry.type
                targetGitObjectSha = ([string]$targetEntry.sha).ToLowerInvariant()
                targetParentTreeSha = ([string]$targetProof.ParentTreeSha).ToLowerInvariant()
            })

            $targetUriPath = ($targetPath.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
            $targetUrl = "https://raw.githubusercontent.com/$owner/$repository/$commit/$targetUriPath"
            if ([string]$targetEntry.mode -ceq '120000' -and [string]$targetEntry.type -ceq 'blob') {
                $nextLink = Invoke-MarkdownRendererBoundedGet -Uri $targetUrl -HttpClient $HttpClient -MaxBytes $MaxReadmeBytes -AllowedHost 'raw.githubusercontent.com' -RequestOverride $RawRequestOverride -MaxAttempts $MaxAttempts -MaxRetryWaitSeconds $MaxRetryWaitSeconds
                if (-not $nextLink.Success) { throw $(if ($nextLink.Reason -in @('rate-limit', 'rate-limit-deferred')) { 'symlink-target-rate-limit-deferred' } elseif ($nextLink.Reason -ceq 'retry-deferred') { 'symlink-target-retry-deferred' } elseif ($nextLink.Reason -ceq 'redirect-rejected') { 'symlink-target-redirect-rejected' } else { 'symlink-target-fetch-failed' }) }
                if ((Get-MarkdownRendererGitBlobSha1 -Bytes $nextLink.Bytes) -cne ([string]$targetEntry.sha).ToLowerInvariant()) {
                    throw 'symlink-target-blob-proof-mismatch'
                }
                $linkPath = $targetPath
                $linkBytes = [byte[]]$nextLink.Bytes
                continue
            }
            if ([string]$targetEntry.type -ceq 'commit' -or [string]$targetEntry.mode -ceq '160000') { throw 'symlink-target-is-submodule' }
            if ([string]$targetEntry.type -cne 'blob' -or [string]$targetEntry.mode -notin @('100644', '100755')) {
                throw 'symlink-target-is-not-a-regular-file'
            }
            if (([string]$targetEntry.sha).ToLowerInvariant() -cne ([string]$Identity.expectedGitBlobSha1).ToLowerInvariant()) {
                throw 'symlink-target-git-blob-does-not-match-manifest'
            }
            $finalResponse = Invoke-MarkdownRendererBoundedGet -Uri $targetUrl -HttpClient $HttpClient -MaxBytes $MaxReadmeBytes -AllowedHost 'raw.githubusercontent.com' -RequestOverride $RawRequestOverride -MaxAttempts $MaxAttempts -MaxRetryWaitSeconds $MaxRetryWaitSeconds
            if (-not $finalResponse.Success) { throw $(if ($finalResponse.Reason -in @('rate-limit', 'rate-limit-deferred')) { 'symlink-target-rate-limit-deferred' } elseif ($finalResponse.Reason -ceq 'retry-deferred') { 'symlink-target-retry-deferred' } elseif ($finalResponse.Reason -ceq 'redirect-rejected') { 'symlink-target-redirect-rejected' } else { 'symlink-target-fetch-failed' }) }
            $finalBytes = [byte[]]$finalResponse.Bytes
            $finalSha1 = Get-MarkdownRendererGitBlobSha1 -Bytes $finalBytes
            if ($finalBytes.Length -ne [long]$Identity.expectedByteSize -or
                $finalSha1 -cne ([string]$Identity.expectedGitBlobSha1).ToLowerInvariant()) {
                throw 'target-content-mismatch'
            }
            $proof = [pscustomobject][ordered]@{
                schemaVersion = 1
                resolver = 'github-git-tree-symlink-v1'
                commitSha = $commit
                rootTreeSha = [string]$rootTreeSha
                linkChain = @($proofs)
                finalTargetPath = $targetPath
                finalTreeEntrySha = ([string]$targetEntry.sha).ToLowerInvariant()
                finalContentGitBlobSha1 = $finalSha1
                finalContentByteSize = [long]$finalBytes.Length
            }
            $proof | Add-Member -NotePropertyName proofSha256 -NotePropertyValue (Get-MarkdownRendererSymlinkProofSha256 -Proof $proof)
            return [pscustomobject]@{
                Success = $true
                Bytes = $finalBytes
                Proof = $proof
                Failure = $null
            }
        }
        throw 'symlink-chain-exceeds-limit'
    }
    catch {
        $safeFailure = [string]$_.Exception.Message
        if ($safeFailure -notmatch '^(request-uri-outside-allowlist|git-api-response-over-budget|git-api-http-failure|git-api-rate-limit-deferred|git-api-retry-deferred|git-api-root-tree-changed|git-api-commit-proof-invalid|git-api-tree-proof-invalid|git-tree-path-entry-missing-or-ambiguous|git-tree-entry-hash-invalid|git-tree-parent-is-not-a-tree|git-tree-path-entry-missing|symlink-target-invalid-utf8|symlink-target-invalid|symlink-target-escapes-repository|symlink-loop|manifest-path-is-not-git-symlink|symlink-blob-proof-mismatch|symlink-target-fetch-failed|symlink-target-rate-limit-deferred|symlink-target-retry-deferred|symlink-target-redirect-rejected|symlink-target-blob-proof-mismatch|symlink-target-is-submodule|symlink-target-is-not-a-regular-file|symlink-target-git-blob-does-not-match-manifest|target-content-mismatch|symlink-chain-exceeds-limit|legacy-proof-tree-facts-changed)$') {
            $safeFailure = 'symlink-resolution-failed'
        }
        return [pscustomobject]@{ Success = $false; Bytes = $null; Proof = $null; Failure = $safeFailure }
    }
}

function Test-MarkdownRendererSymlinkResolutionShape {
    param([Parameter(Mandatory)][object] $Proof, [Parameter(Mandatory)][object] $Identity, [switch] $RequireRootTreeSha)
    try {
        if ([string]$Proof.resolver -cne 'github-git-tree-symlink-v1' -or
            [string]$Proof.commitSha -cne [string]$Identity.commitSha -or
            [string]$Proof.finalContentGitBlobSha1 -cne [string]$Identity.expectedGitBlobSha1 -or
            [long]$Proof.finalContentByteSize -ne [long]$Identity.expectedByteSize -or
            [string]$Proof.finalTreeEntrySha -cne [string]$Identity.expectedGitBlobSha1) { return $false }
        $rootTreeProperty = $Proof.PSObject.Properties['rootTreeSha']
        if ($RequireRootTreeSha -and $null -eq $rootTreeProperty) { return $false }
        if ($RequireRootTreeSha -and [int]$Proof.schemaVersion -ne 1) { return $false }
        if ($RequireRootTreeSha) {
            $requiredTopLevel = @('schemaVersion', 'resolver', 'commitSha', 'rootTreeSha', 'linkChain', 'finalTargetPath', 'finalTreeEntrySha', 'finalContentGitBlobSha1', 'finalContentByteSize', 'proofSha256')
            $actualTopLevel = @($Proof.PSObject.Properties | ForEach-Object { $_.Name })
            if ($actualTopLevel.Count -ne $requiredTopLevel.Count -or
                @($requiredTopLevel | Where-Object { $_ -notin $actualTopLevel }).Count -ne 0) { return $false }
        }
        if ($null -ne $rootTreeProperty -and [string]$rootTreeProperty.Value -notmatch '^[0-9a-f]{40}$') { return $false }
        $chain = @($Proof.linkChain)
        if ($chain.Count -lt 1 -or $chain.Count -gt 4) { return $false }
        $nextPath = [string]$Identity.readmePath
        foreach ($link in $chain) {
            if ($RequireRootTreeSha) {
                $requiredLinkProperties = @('linkPath', 'linkMode', 'linkType', 'linkGitBlobSha1', 'fetchedLinkGitBlobSha1', 'linkParentTreeSha', 'targetText', 'normalizedTargetPath', 'targetMode', 'targetType', 'targetGitObjectSha', 'targetParentTreeSha')
                $actualLinkProperties = @($link.PSObject.Properties | ForEach-Object { $_.Name })
                if ($actualLinkProperties.Count -ne $requiredLinkProperties.Count -or
                    @($requiredLinkProperties | Where-Object { $_ -notin $actualLinkProperties }).Count -ne 0) { return $false }
            }
            if ([string]$link.linkPath -cne $nextPath -or [string]$link.linkMode -cne '120000' -or
                [string]$link.linkGitBlobSha1 -notmatch '^[0-9a-f]{40}$' -or
                [string]$link.fetchedLinkGitBlobSha1 -cne [string]$link.linkGitBlobSha1 -or
                [string]$link.linkParentTreeSha -notmatch '^[0-9a-f]{40}$' -or
                [string]$link.targetParentTreeSha -notmatch '^[0-9a-f]{40}$' -or
                [string]$link.targetGitObjectSha -notmatch '^[0-9a-f]{40}$') { return $false }
            $linkTypeProperty = $link.PSObject.Properties['linkType']
            if ($RequireRootTreeSha -and $null -eq $linkTypeProperty) { return $false }
            if ($null -ne $linkTypeProperty -and [string]$linkTypeProperty.Value -cne 'blob') { return $false }
            $pointerBytes = [Text.UTF8Encoding]::new($false, $true).GetBytes([string]$link.targetText)
            if ((Get-MarkdownRendererGitBlobSha1 -Bytes $pointerBytes) -cne [string]$link.linkGitBlobSha1) { return $false }
            $normalized = Resolve-MarkdownRendererSymlinkTargetPath -LinkPath $nextPath -LinkBytes $pointerBytes
            if ($normalized -cne [string]$link.normalizedTargetPath) { return $false }
            $nextPath = $normalized
        }
        $last = $chain[$chain.Count - 1]
        if ([string]$Proof.finalTargetPath -cne $nextPath -or
            [string]$last.targetMode -notin @('100644', '100755') -or
            [string]$last.targetType -cne 'blob' -or
            [string]$last.targetGitObjectSha -cne [string]$Identity.expectedGitBlobSha1) { return $false }
        for ($index = 0; $index -lt ($chain.Count - 1); $index++) {
            if ([string]$chain[$index].targetMode -cne '120000' -or
                [string]$chain[$index].targetType -cne 'blob' -or
                [string]$chain[$index].targetGitObjectSha -cne [string]$chain[$index + 1].linkGitBlobSha1) { return $false }
        }
        if ($null -ne $rootTreeProperty) {
            if (($chain[0].linkPath -notmatch '/') -and [string]$chain[0].linkParentTreeSha -cne [string]$rootTreeProperty.Value) { return $false }
            if (($chain[$chain.Count - 1].normalizedTargetPath -notmatch '/') -and [string]$chain[$chain.Count - 1].targetParentTreeSha -cne [string]$rootTreeProperty.Value) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-MarkdownRendererSymlinkResolutionProof {
    param([Parameter(Mandatory)][object] $Proof, [Parameter(Mandatory)][object] $Identity)
    if (-not (Test-MarkdownRendererSymlinkResolutionShape -Proof $Proof -Identity $Identity -RequireRootTreeSha)) { return $false }
    $checksumProperty = $Proof.PSObject.Properties['proofSha256']
    if ($null -eq $checksumProperty -or [string]$checksumProperty.Value -notmatch '^[0-9a-f]{64}$') { return $false }
    return ([string]$checksumProperty.Value -ceq (Get-MarkdownRendererSymlinkProofSha256 -Proof $Proof))
}

function Upgrade-MarkdownRendererLegacySymlinkResolutionProof {
    param(
        [Parameter(Mandatory)][object] $LegacyProof,
        [Parameter(Mandatory)][object] $Identity,
        [Parameter(Mandatory)][AllowNull()][object] $HttpClient,
        [Parameter(Mandatory)][int] $MaxBytes,
        [Parameter(Mandatory)][int] $MaxAttempts,
        [Parameter(Mandatory)][int] $MaxRetryWaitSeconds,
        [AllowNull()][scriptblock] $ApiRequestOverride
    )
    if ($null -ne $LegacyProof.PSObject.Properties['schemaVersion'] -or
        $null -ne $LegacyProof.PSObject.Properties['proofSha256'] -or
        -not (Test-MarkdownRendererSymlinkResolutionShape -Proof $LegacyProof -Identity $Identity)) {
        throw 'existing-symlink-proof-invalid; explicit migration is required'
    }
    $treeCache = @{}
    $rootTreeSha = $null
    $upgradedChain = [Collections.Generic.List[object]]::new()
    foreach ($legacyLink in @($LegacyProof.linkChain)) {
        $linkProof = Get-MarkdownRendererGitHubTreeEntry `
            -Identity $Identity -RepositoryPath ([string]$legacyLink.linkPath) `
            -HttpClient $HttpClient -MaxBytes $MaxBytes -TreeCache $treeCache `
            -ApiRequestOverride $ApiRequestOverride -MaxAttempts $MaxAttempts `
            -MaxRetryWaitSeconds $MaxRetryWaitSeconds
        if ($null -eq $rootTreeSha) { $rootTreeSha = [string]$linkProof.RootTreeSha }
        elseif ([string]$rootTreeSha -cne [string]$linkProof.RootTreeSha) { throw 'legacy-proof-tree-facts-changed' }
        $entry = $linkProof.Entry
        if ([string]$entry.mode -cne [string]$legacyLink.linkMode -or
            [string]$entry.type -cne 'blob' -or
            ([string]$entry.sha).ToLowerInvariant() -cne [string]$legacyLink.linkGitBlobSha1 -or
            ([string]$linkProof.ParentTreeSha).ToLowerInvariant() -cne [string]$legacyLink.linkParentTreeSha) {
            throw 'legacy-proof-tree-facts-changed'
        }
        $pointerBytes = [Text.UTF8Encoding]::new($false, $true).GetBytes([string]$legacyLink.targetText)
        $targetPath = Resolve-MarkdownRendererSymlinkTargetPath -LinkPath ([string]$legacyLink.linkPath) -LinkBytes $pointerBytes
        if ($targetPath -cne [string]$legacyLink.normalizedTargetPath) { throw 'legacy-proof-tree-facts-changed' }
        $targetProof = Get-MarkdownRendererGitHubTreeEntry `
            -Identity $Identity -RepositoryPath $targetPath `
            -HttpClient $HttpClient -MaxBytes $MaxBytes -TreeCache $treeCache `
            -ApiRequestOverride $ApiRequestOverride -MaxAttempts $MaxAttempts `
            -MaxRetryWaitSeconds $MaxRetryWaitSeconds
        if ([string]$rootTreeSha -cne [string]$targetProof.RootTreeSha -or
            [string]$targetProof.Entry.mode -cne [string]$legacyLink.targetMode -or
            [string]$targetProof.Entry.type -cne [string]$legacyLink.targetType -or
            ([string]$targetProof.Entry.sha).ToLowerInvariant() -cne [string]$legacyLink.targetGitObjectSha -or
            ([string]$targetProof.ParentTreeSha).ToLowerInvariant() -cne [string]$legacyLink.targetParentTreeSha) {
            throw 'legacy-proof-tree-facts-changed'
        }
        $upgradedChain.Add([pscustomobject][ordered]@{
            linkPath = [string]$legacyLink.linkPath
            linkMode = [string]$legacyLink.linkMode
            linkType = [string]$entry.type
            linkGitBlobSha1 = [string]$legacyLink.linkGitBlobSha1
            fetchedLinkGitBlobSha1 = [string]$legacyLink.fetchedLinkGitBlobSha1
            linkParentTreeSha = [string]$legacyLink.linkParentTreeSha
            targetText = [string]$legacyLink.targetText
            normalizedTargetPath = [string]$legacyLink.normalizedTargetPath
            targetMode = [string]$legacyLink.targetMode
            targetType = [string]$legacyLink.targetType
            targetGitObjectSha = [string]$legacyLink.targetGitObjectSha
            targetParentTreeSha = [string]$legacyLink.targetParentTreeSha
        })
    }
    if ($null -eq $rootTreeSha) { throw 'legacy-proof-tree-facts-changed' }
    $upgraded = [pscustomobject][ordered]@{
        schemaVersion = 1
        resolver = [string]$LegacyProof.resolver
        commitSha = [string]$LegacyProof.commitSha
        rootTreeSha = $rootTreeSha
        linkChain = @($upgradedChain)
        finalTargetPath = [string]$LegacyProof.finalTargetPath
        finalTreeEntrySha = [string]$LegacyProof.finalTreeEntrySha
        finalContentGitBlobSha1 = [string]$LegacyProof.finalContentGitBlobSha1
        finalContentByteSize = [long]$LegacyProof.finalContentByteSize
    }
    $upgraded | Add-Member -NotePropertyName proofSha256 -NotePropertyValue (Get-MarkdownRendererSymlinkProofSha256 -Proof $upgraded)
    if (-not (Test-MarkdownRendererSymlinkResolutionShape -Proof $upgraded -Identity $Identity -RequireRootTreeSha)) {
        throw 'legacy-proof-tree-facts-changed'
    }
    return $upgraded
}

function Write-MarkdownRendererBytesAtomically {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][byte[]] $Bytes)
    $fullPath = Assert-MarkdownRendererNoReparsePath -Path $Path
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [void](Assert-MarkdownRendererNoReparsePath -Path $directory)
    $tempPath = "$fullPath.$([guid]::NewGuid().ToString('N')).tmp"
    [void](Assert-MarkdownRendererNoReparsePath -Path $tempPath)
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
        [void](Assert-MarkdownRendererNoReparsePath -Path $tempPath)
        [void](Assert-MarkdownRendererNoReparsePath -Path $fullPath)
        try { [IO.File]::Move($tempPath, $fullPath) }
        catch [IO.IOException] {
            if (-not [IO.File]::Exists($fullPath)) { throw }
        }
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ([IO.File]::Exists($tempPath)) { [IO.File]::Delete($tempPath) }
    }
}

function Write-MarkdownRendererJsonAtomically {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][object] $Value)
    $fullPath = Assert-MarkdownRendererNoReparsePath -Path $Path
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [void](Assert-MarkdownRendererNoReparsePath -Path $directory)
    $tempPath = "$fullPath.$([guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$tempPath.bak"
    $json = ($Value | ConvertTo-Json -Depth 20) + "`n"
    $jsonBytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    [void](Assert-MarkdownRendererNoReparsePath -Path $tempPath)
    [void](Assert-MarkdownRendererNoReparsePath -Path $backupPath)
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Write($jsonBytes, 0, $jsonBytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
        [void](Assert-MarkdownRendererNoReparsePath -Path $tempPath)
        [void](Assert-MarkdownRendererNoReparsePath -Path $fullPath)
        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Replace($tempPath, $fullPath, $backupPath)
            if ([IO.File]::Exists($backupPath)) { [IO.File]::Delete($backupPath) }
        }
        else { [IO.File]::Move($tempPath, $fullPath) }
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ([IO.File]::Exists($tempPath)) { [IO.File]::Delete($tempPath) }
        if ([IO.File]::Exists($backupPath)) { [IO.File]::Delete($backupPath) }
    }
}

function Get-MarkdownRendererRetryDelay {
    param(
        [Parameter(Mandatory)][int] $Attempt,
        [AllowNull()][Nullable[int]] $RetryAfterSeconds,
        [Parameter(Mandatory)][int] $MaxRetryWaitSeconds
    )
    if ($null -ne $RetryAfterSeconds) { return [int]$RetryAfterSeconds }
    return [Math]::Min([int][Math]::Pow(2, [Math]::Max(0, $Attempt - 1)), $MaxRetryWaitSeconds + 1)
}

function Start-MarkdownRendererReadmeRequest {
    param(
        [Parameter(Mandatory)][object] $HttpClient,
        [Parameter(Mandatory)][object] $Identity,
        [Parameter(Mandatory)][Threading.CancellationToken] $CancellationToken
    )

    return $HttpClient.GetAsync(
        [Uri][string]$Identity.downloadUrl,
        [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
        $CancellationToken)
}

function Invoke-MarkdownRendererReadmeFetch {
    param(
        [Parameter(Mandatory)][object] $Identity,
        [Parameter(Mandatory)][int] $MaxAttempts,
        [Parameter(Mandatory)][int] $MaxReadmeBytes,
        [Parameter(Mandatory)][int] $MaxRetryWaitSeconds,
        [AllowNull()][object] $HttpClient,
        [AllowNull()][object] $InitialTask,
        [AllowNull()][object] $InitialCancellationTokenSource = $null,
        [AllowNull()][scriptblock] $RequestOverride
    )

    $lastCategory = 'network-error'
    $lastHttpStatus = $null
    $lastMessage = 'network-error'
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $statusCode = 0
        $bytes = $null
        $retryAfterSeconds = $null
        $rateLimited = $false
        $requestCancellation = $null
        $httpResponse = $null
        try {
            if ($null -ne $RequestOverride) {
                $response = & $RequestOverride $Identity $attempt
                $statusCode = [int]$response.StatusCode
                $bytes = $response.Bytes
                $retryAfterSeconds = $response.RetryAfterSeconds
                $rateLimited = [bool]$response.RateLimited
                $safeMessage = if ($null -ne $response.SafeMessage) { [string]$response.SafeMessage } else { 'http-error' }
            }
            else {
                if ($attempt -eq 1 -and $null -ne $InitialTask) {
                    $task = $InitialTask
                    $requestCancellation = $InitialCancellationTokenSource
                    if ($null -eq $requestCancellation) {
                        $requestCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(45))
                    }
                }
                else {
                    $requestCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(45))
                    $task = Start-MarkdownRendererReadmeRequest `
                        -HttpClient $HttpClient `
                        -Identity $Identity `
                        -CancellationToken $requestCancellation.Token
                }
                $httpResponse = $task.GetAwaiter().GetResult()
                $statusCode = [int]$httpResponse.StatusCode
                if ($statusCode -eq 200) {
                    $contentLength = $httpResponse.Content.Headers.ContentLength
                    if ($null -ne $contentLength -and $contentLength -gt $MaxReadmeBytes) {
                        $requestCancellation.Cancel()
                        return [pscustomobject]@{ Success = $false; Attempts = $attempt; Category = 'upstream-content-mismatch'; HttpStatus = $statusCode; SafeMessage = 'response-over-budget'; Retry = $false; Bytes = $null }
                    }
                    $contentStream = $null
                    $memory = $null
                    try {
                        $contentStream = $httpResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                        $memory = [IO.MemoryStream]::new([Math]::Min($MaxReadmeBytes, 65536))
                        $buffer = [byte[]]::new(8192)
                        while ($true) {
                            $remaining = $MaxReadmeBytes - [int]$memory.Length
                            $readLimit = [int][Math]::Min($buffer.Length, $remaining + 1)
                            $read = $contentStream.ReadAsync($buffer, 0, $readLimit, $requestCancellation.Token).GetAwaiter().GetResult()
                            if ($read -le 0) { break }
                            if ($read -gt $remaining) {
                                $requestCancellation.Cancel()
                                return [pscustomobject]@{ Success = $false; Attempts = $attempt; Category = 'upstream-content-mismatch'; HttpStatus = $statusCode; SafeMessage = 'response-over-budget'; Retry = $false; Bytes = $null }
                            }
                            $memory.Write($buffer, 0, $read)
                        }
                        $bytes = $memory.ToArray()
                    }
                    finally {
                        if ($null -ne $memory) { $memory.Dispose() }
                        if ($null -ne $contentStream) { $contentStream.Dispose() }
                    }
                }
                $rateLimited = ($statusCode -eq 429)
                $retryAfter = $httpResponse.Headers.RetryAfter
                if ($null -ne $retryAfter) {
                    if ($null -ne $retryAfter.Delta) { $retryAfterSeconds = ConvertTo-MarkdownRendererBoundedSeconds -Seconds $retryAfter.Delta.TotalSeconds }
                    elseif ($null -ne $retryAfter.Date) { $retryAfterSeconds = ConvertTo-MarkdownRendererBoundedSeconds -Seconds ($retryAfter.Date.UtcDateTime - [DateTime]::UtcNow).TotalSeconds }
                }
                if ($statusCode -eq 403 -and $null -ne $retryAfterSeconds) { $rateLimited = $true }
                $remainingValues = $null
                $resetValues = $null
                if ($httpResponse.Headers.TryGetValues('X-RateLimit-Remaining', [ref]$remainingValues) -and
                    [string]$remainingValues[0] -eq '0') {
                    $rateLimited = $true
                    if ($httpResponse.Headers.TryGetValues('X-RateLimit-Reset', [ref]$resetValues)) {
                        $resetEpoch = 0L
                        if ([long]::TryParse([string]$resetValues[0], [ref]$resetEpoch)) {
                            $retryAfterSeconds = [Math]::Max(0, [int][Math]::Min([int]::MaxValue, $resetEpoch - [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()))
                        }
                    }
                }
                $safeMessage = if ($rateLimited) { 'rate-limited' } else { 'http-error' }
                if ($statusCode -eq 200) {
                    return [pscustomobject]@{ Success = $true; Attempts = $attempt; Category = $null; HttpStatus = 200; SafeMessage = $null; Retry = $false; Bytes = [byte[]]$bytes }
                }
            }
            if ($null -ne $RequestOverride -and $statusCode -eq 200) {
                return [pscustomobject]@{ Success = $true; Attempts = $attempt; Category = $null; HttpStatus = 200; SafeMessage = $null; Retry = $false; Bytes = [byte[]]$bytes }
            }
        }
        catch {
            $statusCode = 0
            $safeMessage = 'network-error'
            $bytes = $null
        }
        finally {
            if ($null -ne $httpResponse) { $httpResponse.Dispose() }
            if ($null -ne $requestCancellation) { $requestCancellation.Dispose() }
        }

        $lastHttpStatus = if ($statusCode -gt 0) { $statusCode } else { $null }
        $lastMessage = $safeMessage
        $lastCategory = if ($rateLimited) { 'upstream-rate-limit' } elseif ($statusCode -gt 0) { 'upstream-http-failure' } else { 'upstream-network-failure' }
        $retryable = ($statusCode -eq 0 -or $statusCode -eq 408 -or $statusCode -eq 429 -or $statusCode -ge 500 -or $rateLimited)
        if (-not $retryable) {
            return [pscustomobject]@{ Success = $false; Attempts = $attempt; Category = $lastCategory; HttpStatus = $lastHttpStatus; SafeMessage = $lastMessage; Retry = $false; Bytes = $null }
        }

        $delay = Get-MarkdownRendererRetryDelay -Attempt $attempt -RetryAfterSeconds $retryAfterSeconds -MaxRetryWaitSeconds $MaxRetryWaitSeconds
        if ($delay -gt $MaxRetryWaitSeconds) {
            return [pscustomobject]@{ Success = $false; Attempts = $attempt; Category = if ($rateLimited) { 'upstream-rate-limit' } else { 'upstream-retry-deferred' }; HttpStatus = $lastHttpStatus; SafeMessage = 'retry-after-exceeds-run-wait-budget'; Retry = $true; Bytes = $null }
        }
        if ($attempt -ge $MaxAttempts) {
            return [pscustomobject]@{ Success = $false; Attempts = $attempt; Category = $lastCategory; HttpStatus = $lastHttpStatus; SafeMessage = $lastMessage; Retry = [bool]$rateLimited; Bytes = $null }
        }
        if ($delay -gt 0) { Start-Sleep -Seconds $delay }
    }
    return [pscustomobject]@{ Success = $false; Attempts = $MaxAttempts; Category = $lastCategory; HttpStatus = $lastHttpStatus; SafeMessage = $lastMessage; Retry = $false; Bytes = $null }
}

function Invoke-MarkdownRendererTop500Acquisition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object] $Corpus,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [ValidateRange(1, 8)][int] $MaxConcurrency = 4,
        [ValidateRange(1, 5)][int] $MaxAttempts = 3,
        [ValidateRange(1, 8388608)][int] $MaxReadmeBytes = 8388608,
        [ValidateRange(0, 60)][int] $MaxRetryWaitSeconds = 30,
        [ValidateRange(1, 4)][int] $MaxSymlinkDepth = 4,
        [AllowNull()][scriptblock] $RequestOverride,
        [AllowNull()][scriptblock] $ApiRequestOverride,
        [AllowNull()][scriptblock] $RawRequestOverride
    )

    $outputPath = Assert-MarkdownRendererNoReparsePath -Path $OutputDirectory
    $objectDirectory = Join-Path $outputPath 'objects/git-blob-sha1'
    [void](Assert-MarkdownRendererNoReparsePath -Path $objectDirectory)
    [IO.Directory]::CreateDirectory($outputPath) | Out-Null
    [IO.Directory]::CreateDirectory($objectDirectory) | Out-Null
    [void](Assert-MarkdownRendererNoReparsePath -Path $outputPath)
    [void](Assert-MarkdownRendererNoReparsePath -Path $objectDirectory)
    $acquisitionManifestPath = Assert-MarkdownRendererNoReparsePath -Path (Join-Path $outputPath 'source-acquisition.json')
    $statusPath = Assert-MarkdownRendererNoReparsePath -Path (Join-Path $outputPath 'source-status.json')
    $records = [Collections.Generic.List[object]]::new()
    $priorStatusByRank = @{}
    $pending = [Collections.Generic.List[object]]::new()
    $proofMigrations = [Collections.Generic.List[object]]::new()
    $localIntegrityFailures = 0
    $reusedCount = 0
    $downloadedCount = 0
    $upstreamFailures = 0
    $upstreamDeferred = 0
    $stopScheduling = $false
    $stopSchedulingClassification = 'upstream-rate-limit'
    $client = $null

    if ([IO.File]::Exists($statusPath)) {
        $priorStatus = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString((Read-MarkdownRendererFileBytesBounded -Path $statusPath -MaxBytes 8388608)))
        if ([int]$priorStatus.schemaVersion -ne 1 -or
            [string]$priorStatus.corpusManifestSha256 -cne [string]$Corpus.Sha256 -or
            @($priorStatus.records).Count -ne $Corpus.RequestedCount) {
            throw 'Existing source status belongs to a different or incomplete corpus; refusing to discard its provenance.'
        }
        foreach ($priorRecord in @($priorStatus.records)) {
            $priorRank = [int]$priorRecord.rank
            if ($priorRank -lt 1 -or $priorRank -gt $Corpus.RequestedCount -or $priorStatusByRank.ContainsKey([string]$priorRank)) {
                throw 'Existing source status contains a duplicate or invalid rank; refusing to discard its provenance.'
            }
            $priorStatusByRank[[string]$priorRank] = $priorRecord
        }
    }

    foreach ($identity in $Corpus.Identities) {
        $objectPath = Get-MarkdownRendererGitBlobObjectPath -ObjectDirectory $objectDirectory -GitBlobSha1 $identity.expectedGitBlobSha1
        $relativeObjectPath = "objects/git-blob-sha1/$($identity.expectedGitBlobSha1.Substring(0, 2))/$($identity.expectedGitBlobSha1).md"
        $cached = Test-MarkdownRendererGitBlobObject -Path $objectPath -ExpectedByteSize $identity.expectedByteSize -ExpectedGitBlobSha1 $identity.expectedGitBlobSha1
        $record = [pscustomobject][ordered]@{
            rank = $identity.rank
            repositoryId = $identity.repositoryId
            fullName = $identity.fullName
            commitSha = $identity.commitSha
            defaultBranch = $identity.defaultBranch
            readmePath = $identity.readmePath
            expectedByteSize = $identity.expectedByteSize
            expectedGitBlobSha1 = $identity.expectedGitBlobSha1
            htmlUrl = $identity.htmlUrl
            downloadUrl = $identity.downloadUrl
            objectPath = $relativeObjectPath
            contentSha256 = $null
            initialResponseByteSize = $null
            initialResponseGitBlobSha1 = $null
            observedByteSize = $null
            observedGitBlobSha1 = $null
            symlinkResolution = $null
            status = 'pending'
            classification = $null
            httpStatus = $null
            attempts = 0
            message = $null
        }
        $priorRecord = $priorStatusByRank[[string]$identity.rank]
        if ($null -ne $priorRecord -and
            ([long]$priorRecord.repositoryId -ne [long]$identity.repositoryId -or
             [string]$priorRecord.fullName -cne [string]$identity.fullName -or
             [string]$priorRecord.commitSha -cne [string]$identity.commitSha -or
             [string]$priorRecord.readmePath -cne [string]$identity.readmePath -or
             [long]$priorRecord.expectedByteSize -ne [long]$identity.expectedByteSize -or
             [string]$priorRecord.expectedGitBlobSha1 -cne [string]$identity.expectedGitBlobSha1 -or
             [string]$priorRecord.objectPath -cne $relativeObjectPath)) {
            throw "Existing source status identity differs from the frozen corpus at rank $($identity.rank); refusing to discard its provenance."
        }
        if ($cached.Valid) {
            $record.status = 'verified-cached'
            $record.contentSha256 = $cached.Sha256
            $priorProofProperty = if ($null -ne $priorRecord) { $priorRecord.PSObject.Properties['symlinkResolution'] } else { $null }
            if ($null -ne $priorProofProperty -and $null -ne $priorProofProperty.Value) {
                $priorProof = $priorProofProperty.Value
                $hasSchema = $null -ne $priorProof.PSObject.Properties['schemaVersion']
                $hasChecksum = $null -ne $priorProof.PSObject.Properties['proofSha256']
                $hasRootTree = $null -ne $priorProof.PSObject.Properties['rootTreeSha']
                if ($hasSchema -and $hasChecksum) {
                    if (-not (Test-MarkdownRendererSymlinkResolutionProof -Proof $priorProof -Identity $identity)) {
                        throw "Existing symlink provenance failed integrity validation at rank $($identity.rank); refusing to discard it."
                    }
                    $record.symlinkResolution = $priorProof
                }
                elseif (-not $hasSchema -and -not $hasChecksum -and -not $hasRootTree -and
                    (Test-MarkdownRendererSymlinkResolutionShape -Proof $priorProof -Identity $identity)) {
                    # Legacy proofs are not trusted offline. Revalidate every stored Git tree fact once below,
                    # then persist the new checksummed schema without refetching the accepted README body.
                    $proofMigrations.Add([pscustomobject]@{ Record = $record; Identity = $identity; LegacyProof = $priorProof })
                }
                else {
                    throw "Existing symlink provenance requires explicit migration or repair at rank $($identity.rank); refusing to discard it."
                }
                $firstLink = @($priorProof.linkChain)[0]
                $initialPointerBytes = [Text.UTF8Encoding]::new($false, $true).GetBytes([string]$firstLink.targetText)
                $record.initialResponseByteSize = [long]$initialPointerBytes.Length
                $record.initialResponseGitBlobSha1 = [string]$firstLink.linkGitBlobSha1
                $record.observedByteSize = [long]$identity.expectedByteSize
                $record.observedGitBlobSha1 = [string]$identity.expectedGitBlobSha1
            }
            else {
                $record.initialResponseByteSize = [long]$identity.expectedByteSize
                $record.initialResponseGitBlobSha1 = [string]$identity.expectedGitBlobSha1
                $record.observedByteSize = [long]$identity.expectedByteSize
                $record.observedGitBlobSha1 = [string]$identity.expectedGitBlobSha1
            }
            $reusedCount++
        }
        elseif ($cached.Exists) {
            $record.status = 'local-integrity-failure'
            $record.classification = 'local-cache-corruption'
            $record.message = [string]$cached.Reason
            $localIntegrityFailures++
        }
        else { $pending.Add([pscustomobject]@{ Record = $record; ObjectPath = $objectPath }) }
        $records.Add($record)
    }

    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 1
        corpusManifestSha256 = $Corpus.Sha256
        corpusManifestPath = $script:MarkdownRendererTop500CorpusRelativePath
        requestedCount = $Corpus.RequestedCount
        createdAtUtc = [DateTime]::UtcNow.ToString('O')
        objectAddressing = 'git-blob-sha1'
        records = @($records | ForEach-Object {
            [pscustomobject][ordered]@{
                rank = $_.rank; repositoryId = $_.repositoryId; fullName = $_.fullName
                commitSha = $_.commitSha; defaultBranch = $_.defaultBranch; readmePath = $_.readmePath
                expectedByteSize = $_.expectedByteSize; expectedGitBlobSha1 = $_.expectedGitBlobSha1
                htmlUrl = $_.htmlUrl; downloadUrl = $_.downloadUrl; objectPath = $_.objectPath
            }
        })
    }
    if ([IO.File]::Exists($acquisitionManifestPath)) {
        $existing = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString((Read-MarkdownRendererFileBytesBounded -Path $acquisitionManifestPath -MaxBytes 8388608)))
        if ([string]$existing.corpusManifestSha256 -cne $Corpus.Sha256 -or [int]$existing.requestedCount -ne $Corpus.RequestedCount) {
            throw 'Existing source-acquisition manifest belongs to a different frozen corpus; refusing to overwrite it.'
        }
        if ([int]$existing.schemaVersion -ne 1 -or [string]$existing.objectAddressing -cne 'git-blob-sha1') {
            throw 'Existing source-acquisition manifest has an unsupported schema or object-addressing policy.'
        }
        if (@($existing.records).Count -ne $Corpus.RequestedCount) {
            throw 'Existing source-acquisition manifest is incomplete; refusing to overwrite it.'
        }
        foreach ($record in $records) {
            $prior = @($existing.records | Where-Object { [int]$_.rank -eq [int]$record.rank })
            if ($prior.Count -ne 1 -or
                [string]$prior[0].fullName -cne [string]$record.fullName -or
                [long]$prior[0].repositoryId -ne [long]$record.repositoryId -or
                [string]$prior[0].commitSha -cne [string]$record.commitSha -or
                [string]$prior[0].defaultBranch -cne [string]$record.defaultBranch -or
                [string]$prior[0].readmePath -cne [string]$record.readmePath -or
                [long]$prior[0].expectedByteSize -ne [long]$record.expectedByteSize -or
                [string]$prior[0].expectedGitBlobSha1 -cne [string]$record.expectedGitBlobSha1 -or
                [string]$prior[0].htmlUrl -cne [string]$record.htmlUrl -or
                [string]$prior[0].downloadUrl -cne [string]$record.downloadUrl -or
                [string]$prior[0].objectPath -cne [string]$record.objectPath) {
                throw "Existing source-acquisition identity differs from the frozen corpus at rank $($record.rank); refusing to overwrite it."
            }
        }
        $manifest.createdAtUtc = [string]$existing.createdAtUtc
    }
    else { Write-MarkdownRendererJsonAtomically -Path $acquisitionManifestPath -Value $manifest }

    function Write-CurrentStatus {
        param([Parameter(Mandatory)][string] $RunState)
        $counts = [ordered]@{
            verified = @($records | Where-Object { $_.status -in @('verified-cached', 'verified-downloaded') }).Count
            verifiedCached = @($records | Where-Object { $_.status -ceq 'verified-cached' }).Count
            verifiedDownloaded = @($records | Where-Object { $_.status -ceq 'verified-downloaded' }).Count
            upstreamFailures = @($records | Where-Object { $_.status -ceq 'upstream-failure' }).Count
            upstreamIntegrityFailures = @($records | Where-Object { $_.status -ceq 'upstream-integrity-failure' }).Count
            upstreamResolutionFailures = @($records | Where-Object { $_.status -ceq 'upstream-resolution-failure' }).Count
            upstreamDeferred = @($records | Where-Object { $_.status -ceq 'deferred' }).Count
            localIntegrityFailures = @($records | Where-Object { $_.status -ceq 'local-integrity-failure' }).Count
            pending = @($records | Where-Object { $_.status -ceq 'pending' }).Count
        }
        $status = [pscustomobject][ordered]@{
            schemaVersion = 1
            corpusManifestSha256 = $Corpus.Sha256
            updatedAtUtc = [DateTime]::UtcNow.ToString('O')
            runState = $RunState
            complete = ($counts.verified -eq $Corpus.RequestedCount -and $counts.pending -eq 0 -and $counts.upstreamFailures -eq 0 -and $counts.upstreamIntegrityFailures -eq 0 -and $counts.upstreamResolutionFailures -eq 0 -and $counts.upstreamDeferred -eq 0 -and $counts.localIntegrityFailures -eq 0)
            counts = [pscustomobject]$counts
            records = @($records)
        }
        Write-MarkdownRendererJsonAtomically -Path $statusPath -Value $status
    }

    try {
        if (($pending.Count -gt 0 -and $null -eq $RequestOverride) -or
            ($proofMigrations.Count -gt 0 -and $null -eq $ApiRequestOverride)) {
            $handler = [Net.Http.HttpClientHandler]::new()
            $handler.UseDefaultCredentials = $false
            $handler.UseCookies = $false
            $handler.AllowAutoRedirect = $false
            $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
            $client = [Net.Http.HttpClient]::new($handler, $true)
            $client.Timeout = [TimeSpan]::FromSeconds(45)
            $client.MaxResponseContentBufferSize = [long]$MaxReadmeBytes
            $client.DefaultRequestHeaders.UserAgent.ParseAdd('MarkdownRenderer-CorrectnessCorpus/1.0')
            $client.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
        }

        foreach ($migration in $proofMigrations) {
            $migration.Record.symlinkResolution = Upgrade-MarkdownRendererLegacySymlinkResolutionProof `
                -LegacyProof $migration.LegacyProof `
                -Identity $migration.Identity `
                -HttpClient $client `
                -MaxBytes 8388608 `
                -MaxAttempts $MaxAttempts `
                -MaxRetryWaitSeconds $MaxRetryWaitSeconds `
                -ApiRequestOverride $ApiRequestOverride
        }
        Write-CurrentStatus -RunState 'running'

        $cursor = 0
        while ($cursor -lt $pending.Count -and -not $stopScheduling) {
            $batchEnd = [Math]::Min($cursor + $MaxConcurrency, $pending.Count)
            $batch = @($pending.GetRange($cursor, $batchEnd - $cursor).ToArray())
            $active = [Collections.Generic.List[object]]::new()
            foreach ($work in $batch) {
                $task = $null
                $requestCancellation = $null
                if ($null -eq $RequestOverride) {
                    try {
                        $requestCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(45))
                        $task = Start-MarkdownRendererReadmeRequest `
                            -HttpClient $client `
                            -Identity $work.Record `
                            -CancellationToken $requestCancellation.Token
                    }
                    catch {
                        if ($null -ne $requestCancellation) { $requestCancellation.Dispose() }
                        $requestCancellation = $null
                        $task = $null
                    }
                }
                $active.Add([pscustomobject]@{ Work = $work; InitialTask = $task; InitialCancellationTokenSource = $requestCancellation })
            }

            foreach ($entry in $active) {
                $result = Invoke-MarkdownRendererReadmeFetch `
                    -Identity $entry.Work.Record `
                    -MaxAttempts $MaxAttempts `
                    -MaxReadmeBytes $MaxReadmeBytes `
                    -MaxRetryWaitSeconds $MaxRetryWaitSeconds `
                    -HttpClient $client `
                    -InitialTask $entry.InitialTask `
                    -InitialCancellationTokenSource $entry.InitialCancellationTokenSource `
                    -RequestOverride $RequestOverride
                $record = $entry.Work.Record
                $record.attempts = $result.Attempts
                $record.httpStatus = $result.HttpStatus
                if ($result.Success) {
                    $bytes = [byte[]]$result.Bytes
                    $record.initialResponseByteSize = [long]$bytes.Length
                    $record.initialResponseGitBlobSha1 = Get-MarkdownRendererGitBlobSha1 -Bytes $bytes
                    $record.observedByteSize = $record.initialResponseByteSize
                    $record.observedGitBlobSha1 = $record.initialResponseGitBlobSha1
                    $resolved = $null
                    $directMatch = ($bytes.LongLength -eq $record.expectedByteSize -and
                        $bytes.LongLength -le $MaxReadmeBytes -and
                        $record.observedGitBlobSha1 -ceq $record.expectedGitBlobSha1)
                    if (-not $directMatch -and ($null -eq $RequestOverride -or ($null -ne $ApiRequestOverride -and $null -ne $RawRequestOverride))) {
                        $resolved = Resolve-MarkdownRendererPinnedReadmeSymlink `
                            -Identity $record `
                            -InitialLinkBytes $bytes `
                            -HttpClient $client `
                            -MaxReadmeBytes $MaxReadmeBytes `
                            -MaxLinkDepth $MaxSymlinkDepth `
                            -ApiRequestOverride $ApiRequestOverride `
                            -RawRequestOverride $RawRequestOverride `
                            -MaxAttempts $MaxAttempts `
                            -MaxRetryWaitSeconds $MaxRetryWaitSeconds
                        if ($resolved.Success) {
                            $bytes = [byte[]]$resolved.Bytes
                            $record.symlinkResolution = $resolved.Proof
                            $record.observedByteSize = [long]$bytes.Length
                            $record.observedGitBlobSha1 = Get-MarkdownRendererGitBlobSha1 -Bytes $bytes
                        }
                    }
                    $verifiedContentSha1 = if ($null -ne $bytes) { Get-MarkdownRendererGitBlobSha1 -Bytes $bytes } else { '' }
                    if ($bytes.LongLength -ne $record.expectedByteSize -or $bytes.LongLength -gt $MaxReadmeBytes -or
                        $verifiedContentSha1 -cne $record.expectedGitBlobSha1) {
                        if ($null -ne $resolved -and -not $resolved.Success -and
                            $resolved.Failure -in @('git-api-rate-limit-deferred', 'symlink-target-rate-limit-deferred', 'git-api-retry-deferred', 'symlink-target-retry-deferred')) {
                            $record.status = 'deferred'
                            $record.classification = if ($resolved.Failure -match 'rate-limit') { 'upstream-rate-limit' } else { 'upstream-retry-deferred' }
                            $record.message = [string]$resolved.Failure
                            $stopScheduling = $true
                            $stopSchedulingClassification = [string]$record.classification
                        }
                        elseif ($null -ne $resolved -and -not $resolved.Success -and
                            $resolved.Failure -notin @('manifest-path-is-not-git-symlink', 'symlink-blob-proof-mismatch', 'target-content-mismatch', 'symlink-target-git-blob-does-not-match-manifest')) {
                            $record.status = 'upstream-resolution-failure'
                            $record.classification = 'upstream-symlink-resolution'
                            $record.message = [string]$resolved.Failure
                        }
                        else {
                            $record.status = 'upstream-integrity-failure'
                            $record.classification = 'upstream-content-mismatch'
                            $record.message = if ($bytes.LongLength -ne $record.expectedByteSize) { 'byte-size-mismatch' } else { 'git-blob-sha1-mismatch' }
                        }
                    }
                    else {
                        try {
                            Write-MarkdownRendererBytesAtomically -Path $entry.Work.ObjectPath -Bytes $bytes
                            $saved = Test-MarkdownRendererGitBlobObject -Path $entry.Work.ObjectPath -ExpectedByteSize $record.expectedByteSize -ExpectedGitBlobSha1 $record.expectedGitBlobSha1
                            if (-not $saved.Valid) { throw 'post-write-object-verification-failed' }
                            $record.status = 'verified-downloaded'
                            $record.contentSha256 = $saved.Sha256
                            $record.classification = $null
                            $record.message = $null
                            $downloadedCount++
                        }
                        catch {
                            $record.status = 'local-integrity-failure'
                            $record.classification = 'local-cache-write-failure'
                            $record.message = 'object-write-or-verification-failed'
                            $localIntegrityFailures++
                        }
                    }
                }
                elseif ($result.Retry -and $result.Category -in @('upstream-rate-limit', 'upstream-retry-deferred')) {
                    $record.status = 'deferred'
                    $record.classification = [string]$result.Category
                    $record.message = [string]$result.SafeMessage
                    $stopScheduling = $true
                    $stopSchedulingClassification = [string]$result.Category
                }
                elseif ($result.Category -ceq 'upstream-content-mismatch') {
                    $record.status = 'upstream-integrity-failure'
                    $record.classification = [string]$result.Category
                    $record.message = [string]$result.SafeMessage
                }
                else {
                    $record.status = 'upstream-failure'
                    $record.classification = [string]$result.Category
                    $record.message = [string]$result.SafeMessage
                }
                Write-CurrentStatus -RunState 'running'
            }
            $cursor = $batchEnd
        }

        if ($stopScheduling) {
            while ($cursor -lt $pending.Count) {
                $work = $pending[$cursor]
                $work.Record.status = 'deferred'
                $work.Record.classification = $stopSchedulingClassification
                $work.Record.message = if ($stopSchedulingClassification -ceq 'upstream-rate-limit') { 'not-requested-after-rate-limit' } else { 'not-requested-after-transient-failure' }
                $cursor++
            }
        }
        Write-CurrentStatus -RunState 'finished'
    }
    finally { if ($null -ne $client) { $client.Dispose() } }

    $finalStatus = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString((Read-MarkdownRendererFileBytesBounded -Path $statusPath -MaxBytes 8388608)))
    return [pscustomobject]@{
        ManifestPath = $acquisitionManifestPath
        StatusPath = $statusPath
        ObjectDirectory = $objectDirectory
        CorpusSha256 = $Corpus.Sha256
        RequestedCount = $Corpus.RequestedCount
        VerifiedCount = [int]$finalStatus.counts.verified
        DownloadedThisRun = $downloadedCount
        ReusedVerified = $reusedCount
        UpstreamFailures = [int]$finalStatus.counts.upstreamFailures
        UpstreamIntegrityFailures = [int]$finalStatus.counts.upstreamIntegrityFailures
        UpstreamResolutionFailures = [int]$finalStatus.counts.upstreamResolutionFailures
        UpstreamDeferred = [int]$finalStatus.counts.upstreamDeferred
        LocalIntegrityFailures = [int]$finalStatus.counts.localIntegrityFailures
        Pending = [int]$finalStatus.counts.pending
        Complete = [bool]$finalStatus.complete
    }
}

function Invoke-MarkdownRendererTop500SourceAcquisition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $CorpusManifestPath,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [ValidateRange(1, 8)][int] $MaxConcurrency = 4,
        [ValidateRange(1, 5)][int] $MaxAttempts = 3,
        [ValidateRange(1, 8388608)][int] $MaxReadmeBytes = 8388608,
        [ValidateRange(0, 60)][int] $MaxRetryWaitSeconds = 30
    )
    $corpus = Read-MarkdownRendererPinnedCorpus `
        -Path $CorpusManifestPath `
        -ExpectedSha256 $script:MarkdownRendererTop500CorpusSha256 `
        -ExpectedCount $script:MarkdownRendererTop500ExpectedCount `
        -MaxReadmeBytes $MaxReadmeBytes
    return Invoke-MarkdownRendererTop500Acquisition `
        -Corpus $corpus `
        -OutputDirectory $OutputDirectory `
        -MaxConcurrency $MaxConcurrency `
        -MaxAttempts $MaxAttempts `
        -MaxReadmeBytes $MaxReadmeBytes `
        -MaxRetryWaitSeconds $MaxRetryWaitSeconds
}

if ($MyInvocation.InvocationName -ne '.') {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    if ([string]::IsNullOrWhiteSpace($CorpusManifestPath)) {
        $CorpusManifestPath = Join-Path $repoRoot $script:MarkdownRendererTop500CorpusRelativePath
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $repoRoot 'artifacts/readme-top500/source-acquisition-615c56723fb1'
    }

    $result = Invoke-MarkdownRendererTop500SourceAcquisition `
        -CorpusManifestPath $CorpusManifestPath `
        -OutputDirectory $OutputDirectory `
        -MaxConcurrency $MaxConcurrency `
        -MaxAttempts $MaxAttempts `
        -MaxReadmeBytes $MaxReadmeBytes `
        -MaxRetryWaitSeconds $MaxRetryWaitSeconds
    $result | Format-List
    if (-not $result.Complete) { exit 2 }
}
