$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$smokePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Invoke-JitHubReleaseSandboxSmoke.ps1'
$projectPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $smokePath) '..\JitHub.WinUI\JitHub.WinUI.csproj'))
if (-not (Test-Path -LiteralPath $smokePath -PathType Leaf)) {
    throw "Sandbox smoke script was not found: $smokePath"
}

function winapp {
    if ($args.Count -eq 1 -and $args[0] -eq '--version') {
        $global:LASTEXITCODE = 0
        Write-Output '0.7.0'
        return
    }

    $invocation = $args -join ' '
    $isReadOnlyPackageQuery = $invocation -match '^target exec sandbox powershell -NoProfile -NonInteractive -Command ' -and
        $invocation -match "Get-AppxPackage -Name 'Microsoft\.Services\.Store\.Engagement'"
    if (-not $isReadOnlyPackageQuery) {
        throw "Unexpected WinApp invocation; only version and the read-only package query are allowed: $($args -join ' ')"
    }

    $global:LASTEXITCODE = 0
    Write-Output $script:GuestPackageJson
}

function dotnet {
    throw 'dotnet must not be invoked by an invalid-option preflight.'
}

function Assert-SmokePreflightRejected {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][hashtable]$Parameters,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    try {
        $invokeParameters = @{ ProjectPath = $script:projectPath }
        foreach ($parameterName in $Parameters.Keys) {
            $invokeParameters[$parameterName] = $Parameters[$parameterName]
        }
        & $script:smokePath @invokeParameters
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "$Name failed for the wrong reason. Expected '$ExpectedMessage'; got '$($_.Exception.Message)'."
        }
        Write-Host "PASS: $Name"
        return
    }

    throw "$Name unexpectedly passed preflight."
}

Assert-SmokePreflightRejected `
    -Name 'AOT mode requires a publish directory' `
    -Parameters @{ NativeAot = $true; SkipScreenshot = $true } `
    -ExpectedMessage '-NativeAot requires an existing -PublishDirectory'

Assert-SmokePreflightRejected `
    -Name 'Publish directory cannot imply AOT mode' `
    -Parameters @{ PublishDirectory = 'C:\missing-native-aot-output'; SkipScreenshot = $true } `
    -ExpectedMessage '-PublishDirectory is accepted only together with -NativeAot'

