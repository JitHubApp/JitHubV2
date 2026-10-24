$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path $PSScriptRoot '..\Invoke-MarkdownRendererTop500SourceAcquisition.ps1'
. $scriptPath

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$pinnedPath = Join-Path $repoRoot 'artifacts/readme-top500/corpus-36360809497/top-repositories.json'
$pinnedSha = '615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72'
$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "markdownrenderer-top500-source-tests-$([guid]::NewGuid().ToString('N'))"))
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null
$script:assertions = 0

$httpTestTypes = @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class MarkdownRendererTop500OverBudgetStream : Stream
{
    private readonly MemoryStream inner;
    private bool disposed;
    private int bytesRead;
    private bool readTokenCanBeCanceled;
    private bool cancellationRequestedWhenDisposed;
    private CancellationToken lastReadToken;

    public MarkdownRendererTop500OverBudgetStream(byte[] bytes)
    {
        inner = new MemoryStream(bytes, false);
    }

    public int BytesRead { get { return Interlocked.CompareExchange(ref bytesRead, 0, 0); } }
    public bool ReadTokenCanBeCanceled { get { return readTokenCanBeCanceled; } }
    public bool IsDisposed { get { return disposed; } }
    public bool CancellationRequestedWhenDisposed { get { return cancellationRequestedWhenDisposed; } }
    public override bool CanRead { get { return !disposed; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Interlocked.Add(ref bytesRead, read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        readTokenCanBeCanceled = cancellationToken.CanBeCanceled;
        lastReadToken = cancellationToken;
        return ReadAsyncCore(buffer, offset, count, cancellationToken);
    }

    private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref bytesRead, read);
        return read;
    }

    public override void Flush() { throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            cancellationRequestedWhenDisposed = lastReadToken.CanBeCanceled && lastReadToken.IsCancellationRequested;
            disposed = true;
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

public sealed class MarkdownRendererTop500OverBudgetHandler : HttpMessageHandler
{
    private int requestCount;
    private MarkdownRendererTop500OverBudgetStream lastUnknownLengthStream;
    private bool unknownLengthHeaderWasAbsent;
    private bool declaredLengthWasOverBudget;

    public int RequestCount { get { return Interlocked.CompareExchange(ref requestCount, 0, 0); } }
    public MarkdownRendererTop500OverBudgetStream LastUnknownLengthStream { get { return lastUnknownLengthStream; } }
    public bool UnknownLengthHeaderWasAbsent { get { return unknownLengthHeaderWasAbsent; } }
    public bool DeclaredLengthWasOverBudget { get { return declaredLengthWasOverBudget; } }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requestCount);
        byte[] bytes = Encoding.ASCII.GetBytes("12345678ABCDEFGH");
        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
        if (request.RequestUri.AbsolutePath == "/declared-overrun")
        {
            response.Content = new ByteArrayContent(bytes);
            declaredLengthWasOverBudget = response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > 8;
        }
        else if (request.RequestUri.AbsolutePath == "/chunked-overrun")
        {
            lastUnknownLengthStream = new MarkdownRendererTop500OverBudgetStream(bytes);
            response.Content = new StreamContent(lastUnknownLengthStream);
            unknownLengthHeaderWasAbsent = !response.Content.Headers.ContentLength.HasValue;
            response.Headers.TransferEncodingChunked = true;
        }
        else
        {
            response.Dispose();
            throw new InvalidOperationException("Unexpected local HTTP test path.");
        }
        return Task.FromResult(response);
    }
}
'@
if (-not ('MarkdownRendererTop500OverBudgetHandler' -as [type])) {
    if ($PSVersionTable.PSVersion.Major -le 5) {
        Add-Type -TypeDefinition $httpTestTypes -Language CSharp -ReferencedAssemblies 'System.Net.Http.dll' -ErrorAction Stop
    }
    else {
        Add-Type -TypeDefinition $httpTestTypes -Language CSharp -ErrorAction Stop
    }
}

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    $script:assertions++
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Message)
    $script:assertions++
    $threw = $false
    try { & $Action } catch { $threw = $true }
    if (-not $threw) { throw $Message }
}

function New-FixtureRepository {
    param([Parameter(Mandatory)][int]$Rank, [Parameter(Mandatory)][byte[]]$Bytes)
    $name = "fixture/repository$Rank"
    $commit = ([string]$Rank).PadLeft(40, '0')
    $path = 'docs/README.md'
    $repo = [pscustomobject][ordered]@{
        rank = $Rank
        id = 10000 + $Rank
        fullName = $name
        defaultBranch = 'main'
        commitSha = $commit
        stars = 10
        url = "https://github.com/$name"
        pushedAtUtc = '2026-09-28T00:00:00+00:00'
        readme = [pscustomobject][ordered]@{
            available = $true
            byteSize = $Bytes.LongLength
            path = $path
            sha = Get-MarkdownRendererGitBlobSha1 -Bytes $Bytes
            htmlUrl = "https://github.com/$name/blob/$commit/$path"
            downloadUrl = "https://raw.githubusercontent.com/$name/$commit/$path"
        }
    }
    return $repo
}

function Write-FixtureCorpus {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][object[]]$Repositories)
    $value = [pscustomobject][ordered]@{
        schemaVersion = 2
        generatedAtUtc = '2026-09-28T00:00:00+00:00'
        source = 'fixture'
        query = 'fixture'
        requestedCount = $Repositories.Count
        repositories = @($Repositories)
    }
    $json = ($value | ConvertTo-Json -Depth 10) + "`n"
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    [IO.File]::WriteAllText($Path, $json, $encoding)
    $bytes = [IO.File]::ReadAllBytes($Path)
    return [pscustomobject]@{ Path = $Path; Sha256 = Get-MarkdownRendererSha256 -Bytes $bytes }
}

function New-FetchOverride {
    param([Parameter(Mandatory)][scriptblock]$Body)
    return $Body
}

