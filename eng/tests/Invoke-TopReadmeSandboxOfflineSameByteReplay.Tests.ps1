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
$captureFunctionNames = @('Add-BoundedByteCount', 'Get-OrdinaryTreeFiles', 'Get-CaseId', 'Get-ValidatedCaptureCase', 'Test-SafeCredentialFilename', 'Test-SourceFileIncluded', 'Copy-VerifiedFile', 'Add-SourceTreeToSnapshot', 'Get-Percentile', 'Get-OfflineAggregate')
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
$script:MaximumSourceSnapshotBytes = 128MB
foreach ($variableName in @('CredentialFileNames', 'SensitiveExtensions', 'ExcludedDirectoryNames')) {
    $initializerAssignment = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -ceq ('$script:' + $variableName)
    }, $true)
    if ($null -eq $initializerAssignment) { throw "Missing snapshot policy initializer: $variableName" }
    Invoke-Expression $initializerAssignment.Extent.Text
}
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
    $nestedSourceRoot = Join-Path $emptyTreeTestRoot 'runtime-source'
    [IO.Directory]::CreateDirectory($nestedSourceRoot) | Out-Null
    $fixtureHelper = Join-Path $nestedSourceRoot 'Install-PinnedWindowsAppRuntime.ps1'
    [IO.File]::WriteAllText($fixtureHelper, '# Snapshot identity fixture')
    Add-SourceTreeToSnapshot -RootName 'eng\readme-audit' -SourceRoot $nestedSourceRoot -SnapshotRoot $emptySnapshotRoot `
        -FileManifest $emptyFileManifest -TotalBytes ([ref]$emptySourceBytes)
    $helperEntries = @($emptyFileManifest | Where-Object {
        [string]$_.relativePath -ceq 'eng/readme-audit/Install-PinnedWindowsAppRuntime.ps1'
    })
    if ($helperEntries.Count -ne 1 -or
        [string]$helperEntries[0].sha256 -cne (Get-FileHash -LiteralPath $fixtureHelper -Algorithm SHA256).Hash.ToLowerInvariant() -or
        -not (Test-Path -LiteralPath (Join-Path $emptySnapshotRoot $helperEntries[0].relativePath) -PathType Leaf)) {
        throw 'Nested source roots must record a canonical slash-separated path and the staged helper hash.'
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
$guestInvocationFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-GuestBatchExecArguments' }, $true)
if ($null -eq $guestInvocationFunction) { throw 'Offline guest batch file invocation helper is missing.' }
Invoke-Expression $guestInvocationFunction.Extent.Text
$EdgeExecutablePath = ''
$generatedGuestBatch = New-GuestBatchScript -GuestRepo 'fixture\repo' -GuestWorkRoot 'C:\Sandbox\work' `
    -Start 1 -Amount 1 -OutputRelativePath 'artifacts\output'
