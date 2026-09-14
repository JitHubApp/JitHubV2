[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\mermaid-release-evidence')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$markdownRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $markdownRoot '..')).Path
$runName = 'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff', [Globalization.CultureInfo]::InvariantCulture)
$runDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory $runName))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$evidenceLockPath = Join-Path $runDirectory 'evidence.packages.lock.json'

function Copy-EvidenceSourceTree {
    param(
        [Parameter(Mandatory)][string] $Source,
        [Parameter(Mandatory)][string] $Destination
    )

    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        # NuGet lock files describe the canonical repository restore graph. The
        # evidence tree publishes several RIDs from a disposable graph, so a
        # copied lock would either be rewritten or reject the next RID as NU1004.
        if ($file.Name -ieq 'packages.lock.json') {
            continue
        }

        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $segments = $relative -split '[\\/]'
        if ($segments | Where-Object {
            $_ -eq 'bin' -or $_ -eq 'target' -or $_ -eq 'artifacts' -or $_ -like 'obj*'
        }) {
            continue
        }

        $destinationPath = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
        [IO.File]::Copy($file.FullName, $destinationPath, $true)
    }
}

# Release evidence must never rewrite the shared repository's lock files or
# project.assets.json files. Stage only the required source projects beneath
# this immutable run directory and let NuGet/MSBuild use their normal relative
# obj paths there. This also makes concurrent RID lanes independent.
$evidenceRepositoryRoot = Join-Path $runDirectory 'source'
$evidenceMarkdownRoot = Join-Path $evidenceRepositoryRoot 'MarkdownRenderer'
[IO.Directory]::CreateDirectory($evidenceMarkdownRoot) | Out-Null
[IO.File]::Copy(
    (Join-Path $repositoryRoot 'Directory.Build.props'),
    (Join-Path $evidenceRepositoryRoot 'Directory.Build.props'),
    $true)
[IO.File]::Copy(
    (Join-Path $repositoryRoot 'NuGet.Config'),
    (Join-Path $evidenceRepositoryRoot 'NuGet.Config'),
    $true)
[IO.File]::Copy(
    (Join-Path $repositoryRoot 'LICENSE'),
    (Join-Path $evidenceRepositoryRoot 'LICENSE'),
    $true)
[IO.Directory]::CreateDirectory((Join-Path $evidenceRepositoryRoot 'JitHub.Web\wwwroot')) | Out-Null
[IO.File]::Copy(
    (Join-Path $repositoryRoot 'JitHub.Web\wwwroot\icon-192.png'),
    (Join-Path $evidenceRepositoryRoot 'JitHub.Web\wwwroot\icon-192.png'),
    $true)
[IO.File]::Copy(
    (Join-Path $markdownRoot 'Directory.Build.targets'),
    (Join-Path $evidenceMarkdownRoot 'Directory.Build.targets'),
    $true)
Copy-EvidenceSourceTree `
    -Source (Join-Path $repositoryRoot 'docs\markdown-renderer') `
    -Destination (Join-Path $evidenceRepositoryRoot 'docs\markdown-renderer')
foreach ($projectName in @(
    'MarkdownRenderer',
    'MarkdownRenderer.Core',
    'MarkdownRenderer.Mermaid',
    'MarkdownRenderer.Mermaid.Tests'
)) {
    Copy-EvidenceSourceTree `
        -Source (Join-Path $markdownRoot $projectName) `
        -Destination (Join-Path $evidenceMarkdownRoot $projectName)
}

