[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$netHttpAssembly = [Reflection.Assembly]::LoadWithPartialName('System.Net.Http')
$handlerSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public static class MarkdownRendererHtmlResponseFactory
{
    public static HttpResponseMessage CreateResponse(int statusCode) { return new HttpResponseMessage((System.Net.HttpStatusCode)statusCode); }
}

public sealed class MarkdownRendererHtmlQueueHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses = new Queue<HttpResponseMessage>();
    public int RequestCount { get; private set; }
    public bool SawAuthorization { get; private set; }
    public bool SawCookie { get; private set; }
    public string LastRequestUrl { get; private set; }
    public int DelayMilliseconds { get; set; }
    public string FinalRequestUriOverride { get; set; }
    public void Enqueue(HttpResponseMessage response) { responses.Enqueue(response); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastRequestUrl = request.RequestUri.AbsoluteUri;
        SawAuthorization = SawAuthorization || request.Headers.Authorization != null;
        SawCookie = SawCookie || request.Headers.Contains("Cookie");
        if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellationToken).ConfigureAwait(false);
        else if (DelayMilliseconds < 0) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        if (responses.Count == 0) throw new InvalidOperationException("No synthetic response was queued.");
        HttpResponseMessage response = responses.Dequeue();
        response.RequestMessage = request;
        if (!String.IsNullOrEmpty(FinalRequestUriOverride)) request.RequestUri = new Uri(FinalRequestUriOverride);
        return response;
    }
}

