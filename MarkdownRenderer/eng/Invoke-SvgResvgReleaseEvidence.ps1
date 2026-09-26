[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('Preview', 'Release')]
    [string] $Mode = 'Preview',

    [Parameter(Mandatory = $false)]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\svg-resvg-release-evidence'),

    [string] $EdgePerformanceEvidence,

    [string] $DeviceMatrixEvidence,

    [string] $FaultInjectionEvidence,

    [string] $StoreDeploymentEvidence,

    [string] $LiveAuditEvidence
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$markdownRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $markdownRoot '..')).Path
$providerRoot = Join-Path $markdownRoot 'MarkdownRenderer.Svg.Resvg'
$runName = 'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff', [Globalization.CultureInfo]::InvariantCulture)
$runDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory $runName))
$testResultsDirectory = Join-Path $runDirectory 'tests'
$pixelArtifactsDirectory = Join-Path $runDirectory 'pixels'
$buildArtifactsDirectory = Join-Path $runDirectory 'build-artifacts'
$candidatePackages = Join-Path $runDirectory 'packages-candidate'
$referencePackages = Join-Path $runDirectory 'packages-reference'
$evidenceLockPath = Join-Path $runDirectory 'evidence.packages.lock.json'
$requireReleaseEvidence = $Mode -eq 'Release'
foreach ($directory in @(
    $runDirectory,
    $testResultsDirectory,
    $pixelArtifactsDirectory,
    $buildArtifactsDirectory,
    $candidatePackages,
    $referencePackages)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}

# WindowsAppSDK self-contained targets invoke mt.exe by name. Prefer the pinned
# 26100 SDK host tool when it exists but was not added to PATH by the shell.
$sdkTools = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
if (Test-Path -LiteralPath (Join-Path $sdkTools 'mt.exe') -PathType Leaf) {
    $env:PATH = $sdkTools + [IO.Path]::PathSeparator + $env:PATH
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $performsRestore = $Arguments[0] -eq 'restore' -or
        ($Arguments[0] -in @('build', 'test', 'pack', 'publish') -and $Arguments -notcontains '--no-restore')
    if ($performsRestore) {
        $Arguments += @(
            '-p:RestorePackagesWithLockFile=false',
            '-p:RestoreLockedMode=false',
            "-p:NuGetLockFilePath=$evidenceLockPath"
        )
    }
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StreamSha256([IO.Stream] $Stream) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($hash.ComputeHash($Stream)).ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Get-PeMachine([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "'$Path' is not an MZ executable."
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or
        $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "'$Path' has no valid PE signature."
    }
    switch ([BitConverter]::ToUInt16($bytes, $peOffset + 4)) {
        332 { return 'x86' }
        34404 { return 'x64' }
        43620 { return 'arm64' }
        default { return 'unknown' }
    }
}

function Get-TrxCounters([string] $Path, [int] $MinimumExecuted) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing test evidence '$Path'." }
    $trx = [xml] (Get-Content -LiteralPath $Path -Raw)
    $counters = $trx.TestRun.ResultSummary.Counters
    $executed = [int] $counters.executed
    if ([int] $counters.failed -ne 0 -or $executed -ne [int] $counters.passed -or $executed -lt $MinimumExecuted) {
        throw "Test evidence '$Path' is incomplete or contains failures (executed=$executed, passed=$($counters.passed), failed=$($counters.failed))."
    }
    return [pscustomobject][ordered]@{
        total = [int] $counters.total
        executed = $executed
        passed = [int] $counters.passed
        failed = [int] $counters.failed
        path = $Path
    }
}

function Import-GateEvidence([string] $Path, [string] $ExpectedGate) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        if ($requireReleaseEvidence) {
            throw "Release mode requires -$ExpectedGate evidence."
        }
        return [pscustomobject][ordered]@{
            gate = $ExpectedGate
            status = 'not-provided-preview'
        }
    }
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $value = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json -Depth 64
    if ([int] $value.schemaVersion -ne 1 -or [string] $value.gate -cne $ExpectedGate -or
        [string] $value.status -cne 'pass') {
        throw "Evidence '$resolved' is not passing schema-v1 '$ExpectedGate' evidence."
    }
    $value | Add-Member -NotePropertyName evidencePath -NotePropertyValue $resolved -Force
    $value | Add-Member -NotePropertyName evidenceSha256 -NotePropertyValue (Get-Sha256 $resolved) -Force
    return $value
}

