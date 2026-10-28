$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ResvgVendoredSources.ps1')

foreach ($unsafePath in @('../escape', 'LICENSE:stream', '/absolute', 'src//duplicate')) {
    if (Test-ResvgPackageRelativePath -Path $unsafePath) {
        throw "The portable package-path validator accepted an unsafe or ambiguous path: $unsafePath."
    }
}
if (-not (Test-ResvgPackageRelativePath -Path 'tests/fonts/NotoSans-Regular.ttf')) {
    throw 'The portable package-path validator rejected a valid relative path.'
}

function Get-TestSha256([byte[]] $Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-TestUtf8Sha256([string] $Text) {
    Get-TestSha256 ([Text.UTF8Encoding]::new($false).GetBytes($Text))
}

function Write-TestUtf8([string] $Path, [string] $Text) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Assert-TestThrows([scriptblock] $Action, [string] $ExpectedMessage) {
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -like "*$ExpectedMessage*") {
            return
        }
        throw
    }
    throw "Expected provenance validation to reject: $ExpectedMessage"
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("resvg-source-provenance-{0}" -f [guid]::NewGuid().ToString('N'))
$providerRoot = Join-Path $testRoot 'provider'
$vendorRoot = Join-Path $providerRoot 'native/vendor/probe'
$manifestPath = Join-Path $vendorRoot 'UPSTREAM.json'
$supplementalManifestPath = Join-Path $vendorRoot 'SUPPLEMENTAL-TEST-FIXTURES.json'

$upstreamSourceText = "// pinned upstream source`n"
$upstreamCargoText = "[package]`nname = `"probe`"`nversion = `"1.0.0`"`n"
$upstreamLicenseText = "BSD-3-Clause test license text`n"
$addedSourceText = "// SPDX-License-Identifier: MIT`n// independently pinned local addition`n"
$addedLicenseText = "MIT License`n`nCopyright (c) JitHub contributors`n"
$fixtureLicenseText = "OFL test license text`n"
$fixtureFontBytes = [byte[]](0, 1, 2, 3, 255)

$sourcePath = 'src/lib.rs'
$cargoPath = 'Cargo.toml'
$licensePath = 'LICENSE'
$addedPath = 'src/scan/path_analytic.rs'
$addedLicensePath = 'LICENSE-JITHUB-MIT'
$fixtureLicensePath = 'tests/fonts/TEST-LICENSE-OFL.txt'
$fixturePath = 'tests/fonts/TEST-Font.ttf'

Write-TestUtf8 (Join-Path $vendorRoot $sourcePath) $upstreamSourceText
Write-TestUtf8 (Join-Path $vendorRoot $cargoPath) $upstreamCargoText
Write-TestUtf8 (Join-Path $vendorRoot $licensePath) $upstreamLicenseText
Write-TestUtf8 (Join-Path $vendorRoot $addedPath) $addedSourceText
Write-TestUtf8 (Join-Path $vendorRoot $addedLicensePath) $addedLicenseText
Write-TestUtf8 (Join-Path $vendorRoot $fixtureLicensePath) $fixtureLicenseText
$fixtureParent = Split-Path -Parent (Join-Path $vendorRoot $fixturePath)
if (-not (Test-Path -LiteralPath $fixtureParent -PathType Container)) {
    New-Item -ItemType Directory -Path $fixtureParent -Force | Out-Null
}
[IO.File]::WriteAllBytes((Join-Path $vendorRoot $fixturePath), $fixtureFontBytes)

