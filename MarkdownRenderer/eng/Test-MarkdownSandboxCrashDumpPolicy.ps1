[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MarkdownSandboxCrashDumpPolicy.ps1')

$script:PolicyTestResults = New-Object 'System.Collections.Generic.List[object]'

function Assert-PolicyTrue {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-PolicyEqual {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if (-not [object]::Equals($Expected, $Actual)) {
        throw "$Message (expected '$Expected', actual '$Actual')"
    }
}

function Assert-PolicyThrows {
    param([scriptblock]$Action, [string]$MessageFragment)
    $caught = $null
    try { & $Action } catch { $caught = $_ }
    if ($null -eq $caught) { throw 'Expected policy operation to throw, but it succeeded.' }
    if ($MessageFragment -and -not $caught.Exception.Message.Contains($MessageFragment)) {
        throw "Expected error containing '$MessageFragment'; received '$($caught.Exception.Message)'."
    }
}

function Invoke-PolicyTest {
    param([string]$Name, [scriptblock]$Test)
    try {
        & $Test
        $script:PolicyTestResults.Add([pscustomobject]@{ Name = $Name; Status = 'PASS'; Detail = $null })
        Write-Host "PASS $Name"
    } catch {
        $script:PolicyTestResults.Add([pscustomobject]@{ Name = $Name; Status = 'FAIL'; Detail = $_.Exception.Message })
        Write-Host "FAIL $Name :: $($_.Exception.Message)"
    }
}

function New-FakeCrashDumpStore {
    return [pscustomobject]@{ Keys = @{} }
}

function Add-FakeCrashDumpKey {
    param([object]$Store, [string]$Path)
    if (-not $Store.Keys.ContainsKey($Path)) {
        $Store.Keys[$Path] = [pscustomobject]@{ Values = @{} }
    }
}

function Set-FakeCrashDumpValue {
    param([object]$Store, [string]$Path, [string]$Name, [string]$Kind, [object]$Data)
    Add-FakeCrashDumpKey -Store $Store -Path $Path
    $Store.Keys[$Path].Values[$Name] = [pscustomobject]@{
        Kind = $Kind
        Data = Copy-MarkdownSandboxCrashDumpPolicyData $Data
    }
}

function Get-FakeCrashDumpKeyState {
    param([object]$Store, [string]$Path)
    if (-not $Store.Keys.ContainsKey($Path)) {
        return [pscustomobject]@{ Exists = $false; Values = @(); SubKeys = @() }
    }
    $values = @()
    foreach ($name in @($Store.Keys[$Path].Values.Keys)) {
        $entry = $Store.Keys[$Path].Values[$name]
        $values += [pscustomobject]@{ Name = [string]$name; Kind = [string]$entry.Kind; Data = Copy-MarkdownSandboxCrashDumpPolicyData $entry.Data }
    }
    $prefix = $Path.TrimEnd('\') + '\'
    $subKeys = @()
    foreach ($candidate in @($Store.Keys.Keys)) {
        if ($candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $tail = $candidate.Substring($prefix.Length)
            $child = ($tail -split '\\', 2)[0]
            if ($subKeys -notcontains $child) { $subKeys += $child }
        }
    }
    return [pscustomobject]@{ Exists = $true; Values = @($values); SubKeys = @($subKeys) }
}

function Get-FakeCrashDumpReader {
    param([object]$Store)
    return ({ param([string]$Path) Get-FakeCrashDumpKeyState -Store $Store -Path $Path }).GetNewClosure()
}

function Get-FakeCrashDumpValue {
    param([object]$Store, [string]$Path, [string]$Name)
    $state = Get-FakeCrashDumpKeyState -Store $Store -Path $Path
    $matches = @($state.Values | Where-Object { [string]::Equals($_.Name, $Name, [StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -eq 0) { return [pscustomobject]@{ Exists = $false; Kind = $null; Data = $null } }
    return [pscustomobject]@{ Exists = $true; Kind = $matches[0].Kind; Data = $matches[0].Data }
}

function Invoke-FakeCrashDumpPlan {
    param(
        [object]$Store,
        [object]$Plan,
        [object]$Snapshot,
        [ValidateSet('Setup', 'Restore')][string]$PlanKind,
        [int]$FailBeforeActionIndex = -1
    )
    $reader = Get-FakeCrashDumpReader -Store $Store
    if ($PlanKind -eq 'Setup') {
        $null = Assert-MarkdownSandboxCrashDumpSetupPlanApplicable -Snapshot $Snapshot -SetupPlan $Plan -ReadKey $reader
    } else {
        $null = Assert-MarkdownSandboxCrashDumpRestorePlanApplicable -Snapshot $Snapshot -RestorePlan $Plan -ReadKey $reader
    }
    $target = Get-MarkdownSandboxCrashDumpTarget
    for ($index = 0; $index -lt $Plan.Actions.Count; $index++) {
        if ($index -eq $FailBeforeActionIndex) { throw "Injected fake-store failure before action $index." }
        $action = $Plan.Actions[$index]
        if ($action.Operation -eq 'SetValue' -or $action.Operation -eq 'RemoveValue') {
            $current = Get-FakeCrashDumpValue -Store $Store -Path $action.Path -Name $action.Name
            if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $current -Right $action.ExpectedCurrent)) {
                throw "Fake adapter rejected concurrent modification of '$($action.Name)'."
            }
        }
        switch -Exact ($action.Operation) {
            'SetValue' {
                Add-FakeCrashDumpKey -Store $Store -Path $target.BaseKey
                Add-FakeCrashDumpKey -Store $Store -Path $target.AppKey
                Set-FakeCrashDumpValue -Store $Store -Path $action.Path -Name $action.Name -Kind $action.Kind -Data $action.Data
            }
            'RemoveValue' {
                if ($Store.Keys.ContainsKey($action.Path)) { $null = $Store.Keys[$action.Path].Values.Remove($action.Name) }
            }
            'RemoveKeyIfEmpty' {
                if ($Store.Keys.ContainsKey($action.Path)) {
                    $state = Get-FakeCrashDumpKeyState -Store $Store -Path $action.Path
                    if ($state.Values.Count -eq 0 -and $state.SubKeys.Count -eq 0) {
                        $null = $Store.Keys.Remove($action.Path)
                    }
                }
            }
            default { throw "Unexpected fake-plan operation '$($action.Operation)'." }
        }
    }
}

function New-FakePolicyInputs {
    param([object]$Store)
    $reader = Get-FakeCrashDumpReader -Store $Store
    $snapshot = Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $reader
    $plan = New-MarkdownSandboxCrashDumpSetupPlan -Snapshot $snapshot `
        -GuestWorkRoot 'C:\WinApp\work' `
        -DumpFolder 'C:\WinApp\work\CrashDumps\run-7a0f' `
        -PathExists { param($Path) return $false }
    return [pscustomobject]@{ Reader = $reader; Snapshot = $snapshot; SetupPlan = $plan }
}

function Assert-FakeRestored {
    param([object]$Snapshot, [object]$Store)
    $status = Get-MarkdownSandboxCrashDumpRestoreStatus -Snapshot $Snapshot -ReadKey (Get-FakeCrashDumpReader -Store $Store)
    if (-not $status.IsRestored) { throw "Expected exact restoration; got: $($status.Failures -join ', ')." }
}

$target = Get-MarkdownSandboxCrashDumpTarget

Invoke-PolicyTest 'target is fixed to the x64 per-app LocalDumps key' {
    Assert-PolicyEqual 'LocalMachine' $target.Hive 'Wrong hive.'
    Assert-PolicyEqual 'Registry64' $target.RegistryView 'Wrong registry view.'
    Assert-PolicyEqual 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps' $target.BaseSubKey 'Wrong base subkey.'
    Assert-PolicyEqual ($target.BaseSubKey + '\MarkdownRenderer.Sample.exe') $target.AppSubKey 'Wrong app subkey.'
    Assert-PolicyEqual 3 $target.ManagedNames.Count 'Unexpected managed value count.'
}

Invoke-PolicyTest 'path validation accepts a fresh strict descendant without filesystem access' {
    $seen = [pscustomobject]@{ Path = $null }
    $path = Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' `
        -DumpFolder 'C:\WinApp\work\CrashDumps\run-7a0f' `
        -PathExists ({ param($candidate) $seen.Path = $candidate; return $false }).GetNewClosure()
    Assert-PolicyEqual 'C:\WinApp\work\CrashDumps\run-7a0f' $path 'Unexpected canonical path.'
    Assert-PolicyEqual $path $seen.Path 'Path predicate did not receive canonical path.'
}

Invoke-PolicyTest 'path validation rejects relative, UNC, drive-relative, and missing paths' {
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'relative\dump' -PathExists { $false } } 'absolute'
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder '\\server\share\dump' -PathExists { $false } } 'absolute'
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:work' -DumpFolder 'C:\WinApp\work\dump' -PathExists { $false } } 'absolute'
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\%TEMP%\dump' -PathExists { $false } } 'unsafe'
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\dump' -PathExists $null } 'PathExists'
}

Invoke-PolicyTest 'path validation rejects equal-root, sibling-prefix, traversal, different-volume, and existing paths' {
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work' -PathExists { $false } } 'descendant'
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work-old\dump' -PathExists { $false } } 'descendant'
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\..\outside' -PathExists { $false } } 'traversal'
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'D:\WinApp\work\dump' -PathExists { $false } } 'volume'
    Assert-PolicyThrows { Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\dump' -PathExists { $true } } 'fresh'
}

Invoke-PolicyTest 'snapshot detects an app key without its required base key' {
    $brokenReader = { param($path) if ($path -eq $target.AppKey) { return [pscustomobject]@{ Exists = $true; Values = @(); SubKeys = @() } }; return [pscustomobject]@{ Exists = $false; Values = @(); SubKeys = @() } }.GetNewClosure()
    Assert-PolicyThrows { Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $brokenReader } 'without its base key'
}

Invoke-PolicyTest 'setup plan contains only the three fixed guarded writes in stable order' {
    $store = New-FakeCrashDumpStore
    $inputs = New-FakePolicyInputs -Store $store
    $plan = $inputs.SetupPlan
    Assert-PolicyEqual 'DumpFolder,DumpCount,DumpType' (($plan.Actions | ForEach-Object { $_.Name }) -join ',') 'Unexpected write order.'
    Assert-PolicyEqual 'ExpandString' $plan.Actions[0].Kind 'DumpFolder must use REG_EXPAND_SZ.'
    Assert-PolicyEqual 'C:\WinApp\work\CrashDumps\run-7a0f' $plan.Actions[0].Data 'Dump folder changed.'
    Assert-PolicyEqual 'DWord' $plan.Actions[1].Kind 'DumpCount must use REG_DWORD.'
    Assert-PolicyEqual 1 $plan.Actions[1].Data 'DumpCount must be one.'
    Assert-PolicyEqual 'DWord' $plan.Actions[2].Kind 'DumpType must use REG_DWORD.'
    Assert-PolicyEqual 2 $plan.Actions[2].Data 'DumpType must request a full dump.'
    foreach ($action in $plan.Actions) {
        Assert-PolicyEqual $target.AppKey $action.Path 'Plan escaped the fixed app key.'
        Assert-PolicyTrue ($null -ne $action.ExpectedCurrent) 'Plan action omitted its old-value guard.'
    }
}

Invoke-PolicyTest 'setup applicability rejects a concurrent change before writing any values' {
    $store = New-FakeCrashDumpStore
    Add-FakeCrashDumpKey -Store $store -Path $target.BaseKey
    Add-FakeCrashDumpKey -Store $store -Path $target.AppKey
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpCount' -Kind 'String' -Data 'prior'
    $inputs = New-FakePolicyInputs -Store $store
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpCount' -Kind 'String' -Data 'concurrent'
    $beforeFolder = Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpFolder'
    Assert-PolicyThrows { Assert-MarkdownSandboxCrashDumpSetupPlanApplicable -Snapshot $inputs.Snapshot -SetupPlan $inputs.SetupPlan -ReadKey (Get-FakeCrashDumpReader -Store $store) } 'changed after'
    $afterFolder = Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpFolder'
    Assert-PolicyTrue (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $beforeFolder -Right $afterFolder) 'Setup modified values despite failed preflight.'
    Assert-PolicyEqual 'concurrent' (Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpCount').Data 'Concurrent value was overwritten.'
}

Invoke-PolicyTest 'partial setup failure rolls back only applied values and removes newly created empty keys' {
    $store = New-FakeCrashDumpStore
    $inputs = New-FakePolicyInputs -Store $store
    try { Invoke-FakeCrashDumpPlan -Store $store -Plan $inputs.SetupPlan -Snapshot $inputs.Snapshot -PlanKind Setup -FailBeforeActionIndex 1; throw 'Injected failure was not observed.' }
    catch { if (-not $_.Exception.Message.Contains('Injected fake-store failure')) { throw } }
    $rollback = New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $inputs.Snapshot -SetupPlan $inputs.SetupPlan -ReadKey (Get-FakeCrashDumpReader -Store $store)
    Invoke-FakeCrashDumpPlan -Store $store -Plan $rollback -Snapshot $inputs.Snapshot -PlanKind Restore
    Assert-FakeRestored -Snapshot $inputs.Snapshot -Store $store
    Assert-PolicyTrue (-not $store.Keys.ContainsKey($target.BaseKey)) 'New empty base key was not removed.'
    Assert-PolicyTrue (-not $store.Keys.ContainsKey($target.AppKey)) 'New empty app key was not removed.'
}

Invoke-PolicyTest 'restore round-trips all supported registry kinds with raw types intact' {
    $cases = @(
        @{ Kind = 'None'; Data = [byte[]]@(0, 1, 255) },
        @{ Kind = 'String'; Data = 'literal %TEMP% and & text' },
        @{ Kind = 'ExpandString'; Data = '%UNEXPANDED_VARIABLE%\raw' },
        @{ Kind = 'Binary'; Data = [byte[]]@(3, 7, 9, 255) },
        @{ Kind = 'DWord'; Data = [int]-2147483648 },
        @{ Kind = 'MultiString'; Data = [string[]]@('one', '', 'three') },
        @{ Kind = 'MultiString'; Data = [string[]]@() },
        @{ Kind = 'QWord'; Data = [long]9223372036854775807 }
    )
    foreach ($case in $cases) {
        $store = New-FakeCrashDumpStore
        Add-FakeCrashDumpKey -Store $store -Path $target.BaseKey
        Add-FakeCrashDumpKey -Store $store -Path $target.AppKey
        foreach ($name in $target.ManagedNames) {
            Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name $name -Kind $case.Kind -Data $case.Data
        }
        $inputs = New-FakePolicyInputs -Store $store
        $json = ConvertTo-MarkdownSandboxCrashDumpReceiptJson -Snapshot $inputs.Snapshot -SetupPlan $inputs.SetupPlan
        $restoredReceipt = ConvertFrom-MarkdownSandboxCrashDumpReceiptJson -Json $json
        foreach ($name in $target.ManagedNames) {
            Assert-PolicyTrue (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $inputs.Snapshot.Values[$name] -Right $restoredReceipt.Snapshot.Values[$name]) "JSON changed $($case.Kind) value '$name'."
            Assert-PolicyEqual $case.Data.GetType().FullName $restoredReceipt.Snapshot.Values[$name].Data.GetType().FullName "JSON changed the CLR type for $($case.Kind)."
        }
        Assert-MarkdownSandboxCrashDumpSetupPlan -Snapshot $restoredReceipt.Snapshot -SetupPlan $restoredReceipt.SetupPlan
        Assert-PolicyEqual $json (ConvertTo-MarkdownSandboxCrashDumpReceiptJson -Snapshot $restoredReceipt.Snapshot -SetupPlan $restoredReceipt.SetupPlan) 'Receipt JSON is not stable across typed round-trip.'
        Invoke-FakeCrashDumpPlan -Store $store -Plan $restoredReceipt.SetupPlan -Snapshot $restoredReceipt.Snapshot -PlanKind Setup
        $restorePlan = New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $restoredReceipt.Snapshot -SetupPlan $restoredReceipt.SetupPlan -ReadKey (Get-FakeCrashDumpReader -Store $store)
        Invoke-FakeCrashDumpPlan -Store $store -Plan $restorePlan -Snapshot $restoredReceipt.Snapshot -PlanKind Restore
        Assert-FakeRestored -Snapshot $restoredReceipt.Snapshot -Store $store
    }
}

Invoke-PolicyTest 'receipt normalization rejects changed target and unknown managed values' {
    $store = New-FakeCrashDumpStore
    $inputs = New-FakePolicyInputs -Store $store
    $json = ConvertTo-MarkdownSandboxCrashDumpReceiptJson -Snapshot $inputs.Snapshot -SetupPlan $inputs.SetupPlan
    $wrongDocument = ConvertFrom-Json -InputObject $json
    $wrongDocument.targetId = 'wrong-target'
    $wrongTarget = ConvertTo-Json -InputObject $wrongDocument -Depth 24 -Compress
    Assert-PolicyThrows { ConvertFrom-MarkdownSandboxCrashDumpReceiptJson -Json $wrongTarget } 'target identity'
    $unknownName = $json.Replace('"DumpType":', '"OtherValue":{"exists":false,"kind":null,"encoding":null,"data":null},"DumpType":')
    Assert-PolicyThrows { ConvertFrom-MarkdownSandboxCrashDumpReceiptJson -Json $unknownName } 'unexpected property set'
}

Invoke-PolicyTest 'restore plan detects a concurrent target write before emitting any plan' {
    $store = New-FakeCrashDumpStore
    Add-FakeCrashDumpKey -Store $store -Path $target.BaseKey
    Add-FakeCrashDumpKey -Store $store -Path $target.AppKey
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpFolder' -Kind 'String' -Data 'prior-folder'
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpCount' -Kind 'MultiString' -Data ([string[]]@('old'))
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpType' -Kind 'QWord' -Data ([long]7)
    $inputs = New-FakePolicyInputs -Store $store
    Invoke-FakeCrashDumpPlan -Store $store -Plan $inputs.SetupPlan -Snapshot $inputs.Snapshot -PlanKind Setup
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpType' -Kind 'String' -Data 'concurrent-owner'
    Assert-PolicyThrows { New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $inputs.Snapshot -SetupPlan $inputs.SetupPlan -ReadKey (Get-FakeCrashDumpReader -Store $store) } 'Concurrent modification'
    Assert-PolicyEqual 'C:\WinApp\work\CrashDumps\run-7a0f' (Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpFolder').Data 'Folder changed despite conflict detection.'
    Assert-PolicyEqual 'concurrent-owner' (Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpType').Data 'Concurrent target write was overwritten.'
}

Invoke-PolicyTest 'restore applicability catches a write after planning before any restore action' {
    $store = New-FakeCrashDumpStore
    Add-FakeCrashDumpKey -Store $store -Path $target.BaseKey
    Add-FakeCrashDumpKey -Store $store -Path $target.AppKey
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpFolder' -Kind 'String' -Data 'old-folder'
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpCount' -Kind 'DWord' -Data ([int]9)
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpType' -Kind 'QWord' -Data ([long]4)
    $inputs = New-FakePolicyInputs -Store $store
    Invoke-FakeCrashDumpPlan -Store $store -Plan $inputs.SetupPlan -Snapshot $inputs.Snapshot -PlanKind Setup
    $restore = New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $inputs.Snapshot -SetupPlan $inputs.SetupPlan -ReadKey (Get-FakeCrashDumpReader -Store $store)
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpType' -Kind 'String' -Data 'late-concurrent-write'
    Assert-PolicyThrows { Assert-MarkdownSandboxCrashDumpRestorePlanApplicable -Snapshot $inputs.Snapshot -RestorePlan $restore -ReadKey (Get-FakeCrashDumpReader -Store $store) } 'changed after the restore plan'
    Assert-PolicyEqual 'C:\WinApp\work\CrashDumps\run-7a0f' (Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpFolder').Data 'Failed global preflight changed an earlier managed value.'
    Assert-PolicyEqual 'late-concurrent-write' (Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'DumpType').Data 'Concurrent target value was overwritten.'
}

Invoke-PolicyTest 'unrelated values and subkeys survive an exact setup and restoration' {
    $store = New-FakeCrashDumpStore
    Add-FakeCrashDumpKey -Store $store -Path $target.BaseKey
    Add-FakeCrashDumpKey -Store $store -Path $target.AppKey
    Set-FakeCrashDumpValue -Store $store -Path $target.BaseKey -Name 'OtherProgram.exe' -Kind 'String' -Data 'other-folder'
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'OwnerNote' -Kind 'ExpandString' -Data '%KEEP_ME%'
    Add-FakeCrashDumpKey -Store $store -Path ($target.AppKey + '\ForeignChild')
    $inputs = New-FakePolicyInputs -Store $store
    Invoke-FakeCrashDumpPlan -Store $store -Plan $inputs.SetupPlan -Snapshot $inputs.Snapshot -PlanKind Setup
    $restore = New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $inputs.Snapshot -SetupPlan $inputs.SetupPlan -ReadKey (Get-FakeCrashDumpReader -Store $store)
    Invoke-FakeCrashDumpPlan -Store $store -Plan $restore -Snapshot $inputs.Snapshot -PlanKind Restore
    Assert-FakeRestored -Snapshot $inputs.Snapshot -Store $store
    Assert-PolicyEqual 'other-folder' (Get-FakeCrashDumpValue -Store $store -Path $target.BaseKey -Name 'OtherProgram.exe').Data 'Unrelated base value changed.'
    Assert-PolicyEqual '%KEEP_ME%' (Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'OwnerNote').Data 'Unrelated app value changed.'
    Assert-PolicyTrue $store.Keys.ContainsKey($target.AppKey + '\ForeignChild') 'Unrelated subkey was deleted.'
}

Invoke-PolicyTest 'new app/base keys are deleted only while empty and foreign values are retained' {
    $store = New-FakeCrashDumpStore
    $inputs = New-FakePolicyInputs -Store $store
    Invoke-FakeCrashDumpPlan -Store $store -Plan $inputs.SetupPlan -Snapshot $inputs.Snapshot -PlanKind Setup
    Set-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'ForeignValue' -Kind 'String' -Data 'keep'
    $restore = New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $inputs.Snapshot -SetupPlan $inputs.SetupPlan -ReadKey (Get-FakeCrashDumpReader -Store $store)
    Assert-PolicyTrue (@($restore.Actions | Where-Object { $_.Operation -eq 'RemoveKeyIfEmpty' }).Count -eq 2) 'Restore plan must use conditional cleanup for only the newly created keys.'
    Invoke-FakeCrashDumpPlan -Store $store -Plan $restore -Snapshot $inputs.Snapshot -PlanKind Restore
    $status = Get-MarkdownSandboxCrashDumpRestoreStatus -Snapshot $inputs.Snapshot -ReadKey (Get-FakeCrashDumpReader -Store $store)
    Assert-PolicyTrue $status.ManagedValuesRestored 'Managed WER values were not restored.'
    Assert-PolicyTrue (-not $status.KeyExistenceRestored) 'Nonempty newly-created keys should be reported as retained.'
    Assert-PolicyTrue ($store.Keys.ContainsKey($target.AppKey)) 'Nonempty app key was deleted.'
    Assert-PolicyEqual 'keep' (Get-FakeCrashDumpValue -Store $store -Path $target.AppKey -Name 'ForeignValue').Data 'Foreign value was deleted.'
}

$failed = @($script:PolicyTestResults | Where-Object { $_.Status -eq 'FAIL' })
$passed = @($script:PolicyTestResults | Where-Object { $_.Status -eq 'PASS' })
Write-Host "Offline WER policy tests: $($passed.Count) passed, $($failed.Count) failed. These exercise only injected in-memory state; they do not verify live Windows Error Reporting behavior."
if ($failed.Count -gt 0) {
    $script:PolicyTestResults | Where-Object { $_.Status -eq 'FAIL' } | ForEach-Object { Write-Error "$($_.Name): $($_.Detail)" }
    exit 1
}
exit 0