$specialGuestWorkRoot = "C:\Sandbox\O'Connor Workspace\work"
$specialGuestRepo = 'offline-same-byte\run-123\repo'
$specialGuestBatch = New-GuestBatchScript -GuestRepo $specialGuestRepo -GuestWorkRoot $specialGuestWorkRoot `
    -Start 1 -Amount 1 -OutputRelativePath 'artifacts\output'
$specialGuestTokens = $null
$specialGuestParseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($specialGuestBatch, [ref]$specialGuestTokens, [ref]$specialGuestParseErrors)
$specialRepoPath = Join-Path $specialGuestWorkRoot $specialGuestRepo
$specialRepoLiteral = $specialRepoPath.Replace("'", "''")
$specialAppPath = Join-Path $specialRepoPath 'artifacts\offline-same-byte-candidate\JitHub.WinUI.exe'
$specialAppLiteral = $specialAppPath.Replace("'", "''")
if ($specialGuestParseErrors.Count -gt 0 -or
    -not $specialGuestBatch.Contains("`$repo = '$specialRepoLiteral'", [StringComparison]::Ordinal) -or
    -not $specialGuestBatch.Contains("'$specialAppLiteral'", [StringComparison]::Ordinal)) {
    throw 'Generated guest PowerShell must preserve embedded apostrophes in every staged path literal.'
}
$waitForExitIndex = $generatedGuestBatch.IndexOf('$process.WaitForExit()', [StringComparison]::Ordinal)
$refreshAfterWaitIndex = $generatedGuestBatch.IndexOf('$process.Refresh()', $waitForExitIndex, [StringComparison]::Ordinal)
$readExitCodeIndex = $generatedGuestBatch.IndexOf('$rawExitCode = $process.ExitCode', [StringComparison]::Ordinal)
$nullExitCodeGuardIndex = $generatedGuestBatch.IndexOf('if ($null -eq $rawExitCode)', [StringComparison]::Ordinal)
$disposeProcessIndex = $generatedGuestBatch.IndexOf('$process.Dispose()', $nullExitCodeGuardIndex, [StringComparison]::Ordinal)
if ($waitForExitIndex -lt 0 -or $refreshAfterWaitIndex -le $waitForExitIndex -or
    $readExitCodeIndex -le $refreshAfterWaitIndex -or $nullExitCodeGuardIndex -le $readExitCodeIndex -or
    $disposeProcessIndex -le $nullExitCodeGuardIndex) {
    throw 'Generated guest runner must refresh the process snapshot, reject a missing exit code, then dispose its owned process handle.'
}
$guestTokens = $null
$guestParseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($generatedGuestBatch, [ref]$guestTokens, [ref]$guestParseErrors)
if ($guestParseErrors.Count -gt 0) {
    throw "Generated offline guest script did not parse: $($guestParseErrors[0].Message) at line $($guestParseErrors[0].Extent.StartLineNumber)."
}
$longGuestRoot = 'C:\Users\neroc\AppData\Local\Microsoft\WinApp\Sandbox\ManagedRoot\work'
$longGuestRepo = 'offline-same-byte\' + ('a' * 32) + '\repo'
$longPathGuestBatch = New-GuestBatchScript -GuestRepo $longGuestRepo -GuestWorkRoot $longGuestRoot `
    -Start 1 -Amount 25 -OutputRelativePath 'artifacts\offline-same-byte-output\ranks-001-025'
$encodedBatchCommand = 'powershell.exe -NoProfile -NonInteractive -EncodedCommand ' +
    [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($longPathGuestBatch))
if ($encodedBatchCommand.Length -lt 32767) {
    throw 'The regression fixture no longer demonstrates that encoding the generated guest runner exceeds the Windows command-line limit.'
}
$guestScriptPathForTest = 'C:\Sandbox\work\offline-same-byte\run-123\repo\offline-same-byte-ranks-001-001-run-123.ps1'
$guestExecArgs = New-GuestBatchExecArguments -GuestCwd 'C:\Sandbox\work\offline-same-byte\run-123\repo' -GuestScriptPath $guestScriptPathForTest
if ($guestExecArgs -notcontains '-ExecutionPolicy' -or $guestExecArgs -notcontains 'Bypass' -or
    $guestExecArgs -notcontains '-File' -or $guestExecArgs -contains '-EncodedCommand' -or
    $guestExecArgs[-1] -cne $guestScriptPathForTest) {
    throw 'Oversized guest runner content must travel as a staged script file and launch through process-scoped PowerShell -ExecutionPolicy Bypass -File.'
}
$guestLiteralFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-GuestPowerShellSingleQuotedLiteral' }, $true)
if ($null -eq $guestLiteralFunction) { throw 'Guest PowerShell path literal helper is missing.' }
Invoke-Expression $guestLiteralFunction.Extent.Text
$guestRepoAbsolute = "C:\Sandbox\O'Connor Workspace\run-123\repo"
$escapedGuestRepoLiteral = ConvertTo-GuestPowerShellSingleQuotedLiteral -Value $guestRepoAbsolute
$guestRepoLiteralAssignment = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -ceq '$guestRepoAbsoluteLiteral'
}, $true)
$relativeProbeAssignment = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -ceq '$relativeProbe'
}, $true)
if ($escapedGuestRepoLiteral -cne "'C:\Sandbox\O''Connor Workspace\run-123\repo'" -or
    $null -eq $guestRepoLiteralAssignment -or
    -not $guestRepoLiteralAssignment.Extent.Text.Contains('ConvertTo-GuestPowerShellSingleQuotedLiteral -Value $guestRepoAbsolute', [StringComparison]::Ordinal) -or
    $null -eq $relativeProbeAssignment -or
    -not $relativeProbeAssignment.Extent.Text.Contains('$guestRepoAbsoluteLiteral', [StringComparison]::Ordinal)) {
    throw 'The top-level guest staging-path probe must safely quote apostrophes in the absolute guest repository path.'
}
$longScriptPath = 'C:\' + ('x' * 32780) + '.ps1'
$oversizedCommandRejected = $false
try { $null = New-GuestBatchExecArguments -GuestCwd 'C:\Sandbox\work' -GuestScriptPath $longScriptPath }
catch { $oversizedCommandRejected = $_.Exception.Message.Contains('32767-character process limit', [StringComparison]::Ordinal) }
if (-not $oversizedCommandRejected) { throw 'The staged-script launch helper must still reject a genuinely oversized PowerShell command line.' }
$offlineBatchSource = Get-Content -LiteralPath $scriptPath -Raw
$batchGeneratorIndex = $offlineBatchSource.IndexOf('$guestScript = New-GuestBatchScript', [StringComparison]::Ordinal)
$obsoleteInlineEncodingIndex = if ($batchGeneratorIndex -ge 0) {
    $offlineBatchSource.IndexOf('$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($guestScript))', $batchGeneratorIndex, [StringComparison]::Ordinal)
}
else { -1 }
if (-not $offlineBatchSource.Contains("target', 'push', 'sandbox', `$hostGuestScriptPath, `$guestControlRelativePath", [StringComparison]::Ordinal) -or
    -not $offlineBatchSource.Contains("New-GuestBatchExecArguments -GuestCwd `$guestCwd -GuestScriptPath `$guestScriptAbsolute", [StringComparison]::Ordinal) -or
    $obsoleteInlineEncodingIndex -ge 0) {
    throw 'Offline shards must push their generated control script and invoke the staged path with the bounded -File argument list.'
}
$generatedGuestAst = [System.Management.Automation.Language.Parser]::ParseInput($generatedGuestBatch, [ref]$guestTokens, [ref]$guestParseErrors)
foreach ($functionName in @('Read-CompletedLogLines', 'Update-RunnerProgress', 'Stop-OwnedRunnerTree')) {
    $generatedFunction = $generatedGuestAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if ($null -eq $generatedFunction) { throw "Generated offline batch is missing '$functionName'." }
    Invoke-Expression $generatedFunction.Extent.Text
}
foreach ($functionName in @('ConvertTo-WindowsCommandLineArgument', 'Get-BoundedLogTail')) {
    $generatedFunction = $generatedGuestAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if ($null -eq $generatedFunction) { throw "Generated offline batch is missing '$functionName'." }
    Invoke-Expression $generatedFunction.Extent.Text
}
if (-not $generatedGuestBatch.Contains('$maximumBytesPerPoll = 1048576', [StringComparison]::Ordinal) -or
    -not $generatedGuestBatch.Contains('$maximumLineBytes = 1048576', [StringComparison]::Ordinal) -or
    -not $generatedGuestBatch.Contains('[AllowEmptyString()]', [StringComparison]::Ordinal)) {
    throw 'Generated progress reader must bound each poll and partial line, and accept blank progress lines.'
}
if (-not $generatedGuestBatch.Contains('$runnerArgumentLine = ($arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument', [StringComparison]::Ordinal) -or
    $generatedGuestBatch.Contains('Start-Process -FilePath $runner -ArgumentList $arguments', [StringComparison]::Ordinal) -or
    -not $generatedGuestBatch.Contains('$runnerProcessHandle = $process.SafeHandle', [StringComparison]::Ordinal) -or
    $generatedGuestBatch.Contains('Get-Content -LiteralPath $stdout', [StringComparison]::Ordinal) -or
    $generatedGuestBatch.Contains('Get-Content -LiteralPath $stderr', [StringComparison]::Ordinal)) {
    throw 'Generated guest runner must quote child arguments, retain its redirected process handle, and bound final log output.'
}

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) { throw 'Windows PowerShell 5.1 is required for the guest-script regressions.' }
$readerRoot = Join-Path ([IO.Path]::GetTempPath()) ('offline-log-reader-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($readerRoot) | Out-Null
try {
    $logPath = Join-Path $readerRoot 'runner.stdout.log'
    $writer = [IO.FileStream]::new($logPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $committedOffset = 0L
    $pendingBytes = [byte[]]::new(0)
    try {
        $prefix = [Text.Encoding]::UTF8.GetBytes('rank-5 caf')
        $writer.Write($prefix, 0, $prefix.Length)
        $writer.Write([byte[]]@(0xC3), 0, 1)
        $writer.Flush()
        $beforeNewline = @(Read-CompletedLogLines -Path $logPath -CommittedByteOffset ([ref]$committedOffset) -PendingBytes ([ref]$pendingBytes))
        if ($beforeNewline.Count -ne 0 -or $committedOffset -ne 0 -or $pendingBytes.Length -ne ($prefix.Length + 1)) {
            throw 'The incremental reader exposed an unterminated line or discarded its split UTF-8 prefix.'
        }

        $writer.Write([byte[]]@(0xA9), 0, 1)
        $tail = [Text.Encoding]::UTF8.GetBytes("`r`n`nsecond`nlast-partial")
        $writer.Write($tail, 0, $tail.Length)
        $writer.Flush()
        $completed = @(Read-CompletedLogLines -Path $logPath -CommittedByteOffset ([ref]$committedOffset) -PendingBytes ([ref]$pendingBytes))
        if ($completed.Count -ne 3 -or $completed[0] -cne 'rank-5 café' -or $completed[1] -cne '' -or
            $completed[2] -cne 'second' -or [Text.Encoding]::UTF8.GetString($pendingBytes) -cne 'last-partial') {
            throw 'The incremental reader did not preserve UTF-8 split bytes, CRLF, blank lines, and a pending final line.'
        }
        Update-RunnerProgress -Line ''
    }
    finally { $writer.Dispose() }
    $flushed = @(Read-CompletedLogLines -Path $logPath -CommittedByteOffset ([ref]$committedOffset) -PendingBytes ([ref]$pendingBytes) -FlushIncompleteLine)
    if ($flushed.Count -ne 1 -or $flushed[0] -cne 'last-partial' -or $pendingBytes.Length -ne 0) {
        throw 'The final unterminated runner line was not drained exactly once after the writer exited.'
    }

    $backlogPath = Join-Path $readerRoot 'large-runner.stdout.log'
    $block = [byte[]]::new(65536)
    for ($index = 0; $index -lt ($block.Length - 1); $index++) { $block[$index] = 120 }
    $block[$block.Length - 1] = 10
    $backlog = [byte[]]::new((20 * $block.Length) + 10)
    for ($offset = 0; $offset -lt (20 * $block.Length); $offset += $block.Length) {
        [Array]::Copy($block, 0, $backlog, $offset, $block.Length)
    }
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('final-tail'), 0, $backlog, 20 * $block.Length, 10)
    [IO.File]::WriteAllBytes($backlogPath, $backlog)
    $largeCommitted = 0L
    $largePending = [byte[]]::new(0)
    $firstBatch = @(Read-CompletedLogLines -Path $backlogPath -CommittedByteOffset ([ref]$largeCommitted) -PendingBytes ([ref]$largePending))
    if ($firstBatch.Count -ne 16 -or $largeCommitted -ne 1048576 -or $largePending.Length -ne 0) {
        throw 'One progress poll consumed more than its 1 MiB snapshot budget or failed to process its bounded chunk.'
    }
    $secondBatch = @(Read-CompletedLogLines -Path $backlogPath -CommittedByteOffset ([ref]$largeCommitted) -PendingBytes ([ref]$largePending))
    if ($secondBatch.Count -ne 4 -or $largePending.Length -ne 10) {
        throw 'The second bounded poll did not preserve the final partial line after a >1 MiB output backlog.'
    }
    $finalBatch = @(Read-CompletedLogLines -Path $backlogPath -CommittedByteOffset ([ref]$largeCommitted) -PendingBytes ([ref]$largePending) -FlushIncompleteLine)
    if ($finalBatch.Count -ne 1 -or $finalBatch[0] -cne 'final-tail' -or $largeCommitted -ne $backlog.Length) {
        throw 'Final backlog drain did not flush only after all bytes beyond the 1 MiB poll limit were consumed.'
    }

    $truncatePath = Join-Path $readerRoot 'truncated-runner.stdout.log'
    [IO.File]::WriteAllText($truncatePath, "committed`npending", [Text.UTF8Encoding]::new($false))
    $truncateCommitted = 0L
    $truncatePending = [byte[]]::new(0)
    $null = @(Read-CompletedLogLines -Path $truncatePath -CommittedByteOffset ([ref]$truncateCommitted) -PendingBytes ([ref]$truncatePending))
    $truncateStream = [IO.File]::Open($truncatePath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try { $truncateStream.SetLength($truncateCommitted + $truncatePending.Length - 1) }
    finally { $truncateStream.Dispose() }
    $truncationRejected = $false
    try { $null = @(Read-CompletedLogLines -Path $truncatePath -CommittedByteOffset ([ref]$truncateCommitted) -PendingBytes ([ref]$truncatePending)) }
    catch { $truncationRejected = $_.Exception.Message.Contains('truncated', [StringComparison]::OrdinalIgnoreCase) }
    if (-not $truncationRejected) { throw 'The incremental reader silently accepted a log truncated behind its committed/pending cursor.' }

    $oversizedLinePath = Join-Path $readerRoot 'oversized-line.stdout.log'
    [IO.File]::WriteAllBytes($oversizedLinePath, [byte[]]::new(1048577))
    $oversizedRejected = $false
    $oversizedCommitted = 0L
    $oversizedPending = [byte[]]::new(0)
    try {
        $null = @(Read-CompletedLogLines -Path $oversizedLinePath -CommittedByteOffset ([ref]$oversizedCommitted) -PendingBytes ([ref]$oversizedPending))
        $null = @(Read-CompletedLogLines -Path $oversizedLinePath -CommittedByteOffset ([ref]$oversizedCommitted) -PendingBytes ([ref]$oversizedPending))
    }
    catch { $oversizedRejected = $_.Exception.Message.Contains('1 MiB progress bound', [StringComparison]::Ordinal) }
    if (-not $oversizedRejected) { throw 'The progress reader accepted an unbounded unterminated line.' }

    $tailPath = Join-Path $readerRoot 'large-runner-diagnostic-tail.log'
    $tailContent = 'prefix-start-' + ('x' * 40000) + "`n" + ('middle-' * 4000) + "`ntail-sentinel"
    [IO.File]::WriteAllText($tailPath, $tailContent, [Text.UTF8Encoding]::new($false))
    $boundedTail = Get-BoundedLogTail -Path $tailPath
    if ($boundedTail.TotalBytes -le 32768 -or $boundedTail.BytesRead -ne 32768 -or
        $boundedTail.Text.Length -gt 32768 -or $boundedTail.Text.Contains('prefix-start-', [StringComparison]::Ordinal) -or
        -not $boundedTail.Text.Contains('tail-sentinel', [StringComparison]::Ordinal)) {
        throw 'Final runner diagnostics must read at most the fixed byte tail and retain the latest complete output.'
    }

    $ownedChild = Start-Process -FilePath $env:ComSpec -ArgumentList '/d /c ping -n 10 127.0.0.1 > nul' -PassThru -WindowStyle Hidden
    $unrelatedChild = Start-Process -FilePath $env:ComSpec -ArgumentList '/d /c ping -n 10 127.0.0.1 > nul' -PassThru -WindowStyle Hidden
    try {
        if (-not (Stop-OwnedRunnerTree -OwnedProcess $ownedChild)) { throw 'The owned runner process tree did not stop after the monitor failure cleanup.' }
        $unrelatedChild.Refresh()
        if ($unrelatedChild.HasExited) { throw 'Owned runner cleanup terminated a different, unrelated child process.' }
    }
    finally {
        $null = Stop-OwnedRunnerTree -OwnedProcess $ownedChild
        $null = Stop-OwnedRunnerTree -OwnedProcess $unrelatedChild
    }
}
finally { Remove-OwnedTestTempRoot -Path $readerRoot -NamePrefix 'offline-log-reader' }

