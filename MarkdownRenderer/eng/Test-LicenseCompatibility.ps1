[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $ComplianceDirectory = (Join-Path $PSScriptRoot '..\artifacts\package-compliance'),

    [Parameter(Mandatory = $false)]
    [string] $PolicyPath = (Join-Path $PSScriptRoot 'license-compatibility-policy.json'),

    [switch] $SkipPackageArtifacts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-LicenseIdentifiers {
    param([Parameter(Mandatory)][string] $Expression)

    if ([string]::IsNullOrWhiteSpace($Expression) -or $Expression -ceq 'NOASSERTION') {
        throw "License expression '$Expression' is not reviewable."
    }

    return @([regex]::Matches($Expression, '[A-Za-z0-9][A-Za-z0-9.+-]*') |
        ForEach-Object { $_.Value } |
        Where-Object { $_ -cnotin @('AND', 'OR', 'WITH') } |
        Sort-Object -Unique)
}

function Assert-LicenseExpressionAllowed {
    param(
        [Parameter(Mandatory)][string] $Expression,
        [Parameter(Mandatory)][string] $Subject,
        [Parameter(Mandatory)][string[]] $AllowedIdentifiers,
        [Parameter(Mandatory)] $Policy,
        [string] $PackageName
    )

    foreach ($identifier in @(Get-LicenseIdentifiers -Expression $Expression)) {
        if ($identifier -cin $AllowedIdentifiers) {
            continue
        }

        $review = @($Policy.reviewedLicenseReferences | Where-Object {
            $_.identifier -ceq $identifier -and $_.package -ceq $PackageName
        })
        if ($review.Count -eq 1) {
            continue
        }

        throw "$Subject uses unapproved license identifier '$identifier' in '$Expression'."
    }
}

if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw "License compatibility policy '$PolicyPath' does not exist."
}

