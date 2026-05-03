param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$BundlePlatforms = 'x86|x64|ARM64',
    [string]$TargetPlatformVersion = '10.0.26100.0',
    [switch]$UseSigningCertificate,
    [string]$PackageCertificateBase64,
    [string]$PackageCertificatePassword,
    [string]$PackageCertificateThumbprint
)

$ErrorActionPreference = 'Stop'

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-BuildPlatforms {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $platforms = $Value.Split('|', [System.StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    if ($platforms.Count -eq 0) {
        throw 'BundlePlatforms must include at least one platform.'
    }

    return $platforms
}

function Get-TransientTempPath {
    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        return $env:RUNNER_TEMP
    }

    if (-not [string]::IsNullOrWhiteSpace($env:TEMP)) {
        return $env:TEMP
    }

    return [System.IO.Path]::GetTempPath()
}

function Get-PrimaryPlatform {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Platforms
    )

    foreach ($preferredPlatform in @('x64', 'ARM64', 'x86')) {
        if ($Platforms -contains $preferredPlatform) {
            return $preferredPlatform
        }
    }

    return $Platforms[0]
}

function Invoke-StoreUploadBuild {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedProjectPath,

        [Parameter(Mandatory = $true)]
        [string[]]$Platforms,

        [Parameter(Mandatory = $true)]
        [string]$PackageOutputDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ResolvedTargetPlatformVersion,

        [switch]$SignPackage,
        [string]$CertificatePath,
        [string]$CertificatePasswordValue,
        [string]$CertificateThumbprintValue
    )

    $primaryPlatform = Get-PrimaryPlatform -Platforms $Platforms
    $bundleMode = if ($Platforms.Count -gt 1) { 'Always' } else { 'Never' }

    $buildArguments = @(
        'msbuild'
        $ResolvedProjectPath
        '/restore'
        '/m'
        '/p:Configuration=Release'
        "/p:Platform=$primaryPlatform"
        "/p:TargetPlatformVersion=$ResolvedTargetPlatformVersion"
        '/p:GenerateAppxPackageOnBuild=True'
        '/p:UapAppxPackageBuildMode=StoreUpload'
        "/p:AppxBundle=$bundleMode"
        '/p:GenerateAppInstallerFile=False'
        '/p:AppxAutoIncrementPackageRevision=False'
        '/p:HoursBetweenUpdateChecks=0'
        '/p:AppxSymbolPackageEnabled=False'
        "/p:AppxPackageDir=$PackageOutputDirectory\"
    )

    if ($SignPackage) {
        $buildArguments += '/p:AppxPackageSigningEnabled=True'
        $buildArguments += '/p:GenerateTemporaryStoreCertificate=False'
        $buildArguments += "/p:PackageCertificateKeyFile=$CertificatePath"
        $buildArguments += "/p:PackageCertificatePassword=$CertificatePasswordValue"

        if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprintValue)) {
            $buildArguments += "/p:PackageCertificateThumbprint=$CertificateThumbprintValue"
        }
    }
    else {
        $buildArguments += '/p:AppxPackageSigningEnabled=False'
        $buildArguments += '/p:GenerateTemporaryStoreCertificate=False'
    }

    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet msbuild StoreUpload packaging failed.'
    }
}

$resolvedProjectPath = Resolve-AbsolutePath -Path $ProjectPath
if (-not (Test-Path -LiteralPath $resolvedProjectPath)) {
    throw "Project not found: $resolvedProjectPath"
}

$resolvedOutputDirectory = Resolve-AbsolutePath -Path $OutputDirectory
$platforms = Get-BuildPlatforms -Value $BundlePlatforms
if ($platforms.Count -gt 1) {
    throw @"
Multi-platform Store bundles are currently blocked while editor assets are packaged as direct files.

Each individual platform package builds successfully, but the bundle resource-indexing step treats parts of the VS Code asset tree as Windows resource qualifiers and fails before producing the .msixupload.

Use a single platform such as x64 for the current Store workflow. To restore x86/x64/ARM64 in one Store submission, add a generated editor-asset archive/package path so makepri does not index the raw node_modules/dist tree.
"@
}

if (Test-Path -LiteralPath $resolvedOutputDirectory) {
    Remove-Item -LiteralPath $resolvedOutputDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$certificatePath = $null
$effectiveCertificatePassword = $PackageCertificatePassword
$effectiveCertificateThumbprint = $PackageCertificateThumbprint
try {
    if ($UseSigningCertificate) {
        if ([string]::IsNullOrWhiteSpace($PackageCertificateBase64)) {
            throw 'PackageCertificateBase64 is required when UseSigningCertificate is enabled.'
        }

        if ([string]::IsNullOrWhiteSpace($PackageCertificatePassword)) {
            throw 'PackageCertificatePassword is required when UseSigningCertificate is enabled.'
        }

        $certificatePath = Join-Path (Get-TransientTempPath) 'JitHub-WinUI-StorePackage.pfx'
        [System.IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($PackageCertificateBase64))

        if ([string]::IsNullOrWhiteSpace($effectiveCertificateThumbprint)) {
            $packageCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $certificatePath,
                $effectiveCertificatePassword,
                [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
            $effectiveCertificateThumbprint = $packageCertificate.Thumbprint
            $packageCertificate.Dispose()
        }
    }

    $buildParameters = @{
        ResolvedProjectPath = $resolvedProjectPath
        Platforms = $platforms
        PackageOutputDirectory = $resolvedOutputDirectory
        ResolvedTargetPlatformVersion = $TargetPlatformVersion
    }

    if ($UseSigningCertificate) {
        $buildParameters.SignPackage = $true
        $buildParameters.CertificatePath = $certificatePath
        $buildParameters.CertificatePasswordValue = $effectiveCertificatePassword
        $buildParameters.CertificateThumbprintValue = $effectiveCertificateThumbprint
    }

    Invoke-StoreUploadBuild @buildParameters

    $uploadPackage = Get-ChildItem -Path $resolvedOutputDirectory -Recurse -File |
        Where-Object { $_.Extension -in '.appxupload', '.msixupload' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $uploadPackage) {
        throw "No .appxupload or .msixupload file was created under $resolvedOutputDirectory."
    }

    Write-Host "Created Store upload package: $($uploadPackage.FullName)"
}
finally {
    if ($certificatePath -and (Test-Path -LiteralPath $certificatePath)) {
        Remove-Item -LiteralPath $certificatePath -Force
    }
}
