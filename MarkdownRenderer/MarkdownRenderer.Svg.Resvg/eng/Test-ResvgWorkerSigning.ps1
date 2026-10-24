$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$providerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptPath = Join-Path $PSScriptRoot 'Sign-ResvgWorkers.ps1'
$evidenceVerifierPath = Join-Path $PSScriptRoot 'Verify-ResvgWorkerSigningEvidence.ps1'
$releaseRunnerPath = [IO.Path]::GetFullPath((Join-Path $providerRoot '..\eng\Invoke-SvgResvgReleaseEvidence.ps1'))
$workerName = 'MarkdownRenderer.Svg.Resvg.Worker.exe'
$rids = @('win-x86', 'win-x64', 'win-arm64')
$machines = @{ 'win-x86' = 'x86'; 'win-x64' = 'x64'; 'win-arm64' = 'arm64' }
$sourceHashes = @{}
foreach ($rid in $rids) {
    $sourcePath = Join-Path $providerRoot "runtimes\$rid\native\$workerName"
    $sourceHashes[$rid] = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('resvg-worker-signing-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $signer = '0000000000000000000000000000000000000000'
    $timestamp = 'https://timestamp.invalid.example/'
    $missingCertificate = Join-Path $testRoot 'missing-certificate.pfx'
    $newOutput = Join-Path $testRoot 'signed-workers'

    $threw = $false
    try {
        & $scriptPath `
            -InputDirectory $providerRoot `
            -OutputDirectory $newOutput `
            -CertificatePath $missingCertificate `
            -ExpectedSignerThumbprint $signer `
            -TimestampServer $timestamp
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike '*certificate file does not exist*') {
            throw "Missing-certificate test failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $threw -or (Test-Path -LiteralPath $newOutput)) {
        throw 'Missing signer certificate must fail before producing any staged worker output.'
    }

    $missingStoreSignerOutput = Join-Path $testRoot 'missing-store-signer-output'
    $threw = $false
    try {
        & $scriptPath `
            -InputDirectory $providerRoot `
            -OutputDirectory $missingStoreSignerOutput `
            -CertificateStore 'CurrentUser\My' `
            -ExpectedSignerThumbprint $signer `
            -TimestampServer $timestamp
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike '*was not found exactly once in*') {
            throw "Missing-store-signer test failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $threw -or (Test-Path -LiteralPath $missingStoreSignerOutput)) {
        throw 'An absent exact store thumbprint must fail before creating the signed output directory.'
    }
    $unexpectedStages = @(Get-ChildItem -LiteralPath $testRoot -Directory -Force | Where-Object { $_.Name -like '.resvg-workers-stage-*' })
    if ($unexpectedStages.Count -ne 0) {
        throw 'Signer preflight failure left an unexpected temporary signing stage.'
    }

    $existingOutput = Join-Path $testRoot 'existing-output'
    [IO.Directory]::CreateDirectory($existingOutput) | Out-Null
    $markerPath = Join-Path $existingOutput 'preserve-me.txt'
    [IO.File]::WriteAllText($markerPath, 'caller-owned')
    $threw = $false
    try {
        & $scriptPath `
            -InputDirectory $providerRoot `
            -OutputDirectory $existingOutput `
            -CertificatePath $missingCertificate `
            -ExpectedSignerThumbprint $signer `
            -TimestampServer $timestamp
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike '*already exists*') {
            throw "Existing-output test failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $threw -or
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
        (Get-Content -LiteralPath $markerPath -Raw) -cne 'caller-owned') {
        throw 'Signing must refuse an existing output directory without modifying its contents.'
    }

    $fakeSignedRoot = Join-Path $testRoot 'fake-signed-workers'
    $fakeRecords = [Collections.Generic.List[object]]::new()
    foreach ($rid in $rids) {
        $relativePath = "runtimes\$rid\native\$workerName"
        $sourcePath = Join-Path $providerRoot $relativePath
        $fakePath = Join-Path $fakeSignedRoot $relativePath
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fakePath)) | Out-Null
        [IO.File]::Copy($sourcePath, $fakePath)
        $item = Get-Item -LiteralPath $fakePath
        $hash = (Get-FileHash -LiteralPath $fakePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $fakeRecords.Add([ordered]@{
            rid = $rid
            machine = $machines[$rid]
            bytes = [long]$item.Length
            sourceBytes = [long](Get-Item -LiteralPath $sourcePath).Length
            sourceSha256 = $hash
            signedSha256 = $hash
            signatureStatus = 'Valid'
            signerThumbprint = $signer
            signerSubject = 'Test evidence only; no certificate was created.'
            timestampSubject = 'Test evidence only; no timestamp was created.'
        })
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        signerSource = 'certificate-store'
        certificateStore = 'CurrentUser\My'
        expectedSignerThumbprint = $signer
        timestampServer = $timestamp
        timestampRequired = $true
        hashAlgorithm = 'SHA256'
        workers = @($fakeRecords)
    }
    $firstRecord = $manifest.workers[0]
    $firstRecordPath = "runtimes\$($firstRecord.rid)\native\$workerName"
    $firstRecord.signedSha256 = '0000000000000000000000000000000000000000000000000000000000000000'
    [IO.File]::WriteAllText(
        (Join-Path $fakeSignedRoot 'worker-signing-evidence.json'),
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    $threw = $false
    try {
        & $evidenceVerifierPath -WorkerRoot $fakeSignedRoot -UnsignedWorkerRoot $providerRoot -ExpectedSignerThumbprint $signer -ExpectedTimestampServer $timestamp
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike '*differs from the recorded architecture, length, hash*') {
            throw "Tampered-hash evidence test failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $threw) {
        throw 'Signing evidence with a mismatched worker hash must fail closed.'
    }

    $firstRecord.signedSha256 = $firstRecord.sourceSha256
    $firstRecord.sourceSha256 = '0000000000000000000000000000000000000000000000000000000000000000'
    [IO.File]::WriteAllText(
        (Join-Path $fakeSignedRoot 'worker-signing-evidence.json'),
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    $threw = $false
    try {
        & $evidenceVerifierPath -WorkerRoot $fakeSignedRoot -UnsignedWorkerRoot $providerRoot -ExpectedSignerThumbprint $signer -ExpectedTimestampServer $timestamp
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike '*unsigned source differs*') {
            throw "Source-provenance binding test failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $threw) {
        throw 'A self-authored manifest cannot pair signed workers with different current unsigned source bytes.'
    }

    $firstRecord.sourceSha256 = (Get-FileHash -LiteralPath (Join-Path $providerRoot $firstRecordPath) -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        (Join-Path $fakeSignedRoot 'worker-signing-evidence.json'),
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    $threw = $false
    try {
        & $evidenceVerifierPath -WorkerRoot $fakeSignedRoot -UnsignedWorkerRoot $providerRoot -ExpectedSignerThumbprint $signer -ExpectedTimestampServer $timestamp
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike '*does not contain a terminal appended Certificate Table*') {
            throw "Unsigned worker evidence test failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $threw) {
        throw 'A manifest cannot make unsigned worker bytes pass the Authenticode trust gate.'
    }

    . (Join-Path $PSScriptRoot 'ResvgAuthenticodeImage.ps1')
    $sourceForPreimage = Join-Path $providerRoot $firstRecordPath
    $syntheticSigned = Join-Path $testRoot 'synthetic-signed-image.exe'
    $preimageBytes = [IO.File]::ReadAllBytes($sourceForPreimage)
    $layout = Get-ResvgPeSigningLayout $preimageBytes
    $certificateOffset = [int](($preimageBytes.Length + 7) -band (-bnot 7))
    $certificateSize = 16
    $syntheticBytes = [byte[]]::new($certificateOffset + $certificateSize)
    [Array]::Copy($preimageBytes, $syntheticBytes, $preimageBytes.Length)
    [Array]::Copy([BitConverter]::GetBytes([uint32]0x12345678), 0, $syntheticBytes, $layout.ChecksumOffset, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]$certificateOffset), 0, $syntheticBytes, $layout.SecurityDirectoryOffset, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]$certificateSize), 0, $syntheticBytes, $layout.SecurityDirectoryOffset + 4, 4)
    [IO.File]::WriteAllBytes($syntheticSigned, $syntheticBytes)
    if (-not (Test-ResvgPePreimage $sourceForPreimage $syntheticSigned)) {
        throw 'PE preimage comparison did not accept the checksum/security-directory/certificate-only transformation.'
    }
    $syntheticBytes[0x200] = $syntheticBytes[0x200] -bxor 0x01
    [IO.File]::WriteAllBytes($syntheticSigned, $syntheticBytes)
    $threw = $false
    try {
        $null = Test-ResvgPePreimage $sourceForPreimage $syntheticSigned
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike '*differs from unsigned source outside Authenticode-excluded PE fields*') {
            throw "Signed-image-content binding test failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $threw) {
        throw 'PE preimage comparison must reject changes outside documented Authenticode exclusions.'
    }

    $releaseOutput = Join-Path $testRoot 'release-without-signed-workers'
    $threw = $false
    try {
        & $releaseRunnerPath -Mode Release -OutputDirectory $releaseOutput
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike '*requires a separately staged, signed all-RID worker directory*') {
            throw "Release-mode signing preflight test failed for an unexpected reason: $($_.Exception.Message)"
        }
    }
    if (-not $threw -or (Test-Path -LiteralPath $releaseOutput)) {
        throw 'Release mode must fail before creating run artifacts when signed worker inputs are absent.'
    }

    foreach ($rid in $rids) {
        $sourcePath = Join-Path $providerRoot "runtimes\$rid\native\$workerName"
        $currentHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        if ($currentHash -cne $sourceHashes[$rid]) {
            throw "Signing preflight changed source worker '$rid'."
        }
    }
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTestRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTestRoot) -notmatch '^resvg-worker-signing-test-[0-9a-f]{32}$') {
        throw 'Refusing to remove a signing test directory outside its generated temporary path.'
    }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
}

Write-Output 'Resvg worker signing preflight, exact-store-signer fail-closed, no-overwrite, source-hash binding, PE preimage integrity, unsigned/tampered-evidence rejection, and release-mode fail-closed checks passed.'
