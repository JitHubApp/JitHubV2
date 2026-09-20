param()

$ErrorActionPreference = 'Stop'
$provider = Split-Path -Parent $PSScriptRoot
$repositoryPolicyPath = Join-Path (Split-Path -Parent $provider) 'eng\license-compatibility-policy.json'
$manifest = Join-Path $provider 'native\Cargo.toml'
$lockFile = Join-Path $provider 'native\Cargo.lock'
$sbomPath = Join-Path $provider 'NATIVE_RUST_DEPENDENCIES.json'
$licenseNoticesPath = Join-Path $provider 'licenses\RUST-DEPENDENCY-LICENSES.txt'

function Get-LockedPackages([string] $path) {
    $text = Get-Content -LiteralPath $path -Raw
    $result = @{}
    foreach ($match in [regex]::Matches($text, '(?ms)^\[\[package\]\]\r?\n(?<body>.*?)(?=^\[\[package\]\]|\z)')) {
        $body = $match.Groups['body'].Value
        $nameMatch = [regex]::Match($body, '(?m)^name = "(?<value>[^"]+)"$')
        $versionMatch = [regex]::Match($body, '(?m)^version = "(?<value>[^"]+)"$')
        if (-not $nameMatch.Success -or -not $versionMatch.Success) {
            throw 'Cargo.lock contains a package without a name or version.'
        }
        $sourceMatch = [regex]::Match($body, '(?m)^source = "(?<value>[^"]+)"$')
        $checksumMatch = [regex]::Match($body, '(?m)^checksum = "(?<value>[0-9a-f]+)"$')
        $identity = "$($nameMatch.Groups['value'].Value)@$($versionMatch.Groups['value'].Value)"
        if ($result.ContainsKey($identity)) {
            throw "Cargo.lock contains ambiguous duplicate identity $identity."
        }
        $result[$identity] = [ordered]@{
            source = if ($sourceMatch.Success) { $sourceMatch.Groups['value'].Value } else { $null }
            checksum = if ($checksumMatch.Success) { $checksumMatch.Groups['value'].Value } else { $null }
        }
    }
    return $result
}

if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
    throw 'The pinned resvg Cargo.lock is missing.'
}

$sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json -Depth 32
$policy = Get-Content -LiteralPath $repositoryPolicyPath -Raw | ConvertFrom-Json -Depth 32
$cargoErrorPath = [IO.Path]::GetTempFileName()
try {
    # Cargo can emit lock-wait and cache diagnostics on stderr even when the
    # command succeeds. Keep that stream out of the machine-readable JSON.
    $metadataText = (& cargo metadata --manifest-path $manifest --locked --offline --format-version 1 2> $cargoErrorPath) -join "`n"
    $cargoExitCode = $LASTEXITCODE
    $cargoError = if (Test-Path -LiteralPath $cargoErrorPath) {
        Get-Content -LiteralPath $cargoErrorPath -Raw
    }
    else {
        ''
    }
    if ($cargoExitCode -ne 0) {
        throw "cargo metadata failed in locked offline mode: $cargoError"
    }
}
finally {
    Remove-Item -LiteralPath $cargoErrorPath -Force -ErrorAction SilentlyContinue
}
$metadata = $metadataText | ConvertFrom-Json -Depth 64
$locked = Get-LockedPackages $lockFile
$licenseNotices = Get-Content -LiteralPath $licenseNoticesPath -Raw

if ($sbom.schemaVersion -lt 2 -or $null -eq $sbom.packages) {
    throw 'The native SBOM must contain full schema-v2 package records.'
}
if (@($sbom.licensePolicy.unknownLicenses).Count -ne 0) {
    throw "The native SBOM contains unknown licenses: $($sbom.licensePolicy.unknownLicenses -join ', ')."
}

$actual = @($metadata.packages | ForEach-Object { "$($_.name)@$($_.version)" } | Sort-Object)
$declared = @($sbom.components | Sort-Object)
$packageRecords = @($sbom.packages)
if ($actual.Count -ne $sbom.packageCount -or
    $declared.Count -ne $sbom.packageCount -or
    $packageRecords.Count -ne $sbom.packageCount) {
    throw "SBOM package count drift: metadata=$($actual.Count), components=$($declared.Count), records=$($packageRecords.Count), declared=$($sbom.packageCount)."
}
$difference = @(Compare-Object -ReferenceObject $actual -DifferenceObject $declared)
if ($difference.Count -ne 0) {
    throw "SBOM component inventory does not match Cargo.lock: $($difference | Out-String)"
}

