[CmdletBinding()]
param(
    [string]$InputFolder = "",
    [string]$ManifestPath = "",
    [string]$ExecutableRelativePath = "",
    [string]$WinApp = "winapp",
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-WinApp {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = (& $script:WinApp @Arguments 2>&1 | Out-String -Width 4096)
    if ($LASTEXITCODE -ne 0) {
        throw "winapp $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$output"
    }
    return $output.Trim()
}

function Set-IdentityName {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Name)

    if ($Name -notmatch '^[A-Za-z0-9]{1,50}$') { throw "Unsafe package identity name '$Name'." }
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $nodes = @($document.SelectNodes("//*[local-name()='Identity']"))
    if ($nodes.Count -ne 1) { throw "Expected exactly one Package/Identity in '$Path'." }
    $nodes[0].SetAttribute("Name", $Name)
    $document.Save($Path)
}

function Get-SandboxSnapshot { (Invoke-WinApp @("target", "snapshot", "sandbox", "--json")) | ConvertFrom-Json }

function Get-GuestPackageState {
    param([Parameter(Mandatory)][string]$Name)

    $code = "`$p=@(Get-AppxPackage -Name '$Name' -ErrorAction SilentlyContinue); if (`$p.Count -eq 0) { 'PROBE_ABSENT:${Name}' } elseif (`$p.Count -eq 1) { 'PROBE_PRESENT:${Name}:' + `$p[0].PackageFullName } else { 'PROBE_AMBIGUOUS:${Name}:' + `$p.Count }"
    $raw = Invoke-WinApp @("target", "exec", "sandbox", "--json", "--", "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", $code)
    if ($raw.Contains("PROBE_ABSENT:${Name}")) { return "Absent" }
    if ($raw.Contains("PROBE_AMBIGUOUS:${Name}:")) { return "Ambiguous" }
    if ($raw.Contains("PROBE_PRESENT:${Name}:")) { return "Present" }
    throw "Sandbox package query for '$Name' returned no recognized sentinel: $raw"
}

function Assert-HostPackageAbsent {
    param([Parameter(Mandatory)][string]$Name)
    if (@(Get-AppxPackage -Name $Name -ErrorAction SilentlyContinue).Count) { throw "Refusing to use pre-existing host package identity '$Name'." }
}

