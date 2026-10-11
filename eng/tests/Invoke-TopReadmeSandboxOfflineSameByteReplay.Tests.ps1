$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'eng\Invoke-TopReadmeSandboxOfflineSameByteReplay.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Offline Sandbox replay script is missing: '$scriptPath'."
}

function Remove-OwnedTestTempRoot([string] $Path, [string] $NamePrefix) {
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetDirectoryName($resolvedPath)
    $leaf = [IO.Path]::GetFileName($resolvedPath)
    if (-not [string]::Equals($resolvedParent, $tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        $leaf -notmatch ('^' + [regex]::Escape($NamePrefix) + '-[0-9a-f]{32}$')) {
        throw "Refusing to recursively remove a test path that is not an expected GUID child of the temp directory: '$resolvedPath'."
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) { return }
    $item = Get-Item -LiteralPath $resolvedPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to recursively remove a reparse-point test directory: '$resolvedPath'."
    }
    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Offline Sandbox replay script did not parse: $($parseErrors[0].Message) at line $($parseErrors[0].Extent.StartLineNumber)."
}

foreach ($functionName in @('Get-AndAssertSandboxSnapshot', 'Initialize-SandboxWorkRoot')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if ($null -eq $function) { throw "Required offline replay function '$functionName' is missing." }
}

