[CmdletBinding()]
param(
    [switch] $RequireValid,

    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$provider = Split-Path -Parent $PSScriptRoot
$workers = @(
    'runtimes\win-x86\native\MarkdownRenderer.Svg.Resvg.Worker.exe',
    'runtimes\win-x64\native\MarkdownRenderer.Svg.Resvg.Worker.exe',
    'runtimes\win-arm64\native\MarkdownRenderer.Svg.Resvg.Worker.exe'
)

$results = [Collections.Generic.List[object]]::new()
foreach ($relativePath in $workers) {
    $path = Join-Path $provider $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing resvg worker $relativePath."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    $valid = $signature.Status -eq [Management.Automation.SignatureStatus]::Valid
    $results.Add([pscustomobject][ordered]@{
        path = $relativePath.Replace('\', '/')
        status = $signature.Status.ToString()
        valid = $valid
        signerSubject = if ($null -eq $signature.SignerCertificate) { $null } else { $signature.SignerCertificate.Subject }
        signerThumbprint = if ($null -eq $signature.SignerCertificate) { $null } else { $signature.SignerCertificate.Thumbprint }
        timestampSubject = if ($null -eq $signature.TimeStamperCertificate) { $null } else { $signature.TimeStamperCertificate.Subject }
    })
}

$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    signaturesRequired = [bool] $RequireValid
    allValid = @($results | Where-Object { -not $_.valid }).Count -eq 0
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
    throw "One or more resvg workers failed Authenticode validation: $invalidSummary."
}
else {
    Write-Warning 'One or more resvg workers are unsigned development artifacts. Production release packaging must set RequireResvgWorkerSignatures=true.'
}
