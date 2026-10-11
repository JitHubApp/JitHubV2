[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\JitHub.WinUI\JitHub.WinUI.csproj'),
    [string]$ScreenshotPath = (Join-Path $PSScriptRoot '..\artifacts\screenshots\winapp-cli\jithub-release-sandbox.png'),
    [switch]$NativeAot,
    [string]$PublishDirectory = '',
    [switch]$SkipScreenshot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name is not available on PATH."
    }
}

function Get-EngagementGuestPackages {
    $probe = @'
$packages = @(Get-AppxPackage -Name 'Microsoft.Services.Store.Engagement' -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{
        Name = [string]$_.Name
        Version = [string]$_.Version
        Architecture = [string]$_.Architecture
        Publisher = [string]$_.Publisher
        PackageFullName = [string]$_.PackageFullName
    }
})
ConvertTo-Json -InputObject $packages -Compress -Depth 4
'@

    $output = (& winapp target exec sandbox -- powershell -NoProfile -NonInteractive -Command $probe 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect Microsoft.Services.Store.Engagement in the sandbox: $output"
    }

    try {
        $packages = @(ConvertFrom-Json -InputObject $output)
    }
    catch {
        throw "The sandbox package query did not return valid JSON: $output"
    }

    return $packages
}

function Test-ExpectedEngagementPackage {
    param([object[]]$Packages)

    return @($Packages | Where-Object {
        $_.Name -eq 'Microsoft.Services.Store.Engagement' -and
        $_.Version -eq '10.0.23012.0' -and
        $_.Architecture -eq 'X64' -and
        $_.Publisher -match '^CN=Microsoft Corporation(?:,|$)'
    })
}

function Get-ReleaseSandboxRunArguments {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [switch]$NativeAot,
        [string]$PublishDirectory = ''
    )

    $arguments = @('run', $ProjectPath, '--on', 'sandbox', '--detach', '--json', '-c', 'Release', '--arch', 'x64')
    if ($NativeAot) {
        # WinApp CLI always publishes in AOT mode and rejects --no-build.
        # Keep the explicit output directory. Reverify it after the publish,
        # then compare the activated bytes with that verified artifact.
        $publishDirectoryForMsBuild = $PublishDirectory.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
        $arguments += @('--aot', '--no-restore', '-p', "PublishDir=$publishDirectoryForMsBuild")
    }
    else {
        $arguments += '--no-build'
    }

    return $arguments
}

function Assert-NativeAotGuestIdentity {
    param(
        [Parameter(Mandatory = $true)][object]$GuestProof,
        [Parameter(Mandatory = $true)][int]$ExpectedProcessId,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    if ($ExpectedProcessId -le 0 -or $ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        [int]$GuestProof.ProcessId -ne $ExpectedProcessId -or
        [string]$GuestProof.ExecutableSha256 -cne $ExpectedSha256 -or
        @($GuestProof.LoadedForbiddenModules).Count -ne 0) {
        throw 'The running Sandbox executable does not match the verified NativeAOT publish or loaded a managed runtime.'
    }
}

Require-Command -Name 'winapp'
Require-Command -Name 'dotnet'

$versionText = (& winapp --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $versionText -notmatch '(\d+)\.(\d+)\.(\d+)') {
    throw "Cannot determine WinApp CLI version: $versionText"
}
if ([version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3]) -lt [version]'0.7.0') {
    throw "WinApp CLI 0.7 or newer is required; found $versionText."
}

$expectedProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\JitHub.WinUI\JitHub.WinUI.csproj'))
$resolvedProject = [IO.Path]::GetFullPath($ProjectPath)
if ($resolvedProject -ine $expectedProject) {
    throw 'This smoke script is scoped to JitHub.WinUI.csproj.'
}
if (-not (Test-Path -LiteralPath $expectedProject -PathType Leaf)) {
    throw "JitHub project file was not found: $expectedProject"
}

