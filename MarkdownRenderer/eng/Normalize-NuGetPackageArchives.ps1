[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "Package directory '$packageRoot' does not exist."
}

$packages = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object Name)
if ($packages.Count -eq 0) {
    throw "No nupkg files were found in '$packageRoot'."
}

$fixedTimestamp = [DateTimeOffset]::ParseExact(
    '2000-01-01T00:00:00+00:00',
    'yyyy-MM-ddTHH:mm:sszzz',
    [Globalization.CultureInfo]::InvariantCulture)
$bufferSize = 65536
Add-Type -AssemblyName System.IO.Compression
if ($null -eq ('MarkdownRenderer.PackageTools.ZipCrc32' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
namespace MarkdownRenderer.PackageTools
{
    public static class ZipCrc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Initialize() => 0xffffffffU;

        public static uint Append(uint crc, byte[] buffer, int offset, int count)
        {
            int end = offset + count;
            for (int index = offset; index < end; index++)
            {
                crc = Table[(int)((crc ^ buffer[index]) & 0xff)] ^ (crc >> 8);
            }
            return crc;
        }

        public static uint Finish(uint crc) => ~crc;

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (int index = 0; index < table.Length; index++)
            {
                uint value = (uint)index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
                }
                table[index] = value;
            }
            return table;
        }
    }
}
'@
}

function Copy-BoundedZipStream {
    param(
        [Parameter(Mandatory)][IO.Stream] $Source,
        [Parameter(Mandatory)][IO.Stream] $Destination,
        [Parameter(Mandatory)][byte[]] $Buffer
    )

    while (($read = $Source.Read($Buffer, 0, $Buffer.Length)) -gt 0) {
        $Destination.Write($Buffer, 0, $read)
    }
}

function Get-BoundedZipStreamDigest {
    param(
        [Parameter(Mandatory)][IO.Stream] $Source,
        [Parameter(Mandatory)][byte[]] $Buffer
    )

    $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    [long] $length = 0
    [uint32] $crc = [MarkdownRenderer.PackageTools.ZipCrc32]::Initialize()
    try {
        while (($read = $Source.Read($Buffer, 0, $Buffer.Length)) -gt 0) {
            $hash.AppendData($Buffer, 0, $read)
            $crc = [MarkdownRenderer.PackageTools.ZipCrc32]::Append($crc, $Buffer, 0, $read)
            $length += $read
        }

        return [pscustomobject]@{
            Length = $length
            Sha256 = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
            Crc32 = [MarkdownRenderer.PackageTools.ZipCrc32]::Finish($crc)
        }
    }
    finally {
        $hash.Dispose()
    }
}

