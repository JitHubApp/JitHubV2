[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Inspect','Prepare','Restore')][string]$Mode,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedGuestComputerName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedHostComputerName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RunDirectory,
    [string]$GuestWorkRoot = 'C:\WinApp\work',
    [ValidateRange(1073741824,1099511627776)][long]$MinimumFreeBytes = 8589934592,
    [switch]$RunApprovedSandboxDiagnostic
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This adapter is deliberately separate from app launch/input. The host must pin
# the live Sandbox epoch, call Prepare only after approval, and call Restore in
# its own finally. A full dump is sensitive process memory: never upload/commit it.
function Assert-MarkdownCrashDumpGuestContext {
    param(
        [Parameter(Mandatory)][string]$ExpectedGuestComputerName,
        [Parameter(Mandatory)][string]$ExpectedHostComputerName,
        [Parameter(Mandatory)][string]$ComputerName,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][bool]$IsAdministrator,
        [Parameter(Mandatory)][bool]$Is64BitProcess,
        [Parameter(Mandatory)][bool]$Is64BitOperatingSystem,
        [Parameter(Mandatory)][string]$GuestWorkRoot
    )
    $comparison = [StringComparison]::OrdinalIgnoreCase
    if ([string]::IsNullOrWhiteSpace($ExpectedGuestComputerName) -or
        [string]::IsNullOrWhiteSpace($ExpectedHostComputerName) -or
        [string]::Equals($ExpectedGuestComputerName,$ExpectedHostComputerName,$comparison) -or
        [string]::Equals($ComputerName,$ExpectedHostComputerName,$comparison) -or
        -not [string]::Equals($ComputerName,$ExpectedGuestComputerName,$comparison)) {
        throw 'Crash-dump setup must run in the independently identified Sandbox, never the host or an ambiguous machine.'
    }
    if ($UserName -ine 'WDAGUtilityAccount' -or -not $IsAdministrator -or
        -not $Is64BitProcess -or -not $Is64BitOperatingSystem) {
        throw 'This diagnostic requires the approved x64 Sandbox administrator context; no elevation or alternate registry hive is attempted.'
    }
    if (-not [string]::Equals([IO.Path]::GetFullPath($GuestWorkRoot).TrimEnd('\'),'C:\WinApp\work',$comparison)) {
        throw 'Crash-dump setup is restricted to the Sandbox managed C:\WinApp\work root.'
    }
}

function Assert-MarkdownCrashDumpFileLocation {
    param([Parameter(Mandatory)][string]$GuestWorkRoot,[Parameter(Mandatory)][string]$RunDirectory)
    if ($RunDirectory -notmatch '^[A-Za-z]:\\' -or $RunDirectory.IndexOf(':',2) -ge 0 -or
        $RunDirectory -match '(^|[\\/])\.\.?([\\/]|$)' -or $RunDirectory.Contains('%')) {
        throw 'RunDirectory must be an absolute local path without traversal, streams or environment expansion.'
    }
    $root = [IO.Path]::GetFullPath($GuestWorkRoot).TrimEnd('\')
    $run = [IO.Path]::GetFullPath($RunDirectory).TrimEnd('\')
    if (-not $run.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'RunDirectory must be strictly below the verified guest work root.'
    }
    $cursor = $run
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A diagnostic directory component is not an ordinary directory: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'The verified Sandbox work root does not exist.' }
    return $run
}

function ConvertTo-MarkdownCrashDumpRegistryData {
    param([Parameter(Mandatory)][string]$Kind,[AllowNull()]$Data)
    switch -Exact ($Kind) {
        'String' { if ($Data -isnot [string]) { throw 'Registry String data must be a string.' }; return $Data }
        'ExpandString' { if ($Data -isnot [string]) { throw 'Registry ExpandString data must remain unexpanded text.' }; return $Data }
        'DWord' {
            if (($Data -isnot [int] -and $Data -isnot [long]) -or $Data -lt [int]::MinValue -or $Data -gt [int]::MaxValue) { throw 'Registry DWord data must retain its signed 32-bit raw representation.' }
            return [int]$Data
        }
        'QWord' {
            if ($Data -isnot [int] -and $Data -isnot [long]) { throw 'Registry QWord data must retain its signed 64-bit raw representation.' }
            return [long]$Data
        }
        'MultiString' {
            if ($null -eq $Data -or $Data -is [string] -or $Data -isnot [array]) { throw 'Registry MultiString data must be an explicit string array.' }
            foreach ($entry in $Data) { if ($entry -isnot [string]) { throw 'Registry MultiString contains a non-string value.' } }
            return ,([string[]]$Data)
        }
        'Binary' {
            if ($null -eq $Data -or $Data -isnot [array]) { throw 'Registry Binary data must be an explicit byte array.' }
            foreach ($entry in $Data) { if (($entry -isnot [byte] -and $entry -isnot [int] -and $entry -isnot [long]) -or $entry -lt 0 -or $entry -gt 255) { throw 'Registry Binary contains invalid byte data.' } }
            return ,([byte[]]$Data)
        }
        'None' {
            if ($null -eq $Data -or $Data -isnot [array]) { throw 'Registry None data must be an explicit byte array.' }
            foreach ($entry in $Data) { if (($entry -isnot [byte] -and $entry -isnot [int] -and $entry -isnot [long]) -or $entry -lt 0 -or $entry -gt 255) { throw 'Registry None contains invalid byte data.' } }
            return ,([byte[]]$Data)
        }
        default { throw "Unsupported registry value kind '$Kind'; refusing a lossy restore." }
    }
}

function Resolve-MarkdownCrashDumpRegistrySubKey {
    param([Parameter(Mandatory)][string]$Path)
    $target = Get-MarkdownSandboxCrashDumpTarget
    if ([string]::Equals($Path,$target.BaseKey,[StringComparison]::OrdinalIgnoreCase)) { return $target.BaseSubKey }
    if ([string]::Equals($Path,$target.AppKey,[StringComparison]::OrdinalIgnoreCase)) { return $target.AppSubKey }
    throw 'The diagnostic registry adapter accepts only its fixed LocalDumps base and sample-specific key.'
}

function Read-MarkdownCrashDumpRegistryKey {
    param([Parameter(Mandatory)][string]$Path)
    $subKey = Resolve-MarkdownCrashDumpRegistrySubKey $Path
    $key = $script:CrashDumpRegistryRoot.OpenSubKey($subKey,$false)
    if ($null -eq $key) { return [pscustomobject]@{Exists=$false;Values=@();SubKeys=@()} }
    try {
        $values = @(foreach ($name in $key.GetValueNames()) {
            [pscustomobject]@{Name=$name;Kind=$key.GetValueKind($name).ToString();Data=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
        })
        return [pscustomobject]@{Exists=$true;Values=$values;SubKeys=@($key.GetSubKeyNames())}
    }
    finally { $key.Dispose() }
}

function Invoke-MarkdownCrashDumpRegistryAction {
    param([Parameter(Mandatory)]$Action)
    $target = Get-MarkdownSandboxCrashDumpTarget
    $subKey = Resolve-MarkdownCrashDumpRegistrySubKey ([string]$Action.Path)
    Assert-MarkdownSandboxCrashDumpRestoreAction -Action $Action
    if ([string]$Action.Operation -in @('SetValue','RemoveValue')) {
        $currentKey = Read-MarkdownCrashDumpRegistryKey -Path ([string]$Action.Path)
        $currentValue = Get-MarkdownSandboxCrashDumpNamedValue -KeyState $currentKey -Name ([string]$Action.Name)
        if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $currentValue -Right $Action.ExpectedCurrent)) {
            throw "Concurrent modification changed '$($Action.Name)' immediately before its write; no action was applied."
        }
    }
    switch -Exact ([string]$Action.Operation) {
        'SetValue' {
            if ($subKey -cne $target.AppSubKey -or [string]$Action.Name -cnotin @('DumpFolder','DumpCount','DumpType')) { throw 'Only the three fixed per-executable dump values may be written.' }
            $data = ConvertTo-MarkdownCrashDumpRegistryData -Kind ([string]$Action.Kind) -Data $Action.Data
            $kind = [Microsoft.Win32.RegistryValueKind]([Enum]::Parse([Microsoft.Win32.RegistryValueKind],[string]$Action.Kind,$false))
            $key = $script:CrashDumpRegistryRoot.CreateSubKey($subKey,$true)
            try { $key.SetValue([string]$Action.Name,$data,$kind); $key.Flush() }
            finally { $key.Dispose() }
        }
        'RemoveValue' {
            if ($subKey -cne $target.AppSubKey -or [string]$Action.Name -cnotin @('DumpFolder','DumpCount','DumpType')) { throw 'Only the three fixed per-executable dump values may be removed.' }
            $key = $script:CrashDumpRegistryRoot.OpenSubKey($subKey,$true)
            if ($null -ne $key) { try { $key.DeleteValue([string]$Action.Name,$false); $key.Flush() } finally { $key.Dispose() } }
        }
        'RemoveKeyIfEmpty' {
            # RegistryKey offers no atomic delete-if-empty operation. Checking
            # then deleting could destroy a concurrent writer's new values.
            # Keep inert keys and report the exact residue instead; never claim
            # their original absence was restored. No key deletion is attempted.
            return
        }
        default { throw "Unknown crash-dump registry action '$($Action.Operation)'." }
    }
}

function Invoke-MarkdownCrashDumpRegistryPlan {
    param([Parameter(Mandatory)]$Plan,[Parameter(Mandatory)]$Snapshot,[Parameter(Mandatory)]$SetupPlan,[Parameter(Mandatory)][scriptblock]$ReadKey)
    foreach ($action in $Plan.Actions) {
        # Revalidate all managed names between writes, not just when the plan
        # was generated. Partial setup/restore states are explicitly admitted.
        $null = New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $Snapshot -SetupPlan $SetupPlan -ReadKey $ReadKey
        Invoke-MarkdownCrashDumpRegistryAction -Action $action
    }
}

function Assert-MarkdownCrashDumpManagedValues {
    param([Parameter(Mandatory)]$Expected,[Parameter(Mandatory)][scriptblock]$ReadKey)
    $current = Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $ReadKey
    foreach ($name in (Get-MarkdownSandboxCrashDumpTarget).ManagedNames) {
        if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $current.Values[$name] -Right $Expected.Values[$name])) {
            throw "Diagnostic registry verification failed for '$name'."
        }
    }
    return $current
}