$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -Depth 100
if ($policy.schemaVersion -ne 1) {
    throw "Unsupported license compatibility policy schema '$($policy.schemaVersion)'."
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repositoryLicensePath = Join-Path $repositoryRoot 'LICENSE'
$repositoryLicense = Get-Content -LiteralPath $repositoryLicensePath -Raw
if ($policy.repositoryLicense -cne 'MIT' -or -not $repositoryLicense.StartsWith('MIT License', [StringComparison]::Ordinal)) {
    throw 'The repository license no longer matches the reviewed MIT policy.'
}

$allowedSpdx = @($policy.allowedSpdxIdentifiers | ForEach-Object { [string] $_ })
$allowedLegacy = @($policy.allowedLegacyIdentifiers | ForEach-Object { [string] $_ })
$reviewedSubjects = [Collections.Generic.List[object]]::new()

if (-not $SkipPackageArtifacts) {
    $spdxFiles = @(Get-ChildItem -LiteralPath $ComplianceDirectory -Filter '*.spdx.json' -File | Sort-Object Name)
    if ($spdxFiles.Count -eq 0) {
        throw "No generated SPDX package reports were found in '$ComplianceDirectory'."
    }

    foreach ($spdxFile in $spdxFiles) {
        $spdx = Get-Content -LiteralPath $spdxFile.FullName -Raw | ConvertFrom-Json -Depth 100
        foreach ($package in @($spdx.packages)) {
            Assert-LicenseExpressionAllowed `
                -Expression ([string] $package.licenseDeclared) `
                -Subject "$($spdxFile.Name):$($package.name)" `
                -AllowedIdentifiers $allowedSpdx `
                -Policy $policy `
                -PackageName ([string] $package.name)
        }
    }

    $reviewedSubjects.Add([pscustomobject][ordered]@{
        subject = 'NuGet package closure'
        count = $spdxFiles.Count
        status = 'allowed'
    })
}

$mermaidRoot = Join-Path $repositoryRoot 'MarkdownRenderer\MarkdownRenderer.Mermaid'
$rustInventoryPath = Join-Path $mermaidRoot 'NATIVE_RUST_DEPENDENCIES.json'
$rustInventory = Get-Content -LiteralPath $rustInventoryPath -Raw | ConvertFrom-Json -Depth 100
if ([int] $rustInventory.packageCount -ne @($rustInventory.packages).Count) {
    throw 'The native Rust dependency inventory count is inconsistent.'
}

$restrictedByIdentifier = @{}
foreach ($restriction in @($policy.restrictedRuntimeDependencies)) {
    $noticePath = Join-Path $repositoryRoot ([string] $restriction.notice)
    if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
        throw "Restricted license notice '$noticePath' does not exist."
    }

    $restrictedByIdentifier[[string] $restriction.identifier] = @(
        $restriction.packages | ForEach-Object { [string] $_ })
}

foreach ($package in @($rustInventory.packages)) {
    $subject = "$($package.name)@$($package.version)"
    $expression = [string] $package.license
    Assert-LicenseExpressionAllowed `
        -Expression $expression `
        -Subject "native Rust dependency $subject" `
        -AllowedIdentifiers $allowedSpdx `
        -Policy $policy

    foreach ($identifier in @(Get-LicenseIdentifiers -Expression $expression)) {
        if ($restrictedByIdentifier.ContainsKey($identifier) -and
            $subject -cnotin @($restrictedByIdentifier[$identifier])) {
            throw "Native Rust dependency '$subject' is not reviewed for restricted license '$identifier'."
        }
    }
}

foreach ($restriction in @($policy.restrictedRuntimeDependencies)) {
    $identifier = [string] $restriction.identifier
    $actual = @($rustInventory.packages | Where-Object {
        $identifier -cin @(Get-LicenseIdentifiers -Expression ([string] $_.license))
    } | ForEach-Object { "$($_.name)@$($_.version)" } | Sort-Object)
    $expected = @($restriction.packages | ForEach-Object { [string] $_ } | Sort-Object)
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw "The reviewed dependency set for restricted license '$identifier' changed."
    }
}

$mermanProvenance = Get-Content -LiteralPath (Join-Path $mermaidRoot 'MERMAN_PROVENANCE.json') -Raw | ConvertFrom-Json -Depth 100
if ([bool] $mermanProvenance.licensingBoundary.elkShipped -or
    @($mermanProvenance.cargo.features) -ccontains 'layout-elk') {
    throw 'The reviewed Mermaid artifact must not include the EPL-licensed ELK implementation.'
}
$reviewedSubjects.Add([pscustomobject][ordered]@{
    subject = 'Native Rust dependency closure'
    count = [int] $rustInventory.packageCount
    status = 'allowed'
})

$textMateProvenancePath = Join-Path $repositoryRoot 'MarkdownRenderer\eng\textmate\GRAMMAR-PROVENANCE.v2.0.3.json'
$textMate = Get-Content -LiteralPath $textMateProvenancePath -Raw | ConvertFrom-Json -Depth 100
foreach ($identifier in @($textMate.licenseIdentifiers)) {
    if ([string] $identifier -cnotin @($allowedSpdx + $allowedLegacy)) {
        throw "TextMate grammar inventory uses unapproved license identifier '$identifier'."
    }
}
$reviewedSubjects.Add([pscustomobject][ordered]@{
    subject = 'TextMate grammar catalog'
    count = [int] @($textMate.registrations).Count
    status = 'allowed'
})

$mathProvenancePath = Join-Path $repositoryRoot 'MarkdownRenderer\MarkdownRenderer.Math\ThirdParty\PROVENANCE.json'
$math = Get-Content -LiteralPath $mathProvenancePath -Raw | ConvertFrom-Json -Depth 100
foreach ($font in @($math.fonts)) {
    if ([string] $font.license -cnotin @($allowedSpdx + $allowedLegacy)) {
        throw "Math font '$($font.file)' uses unapproved license '$($font.license)'."
    }
}
$reviewedSubjects.Add([pscustomobject][ordered]@{
    subject = 'Vendored math source and fonts'
    count = [int] $math.compiledSnapshot.fileCountIncludingFonts
    status = 'allowed'
})

$thorVgPath = Join-Path $repositoryRoot 'MarkdownRenderer\MarkdownRenderer.Svg.ThorVG\THORVG_PROVENANCE.json'
$thorVg = Get-Content -LiteralPath $thorVgPath -Raw | ConvertFrom-Json -Depth 100
Assert-LicenseExpressionAllowed `
    -Expression ([string] $thorVg.license) `
    -Subject 'ThorVG native dependency' `
    -AllowedIdentifiers $allowedSpdx `
    -Policy $policy
$reviewedSubjects.Add([pscustomobject][ordered]@{
    subject = 'ThorVG native dependency'
    count = @($thorVg.assets).Count
    status = 'allowed'
})

[IO.Directory]::CreateDirectory($ComplianceDirectory) | Out-Null
$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    repositoryLicense = [string] $policy.repositoryLicense
    policy = [IO.Path]::GetFileName($PolicyPath)
    reviewedSubjects = @($reviewedSubjects)
    result = 'pass'
}
$reportPath = Join-Path $ComplianceDirectory 'license-compatibility.json'
[IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 20) + "`n",
    [Text.UTF8Encoding]::new($false))
Write-Host "License compatibility policy passed. Report: '$reportPath'."
