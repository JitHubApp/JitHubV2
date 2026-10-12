import { execFile } from "node:child_process";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

// This pure matcher can be exercised with synthetic command lines. It parses
// Windows argv and never queries or terminates a process itself.
export const edgeProfileArgumentMatcherScript = String.raw`
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Management
if (-not ('Jithub.ReadmeAudit.CommandLineNativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Jithub.ReadmeAudit {
    public static class CommandLineNativeMethods {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);
        [DllImport("kernel32.dll", SetLastError = false)]
        public static extern IntPtr LocalFree(IntPtr memory);
    }
}
'@
}

function Test-BalancedAuditCommandLineQuotes([string]$CommandLine) {
    $insideQuotes = $false
    $index = 0
    while ($index -lt $CommandLine.Length) {
        if ($CommandLine[$index] -eq [char]92) {
            $slashStart = $index
            while ($index -lt $CommandLine.Length -and $CommandLine[$index] -eq [char]92) { $index++ }
            $slashCount = $index - $slashStart
            if ($index -lt $CommandLine.Length -and $CommandLine[$index] -eq [char]34) {
                if (($slashCount % 2) -eq 0) { $insideQuotes = -not $insideQuotes }
                $index++
            }
            continue
        }
        if ($CommandLine[$index] -eq [char]34) { $insideQuotes = -not $insideQuotes }
        $index++
    }
    return -not $insideQuotes
}

function Test-AuditFullyQualifiedWindowsPath([string]$CandidatePath) {
    try {
        $root = [IO.Path]::GetPathRoot($CandidatePath)
        if ([string]::IsNullOrEmpty($root)) { return $false }
        if ([regex]::IsMatch($root, '^[A-Za-z]:[\\/]$')) { return $true }
        return $root.StartsWith('\\', [StringComparison]::Ordinal) -and $root.Length -gt 2
    }
    catch { return $false }
}

function Get-AuditProfilePathRelationship([string]$CandidatePath, [string]$ExpectedProfile) {
    if ([string]::Equals($CandidatePath, $ExpectedProfile, [StringComparison]::Ordinal)) { return 'Exact' }
    try {
        $candidateFullPath = [IO.Path]::GetFullPath($CandidatePath)
        $expectedFullPath = [IO.Path]::GetFullPath($ExpectedProfile)
        if ([string]::Equals($candidateFullPath, $expectedFullPath, [StringComparison]::OrdinalIgnoreCase)) {
            return 'Equivalent'
        }
        if (-not (Test-AuditFullyQualifiedWindowsPath $CandidatePath) -and
            [string]::Equals([IO.Path]::GetFileName($candidateFullPath), [IO.Path]::GetFileName($expectedFullPath), [StringComparison]::OrdinalIgnoreCase)) {
            return 'PotentialRelative'
        }
        return 'Different'
    }
    catch { return 'Unknown' }
}

function Get-AuditProfileArgumentOwnership([string]$CommandLine, [string]$ExpectedProfile) {
    if ([string]::IsNullOrWhiteSpace($CommandLine) -or [string]::IsNullOrWhiteSpace($ExpectedProfile)) { return 'Unknown' }
    if (-not (Test-BalancedAuditCommandLineQuotes $CommandLine)) { return 'Unknown' }
    $argumentCount = 0
    $argumentVector = [Jithub.ReadmeAudit.CommandLineNativeMethods]::CommandLineToArgvW($CommandLine, [ref]$argumentCount)
    if ($argumentVector -eq [IntPtr]::Zero) { return 'Unknown' }
    try {
        if ($argumentCount -lt 1 -or $argumentCount -gt 32767) { return 'Unknown' }
        $recognizedProfileSwitches = 0
        $containsExpectedProfile = $false
        $containsUncertainProfilePath = $false
        $canonicalSingleExpectedProfile = $false
        for ($index = 1; $index -lt $argumentCount; $index++) {
            $argumentPointer = [Runtime.InteropServices.Marshal]::ReadIntPtr($argumentVector, $index * [IntPtr]::Size)
            if ($argumentPointer -eq [IntPtr]::Zero) { return 'Unknown' }
            $argument = [Runtime.InteropServices.Marshal]::PtrToStringUni($argumentPointer)
            if ($argument.Trim() -cne $argument) { return 'Unknown' }
            if ($argument -ceq '--') { break }

            $prefixLength = 0
            while ($prefixLength -lt $argument.Length -and
                ($argument[$prefixLength] -eq [char]45 -or $argument[$prefixLength] -eq [char]47)) {
                $prefixLength++
            }
            if ($prefixLength -eq 0 -or $prefixLength -eq $argument.Length) { continue }
            $switchText = $argument.Substring($prefixLength)
            $equalsIndex = $switchText.IndexOf('=')
            $switchKey = if ($equalsIndex -ge 0) { $switchText.Substring(0, $equalsIndex) } else { $switchText }
            if ([string]::Equals($switchKey, 'single-argument', [StringComparison]::OrdinalIgnoreCase)) { return 'Unknown' }
            if ([string]::Equals($switchKey, 'user-data-dir', [StringComparison]::OrdinalIgnoreCase)) {
                $recognizedProfileSwitches++
                if ($equalsIndex -lt 0) { continue }
                $profileValue = $switchText.Substring($equalsIndex + 1)
                $pathRelationship = Get-AuditProfilePathRelationship $profileValue $ExpectedProfile
                if ($pathRelationship -eq 'Exact') {
                    $containsExpectedProfile = $true
                    if ($argument.StartsWith('--user-data-dir=', [StringComparison]::Ordinal) -and
                        $recognizedProfileSwitches -eq 1) {
                        $canonicalSingleExpectedProfile = $true
                    }
                    else { $containsUncertainProfilePath = $true }
                }
                elseif ($pathRelationship -in @('Equivalent', 'PotentialRelative', 'Unknown')) {
                    $containsUncertainProfilePath = $true
                }
            }
        }
        if ($containsUncertainProfilePath -or ($containsExpectedProfile -and $recognizedProfileSwitches -ne 1)) { return 'Unknown' }
        if ($canonicalSingleExpectedProfile -and $recognizedProfileSwitches -eq 1) { return 'Owner' }
        return 'NotOwner'
    }
    finally {
        [void][Jithub.ReadmeAudit.CommandLineNativeMethods]::LocalFree($argumentVector)
    }
}

function Test-ExactAuditProfileArgument([string]$CommandLine, [string]$ExpectedProfile) {
    return (Get-AuditProfileArgumentOwnership $CommandLine $ExpectedProfile) -eq 'Owner'
}

function Get-AuditProfileProcessRows(
    [int[]]$ObservedProcessIds,
    [object[]]$ProcessRows,
    [int[]]$RecheckedProcessIds,
    [string]$ExpectedProfile) {
    $cimProcessIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($row in $ProcessRows) {
        if ($null -eq $row) { throw 'Audit cleanup could not inspect an Edge process identity.' }
        $processIdProperty = $row.PSObject.Properties['ProcessId']
        $cimProcessId = 0
        if ($null -eq $processIdProperty -or
            -not [int]::TryParse([string]$processIdProperty.Value, [ref]$cimProcessId) -or
            $cimProcessId -le 0 -or -not $cimProcessIds.Add($cimProcessId)) {
            throw 'Audit cleanup could not reconcile the Edge process inventory.'
        }
    }
    $recheckedIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($processId in $RecheckedProcessIds) {
        if ($processId -le 0) { throw 'Audit cleanup could not reconcile the Edge process inventory.' }
        [void]$recheckedIds.Add($processId)
        if (-not $cimProcessIds.Contains($processId)) {
            throw 'Audit cleanup could not confirm an omitted live Edge process command line.'
        }
    }
    foreach ($processId in $ObservedProcessIds) {
        if ($processId -le 0) { throw 'Audit cleanup could not reconcile the Edge process inventory.' }
        if (-not $cimProcessIds.Contains($processId) -and $recheckedIds.Contains($processId)) {
            throw 'Audit cleanup could not confirm an omitted live Edge process command line.'
        }
    }
    $ownedRows = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $ProcessRows) {
        $processIdProperty = $row.PSObject.Properties['ProcessId']
        $cimProcessId = 0
        if ($null -eq $processIdProperty -or
            -not [int]::TryParse([string]$processIdProperty.Value, [ref]$cimProcessId) -or
            $cimProcessId -le 0) {
            throw 'Audit cleanup could not reconcile the Edge process inventory.'
        }
        if (-not $recheckedIds.Contains($cimProcessId)) { continue }

        $commandLineProperty = $row.PSObject.Properties['CommandLine']
        if ($null -eq $commandLineProperty -or
            -not ($commandLineProperty.Value -is [string]) -or
            [string]::IsNullOrWhiteSpace($commandLineProperty.Value)) {
            throw 'Audit cleanup could not inspect an Edge process command line.'
        }
        $ownership = Get-AuditProfileArgumentOwnership $commandLineProperty.Value $ExpectedProfile
        if ($ownership -eq 'Unknown') {
            throw 'Audit cleanup could not establish whether an Edge process owns the audit profile.'
        }
        if ($ownership -eq 'Owner') {
            $ownedRows.Add($row)
        }
    }
    return $ownedRows.ToArray()
}

function ConvertTo-AuditUtcStartTime([object]$Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetimeoffset]) { return $Value.UtcDateTime }
    if ($Value -is [datetime]) {
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { return $null }
        return $Value.ToUniversalTime()
    }
    if ($Value -is [string] -and $Value -cmatch '^[0-9]{14}\.[0-9]{6}[+-][0-9]{3}$') {
        try {
            # Preserve the authored offset, not an ambiguous local wall clock.
            # ManagementDateTimeConverter returns Unspecified on .NET Framework
            # and converts through the local zone; repeated DST hours can then
            # alias different process start instants. DMTF offsets are minutes.
            $parsed = [DateTime]::ParseExact($Value.Substring(0, 21),
                'yyyyMMddHHmmss.ffffff', [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None)
            $offsetMinutes = [int]::Parse($Value.Substring(21),
                [Globalization.NumberStyles]::AllowLeadingSign,
                [Globalization.CultureInfo]::InvariantCulture)
            return [DateTime]::SpecifyKind($parsed.AddMinutes(-$offsetMinutes), [DateTimeKind]::Utc)
        }
        catch { return $null }
    }
    return $null
}

function Test-ExactAuditProcessIdentity(
    [int]$SnapshotProcessId,
    [int]$CandidateProcessId,
    [string]$ProcessName,
    [object]$CimCreationDate,
    [datetime]$ProcessStartTimeUtc,
    [bool]$HasHandle) {
    if (-not $HasHandle -or $SnapshotProcessId -le 0 -or $CandidateProcessId -ne $SnapshotProcessId -or
        -not [string]::Equals($ProcessName, 'msedge', [StringComparison]::OrdinalIgnoreCase) -or
        $ProcessStartTimeUtc -eq [datetime]::MinValue -or
        $ProcessStartTimeUtc.Kind -ne [DateTimeKind]::Utc) { return $false }
    $cimStartUtc = ConvertTo-AuditUtcStartTime $CimCreationDate
    if ($null -eq $cimStartUtc) { return $false }
    $processStartUtc = $ProcessStartTimeUtc
    $cimMicrosecondTicks = $cimStartUtc.Ticks - ($cimStartUtc.Ticks % 10)
    $processMicrosecondTicks = $processStartUtc.Ticks - ($processStartUtc.Ticks % 10)
    return $cimMicrosecondTicks -eq $processMicrosecondTicks
}
`;