function Get-MarkdownCrashDumpRestoreVerification {
    param([Parameter(Mandatory)]$Snapshot,[Parameter(Mandatory)][scriptblock]$ReadKey)
    $current = Assert-MarkdownCrashDumpManagedValues -Expected $Snapshot -ReadKey $ReadKey
    $target = Get-MarkdownSandboxCrashDumpTarget
    $retained = @()
    $inert = @()
    foreach ($entry in @(
        @{Path=$target.AppKey;Before=[bool]$Snapshot.AppKeyExists;Now=[bool]$current.AppKeyExists},
        @{Path=$target.BaseKey;Before=[bool]$Snapshot.BaseKeyExists;Now=[bool]$current.BaseKeyExists}
    )) {
        if ($entry.Before -and -not $entry.Now) { throw 'Restore removed a key that existed before diagnostics.' }
        if (-not $entry.Before -and $entry.Now) {
            $key = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $entry.Path
            $onlyInertAppChild = $entry.Path -ceq $target.BaseKey -and @($key.Values).Count -eq 0 -and
                @($key.SubKeys).Count -eq 1 -and [string]$key.SubKeys[0] -ceq 'MarkdownRenderer.Sample.exe' -and $inert -ccontains $target.AppKey
            if ((@($key.Values).Count -eq 0 -and @($key.SubKeys).Count -eq 0) -or $onlyInertAppChild) { $inert += [string]$entry.Path }
            $retained += [string]$entry.Path
        }
    }
    return [pscustomobject]@{managedValuesRestored=$true;exactKeyExistenceRestored=($retained.Count -eq 0);inertKeysRetained=@($inert);retainedKeyOriginNotAttributed=$true;foreignStateKeysRetained=@($retained|Where-Object{$inert -cnotcontains $_});current=$current}
}