$sourceEntries = @(
    [ordered]@{ path = $sourcePath; sha256 = Get-TestUtf8Sha256 $upstreamSourceText },
    [ordered]@{ path = $cargoPath; sha256 = Get-TestUtf8Sha256 $upstreamCargoText },
    [ordered]@{ path = $licensePath; sha256 = Get-TestUtf8Sha256 $upstreamLicenseText }
)
$sourceLines = @($sourceEntries | ForEach-Object { "$($_.path) $($_.sha256)`n" })
[Array]::Sort($sourceLines, [StringComparer]::Ordinal)
$sourceInventorySha256 = Get-TestUtf8Sha256 ([string]::Concat($sourceLines))
$emptyPatchSetSha256 = Get-TestUtf8Sha256 ''
$addedHash = Get-TestUtf8Sha256 $addedSourceText
$addedLicenseHash = Get-TestUtf8Sha256 $addedLicenseText
$licenseHash = Get-TestUtf8Sha256 $upstreamLicenseText
$fixtureLicenseHash = Get-TestUtf8Sha256 $fixtureLicenseText
$fixtureHash = Get-TestSha256 $fixtureFontBytes
$addedLines = @(
    "$addedPath $addedHash MIT $addedLicensePath $addedLicenseHash`n",
    "$addedLicensePath $addedLicenseHash MIT $addedLicensePath $addedLicenseHash`n"
)
[Array]::Sort($addedLines, [StringComparer]::Ordinal)
$addedFilesSha256 = Get-TestUtf8Sha256 ([string]::Concat($addedLines))

$addedSource = [ordered]@{
    path = $addedPath
    sha256 = $addedHash
    license = 'MIT'
    licenseTextPath = $addedLicensePath
    licenseTextSha256 = $addedLicenseHash
}
$addedLicense = [ordered]@{
    path = $addedLicensePath
    sha256 = $addedLicenseHash
    license = 'MIT'
    licenseTextPath = $addedLicensePath
    licenseTextSha256 = $addedLicenseHash
}
$fixtureRecords = @(
    [ordered]@{
        path = $fixtureLicensePath
        upstreamPath = 'crates/probe/tests/fonts/TEST-LICENSE-OFL.txt'
        sha256 = $fixtureLicenseHash
        license = 'OFL-1.1'
        licenseTextPath = $fixtureLicensePath
        licenseTextSha256 = $fixtureLicenseHash
    },
    [ordered]@{
        path = $fixturePath
        upstreamPath = 'crates/probe/tests/fonts/TEST-Font.ttf'
        sha256 = $fixtureHash
        license = 'OFL-1.1'
        licenseTextPath = $fixtureLicensePath
        licenseTextSha256 = $fixtureLicenseHash
    }
)

$identity = @{
    schemaVersion = 1
    repository = 'https://example.invalid/probe.git'
    revision = '0123456789abcdef0123456789abcdef01234567'
    packagePath = 'crates/probe'
    sourceTree = '0123456789abcdef0123456789abcdef01234567'
    archiveSha256 = ('1' * 64)
    packageName = 'probe'
    packageVersion = '1.0.0'
    license = 'BSD-3-Clause'
    vendoredLicense = 'BSD-3-Clause AND MIT'
    sourceInventorySha256 = $sourceInventorySha256
    patchSetSha256 = $emptyPatchSetSha256
}
$manifest = [ordered]@{
    schemaVersion = $identity.schemaVersion
    repository = $identity.repository
    revision = $identity.revision
    packagePath = $identity.packagePath
    sourceTree = $identity.sourceTree
    archiveSha256 = $identity.archiveSha256
    packageName = $identity.packageName
    packageVersion = $identity.packageVersion
    license = $identity.license
    vendoredLicense = $identity.vendoredLicense
    sourceInventorySha256 = $sourceInventorySha256
    patchSetSha256 = $emptyPatchSetSha256
    sourceFiles = $sourceEntries
    patches = @()
    addedFilesSha256 = $addedFilesSha256
    addedFiles = @($addedSource, $addedLicense)
}
$supplementalManifest = [ordered]@{
    schemaVersion = 1
    packageName = 'probe'
    packageVersion = '1.0.0'
    repository = $identity.repository
    revision = $identity.revision
    purpose = 'test-only'
    files = $fixtureRecords
}

Write-TestUtf8 $manifestPath ($manifest | ConvertTo-Json -Depth 12)
Write-TestUtf8 $supplementalManifestPath ($supplementalManifest | ConvertTo-Json -Depth 12)

$expected = @{
    packageName = 'probe'
    packageVersion = '1.0.0'
    repository = $identity.repository
    revision = $identity.revision
    packagePath = 'crates/probe'
    sourceTree = $identity.sourceTree
    archiveSha256 = $identity.archiveSha256
    license = 'BSD-3-Clause'
    vendoredLicense = 'BSD-3-Clause AND MIT'
    sourceInventorySha256 = $sourceInventorySha256
    patchSetSha256 = $emptyPatchSetSha256
    expectedPatchPaths = @()
    addedFilesSha256 = $addedFilesSha256
    addedFiles = @($addedSource, $addedLicense)
    supplementalTestFixtures = $fixtureRecords
}

