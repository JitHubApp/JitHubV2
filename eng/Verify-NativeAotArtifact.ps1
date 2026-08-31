param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [ValidateSet('x86', 'x64', 'arm64')]
    [string[]]$Architecture
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$resolvedInputPath = [System.IO.Path]::GetFullPath($InputPath)
if (-not (Test-Path -LiteralPath $resolvedInputPath)) {
    throw "Native AOT artifact not found: $resolvedInputPath"
}

$expectedMachines = @{
    x86 = 0x014c
    x64 = 0x8664
    arm64 = 0xaa64
}
$forbiddenRuntimeFiles = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('coreclr.dll', 'clrjit.dll', 'hostfxr.dll', 'hostpolicy.dll', 'createdump.exe'),
    [System.StringComparer]::OrdinalIgnoreCase)
$requiredNativeFiles = @(
    'JitHub.WinUI.exe',
    'e_sqlite3.dll',
    'libHarfBuzzSharp.dll',
    'libSkiaSharp.dll',
    'Microsoft.Graphics.Canvas.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'thorvg.dll',
    'WebView2Loader.dll',
    'WinUIEditor.dll'
)
$verifiedArchitectures = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$temporaryDirectories = [System.Collections.Generic.List[string]]::new()

function New-VerificationDirectory {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $path = Join-Path $tempRoot ("JitHub.NativeAot.Verify." + [System.Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    $temporaryDirectories.Add($path)
    return $path
}

function Expand-ZipArtifact {
    param([Parameter(Mandatory = $true)][string]$Path)

    $destination = New-VerificationDirectory
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Path, $destination)
    return $destination
}

function Get-PeInfo {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'Read')
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5a4d) {
                throw "File is not a valid PE image: $Path"
            }

            $stream.Position = 0x3c
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0 -or $peOffset + 256 -gt $stream.Length) {
                throw "PE header is out of range: $Path"
            }

            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "PE signature is invalid: $Path"
            }

            $machine = $reader.ReadUInt16()
            $stream.Position = $peOffset + 24
            $optionalHeaderStart = $stream.Position
            $optionalHeaderMagic = $reader.ReadUInt16()
            $dataDirectoryOffset = switch ($optionalHeaderMagic) {
                0x010b { 96 }
                0x020b { 112 }
                default { throw "Unsupported PE optional header 0x$($optionalHeaderMagic.ToString('X4')): $Path" }
            }

            $stream.Position = $optionalHeaderStart + $dataDirectoryOffset + (14 * 8)
            $clrRva = $reader.ReadUInt32()
            $clrSize = $reader.ReadUInt32()
            return [pscustomobject]@{
                Machine = [int]$machine
                ClrRva = $clrRva
                ClrSize = $clrSize
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-ManifestArchitecture {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    [xml]$manifest = Get-Content -LiteralPath $ManifestPath
    $value = [string]$manifest.Package.Identity.ProcessorArchitecture
    return $value.ToLowerInvariant()
}

function Assert-ActivationManifest {
    param(
        [Parameter(Mandatory = $true)][string]$LayoutPath,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$CurrentArchitecture
    )

    [xml]$manifest = Get-Content -LiteralPath $ManifestPath
    $namespace = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

    $application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespace)
    if (-not $application) {
        throw "Package manifest has no application entry: $ManifestPath"
    }

    $executable = [string]$application.Executable
    if ([string]::IsNullOrWhiteSpace($executable) -or -not (Test-Path -LiteralPath (Join-Path $LayoutPath $executable))) {
        throw "Package application executable is missing: $executable"
    }

    $dependencyNames = @($manifest.SelectNodes('/f:Package/f:Dependencies/f:PackageDependency', $namespace) |
        ForEach-Object { [string]$_.Name })
    $hasStoreEngagement = $dependencyNames -contains 'Microsoft.Services.Store.Engagement'
    if (-not $hasStoreEngagement) {
        throw "Store Engagement dependency is missing from the $CurrentArchitecture manifest."
    }

    $activationPaths = @($manifest.SelectNodes('//f:Extension[@Category="windows.activatableClass.inProcessServer"]/f:InProcessServer/f:Path', $namespace))
    foreach ($pathNode in $activationPaths) {
        $implementation = [string]$pathNode.InnerText
        if (-not (Test-Path -LiteralPath (Join-Path $LayoutPath $implementation))) {
            throw "WinRT activation manifest references a missing implementation: $implementation"
        }
    }

    $activationIds = @($manifest.SelectNodes('//f:ActivatableClass', $namespace) |
        ForEach-Object { [string]$_.ActivatableClassId })
    foreach ($requiredId in @('WinUIEditor.EditorBaseControl', 'WinUIEditor.XamlMetaDataProvider', 'WinUIEditor.CodeEditorControl')) {
        if ($activationIds -notcontains $requiredId) {
            throw "WinUIEdit activation class is missing from the manifest: $requiredId"
        }
    }
}

