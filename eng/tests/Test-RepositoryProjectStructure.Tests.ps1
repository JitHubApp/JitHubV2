# Self-test for Test-RepositoryProjectStructure.ps1: a conforming temporary tree
# passes, and each rule fails on a tree that breaks only that rule.
#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Test-RepositoryProjectStructure.ps1'))
$pwsh = (Get-Process -Id $PID).Path
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "jithub-project-folders-$([guid]::NewGuid().ToString('N'))"

function Write-TreeFile([string] $Tree, [string] $RelativePath, [string] $Content) {
    $path = Join-Path $Tree $RelativePath
    [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Content)
}

function Write-Exceptions([string] $Tree, [object[]] $Includes) {
    Write-TreeFile $Tree 'eng/project-folder-exceptions.json' (@{ includes = $Includes } | ConvertTo-Json -Depth 4)
}

# A tree that follows every rule: in-folder items in each spelling the check
# reads, package and item references, a listed exception, and violations only
# where the check does not look.
function New-ConformingTree([string] $Name) {
    $tree = Join-Path $tempRoot $Name
    Write-TreeFile $tree 'LICENSE' 'MIT'
    Write-TreeFile $tree 'App/App.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Generated\*.cs;Views\**\*.cs" />
    <Page Include="Views\MainPage.xaml" />
    <ApplicationDefinition Include="App.xaml" />
    <PRIResource Include="Strings\en-US\Resources.resw" />
    <Content Include="$(MSBuildThisFileDirectory)Assets\Logo.png" />
    <Content Include="$(MSBuildProjectDirectory)\Assets\..\Assets\Icon.ico" />
    <Content Include="$(NuGetPackageRoot)\some.package\1.0.0\license.txt" Link="Notices\license.txt" />
    <None Include="$(PkgSome_Package)\NOTICE.txt" />
    <EmbeddedResource Include=".\Resources\Data.json" />
    <Content Include="..\LICENSE" Link="Notices\App-LICENSE.txt" />
    <None Include="@(Content)" />
    <!-- Update, Remove and other item types do not add the project's files. -->
    <None Update="..\Lib\README.md" />
    <Compile Remove="..\Lib\**" />
    <AdditionalFiles Include="..\eng\BannedSymbols.txt" />
  </ItemGroup>
  <Target Name="Stage">
    <ItemGroup>
      <Content Include="Staged\*.txt" />
    </ItemGroup>
  </Target>
</Project>
'@
    Write-TreeFile $tree 'Lib/Lib.csproj' '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><None Include="README.md" Pack="true" /></ItemGroup></Project>'
    # A shared import resolves plain paths against each importing project.
    Write-TreeFile $tree 'Directory.Build.props' '<Project><ItemGroup><None Include="$(MSBuildProjectDirectory)\PACKAGE.md" /><None Include="NOTES.md" /></ItemGroup></Project>'
    Write-Exceptions $tree @(@{ file = 'App/App.csproj'; include = '..\LICENSE'; reason = 'Self-test exception.' })
    # Build output and dependencies are skipped.
    Write-TreeFile $tree 'App/obj/App.csproj.nuget.g.props' '<Project><ItemGroup><None Include="..\..\Lib\README.md" /></ItemGroup></Project>'
    Write-TreeFile $tree 'web/node_modules/pkg/pkg.csproj' '<Project><ItemGroup><Compile Include="..\..\..\App\Program.cs" /></ItemGroup></Project>'
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
    $violations = @($result.Output | Where-Object { $_ -match '^RPS\d{4} ' })
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

    Assert-Check 'RPS0001 compile link into another project' {
        param($tree)
        Write-TreeFile $tree 'Lib.Tests/Lib.Tests.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="..\App\Services\Service.cs" />
    <Compile Include="..\App\ViewModels\**\*.cs" />
  </ItemGroup>
</Project>
'@
    } @(
        "RPS0001 Lib.Tests/Lib.Tests.csproj: Compile '..\App\Services\Service.cs'"
        "RPS0001 Lib.Tests/Lib.Tests.csproj: Compile '..\App\ViewModels\**\*.cs'"
    )

    Assert-Check 'RPS0001 every checked item type' {
        param($tree)
        Write-TreeFile $tree 'Lib/Lib.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Page Include="..\App\Views\Shared.xaml" />
    <ApplicationDefinition Include="..\App\App.xaml" />
    <PRIResource Include="..\App\Strings\en-US\Resources.resw" />
    <None Include="..\docs\guide.md" />
    <Content Include="..\App\Assets\Logo.png" />
    <EmbeddedResource Include="..\App\Resources\Data.json" />
  </ItemGroup>
</Project>
'@
    } @(
        "RPS0001 Lib/Lib.csproj: Page '..\App\Views\Shared.xaml'"
        "RPS0001 Lib/Lib.csproj: ApplicationDefinition '..\App\App.xaml'"
        "RPS0001 Lib/Lib.csproj: PRIResource '..\App\Strings\en-US\Resources.resw'"
        "RPS0001 Lib/Lib.csproj: None '..\docs\guide.md'"
        "RPS0001 Lib/Lib.csproj: Content '..\App\Assets\Logo.png'"
        "RPS0001 Lib/Lib.csproj: EmbeddedResource '..\App\Resources\Data.json'"
    )

    Assert-Check 'RPS0001 each entry of a multi-file include, in a target' {
        param($tree)
        Write-TreeFile $tree 'Lib/Lib.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <Target Name="Stage">
    <ItemGroup>
      <Content Include="Assets\Own.png; ..\App\Assets\Logo.png" />
    </ItemGroup>
  </Target>
</Project>
'@
    } @("RPS0001 Lib/Lib.csproj: Content '..\App\Assets\Logo.png'")

    Assert-Check 'RPS0001 paths that climb out after descending, or are rooted' {
        param($tree)
        Write-TreeFile $tree 'Lib/Lib.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <None Include="docs\..\..\App\README.md" />
    <None Include="$(MSBuildProjectDirectory)\..\App\README.md" />
    <None Include="$(MSBuildThisFileDirectory)..\App\README.md" />
    <None Include="C:\shared\README.md" />
  </ItemGroup>
</Project>
'@
    } @(
        "RPS0001 Lib/Lib.csproj: None 'docs\..\..\App\README.md'"
        "RPS0001 Lib/Lib.csproj: None '`$(MSBuildProjectDirectory)\..\App\README.md'"
        "RPS0001 Lib/Lib.csproj: None '`$(MSBuildThisFileDirectory)..\App\README.md'"
        "RPS0001 Lib/Lib.csproj: None 'C:\shared\README.md'"
    )

    Assert-Check 'RPS0001 shared import that adds a file from outside the projects' {
        param($tree)
        Write-TreeFile $tree 'Directory.Build.props' @'
<Project>
  <ItemGroup>
    <None Include="$(MSBuildThisFileDirectory)LICENSE" Pack="true" />
    <None Include="..\icon.png" Pack="true" />
  </ItemGroup>
</Project>
'@
    } @(
        "RPS0001 Directory.Build.props: None '`$(MSBuildThisFileDirectory)LICENSE'"
        "RPS0001 Directory.Build.props: None '..\icon.png'"
    )

    Assert-Check 'import inside a project folder may name its own files' {
        param($tree)
        Write-TreeFile $tree 'App/App.Assets.targets' '<Project><ItemGroup><Content Include="$(MSBuildThisFileDirectory)Assets\Splash.png" /></ItemGroup></Project>'
    } @()

    Assert-Check 'RPS0002 include from a property the check cannot place' {
        param($tree)
        Write-TreeFile $tree 'Lib/Lib.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><SharedRoot>$(MSBuildThisFileDirectory)..\App</SharedRoot></PropertyGroup>
  <ItemGroup>
    <Compile Include="$(SharedRoot)\Service.cs" />
    <None Include="Notices\$(Rid)\NOTICE.txt" />
    <None Include="$(MSBuildProjectDirectory)\$(Rid)\NOTICE.txt" />
  </ItemGroup>
</Project>
'@
    } @(
        "RPS0002 Lib/Lib.csproj: Compile '`$(SharedRoot)\Service.cs'"
        "RPS0002 Lib/Lib.csproj: None 'Notices\`$(Rid)\NOTICE.txt'"
        "RPS0002 Lib/Lib.csproj: None '`$(MSBuildProjectDirectory)\`$(Rid)\NOTICE.txt'"
    )

    Assert-Check 'listed includes pass, including one from a property' {
        param($tree)
        Write-TreeFile $tree 'Lib/Lib.csproj' '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><None Include="$(PackageReadme)" Pack="true" /></ItemGroup></Project>'
        Write-Exceptions $tree @(
            @{ file = 'App/App.csproj'; include = '..\LICENSE'; reason = 'Self-test exception.' }
            @{ file = 'Lib/Lib.csproj'; include = '$(PackageReadme)'; reason = 'Self-test exception.' }
        )
    } @()

    Assert-Check 'RPS0001 an exception covers only the file and include it names' {
        param($tree)
        Write-TreeFile $tree 'Lib/Lib.csproj' '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><None Include="..\LICENSE" Pack="true" /><Content Include="..\LICENSE.txt" /></ItemGroup></Project>'
    } @(
        "RPS0001 Lib/Lib.csproj: None '..\LICENSE'"
        "RPS0001 Lib/Lib.csproj: Content '..\LICENSE.txt'"
    )

    Assert-Check 'RPS0003 exceptions that are incomplete or not current' {
        param($tree)
        Write-Exceptions $tree @(
            @{ file = 'App/App.csproj'; include = '..\LICENSE'; reason = 'Self-test exception.' }
            @{ file = 'App/App.csproj'; include = '..\LICENSE'; reason = 'Listed twice.' }
            @{ include = '..\README.md'; reason = 'No file.' }
            @{ file = 'Lib/Lib.csproj'; include = 'README.md'; reason = 'In its own folder.' }
            @{ file = 'Gone/Gone.csproj'; include = '..\LICENSE'; reason = 'The project is gone.' }
            @{ file = 'App/App.csproj'; include = '..\NOTICE'; reason = '' }
        )
    } @(
        "RPS0003 eng/project-folder-exceptions.json: '..\LICENSE' in 'App/App.csproj' is listed twice."
        'RPS0003 eng/project-folder-exceptions.json: an entry has no file or no include.'
        "RPS0003 eng/project-folder-exceptions.json: '..\NOTICE' in 'App/App.csproj' has no reason."
        "RPS0003 eng/project-folder-exceptions.json: 'README.md' in 'Lib/Lib.csproj' is no longer an out-of-folder include"
        "RPS0003 eng/project-folder-exceptions.json: 'Gone/Gone.csproj' does not exist"
        "RPS0003 eng/project-folder-exceptions.json: '..\NOTICE' in 'App/App.csproj' is no longer an out-of-folder include"
    )

    Assert-Check 'RPS0001 without an exceptions file' {
        param($tree)
        [IO.File]::Delete((Join-Path $tree 'eng/project-folder-exceptions.json'))
    } @("RPS0001 App/App.csproj: Content '..\LICENSE'")
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) project folder self-test case(s) failed: $($failures -join '; ')"
}
Write-Host 'Project folder check self-test passed.'
# The negative cases run the check in child processes that exit 1. End with an
# explicit success code so that last child's exit code is not the script's.
exit 0
