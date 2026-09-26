[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),

    [Parameter(Mandatory = $false)]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\package-compliance'),

    [Parameter(Mandatory = $false)]
    [string] $ReferenceManifest,

    [Parameter(Mandatory = $false)]
    [string] $LicensePolicyPath = (Join-Path $PSScriptRoot 'license-compatibility-policy.json'),

    [switch] $FailOnUnknownLicense,

    [switch] $SkipSizeGates
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
if (-not $SkipSizeGates) {
    & (Join-Path $PSScriptRoot 'Test-PackageSizeGates.ps1') -PackageDirectory $PackageDirectory
}

$artifactArguments = @{
    PackageDirectory = $PackageDirectory
    OutputDirectory = $OutputDirectory
}
if ($FailOnUnknownLicense) {
    $artifactArguments.FailOnUnknownLicense = $true
}
& (Join-Path $PSScriptRoot 'New-PackageComplianceArtifacts.ps1') @artifactArguments

& (Join-Path $PSScriptRoot 'Test-LicenseCompatibility.ps1') `
    -ComplianceDirectory $OutputDirectory `
    -PolicyPath $LicensePolicyPath

& (Join-Path $PSScriptRoot 'Test-NativePackageCompliance.ps1') `
    -PackageDirectory $PackageDirectory `
    -OutputPath (Join-Path $OutputDirectory 'native-package-compliance.json')

$manifestArguments = @{
    PackageDirectory = $PackageDirectory
    OutputPath = (Join-Path $OutputDirectory 'package-reproducibility.json')
}
if (-not [string]::IsNullOrWhiteSpace($ReferenceManifest)) {
    $manifestArguments.ReferenceManifest = $ReferenceManifest
}
& (Join-Path $PSScriptRoot 'New-PackageReproducibilityManifest.ps1') @manifestArguments

Write-Host "Package compliance gates completed for '$PackageDirectory'."
