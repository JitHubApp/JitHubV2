[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CorpusManifestPath,
    [Parameter(Mandatory)][string] $SourceRoot,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateRange(1, 500)][int] $StartRank = 1,
    [ValidateRange(1, 500)][int] $Count = 500,
    [ValidateRange(1, 32)][int] $MaxHtmlMiB = 32,
    [ValidateRange(5, 120)][int] $RequestTimeoutSeconds = 30,
    [ValidateRange(0, 10000)][int] $InterRequestDelayMilliseconds = 1000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:MarkdownRendererCorrectnessHtmlCorpusSha256 = '615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72'
$script:MarkdownRendererCorrectnessHtmlCorpusPath = 'MarkdownRenderer/eng/correctness-inputs/top-repositories.json'
$script:MarkdownRendererCorrectnessHtmlSourceManifestName = 'source-acquisition.json'
$script:MarkdownRendererCorrectnessHtmlContract = 'github-commit-page-response-only-v1'
$script:MarkdownRendererCorrectnessHtmlMaxManifestBytes = 8388608

# The upstream source-acquisition script is only dot-sourced for its pinned-corpus,
# Git-blob verification, path-safety, hashing, and atomic-write primitives.
. (Join-Path $PSScriptRoot 'Invoke-MarkdownRendererTop500SourceAcquisition.ps1') -CorpusManifestPath $CorpusManifestPath -OutputDirectory $OutputDirectory

function Get-MarkdownRendererCorrectnessHtmlObjectPath {
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string] $Sha256)
    return [IO.Path]::GetFullPath((Join-Path $Root (Join-Path 'objects/sha256' (Join-Path $Sha256.Substring(0, 2) "$Sha256.html"))))
}

function ConvertTo-MarkdownRendererCorrectnessHtmlJsonBytes {
    param([Parameter(Mandatory)][object] $Value)
    $text = (ConvertTo-Json -InputObject $Value -Depth 20) + "`n"
    return [Text.UTF8Encoding]::new($false, $true).GetBytes($text)
}

function Write-MarkdownRendererCorrectnessHtmlImmutableJson {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][object] $Value)
    $bytes = ConvertTo-MarkdownRendererCorrectnessHtmlJsonBytes -Value $Value
    try { Write-MarkdownRendererBytesAtomically -Path $Path -Bytes $bytes }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Read-MarkdownRendererCorrectnessHtmlJson {
    param([Parameter(Mandatory)][string] $Path, [ValidateRange(1, 8388608)][int] $MaxBytes = 8388608)
    $bytes = Read-MarkdownRendererFileBytesBounded -Path $Path -MaxBytes $MaxBytes
    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        return ConvertFrom-Json -InputObject $text
    }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Read-MarkdownRendererCorrectnessHtmlBodyBounded {
    param([Parameter(Mandatory)][string] $Path, [ValidateRange(1, 33554432)][int] $MaxBytes = 33554432)
    $fullPath = Assert-MarkdownRendererNoReparsePath -Path $Path
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -gt $MaxBytes) { throw 'file-over-budget' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw 'file-changed-during-read' }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) { throw 'file-changed-during-read' }
        return ,$bytes
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Test-MarkdownRendererCorrectnessHtmlPathWithinRoot {
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string] $Candidate)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-MarkdownRendererCorrectnessHtmlFrozenSources {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $CorpusManifestPath, [Parameter(Mandatory)][string] $SourceRoot)

    $corpus = Read-MarkdownRendererPinnedCorpus -Path $CorpusManifestPath -ExpectedSha256 $script:MarkdownRendererCorrectnessHtmlCorpusSha256 -ExpectedCount 500
    $sourceRootFull = [IO.Path]::GetFullPath($SourceRoot)
    [void](Assert-MarkdownRendererNoReparsePath -Path $sourceRootFull)
    $sourceManifestPath = Join-Path $sourceRootFull $script:MarkdownRendererCorrectnessHtmlSourceManifestName
    if (-not [IO.File]::Exists($sourceManifestPath)) { throw 'frozen-source-acquisition-manifest-missing' }
    $sourceManifest = Read-MarkdownRendererCorrectnessHtmlJson -Path $sourceManifestPath -MaxBytes $script:MarkdownRendererCorrectnessHtmlMaxManifestBytes
    if ([int]$sourceManifest.schemaVersion -ne 1 -or [int]$sourceManifest.requestedCount -ne 500 -or
        [string]$sourceManifest.corpusManifestSha256 -cne $script:MarkdownRendererCorrectnessHtmlCorpusSha256 -or
        [string]$sourceManifest.corpusManifestPath -cne $script:MarkdownRendererCorrectnessHtmlCorpusPath -or
        [string]$sourceManifest.objectAddressing -cne 'git-blob-sha1') {
        throw 'frozen-source-acquisition-manifest-identity-mismatch'
    }
    $sourceRecords = @($sourceManifest.records)
    if ($sourceRecords.Count -ne 500) { throw 'frozen-source-acquisition-record-count-mismatch' }
    $sourceByRank = @{}
    foreach ($record in $sourceRecords) {
        $rank = [int]$record.rank
        if ($rank -lt 1 -or $rank -gt 500 -or $sourceByRank.ContainsKey($rank)) { throw 'frozen-source-acquisition-duplicate-or-invalid-rank' }
        $sourceByRank[$rank] = $record
    }

    $records = [Collections.Generic.List[object]]::new()
    foreach ($identity in $corpus.Identities) {
        $source = $sourceByRank[[int]$identity.rank]
        if ([long]$source.repositoryId -ne [long]$identity.repositoryId -or
            [string]$source.fullName -cne [string]$identity.fullName -or
            [string]$source.commitSha -cne [string]$identity.commitSha -or
            [string]$source.readmePath -cne [string]$identity.readmePath -or
            [long]$source.expectedByteSize -ne [long]$identity.expectedByteSize -or
            [string]$source.expectedGitBlobSha1 -cne [string]$identity.expectedGitBlobSha1 -or
            [string]$source.htmlUrl -cne [string]$identity.htmlUrl -or
            [string]$source.downloadUrl -cne [string]$identity.downloadUrl) {
            throw "frozen-source-acquisition-identity-mismatch-rank-$($identity.rank)"
        }
        $objectPath = Get-MarkdownRendererGitBlobObjectPath -ObjectDirectory (Join-Path $sourceRootFull 'objects/git-blob-sha1') -GitBlobSha1 ([string]$identity.expectedGitBlobSha1)
        $expectedObjectRelativePath = (Join-Path 'objects/git-blob-sha1' (Join-Path $identity.expectedGitBlobSha1.Substring(0, 2) "$($identity.expectedGitBlobSha1).md")).Replace('\', '/')
        if ([string]$source.objectPath -cne $expectedObjectRelativePath) {
            throw "frozen-source-object-path-mismatch-rank-$($identity.rank)"
        }
        $records.Add([pscustomobject][ordered]@{
            rank = [int]$identity.rank
            repositoryId = [long]$identity.repositoryId
            fullName = [string]$identity.fullName
            commitSha = [string]$identity.commitSha
            defaultBranch = [string]$identity.defaultBranch
            readmePath = [string]$identity.readmePath
            expectedByteSize = [long]$identity.expectedByteSize
            expectedGitBlobSha1 = [string]$identity.expectedGitBlobSha1
            htmlUrl = [string]$identity.htmlUrl
            downloadUrl = [string]$identity.downloadUrl
            sourceObjectPath = [string]$source.objectPath
            sourceObjectAbsolutePath = $objectPath
        })
    }
    return [pscustomobject]@{ Corpus = $corpus; SourceRoot = $sourceRootFull; Records = @($records) }
}