function Assert-ContainsAll([object[]] $Actual, [object[]] $Required, [string] $Label) {
    foreach ($item in $Required) {
        if ($item -notin $Actual) { throw "$Label is missing required value '$item'." }
    }
}

function Test-IsProvided([object] $Evidence) {
    return [string] $Evidence.status -ne 'not-provided-preview'
}

function Copy-DeploymentWithoutWorker([string] $Source, [string] $Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.Name -ieq 'MarkdownRenderer.Svg.Resvg.Worker.exe') { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse
    }
}

function Invoke-CompatibleExecutable {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Rid,
        [Parameter(Mandatory)][string] $HostArchitecture,
        [string[]] $Arguments = @()
    )

    $compatible = $Rid -eq "win-$HostArchitecture" -or
        ($HostArchitecture -eq 'x64' -and $Rid -eq 'win-x86')
    if (-not $compatible) {
        return [pscustomobject][ordered]@{
            executed = $false
            exitCode = $null
            output = $null
            reason = "Cross-published $Rid output cannot run on the $HostArchitecture evidence host."
        }
    }
    $output = @(& $Path @Arguments 2>&1)
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

$provenancePath = Join-Path $providerRoot 'RESVG_PROVENANCE.json'
$sbomPath = Join-Path $providerRoot 'NATIVE_RUST_DEPENDENCIES.json'
$cargoLockPath = Join-Path $providerRoot 'native\Cargo.lock'
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -Depth 32
if ([string] $provenance.version -cne '0.48.1' -or
    [string] $provenance.commit -cne '68b14c4c3bccdb60344c777406486b54c36ec1a4' -or
    [string] $provenance.selectedLicense -cne 'MIT') {
    throw 'The resvg version, source commit, or selected license differs from the approved release pin.'
}
if (-not (Test-Path -LiteralPath $cargoLockPath -PathType Leaf)) { throw 'The pinned resvg Cargo.lock is missing.' }
& (Join-Path $providerRoot 'eng\Verify-ResvgSupplyChain.ps1') | Write-Host

$signatureEvidencePath = Join-Path $runDirectory 'worker-signatures.json'
$signatureArguments = @{
    OutputPath = $signatureEvidencePath
}
if ($requireReleaseEvidence) { $signatureArguments.RequireValid = $true }
& (Join-Path $providerRoot 'eng\Test-ResvgWorkerSignatures.ps1') @signatureArguments | Write-Host

# Performance/device/store evidence is produced by pinned-machine and signed
# deployment lanes. This orchestrator validates it but does not pretend a
# hosted or developer machine exercised hardware it did not actually have.
$edgePerformance = Import-GateEvidence $EdgePerformanceEvidence 'edge-relative-performance-efficiency'
if (Test-IsProvided $edgePerformance) {
    if (-not [bool] $edgePerformance.sameMachine -or [string] $edgePerformance.browser -cne 'Microsoft Edge') {
        throw 'Edge performance evidence must compare candidate and Edge on the same machine.'
    }
    foreach ($metric in @(
        @{ Name = 'warmUncachedLatencyRatio'; Max = 1.10 },
        @{ Name = 'edgeThroughputToCandidateRatio'; Max = 1.10 },
        @{ Name = 'cpuRatio'; Max = 1.10 },
        @{ Name = 'incrementalMemoryRatio'; Max = 1.10 },
        @{ Name = 'warmIconP95Ms'; Max = 8.33 },
        @{ Name = 'katexClassP95Ms'; Max = 16.67 },
        @{ Name = 'otherComplexP95Ms'; Max = 100.0 },
        @{ Name = 'emptyFontCacheFirstTextMs'; Max = 750.0 },
        @{ Name = 'twelveVisibleCompletionMs'; Max = 500.0 },
        @{ Name = 'uiPublicationMaxMs'; Max = 2.0 },
        @{ Name = 'uiCallbackP95Ms'; Max = 8.329999 },
        @{ Name = 'uiCallbackMaxMs'; Max = 16.669999 },
        @{ Name = 'retainedGrowthBytes'; Max = 8MB },
        @{ Name = 'handleGrowth'; Max = 4 })) {
        $property = $edgePerformance.metrics.PSObject.Properties[$metric.Name]
        if ($null -eq $property -or [double] $property.Value -gt [double] $metric.Max) {
            throw "Edge performance metric '$($metric.Name)' is missing or exceeds $($metric.Max)."
        }
    }
    if ([int] $edgePerformance.metrics.cacheHitWorkerRequests -ne 0 -or
        [double] $edgePerformance.metrics.cacheHitMs -gt 16.67 -or
        -not [bool] $edgePerformance.inputRemainedResponsive -or
        -not [bool] $edgePerformance.perSampleP95GatePassed -or
        -not [bool] $edgePerformance.noCacheBudgetOverrun -or
        -not [bool] $edgePerformance.memoryPressureTrimValidated) {
        throw 'Edge performance evidence fails the cache-hit or input-responsiveness contract.'
    }
}

