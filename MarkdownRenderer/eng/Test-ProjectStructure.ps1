<#
.SYNOPSIS
Checks the MarkdownRenderer code structure rules that no single project build
can see.

.DESCRIPTION
Part of AGENTS.md "Code Structure Standards". The build check in
MarkdownRenderer/Directory.Build.targets covers each project's Compile and
InternalsVisibleTo items (MRS0001, MRS0002). This script covers the rest:

  MRS0003  No source file declares [assembly: InternalsVisibleTo]. Grants are
           InternalsVisibleTo items in the owning project file, where the build
           check sees them.
  MRS0004  Every .cs file under src, tests, samples, perf and tools lies in a
           folder that holds a project file, unless a project compiles it
           through a Compile item with ProjectStructureException metadata.
  MRS0005  Every project file under those folders is in MarkdownRenderer.slnx.
  MRS0006  Every project that MarkdownRenderer.slnx lists exists.
  MRS0007  A Compile item with ProjectStructureException names literal files
           that exist, so each exception is explicit.
  MRS0008  No .cs file under those folders is over 1,000 lines unless
           eng/file-size-exceptions.json lists it with a reason.
  MRS0009  A listed file stays within its recorded line cap.
  MRS0010  Each size exception is current: the file exists, is still over
           1,000 lines, and its cap equals its length, so the cap only
           ratchets down.
  MRS0011  No project under src declares RuntimeIdentifier,
           RuntimeIdentifiers or PlatformTarget. Shipping libraries inherit
           their supported runtimes and architecture-neutral target from
           src/Directory.Build.props, so every package and lock file agrees.

bin, obj and artifacts folders are skipped. The script prints one line per
violation and exits 1 if there is any.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    # The MarkdownRenderer folder. The self-test points it at temporary trees.
    [string] $Root = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$sourceFolders = @('src', 'tests', 'samples', 'perf', 'tools')
$projectExtensions = @('.csproj', '.fsproj', '.vbproj', '.vcxproj', '.wapproj', '.esproj')
$skippedFolders = [Collections.Generic.HashSet[string]]::new([string[]]@('bin', 'obj', 'artifacts'), [StringComparer]::OrdinalIgnoreCase)
$pathComparer = [StringComparer]::OrdinalIgnoreCase

$violations = [Collections.Generic.List[string]]::new()
function Add-Violation([string] $Code, [string] $Path, [string] $Message) {
    $relative = [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
    $violations.Add("$Code ${relative}: $Message")
}

# Files under $Directory, without descending into skipped or reparse-point folders.
function Get-TreeFiles([string] $Directory) {
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    if (Test-Path -LiteralPath $Directory -PathType Container) { $pending.Push([IO.DirectoryInfo]::new($Directory)) }
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($file in $current.EnumerateFiles()) { $file }
        foreach ($child in $current.EnumerateDirectories()) {
            $isReparsePoint = ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            if (-not $skippedFolders.Contains($child.Name) -and -not $isReparsePoint) { $pending.Push($child) }
        }
    }
}

$allFiles = @(Get-TreeFiles $Root)
$sourceFiles = @($allFiles | Where-Object Extension -EQ '.cs')
$projectFiles = @($allFiles | Where-Object { $projectExtensions -contains $_.Extension.ToLowerInvariant() })
function Test-InSourceFolder([string] $Path) {
    $first = [IO.Path]::GetRelativePath($Root, $Path).Split([IO.Path]::DirectorySeparatorChar)[0]
    return $sourceFolders -contains $first
}

# MRS0003: grants belong in project files.
$assemblyGrant = [regex]::new('\[\s*assembly\s*:[^\]]*\bInternalsVisibleTo(Attribute)?\s*\(')
foreach ($file in $sourceFiles) {
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        $lineNumber++
        $code = $line.TrimStart()
        if ($code.StartsWith('//') -or $code.StartsWith('*')) { continue }
        if ($assemblyGrant.IsMatch($code)) {
            Add-Violation 'MRS0003' $file.FullName "line ${lineNumber}: [assembly: InternalsVisibleTo] in source. Declare the grant as an InternalsVisibleTo item in the project file so the build check sees it."
        }
    }
}

