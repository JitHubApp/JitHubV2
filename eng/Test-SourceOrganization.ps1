<#
.SYNOPSIS
Checks that the C# files of every project in the repository are laid out by
namespace, folder and visibility.

.DESCRIPTION
Part of AGENTS.md "Code Structure Standards", "Source organization". This
check owns the C# source layout inside a project folder.
eng/Test-RepositoryProjectStructure.ps1 owns which files a project builds.

Every .csproj in the repository is checked, with the .cs files under its
folder. A file belongs to the nearest project folder above it.

  SRC0001  A project's RootNamespace differs from its AssemblyName. Both
           default to the project file name.
  SRC0002  A file's namespace is not the root namespace plus its folder path
           from the project folder. The namespace is the file's first
           'namespace X;' or 'namespace X {'. A file with no namespace fails,
           except a Program.cs with top-level statements and a file that
           declares no types, such as GlobalUsings.cs or AssemblyInfo.cs.
  SRC0003  A file declares more than one namespace.
  SRC0004  A folder holds more than 30 C# files directly. A .xaml.cs file
           counts once, with its .xaml.
  SRC0005  A project folder holds more than 10 C# files directly.
  SRC0006  In a project with PublicAPI.Shipped.txt, a public top-level type
           is under a folder named Internal.
  SRC0007  In such a project, a top-level type that is not public is outside
           every folder named Internal. A type with no access modifier is
           internal, except that a partial part with no modifier takes the one
           another part of the type declares in the same project.
  SRC0008  Each entry in eng/source-organization-exceptions.json names a code,
           a path and a reason, and still matches a violation, so the list
           only shrinks. The path is the project file for SRC0001, the folder
           for SRC0004 and SRC0005, and the C# file for the other codes.

The C# is read with a light scanner, not a compiler. Comments, string and
character literals and preprocessor lines are blanked first, so text inside
them is never read as code. A top-level type is a declaration at brace depth 0
under a file-scoped namespace or with no namespace, and at depth 1 inside a
block namespace.

bin, obj, artifacts, node_modules, .git, .vs and ThirdParty folders are
skipped, and so are generated *.g.cs, *.g.i.cs and *.Designer.cs files. The
script prints one line per violation and exits 1 if there is any.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    # The repository root. The self-test points it at temporary trees.
    [string] $Root = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$skippedFolders = [Collections.Generic.HashSet[string]]::new(
    [string[]]@('bin', 'obj', 'artifacts', 'node_modules', '.git', '.vs', 'ThirdParty'),
    [StringComparer]::OrdinalIgnoreCase)
$generatedSuffixes = @('.g.cs', '.g.i.cs', '.Designer.cs')
$folderFileLimit = 30
$projectFolderFileLimit = 10
$internalSegment = 'Internal'
$exceptionsRelativePath = 'eng/source-organization-exceptions.json'
$exceptionCode = 'SRC0008'

# Comments, literals and preprocessor lines, leftmost first. An interpolation
# hole may hold literals and one level of braces; a hole the pattern cannot
# read falls back to the plain string forms.
$hole = '\{(?:[^{}"'']|"(?:[^"\\\r\n]|\\.)*"|''(?:[^''\\\r\n]|\\.)+''|\{[^{}]*\})*\}'
$nonCode = [regex]::new(@(
        '(?m)^[ \t]*\#[^\r\n]*'                              # preprocessor line
        '//[^\r\n]*'                                         # line comment
        '/\*[\s\S]*?\*/'                                     # block comment
        '\$*(?<quotes>"{3,})[\s\S]*?\k<quotes>'              # raw string
        '(?:\$@|@\$)"(?:[^"{]|""|\{\{|' + $hole + ')*"'      # interpolated verbatim string
        '\$"(?:[^"\\{\r\n]|\\.|\{\{|' + $hole + ')*"'        # interpolated string
        '@"(?:[^"]|"")*"'                                    # verbatim string
        '"(?:[^"\\\r\n]|\\.)*"'                              # string
        "'(?:[^'\\\r\n]|\\.)+'"                              # character
    ) -join '|')
