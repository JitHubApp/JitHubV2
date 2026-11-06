[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Expected,

    [Parameter(Mandatory)]
    [string]$Actual
)

$ErrorActionPreference = 'Stop'

function Get-UInt16([byte[]]$Bytes, [int]$Offset) {
    if ($Offset -lt 0 -or $Offset + 2 -gt $Bytes.Length) {
        throw 'The worker has a truncated PE field.'
    }
    return [BitConverter]::ToUInt16($Bytes, $Offset)
}

function Get-UInt32([byte[]]$Bytes, [int]$Offset) {
    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) {
        throw 'The worker has a truncated PE field.'
    }
    return [BitConverter]::ToUInt32($Bytes, $Offset)
}

function Clear-Bytes([byte[]]$Bytes, [int]$Offset, [int]$Count) {
    if ($Offset -lt 0 -or $Count -lt 0 -or $Offset + $Count -gt $Bytes.Length) {
        throw 'The worker has an invalid PE range.'
    }
    [Array]::Clear($Bytes, $Offset, $Count)
}

function ConvertTo-FileOffset(
    [byte[]]$Bytes,
    [uint32]$Rva,
    [int]$SectionTable,
    [int]$SectionCount) {
    for ($index = 0; $index -lt $SectionCount; $index++) {
        $section = $SectionTable + ($index * 40)
        $virtualSize = Get-UInt32 $Bytes ($section + 8)
        $virtualAddress = Get-UInt32 $Bytes ($section + 12)
        $rawSize = Get-UInt32 $Bytes ($section + 16)
        $rawPointer = Get-UInt32 $Bytes ($section + 20)
        $mappedSize = [Math]::Max($virtualSize, $rawSize)
        if ($Rva -ge $virtualAddress -and $Rva -lt $virtualAddress + $mappedSize) {
            return [int]($rawPointer + ($Rva - $virtualAddress))
        }
    }
    throw 'The worker debug directory points outside its PE sections.'
}

function Get-NormalizedWorkerHash([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "The worker binary was not found: '$resolved'."
    }

    [byte[]]$bytes = [IO.File]::ReadAllBytes($resolved)
    if ($bytes.Length -lt 64 -or (Get-UInt16 $bytes 0) -ne 0x5A4D) {
        throw "The worker is not a valid DOS/PE image: '$resolved'."
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ((Get-UInt32 $bytes $peOffset) -ne 0x00004550) {
        throw "The worker does not contain a valid PE signature: '$resolved'."
    }

    $sectionCount = Get-UInt16 $bytes ($peOffset + 6)
    $optionalHeaderSize = Get-UInt16 $bytes ($peOffset + 20)
    $optionalHeader = $peOffset + 24
    $optionalMagic = Get-UInt16 $bytes $optionalHeader
    $dataDirectories = switch ($optionalMagic) {
        0x010B { $optionalHeader + 96 }
        0x020B { $optionalHeader + 112 }
        default { throw "The worker has unsupported PE optional-header magic 0x$($optionalMagic.ToString('X4'))." }
    }

    # /Brepro intentionally derives these PE provenance fields from the build.
    # They can differ when the same source is compiled from another absolute
    # checkout, while all executable sections remain byte-for-byte identical.
    # Normalize only the standard timestamp/checksum and CodeView identity;
    # linker version, sections, imports, resources, and executable bytes remain
    # covered by the SHA-256 comparison.
    Clear-Bytes $bytes ($peOffset + 8) 4
    Clear-Bytes $bytes ($optionalHeader + 64) 4

    $debugRva = Get-UInt32 $bytes ($dataDirectories + (6 * 8))
    $debugSize = Get-UInt32 $bytes ($dataDirectories + (6 * 8) + 4)
    $sectionTable = $optionalHeader + $optionalHeaderSize
    if ($debugRva -ne 0 -and $debugSize -ge 28) {
        $debugOffset = ConvertTo-FileOffset $bytes $debugRva $sectionTable $sectionCount
        for ($entryOffset = 0; $entryOffset + 28 -le $debugSize; $entryOffset += 28) {
            $entry = $debugOffset + $entryOffset
            Clear-Bytes $bytes ($entry + 4) 4
            $type = Get-UInt32 $bytes ($entry + 12)
            $dataSize = Get-UInt32 $bytes ($entry + 16)
            $dataPointer = Get-UInt32 $bytes ($entry + 24)
            if ($type -eq 2 -and $dataSize -ge 24 -and
                (Get-UInt32 $bytes $dataPointer) -eq 0x53445352) {
                # RSDS GUID (16 bytes) plus age (4 bytes).
                Clear-Bytes $bytes ($dataPointer + 4) 20
            }
        }
    }

    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

$expectedPath = [IO.Path]::GetFullPath($Expected)
$actualPath = [IO.Path]::GetFullPath($Actual)
$expectedLength = (Get-Item -LiteralPath $expectedPath).Length
$actualLength = (Get-Item -LiteralPath $actualPath).Length
$expectedHash = Get-NormalizedWorkerHash $expectedPath
$actualHash = Get-NormalizedWorkerHash $actualPath

if ($expectedLength -ne $actualLength -or $expectedHash -cne $actualHash) {
    $expectedRaw = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash
    $actualRaw = (Get-FileHash -LiteralPath $actualPath -Algorithm SHA256).Hash
    throw @"
The checked-in resvg worker does not match its pinned source build.
Expected: length=$expectedLength normalized=$expectedHash raw=$expectedRaw
Actual:   length=$actualLength normalized=$actualHash raw=$actualRaw
"@
}

Write-Output "Verified reproducible resvg worker payload: $actualHash"
