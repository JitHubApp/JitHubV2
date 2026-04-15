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

function Get-PackagePublisherIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedProjectPath
    )

    $projectDirectory = Split-Path -Parent $ResolvedProjectPath
    $manifestPath = Join-Path $projectDirectory 'Package.appxmanifest'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Package manifest not found next to project: $manifestPath"
    }

    [xml]$manifest = Get-Content -LiteralPath $manifestPath
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaceManager.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identityNode = $manifest.SelectSingleNode('/appx:Package/appx:Identity', $namespaceManager)
    if ($null -eq $identityNode -or [string]::IsNullOrWhiteSpace($identityNode.Publisher)) {
        throw "Publisher identity was not found in $manifestPath"
    }

    return $identityNode.Publisher
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

    if ($Platforms.Count -gt 1) {
        $buildArguments += "/p:AppxBundlePlatforms=$($Platforms -join '|')"
    }

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
        $buildArguments += '/p:AppxPackageSigningEnabled=True'
        $buildArguments += '/p:GenerateTemporaryStoreCertificate=True'
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
$generatedCertificateThumbprint = $null
$packagePublisherIdentity = Get-PackagePublisherIdentity -ResolvedProjectPath $resolvedProjectPath
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
    else {
        $temporaryPasswordText = [Guid]::NewGuid().ToString('N')
        $temporaryPassword = ConvertTo-SecureString -String $temporaryPasswordText -AsPlainText -Force
        $temporaryCertificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $packagePublisherIdentity `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -FriendlyName 'JitHub Temporary Store Package'

        $certificatePath = Join-Path (Get-TransientTempPath) 'JitHub-WinUI-TemporaryStorePackage.pfx'
        Export-PfxCertificate -Cert $temporaryCertificate -FilePath $certificatePath -Password $temporaryPassword | Out-Null

        $effectiveCertificatePassword = $temporaryPasswordText
        $effectiveCertificateThumbprint = $temporaryCertificate.Thumbprint
        $generatedCertificateThumbprint = $temporaryCertificate.Thumbprint
    }

    Invoke-StoreUploadBuild `
        -ResolvedProjectPath $resolvedProjectPath `
        -Platforms $platforms `
        -PackageOutputDirectory $resolvedOutputDirectory `
        -ResolvedTargetPlatformVersion $TargetPlatformVersion `
        -SignPackage `
        -CertificatePath $certificatePath `
        -CertificatePasswordValue $effectiveCertificatePassword `
        -CertificateThumbprintValue $effectiveCertificateThumbprint

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
    if ($generatedCertificateThumbprint) {
        Get-ChildItem -LiteralPath "Cert:\CurrentUser\My\$generatedCertificateThumbprint" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    if ($certificatePath -and (Test-Path -LiteralPath $certificatePath)) {
        Remove-Item -LiteralPath $certificatePath -Force
    }
}