$balancedBlock = [regex]::new('\{(?>[^{}]+|\{(?<open>)|\}(?<-open>))*(?(open)(?!))\}')
$namespaceDeclaration = [regex]::new('(?<![\w@.])namespace\s+(?<name>@?[A-Za-z_][\w.]*)\s*(?<form>[;{])')
$modifiers = '(?<modifiers>(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe)\s+)*)'
$declarationStart = '(?<=(?:^|[;{}\]])\s*)'
$typeDeclaration = [regex]::new(
    "$declarationStart$modifiers(?<kind>class|struct|interface|enum|record(?:\s+class|\s+struct)?)\s+(?<name>@?[A-Za-z_]\w*)(?:\s*<(?<typeParameters>[^<>]*)>)?" +
    "|$declarationStart$modifiers(?<kind>delegate)\s+[\w.<>\[\]?,\s]+?\s(?<name>@?[A-Za-z_]\w*)\s*(?:<(?<typeParameters>[^<>]*)>)?\s*\(")
$accessModifier = [regex]::new('\b(public|internal|private|protected|file)\b')
# Leading directives and global attributes; what follows them in a file with
# no namespace is either a type declaration or a top-level statement.
$fileHeader = [regex]::new('^\s*(?:(?:global\s+)?using\s[^;]*;\s*|extern\s+alias\s[^;]*;\s*|\[\s*(?:assembly|module)\s*:[^\]]*\]\s*)*')
$typeStart = [regex]::new('^(?:\[|(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe)\s+)*(?:class|struct|interface|enum|record|delegate)\b)')

function Get-RepositoryPath([string] $Path) {
    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

# A JSON member that may be absent; StrictMode throws on reading a missing property.
function Get-JsonMember([object] $Object, [string] $Name) {
    $member = $Object.PSObject.Properties[$Name]
    if ($null -eq $member) { return $null }
    return $member.Value
}

$violations = [Collections.Generic.List[string]]::new()
$exceptions = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$exceptionOrder = [Collections.Generic.List[string]]::new()
$usedExceptions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$exceptionsPath = Join-Path $Root $exceptionsRelativePath
function Get-ExceptionKey([string] $Code, [string] $Path) { return "$Code|$($Path.Replace('\', '/').TrimEnd('/'))" }

# A listed exception absorbs every violation of its code at its path.
function Add-Violation([string] $Code, [string] $Path, [string] $Message) {
    $relative = Get-RepositoryPath $Path
    $key = Get-ExceptionKey $Code $relative
    if ($Code -ne $exceptionCode -and $exceptions.ContainsKey($key)) {
        [void] $usedExceptions.Add($key)
        return
    }
    $violations.Add("$Code ${relative}: $Message")
}

if (Test-Path -LiteralPath $exceptionsPath -PathType Leaf) {
    $document = Get-Content -LiteralPath $exceptionsPath -Raw | ConvertFrom-Json
    $entries = Get-JsonMember $document 'exceptions'
    foreach ($entry in @(if ($null -ne $entries) { $entries })) {
        $code = Get-JsonMember $entry 'code'
        $path = Get-JsonMember $entry 'path'
        if ([string]::IsNullOrWhiteSpace($code) -or [string]::IsNullOrWhiteSpace($path)) {
            Add-Violation $exceptionCode $exceptionsPath 'an entry has no code or no path.'
            continue
        }
        if ([string]::IsNullOrWhiteSpace((Get-JsonMember $entry 'reason'))) {
            Add-Violation $exceptionCode $exceptionsPath "$code for '$path' has no reason."
        }
        $key = Get-ExceptionKey $code $path
        if ($exceptions.ContainsKey($key)) {
            Add-Violation $exceptionCode $exceptionsPath "$code for '$path' is listed twice."
            continue
        }
        $exceptions[$key] = $entry
        $exceptionOrder.Add($key)
    }
}

# Files under $Directory, without descending into skipped or reparse-point folders.
function Get-TreeFiles([string] $Directory) {
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($Directory))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($file in $current.EnumerateFiles()) { $file }
        foreach ($child in $current.EnumerateDirectories()) {
            $isReparsePoint = ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            if (-not $skippedFolders.Contains($child.Name) -and -not $isReparsePoint) { $pending.Push($child) }
        }
    }
}

# The last value of an MSBuild property in the project file, read as written,
# or the project name when the file does not set it.
function Get-ProjectProperty([xml] $Document, [string] $Name, [string] $ProjectName) {
    $value = $null
    foreach ($node in $Document.SelectNodes("//*[local-name()='PropertyGroup']/*[local-name()='$Name']")) { $value = $node.InnerText.Trim() }
    if ([string]::IsNullOrWhiteSpace($value)) { return $ProjectName }
    return $value.Replace('$(MSBuildProjectName)', $ProjectName)
}