$deviceMatrix = Import-GateEvidence $DeviceMatrixEvidence 'device-theme-dpi-matrix'
if (Test-IsProvided $deviceMatrix) {
    Assert-ContainsAll @($deviceMatrix.simulatedScales) @(1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 3.0, 4.0) 'Simulated scale matrix'
    Assert-ContainsAll @($deviceMatrix.physicallyInspectedScales) @(1.0, 1.5, 2.0) 'Physical scale matrix'
    Assert-ContainsAll @($deviceMatrix.themes) @('Light', 'Dark', 'HighContrastBuiltIn', 'HighContrastCustom') 'Theme matrix'
    Assert-ContainsAll @($deviceMatrix.graphicsPaths) @('HardwareD3D', 'WARP') 'Graphics-path matrix'
    Assert-ContainsAll @($deviceMatrix.architectures) @('x86', 'x64', 'arm64') 'Architecture matrix'
    foreach ($requiredBoolean in @('mixedDpiMonitorMove', 'textScaling', 'hdrComposition')) {
        $property = $deviceMatrix.PSObject.Properties[$requiredBoolean]
        if ($null -eq $property -or -not [bool] $property.Value) {
            throw "Device matrix lacks passing '$requiredBoolean' evidence."
        }
    }
}

$faultInjection = Import-GateEvidence $FaultInjectionEvidence 'worker-fault-injection'
if (Test-IsProvided $faultInjection) {
    Assert-ContainsAll @($faultInjection.scenarios) @(
        'access-violation', 'fail-fast', 'hang', 'partial-response', 'oversized-length',
        'wrong-id', 'stale-cancellation-response', 'pipe-loss', 'oom', 'device-loss') 'Fault scenarios'
    if (-not [bool] $faultInjection.appRemainedResponsive -or
        -not [bool] $faultInjection.typedAccessibleFallbacks -or
        -not [bool] $faultInjection.unrelatedContentRecovered -or
        [int] $faultInjection.orphanProcessCount -ne 0 -or
        [long] $faultInjection.retainedGrowthBytes -gt 8MB -or
        [int] $faultInjection.handleGrowth -gt 4) {
        throw 'Fault-injection evidence violates responsiveness, process, memory, or handle gates.'
    }
    if ([long] $faultInjection.jobCommitBytes.x86 -gt 192MB -or
        [long] $faultInjection.jobCommitBytes.x64 -gt 384MB -or
        [long] $faultInjection.jobCommitBytes.arm64 -gt 384MB) {
        throw 'Fault-injection evidence exceeds a worker-pool Job commit ceiling.'
    }
}

