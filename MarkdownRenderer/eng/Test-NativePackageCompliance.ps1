[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),

    [Parameter(Mandatory = $false)]
    [string] $AllowlistPath = (Join-Path $PSScriptRoot 'native-abi-allowlist.json'),

    [Parameter(Mandatory = $false)]
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\artifacts\native-package-compliance.json'),

    [switch] $InventoryOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackageCompliance.Common.ps1')

function Read-UInt16LE([byte[]] $Bytes, [int] $Offset) {
    if ($Offset -lt 0 -or $Offset + 2 -gt $Bytes.Length) { throw "PE read exceeds file bounds at offset $Offset." }
    return [BitConverter]::ToUInt16($Bytes, $Offset)
}

function Read-UInt32LE([byte[]] $Bytes, [int] $Offset) {
    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) { throw "PE read exceeds file bounds at offset $Offset." }
    return [BitConverter]::ToUInt32($Bytes, $Offset)
}

function Convert-RvaToFileOffset([uint32] $Rva, [object[]] $Sections, [int] $FileLength) {
    foreach ($section in $Sections) {
        $extent = [Math]::Max([uint64] $section.VirtualSize, [uint64] $section.RawSize)
        if ([uint64] $Rva -ge [uint64] $section.VirtualAddress -and [uint64] $Rva -lt ([uint64] $section.VirtualAddress + $extent)) {
            $offset = [uint64] $section.RawPointer + ([uint64] $Rva - [uint64] $section.VirtualAddress)
            if ($offset -ge [uint64] $FileLength) { throw "PE RVA 0x$($Rva.ToString('x')) maps outside the file." }
            return [int] $offset
        }
    }
    if ($Rva -lt $FileLength) { return [int] $Rva }
    throw "PE RVA 0x$($Rva.ToString('x')) does not map to a section."
}

function Read-AsciiZeroTerminated([byte[]] $Bytes, [int] $Offset) {
    $end = $Offset
    $maximum = [Math]::Min($Bytes.Length, $Offset + 65536)
    while ($end -lt $maximum -and $Bytes[$end] -ne 0) { $end++ }
    if ($end -eq $maximum) { throw "PE export name at offset $Offset is not terminated." }
    return [Text.Encoding]::ASCII.GetString($Bytes, $Offset, $end - $Offset)
}