# Declared one-folder exceptions, read from the project files that compile them.
$exceptionFiles = [Collections.Generic.HashSet[string]]::new($pathComparer)
foreach ($project in $projectFiles) {
    [xml] $document = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($item in $document.SelectNodes("//*[local-name()='Compile']")) {
        $reason = $item.GetAttribute('ProjectStructureException')
        if ([string]::IsNullOrWhiteSpace($reason)) {
            $metadata = $item.SelectSingleNode("*[local-name()='ProjectStructureException']")
            if ($null -ne $metadata) { $reason = $metadata.InnerText }
        }
        if ([string]::IsNullOrWhiteSpace($reason)) { continue }
        foreach ($include in $item.GetAttribute('Include').Split(';', [StringSplitOptions]::RemoveEmptyEntries -bor [StringSplitOptions]::TrimEntries)) {
            if ($include -match '[$@%*?]') {
                Add-Violation 'MRS0007' $project.FullName "Compile '$include' has a ProjectStructureException but is not a literal file path; an exception names each file explicitly."
                continue
            }
            $path = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName $include))
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                Add-Violation 'MRS0007' $project.FullName "Compile '$include' has a ProjectStructureException but the file does not exist."
                continue
            }
            [void] $exceptionFiles.Add($path)
        }
    }
}

# MRS0004: every source file is owned by a project folder.
$projectDirectories = [Collections.Generic.HashSet[string]]::new([string[]]@($projectFiles | ForEach-Object DirectoryName), $pathComparer)
foreach ($file in $sourceFiles) {
    if (-not (Test-InSourceFolder $file.FullName)) { continue }
    $owned = $false
    for ($directory = $file.Directory; $null -ne $directory -and $directory.FullName.Length -gt $Root.Length; $directory = $directory.Parent) {
        if ($projectDirectories.Contains($directory.FullName)) { $owned = $true; break }
    }
    if (-not $owned -and -not $exceptionFiles.Contains($file.FullName)) {
        Add-Violation 'MRS0004' $file.FullName 'source file in no project folder. Move it into the project that compiles it, or into a library both projects reference; a linked file needs a Compile item with ProjectStructureException metadata.'
    }
}

# MRS0005 and MRS0006: the solution lists exactly the projects on disk.
$solutionPath = Join-Path $Root 'MarkdownRenderer.slnx'
$solutionProjects = [Collections.Generic.HashSet[string]]::new($pathComparer)
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    Add-Violation 'MRS0006' $solutionPath 'solution file not found.'
}
else {
    [xml] $solution = Get-Content -LiteralPath $solutionPath -Raw
    foreach ($entry in $solution.SelectNodes("//*[local-name()='Project']")) {
        $relative = $entry.GetAttribute('Path')
        $path = [IO.Path]::GetFullPath((Join-Path $Root $relative))
        [void] $solutionProjects.Add($path)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Add-Violation 'MRS0006' $solutionPath "lists '$relative', which does not exist."
        }
    }
}
foreach ($project in $projectFiles) {
    if ((Test-InSourceFolder $project.FullName) -and -not $solutionProjects.Contains($project.FullName)) {
        Add-Violation 'MRS0005' $project.FullName 'project is not in MarkdownRenderer.slnx.'
    }
}

# MRS0011: shipping libraries inherit their runtimes and platform target.
$inheritedPlatformProperties = @('RuntimeIdentifier', 'RuntimeIdentifiers', 'PlatformTarget')
foreach ($project in $projectFiles) {
    $first = [IO.Path]::GetRelativePath($Root, $project.FullName).Split([IO.Path]::DirectorySeparatorChar)[0]
    if ($first -ne 'src') { continue }
    [xml] $document = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($name in $inheritedPlatformProperties) {
        if ($document.SelectNodes("//*[local-name()='PropertyGroup']/*[local-name()='$name']").Count -gt 0) {
            Add-Violation 'MRS0011' $project.FullName "declares $name. Shipping libraries inherit it from src/Directory.Build.props; remove the local value."
        }
    }
}

