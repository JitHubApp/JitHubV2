$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

$normalizer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Normalize-NuGetPackageArchives.ps1'))
$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "markdown-renderer-nupkg-normalizer-$([guid]::NewGuid().ToString('N'))"))
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null
$fixedTimestamp = [DateTimeOffset]::ParseExact(
    '2000-01-01T00:00:00+00:00',
    'yyyy-MM-ddTHH:mm:sszzz',
    [Globalization.CultureInfo]::InvariantCulture)

function Get-TestStreamDigest {
    param([Parameter(Mandatory)][IO.Stream] $Source)

    $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    $buffer = [byte[]]::new(65536)
    [long] $length = 0
    try {
        while (($read = $Source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $hash.AppendData($buffer, 0, $read)
            $length += $read
        }
        return [pscustomobject]@{
            Length = $length
            Sha256 = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
        }
    }
    finally { $hash.Dispose() }
}

function New-TestPackage {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][DateTimeOffset] $Timestamp,
        [switch] $IncludeSignature,
        [IO.Compression.CompressionLevel] $CompressionLevel = [IO.Compression.CompressionLevel]::NoCompression
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true,
            [Text.Encoding]::UTF8)
        try {
            $entries = [Collections.Generic.List[object]]::new()
            $entries.Add([pscustomobject]@{
                Name = '[Content_Types].xml'
                Attributes = 0x20
                Content = [Text.Encoding]::UTF8.GetBytes('<Types><Default Extension="dll" ContentType="application/octet-stream" /></Types>')
            })
            $entries.Add([pscustomobject]@{
                Name = 'lib/net10.0/demo.dll'
                Attributes = -2119958528
                Content = [byte[]]::new(1024 * 1024)
            })
            [Array]::Fill($entries[1].Content, [byte]0x41)
            $entries.Add([pscustomobject]@{
                Name = 'content/café.svg'
                Attributes = 0x20
                Content = [Text.Encoding]::UTF8.GetBytes('<svg xmlns="http://www.w3.org/2000/svg" />')
            })
            $entries.Add([pscustomobject]@{
                Name = 'lib/net10.0/'
                Attributes = 0x10
                Content = [byte[]]::new(0)
            })
            if ($IncludeSignature) {
                $entries.Add([pscustomobject]@{
                    Name = '.signature.p7s'
                    Attributes = 0x20
                    Content = [byte[]](1, 2, 3, 4)
                })
            }

            foreach ($item in $entries) {
                $entry = $archive.CreateEntry($item.Name, $CompressionLevel)
                $entry.ExternalAttributes = $item.Attributes
                $entry.LastWriteTime = $Timestamp
                $entryStream = $entry.Open()
                try {
                    $entryStream.Write($item.Content, 0, $item.Content.Length)
                }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-TestPackageSnapshot {
    param([Parameter(Mandatory)][string] $Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Read,
            $true)
        try {
            $entries = [Collections.Generic.List[object]]::new()
            foreach ($entry in $archive.Entries) {
                $entryStream = $entry.Open()
                try { $digest = Get-TestStreamDigest -Source $entryStream }
                finally { $entryStream.Dispose() }
                $entries.Add([pscustomobject]@{
                    Name = $entry.FullName
                    ExternalAttributes = $entry.ExternalAttributes
                    Length = $digest.Length
                    Sha256 = $digest.Sha256
                    LastWriteTime = $entry.LastWriteTime
                })
            }
            return ,$entries.ToArray()
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Invoke-Normalizer {
    param([Parameter(Mandatory)][string] $Directory)
    & $normalizer -PackageDirectory $Directory | Out-Null
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string] $Path)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [IO.File]::ReadAllBytes($Path))).ToLowerInvariant()
}