function Initialize-SymlinkFixtureApi {
    param(
        [Parameter(Mandatory)][string]$CommitSha,
        [Parameter(Mandatory)][string]$LinkBlobSha,
        [Parameter(Mandatory)][string]$TargetBlobSha
    )
    $script:symlinkRootTree = '1111111111111111111111111111111111111111'
    $script:symlinkDocsTree = '2222222222222222222222222222222222222222'
    $script:symlinkContentTree = '3333333333333333333333333333333333333333'
    $script:symlinkCommit = $CommitSha
    $script:symlinkLinkBlobSha = $LinkBlobSha
    $script:symlinkTargetBlobSha = $TargetBlobSha
    $script:symlinkMode = '120000'
    $script:symlinkTargetMode = '100644'
    $script:symlinkTargetType = 'blob'
    $script:symlinkLinkEntrySha = $LinkBlobSha
    $script:symlinkRawTargetBytes = $null
    $script:symlinkRawRedirect = $null
    $script:symlinkRawUris = [Collections.Generic.List[string]]::new()
    $script:symlinkApiUris = [Collections.Generic.List[string]]::new()
}

function Get-SymlinkApiOverride {
    return {
        param($uri)
        $script:symlinkApiUris.Add([string]$uri)
        if ($uri -ceq "https://api.github.com/repos/fixture/repository1/git/commits/$($script:symlinkCommit)") {
            return [pscustomobject]@{ StatusCode = 200; Json = [pscustomobject]@{ sha = $script:symlinkCommit; tree = [pscustomobject]@{ sha = $script:symlinkRootTree } }; Bytes = $null; Location = $null }
        }
        if ($uri -ceq "https://api.github.com/repos/fixture/repository1/git/trees/$($script:symlinkRootTree)") {
            return [pscustomobject]@{ StatusCode = 200; Json = [pscustomobject]@{ sha = $script:symlinkRootTree; truncated = $false; tree = @(
                [pscustomobject]@{ path = 'docs'; mode = '040000'; type = 'tree'; sha = $script:symlinkDocsTree },
                [pscustomobject]@{ path = 'content'; mode = '040000'; type = 'tree'; sha = $script:symlinkContentTree }) }; Bytes = $null; Location = $null }
        }
        if ($uri -ceq "https://api.github.com/repos/fixture/repository1/git/trees/$($script:symlinkDocsTree)") {
            return [pscustomobject]@{ StatusCode = 200; Json = [pscustomobject]@{ sha = $script:symlinkDocsTree; truncated = $false; tree = @([pscustomobject]@{ path = 'README.md'; mode = $script:symlinkMode; type = 'blob'; sha = $script:symlinkLinkEntrySha }) }; Bytes = $null; Location = $null }
        }
        if ($uri -ceq "https://api.github.com/repos/fixture/repository1/git/trees/$($script:symlinkContentTree)") {
            return [pscustomobject]@{ StatusCode = 200; Json = [pscustomobject]@{ sha = $script:symlinkContentTree; truncated = $false; tree = @([pscustomobject]@{ path = 'README.md'; mode = $script:symlinkTargetMode; type = $script:symlinkTargetType; sha = $script:symlinkTargetBlobSha }) }; Bytes = $null; Location = $null }
        }
        throw "Unexpected synthetic GitHub API URL '$uri'."
    }
}

function Get-SymlinkRawOverride {
    return {
        param($uri)
        $script:symlinkRawUris.Add([string]$uri)
        if ($null -ne $script:symlinkRawRedirect) {
            return [pscustomobject]@{ StatusCode = 302; Json = $null; Bytes = $null; Location = [string]$script:symlinkRawRedirect }
        }
        return [pscustomobject]@{ StatusCode = 200; Json = $null; Bytes = [byte[]]$script:symlinkRawTargetBytes; Location = $null }
    }
}