$storeDeployment = Import-GateEvidence $StoreDeploymentEvidence 'store-child-process-deployment'
if (Test-IsProvided $storeDeployment) {
    Assert-ContainsAll @($storeDeployment.architectures) @('x86', 'x64', 'arm64') 'Store deployment architecture matrix'
    foreach ($requiredBoolean in @(
        'nativeAot', 'trimming', 'packaged', 'unpackaged', 'msixSigned',
        'storePolicyValidated', 'childProcessLaunch', 'killOnCloseValidated')) {
        $property = $storeDeployment.PSObject.Properties[$requiredBoolean]
        if ($null -eq $property -or -not [bool] $property.Value) {
            throw "Store deployment evidence lacks passing '$requiredBoolean' evidence."
        }
    }
}

$liveAudits = Import-GateEvidence $LiveAuditEvidence 'representative-live-svg-audits'
if (Test-IsProvided $liveAudits) {
    Assert-ContainsAll @($liveAudits.samples) @('KaTeX', 'Flutter', 'open-ui', 'ShieldsBadge', 'Mermaid') 'Live audit sample matrix'
    if (-not [bool] $liveAudits.externalResourcesBlocked -or -not [bool] $liveAudits.accessibilityValidated) {
        throw 'Live SVG audit evidence lacks security or accessibility validation.'
    }
}

$testsProject = Join-Path $markdownRoot 'MarkdownRenderer.Svg.Resvg.Tests\MarkdownRenderer.Svg.Resvg.Tests.csproj'
$pixelTestsProject = Join-Path $markdownRoot 'MarkdownRenderer.PixelTests\MarkdownRenderer.PixelTests.csproj'
$packageProjects = @(
    (Join-Path $markdownRoot 'MarkdownRenderer.Core\MarkdownRenderer.Core.csproj'),
    (Join-Path $markdownRoot 'MarkdownRenderer\MarkdownRenderer.csproj'),
    (Join-Path $markdownRoot 'MarkdownRenderer.Package\MarkdownRenderer.Package.csproj'),
    (Join-Path $providerRoot 'MarkdownRenderer.Svg.Resvg.csproj')
)

