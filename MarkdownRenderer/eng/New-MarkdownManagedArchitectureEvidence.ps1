[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputPath,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$rendererRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$targetFramework = 'net10.0-windows10.0.26100.0'
$rendererProject = Join-Path $rendererRoot 'MarkdownRenderer\MarkdownRenderer.csproj'
$harnessProject = Join-Path $rendererRoot 'MarkdownRenderer.PerformanceHarness\MarkdownRenderer.PerformanceHarness.csproj'
$sampleProject = Join-Path $rendererRoot 'MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj'

function Invoke-CheckedBuild {
    param(
        [Parameter(Mandatory)][string] $Project,
        [Parameter(Mandatory)][string] $Platform
    )

    & dotnet build $Project -c $Configuration "-p:Platform=$Platform" --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Architecture evidence build failed for '$Project' ($Platform)."
    }
}

if (-not $NoBuild) {
    foreach ($platform in @('x86', 'x64', 'ARM64')) {
        Invoke-CheckedBuild -Project $rendererProject -Platform $platform
    }
    Invoke-CheckedBuild -Project $sampleProject -Platform 'x86'
    foreach ($platform in @('x64', 'ARM64')) {
        Invoke-CheckedBuild -Project $harnessProject -Platform $platform
    }
}

$records = [Collections.Generic.List[object]]::new()
function Add-PeEvidence {
    param(
        [Parameter(Mandatory)][string] $Platform,
        [Parameter(Mandatory)][string] $Component,
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ExpectedMachine,
        [Parameter(Mandatory)][ValidateSet('AnyCpuLibrary', 'PlatformManagedApp', 'Native')]
        [string] $ExpectedKind
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Architecture evidence input '$Path' does not exist. Build the matrix or omit -NoBuild."
    }

    $stream = [IO.File]::OpenRead($Path)
    $reader = $null
    try {
        $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        $headers = $reader.PEHeaders
        $corHeader = $headers.CorHeader
        $machine = $headers.CoffHeader.Machine.ToString()
        $magic = $headers.PEHeader.Magic.ToString()
        $hasCorHeader = $null -ne $corHeader
        $flags = if ($hasCorHeader) { $corHeader.Flags } else { 0 }
        $ilOnly = $hasCorHeader -and (($flags -band [System.Reflection.PortableExecutable.CorFlags]::ILOnly) -ne 0)
        $requires32Bit = $hasCorHeader -and (($flags -band [System.Reflection.PortableExecutable.CorFlags]::Requires32Bit) -ne 0)
        $prefers32Bit = $hasCorHeader -and (($flags -band [System.Reflection.PortableExecutable.CorFlags]::Prefers32Bit) -ne 0)

        $passed = switch ($ExpectedKind) {
            'AnyCpuLibrary' {
                $machine -eq 'I386' -and $magic -eq 'PE32' -and $hasCorHeader -and
                    $ilOnly -and -not $requires32Bit -and -not $prefers32Bit
            }
            'PlatformManagedApp' {
                $machine -eq $ExpectedMachine -and $hasCorHeader -and $ilOnly
            }
            'Native' {
                $machine -eq $ExpectedMachine -and -not $hasCorHeader
            }
        }

        $records.Add([pscustomobject][ordered]@{
            platform = $Platform
            component = $Component
            path = [IO.Path]::GetFullPath($Path)
            expectedKind = $ExpectedKind
            expectedMachine = $ExpectedMachine
            machine = $machine
            peMagic = $magic
            hasCorHeader = $hasCorHeader
            corFlags = if ($hasCorHeader) { $flags.ToString() } else { '' }
            ilOnly = $ilOnly
            requires32Bit = $requires32Bit
            prefers32Bit = $prefers32Bit
            lengthBytes = (Get-Item -LiteralPath $Path).Length
            sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
            passed = $passed
        })
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        $stream.Dispose()
    }
}

foreach ($platform in @('x86', 'x64', 'ARM64')) {
    $libraryDirectory = Join-Path $rendererRoot "MarkdownRenderer.Core\bin\$platform\$Configuration\$targetFramework"
    Add-PeEvidence -Platform $platform -Component 'MarkdownRenderer.Core' `
        -Path (Join-Path $libraryDirectory 'MarkdownRenderer.Core.dll') `
        -ExpectedMachine 'I386' -ExpectedKind 'AnyCpuLibrary'

    $libraryDirectory = Join-Path $rendererRoot "MarkdownRenderer\bin\$platform\$Configuration\$targetFramework"
    Add-PeEvidence -Platform $platform -Component 'MarkdownRenderer.WinUI' `
        -Path (Join-Path $libraryDirectory 'MarkdownRenderer.dll') `
        -ExpectedMachine 'I386' -ExpectedKind 'AnyCpuLibrary'
}