function Assert-NativeLayout {
    param(
        [Parameter(Mandatory = $true)][string]$LayoutPath,
        [Parameter(Mandatory = $true)][string]$CurrentArchitecture,
        [bool]$IsPackaged
    )

    if (-not $expectedMachines.ContainsKey($CurrentArchitecture)) {
        throw "Unsupported artifact architecture: $CurrentArchitecture"
    }

    foreach ($requiredFile in $requiredNativeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $LayoutPath $requiredFile))) {
            throw "Required $CurrentArchitecture native dependency is missing: $requiredFile"
        }
    }

    $forbidden = Get-ChildItem -LiteralPath $LayoutPath -Recurse -File |
        Where-Object { $forbiddenRuntimeFiles.Contains($_.Name) }
    if ($forbidden) {
        throw "CoreCLR/JIT host payload is forbidden: $($forbidden.FullName -join ', ')"
    }

    $managedHostMetadata = Get-ChildItem -LiteralPath $LayoutPath -Recurse -File |
        Where-Object { $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json' }
    if ($managedHostMetadata) {
        throw "Managed host metadata is forbidden in a Native AOT artifact: $($managedHostMetadata.Name -join ', ')"
    }

    if ($IsPackaged) {
        $packagedSymbols = Get-ChildItem -LiteralPath $LayoutPath -Recurse -File |
            Where-Object { $_.Extension -in @('.pdb', '.appxsym') }
        if ($packagedSymbols) {
            throw "Symbols must be published separately from the MSIX payload: $($packagedSymbols.Name -join ', ')"
        }
    }

    $peFiles = Get-ChildItem -LiteralPath $LayoutPath -Recurse -File |
        Where-Object { $_.Extension -in @('.exe', '.dll') }
    if (-not $peFiles) {
        throw "No executable PE files were found in $LayoutPath."
    }

    foreach ($file in $peFiles) {
        $pe = Get-PeInfo $file.FullName
        if ($pe.Machine -ne $expectedMachines[$CurrentArchitecture]) {
            throw "Wrong-machine binary in $CurrentArchitecture artifact: $($file.Name) is 0x$($pe.Machine.ToString('X4'))."
        }
        if ($pe.ClrRva -ne 0 -or $pe.ClrSize -ne 0) {
            throw "Managed IL/CLR PE header is forbidden: $($file.FullName)"
        }
    }

    $manifestPath = Join-Path $LayoutPath 'AppxManifest.xml'
    if (Test-Path -LiteralPath $manifestPath) {
        Assert-ActivationManifest -LayoutPath $LayoutPath -ManifestPath $manifestPath -CurrentArchitecture $CurrentArchitecture
    }

    $null = $verifiedArchitectures.Add($CurrentArchitecture)
    Write-Host "Verified native $CurrentArchitecture payload: $LayoutPath"
}

function Test-PackageArchive {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    $layout = Expand-ZipArtifact $PackagePath
    $manifestPath = Join-Path $layout 'AppxManifest.xml'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Package does not contain AppxManifest.xml: $PackagePath"
    }

    $packageArchitecture = Get-ManifestArchitecture $manifestPath
    Assert-NativeLayout -LayoutPath $layout -CurrentArchitecture $packageArchitecture -IsPackaged $true
}