$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($identifier in $policy.allowedSpdxIdentifiers) {
    [void]$allowed.Add([string]$identifier)
}
$metadataByIdentity = @{}
foreach ($package in $metadata.packages) {
    $identity = "$($package.name)@$($package.version)"
    if ($metadataByIdentity.ContainsKey($identity)) {
        throw "Cargo metadata contains ambiguous duplicate identity $identity."
    }
    $metadataByIdentity[$identity] = $package
}

$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($record in $packageRecords) {
    $identity = "$($record.name)@$($record.version)"
    if (-not $seen.Add($identity)) {
        throw "The native SBOM contains duplicate package record $identity."
    }
    if (-not $metadataByIdentity.ContainsKey($identity) -or -not $locked.ContainsKey($identity)) {
        throw "The native SBOM contains package $identity that is not in locked metadata."
    }
    $package = $metadataByIdentity[$identity]
    $lockedPackage = $locked[$identity]
    $expression = [string]$package.license
    if ([string]::IsNullOrWhiteSpace($expression) -or [string]$record.license -ne $expression) {
        throw "Unknown or stale license expression for $identity."
    }
    if ([string]$record.source -ne [string]$lockedPackage.source -or
        [string]$record.source -ne [string]$package.source) {
        throw "Source drift for $identity."
    }
    if ([string]$record.checksum -ne [string]$lockedPackage.checksum) {
        throw "Checksum drift for $identity."
    }
    if ([string]$record.source -like 'registry+*' -and
        [string]$record.checksum -notmatch '^[0-9a-f]{64}$') {
        throw "Registry package $identity lacks a locked SHA-256 checksum."
    }
    if ([string]$record.source -notlike 'registry+*' -and
        -not [string]::IsNullOrEmpty([string]$record.checksum)) {
        throw "Non-registry package $identity unexpectedly declares a registry checksum."
    }
    $evidenceHashes = @($record.licenseEvidenceSha256)
    if (-not [string]::IsNullOrEmpty([string]$record.source) -and $evidenceHashes.Count -eq 0) {
        throw "Dependency $identity has no vendored license evidence."
    }
    foreach ($evidenceHash in $evidenceHashes) {
        if ([string]$evidenceHash -notmatch '^[0-9a-f]{64}$' -or
            $licenseNotices -notmatch "\[sha256: $([regex]::Escape([string]$evidenceHash))\]") {
            throw "Dependency $identity references missing or malformed license evidence $evidenceHash."
        }
    }

    $selection = [string]$record.selectedLicense
    if ([string]::IsNullOrWhiteSpace($selection)) {
        throw "No selected license is recorded for $identity."
    }
    $selectedIdentifiers = @($selection -split '\s+AND\s+' | ForEach-Object { $_.Trim('(', ')', ' ') })
    foreach ($identifier in $selectedIdentifiers) {
        if (-not $allowed.Contains($identifier)) {
            throw "Selected license $identifier for $identity is not repository-compatible."
        }
        if ($expression -notmatch "(?<![A-Za-z0-9.-])$([regex]::Escape($identifier))(?![A-Za-z0-9.-])") {
            throw "Selected license $identifier is not offered by $identity ($expression)."
        }
    }
}

$resvg = @($metadata.packages | Where-Object name -EQ 'resvg')
if ($resvg.Count -ne 1 -or $resvg[0].version -ne '0.48.1' -or
    $resvg[0].source -notmatch '68b14c4c3bccdb60344c777406486b54c36ec1a4') {
    throw 'The resvg package is not pinned to the approved 0.48.1 source commit.'
}
if ($sbom.resvgCommit -ne '68b14c4c3bccdb60344c777406486b54c36ec1a4') {
    throw 'The native SBOM resvg commit is stale.'
}

Write-Output "Verified $($actual.Count) locked Rust packages, sources, checksums, and license selections."