function New-MarkdownRendererCorrectnessHtmlSnapshotIndex {
    param([Parameter(Mandatory)][object] $FrozenSources)
    $identities = foreach ($record in $FrozenSources.Records) {
        [pscustomobject][ordered]@{
            rank = $record.rank
            repositoryId = $record.repositoryId
            fullName = $record.fullName
            commitSha = $record.commitSha
            defaultBranch = $record.defaultBranch
            readmePath = $record.readmePath
            expectedReadmeByteSize = $record.expectedByteSize
            expectedReadmeGitBlobSha1 = $record.expectedGitBlobSha1
            requestUrl = $record.htmlUrl
            expectedRawReadmeUrl = $record.downloadUrl
            sourceObjectPath = $record.sourceObjectPath
        }
    }
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        contract = $script:MarkdownRendererCorrectnessHtmlContract
        frozenReadmeCorpusSha256 = $FrozenSources.Corpus.Sha256
        frozenReadmeCorpusPath = $script:MarkdownRendererCorrectnessHtmlCorpusPath
        frozenReadmeCount = 500
        sourceAcquisitionContract = 'git-blob-sha1-verified-before-request'
        responseMeaning = 'raw-github-commit-page-http-response-only'
        explicitNonClaims = @('not-browser-rendered', 'not-resource-complete', 'not-capture-ready', 'not-an-audit-result')
        identities = @($identities)
    }
}

function Assert-MarkdownRendererCorrectnessHtmlIndexMatches {
    param([Parameter(Mandatory)][string] $IndexPath, [Parameter(Mandatory)][object] $ExpectedIndex)
    $actual = Read-MarkdownRendererCorrectnessHtmlJson -Path $IndexPath -MaxBytes $script:MarkdownRendererCorrectnessHtmlMaxManifestBytes
    $expectedBytes = ConvertTo-MarkdownRendererCorrectnessHtmlJsonBytes -Value $ExpectedIndex
    $actualBytes = ConvertTo-MarkdownRendererCorrectnessHtmlJsonBytes -Value $actual
    try {
        if ([Convert]::ToBase64String($expectedBytes) -cne [Convert]::ToBase64String($actualBytes)) {
            throw 'response-only-snapshot-index-is-immutable-and-does-not-match-frozen-input'
        }
    }
    finally {
        [Array]::Clear($expectedBytes, 0, $expectedBytes.Length)
        [Array]::Clear($actualBytes, 0, $actualBytes.Length)
    }
}

function Get-MarkdownRendererCorrectnessHtmlCasePaths {
    param([Parameter(Mandatory)][string] $OutputDirectory, [Parameter(Mandatory)][int] $Rank)
    $case = Join-Path $OutputDirectory ('cases/rank-{0:D4}' -f $Rank)
    return [pscustomobject]@{
        Directory = $case
        Start = Join-Path $case 'attempt-start.json'
        Result = Join-Path $case 'attempt.json'
    }
}

function Test-MarkdownRendererCorrectnessHtmlAttemptIdentity {
    param([Parameter(Mandatory)][object] $Attempt, [Parameter(Mandatory)][object] $Identity)
    return ([int]$Attempt.schemaVersion -eq 1 -and
        [string]$Attempt.contract -ceq $script:MarkdownRendererCorrectnessHtmlContract -and
        @('terminal-result', 'request-start') -ccontains [string]$Attempt.recordKind -and
        [int]$Attempt.rank -eq [int]$Identity.rank -and
        [long]$Attempt.repositoryId -eq [long]$Identity.repositoryId -and
        [string]$Attempt.fullName -ceq [string]$Identity.fullName -and
        [string]$Attempt.commitSha -ceq [string]$Identity.commitSha -and
        [string]$Attempt.readmePath -ceq [string]$Identity.readmePath -and
        [long]$Attempt.expectedReadmeByteSize -eq [long]$Identity.expectedByteSize -and
        [string]$Attempt.expectedReadmeGitBlobSha1 -ceq [string]$Identity.expectedGitBlobSha1 -and
        [string]$Attempt.requestUrl -ceq [string]$Identity.htmlUrl)
}