$scriptText = Get-Content -LiteralPath $scriptPath -Raw
$initializer = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-SandboxWorkRoot' }, $true)
$initializerText = $initializer.Extent.Text
foreach ($required in @('capabilities.managedRoot', "'work'", '[IO.Directory]::CreateDirectory', '[IO.FileAttributes]::ReparsePoint')) {
    if (-not $initializerText.Contains($required, [StringComparison]::Ordinal)) {
        throw "Offline Sandbox work-root initializer is missing the bounded directory invariant '$required'."
    }
}
$initializerExec = [regex]::Match($initializerText, "Invoke-WinApp -Arguments @\('target', 'exec', 'sandbox', '--', 'powershell\.exe'[^\r\n]*\) -Description 'initialize expected Sandbox work root'")
if (-not $initializerExec.Success -or $initializerExec.Value.Contains('--cwd', [StringComparison]::Ordinal)) {
    throw 'Fresh Sandbox work-root initialization must run without --cwd, because that directory may not exist yet.'
}
$initializeCall = $scriptText.IndexOf('Initialize-SandboxWorkRoot -Snapshot $snapshotNow', [StringComparison]::Ordinal)
$uniquePreflightCall = $scriptText.IndexOf("-Description 'unique guest staging-path preflight'", [StringComparison]::Ordinal)
if ($initializeCall -lt 0 -or $uniquePreflightCall -lt 0 -or $initializeCall -ge $uniquePreflightCall) {
    throw 'The expected Sandbox work root must be initialized before the first --cwd-based staging preflight.'
}
$verifierCall = $scriptText.IndexOf('Invoke-NativeAotArtifactVerifier -VerifierPath', [StringComparison]::Ordinal)
if ($verifierCall -lt 0 -or $scriptText.Contains("if (`$LASTEXITCODE -ne 0) { throw 'The selected JitHub app is not a verified x64 NativeAOT artifact.' }", [StringComparison]::Ordinal)) {
    throw 'Native AOT verification must use the script verifier contract rather than a stale native-process exit code.'
}
$verifierFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-NativeAotArtifactVerifier' }, $true)
if ($null -eq $verifierFunction) { throw 'Native AOT verifier invocation helper is missing.' }
Invoke-Expression $verifierFunction.Extent.Text
$verifierTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('offline-aot-verifier-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($verifierTestRoot) | Out-Null
try {
    $passingVerifier = Join-Path $verifierTestRoot 'passing-verifier.ps1'
    $failingVerifier = Join-Path $verifierTestRoot 'failing-verifier.ps1'
    [IO.File]::WriteAllText($passingVerifier, "param([string]`$InputPath, [string[]]`$Architecture)`nif (`$InputPath -cne 'fixture' -or `$Architecture.Count -ne 1 -or `$Architecture[0] -cne 'x64') { throw 'wrong verifier arguments' }`nWrite-Output 'verified'`n")
    [IO.File]::WriteAllText($failingVerifier, "param([string]`$InputPath, [string[]]`$Architecture)`nthrow 'verification rejected'`n")
    $priorLastExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    try {
        $global:LASTEXITCODE = 23
        Invoke-NativeAotArtifactVerifier -VerifierPath $passingVerifier -InputPath 'fixture' | Out-Null
        if ($global:LASTEXITCODE -ne 23) { throw 'Verifier script invocation unexpectedly rewrote the preexisting native exit code.' }
        $verificationRejected = $false
        try { Invoke-NativeAotArtifactVerifier -VerifierPath $failingVerifier -InputPath 'fixture' }
        catch { $verificationRejected = $_.Exception.Message.Contains('verification rejected', [StringComparison]::Ordinal) }
        if (-not $verificationRejected) { throw 'Native AOT verifier helper did not propagate a real verifier failure.' }
    }
    finally { $global:LASTEXITCODE = $priorLastExitCode }
}
finally { Remove-Item -LiteralPath $verifierTestRoot -Recurse -Force }

$identityFunctionNames = @('Assert-OrdinaryPath', 'Get-ReadmeFileDigests', 'Assert-SameByteReadmeIdentity')
foreach ($functionName in $identityFunctionNames) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if ($null -eq $function) { throw "Required README identity validator '$functionName' is missing." }
    Invoke-Expression $function.Extent.Text
}
$captureFunctionNames = @('Add-BoundedByteCount', 'Get-OrdinaryTreeFiles', 'Get-CaseId', 'Get-ValidatedCaptureCase', 'Add-SourceTreeToSnapshot', 'Get-Percentile', 'Get-OfflineAggregate')
foreach ($functionName in $captureFunctionNames) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if ($null -eq $function) { throw "Required capture validation function '$functionName' is missing." }
    Invoke-Expression $function.Extent.Text
}
$script:MaximumCorpusFileBytes = 64MB
$script:MaximumCorpusFiles = 15016
$script:MaximumCorpusEntries = 16032
$script:MaximumCorpusBytes = 308MB
$script:MaximumSourceFileBytes = 64MB
$emptyTreeTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('offline-empty-tree-' + [Guid]::NewGuid().ToString('N'))
$emptySourceRoot = Join-Path $emptyTreeTestRoot 'empty-source'
$emptySnapshotRoot = Join-Path $emptyTreeTestRoot 'snapshot'
[IO.Directory]::CreateDirectory($emptySourceRoot) | Out-Null
[IO.Directory]::CreateDirectory($emptySnapshotRoot) | Out-Null
try {
    $emptyFileManifest = [Collections.Generic.List[object]]::new()
    $emptySourceBytes = 0L
    Add-SourceTreeToSnapshot -RootName 'empty-fixture' -SourceRoot $emptySourceRoot -SnapshotRoot $emptySnapshotRoot `
        -FileManifest $emptyFileManifest -TotalBytes ([ref]$emptySourceBytes)
    if ($emptyFileManifest.Count -ne 0 -or $emptySourceBytes -ne 0) {
        throw 'Empty filtered source roots must leave the initial snapshot manifest and byte ledger empty.'
    }
}
finally { Remove-Item -LiteralPath $emptyTreeTestRoot -Recurse -Force }
$identityRoot = Join-Path ([IO.Path]::GetTempPath()) ('readme-identity-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($identityRoot) | Out-Null
try {
    $readmeBytes = [Text.Encoding]::UTF8.GetBytes("# Exact README`npublic capture bytes`r`n")
    $readmePath = Join-Path $identityRoot 'readme.md'
    [IO.File]::WriteAllBytes($readmePath, $readmeBytes)
    $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($readmeBytes)).ToLowerInvariant()
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($readmeBytes.Length)`0")
    $gitBlobBytes = [byte[]]::new($header.Length + $readmeBytes.Length)
    [Array]::Copy($header, 0, $gitBlobBytes, 0, $header.Length)
    [Array]::Copy($readmeBytes, 0, $gitBlobBytes, $header.Length, $readmeBytes.Length)
    $gitBlobSha1 = [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($gitBlobBytes)).ToLowerInvariant()

    $repository = [pscustomobject]@{
        readme = [pscustomobject]@{ sha = $gitBlobSha1; byteSize = [long]$readmeBytes.Length }
    }
    $browserCorpus = [pscustomobject]@{ readmeSha256 = $sha256; readmeBytes = [long]$readmeBytes.Length }
    $corpusManifest = [pscustomobject]@{
        readme = [pscustomobject]@{ sha256 = $sha256; byteSize = [long]$readmeBytes.Length; file = 'readme.md' }
        repository = [pscustomobject]@{ readmeGitBlobSha1 = $gitBlobSha1 }
    }

    Assert-SameByteReadmeIdentity -Repository $repository -BrowserCorpus $browserCorpus `
        -CorpusManifest $corpusManifest -CorpusDirectory $identityRoot

    [IO.File]::WriteAllBytes($readmePath, [Text.Encoding]::UTF8.GetBytes('# tampered bytes'))
    $tamperedBytesRejected = $false
    try {
        Assert-SameByteReadmeIdentity -Repository $repository -BrowserCorpus $browserCorpus `
            -CorpusManifest $corpusManifest -CorpusDirectory $identityRoot
    }
    catch { $tamperedBytesRejected = $true }
    if (-not $tamperedBytesRejected) { throw 'Same-byte README identity accepted bytes that differ from the browser SHA-256 and pinned Git blob.' }

    [IO.File]::WriteAllBytes($readmePath, $readmeBytes)
    $tamperedSha = [pscustomobject]@{ readmeSha256 = ('0' * 64); readmeBytes = [long]$readmeBytes.Length }
    $tamperedShaRejected = $false
    try {
        Assert-SameByteReadmeIdentity -Repository $repository -BrowserCorpus $tamperedSha `
            -CorpusManifest $corpusManifest -CorpusDirectory $identityRoot
    }
    catch { $tamperedShaRejected = $true }
    if (-not $tamperedShaRejected) { throw 'Same-byte README identity accepted a tampered browser SHA-256.' }
}
finally {
    Remove-Item -LiteralPath $identityRoot -Recurse -Force
}