# The source with comments, literals and preprocessor lines blanked.
function Get-Code([string] $Source) {
    return $nonCode.Replace($Source, ' ')
}

# $Code with the content of every brace block one level down removed, so only
# declarations at its own depth remain.
function Get-OuterText([string] $Code) {
    return $balancedBlock.Replace($Code, '{}')
}

function Test-TopLevelStatements([string] $OuterText) {
    $rest = $OuterText.Substring($fileHeader.Match($OuterText).Length).TrimStart()
    return $rest.Length -gt 0 -and -not $typeStart.IsMatch($rest)
}

$allFiles = @(Get-TreeFiles $Root)
$projects = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($projectFile in @($allFiles | Where-Object { $_.Extension -ieq '.csproj' } | Sort-Object FullName)) {
    if ($projects.ContainsKey($projectFile.DirectoryName)) { continue }
    $name = [IO.Path]::GetFileNameWithoutExtension($projectFile.Name)
    [xml] $document = Get-Content -LiteralPath $projectFile.FullName -Raw
    $rootNamespace = Get-ProjectProperty $document 'RootNamespace' $name
    $assemblyName = Get-ProjectProperty $document 'AssemblyName' $name
    if ($rootNamespace -cne $assemblyName) {
        Add-Violation 'SRC0001' $projectFile.FullName "RootNamespace '$rootNamespace' differs from AssemblyName '$assemblyName'. Make the root namespace the assembly name, and the namespaces of its files with it."
    }
    $projects[$projectFile.DirectoryName] = [pscustomobject]@{
        Directory = $projectFile.DirectoryName
        RootNamespace = $rootNamespace
        TracksPublicApi = Test-Path -LiteralPath (Join-Path $projectFile.DirectoryName 'PublicAPI.Shipped.txt') -PathType Leaf
    }
}

# The project that owns each folder: the nearest project folder at or above it.
$folderOwners = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
function Get-FolderOwner([string] $Directory) {
    if ($folderOwners.ContainsKey($Directory)) { return $folderOwners[$Directory] }
    $owner = $null
    if ($projects.ContainsKey($Directory)) { $owner = $projects[$Directory] }
    elseif ($Directory.Length -gt $Root.Length) {
        $parent = [IO.Path]::GetDirectoryName($Directory)
        if ($null -ne $parent) { $owner = Get-FolderOwner $parent }
    }
    $folderOwners[$Directory] = $owner
    return $owner
}

$sourceFiles = @($allFiles | Where-Object {
        $file = $_
        $file.Extension -ieq '.cs' -and -not @($generatedSuffixes | Where-Object { $file.Name.EndsWith($_, [StringComparison]::OrdinalIgnoreCase) })
    } | Sort-Object FullName)
$folderCounts = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::OrdinalIgnoreCase)
$typeParts = [Collections.Generic.List[object]]::new()
$checkedFiles = 0