$executionMode = if ($NativeAot) { 'native-aot' } else { 'release-jit' }
$resolvedPublishDirectory = $null
$expectedAotExecutableSha256 = $null
if ($NativeAot) {
    if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
        throw '-NativeAot requires an existing -PublishDirectory from the exact x64 NativeAOT publish to activate.'
    }

    $resolvedPublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
    if (-not (Test-Path -LiteralPath $resolvedPublishDirectory -PathType Container)) {
        throw "The explicit NativeAOT publish directory does not exist: $resolvedPublishDirectory"
    }

    & (Join-Path $PSScriptRoot 'Verify-NativeAotArtifact.ps1') `
        -InputPath $resolvedPublishDirectory `
        -Architecture 'x64'
    if (-not $?) {
        throw "NativeAOT artifact verification failed for the explicit publish directory: $resolvedPublishDirectory"
    }

    $expectedAotExecutablePath = Join-Path $resolvedPublishDirectory 'JitHub.WinUI.exe'
    if (-not (Test-Path -LiteralPath $expectedAotExecutablePath -PathType Leaf)) {
        throw "The verified NativeAOT publish is missing its expected executable: $expectedAotExecutablePath"
    }
    $expectedAotExecutableSha256 = (Get-FileHash -LiteralPath $expectedAotExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Preflight verified the exact x64 NativeAOT publish directory: $resolvedPublishDirectory"
}
elseif (-not [string]::IsNullOrWhiteSpace($PublishDirectory)) {
    throw '-PublishDirectory is accepted only together with -NativeAot; the default Release smoke is managed/JIT.'
}

Write-Host "Sandbox smoke execution mode: $executionMode."

$lockPath = Join-Path (Split-Path -Parent $expectedProject) 'packages.lock.json'
if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "The JitHub.WinUI locked NuGet graph was not found: $lockPath"
}
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$engagementEntries = @(
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        if ($framework.Name -notlike '*/*') {
            $entry = $framework.Value.PSObject.Properties['Microsoft.Services.Store.Engagement']
            if ($null -ne $entry) {
                $entry.Value
            }
        }
    }
)
$lockedVersions = @($engagementEntries | ForEach-Object { [string]$_.resolved } | Sort-Object -Unique)
if ($lockedVersions.Count -ne 1 -or [string]::IsNullOrWhiteSpace($lockedVersions[0])) {
    throw 'Microsoft.Services.Store.Engagement must have one version in the JitHub.WinUI locked dependency graph.'
}
if (@($engagementEntries | Where-Object { $_.type -ne 'Direct' }).Count -gt 0) {
    throw 'Microsoft.Services.Store.Engagement is not a direct dependency in the JitHub.WinUI lock file.'
}
$lockedVersion = $lockedVersions[0]

# NuGet generates this MSBuild property because the PackageReference sets
# GeneratePathProperty=true. Resolve through it instead of assuming a cache root.
$packageRoot = (& dotnet msbuild $expectedProject '-getProperty:PkgMicrosoft_Services_Store_Engagement' | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($packageRoot)) {
    throw 'MSBuild did not resolve PkgMicrosoft_Services_Store_Engagement. Restore JitHub.WinUI before running this smoke.'
}
if ((Split-Path -Leaf $packageRoot) -ne $lockedVersion) {
    throw "The resolved NuGet package path does not match locked version ${lockedVersion}: $packageRoot"
}

$relativeAppxPath = 'SDK\Windows Kits\10\ExtensionSDKs\Microsoft.Services.Store.Engagement\10.0\Appx\x64\Microsoft.Services.Store.Engagement.x64.10.0.appx'
$engagementAppxPath = Join-Path $packageRoot $relativeAppxPath
if (-not (Test-Path -LiteralPath $engagementAppxPath -PathType Leaf)) {
    throw "The locked x64 Engagement SDK Appx was not found: $engagementAppxPath"
}