$captureRootForTest = Join-Path ([IO.Path]::GetTempPath()) ('offline-capture-identity-' + [Guid]::NewGuid().ToString('N'))
$script:CaptureRoot = $captureRootForTest
$browserDirectoryForTest = Join-Path $captureRootForTest 'cases\005-Example-Repo\browser'
$corpusDirectoryForTest = Join-Path $browserDirectoryForTest 'same-byte-corpus'
[IO.Directory]::CreateDirectory($corpusDirectoryForTest) | Out-Null
try {
    $readmeBytesForCapture = [Text.Encoding]::UTF8.GetBytes("# Pinned README`nbytes are exact.`r`n")
    $readmePathForCapture = Join-Path $corpusDirectoryForTest 'readme.md'
    [IO.File]::WriteAllBytes($readmePathForCapture, $readmeBytesForCapture)
    $expectedSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($readmeBytesForCapture)).ToLowerInvariant()
    $gitHeader = [Text.Encoding]::ASCII.GetBytes("blob $($readmeBytesForCapture.Length)`0")
    $gitBlobInput = [byte[]]::new($gitHeader.Length + $readmeBytesForCapture.Length)
    [Array]::Copy($gitHeader, 0, $gitBlobInput, 0, $gitHeader.Length)
    [Array]::Copy($readmeBytesForCapture, 0, $gitBlobInput, $gitHeader.Length, $readmeBytesForCapture.Length)
    $expectedGitBlob = [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($gitBlobInput)).ToLowerInvariant()
    $expectedCommit = 'a' * 40
    $expectedReadmePath = 'README.md'
    $repositoryForCapture = [pscustomobject]@{
        rank = 5
        fullName = 'Example/Repo'
        commitSha = $expectedCommit
        readme = [pscustomobject]@{ available = $true; sha = $expectedGitBlob; byteSize = [long]$readmeBytesForCapture.Length; path = $expectedReadmePath }
    }

    function Write-TestCaptureIdentity {
        param(
            [Parameter(Mandatory = $true)][string]$GitBlobSha1,
            [Parameter(Mandatory = $true)][string]$ReadmeSha256,
            [Parameter(Mandatory = $true)][string]$CommitSha,
            [Parameter(Mandatory = $true)][string]$ReadmePath
        )
        $corpusManifest = [ordered]@{
            schemaVersion = 2
            complete = $true
            repository = [ordered]@{
                fullName = 'Example/Repo'
                commitSha = $CommitSha
                readmePath = $ReadmePath
                readmeGitBlobSha1 = $GitBlobSha1
            }
            readme = [ordered]@{ file = 'readme.md'; byteSize = [long]$readmeBytesForCapture.Length; sha256 = $ReadmeSha256 }
            browserRender = [ordered]@{ status = 'not-applicable'; reason = 'github-source-view' }
        }
        $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(($corpusManifest | ConvertTo-Json -Depth 16 -Compress) + "`n")
        $manifestPath = Join-Path $corpusDirectoryForTest 'manifest.json'
        [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
        $manifestSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant()
        $browser = [ordered]@{
            schemaVersion = 5
            readmeSha = $expectedGitBlob
            readmeRendered = $false
            sameByteCorpus = [ordered]@{
                # The public capture runs on Windows and serializes Path.Combine with backslashes.
                manifest = 'same-byte-corpus\manifest.json'
                manifestSha256 = $manifestSha
                readmeSha256 = $ReadmeSha256
                readmeBytes = [long]$readmeBytesForCapture.Length
            }
            sameByteHtmlReplay = [ordered]@{ status = 'not-applicable' }
        }
        $browserPath = Join-Path $browserDirectoryForTest 'browser.json'
        [IO.File]::WriteAllText($browserPath, ($browser | ConvertTo-Json -Depth 16 -Compress), [Text.UTF8Encoding]::new($false))
    }

    Write-TestCaptureIdentity -GitBlobSha1 $expectedGitBlob -ReadmeSha256 $expectedSha256 `
        -CommitSha $expectedCommit -ReadmePath $expectedReadmePath
    $validCapture = Get-ValidatedCaptureCase -Repository $repositoryForCapture
    if ($validCapture.Rank -ne 5 -or $validCapture.CorpusManifestRelativePath -cne 'same-byte-corpus/manifest.json') {
        throw 'Offline capture validation did not safely normalize a valid Windows same-byte manifest path and bind its Git/README identities.'
    }

    $badGitBlob = '0' * 40
    Write-TestCaptureIdentity -GitBlobSha1 $badGitBlob -ReadmeSha256 $expectedSha256 `
        -CommitSha $expectedCommit -ReadmePath $expectedReadmePath
    $badGitBlobRejected = $false
    try { $null = Get-ValidatedCaptureCase -Repository $repositoryForCapture }
    catch { $badGitBlobRejected = $true }
    if (-not $badGitBlobRejected) { throw 'Offline capture validation accepted a corpus manifest bound to the wrong Git blob SHA-1.' }

    $badSha256 = 'f' * 64
    Write-TestCaptureIdentity -GitBlobSha1 $expectedGitBlob -ReadmeSha256 $badSha256 `
        -CommitSha $expectedCommit -ReadmePath $expectedReadmePath
    $badSha256Rejected = $false
    try { $null = Get-ValidatedCaptureCase -Repository $repositoryForCapture }
    catch { $badSha256Rejected = $true }
    if (-not $badSha256Rejected) { throw 'Offline capture validation accepted a corpus SHA-256 that does not match the exact README bytes.' }

    Write-TestCaptureIdentity -GitBlobSha1 $expectedGitBlob -ReadmeSha256 $expectedSha256 `
        -CommitSha ('b' * 40) -ReadmePath $expectedReadmePath
    $badManifestBindingRejected = $false
    try { $null = Get-ValidatedCaptureCase -Repository $repositoryForCapture }
    catch { $badManifestBindingRejected = $true }
    if (-not $badManifestBindingRejected) { throw 'Offline capture validation accepted a hash-consistent corpus manifest for a different commit.' }

    $sameLengthTamper = [byte[]]$readmeBytesForCapture.Clone()
    $sameLengthTamper[0] = $sameLengthTamper[0] -bxor 1
    [IO.File]::WriteAllBytes($readmePathForCapture, $sameLengthTamper)
    Write-TestCaptureIdentity -GitBlobSha1 $expectedGitBlob -ReadmeSha256 $expectedSha256 `
        -CommitSha $expectedCommit -ReadmePath $expectedReadmePath
    $sameLengthTamperRejected = $false
    try { $null = Get-ValidatedCaptureCase -Repository $repositoryForCapture }
    catch { $sameLengthTamperRejected = $true }
    if (-not $sameLengthTamperRejected) { throw 'Offline capture validation accepted same-length README bytes changed after capture.' }
}
finally {
    Remove-Item -LiteralPath $captureRootForTest -Recurse -Force
}

$script:resolvedStartRank = 1
$script:resolvedCount = 1
$aggregateTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('offline-empty-failures-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($aggregateTestRoot) | Out-Null
try {
    $aggregateRepository = [pscustomobject]@{
        rank = 1
        fullName = 'Example/Empty'
        commitSha = 'a' * 40
        readme = [pscustomobject]@{ available = $false; sha = ''; byteSize = 0L; path = '' }
    }
    $aggregatePlan = [pscustomobject]@{
        Repositories = @($aggregateRepository)
        Captures = @{ 1 = [pscustomobject]@{ ReadmeRendered = $false } }
    }
    $aggregateShard = [pscustomobject]@{ Repositories = @($aggregateRepository) }
    $emptyFailures = [Collections.Generic.List[string]]::new()
    $aggregate = Get-OfflineAggregate -Pinned ([pscustomobject]@{ Sha256 = 'b' * 64 }) `
        -Plan $aggregatePlan -Shards @($aggregateShard) -EvidenceRoot $aggregateTestRoot `
        -CompletedShardCount 0 -Failures $emptyFailures
    if ($aggregate.executionStatus -cne 'failed' -or $emptyFailures.Count -ne 1 -or
        -not $emptyFailures[0].Contains('Rank 1 has 0 offline case result records.', [StringComparison]::Ordinal)) {
        throw 'Offline aggregate did not preserve the expected failed result when the initial failure list was empty.'
    }
}
finally { Remove-OwnedTestTempRoot -Path $aggregateTestRoot -NamePrefix 'offline-empty-failures' }

$guestBatchFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-GuestBatchScript' }, $true)
if ($null -eq $guestBatchFunction) { throw 'Offline guest batch generator is missing.' }
Invoke-Expression $guestBatchFunction.Extent.Text
$EdgeExecutablePath = ''
$generatedGuestBatch = New-GuestBatchScript -GuestRepo 'fixture\repo' -GuestWorkRoot 'C:\Sandbox\work' `
    -Start 1 -Amount 1 -OutputRelativePath 'artifacts\output'
$waitForExitIndex = $generatedGuestBatch.IndexOf('$process.WaitForExit()', [StringComparison]::Ordinal)
$refreshAfterWaitIndex = $generatedGuestBatch.IndexOf('$process.Refresh()', $waitForExitIndex, [StringComparison]::Ordinal)
$readExitCodeIndex = $generatedGuestBatch.IndexOf('$rawExitCode = $process.ExitCode', [StringComparison]::Ordinal)
$nullExitCodeGuardIndex = $generatedGuestBatch.IndexOf('if ($null -eq $rawExitCode)', [StringComparison]::Ordinal)
if ($waitForExitIndex -lt 0 -or $refreshAfterWaitIndex -le $waitForExitIndex -or
    $readExitCodeIndex -le $refreshAfterWaitIndex -or $nullExitCodeGuardIndex -le $readExitCodeIndex) {
    throw 'Generated guest runner must refresh the process snapshot after WaitForExit and reject a missing exit code.'
}
$guestTokens = $null
$guestParseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($generatedGuestBatch, [ref]$guestTokens, [ref]$guestParseErrors)
if ($guestParseErrors.Count -gt 0) {
    throw "Generated offline guest script did not parse: $($guestParseErrors[0].Message) at line $($guestParseErrors[0].Extent.StartLineNumber)."
}

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) { throw 'Windows PowerShell 5.1 is required for the fast-child exit-code regression.' }
$fastChildRoot = Join-Path ([IO.Path]::GetTempPath()) ('offline-fast-child-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fastChildRoot) | Out-Null
try {
    $fastChildScript = Join-Path $fastChildRoot 'fast-child-exit-code.ps1'
    [IO.File]::WriteAllText($fastChildScript, @'
$ErrorActionPreference = 'Stop'
$child = Start-Process -FilePath $env:ComSpec -ArgumentList '/d /c exit 7' -PassThru -WindowStyle Hidden
$child.WaitForExit()
$child.Refresh()
$rawExitCode = $child.ExitCode
if ($null -eq $rawExitCode) { exit 91 }
if ([int]$rawExitCode -ne 7) { exit 92 }
exit 0
'@, [Text.UTF8Encoding]::new($false))
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $fastChildScript
    $fastChildProbeExitCode = $LASTEXITCODE
    if ($fastChildProbeExitCode -ne 0) {
        throw "Windows PowerShell 5.1 failed to observe a quickly exited child after Refresh (probe exit $fastChildProbeExitCode)."
    }
}
finally { Remove-OwnedTestTempRoot -Path $fastChildRoot -NamePrefix 'offline-fast-child' }

Write-Host 'PASS: offline replay initializes only the expected attached-Sandbox work root before using it as --cwd.'
Write-Host 'PASS: offline replay binds the 64-digit README SHA-256 to actual bytes and the separate pinned Git blob SHA-1.'
Write-Host 'PASS: full offline capture-case validation accepts valid bytes and rejects bad Git blob, SHA-256, commit binding, and same-length byte tampering.'
Write-Host 'PASS: offline aggregation accepts a genuinely empty initial failure list and records missing case evidence as a failure.'
Write-Host 'PASS: offline guest exit capture refreshes the Windows PowerShell process snapshot and handles a fast child without a blank exit status.'
