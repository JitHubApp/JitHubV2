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

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Output $_ }
    Write-Output "$($violations.Count) code structure violation(s) in $Root. See AGENTS.md, ""Code Structure Standards""."
    exit 1
}
Write-Output "Code structure check passed: $($projectFiles.Count) projects and $($sourceFiles.Count) source files in $Root."