# MRS0008 to MRS0010: files over 1,000 lines fail review unless listed, and a
# listed file's cap follows its length down.
$maxFileLines = 1000
$sizeExceptionsPath = Join-Path $Root 'eng/file-size-exceptions.json'
$sizeExceptions = [Collections.Generic.Dictionary[string, object]]::new($pathComparer)
# Entry paths in file order, so stale entries are reported in a stable order.
$sizeExceptionOrder = [Collections.Generic.List[string]]::new()
# A JSON member that may be absent; StrictMode throws on reading a missing property.
function Get-JsonMember([object] $Object, [string] $Name) {
    $member = $Object.PSObject.Properties[$Name]
    if ($null -eq $member) { return $null }
    return $member.Value
}
if (Test-Path -LiteralPath $sizeExceptionsPath -PathType Leaf) {
    $sizeDocument = Get-Content -LiteralPath $sizeExceptionsPath -Raw | ConvertFrom-Json
    $sizeEntries = Get-JsonMember $sizeDocument 'files'
    foreach ($entry in @(if ($null -ne $sizeEntries) { $sizeEntries })) {
        $entryPath = Get-JsonMember $entry 'path'
        if ([string]::IsNullOrWhiteSpace($entryPath)) {
            Add-Violation 'MRS0010' $sizeExceptionsPath 'an entry has no path.'
            continue
        }
        if ([string]::IsNullOrWhiteSpace((Get-JsonMember $entry 'reason'))) {
            Add-Violation 'MRS0010' $sizeExceptionsPath "'$entryPath' has no reason."
        }
        $entryLines = Get-JsonMember $entry 'lines'
        if ($entryLines -isnot [long] -and $entryLines -isnot [int]) {
            Add-Violation 'MRS0010' $sizeExceptionsPath "'$entryPath' has no whole-number line cap."
            continue
        }
        $path = [IO.Path]::GetFullPath((Join-Path $Root $entryPath))
        if ($sizeExceptions.ContainsKey($path)) {
            Add-Violation 'MRS0010' $sizeExceptionsPath "'$entryPath' is listed twice."
            continue
        }
        $sizeExceptions[$path] = $entry
        $sizeExceptionOrder.Add($path)
    }
}
foreach ($file in $sourceFiles) {
    if (-not (Test-InSourceFolder $file.FullName)) { continue }
    $lines = [IO.File]::ReadAllLines($file.FullName).Length
    if ($lines -le $maxFileLines) { continue }
    if (-not $sizeExceptions.ContainsKey($file.FullName)) {
        Add-Violation 'MRS0008' $file.FullName "$lines lines; a file over $maxFileLines lines fails review. Split it by responsibility."
        continue
    }
    $cap = [int] $sizeExceptions[$file.FullName].lines
    if ($lines -gt $cap) {
        Add-Violation 'MRS0009' $file.FullName "$lines lines, over its recorded cap of $cap in eng/file-size-exceptions.json. Move the new code into its own type."
    }
}
foreach ($path in $sizeExceptionOrder) {
    $relative = $sizeExceptions[$path].path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Violation 'MRS0010' $sizeExceptionsPath "'$relative' does not exist; remove its entry."
        continue
    }
    $lines = [IO.File]::ReadAllLines($path).Length
    $cap = [int] $sizeExceptions[$path].lines
    if ($lines -le $maxFileLines) {
        Add-Violation 'MRS0010' $sizeExceptionsPath "'$relative' is $lines lines, within the limit; remove its entry."
    }
    elseif ($lines -lt $cap) {
        Add-Violation 'MRS0010' $sizeExceptionsPath "'$relative' is $lines lines; lower its cap from $cap to $lines."
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Output $_ }
    Write-Output "$($violations.Count) code structure violation(s) in $Root. See AGENTS.md, ""Code Structure Standards""."
    exit 1
}
Write-Output "Code structure check passed: $($projectFiles.Count) projects and $($sourceFiles.Count) source files in $Root."