function Test-BundleArchive {
    param([Parameter(Mandatory = $true)][string]$BundlePath)

    $layout = Expand-ZipArtifact $BundlePath
    $bundleManifestPath = Join-Path $layout 'AppxMetadata\AppxBundleManifest.xml'
    if (-not (Test-Path -LiteralPath $bundleManifestPath)) {
        throw "Bundle does not contain AppxBundleManifest.xml: $BundlePath"
    }

    [xml]$bundleManifest = Get-Content -LiteralPath $bundleManifestPath
    $namespace = [System.Xml.XmlNamespaceManager]::new($bundleManifest.NameTable)
    $namespace.AddNamespace('b', $bundleManifest.DocumentElement.NamespaceURI)
    $applicationPackages = @($bundleManifest.SelectNodes('//b:Package[@Type="application"]', $namespace))
    if (-not $applicationPackages) {
        throw "Bundle contains no application packages: $BundlePath"
    }

    foreach ($package in $applicationPackages) {
        $fileName = [string]$package.FileName
        Test-PackageArchive (Join-Path $layout $fileName)
    }
}

function Test-StoreUploadArchive {
    param([Parameter(Mandatory = $true)][string]$UploadPath)

    $upload = Expand-ZipArtifact $UploadPath
    $bundles = @(Get-ChildItem -LiteralPath $upload -File |
        Where-Object { $_.Extension -in @('.msixbundle', '.appxbundle') })
    $packages = @(Get-ChildItem -LiteralPath $upload -File |
        Where-Object { $_.Extension -in @('.msix', '.appx') })
    if ($bundles.Count + $packages.Count -eq 0) {
        throw 'Store upload contains no application package or app bundle.'
    }

    foreach ($bundle in $bundles) {
        Test-BundleArchive $bundle.FullName
    }
    foreach ($package in $packages) {
        Test-PackageArchive $package.FullName
    }
}

try {
    if (Test-Path -LiteralPath $resolvedInputPath -PathType Container) {
        if (-not $Architecture -or $Architecture.Count -ne 1) {
            throw 'A loose publish directory requires exactly one -Architecture value.'
        }
        Assert-NativeLayout -LayoutPath $resolvedInputPath -CurrentArchitecture $Architecture[0] -IsPackaged $false
    }
    else {
        $extension = [System.IO.Path]::GetExtension($resolvedInputPath).ToLowerInvariant()
        switch ($extension) {
            '.msix' { Test-PackageArchive $resolvedInputPath }
            '.appx' { Test-PackageArchive $resolvedInputPath }
            '.msixbundle' { Test-BundleArchive $resolvedInputPath }
            '.appxbundle' { Test-BundleArchive $resolvedInputPath }
            '.msixupload' { Test-StoreUploadArchive $resolvedInputPath }
            '.appxupload' { Test-StoreUploadArchive $resolvedInputPath }
            default { throw "Unsupported Native AOT artifact type: $extension" }
        }
    }

    if ($Architecture) {
        foreach ($expectedArchitecture in $Architecture) {
            if (-not $verifiedArchitectures.Contains($expectedArchitecture)) {
                throw "Artifact does not contain required architecture: $expectedArchitecture"
            }
        }
    }

    Write-Host "Native AOT artifact verification passed for $($verifiedArchitectures -join ', ')."
}
finally {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($directory in $temporaryDirectories) {
        $resolvedDirectory = [System.IO.Path]::GetFullPath($directory)
        if (-not $resolvedDirectory.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [System.IO.Path]::GetFileName($resolvedDirectory).StartsWith('JitHub.NativeAot.Verify.', [System.StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected verification directory: $resolvedDirectory"
        }
        if (Test-Path -LiteralPath $resolvedDirectory) {
            [System.IO.Directory]::Delete($resolvedDirectory, $true)
        }
    }
}