$valid = Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
if ($valid.AddedFiles.Count -ne 2 -or
    $valid.AddedFilesSha256 -cne $addedFilesSha256 -or
    $valid.License -cne 'BSD-3-Clause AND MIT' -or
    $valid.SupplementalTestFixtures.Count -ne 2) {
    throw 'The pinned local source or supplemental fixture evidence was not returned in the verified record.'
}

# Git-pinned packages use the independently pinned revision/tree/inventory,
# not a registry archive hash. Under StrictMode, the absent optional field must
# remain absent; an unpinned manifest-provided value must still be rejected.
$expectedWithoutArchive = $expected.Clone()
$expectedWithoutArchive.Remove('archiveSha256')
$manifestWithoutArchive = [ordered]@{}
foreach ($manifestKey in $manifest.Keys) {
    if ($manifestKey -cne 'archiveSha256') {
        $manifestWithoutArchive[$manifestKey] = $manifest[$manifestKey]
    }
}
Write-TestUtf8 $manifestPath ($manifestWithoutArchive | ConvertTo-Json -Depth 12)
$validWithoutArchive = Get-ResvgVendoredPackageProvenance `
    -ProviderRoot $providerRoot `
    -Expected $expectedWithoutArchive
if (-not [string]::IsNullOrEmpty([string]$validWithoutArchive.ArchiveSha256) -or
    -not [string]::IsNullOrEmpty([string]$validWithoutArchive.SbomRecord.archiveSha256)) {
    throw 'A Git-pinned source without an archive pin must not acquire an archive digest in its evidence record.'
}

$manifestWithUnpinnedArchive = [ordered]@{}
foreach ($manifestKey in $manifestWithoutArchive.Keys) {
    $manifestWithUnpinnedArchive[$manifestKey] = $manifestWithoutArchive[$manifestKey]
}
$manifestWithUnpinnedArchive['archiveSha256'] = ('2' * 64)
Write-TestUtf8 $manifestPath ($manifestWithUnpinnedArchive | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expectedWithoutArchive
} 'archiveSha256 without an independent pin'