function Test-MarkdownRendererCorrectnessHtmlCapturedMetadata {
    param([Parameter(Mandatory)][string] $OutputDirectory, [Parameter(Mandatory)][object] $Attempt, [Parameter(Mandatory)][object] $Identity)
    if ([string]$Attempt.outcome -cne 'response-captured' -or
        [int]$Attempt.httpStatus -ne 200 -or
        [string]$Attempt.contentType -cne 'text/html' -or
        [string]$Attempt.requestUrl -cne [string]$Identity.htmlUrl -or
        [string]$Attempt.finalUrl -cne [string]$Identity.htmlUrl -or
        $Attempt.sourceVerifiedBeforeRequest -isnot [bool] -or -not [bool]$Attempt.sourceVerifiedBeforeRequest -or
        [long]$Attempt.sourceByteSize -ne [long]$Identity.expectedByteSize -or
        [string]$Attempt.sourceGitBlobSha1 -cne [string]$Identity.expectedGitBlobSha1 -or
        [string]$Attempt.htmlSha256 -notmatch '^[0-9a-f]{64}$' -or
        [long]$Attempt.htmlByteSize -lt 1 -or [long]$Attempt.htmlByteSize -gt 33554432) { return $false }
    try { [void][DateTimeOffset]::Parse([string]$Attempt.responseAtUtc) }
    catch { return $false }
    $objectPath = Get-MarkdownRendererCorrectnessHtmlObjectPath -Root $OutputDirectory -Sha256 ([string]$Attempt.htmlSha256)
    $root = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $expectedRelative = $objectPath.Substring($root.Length).Replace('\', '/')
    return ([string]$Attempt.htmlObjectPath -ceq $expectedRelative -and
        $Attempt.htmlObjectVerified -is [bool] -and [bool]$Attempt.htmlObjectVerified)
}

function Read-MarkdownRendererCorrectnessHtmlVerifiedObject {
    param([Parameter(Mandatory)][string] $OutputDirectory, [Parameter(Mandatory)][object] $Attempt, [Parameter(Mandatory)][object] $Identity)
    if (-not (Test-MarkdownRendererCorrectnessHtmlCapturedMetadata -OutputDirectory $OutputDirectory -Attempt $Attempt -Identity $Identity)) { return $null }
    $objectPath = Get-MarkdownRendererCorrectnessHtmlObjectPath -Root $OutputDirectory -Sha256 ([string]$Attempt.htmlSha256)
    [void](Assert-MarkdownRendererNoReparsePath -Path $objectPath)
    if (-not [IO.File]::Exists($objectPath)) { return $null }
    $script:MarkdownRendererCorrectnessHtmlObjectReadCount++
    try { $bytes = Read-MarkdownRendererCorrectnessHtmlBodyBounded -Path $objectPath -MaxBytes 33554432 }
    catch { return $null }
    try {
        if ($bytes.Length -ne [long]$Attempt.htmlByteSize -or (Get-MarkdownRendererSha256 -Bytes $bytes) -cne [string]$Attempt.htmlSha256) { return $null }
        return [pscustomobject]@{ Path = $objectPath; ByteSize = $bytes.Length; Sha256 = [string]$Attempt.htmlSha256 }
    }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Get-MarkdownRendererCorrectnessHtmlRateLimitState {
    param([Parameter(Mandatory)][Net.Http.HttpResponseMessage] $Response)
    $remaining = $null
    $values = $null
    if ($Response.Headers.TryGetValues('X-RateLimit-Remaining', [ref]$values)) {
        $parsed = 0
        if ([int]::TryParse([string](@($values)[0]), [ref]$parsed)) { $remaining = $parsed }
    }
    return ([int]$Response.StatusCode -eq 429 -or
        ([int]$Response.StatusCode -eq 403 -and $null -ne $remaining -and $remaining -le 0))
}

function Get-MarkdownRendererCorrectnessHtmlContentType {
    param([AllowNull()][Net.Http.Headers.MediaTypeHeaderValue] $ContentType)
    if ($null -eq $ContentType) { return $null }
    return [string]$ContentType.MediaType
}

function Invoke-MarkdownRendererCorrectnessHtmlGet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Net.Http.HttpClient] $HttpClient,
        [Parameter(Mandatory)][object] $Identity,
        [Parameter(Mandatory)][ValidateRange(1, 33554432)][int] $MaxBytes,
        [Parameter(Mandatory)][ValidateRange(5, 120)][int] $TimeoutSeconds,
        [Parameter(Mandatory)][Threading.CancellationToken] $CancellationToken
    )
    $requestUri = $null
    if (-not [Uri]::TryCreate([string]$Identity.htmlUrl, [UriKind]::Absolute, [ref]$requestUri) -or
        $requestUri.Scheme -cne 'https' -or $requestUri.Host -cne 'github.com' -or $requestUri.Port -ne 443 -or
        -not [string]::IsNullOrEmpty($requestUri.UserInfo) -or -not [string]::IsNullOrEmpty($requestUri.Query) -or
        -not [string]::IsNullOrEmpty($requestUri.Fragment) -or $requestUri.AbsoluteUri -cne [string]$Identity.htmlUrl) {
        throw 'request-uri-outside-frozen-github-commit-page'
    }

    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $requestUri)
    $request.Headers.Accept.ParseAdd('text/html')
    $request.Headers.AcceptEncoding.ParseAdd('identity')
    $request.Headers.UserAgent.ParseAdd('MarkdownRenderer-CorrectnessCorpus/1.0')
    $request.Headers.CacheControl = [Net.Http.Headers.CacheControlHeaderValue]::new()
    $response = $null
    $bodyStream = $null
    $memory = $null
    $timeoutCts = [Threading.CancellationTokenSource]::new($TimeoutSeconds * 1000)
    $linkedCts = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken, $timeoutCts.Token)
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $status = 0
    $finalUri = $requestUri.AbsoluteUri
    $contentType = $null
    try {
        $response = $HttpClient.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $linkedCts.Token).GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $finalUri = $requestUri.AbsoluteUri
        if ($null -ne $response.RequestMessage -and $null -ne $response.RequestMessage.RequestUri) { $finalUri = $response.RequestMessage.RequestUri.AbsoluteUri }
        $contentType = Get-MarkdownRendererCorrectnessHtmlContentType -ContentType $response.Content.Headers.ContentType
        $common = [ordered]@{
            requestUrl = $requestUri.AbsoluteUri
            finalUrl = $finalUri
            httpStatus = $status
            contentType = $contentType
            responseAtUtc = $now
            rateLimited = [bool](Get-MarkdownRendererCorrectnessHtmlRateLimitState -Response $response)
        }
        if ($status -ge 300 -and $status -lt 400) {
            $location = $null
            if ($null -ne $response.Headers.Location) { $location = [string]$response.Headers.Location }
            $common.locationHeader = $location
            $common.outcome = 'redirect-rejected'
            return [pscustomobject]$common
        }
        if ($finalUri -cne $requestUri.AbsoluteUri) {
            $common.outcome = 'unexpected-final-url'
            return [pscustomobject]$common
        }
        if ($status -eq 429 -or [bool]$common.rateLimited) {
            $common.outcome = 'upstream-rate-limit'
            return [pscustomobject]$common
        }
        if ($status -ne 200) {
            $common.outcome = 'upstream-http-failure'
            return [pscustomobject]$common
        }
        if ($contentType -cne 'text/html') {
            $common.outcome = 'unexpected-content-type'
            return [pscustomobject]$common
        }
        if ($response.Content.Headers.ContentEncoding.Count -gt 0 -and
            -not (@($response.Content.Headers.ContentEncoding) -contains 'identity')) {
            $common.outcome = 'encoded-response-rejected'
            return [pscustomobject]$common
        }
        if ($null -ne $response.Content.Headers.ContentLength -and [long]$response.Content.Headers.ContentLength -gt $MaxBytes) {
            $common.outcome = 'response-over-budget'
            $common.declaredByteSize = [long]$response.Content.Headers.ContentLength
            return [pscustomobject]$common
        }

        $bodyStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $memory = [IO.MemoryStream]::new([Math]::Min($MaxBytes, 1048576))
        $buffer = [byte[]]::new(65536)
        $overBudget = $false
        while ($true) {
            $remainingPlusOne = $MaxBytes - [int]$memory.Length + 1
            if ($remainingPlusOne -le 0) { $overBudget = $true; break }
            $requested = [Math]::Min($buffer.Length, $remainingPlusOne)
            $read = $bodyStream.ReadAsync($buffer, 0, $requested, $linkedCts.Token).GetAwaiter().GetResult()
            if ($read -eq 0) { break }
            if ($memory.Length + $read -gt $MaxBytes) { $overBudget = $true; break }
            $memory.Write($buffer, 0, $read)
        }
        [Array]::Clear($buffer, 0, $buffer.Length)
        if ($overBudget) {
            $common.outcome = 'response-over-budget'
            $common.maxByteSize = $MaxBytes
            return [pscustomobject]$common
        }
        $bytes = $memory.ToArray()
        if ($bytes.Length -eq 0) {
            $common.outcome = 'empty-response-body'
            return [pscustomobject]$common
        }
        $common.outcome = 'response-captured'
        $common.htmlBytes = $bytes
        $common.htmlByteSize = $bytes.Length
        $common.htmlSha256 = Get-MarkdownRendererSha256 -Bytes $bytes
        return [pscustomobject]$common
    }
    catch [OperationCanceledException] {
        $outcome = if ($CancellationToken.IsCancellationRequested) { 'cancelled' } elseif ($timeoutCts.IsCancellationRequested) { 'request-timeout' } else { 'request-cancelled' }
        return [pscustomobject][ordered]@{ requestUrl = $requestUri.AbsoluteUri; finalUrl = $finalUri; httpStatus = $status; contentType = $contentType; responseAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); rateLimited = $false; outcome = $outcome }
    }
    catch {
        $failureOutcome = if ($status -eq 200) { 'response-body-read-failure' } else { 'upstream-network-failure' }
        return [pscustomobject][ordered]@{ requestUrl = $requestUri.AbsoluteUri; finalUrl = $finalUri; httpStatus = $status; contentType = $contentType; responseAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); rateLimited = $false; outcome = $failureOutcome; safeFailureType = $_.Exception.GetType().FullName }
    }
    finally {
        if ($null -ne $memory) { $memory.Dispose() }
        if ($null -ne $bodyStream) { $bodyStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
        $linkedCts.Dispose()
        $timeoutCts.Dispose()
    }
}

