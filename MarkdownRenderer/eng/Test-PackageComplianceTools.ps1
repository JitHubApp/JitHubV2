[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackageCompliance.Common.ps1')
. (Join-Path $PSScriptRoot 'PackageSizeGates.Common.ps1')

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Self-test assertion failed: $Message" }
}

function New-FixturePackage(
    [string] $Path,
    [string] $Payload,
    [string] $PackageId = 'Fixture.Package',
    [DateTimeOffset] $Timestamp = '1980-01-01T00:00:00+00:00',
    [AllowNull()][byte[]] $ManagedAssemblyBytes = $null,
    [string] $ManagedAssemblyPath = 'lib/net10.0/Fixture.Package.dll',
    [switch] $NoManagedAsset) {
    if ($NoManagedAsset -and $null -ne $ManagedAssemblyBytes) {
        throw 'A no-managed-asset fixture cannot also carry managed assembly bytes.'
    }
    $parent = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $file = [IO.File]::Create($Path)
    try {
        $archive = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $nuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>$PackageId</id>
    <version>1.2.3-preview.1</version>
    <authors>Fixture Authors</authors>
    <description>Deterministic compliance fixture.</description>
    <license type="expression">MIT</license>
    <repository type="git" url="https://example.invalid/fixture" />
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Fixture.Dependency.WithoutLicenseMetadata" version="4.5.6" />
      </group>
    </dependencies>
  </metadata>
</package>
"@
            $contents = [ordered]@{
                'Fixture.Package.nuspec' = $nuspec
                'LICENSE' = "MIT fixture license evidence.`n"
            }
            if (-not $NoManagedAsset -and $null -eq $ManagedAssemblyBytes) {
                $contents[$ManagedAssemblyPath] = $Payload
            }
            foreach ($item in $contents.GetEnumerator()) {
                $entry = $archive.CreateEntry($item.Key, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $Timestamp
                $stream = $entry.Open()
                try {
                    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 1024, $true)
                    try { $writer.Write($item.Value) } finally { $writer.Dispose() }
                }
                finally { $stream.Dispose() }
            }

            if ($null -ne $ManagedAssemblyBytes) {
                $entry = $archive.CreateEntry($ManagedAssemblyPath, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $Timestamp
                $stream = $entry.Open()
                try { $stream.Write($ManagedAssemblyBytes, 0, $ManagedAssemblyBytes.Length) }
                finally { $stream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $file.Dispose() }
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ('MarkdownRenderer.PackageCompliance.Tests.' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $expectedPackageIds = @(Get-ExpectedReleasePackageIds)
    Assert-True ($expectedPackageIds.Count -eq 8) 'the release package set must contain exactly 8 IDs'

    & (Join-Path $PSScriptRoot 'Test-ResvgSelectedDeploymentSize.ps1')

    $completePackageSet = Join-Path $temporaryRoot 'complete-package-set'
    foreach ($packageId in $expectedPackageIds) {
        New-FixturePackage `
            -Path (Join-Path $completePackageSet ($packageId + '.1.2.3-preview.1.nupkg')) `
            -Payload ('payload-' + $packageId) `
            -PackageId $packageId
    }
    Assert-ExpectedReleasePackageSet -PackageDirectory $completePackageSet

    foreach ($scope in @('Math', 'SvgResvg', 'TextMate')) {
        $scopedIds = @(Get-ExpectedPackageIdsForScope -Scope $scope)
        Assert-True ($scopedIds.Count -gt 0 -and $scopedIds.Count -lt $expectedPackageIds.Count) `
            "$scope must have a fixed, proper subset of the full release package set"
        $scopedSet = Join-Path $temporaryRoot ("scope-" + $scope)
        foreach ($packageId in $scopedIds) {
            New-FixturePackage `
                -Path (Join-Path $scopedSet ($packageId + '.1.2.3-preview.1.nupkg')) `
                -Payload ('payload-' + $packageId) `
                -PackageId $packageId
        }
        Assert-ExpectedReleasePackageSet -PackageDirectory $scopedSet -Scope $scope

        $defaultScopeRejected = $false
        try { Assert-ExpectedReleasePackageSet -PackageDirectory $scopedSet }
        catch { $defaultScopeRejected = $_.Exception.Message -like '*Expected exactly 8 release packages*' }
        Assert-True $defaultScopeRejected "$scope subset must never satisfy the default full-release gate"
    }

    $missingPackageSet = Join-Path $temporaryRoot 'missing-package-set'
    foreach ($packageId in $expectedPackageIds | Select-Object -SkipLast 1) {
        New-FixturePackage `
            -Path (Join-Path $missingPackageSet ($packageId + '.1.2.3-preview.1.nupkg')) `
            -Payload ('payload-' + $packageId) `
            -PackageId $packageId
    }
    $missingPackageSetFailed = $false
    try { Assert-ExpectedReleasePackageSet -PackageDirectory $missingPackageSet }
    catch { $missingPackageSetFailed = $_.Exception.Message -like '*Expected exactly 8 release packages*' }
    Assert-True $missingPackageSetFailed 'the release package gate must reject a missing package/count shortfall'

    $unexpectedPackageSet = Join-Path $temporaryRoot 'unexpected-package-set'
    foreach ($packageId in $expectedPackageIds | Select-Object -SkipLast 1) {
        New-FixturePackage `
            -Path (Join-Path $unexpectedPackageSet ($packageId + '.1.2.3-preview.1.nupkg')) `
            -Payload ('payload-' + $packageId) `
            -PackageId $packageId
    }
    New-FixturePackage `
        -Path (Join-Path $unexpectedPackageSet 'MarkdownRenderer.Unexpected.1.2.3-preview.1.nupkg') `
        -Payload 'unexpected-payload' `
        -PackageId 'MarkdownRenderer.Unexpected'
    $unexpectedPackageSetFailed = $false
    try { Assert-ExpectedReleasePackageSet -PackageDirectory $unexpectedPackageSet }
    catch { $unexpectedPackageSetFailed = $_.Exception.Message -like '*ID set mismatch*' }
    Assert-True $unexpectedPackageSetFailed 'the release package gate must reject an unexpected ID even when the count is 8'

    $duplicatePackageSet = Join-Path $temporaryRoot 'duplicate-package-set'
    foreach ($packageId in $expectedPackageIds | Select-Object -First 7) {
        New-FixturePackage `
            -Path (Join-Path $duplicatePackageSet ($packageId + '.1.2.3-preview.1.nupkg')) `
            -Payload ('payload-' + $packageId) `
            -PackageId $packageId
    }
    New-FixturePackage `
        -Path (Join-Path $duplicatePackageSet 'MarkdownRenderer.Duplicate.1.2.3-preview.1.nupkg') `
        -Payload 'duplicate-payload' `
        -PackageId $expectedPackageIds[0]
    $duplicatePackageSetFailed = $false
    try { Assert-ExpectedReleasePackageSet -PackageDirectory $duplicatePackageSet }
    catch { $duplicatePackageSetFailed = $_.Exception.Message -like '*IDs must be unique*' }
    Assert-True $duplicatePackageSetFailed 'the release package gate must reject duplicate IDs that mask a missing package'

    $packages = Join-Path $temporaryRoot 'packages'
    $packagePath = Join-Path $packages 'Fixture.Package.1.2.3-preview.1.nupkg'
    New-FixturePackage -Path $packagePath -Payload 'payload-one'

    $outputA = Join-Path $temporaryRoot 'output-a'
    $outputB = Join-Path $temporaryRoot 'output-b'
    & (Join-Path $PSScriptRoot 'New-PackageComplianceArtifacts.ps1') -PackageDirectory $packages -OutputDirectory $outputA
    & (Join-Path $PSScriptRoot 'New-PackageComplianceArtifacts.ps1') -PackageDirectory $packages -OutputDirectory $outputB

    $filesA = @(Get-ChildItem -LiteralPath $outputA -File | Sort-Object Name)
    $filesB = @(Get-ChildItem -LiteralPath $outputB -File | Sort-Object Name)
    Assert-True ((($filesA.Name) -join "`n") -ceq (($filesB.Name) -join "`n")) 'generated artifact names must be stable'
    foreach ($fileA in $filesA) {
        $fileB = Get-Item -LiteralPath (Join-Path $outputB $fileA.Name)
        Assert-True ((Get-FileSha256 $fileA.FullName) -ceq (Get-FileSha256 $fileB.FullName)) "'$($fileA.Name)' must be byte-for-byte deterministic"
    }

    $sbomPath = Join-Path $outputA 'Fixture.Package.1.2.3-preview.1.spdx.json'
    $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json -Depth 100
    Assert-True ($sbom.spdxVersion -ceq 'SPDX-2.3') 'SBOM version must be SPDX 2.3'
    Assert-True ($sbom.packages[0].licenseDeclared -ceq 'MIT') 'root license must come from the nuspec expression'
    Assert-True ($sbom.packages[1].licenseDeclared -ceq 'NOASSERTION') 'unknown dependency licenses must not be invented'

    $strictFailed = $false
    try {
        & (Join-Path $PSScriptRoot 'New-PackageComplianceArtifacts.ps1') -PackageDirectory $packages -OutputDirectory (Join-Path $temporaryRoot 'strict') -FailOnUnknownLicense
    }
    catch { $strictFailed = $true }
    Assert-True $strictFailed 'strict license mode must fail on dependency license NOASSERTION'

    $compatibilityFailed = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-LicenseCompatibility.ps1') -ComplianceDirectory $outputA
    }
    catch { $compatibilityFailed = $true }
    Assert-True $compatibilityFailed 'compatibility mode must fail on dependency license NOASSERTION'

    $sbom.packages[1].licenseDeclared = 'MIT'
    [IO.File]::WriteAllText(
        $sbomPath,
        ($sbom | ConvertTo-Json -Depth 100) + "`n",
        [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Test-LicenseCompatibility.ps1') -ComplianceDirectory $outputA

    $sbom.packages[0].licenseDeclared = 'GPL-3.0-only'
    [IO.File]::WriteAllText(
        $sbomPath,
        ($sbom | ConvertTo-Json -Depth 100) + "`n",
        [Text.UTF8Encoding]::new($false))
    $incompatibleFailed = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-LicenseCompatibility.ps1') -ComplianceDirectory $outputA
    }
    catch { $incompatibleFailed = $true }
    Assert-True $incompatibleFailed 'compatibility mode must reject a known but unapproved license'

    $referenceManifest = Join-Path $temporaryRoot 'reference.json'
    $candidateManifest = Join-Path $temporaryRoot 'candidate.json'
    & (Join-Path $PSScriptRoot 'New-PackageReproducibilityManifest.ps1') -PackageDirectory $packages -OutputPath $referenceManifest
    & (Join-Path $PSScriptRoot 'New-PackageReproducibilityManifest.ps1') -PackageDirectory $packages -OutputPath $candidateManifest -ReferenceManifest $referenceManifest

    $timestampedA = Join-Path $temporaryRoot 'timestamped-a'
    $timestampedB = Join-Path $temporaryRoot 'timestamped-b'
    New-FixturePackage `
        -Path (Join-Path $timestampedA 'Fixture.Package.1.2.3-preview.1.nupkg') `
        -Payload 'same-payload' `
        -Timestamp '2026-01-01T00:00:00+00:00'
    New-FixturePackage `
        -Path (Join-Path $timestampedB 'Fixture.Package.1.2.3-preview.1.nupkg') `
        -Payload 'same-payload' `
        -Timestamp '2026-01-01T00:02:00+00:00'
    & (Join-Path $PSScriptRoot 'Normalize-NuGetPackageArchives.ps1') -PackageDirectory $timestampedA
    & (Join-Path $PSScriptRoot 'Normalize-NuGetPackageArchives.ps1') -PackageDirectory $timestampedB
    $normalizedReference = Join-Path $temporaryRoot 'normalized-reference.json'
    & (Join-Path $PSScriptRoot 'New-PackageReproducibilityManifest.ps1') `
        -PackageDirectory $timestampedA `
        -OutputPath $normalizedReference
    & (Join-Path $PSScriptRoot 'New-PackageReproducibilityManifest.ps1') `
        -PackageDirectory $timestampedB `
        -OutputPath (Join-Path $temporaryRoot 'normalized-candidate.json') `
        -ReferenceManifest $normalizedReference

    $changedPackages = Join-Path $temporaryRoot 'changed-packages'
    New-FixturePackage -Path (Join-Path $changedPackages 'Fixture.Package.1.2.3-preview.1.nupkg') -Payload 'payload-two'
    $mismatchFailed = $false
    try {
        & (Join-Path $PSScriptRoot 'New-PackageReproducibilityManifest.ps1') -PackageDirectory $changedPackages -OutputPath (Join-Path $temporaryRoot 'changed.json') -ReferenceManifest $referenceManifest
    }
    catch { $mismatchFailed = $true }
    Assert-True $mismatchFailed 'reproducibility comparison must detect changed package bytes or entries'

    $trustedAssemblies = [string] [AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES')
    $anyCpuAssemblyPath = @($trustedAssemblies -split [IO.Path]::PathSeparator |
        Where-Object { [IO.Path]::GetFileName($_) -eq 'System.Runtime.dll' } |
        Select-Object -First 1)
    if ($anyCpuAssemblyPath.Count -eq 0) {
        $anyCpuAssemblyPath = @([object].Assembly.Location)
    }
    $anyCpuAssemblyBytes = [IO.File]::ReadAllBytes([string] $anyCpuAssemblyPath[0])
    $peHeaderOffset = [BitConverter]::ToInt32($anyCpuAssemblyBytes, 0x3c)
    $amd64Bytes = [byte[]] $anyCpuAssemblyBytes.Clone()
    [Array]::Copy([BitConverter]::GetBytes([UInt16] 0x8664), 0, $amd64Bytes, $peHeaderOffset + 4, 2)

    $managedPackageSet = Join-Path $temporaryRoot 'managed-anycpu-package'
    New-FixturePackage `
        -Path (Join-Path $managedPackageSet 'Fixture.Package.1.2.3-preview.1.nupkg') `
        -Payload '' `
        -ManagedAssemblyBytes $anyCpuAssemblyBytes `
        -ManagedAssemblyPath 'runtimes/win-x64/lib/net10.0/Fixture.Runtime.dll'
    $managedArchitectureReport = Join-Path $temporaryRoot 'managed-anycpu-report.json'
    & (Join-Path $PSScriptRoot 'Test-ManagedPackageArchitecture.ps1') `
        -PackageDirectory $managedPackageSet `
        -OutputPath $managedArchitectureReport
    $managedArchitectureEvidence = Get-Content -LiteralPath $managedArchitectureReport -Raw | ConvertFrom-Json -Depth 100
    Assert-True ($managedArchitectureEvidence.passed -and $managedArchitectureEvidence.assets.Count -eq 1) 'RID-specific managed runtime assets must pass when their PE remains AnyCPU IL'

    $architectureTargetedPackageSet = Join-Path $temporaryRoot 'managed-amd64-package'
    New-FixturePackage `
        -Path (Join-Path $architectureTargetedPackageSet 'Fixture.Package.1.2.3-preview.1.nupkg') `
        -Payload '' `
        -ManagedAssemblyBytes $amd64Bytes
    $architectureTargetedReport = Join-Path $temporaryRoot 'managed-amd64-report.json'
    $architectureTargetedFailed = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-ManagedPackageArchitecture.ps1') `
            -PackageDirectory $architectureTargetedPackageSet `
            -OutputPath $architectureTargetedReport
    }
    catch { $architectureTargetedFailed = $_.Exception.Message -like '*Managed package architecture compliance failed*' }
    Assert-True $architectureTargetedFailed 'package compliance must reject Amd64 managed DLLs even under an RID-independent lib path'
    $architectureTargetedEvidence = Get-Content -LiteralPath $architectureTargetedReport -Raw | ConvertFrom-Json -Depth 100
    Assert-True (-not $architectureTargetedEvidence.passed -and $architectureTargetedEvidence.failures[0] -like '*expected I386/PE32*') 'the managed architecture report must identify the incompatible PE architecture'

    $requiredAssemblySet = Join-Path $temporaryRoot 'required-managed-asset-present'
    New-FixturePackage `
        -Path (Join-Path $requiredAssemblySet 'MarkdownRenderer.1.2.3-preview.1.nupkg') `
        -Payload '' `
        -PackageId 'MarkdownRenderer' `
        -ManagedAssemblyBytes $anyCpuAssemblyBytes `
        -ManagedAssemblyPath 'lib/net10.0/MarkdownRenderer.dll'
    $requiredAssemblyReport = Join-Path $temporaryRoot 'required-managed-asset-present.json'
    & (Join-Path $PSScriptRoot 'Test-ManagedPackageArchitecture.ps1') `
        -PackageDirectory $requiredAssemblySet `
        -OutputPath $requiredAssemblyReport
    $requiredAssemblyEvidence = Get-Content -LiteralPath $requiredAssemblyReport -Raw | ConvertFrom-Json -Depth 100
    Assert-True $requiredAssemblyEvidence.passed 'a code-bearing package with its required AnyCPU library asset must pass'

    $missingAssemblySet = Join-Path $temporaryRoot 'required-managed-asset-missing'
    New-FixturePackage `
        -Path (Join-Path $missingAssemblySet 'MarkdownRenderer.1.2.3-preview.1.nupkg') `
        -Payload '' `
        -PackageId 'MarkdownRenderer' `
        -ManagedAssemblyBytes $anyCpuAssemblyBytes `
        -ManagedAssemblyPath 'lib/net10.0/Wrong.dll'
    $missingAssemblyReport = Join-Path $temporaryRoot 'required-managed-asset-missing.json'
    $missingAssemblyFailed = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-ManagedPackageArchitecture.ps1') `
            -PackageDirectory $missingAssemblySet `
            -OutputPath $missingAssemblyReport
    }
    catch { $missingAssemblyFailed = $_.Exception.Message -like '*Managed package architecture compliance failed*' }
    Assert-True $missingAssemblyFailed 'package compliance must reject a code-bearing package without its required DLL'
    $missingAssemblyEvidence = Get-Content -LiteralPath $missingAssemblyReport -Raw | ConvertFrom-Json -Depth 100
    Assert-True (-not $missingAssemblyEvidence.passed -and $missingAssemblyEvidence.failures[0] -like '*expected exactly one runtime library asset*') 'the managed architecture report must identify the absent runtime DLL'

    $emptyMetapackageSet = Join-Path $temporaryRoot 'metapackage-empty'
    New-FixturePackage `
        -Path (Join-Path $emptyMetapackageSet 'MarkdownRenderer.All.1.2.3-preview.1.nupkg') `
        -Payload '' `
        -PackageId 'MarkdownRenderer.All' `
        -NoManagedAsset
    $emptyMetapackageReport = Join-Path $temporaryRoot 'metapackage-empty.json'
    & (Join-Path $PSScriptRoot 'Test-ManagedPackageArchitecture.ps1') `
        -PackageDirectory $emptyMetapackageSet `
        -OutputPath $emptyMetapackageReport
    $emptyMetapackageEvidence = Get-Content -LiteralPath $emptyMetapackageReport -Raw | ConvertFrom-Json -Depth 100
    Assert-True ($emptyMetapackageEvidence.passed -and $emptyMetapackageEvidence.assets.Count -eq 0) 'the All metapackage is intentionally DLL-free'

    $nonemptyMetapackageSet = Join-Path $temporaryRoot 'metapackage-nonempty'
    New-FixturePackage `
        -Path (Join-Path $nonemptyMetapackageSet 'MarkdownRenderer.All.1.2.3-preview.1.nupkg') `
        -Payload '' `
        -PackageId 'MarkdownRenderer.All' `
        -ManagedAssemblyBytes $anyCpuAssemblyBytes `
        -ManagedAssemblyPath 'lib/net10.0/Unexpected.dll'
    $nonemptyMetapackageReport = Join-Path $temporaryRoot 'metapackage-nonempty.json'
    $nonemptyMetapackageFailed = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-ManagedPackageArchitecture.ps1') `
            -PackageDirectory $nonemptyMetapackageSet `
            -OutputPath $nonemptyMetapackageReport
    }
    catch { $nonemptyMetapackageFailed = $_.Exception.Message -like '*Managed package architecture compliance failed*' }
    Assert-True $nonemptyMetapackageFailed 'the All metapackage must reject an unexpected managed DLL'
    $nonemptyMetapackageEvidence = Get-Content -LiteralPath $nonemptyMetapackageReport -Raw | ConvertFrom-Json -Depth 100
    Assert-True (-not $nonemptyMetapackageEvidence.passed -and $nonemptyMetapackageEvidence.failures[0] -like '*must not contain managed DLL assets*') 'the managed architecture report must identify code added to a metapackage'

    & (Join-Path $PSScriptRoot 'tests\Normalize-NuGetPackageArchives.Tests.ps1')

    Write-Host 'Package compliance tooling self-tests passed.'
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $temporaryPrefix = $temporaryBase.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected self-test path '$resolvedRoot'."
    }
    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
