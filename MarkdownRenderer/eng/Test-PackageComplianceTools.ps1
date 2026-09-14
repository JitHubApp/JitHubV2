[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackageCompliance.Common.ps1')

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Self-test assertion failed: $Message" }
}

function New-FixturePackage(
    [string] $Path,
    [string] $Payload,
    [DateTimeOffset] $Timestamp = '1980-01-01T00:00:00+00:00') {
    $parent = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $file = [IO.File]::Create($Path)
    try {
        $archive = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $contents = [ordered]@{
                'Fixture.Package.nuspec' = @'
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>Fixture.Package</id>
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
'@
                'LICENSE' = "MIT fixture license evidence.`n"
                'lib/net10.0/Fixture.Package.dll' = $Payload
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
        }
        finally { $archive.Dispose() }
    }
    finally { $file.Dispose() }
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ('MarkdownRenderer.PackageCompliance.Tests.' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
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