$x86Consumer = Join-Path $rendererRoot "MarkdownRenderer.Sample\bin\x86\$Configuration\$targetFramework\win-x86"
Add-PeEvidence -Platform 'x86' -Component 'sample-apphost' `
    -Path (Join-Path $x86Consumer 'MarkdownRenderer.Sample.exe') `
    -ExpectedMachine 'I386' -ExpectedKind 'Native'
Add-PeEvidence -Platform 'x86' -Component 'Win2D-native' `
    -Path (Join-Path $x86Consumer 'Microsoft.Graphics.Canvas.dll') `
    -ExpectedMachine 'I386' -ExpectedKind 'Native'
Add-PeEvidence -Platform 'x86' -Component 'consumer-MarkdownRenderer.Core' `
    -Path (Join-Path $x86Consumer 'MarkdownRenderer.Core.dll') `
    -ExpectedMachine 'I386' -ExpectedKind 'AnyCpuLibrary'
Add-PeEvidence -Platform 'x86' -Component 'consumer-MarkdownRenderer.WinUI' `
    -Path (Join-Path $x86Consumer 'MarkdownRenderer.dll') `
    -ExpectedMachine 'I386' -ExpectedKind 'AnyCpuLibrary'

foreach ($platform in @('x64', 'ARM64')) {
    $rid = if ($platform -eq 'x64') { 'win-x64' } else { 'win-arm64' }
    $expectedMachine = if ($platform -eq 'x64') { 'Amd64' } else { 'Arm64' }
    $consumer = Join-Path $rendererRoot "MarkdownRenderer.PerformanceHarness\bin\$platform\$Configuration\$targetFramework\$rid"
    Add-PeEvidence -Platform $platform -Component 'performance-harness-managed' `
        -Path (Join-Path $consumer 'MarkdownRenderer.PerformanceHarness.dll') `
        -ExpectedMachine $expectedMachine -ExpectedKind 'PlatformManagedApp'
    Add-PeEvidence -Platform $platform -Component 'performance-harness-apphost' `
        -Path (Join-Path $consumer 'MarkdownRenderer.PerformanceHarness.exe') `
        -ExpectedMachine $expectedMachine -ExpectedKind 'Native'
    Add-PeEvidence -Platform $platform -Component 'Win2D-native' `
        -Path (Join-Path $consumer 'Microsoft.Graphics.Canvas.dll') `
        -ExpectedMachine $expectedMachine -ExpectedKind 'Native'
    Add-PeEvidence -Platform $platform -Component 'consumer-MarkdownRenderer.Core' `
        -Path (Join-Path $consumer 'MarkdownRenderer.Core.dll') `
        -ExpectedMachine 'I386' -ExpectedKind 'AnyCpuLibrary'
    Add-PeEvidence -Platform $platform -Component 'consumer-MarkdownRenderer.WinUI' `
        -Path (Join-Path $consumer 'MarkdownRenderer.dll') `
        -ExpectedMachine 'I386' -ExpectedKind 'AnyCpuLibrary'
}

$failed = @($records | Where-Object { -not $_.passed })
$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTimeOffset]::UtcNow
    configuration = $Configuration
    evidenceScope = 'static cross-architecture build and PE-header/native-resolution evidence only'
    satisfiesPhysicalRuntimePerformanceGates = $false
    physicalRuntimeRequirement = 'Run the performance harness on pinned physical x64 and ARM64 120 Hz reference machines.'
    hostProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    platforms = @('x86', 'x64', 'ARM64')
    records = $records
    passed = $failed.Count -eq 0
    failures = @($failed | ForEach-Object {
        "$($_.platform)/$($_.component): expected $($_.expectedKind) $($_.expectedMachine), got $($_.machine) $($_.corFlags)."
    })
}

$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($fullOutputPath)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'Architecture evidence output path must have a parent directory.'
}
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[IO.File]::WriteAllText(
    $fullOutputPath,
    ($report | ConvertTo-Json -Depth 20),
    [Text.UTF8Encoding]::new($false))

if (-not $report.passed) {
    throw "Managed architecture evidence failed:`n - $($report.failures -join "`n - ")"
}

Write-Host "Managed architecture evidence passed and was written to '$fullOutputPath'."