foreach ($package in $packages) {
    $temporaryPath = Join-Path $package.DirectoryName ".$($package.Name).canonical-$([guid]::NewGuid().ToString('N')).tmp"
    $temporaryCreated = $false
    $sourceStream = $null
    $sourceArchive = $null

    try {
        try {
            $sourceStream = [IO.File]::Open(
                $package.FullName,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::Read)
            $sourceArchive = [IO.Compression.ZipArchive]::new(
                $sourceStream,
                [IO.Compression.ZipArchiveMode]::Read,
                $true)

            foreach ($entry in $sourceArchive.Entries) {
                if ([string]::Equals($entry.FullName, '.signature.p7s', [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Refusing to rewrite signed NuGet package '$($package.Name)'."
                }
            }

            $temporaryStream = [IO.File]::Open(
                $temporaryPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            $temporaryCreated = $true
            try {
                $outputArchive = [IO.Compression.ZipArchive]::new(
                    $temporaryStream,
                    [IO.Compression.ZipArchiveMode]::Create,
                    $true,
                    [Text.Encoding]::UTF8)
                try {
                    $buffer = [byte[]]::new($bufferSize)
                    foreach ($sourceEntry in $sourceArchive.Entries) {
                        $outputEntry = $outputArchive.CreateEntry(
                            $sourceEntry.FullName,
                            [IO.Compression.CompressionLevel]::SmallestSize)
                        $outputEntry.ExternalAttributes = $sourceEntry.ExternalAttributes
                        $outputEntry.LastWriteTime = $fixedTimestamp

                        $sourceEntryStream = $sourceEntry.Open()
                        try {
                            $outputEntryStream = $outputEntry.Open()
                            try {
                                Copy-BoundedZipStream -Source $sourceEntryStream `
                                    -Destination $outputEntryStream -Buffer $buffer
                            }
                            finally {
                                $outputEntryStream.Dispose()
                            }
                        }
                        finally {
                            $sourceEntryStream.Dispose()
                        }
                    }
                }
                finally {
                    $outputArchive.Dispose()
                }
            }
            finally {
                $temporaryStream.Dispose()
            }

            $verificationStream = [IO.File]::OpenRead($temporaryPath)
            try {
                $verificationArchive = [IO.Compression.ZipArchive]::new(
                    $verificationStream,
                    [IO.Compression.ZipArchiveMode]::Read,
                    $true)
                try {
                    if ($verificationArchive.Entries.Count -ne $sourceArchive.Entries.Count) {
                        throw "Rewritten package '$($package.Name)' changed its entry count."
                    }

                    $buffer = [byte[]]::new($bufferSize)
                    for ($index = 0; $index -lt $sourceArchive.Entries.Count; $index++) {
                        $sourceEntry = $sourceArchive.Entries[$index]
                        $verifiedEntry = $verificationArchive.Entries[$index]
                        if (-not [string]::Equals($sourceEntry.FullName, $verifiedEntry.FullName, [StringComparison]::Ordinal)) {
                            throw "Rewritten package '$($package.Name)' changed entry order or name at index $index."
                        }
                        if ($sourceEntry.ExternalAttributes -ne $verifiedEntry.ExternalAttributes) {
                            throw "Rewritten package '$($package.Name)' changed external attributes for '$($sourceEntry.FullName)'."
                        }
                        # DOS ZIP headers store wall-clock fields, not a zone.
                        # ZipArchive reconstructs the offset using the current
                        # machine, so compare the fields rather than instants.
                        if ($verifiedEntry.LastWriteTime.DateTime -ne $fixedTimestamp.DateTime) {
                            throw "Rewritten package '$($package.Name)' has a non-canonical timestamp for '$($verifiedEntry.FullName)'."
                        }

                        if ($sourceEntry.Length -ne $verifiedEntry.Length) {
                            throw "Rewritten package '$($package.Name)' changed declared length for '$($sourceEntry.FullName)'."
                        }

                        $sourceEntryStream = $sourceEntry.Open()
                        try {
                            $verifiedEntryStream = $verifiedEntry.Open()
                            try {
                                $sourceDigest = Get-BoundedZipStreamDigest -Source $sourceEntryStream -Buffer $buffer
                                $verifiedDigest = Get-BoundedZipStreamDigest -Source $verifiedEntryStream -Buffer $buffer
                            }
                            finally {
                                $verifiedEntryStream.Dispose()
                            }
                        }
                        finally {
                            $sourceEntryStream.Dispose()
                        }

                        if ($sourceDigest.Length -ne $sourceEntry.Length -or
                            $verifiedDigest.Length -ne $verifiedEntry.Length -or
                            $sourceDigest.Length -ne $verifiedDigest.Length -or
                            $sourceDigest.Crc32 -ne $sourceEntry.Crc32 -or
                            $verifiedDigest.Crc32 -ne $verifiedEntry.Crc32 -or
                            $sourceEntry.Crc32 -ne $verifiedEntry.Crc32 -or
                            -not [string]::Equals($sourceDigest.Sha256, $verifiedDigest.Sha256, [StringComparison]::Ordinal)) {
                            throw "Rewritten package '$($package.Name)' changed or found corrupt content metadata for '$($sourceEntry.FullName)'."
                        }
                    }
                }
                finally {
                    $verificationArchive.Dispose()
                }
            }
            finally {
                $verificationStream.Dispose()
            }
        }
        finally {
            try {
                if ($null -ne $sourceArchive) { $sourceArchive.Dispose() }
            }
            finally {
                if ($null -ne $sourceStream) { $sourceStream.Dispose() }
            }
        }

        # Publish the fully verified archive with one same-directory rename.
        # These are unsigned build artifacts: filesystem metadata comes from
        # the output directory, not from destination-specific ACLs/streams.
        # Unlike ReplaceFile, this does not request WRITE_DAC to merge target
        # metadata. Both paths are in the package directory, so this remains
        # an on-volume rename and does not take MoveFileEx's copy/delete path.
        [IO.File]::Move($temporaryPath, $package.FullName, $true)
        $temporaryCreated = $false
    }
    finally {
        if ($temporaryCreated -and [IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

Write-Host "Canonicalized ZIP compression and timestamps in $($packages.Count) NuGet packages under '$packageRoot'."