function New-MarkdownRendererCorrectnessHtmlHttpClient {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = $false
    $handler.PreAuthenticate = $false
    $handler.Credentials = $null
    $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
    $client = [Net.Http.HttpClient]::new($handler, $true)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    return $client
}

function Write-MarkdownRendererCorrectnessHtmlObject {
    param([Parameter(Mandatory)][string] $OutputDirectory, [Parameter(Mandatory)][byte[]] $Bytes, [Parameter(Mandatory)][string] $ExpectedSha256)
    $actualHash = Get-MarkdownRendererSha256 -Bytes $Bytes
    if ($actualHash -cne $ExpectedSha256) { throw 'response-body-sha256-changed-before-storage' }
    $path = Get-MarkdownRendererCorrectnessHtmlObjectPath -Root $OutputDirectory -Sha256 $actualHash
    [void](Assert-MarkdownRendererNoReparsePath -Path $path)
    if ([IO.File]::Exists($path)) {
        $script:MarkdownRendererCorrectnessHtmlObjectReadCount++
        $existing = Read-MarkdownRendererCorrectnessHtmlBodyBounded -Path $path -MaxBytes 33554432
        try {
            if ($existing.Length -ne $Bytes.Length -or (Get-MarkdownRendererSha256 -Bytes $existing) -cne $actualHash) {
                throw 'existing-content-addressed-html-object-is-corrupt-and-will-not-be-overwritten'
            }
        }
        finally { [Array]::Clear($existing, 0, $existing.Length) }
    }
    else {
        Write-MarkdownRendererBytesAtomically -Path $path -Bytes $Bytes
        $script:MarkdownRendererCorrectnessHtmlObjectReadCount++
        $verified = Read-MarkdownRendererCorrectnessHtmlBodyBounded -Path $path -MaxBytes 33554432
        try {
            if ($verified.Length -ne $Bytes.Length -or (Get-MarkdownRendererSha256 -Bytes $verified) -cne $actualHash) {
                throw 'stored-content-addressed-html-object-failed-verification'
            }
        }
        finally { [Array]::Clear($verified, 0, $verified.Length) }
    }
    $root = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $relativePath = $path.Substring($root.Length).Replace('\', '/')
    return [pscustomobject]@{ RelativePath = $relativePath; AbsolutePath = $path }
}

function New-MarkdownRendererCorrectnessHtmlAttemptBase {
    param([Parameter(Mandatory)][string] $Kind, [Parameter(Mandatory)][object] $Identity, [Parameter(Mandatory)][string] $StartedAtUtc)
    return [ordered]@{
        schemaVersion = 1
        recordKind = $Kind
        contract = $script:MarkdownRendererCorrectnessHtmlContract
        rank = [int]$Identity.rank
        repositoryId = [long]$Identity.repositoryId
        fullName = [string]$Identity.fullName
        commitSha = [string]$Identity.commitSha
        defaultBranch = [string]$Identity.defaultBranch
        readmePath = [string]$Identity.readmePath
        expectedReadmeByteSize = [long]$Identity.expectedByteSize
        expectedReadmeGitBlobSha1 = [string]$Identity.expectedGitBlobSha1
        requestUrl = [string]$Identity.htmlUrl
        expectedRawReadmeUrl = [string]$Identity.downloadUrl
        sourceObjectPath = [string]$Identity.sourceObjectPath
        startedAtUtc = $StartedAtUtc
    }
}

function Get-MarkdownRendererCorrectnessHtmlRunStatus {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary] $CaseOutcomes,
        [Parameter(Mandatory)][string] $RunStartedAtUtc,
        [AllowNull()][string] $RunStopReason,
        [int] $ResponseRecordCount = -1,
        [Parameter(Mandatory)][int] $ContentObjectHashReadCount,
        [Parameter(Mandatory)][int] $ContentAddressedObjectsVerifiedThisRun,
        [Parameter(Mandatory)][string] $FullIntegrityVerification
    )
    $counts = @{}
    $captured = 0
    $integrityFailures = 0
    foreach ($outcome in $CaseOutcomes.Values) {
        if (-not $counts.ContainsKey($outcome)) { $counts[$outcome] = 0 }
        $counts[$outcome] = [int]$counts[$outcome] + 1
        if ($outcome -ceq 'response-captured') { $captured++ }
        if ($outcome -ceq 'local-integrity-failure') { $integrityFailures++ }
    }
    if ($ResponseRecordCount -lt 0) { $ResponseRecordCount = $captured }
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        contract = $script:MarkdownRendererCorrectnessHtmlContract
        frozenReadmeCorpusSha256 = $script:MarkdownRendererCorrectnessHtmlCorpusSha256
        frozenReadmeCount = 500
        runStartedAtUtc = $RunStartedAtUtc
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        runStopReason = $RunStopReason
        outcomes = $counts
        responseCapturedRecordCount = $ResponseRecordCount
        localIntegrityFailureCount = $integrityFailures
        all500ResponseRecordsCaptured = ($ResponseRecordCount -eq 500)
        all500ResponsesCaptured = ($ResponseRecordCount -eq 500 -and $integrityFailures -eq 0 -and $FullIntegrityVerification -ceq 'passed')
        fullIntegrityVerification = $FullIntegrityVerification
        verifiedResponseCount = if ($FullIntegrityVerification -ceq 'passed') { 500 } else { 0 }
        contentAddressedObjectsVerifiedThisRun = $ContentAddressedObjectsVerifiedThisRun
        contentObjectHashReadCount = $ContentObjectHashReadCount
        auditInputReady = $false
        captureReady = $false
        phase3Status = 'not-evaluated'
        auditStatus = 'not-evaluated'
        resourceCompleteness = 'not-evaluated'
        browserRendered = $false
    }
}

