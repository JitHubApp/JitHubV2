Set-StrictMode -Version Latest

$script:ResvgWorkerIdentityRids = [ordered]@{
    'win-x86' = @{ target = 'i686-pc-windows-msvc'; machine = 'x86' }
    'win-x64' = @{ target = 'x86_64-pc-windows-msvc'; machine = 'x64' }
    'win-arm64' = @{ target = 'aarch64-pc-windows-msvc'; machine = 'arm64' }
}
$script:ResvgWorkerIdentityFileName = 'MarkdownRenderer.Svg.Resvg.Worker.exe'
$script:ResvgWorkerFingerprintTextExtensions = @(
    '.cs', '.css', '.html', '.json', '.lock', '.md', '.ps1', '.psm1', '.rs', '.sh',
    '.svg', '.toml', '.txt', '.xml', '.yaml', '.yml'
)

function Get-ResvgWorkerFingerprintBytes([string] $RelativePath, [byte[]] $ContentBytes) {
    $extension = [IO.Path]::GetExtension($RelativePath).ToLowerInvariant()
    if ($extension -notin $script:ResvgWorkerFingerprintTextExtensions -or
        [Array]::IndexOf($ContentBytes, [byte]0x0d) -lt 0) {
        return ,$ContentBytes
    }

    # Git's text checkout conversion changes CRLF to LF. Canonicalize only that
    # pair for the build identity; leave lone CR and every other byte untouched.
    $stream = [IO.MemoryStream]::new($ContentBytes.Length)
    for ($index = 0; $index -lt $ContentBytes.Length; $index++) {
        if ($ContentBytes[$index] -eq 0x0d -and
            $index + 1 -lt $ContentBytes.Length -and
            $ContentBytes[$index + 1] -eq 0x0a) {
            $stream.WriteByte(0x0a)
            $index++
            continue
        }
        $stream.WriteByte($ContentBytes[$index])
    }
    return ,$stream.ToArray()
}

function Get-ResvgWorkerIdentityRids {
    return $script:ResvgWorkerIdentityRids
}