Push-Location $repositoryRoot
try {
    foreach ($testProject in @($testsProject, $pixelTestsProject)) {
        Invoke-DotNet @(
            'restore', $testProject, '-p:Configuration=Release', '-p:Platform=x64',
            "-p:ArtifactsPath=$buildArtifactsDirectory", '--disable-parallel'
        )
    }
    Invoke-DotNet @(
        'test', $testsProject, '-c', 'Release', '-p:Platform=x64', '--no-restore',
        '-p:RestorePackagesWithLockFile=false', "-p:ArtifactsPath=$buildArtifactsDirectory",
        '--disable-build-servers', '-p:UseSharedCompilation=false', '-nr:false',
        '--logger', 'trx;LogFileName=svg-resvg-contracts.trx',
        '--results-directory', $testResultsDirectory
    )

    $previousArtifactRoot = [Environment]::GetEnvironmentVariable('MARKDOWN_RENDERER_SVG_ARTIFACT_ROOT', [EnvironmentVariableTarget]::Process)
    $previousRequireEdge = [Environment]::GetEnvironmentVariable('MARKDOWN_RENDERER_REQUIRE_EDGE_ORACLE', [EnvironmentVariableTarget]::Process)
    try {
        [Environment]::SetEnvironmentVariable('MARKDOWN_RENDERER_SVG_ARTIFACT_ROOT', $pixelArtifactsDirectory, [EnvironmentVariableTarget]::Process)
        if ($requireReleaseEvidence) {
            [Environment]::SetEnvironmentVariable('MARKDOWN_RENDERER_REQUIRE_EDGE_ORACLE', '1', [EnvironmentVariableTarget]::Process)
        }
        Invoke-DotNet @(
            'test', $pixelTestsProject, '-c', 'Release', '-p:Platform=x64', '--no-restore',
            '-p:RestorePackagesWithLockFile=false', "-p:ArtifactsPath=$buildArtifactsDirectory",
            '--disable-build-servers', '-p:UseSharedCompilation=false', '-nr:false',
            '--logger', 'trx;LogFileName=svg-edge-pixels.trx',
            '--results-directory', $testResultsDirectory
        )
    }
    finally {
        [Environment]::SetEnvironmentVariable('MARKDOWN_RENDERER_SVG_ARTIFACT_ROOT', $previousArtifactRoot, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable('MARKDOWN_RENDERER_REQUIRE_EDGE_ORACLE', $previousRequireEdge, [EnvironmentVariableTarget]::Process)
    }

    foreach ($project in $packageProjects) {
        Invoke-DotNet @(
            'restore', $project, '-p:Configuration=Release', '-p:Platform=x64',
            "-p:ArtifactsPath=$buildArtifactsDirectory", '--disable-parallel'
        )
        foreach ($directory in @($candidatePackages, $referencePackages)) {
            $packArguments = @(
                'pack', $project, '-c', 'Release', '-p:Platform=x64', '--no-restore',
                '-p:RestorePackagesWithLockFile=false', "-p:ArtifactsPath=$buildArtifactsDirectory",
                '--disable-build-servers', '-p:UseSharedCompilation=false', '-nr:false', '-o', $directory
            )
            if ($project -eq $packageProjects[-1] -and $requireReleaseEvidence) {
                $packArguments += '-p:RequireResvgWorkerSignatures=true'
            }
            Invoke-DotNet $packArguments
        }
    }
}
finally {
    Pop-Location
}

$contractEvidence = Get-TrxCounters (Join-Path $testResultsDirectory 'svg-resvg-contracts.trx') 8
$pixelEvidence = Get-TrxCounters (Join-Path $testResultsDirectory 'svg-edge-pixels.trx') 1

& (Join-Path $PSScriptRoot 'Normalize-NuGetPackageArchives.ps1') -PackageDirectory $candidatePackages
& (Join-Path $PSScriptRoot 'Normalize-NuGetPackageArchives.ps1') -PackageDirectory $referencePackages
$referenceManifest = Join-Path $runDirectory 'package-reproducibility-reference.json'
& (Join-Path $PSScriptRoot 'New-PackageReproducibilityManifest.ps1') `
    -PackageDirectory $referencePackages `
    -OutputPath $referenceManifest
$complianceDirectory = Join-Path $runDirectory 'package-compliance'
& (Join-Path $PSScriptRoot 'Invoke-PackageCompliance.ps1') `
    -PackageDirectory $candidatePackages `
    -OutputDirectory $complianceDirectory `
    -ReferenceManifest $referenceManifest `
    -FailOnUnknownLicense `
    -SkipSizeGates

$package = Get-ChildItem -LiteralPath $candidatePackages -Filter 'MarkdownRenderer.Svg.Resvg.*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Select-Object -First 1
if ($null -eq $package) { throw 'The MarkdownRenderer.Svg.Resvg package was not produced.' }
$packageCapBytes = [long] (5MB)
$selectedDeploymentCapBytes = [long] (3.5MB)
if ($package.Length -gt $packageCapBytes) {
    throw "The all-RID resvg package is $($package.Length) bytes; the compressed cap is $packageCapBytes bytes."
}

$requiredEntries = @(
    'LICENSE',
    'README.md',
    'THIRD-PARTY-NOTICES.md',
    'provenance/RESVG_PROVENANCE.json',
    'provenance/NATIVE_RUST_DEPENDENCIES.json',
    'provenance/native/Cargo.lock',
    'licenses/RESVG-LICENSE-MIT.txt',
    'licenses/RUST-DEPENDENCY-LICENSES.txt',
    'build/native/PROTOCOL.md',
    'lib/net10.0-windows10.0.26100/MarkdownRenderer.Svg.Resvg.dll',
    'lib/net10.0-windows10.0.26100/MarkdownRenderer.Svg.Resvg.xml',
    'runtimes/win-x86/native/MarkdownRenderer.Svg.Resvg.Worker.exe',
    'runtimes/win-x64/native/MarkdownRenderer.Svg.Resvg.Worker.exe',
    'runtimes/win-arm64/native/MarkdownRenderer.Svg.Resvg.Worker.exe'
)
$expectedMachines = @{ 'win-x86' = 'x86'; 'win-x64' = 'x64'; 'win-arm64' = 'arm64' }
$nativeAssets = [Collections.Generic.List[object]]::new()
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entryNames = @($archive.Entries.FullName)
    foreach ($required in $requiredEntries) {
        if ($required -notin $entryNames) { throw "The resvg package is missing '$required'." }
    }
    $nativeEntries = @($archive.Entries | Where-Object {
        $_.FullName -match '^runtimes/win-(x86|x64|arm64)/native/MarkdownRenderer\.Svg\.Resvg\.Worker\.exe$'
    })
    if ($nativeEntries.Count -ne 3) { throw 'The resvg package must contain exactly one worker for x86, x64, and ARM64.' }
    foreach ($entry in $nativeEntries) {
        if ($entry.Length -gt $selectedDeploymentCapBytes) {
            throw "Selected deployment worker '$($entry.FullName)' is $($entry.Length) bytes; the cap is $selectedDeploymentCapBytes bytes."
        }
        $rid = ([regex]::Match($entry.FullName, '^runtimes/(?<rid>win-(x86|x64|arm64))/')).Groups['rid'].Value
        $stream = $entry.Open()
        $temporaryWorker = Join-Path $runDirectory ("package-$rid-worker.exe")
        try {
            $file = [IO.File]::Create($temporaryWorker)
            try { $stream.CopyTo($file) } finally { $file.Dispose() }
        }
        finally { $stream.Dispose() }
        $machine = Get-PeMachine $temporaryWorker
        if ($machine -ne $expectedMachines[$rid]) {
            throw "Packaged worker '$($entry.FullName)' is $machine, expected $($expectedMachines[$rid])."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $temporaryWorker
        if ($requireReleaseEvidence -and $signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
            throw "Packaged release worker '$($entry.FullName)' has invalid Authenticode status $($signature.Status)."
        }
        $nativeAssets.Add([pscustomobject][ordered]@{
            path = $entry.FullName
            machine = $machine
            uncompressedBytes = $entry.Length
            compressedBytes = $entry.CompressedLength
            sha256 = Get-Sha256 $temporaryWorker
            authenticodeStatus = $signature.Status.ToString()
        })
    }

    $nuspecEntry = $archive.GetEntry('MarkdownRenderer.Svg.Resvg.nuspec')
    if ($null -eq $nuspecEntry) { throw 'The resvg package has no nuspec.' }
    $reader = [IO.StreamReader]::new($nuspecEntry.Open())
    try { $nuspec = [xml] $reader.ReadToEnd() } finally { $reader.Dispose() }
    $packageVersion = [string] $nuspec.package.metadata.version
    if ([string] $nuspec.package.metadata.license.type -ne 'expression' -or
        [string] $nuspec.package.metadata.license.InnerText -ne 'MIT') {
        throw 'The resvg package must declare the repository MIT license expression.'
    }
}
finally {
    $archive.Dispose()
}

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
    <Platform Condition="'$(Platform)' == '' or '$(Platform)' == 'AnyCPU'">x64</Platform>
    <InvariantGlobalization>true</InvariantGlobalization>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <ILLinkTreatWarningsAsErrors>false</ILLinkTreatWarningsAsErrors>
    <UseWinUI>false</UseWinUI>
    <WindowsAppSdkSelfContained>true</WindowsAppSdkSelfContained>
    <EnableMsixTooling>true</EnableMsixTooling>
    <PublishAot>true</PublishAot>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
    <RestorePackagesPath>$(MSBuildProjectDirectory)\..\consumer-packages-cache</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MarkdownRenderer.Svg.Resvg" Version="__VERSION__" />
  </ItemGroup>
</Project>
'@.Replace('__VERSION__', $packageVersion)
[IO.File]::WriteAllText(
    (Join-Path $consumerDirectory 'SvgResvgPackageConsumer.csproj'),
    $consumerProject,
    [Text.UTF8Encoding]::new($false))
[IO.File]::Copy((Join-Path $markdownRoot 'MarkdownRenderer.Svg.Resvg.AotSmoke\Program.cs'), (Join-Path $consumerDirectory 'Program.cs'))
$consumerProjectPath = Join-Path $consumerDirectory 'SvgResvgPackageConsumer.csproj'

Push-Location $repositoryRoot
try {
    Invoke-DotNet @(
        'restore', $consumerProjectPath, '-p:Configuration=Release',
        "-p:RestoreAdditionalProjectSources=$candidatePackages", '--no-cache', '--disable-parallel'
    )
}
finally { Pop-Location }

$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$matrix = @(
    [pscustomobject]@{ Platform = 'x86'; Rid = 'win-x86'; Machine = 'x86' },
    [pscustomobject]@{ Platform = 'x64'; Rid = 'win-x64'; Machine = 'x64' },
    [pscustomobject]@{ Platform = 'ARM64'; Rid = 'win-arm64'; Machine = 'arm64' }
)
$deployments = [Collections.Generic.List[object]]::new()
foreach ($target in $matrix) {
    foreach ($deploymentMode in @('nativeaot', 'trimmed')) {
        $publishDirectory = Join-Path $runDirectory "$deploymentMode-$($target.Rid)"
        $arguments = @(
            'publish', $consumerProjectPath, '-c', 'Release', '-r', $target.Rid,
            "-p:Platform=$($target.Platform)", '--self-contained', 'true', '--no-restore',
            '--disable-build-servers', '-p:UseSharedCompilation=false', '-nr:false', '-o', $publishDirectory,
            '-p:PublishTrimmed=true', '-p:TrimMode=full'
        )
        if ($deploymentMode -eq 'nativeaot') {
            $arguments += '-p:PublishAot=true'
        }
        else {
            $arguments += @('-p:PublishAot=false', '-p:PublishSingleFile=false')
        }
        Push-Location $repositoryRoot
        try { Invoke-DotNet $arguments } finally { Pop-Location }

        $executable = Join-Path $publishDirectory 'SvgResvgPackageConsumer.exe'
        $worker = Join-Path $publishDirectory 'MarkdownRenderer.Svg.Resvg.Worker.exe'
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
            -not (Test-Path -LiteralPath $worker -PathType Leaf)) {
            throw "$deploymentMode $($target.Rid) did not publish both the consumer and its adjacent worker."
        }
        if ((Get-PeMachine $executable) -ne $target.Machine -or (Get-PeMachine $worker) -ne $target.Machine) {
            throw "$deploymentMode $($target.Rid) selected an incorrect PE architecture."
        }
        $extraWorkers = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -Filter 'MarkdownRenderer.Svg.Resvg.Worker.exe' -File)
        if ($extraWorkers.Count -ne 1) { throw "$deploymentMode $($target.Rid) published $($extraWorkers.Count) workers; expected exactly one." }
        $execution = Invoke-CompatibleExecutable -Path $executable -Rid $target.Rid -HostArchitecture $hostArchitecture
        $deployments.Add([pscustomobject][ordered]@{
            mode = $deploymentMode
            rid = $target.Rid
            executableMachine = Get-PeMachine $executable
            workerMachine = Get-PeMachine $worker
            workerBytes = (Get-Item -LiteralPath $worker).Length
            workerSha256 = Get-Sha256 $worker
            executed = $execution.executed
            exitCode = $execution.exitCode
            output = $execution.output
            skipReason = $execution.reason
        })
    }
}

$hostTarget = @($matrix | Where-Object Rid -eq "win-$hostArchitecture") | Select-Object -First 1
if ($null -eq $hostTarget) { throw "Unsupported evidence-host architecture '$hostArchitecture'." }
$hostDeployment = Join-Path $runDirectory "nativeaot-$($hostTarget.Rid)"
$hostExecutableName = 'SvgResvgPackageConsumer.exe'
$fallbacks = [Collections.Generic.List[object]]::new()

$missingDeployment = Join-Path $runDirectory 'fallback-missing-worker'
Copy-DeploymentWithoutWorker -Source $hostDeployment -Destination $missingDeployment
$missingExecution = Invoke-CompatibleExecutable `
    -Path (Join-Path $missingDeployment $hostExecutableName) `
    -Rid $hostTarget.Rid -HostArchitecture $hostArchitecture -Arguments @('--expect-unavailable')
$fallbacks.Add([pscustomobject][ordered]@{
    scenario = 'missing-worker'
    executed = $missingExecution.executed
    exitCode = $missingExecution.exitCode
    output = $missingExecution.output
})

$wrongTarget = @($matrix | Where-Object Rid -ne $hostTarget.Rid) | Select-Object -First 1
$wrongDeployment = Join-Path $runDirectory 'fallback-wrong-worker-architecture'
Copy-DeploymentWithoutWorker -Source $hostDeployment -Destination $wrongDeployment
$wrongWorker = Join-Path $providerRoot "runtimes\$($wrongTarget.Rid)\native\MarkdownRenderer.Svg.Resvg.Worker.exe"
[IO.File]::Copy($wrongWorker, (Join-Path $wrongDeployment 'MarkdownRenderer.Svg.Resvg.Worker.exe'))
$wrongExecution = Invoke-CompatibleExecutable `
    -Path (Join-Path $wrongDeployment $hostExecutableName) `
    -Rid $hostTarget.Rid -HostArchitecture $hostArchitecture -Arguments @('--expect-unavailable')
$fallbacks.Add([pscustomobject][ordered]@{
    scenario = 'wrong-worker-architecture'
    injectedMachine = Get-PeMachine $wrongWorker
    executed = $wrongExecution.executed
    exitCode = $wrongExecution.exitCode
    output = $wrongExecution.output
})

$signatureEvidence = Get-Content -LiteralPath $signatureEvidencePath -Raw | ConvertFrom-Json -Depth 16
$nativeSbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json -Depth 64
$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    mode = $Mode
    generatedUtc = [DateTime]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    hostArchitecture = $hostArchitecture
    source = [pscustomobject][ordered]@{
        resvgVersion = [string] $provenance.version
        resvgCommit = [string] $provenance.commit
        selectedLicense = [string] $provenance.selectedLicense
        cargoLockSha256 = Get-Sha256 $cargoLockPath
        nativeSbomSha256 = Get-Sha256 $sbomPath
        nativePackageCount = [int] $nativeSbom.packageCount
    }
    contractAndFaultTests = $contractEvidence
    edgePixelTests = $pixelEvidence
    edgeOracleRequired = $requireReleaseEvidence
    mandatoryReleaseEvidence = [pscustomobject][ordered]@{
        edgePerformanceAndEfficiency = $edgePerformance
        deviceThemeDpiMatrix = $deviceMatrix
        faultInjection = $faultInjection
        storeChildProcessDeployment = $storeDeployment
        representativeLiveAudits = $liveAudits
    }
    signatures = $signatureEvidence
    package = [pscustomobject][ordered]@{
        id = 'MarkdownRenderer.Svg.Resvg'
        version = $packageVersion
        path = $package.FullName
        compressedBytes = $package.Length
        compressedCapBytes = $packageCapBytes
        sha256 = Get-Sha256 $package.FullName
        selectedDeploymentCapBytes = $selectedDeploymentCapBytes
        workers = @($nativeAssets)
    }
    compliance = [pscustomobject][ordered]@{
        directory = $complianceDirectory
        nativeAbi = Join-Path $complianceDirectory 'native-package-compliance.json'
        reproducibility = Join-Path $complianceDirectory 'package-reproducibility.json'
        referenceReproducibility = $referenceManifest
    }
    deployments = @($deployments)
    failureFallbacks = @($fallbacks)
    releaseReady = $requireReleaseEvidence
}
$reportPath = Join-Path $runDirectory 'svg-resvg-release-evidence.json'
[IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 32) + "`n",
    [Text.UTF8Encoding]::new($false))

if ($requireReleaseEvidence) {
    Write-Host "resvg release evidence completed with every mandatory supplied gate validated. Evidence: '$reportPath'."
}
else {
    Write-Host "resvg preview evidence completed; it does not assert production release readiness. Evidence: '$reportPath'."
}