public sealed class MarkdownRendererHtmlNonSeekableStream : Stream
{
    private readonly byte[] bytes;
    private int offset;
    public int BytesRead { get; private set; }
    public MarkdownRendererHtmlNonSeekableStream(byte[] value) { bytes = value; }
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
    public override int Read(byte[] buffer, int bufferOffset, int count)
    {
        int amount = Math.Min(count, bytes.Length - offset);
        if (amount <= 0) return 0;
        Array.Copy(bytes, offset, buffer, bufferOffset, amount);
        offset += amount;
        BytesRead += amount;
        return amount;
    }
    public override Task<int> ReadAsync(byte[] buffer, int bufferOffset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, bufferOffset, count));
    }
    public override void Flush() { }
    public override long Seek(long value, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int bufferOffset, int count) { throw new NotSupportedException(); }
}
'@
if ($PSVersionTable.PSVersion.Major -lt 6) {
    if ($null -eq $netHttpAssembly) { throw 'The Windows PowerShell test host could not load System.Net.Http.' }
    $references = @(
        [object].Assembly.Location,
        [Reflection.Assembly]::LoadWithPartialName('System').Location,
        [Reflection.Assembly]::LoadWithPartialName('System.Core').Location,
        $netHttpAssembly.Location)
    Add-Type -ReferencedAssemblies $references -TypeDefinition $handlerSource
}
else {
    Add-Type -TypeDefinition $handlerSource
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$scriptPath = Join-Path $repoRoot 'MarkdownRenderer/eng/Invoke-MarkdownRendererCorrectnessHtmlAcquisition.ps1'
$corpusPath = Join-Path $repoRoot 'docs/markdown-renderer/evidence/2026-10-17/top-repositories.json'
$sourceRoot = Join-Path $repoRoot 'artifacts/readme-top500/source-acquisition-615c56723fb1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('markdownrenderer-html-response-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
. $scriptPath -CorpusManifestPath $corpusPath -SourceRoot $sourceRoot -OutputDirectory (Join-Path $testRoot 'dot-source-only')

$script:CaseCount = 0
function Assert-True {
    param([Parameter(Mandatory)][bool] $Value, [Parameter(Mandatory)][string] $Message)
    if (-not $Value) { throw "ASSERT TRUE FAILED: $Message" }
    $script:CaseCount++
}
function Assert-Equal {
    param([AllowNull()][object] $Actual, [AllowNull()][object] $Expected, [Parameter(Mandatory)][string] $Message)
    if ([string]$Actual -cne [string]$Expected) { throw "ASSERT EQUAL FAILED: $Message; expected '$Expected', got '$Actual'." }
    $script:CaseCount++
}
function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock] $Action, [Parameter(Mandatory)][string] $Message)
    $thrown = $false
    try { & $Action } catch { $thrown = $true }
    if (-not $thrown) { throw "ASSERT THROWS FAILED: $Message" }
    $script:CaseCount++
}
function New-TestClient {
    param([Parameter(Mandatory)][MarkdownRendererHtmlQueueHandler] $Handler)
    $client = [Net.Http.HttpClient]::new($Handler, $false)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    return $client
}
function New-TestResponse {
    param([int] $StatusCode = 200, [byte[]] $Bytes = [Text.Encoding]::UTF8.GetBytes('<html>sample</html>'), [string] $ContentType = 'text/html')
    $response = [MarkdownRendererHtmlResponseFactory]::CreateResponse($StatusCode)
    $response.Content = [Net.Http.ByteArrayContent]::new($Bytes)
    if (-not [string]::IsNullOrWhiteSpace($ContentType)) { $response.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::Parse($ContentType) }
    return $response
}
function Get-TestIdentities {
    return (Get-MarkdownRendererCorrectnessHtmlFrozenSources -CorpusManifestPath $corpusPath -SourceRoot $sourceRoot).Records
}
function Invoke-TestCore {
    param(
        [Parameter(Mandatory)][object[]] $Identities,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [Parameter(Mandatory)][MarkdownRendererHtmlQueueHandler] $Handler,
        [Parameter(Mandatory)][int] $Count,
        [int] $StartRank = 1,
        [int] $MaxBytes = 1048576,
        [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None,
        [int] $TimeoutSeconds = 30
    )
    $client = New-TestClient -Handler $Handler
    try {
        return Invoke-MarkdownRendererCorrectnessHtmlAcquisitionCore -Identities $Identities -SourceRoot $sourceRoot -OutputDirectory $OutputDirectory -HttpClient $client -StartRank $StartRank -Count $Count -MaxBytes $MaxBytes -TimeoutSeconds $TimeoutSeconds -InterRequestDelayMilliseconds 0 -CancellationToken $CancellationToken
    }
    finally { $client.Dispose() }
}
function Get-TestCasePaths {
    param([string] $OutputDirectory, [int] $Rank)
    return Get-MarkdownRendererCorrectnessHtmlCasePaths -OutputDirectory $OutputDirectory -Rank $Rank
}

try {
    $identities = @(Get-TestIdentities)
    Assert-Equal $identities.Count 500 'real frozen identity manifest retains all 500 ranks'
    Assert-Equal ([IO.Path]::GetFileName($scriptPath)) 'Invoke-MarkdownRendererCorrectnessHtmlAcquisition.ps1' 'dot-sourcing the helper does not run its acquisition entry point'

    # Exercise the true script entry point with caller-supplied paths. A source-manifest
    # error is expected; null/reset caller parameters would instead fail at binding.
    $entryOutput = Join-Path $testRoot 'entry-point'
    $entryError = $null
    try { & $scriptPath -CorpusManifestPath $corpusPath -SourceRoot (Join-Path $testRoot 'missing-source-root') -OutputDirectory $entryOutput -StartRank 1 -Count 1 -InterRequestDelayMilliseconds 0 | Out-Null }
    catch { $entryError = $_.Exception.Message }
    Assert-True ($entryError -match 'frozen-source-acquisition-manifest-missing') 'real entry point preserves its caller arguments through helper import'

    $mismatchedSourceRoot = Join-Path $testRoot 'mismatched-source-manifest'
    [IO.Directory]::CreateDirectory($mismatchedSourceRoot) | Out-Null
    $sourceManifest = Read-MarkdownRendererCorrectnessHtmlJson -Path (Join-Path $sourceRoot 'source-acquisition.json') -MaxBytes 8388608
    $sourceManifest.records[0].commitSha = '0000000000000000000000000000000000000000'
    $sourceManifestText = (ConvertTo-Json -InputObject $sourceManifest -Depth 20) + "`n"
    [IO.File]::WriteAllText((Join-Path $mismatchedSourceRoot 'source-acquisition.json'), $sourceManifestText, [Text.UTF8Encoding]::new($false))
    Assert-Throws { Get-MarkdownRendererCorrectnessHtmlFrozenSources -CorpusManifestPath $corpusPath -SourceRoot $mismatchedSourceRoot } 'mismatched source-record identity is rejected before constructing requests'

    $successOutput = Join-Path $testRoot 'success'
    $successHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $successHandler.Enqueue((New-TestResponse -Bytes ([Text.Encoding]::UTF8.GetBytes('<html>rank one</html>'))))
    $successHandler.Enqueue((New-TestResponse -Bytes ([Text.Encoding]::UTF8.GetBytes('<html>rank two</html>'))))
    $successStatus = Invoke-TestCore -Identities $identities -OutputDirectory $successOutput -Handler $successHandler -Count 2
    Assert-Equal $successHandler.RequestCount 2 'one actual HttpClient send per requested case'
    Assert-Equal $successStatus.responseCapturedRecordCount 2 'both complete response bodies captured'
    Assert-Equal $successStatus.contentObjectHashReadCount 2 'per-request status refresh does not rehash prior response bodies'
    Assert-Equal $successStatus.contentAddressedObjectsVerifiedThisRun 2 'only the two new content objects are verified in the partial run'
    Assert-Equal $successStatus.fullIntegrityVerification 'not-run' 'partial response batches cannot claim full-snapshot integrity'
    Assert-True (-not $successStatus.all500ResponseRecordsCaptured -and -not $successStatus.all500ResponsesCaptured) 'partial responses cannot claim all 500 responses'
    Assert-True (-not $successHandler.SawAuthorization -and -not $successHandler.SawCookie) 'request contains no credentials or cookies'
    Assert-True (-not $successStatus.auditInputReady -and -not $successStatus.captureReady -and -not $successStatus.browserRendered) 'response-only results cannot claim audit or browser capture readiness'
    Assert-Equal $successStatus.phase3Status 'not-evaluated' 'response acquisition does not evaluate Phase 3'
    $index = Read-MarkdownRendererCorrectnessHtmlJson -Path (Join-Path $successOutput 'snapshot-index.json')
    Assert-Equal $index.contract 'github-commit-page-response-only-v1' 'snapshot uses its distinct response-only contract'
    Assert-Equal @($index.identities).Count 500 'immutable index preserves all frozen identities, not just the requested subset'
    Assert-True (@($index.explicitNonClaims) -contains 'not-capture-ready') 'index states that HTML is not a browser capture'
    $firstAttemptPath = Get-TestCasePaths -OutputDirectory $successOutput -Rank 1
    $firstAttempt = Read-MarkdownRendererCorrectnessHtmlJson -Path $firstAttemptPath.Result
    Assert-True ($firstAttempt.sourceVerifiedBeforeRequest -and $firstAttempt.sourceGitBlobSha1 -ceq $identities[0].expectedGitBlobSha1) 'raw frozen Git blob proof is recorded before the HTTP response'
    Assert-Equal $firstAttempt.httpStatus 200 'HTTP status is saved with the response'
    Assert-True ($firstAttempt.requestUrl -ceq $identities[0].htmlUrl -and $firstAttempt.finalUrl -ceq $identities[0].htmlUrl) 'request and final URL are exact frozen GitHub page URL'
    Assert-True ($null -ne (Read-MarkdownRendererCorrectnessHtmlVerifiedObject -OutputDirectory $successOutput -Attempt $firstAttempt -Identity $identities[0])) 'stored raw bytes validate by content address and SHA-256'

    $resumeHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $resumeStatus = Invoke-TestCore -Identities $identities -OutputDirectory $successOutput -Handler $resumeHandler -Count 2
    Assert-Equal $resumeHandler.RequestCount 0 'verified response objects resume without a network request'
    Assert-Equal $resumeStatus.responseCapturedRecordCount 2 'verified responses remain captured on resume'
    Assert-Equal $resumeStatus.contentObjectHashReadCount 2 'resume validation hashes each distinct cached object once'

    # Exercise the final all-500 integrity gate without network traffic. The synthetic
    # records share one content-addressed body, which must be hashed exactly once.
    $fullOutput = Join-Path $testRoot 'full-integrity'
    [IO.Directory]::CreateDirectory($fullOutput) | Out-Null
    $fullFrozen = [pscustomobject]@{ Corpus = [pscustomobject]@{ Sha256 = $script:MarkdownRendererCorrectnessHtmlCorpusSha256 }; Records = $identities }
    $fullIndex = New-MarkdownRendererCorrectnessHtmlSnapshotIndex -FrozenSources $fullFrozen
    Write-MarkdownRendererCorrectnessHtmlImmutableJson -Path (Join-Path $fullOutput 'snapshot-index.json') -Value $fullIndex
    $fullBytes = [Text.Encoding]::UTF8.GetBytes('<html>shared synthetic body</html>')
    $script:MarkdownRendererCorrectnessHtmlObjectReadCount = 0
    $fullStored = Write-MarkdownRendererCorrectnessHtmlObject -OutputDirectory $fullOutput -Bytes $fullBytes -ExpectedSha256 (Get-MarkdownRendererSha256 -Bytes $fullBytes)
    foreach ($identity in $identities) {
        $casePaths = Get-MarkdownRendererCorrectnessHtmlCasePaths -OutputDirectory $fullOutput -Rank ([int]$identity.rank)
        [IO.Directory]::CreateDirectory($casePaths.Directory) | Out-Null
        $attempt = New-MarkdownRendererCorrectnessHtmlAttemptBase -Kind 'terminal-result' -Identity $identity -StartedAtUtc ([DateTimeOffset]::UtcNow.ToString('o'))
        $attempt.outcome = 'response-captured'
        $attempt.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $attempt.requestUrl = [string]$identity.htmlUrl
        $attempt.finalUrl = [string]$identity.htmlUrl
        $attempt.httpStatus = 200
        $attempt.contentType = 'text/html'
        $attempt.responseAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $attempt.rateLimited = $false
        $attempt.sourceByteSize = [long]$identity.expectedByteSize
        $attempt.sourceGitBlobSha1 = [string]$identity.expectedGitBlobSha1
        $attempt.sourceVerifiedBeforeRequest = $true
        $attempt.htmlSha256 = [string](Get-MarkdownRendererSha256 -Bytes $fullBytes)
        $attempt.htmlByteSize = [long]$fullBytes.Length
        $attempt.htmlObjectPath = [string]$fullStored.RelativePath
        $attempt.htmlObjectVerified = $true
        Write-MarkdownRendererCorrectnessHtmlImmutableJson -Path $casePaths.Result -Value $attempt
    }
    $fullOutcomes = Get-MarkdownRendererCorrectnessHtmlCaseOutcomeMap -OutputDirectory $fullOutput -Identities $identities
    $fullVerifiedHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $script:MarkdownRendererCorrectnessHtmlObjectReadCount = 0
    $fullIntegrity = Complete-MarkdownRendererCorrectnessHtmlSnapshotIntegrity -OutputDirectory $fullOutput -Identities $identities -CaseOutcomes $fullOutcomes -VerifiedObjectHashes $fullVerifiedHashes
    Assert-Equal $fullIntegrity.Status 'passed' 'all 500 valid response bodies pass the final integrity gate'
    Assert-Equal $fullIntegrity.VerifiedResponseCount 500 'full-integrity pass reports all 500 responses only after verification'
    Assert-Equal $script:MarkdownRendererCorrectnessHtmlObjectReadCount 1 'full integrity hashes a shared content-addressed object exactly once'
    $fullStatus = Get-MarkdownRendererCorrectnessHtmlRunStatus -CaseOutcomes $fullOutcomes -RunStartedAtUtc ([DateTimeOffset]::UtcNow.ToString('o')) -RunStopReason $null -ResponseRecordCount 500 -ContentObjectHashReadCount $script:MarkdownRendererCorrectnessHtmlObjectReadCount -ContentAddressedObjectsVerifiedThisRun $fullVerifiedHashes.Count -FullIntegrityVerification $fullIntegrity.Status
    Assert-True ($fullStatus.all500ResponseRecordsCaptured -and $fullStatus.all500ResponsesCaptured) 'all-500 completion is reported only after the full integrity pass'

    [IO.File]::WriteAllBytes($fullStored.AbsolutePath, [Text.Encoding]::UTF8.GetBytes('<tampered>'))
    $tamperedFullOutcomes = Get-MarkdownRendererCorrectnessHtmlCaseOutcomeMap -OutputDirectory $fullOutput -Identities $identities
    $tamperedVerifiedHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $script:MarkdownRendererCorrectnessHtmlObjectReadCount = 0
    $tamperedFullIntegrity = Complete-MarkdownRendererCorrectnessHtmlSnapshotIntegrity -OutputDirectory $fullOutput -Identities $identities -CaseOutcomes $tamperedFullOutcomes -VerifiedObjectHashes $tamperedVerifiedHashes
    Assert-Equal $tamperedFullIntegrity.Status 'failed' 'final integrity gate catches content tampered after acquisition'
    Assert-Equal $script:MarkdownRendererCorrectnessHtmlObjectReadCount 1 'a tampered shared body is checked once, not once per rank'
    $tamperedFullStatus = Get-MarkdownRendererCorrectnessHtmlRunStatus -CaseOutcomes $tamperedFullOutcomes -RunStartedAtUtc ([DateTimeOffset]::UtcNow.ToString('o')) -RunStopReason $null -ResponseRecordCount 500 -ContentObjectHashReadCount $script:MarkdownRendererCorrectnessHtmlObjectReadCount -ContentAddressedObjectsVerifiedThisRun $tamperedVerifiedHashes.Count -FullIntegrityVerification $tamperedFullIntegrity.Status
    Assert-True ($tamperedFullStatus.all500ResponseRecordsCaptured -and -not $tamperedFullStatus.all500ResponsesCaptured -and $tamperedFullStatus.verifiedResponseCount -eq 0) 'response metadata cannot claim all-500 integrity after a final body hash failure'

    # A corrupted body is never replaced or refetched on resume.
    $objectPath = Get-MarkdownRendererCorrectnessHtmlObjectPath -Root $successOutput -Sha256 $firstAttempt.htmlSha256
    [IO.File]::WriteAllBytes($objectPath, [byte[]]@(1, 2, 3))
    $corruptHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $corruptStatus = Invoke-TestCore -Identities $identities -OutputDirectory $successOutput -Handler $corruptHandler -Count 2
    Assert-Equal $corruptHandler.RequestCount 0 'corrupt content-addressed response is not silently fetched again'
    Assert-True ($corruptStatus.localIntegrityFailureCount -gt 0) 'corrupt cached response is classified as local integrity failure'
    Assert-Equal ([IO.File]::ReadAllBytes($objectPath).Length) 3 'corrupt cache is preserved for diagnosis and not overwritten'

    # Missing raw source: no request can be issued until source identity is proven.
    $missingIdentity = $identities[0] | Select-Object *
    $missingIdentity.expectedGitBlobSha1 = '0000000000000000000000000000000000000000'
    $missingIdentity.expectedByteSize = 1
    $missingIdentity.sourceObjectPath = 'objects/git-blob-sha1/00/0000000000000000000000000000000000000000.md'
    $missingIdentities = @($identities)
    $missingIdentities[0] = $missingIdentity
    $missingHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $missingStatus = Invoke-TestCore -Identities $missingIdentities -OutputDirectory (Join-Path $testRoot 'missing-source') -Handler $missingHandler -Count 1
    Assert-Equal $missingHandler.RequestCount 0 'missing source bytes are rejected before HTTP'
    $missingResult = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory (Join-Path $testRoot 'missing-source') -Rank 1).Result)
    Assert-Equal $missingResult.outcome 'frozen-source-object-invalid' 'missing source has explicit upstream-independent status'

    # Mismatched raw bytes at the expected object path are also rejected before HTTP.
    $wrongSourceRoot = Join-Path $testRoot 'wrong-source'
    $wrongPath = Get-MarkdownRendererGitBlobObjectPath -ObjectDirectory (Join-Path $wrongSourceRoot 'objects/git-blob-sha1') -GitBlobSha1 ([string]$identities[0].expectedGitBlobSha1)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($wrongPath)) | Out-Null
    [IO.File]::WriteAllBytes($wrongPath, [Text.Encoding]::UTF8.GetBytes('not the frozen readme'))
    $wrongHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $wrongClient = New-TestClient -Handler $wrongHandler
    try { $wrongStatus = Invoke-MarkdownRendererCorrectnessHtmlAcquisitionCore -Identities $identities -SourceRoot $wrongSourceRoot -OutputDirectory (Join-Path $testRoot 'wrong-source-result') -HttpClient $wrongClient -StartRank 1 -Count 1 -MaxBytes 1048576 -TimeoutSeconds 30 -InterRequestDelayMilliseconds 0 }
    finally { $wrongClient.Dispose() }
    Assert-Equal $wrongHandler.RequestCount 0 'mismatched raw bytes never reach the network'
    $wrongResult = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory (Join-Path $testRoot 'wrong-source-result') -Rank 1).Result)
    Assert-Equal $wrongResult.outcome 'frozen-source-object-invalid' 'mismatched raw body has explicit source-integrity status'

    # The real HttpClient send path rejects redirects and records their status/location.
    $redirectOutput = Join-Path $testRoot 'redirect'
    $redirectHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $redirect = New-TestResponse -StatusCode 302 -Bytes ([byte[]]@()) -ContentType ''
    $redirect.Headers.Location = [Uri]'https://evil.example/other'
    $redirectHandler.Enqueue($redirect)
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $redirectOutput -Handler $redirectHandler -Count 1)
    $redirectResult = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $redirectOutput -Rank 1).Result)
    Assert-Equal $redirectHandler.RequestCount 1 'redirect is rejected after one HttpClient request with no automatic follow'
    Assert-Equal $redirectResult.outcome 'redirect-rejected' 'redirect outcome is distinct'
    Assert-Equal $redirectResult.httpStatus 302 'redirect HTTP status remains visible'
    Assert-Equal $redirectResult.locationHeader 'https://evil.example/other' 'untrusted redirect target is recorded but never requested'

    $unexpectedFinalOutput = Join-Path $testRoot 'unexpected-final-url'
    $unexpectedFinalHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $unexpectedFinalHandler.FinalRequestUriOverride = 'https://evil.example/repo/README.md'
    $unexpectedFinalHandler.Enqueue((New-TestResponse))
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $unexpectedFinalOutput -Handler $unexpectedFinalHandler -Count 1)
    $unexpectedFinal = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $unexpectedFinalOutput -Rank 1).Result)
    Assert-Equal $unexpectedFinal.outcome 'unexpected-final-url' 'HTTP 200 from an unexpected effective URL is rejected'
    Assert-Equal $unexpectedFinalHandler.RequestCount 1 'unexpected final URL is never followed with another request'
    Assert-Equal $unexpectedFinal.requestUrl $identities[0].htmlUrl 'unexpected final URL retains its frozen request URL'
    Assert-Equal $unexpectedFinal.finalUrl 'https://evil.example/repo/README.md' 'unexpected effective URL is recorded for diagnosis'

    $httpFailureOutput = Join-Path $testRoot 'http-failure'
    $httpFailureHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $httpFailureHandler.Enqueue((New-TestResponse -StatusCode 404 -Bytes ([byte[]]@()) -ContentType 'text/html'))
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $httpFailureOutput -Handler $httpFailureHandler -Count 1)
    $httpFailure = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $httpFailureOutput -Rank 1).Result)
    Assert-Equal $httpFailure.outcome 'upstream-http-failure' 'ordinary upstream HTTP failure is distinct from transport failure'
    Assert-Equal $httpFailure.httpStatus 404 'HTTP failure status is retained'
    Assert-Equal $httpFailureHandler.RequestCount 1 'HTTP failure is not retried'

    $networkFailureOutput = Join-Path $testRoot 'network-failure'
    $networkFailureHandler = [MarkdownRendererHtmlQueueHandler]::new()
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $networkFailureOutput -Handler $networkFailureHandler -Count 1)
    $networkFailure = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $networkFailureOutput -Rank 1).Result)
    Assert-Equal $networkFailure.outcome 'upstream-network-failure' 'transport failure is distinct from HTTP status failure'
    Assert-Equal $networkFailureHandler.RequestCount 1 'transport failure is not retried'

    $rateLimitOutput = Join-Path $testRoot 'rate-limit'
    $rateLimitHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $rateLimitHandler.Enqueue((New-TestResponse -StatusCode 429 -Bytes ([byte[]]@()) -ContentType 'text/html'))
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $rateLimitOutput -Handler $rateLimitHandler -Count 2)
    $rateLimitResult = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $rateLimitOutput -Rank 1).Result)
    Assert-Equal $rateLimitResult.outcome 'upstream-rate-limit' 'HTTP 429 is explicitly separated from other failures'
    Assert-Equal $rateLimitHandler.RequestCount 1 'rate limit stops the sequential batch without retrying or dispatching rank two'
    Assert-True (-not [IO.File]::Exists((Get-TestCasePaths -OutputDirectory $rateLimitOutput -Rank 2).Start)) 'unissued work remains pending instead of being fabricated as attempted'

    $wrongContentOutput = Join-Path $testRoot 'wrong-content-type'
    $wrongContentHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $wrongContentHandler.Enqueue((New-TestResponse -ContentType 'application/octet-stream'))
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $wrongContentOutput -Handler $wrongContentHandler -Count 1)
    $wrongContent = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $wrongContentOutput -Rank 1).Result)
    Assert-Equal $wrongContent.outcome 'unexpected-content-type' 'non-HTML response is not accepted as page HTML'

    $declaredOversizeOutput = Join-Path $testRoot 'declared-oversize'
    $declaredOversizeHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $declaredOversize = New-TestResponse -Bytes ([byte[]]@(1, 2, 3, 4, 5, 6, 7, 8, 9))
    $declaredOversize.Content.Headers.ContentLength = 9
    $declaredOversizeHandler.Enqueue($declaredOversize)
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $declaredOversizeOutput -Handler $declaredOversizeHandler -Count 1 -MaxBytes 8)
    $declaredResult = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $declaredOversizeOutput -Rank 1).Result)
    Assert-Equal $declaredResult.outcome 'response-over-budget' 'declared over-budget body is classified without buffering'
    Assert-Equal $declaredResult.declaredByteSize 9 'declared oversize length is recorded'

    $streamOutput = Join-Path $testRoot 'stream-overrun'
    $streamHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $oversizeStream = [MarkdownRendererHtmlNonSeekableStream]::new([byte[]]@(1, 2, 3, 4, 5, 6, 7, 8, 9))
    $streamResponse = [MarkdownRendererHtmlResponseFactory]::CreateResponse(200)
    $streamResponse.Content = [Net.Http.StreamContent]::new($oversizeStream)
    $streamResponse.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/html')
    $streamHandler.Enqueue($streamResponse)
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $streamOutput -Handler $streamHandler -Count 1 -MaxBytes 8)
    $streamResult = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $streamOutput -Rank 1).Result)
    Assert-Equal $streamResult.outcome 'response-over-budget' 'unknown-length chunked body overrun is classified'
    Assert-Equal $oversizeStream.BytesRead 9 'bounded stream reads no more than cap plus one byte'
    Assert-Equal $streamHandler.RequestCount 1 'stream overrun is never retried'

    $cancelOutput = Join-Path $testRoot 'cancelled'
    $cancelHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $cancelHandler.DelayMilliseconds = -1
    $cancelClient = New-TestClient -Handler $cancelHandler
    $cancelSource = [Threading.CancellationTokenSource]::new()
    $cancelSource.CancelAfter(1000)
    try { $cancelStatus = Invoke-MarkdownRendererCorrectnessHtmlAcquisitionCore -Identities $identities -SourceRoot $sourceRoot -OutputDirectory $cancelOutput -HttpClient $cancelClient -StartRank 1 -Count 1 -MaxBytes 1048576 -TimeoutSeconds 30 -InterRequestDelayMilliseconds 0 -CancellationToken $cancelSource.Token }
    finally { $cancelClient.Dispose(); $cancelSource.Dispose() }
    $cancelResult = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $cancelOutput -Rank 1).Result)
    Assert-Equal $cancelHandler.RequestCount 1 'active cancellation sends one request and does not retry'
    Assert-Equal $cancelResult.outcome 'cancelled' 'caller cancellation is explicit'

    $timeoutOutput = Join-Path $testRoot 'timeout'
    $timeoutHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $timeoutHandler.DelayMilliseconds = -1
    $timeoutClient = New-TestClient -Handler $timeoutHandler
    try { $timeoutStatus = Invoke-MarkdownRendererCorrectnessHtmlAcquisitionCore -Identities $identities -SourceRoot $sourceRoot -OutputDirectory $timeoutOutput -HttpClient $timeoutClient -StartRank 1 -Count 1 -MaxBytes 1048576 -TimeoutSeconds 5 -InterRequestDelayMilliseconds 0 }
    finally { $timeoutClient.Dispose() }
    $timeoutResult = Read-MarkdownRendererCorrectnessHtmlJson -Path ((Get-TestCasePaths -OutputDirectory $timeoutOutput -Rank 1).Result)
    Assert-Equal $timeoutHandler.RequestCount 1 'timeout is one request per case, without retry'
    Assert-Equal $timeoutResult.outcome 'request-timeout' 'request timeout is distinct from caller cancellation'

    $tamperedIndexOutput = Join-Path $testRoot 'tampered-index'
    $tamperedIndexHandler = [MarkdownRendererHtmlQueueHandler]::new()
    [void](Invoke-TestCore -Identities $identities -OutputDirectory $tamperedIndexOutput -Handler $tamperedIndexHandler -Count 1)
    $indexPath = Join-Path $tamperedIndexOutput 'snapshot-index.json'
    $tamperedIndex = Read-MarkdownRendererCorrectnessHtmlJson -Path $indexPath
    $tamperedIndex.frozenReadmeCorpusSha256 = ('0' * 64)
    [IO.File]::WriteAllText($indexPath, (ConvertTo-Json -InputObject $tamperedIndex -Depth 20), [Text.UTF8Encoding]::new($false))
    $tamperResumeHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $tamperClient = New-TestClient -Handler $tamperResumeHandler
    try { Assert-Throws { Invoke-MarkdownRendererCorrectnessHtmlAcquisitionCore -Identities $identities -SourceRoot $sourceRoot -OutputDirectory $tamperedIndexOutput -HttpClient $tamperClient -StartRank 1 -Count 1 -MaxBytes 1048576 -TimeoutSeconds 30 -InterRequestDelayMilliseconds 0 } 'modified frozen snapshot index is rejected' }
    finally { $tamperClient.Dispose() }
    Assert-Equal $tamperResumeHandler.RequestCount 0 'tampered manifest does not initiate a request'

    $orphanOutput = Join-Path $testRoot 'orphaned-output'
    [IO.Directory]::CreateDirectory((Join-Path $orphanOutput 'cases')) | Out-Null
    $orphanHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $orphanClient = New-TestClient -Handler $orphanHandler
    try { Assert-Throws { Invoke-MarkdownRendererCorrectnessHtmlAcquisitionCore -Identities $identities -SourceRoot $sourceRoot -OutputDirectory $orphanOutput -HttpClient $orphanClient -StartRank 1 -Count 1 -MaxBytes 1048576 -TimeoutSeconds 30 -InterRequestDelayMilliseconds 0 } 'unindexed existing snapshot directory is not reinterpreted' }
    finally { $orphanClient.Dispose() }
    Assert-Equal $orphanHandler.RequestCount 0 'orphaned legacy files cannot trigger network requests under a new index'

    # Unsafe caller URL is rejected before SendAsync.
    $badUrlIdentity = $identities[0] | Select-Object *
    $badUrlIdentity.htmlUrl = 'https://github.com.evil.example/repo/blob/commit/README.md'
    $urlHandler = [MarkdownRendererHtmlQueueHandler]::new()
    $urlClient = New-TestClient -Handler $urlHandler
    try { Assert-Throws { Invoke-MarkdownRendererCorrectnessHtmlGet -HttpClient $urlClient -Identity $badUrlIdentity -MaxBytes 1048576 -TimeoutSeconds 30 -CancellationToken ([Threading.CancellationToken]::None) } 'non-GitHub URL is rejected before HTTP' }
    finally { $urlClient.Dispose() }
    Assert-Equal $urlHandler.RequestCount 0 'rejected host/path is never requested'

    Write-Output ("PASS: {0} assertions; response-only HTML acquisition tests; PowerShell {1}." -f $script:CaseCount, $PSVersionTable.PSVersion)
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
