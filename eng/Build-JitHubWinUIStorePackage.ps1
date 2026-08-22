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

function Test-IsStrictChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ChildPath,

        [Parameter(Mandatory = $true)]
        [string]$ParentPath
    )

    $relativePath = [System.IO.Path]::GetRelativePath(
        (Resolve-AbsolutePath -Path $ParentPath),
        (Resolve-AbsolutePath -Path $ChildPath))
    return $relativePath -ne '.' -and
        -not [System.IO.Path]::IsPathRooted($relativePath) -and
        $relativePath -ne '..' -and
        -not $relativePath.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::Ordinal)
}

function Remove-VerifiedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$RequiredParent,
        [string[]]$ProtectedPaths = @()
    )

    $resolvedPath = Resolve-AbsolutePath -Path $Path
    $rootPath = [System.IO.Path]::GetPathRoot($resolvedPath)
    if ([string]::Equals(
            $resolvedPath.TrimEnd('\', '/'),
            $rootPath.TrimEnd('\', '/'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to recursively remove a filesystem root: $resolvedPath"
    }

    if (-not [string]::IsNullOrWhiteSpace($RequiredParent) -and
        -not (Test-IsStrictChildPath -ChildPath $resolvedPath -ParentPath $RequiredParent)) {
        throw "Refusing to recursively remove '$resolvedPath' because it is not a strict child of '$RequiredParent'."
    }

    foreach ($protectedPath in $ProtectedPaths) {
        if ([string]::IsNullOrWhiteSpace($protectedPath)) {
            continue
        }

        $resolvedProtectedPath = Resolve-AbsolutePath -Path $protectedPath
        if ([string]::Equals($resolvedPath, $resolvedProtectedPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Test-IsStrictChildPath -ChildPath $resolvedProtectedPath -ParentPath $resolvedPath)) {
            throw "Refusing to recursively remove '$resolvedPath' because it contains protected path '$resolvedProtectedPath'."
        }
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        $item = Get-Item -LiteralPath $resolvedPath
        if (-not $item.PSIsContainer) {
            throw "Expected a directory before recursive removal: $resolvedPath"
        }

        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
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

function Get-NativeArchitecture {
    param([Parameter(Mandatory = $true)][string]$Platform)

    switch ($Platform.ToUpperInvariant()) {
        'X86' { return 'x86' }
        'X64' { return 'x64' }
        'ARM64' { return 'arm64' }
        default { throw "Unsupported Store package platform: $Platform" }
    }
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

function Get-PackageManifestMetadata {
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

        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        return [pscustomobject]@{
            Architecture = [string]$manifest.Package.Identity.ProcessorArchitecture
            Dependencies = [string]$manifest.Package.Dependencies.OuterXml
            Version = [string]$manifest.Package.Identity.Version
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

        $looseArchitecturePackages = @($rootEntries |
            Where-Object { [System.IO.Path]::GetExtension($_.FullName) -in '.appx', '.msix' }
        )

        Write-Host "Verified Store upload package contains $(@($bundleEntries).Count) bundle(s) and $($looseArchitecturePackages.Count) standalone architecture package(s)."
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
        Remove-VerifiedDirectory -Path $stageDirectory -RequiredParent $PackageOutputDirectory

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

    $firstPackageName = $architecturePackages[0].BaseName
    $packagePrefix = $firstPackageName -replace '_(x86|x64|arm|arm64)$', ''
    $platformLabel = ($Platforms | ForEach-Object { $_.Trim() }) -join '_'
    $uploadExtension = $singleArchitectureUploads[0].Extension
    $combinedUploadPath = Join-Path $PackageOutputDirectory "$packagePrefix`_$platformLabel`_bundle$uploadExtension"

    if (Test-Path -LiteralPath $combinedUploadPath) {
        Remove-Item -LiteralPath $combinedUploadPath -Force
    }

    $makeAppxPath = Get-MakeAppxPath -TargetPlatformVersion $TargetPlatformVersion
    $packageRecords = foreach ($architecturePackage in $architecturePackages) {
        $metadata = Get-PackageManifestMetadata -PackagePath $architecturePackage.FullName
        [pscustomobject]@{
            File = $architecturePackage
            Architecture = $metadata.Architecture
            Dependencies = $metadata.Dependencies
            Version = $metadata.Version
        }
    }

    $bundleCount = 0
    $groupIndex = 0
    foreach ($dependencyGroup in @($packageRecords | Group-Object Dependencies)) {
        $groupIndex++
        $groupRecords = @($dependencyGroup.Group | Sort-Object Architecture)
        if ($groupRecords.Count -eq 1) {
            Copy-Item -LiteralPath $groupRecords[0].File.FullName -Destination $combinedUploadStageDirectory -Force
            continue
        }

        $groupInputDirectory = Join-Path $bundleInputDirectory "dependency-group-$groupIndex"
        New-Item -ItemType Directory -Path $groupInputDirectory -Force | Out-Null
        foreach ($record in $groupRecords) {
            Copy-Item -LiteralPath $record.File.FullName -Destination $groupInputDirectory -Force
        }

        $architectureLabel = ($groupRecords | ForEach-Object { $_.Architecture }) -join '_'
        $bundlePath = Join-Path $combinedUploadStageDirectory "$packagePrefix`_$architectureLabel.msixbundle"
        $makeAppxArguments = @(
            'bundle'
            '/d'
            $groupInputDirectory
            '/p'
            $bundlePath
            '/o'
            '/bv'
            $groupRecords[0].Version
        )

        Write-Host "Creating Store app bundle for $architectureLabel with $makeAppxPath."
        & $makeAppxPath @makeAppxArguments
        if ($LASTEXITCODE -ne 0) {
            throw "makeappx bundle failed for $architectureLabel."
        }

        $bundleCount++
    }

    if ($bundleCount -eq 0) {
        throw 'The Store upload must retain at least one app bundle because this app previously shipped as a bundle.'
    }

    $symbolPackages = Get-ChildItem -Path $uploadStageDirectory -File |
        Where-Object { $_.Extension -eq '.appxsym' } |
        Sort-Object Name

    foreach ($symbolPackage in $symbolPackages) {
        Copy-Item -LiteralPath $symbolPackage.FullName -Destination $combinedUploadStageDirectory -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($combinedUploadStageDirectory, $combinedUploadPath)

    foreach ($singleArchitectureUpload in $singleArchitectureUploads) {
        Remove-Item -LiteralPath $singleArchitectureUpload.FullName -Force
    }

    foreach ($stageDirectory in @($uploadStageDirectory, $bundleInputDirectory, $combinedUploadStageDirectory)) {
        Remove-VerifiedDirectory -Path $stageDirectory -RequiredParent $PackageOutputDirectory
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
    $nativeArchitecture = Get-NativeArchitecture -Platform $primaryPlatform
    $runtimeIdentifier = "win-$nativeArchitecture"
    $bundleMode = if ($Platforms.Count -gt 1) { 'Always' } else { 'Never' }

    $buildArguments = @(
        'msbuild'
        $ResolvedProjectPath
        '/m'
        '/p:Configuration=Release'
        "/p:Platform=$primaryPlatform"
        "/p:RuntimeIdentifier=$runtimeIdentifier"
        '/p:PublishAot=true'
        '/p:PublishTrimmed=true'
        '/p:SelfContained=true'
        '/p:PublishReadyToRun=false'
        '/p:RestoreLockedMode=true'
        '/p:SkipReleaseSecurityGate=true'
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
$repositoryRoot = Resolve-AbsolutePath -Path (Join-Path $PSScriptRoot '..')

Remove-VerifiedDirectory `
    -Path $resolvedOutputDirectory `
    -ProtectedPaths @($repositoryRoot, $resolvedProjectPath, $PSScriptRoot)

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$certificatePath = $null
$effectiveCertificatePassword = $PackageCertificatePassword
$effectiveCertificateThumbprint = $PackageCertificateThumbprint
try {
    & (Join-Path $PSScriptRoot 'Update-NativeAotDependencyLedger.ps1') -Verify

    $testProjectPath = Join-Path (Split-Path -Parent $resolvedProjectPath) '..\JitHub.WinUI.Tests\JitHub.WinUI.Tests.csproj'
    $resolvedTestProjectPath = Resolve-AbsolutePath -Path $testProjectPath
    & dotnet restore $resolvedTestProjectPath `
        -p:Platform=x64 `
        -p:RuntimeIdentifier=win-x64 `
        --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw 'Locked release-security test restore failed.'
    }

    & dotnet test $resolvedTestProjectPath `
        -c Release `
        -p:Platform=x64 `
        -p:RuntimeIdentifier=win-x64 `
        -p:SkipReleaseSecurityGate=true `
        --filter 'Category=ReleaseSecurity' `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'Release security validation failed.'
    }

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

        $nativeArchitecture = Get-NativeArchitecture -Platform $platform
        & (Join-Path $PSScriptRoot 'Restore-NativeAot.ps1') -Architecture $nativeArchitecture

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

        $architecturePackage = Get-ChildItem -Path $resolvedOutputDirectory -Recurse -File |
            Where-Object {
                $_.Extension -in @('.appx', '.msix') -and
                $_.BaseName -match "_$([System.Text.RegularExpressions.Regex]::Escape($platform))($|_)"
            } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $architecturePackage) {
            throw "No architecture package was produced for $platform."
        }

        & (Join-Path $PSScriptRoot 'Verify-NativeAotArtifact.ps1') `
            -InputPath $architecturePackage.FullName `
            -Architecture $nativeArchitecture
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
        $expectedArchitectures = @($platforms | ForEach-Object { Get-NativeArchitecture -Platform $_ })
        & (Join-Path $PSScriptRoot 'Verify-NativeAotArtifact.ps1') `
            -InputPath $uploadPackage.FullName `
            -Architecture $expectedArchitectures
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