try {
    $pinned = Read-MarkdownRendererPinnedCorpus -Path $pinnedPath -ExpectedSha256 $pinnedSha -ExpectedCount 500
    Assert-True ($pinned.Identities.Count -eq 500) 'Pinned frozen manifest did not validate to 500 immutable README identities.'
    Assert-True ($pinned.Sha256 -ceq $pinnedSha) 'Pinned frozen manifest digest changed.'
    Assert-True ($pinned.Identities[0].rank -eq 1 -and $pinned.Identities[499].rank -eq 500) 'Pinned manifest ranks are not complete and ordered.'
    Assert-True ($pinned.Identities[0].downloadUrl -ceq 'https://raw.githubusercontent.com/codecrafters-io/build-your-own-x/aa17439b62f384511a5561ce308e9598b94d8989/README.md') 'Pinned rank-one raw URL no longer binds to the frozen commit and path.'
    Assert-Throws { Read-MarkdownRendererPinnedCorpus -Path $pinnedPath -ExpectedSha256 ('0' * 64) -ExpectedCount 500 } 'Manifest digest tampering was accepted.'
    $oversizedManifestPath = Join-Path $tempRoot 'oversized-manifest.json'
    [IO.File]::Copy($pinnedPath, $oversizedManifestPath)
    $manifestAppend = [IO.FileStream]::new($oversizedManifestPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $manifestAppend.SetLength(8388609) } finally { $manifestAppend.Dispose() }
    Assert-Throws { Read-MarkdownRendererPinnedCorpus -Path $oversizedManifestPath -ExpectedSha256 $pinnedSha -ExpectedCount 500 } 'An oversized frozen manifest was loaded instead of rejected at the 8 MiB bound.'

    $bytesOne = [Text.Encoding]::UTF8.GetBytes("# Fixture one`nExact bytes.`n")
    $bytesTwo = [Text.Encoding]::UTF8.GetBytes("# Fixture two`nAlso exact.`n")
    $fixtureCorpusPath = Join-Path $tempRoot 'fixture-corpus.json'
    $fixtureFile = Write-FixtureCorpus -Path $fixtureCorpusPath -Repositories @(
        (New-FixtureRepository -Rank 1 -Bytes $bytesOne),
        (New-FixtureRepository -Rank 2 -Bytes $bytesTwo))
    $fixtureCorpus = Read-MarkdownRendererPinnedCorpus -Path $fixtureCorpusPath -ExpectedSha256 $fixtureFile.Sha256 -ExpectedCount 2
    Assert-True ($fixtureCorpus.Identities.Count -eq 2) 'A valid small fixture corpus was rejected.'

    $localHttpHandler = [MarkdownRendererTop500OverBudgetHandler]::new()
    $localHttpClient = [Net.Http.HttpClient]::new($localHttpHandler, $true)
    $localHttpClient.Timeout = [TimeSpan]::FromSeconds(5)
    $localHttpClient.MaxResponseContentBufferSize = 8
    try {
        foreach ($httpCase in @(
                [pscustomobject]@{ Path = '/declared-overrun'; Description = 'declared Content-Length' },
                [pscustomobject]@{ Path = '/chunked-overrun'; Description = 'unknown-length chunked body' })) {
            $httpIdentity = [pscustomobject]@{ downloadUrl = "https://top500-acquisition-test.invalid$($httpCase.Path)" }
            $requestCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
            try {
                $initialTask = Start-MarkdownRendererReadmeRequest `
                    -HttpClient $localHttpClient `
                    -Identity $httpIdentity `
                    -CancellationToken $requestCancellation.Token
                $httpResult = Invoke-MarkdownRendererReadmeFetch `
                    -Identity $httpIdentity `
                    -MaxAttempts 3 `
                    -MaxReadmeBytes 8 `
                    -MaxRetryWaitSeconds 0 `
                    -HttpClient $localHttpClient `
                    -InitialTask $initialTask `
                    -InitialCancellationTokenSource $requestCancellation `
                    -RequestOverride $null
            }
            finally {
                $requestCancellation.Dispose()
            }
            Assert-True (-not $httpResult.Success -and $httpResult.Category -ceq 'upstream-content-mismatch' -and $httpResult.SafeMessage -ceq 'response-over-budget') "Actual HttpClient path did not classify the $($httpCase.Description) as a bounded-content rejection."
            Assert-True ($httpResult.HttpStatus -eq 200 -and $httpResult.Attempts -eq 1 -and -not $httpResult.Retry) "Actual HttpClient path lost HTTP 200 or retried the $($httpCase.Description)."
            $expectedRequestCount = if ($httpCase.Path -ceq '/declared-overrun') { 1 } else { 2 }
            Assert-True ($localHttpHandler.RequestCount -eq $expectedRequestCount) "Actual HttpClient path issued an unexpected retry for the $($httpCase.Description)."
        }
        Assert-True $localHttpHandler.DeclaredLengthWasOverBudget 'Declared-overrun fixture did not expose an over-budget Content-Length header.'
        $unknownLengthStream = $localHttpHandler.LastUnknownLengthStream
        Assert-True ($localHttpHandler.UnknownLengthHeaderWasAbsent -and $null -ne $unknownLengthStream) 'Chunked-overrun fixture unexpectedly declared a Content-Length or did not provide a streaming body.'
        Assert-True ($unknownLengthStream.BytesRead -eq 9 -and $unknownLengthStream.BytesRead -le 8 + 1) 'Unknown-length body reading exceeded the configured maximum plus the single detection byte.'
        Assert-True ($unknownLengthStream.ReadTokenCanBeCanceled -and $unknownLengthStream.CancellationRequestedWhenDisposed -and $unknownLengthStream.IsDisposed) 'The actual HttpClient body read did not receive cancellation, cancel its body token, or dispose the response stream after rejecting an over-budget body.'
    }
    finally {
        $localHttpClient.Dispose()
    }

    $tamperedRepo = New-FixtureRepository -Rank 1 -Bytes $bytesOne
    $tamperedRepo.readme.downloadUrl = 'https://example.invalid/readme.md'
    $tamperedPath = Join-Path $tempRoot 'tampered-url.json'
    $tampered = Write-FixtureCorpus -Path $tamperedPath -Repositories @($tamperedRepo)
    Assert-Throws { Read-MarkdownRendererPinnedCorpus -Path $tampered.Path -ExpectedSha256 $tampered.Sha256 -ExpectedCount 1 } 'A non-GitHub raw URL was accepted.'

    $duplicatePath = Join-Path $tempRoot 'duplicate-rank.json'
    $duplicate = Write-FixtureCorpus -Path $duplicatePath -Repositories @(
        (New-FixtureRepository -Rank 1 -Bytes $bytesOne),
        (New-FixtureRepository -Rank 1 -Bytes $bytesTwo))
    Assert-Throws { Read-MarkdownRendererPinnedCorpus -Path $duplicate.Path -ExpectedSha256 $duplicate.Sha256 -ExpectedCount 2 } 'Duplicate or missing manifest ranks were accepted.'

    $output = Join-Path $tempRoot 'acquisition'
    $script:fetchCalls = 0
    $script:fixtureBytesByRank = @{ 1 = $bytesOne; 2 = $bytesTwo }
    $successfulFetch = {
        param($identity, $attempt)
        $script:fetchCalls++
        [pscustomobject]@{ StatusCode = 200; Bytes = $script:fixtureBytesByRank[[int]$identity.rank]; RetryAfterSeconds = $null; RateLimited = $false; SafeMessage = $null }
    }
    $firstRun = Invoke-MarkdownRendererTop500Acquisition -Corpus $fixtureCorpus -OutputDirectory $output -MaxConcurrency 2 -RequestOverride $successfulFetch
    Assert-True ($firstRun.VerifiedCount -eq 2 -and $firstRun.DownloadedThisRun -eq 2 -and $firstRun.Complete) "Valid bodies were not verified and stored as complete: $(ConvertTo-Json -InputObject $firstRun -Compress)."
    Assert-True ($script:fetchCalls -eq 2) 'The first acquisition did not request each missing body once.'
    $objects = @($firstRun.ObjectDirectory | Get-ChildItem -Recurse -File)
    Assert-True ($objects.Count -eq 2) 'Expected one content-addressed object per distinct Git blob.'
    foreach ($identity in $fixtureCorpus.Identities) {
        $objectPath = Get-MarkdownRendererGitBlobObjectPath -ObjectDirectory $firstRun.ObjectDirectory -GitBlobSha1 $identity.expectedGitBlobSha1
        $object = Test-MarkdownRendererGitBlobObject -Path $objectPath -ExpectedByteSize $identity.expectedByteSize -ExpectedGitBlobSha1 $identity.expectedGitBlobSha1
        Assert-True $object.Valid 'Stored content-addressed bytes failed Git blob and byte-size verification.'
    }
    $oversizedObjectPath = Join-Path $tempRoot 'oversized-corrupt-object.md'
    $oversizedObjectStream = [IO.FileStream]::new($oversizedObjectPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $oversizedObjectStream.SetLength(10MB) } finally { $oversizedObjectStream.Dispose() }
    $oversizedObject = Test-MarkdownRendererGitBlobObject -Path $oversizedObjectPath -ExpectedByteSize 1 -ExpectedGitBlobSha1 (Get-MarkdownRendererGitBlobSha1 -Bytes ([byte[]]@(65)))
    Assert-True ($oversizedObject.Exists -and -not $oversizedObject.Valid -and $oversizedObject.Reason -ceq 'byte-size-mismatch') 'An oversized corrupt cache body was allocated/read before its expected size was rejected.'

    $script:fetchCalls = 0
    $noFetch = { param($identity, $attempt) $script:fetchCalls++; throw 'A verified cached body must never be fetched again.' }
    $resumeRun = Invoke-MarkdownRendererTop500Acquisition -Corpus $fixtureCorpus -OutputDirectory $output -MaxConcurrency 2 -RequestOverride $noFetch
    Assert-True ($resumeRun.VerifiedCount -eq 2 -and $resumeRun.ReusedVerified -eq 2 -and $script:fetchCalls -eq 0) 'Resume refetched a valid cached body.'

    $targetBytes = [Text.Encoding]::UTF8.GetBytes("# Linked README`nResolved from a commit-pinned symlink.`n")
    $linkBytes = [Text.Encoding]::UTF8.GetBytes('../content/README.md')
    $linkRepo = New-FixtureRepository -Rank 1 -Bytes $targetBytes
    $linkRepo.readme.path = 'docs/README.md'
    $linkRepo.readme.htmlUrl = "https://github.com/fixture/repository1/blob/$($linkRepo.commitSha)/docs/README.md"
    $linkRepo.readme.downloadUrl = "https://raw.githubusercontent.com/fixture/repository1/$($linkRepo.commitSha)/docs/README.md"
    $linkCorpusFile = Write-FixtureCorpus -Path (Join-Path $tempRoot 'symlink-corpus.json') -Repositories @($linkRepo)
    $linkCorpus = Read-MarkdownRendererPinnedCorpus -Path $linkCorpusFile.Path -ExpectedSha256 $linkCorpusFile.Sha256 -ExpectedCount 1
    Initialize-SymlinkFixtureApi -CommitSha $linkRepo.commitSha -LinkBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -TargetBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $targetBytes)
    $script:symlinkRawTargetBytes = $targetBytes
    $linkTopFetch = { param($identity, $attempt) [pscustomobject]@{ StatusCode = 200; Bytes = $linkBytes; RetryAfterSeconds = $null; RateLimited = $false; SafeMessage = $null } }
    $linkRun = Invoke-MarkdownRendererTop500Acquisition `
        -Corpus $linkCorpus `
        -OutputDirectory (Join-Path $tempRoot 'symlink-valid') `
        -MaxConcurrency 1 `
        -RequestOverride $linkTopFetch `
        -ApiRequestOverride (Get-SymlinkApiOverride) `
        -RawRequestOverride (Get-SymlinkRawOverride)
    $linkStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($linkRun.StatusPath, [Text.Encoding]::UTF8))
    $linkRecord = @($linkStatus.records)[0]
    Assert-True ($linkRun.VerifiedCount -eq 1 -and $linkRun.Complete) "A tree-proven same-commit README symlink did not resolve to the declared content: $(ConvertTo-Json -InputObject $linkRun -Compress); status=$([IO.File]::ReadAllText($linkRun.StatusPath, [Text.Encoding]::UTF8))."
    Assert-True ($linkRecord.fullName -ceq $linkRepo.fullName -and $linkRecord.commitSha -ceq $linkRepo.commitSha -and $linkRecord.readmePath -ceq 'docs/README.md') 'Symlink resolution changed the frozen original README identity.'
    Assert-True ($linkRecord.symlinkResolution.resolver -ceq 'github-git-tree-symlink-v1' -and $linkRecord.symlinkResolution.linkChain[0].linkMode -ceq '120000') 'Symlink proof omitted the authoritative Git tree mode.'
    Assert-True ($linkRecord.symlinkResolution.linkChain[0].linkGitBlobSha1 -ceq (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -and $linkRecord.symlinkResolution.finalTargetPath -ceq 'content/README.md') 'Symlink proof did not bind pointer bytes and the normalized target.'
    Assert-True ($linkRecord.symlinkResolution.schemaVersion -eq 1 -and $linkRecord.symlinkResolution.proofSha256 -match '^[0-9a-f]{64}$' -and $linkRecord.symlinkResolution.rootTreeSha -ceq $script:symlinkRootTree) 'New symlink proof omitted its version, root-tree authority, or integrity checksum.'
    Assert-True ($linkRecord.initialResponseByteSize -eq $linkBytes.Length -and $linkRecord.initialResponseGitBlobSha1 -ceq (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -and $linkRecord.observedByteSize -eq $targetBytes.Length -and $linkRecord.observedGitBlobSha1 -ceq (Get-MarkdownRendererGitBlobSha1 -Bytes $targetBytes)) 'Status does not distinguish the pointer response from the accepted target README bytes.'
    Assert-True ($script:symlinkRawUris.Count -eq 1 -and $script:symlinkRawUris[0] -ceq "https://raw.githubusercontent.com/fixture/repository1/$($linkRepo.commitSha)/content/README.md") 'Symlink resolution fetched outside the same repository and full commit.'

    $noSymlinkRefetch = { param($identity, $attempt) throw 'A verified symlink-resolved body must not be fetched again.' }
    $linkResume = Invoke-MarkdownRendererTop500Acquisition `
        -Corpus $linkCorpus `
        -OutputDirectory (Join-Path $tempRoot 'symlink-valid') `
        -MaxConcurrency 1 `
        -RequestOverride $noSymlinkRefetch
    $resumedStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($linkResume.StatusPath, [Text.Encoding]::UTF8))
    $resumedRecord = @($resumedStatus.records)[0]
    Assert-True ($linkResume.VerifiedCount -eq 1 -and $linkResume.ReusedVerified -eq 1 -and $resumedRecord.symlinkResolution.finalTargetPath -ceq 'content/README.md') 'Safe resume refetched or discarded valid symlink provenance.'
    Assert-True ($resumedRecord.initialResponseByteSize -eq $linkBytes.Length -and $resumedRecord.initialResponseGitBlobSha1 -ceq (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -and $resumedRecord.observedByteSize -eq $targetBytes.Length -and $resumedRecord.observedGitBlobSha1 -ceq (Get-MarkdownRendererGitBlobSha1 -Bytes $targetBytes)) 'Resume did not reconstruct the pointer observation and accepted target hashes from the validated proof and cached body.'
    $goodProofStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($linkResume.StatusPath, [Text.Encoding]::UTF8))
    $goodProof = @($goodProofStatus.records)[0].symlinkResolution
    $proofHashFields = @(
        [pscustomobject]@{ Name = 'commitSha'; Path = @('commitSha') },
        [pscustomobject]@{ Name = 'rootTreeSha'; Path = @('rootTreeSha') },
        [pscustomobject]@{ Name = 'linkGitBlobSha1'; Path = @('linkChain', 0, 'linkGitBlobSha1') },
        [pscustomobject]@{ Name = 'fetchedLinkGitBlobSha1'; Path = @('linkChain', 0, 'fetchedLinkGitBlobSha1') },
        [pscustomobject]@{ Name = 'linkParentTreeSha'; Path = @('linkChain', 0, 'linkParentTreeSha') },
        [pscustomobject]@{ Name = 'targetGitObjectSha'; Path = @('linkChain', 0, 'targetGitObjectSha') },
        [pscustomobject]@{ Name = 'targetParentTreeSha'; Path = @('linkChain', 0, 'targetParentTreeSha') },
        [pscustomobject]@{ Name = 'finalTreeEntrySha'; Path = @('finalTreeEntrySha') },
        [pscustomobject]@{ Name = 'finalContentGitBlobSha1'; Path = @('finalContentGitBlobSha1') },
        [pscustomobject]@{ Name = 'proofSha256'; Path = @('proofSha256') })
    foreach ($field in $proofHashFields) {
        $copy = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $goodProof -Depth 20 -Compress)
        $target = $copy
        for ($pathIndex = 0; $pathIndex -lt ($field.Path.Count - 1); $pathIndex++) {
            $segment = $field.Path[$pathIndex]
            $target = if ($segment -is [int]) { $target[$segment] } else { $target.PSObject.Properties[[string]$segment].Value }
        }
        $leaf = $field.Path[$field.Path.Count - 1]
        $originalHex = if ($leaf -is [int]) { [string]$target[$leaf] } else { [string]$target.PSObject.Properties[[string]$leaf].Value }
        $replacement = if ($originalHex[0] -ceq '0') { '1' + $originalHex.Substring(1) } else { '0' + $originalHex.Substring(1) }
        if ($leaf -is [int]) { $target[$leaf] = $replacement } else { $target.PSObject.Properties[[string]$leaf].Value = $replacement }
        Assert-True (-not (Test-MarkdownRendererSymlinkResolutionProof -Proof $copy -Identity $linkCorpus.Identities[0])) "Checksummed symlink proof accepted tampered hash field '$($field.Name)'."
    }

    # A one-time migration must bind every legacy tree fact to the pinned commit before adding the new checksum.
    $legacyStatus = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $goodProofStatus -Depth 20 -Compress)
    $legacyProof = @($legacyStatus.records)[0].symlinkResolution
    [void]$legacyProof.PSObject.Properties.Remove('schemaVersion')
    [void]$legacyProof.PSObject.Properties.Remove('proofSha256')
    [void]$legacyProof.PSObject.Properties.Remove('rootTreeSha')
    [void]$legacyProof.linkChain[0].PSObject.Properties.Remove('linkType')
    Write-MarkdownRendererJsonAtomically -Path $linkResume.StatusPath -Value $legacyStatus
    Initialize-SymlinkFixtureApi -CommitSha $linkRepo.commitSha -LinkBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -TargetBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $targetBytes)
    $migrationTopCalls = 0
    $migrationNoBody = { param($identity, $attempt) $script:migrationTopCalls++; throw 'Legacy proof migration must never refetch the accepted README body.' }
    $migrationNoRaw = { param($uri, $attempt) $script:symlinkRawUris.Add([string]$uri); throw 'Legacy proof migration must not fetch raw README bytes.' }
    $migrationRun = Invoke-MarkdownRendererTop500Acquisition `
        -Corpus $linkCorpus `
        -OutputDirectory (Join-Path $tempRoot 'symlink-valid') `
        -MaxConcurrency 1 `
        -RequestOverride $migrationNoBody `
        -ApiRequestOverride (Get-SymlinkApiOverride) `
        -RawRequestOverride $migrationNoRaw
    $migrationStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($migrationRun.StatusPath, [Text.Encoding]::UTF8))
    $migratedRecord = @($migrationStatus.records)[0]
    Assert-True ($migrationRun.VerifiedCount -eq 1 -and $migrationRun.Complete -and $script:migrationTopCalls -eq 0 -and $script:symlinkRawUris.Count -eq 0) 'Legacy proof migration refetched the accepted README body or failed cached verification.'
    Assert-True ($migratedRecord.symlinkResolution.schemaVersion -eq 1 -and $migratedRecord.symlinkResolution.proofSha256 -match '^[0-9a-f]{64}$' -and $migratedRecord.symlinkResolution.rootTreeSha -ceq $script:symlinkRootTree) 'Legacy proof migration did not persist a checksummed proof tied to live pinned-commit tree facts.'

    # A valid-shape legacy proof with altered tree facts cannot be blessed by migration, and failure must not overwrite status.
    $legacyFailureStatus = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $migrationStatus -Depth 20 -Compress)
    $legacyFailureProof = @($legacyFailureStatus.records)[0].symlinkResolution
    [void]$legacyFailureProof.PSObject.Properties.Remove('schemaVersion')
    [void]$legacyFailureProof.PSObject.Properties.Remove('proofSha256')
    [void]$legacyFailureProof.PSObject.Properties.Remove('rootTreeSha')
    [void]$legacyFailureProof.linkChain[0].PSObject.Properties.Remove('linkType')
    $legacyFailureProof.linkChain[0].linkParentTreeSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    Write-MarkdownRendererJsonAtomically -Path $linkResume.StatusPath -Value $legacyFailureStatus
    $legacyFailureBefore = [IO.File]::ReadAllBytes($linkResume.StatusPath)
    Initialize-SymlinkFixtureApi -CommitSha $linkRepo.commitSha -LinkBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -TargetBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $targetBytes)
    Assert-Throws {
        Invoke-MarkdownRendererTop500Acquisition `
            -Corpus $linkCorpus `
            -OutputDirectory (Join-Path $tempRoot 'symlink-valid') `
            -MaxConcurrency 1 `
            -RequestOverride $migrationNoBody `
            -ApiRequestOverride (Get-SymlinkApiOverride)
    } 'Legacy proof migration accepted a parent-tree mismatch.'
    $legacyFailureAfter = [IO.File]::ReadAllBytes($linkResume.StatusPath)
    Assert-True ([Convert]::ToBase64String($legacyFailureBefore) -ceq [Convert]::ToBase64String($legacyFailureAfter)) 'Failed legacy proof migration overwrote the existing status artifact.'

    $retryCallCount = 0
    $retryApi = {
        param($uri, $attempt)
        $script:retryCallCount++
        if ($script:retryCallCount -eq 1) { return [pscustomobject]@{ StatusCode = 503; Json = $null; Bytes = $null; Location = $null; RetryAfterSeconds = 0 } }
        if ($uri -ceq "https://api.github.com/repos/fixture/repository1/git/commits/$($script:symlinkCommit)") {
            return [pscustomobject]@{ StatusCode = 200; Json = [pscustomobject]@{ sha = $script:symlinkCommit; tree = [pscustomobject]@{ sha = $script:symlinkRootTree } }; Bytes = $null; Location = $null }
        }
        throw "Unexpected retry API URL '$uri'."
    }
    $retryGet = Invoke-MarkdownRendererBoundedGet -Uri "https://api.github.com/repos/fixture/repository1/git/commits/$($linkRepo.commitSha)" -HttpClient $null -MaxBytes 4096 -AllowedHost 'api.github.com' -RequestOverride $retryApi -MaxAttempts 2 -MaxRetryWaitSeconds 0
    Assert-True ($retryGet.Success -and $retryGet.Attempts -eq 2 -and $retryCallCount -eq 2) 'A transient Git API 5xx response was not retried under the bounded retry policy.'
    Assert-Throws { Invoke-MarkdownRendererBoundedGet -Uri "https://api.github.com:444/repos/fixture/repository1/git/commits/$($linkRepo.commitSha)" -HttpClient $null -MaxBytes 4096 -AllowedHost 'api.github.com' -RequestOverride $retryApi } 'A nonstandard HTTPS port bypassed the GitHub API host allowlist.'
    Assert-Throws { Invoke-MarkdownRendererBoundedGet -Uri "https://api.github.com/repos/fixture/repository1/git/commits/$($linkRepo.commitSha)?redirect=https://evil.example" -HttpClient $null -MaxBytes 4096 -AllowedHost 'api.github.com' -RequestOverride $retryApi } 'A query-bearing GitHub API URL bypassed the pinned request construction.'
    $rateLimitCallCount = 0
    $primaryRateLimit = {
        param($uri, $attempt)
        $script:rateLimitCallCount++
        [pscustomobject]@{ StatusCode = 403; Json = $null; Bytes = $null; Location = $null; RateLimited = $true; RetryAfterSeconds = 120 }
    }
    $deferredGet = Invoke-MarkdownRendererBoundedGet -Uri "https://api.github.com/repos/fixture/repository1/git/commits/$($linkRepo.commitSha)" -HttpClient $null -MaxBytes 4096 -AllowedHost 'api.github.com' -RequestOverride $primaryRateLimit -MaxAttempts 3 -MaxRetryWaitSeconds 0
    Assert-True (-not $deferredGet.Success -and $deferredGet.Reason -ceq 'rate-limit-deferred' -and $deferredGet.Attempts -eq 1 -and $rateLimitCallCount -eq 1) 'A GitHub primary-rate-limit response did not defer within the configured wait budget.'

    Initialize-SymlinkFixtureApi -CommitSha $linkRepo.commitSha -LinkBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -TargetBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $targetBytes)
    $script:symlinkRawTargetBytes = [Text.Encoding]::UTF8.GetBytes('different target body')
    $badTarget = Invoke-MarkdownRendererTop500Acquisition `
        -Corpus $linkCorpus `
        -OutputDirectory (Join-Path $tempRoot 'symlink-hash-mismatch') `
        -MaxConcurrency 1 `
        -MaxAttempts 3 `
        -MaxRetryWaitSeconds 0 `
        -RequestOverride $linkTopFetch `
        -ApiRequestOverride (Get-SymlinkApiOverride) `
        -RawRequestOverride (Get-SymlinkRawOverride)
    $badTargetStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($badTarget.StatusPath, [Text.Encoding]::UTF8))
    Assert-True ($badTarget.UpstreamIntegrityFailures -eq 1 -and @($badTargetStatus.records)[0].attempts -eq 1 -and $script:symlinkRawUris.Count -eq 1) 'A content-hash mismatch was retried or was not kept as an upstream integrity failure.'
    $tamperedStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($linkResume.StatusPath, [Text.Encoding]::UTF8))
    $tamperedStatus.records[0].symlinkResolution.linkChain[0].targetText = '../outside.md'
    Write-MarkdownRendererJsonAtomically -Path $linkResume.StatusPath -Value $tamperedStatus
    Assert-Throws {
        Invoke-MarkdownRendererTop500Acquisition `
            -Corpus $linkCorpus `
            -OutputDirectory (Join-Path $tempRoot 'symlink-valid') `
            -MaxConcurrency 1 `
            -RequestOverride $noSymlinkRefetch
    } 'Tampered symlink provenance was silently accepted or discarded during resume.'

    $secondLinkBytes = [Text.Encoding]::UTF8.GetBytes('../second/README.md')
    Initialize-SymlinkFixtureApi -CommitSha $linkRepo.commitSha -LinkBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -TargetBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $secondLinkBytes)
    $script:symlinkMode = '120000'
    $script:symlinkTargetMode = '120000'
    $script:symlinkTargetType = 'blob'
    $script:symlinkRawTargetBytes = $secondLinkBytes
    $depthRun = Invoke-MarkdownRendererTop500Acquisition `
        -Corpus $linkCorpus `
        -OutputDirectory (Join-Path $tempRoot 'symlink-depth-limit') `
        -MaxConcurrency 1 `
        -MaxSymlinkDepth 1 `
        -RequestOverride $linkTopFetch `
        -ApiRequestOverride (Get-SymlinkApiOverride) `
        -RawRequestOverride (Get-SymlinkRawOverride)
    $depthStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($depthRun.StatusPath, [Text.Encoding]::UTF8))
    Assert-True ($depthRun.UpstreamResolutionFailures -eq 1 -and @($depthStatus.records)[0].message -ceq 'symlink-chain-exceeds-limit') 'A symlink chain beyond the configured bound was not rejected distinctly.'
    Assert-True ($script:symlinkRawUris.Count -eq 1 -and $script:symlinkRawUris[0] -ceq "https://raw.githubusercontent.com/fixture/repository1/$($linkRepo.commitSha)/content/README.md") 'Bounded symlink resolution fetched beyond the configured chain depth.'

    Initialize-SymlinkFixtureApi -CommitSha $linkRepo.commitSha -LinkBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -TargetBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $targetBytes)
    $apiRateLimit = {
        param($uri)
        [pscustomobject]@{ StatusCode = 429; Json = $null; Bytes = $null; Location = $null; RateLimited = $true; RetryAfterSeconds = 120 }
    }
    $deferredSymlink = Invoke-MarkdownRendererTop500Acquisition `
        -Corpus $linkCorpus `
        -OutputDirectory (Join-Path $tempRoot 'symlink-rate-limited') `
        -MaxConcurrency 1 `
        -RequestOverride $linkTopFetch `
        -ApiRequestOverride $apiRateLimit `
        -RawRequestOverride (Get-SymlinkRawOverride)
    $deferredStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($deferredSymlink.StatusPath, [Text.Encoding]::UTF8))
    Assert-True ($deferredSymlink.UpstreamDeferred -eq 1 -and $deferredSymlink.UpstreamResolutionFailures -eq 0 -and @($deferredStatus.records)[0].message -ceq 'git-api-rate-limit-deferred') 'GitHub API rate limiting was mislabeled as a permanent source failure.'
    Assert-True ($script:symlinkRawUris.Count -eq 0) 'The resolver continued target fetching after the API requested rate-limit backoff.'

    $unsafeCases = @(
        [pscustomobject]@{ Name = 'root-escape'; LinkText = '../../../../outside.md'; Mode = '120000'; LinkEntrySha = (Get-MarkdownRendererGitBlobSha1 -Bytes ([Text.Encoding]::UTF8.GetBytes('../../../../outside.md'))); TargetMode = '100644'; TargetType = 'blob'; Redirect = $null; ExpectedFailure = 'symlink-target-escapes-repository' },
        [pscustomobject]@{ Name = 'symlink-loop'; LinkText = 'README.md'; Mode = '120000'; LinkEntrySha = (Get-MarkdownRendererGitBlobSha1 -Bytes ([Text.Encoding]::UTF8.GetBytes('README.md'))); TargetMode = '100644'; TargetType = 'blob'; Redirect = $null; ExpectedFailure = 'symlink-loop' },
        [pscustomobject]@{ Name = 'not-a-symlink'; LinkText = '../content/README.md'; Mode = '100644'; LinkEntrySha = (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes); TargetMode = '100644'; TargetType = 'blob'; Redirect = $null; ExpectedFailure = 'manifest-path-is-not-git-symlink' },
        [pscustomobject]@{ Name = 'link-blob-mismatch'; LinkText = '../content/README.md'; Mode = '120000'; LinkEntrySha = ('0' * 40); TargetMode = '100644'; TargetType = 'blob'; Redirect = $null; ExpectedFailure = 'symlink-blob-proof-mismatch' },
        [pscustomobject]@{ Name = 'submodule'; LinkText = '../content/README.md'; Mode = '120000'; LinkEntrySha = (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes); TargetMode = '160000'; TargetType = 'commit'; Redirect = $null; ExpectedFailure = 'symlink-target-is-submodule' },
        [pscustomobject]@{ Name = 'external-redirect'; LinkText = '../content/README.md'; Mode = '120000'; LinkEntrySha = (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes); TargetMode = '100644'; TargetType = 'blob'; Redirect = 'https://evil.example/README.md'; ExpectedFailure = 'symlink-target-redirect-rejected' }
    )
    foreach ($case in $unsafeCases) {
        $unsafeOutput = Join-Path $tempRoot "symlink-$($case.Name)"
        Initialize-SymlinkFixtureApi -CommitSha $linkRepo.commitSha -LinkBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $linkBytes) -TargetBlobSha (Get-MarkdownRendererGitBlobSha1 -Bytes $targetBytes)
        $script:symlinkMode = $case.Mode
        $script:symlinkLinkEntrySha = $case.LinkEntrySha
        $script:symlinkTargetMode = $case.TargetMode
        $script:symlinkTargetType = $case.TargetType
        $script:symlinkRawTargetBytes = $targetBytes
        $script:symlinkRawRedirect = $case.Redirect
        $caseLinkBytes = [Text.Encoding]::UTF8.GetBytes([string]$case.LinkText)
        $caseFetch = { param($identity, $attempt) [pscustomobject]@{ StatusCode = 200; Bytes = $caseLinkBytes; RetryAfterSeconds = $null; RateLimited = $false; SafeMessage = $null } }
        $caseRun = Invoke-MarkdownRendererTop500Acquisition `
            -Corpus $linkCorpus `
            -OutputDirectory $unsafeOutput `
            -MaxConcurrency 1 `
            -RequestOverride $caseFetch `
            -ApiRequestOverride (Get-SymlinkApiOverride) `
            -RawRequestOverride (Get-SymlinkRawOverride)
        $caseStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($caseRun.StatusPath, [Text.Encoding]::UTF8))
        $caseRecord = @($caseStatus.records)[0]
        Assert-True (-not $caseRun.Complete -and $caseRun.VerifiedCount -eq 0) "Unsafe symlink case '$($case.Name)' was accepted."
        Assert-True ($caseRecord.message -ceq $case.ExpectedFailure -or $caseRecord.classification -ceq 'upstream-content-mismatch') "Unsafe symlink case '$($case.Name)' did not preserve a specific failure classification."
        if ($case.Name -in @('root-escape', 'symlink-loop', 'not-a-symlink', 'link-blob-mismatch', 'submodule')) {
            Assert-True ($script:symlinkRawUris.Count -eq 0) "Unsafe symlink case '$($case.Name)' made a target raw request."
        }
        if ($case.Name -ceq 'external-redirect') {
            Assert-True ($script:symlinkRawUris.Count -eq 1) 'External redirect test did not reach the same-commit raw request boundary.'
        }
    }

    $mismatchOutput = Join-Path $tempRoot 'mismatch'
    $script:fixtureBytesByRank = @{ 1 = $bytesOne; 2 = [Text.Encoding]::UTF8.GetBytes('wrong body') }
    $mismatchRun = Invoke-MarkdownRendererTop500Acquisition -Corpus $fixtureCorpus -OutputDirectory $mismatchOutput -MaxConcurrency 1 -RequestOverride $successfulFetch
    Assert-True ($mismatchRun.VerifiedCount -eq 1 -and $mismatchRun.UpstreamIntegrityFailures -eq 1 -and -not $mismatchRun.Complete) 'A body with the wrong Git blob identity was accepted or misclassified.'
    $status = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($mismatchRun.StatusPath, [Text.Encoding]::UTF8))
    Assert-True (@($status.records | Where-Object { $_.status -ceq 'upstream-integrity-failure' }).Count -eq 1) 'Upstream content-integrity failure was not retained in the status artifact.'

    $failureOutput = Join-Path $tempRoot 'upstream-failure'
    $httpFailure = {
        param($identity, $attempt)
        if ([int]$identity.rank -eq 1) {
            [pscustomobject]@{ StatusCode = 404; Bytes = $null; RetryAfterSeconds = $null; RateLimited = $false; SafeMessage = 'http-error' }
        }
        else { [pscustomobject]@{ StatusCode = 200; Bytes = $bytesTwo; RetryAfterSeconds = $null; RateLimited = $false; SafeMessage = $null } }
    }
    $httpRun = Invoke-MarkdownRendererTop500Acquisition -Corpus $fixtureCorpus -OutputDirectory $failureOutput -MaxConcurrency 1 -RequestOverride $httpFailure
    Assert-True ($httpRun.VerifiedCount -eq 1 -and $httpRun.UpstreamFailures -eq 1 -and $httpRun.UpstreamIntegrityFailures -eq 0) 'An HTTP resource failure was not separated from content-integrity failures.'

    $rateLimitOutput = Join-Path $tempRoot 'rate-limit'
    $script:fetchCalls = 0
    $rateLimit = {
        param($identity, $attempt)
        $script:fetchCalls++
        [pscustomobject]@{ StatusCode = 429; Bytes = $null; RetryAfterSeconds = 120; RateLimited = $true; SafeMessage = 'rate-limited' }
    }
    $rateRun = Invoke-MarkdownRendererTop500Acquisition -Corpus $fixtureCorpus -OutputDirectory $rateLimitOutput -MaxConcurrency 1 -MaxRetryWaitSeconds 0 -RequestOverride $rateLimit
    Assert-True ($rateRun.UpstreamDeferred -eq 2 -and $rateRun.UpstreamFailures -eq 0 -and $script:fetchCalls -eq 1) 'Rate limiting was retried before Retry-After or mislabeled as an upstream resource failure.'

    $corruptPath = Get-MarkdownRendererGitBlobObjectPath -ObjectDirectory $firstRun.ObjectDirectory -GitBlobSha1 $fixtureCorpus.Identities[0].expectedGitBlobSha1
    [IO.File]::WriteAllBytes($corruptPath, [Text.Encoding]::UTF8.GetBytes('tampered local data'))
    $corruptBytesBefore = [IO.File]::ReadAllBytes($corruptPath)
    $script:fetchCalls = 0
    $corruptRun = Invoke-MarkdownRendererTop500Acquisition -Corpus $fixtureCorpus -OutputDirectory $output -MaxConcurrency 1 -RequestOverride $noFetch
    $corruptBytesAfter = [IO.File]::ReadAllBytes($corruptPath)
    Assert-True ($corruptRun.LocalIntegrityFailures -eq 1 -and $script:fetchCalls -eq 0) 'A corrupt local content-addressed object was silently refetched or accepted.'
    Assert-True ([Convert]::ToBase64String($corruptBytesBefore) -ceq [Convert]::ToBase64String($corruptBytesAfter)) 'Corrupt cached bytes were overwritten during safe resume.'

    $junctionCheck = 'UNAVAILABLE'
    $junctionOutput = Join-Path $tempRoot 'junction-output'
    $junctionTarget = Join-Path $tempRoot 'junction-target'
    [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    try {
        New-Item -ItemType Junction -Path $junctionOutput -Target $junctionTarget -ErrorAction Stop | Out-Null
        $junctionCheck = 'PASS'
        Assert-Throws { Invoke-MarkdownRendererTop500Acquisition -Corpus $fixtureCorpus -OutputDirectory $junctionOutput -RequestOverride $noFetch } 'A reparse-point output root was followed.'
        Assert-Throws { Write-MarkdownRendererJsonAtomically -Path (Join-Path $junctionOutput 'source-status.json') -Value ([pscustomobject]@{ test = $true }) } 'An atomic status write followed a junction parent.'
        $junctionObject = Test-MarkdownRendererGitBlobObject -Path (Join-Path $junctionOutput 'object.md') -ExpectedByteSize 1 -ExpectedGitBlobSha1 (Get-MarkdownRendererGitBlobSha1 -Bytes ([byte[]]@(65)))
        Assert-True ($junctionObject.Exists -and -not $junctionObject.Valid -and $junctionObject.Reason -ceq 'reparse-point-path-rejected') 'An object-cache read followed a junction parent.'
        Assert-True (-not [IO.File]::Exists((Join-Path $junctionTarget 'source-status.json')) -and -not [IO.File]::Exists((Join-Path $junctionTarget 'source-acquisition.json'))) 'A rejected reparse-point output root was written through to its target.'
        Assert-Throws { Read-MarkdownRendererFileBytesBounded -Path $junctionOutput -MaxBytes 1024 } 'A bounded read followed a junction path.'
        Assert-Throws { Read-MarkdownRendererFileBytesBounded -Path (Join-Path $junctionOutput 'source-status.json') -MaxBytes 1024 } 'A bounded status read followed a junction parent.'
        [IO.Directory]::Delete($junctionOutput, $false)
    }
    catch {
        if ($junctionCheck -ceq 'PASS') { throw }
        $junctionCheck = 'UNAVAILABLE'
    }

    [pscustomobject]@{
        Result = 'PASS'
        Assertions = $script:assertions
        PinnedManifestEntries = $pinned.Identities.Count
        PinnedManifestSha256 = $pinned.Sha256
        JunctionReparseCheck = $junctionCheck
        TemporaryArtifactsRemoved = $true
    } | ConvertTo-Json -Compress
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($tempRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedRoot) -notlike 'markdownrenderer-top500-source-tests-*') {
        throw 'Refusing to remove a test temporary directory outside the uniquely owned temp root.'
    }
    if ([IO.Directory]::Exists($resolvedRoot)) { [IO.Directory]::Delete($resolvedRoot, $true) }
}
