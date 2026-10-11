$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'eng\Invoke-TopReadmeSandboxPublicSameByteCapture.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Public Sandbox capture script is missing: '$scriptPath'."
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Public Sandbox capture script did not parse: $($parseErrors[0].Message) at line $($parseErrors[0].Extent.StartLineNumber)."
}

foreach ($functionName in @('Get-PinnedManifest', 'Get-OrdinaryFiles', 'New-CaptureSnapshot', 'Get-AndAssertSandboxSnapshot', 'Initialize-SandboxWorkRoot', 'ConvertTo-WindowsProcessArgument', 'Invoke-CapturedProcess', 'New-GuestBatchScript', 'Test-CapturedShard', 'Resolve-SafeBrowserCorpusManifestPath', 'Assert-OrdinaryPath', 'Read-BoundedFileBytes', 'Get-BoundedFileHash', 'Read-BoundedJsonFile', 'Assert-CapturedCorpusTree', 'Assert-AvailableReadmeRendered', 'Assert-CapturedRenderedHtml')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if ($null -eq $function) { throw "Required public capture function '$functionName' is missing." }
}

$capturedShardFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-CapturedShard' }, $true)
if (-not $capturedShardFunction.Extent.Text.Contains('Resolve-SafeBrowserCorpusManifestPath', [StringComparison]::Ordinal)) {
    throw 'Captured-shard validation must resolve the corpus manifest through the safe relative-path validator.'
}
$capturedShardText = $capturedShardFunction.Extent.Text
$ordinaryManifestIndex = $capturedShardText.IndexOf('Assert-OrdinaryPath -Path $corpusRoot', [StringComparison]::Ordinal)
$treeScanIndex = $capturedShardText.IndexOf('Assert-CapturedCorpusTree -CorpusDirectory $corpusDirectory', [StringComparison]::Ordinal)
$manifestReadIndex = $capturedShardText.IndexOf('Read-BoundedJsonFile -Path $corpusRoot', [StringComparison]::Ordinal)
if ($ordinaryManifestIndex -lt 0 -or $treeScanIndex -lt 0 -or $manifestReadIndex -lt 0 -or
    $ordinaryManifestIndex -ge $treeScanIndex -or $treeScanIndex -ge $manifestReadIndex -or
    $capturedShardText.Contains('[IO.File]::ReadAllBytes', [StringComparison]::Ordinal) -or
    $capturedShardText.Contains('Get-Content -LiteralPath', [StringComparison]::Ordinal) -or
    $capturedShardText.Contains('Get-FileHash -LiteralPath', [StringComparison]::Ordinal)) {
    throw 'Captured-shard validation must scan the bounded ordinary corpus tree before reading its manifest, and all guest files must use bounded readers.'
}
$boundedReaderFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Read-BoundedFileBytes' }, $true)
$boundedReaderText = $boundedReaderFunction.Extent.Text
$ordinaryIndex = $boundedReaderText.IndexOf('Assert-OrdinaryPath -Path $Path', [StringComparison]::Ordinal)
$openIndex = $boundedReaderText.IndexOf('[IO.FileStream]::new', [StringComparison]::Ordinal)
$lengthCheckIndex = $boundedReaderText.IndexOf('$length -gt $MaximumBytes', [StringComparison]::Ordinal)
if ($ordinaryIndex -lt 0 -or $openIndex -lt 0 -or $lengthCheckIndex -lt 0 -or $ordinaryIndex -ge $openIndex -or
    -not $boundedReaderText.Contains('[IO.FileShare]::Read', [StringComparison]::Ordinal) -or
    -not $boundedReaderText.Contains('$stream.ReadByte() -ne -1', [StringComparison]::Ordinal)) {
    throw 'Bounded file reads must reject reparse ancestors before open, cap length on the opened handle, and detect mutation without ReadAllBytes.'
}
$treeFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-CapturedCorpusTree' }, $true)
if (-not $treeFunction.Extent.Text.Contains('$script:MaximumCorpusEntries', [StringComparison]::Ordinal) -or
    -not $treeFunction.Extent.Text.Contains('if (++$entryCount', [StringComparison]::Ordinal)) {
    throw 'Captured corpus traversal must count directories as well as files under a total-entry cap.'
}
$manifestPathFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-SafeBrowserCorpusManifestPath' }, $true)
Invoke-Expression $manifestPathFunction.Extent.Text
$browserRoot = Join-Path ([IO.Path]::GetTempPath()) 'readme-capture-browser-root'
$validCorpusDirectory = 'same-byte-corpus-485a494fd1214d239707630f57816c13'
$validWindowsCorpusPath = "$validCorpusDirectory\manifest.json"
$resolvedWindowsManifest = Resolve-SafeBrowserCorpusManifestPath -BrowserRoot $browserRoot -RelativePath $validWindowsCorpusPath -Rank 5
$expectedWindowsManifest = [IO.Path]::GetFullPath((Join-Path $browserRoot "$validCorpusDirectory\manifest.json"))
if (-not $resolvedWindowsManifest.Equals($expectedWindowsManifest, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A contained guest-relative Windows corpus path must resolve beneath the browser evidence directory.'
}
$validPortableCorpusDirectory = 'same-byte-corpus-0123456789abcdef0123456789abcdef'
$validPortableManifest = Resolve-SafeBrowserCorpusManifestPath -BrowserRoot $browserRoot -RelativePath "$validPortableCorpusDirectory/manifest.json" -Rank 5
if (-not $validPortableManifest.Equals([IO.Path]::GetFullPath((Join-Path $browserRoot "$validPortableCorpusDirectory\manifest.json")), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A contained portable corpus path must resolve beneath the browser evidence directory.'
}
$controlCharacterPath = "same-byte-corpus-$('a' * 32)`t/manifest.json"
$trailingNewlinePath = "$validCorpusDirectory/manifest.json`n"
$nullCharacterPath = "same-byte-corpus-$('a' * 32)$([char]0)/manifest.json"
foreach ($invalidCorpusPath in @(
    '',
    '../outside/manifest.json',
    "$validCorpusDirectory/../manifest.json",
    "$validCorpusDirectory/.. /manifest.json",
    "$validCorpusDirectory/. /manifest.json",
    "$validCorpusDirectory /manifest.json",
    "$validCorpusDirectory./manifest.json",
    "$validCorpusDirectory/../$validCorpusDirectory\manifest.json",
    'same-byte-corpus-CON/manifest.json',
    'same-byte-corpus-NUL/manifest.json',
    'same-byte-corpus/../manifest.json',
    'C:relative/manifest.json',
    '//server/share/manifest.json',
    '//?/C:/outside/manifest.json',
    '/outside/manifest.json',
    '\outside\manifest.json',
    'C:\outside\manifest.json',
    'same-byte-corpus//manifest.json',
    'same-byte-corpus\\manifest.json',
    'same-byte-corpus/manifest.json:stream',
    'same-byte-corpus/not-manifest.json',
    $controlCharacterPath,
    $trailingNewlinePath,
    $nullCharacterPath)) {
    $rejected = $false
    try { [void](Resolve-SafeBrowserCorpusManifestPath -BrowserRoot $browserRoot -RelativePath $invalidCorpusPath -Rank 5) }
    catch { $rejected = $_.Exception.Message.Contains('path is unsafe', [StringComparison]::Ordinal) }
    if (-not $rejected) { throw "Unsafe corpus manifest path was accepted or failed for an unrelated reason: '$invalidCorpusPath'." }
}

# Exercise bounded readers, directory-only traversal caps, and actual reparse rejection.
$helperNames = @('Assert-OrdinaryPath', 'Read-BoundedFileBytes', 'Get-BoundedFileHash', 'Read-BoundedJsonFile', 'Assert-CapturedCorpusTree', 'Assert-AvailableReadmeRendered', 'Assert-CapturedRenderedHtml')
$helperDefinitions = foreach ($functionName in $helperNames) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    $function.Extent.Text
}
Invoke-Expression ($helperDefinitions -join "`n")
$script:MaximumCaptureReportBytes = 4MB
$script:MaximumCorpusManifestBytes = 4MB
$script:MaximumCorpusFileBytes = 64MB
$script:MaximumCorpusFiles = 15016
$script:MaximumCorpusBytes = 308MB
$script:MaximumCorpusEntries = 100000
$ioTempFullPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@([char]92, [char]47)) + [IO.Path]::DirectorySeparatorChar
$ioFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('same-byte-bounded-reader-' + [guid]::NewGuid().ToString('N'))
$ioFixtureFullPath = [IO.Path]::GetFullPath($ioFixtureRoot)
if (-not $ioFixtureFullPath.StartsWith($ioTempFullPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporary bounded-reader fixture escaped the system temporary directory.'
}
try {
    [IO.Directory]::CreateDirectory($ioFixtureRoot) | Out-Null
    $boundedFile = Join-Path $ioFixtureRoot 'bounded.bin'
    $expectedBytes = [byte[]]@(0, 1, 127, 128, 255)
    [IO.File]::WriteAllBytes($boundedFile, $expectedBytes)
    $readBytes = Read-BoundedFileBytes -Path $boundedFile -MaximumBytes 5 -Description 'bounded-reader exact file'
    if ([Convert]::ToHexString($readBytes) -cne [Convert]::ToHexString($expectedBytes)) {
        throw 'Bounded reader changed exact file bytes.'
    }
    $readHash = Get-BoundedFileHash -Path $boundedFile -MaximumBytes 5 -Description 'bounded-hash exact file'
    if ($readHash.Length -ne 5 -or $readHash.Sha256 -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($expectedBytes)).ToLowerInvariant()) {
        throw 'Streaming bounded hash did not bind exact file bytes and length.'
    }
    $oversizedRejected = $false
    try { [void](Read-BoundedFileBytes -Path $boundedFile -MaximumBytes 4 -Description 'bounded-reader over-cap file') }
    catch { $oversizedRejected = $_.Exception.Message.Contains('read cap', [StringComparison]::Ordinal) }
    if (-not $oversizedRejected) { throw 'Bounded reader accepted a file larger than its configured cap.' }
    $oversizedHashRejected = $false
    try { [void](Get-BoundedFileHash -Path $boundedFile -MaximumBytes 4 -Description 'bounded-hash over-cap file') }
    catch { $oversizedHashRejected = $_.Exception.Message.Contains('read cap', [StringComparison]::Ordinal) }
    if (-not $oversizedHashRejected) { throw 'Streaming bounded hash accepted a file larger than its configured cap.' }

    $jsonPath = Join-Path $ioFixtureRoot 'bounded.json'
    [IO.File]::WriteAllText($jsonPath, '{"ok":true}', [Text.UTF8Encoding]::new($false))
    $jsonRead = Read-BoundedJsonFile -Path $jsonPath -MaximumBytes 32 -Description 'bounded JSON file' -Depth 4
    if ($jsonRead.Value.ok -ne $true -or $jsonRead.Sha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Bounded JSON read did not return the parsed value and hash of the same bytes.'
    }

    $corpusDirectory = Join-Path $ioFixtureRoot 'corpus'
    [IO.Directory]::CreateDirectory($corpusDirectory) | Out-Null
    foreach ($index in 0..3) { [IO.Directory]::CreateDirectory((Join-Path $corpusDirectory "empty-$index")) | Out-Null }
    $script:MaximumCorpusEntries = 3
    $directoryFanoutRejected = $false
    try { Assert-CapturedCorpusTree -CorpusDirectory $corpusDirectory -Rank 5 }
    catch { $directoryFanoutRejected = $_.Exception.Message.Contains('entry count exceeds cap', [StringComparison]::Ordinal) }
    finally { $script:MaximumCorpusEntries = 100000 }
    if (-not $directoryFanoutRejected) { throw 'Captured corpus traversal did not count directory-only fanout against its entry cap.' }

    $outsideDirectory = Join-Path $ioFixtureRoot 'outside'
    $reparseCorpus = Join-Path $ioFixtureRoot 'reparse-corpus'
    [IO.Directory]::CreateDirectory($outsideDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($reparseCorpus) | Out-Null
    $junctionPath = Join-Path $reparseCorpus 'redirected'
    $junctionCreated = $false
    try {
        [void](New-Item -ItemType Junction -Path $junctionPath -Target $outsideDirectory -ErrorAction Stop)
        $junctionCreated = $true
    }
    catch {
        Write-Host 'SKIP: filesystem does not permit the bounded-corpus junction regression in this environment.'
    }
    if ($junctionCreated) {
        $reparseRejected = $false
        try { Assert-CapturedCorpusTree -CorpusDirectory $reparseCorpus -Rank 5 }
        catch { $reparseRejected = $_.Exception.Message.Contains('redirected entry', [StringComparison]::Ordinal) }
        if (-not $reparseRejected) { throw 'Captured corpus traversal followed or accepted a directory junction.' }

        $fileReparseRejected = $false
        try { [void](Read-BoundedFileBytes -Path (Join-Path $junctionPath 'bounded.bin') -MaximumBytes 5 -Description 'reparse file probe') }
        catch { $fileReparseRejected = $_.Exception.Message.Contains('redirected path component', [StringComparison]::Ordinal) }
        if (-not $fileReparseRejected) { throw 'Bounded file reader did not reject a reparse ancestor before opening the file.' }
    }

    $availableRepository = [pscustomobject]@{ readme = [pscustomobject]@{ available = $true } }
    $unprovenSourceView = [pscustomobject]@{
        readmeRendered = $false
        sameByteHtmlReplay = [pscustomobject]@{ status = 'not-applicable'; reason = 'github-source-view' }
    }
    $unprovenSourceViewRejected = $false
    try { Assert-AvailableReadmeRendered -Repository $availableRepository -Browser $unprovenSourceView -Rank 5 }
    catch { $unprovenSourceViewRejected = $_.Exception.Message.Contains('was not rendered', [StringComparison]::Ordinal) }
    if (-not $unprovenSourceViewRejected) {
        throw 'An available README with only self-declared source-view status must not count as a captured rendering case.'
    }
    $stringBooleanRejected = $false
    try { Assert-AvailableReadmeRendered -Repository $availableRepository -Browser ([pscustomobject]@{ readmeRendered = 'true' }) -Rank 5 }
    catch { $stringBooleanRejected = $_.Exception.Message.Contains('Boolean README-rendered status', [StringComparison]::Ordinal) }
    if (-not $stringBooleanRejected) { throw 'A string-valued readmeRendered field was accepted as a Boolean.' }

    $renderedBrowser = [pscustomobject]@{ readmeRendered = $true }
    $notApplicableManifest = [pscustomobject]@{ browserRender = [pscustomobject]@{ status = 'not-applicable'; reason = 'github-source-view' } }
    $notApplicableRejected = $false
    try { Assert-CapturedRenderedHtml -Browser $renderedBrowser -CorpusManifest $notApplicableManifest -Rank 5 }
    catch { $notApplicableRejected = $_.Exception.Message.Contains('lacks verified rendered-HTML evidence', [StringComparison]::Ordinal) }
    if (-not $notApplicableRejected) { throw 'A rendered README with not-applicable HTML status was accepted for this capture.' }

    $missingStatusManifest = [pscustomobject]@{ browserRender = [pscustomobject]@{ file = 'rendered.html'; byteSize = 4; sha256 = 'a' * 64 } }
    $missingStatusRejected = $false
    try { Assert-CapturedRenderedHtml -Browser $renderedBrowser -CorpusManifest $missingStatusManifest -Rank 5 }
    catch { $missingStatusRejected = $_.Exception.Message.Contains('lacks verified rendered-HTML evidence', [StringComparison]::Ordinal) }
    if (-not $missingStatusRejected) { throw 'A rendered README with missing HTML status was accepted for this capture.' }

    $passedManifest = [pscustomobject]@{ browserRender = [pscustomobject]@{ status = 'passed'; file = 'rendered.html'; byteSize = 4; sha256 = 'a' * 64 } }
    Assert-CapturedRenderedHtml -Browser $renderedBrowser -CorpusManifest $passedManifest -Rank 5
}
finally {
    if (Test-Path -LiteralPath $ioFixtureFullPath) { Remove-Item -LiteralPath $ioFixtureFullPath -Recurse -Force }
}

# The batch-level summary is not sufficient if an individual case report contradicts it.
$capturedShardFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-CapturedShard' }, $true)
Invoke-Expression $capturedShardFunction.Extent.Text
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('same-byte-case-status-' + [guid]::NewGuid().ToString('N'))
$fixtureFullPath = [IO.Path]::GetFullPath($fixtureRoot)
$tempFullPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@([char]92, [char]47)) + [IO.Path]::DirectorySeparatorChar
if (-not $fixtureFullPath.StartsWith($tempFullPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporary case-status fixture escaped the system temporary directory.'
}
try {
    $shardRoot = Join-Path $fixtureRoot 'shard'
    $batchDirectory = Join-Path $shardRoot 'capture-batches'
    $caseRoot = Join-Path $shardRoot 'cases\005-fixture-repo'
    [IO.Directory]::CreateDirectory($batchDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    $pinnedSha = 'a' * 64
    $repository = [pscustomobject]@{
        rank = 5
        fullName = 'fixture/repo'
        commitSha = 'b' * 40
        readme = [pscustomobject]@{ available = $false; sha = '' }
    }
    $shard = [pscustomobject]@{ Start = 5; End = 5; Count = 1; Repositories = @($repository) }
    $batchFixture = [ordered]@{
        schemaVersion = 1
        scope = 'public-pinned-same-byte-browser-capture-in-existing-windows-sandbox'
        overallReleaseQualification = 'not-qualified'
        authenticatedLiveAuditEquivalent = $false
        nativeJitHubRenderComparison = 'not-run'
        sameByteEdgeFullPageReplay = 'not-run'
        pinnedManifestSha256 = $pinnedSha
        requestedStartRank = 5
        requestedCount = 1
        executionStatus = 'captured-within-browser-only-scope'
        capturedCaseCount = 1
        failedCaseCount = 0
    }
    $caseFixture = [ordered]@{
        rank = 5
        fullName = 'fixture/repo'
        commitSha = 'b' * 40
        readmeSha = 'absent'
        status = 'failed'
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $batchDirectory 'ranks-005-005.json'), ($batchFixture | ConvertTo-Json -Depth 16 -Compress), $utf8)
    [IO.File]::WriteAllText((Join-Path $caseRoot 'capture.json'), ($caseFixture | ConvertTo-Json -Depth 16 -Compress), $utf8)
    $contradictoryBatchRejected = $false
    try { Test-CapturedShard -ShardRoot $shardRoot -Shard $shard -ExpectedManifestSha256 $pinnedSha }
    catch { $contradictoryBatchRejected = $_.Exception.Message.Contains('Capture result is not successful for pinned rank 5', [StringComparison]::Ordinal) }
    if (-not $contradictoryBatchRejected) {
        throw 'A batch claiming all captures succeeded must reject an individual failed case report.'
    }
}
finally {
    if (Test-Path -LiteralPath $fixtureFullPath) {
        Remove-Item -LiteralPath $fixtureFullPath -Recurse -Force
    }
}

$scriptText = Get-Content -LiteralPath $scriptPath -Raw
foreach ($required in @(
    'target snapshot sandbox --json',
    "'target', 'push', 'sandbox'",
    "'target', 'exec', 'sandbox'",
    "'target', 'pull', 'sandbox'",
    "--probe=readme-public-same-byte-capture",
    "--capture-same-byte-corpus",
    "overallReleaseQualification = 'not-qualified'",
    "authenticatedLiveAuditEquivalent = `$false",
    "liveTop500RankingFreshness = 'not-checked'",
    "nativeJitHubRenderComparison = 'not-run'",
    "sameByteEdgeFullPageReplay = 'not-run'",
    '$script:MaximumCorpusManifestBytes = 4MB',
    'nodeVersion = $nodeVersion',
    'Staged Node failed the expected-version probe before any repository capture.',
    "No host browser or UI automation were performed",
    "no host fallback was attempted")) {
    if (-not $scriptText.Contains($required, [StringComparison]::Ordinal)) {
        throw "Public Sandbox capture script is missing required isolation/scope contract '$required'."
    }
}

$guestBatch = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-GuestBatchScript' }, $true)
$guestBatchText = $guestBatch.Extent.Text
$functionDefinitions = foreach ($functionName in @('ConvertTo-WindowsProcessArgument', 'Invoke-CapturedProcess')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    $function.Extent.Text
}
Invoke-Expression ($functionDefinitions -join "`n")
$nodeProbeIndex = $guestBatchText.IndexOf('nodeVersionExitCode', [StringComparison]::Ordinal)
$runnerStartIndex = $guestBatchText.IndexOf('Invoke-CapturedProcess -FilePath `$runner', [StringComparison]::Ordinal)
if ($nodeProbeIndex -lt 0 -or $runnerStartIndex -lt 0 -or $nodeProbeIndex -ge $runnerStartIndex) {
    throw 'The staged Node executable must pass its pinned version check before the first browser-capture runner invocation.'
}
$sandbox = [pscustomobject]@{ workRoot = 'C:\WinApp\work' }
$EdgeExecutablePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$script:GitHubCredentialEnvironmentNames = @('GITHUB_TOKEN')
Invoke-Expression $guestBatchText
$generatedGuestBatch = New-GuestBatchScript -GuestRepo 'readme-public-same-byte\test\repo' -GuestOutput 'artifacts\out' -FirstRank 1 -Amount 1 -ExpectedNodeVersion 'v22.22.0'
$generatedProbeIndex = $generatedGuestBatch.IndexOf("`$nodeVersionOutput", [StringComparison]::Ordinal)
$generatedRunnerIndex = $generatedGuestBatch.IndexOf('Invoke-CapturedProcess -FilePath $runner', [StringComparison]::Ordinal)
if ($generatedProbeIndex -lt 0 -or $generatedRunnerIndex -lt 0 -or $generatedProbeIndex -ge $generatedRunnerIndex -or
    -not $generatedGuestBatch.Contains("-cne 'v22.22.0'", [StringComparison]::Ordinal) -or
    -not $generatedGuestBatch.Contains("'runner-output.log'", [StringComparison]::Ordinal) -or
    -not $generatedGuestBatch.Contains("'runner-error.log'", [StringComparison]::Ordinal) -or
    $generatedGuestBatch.Contains('& $runner @runnerArgs', [StringComparison]::Ordinal)) {
    throw 'Generated guest capture script must validate staged Node, keep runner stdout/stderr separate, and avoid PowerShell native stderr redirection.'
}
$guestTokens = $null
$guestParseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($generatedGuestBatch, [ref]$guestTokens, [ref]$guestParseErrors)
if ($guestParseErrors.Count -gt 0) {
    throw "Generated guest process launcher did not parse: $($guestParseErrors[0].Message) at line $($guestParseErrors[0].Extent.StartLineNumber)."
}

# Exercise the exact ProcessStartInfo launcher from Windows PowerShell 5.1 with a child that writes stderr,
# continues writing stdout, persists its report, and exits nonzero. This pins the old abort-after-stderr failure.
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) { throw 'Windows PowerShell 5.1 is required for the guest stderr regression.' }
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('readme-capture-stderr-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null
try {
    $fakeRunner = Join-Path $tempRoot 'fake runner.ps1'
    $reportPath = Join-Path $tempRoot 'capture report.json'
    $stdoutPath = Join-Path $tempRoot 'runner-output.log'
    $stderrPath = Join-Path $tempRoot 'runner-error.log'
    $fakeRunnerSource = @'
param([string]$ReportPath, [string]$Marker)
$ErrorActionPreference = 'Stop'
[Console]::Out.WriteLine('stdout-before')
[Console]::Error.WriteLine('stderr-record')
[IO.File]::WriteAllText($ReportPath, '{"completed":true}')
[Console]::Out.WriteLine("stdout-after:$Marker")
exit 19
'@
    [IO.File]::WriteAllText($fakeRunner, $fakeRunnerSource, (New-Object System.Text.UTF8Encoding($false)))
    $exitCode = Invoke-CapturedProcess -FilePath $windowsPowerShell -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $fakeRunner, '-ReportPath', $reportPath, '-Marker', 'marker with spaces') -WorkingDirectory $tempRoot -StandardOutputPath $stdoutPath -StandardErrorPath $stderrPath
    $stdout = Get-Content -LiteralPath $stdoutPath -Raw
    $stderr = Get-Content -LiteralPath $stderrPath -Raw
    if ($exitCode -ne 19 -or
        -not $stdout.Contains('stdout-before', [StringComparison]::Ordinal) -or
        -not $stdout.Contains('stdout-after:marker with spaces', [StringComparison]::Ordinal) -or
        -not $stderr.Contains('stderr-record', [StringComparison]::Ordinal) -or
        -not (Test-Path -LiteralPath $reportPath -PathType Leaf) -or
        (Get-Content -LiteralPath $reportPath -Raw) -cne '{"completed":true}') {
        throw "Captured PS5 runner did not preserve complete output/report/exit status (exit=$exitCode)."
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
foreach ($forbidden in @('target create', 'target connect', 'FlaUI', '--app=')) {
    if ($scriptText.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Public browser-only capture must not use '$forbidden'."
    }
}

$initializer = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-SandboxWorkRoot' }, $true)
$initializerText = $initializer.Extent.Text
foreach ($required in @('capabilities.managedRoot', "'work'", '[IO.Directory]::CreateDirectory', '[IO.FileAttributes]::ReparsePoint')) {
    if (-not $initializerText.Contains($required, [StringComparison]::Ordinal)) {
        throw "Sandbox work-root initializer is missing the bounded directory invariant '$required'."
    }
}
$initializerExec = [regex]::Match($initializerText, "Invoke-WinApp -Arguments @\('target', 'exec', 'sandbox', '--', 'powershell\.exe'[^\r\n]*\) -Description 'initialize expected Sandbox work root'")
if (-not $initializerExec.Success -or $initializerExec.Value.Contains('--cwd', [StringComparison]::Ordinal)) {
    throw 'Fresh Sandbox work-root initialization must run without --cwd, because that directory may not exist yet.'
}
$initializeCall = $scriptText.IndexOf('Initialize-SandboxWorkRoot -Snapshot $snapshotNow', [StringComparison]::Ordinal)
$uniquePreflightCall = $scriptText.IndexOf("-Description 'unique Sandbox staging path preflight'", [StringComparison]::Ordinal)
if ($initializeCall -lt 0 -or $uniquePreflightCall -lt 0 -or $initializeCall -ge $uniquePreflightCall) {
    throw 'The expected Sandbox work root must be initialized before the first --cwd-based staging preflight.'
}

# Invalid rank bounds must fail before reading/building inputs or contacting the Sandbox.
$invalidRangeRejected = $false
try {
    & $scriptPath -AutomationRunnerDirectory 'C:\missing-runner' -NodeExecutablePath 'C:\missing-node.exe' -StartRank 500 -Count 2
}
catch {
    $invalidRangeRejected = $_.Exception.Message.Contains('within 1..500', [StringComparison]::Ordinal)
    if (-not $invalidRangeRejected) { throw }
}
if (-not $invalidRangeRejected) { throw 'Public capture accepted a rank range beyond the pinned top 500.' }

Write-Host 'PASS: public same-byte capture stays inside the existing Sandbox, strips credentials, validates pinned identities, and remains explicitly unqualified.'