$missingPublishDirectory = Join-Path ([IO.Path]::GetTempPath()) "jithub-missing-aot-publish-$([guid]::NewGuid().ToString('N'))"
if (Test-Path -LiteralPath $missingPublishDirectory) {
    throw "The negative-test publish path unexpectedly exists: $missingPublishDirectory"
}
Assert-SmokePreflightRejected `
    -Name 'AOT mode rejects a missing exact publish directory' `
    -Parameters @{ NativeAot = $true; PublishDirectory = $missingPublishDirectory; SkipScreenshot = $true } `
    -ExpectedMessage 'explicit NativeAOT publish directory does not exist'

# Load only the package-query and command-construction functions. This keeps the test
# deterministic and prevents the script's release smoke flow from running.
$scriptText = Get-Content -LiteralPath $smokePath -Raw
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Could not parse the Sandbox smoke script: $($parseErrors[0].Message)"
}

foreach ($functionName in @('Get-EngagementGuestPackages', 'Test-ExpectedEngagementPackage', 'Get-ReleaseSandboxRunArguments', 'Assert-NativeAotGuestIdentity')) {
    $functionAst = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
    }, $true)
    if ($null -eq $functionAst) {
        throw "Could not find production function '$functionName' in $smokePath."
    }
    Invoke-Expression $functionAst.Extent.Text
}

$script:GuestPackageJson = '[]'
$emptyPackages = @(Get-EngagementGuestPackages)
if ($emptyPackages.Count -ne 0) {
    throw "An empty JSON array should produce zero guest packages; got $($emptyPackages.Count)."
}
$emptyExpected = @(Test-ExpectedEngagementPackage -Packages $emptyPackages)
if ($emptyExpected.Count -ne 0) {
    throw "An empty guest package list should produce zero expected packages; got $($emptyExpected.Count)."
}
Write-Host 'PASS: empty package JSON is an empty collection and remains safe through the selector.'

$expectedPackage = [pscustomobject]@{
    Name = 'Microsoft.Services.Store.Engagement'
    Version = '10.0.23012.0'
    Architecture = 'X64'
    Publisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
    PackageFullName = 'Microsoft.Services.Store.Engagement_10.0.23012.0_x64__8wekyb3d8bbwe'
}
$script:GuestPackageJson = ConvertTo-Json -InputObject @($expectedPackage) -Compress
$onePackage = @(Get-EngagementGuestPackages)
if ($onePackage.Count -ne 1 -or $onePackage[0].PackageFullName -ne $expectedPackage.PackageFullName) {
    throw "A single package JSON response was not preserved as exactly one package."
}
$oneExpected = @(Test-ExpectedEngagementPackage -Packages $onePackage)
if ($oneExpected.Count -ne 1 -or $oneExpected[0].PackageFullName -ne $expectedPackage.PackageFullName) {
    throw 'The selector did not preserve the single expected package.'
}
Write-Host 'PASS: one package is parsed and selected exactly once.'

$script:GuestPackageJson = ConvertTo-Json -InputObject @($expectedPackage, $expectedPackage) -Compress
$duplicatePackages = @(Get-EngagementGuestPackages)
if ($duplicatePackages.Count -ne 2) {
    throw "Duplicate package JSON entries must remain visible for ambiguity detection; got $($duplicatePackages.Count)."
}
$duplicateExpected = @(Test-ExpectedEngagementPackage -Packages $duplicatePackages)
if ($duplicateExpected.Count -ne 2) {
    throw "The selector must preserve duplicate expected packages for caller-side ambiguity detection; got $($duplicateExpected.Count)."
}
Write-Host 'PASS: duplicate packages remain visible to caller-side ambiguity checks.'

$aotArguments = @(Get-ReleaseSandboxRunArguments -ProjectPath $projectPath -NativeAot -PublishDirectory 'C:\AOT output\')
if ($aotArguments -contains '--no-build' -or
    $aotArguments -notcontains '--aot' -or
    $aotArguments -notcontains '--no-restore' -or
    $aotArguments -notcontains 'PublishDir=C:\AOT output\' -or
    $aotArguments[1] -ne $projectPath -or
    $aotArguments[2] -ne '--on' -or $aotArguments[3] -ne 'sandbox') {
    throw "AOT smoke arguments must publish the explicit project/output in Sandbox without --no-build: $($aotArguments -join ' ')"
}
Write-Host 'PASS: AOT mode uses a CLI-supported publish and preserves the explicit path as one argument.'

$jitArguments = @(Get-ReleaseSandboxRunArguments -ProjectPath $projectPath)
if ($jitArguments -notcontains '--no-build' -or
    $jitArguments -contains '--aot' -or $jitArguments -contains '--no-restore' -or
    @($jitArguments | Where-Object { $_ -like 'PublishDir=*' }).Count -ne 0 -or
    $jitArguments[1] -ne $projectPath -or
    $jitArguments[2] -ne '--on' -or $jitArguments[3] -ne 'sandbox') {
    throw "JIT smoke arguments must use existing project build output without claiming AOT: $($jitArguments -join ' ')"
}
Write-Host 'PASS: JIT mode remains separate and does not imply AOT validation.'

$prePublishHash = 'a' * 64
$publishedHash = 'b' * 64
$guestProof = [pscustomobject]@{
    ProcessId = 1234
    ExecutableSha256 = $publishedHash
    LoadedForbiddenModules = @()
}
Assert-NativeAotGuestIdentity -GuestProof $guestProof -ExpectedProcessId 1234 -ExpectedSha256 $publishedHash
Write-Host 'PASS: running native bytes match the verified post-publish output.'

foreach ($case in @(
    @{ Name = 'stale pre-publish hash'; ProcessId = 1234; Hash = $prePublishHash; Modules = @() },
    @{ Name = 'wrong process'; ProcessId = 5678; Hash = $publishedHash; Modules = @() },
    @{ Name = 'managed runtime'; ProcessId = 1234; Hash = $publishedHash; Modules = @('coreclr.dll') },
    @{ Name = 'invalid expected hash'; ProcessId = 1234; Hash = ''; Modules = @() }
)) {
    $guestProof.LoadedForbiddenModules = $case.Modules
    $rejected = $false
    try {
        Assert-NativeAotGuestIdentity -GuestProof $guestProof -ExpectedProcessId $case.ProcessId -ExpectedSha256 $case.Hash
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Identity verification accepted $($case.Name)." }
    Write-Host "PASS: identity verification rejects $($case.Name)."
}

Write-Host 'Release Sandbox smoke tests passed; only the mocked read-only package query ran, with no Sandbox commands or app launch.'
