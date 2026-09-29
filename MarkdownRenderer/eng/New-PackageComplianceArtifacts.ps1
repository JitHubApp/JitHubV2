[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),

    [Parameter(Mandatory = $false)]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\package-compliance'),

    [Parameter(Mandatory = $false)]
    [datetime] $CreationTimeUtc = [datetime]::new(1980, 1, 1, 0, 0, 0, [DateTimeKind]::Utc),

    [Parameter(Mandatory = $false)]
    [string] $DependencyEvidencePath = (Join-Path $PSScriptRoot 'dependency-license-evidence.json'),

    [Parameter(Mandatory = $false)]
    [string] $GlobalPackagesDirectory = $env:NUGET_PACKAGES,

    [switch] $FailOnUnknownLicense
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PackageCompliance.Common.ps1')

function Get-MinimumDependencyVersion {
    param([Parameter(Mandatory)][string] $Version)

    $value = $Version.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return '' }
    if ($value[0] -eq '[' -or $value[0] -eq '(') {
        $comma = $value.IndexOf(',')
        if ($comma -gt 1) { return $value.Substring(1, $comma - 1).Trim() }
        if ($value.EndsWith(']') -and $value.Length -gt 2) { return $value.Substring(1, $value.Length - 2).Trim() }
    }
    return $value
}

function Get-DependencyIdentityKey {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Version
    )

    return ($Id + '@' + (Get-MinimumDependencyVersion -Version $Version)).ToLowerInvariant()
}

$licenseOverrides = @{}
if (Test-Path -LiteralPath $DependencyEvidencePath -PathType Leaf) {
    $overrideDocument = Get-Content -LiteralPath $DependencyEvidencePath -Raw | ConvertFrom-Json
    if ($overrideDocument.schemaVersion -ne 1) {
        throw "Unsupported dependency-license evidence schema '$($overrideDocument.schemaVersion)'."
    }
    foreach ($override in @($overrideDocument.packages)) {
        $overrideKey = Get-DependencyIdentityKey -Id $override.id -Version $override.version
        if ($licenseOverrides.ContainsKey($overrideKey)) {
            throw "Duplicate dependency-license evidence '$overrideKey'."
        }
        if ([string]::IsNullOrWhiteSpace($override.licenseExpression) -or
            [string]::IsNullOrWhiteSpace($override.packageSha256) -or
            [string]::IsNullOrWhiteSpace($override.evidenceUrl)) {
            throw "Dependency-license evidence '$overrideKey' is incomplete."
        }
        $licenseOverrides[$overrideKey] = $override
    }
}

if ([string]::IsNullOrWhiteSpace($GlobalPackagesDirectory)) {
    $GlobalPackagesDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget\packages'
}