function Get-ResvgWorkerProtocolVersions([string] $ProviderRoot) {
    $hostPath = Join-Path $ProviderRoot 'Internal\WorkerProtocol.cs'
    $nativePath = Join-Path $ProviderRoot 'native\src\main.rs'
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
        throw 'The host or native worker protocol source is missing.'
    }

    $hostMatches = [regex]::Matches(
        [IO.File]::ReadAllText($hostPath),
        '(?m)^\s*public\s+const\s+ushort\s+Version\s*=\s*(\d+)\s*;')
    $nativeMatches = [regex]::Matches(
        [IO.File]::ReadAllText($nativePath),
        '(?m)^\s*const\s+VERSION\s*:\s*u16\s*=\s*(\d+)\s*;')
    if ($hostMatches.Count -ne 1 -or $nativeMatches.Count -ne 1) {
        throw 'Could not uniquely identify both host and native worker protocol versions.'
    }

    return [pscustomobject]@{
        Host = [int]::Parse($hostMatches[0].Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
        Native = [int]::Parse($nativeMatches[0].Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Assert-ResvgWorkerFixedInputPath([string] $Root, [string] $RelativePath) {
    $components = $RelativePath.Replace('\', '/').Split('/')
    if ($components.Count -eq 0 -or
        $components.Where({ [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') }).Count -gt 0) {
        throw "Worker source path is not a normalized relative path: '$RelativePath'."
    }

    $current = [IO.Path]::GetFullPath($Root)
    for ($index = 0; $index -lt $components.Count; $index++) {
        $current = Join-Path $current $components[$index]
        if (-not (Test-Path -LiteralPath $current)) {
            throw "A worker source/build input is missing: '$RelativePath'."
        }

        $attributes = [IO.File]::GetAttributes($current)
        if ($index -lt $components.Count - 1 -and
            ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Worker source/build input traverses a parent reparse point: '$RelativePath' ($current)."
        }
        if ($index -lt $components.Count - 1 -and
            ($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
            throw "Worker source/build input parent is not a directory: '$RelativePath' ($current)."
        }
    }

    return [IO.Path]::GetFullPath($current)
}

function Get-ResvgWorkerSourceFingerprint([string] $ProviderRoot) {
    $root = [IO.Path]::GetFullPath($ProviderRoot)
    $nativeRoot = Join-Path $root 'native'
    if (-not (Test-Path -LiteralPath $nativeRoot -PathType Container)) {
        throw "Native source directory '$nativeRoot' is missing."
    }
    if (([IO.File]::GetAttributes($nativeRoot) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Native source root is a reparse point: '$nativeRoot'."
    }

    $paths = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($nativeRoot)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $name = [IO.Path]::GetFileName($entry)
            $attributes = [IO.File]::GetAttributes($entry)
            $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
            if ($isDirectory) {
                $relativeDirectory = [IO.Path]::GetRelativePath($nativeRoot, $entry).Replace('\', '/')
                if ($name -in @('target', '.git') -or $relativeDirectory -ceq 'artifacts') {
                    continue
                }
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Native source tree contains a directory reparse point: '$entry'."
                }
                $pending.Push($entry)
                continue
            }
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $target = [IO.FileInfo]::new($entry).ResolveLinkTarget($true)
                if ($null -eq $target) { throw "Native source reparse point could not be resolved: '$entry'." }
                $resolvedTarget = [IO.Path]::GetFullPath($target.FullName)
                $nativePrefix = $nativeRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                if (-not $resolvedTarget.StartsWith($nativePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Native source file reparse point escapes the native source root: '$entry'."
                }
            }
            $relative = [IO.Path]::GetRelativePath($nativeRoot, $entry).Replace('\', '/')
            if ($relative.Split('/').Where({ $_ -in @('', '.', '..') }).Count -gt 0) {
                throw "Native source path is not a normalized relative path: '$relative'."
            }
            $paths.Add("native/$relative")
        }
    }

    $fixedPaths = @(
        'Internal/WorkerProtocol.cs',
        'NATIVE_RUST_DEPENDENCIES.json',
        'RESVG_PROVENANCE.json',
        'eng/Build-ResvgWorker.ps1',
        'eng/Compare-ResvgWorkerBinary.ps1',
        'eng/Generate-ResvgSupplyChain.ps1',
        'eng/ResvgWorkerIdentity.psm1',
        'eng/ResvgVendoredSources.ps1',
        'eng/Update-ResvgWorkerIdentity.ps1',
        'eng/Verify-ResvgWorkerIdentity.ps1',
        'eng/Verify-ResvgSupplyChain.ps1',
        'eng/SpdxLicenseSelection.ps1'
    )
    foreach ($relative in $fixedPaths) {
        $fullPath = Assert-ResvgWorkerFixedInputPath $root $relative
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "A worker source/build input is not a file: '$relative'."
        }
        $paths.Add($relative)
    }

    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $records = [Collections.Generic.List[object]]::new()
    foreach ($relative in $paths) {
        if (-not $unique.Add($relative)) {
            throw "Worker source inventory contains a duplicate path: '$relative'."
        }
        $fullPath = Join-Path $root $relative.Replace('/', '\')
        $normalizedFullPath = [IO.Path]::GetFullPath($fullPath)
        $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $normalizedFullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Worker source path escaped its provider root: '$relative'."
        }
        $attributes = [IO.File]::GetAttributes($normalizedFullPath)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $target = [IO.FileInfo]::new($normalizedFullPath).ResolveLinkTarget($true)
            if ($null -eq $target) { throw "Worker source reparse point could not be resolved: '$relative'." }
            $resolvedTarget = [IO.Path]::GetFullPath($target.FullName)
            if (-not $resolvedTarget.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Worker source reparse point escapes its provider root: '$relative'."
            }
            $normalizedFullPath = $resolvedTarget
        }
        [byte[]]$contentBytes = [IO.File]::ReadAllBytes($normalizedFullPath)
        [byte[]]$fingerprintBytes = Get-ResvgWorkerFingerprintBytes $relative $contentBytes
        $contentHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($fingerprintBytes)).ToLowerInvariant()
        $records.Add([pscustomobject]@{ Path = $relative; Hash = $contentHash })
    }

    $records.Sort([Comparison[object]]{
        param($left, $right)
        [StringComparer]::Ordinal.Compare([string]$left.Path, [string]$right.Path)
    })
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append("resvg-worker-source-fingerprint-v2`n")
    foreach ($record in $records) {
        [void]$builder.Append([string]$record.Path).Append([char]0).Append([string]$record.Hash).Append("`n")
    }
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($builder.ToString()))).ToLowerInvariant()
}

function Get-ResvgWorkerBuildFingerprint(
    [string] $SourceFingerprint,
    [string] $Target,
    [string] $RustcCommit) {
    if ($SourceFingerprint -notmatch '^[0-9a-f]{64}$' -or
        $RustcCommit -notmatch '^[0-9a-f]{40}$' -or
        $Target -notin @($script:ResvgWorkerIdentityRids.Values | ForEach-Object { $_.target })) {
        throw 'Worker build fingerprint inputs are invalid.'
    }
    $canonical = "resvg-worker-build-recipe-v1`0$SourceFingerprint`0$Target`0$RustcCommit`0release-locked-brepro-crt-static"
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
}

function Get-ResvgWorkerImageInfo([string] $Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Worker binary '$resolved' is missing."
    }
    [byte[]]$bytes = [IO.File]::ReadAllBytes($resolved)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) {
        throw "Worker '$resolved' is not a valid PE image."
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 24 -gt $bytes.Length -or
        [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "Worker '$resolved' has an invalid PE header."
    }
    $machineCode = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $machine = switch ($machineCode) {
        332 { 'x86' }
        34404 { 'x64' }
        43620 { 'arm64' }
        default { throw "Worker '$resolved' has unsupported PE machine 0x$($machineCode.ToString('X4'))." }
    }
    $rawHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $normalized = [byte[]]$bytes.Clone()
    $optionalHeaderSize = [BitConverter]::ToUInt16($normalized, $peOffset + 20)
    $optionalHeader = $peOffset + 24
    if ($optionalHeader + $optionalHeaderSize -gt $normalized.Length) {
        throw "Worker '$resolved' has a truncated PE optional header."
    }
    $optionalMagic = [BitConverter]::ToUInt16($normalized, $optionalHeader)
    $directoriesOffset = switch ($optionalMagic) {
        0x010b { $optionalHeader + 96 }
        0x020b { $optionalHeader + 112 }
        default { throw "Worker '$resolved' has unsupported PE optional-header magic." }
    }
    [Array]::Clear($normalized, $peOffset + 8, 4)
    [Array]::Clear($normalized, $optionalHeader + 64, 4)
    $sectionCount = [BitConverter]::ToUInt16($normalized, $peOffset + 6)
    $sectionTable = $optionalHeader + $optionalHeaderSize
    $debugRva = [BitConverter]::ToUInt32($normalized, $directoriesOffset + 6 * 8)
    $debugSize = [BitConverter]::ToUInt32($normalized, $directoriesOffset + 6 * 8 + 4)
    if ($debugRva -ne 0 -and $debugSize -ge 28) {
        $debugOffset = -1
        for ($index = 0; $index -lt $sectionCount; $index++) {
            $section = $sectionTable + $index * 40
            if ($section + 40 -gt $normalized.Length) { throw 'Worker PE section table is truncated.' }
            $virtualSize = [BitConverter]::ToUInt32($normalized, $section + 8)
            $virtualAddress = [BitConverter]::ToUInt32($normalized, $section + 12)
            $rawSize = [BitConverter]::ToUInt32($normalized, $section + 16)
            $rawPointer = [BitConverter]::ToUInt32($normalized, $section + 20)
            $mappedSize = [Math]::Max($virtualSize, $rawSize)
            if ($debugRva -ge $virtualAddress -and [long]$debugRva -lt [long]$virtualAddress + $mappedSize) {
                $debugOffset = [int]($rawPointer + ($debugRva - $virtualAddress))
                break
            }
        }
        if ($debugOffset -lt 0 -or $debugOffset + $debugSize -gt $normalized.Length) {
            throw "Worker '$resolved' debug directory points outside its PE image."
        }
        for ($entryOffset = 0; $entryOffset + 28 -le $debugSize; $entryOffset += 28) {
            $entry = $debugOffset + $entryOffset
            [Array]::Clear($normalized, $entry + 4, 4)
            $type = [BitConverter]::ToUInt32($normalized, $entry + 12)
            $dataSize = [BitConverter]::ToUInt32($normalized, $entry + 16)
            $dataPointer = [BitConverter]::ToUInt32($normalized, $entry + 24)
            if ($type -eq 2 -and $dataSize -ge 24 -and $dataPointer + 24 -le $normalized.Length -and
                [BitConverter]::ToUInt32($normalized, [int]$dataPointer) -eq 0x53445352) {
                [Array]::Clear($normalized, [int]$dataPointer + 4, 20)
            }
        }
    }
    return [pscustomobject]@{
        path = $resolved
        bytes = [long]$bytes.Length
        machine = $machine
        sha256 = $rawHash
        normalizedSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($normalized)).ToLowerInvariant()
    }
}

Export-ModuleMember -Function Get-ResvgWorkerIdentityRids, Get-ResvgWorkerProtocolVersions, Get-ResvgWorkerSourceFingerprint, Get-ResvgWorkerBuildFingerprint, Get-ResvgWorkerFingerprintBytes, Get-ResvgWorkerImageInfo
