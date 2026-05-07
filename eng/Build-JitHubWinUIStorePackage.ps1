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

function Get-MakeAppxPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetPlatformVersion
    )

    $windowsKitRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }

    $candidatePaths = foreach ($windowsKitRoot in $windowsKitRoots) {
        Join-Path $windowsKitRoot "$TargetPlatformVersion\x64\makeappx.exe"
        Join-Path $windowsKitRoot "$TargetPlatformVersion\x86\makeappx.exe"
    }

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath) {
            return $candidatePath
        }
    }

    $availableVersions = foreach ($windowsKitRoot in $windowsKitRoots) {
        Get-ChildItem -Path $windowsKitRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe') } |
            Select-Object -ExpandProperty Name
    }

    throw "makeappx.exe for Windows SDK $TargetPlatformVersion was not found. Available SDK versions: $($availableVersions -join ', ')"
}

function Get-PackageIdentityVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $manifestEntry = $archive.Entries | Where-Object { $_.FullName -eq 'AppxManifest.xml' } | Select-Object -First 1
        if (-not $manifestEntry) {
            throw "Package does not contain AppxManifest.xml: $PackagePath"
        }

        $manifestStream = $manifestEntry.Open()
        try {
            $reader = [System.IO.StreamReader]::new($manifestStream)
            try {
                [xml]$manifest = $reader.ReadToEnd()
                $version = $manifest.Package.Identity.Version
                if ([string]::IsNullOrWhiteSpace($version)) {
                    throw "Package manifest does not contain an Identity Version: $PackagePath"
                }

                return $version
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $manifestStream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-StoreUploadPackageContainsBundle {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $rootEntries = $archive.Entries |
            Where-Object { [string]::IsNullOrWhiteSpace([System.IO.Path]::GetDirectoryName($_.FullName)) }

        $bundleEntries = $rootEntries |
            Where-Object { [System.IO.Path]::GetExtension($_.FullName) -in '.appxbundle', '.msixbundle' }

        if (-not $bundleEntries) {
            $entries = $archive.Entries | ForEach-Object { $_.FullName }
            throw "Store upload package must contain a root .appxbundle or .msixbundle because this Store app previously shipped as a bundle. Package '$PackagePath' only contains: $($entries -join ', ')"
        }

        $looseArchitecturePackages = $rootEntries |
            Where-Object { [System.IO.Path]::GetExtension($_.FullName) -in '.appx', '.msix' }

        if ($looseArchitecturePackages) {
            $loosePackageNames = $looseArchitecturePackages | ForEach-Object { $_.FullName }
            throw "Store upload package contains loose architecture packages instead of a bundle: $($loosePackageNames -join ', ')"
        }

        Write-Host "Verified Store upload package contains bundle: $($bundleEntries[0].FullName)"
    }
    finally {
        $archive.Dispose()
    }
}

function New-BundledStoreUploadPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageOutputDirectory,

        [Parameter(Mandatory = $true)]
        [string[]]$Platforms,

        [Parameter(Mandatory = $true)]
        [string]$TargetPlatformVersion
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $singleArchitectureUploads = Get-WindowsPackageUploadFiles -Path $PackageOutputDirectory
    if ($singleArchitectureUploads.Count -lt $Platforms.Count) {
        $expected = $Platforms -join ', '
        $actual = $singleArchitectureUploads | ForEach-Object { $_.Name }
        throw "Expected single-architecture upload packages for $expected, but only found: $($actual -join ', ')"
    }

    $uploadStageDirectory = Join-Path $PackageOutputDirectory 'upload-stage'
    $bundleInputDirectory = Join-Path $PackageOutputDirectory 'bundle-input'
    $combinedUploadStageDirectory = Join-Path $PackageOutputDirectory 'combined-upload-stage'

    foreach ($stageDirectory in @($uploadStageDirectory, $bundleInputDirectory, $combinedUploadStageDirectory)) {
        if (Test-Path -LiteralPath $stageDirectory) {
            Remove-Item -LiteralPath $stageDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
    }

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

    foreach ($architecturePackage in $architecturePackages) {
        Copy-Item -LiteralPath $architecturePackage.FullName -Destination $bundleInputDirectory -Force
    }

    $firstPackageName = $architecturePackages[0].BaseName
    $packagePrefix = $firstPackageName -replace '_(x86|x64|arm|arm64)$', ''
    $platformLabel = ($Platforms | ForEach-Object { $_.Trim() }) -join '_'
    $uploadExtension = $singleArchitectureUploads[0].Extension
    $combinedBundlePath = Join-Path $PackageOutputDirectory "$packagePrefix`_$platformLabel.msixbundle"
    $combinedUploadPath = Join-Path $PackageOutputDirectory "$packagePrefix`_$platformLabel`_bundle$uploadExtension"

    foreach ($outputPath in @($combinedBundlePath, $combinedUploadPath)) {
        if (Test-Path -LiteralPath $outputPath) {
            Remove-Item -LiteralPath $outputPath -Force
        }
    }

    $bundleVersion = Get-PackageIdentityVersion -PackagePath $architecturePackages[0].FullName
    $makeAppxPath = Get-MakeAppxPath -TargetPlatformVersion $TargetPlatformVersion
    $makeAppxArguments = @(
        'bundle'
        '/d'
        $bundleInputDirectory
        '/p'
        $combinedBundlePath
        '/o'
        '/bv'
        $bundleVersion
    )

    Write-Host "Creating Store app bundle with $makeAppxPath."
    & $makeAppxPath @makeAppxArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'makeappx bundle failed.'
    }

    Copy-Item -LiteralPath $combinedBundlePath -Destination $combinedUploadStageDirectory -Force

    $symbolPackages = Get-ChildItem -Path $uploadStageDirectory -File |
        Where-Object { $_.Extension -eq '.appxsym' } |
        Sort-Object Name

    foreach ($symbolPackage in $symbolPackages) {
        Copy-Item -LiteralPath $symbolPackage.FullName -Destination $combinedUploadStageDirectory -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($combinedUploadStageDirectory, $combinedUploadPath)

    if (Test-Path -LiteralPath $combinedBundlePath) {
        Remove-Item -LiteralPath $combinedBundlePath -Force
    }

    foreach ($singleArchitectureUpload in $singleArchitectureUploads) {
        Remove-Item -LiteralPath $singleArchitectureUpload.FullName -Force
    }

    foreach ($stageDirectory in @($uploadStageDirectory, $bundleInputDirectory, $combinedUploadStageDirectory)) {
        if (Test-Path -LiteralPath $stageDirectory) {
            Remove-Item -LiteralPath $stageDirectory -Recurse -Force
        }
    }

    Assert-StoreUploadPackageContainsBundle -PackagePath $combinedUploadPath

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

    Write-Host "Creating bundled Store upload package for $($platforms -join ', ')."
    $null = New-BundledStoreUploadPackage -PackageOutputDirectory $resolvedOutputDirectory -Platforms $platforms -TargetPlatformVersion $TargetPlatformVersion

    $uploadPackages = Get-WindowsPackageUploadFiles -Path $resolvedOutputDirectory

    if (-not $uploadPackages) {
        throw "No .appxupload or .msixupload file was created under $resolvedOutputDirectory."
    }

    if ($uploadPackages.Count -ne 1) {
        $actual = $uploadPackages | ForEach-Object { $_.Name }
        throw "Expected exactly one bundled Store upload package, but found: $($actual -join ', ')"
    }

    foreach ($uploadPackage in $uploadPackages) {
        Assert-StoreUploadPackageContainsBundle -PackagePath $uploadPackage.FullName
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