$stagedNuGetLocks = @(Get-ChildItem -LiteralPath $evidenceRepositoryRoot -Recurse -File -Filter 'packages.lock.json')
if ($stagedNuGetLocks.Count -ne 0) {
    throw "The disposable Mermaid evidence source unexpectedly contains NuGet lock files: $($stagedNuGetLocks.FullName -join ', ')."
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $commandPerformsRestore = $Arguments[0] -eq 'restore' -or
        ($Arguments[0] -in @('build', 'test', 'pack', 'publish') -and
         $Arguments -notcontains '--no-restore')
    if ($commandPerformsRestore) {
        # Every restore stays within this disposable run and is independent of
        # the repository's canonical lock graph. This also covers implicit
        # restores performed by project-reference publish probes.
        $Arguments += @(
            '-p:RestorePackagesWithLockFile=false',
            '-p:RestoreLockedMode=false',
            ("-p:NuGetLockFilePath=$evidenceLockPath")
        )
    }
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PeMachine {
    param([Parameter(Mandatory)][string] $Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "Deployment output '$Path' is not an MZ executable."
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or
        $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "Deployment output '$Path' has no valid PE signature."
    }

    switch ([BitConverter]::ToUInt16($bytes, $peOffset + 4)) {
        332 { return 'x86' }
        34404 { return 'x64' }
        43620 { return 'arm64' }
        default { return 'unknown' }
    }
}

function Invoke-CompatibleExecutable {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Rid,
        [Parameter(Mandatory)][string] $HostArchitecture
    )

    $compatible = $Rid -eq ('win-' + $HostArchitecture) -or
        ($HostArchitecture -eq 'x64' -and $Rid -eq 'win-x86')
    if (-not $compatible) {
        return [pscustomobject][ordered]@{
            executed = $false
            exitCode = $null
            output = $null
            reason = "Cross-published $Rid output cannot run on the $HostArchitecture evidence host."
        }
    }

    $output = @(& $Path 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Deployment smoke '$Path' failed with exit code $exitCode. Output: $($output -join "`n")"
    }

    return [pscustomobject][ordered]@{
        executed = $true
        exitCode = $exitCode
        output = ($output -join "`n")
        reason = $null
    }
}

$testsProject = Join-Path $evidenceMarkdownRoot 'MarkdownRenderer.Mermaid.Tests\MarkdownRenderer.Mermaid.Tests.csproj'
$packageProject = Join-Path $evidenceMarkdownRoot 'MarkdownRenderer.Mermaid\MarkdownRenderer.Mermaid.csproj'
$testResultsDirectory = Join-Path $runDirectory 'tests'
$packageDirectory = Join-Path $runDirectory 'packages'
[IO.Directory]::CreateDirectory($testResultsDirectory) | Out-Null
[IO.Directory]::CreateDirectory($packageDirectory) | Out-Null

Push-Location $evidenceRepositoryRoot
try {
    Invoke-DotNet @('restore', $testsProject, '-p:Configuration=Release', '-p:Platform=x64', '--disable-parallel')
    Invoke-DotNet @(
        'test', $testsProject, '-c', 'Release', '-p:Platform=x64', '--no-restore',
        '--disable-build-servers', '-p:UseSharedCompilation=false', '-nr:false',
        '--logger', 'trx;LogFileName=mermaid-contracts.trx', '--results-directory', $testResultsDirectory
    )
    Invoke-DotNet @(
        'pack', (Join-Path $evidenceMarkdownRoot 'MarkdownRenderer.Core\MarkdownRenderer.Core.csproj'),
        '-c', 'Release', '-p:Platform=x64', '--no-restore',
        '--disable-build-servers', '-p:UseSharedCompilation=false', '-nr:false', '-o', $packageDirectory
    )
    Invoke-DotNet @(
        'pack', $packageProject, '-c', 'Release', '-p:Platform=x64', '--no-restore',
        '--disable-build-servers', '-p:UseSharedCompilation=false', '-nr:false', '-o', $packageDirectory
    )
}
finally {
    Pop-Location
}

$package = Get-ChildItem -LiteralPath $packageDirectory -Filter 'MarkdownRenderer.Mermaid.*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $package) { throw 'The Mermaid NuGet package was not produced.' }

