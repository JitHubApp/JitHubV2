[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),

    [Parameter(Mandatory = $false)]
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\artifacts\package-reproducibility.json'),

    [Parameter(Mandatory = $false)]
    [string] $ReferenceManifest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackageCompliance.Common.ps1')

$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object Name)
if ($packages.Count -eq 0) {
    throw "No nupkg files were found in '$PackageDirectory'."
}

$manifestPackages = @($packages | ForEach-Object {
    $evidence = Get-NuGetPackageEvidence -Package $_
    [pscustomobject][ordered]@{
        packageFile = $evidence.PackageFile
        packageId = $evidence.Id
        packageVersion = $evidence.Version
        size = $evidence.PackageLength
        sha256 = $evidence.PackageSha256
        entries = @($evidence.Entries | ForEach-Object {
            [pscustomobject][ordered]@{
                path = $_.Path
                size = $_.Length
                compressedSize = $_.CompressedLength
                sha256 = $_.Sha256
            }
        })
    }
})

$manifest = [pscustomobject][ordered]@{
    schemaVersion = 1
    hashAlgorithm = 'SHA256'
    packages = $manifestPackages
}
$json = ConvertTo-DeterministicJson -InputObject $manifest
$expectedJson = $null
if (-not [string]::IsNullOrWhiteSpace($ReferenceManifest)) {
    if (-not (Test-Path -LiteralPath $ReferenceManifest -PathType Leaf)) {
        throw "Reference manifest '$ReferenceManifest' does not exist."
    }
    $expectedObject = Get-Content -LiteralPath $ReferenceManifest -Raw | ConvertFrom-Json -Depth 100
    $expectedJson = ConvertTo-DeterministicJson -InputObject $expectedObject
}
Write-DeterministicText -Path $OutputPath -Content $json

if (-not [string]::IsNullOrWhiteSpace($ReferenceManifest)) {
    if (-not [string]::Equals($expectedJson.TrimEnd(), $json.TrimEnd(), [StringComparison]::Ordinal)) {
        throw "Package hashes, sizes, or archive entries differ from reference manifest '$ReferenceManifest'. Candidate manifest: '$OutputPath'."
    }

    Write-Host "Package reproducibility matched '$ReferenceManifest' for $($packages.Count) packages."
}
else {
    Write-Host "Wrote deterministic hashes and sizes for $($packages.Count) packages to '$OutputPath'."
}