$manifestMissingPinnedArchive = [ordered]@{}
foreach ($manifestKey in $manifest.Keys) {
    if ($manifestKey -cne 'archiveSha256') {
        $manifestMissingPinnedArchive[$manifestKey] = $manifest[$manifestKey]
    }
}
Write-TestUtf8 $manifestPath ($manifestMissingPinnedArchive | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'unexpected archiveSha256'
Write-TestUtf8 $manifestPath ($manifest | ConvertTo-Json -Depth 12)

$replacementSourceText = "// replacement controlled only by the adjacent manifest`n"
$replacementSourcePath = Join-Path $vendorRoot $sourcePath
Write-TestUtf8 $replacementSourcePath $replacementSourceText
$tamperedSourceManifest = $manifest | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12
$tamperedSourceEntry = @($tamperedSourceManifest.sourceFiles | Where-Object { [string]$_.path -ceq $sourcePath })
if ($tamperedSourceEntry.Count -ne 1) {
    throw 'The synthetic source inventory does not contain its pinned source exactly once.'
}
$tamperedSourceEntry[0].sha256 = Get-TestUtf8Sha256 $replacementSourceText
$tamperedSourceLines = @($tamperedSourceManifest.sourceFiles | ForEach-Object { "$($_.path) $($_.sha256)`n" })
[Array]::Sort($tamperedSourceLines, [StringComparer]::Ordinal)
$tamperedSourceManifest.sourceInventorySha256 = Get-TestUtf8Sha256 ([string]::Concat($tamperedSourceLines))
Write-TestUtf8 $manifestPath ($tamperedSourceManifest | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'unexpected sourceInventorySha256'
Write-TestUtf8 $replacementSourcePath $upstreamSourceText
Write-TestUtf8 $manifestPath ($manifest | ConvertTo-Json -Depth 12)

$tamperedAddedManifest = $manifest | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12
$tamperedAddedManifest.addedFiles[0].license = 'GPL-3.0-only'
Write-TestUtf8 $manifestPath ($tamperedAddedManifest | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'added-file entry'

$tamperedAddedManifest = $manifest | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12
$tamperedAddedManifest.addedFiles[0].sha256 = ('0' * 64)
Write-TestUtf8 $manifestPath ($tamperedAddedManifest | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'added-file entry'

Write-TestUtf8 $manifestPath ($manifest | ConvertTo-Json -Depth 12)
$tamperedAddedManifest = $manifest | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12
$tamperedAddedManifest.addedFiles[0].path = 'src/Scan/path_analytic.rs'
Write-TestUtf8 $manifestPath ($tamperedAddedManifest | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'added-file entry'

$tamperedAddedManifest = $manifest | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12
$tamperedAddedManifest.addedFiles = @($manifest.addedFiles[0], $manifest.addedFiles[0])
Write-TestUtf8 $manifestPath ($tamperedAddedManifest | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'added-file entry'

Write-TestUtf8 $manifestPath ($manifest | ConvertTo-Json -Depth 12)
$tamperedAddedLicense = "MIT License`n`nTampered local notice`n"
Write-TestUtf8 (Join-Path $vendorRoot $addedLicensePath) $tamperedAddedLicense
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'added-file license text differs from its independent pin'
Write-TestUtf8 (Join-Path $vendorRoot $addedLicensePath) $addedLicenseText

$traversalExpected = $expected.Clone()
$traversalSource = [ordered]@{
    path = '../escape.rs'
    sha256 = $addedHash
    license = 'MIT'
    licenseTextPath = $addedLicensePath
    licenseTextSha256 = $addedLicenseHash
}
$traversalAddedFiles = @($traversalSource, $addedLicense)
$traversalLines = @(
    "$($traversalSource.path) $($traversalSource.sha256) $($traversalSource.license) $($traversalSource.licenseTextPath) $($traversalSource.licenseTextSha256)`n",
    "$($addedLicense.path) $($addedLicense.sha256) $($addedLicense.license) $($addedLicense.licenseTextPath) $($addedLicense.licenseTextSha256)`n"
)
[Array]::Sort($traversalLines, [StringComparer]::Ordinal)
$traversalAddedFilesSha256 = Get-TestUtf8Sha256 ([string]::Concat($traversalLines))
$traversalExpected.addedFiles = $traversalAddedFiles
$traversalExpected.addedFilesSha256 = $traversalAddedFilesSha256
$traversalManifest = $manifest | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12
$traversalManifest.addedFiles = $traversalAddedFiles
$traversalManifest.addedFilesSha256 = $traversalAddedFilesSha256
Write-TestUtf8 $manifestPath ($traversalManifest | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $traversalExpected
} 'unapproved or malformed added-file entry'

Write-TestUtf8 $manifestPath ($manifest | ConvertTo-Json -Depth 12)
$tamperedFixtureManifest = $supplementalManifest | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12
$tamperedFixtureManifest.files = @($tamperedFixtureManifest.files | Select-Object -First 1)
Write-TestUtf8 $supplementalManifestPath ($tamperedFixtureManifest | ConvertTo-Json -Depth 12)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'supplemental fixture count drift'

Write-TestUtf8 $supplementalManifestPath ($supplementalManifest | ConvertTo-Json -Depth 12)
$unapprovedSourcePath = Join-Path $vendorRoot 'src/scan/unapproved.rs'
Write-TestUtf8 $unapprovedSourcePath '// must not be silently admitted'
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'untracked source file'
Remove-Item -LiteralPath $unapprovedSourcePath -Force

$hiddenSourcePath = Join-Path $vendorRoot 'src/scan/hidden-unapproved.rs'
Write-TestUtf8 $hiddenSourcePath '// hidden files must remain in the inventory check'
[IO.File]::SetAttributes($hiddenSourcePath, [IO.FileAttributes]::Hidden)
Assert-TestThrows {
    Get-ResvgVendoredPackageProvenance -ProviderRoot $providerRoot -Expected $expected
} 'untracked source file'

Write-Output 'Verified independent added-source pins, license evidence, exact supplemental font inventory, and tamper rejection.'