$runtimeFunction = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-GuestWindowsAppRuntimePreflightScript' }, $true)
if ($null -eq $runtimeFunction) { throw 'Pinned Windows App Runtime guest preflight generator is missing.' }
Invoke-Expression $runtimeFunction.Extent.Text
$runtimeInvoker = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-SandboxWindowsAppRuntimePreflight' }, $true)
if ($null -eq $runtimeInvoker -or
    -not $runtimeInvoker.Extent.Text.Contains("'-ExecutionPolicy', 'Bypass', '-EncodedCommand'", [StringComparison]::Ordinal)) {
    throw 'Runtime preflight must invoke the staged installer in process-scoped Bypass PowerShell because guest policy is restrictive.'
}
$runtimeRoot = Join-Path ([IO.Path]::GetTempPath()) ('offline-runtime-preflight-' + [Guid]::NewGuid().ToString('N'))
$incompleteRoot = $null
[IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
try {
    $helperRelative = 'eng\readme-audit\Install-PinnedWindowsAppRuntime.ps1'
    $helperFixturePath = Join-Path $runtimeRoot $helperRelative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($helperFixturePath)) | Out-Null
    $markerPath = Join-Path $runtimeRoot 'installer-helper-invoked.marker'
    $helperFixture = "[IO.File]::WriteAllText('$($markerPath.Replace("'", "''"))', 'invoked')`n"
    [IO.File]::WriteAllText($helperFixturePath, $helperFixture, [Text.UTF8Encoding]::new($false))
    $helperSha = (Get-FileHash -LiteralPath $helperFixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $runtimeGuestScript = New-GuestWindowsAppRuntimePreflightScript -GuestRepoAbsolute $runtimeRoot -ExpectedHelperSha256 $helperSha
    $runtimePackages = @(
        [pscustomobject]@{ Name = 'Microsoft.WindowsAppRuntime.1.8'; Version = '8000.921.1539.0'; Architecture = 'X64'; PackageFamilyName = 'Microsoft.WindowsAppRuntime.1.8_8wekyb3d8bbwe'; PackageFullName = 'Microsoft.WindowsAppRuntime.1.8_8000.921.1539.0_x64__8wekyb3d8bbwe'; Status = 'Ok' },
        [pscustomobject]@{ Name = 'MicrosoftCorporationII.WinAppRuntime.Main.1.8'; Version = '8000.921.1539.0'; Architecture = 'X64'; PackageFamilyName = 'MicrosoftCorporationII.WinAppRuntime.Main.1.8_8wekyb3d8bbwe'; PackageFullName = 'main'; Status = 'Ok' },
        [pscustomobject]@{ Name = 'MicrosoftCorporationII.WinAppRuntime.Singleton'; Version = '8000.921.1539.0'; Architecture = 'X64'; PackageFamilyName = 'MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe'; PackageFullName = 'singleton'; Status = 'Ok' },
        [pscustomobject]@{ Name = 'Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6'; Version = '8000.921.1539.0'; Architecture = 'X64'; PackageFamilyName = 'Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8wekyb3d8bbwe'; PackageFullName = 'ddlm'; Status = 'Ok' })
    $packagesJsonPath = Join-Path $runtimeRoot 'packages.json'
    [IO.File]::WriteAllText($packagesJsonPath, (ConvertTo-Json -InputObject $runtimePackages -Depth 4), [Text.UTF8Encoding]::new($false))
    $packagesJsonPathLiteral = $packagesJsonPath.Replace("'", "''")
    $runtimePrelude = "function Get-AppxPackage { `$items = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText('$packagesJsonPathLiteral')); foreach (`$item in @(`$items)) { Write-Output `$item } }`n"
    $reuseScriptPath = Join-Path $runtimeRoot 'runtime-reuse.ps1'
    [IO.File]::WriteAllText($reuseScriptPath, $runtimePrelude + $runtimeGuestScript, [Text.UTF8Encoding]::new($false))
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $reuseScriptPath
    $reuseReportPath = Join-Path $runtimeRoot 'artifacts\offline-same-byte-runtime-setup\windows-app-runtime.json'
    $reuseReport = Get-Content -LiteralPath $reuseReportPath -Raw | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $markerPath)) {
        throw "A complete healthy current-user x64 runtime should be reused without invoking the pinned installer helper. Report: $($reuseReport | ConvertTo-Json -Depth 8 -Compress)"
    }
    if ($reuseReport.status -cne 'ready' -or $reuseReport.action -cne 'reused-current-user-runtime' -or
        $reuseReport.requiredPackageFamilies.Count -ne 4 -or $reuseReport.inventoryAfter.missingPackageFamilies.Count -ne 0) {
        throw 'Runtime preflight did not report the full healthy current-user package-family set for reuse.'
    }

    $incompleteRoot = Join-Path ([IO.Path]::GetTempPath()) ('offline-runtime-incomplete-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($incompleteRoot) | Out-Null
    $incompleteMarker = Join-Path $incompleteRoot 'installer-helper-invoked.marker'
    $incompleteHelper = Join-Path $incompleteRoot $helperRelative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($incompleteHelper)) | Out-Null
    [IO.File]::WriteAllText($incompleteHelper, "[IO.File]::WriteAllText('$($incompleteMarker.Replace("'", "''"))', 'invoked')`n", [Text.UTF8Encoding]::new($false))
    $incompleteHelperSha = (Get-FileHash -LiteralPath $incompleteHelper -Algorithm SHA256).Hash.ToLowerInvariant()
    $incompleteGuestScript = New-GuestWindowsAppRuntimePreflightScript -GuestRepoAbsolute $incompleteRoot -ExpectedHelperSha256 $incompleteHelperSha
    $unhealthySingleton = @($runtimePackages | Where-Object { $_.PackageFamilyName -ceq 'MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe' })[0]
    $unhealthySingleton.Status = 'Error'
    $incompletePackages = @($runtimePackages | Where-Object { $_.PackageFamilyName -cne 'Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8wekyb3d8bbwe' })
    [IO.File]::WriteAllText($packagesJsonPath, (ConvertTo-Json -InputObject $incompletePackages -Depth 4), [Text.UTF8Encoding]::new($false))
    $incompleteRuntimePrelude = "function Get-AppxPackage { `$items = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText('$packagesJsonPathLiteral')); foreach (`$item in @(`$items)) { Write-Output `$item } }`n"
    $installAttemptScriptPath = Join-Path $incompleteRoot 'runtime-install-attempt.ps1'
    [IO.File]::WriteAllText($installAttemptScriptPath, $incompleteRuntimePrelude + $incompleteGuestScript, [Text.UTF8Encoding]::new($false))
    $LASTEXITCODE = 0
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installAttemptScriptPath 2>$null
    $installAttemptExitCode = $LASTEXITCODE
    $incompleteReportPath = Join-Path $incompleteRoot 'artifacts\offline-same-byte-runtime-setup\windows-app-runtime.json'
    $installFailureReport = Get-Content -LiteralPath $incompleteReportPath -Raw | ConvertFrom-Json
    if ($installAttemptExitCode -eq 0 -or
        -not (Test-Path -LiteralPath $incompleteMarker) -or
        $installFailureReport.status -cne 'failed' -or
        @($installFailureReport.inventoryBefore.missingPackageFamilies) -notcontains 'MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe' -or
        @($installFailureReport.inventoryBefore.missingPackageFamilies) -notcontains 'Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8wekyb3d8bbwe') {
        throw ("Missing or unhealthy runtime families must attempt only the pinned helper, return a nonzero process exit, and fail closed in the report when the verified installer is unavailable. exit=$installAttemptExitCode marker=$([bool](Test-Path -LiteralPath $incompleteMarker)) status=$($installFailureReport.status) missing=$(@($installFailureReport.inventoryBefore.missingPackageFamilies) -join ',')")
    }

    $helperSource = Get-Content -LiteralPath (Join-Path $repoRoot $helperRelative) -Raw
    if (-not $helperSource.Contains('-UseBasicParsing', [StringComparison]::Ordinal) -or
        -not $helperSource.Contains("`$ProgressPreference = 'SilentlyContinue'", [StringComparison]::Ordinal) -or
        -not $helperSource.Contains("Where-Object Status -eq 'Ok'", [StringComparison]::Ordinal) -or
        -not $helperSource.Contains('if ($null -eq $rawExitCode)', [StringComparison]::Ordinal) -or
        -not $helperSource.Contains('MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe', [StringComparison]::Ordinal)) {
        throw 'Pinned runtime helper must be PS5-safe, reject unhealthy packages, and fail closed on a missing fast-child exit code.'
    }
    if (-not $runtimeGuestScript.Contains("`$ProgressPreference = 'SilentlyContinue'", [StringComparison]::Ordinal)) {
        throw 'Guest runtime preflight must suppress only native download progress chatter on its own process.'
    }
}
finally {
    Remove-OwnedTestTempRoot -Path $runtimeRoot -NamePrefix 'offline-runtime-preflight'
    if ($null -ne $incompleteRoot) { Remove-OwnedTestTempRoot -Path $incompleteRoot -NamePrefix 'offline-runtime-incomplete' }
}

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) { throw 'Windows PowerShell 5.1 is required for the redirected-child exit-code regression.' }
$argumentProbeRoot = Join-Path ([IO.Path]::GetTempPath()) ('offline-child-arguments-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($argumentProbeRoot) | Out-Null
try {
    $argumentEchoScript = Join-Path $argumentProbeRoot 'echo-arguments.ps1'
    $argumentJsonPath = Join-Path $argumentProbeRoot 'arguments.json'
    $argumentStdoutPath = Join-Path $argumentProbeRoot 'child.stdout.log'
    $argumentStderrPath = Join-Path $argumentProbeRoot 'child.stderr.log'
    [IO.File]::WriteAllText($argumentEchoScript, '[IO.File]::WriteAllText($env:ARGUMENT_ECHO_OUTPUT, (ConvertTo-Json -InputObject @($args) -Compress))' + "`n", [Text.UTF8Encoding]::new($false))
    $expectedChildArguments = @(
        '--app=C:\Program Files\JitHub\JitHub.WinUI.exe',
        "--node=C:\Users\O'Connor\Tools\node.exe",
        '--edge=C:\Program Files (x86)\Microsoft\Edge\msedge.exe',
        '--tail=C:\Program Files\Tail\',
        'quote="value"')
    $windowsPowerShellArguments = @('-NoProfile', '-NonInteractive', '-File', $argumentEchoScript) + $expectedChildArguments
    $windowsPowerShellArgumentLine = ($windowsPowerShellArguments | ForEach-Object {
        ConvertTo-WindowsCommandLineArgument -Argument $_
    }) -join ' '
    $priorArgumentEchoOutput = [Environment]::GetEnvironmentVariable('ARGUMENT_ECHO_OUTPUT', 'Process')
    [Environment]::SetEnvironmentVariable('ARGUMENT_ECHO_OUTPUT', $argumentJsonPath, 'Process')
    try {
        $redirectedChild = Start-Process -FilePath $windowsPowerShell -ArgumentList $windowsPowerShellArgumentLine `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput $argumentStdoutPath -RedirectStandardError $argumentStderrPath
        $redirectedChildHandle = $redirectedChild.SafeHandle
        if ($null -eq $redirectedChildHandle -or $redirectedChildHandle.IsInvalid) {
            throw 'The redirected Windows PowerShell child did not expose its original process handle.'
        }
        while (-not $redirectedChild.HasExited) {
            Start-Sleep -Milliseconds 10
            $redirectedChild.Refresh()
        }
        $redirectedChild.WaitForExit()
        $redirectedChild.Refresh()
        if ($null -eq $redirectedChild.ExitCode -or [int]$redirectedChild.ExitCode -ne 0) {
            throw "Redirected child argument echo failed or lost its exit code: '$($redirectedChild.ExitCode)' $(Get-Content -LiteralPath $argumentStderrPath -Raw -ErrorAction SilentlyContinue)"
        }
        $actualChildArguments = @(Get-Content -LiteralPath $argumentJsonPath -Raw | ConvertFrom-Json)
        if ($actualChildArguments.Count -ne $expectedChildArguments.Count) {
            throw 'Windows command-line quoting changed the argument count for paths with spaces, quotes, or trailing backslashes.'
        }
        for ($index = 0; $index -lt $expectedChildArguments.Count; $index++) {
            if ([string]$actualChildArguments[$index] -cne [string]$expectedChildArguments[$index]) {
                throw "Windows command-line quoting changed argument $index from '$($expectedChildArguments[$index])' to '$($actualChildArguments[$index])'."
            }
        }
    }
    finally { [Environment]::SetEnvironmentVariable('ARGUMENT_ECHO_OUTPUT', $priorArgumentEchoOutput, 'Process') }
}
finally { Remove-OwnedTestTempRoot -Path $argumentProbeRoot -NamePrefix 'offline-child-arguments' }

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
Write-Host 'PASS: generated guest paths survive apostrophes, child arguments preserve Windows argv across spaces/quotes, and redirected runner exit codes remain observable.'
Write-Host 'PASS: generated shard scripts are pushed as files, keeping the PowerShell command line below the Windows limit.'
