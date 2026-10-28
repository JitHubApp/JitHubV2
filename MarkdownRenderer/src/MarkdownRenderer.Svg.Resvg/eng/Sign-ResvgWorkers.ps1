[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $InputDirectory,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [string] $CertificatePath,

    [ValidateSet('CurrentUser\My', 'LocalMachine\My')]
    [string] $CertificateStore = 'CurrentUser\My',

    [Parameter(Mandatory)]
    [ValidatePattern('^(?i:[0-9a-f]{40})$')]
    [string] $ExpectedSignerThumbprint,

    [Parameter(Mandatory)]
    [ValidatePattern('^https?://[^\s]+$')]
    [string] $TimestampServer,

    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string] $PasswordEnvironmentVariable = 'RESVG_WORKER_SIGNING_PFX_PASSWORD'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ResvgAuthenticodeImage.ps1')

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PeMachine([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "Worker '$Path' is not a valid PE image."
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or
        $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "Worker '$Path' has no valid PE signature."
    }
    switch ([BitConverter]::ToUInt16($bytes, $peOffset + 4)) {
        332 { return 'x86' }
        34404 { return 'x64' }
        43620 { return 'arm64' }
        default { return 'unknown' }
    }
}

function Test-PathWithin([string] $Path, [string] $Directory) {
    $normalizedDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $normalizedPath = [IO.Path]::GetFullPath($Path)
    return $normalizedPath.StartsWith($normalizedDirectory, [StringComparison]::OrdinalIgnoreCase)
}

$providerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $providerRoot '..\..\..'))
if (-not (Test-Path -LiteralPath $InputDirectory -PathType Container)) {
    throw "Input worker directory '$InputDirectory' does not exist."
}
$inputRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $InputDirectory).Path)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$certificateFullPath = $null
$expectedThumbprint = $ExpectedSignerThumbprint.Replace(' ', '').ToUpperInvariant()
$maximumWorkerBytes = [long](4500KB)

if (Test-Path -LiteralPath $outputPath) {
    throw "Output directory already exists; refusing to overwrite it: '$outputPath'."
}
if ((Test-PathWithin $outputPath $inputRoot) -or (Test-PathWithin $inputRoot $outputPath)) {
    throw 'Input and output worker trees must be separate; refusing an overlapping staging path.'
}
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $certificateFullPath = [IO.Path]::GetFullPath($CertificatePath)
    if (Test-PathWithin $certificateFullPath $repositoryRoot) {
        throw 'Signing certificate files must be kept outside the repository checkout.'
    }
    if (-not (Test-Path -LiteralPath $certificateFullPath -PathType Leaf)) {
        throw "The caller-selected signing certificate file does not exist: '$certificateFullPath'."
    }
}

$expected = [ordered]@{
    'win-x86' = 'x86'
    'win-x64' = 'x64'
    'win-arm64' = 'arm64'
}
$sources = [Collections.Generic.List[object]]::new()
foreach ($rid in $expected.Keys) {
    $relativePath = "runtimes\$rid\native\MarkdownRenderer.Svg.Resvg.Worker.exe"
    $sourcePath = Join-Path $inputRoot $relativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required unsigned worker '$relativePath' is missing from the input tree."
    }
    $sourceItem = Get-Item -LiteralPath $sourcePath
    if ($sourceItem.Length -gt $maximumWorkerBytes) {
        throw "Worker '$relativePath' exceeds the 4,500 KiB selected-deployment limit."
    }
    $machine = Get-PeMachine $sourcePath
    if ($machine -cne $expected[$rid]) {
        throw "Worker '$relativePath' is $machine, expected $($expected[$rid])."
    }
    $sourceSignature = Get-AuthenticodeSignature -LiteralPath $sourcePath
    if ($sourceSignature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
        throw "Input worker '$relativePath' must be an unsigned build artifact; found status $($sourceSignature.Status)."
    }
    $sources.Add([pscustomobject]@{
        Rid = $rid
        RelativePath = $relativePath
        Path = $sourcePath
        Bytes = [long]$sourceItem.Length
        Machine = $machine
        Sha256 = Get-Sha256 $sourcePath
    })
}