$requiredEntries = @(
    'LICENSE',
    'icon-192.png',
    'README.md',
    'provenance/MERMAN_PROVENANCE.json',
    'provenance/MERMAN_PATCH_LEDGER.md',
    'provenance/MERMAN_PARITY_BASELINE.md',
    'provenance/NATIVE_RUST_DEPENDENCIES.json',
    'provenance/native/Cargo.lock',
    'licenses/MERMAN-LICENSE-MIT.txt',
    'licenses/MERMAN-THIRD-PARTY-NOTICES.md',
    'build/native/MMIR_FORMAT.md',
    'build/native/abi/mmir-v1.json',
    'build/native/include/markdown_renderer_mermaid.h',
    'lib/net10.0-windows10.0.26100/MarkdownRenderer.Mermaid.dll',
    'lib/net10.0-windows10.0.26100/MarkdownRenderer.Mermaid.xml',
    'runtimes/win-x86/native/MarkdownRenderer.Mermaid.Native.dll',
    'runtimes/win-x64/native/MarkdownRenderer.Mermaid.Native.dll',
    'runtimes/win-arm64/native/MarkdownRenderer.Mermaid.Native.dll'
)
$nativeHardCap = [long] (12MB)
$nativeOptimizationTarget = [long] (10MB)
$nativeAssets = [Collections.Generic.List[object]]::new()
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entryNames = @($archive.Entries.FullName)
    foreach ($required in $requiredEntries) {
        if ($required -notin $entryNames) { throw "Mermaid package is missing '$required'." }
    }

    $nativeEntries = @($archive.Entries | Where-Object {
        $_.FullName -match '^runtimes/win-(x86|x64|arm64)/native/MarkdownRenderer\.Mermaid\.Native\.dll$'
    })
    if ($nativeEntries.Count -ne 3) { throw 'Mermaid must package exactly one native DLL for x86, x64, and ARM64.' }
    foreach ($entry in $nativeEntries) {
        if ($entry.CompressedLength -gt $nativeHardCap) {
            throw "Selected-RID asset '$($entry.FullName)' is $($entry.CompressedLength) compressed bytes; the cap is $nativeHardCap."
        }

        $nativeAssets.Add([pscustomobject][ordered]@{
            path = $entry.FullName
            uncompressedBytes = $entry.Length
            compressedBytes = $entry.CompressedLength
            underOptimizationTarget = $entry.CompressedLength -le $nativeOptimizationTarget
        })
    }

    $nuspec = $archive.GetEntry('MarkdownRenderer.Mermaid.nuspec')
    if ($null -eq $nuspec) { throw 'Mermaid package has no nuspec.' }
    $reader = [IO.StreamReader]::new($nuspec.Open())
    try { $nuspecXml = [xml] $reader.ReadToEnd() } finally { $reader.Dispose() }
    $packageVersion = [string] $nuspecXml.package.metadata.version
    $metadata = $nuspecXml.package.metadata
    if ([string] $metadata.license.type -ne 'expression' -or [string] $metadata.license.InnerText -ne 'MIT') {
        throw 'Mermaid package must declare the MIT license expression.'
    }
    if ([string] $metadata.icon -ne 'icon-192.png' -or
        [string] $metadata.projectUrl -ne 'https://github.com/JitHubApp/JitHubV2/tree/main/docs/markdown-renderer') {
        throw 'Mermaid package icon/project URL metadata is incomplete.'
    }
    if ([string] $metadata.repository.type -ne 'git' -or
        [string] $metadata.repository.url -ne 'https://github.com/JitHubApp/JitHubV2') {
        throw 'Mermaid package repository/SourceLink metadata is incomplete.'
    }
}
finally {
    $archive.Dispose()
}

$nativeCompliancePath = Join-Path $runDirectory 'native-package-compliance.json'
& (Join-Path $PSScriptRoot 'Test-NativePackageCompliance.ps1') `
    -PackageDirectory $packageDirectory `
    -OutputPath $nativeCompliancePath

$consumerDirectory = Join-Path $runDirectory 'package-consumer'
[IO.Directory]::CreateDirectory($consumerDirectory) | Out-Null
$consumerProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x86;x64;ARM64</Platforms>
    <RuntimeIdentifiers>win-x86;win-x64;win-arm64</RuntimeIdentifiers>
    <Platform Condition="'$(Platform)' == '' or '$(Platform)' == 'AnyCPU' or '$(Platform)' == 'Any CPU'">x64</Platform>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RestoreLockedMode>false</RestoreLockedMode>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
    <RestorePackagesPath>$(MSBuildProjectDirectory)\packages-cache</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MarkdownRenderer.Mermaid" Version="__VERSION__" />
  </ItemGroup>
</Project>
'@.Replace('__VERSION__', $packageVersion)
[IO.File]::WriteAllText(
    (Join-Path $consumerDirectory 'MermaidPackageConsumer.csproj'),
    $consumerProject,
    [Text.UTF8Encoding]::new($false))
