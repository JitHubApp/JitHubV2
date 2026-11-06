<#
.SYNOPSIS
Lists the project files and central package files that the dependency audit
reads under a repository root.

.DESCRIPTION
Writes one FileInfo for each *.csproj and Directory.Packages.props file under
-Root. Generated folders (bin, obj, bin-*, obj-*, artifacts, _scratch,
.codex-artifacts, .git) and reparse points such as junctions are not
descended into, so the audit never reads build output or a tree that lives
outside the repository.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Root
)

$ErrorActionPreference = "Stop"

$excludedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('bin', 'obj', 'artifacts', '_scratch', '.codex-artifacts', '.git')) {
    [void] $excludedDirectoryNames.Add($name)
}

$pendingDirectories = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
$pendingDirectories.Push([System.IO.DirectoryInfo]::new($Root))

while ($pendingDirectories.Count -gt 0) {
    $directory = $pendingDirectories.Pop()
    foreach ($projectFile in $directory.EnumerateFiles('*.csproj', [System.IO.SearchOption]::TopDirectoryOnly)) {
        $projectFile
    }

    foreach ($packagesFile in $directory.EnumerateFiles('Directory.Packages.props', [System.IO.SearchOption]::TopDirectoryOnly)) {
        $packagesFile
    }

    foreach ($childDirectory in $directory.EnumerateDirectories('*', [System.IO.SearchOption]::TopDirectoryOnly)) {
        $isGeneratedDirectory =
            $excludedDirectoryNames.Contains($childDirectory.Name) -or
            $childDirectory.Name.StartsWith('bin-', [StringComparison]::OrdinalIgnoreCase) -or
            $childDirectory.Name.StartsWith('obj-', [StringComparison]::OrdinalIgnoreCase)
        $isReparsePoint = ($childDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        if (-not $isGeneratedDirectory -and -not $isReparsePoint) {
            $pendingDirectories.Push($childDirectory)
        }
    }
}