function Get-MarkdownRendererCorrectnessHtmlCaseOutcomeMap {
    param([Parameter(Mandatory)][string] $OutputDirectory, [Parameter(Mandatory)][object[]] $Identities)
    $outcomes = @{}
    foreach ($identity in $Identities) {
        $paths = Get-MarkdownRendererCorrectnessHtmlCasePaths -OutputDirectory $OutputDirectory -Rank ([int]$identity.rank)
        $outcome = 'pending'
        if ([IO.File]::Exists($paths.Result)) {
            try {
                $attempt = Read-MarkdownRendererCorrectnessHtmlJson -Path $paths.Result -MaxBytes 65536
                if (-not (Test-MarkdownRendererCorrectnessHtmlAttemptIdentity -Attempt $attempt -Identity $identity)) { $outcome = 'local-integrity-failure' }
                else {
                    $outcome = [string]$attempt.outcome
                    if ($outcome -ceq 'response-captured' -and -not (Test-MarkdownRendererCorrectnessHtmlCapturedMetadata -OutputDirectory $OutputDirectory -Attempt $attempt -Identity $identity)) {
                        $outcome = 'local-integrity-failure'
                    }
                }
            }
            catch { $outcome = 'local-integrity-failure' }
        }
        elseif ([IO.File]::Exists($paths.Start)) { $outcome = 'interrupted-request-outcome-unknown' }
        $outcomes[[int]$identity.rank] = $outcome
    }
    return $outcomes
}