function Corrupt-TestPackageEntry {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $EntryName
    )

    $bytes = [IO.File]::ReadAllBytes($Path)
    for ($offset = 0; $offset -le $bytes.Length - 30; $offset++) {
        if ([BitConverter]::ToUInt32($bytes, $offset) -ne 0x04034b50) { continue }

        $method = [BitConverter]::ToUInt16($bytes, $offset + 8)
        $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 26)
        $extraLength = [BitConverter]::ToUInt16($bytes, $offset + 28)
        $name = [Text.Encoding]::ASCII.GetString($bytes, $offset + 30, $nameLength)
        if (-not [string]::Equals($name, $EntryName, [StringComparison]::Ordinal)) { continue }
        if ($method -ne 8) { throw "Fixture entry '$EntryName' is not deflated." }

        $dataOffset = $offset + 30 + $nameLength + $extraLength
        $compressedLength = [BitConverter]::ToUInt32($bytes, $offset + 18)
        if ($compressedLength -lt 2 -or $dataOffset + 1 -ge $bytes.Length) {
            throw "Fixture entry '$EntryName' has no corruptible deflate payload."
        }
        $bytes[$dataOffset] = 0xff
        $bytes[$dataOffset + 1] = 0xff
        [IO.File]::WriteAllBytes($Path, $bytes)
        return
    }
    throw "Could not find local header for fixture entry '$EntryName'."
}