$certificate = $null
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $storeLocation = if ($CertificateStore -ceq 'LocalMachine\My') {
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
    }
    else {
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
    }
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::My,
        $storeLocation)
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = @($store.Certificates | Where-Object {
            $_.Thumbprint.Replace(' ', '').ToUpperInvariant() -ceq $expectedThumbprint
        })
        if ($matches.Count -ne 1) {
            throw "The exact caller-selected signer '$expectedThumbprint' was not found exactly once in '$CertificateStore'."
        }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [Security.Cryptography.X509Certificates.X509Certificate2]$matches[0])
    }
    finally {
        $store.Close()
    }
}
else {
    $password = [Environment]::GetEnvironmentVariable($PasswordEnvironmentVariable, [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrEmpty($password)) {
        throw "The caller must provide the PFX password through process environment variable '$PasswordEnvironmentVariable'; the value is never accepted on the command line."
    }
    $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
    $password = $null
    try {
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $certificateFullPath,
            $securePassword,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    }
    finally {
        $securePassword.Dispose()
    }
}

try {
    if (-not $certificate.HasPrivateKey -or
        $certificate.NotBefore -gt [DateTime]::Now -or
        $certificate.NotAfter -le [DateTime]::Now) {
        throw 'The selected signing certificate is not currently valid or does not contain a private key.'
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $eku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { ([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_).EnhancedKeyUsages } |
        ForEach-Object { $_.Value })
    if ($codeSigningOid -notin $eku) {
        throw 'The selected certificate does not declare the Code Signing extended key usage.'
    }
    if ($certificate.Thumbprint.Replace(' ', '').ToUpperInvariant() -cne $expectedThumbprint) {
        throw 'The selected certificate thumbprint differs from the caller-selected expected signer.'
    }

    $outputParent = [IO.Path]::GetDirectoryName($outputPath)
    if ([string]::IsNullOrWhiteSpace($outputParent)) {
        throw 'Output directory must have an explicit parent path.'
    }
    [IO.Directory]::CreateDirectory($outputParent) | Out-Null
    $stagePath = Join-Path $outputParent ('.resvg-workers-stage-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $stagePath) {
        throw 'The generated signing stage path unexpectedly already exists.'
    }

    $committed = $false
    try {
        [IO.Directory]::CreateDirectory($stagePath) | Out-Null
        $workerEvidence = [Collections.Generic.List[object]]::new()
        foreach ($source in $sources) {
            $destinationPath = Join-Path $stagePath $source.RelativePath
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
            [IO.File]::Copy($source.Path, $destinationPath, $false)
            if ((Get-Sha256 $source.Path) -cne $source.Sha256 -or
                (Get-Sha256 $destinationPath) -cne $source.Sha256) {
                throw "Input or staged worker '$($source.RelativePath)' changed before signing."
            }

            $null = Set-AuthenticodeSignature `
                -LiteralPath $destinationPath `
                -Certificate $certificate `
                -TimestampServer $TimestampServer `
                -HashAlgorithm SHA256 `
                -ErrorAction Stop
            $signature = Get-AuthenticodeSignature -LiteralPath $destinationPath
            $null = Test-ResvgPePreimage $source.Path $destinationPath
            if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
                $null -eq $signature.SignerCertificate -or
                $signature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant() -cne $expectedThumbprint -or
                $null -eq $signature.TimeStamperCertificate) {
                throw "Signed worker '$($source.RelativePath)' did not verify as trusted, timestamped, and signed by the selected certificate."
            }

            $signedItem = Get-Item -LiteralPath $destinationPath
            if ($signedItem.Length -gt $maximumWorkerBytes) {
                throw "Signed worker '$($source.RelativePath)' exceeds the 4,500 KiB selected-deployment limit."
            }
            $workerEvidence.Add([ordered]@{
                rid = $source.Rid
                machine = $source.Machine
                bytes = [long]$signedItem.Length
                sourceBytes = [long]$source.Bytes
                sourceSha256 = $source.Sha256
                signedSha256 = Get-Sha256 $destinationPath
                signatureStatus = $signature.Status.ToString()
                signerThumbprint = $signature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant()
                signerSubject = $signature.SignerCertificate.Subject
                timestampSubject = $signature.TimeStamperCertificate.Subject
            })
        }

        foreach ($source in $sources) {
            if ((Get-Sha256 $source.Path) -cne $source.Sha256) {
                throw "Input worker '$($source.RelativePath)' changed during signing; source files are never modified by this stage."
            }
        }

        $evidence = [ordered]@{
            schemaVersion = 1
            signerSource = if ([string]::IsNullOrWhiteSpace($CertificatePath)) { 'certificate-store' } else { 'pfx-file' }
            certificateStore = if ([string]::IsNullOrWhiteSpace($CertificatePath)) { $CertificateStore } else { $null }
            expectedSignerThumbprint = $expectedThumbprint
            timestampServer = $TimestampServer
            timestampRequired = $true
            hashAlgorithm = 'SHA256'
            workers = @($workerEvidence)
        }
        $evidenceJson = $evidence | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText(
            (Join-Path $stagePath 'worker-signing-evidence.json'),
            $evidenceJson + "`n",
            [Text.UTF8Encoding]::new($false))

        & (Join-Path $PSScriptRoot 'Verify-ResvgWorkerSigningEvidence.ps1') `
            -WorkerRoot $stagePath `
            -UnsignedWorkerRoot $inputRoot `
            -ExpectedSignerThumbprint $expectedThumbprint `
            -ExpectedTimestampServer $TimestampServer | Out-Null

        [IO.Directory]::Move($stagePath, $outputPath)
        $committed = $true
    }
    finally {
        if (-not $committed -and (Test-Path -LiteralPath $stagePath -PathType Container)) {
            $resolvedStage = [IO.Path]::GetFullPath($stagePath)
            $resolvedParent = [IO.Path]::GetFullPath($outputParent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if (-not $resolvedStage.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.resvg-workers-stage-[0-9a-f]{32}$') {
                throw 'Refusing to remove a signing stage outside its generated, validated temporary path.'
            }
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}
finally {
    if ($null -ne $certificate) {
        $certificate.Dispose()
    }
}

Write-Output "Signed and staged all three resvg workers with caller-selected signer $expectedThumbprint at '$outputPath'."