function Get-ExternalPackageEvidence {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Version
    )

    $resolvedVersion = Get-MinimumDependencyVersion -Version $Version
    if ([string]::IsNullOrWhiteSpace($resolvedVersion)) { return $null }
    $key = Get-DependencyIdentityKey -Id $Id -Version $resolvedVersion
    $packageDirectory = Join-Path $GlobalPackagesDirectory (Join-Path $Id.ToLowerInvariant() $resolvedVersion.ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) { return $null }

    $candidatePackages = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nupkg' -File |
        Where-Object { $_.Name -notlike '*.symbols.nupkg' })
    if ($candidatePackages.Count -ne 1) {
        throw "Expected one restored nupkg for '$Id $resolvedVersion' in '$packageDirectory'; found $($candidatePackages.Count)."
    }

    $externalEvidence = Get-NuGetPackageEvidence -Package $candidatePackages[0]
    if (-not $externalEvidence.Id.Equals($Id, [StringComparison]::OrdinalIgnoreCase) -or
        -not $externalEvidence.Version.Equals($resolvedVersion, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Restored package identity mismatch for '$Id $resolvedVersion'."
    }

    if ($licenseOverrides.ContainsKey($key)) {
        $override = $licenseOverrides[$key]
        if (-not $externalEvidence.PackageSha256.Equals($override.packageSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package hash for reviewed dependency-license evidence '$key' changed."
        }
        $externalEvidence.DeclaredLicense = [string] $override.licenseExpression
        $externalEvidence.LicenseEvidence = 'reviewed exact-package override: ' + [string] $override.evidenceUrl
    }

    $externalEvidence | Add-Member -NotePropertyName IsProducedPackage -NotePropertyValue $false
    return $externalEvidence
}

function Get-ProducedDependencyGraph {
    param(
        [Parameter(Mandatory)] $RootEvidence,
        [Parameter(Mandatory)][hashtable] $EvidenceByIdentity
    )

    $rootKey = ($RootEvidence.Id + '@' + $RootEvidence.Version).ToLowerInvariant()
    $components = @{}
    $edges = @{}
    $expanded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [Collections.Queue]::new()
    foreach ($dependency in $RootEvidence.Dependencies) {
        $queue.Enqueue([pscustomobject]@{ ParentKey = $rootKey; Dependency = $dependency; Depth = 1 })
    }

    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        $dependency = $item.Dependency
        if ([string]::IsNullOrWhiteSpace($dependency.Id)) {
            throw "Package '$($RootEvidence.Id)' contains a dependency without an ID."
        }

        $resolvedDependencyVersion = Get-MinimumDependencyVersion -Version $dependency.Version
        $componentKey = Get-DependencyIdentityKey -Id $dependency.Id -Version $resolvedDependencyVersion
        $producedEvidence = if ($EvidenceByIdentity.ContainsKey($componentKey)) { $EvidenceByIdentity[$componentKey] } else { $null }
        if ($componentKey -ne $rootKey) {
            if (-not $components.ContainsKey($componentKey)) {
                $components[$componentKey] = [pscustomobject]@{
                    Key = $componentKey
                    Id = $dependency.Id
                    Version = $resolvedDependencyVersion
                    DeclaredLicense = if ($null -ne $producedEvidence) { $producedEvidence.DeclaredLicense } else { 'NOASSERTION' }
                    ProducedEvidence = $producedEvidence
                    TargetFrameworks = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    Parents = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                    MinimumDepth = [int] $item.Depth
                }
            }

            $component = $components[$componentKey]
            if (-not [string]::IsNullOrWhiteSpace($dependency.TargetFramework)) {
                [void] $component.TargetFrameworks.Add($dependency.TargetFramework)
            }
            [void] $component.Parents.Add($item.ParentKey)
            if ($item.Depth -lt $component.MinimumDepth) { $component.MinimumDepth = [int] $item.Depth }
        }

        $edgeKey = $item.ParentKey + '>' + $componentKey
        if (-not $edges.ContainsKey($edgeKey)) {
            $edges[$edgeKey] = [pscustomobject]@{ FromKey = $item.ParentKey; ToKey = $componentKey }
        }

        if ($null -ne $producedEvidence -and $componentKey -ne $rootKey -and $expanded.Add($componentKey)) {
            foreach ($childDependency in $producedEvidence.Dependencies) {
                $queue.Enqueue([pscustomobject]@{ ParentKey = $componentKey; Dependency = $childDependency; Depth = ([int] $item.Depth + 1) })
            }
        }
    }

    return [pscustomobject]@{
        RootKey = $rootKey
        Components = @($components.Values | Sort-Object Id, Version)
        ComponentMap = $components
        Edges = @($edges.Values | Sort-Object FromKey, ToKey)
    }
}