function Corrupt-TestPackageCentralDirectoryCrc {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $EntryName
    )

    $bytes = [IO.File]::ReadAllBytes($Path)
    $eocdOffset = -1
    $minimumEocdOffset = [Math]::Max(0, $bytes.Length - 65557)
    for ($offset = $bytes.Length - 22; $offset -ge $minimumEocdOffset; $offset--) {
        if ([BitConverter]::ToUInt32($bytes, $offset) -ne 0x06054b50) { continue }
        $commentLength = [BitConverter]::ToUInt16($bytes, $offset + 20)
        if ($offset + 22 + $commentLength -eq $bytes.Length) {
            $eocdOffset = $offset
            break
        }
    }
    if ($eocdOffset -lt 0) { throw "Could not find end-of-central-directory record in '$Path'." }

    $centralDirectorySize = [long][BitConverter]::ToUInt32($bytes, $eocdOffset + 12)
    $centralDirectoryOffset = [long][BitConverter]::ToUInt32($bytes, $eocdOffset + 16)
    $centralDirectoryEnd = $centralDirectoryOffset + $centralDirectorySize
    if ($centralDirectoryOffset -eq [uint32]::MaxValue -or
        $centralDirectorySize -eq [uint32]::MaxValue -or
        $centralDirectoryOffset -lt 0 -or
        $centralDirectorySize -lt 0 -or
        $centralDirectoryEnd -gt $eocdOffset) {
        throw "Fixture '$Path' has an unsupported or invalid central-directory range."
    }

    $matches = 0
    for ($offset = $centralDirectoryOffset; $offset -lt $centralDirectoryEnd;) {
        if ($offset + 46 -gt $centralDirectoryEnd -or
            [BitConverter]::ToUInt32($bytes, [int]$offset) -ne 0x02014b50) {
            throw "Fixture '$Path' has a malformed central-directory record."
        }

        $nameLength = [BitConverter]::ToUInt16($bytes, [int]($offset + 28))
        $extraLength = [BitConverter]::ToUInt16($bytes, [int]($offset + 30))
        $commentLength = [BitConverter]::ToUInt16($bytes, [int]($offset + 32))
        $recordEnd = $offset + 46 + $nameLength + $extraLength + $commentLength
        if ($recordEnd -gt $centralDirectoryEnd) {
            throw "Fixture '$Path' has a truncated central-directory record."
        }

        $name = [Text.Encoding]::UTF8.GetString($bytes, [int]($offset + 46), $nameLength)
        if ([string]::Equals($name, $EntryName, [StringComparison]::Ordinal)) {
            $crcOffset = [int]($offset + 16)
            $bytes[$crcOffset] = [byte]($bytes[$crcOffset] -bxor 0x01)
            $matches++
        }

        $offset = $recordEnd
    }

    if ($matches -ne 1) { throw "Expected one central-directory record for '$EntryName' in '$Path', found $matches." }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Assert-NoNormalizerTemporaryFiles {
    param([Parameter(Mandatory)][string] $Directory, [Parameter(Mandatory)][string] $PackageName)
    $temporaryFiles = @(Get-ChildItem -LiteralPath $Directory `
        -Filter ".$PackageName.canonical-*.tmp" -File)
    if ($temporaryFiles.Count -ne 0) {
        throw "Normalizer left temporary files for '$PackageName': $($temporaryFiles.Name -join ', ')."
    }
}

function Assert-SnapshotsMatch {
    param(
        [Parameter(Mandatory)][object[]] $Expected,
        [Parameter(Mandatory)][object[]] $Actual,
        [Parameter(Mandatory)][string] $Label
    )

    if ($Expected.Length -ne $Actual.Length) {
        throw "$Label changed the number of package entries."
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        $left = $Expected[$index]
        $right = $Actual[$index]
        if (-not [string]::Equals($left.Name, $right.Name, [StringComparison]::Ordinal) -or
            $left.ExternalAttributes -ne $right.ExternalAttributes -or
            $left.Length -ne $right.Length -or
            -not [string]::Equals($left.Sha256, $right.Sha256, [StringComparison]::Ordinal)) {
            throw "$Label changed ordered entry metadata or payload at index $index ('$($left.Name)')."
        }
        if ($right.LastWriteTime.DateTime -ne $fixedTimestamp.DateTime) {
            throw "$Label left a non-deterministic timestamp on '$($right.Name)'."
        }
    }
}

try {
    $firstDirectory = Join-Path $tempRoot 'first'
    $secondDirectory = Join-Path $tempRoot 'second'
    [IO.Directory]::CreateDirectory($firstDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($secondDirectory) | Out-Null
    $firstPackage = Join-Path $firstDirectory 'fixture.nupkg'
    $secondPackage = Join-Path $secondDirectory 'fixture.nupkg'
    New-TestPackage -Path $firstPackage -Timestamp ([DateTimeOffset]::Parse('2024-04-05T06:07:08+00:00'))
    New-TestPackage -Path $secondPackage -Timestamp ([DateTimeOffset]::Parse('2025-08-09T10:11:12+00:00'))
    $expected = Get-TestPackageSnapshot -Path $firstPackage
    $sourceLength = ([IO.FileInfo]::new($firstPackage)).Length

    Invoke-Normalizer -Directory $firstDirectory
    Invoke-Normalizer -Directory $secondDirectory
    Assert-NoNormalizerTemporaryFiles -Directory $firstDirectory -PackageName 'fixture.nupkg'
    Assert-NoNormalizerTemporaryFiles -Directory $secondDirectory -PackageName 'fixture.nupkg'
    $firstNormalized = Get-TestPackageSnapshot -Path $firstPackage
    $secondNormalized = Get-TestPackageSnapshot -Path $secondPackage
    Assert-SnapshotsMatch -Expected $expected -Actual $firstNormalized -Label 'Lossless normalization'
    Assert-SnapshotsMatch -Expected $expected -Actual $secondNormalized -Label 'Deterministic normalization'
    if (([IO.FileInfo]::new($firstPackage)).Length -ge $sourceLength) {
        throw 'SmallestSize did not reduce the deliberately uncompressed package fixture.'
    }
    if (-not [string]::Equals((Get-FileSha256 $firstPackage), (Get-FileSha256 $secondPackage), [StringComparison]::Ordinal)) {
        throw 'Equivalent packages with different source timestamps did not normalize to identical bytes.'
    }

    $firstBytesHash = Get-FileSha256 $firstPackage
    Invoke-Normalizer -Directory $firstDirectory
    Assert-NoNormalizerTemporaryFiles -Directory $firstDirectory -PackageName 'fixture.nupkg'
    if (-not [string]::Equals($firstBytesHash, (Get-FileSha256 $firstPackage), [StringComparison]::Ordinal)) {
        throw 'Normalization was not byte-for-byte idempotent.'
    }

    $signedDirectory = Join-Path $tempRoot 'signed'
    [IO.Directory]::CreateDirectory($signedDirectory) | Out-Null
    $signedPackage = Join-Path $signedDirectory 'signed.nupkg'
    New-TestPackage -Path $signedPackage -Timestamp ([DateTimeOffset]::Parse('2024-01-02T03:04:06+00:00')) -IncludeSignature
    $signedHash = Get-FileSha256 $signedPackage
    $signedRejected = $false
    try { Invoke-Normalizer -Directory $signedDirectory }
    catch { $signedRejected = $true }
    if (-not $signedRejected) { throw 'A signed .nupkg was accepted for recompression.' }
    if (-not [string]::Equals($signedHash, (Get-FileSha256 $signedPackage), [StringComparison]::Ordinal)) {
        throw 'Signed-package refusal changed the original package bytes.'
    }
    Assert-NoNormalizerTemporaryFiles -Directory $signedDirectory -PackageName 'signed.nupkg'

    $copyFailureDirectory = Join-Path $tempRoot 'copy-failure'
    [IO.Directory]::CreateDirectory($copyFailureDirectory) | Out-Null
    $copyFailurePackage = Join-Path $copyFailureDirectory 'corrupt.nupkg'
    New-TestPackage -Path $copyFailurePackage -Timestamp ([DateTimeOffset]::Parse('2024-02-03T04:05:06+00:00')) `
        -CompressionLevel ([IO.Compression.CompressionLevel]::SmallestSize)
    Corrupt-TestPackageEntry -Path $copyFailurePackage -EntryName '[Content_Types].xml'
    $corruptPackageHash = Get-FileSha256 $copyFailurePackage
    $copyFailureRejected = $false
    try { Invoke-Normalizer -Directory $copyFailureDirectory }
    catch { $copyFailureRejected = $true }
    if (-not $copyFailureRejected) { throw 'A package with an invalid deflate block was accepted.' }
    if (-not [string]::Equals($corruptPackageHash, (Get-FileSha256 $copyFailurePackage), [StringComparison]::Ordinal)) {
        throw 'A failed entry copy changed the original package bytes.'
    }
    Assert-NoNormalizerTemporaryFiles -Directory $copyFailureDirectory -PackageName 'corrupt.nupkg'

    $crcFailureDirectory = Join-Path $tempRoot 'crc-failure'
    [IO.Directory]::CreateDirectory($crcFailureDirectory) | Out-Null
    $crcFailurePackage = Join-Path $crcFailureDirectory 'bad-crc.nupkg'
    New-TestPackage -Path $crcFailurePackage -Timestamp ([DateTimeOffset]::Parse('2024-02-03T04:05:06+00:00'))
    Corrupt-TestPackageCentralDirectoryCrc -Path $crcFailurePackage -EntryName '[Content_Types].xml'
    $badCrcPackageHash = Get-FileSha256 $crcFailurePackage
    $badCrcRejected = $false
    try { Invoke-Normalizer -Directory $crcFailureDirectory }
    catch { $badCrcRejected = $true }
    if (-not $badCrcRejected) { throw 'A package with an invalid central-directory CRC-32 was silently repaired.' }
    if (-not [string]::Equals($badCrcPackageHash, (Get-FileSha256 $crcFailurePackage), [StringComparison]::Ordinal)) {
        throw 'A CRC-32 validation failure changed the original package bytes.'
    }
    Assert-NoNormalizerTemporaryFiles -Directory $crcFailureDirectory -PackageName 'bad-crc.nupkg'

    $lockedDirectory = Join-Path $tempRoot 'locked'
    [IO.Directory]::CreateDirectory($lockedDirectory) | Out-Null
    $lockedPackage = Join-Path $lockedDirectory 'locked.nupkg'
    New-TestPackage -Path $lockedPackage -Timestamp ([DateTimeOffset]::Parse('2024-02-03T04:05:06+00:00'))
    $lockedHash = Get-FileSha256 $lockedPackage
    $guard = [IO.File]::Open(
        $lockedPackage,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $replaceRejected = $false
        try { Invoke-Normalizer -Directory $lockedDirectory }
        catch { $replaceRejected = $true }
        if (-not $replaceRejected) { throw 'Replacement unexpectedly succeeded while the original denied delete sharing.' }
    }
    finally { $guard.Dispose() }
    if (-not [string]::Equals($lockedHash, (Get-FileSha256 $lockedPackage), [StringComparison]::Ordinal)) {
        throw 'A failed same-directory publication changed the original package bytes.'
    }
    Assert-NoNormalizerTemporaryFiles -Directory $lockedDirectory -PackageName 'locked.nupkg'

    Write-Host 'NuGet archive normalization tests passed: payloads, order, names, external attributes, CRC-32 validation, deterministic timestamps/bytes, idempotence, signed refusal, publication failure preservation, and exact temporary cleanup.'
}
finally {
    $temporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedRoot = [IO.Path]::GetFullPath($tempRoot)
    if (-not $resolvedRoot.StartsWith($temporaryDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedRoot) -notlike 'markdown-renderer-nupkg-normalizer-*') {
        throw "Refusing to remove unexpected test directory '$resolvedRoot'."
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
