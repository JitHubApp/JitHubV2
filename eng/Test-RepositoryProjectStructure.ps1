<#
.SYNOPSIS
Checks that no project in the repository takes source or content files from
outside its own folder.

.DESCRIPTION
Part of AGENTS.md "Code Structure Standards": one project owns one folder, and
code or files two projects need go in a project both reference. This check
covers every MSBuild file in the repository, the app and the renderer alike.
MarkdownRenderer/eng/Test-ProjectStructure.ps1 keeps the renderer's other
structure rules.

  RPS0001  A Compile, Page, ApplicationDefinition, PRIResource, None, Content
           or EmbeddedResource Include names a file outside the folder of the
           project that builds it.
  RPS0002  Such an Include starts from a property the check cannot place.
           Name the file from the project folder, or list the include.
  RPS0003  Each entry in eng/project-folder-exceptions.json names an MSBuild
           file, the include exactly as written there and a reason, and
           still matches an include, so the list only shrinks.

An Include is read as written, without evaluating MSBuild:
  - A plain relative path is relative to the project folder. In a .props or
    .targets file that is the folder of each project that imports it.
  - $(MSBuildProjectDirectory) is the project folder.
  - $(MSBuildThisFileDirectory) is the folder of the file that declares the
    item. From a shared .props or .targets file above the projects, that
    names a file outside every project folder.
  - $(NuGetPackageRoot) and $(Pkg...) name files in restored packages, which
    are not repository files.
  - @(...) reuses items already declared, and is checked where they are.
Update and Remove do not add files and are not checked.

bin, obj, artifacts, node_modules and .git folders are skipped. The script
prints one line per violation and exits 1 if there is any.
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
$projectExtensions = @('.csproj', '.fsproj', '.vbproj', '.vcxproj', '.wapproj', '.esproj')
$importExtensions = @('.props', '.targets')
$checkedItemTypes = @('Compile', 'Page', 'ApplicationDefinition', 'PRIResource', 'None', 'Content', 'EmbeddedResource')
$skippedFolders = [Collections.Generic.HashSet[string]]::new(
    [string[]]@('bin', 'obj', 'artifacts', 'node_modules', '.git', '.vs'),
    [StringComparer]::OrdinalIgnoreCase)
$exceptionsRelativePath = 'eng/project-folder-exceptions.json'

$violations = [Collections.Generic.List[string]]::new()
function Get-RepositoryPath([string] $Path) {
    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}