$consumerProgram = @'
using MarkdownRenderer.Mermaid;

MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.Probe();
if (!runtime.IsAvailable)
{
    Console.Error.WriteLine($"Native probe failed: {runtime.Status}: {runtime.Message}");
    return 10;
}

using var renderer = new MermaidRenderer();
MermaidRenderResult result = await renderer.RenderAsync("flowchart LR\nA-->B");
if (result.Status != MermaidRenderStatus.Success || result.Scene is null ||
    result.Scene.Commands.Count == 0 || result.Scene.Semantics.Count == 0)
{
    Console.Error.WriteLine($"Native render failed: {result.Status}");
    return 11;
}

Console.WriteLine($"MMIR {result.Scene.Version.Major}.{result.Scene.Version.Minor}; commands={result.Scene.Commands.Count}; semantics={result.Scene.Semantics.Count}");
return 0;
'@
[IO.File]::WriteAllText(
    (Join-Path $consumerDirectory 'Program.cs'),
    $consumerProgram,
    [Text.UTF8Encoding]::new($false))

$consumerProjectPath = Join-Path $consumerDirectory 'MermaidPackageConsumer.csproj'
Push-Location $evidenceRepositoryRoot
try {
    Invoke-DotNet @(
        'restore', $consumerProjectPath, '-p:Configuration=Release', '-p:RestoreLockedMode=false',
        ('-p:RestoreAdditionalProjectSources=' + $packageDirectory), '--no-cache', '--disable-parallel'
    )
}
finally {
    Pop-Location
}

$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$matrix = @(
    [pscustomobject]@{ Platform = 'x86'; Rid = 'win-x86'; Machine = 'x86' },
    [pscustomobject]@{ Platform = 'x64'; Rid = 'win-x64'; Machine = 'x64' },
    [pscustomobject]@{ Platform = 'ARM64'; Rid = 'win-arm64'; Machine = 'arm64' }
)
$deployments = [Collections.Generic.List[object]]::new()
foreach ($target in $matrix) {
    foreach ($mode in @('nativeaot', 'trimmed-single-file')) {
        $publishDirectory = Join-Path $runDirectory ($mode + '-' + $target.Rid)
        $arguments = @(
            'publish', $consumerProjectPath, '-c', 'Release', '-r', $target.Rid,
            ('-p:Platform=' + $target.Platform), '--self-contained', 'true', '--no-restore',
            '--disable-build-servers', '-p:UseSharedCompilation=false', '-nr:false', '-o', $publishDirectory
        )
        if ($mode -eq 'nativeaot') {
            $arguments += '-p:PublishAot=true'
        }
        else {
            $arguments += @(
                '-p:PublishAot=false', '-p:PublishTrimmed=true', '-p:TrimMode=full',
                '-p:PublishSingleFile=true'
            )
        }

        Push-Location $evidenceRepositoryRoot
        try { Invoke-DotNet $arguments } finally { Pop-Location }

        $executable = Join-Path $publishDirectory 'MermaidPackageConsumer.exe'
        $nativeLibrary = Join-Path $publishDirectory 'MarkdownRenderer.Mermaid.Native.dll'
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "$mode $($target.Rid) did not produce a consumer executable."
        }
        if (-not (Test-Path -LiteralPath $nativeLibrary -PathType Leaf)) {
            throw "$mode $($target.Rid) did not select a native Mermaid runtime asset."
        }

        $executableMachine = Get-PeMachine $executable
        $nativeMachine = Get-PeMachine $nativeLibrary
        if ($executableMachine -ne $target.Machine -or $nativeMachine -ne $target.Machine) {
            throw "$mode $($target.Rid) produced executable/native machines $executableMachine/$nativeMachine; expected $($target.Machine)."
        }

        $execution = Invoke-CompatibleExecutable -Path $executable -Rid $target.Rid -HostArchitecture $hostArchitecture
        $deployments.Add([pscustomobject][ordered]@{
            mode = $mode
            rid = $target.Rid
            executableMachine = $executableMachine
            nativeMachine = $nativeMachine
            executableBytes = (Get-Item -LiteralPath $executable).Length
            nativeBytes = (Get-Item -LiteralPath $nativeLibrary).Length
            executed = $execution.executed
            exitCode = $execution.exitCode
            output = $execution.output
            skipReason = $execution.reason
        })
    }
}

