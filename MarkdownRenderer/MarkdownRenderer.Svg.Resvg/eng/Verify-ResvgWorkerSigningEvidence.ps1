[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $WorkerRoot,

    [Parameter(Mandatory)]
    [string] $UnsignedWorkerRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^(?i:[0-9a-f]{40})$')]
    [string] $ExpectedSignerThumbprint,

    [Parameter(Mandatory)]
    [ValidatePattern('^https?://[^\s]+$')]
    [string] $ExpectedTimestampServer
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

if (-not (Test-Path -LiteralPath $WorkerRoot -PathType Container)) {
    throw "Worker directory '$WorkerRoot' does not exist."
}
if (-not (Test-Path -LiteralPath $UnsignedWorkerRoot -PathType Container)) {
    throw "Unsigned worker directory '$UnsignedWorkerRoot' does not exist."
}
$rootPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $WorkerRoot).Path)
$unsignedRootPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $UnsignedWorkerRoot).Path)
$expectedThumbprint = $ExpectedSignerThumbprint.Replace(' ', '').ToUpperInvariant()
$maximumWorkerBytes = [long](3.5 * 1MB)
$evidencePath = Join-Path $rootPath 'worker-signing-evidence.json'
if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw "Signed worker evidence is missing from '$rootPath'."
}
$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -Depth 16
$signerSource = [string]$evidence.signerSource
$certificateStore = [string]$evidence.certificateStore
$validSignerSource =
    ($signerSource -ceq 'certificate-store' -and $certificateStore -cin @('CurrentUser\My', 'LocalMachine\My')) -or
    ($signerSource -ceq 'pfx-file' -and [string]::IsNullOrWhiteSpace($certificateStore))
if ([int]$evidence.schemaVersion -ne 1 -or
    -not $validSignerSource -or
    [string]$evidence.expectedSignerThumbprint -cne $expectedThumbprint -or
    [string]$evidence.timestampServer -cne $ExpectedTimestampServer -or
    [string]$evidence.timestampServer -notmatch '^https?://[^\s]+$' -or
    -not [bool]$evidence.timestampRequired -or
    [string]$evidence.hashAlgorithm -cne 'SHA256') {
    throw 'Signed worker evidence does not match the requested signer and timestamp policy.'
}

$expected = [ordered]@{
    'win-x86' = 'x86'
    'win-x64' = 'x64'
    'win-arm64' = 'arm64'
}
$records = @($evidence.workers)
if ($records.Count -ne $expected.Count) {
    throw "Signed worker evidence must contain exactly $($expected.Count) architecture records."
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($record in $records) {
    $rid = [string]$record.rid
    if (-not $expected.Contains($rid) -or -not $seen.Add($rid)) {
        throw "Signed worker evidence contains an unexpected or duplicate RID '$rid'."
    }
    $relativePath = "runtimes\$rid\native\MarkdownRenderer.Svg.Resvg.Worker.exe"
    $workerPath = Join-Path $rootPath $relativePath
    $unsignedPath = Join-Path $unsignedRootPath $relativePath
    if (-not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
        throw "Signed worker '$relativePath' is missing."
    }
    if (-not (Test-Path -LiteralPath $unsignedPath -PathType Leaf)) {
        throw "Corresponding unsigned source worker '$relativePath' is missing."
    }
    $worker = Get-Item -LiteralPath $workerPath
    $unsignedWorker = Get-Item -LiteralPath $unsignedPath
    if ($worker.Length -gt $maximumWorkerBytes -or $unsignedWorker.Length -gt $maximumWorkerBytes) {
        throw "Signed or unsigned worker '$relativePath' exceeds the existing 3.5 MiB selected-deployment limit."
    }
    $hash = Get-Sha256 $workerPath
    $sourceHash = Get-Sha256 $unsignedPath
    $machine = Get-PeMachine $workerPath
    $sourceMachine = Get-PeMachine $unsignedPath
    $sourceSignature = Get-AuthenticodeSignature -LiteralPath $unsignedPath
    if ($machine -cne $expected[$rid] -or
        $machine -cne [string]$record.machine -or
        [long]$worker.Length -ne [long]$record.bytes -or
        $hash -cne [string]$record.signedSha256 -or
        $sourceMachine -cne $expected[$rid] -or
        [long]$unsignedWorker.Length -ne [long]$record.sourceBytes -or
        $sourceHash -cne [string]$record.sourceSha256 -or
        $sourceSignature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
        throw "Signed worker '$relativePath' or its unsigned source differs from the recorded architecture, length, hash, or unsigned-source state."
    }
    if ($hash -notmatch '^[0-9a-f]{64}$' -or
        [string]$record.sourceSha256 -notmatch '^[0-9a-f]{64}$' -or
        [string]$record.signatureStatus -cne 'Valid' -or
        [string]$record.signerThumbprint -cne $expectedThumbprint -or
        [string]::IsNullOrWhiteSpace([string]$record.timestampSubject)) {
        throw "Signed worker evidence for '$relativePath' is incomplete or not valid."
    }

    $null = Test-ResvgPePreimage $unsignedPath $workerPath

    $signature = Get-AuthenticodeSignature -LiteralPath $workerPath
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant() -cne $expectedThumbprint -or
        $null -eq $signature.TimeStamperCertificate -or
        $signature.TimeStamperCertificate.Subject -cne [string]$record.timestampSubject) {
        throw "Authenticode trust, signer, or timestamp verification failed for '$relativePath'."
    }
}

Write-Output "Verified signed worker evidence and trusted Authenticode for $($records.Count) RIDs."
