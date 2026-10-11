$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ResvgWorkerIdentity.psm1') -Force

$providerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $providerRoot '..\..'))
$verifier = Join-Path $PSScriptRoot 'Verify-ResvgWorkerIdentity.ps1'
$identityModule = Join-Path $PSScriptRoot 'ResvgWorkerIdentity.psm1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("resvg-worker-identity-{0}" -f [guid]::NewGuid().ToString('N'))
$workerRoot = Join-Path $tempRoot 'fixture-root'
$checks = 0

function Write-IdentityFixture([object] $Manifest) {
    [IO.Directory]::CreateDirectory($workerRoot) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $workerRoot 'RESVG_WORKER_IDENTITY.json'),
        (($Manifest | ConvertTo-Json -Depth 12) + "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Copy-IdentityFixtureWorkers {
    foreach ($rid in (Get-ResvgWorkerIdentityRids).Keys) {
        $relative = "runtimes\$rid\native\MarkdownRenderer.Svg.Resvg.Worker.exe"
        $source = Join-Path $providerRoot $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Identity fixture requires the current worker at '$relative'."
        }
        $destination = Join-Path $workerRoot $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($source, $destination, $true)
    }
}

function Assert-IdentityRejected([string] $Name, [scriptblock] $Mutate) {
    $candidate = $script:baseline | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12
    & $Mutate $candidate
    Write-IdentityFixture $candidate
    $rejected = $false
    try {
        & $verifier -WorkerRoot $workerRoot -Rid 'win-x64' | Out-Null
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Worker identity verifier accepted invalid fixture '$Name'." }
    $script:checks++
}

function Write-SourceFixtureText([string] $Root, [string] $RelativePath, [string] $Text) {
    $path = Join-Path $Root $RelativePath.Replace('/', '\')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text, [Text.UTF8Encoding]::new($false))
}

function Write-SourceFixtureBytes([string] $Root, [string] $RelativePath, [byte[]] $Bytes) {
    $path = Join-Path $Root $RelativePath.Replace('/', '\')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllBytes($path, $Bytes)
}

try {
    if (-not (Test-Path -LiteralPath $providerRoot -PathType Container) -or
        -not [IO.Path]::GetFullPath($providerRoot).StartsWith(
            [IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\') + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Identity test root is outside the expected repository provider.'
    }
    Copy-IdentityFixtureWorkers
    $protocol = Get-ResvgWorkerProtocolVersions $providerRoot
    $sourceFingerprint = Get-ResvgWorkerSourceFingerprint $providerRoot
    $rustcCommit = '1111111111111111111111111111111111111111'
    $records = [Collections.Generic.List[object]]::new()
    foreach ($rid in (Get-ResvgWorkerIdentityRids).Keys) {
        $expected = (Get-ResvgWorkerIdentityRids)[$rid]
        $relative = "runtimes\$rid\native\MarkdownRenderer.Svg.Resvg.Worker.exe"
        $image = Get-ResvgWorkerImageInfo (Join-Path $workerRoot $relative)
        if ($image.machine -cne [string]$expected.machine) {
            throw "Current worker fixture '$rid' has unexpected PE architecture '$($image.machine)'."
        }
        $records.Add([ordered]@{
            rid = $rid
            target = [string]$expected.target
            machine = $image.machine
            bytes = [long]$image.bytes
            sha256 = $image.sha256
            normalizedSha256 = $image.normalizedSha256
            sourceFingerprint = $sourceFingerprint
            buildFingerprint = Get-ResvgWorkerBuildFingerprint $sourceFingerprint ([string]$expected.target) $rustcCommit
            rustcCommit = $rustcCommit
            hostProtocolVersion = $protocol.Host
            nativeProtocolVersion = $protocol.Native
            buildOutputBytes = [long]$image.bytes
            buildOutputSha256 = $image.sha256
            buildOutputNormalizedSha256 = $image.normalizedSha256
        })
    }
    $script:baseline = [ordered]@{
        schemaVersion = 1
        workerFileName = 'MarkdownRenderer.Svg.Resvg.Worker.exe'
        workers = @($records)
    }
    Write-IdentityFixture $baseline
    foreach ($rid in (Get-ResvgWorkerIdentityRids).Keys) {
        & $verifier -WorkerRoot $workerRoot -Rid $rid | Out-Null
        $checks++
    }

    Assert-IdentityRejected 'missing RID' {
        param($value) $value.workers = @($value.workers | Select-Object -First 2)
    }
    Assert-IdentityRejected 'duplicate RID' {
        param($value) $value.workers[2].rid = 'win-x86'
    }
    Assert-IdentityRejected 'RID case alias' {
        param($value) $value.workers[2].rid = 'WIN-X64'
    }
    Assert-IdentityRejected 'stale nonselected RID source fingerprint' {
        param($value) $value.workers[0].sourceFingerprint = 'f' * 64
    }
    Assert-IdentityRejected 'host protocol mismatch' {
        param($value) $value.workers[0].hostProtocolVersion++
    }
    Assert-IdentityRejected 'native protocol mismatch' {
        param($value) $value.workers[0].nativeProtocolVersion++
    }
    Assert-IdentityRejected 'host/native protocol disagreement' {
        param($value) $value.workers[1].nativeProtocolVersion = $value.workers[1].hostProtocolVersion + 1
    }
    Assert-IdentityRejected 'build fingerprint mismatch' {
        param($value) $value.workers[0].buildFingerprint = 'e' * 64
    }
    Assert-IdentityRejected 'mixed compiler toolchains' {
        param($value)
        $value.workers[0].rustcCommit = '2222222222222222222222222222222222222222'
        $value.workers[0].buildFingerprint = Get-ResvgWorkerBuildFingerprint `
            ([string]$value.workers[0].sourceFingerprint) `
            ([string]$value.workers[0].target) `
            ([string]$value.workers[0].rustcCommit)
    }
    Assert-IdentityRejected 'binary hash mismatch' {
        param($value) $value.workers[1].sha256 = 'd' * 64
    }
    Assert-IdentityRejected 'PE architecture mismatch' {
        param($value) $value.workers[1].machine = 'arm64'
    }
    Assert-IdentityRejected 'reproducible comparison mismatch' {
        param($value) $value.workers[1].buildOutputNormalizedSha256 = 'c' * 64
    }

    $tamperedPath = Join-Path $workerRoot 'runtimes\win-x86\native\MarkdownRenderer.Svg.Resvg.Worker.exe'
    [byte[]]$tampered = [IO.File]::ReadAllBytes($tamperedPath)
    $tampered[$tampered.Length - 1] = $tampered[$tampered.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($tamperedPath, $tampered)
    Write-IdentityFixture $baseline
    $tamperRejected = $false
    try { & $verifier -WorkerRoot $workerRoot -Rid 'win-x64' | Out-Null }
    catch { $tamperRejected = $true }
    if (-not $tamperRejected) { throw 'Worker identity verifier accepted a nonselected RID payload-byte mutation.' }
    $checks++

    $sourceFixture = Join-Path $tempRoot 'source-fixture'
    [IO.Directory]::CreateDirectory($sourceFixture) | Out-Null
    foreach ($relative in @(
        'Internal/WorkerProtocol.cs',
        'NATIVE_RUST_DEPENDENCIES.json',
        'RESVG_PROVENANCE.json',
        'eng/Build-ResvgWorker.ps1',
        'eng/Compare-ResvgWorkerBinary.ps1',
        'eng/Generate-ResvgSupplyChain.ps1',
        'eng/ResvgWorkerIdentity.psm1',
        'eng/Update-ResvgWorkerIdentity.ps1',
        'eng/Verify-ResvgWorkerIdentity.ps1',
        'eng/ResvgVendoredSources.ps1',
        'eng/Verify-ResvgSupplyChain.ps1',
        'eng/SpdxLicenseSelection.ps1',
        'native/src/main.rs',
        'native/src/input.rs'
    )) {
        Write-SourceFixtureText $sourceFixture $relative "pinned:$relative`n"
    }
    $sourceInitial = Get-ResvgWorkerSourceFingerprint $sourceFixture

    foreach ($relative in @(
        'native/src/main.rs',
        'NATIVE_RUST_DEPENDENCIES.json',
        'eng/ResvgWorkerIdentity.psm1',
        'eng/Build-ResvgWorker.ps1'
    )) {
        [byte[]]$lfBytes = [Text.Encoding]::UTF8.GetBytes("pinned:$relative`n")
        [byte[]]$crlfBytes = [Text.Encoding]::UTF8.GetBytes("pinned:$relative`r`n")
        Write-SourceFixtureBytes $sourceFixture $relative $crlfBytes
        if ((Get-ResvgWorkerSourceFingerprint $sourceFixture) -cne $sourceInitial) {
            throw "CRLF checkout conversion changed the worker source fingerprint for '$relative'."
        }
        Write-SourceFixtureBytes $sourceFixture $relative $lfBytes
    }
    $checks++

    Write-SourceFixtureBytes $sourceFixture 'native/src/input.rs' ([Text.Encoding]::UTF8.GetBytes("line`r"))
    $loneCrFingerprint = Get-ResvgWorkerSourceFingerprint $sourceFixture
    Write-SourceFixtureBytes $sourceFixture 'native/src/input.rs' ([Text.Encoding]::UTF8.GetBytes("line`n"))
    if ((Get-ResvgWorkerSourceFingerprint $sourceFixture) -ceq $loneCrFingerprint) {
        throw 'A lone CR byte in a text input was incorrectly normalized as a Git CRLF conversion.'
    }
    Write-SourceFixtureText $sourceFixture 'native/src/input.rs' "pinned:native/src/input.rs`n"
    $checks++

    $binaryRelative = 'native/assets/fixture.png'
    [byte[]]$binaryCrLf = @(0x89, 0x0d, 0x0a, 0x00, 0xff)
    Write-SourceFixtureBytes $sourceFixture $binaryRelative $binaryCrLf
    $binaryInitial = Get-ResvgWorkerSourceFingerprint $sourceFixture
    [byte[]]$binaryChanged = @(0x89, 0x0a, 0x0d, 0x00, 0xff)
    Write-SourceFixtureBytes $sourceFixture $binaryRelative $binaryChanged
    if ((Get-ResvgWorkerSourceFingerprint $sourceFixture) -ceq $binaryInitial) {
        throw 'A binary asset byte change was incorrectly normalized as a line-ending conversion.'
    }
    $checks++
    Remove-Item -LiteralPath (Join-Path $sourceFixture 'native/assets/fixture.png') -Force

    Write-SourceFixtureText $sourceFixture 'native/src/input.rs' "changed native input`n"
    $sourceChanged = Get-ResvgWorkerSourceFingerprint $sourceFixture
    if ($sourceInitial -ceq $sourceChanged) { throw 'Native input changes did not invalidate the worker source fingerprint.' }
    $checks++
    Write-SourceFixtureText $sourceFixture 'native/src/input.rs' "pinned:native/src/input.rs`n"
    Write-SourceFixtureText $sourceFixture 'eng/Build-ResvgWorker.ps1' 'changed build helper`n'
    $buildHelperChanged = Get-ResvgWorkerSourceFingerprint $sourceFixture
    if ($sourceInitial -ceq $buildHelperChanged) { throw 'Build helper changes did not invalidate the worker source fingerprint.' }
    $checks++
    Write-SourceFixtureText $sourceFixture 'eng/Build-ResvgWorker.ps1' "pinned:eng/Build-ResvgWorker.ps1`n"
    Write-SourceFixtureText $sourceFixture 'native/target/generated.rs' 'ignored target output`n'
    Write-SourceFixtureText $sourceFixture 'native/artifacts/old.log' 'ignored diagnostic output`n'
    if ((Get-ResvgWorkerSourceFingerprint $sourceFixture) -cne $sourceInitial) {
        throw 'Generated target/artifact files unexpectedly change the source fingerprint.'
    }
    $checks++

    $fixtureEngPath = Join-Path $sourceFixture 'eng'
    $fixtureEngTarget = Join-Path $sourceFixture 'eng-target'
    Move-Item -LiteralPath $fixtureEngPath -Destination $fixtureEngTarget
    $fixtureEngLinkCreated = $false
    try {
        try {
            [IO.Directory]::CreateSymbolicLink($fixtureEngPath, $fixtureEngTarget) | Out-Null
            $fixtureEngLinkCreated = $true
        }
        catch {
            try {
                New-Item -ItemType Junction -Path $fixtureEngPath -Target $fixtureEngTarget | Out-Null
                $fixtureEngLinkCreated = $true
            }
            catch {
                Write-Output 'Skipped fixed-input parent-reparse regression: this Windows account cannot create a directory link.'
            }
        }

        if ($fixtureEngLinkCreated) {
            $reparseRejected = $false
            try { Get-ResvgWorkerSourceFingerprint $sourceFixture | Out-Null }
            catch { $reparseRejected = $true }
            if (-not $reparseRejected) {
                throw 'Worker source fingerprint accepted a fixed input reached through an in-root parent junction.'
            }
            $checks++
        }
    }
    finally {
        if (Test-Path -LiteralPath $fixtureEngPath) {
            $linkAttributes = [IO.File]::GetAttributes($fixtureEngPath)
            if (($linkAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                [IO.Directory]::Delete($fixtureEngPath)
            }
            elseif (($linkAttributes -band [IO.FileAttributes]::Directory) -ne 0) {
                Remove-Item -LiteralPath $fixtureEngPath -Recurse -Force
            }
        }
        if (Test-Path -LiteralPath $fixtureEngTarget -PathType Container) {
            Move-Item -LiteralPath $fixtureEngTarget -Destination $fixtureEngPath
        }
    }

    [xml]$testProject = Get-Content -LiteralPath (Join-Path $providerRoot '..\MarkdownRenderer.Svg.Resvg.Tests\MarkdownRenderer.Svg.Resvg.Tests.csproj') -Raw
    $content = @($testProject.SelectNodes('//Content') | Where-Object { $_.GetAttribute('Include') -like '*MarkdownRenderer.Svg.Resvg.Worker.exe' })
    if ($content.Count -ne 1 -or
        $content[0].GetAttribute('Include') -notmatch '\$\(ResvgWorkerDirectory\).*\$\(ResvgWorkerRid\)' -or
        $content[0].GetAttribute('Include') -match 'win-x64') {
        throw 'The provider test project does not deploy the worker from the selected worker directory and RID.'
    }
    $checks++

    $testProjectPath = Join-Path $providerRoot '..\MarkdownRenderer.Svg.Resvg.Tests\MarkdownRenderer.Svg.Resvg.Tests.csproj'
    $customRid = 'win-arm64'
    $itemsJson = @(& dotnet msbuild $testProjectPath '-getItem:Content' "-p:ResvgWorkerDirectory=$workerRoot" "-p:ResvgWorkerRid=$customRid" '-nologo') -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'MSBuild could not evaluate the provider test project worker override.' }
    $evaluatedItems = $itemsJson | ConvertFrom-Json -Depth 12
    $selectedTestWorker = @($evaluatedItems.Items.Content | Where-Object { $_.Link -ceq 'MarkdownRenderer.Svg.Resvg.Worker.exe' })
    $expectedTestWorker = [IO.Path]::GetFullPath((Join-Path $workerRoot "runtimes\$customRid\native\MarkdownRenderer.Svg.Resvg.Worker.exe"))
    if ($selectedTestWorker.Count -ne 1 -or
        [IO.Path]::GetFullPath([string]$selectedTestWorker[0].Identity) -cne $expectedTestWorker) {
        throw 'The provider test project ignored the selected worker-directory or RID override.'
    }
    $checks++
    Write-Output "Passed $checks worker identity fixtures (three RID positives and stale, incomplete, protocol, PE, comparison, and payload tamper negatives)."
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTemp.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedTemp) -notmatch '^resvg-worker-identity-[0-9a-f]{32}$') {
            throw 'Refusing to remove an unexpected worker identity test directory.'
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
