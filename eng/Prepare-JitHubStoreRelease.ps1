param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$PackageIdentityName,

    [Parameter(Mandatory = $true)]
    [string]$PackagePublisher,

    [string]$PublisherDisplayName,
    [string]$AppDisplayName,
    [string]$PhoneProductId,
    [string]$PhonePublisherId
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Manifest not found: $ManifestPath"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Version must not be empty.'
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version '$Version' must use the four-part format Major.Minor.Build.Revision."
}

if ([string]::IsNullOrWhiteSpace($PackageIdentityName)) {
    throw 'PackageIdentityName must not be empty.'
}

if ([string]::IsNullOrWhiteSpace($PackagePublisher)) {
    throw 'PackagePublisher must not be empty.'
}

$manifestContent = Get-Content -LiteralPath $ManifestPath -Raw
[xml]$manifest = $manifestContent

$namespaceManager = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$namespaceManager.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespaceManager.AddNamespace('mp', 'http://schemas.microsoft.com/appx/2014/phone/manifest')
$namespaceManager.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')

$identityNode = $manifest.SelectSingleNode('/appx:Package/appx:Identity', $namespaceManager)
if (-not $identityNode) {
    throw 'Package identity node was not found in the manifest.'
}

$propertiesNode = $manifest.SelectSingleNode('/appx:Package/appx:Properties', $namespaceManager)
if (-not $propertiesNode) {
    throw 'Package properties node was not found in the manifest.'
}

$visualElementsNode = $manifest.SelectSingleNode('/appx:Package/appx:Applications/appx:Application/uap:VisualElements', $namespaceManager)
if (-not $visualElementsNode) {
    throw 'VisualElements node was not found in the manifest.'
}

$phoneIdentityNode = $manifest.SelectSingleNode('/appx:Package/mp:PhoneIdentity', $namespaceManager)

$identityNode.SetAttribute('Version', $Version)
$identityNode.SetAttribute('Name', $PackageIdentityName)
$identityNode.SetAttribute('Publisher', $PackagePublisher)

if (-not [string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    $publisherDisplayNameNode = $propertiesNode.SelectSingleNode('appx:PublisherDisplayName', $namespaceManager)
    if (-not $publisherDisplayNameNode) {
        throw 'PublisherDisplayName node was not found in the manifest.'
    }

    $publisherDisplayNameNode.InnerText = $PublisherDisplayName
}

if (-not [string]::IsNullOrWhiteSpace($AppDisplayName)) {
    $displayNameNode = $propertiesNode.SelectSingleNode('appx:DisplayName', $namespaceManager)
    if (-not $displayNameNode) {
        throw 'DisplayName node was not found in the manifest.'
    }

    $displayNameNode.InnerText = $AppDisplayName
    $visualElementsNode.SetAttribute('DisplayName', $AppDisplayName)
}

$hasPhoneIdentityValues = -not [string]::IsNullOrWhiteSpace($PhoneProductId) -or -not [string]::IsNullOrWhiteSpace($PhonePublisherId)
if ($hasPhoneIdentityValues) {
    if ([string]::IsNullOrWhiteSpace($PhoneProductId) -or [string]::IsNullOrWhiteSpace($PhonePublisherId)) {
        throw 'Phone identity overrides must provide both PhoneProductId and PhonePublisherId.'
    }

    if (-not $phoneIdentityNode) {
        throw 'PhoneIdentity node was not found in the manifest.'
    }

    $phoneIdentityNode.SetAttribute('PhoneProductId', $PhoneProductId)
    $phoneIdentityNode.SetAttribute('PhonePublisherId', $PhonePublisherId)
}

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.IndentChars = '  '
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$settings.NewLineChars = "`r`n"
$settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace

$resolvedManifestPath = Resolve-Path -LiteralPath $ManifestPath | Select-Object -ExpandProperty Path
$writer = [System.Xml.XmlWriter]::Create($resolvedManifestPath, $settings)

try {
    $manifest.Save($writer)
}
finally {
    $writer.Dispose()
}