function Complete-MarkdownRendererCorrectnessHtmlSnapshotIntegrity {
    param(
        [Parameter(Mandatory)][string] $OutputDirectory,
        [Parameter(Mandatory)][object[]] $Identities,
        [Parameter(Mandatory)][System.Collections.IDictionary] $CaseOutcomes,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]] $VerifiedObjectHashes
    )
    if (@($CaseOutcomes.Values | Where-Object { $_ -ceq 'response-captured' }).Count -ne 500) {
        return [pscustomobject]@{ Status = 'not-run'; VerifiedResponseCount = 0 }
    }

    # This is the one full-snapshot pass. Verify each unique content-addressed file
    # once at the end, even when it was validated during an earlier resume/run.
    $verifiedHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $checkedHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($identity in $Identities) {
        $rank = [int]$identity.rank
        if ($CaseOutcomes[$rank] -cne 'response-captured') { continue }
        $paths = Get-MarkdownRendererCorrectnessHtmlCasePaths -OutputDirectory $OutputDirectory -Rank $rank
        try {
            $attempt = Read-MarkdownRendererCorrectnessHtmlJson -Path $paths.Result -MaxBytes 65536
            if (-not (Test-MarkdownRendererCorrectnessHtmlAttemptIdentity -Attempt $attempt -Identity $identity) -or
                -not (Test-MarkdownRendererCorrectnessHtmlCapturedMetadata -OutputDirectory $OutputDirectory -Attempt $attempt -Identity $identity)) {
                $CaseOutcomes[$rank] = 'local-integrity-failure'
                continue
            }
            $hash = [string]$attempt.htmlSha256
            if ($checkedHashes.Contains($hash)) {
                if (-not $verifiedHashes.Contains($hash)) { $CaseOutcomes[$rank] = 'local-integrity-failure' }
                continue
            }
            [void]$checkedHashes.Add($hash)
            if ($null -eq (Read-MarkdownRendererCorrectnessHtmlVerifiedObject -OutputDirectory $OutputDirectory -Attempt $attempt -Identity $identity)) {
                $CaseOutcomes[$rank] = 'local-integrity-failure'
                continue
            }
            [void]$verifiedHashes.Add($hash)
            [void]$VerifiedObjectHashes.Add($hash)
        }
        catch { $CaseOutcomes[$rank] = 'local-integrity-failure' }
    }
    $failed = @($CaseOutcomes.Values | Where-Object { $_ -ceq 'local-integrity-failure' }).Count -gt 0
    return [pscustomobject]@{
        Status = if ($failed) { 'failed' } else { 'passed' }
        VerifiedResponseCount = if ($failed) { 0 } else { 500 }
    }
}