$signature = Get-AuthenticodeSignature -LiteralPath $engagementAppxPath
if ($signature.Status.ToString() -ne 'Valid' -or
    $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
    throw "The Engagement SDK Appx does not have a valid Microsoft signature. Status: $($signature.Status); signer: $($signature.SignerCertificate.Subject)"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($engagementAppxPath)
try {
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) {
        throw 'The Engagement SDK Appx does not contain AppxManifest.xml.'
    }
    $manifestReader = [IO.StreamReader]::new($manifestEntry.Open())
    try {
        [xml]$appxManifest = $manifestReader.ReadToEnd()
    }
    finally {
        $manifestReader.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$identity = $appxManifest.Package.Identity
if ($identity.Name -ne 'Microsoft.Services.Store.Engagement' -or
    $identity.Version -ne '10.0.23012.0' -or
    $identity.ProcessorArchitecture -ne 'x64' -or
    $identity.Publisher -notmatch '^CN=Microsoft Corporation(?:,|$)' -or
    $identity.Publisher -ne $signature.SignerCertificate.Subject) {
    throw "Unexpected Engagement SDK Appx identity: $($identity.Name), $($identity.Version), $($identity.ProcessorArchitecture), $($identity.Publisher)"
}
Write-Host "Validated locked Engagement SDK package $lockedVersion and signed x64 Appx identity $($identity.Version)."

$snapshotText = (& winapp target snapshot sandbox --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect the WinApp sandbox target: $snapshotText"
}
try {
    $snapshot = ConvertFrom-Json -InputObject $snapshotText
}
catch {
    throw "The WinApp sandbox snapshot did not return valid JSON: $snapshotText"
}
if (-not $snapshot.running -or
    $snapshot.capabilities.architecture -ne 'x64' -or
    -not $snapshot.capabilities.supportsInteractiveDesktop -or
    [string]::IsNullOrWhiteSpace([string]$snapshot.workRoot)) {
    throw 'The WinApp sandbox must be running with an x64 interactive desktop before this smoke can continue.'
}

$guestPackages = @(Get-EngagementGuestPackages)
$expectedGuestPackages = @(Test-ExpectedEngagementPackage -Packages $guestPackages)
if ($expectedGuestPackages.Count -eq 1 -and $guestPackages.Count -eq 1) {
    Write-Host "Sandbox already has $($expectedGuestPackages[0].PackageFullName)."
}
elseif ($guestPackages.Count -eq 0) {
    $stagedRelativePath = 'prerequisites/Microsoft.Services.Store.Engagement.x64.10.0.appx'
    $guestAppxPath = Join-Path ([string]$snapshot.workRoot) ($stagedRelativePath -replace '/', '\')
    $pushText = (& winapp target push sandbox $engagementAppxPath $stagedRelativePath 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not stage the signed Engagement SDK Appx in the sandbox: $pushText"
    }
    if (-not [string]::IsNullOrWhiteSpace($pushText)) { Write-Host $pushText }

    $quotedGuestAppxPath = "'" + $guestAppxPath.Replace("'", "''") + "'"
    $installCommand = "Add-AppxPackage -Path $quotedGuestAppxPath -ErrorAction Stop"
    $installText = (& winapp target exec sandbox -- powershell -NoProfile -NonInteractive -Command $installCommand 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not install the signed Engagement SDK Appx in the sandbox: $installText"
    }

    $guestPackages = @(Get-EngagementGuestPackages)
    $expectedGuestPackages = @(Test-ExpectedEngagementPackage -Packages $guestPackages)
    if ($expectedGuestPackages.Count -ne 1 -or $guestPackages.Count -ne 1) {
        throw "The sandbox did not report the expected Engagement SDK package after installation: $(ConvertTo-Json -InputObject $guestPackages -Compress -Depth 4)"
    }
    Write-Host "Installed and verified $($expectedGuestPackages[0].PackageFullName) in the sandbox."
}
else {
    $guestDetails = ConvertTo-Json -InputObject $guestPackages -Compress -Depth 4
    throw "The sandbox has an unexpected Microsoft.Services.Store.Engagement package state. No guest package was removed or replaced: $guestDetails"
}

$previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = "jithub-release-sandbox-$([guid]::NewGuid().ToString('N'))"
$launched = $false
try {
    $runArguments = @(Get-ReleaseSandboxRunArguments -ProjectPath $expectedProject -NativeAot:$NativeAot -PublishDirectory $resolvedPublishDirectory)
    $runText = (& winapp @runArguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "JitHub Release x64 did not launch in Windows Sandbox: $runText"
    }
    $jsonStarts = [regex]::Matches($runText, '(?m)^\s*\{')
    if ($jsonStarts.Count -eq 0) {
        throw "The sandbox launch did not return JSON: $runText"
    }
    $run = $runText.Substring($jsonStarts[$jsonStarts.Count - 1].Index).Trim() | ConvertFrom-Json
    if ($null -eq $run.ProcessId -or [int]$run.ProcessId -le 0 -or
        -not $run.Sandbox -or $run.ProcessScope -ne 'sandbox') {
        throw 'WinApp CLI did not report a sandbox-scoped JitHub Release process.'
    }

    $launched = $true
    $appProcessId = [int]$run.ProcessId
    $expectedUiTargetArgs = "--on sandbox -a $appProcessId"
    if ([string]$run.UiTargetArgs -cne $expectedUiTargetArgs) {
        throw "WinApp CLI did not report the exact Sandbox UI target arguments for PID $appProcessId. Expected '$expectedUiTargetArgs'; got '$($run.UiTargetArgs)'."
    }
    Write-Host "JitHub x64 started in Windows Sandbox (PID $appProcessId; mode $executionMode)."

    if ($NativeAot) {
        # --aot necessarily republishes. Its output may differ from the earlier
        # preflight artifact (including when project evaluation adds properties).
        # Verify the output produced by this launch, not stale pre-publish bytes.
        # Guest identity still must match this exact verified native executable.
        & (Join-Path $PSScriptRoot 'Verify-NativeAotArtifact.ps1') `
            -InputPath $resolvedPublishDirectory `
            -Architecture 'x64'
        if (-not $?) {
            throw 'The NativeAOT output produced by WinApp failed artifact verification.'
        }
        $prePublishExecutableSha256 = $expectedAotExecutableSha256
        $expectedAotExecutableSha256 = (Get-FileHash -LiteralPath $expectedAotExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "Verified post-publish NativeAOT executable (before: $prePublishExecutableSha256; after: $expectedAotExecutableSha256)."

        $verifyGuestExecutableProbe = @'
$processId = __APP_PID__
$process = Get-Process -Id $processId -ErrorAction Stop
$executablePath = [string]$process.Path
if ([string]::IsNullOrWhiteSpace($executablePath) -or
    [IO.Path]::GetFileName($executablePath) -ine 'JitHub.WinUI.exe') {
    throw "Sandbox PID $processId does not resolve to the JitHub.WinUI executable: '$executablePath'."
}
$sha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
$forbiddenModules = @('coreclr.dll', 'clrjit.dll', 'hostfxr.dll', 'hostpolicy.dll', 'createdump.exe')
$loadedForbiddenModules = @($process.Modules | ForEach-Object { [string]$_.ModuleName } |
    Where-Object { $forbiddenModules -contains $_ })
if ($loadedForbiddenModules.Count -gt 0) {
    throw "The AOT Sandbox process loaded managed runtime modules: $($loadedForbiddenModules -join ', ')."
}
[pscustomobject]@{
    ProcessId = $processId
    ExecutablePath = $executablePath
    ExecutableSha256 = $sha256
    LoadedForbiddenModules = $loadedForbiddenModules
} | ConvertTo-Json -Compress
'@ -replace '__APP_PID__', [string]$appProcessId
        $guestProofText = (& winapp target exec sandbox -- powershell -NoProfile -NonInteractive -Command $verifyGuestExecutableProbe 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Could not verify the running AOT executable in the Sandbox: $guestProofText"
        }
        try {
            $guestProof = ConvertFrom-Json -InputObject $guestProofText
        }
        catch {
            throw "The Sandbox AOT executable proof did not return valid JSON: $guestProofText"
        }
        Assert-NativeAotGuestIdentity -GuestProof $guestProof -ExpectedProcessId $appProcessId -ExpectedSha256 $expectedAotExecutableSha256
        Write-Host "Verified Sandbox AOT executable bytes against '$resolvedPublishDirectory' (SHA-256 $expectedAotExecutableSha256); no managed runtime modules were loaded."
    }

    $waitText = (& winapp ui wait-for LoginSignInButton --on sandbox -a $appProcessId --type Button -t 15000 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "The JitHub Release sign-in shell did not become accessible in the sandbox: $waitText"
    }
    Write-Host 'Verified the unauthenticated sign-in button without submitting credentials.'

    $statusText = (& winapp ui status --on sandbox -a $appProcessId --json 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read JitHub's sandbox UI status: $statusText"
    }
    try {
        $uiStatus = ConvertFrom-Json -InputObject $statusText
    }
    catch {
        throw "The JitHub sandbox UI status did not return valid JSON: $statusText"
    }
    if ($uiStatus.processId -ne [int]$appProcessId -or $uiStatus.hwnd -le 0 -or $uiStatus.windowDpi -le 0) {
        throw "JitHub's sandbox window status is incomplete: $statusText"
    }
    Write-Host "Sandbox window DPI: $($uiStatus.windowDpi) (scale $($uiStatus.scale), $($uiStatus.dpiAwareness))."

    if (-not $SkipScreenshot) {
        $fullScreenshotPath = [IO.Path]::GetFullPath($ScreenshotPath)
        $screenshotDirectory = Split-Path -Parent $fullScreenshotPath
        New-Item -ItemType Directory -Path $screenshotDirectory -Force | Out-Null
        $screenshotText = (& winapp ui screenshot --on sandbox -a $appProcessId -o $fullScreenshotPath 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Could not capture the JitHub Release sandbox window: $screenshotText"
        }
        Write-Host "Screenshot written to $fullScreenshotPath"
    }

    if ($NativeAot -and
        (Get-FileHash -LiteralPath $expectedAotExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedAotExecutableSha256) {
        throw 'The verified NativeAOT publish changed while the Sandbox smoke was running.'
    }
}
finally {
    try {
        if ($launched) {
            & winapp ui yield --on sandbox --quiet | Out-Null

        $closeProbe = @'
$processId = __APP_PID__
$process = Get-Process -Id $processId -ErrorAction SilentlyContinue
if ($process) {
    [pscustomobject]@{ ProcessId = $processId; CloseRequested = [bool]$process.CloseMainWindow() } | ConvertTo-Json -Compress
}
else {
    Write-Output 'already-exited'
}
'@ -replace '__APP_PID__', [string]$appProcessId
        $closeText = (& winapp target exec sandbox -- powershell -NoProfile -NonInteractive -Command $closeProbe 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Could not request a graceful close for the JitHub sandbox smoke process ${appProcessId}: $closeText"
        }

        $stateProbe = "if (Get-Process -Id $appProcessId -ErrorAction SilentlyContinue) { Write-Output 'running' } else { Write-Output 'exited' }"
        $processExited = $false
        $lastProcessState = ''
        for ($attempt = 0; $attempt -lt 8; $attempt++) {
            $lastProcessState = (& winapp target exec sandbox -- powershell -NoProfile -NonInteractive -Command $stateProbe 2>&1 | Out-String).Trim()
            if ($LASTEXITCODE -ne 0) {
                throw "Could not verify the JitHub sandbox smoke process $appProcessId after requesting close: $lastProcessState"
            }
            if ($lastProcessState -eq 'exited') {
                $processExited = $true
                break
            }
            if ($lastProcessState -ne 'running') {
                throw "The guest returned an unexpected process state for smoke PID ${appProcessId}: $lastProcessState"
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $processExited) {
            throw "JitHub sandbox smoke process $appProcessId survived the bounded graceful-close wait. Guest state: $lastProcessState"
        }
        Write-Host "Closed the JitHub sandbox smoke process $appProcessId; screenshot evidence was preserved."
        }
    }
    finally {
        if ($null -eq $previousWorkflowId) {
            Remove-Item Env:WINAPP_UI_WORKFLOW_ID -ErrorAction SilentlyContinue
        }
        else {
            $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflowId
        }
    }
}
