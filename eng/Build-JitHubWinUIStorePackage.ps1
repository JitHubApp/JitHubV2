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

function Get-WindowsPackageUploadFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return Get-ChildItem -Path $Path -Recurse -File |
        Where-Object { $_.Extension -in '.appxupload', '.msixupload' } |
        Sort-Object FullName
}

function New-MultiArchitectureUploadPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageOutputDirectory,

        [Parameter(Mandatory = $true)]
        [string[]]$Platforms
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $singleArchitectureUploads = Get-WindowsPackageUploadFiles -Path $PackageOutputDirectory
    if ($singleArchitectureUploads.Count -lt $Platforms.Count) {
        $expected = $Platforms -join ', '
        $actual = $singleArchitectureUploads | ForEach-Object { $_.Name }
        throw "Expected single-architecture upload packages for $expected, but only found: $($actual -join ', ')"
    }

    $uploadStageDirectory = Join-Path $PackageOutputDirectory 'upload-stage'
    if (Test-Path -LiteralPath $uploadStageDirectory) {
        Remove-Item -LiteralPath $uploadStageDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $uploadStageDirectory -Force | Out-Null

    foreach ($singleArchitectureUpload in $singleArchitectureUploads) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($singleArchitectureUpload.FullName)
        try {
            $entriesToExtract = $archive.Entries |
                Where-Object {
                    $extension = [System.IO.Path]::GetExtension($_.FullName)
                    $extension -in '.appx', '.msix', '.appxsym'
                }

            foreach ($entry in $entriesToExtract) {
                $fileName = [System.IO.Path]::GetFileName($entry.FullName)
                if ([string]::IsNullOrWhiteSpace($fileName)) {
                    continue
                }

                $destinationPath = Join-Path $uploadStageDirectory $fileName
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destinationPath, $true)
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    $architecturePackages = Get-ChildItem -Path $uploadStageDirectory -File |
        Where-Object { $_.Extension -in '.appx', '.msix' } |
        Sort-Object Name

    if ($architecturePackages.Count -lt $Platforms.Count) {
        $expected = $Platforms -join ', '
        $actual = $architecturePackages | ForEach-Object { $_.Name }
        throw "Expected architecture packages for $expected, but only staged: $($actual -join ', ')"
    }

    $firstPackageName = $architecturePackages[0].BaseName
    $packagePrefix = $firstPackageName -replace '_(x86|x64|arm|arm64)$', ''
    $platformLabel = ($Platforms | ForEach-Object { $_.Trim() }) -join '_'
    $uploadExtension = $singleArchitectureUploads[0].Extension
    $combinedUploadPath = Join-Path $PackageOutputDirectory "$packagePrefix`_$platformLabel$uploadExtension"

    if (Test-Path -LiteralPath $combinedUploadPath) {
        Remove-Item -LiteralPath $combinedUploadPath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($uploadStageDirectory, $combinedUploadPath)

    foreach ($singleArchitectureUpload in $singleArchitectureUploads) {
        Remove-Item -LiteralPath $singleArchitectureUpload.FullName -Force
    }

    return Get-Item -LiteralPath $combinedUploadPath
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

    foreach ($platform in $platforms) {
        Write-Host "Building Store upload package for $platform."

        $buildParameters = @{
            ResolvedProjectPath = $resolvedProjectPath
            Platforms = @($platform)
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
    }

    if ($platforms.Count -gt 1) {
        Write-Host "Creating multi-architecture Store upload package for $($platforms -join ', ')."
        $null = New-MultiArchitectureUploadPackage -PackageOutputDirectory $resolvedOutputDirectory -Platforms $platforms
    }

    $uploadPackages = Get-WindowsPackageUploadFiles -Path $resolvedOutputDirectory

    if (-not $uploadPackages) {
        throw "No .appxupload or .msixupload file was created under $resolvedOutputDirectory."
    }

    if ($platforms.Count -gt 1 -and $uploadPackages.Count -ne 1) {
        $actual = $uploadPackages | ForEach-Object { $_.Name }
        throw "Expected exactly one combined Store upload package, but found: $($actual -join ', ')"
    }

    Write-Host 'Created Store upload packages:'
    foreach ($uploadPackage in $uploadPackages) {
        Write-Host "  $($uploadPackage.FullName)"
    }
}
finally {
    if ($certificatePath -and (Test-Path -LiteralPath $certificatePath)) {
        Remove-Item -LiteralPath $certificatePath -Force
    }
}