function Invoke-MarkdownRendererCorrectnessHtmlAcquisitionCore {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]] $Identities,
        [Parameter(Mandatory)][string] $SourceRoot,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [Parameter(Mandatory)][Net.Http.HttpClient] $HttpClient,
        [ValidateRange(1, 500)][int] $StartRank = 1,
        [ValidateRange(1, 500)][int] $Count = 500,
        [ValidateRange(1, 33554432)][int] $MaxBytes = 33554432,
        [ValidateRange(5, 120)][int] $TimeoutSeconds = 30,
        [ValidateRange(0, 10000)][int] $InterRequestDelayMilliseconds = 1000,
        [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None
    )
    if ($Identities.Count -ne 500) { throw 'response-only-acquisition-requires-all-500-frozen-identities' }
    if ($StartRank + $Count - 1 -gt 500) { throw 'requested-rank-range-exceeds-frozen-corpus' }
    $outputFull = [IO.Path]::GetFullPath($OutputDirectory)
    $sourceFull = [IO.Path]::GetFullPath($SourceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($outputFull -eq $sourceFull -or $outputFull.StartsWith($sourceFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceFull.StartsWith($outputFull.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'response-snapshot-output-must-be-separate-from-frozen-source-root'
    }
    [void](Assert-MarkdownRendererNoReparsePath -Path $outputFull)
    [IO.Directory]::CreateDirectory($outputFull) | Out-Null
    [void](Assert-MarkdownRendererNoReparsePath -Path $outputFull)

    $frozenSources = [pscustomobject]@{ Corpus = [pscustomobject]@{ Sha256 = $script:MarkdownRendererCorrectnessHtmlCorpusSha256 }; SourceRoot = $sourceFull; Records = @($Identities) }
    $index = New-MarkdownRendererCorrectnessHtmlSnapshotIndex -FrozenSources $frozenSources
    $indexPath = Join-Path $outputFull 'snapshot-index.json'
    if ([IO.File]::Exists($indexPath)) { Assert-MarkdownRendererCorrectnessHtmlIndexMatches -IndexPath $indexPath -ExpectedIndex $index }
    else {
        $unexpectedExisting = @([IO.Directory]::EnumerateFileSystemEntries($outputFull) | Where-Object { [IO.Path]::GetFileName($_) -cne '.acquisition.lock' })
        if ($unexpectedExisting.Count -gt 0) { throw 'existing-response-directory-without-matching-snapshot-index-is-not-reinterpreted' }
        Write-MarkdownRendererCorrectnessHtmlImmutableJson -Path $indexPath -Value $index
    }

    $runStarted = [DateTimeOffset]::UtcNow.ToString('o')
    $stopReason = $null
    $script:MarkdownRendererCorrectnessHtmlObjectReadCount = 0
    $caseOutcomes = Get-MarkdownRendererCorrectnessHtmlCaseOutcomeMap -OutputDirectory $outputFull -Identities $Identities
    $verifiedObjectHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $fullIntegrityVerification = 'not-run'
    $responseRecordCount = @($caseOutcomes.Values | Where-Object { $_ -ceq 'response-captured' }).Count
    $requested = @($Identities | Where-Object { [int]$_.rank -ge $StartRank -and [int]$_.rank -lt ($StartRank + $Count) } | Sort-Object rank)
    if ($requested.Count -ne $Count) { throw 'frozen-identity-range-is-incomplete' }
    foreach ($identity in $requested) {
        if ($CancellationToken.IsCancellationRequested) { $stopReason = 'caller-cancelled'; break }
        $paths = Get-MarkdownRendererCorrectnessHtmlCasePaths -OutputDirectory $outputFull -Rank ([int]$identity.rank)
        [IO.Directory]::CreateDirectory($paths.Directory) | Out-Null
        [void](Assert-MarkdownRendererNoReparsePath -Path $paths.Directory)
        if ([IO.File]::Exists($paths.Result)) {
            try {
                $existingAttempt = Read-MarkdownRendererCorrectnessHtmlJson -Path $paths.Result -MaxBytes 65536
                if (-not (Test-MarkdownRendererCorrectnessHtmlAttemptIdentity -Attempt $existingAttempt -Identity $identity)) { $stopReason = "case-identity-corruption-rank-$($identity.rank)"; break }
                if ([string]$existingAttempt.outcome -ceq 'response-captured' -and $responseRecordCount -ne 500) {
                    $existingHash = [string]$existingAttempt.htmlSha256
                    if (-not $verifiedObjectHashes.Contains($existingHash)) {
                        if ($null -eq (Read-MarkdownRendererCorrectnessHtmlVerifiedObject -OutputDirectory $outputFull -Attempt $existingAttempt -Identity $identity)) {
                            $caseOutcomes[[int]$identity.rank] = 'local-integrity-failure'
                            $stopReason = "cached-response-integrity-failure-rank-$($identity.rank)"
                            break
                        }
                        [void]$verifiedObjectHashes.Add($existingHash)
                    }
                }
                continue
            }
            catch { $stopReason = "case-result-integrity-failure-rank-$($identity.rank)"; break }
        }
        if ([IO.File]::Exists($paths.Start)) {
            $startRecord = $null
            try { $startRecord = Read-MarkdownRendererCorrectnessHtmlJson -Path $paths.Start -MaxBytes 65536 } catch {}
            if ($null -eq $startRecord -or -not (Test-MarkdownRendererCorrectnessHtmlAttemptIdentity -Attempt $startRecord -Identity $identity)) { $stopReason = "case-start-integrity-failure-rank-$($identity.rank)"; break }
            $interrupted = New-MarkdownRendererCorrectnessHtmlAttemptBase -Kind 'terminal-result' -Identity $identity -StartedAtUtc ([string]$startRecord.startedAtUtc)
            $interrupted.outcome = 'interrupted-request-outcome-unknown'
            $interrupted.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            Write-MarkdownRendererCorrectnessHtmlImmutableJson -Path $paths.Result -Value $interrupted
            $caseOutcomes[[int]$identity.rank] = 'interrupted-request-outcome-unknown'
            continue
        }

        $expectedSourceRelativePath = Join-Path 'objects/git-blob-sha1' (Join-Path ([string]$identity.expectedGitBlobSha1).Substring(0, 2) "$($identity.expectedGitBlobSha1).md")
        if ($null -ne $identity.PSObject.Properties['sourceObjectPath'] -and [string]$identity.sourceObjectPath -cne $expectedSourceRelativePath.Replace('\', '/')) {
            $failure = New-MarkdownRendererCorrectnessHtmlAttemptBase -Kind 'terminal-result' -Identity $identity -StartedAtUtc $runStarted
            $failure.outcome = 'frozen-source-object-path-mismatch'
            $failure.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            Write-MarkdownRendererCorrectnessHtmlImmutableJson -Path $paths.Result -Value $failure
            $caseOutcomes[[int]$identity.rank] = [string]$failure.outcome
            continue
        }
        $sourcePath = Join-Path $sourceFull $expectedSourceRelativePath
        $sourceProof = Test-MarkdownRendererGitBlobObject -Path $sourcePath -ExpectedByteSize ([long]$identity.expectedByteSize) -ExpectedGitBlobSha1 ([string]$identity.expectedGitBlobSha1)
        if (-not $sourceProof.Valid) {
            $failure = New-MarkdownRendererCorrectnessHtmlAttemptBase -Kind 'terminal-result' -Identity $identity -StartedAtUtc $runStarted
            $failure.outcome = 'frozen-source-object-invalid'
            $failure.sourceFailure = [string]$sourceProof.Reason
            $failure.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            Write-MarkdownRendererCorrectnessHtmlImmutableJson -Path $paths.Result -Value $failure
            $caseOutcomes[[int]$identity.rank] = [string]$failure.outcome
            continue
        }

        $startRecord = New-MarkdownRendererCorrectnessHtmlAttemptBase -Kind 'request-start' -Identity $identity -StartedAtUtc ([DateTimeOffset]::UtcNow.ToString('o'))
        $startRecord.sourceByteSize = [long]$identity.expectedByteSize
        $startRecord.sourceGitBlobSha1 = [string]$identity.expectedGitBlobSha1
        $startRecord.sourceSha256 = [string]$sourceProof.Sha256
        $startRecord.sourceVerifiedBeforeRequest = $true
        Write-MarkdownRendererCorrectnessHtmlImmutableJson -Path $paths.Start -Value $startRecord

        $fetch = Invoke-MarkdownRendererCorrectnessHtmlGet -HttpClient $HttpClient -Identity $identity -MaxBytes $MaxBytes -TimeoutSeconds $TimeoutSeconds -CancellationToken $CancellationToken
        $result = New-MarkdownRendererCorrectnessHtmlAttemptBase -Kind 'terminal-result' -Identity $identity -StartedAtUtc ([string]$startRecord.startedAtUtc)
        $result.outcome = [string]$fetch.outcome
        $result.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $result.sourceByteSize = [long]$identity.expectedByteSize
        $result.sourceGitBlobSha1 = [string]$identity.expectedGitBlobSha1
        $result.sourceSha256 = [string]$sourceProof.Sha256
        $result.sourceVerifiedBeforeRequest = $true
        foreach ($propertyName in @('requestUrl', 'finalUrl', 'httpStatus', 'contentType', 'responseAtUtc', 'rateLimited', 'locationHeader', 'declaredByteSize', 'maxByteSize', 'safeFailureType')) {
            if ($null -ne $fetch.PSObject.Properties[$propertyName]) { $result[$propertyName] = $fetch.$propertyName }
        }
        if ([string]$fetch.outcome -ceq 'response-captured') {
            try {
                $stored = Write-MarkdownRendererCorrectnessHtmlObject -OutputDirectory $outputFull -Bytes ([byte[]]$fetch.htmlBytes) -ExpectedSha256 ([string]$fetch.htmlSha256)
                $result.htmlSha256 = [string]$fetch.htmlSha256
                $result.htmlByteSize = [long]$fetch.htmlByteSize
                $result.htmlObjectPath = [string]$stored.RelativePath
                $result.htmlObjectVerified = $true
                [void]$verifiedObjectHashes.Add([string]$fetch.htmlSha256)
            }
            catch {
                $result.outcome = 'local-object-storage-failure'
                $result.safeFailureType = $_.Exception.GetType().FullName
            }
            finally { if ($null -ne $fetch.htmlBytes) { [Array]::Clear([byte[]]$fetch.htmlBytes, 0, ([byte[]]$fetch.htmlBytes).Length) } }
        }
        Write-MarkdownRendererCorrectnessHtmlImmutableJson -Path $paths.Result -Value $result
        $caseOutcomes[[int]$identity.rank] = [string]$result.outcome
        if ([string]$result.outcome -ceq 'response-captured') { $responseRecordCount++ }
        $status = Get-MarkdownRendererCorrectnessHtmlRunStatus -CaseOutcomes $caseOutcomes -RunStartedAtUtc $runStarted -RunStopReason $stopReason -ResponseRecordCount $responseRecordCount -ContentObjectHashReadCount $script:MarkdownRendererCorrectnessHtmlObjectReadCount -ContentAddressedObjectsVerifiedThisRun $verifiedObjectHashes.Count -FullIntegrityVerification $fullIntegrityVerification
        Write-MarkdownRendererJsonAtomically -Path (Join-Path $outputFull 'snapshot-status.json') -Value $status
        if ([bool]$fetch.rateLimited) { $stopReason = 'upstream-rate-limit'; break }
        if ($InterRequestDelayMilliseconds -gt 0 -and $identity.rank -lt ($StartRank + $Count - 1)) {
            try { [Threading.Tasks.Task]::Delay($InterRequestDelayMilliseconds, $CancellationToken).GetAwaiter().GetResult() }
            catch [OperationCanceledException] { $stopReason = 'caller-cancelled'; break }
        }
    }
    $integrity = Complete-MarkdownRendererCorrectnessHtmlSnapshotIntegrity -OutputDirectory $outputFull -Identities $Identities -CaseOutcomes $caseOutcomes -VerifiedObjectHashes $verifiedObjectHashes
    $fullIntegrityVerification = [string]$integrity.Status
    $status = Get-MarkdownRendererCorrectnessHtmlRunStatus -CaseOutcomes $caseOutcomes -RunStartedAtUtc $runStarted -RunStopReason $stopReason -ResponseRecordCount $responseRecordCount -ContentObjectHashReadCount $script:MarkdownRendererCorrectnessHtmlObjectReadCount -ContentAddressedObjectsVerifiedThisRun $verifiedObjectHashes.Count -FullIntegrityVerification $fullIntegrityVerification
    Write-MarkdownRendererJsonAtomically -Path (Join-Path $outputFull 'snapshot-status.json') -Value $status
    return $status
}

function Invoke-MarkdownRendererCorrectnessHtmlAcquisition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $CorpusManifestPath,
        [Parameter(Mandatory)][string] $SourceRoot,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [ValidateRange(1, 500)][int] $StartRank = 1,
        [ValidateRange(1, 500)][int] $Count = 500,
        [ValidateRange(1, 32)][int] $MaxHtmlMiB = 32,
        [ValidateRange(5, 120)][int] $RequestTimeoutSeconds = 30,
        [ValidateRange(0, 10000)][int] $InterRequestDelayMilliseconds = 1000
    )
    if ($StartRank + $Count - 1 -gt 500) { throw 'requested-rank-range-exceeds-frozen-corpus' }
    $frozen = Get-MarkdownRendererCorrectnessHtmlFrozenSources -CorpusManifestPath $CorpusManifestPath -SourceRoot $SourceRoot
    $outputFull = [IO.Path]::GetFullPath($OutputDirectory)
    [void](Assert-MarkdownRendererNoReparsePath -Path $outputFull)
    [IO.Directory]::CreateDirectory($outputFull) | Out-Null
    [void](Assert-MarkdownRendererNoReparsePath -Path $outputFull)
    $lockPath = Join-Path $outputFull '.acquisition.lock'
    [void](Assert-MarkdownRendererNoReparsePath -Path $lockPath)
    $lock = $null
    $client = New-MarkdownRendererCorrectnessHtmlHttpClient
    try {
        $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        return Invoke-MarkdownRendererCorrectnessHtmlAcquisitionCore -Identities $frozen.Records -SourceRoot $frozen.SourceRoot -OutputDirectory $outputFull -HttpClient $client -StartRank $StartRank -Count $Count -MaxBytes ($MaxHtmlMiB * 1048576) -TimeoutSeconds $RequestTimeoutSeconds -InterRequestDelayMilliseconds $InterRequestDelayMilliseconds
    }
    finally {
        if ($null -ne $lock) { $lock.Dispose() }
        $client.Dispose()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $status = Invoke-MarkdownRendererCorrectnessHtmlAcquisition -CorpusManifestPath $CorpusManifestPath -SourceRoot $SourceRoot -OutputDirectory $OutputDirectory -StartRank $StartRank -Count $Count -MaxHtmlMiB $MaxHtmlMiB -RequestTimeoutSeconds $RequestTimeoutSeconds -InterRequestDelayMilliseconds $InterRequestDelayMilliseconds
    $status | ConvertTo-Json -Depth 12
    if ($status.localIntegrityFailureCount -gt 0) { exit 2 }
}