function Get-TopLevelManifest {
    param([Parameter(Mandatory)][string]$Folder, [string]$RequestedPath = "")
    if ($RequestedPath) { return (Resolve-Path -LiteralPath $RequestedPath).Path }
    $candidates = @(@("AppxManifest.xml", "Package.appxmanifest") | ForEach-Object { Join-Path $Folder $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($candidates.Count -ne 1) { throw "Expected one top-level manifest in '$Folder'; found $($candidates.Count)." }
    $candidates[0]
}

$script:WinApp = $WinApp
$runHelp = Invoke-WinApp @("run", "--help")
if (@("--manifest", "--output-appx-directory", "--no-launch", "--on" | Where-Object { -not $runHelp.Contains($_) }).Count) { throw "Installed WinApp CLI lacks a required isolation option." }
if (-not $Execute) {
    Write-Output "Plan only: WinApp manifest-override proof will use two fresh Sandbox-only identities, folder mode, and --no-launch."
    Write-Output "No package query, registration, app launch, or cleanup was performed. Use -Execute -InputFolder <built loose AppX folder> to proceed."
    return
}
if ([string]::IsNullOrWhiteSpace($InputFolder)) { throw "-InputFolder is required with -Execute." }

$source = (Resolve-Path -LiteralPath $InputFolder).Path
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "InputFolder must be a build-output directory." }
$sourceManifest = Get-TopLevelManifest -Folder $source -RequestedPath $ManifestPath
$sourceXml = [xml](Get-Content -LiteralPath $sourceManifest -Raw)
$applicationNodes = @($sourceXml.SelectNodes("//*[local-name()='Application']"))
if ($applicationNodes.Count -lt 1) { throw "Input manifest has no Application node; refusing to register it." }
$manifestExe = $applicationNodes[0].GetAttribute("Executable")
if ([string]::IsNullOrWhiteSpace($ExecutableRelativePath)) {
    if ($manifestExe -match '\$targetnametoken\$') { throw "Manifest uses `$targetnametoken`$. Supply -ExecutableRelativePath to disambiguate the staged app executable." }
    $ExecutableRelativePath = $manifestExe
}
$resolvedExe = [IO.Path]::GetFullPath((Join-Path $source $ExecutableRelativePath))
if (-not $resolvedExe.StartsWith([IO.Path]::GetFullPath($source).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $resolvedExe -PathType Leaf)) {
    throw "ExecutableRelativePath must resolve to a file inside InputFolder."
}

$nonce = [guid]::NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant()
$identityA = "CodexProbeA$nonce"
$identityB = "CodexProbeB$nonce"
Assert-HostPackageAbsent -Name $identityA
Assert-HostPackageAbsent -Name $identityB
$snapshotBefore = Get-SandboxSnapshot
if (-not $snapshotBefore.running -or -not $snapshotBefore.attached -or $snapshotBefore.capabilities.architecture -ne "x64") {
    throw "Sandbox must already be running, attached, and x64; this helper never starts or repairs it."
}
if ((Get-GuestPackageState -Name $identityA) -ne "Absent" -or (Get-GuestPackageState -Name $identityB) -ne "Absent") {
    throw "Refusing to use a pre-existing or ambiguous Sandbox identity."
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ("WinAppManifestOverride-$nonce")
if (Test-Path -LiteralPath $scratch) { throw "Unexpected pre-existing scratch directory '$scratch'." }
$inputCopy = Join-Path $scratch "input"
$layout = Join-Path $scratch "layout"
$mutationStarted = $false
$cleanupVerified = $false
$removedNames = [System.Collections.Generic.List[string]]::new()
[void](New-Item -ItemType Directory -Path $inputCopy -Force)
Write-Output "Temporary host input/layout root: $scratch"
try {
    $reparsePoints = @(Get-ChildItem -LiteralPath $source -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($reparsePoints.Count -gt 0) { throw "InputFolder contains reparse points; refusing to copy it." }
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $inputCopy -Recurse -Force

    $relativeManifest = [IO.Path]::GetRelativePath($source, $sourceManifest)
    if ($relativeManifest -eq ".." -or $relativeManifest.StartsWith(".." + [IO.Path]::DirectorySeparatorChar)) {
        $manifestName = if ([IO.Path]::GetExtension($sourceManifest) -ieq ".xml") { "AppxManifest.xml" } else { "Package.appxmanifest" }
        if ((Test-Path -LiteralPath (Join-Path $inputCopy "AppxManifest.xml") -PathType Leaf) -or
            (Test-Path -LiteralPath (Join-Path $inputCopy "Package.appxmanifest") -PathType Leaf)) {
            throw "External manifest template would be ambiguous with an input manifest; refusing to stage."
        }
        $defaultCopy = Join-Path $inputCopy $manifestName
        Copy-Item -LiteralPath $sourceManifest -Destination $defaultCopy
        $sourceAssets = Join-Path (Split-Path -Parent $sourceManifest) "Assets"
        $copiedAssets = Join-Path $inputCopy "Assets"
        if (-not (Test-Path -LiteralPath $copiedAssets) -and (Test-Path -LiteralPath $sourceAssets -PathType Container)) {
            Copy-Item -LiteralPath $sourceAssets -Destination $copiedAssets -Recurse
        }
    }
    else {
        $defaultCopy = Join-Path $inputCopy $relativeManifest
    }
    Set-IdentityName -Path $defaultCopy -Name $identityA
    $override = Join-Path $inputCopy "CodexManifestOverride.appxmanifest"
    Copy-Item -LiteralPath $defaultCopy -Destination $override
    Set-IdentityName -Path $override -Name $identityB
    $snapshotIdsBefore = @($snapshotBefore.deployments | ForEach-Object { [string]$_.deploymentId })

    $runArgs = @("run", $inputCopy, "--manifest", $override, "--output-appx-directory", $layout, "--no-launch", "--json", "--on", "sandbox")
    $runArgs += @("--exe", $ExecutableRelativePath)
    Write-Output "Registering only the temporary override candidate in the existing Sandbox; no application launch requested."
    $mutationStarted = $true
    [void](Invoke-WinApp $runArgs)
    Write-Output "winapp run returned successfully."

    $stateA = Get-GuestPackageState -Name $identityA
    $stateB = Get-GuestPackageState -Name $identityB
    if ($stateA -eq "Absent" -and $stateB -eq "Present") { Write-Output "PASS: --manifest selected '$identityB' over default '$identityA'." }
    else { Write-Output "FAIL: expected only override '$identityB'; default='$stateA', override='$stateB'." }
}
finally {
    if ($mutationStarted) {
        $snapshotAfter = Get-SandboxSnapshot
        $newIds = @($snapshotAfter.deployments | ForEach-Object { [string]$_.deploymentId } | Where-Object { $snapshotIdsBefore -notcontains $_ })
        $newDeployments = @($snapshotAfter.deployments | Where-Object { $newIds -contains [string]$_.deploymentId })
        $unexpected = @($newDeployments | Where-Object {
            $property = $_.PSObject.Properties["packageFullName"]
            if ($null -eq $property) { return $true }
            $fullName = [string]$property.Value
            -not ($fullName.StartsWith($identityA + "_") -or $fullName.StartsWith($identityB + "_"))
        })
        if ($unexpected.Count -gt 0) {
            throw "Unexpected Sandbox deployment observed; fail-closed and preserve '$scratch'. No package was unregistered."
        }

        $states = @{
            $identityA = Get-GuestPackageState -Name $identityA
            $identityB = Get-GuestPackageState -Name $identityB
        }
        foreach ($name in @($identityA, $identityB)) {
            if ($states[$name] -eq "Present") {
                $manifestToRemove = if ($name -eq $identityA) { $defaultCopy } else { $override }
                [void](Invoke-WinApp @("unregister", "--manifest", $manifestToRemove, "--output-appx-directory", $layout, "--json", "--on", "sandbox"))
                $removedNames.Add($name)
            }
            elseif ($states[$name] -eq "Ambiguous") {
                throw "Ambiguous Sandbox package state for '$name'; preserve '$scratch' and do not guess at cleanup."
            }
        }
        $remainingA = Get-GuestPackageState -Name $identityA
        $remainingB = Get-GuestPackageState -Name $identityB
        Assert-HostPackageAbsent -Name $identityA
        Assert-HostPackageAbsent -Name $identityB
        if ($remainingA -ne "Absent" -or $remainingB -ne "Absent") {
            throw "Temporary registration cleanup is incomplete (A=$remainingA, B=$remainingB); preserve '$scratch'."
        }
        $snapshotAfterCleanup = Get-SandboxSnapshot
        $candidateDeployments = @($snapshotAfterCleanup.deployments | Where-Object {
            $property = $_.PSObject.Properties["packageFullName"]
            if ($null -eq $property) { return $false }
            $fullName = [string]$property.Value
            $fullName.StartsWith($identityA + "_") -or $fullName.StartsWith($identityB + "_")
        })
        if (@($candidateDeployments | Where-Object { $_.registrationStatus -eq "registered" }).Count -gt 0) {
            throw "Sandbox still reports a candidate registration; preserve '$scratch'."
        }
        $cleanupVerified = $true
        $retained = @($candidateDeployments | Where-Object { $_.retainedLayout })
        if ($retained.Count -gt 0) {
            Write-Output "Package registration removed; Sandbox reports retained temporary layout(s): $($retained.deploymentId -join ', '). No broad guest cleanup attempted."
        }
        elseif ($removedNames.Count -eq 0) {
            Write-Output "PASS: no candidate package registration was created; both identities remain absent on Sandbox and host."
        }
        else {
            Write-Output "PASS: both temporary identities absent from Sandbox and host after exact unregister of $($removedNames -join ', ')."
        }
    }

    if ($cleanupVerified) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolvedScratch = [IO.Path]::GetFullPath($scratch)
        if ($resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
        }
    }
}

if ($stateA -ne "Absent" -or $stateB -ne "Present") { throw "Manifest override proof failed; temporary registrations were cleaned where exact identity was confirmed." }
