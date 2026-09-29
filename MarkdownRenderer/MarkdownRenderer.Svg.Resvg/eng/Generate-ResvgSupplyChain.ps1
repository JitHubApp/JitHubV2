param()

$ErrorActionPreference = 'Stop'
$provider = Split-Path -Parent $PSScriptRoot
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

$metadataText = & cargo metadata --manifest-path $manifest --locked --offline --format-version 1 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "cargo metadata failed in locked offline mode: $metadataText"
}
$metadata = $metadataText | ConvertFrom-Json -Depth 64
$locked = Get-LockedPackages $lockFile
$existing = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json -Depth 32
$overrides = @{}
foreach ($property in $existing.licensePolicy.selectionOverrides.PSObject.Properties) {
    $overrides[$property.Name] = [string]$property.Value
}
$licenseEvidence = @{}

$packages = @($metadata.packages | Sort-Object name, version | ForEach-Object {
    $identity = "$($_.name)@$($_.version)"
    if (-not $locked.ContainsKey($identity)) {
        throw "Cargo metadata package $identity is absent from Cargo.lock."
    }
    $selection = if ($overrides.ContainsKey($identity)) {
        $overrides[$identity]
    } else {
        [string]$existing.licensePolicy.defaultLicenseSelection
    }
    $evidenceHashes = @()
    $manifestDirectory = Split-Path -Parent ([string]$_.manifest_path)
    $licenseFiles = @(Get-ChildItem -LiteralPath $manifestDirectory -File | Where-Object Name -Match '^(LICENSE|LICENCE|COPYING|UNLICENSE|COPYRIGHT|NOTICE)([-_.].*)?$' | Sort-Object Name)
    foreach ($licenseFile in $licenseFiles) {
        $bytes = [IO.File]::ReadAllBytes($licenseFile.FullName)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $evidenceHashes += $hash
        if (-not $licenseEvidence.ContainsKey($hash)) {
            $licenseEvidence[$hash] = [ordered]@{
                packages = [Collections.Generic.List[string]]::new()
                files = [Collections.Generic.List[string]]::new()
                text = [Text.Encoding]::UTF8.GetString($bytes)
            }
        }
        if (-not $licenseEvidence[$hash].packages.Contains($identity)) {
            $licenseEvidence[$hash].packages.Add($identity)
        }
        $fileIdentity = "$identity/$($licenseFile.Name)"
        if (-not $licenseEvidence[$hash].files.Contains($fileIdentity)) {
            $licenseEvidence[$hash].files.Add($fileIdentity)
        }
    }
    [ordered]@{
        name = [string]$_.name
        version = [string]$_.version
        license = [string]$_.license
        selectedLicense = $selection
        source = $locked[$identity].source
        checksum = $locked[$identity].checksum
        licenseEvidenceSha256 = @($evidenceHashes | Sort-Object -Unique)
    }
})

$document = [ordered]@{
    schemaVersion = 2
    bomFormat = [string]$existing.bomFormat
    generatedFrom = [string]$existing.generatedFrom
    packageCount = $packages.Count
    resvgCommit = [string]$existing.resvgCommit
    licensePolicy = $existing.licensePolicy
    components = @($packages | ForEach-Object { "$($_.name)@$($_.version)" })
    packages = $packages
}

$json = $document | ConvertTo-Json -Depth 16
[IO.File]::WriteAllText($sbomPath, "$json`n", [Text.UTF8Encoding]::new($false))

$notices = [Text.StringBuilder]::new()
[void]$notices.AppendLine('Rust dependency license evidence for MarkdownRenderer.Svg.Resvg')
[void]$notices.AppendLine('Generated from the exact Cargo metadata and package source files referenced by native/Cargo.lock.')
[void]$notices.AppendLine('The NuGet package itself and the local worker crate are licensed under the repository MIT license.')
[void]$notices.AppendLine()
foreach ($hash in @($licenseEvidence.Keys | Sort-Object)) {
    $item = $licenseEvidence[$hash]
    [void]$notices.AppendLine(('=' * 78))
    [void]$notices.AppendLine("[sha256: $hash]")
    [void]$notices.AppendLine("Packages: $(@($item.packages | Sort-Object) -join ', ')")
    [void]$notices.AppendLine("Source files: $(@($item.files | Sort-Object) -join ', ')")
    [void]$notices.AppendLine()
    [void]$notices.AppendLine($item.text.TrimEnd())
    [void]$notices.AppendLine()
}
[IO.File]::WriteAllText($licenseNoticesPath, $notices.ToString(), [Text.UTF8Encoding]::new($false))
Write-Output "Generated $($packages.Count) locked Rust package records and $($licenseEvidence.Count) deduplicated license texts."