function Add-Violation([string] $Code, [string] $Path, [string] $Message) {
    $violations.Add("$Code $(Get-RepositoryPath $Path): $Message")
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

# Path segments of $Path with . and .. applied. A leading '..' means the path
# climbs above its starting folder. Wildcards are ordinary segments here.
function Get-NormalizedSegments([string] $Path) {
    $segments = [Collections.Generic.List[string]]::new()
    foreach ($segment in $Path.Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($segment -eq '.') { continue }
        if ($segment -eq '..' -and $segments.Count -gt 0 -and $segments[$segments.Count - 1] -ne '..') {
            $segments.RemoveAt($segments.Count - 1)
            continue
        }
        $segments.Add($segment)
    }
    return , $segments
}

function Test-StaysInFolder([string] $RelativePath) {
    $segments = Get-NormalizedSegments $RelativePath
    return $segments.Count -eq 0 -or $segments[0] -ne '..'
}

# The exceptions, keyed by declaring file and include.
$exceptionsPath = Join-Path $Root $exceptionsRelativePath
$exceptions = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
$exceptionOrder = [Collections.Generic.List[string]]::new()
$usedExceptions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Get-ExceptionKey([string] $File, [string] $Include) { return "$($File.Replace('\', '/'))|$Include" }
# A JSON member that may be absent; StrictMode throws on reading a missing property.
function Get-JsonMember([object] $Object, [string] $Name) {
    $member = $Object.PSObject.Properties[$Name]
    if ($null -eq $member) { return $null }
    return $member.Value
}
if (Test-Path -LiteralPath $exceptionsPath -PathType Leaf) {
    $document = Get-Content -LiteralPath $exceptionsPath -Raw | ConvertFrom-Json
    $entries = Get-JsonMember $document 'includes'
    foreach ($entry in @(if ($null -ne $entries) { $entries })) {
        $file = Get-JsonMember $entry 'file'
        $include = Get-JsonMember $entry 'include'
        if ([string]::IsNullOrWhiteSpace($file) -or [string]::IsNullOrWhiteSpace($include)) {
            Add-Violation 'RPS0003' $exceptionsPath 'an entry has no file or no include.'
            continue
        }
        if ([string]::IsNullOrWhiteSpace((Get-JsonMember $entry 'reason'))) {
            Add-Violation 'RPS0003' $exceptionsPath "'$include' in '$file' has no reason."
        }
        $key = Get-ExceptionKey $file $include
        if ($exceptions.ContainsKey($key)) {
            Add-Violation 'RPS0003' $exceptionsPath "'$include' in '$file' is listed twice."
            continue
        }
        $exceptions[$key] = $entry
        $exceptionOrder.Add($key)
    }
}

$allFiles = @(Get-TreeFiles $Root)
$msbuildFiles = @($allFiles | Where-Object { ($projectExtensions + $importExtensions) -contains $_.Extension.ToLowerInvariant() } | Sort-Object FullName)
$projectDirectories = [Collections.Generic.HashSet[string]]::new(
    [string[]]@($allFiles | Where-Object { $projectExtensions -contains $_.Extension.ToLowerInvariant() } | ForEach-Object DirectoryName),
    [StringComparer]::OrdinalIgnoreCase)
$packageRoot = [regex]::new('^\$\((NuGetPackageRoot|Pkg[A-Za-z0-9_]+)\)')
$thisFileRoot = [regex]::new('^\$\(MSBuildThisFileDirectory\)')
$projectRoot = [regex]::new('^\$\(MSBuildProjectDirectory\)')

foreach ($msbuildFile in $msbuildFiles) {
    $isProject = $projectExtensions -contains $msbuildFile.Extension.ToLowerInvariant()
    $declaringFile = Get-RepositoryPath $msbuildFile.FullName
    [xml] $document = Get-Content -LiteralPath $msbuildFile.FullName -Raw
    foreach ($itemType in $checkedItemTypes) {
        foreach ($item in $document.SelectNodes("//*[local-name()='ItemGroup']/*[local-name()='$itemType']")) {
            $includeAttribute = $item.GetAttribute('Include')
            foreach ($include in $includeAttribute.Split(';', [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)) {
                if ($include.StartsWith('@(') -or $packageRoot.IsMatch($include)) { continue }
                $key = Get-ExceptionKey $declaringFile $include
                $inFolder = $null
                if ($thisFileRoot.IsMatch($include)) {
                    $rest = $thisFileRoot.Replace($include, '')
                    if (-not $rest.Contains('$(')) {
                        # The declaring file's folder is a project folder only for
                        # a project file or an import that sits in one.
                        $ownsFolder = $isProject -or $projectDirectories.Contains($msbuildFile.DirectoryName)
                        $inFolder = $ownsFolder -and (Test-StaysInFolder $rest)
                    }
                }
                elseif ($projectRoot.IsMatch($include)) {
                    $rest = $projectRoot.Replace($include, '')
                    if (-not $rest.Contains('$(')) { $inFolder = Test-StaysInFolder $rest }
                }
                elseif (-not $include.Contains('$(') -and -not $include.Contains('%(')) {
                    $inFolder = -not [IO.Path]::IsPathRooted($include) -and (Test-StaysInFolder $include)
                }

                if ($inFolder -eq $true) { continue }
                if ($exceptions.ContainsKey($key)) {
                    [void] $usedExceptions.Add($key)
                    continue
                }
                if ($null -eq $inFolder) {
                    Add-Violation 'RPS0002' $msbuildFile.FullName "$itemType '$include' starts from a property this check cannot place. Name the file from the project folder, or list the include in $exceptionsRelativePath with a reason."
                }
                else {
                    Add-Violation 'RPS0001' $msbuildFile.FullName "$itemType '$include' is outside the project folder. Move the file into the project that uses it, or into a project both reference; list a deliberate exception in $exceptionsRelativePath with a reason."
                }
            }
        }
    }
}

foreach ($key in $exceptionOrder) {
    if ($usedExceptions.Contains($key)) { continue }
    $entry = $exceptions[$key]
    $file = Get-JsonMember $entry 'file'
    $include = Get-JsonMember $entry 'include'
    if (-not (Test-Path -LiteralPath (Join-Path $Root $file) -PathType Leaf)) {
        Add-Violation 'RPS0003' $exceptionsPath "'$file' does not exist; remove its entry for '$include'."
    }
    else {
        Add-Violation 'RPS0003' $exceptionsPath "'$include' in '$file' is no longer an out-of-folder include; remove its entry."
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Output $_ }
    Write-Output "$($violations.Count) project folder violation(s) in $Root. See AGENTS.md, ""Code Structure Standards""."
    exit 1
}
Write-Output "Project folder check passed: $($msbuildFiles.Count) MSBuild files and $($usedExceptions.Count) listed exceptions in $Root."