# Project-reference consumers do not necessarily pass Platform. Prove that the
# deployment RID alone selects the matching native payload even though the
# library project has an x64 solution default.
$projectReferenceDirectory = Join-Path $runDirectory 'project-reference-consumer'
[IO.Directory]::CreateDirectory($projectReferenceDirectory) | Out-Null
$mermaidProjectReference = [Security.SecurityElement]::Escape(
    (Join-Path $evidenceMarkdownRoot 'MarkdownRenderer.Mermaid\MarkdownRenderer.Mermaid.csproj'))
$projectReferenceProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <RuntimeIdentifiers>win-x86;win-x64;win-arm64</RuntimeIdentifiers>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="__MERMAID_PROJECT__" />
  </ItemGroup>
</Project>
'@.Replace('__MERMAID_PROJECT__', $mermaidProjectReference)
[IO.File]::WriteAllText(
    (Join-Path $projectReferenceDirectory 'MermaidProjectReferenceConsumer.csproj'),
    $projectReferenceProject,
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(
    (Join-Path $projectReferenceDirectory 'Program.cs'),
    "Console.WriteLine(`"Mermaid project-reference RID selection probe.`");`n",
    [Text.UTF8Encoding]::new($false))
$projectReferenceProjectPath = Join-Path $projectReferenceDirectory 'MermaidProjectReferenceConsumer.csproj'
$projectReferenceDeployments = [Collections.Generic.List[object]]::new()
foreach ($target in $matrix) {
    $publishDirectory = Join-Path $runDirectory ('project-reference-' + $target.Rid)
    Push-Location $evidenceRepositoryRoot
    try {
        Invoke-DotNet @(
            'publish', $projectReferenceProjectPath, '-c', 'Release', '-r', $target.Rid,
            '--self-contained', 'false', '--disable-build-servers', '-p:UseSharedCompilation=false',
            '-nr:false', '-o', $publishDirectory
        )
    }
    finally {
        Pop-Location
    }
    $nativeLibrary = Join-Path $publishDirectory 'MarkdownRenderer.Mermaid.Native.dll'
    if (-not (Test-Path -LiteralPath $nativeLibrary -PathType Leaf)) {
        throw "Project-reference $($target.Rid) publish did not select a native Mermaid runtime asset."
    }
    $nativeMachine = Get-PeMachine $nativeLibrary
    if ($nativeMachine -ne $target.Machine) {
        throw "Project-reference $($target.Rid) selected $nativeMachine native code; expected $($target.Machine)."
    }
    $projectReferenceDeployments.Add([pscustomobject][ordered]@{
        rid = $target.Rid
        platformSpecified = $false
        nativeMachine = $nativeMachine
        nativeBytes = (Get-Item -LiteralPath $nativeLibrary).Length
    })
}

$trx = [xml] (Get-Content -LiteralPath (Join-Path $testResultsDirectory 'mermaid-contracts.trx') -Raw)
$counters = $trx.TestRun.ResultSummary.Counters
$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    hostArchitecture = $hostArchitecture
    contractTests = [pscustomobject][ordered]@{
        total = [int] $counters.total
        executed = [int] $counters.executed
        passed = [int] $counters.passed
        failed = [int] $counters.failed
    }
    package = [pscustomobject][ordered]@{
        id = 'MarkdownRenderer.Mermaid'
        version = $packageVersion
        path = $package.FullName
        compressedBytes = $package.Length
        sha256 = Get-Sha256 $package.FullName
        selectedRidNativeAssets = @($nativeAssets)
        nativeHardCapBytes = $nativeHardCap
        nativeOptimizationTargetBytes = $nativeOptimizationTarget
    }
    nativeCompliance = $nativeCompliancePath
    deployments = @($deployments)
    projectReferenceRidSelection = @($projectReferenceDeployments)
}
$reportPath = Join-Path $runDirectory 'mermaid-release-evidence.json'
[IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 20) + "`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "Mermaid release gates passed. Evidence: '$reportPath'."
