[CmdletBinding()]
param(
    [switch] $RequireValid,

    [string] $WorkerRoot = '',

    [string] $ExpectedSignerThumbprint,

    [switch] $RequireTimestamp,

    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$provider = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($WorkerRoot)) {
    $WorkerRoot = $provider
}
if (-not (Test-Path -LiteralPath $WorkerRoot -PathType Container)) {
    throw "Worker directory '$WorkerRoot' does not exist."
}
$resolvedWorkerRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $WorkerRoot).Path)
$expectedThumbprint = if ([string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    ''
}
else {
    $normalized = $ExpectedSignerThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40}$') {
        throw 'ExpectedSignerThumbprint must contain exactly 40 hexadecimal characters.'
    }
    $normalized
}
$workers = @(
    'runtimes\win-x86\native\MarkdownRenderer.Svg.Resvg.Worker.exe',
    'runtimes\win-x64\native\MarkdownRenderer.Svg.Resvg.Worker.exe',
    'runtimes\win-arm64\native\MarkdownRenderer.Svg.Resvg.Worker.exe'
)

$results = [Collections.Generic.List[object]]::new()
foreach ($relativePath in $workers) {
    $path = Join-Path $resolvedWorkerRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing resvg worker $relativePath."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    $valid = $signature.Status -eq [Management.Automation.SignatureStatus]::Valid
    $signerThumbprint = if ($null -eq $signature.SignerCertificate) { '' } else { $signature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant() }
    $signerMatches = [string]::IsNullOrEmpty($expectedThumbprint) -or $signerThumbprint -ceq $expectedThumbprint
    $timestampPresent = $null -ne $signature.TimeStamperCertificate
    $accepted = $valid -and $signerMatches -and (-not $RequireTimestamp -or $timestampPresent)
    $results.Add([pscustomobject][ordered]@{
        path = $relativePath.Replace('\', '/')
        status = $signature.Status.ToString()
        valid = $valid
        accepted = $accepted
        signerSubject = if ($null -eq $signature.SignerCertificate) { $null } else { $signature.SignerCertificate.Subject }
        signerThumbprint = if ($null -eq $signature.SignerCertificate) { $null } else { $signerThumbprint }
        timestampSubject = if ($null -eq $signature.TimeStamperCertificate) { $null } else { $signature.TimeStamperCertificate.Subject }
        timestampPresent = $timestampPresent
    })
}

$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    signaturesRequired = [bool] $RequireValid
    expectedSignerThumbprint = if ([string]::IsNullOrEmpty($expectedThumbprint)) { $null } else { $expectedThumbprint }
    timestampRequired = [bool] $RequireTimestamp
    allValid = @($results | Where-Object { -not $_.accepted }).Count -eq 0
    workers = @($results)
}
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
    [IO.File]::WriteAllText(
        $resolvedOutput,
        ($report | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false))
}

if ($report.allValid) {
    Write-Output 'Verified Authenticode signatures for all resvg workers.'
}
elseif ($RequireValid) {
    $invalidSummary = @($results | Where-Object { -not $_.valid } |
        ForEach-Object { "$($_.path) ($($_.status))" }) -join ', '
    $policyFailureSummary = @($results | Where-Object { -not $_.accepted } | ForEach-Object {
        $failed = [Collections.Generic.List[string]]::new()
        if (-not $_.valid) { $failed.Add("status=$($_.status)") }
        if (-not $_.accepted -and -not [string]::IsNullOrEmpty($expectedThumbprint) -and
            $_.signerThumbprint -cne $expectedThumbprint) { $failed.Add('signer-mismatch') }
        if ($RequireTimestamp -and -not $_.timestampPresent) { $failed.Add('missing-timestamp') }
        "$($_.path) ($($failed -join ', '))"
    }) -join ', '
    if (-not [string]::IsNullOrWhiteSpace($invalidSummary)) {
        throw "One or more resvg workers failed Authenticode validation: $invalidSummary."
    }
    throw "One or more resvg workers failed the selected signer/timestamp policy: $policyFailureSummary."
}
else {
    Write-Warning 'One or more resvg workers do not satisfy the requested signature policy. Production release packaging must use the signed worker staging directory and set RequireResvgWorkerSignatures=true.'
}