$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object Name)
if ($packages.Count -eq 0) {
    throw "No nupkg files were found in '$PackageDirectory'."
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$allEvidence = @($packages | ForEach-Object { Get-NuGetPackageEvidence -Package $_ })
$evidenceByIdentity = @{}
foreach ($candidateEvidence in $allEvidence) {
    $identityKey = ($candidateEvidence.Id + '@' + $candidateEvidence.Version).ToLowerInvariant()
    if ($evidenceByIdentity.ContainsKey($identityKey)) {
        throw "Duplicate produced package identity '$($candidateEvidence.Id) $($candidateEvidence.Version)'."
    }
    $candidateEvidence | Add-Member -NotePropertyName IsProducedPackage -NotePropertyValue $true
    $evidenceByIdentity[$identityKey] = $candidateEvidence
}


# Resolve the restored external dependency closure from exact package archives.
# This keeps SPDX evidence bound to the same bytes used by the build instead of
# inferring a license from an ID, URL, or project name.
$dependencyQueue = [Collections.Queue]::new()
foreach ($candidateEvidence in $allEvidence) {
    foreach ($dependency in $candidateEvidence.Dependencies) {
        $dependencyQueue.Enqueue($dependency)
    }
}
$expandedDependencyEvidence = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($dependencyQueue.Count -gt 0) {
    $dependency = $dependencyQueue.Dequeue()
    if ([string]::IsNullOrWhiteSpace($dependency.Id)) { continue }
    $dependencyKey = Get-DependencyIdentityKey -Id $dependency.Id -Version $dependency.Version
    if (-not $expandedDependencyEvidence.Add($dependencyKey)) { continue }

    if ($evidenceByIdentity.ContainsKey($dependencyKey)) {
        $dependencyEvidence = $evidenceByIdentity[$dependencyKey]
    }
    else {
        $dependencyEvidence = Get-ExternalPackageEvidence -Id $dependency.Id -Version $dependency.Version
        if ($null -eq $dependencyEvidence) { continue }
        $evidenceByIdentity[$dependencyKey] = $dependencyEvidence
    }

    foreach ($childDependency in $dependencyEvidence.Dependencies) {
        $dependencyQueue.Enqueue($childDependency)
    }
}
$summary = [Collections.Generic.List[object]]::new()
$unknownLicenseFindings = [Collections.Generic.List[string]]::new()

foreach ($evidence in $allEvidence) {
    $safeIdentity = ($evidence.Id + '.' + $evidence.Version) -replace '[^A-Za-z0-9._-]', '_'
    $rootSpdxId = 'SPDXRef-Package-' + ((Get-StringSha256 -Value ($evidence.Id + '@' + $evidence.Version)).Substring(0, 24))

    $spdxFiles = @($evidence.Entries | ForEach-Object {
        $fileSpdxId = 'SPDXRef-File-' + ((Get-StringSha256 -Value ($_.Path + "`0" + $_.Sha256)).Substring(0, 24))
        [pscustomobject][ordered]@{
            fileName = './' + $_.Path
            SPDXID = $fileSpdxId
            checksums = @(
                [pscustomobject][ordered]@{ algorithm = 'SHA1'; checksumValue = $_.Sha1 },
                [pscustomobject][ordered]@{ algorithm = 'SHA256'; checksumValue = $_.Sha256 }
            )
            licenseConcluded = 'NOASSERTION'
            licenseInfoInFiles = @('NOASSERTION')
            copyrightText = 'NOASSERTION'
        }
    })

    $verificationMaterial = ($evidence.Entries.Sha1 | Sort-Object) -join ''
    $verificationBytes = [Text.Encoding]::ASCII.GetBytes($verificationMaterial)
    $verificationStream = [IO.MemoryStream]::new($verificationBytes, $false)
    try {
        $verificationCode = Get-StreamHash -Stream $verificationStream -Algorithm SHA1
    }
    finally {
        $verificationStream.Dispose()
    }

    $spdxPackages = [Collections.Generic.List[object]]::new()
    $spdxPackages.Add([pscustomobject][ordered]@{
        name = $evidence.Id
        SPDXID = $rootSpdxId
        versionInfo = $evidence.Version
        packageFileName = $evidence.PackageFile
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $true
        packageVerificationCode = [pscustomobject][ordered]@{ packageVerificationCodeValue = $verificationCode }
        checksums = @([pscustomobject][ordered]@{ algorithm = 'SHA256'; checksumValue = $evidence.PackageSha256 })
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = $evidence.DeclaredLicense
        copyrightText = 'NOASSERTION'
        supplier = 'NOASSERTION'
        externalRefs = @([pscustomobject][ordered]@{
            referenceCategory = 'PACKAGE-MANAGER'
            referenceType = 'purl'
            referenceLocator = 'pkg:nuget/' + [Uri]::EscapeDataString($evidence.Id) + '@' + [Uri]::EscapeDataString($evidence.Version)
        })
    })

    $dependencyGraph = Get-ProducedDependencyGraph -RootEvidence $evidence -EvidenceByIdentity $evidenceByIdentity
    $dependencyIds = [ordered]@{}
    foreach ($dependency in $dependencyGraph.Components) {
        $dependencySpdxId = 'SPDXRef-Dependency-' + ((Get-StringSha256 -Value $dependency.Key).Substring(0, 24))
        $dependencyIds[$dependency.Key] = $dependencySpdxId
        $spdxPackages.Add([pscustomobject][ordered]@{
            name = $dependency.Id
            SPDXID = $dependencySpdxId
            versionInfo = if ([string]::IsNullOrWhiteSpace($dependency.Version)) { 'NOASSERTION' } else { $dependency.Version }
            downloadLocation = 'NOASSERTION'
            filesAnalyzed = $false
            licenseConcluded = 'NOASSERTION'
            licenseDeclared = $dependency.DeclaredLicense
            copyrightText = 'NOASSERTION'
        })
        if ($dependency.DeclaredLicense -eq 'NOASSERTION') {
            $scope = if ($dependency.Parents.Contains($dependencyGraph.RootKey)) { 'direct' } else { 'transitive' }
            $unknownLicenseFindings.Add("$($evidence.Id) $($evidence.Version): $scope dependency '$($dependency.Id)' '$($dependency.Version)' has no license metadata in the package set or package nuspec.")
        }
    }

    if ($evidence.DeclaredLicense -eq 'NOASSERTION') {
        $unknownLicenseFindings.Add("$($evidence.Id) $($evidence.Version): the package does not declare an SPDX license expression in its nuspec.")
    }

    $relationships = [Collections.Generic.List[object]]::new()
    $relationships.Add([pscustomobject][ordered]@{
        spdxElementId = 'SPDXRef-DOCUMENT'
        relationshipType = 'DESCRIBES'
        relatedSpdxElement = $rootSpdxId
    })
    foreach ($file in $spdxFiles) {
        $relationships.Add([pscustomobject][ordered]@{
            spdxElementId = $rootSpdxId
            relationshipType = 'CONTAINS'
            relatedSpdxElement = $file.SPDXID
        })
    }
    foreach ($edge in $dependencyGraph.Edges) {
        $fromSpdxId = if ($edge.FromKey -eq $dependencyGraph.RootKey) { $rootSpdxId } else { $dependencyIds[$edge.FromKey] }
        $toSpdxId = if ($edge.ToKey -eq $dependencyGraph.RootKey) { $rootSpdxId } else { $dependencyIds[$edge.ToKey] }
        $relationships.Add([pscustomobject][ordered]@{
            spdxElementId = $fromSpdxId
            relationshipType = 'DEPENDS_ON'
            relatedSpdxElement = $toSpdxId
        })
    }

    $spdx = [pscustomobject][ordered]@{
        spdxVersion = 'SPDX-2.3'
        dataLicense = 'CC0-1.0'
        SPDXID = 'SPDXRef-DOCUMENT'
        name = $evidence.Id + '-' + $evidence.Version
        documentNamespace = 'urn:spdx:nuget:' + [Uri]::EscapeDataString($evidence.Id) + ':' + [Uri]::EscapeDataString($evidence.Version) + ':' + $evidence.PackageSha256
        creationInfo = [pscustomobject][ordered]@{
            created = $CreationTimeUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
            creators = @('Tool: MarkdownRenderer.PackageCompliance/1.0')
        }
        documentDescribes = @($rootSpdxId)
        packages = @($spdxPackages)
        files = $spdxFiles
        relationships = @($relationships)
    }

    $spdxPath = Join-Path $OutputDirectory ($safeIdentity + '.spdx.json')
    Write-DeterministicText -Path $spdxPath -Content (ConvertTo-DeterministicJson -InputObject $spdx)

    $notice = [Text.StringBuilder]::new()
    $packageLicenseReviewRequired = $evidence.DeclaredLicense -eq 'NOASSERTION' -or @($dependencyGraph.Components | Where-Object { $_.DeclaredLicense -eq 'NOASSERTION' }).Count -gt 0
    [void] $notice.AppendLine("# Third-party notices evidence for $($evidence.Id) $($evidence.Version)")
    [void] $notice.AppendLine()
    [void] $notice.AppendLine('This report is generated only from the package nuspec and files embedded in the nupkg. It does not infer licenses. `NOASSERTION` means the package did not carry enough evidence and requires release review.')
    [void] $notice.AppendLine()
    [void] $notice.AppendLine("- Package file: ``$($evidence.PackageFile)``")
    [void] $notice.AppendLine("- Package SHA-256: ``$($evidence.PackageSha256)``")
    [void] $notice.AppendLine("- Declared package license: ``$($evidence.DeclaredLicense)``")
    if ($packageLicenseReviewRequired) {
        [void] $notice.AppendLine('- License evidence status: **ACTION REQUIRED** — one or more direct or transitive declarations are `NOASSERTION`.')
    }
    else {
        [void] $notice.AppendLine('- License evidence status: no direct or transitive `NOASSERTION` findings in the provided package set; this is not legal approval.')
    }
    [void] $notice.AppendLine()
    [void] $notice.AppendLine('## Declared dependency closure')
    [void] $notice.AppendLine()
    if ($dependencyGraph.Components.Count -eq 0) {
        [void] $notice.AppendLine('No dependencies are declared in the nuspec.')
    }
    else {
        [void] $notice.AppendLine('| Dependency | Version or range | Scope | Introduced by | Target frameworks | License evidence |')
        [void] $notice.AppendLine('|---|---|---|---|---|---|')
        foreach ($dependency in $dependencyGraph.Components) {
            $scope = if ($dependency.Parents.Contains($dependencyGraph.RootKey)) { 'direct' } else { 'transitive' }
            $parentNames = @($dependency.Parents | Sort-Object | ForEach-Object {
                if ($_ -eq $dependencyGraph.RootKey) { $evidence.Id } else { $dependencyGraph.ComponentMap[$_].Id }
            })
            $targetFrameworks = @($dependency.TargetFrameworks | Sort-Object)
            $licenseDescription = if ($dependency.DeclaredLicense -eq 'NOASSERTION') {
                '`NOASSERTION` — restored package evidence unavailable or incomplete'
            }
            elseif ($dependency.ProducedEvidence.IsProducedPackage) {
                '`' + $dependency.DeclaredLicense + '` — sibling produced package nuspec'
            }
            else {
                '`' + $dependency.DeclaredLicense + '` — ' + (ConvertTo-MarkdownCell $dependency.ProducedEvidence.LicenseEvidence)
            }
            [void] $notice.AppendLine('| ' + (ConvertTo-MarkdownCell $dependency.Id) + ' | ' + (ConvertTo-MarkdownCell $dependency.Version) + ' | ' + $scope + ' | ' + (ConvertTo-MarkdownCell ($parentNames -join ', ')) + ' | ' + (ConvertTo-MarkdownCell ($targetFrameworks -join ', ')) + ' | ' + $licenseDescription + ' |')
        }
    }
    [void] $notice.AppendLine()
    [void] $notice.AppendLine('## Embedded license and notice assets')
    [void] $notice.AppendLine()
    $licenseEntries = @($evidence.Entries | Where-Object { $null -ne $_.LicenseText })
    if ($licenseEntries.Count -eq 0) {
        [void] $notice.AppendLine('No UTF-8 license or notice assets were detected by filename.')
    }
    else {
        foreach ($entry in $licenseEntries) {
            [void] $notice.AppendLine("### ``$($entry.Path)``")
            [void] $notice.AppendLine()
            [void] $notice.AppendLine("SHA-256: ``$($entry.Sha256)``")
            [void] $notice.AppendLine()
            [void] $notice.AppendLine('```text')
            [void] $notice.AppendLine($entry.LicenseText)
            [void] $notice.AppendLine('```')
            [void] $notice.AppendLine()
        }
    }

    $dependencyAssetMap = @{}
    foreach ($dependency in $dependencyGraph.Components) {
        if ($null -eq $dependency.ProducedEvidence) { continue }
        foreach ($entry in @($dependency.ProducedEvidence.Entries | Where-Object { $null -ne $_.LicenseText })) {
            if (-not $dependencyAssetMap.ContainsKey($entry.Sha256)) {
                $dependencyAssetMap[$entry.Sha256] = [pscustomobject]@{
                    Sha256 = $entry.Sha256
                    LicenseText = $entry.LicenseText
                    Sources = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                }
            }
            [void] $dependencyAssetMap[$entry.Sha256].Sources.Add($dependency.Id + ' ' + $dependency.Version + ':' + $entry.Path)
        }
    }
    [void] $notice.AppendLine('## Embedded license and notice assets from dependency packages')
    [void] $notice.AppendLine()
    if ($dependencyAssetMap.Count -eq 0) {
        [void] $notice.AppendLine('No UTF-8 license or notice assets were available from restored dependency packages.')
    }
    else {
        foreach ($asset in @($dependencyAssetMap.Values | Sort-Object Sha256)) {
            [void] $notice.AppendLine('### ' + (($asset.Sources | Sort-Object | ForEach-Object { "``$_``" }) -join ', '))
            [void] $notice.AppendLine()
            [void] $notice.AppendLine("SHA-256: ``$($asset.Sha256)``")
            [void] $notice.AppendLine()
            [void] $notice.AppendLine('```text')
            [void] $notice.AppendLine($asset.LicenseText)
            [void] $notice.AppendLine('```')
            [void] $notice.AppendLine()
        }
    }

    $noticesPath = Join-Path $OutputDirectory ($safeIdentity + '.THIRD-PARTY-NOTICES.md')
    Write-DeterministicText -Path $noticesPath -Content $notice.ToString()

    $summary.Add([pscustomobject][ordered]@{
        Package = $evidence.PackageFile
        Sha256 = $evidence.PackageSha256
        Size = $evidence.PackageLength
        DeclaredLicense = $evidence.DeclaredLicense
        DirectDependencyCount = $evidence.Dependencies.Count
        DependencyClosureCount = $dependencyGraph.Components.Count
        LicenseReviewRequired = $packageLicenseReviewRequired
        Sbom = [IO.Path]::GetFileName($spdxPath)
        Notices = [IO.Path]::GetFileName($noticesPath)
    })
}

$summaryDocument = [pscustomobject][ordered]@{
    schemaVersion = 1
    packages = @($summary)
    findings = @($unknownLicenseFindings | Sort-Object -Unique)
}
Write-DeterministicText -Path (Join-Path $OutputDirectory 'compliance-summary.json') -Content (ConvertTo-DeterministicJson $summaryDocument)

Write-Host "Generated SPDX 2.3 SBOMs and notice reports for $($packages.Count) packages in '$OutputDirectory'."
if ($unknownLicenseFindings.Count -gt 0) {
    Write-Warning "$($unknownLicenseFindings.Count) package/dependency license findings are marked NOASSERTION. Review compliance-summary.json."
    if ($FailOnUnknownLicense) {
        throw 'Unknown license evidence was found and -FailOnUnknownLicense was specified.'
    }
}