function Write-MarkdownCrashDumpReceipt {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)]$Value)
    $bytes = ([Text.UTF8Encoding]::new($false,$true)).GetBytes((ConvertTo-Json -InputObject $Value -Depth 24) + [Environment]::NewLine)
    # CreateNew refuses existing files, symlinks and hardlinks. Diagnostic
    # receipts never overwrite another run's evidence or a foreign target.
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function Read-MarkdownCrashDumpReceipt {
    param([Parameter(Mandatory)][string]$Path)
    # Windows PowerShell 5.1 otherwise defaults to ANSI for a BOM-less file.
    return ([IO.File]::ReadAllText($Path,[Text.UTF8Encoding]::new($false,$true)) | ConvertFrom-Json)
}

if ($Mode -ne 'Inspect' -and -not $RunApprovedSandboxDiagnostic) {
    throw 'Prepare/Restore require explicit -RunApprovedSandboxDiagnostic; no diagnostic settings have been changed.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $isAdministrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
Assert-MarkdownCrashDumpGuestContext -ExpectedGuestComputerName $ExpectedGuestComputerName -ExpectedHostComputerName $ExpectedHostComputerName `
    -ComputerName ([Environment]::MachineName) -UserName ([Environment]::UserName) -IsAdministrator $isAdministrator `
    -Is64BitProcess ([Environment]::Is64BitProcess) -Is64BitOperatingSystem ([Environment]::Is64BitOperatingSystem) -GuestWorkRoot $GuestWorkRoot
$run = Assert-MarkdownCrashDumpFileLocation -GuestWorkRoot $GuestWorkRoot -RunDirectory $RunDirectory
. (Join-Path $PSScriptRoot 'MarkdownSandboxCrashDumpPolicy.ps1')
$snapshotPath = Join-Path $run 'registry-before.json'
$preparedPath = Join-Path $run 'registry-prepared.json'
$restorePath = Join-Path $run 'registry-restored.json'
$dumpFolder = Join-Path $run 'Dumps'
$script:CrashDumpRegistryRoot = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
try {
    $readKey = { param($path) Read-MarkdownCrashDumpRegistryKey -Path $path }
    if ($Mode -eq 'Inspect') {
        [ordered]@{mode=$Mode;computer=[Environment]::MachineName;readOnly=$true;snapshot=(Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $readKey)} | ConvertTo-Json -Depth 24
        return
    }
    if ($Mode -eq 'Prepare') {
        if (Test-Path -LiteralPath $run) { throw 'Prepare requires a fresh, absent run directory; existing evidence must not be overwritten.' }
        if (@([Diagnostics.Process]::GetProcessesByName('MarkdownRenderer.Sample')).Count -ne 0) { throw 'Another sample process is live; do not change shared per-executable dump settings.' }
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($run))
        if ($drive.AvailableFreeSpace -lt $MinimumFreeBytes) { throw "Insufficient guest disk space for a full dump (requires $MinimumFreeBytes free bytes)." }
        $validatedDumpFolder = Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot $GuestWorkRoot -DumpFolder $dumpFolder -PathExists {param($path) Test-Path -LiteralPath $path}
        $snapshot = Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $readKey
        $setup = New-MarkdownSandboxCrashDumpSetupPlan -Snapshot $snapshot -GuestWorkRoot $GuestWorkRoot -DumpFolder $dumpFolder -PathExists {param($path) Test-Path -LiteralPath $path}
        [IO.Directory]::CreateDirectory($dumpFolder) | Out-Null
        # Snapshot is durably written before the first registry mutation, so a
        # canceled host runner still leaves a precise recovery receipt.
        $policyJson = ConvertTo-MarkdownSandboxCrashDumpReceiptJson -Snapshot $snapshot -SetupPlan $setup
        Write-MarkdownCrashDumpReceipt -Path $snapshotPath -Value ([ordered]@{schemaVersion=1;computer=[Environment]::MachineName;runDirectory=$run;dumpFolder=$dumpFolder;policyJson=$policyJson})
        try {
            Invoke-MarkdownCrashDumpRegistryPlan -Plan $setup -Snapshot $snapshot -SetupPlan $setup -ReadKey $readKey
            $actual = Assert-MarkdownCrashDumpManagedValues -Expected $setup -ReadKey $readKey
            $receipt = [ordered]@{schemaVersion=1;status='PREPARED_NOT_A_TEST_PASS';computer=[Environment]::MachineName;dumpFolder=$dumpFolder;snapshotPath=$snapshotPath;current=$actual;appLaunched=$false;requiresHostFinallyRestore=$true}
            Write-MarkdownCrashDumpReceipt -Path $preparedPath -Value $receipt
            $receipt | ConvertTo-Json -Depth 24
        }
        catch {
            $setupError = $_.Exception.ToString()
            try {
                $rollback = New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $snapshot -SetupPlan $setup -ReadKey $readKey
                Invoke-MarkdownCrashDumpRegistryPlan -Plan $rollback -Snapshot $snapshot -SetupPlan $setup -ReadKey $readKey
                $rollbackState = Get-MarkdownCrashDumpRestoreVerification -Snapshot $snapshot -ReadKey $readKey
                Write-MarkdownCrashDumpReceipt -Path (Join-Path $run 'prepare-failed.json') -Value ([ordered]@{setupError=$setupError;rollbackApplied=$true;verification=$rollbackState})
            }
            catch { throw "Dump setup failed: $setupError Rollback could not be confirmed; keep Sandbox open and use $snapshotPath. $($_.Exception)" }
            throw "Dump setup failed and rollback was applied; inspect $run. $setupError"
        }
        return
    }
    if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf)) { throw 'Restore requires the exact prior-state receipt in this run directory.' }
    if (((Get-Item -LiteralPath $snapshotPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The prior-state receipt may not be a reparse point.' }
    if (Test-Path -LiteralPath $restorePath) { throw 'A restore receipt already exists; preserve it and inspect the current state instead of overwriting evidence.' }
    $saved = Read-MarkdownCrashDumpReceipt -Path $snapshotPath
    if ($saved.schemaVersion -ne 1 -or [string]$saved.computer -cne [Environment]::MachineName -or
        -not [string]::Equals([string]$saved.runDirectory,$run,[StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$saved.dumpFolder,$dumpFolder,[StringComparison]::OrdinalIgnoreCase)) { throw 'The prior-state receipt does not belong to this exact guest run.' }
    $policy = ConvertFrom-MarkdownSandboxCrashDumpReceiptJson -Json ([string]$saved.policyJson)
    if (-not [string]::Equals([string]$policy.SetupPlan.DumpFolder,$dumpFolder,[StringComparison]::OrdinalIgnoreCase)) { throw 'The saved policy has a different dump location.' }
    $restore = New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $policy.Snapshot -SetupPlan $policy.SetupPlan -ReadKey $readKey
    Invoke-MarkdownCrashDumpRegistryPlan -Plan $restore -Snapshot $policy.Snapshot -SetupPlan $policy.SetupPlan -ReadKey $readKey
    $restored = Get-MarkdownCrashDumpRestoreVerification -Snapshot $policy.Snapshot -ReadKey $readKey
    $receipt = [ordered]@{schemaVersion=1;status='MANAGED_VALUES_RESTORED';computer=[Environment]::MachineName;verification=$restored;snapshotPath=$snapshotPath;dumpEvidenceRetained=$true}
    Write-MarkdownCrashDumpReceipt -Path $restorePath -Value $receipt
    $receipt | ConvertTo-Json -Depth 24
}
finally { $script:CrashDumpRegistryRoot.Dispose() }