// Edge may hand off startup to another process and let the spawned PID exit
// normally. Browser.close and child.kill cannot then account for the browser
// processes that still own this oracle's unique temporary profile. Select only
// msedge.exe processes whose parsed argv contains one exact profile switch.
const stopProfileScript = String.raw`
$ErrorActionPreference = 'Stop'
$auditEdgeProfilePath = $env:JITHUB_README_EDGE_PROFILE_TO_STOP
if ([string]::IsNullOrWhiteSpace($auditEdgeProfilePath)) { exit 2 }
${edgeProfileArgumentMatcherScript}
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$sawEdgeProcess = $false
function Get-AuditEdgeProcessIds {
    $edgeQueryErrors = @()
    $processes = @(Get-Process -Name msedge -ErrorAction SilentlyContinue -ErrorVariable +edgeQueryErrors)
    if (@($edgeQueryErrors | Where-Object {
        $_.FullyQualifiedErrorId -ne 'NoProcessFoundForGivenName,Microsoft.PowerShell.Commands.GetProcessCommand'
    }).Count -gt 0) {
        throw 'Audit cleanup could not confirm whether Edge processes remain.'
    }
    $processIds = [System.Collections.Generic.List[int]]::new()
    foreach ($process in $processes) {
        try {
            $processId = [int]$process.Id
            if ($processId -le 0) { throw 'Invalid Edge process identifier.' }
            $processIds.Add($processId)
        }
        finally { $process.Dispose() }
    }
    return $processIds.ToArray()
}
for ($pass = 0; $pass -lt 3; $pass++) {
    # No Edge process can own this profile. On busy CI hosts even an empty
    # Win32_Process CIM query may spend many seconds initializing WMI.
    $observedProcessIds = @(Get-AuditEdgeProcessIds)
    if ($observedProcessIds.Count -eq 0) { $sawEdgeProcess = $false; break }
    $sawEdgeProcess = $true
    $processRows = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'")
    $recheckedProcessIds = @(Get-AuditEdgeProcessIds)
    $targets = @(Get-AuditProfileProcessRows -ObservedProcessIds $observedProcessIds -ProcessRows $processRows -RecheckedProcessIds $recheckedProcessIds -ExpectedProfile $auditEdgeProfilePath)
    if ($targets.Count -eq 0) { break }
    foreach ($target in $targets) {
        $processId = 0
        if (-not [int]::TryParse([string]$target.ProcessId, [ref]$processId) -or $processId -le 0) {
            throw 'Audit cleanup encountered a profile-owning process without a valid process identifier.'
        }
        $candidateProcess = $null
        try { $candidateProcess = [Diagnostics.Process]::GetProcessById($processId) }
        catch [ArgumentException] { continue }
        catch { throw 'Audit cleanup could not safely reacquire a profile-owning Edge process.' }
        try {
            # Open and retain this process object's handle before checking the
            # WMI creation timestamp. Kill() then targets this same OS object,
            # never a process that later reuses the numeric PID.
            $heldHandle = $candidateProcess.Handle
            if ($heldHandle -eq [IntPtr]::Zero) {
                throw 'Audit cleanup could not hold the candidate Edge process handle.'
            }
            $processStartUtc = $candidateProcess.StartTime.ToUniversalTime()
            if (-not (Test-ExactAuditProcessIdentity $processId $candidateProcess.Id $candidateProcess.ProcessName $target.CreationDate $processStartUtc ($heldHandle -ne [IntPtr]::Zero))) {
                throw 'Audit cleanup could not verify the candidate Edge process identity.'
            }
            $candidateProcess.Kill()
            if (-not $candidateProcess.WaitForExit(1000) -and -not $candidateProcess.HasExited) {
                throw 'Audit cleanup did not observe the owned Edge process exit.'
            }
        }
        catch {
            # A process that exits between revalidation and Kill is already
            # gone; all other access, identity, and termination errors fail
            # closed and will be checked against a fresh CIM snapshot below.
            try {
                if (-not $candidateProcess.HasExited) { throw }
            }
            catch {
                $cleanupFailures.Add("Could not safely terminate candidate PID $processId.")
            }
        }
        finally {
            $candidateProcess.Dispose()
        }
    }
    Start-Sleep -Milliseconds 100
}
if ($sawEdgeProcess) {
    $remainingObservedProcessIds = @(Get-AuditEdgeProcessIds)
    if ($remainingObservedProcessIds.Count -gt 0) {
        $remainingRows = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'")
        $remainingRecheckedProcessIds = @(Get-AuditEdgeProcessIds)
        $remainingTargets = @(Get-AuditProfileProcessRows -ObservedProcessIds $remainingObservedProcessIds -ProcessRows $remainingRows -RecheckedProcessIds $remainingRecheckedProcessIds -ExpectedProfile $auditEdgeProfilePath)
        if ($remainingTargets.Count -gt 0) {
            throw "Audit cleanup left $($remainingTargets.Count) profile-owning Edge process(es); safely verified termination failures: $($cleanupFailures.Count)."
        }
    }
}
`;

export async function stopBrowserProfileProcesses(profileDirectory, {
  platform = process.platform,
  temporaryDirectory = os.tmpdir(),
  execute = execFileAsync,
  environment = process.env,
} = {}) {
  const resolved = path.resolve(profileDirectory);
  const expectedParent = path.resolve(temporaryDirectory);
  if (path.dirname(resolved) !== expectedParent ||
      !/^jithub-readme-edge-[a-zA-Z0-9]{6}$/u.test(path.basename(resolved))) {
    throw new Error("Refusing to stop Edge processes outside one generated audit profile.");
  }
  if (platform !== "win32") return;
  await execute("powershell.exe", [
    "-NoProfile",
    "-NonInteractive",
    "-Command",
    stopProfileScript,
  ], {
    windowsHide: true,
    timeout: 30_000,
    maxBuffer: 64 * 1024,
    env: {
      ...environment,
      JITHUB_README_EDGE_PROFILE_TO_STOP: resolved,
    },
  });
}
