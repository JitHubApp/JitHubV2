Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression.FileSystem

function ConvertTo-HexString {
    param([Parameter(Mandatory)][byte[]] $Bytes)

    return [Convert]::ToHexString($Bytes).ToLowerInvariant()
}

function Get-StreamHash {
    param(
        [Parameter(Mandatory)][System.IO.Stream] $Stream,
        [Parameter(Mandatory)][ValidateSet('SHA1', 'SHA256')][string] $Algorithm
    )

    $hasher = [System.Security.Cryptography.HashAlgorithm]::Create($Algorithm)
    if ($null -eq $hasher) {
        throw "Hash algorithm '$Algorithm' is unavailable."
    }

    try {
        return ConvertTo-HexString ($hasher.ComputeHash($Stream))
    }
    finally {
        $hasher.Dispose()
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return Get-StreamHash -Stream $stream -Algorithm SHA256
    }
    finally {
        $stream.Dispose()
    }
}

function Get-StringSha256 {
    param([Parameter(Mandatory)][string] $Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $stream = [IO.MemoryStream]::new($bytes, $false)
    try {
        return Get-StreamHash -Stream $stream -Algorithm SHA256
    }
    finally {
        $stream.Dispose()
    }
}

function Get-XmlAttributeValue {
    param(
        [AllowNull()][System.Xml.XmlNode] $Node,
        [Parameter(Mandatory)][string] $Name
    )

    if ($null -eq $Node -or $null -eq $Node.Attributes) {
        return ''
    }

    $attribute = $Node.Attributes[$Name]
    if ($null -eq $attribute) {
        return ''
    }

    return $attribute.Value.Trim()
}

function Read-ZipEntryText {
    param([Parameter(Mandatory)][System.IO.Compression.ZipArchiveEntry] $Entry)

    $stream = $Entry.Open()
    try {
        $reader = [IO.StreamReader]::new(
            $stream,
            [Text.UTF8Encoding]::new($false, $true),
            $true,
            4096,
            $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-NuGetPackageEvidence {
    param([Parameter(Mandatory)][System.IO.FileInfo] $Package)

    $packageSha256 = Get-FileSha256 -Path $Package.FullName
    $archive = [IO.Compression.ZipFile]::OpenRead($Package.FullName)
    try {
        $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName -match '(?i)\.nuspec$' })
        if ($nuspecEntries.Count -ne 1) {
            throw "Package '$($Package.Name)' must contain exactly one nuspec; found $($nuspecEntries.Count)."
        }

        [xml] $nuspec = Read-ZipEntryText -Entry $nuspecEntries[0]
        $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        if ($null -eq $metadata) {
            throw "Package '$($Package.Name)' has no nuspec metadata element."
        }

        $idNode = $metadata.SelectSingleNode("*[local-name()='id']")
        $versionNode = $metadata.SelectSingleNode("*[local-name()='version']")
        $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
        $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
        $projectUrlNode = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
        $authorsNode = $metadata.SelectSingleNode("*[local-name()='authors']")
        $descriptionNode = $metadata.SelectSingleNode("*[local-name()='description']")

        $packageId = if ($null -eq $idNode) { '' } else { $idNode.InnerText.Trim() }
        $packageVersion = if ($null -eq $versionNode) { '' } else { $versionNode.InnerText.Trim() }
        if ([string]::IsNullOrWhiteSpace($packageId) -or [string]::IsNullOrWhiteSpace($packageVersion)) {
            throw "Package '$($Package.Name)' must declare a non-empty id and version."
        }

        $licenseType = Get-XmlAttributeValue -Node $licenseNode -Name 'type'
        $licenseValue = if ($null -eq $licenseNode) { '' } else { $licenseNode.InnerText.Trim() }
        $declaredLicense = if ($licenseType -eq 'expression' -and -not [string]::IsNullOrWhiteSpace($licenseValue)) {
            $licenseValue
        }
        else {
            'NOASSERTION'
        }
        $licenseEvidence = if ($declaredLicense -ne 'NOASSERTION') { 'package nuspec expression' } else { '' }

        $dependencies = @($metadata.SelectNodes("*[local-name()='dependencies']//*[local-name()='dependency']") | ForEach-Object {
            $targetFramework = ''
            if ($_.ParentNode.LocalName -eq 'group') {
                $targetFramework = Get-XmlAttributeValue -Node $_.ParentNode -Name 'targetFramework'
            }

            [pscustomobject][ordered]@{
                Id = Get-XmlAttributeValue -Node $_ -Name 'id'
                Version = Get-XmlAttributeValue -Node $_ -Name 'version'
                TargetFramework = $targetFramework
            }
        } | Sort-Object Id, Version, TargetFramework)

        $entries = @($archive.Entries |
            Where-Object { -not $_.FullName.EndsWith('/', [StringComparison]::Ordinal) } |
            Sort-Object FullName |
            ForEach-Object {
                $entryStream = $_.Open()
                try {
                    $sha1 = Get-StreamHash -Stream $entryStream -Algorithm SHA1
                }
                finally {
                    $entryStream.Dispose()
                }

                $entryStream = $_.Open()
                try {
                    $sha256 = Get-StreamHash -Stream $entryStream -Algorithm SHA256
                }
                finally {
                    $entryStream.Dispose()
                }

                $licenseText = $null
                $isDeclaredLicenseFile = $licenseType -eq 'file' -and $_.FullName.Equals($licenseValue, [StringComparison]::OrdinalIgnoreCase)
                $hasNoticeLikeFileName = $_.FullName -match '(?i)(^|/)[^/]*(license|notice|third[-_. ]?party|copying)[^/]*$'
                if ($isDeclaredLicenseFile -or $hasNoticeLikeFileName) {
                    try {
                        $licenseText = (Read-ZipEntryText -Entry $_).Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd()
                    }
                    catch [Text.DecoderFallbackException] {
                        $licenseText = $null
                    }
                }

                [pscustomobject][ordered]@{
                    Path = $_.FullName.Replace('\', '/')
                    Length = [long] $_.Length
                    CompressedLength = [long] $_.CompressedLength
                    Sha1 = $sha1
                    Sha256 = $sha256
                    LicenseText = $licenseText
                }
            })

        if ($licenseType -eq 'file' -and -not [string]::IsNullOrWhiteSpace($licenseValue)) {
            $declaredLicenseEntry = @($entries | Where-Object {
                $_.Path.Equals($licenseValue.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -and
                $null -ne $_.LicenseText
            } | Select-Object -First 1)
            if ($declaredLicenseEntry.Count -eq 1) {
                $safeLicenseId = $packageId -replace '[^A-Za-z0-9.-]', '-'
                $declaredLicense = 'LicenseRef-' + $safeLicenseId + '-' + $declaredLicenseEntry[0].Sha256.Substring(0, 16)
                $licenseEvidence = 'package file ' + $declaredLicenseEntry[0].Path
            }
        }

        return [pscustomobject][ordered]@{
            PackageFile = $Package.Name
            PackagePath = $Package.FullName
            PackageLength = [long] $Package.Length
            PackageSha256 = $packageSha256
            Id = $packageId
            Version = $packageVersion
            Authors = if ($null -eq $authorsNode) { '' } else { $authorsNode.InnerText.Trim() }
            Description = if ($null -eq $descriptionNode) { '' } else { $descriptionNode.InnerText.Trim() }
            RepositoryUrl = Get-XmlAttributeValue -Node $repositoryNode -Name 'url'
            ProjectUrl = if ($null -eq $projectUrlNode) { '' } else { $projectUrlNode.InnerText.Trim() }
            LicenseType = $licenseType
            LicenseValue = $licenseValue
            DeclaredLicense = $declaredLicense
            LicenseEvidence = $licenseEvidence
            Dependencies = $dependencies
            Entries = $entries
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Write-DeterministicText {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][AllowEmptyString()][string] $Content
    )

    $normalized = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    if (-not $normalized.EndsWith("`n", [StringComparison]::Ordinal)) {
        $normalized += "`n"
    }

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    [IO.File]::WriteAllText($Path, $normalized, [Text.UTF8Encoding]::new($false))
}

function ConvertTo-DeterministicJson {
    param(
        [Parameter(Mandatory)][AllowNull()] $InputObject,
        [ValidateRange(1, 100)][int] $Depth = 32
    )

    return ($InputObject | ConvertTo-Json -Depth $Depth).Replace("`r`n", "`n").Replace("`r", "`n")
}

function ConvertTo-MarkdownCell {
    param([AllowEmptyString()][string] $Value)

    return $Value.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}
