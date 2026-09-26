[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AssemblyPath,

    [Parameter(Mandatory = $true)]
    [string] $XmlPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $XmlPath -PathType Leaf)) {
    throw 'The compiled assembly and XML documentation must exist before filtering.'
}

$assemblyStream = [System.IO.File]::OpenRead($AssemblyPath)
try {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($assemblyStream)
    try {
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $typesByHandle = @{}
        foreach ($handle in $reader.TypeDefinitions) {
            $definition = $reader.GetTypeDefinition($handle)
            $typesByHandle[$handle.GetHashCode()] = [pscustomobject]@{
                Handle = $handle
                Definition = $definition
                Name = $reader.GetString($definition.Name)
                Namespace = $reader.GetString($definition.Namespace)
                Visibility = ([int] $definition.Attributes -band 7)
            }
        }

        function Get-TypeInfo($handle) {
            $entry = $typesByHandle[$handle.GetHashCode()]
            if ($null -eq $entry) {
                throw 'A type definition was missing from the metadata index.'
            }
            if ($null -ne $entry.PSObject.Properties['FullName']) {
                return $entry
            }

            $declaring = $entry.Definition.GetDeclaringType()
            if ($declaring.IsNil) {
                $entry | Add-Member -NotePropertyName FullName -NotePropertyValue $(
                    if ($entry.Namespace) { "$($entry.Namespace).$($entry.Name)" }
                    else { $entry.Name })
                $entry | Add-Member -NotePropertyName Visible -NotePropertyValue (
                    $entry.Visibility -eq 1)
            }
            else {
                $parent = Get-TypeInfo $declaring
                $entry | Add-Member -NotePropertyName FullName -NotePropertyValue (
                    "$($parent.FullName).$($entry.Name)")
                $entry | Add-Member -NotePropertyName Visible -NotePropertyValue (
                    $parent.Visible -and $entry.Visibility -in 2, 4, 7)
            }
            return $entry
        }

        $types = @($reader.TypeDefinitions | ForEach-Object { Get-TypeInfo $_ } |
            Sort-Object { $_.FullName.Length } -Descending)
    }
    finally {
        $pe.Dispose()
    }
}
finally {
    $assemblyStream.Dispose()
}

$document = [System.Xml.XmlDocument]::new()
$document.XmlResolver = $null
$document.Load($XmlPath)
$members = @($document.SelectNodes('/doc/members/member'))
$removed = 0
foreach ($member in $members) {
    $id = [string] $member.GetAttribute('name')
    if ($id.Length -lt 3 -or $id[1] -ne ':') {
        throw "Unexpected XML documentation member ID '$id'."
    }

    $body = $id.Substring(2)
    $owner = $null
    foreach ($type in $types) {
        if (($id[0] -eq 'T' -and $body -eq $type.FullName) -or
            ($id[0] -ne 'T' -and $body.StartsWith(
                "$($type.FullName).", [System.StringComparison]::Ordinal))) {
            $owner = $type
            break
        }
    }
    if ($null -eq $owner) {
        throw "XML documentation member '$id' has no metadata type owner."
    }
    if (-not $owner.Visible) {
        [void] $member.ParentNode.RemoveChild($member)
        $removed++
    }
}

$temporaryPath = "$XmlPath.public.$PID.tmp"
try {
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $false
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.CloseOutput = $true
    $writer = [System.Xml.XmlWriter]::Create($temporaryPath, $settings)
    try {
        $document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }

    Move-Item -LiteralPath $temporaryPath -Destination $XmlPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Host "Packaged public XML documentation: retained $($members.Count - $removed) of $($members.Count) members."
