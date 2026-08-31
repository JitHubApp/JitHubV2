param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$xamlNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml'
$localizedAttributes = @(
    'Text',
    'Content',
    'Header',
    'PlaceholderText',
    'Title',
    'Description',
    'Label',
    'OffContent',
    'OnContent',
    'PrimaryButtonText',
    'SecondaryButtonText',
    'CloseButtonText',
    'AutomationProperties.Name',
    'AutomationProperties.HelpText',
    'ToolTipService.ToolTip'
)

function Test-LocalizableLiteral([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.TrimStart().StartsWith('{')) {
        return $false
    }

    $decoded = [System.Net.WebUtility]::HtmlDecode($Value)
    return $decoded -match '\p{L}'
}

function ConvertTo-ResourceIdentifier([string]$Value) {
    $identifier = [regex]::Replace($Value, '[^A-Za-z0-9]+', ' ').Trim()
    $identifier = (($identifier -split '\s+') | ForEach-Object {
        if ($_.Length -eq 0) { return }
        $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
    }) -join ''

    if ([string]::IsNullOrWhiteSpace($identifier)) {
        return 'Text'
    }

    if ($identifier.Length -gt 56) {
        return $identifier.Substring(0, 56)
    }

    return $identifier
}

function Get-TagAttributes([string]$TagText) {
    $attributes = [ordered]@{}
    foreach ($match in [regex]::Matches(
        $TagText,
        '(?<name>[A-Za-z_][\w:.-]*)\s*=\s*"(?<value>(?:&quot;|[^"])*)"',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        $attributes[$match.Groups['name'].Value] = $match.Groups['value'].Value
    }

    return $attributes
}

function Get-UniqueUid(
    [string]$RelativePath,
    [string]$ElementName,
    [System.Collections.IDictionary]$Attributes,
    [hashtable]$UsedUids) {
    $pathOwner = ConvertTo-ResourceIdentifier ([System.IO.Path]::ChangeExtension($RelativePath, $null))
    $preferred = $Attributes['AutomationProperties.AutomationId']
    if ([string]::IsNullOrWhiteSpace($preferred)) {
        $preferred = $Attributes['x:Name']
    }

    if (-not [string]::IsNullOrWhiteSpace($preferred)) {
        $preferred = "$pathOwner$preferred"
    }
    else {
        $literal = $localizedAttributes |
            Where-Object { $Attributes.Contains($_) -and (Test-LocalizableLiteral $Attributes[$_]) } |
            ForEach-Object { $Attributes[$_] } |
            Select-Object -First 1
        $elementOwner = ConvertTo-ResourceIdentifier (($ElementName -split ':')[-1])
        $literalOwner = ConvertTo-ResourceIdentifier $literal
        $preferred = "$pathOwner$elementOwner$literalOwner"
    }

    $baseUid = ConvertTo-ResourceIdentifier $preferred
    $candidate = $baseUid
    $suffix = 2
    while ($UsedUids.ContainsKey($candidate)) {
        $candidate = "$baseUid$suffix"
        $suffix++
    }

    $UsedUids[$candidate] = $true
    return $candidate
}

function Add-ResourceEntry(
    [System.Collections.Generic.Dictionary[string,string]]$Entries,
    [string]$Name,
    [string]$Value) {
    $decoded = [System.Net.WebUtility]::HtmlDecode($Value)
    $existingValue = $null
    if ($script:sourceOwnedEntries.TryGetValue($Name, [ref]$existingValue)) {
        if (-not [string]::Equals($existingValue, $decoded, [System.StringComparison]::Ordinal)) {
            throw "Resource '$Name' has conflicting fallbacks '$existingValue' and '$decoded'."
        }

        return
    }

    $script:sourceOwnedEntries.Add($Name, $decoded)
    $Entries[$Name] = $decoded
}

$viewsRoot = Join-Path $RepositoryRoot 'JitHub.WinUI\Views'
$englishResourcePath = Join-Path $RepositoryRoot 'JitHub.WinUI\Strings\en-US\Resources.resw'
$pseudoResourcePath = Join-Path $RepositoryRoot 'JitHub.WinUI\Strings\qps-ploc\Resources.resw'

[xml]$englishResources = Get-Content -Raw $englishResourcePath
$resourceEntries = [System.Collections.Generic.Dictionary[string,string]]::new(
    [System.StringComparer]::Ordinal)
$script:sourceOwnedEntries = [System.Collections.Generic.Dictionary[string,string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($entry in $englishResources.root.data) {
    $resourceEntries.Add([string]$entry.name, [string]$entry.value)
}

$files = Get-ChildItem $viewsRoot -Recurse -Filter '*.xaml' |
    Where-Object {
        $_.FullName -notmatch '\\Views\\Pages\\Design\\' -and
        $_.Name -ne 'DevConsole.xaml'
    } |
    Sort-Object FullName

foreach ($file in $files) {
    $relativePath = [System.IO.Path]::GetRelativePath($viewsRoot, $file.FullName)
    $source = [System.IO.File]::ReadAllText($file.FullName)
    $usedUids = @{}
    foreach ($match in [regex]::Matches($source, 'x:Uid\s*=\s*"(?<uid>[^"]+)"')) {
        $usedUids[$match.Groups['uid'].Value] = $true
    }

    $updated = [regex]::Replace(
        $source,
        '<(?<element>[A-Za-z_][\w:.-]*)(?<attributes>(?:\s+[A-Za-z_][\w:.-]*\s*=\s*"(?:&quot;|[^"])*")+\s*)(?<close>/?)>',
        {
            param($match)

            $elementName = $match.Groups['element'].Value
            if ($elementName.Contains('.')) {
                return $match.Value
            }

            $attributes = Get-TagAttributes $match.Value
            $literalProperties = $localizedAttributes | Where-Object {
                $attributes.Contains($_) -and (Test-LocalizableLiteral $attributes[$_])
            }
            if ($literalProperties.Count -eq 0) {
                return $match.Value
            }

            $uid = $attributes['x:Uid']
            if ([string]::IsNullOrWhiteSpace($uid)) {
                $uid = Get-UniqueUid $relativePath $elementName $attributes $usedUids
                $indentMatch = [regex]::Match($match.Groups['attributes'].Value, '^\s*')
                $indent = $indentMatch.Value
                $remainingAttributes = $match.Groups['attributes'].Value.Substring($indent.Length)
                $replacement = "<$elementName$indent" + "x:Uid=`"$uid`" " + $remainingAttributes + $match.Groups['close'].Value + '>'
            }
            else {
                $replacement = $match.Value
            }

            foreach ($property in $literalProperties) {
                Add-ResourceEntry $resourceEntries "$uid.$property" $attributes[$property]
            }

            return $replacement
        },
        [System.Text.RegularExpressions.RegexOptions]::Singleline)

    if (-not [string]::Equals($source, $updated, [System.StringComparison]::Ordinal)) {
        [System.IO.File]::WriteAllText($file.FullName, $updated, [System.Text.UTF8Encoding]::new($false))
    }

    [xml]$updatedDocument = $updated
    foreach ($element in $updatedDocument.SelectNodes('//*')) {
        if ($element.LocalName -notin @('TextBlock', 'Run', 'Button', 'ToggleButton', 'RadioButton', 'CheckBox', 'ComboBoxItem', 'SegmentedItem', 'MenuFlyoutItem')) {
            continue
        }

        $elementChildren = @($element.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })
        $textChildren = @($element.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Text -and -not [string]::IsNullOrWhiteSpace($_.Value) })
        if ($elementChildren.Count -ne 0 -or $textChildren.Count -ne 1 -or -not (Test-LocalizableLiteral $textChildren[0].Value)) {
            continue
        }

        $uid = $element.GetAttribute('Uid', $xamlNamespace)
        if ([string]::IsNullOrWhiteSpace($uid)) {
            throw "$relativePath contains direct user-facing text without x:Uid on <$($element.LocalName)>."
        }

        $property = if ($element.LocalName -in @('TextBlock', 'Run', 'MenuFlyoutItem')) { 'Text' } else { 'Content' }
        Add-ResourceEntry $resourceEntries "$uid.$property" $textChildren[0].Value.Trim()
    }
}

$localizedCodeFiles = Get-ChildItem (Join-Path $RepositoryRoot 'JitHub.WinUI') -Recurse -Filter '*.cs' |
    Where-Object {
        $_.FullName -notmatch '\\(bin|obj)[^\\]*\\' -and
        $_.FullName -notmatch '\\Views\\Pages\\Design\\'
    } |
    Sort-Object FullName
foreach ($file in $localizedCodeFiles) {
    $source = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches(
        $source,
        '\bL(?:F)?\(\s*"(?<key>(?:\\.|[^"])*)"\s*,\s*"(?<fallback>(?:\\.|[^"])*)"',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        $key = [regex]::Unescape($match.Groups['key'].Value)
        $fallback = [regex]::Unescape($match.Groups['fallback'].Value)
        Add-ResourceEntry $resourceEntries $key $fallback
    }
}

$existingNames = @($englishResources.root.data | ForEach-Object { [string]$_.name })
foreach ($entry in $englishResources.root.data) {
    $name = [string]$entry.name
    if ($resourceEntries.ContainsKey($name)) {
        $entry.value = $resourceEntries[$name]
    }
}

foreach ($name in ($resourceEntries.Keys | Sort-Object)) {
    if ($existingNames -contains $name) {
        continue
    }

    $data = $englishResources.CreateElement('data')
    $data.SetAttribute('name', $name)
    $data.SetAttribute('xml:space', 'preserve')
    $value = $englishResources.CreateElement('value')
    $value.InnerText = $resourceEntries[$name]
    [void]$data.AppendChild($value)
    [void]$englishResources.root.AppendChild($data)
}

$writerSettings = [System.Xml.XmlWriterSettings]::new()
$writerSettings.Indent = $true
$writerSettings.IndentChars = '  '
$writerSettings.NewLineChars = "`r`n"
$writerSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
$writerSettings.OmitXmlDeclaration = $false
$writer = [System.Xml.XmlWriter]::Create($englishResourcePath, $writerSettings)
try {
    $englishResources.Save($writer)
}
finally {
    $writer.Dispose()
}

[xml]$pseudoResources = Get-Content -Raw $englishResourcePath
foreach ($entry in $pseudoResources.root.data) {
    $value = [string]$entry.value
    if ($value -notmatch '\p{L}') {
        continue
    }

    $paddingLength = [Math]::Clamp([int][Math]::Ceiling($value.Length * 0.35), 6, 32)
    $entry.value = "⟦$value " + ('~' * $paddingLength) + '⟧'
}

[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($pseudoResourcePath)) | Out-Null
$writer = [System.Xml.XmlWriter]::Create($pseudoResourcePath, $writerSettings)
try {
    $pseudoResources.Save($writer)
}
finally {
    $writer.Dispose()
}

Write-Output "Localized $($files.Count) XAML files and generated qps-ploc resources."
