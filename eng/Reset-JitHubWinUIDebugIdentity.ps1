param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\JitHub.WinUI\JitHub.WinUI.csproj')
)

$ErrorActionPreference = 'Stop'

$resolvedProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$projectDirectory = Split-Path -Parent $resolvedProjectPath
$repositoryDirectory = Split-Path -Parent $projectDirectory
$developmentPackageNames = @(
    'JitHub.WinUI.Debug',
    'JitHub.WinUI.Debug.debug',
    '54742Neromarah.JitHub.debug'
)

$packages = @(Get-AppxPackage -ErrorAction Stop | Where-Object {
    if (-not $_.IsDevelopmentMode) {
        return $false
    }

    if ($developmentPackageNames -contains $_.Name) {
        return $true
    }

    # Older debug tooling registered the loose layout under the production
    # identity. Remove it only when its files belong to this checkout.
    if ($_.Name -ne '54742Neromarah.JitHub' -or [string]::IsNullOrWhiteSpace($_.InstallLocation)) {
        return $false
    }

    $installLocation = [System.IO.Path]::GetFullPath($_.InstallLocation)
    return $installLocation.StartsWith(
        $repositoryDirectory + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
})

foreach ($package in $packages) {
    Write-Host "Removing development package $($package.PackageFullName)..."
    Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
}

if ($packages.Count -eq 0) {
    Write-Host 'No stale JitHub development identities were registered.'
} else {
    Write-Host "Removed $($packages.Count) JitHub development identity registration(s)."
}