function Get-PeEvidence([byte[]] $Bytes) {
    if ($Bytes.Length -lt 64 -or $Bytes[0] -ne 0x4d -or $Bytes[1] -ne 0x5a) { throw 'Native asset is not an MZ executable.' }
    $peOffset = [int] (Read-UInt32LE $Bytes 0x3c)
    if ($peOffset + 24 -gt $Bytes.Length -or $Bytes[$peOffset] -ne 0x50 -or $Bytes[$peOffset + 1] -ne 0x45 -or $Bytes[$peOffset + 2] -ne 0 -or $Bytes[$peOffset + 3] -ne 0) {
        throw 'Native asset has no valid PE signature.'
    }

    $machineValue = Read-UInt16LE $Bytes ($peOffset + 4)
    $machine = switch ($machineValue) {
        0x014c { 'x86' }
        0x8664 { 'x64' }
        0xaa64 { 'arm64' }
        default { 'unknown-0x' + $machineValue.ToString('x4') }
    }
    $sectionCount = Read-UInt16LE $Bytes ($peOffset + 6)
    $optionalHeaderSize = Read-UInt16LE $Bytes ($peOffset + 20)
    $optionalOffset = $peOffset + 24
    $optionalMagic = Read-UInt16LE $Bytes $optionalOffset
    $dataDirectoryOffset = switch ($optionalMagic) {
        0x010b { $optionalOffset + 96 }
        0x020b { $optionalOffset + 112 }
        default { throw "Unsupported PE optional header magic 0x$($optionalMagic.ToString('x4'))." }
    }
    $exportRva = Read-UInt32LE $Bytes $dataDirectoryOffset
    $exportSize = Read-UInt32LE $Bytes ($dataDirectoryOffset + 4)

    $sections = @()
    $sectionOffset = $optionalOffset + $optionalHeaderSize
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $sectionOffset + ($index * 40)
        if ($offset + 40 -gt $Bytes.Length) { throw 'PE section table exceeds file bounds.' }
        $sections += [pscustomobject]@{
            VirtualSize = Read-UInt32LE $Bytes ($offset + 8)
            VirtualAddress = Read-UInt32LE $Bytes ($offset + 12)
            RawSize = Read-UInt32LE $Bytes ($offset + 16)
            RawPointer = Read-UInt32LE $Bytes ($offset + 20)
        }
    }

    $exports = @()
    $functionCount = [uint32] 0
    $namedExportCount = [uint32] 0
    $ordinalOnlyCount = 0
    if ($exportRva -ne 0 -and $exportSize -ne 0) {
        $exportOffset = Convert-RvaToFileOffset $exportRva $sections $Bytes.Length
        $ordinalBase = Read-UInt32LE $Bytes ($exportOffset + 16)
        $functionCount = Read-UInt32LE $Bytes ($exportOffset + 20)
        $namedExportCount = Read-UInt32LE $Bytes ($exportOffset + 24)
        if ($functionCount -gt 1000000 -or $namedExportCount -gt 1000000) { throw "PE export count exceeds the validation budget (functions: $functionCount; names: $namedExportCount)." }
        $addressOfFunctionsRva = Read-UInt32LE $Bytes ($exportOffset + 28)
        $addressOfNamesRva = Read-UInt32LE $Bytes ($exportOffset + 32)
        $addressOfOrdinalsRva = Read-UInt32LE $Bytes ($exportOffset + 36)
        $addressOfFunctionsOffset = Convert-RvaToFileOffset $addressOfFunctionsRva $sections $Bytes.Length
        $addressOfNamesOffset = Convert-RvaToFileOffset $addressOfNamesRva $sections $Bytes.Length
        $addressOfOrdinalsOffset = Convert-RvaToFileOffset $addressOfOrdinalsRva $sections $Bytes.Length
        $namedFunctionIndexes = [Collections.Generic.HashSet[int]]::new()
        $discoveredExports = @(for ($index = 0; $index -lt $namedExportCount; $index++) {
            $nameRva = Read-UInt32LE $Bytes ($addressOfNamesOffset + ($index * 4))
            $nameOffset = Convert-RvaToFileOffset $nameRva $sections $Bytes.Length
            $functionIndex = Read-UInt16LE $Bytes ($addressOfOrdinalsOffset + ($index * 2))
            if ($functionIndex -ge $functionCount) { throw "PE named export references function index $functionIndex outside the function table." }
            [void] $namedFunctionIndexes.Add([int] $functionIndex)
            Read-AsciiZeroTerminated $Bytes $nameOffset
        })
        for ($functionIndex = 0; $functionIndex -lt $functionCount; $functionIndex++) {
            $functionRva = Read-UInt32LE $Bytes ($addressOfFunctionsOffset + ($functionIndex * 4))
            if ($functionRva -ne 0 -and -not $namedFunctionIndexes.Contains([int] $functionIndex)) {
                $discoveredExports += '#' + ([uint64] $ordinalBase + [uint64] $functionIndex).ToString([Globalization.CultureInfo]::InvariantCulture)
                $ordinalOnlyCount++
            }
        }
        $exports = @($discoveredExports | Sort-Object -Unique)
    }

    return [pscustomobject][ordered]@{
        machine = $machine
        machineValue = '0x' + $machineValue.ToString('x4')
        exports = $exports
        exportFunctionCount = [long] $functionCount
        namedExportCount = [long] $namedExportCount
        ordinalOnlyExportCount = $ordinalOnlyCount
    }
}

if (-not (Test-Path -LiteralPath $AllowlistPath -PathType Leaf)) {
    throw "Native ABI allowlist '$AllowlistPath' does not exist."
}
$allowlist = Get-Content -LiteralPath $AllowlistPath -Raw | ConvertFrom-Json -Depth 100
if ($allowlist.schemaVersion -ne 1) { throw "Unsupported native ABI allowlist schema '$($allowlist.schemaVersion)'." }

$packageFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File | Where-Object { $_.Name -notlike '*.symbols.nupkg' } | Sort-Object Name)
$packageEvidence = @($packageFiles | ForEach-Object { Get-NuGetPackageEvidence -Package $_ })
$allowlistPackages = @($allowlist.packages)
$results = [Collections.Generic.List[object]]::new()
$packageResults = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()

