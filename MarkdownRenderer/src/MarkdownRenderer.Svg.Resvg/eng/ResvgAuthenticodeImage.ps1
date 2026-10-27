function Get-ResvgPeSigningLayout([byte[]] $Bytes) {
    if ($Bytes.Length -lt 64 -or $Bytes[0] -ne 0x4d -or $Bytes[1] -ne 0x5a) {
        throw 'Worker is not a valid PE image.'
    }

    $peOffset = [BitConverter]::ToInt32($Bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 24 -gt $Bytes.Length -or
        $Bytes[$peOffset] -ne 0x50 -or $Bytes[$peOffset + 1] -ne 0x45 -or
        $Bytes[$peOffset + 2] -ne 0 -or $Bytes[$peOffset + 3] -ne 0) {
        throw 'Worker has no valid PE signature and COFF header.'
    }

    $optionalHeaderLength = [int][BitConverter]::ToUInt16($Bytes, $peOffset + 20)
    $optionalHeader = $peOffset + 24
    if ($optionalHeaderLength -lt 96 -or $optionalHeader + $optionalHeaderLength -gt $Bytes.Length) {
        throw 'Worker PE optional header is truncated or invalid.'
    }

    $magic = [BitConverter]::ToUInt16($Bytes, $optionalHeader)
    $dataDirectoryOffset = switch ($magic) {
        0x10b { $optionalHeader + 96; break }
        0x20b { $optionalHeader + 112; break }
        default { throw ('Worker PE optional-header magic 0x{0:X4} is unsupported.' -f $magic) }
    }
    $numberOfDirectoriesOffset = if ($magic -eq 0x10b) { $optionalHeader + 92 } else { $optionalHeader + 108 }
    if ($numberOfDirectoriesOffset + 4 -gt $optionalHeader + $optionalHeaderLength -or
        [BitConverter]::ToUInt32($Bytes, $numberOfDirectoriesOffset) -lt 5 -or
        $dataDirectoryOffset + (5 * 8) -gt $optionalHeader + $optionalHeaderLength) {
        throw 'Worker PE optional header does not contain the Certificate Table data directory.'
    }

    $securityDirectory = $dataDirectoryOffset + (4 * 8)
    [pscustomobject]@{
        ChecksumOffset = $optionalHeader + 64
        SecurityDirectoryOffset = $securityDirectory
        CertificateOffset = [long][BitConverter]::ToUInt32($Bytes, $securityDirectory)
        CertificateSize = [long][BitConverter]::ToUInt32($Bytes, $securityDirectory + 4)
    }
}

function Test-ResvgPePreimage([string] $UnsignedPath, [string] $SignedPath) {
    $unsignedBytes = [IO.File]::ReadAllBytes($UnsignedPath)
    $signedBytes = [IO.File]::ReadAllBytes($SignedPath)
    $unsignedLayout = Get-ResvgPeSigningLayout $unsignedBytes
    $signedLayout = Get-ResvgPeSigningLayout $signedBytes

    if ($unsignedLayout.CertificateOffset -ne 0 -or $unsignedLayout.CertificateSize -ne 0) {
        throw "Unsigned source '$UnsignedPath' already has a Certificate Table entry."
    }
    if ($signedLayout.CertificateOffset -lt $unsignedBytes.Length -or
        $signedLayout.CertificateSize -le 0 -or
        $signedLayout.CertificateOffset + $signedLayout.CertificateSize -ne $signedBytes.Length) {
        throw "Signed worker '$SignedPath' does not contain a terminal appended Certificate Table."
    }

    $expectedCertificateOffset = [long](($unsignedBytes.Length + 7) -band -bnot 7)
    if ($signedLayout.CertificateOffset -ne $expectedCertificateOffset) {
        throw "Signed worker '$SignedPath' changed the original PE preimage extent before its Certificate Table."
    }

    $excludedOffsets = [Collections.Generic.HashSet[int]]::new()
    foreach ($offset in $unsignedLayout.ChecksumOffset..($unsignedLayout.ChecksumOffset + 3)) {
        [void]$excludedOffsets.Add($offset)
    }
    foreach ($offset in $unsignedLayout.SecurityDirectoryOffset..($unsignedLayout.SecurityDirectoryOffset + 7)) {
        [void]$excludedOffsets.Add($offset)
    }

    for ($index = 0; $index -lt $unsignedBytes.Length; $index++) {
        if (-not $excludedOffsets.Contains($index) -and $unsignedBytes[$index] -ne $signedBytes[$index]) {
            throw "Signed worker '$SignedPath' differs from unsigned source outside Authenticode-excluded PE fields at byte $index."
        }
    }

    for ($index = $unsignedBytes.Length; $index -lt $signedLayout.CertificateOffset; $index++) {
        if ($signedBytes[$index] -ne 0) {
            throw "Signed worker '$SignedPath' contains nonzero data in the PE certificate-alignment gap."
        }
    }

    return $true
}