foreach ($sourceFile in $sourceFiles) {
    $project = Get-FolderOwner $sourceFile.DirectoryName
    if ($null -eq $project) { continue }
    $checkedFiles++
    $count = 0
    [void] $folderCounts.TryGetValue($sourceFile.DirectoryName, [ref] $count)
    $folderCounts[$sourceFile.DirectoryName] = $count + 1

    $folder = [IO.Path]::GetRelativePath($project.Directory, $sourceFile.DirectoryName).Replace('\', '/')
    $segments = @(if ($folder -ne '.') { $folder.Split('/') })
    $expectedNamespace = (@($project.RootNamespace) + $segments) -join '.'

    $code = Get-Code ([IO.File]::ReadAllText($sourceFile.FullName))
    $namespaces = $namespaceDeclaration.Matches($code)
    if ($namespaces.Count -gt 1) {
        Add-Violation 'SRC0003' $sourceFile.FullName "declares $($namespaces.Count) namespaces. Give each namespace its own files."
    }

    $namespace = $null
    if ($namespaces.Count -eq 0) {
        $outerText = Get-OuterText $code
    }
    else {
        $declaration = $namespaces[0]
        $namespace = $declaration.Groups['name'].Value.Replace('@', '')
        if ($declaration.Groups['form'].Value -eq ';') {
            $outerText = Get-OuterText $code
        }
        else {
            $block = $balancedBlock.Match($code, $declaration.Groups['form'].Index)
            $body = if ($block.Success) { $block.Value.Substring(1, $block.Length - 2) } else { $code.Substring($declaration.Groups['form'].Index + 1) }
            $outerText = Get-OuterText $body
        }
    }

    $types = $typeDeclaration.Matches($outerText)
    if ($null -eq $namespace) {
        $isProgram = $sourceFile.Name -ieq 'Program.cs' -and (Test-TopLevelStatements $outerText)
        if ($types.Count -gt 0 -and -not $isProgram) {
            Add-Violation 'SRC0002' $sourceFile.FullName "declares no namespace. Declare namespace '$expectedNamespace'."
        }
    }
    elseif ($namespace -cne $expectedNamespace) {
        Add-Violation 'SRC0002' $sourceFile.FullName "namespace '$namespace' does not match its folder. Use '$expectedNamespace', or move the file to the folder of its namespace."
    }

    if (-not $project.TracksPublicApi) { continue }
    foreach ($type in $types) {
        $typeParameters = $type.Groups['typeParameters']
        $arity = if ($typeParameters.Success) { $typeParameters.Value.Split(',').Count } else { 0 }
        $access = $accessModifier.Match($type.Groups['modifiers'].Value)
        $typeParts.Add([pscustomobject]@{
                Project = $project.Directory
                Path = $sourceFile.FullName
                IsUnderInternal = $segments -ccontains $internalSegment
                Kind = ($type.Groups['kind'].Value -replace '\s+', ' ')
                Name = $type.Groups['name'].Value
                Key = "$($project.Directory)|$namespace|$($type.Groups['name'].Value)``$arity"
                Access = if ($access.Success) { $access.Value } else { $null }
                IsPartial = $type.Groups['modifiers'].Value -match '\bpartial\b'
            })
    }
}

# A partial part with no access modifier takes the one another part declares.
$declaredAccess = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($part in $typeParts) {
    if ($null -ne $part.Access -and -not $declaredAccess.ContainsKey($part.Key)) { $declaredAccess[$part.Key] = $part.Access }
}
foreach ($part in $typeParts) {
    $access = $part.Access
    if ($null -eq $access -and $part.IsPartial -and $declaredAccess.ContainsKey($part.Key)) { $access = $declaredAccess[$part.Key] }
    if ($null -eq $access) { $access = 'internal' }
    if ($access -eq 'public' -and $part.IsUnderInternal) {
        Add-Violation 'SRC0006' $part.Path "public $($part.Kind) '$($part.Name)' is under an $internalSegment folder. Move it to the feature namespace consumers import."
    }
    elseif ($access -ne 'public' -and -not $part.IsUnderInternal) {
        Add-Violation 'SRC0007' $part.Path "$access $($part.Kind) '$($part.Name)' is outside an $internalSegment folder. Move it under its feature's $internalSegment folder."
    }
}

foreach ($directory in @($folderCounts.Keys | Sort-Object)) {
    $count = $folderCounts[$directory]
    if ($count -gt $folderFileLimit) {
        Add-Violation 'SRC0004' $directory "holds $count C# files directly; the limit is $folderFileLimit. Split it by responsibility into subfolders."
    }
    if ($projects.ContainsKey($directory) -and $count -gt $projectFolderFileLimit) {
        Add-Violation 'SRC0005' $directory "is a project folder that holds $count C# files directly; the limit is $projectFolderFileLimit. Keep entry points, assembly-level files and front-door types there, and move the rest into folders."
    }
}

foreach ($key in $exceptionOrder) {
    if ($usedExceptions.Contains($key)) { continue }
    $entry = $exceptions[$key]
    Add-Violation $exceptionCode $exceptionsPath "'$(Get-JsonMember $entry 'path')' no longer has a $(Get-JsonMember $entry 'code') violation; remove its entry."
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Output $_ }
    Write-Output "$($violations.Count) source organization violation(s) in $Root. See AGENTS.md, ""Code Structure Standards"", ""Source organization""."
    exit 1
}
Write-Output "Source organization check passed: $($projects.Count) projects, $checkedFiles C# files and $($usedExceptions.Count) listed exceptions in $Root."
exit 0