foreach ($evidence in $packageEvidence) {
    $archive = [IO.Compression.ZipFile]::OpenRead($evidence.PackagePath)
    try {
        $nativeEntries = @($archive.Entries | Where-Object { $_.FullName -match '(?i)^runtimes/win-(x86|x64|arm64)/native/.+\.dll$' } | Sort-Object FullName)
        $packageRule = @($allowlistPackages | Where-Object { $_.packageId -eq $evidence.Id }) | Select-Object -First 1
        $packageResults.Add([pscustomobject][ordered]@{
            packageId = $evidence.Id
            packageVersion = $evidence.Version
            nativeAssetCount = $nativeEntries.Count
            allowlistConfigured = $null -ne $packageRule
            status = if ($nativeEntries.Count -gt 0) { 'native-assets-inspected' } elseif ($null -ne $packageRule -and [bool] $packageRule.allowNoNative) { 'allowed-no-native-assets' } else { 'no-native-assets' }
        })
        if ($nativeEntries.Count -eq 0) {
            if ($null -ne $packageRule -and -not [bool] $packageRule.allowNoNative -and @($packageRule.assets).Count -gt 0) {
                $failures.Add("$($evidence.Id): required native assets are absent.")
            }
            continue
        }
        if ($null -eq $packageRule) {
            $failures.Add("$($evidence.Id): package contains native DLLs but has no ABI allowlist rule.")
        }

        foreach ($entry in $nativeEntries) {
            $stream = $entry.Open()
            try {
                $memory = [IO.MemoryStream]::new()
                try { $stream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $memory.Dispose() }
            }
            finally { $stream.Dispose() }
            $pe = Get-PeEvidence $bytes
            $entryPath = $entry.FullName.Replace('\', '/')
            $pathRid = ([regex]::Match($entryPath, '(?i)^runtimes/win-(x86|x64|arm64)/')).Groups[1].Value.ToLowerInvariant()
            if ($pe.machine -ne $pathRid) {
                $failures.Add("$($evidence.Id): '$entryPath' is $($pe.machine), expected $pathRid from its RID path.")
            }
            $assetRule = if ($null -eq $packageRule) { $null } else { @($packageRule.assets | Where-Object { $_.path -eq $entryPath }) | Select-Object -First 1 }
            if ($null -eq $assetRule) {
                $failures.Add("$($evidence.Id): '$entryPath' is not in the native ABI allowlist.")
                $expectedMachine = $null
                $allowedExports = @()
            }
            else {
                $expectedMachine = [string] $assetRule.machine
                $exportsFileProperty = $assetRule.PSObject.Properties['exportsFile']
                if ($null -ne $exportsFileProperty -and -not [string]::IsNullOrWhiteSpace([string] $exportsFileProperty.Value)) {
                    $exportsFilePath = Join-Path (Split-Path -Parent $AllowlistPath) ([string] $exportsFileProperty.Value)
                    if (-not (Test-Path -LiteralPath $exportsFilePath -PathType Leaf)) {
                        throw "Export allowlist file '$exportsFilePath' does not exist."
                    }
                    $allowedExports = @(Get-Content -LiteralPath $exportsFilePath |
                        ForEach-Object { $_.Trim() } |
                        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and -not $_.StartsWith('#', [StringComparison]::Ordinal) } |
                        Sort-Object -Unique)
                }
                else {
                    $allowedExports = @($assetRule.exports | Sort-Object -Unique)
                }
                if ($pe.machine -ne $expectedMachine) {
                    $failures.Add("$($evidence.Id): '$entryPath' is $($pe.machine), allowlist expects $expectedMachine.")
                }
                $unexpectedExports = @($pe.exports | Where-Object { $_ -notin $allowedExports })
                $missingExports = @($allowedExports | Where-Object { $_ -notin $pe.exports })
                if (-not $InventoryOnly -and ($unexpectedExports.Count -gt 0 -or $missingExports.Count -gt 0)) {
                    $failures.Add("$($evidence.Id): '$entryPath' export allowlist mismatch. Unexpected: [$($unexpectedExports -join ', ')]; missing: [$($missingExports -join ', ')].")
                }
            }
            $assetMemory = [IO.MemoryStream]::new($bytes, $false)
            try { $assetSha256 = Get-StreamHash -Stream $assetMemory -Algorithm SHA256 } finally { $assetMemory.Dispose() }
            $results.Add([pscustomobject][ordered]@{
                packageId = $evidence.Id
                packageVersion = $evidence.Version
                path = $entryPath
                sha256 = $assetSha256
                machine = $pe.machine
                expectedMachine = $expectedMachine
                exports = @($pe.exports)
                exportFunctionCount = $pe.exportFunctionCount
                namedExportCount = $pe.namedExportCount
                ordinalOnlyExportCount = $pe.ordinalOnlyExportCount
                allowedExports = $allowedExports
            })
        }
        if ($null -ne $packageRule) {
            foreach ($assetRule in @($packageRule.assets)) {
                if ($assetRule.path -notin $nativeEntries.FullName) {
                    $failures.Add("$($evidence.Id): allowlisted native asset '$($assetRule.path)' is missing.")
                }
            }
        }
    }
    finally { $archive.Dispose() }
}

$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    packages = @($packageResults | Sort-Object packageId, packageVersion)
    assets = @($results | Sort-Object packageId, path)
    failures = @($failures | Sort-Object -Unique)
}
Write-DeterministicText -Path $OutputPath -Content (ConvertTo-DeterministicJson $report)

if ($failures.Count -gt 0 -and -not $InventoryOnly) {
    throw "Native package compliance failed with $($failures.Count) finding(s). See '$OutputPath'."
}
Write-Host "Inspected $($results.Count) selected-RID native assets. Report: '$OutputPath'."
