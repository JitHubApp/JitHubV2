# Self-test for Test-ProjectStructure.ps1: a conforming temporary tree passes,
# and each rule fails on a tree that breaks only that rule.
#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Test-ProjectStructure.ps1'))
$pwsh = (Get-Process -Id $PID).Path
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "markdown-renderer-structure-$([guid]::NewGuid().ToString('N'))"

function Write-TreeFile([string] $Tree, [string] $RelativePath, [string] $Content) {
    $path = Join-Path $Tree $RelativePath
    [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Content)
}

# A tree that follows every rule, including one declared compile-link exception.
function New-ConformingTree([string] $Name) {
    $tree = Join-Path $tempRoot $Name
    Write-TreeFile $tree 'MarkdownRenderer.slnx' @'
<Solution>
  <Folder Name="/src/">
    <Project Path="src/Lib/Lib.csproj" />
    <Project Path="src/Lib.Pack/Lib.Pack.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Lib.Tests/Lib.Tests.csproj" />
  </Folder>
</Solution>
'@
    Write-TreeFile $tree 'src/Lib/Lib.csproj' '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><InternalsVisibleTo Include="Lib.Tests" /></ItemGroup></Project>'
    Write-TreeFile $tree 'src/Lib/Library.cs' 'namespace Lib; public static class Library { }'
    Write-TreeFile $tree 'src/Lib/Properties/Notes.cs' '// [assembly: InternalsVisibleTo("Lib.Other")] stays a comment.'
    Write-TreeFile $tree 'src/Lib.Shared/Shared.cs' 'namespace Lib; internal static class Shared { }'
    Write-TreeFile $tree 'src/Lib.Pack/Lib.Pack.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="..\Lib.Shared\Shared.cs" ProjectStructureException="Self-test exception." />
  </ItemGroup>
</Project>
'@
    Write-TreeFile $tree 'tests/Lib.Tests/Lib.Tests.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    Write-TreeFile $tree 'tests/Lib.Tests/LibraryTests.cs' 'namespace Lib.Tests; public sealed class LibraryTests { }'
    # Generated and consumer trees are skipped.
    Write-TreeFile $tree 'src/Lib/obj/Lib.AssemblyInfo.cs' '[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lib.Tests")]'
    Write-TreeFile $tree 'artifacts/Consumer/Consumer.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    Write-TreeFile $tree 'tests/Lib.Tests/bin/Generated.cs' 'internal static class Generated { }'
    return $tree
}

function Invoke-Check([string] $Tree) {
    $output = @(& $pwsh -NoLogo -NoProfile -File $script -Root $Tree 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

$failures = [Collections.Generic.List[string]]::new()
function Assert-Check([string] $Name, [scriptblock] $Break, [string[]] $Expected) {
    $tree = New-ConformingTree $Name
    if ($Break) { & $Break $tree }
    $result = Invoke-Check $tree
    $violations = @($result.Output | Where-Object { $_ -match '^MRS\d{4} ' })
    $ok = if ($Expected.Count -eq 0) {
        $result.ExitCode -eq 0 -and $violations.Count -eq 0
    }
    else {
        $result.ExitCode -eq 1 -and $violations.Count -eq $Expected.Count -and
            @(for ($i = 0; $i -lt $Expected.Count; $i++) { $violations[$i].StartsWith($Expected[$i], [StringComparison]::Ordinal) }) -notcontains $false
    }
    if ($ok) { Write-Host "PASS $Name" }
    else {
        $failures.Add($Name)
        Write-Host "FAIL $Name (exit $($result.ExitCode)); expected: $($Expected -join ' | ')"
        $result.Output | ForEach-Object { Write-Host "    $_" }
    }
}

try {
    Assert-Check 'conforming tree passes' $null @()

    Assert-Check 'MRS0003 assembly attribute grant in source' {
        param($tree)
        Write-TreeFile $tree 'src/Lib/Properties/AssemblyInfo.cs' "using System.Runtime.CompilerServices;`n`n[assembly: InternalsVisibleTo(`"Lib.Other`")]"
    } @('MRS0003 src/Lib/Properties/AssemblyInfo.cs: line 3:')

    Assert-Check 'MRS0004 source folder without a project' {
        param($tree)
        Write-TreeFile $tree 'tools/Orphan/Orphan.cs' 'internal static class Orphan { }'
    } @('MRS0004 tools/Orphan/Orphan.cs:')

    Assert-Check 'MRS0004 compile link without an exception' {
        param($tree)
        Write-TreeFile $tree 'src/Lib.Pack/Lib.Pack.csproj' '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="..\Lib.Shared\Shared.cs" /></ItemGroup></Project>'
    } @('MRS0004 src/Lib.Shared/Shared.cs:')

    Assert-Check 'MRS0005 project missing from the solution' {
        param($tree)
        Write-TreeFile $tree 'samples/Demo/Demo.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    } @('MRS0005 samples/Demo/Demo.csproj:')

    Assert-Check 'MRS0006 solution entry that does not exist' {
        param($tree)
        $solution = Join-Path $tree 'MarkdownRenderer.slnx'
        $text = [IO.File]::ReadAllText($solution).Replace('</Solution>', "  <Project Path=`"perf/Gone/Gone.csproj`" />`n</Solution>")
        [IO.File]::WriteAllText($solution, $text)
    } @("MRS0006 MarkdownRenderer.slnx: lists 'perf/Gone/Gone.csproj'")

    Assert-Check 'MRS0007 exception that is not a literal existing file' {
        param($tree)
        Write-TreeFile $tree 'src/Lib.Pack/Lib.Pack.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="..\Lib.Shared\Shared.cs" ProjectStructureException="Self-test exception." />
    <Compile Include="..\Lib.Shared\*.cs" ProjectStructureException="Wildcards are not explicit." />
    <Compile Include="..\Lib.Shared\Missing.cs" ProjectStructureException="The file is gone." />
  </ItemGroup>
</Project>
'@
    } @("MRS0007 src/Lib.Pack/Lib.Pack.csproj: Compile '..\Lib.Shared\*.cs'", "MRS0007 src/Lib.Pack/Lib.Pack.csproj: Compile '..\Lib.Shared\Missing.cs'")
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) code structure self-test case(s) failed: $($failures -join '; ')"
}
Write-Host 'Code structure check self-test passed.'
