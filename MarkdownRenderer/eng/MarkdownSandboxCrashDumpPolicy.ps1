# Offline policy for the narrowly scoped Sandbox crash-dump diagnostic.
# This file intentionally contains no Registry or filesystem mutation. Callers
# provide a read-only key reader and a path-existence predicate; a separate
# guest-only adapter is responsible for executing the returned plans.

function Get-MarkdownSandboxCrashDumpTarget {
    $baseSubKey = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'
    $appSubKey = $baseSubKey + '\MarkdownRenderer.Sample.exe'
    return [ordered]@{
        Hive          = 'LocalMachine'
        RegistryView  = 'Registry64'
        BaseSubKey    = $baseSubKey
        AppSubKey     = $appSubKey
        BaseKey       = 'HKLM64:\' + $baseSubKey
        AppKey        = 'HKLM64:\' + $appSubKey
        ManagedNames  = @('DumpFolder', 'DumpCount', 'DumpType')
        TargetId      = 'LocalMachine|Registry64|' + $appSubKey
    }
}

function Copy-MarkdownSandboxCrashDumpPolicyData {
    param([AllowNull()][object]$Data)

    if ($null -eq $Data) { return $null }
    if ($Data -is [System.Array]) {
        $copy = $Data.Clone()
        return ,$copy
    }
    return $Data
}

function Copy-MarkdownSandboxCrashDumpPolicyValueMap {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Values)

    $copy = [ordered]@{}
    foreach ($name in @('DumpFolder', 'DumpCount', 'DumpType')) {
        $entry = $Values[$name]
        $copy[$name] = [pscustomobject]@{
            Exists = [bool]$entry.Exists
            Kind = $entry.Kind
            Data = Copy-MarkdownSandboxCrashDumpPolicyData $entry.Data
        }
    }
    return ,$copy
}

function Test-MarkdownSandboxCrashDumpPolicyValueEqual {
    param(
        [Parameter(Mandatory)][object]$Left,
        [Parameter(Mandatory)][object]$Right
    )

    if ([bool]$Left.Exists -ne [bool]$Right.Exists) { return $false }
    if (-not [bool]$Left.Exists) { return $true }
    if ([string]$Left.Kind -ine [string]$Right.Kind) { return $false }

    $leftData = $Left.Data
    $rightData = $Right.Data
    $leftIsArray = $leftData -is [System.Array]
    $rightIsArray = $rightData -is [System.Array]
    if ($leftIsArray -ne $rightIsArray) { return $false }
    if ($leftIsArray) {
        if ($leftData.GetType() -ne $rightData.GetType()) { return $false }
        if ($leftData.Length -ne $rightData.Length) { return $false }
        for ($index = 0; $index -lt $leftData.Length; $index++) {
            if (-not [object]::Equals($leftData.GetValue($index), $rightData.GetValue($index))) {
                return $false
            }
        }
        return $true
    }
    return [object]::Equals($leftData, $rightData)
}

function Get-MarkdownSandboxCrashDumpKeyState {
    param(
        [Parameter(Mandatory)][scriptblock]$ReadKey,
        [Parameter(Mandatory)][string]$Path
    )

    $state = & $ReadKey $Path
    if ($null -eq $state -or $null -eq $state.PSObject.Properties['Exists']) {
        throw "ReadKey returned an invalid state for '$Path'."
    }
    if ($state.Exists -isnot [bool]) {
        throw "ReadKey.Exists must be Boolean for '$Path'."
    }

    $values = @()
    $subKeys = @()
    if ($state.Exists) {
        if ($null -eq $state.PSObject.Properties['Values'] -or
            $null -eq $state.PSObject.Properties['SubKeys']) {
            throw "Existing key state must include Values and SubKeys for '$Path'."
        }
        foreach ($value in @($state.Values)) {
            if ($null -eq $value -or
                $null -eq $value.PSObject.Properties['Name'] -or
                $null -eq $value.PSObject.Properties['Kind'] -or
                $null -eq $value.PSObject.Properties['Data']) {
                throw "ReadKey returned a malformed value entry for '$Path'."
            }
            $values += [pscustomobject]@{
                Name = [string]$value.Name
                Kind = [string]$value.Kind
                Data = Copy-MarkdownSandboxCrashDumpPolicyData $value.Data
            }
        }
        $subKeys = @($state.SubKeys | ForEach-Object { [string]$_ })
    }

    return [pscustomobject]@{
        Exists  = [bool]$state.Exists
        Values  = @($values)
        SubKeys = @($subKeys)
    }
}

function Get-MarkdownSandboxCrashDumpNamedValue {
    param(
        [Parameter(Mandatory)][object]$KeyState,
        [Parameter(Mandatory)][string]$Name
    )

    $matches = @($KeyState.Values | Where-Object { [string]::Equals([string]$_.Name, $Name, [StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -gt 1) { throw "ReadKey returned duplicate '$Name' values." }
    if ($matches.Count -eq 0) {
        return [pscustomobject]@{ Exists = $false; Kind = $null; Data = $null }
    }

    $supportedKinds = @('None', 'String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord')
    if ($matches[0].Kind -notin $supportedKinds) {
        throw "ReadKey returned unsupported registry value kind '$($matches[0].Kind)' for '$Name'."
    }
    return [pscustomobject]@{
        Exists = $true
        Kind   = [string]$matches[0].Kind
        Data   = Copy-MarkdownSandboxCrashDumpPolicyData $matches[0].Data
    }
}

function Assert-MarkdownSandboxCrashDumpSnapshot {
    param([Parameter(Mandatory)][object]$Snapshot)

    $target = Get-MarkdownSandboxCrashDumpTarget
    $expectedFields = @{
        SchemaVersion = 1
        Hive = $target.Hive
        RegistryView = $target.RegistryView
        BaseSubKey = $target.BaseSubKey
        AppSubKey = $target.AppSubKey
        TargetId = $target.TargetId
    }
    foreach ($field in $expectedFields.Keys) {
        if ($null -eq $Snapshot.PSObject.Properties[$field] -or
            -not [object]::Equals($Snapshot.$field, $expectedFields[$field])) {
            throw "Crash-dump snapshot has invalid target identity or schema field '$field'."
        }
    }
    if ($null -eq $Snapshot.PSObject.Properties['SnapshotId'] -or
        [string]$Snapshot.SnapshotId -notmatch '^[0-9a-fA-F]{32}$') {
        throw 'Crash-dump snapshot has an invalid SnapshotId.'
    }
    if ($Snapshot.BaseKeyExists -isnot [bool] -or $Snapshot.AppKeyExists -isnot [bool]) {
        throw 'Crash-dump snapshot key-existence fields must be Boolean.'
    }
    if ($Snapshot.AppKeyExists -and -not $Snapshot.BaseKeyExists) {
        throw 'Crash-dump snapshot app key cannot exist when its base key is absent.'
    }
    if ($null -eq $Snapshot.PSObject.Properties['Values'] -or
        $Snapshot.Values -isnot [System.Collections.IDictionary]) {
        throw 'Crash-dump snapshot is missing its managed Values dictionary.'
    }
    $names = @($Snapshot.Values.Keys | ForEach-Object { [string]$_ })
    if ($names.Count -ne $target.ManagedNames.Count) {
        throw 'Crash-dump snapshot contains an unexpected managed-value set.'
    }
    foreach ($name in $target.ManagedNames) {
        if (-not $Snapshot.Values.Contains($name)) {
            throw "Crash-dump snapshot is missing managed value '$name'."
        }
        $entry = $Snapshot.Values[$name]
        if ($null -eq $entry -or $entry.Exists -isnot [bool]) {
            throw "Crash-dump snapshot entry '$name' is malformed."
        }
        if ($entry.Exists) {
            $supportedKinds = @('None', 'String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord')
            if ([string]$entry.Kind -notin $supportedKinds -or
                $null -eq $entry.PSObject.Properties['Data']) {
                throw "Crash-dump snapshot entry '$name' has an invalid kind or data field."
            }
        } elseif ($null -ne $entry.Kind -or $null -ne $entry.Data) {
            throw "Absent crash-dump snapshot entry '$name' must not carry a kind or data."
        }
        if (-not $Snapshot.AppKeyExists -and $entry.Exists) {
            throw "Absent crash-dump app key cannot have prior value '$name'."
        }
    }
}

function Test-MarkdownSandboxCrashDumpPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$GuestWorkRoot,
        [Parameter(Mandatory)][string]$DumpFolder,
        [Parameter(Mandatory)][scriptblock]$PathExists
    )

    foreach ($candidate in @(@{ Name = 'GuestWorkRoot'; Value = $GuestWorkRoot }, @{ Name = 'DumpFolder'; Value = $DumpFolder })) {
        if ([string]::IsNullOrWhiteSpace($candidate.Value) -or
            -not [System.IO.Path]::IsPathRooted($candidate.Value) -or
            $candidate.Value -notmatch '^[A-Za-z]:\\') {
            throw "$($candidate.Name) must be an absolute local-drive Windows path."
        }
        if ($candidate.Value.StartsWith('\\', [StringComparison]::Ordinal) -or
            $candidate.Value.StartsWith('//', [StringComparison]::Ordinal)) {
            throw "$($candidate.Name) must not be a UNC or device path."
        }
        $pathRemainder = $candidate.Value.Substring(2)
        if ($pathRemainder -match '[<>:"|?*%]' -or $candidate.Value -match '[\x00-\x1f]') {
            throw "$($candidate.Name) contains a path character that is unsafe for an expanded WER dump path."
        }
    }

    $rootFull = [System.IO.Path]::GetFullPath($GuestWorkRoot)
    $dumpFull = [System.IO.Path]::GetFullPath($DumpFolder)
    $rootVolume = [System.IO.Path]::GetPathRoot($rootFull)
    $dumpVolume = [System.IO.Path]::GetPathRoot($dumpFull)
    if (-not [string]::Equals($rootVolume, $dumpVolume, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DumpFolder must be on the guest workRoot volume.'
    }

    $rawRelative = $DumpFolder.Substring(3)
    if (($rawRelative -split '[\\/]') -contains '..' -or
        ($rawRelative -split '[\\/]') -contains '.') {
        throw 'DumpFolder must not contain dot or parent-traversal path components.'
    }

    $rootPrefix = $rootFull.TrimEnd([char[]]@('\', '/')) + '\'
    if (-not $dumpFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DumpFolder must be a strict descendant of GuestWorkRoot.'
    }

    $existsResult = & $PathExists $dumpFull
    if ($null -eq $existsResult -or @($existsResult).Count -ne 1 -or $existsResult -isnot [bool]) {
        throw 'PathExists must return exactly one Boolean result.'
    }
    if ($existsResult) { throw 'DumpFolder must be a fresh, nonexistent path for this run.' }
    return $dumpFull
}

function Get-MarkdownSandboxCrashDumpSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][scriptblock]$ReadKey)

    $target = Get-MarkdownSandboxCrashDumpTarget
    $base = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.BaseKey
    $app = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.AppKey
    if ($app.Exists -and -not $base.Exists) {
        throw 'Registry state is inconsistent: per-app LocalDumps key exists without its base key.'
    }

    $values = [ordered]@{}
    foreach ($name in $target.ManagedNames) {
        $values[$name] = Get-MarkdownSandboxCrashDumpNamedValue -KeyState $app -Name $name
    }

    $snapshot = [pscustomobject][ordered]@{
        SchemaVersion = 1
        SnapshotId = [guid]::NewGuid().ToString('N')
        Hive = $target.Hive
        RegistryView = $target.RegistryView
        BaseSubKey = $target.BaseSubKey
        AppSubKey = $target.AppSubKey
        TargetId = $target.TargetId
        BaseKeyExists = [bool]$base.Exists
        AppKeyExists = [bool]$app.Exists
        Values = $values
    }
    Assert-MarkdownSandboxCrashDumpSnapshot -Snapshot $snapshot
    return $snapshot
}

function New-MarkdownSandboxCrashDumpSetupPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][string]$GuestWorkRoot,
        [Parameter(Mandatory)][string]$DumpFolder,
        [Parameter(Mandatory)][scriptblock]$PathExists
    )

    Assert-MarkdownSandboxCrashDumpSnapshot -Snapshot $Snapshot
    $canonicalDumpFolder = Test-MarkdownSandboxCrashDumpPath -GuestWorkRoot $GuestWorkRoot -DumpFolder $DumpFolder -PathExists $PathExists
    $target = Get-MarkdownSandboxCrashDumpTarget
    $configured = [ordered]@{
        DumpFolder = [pscustomobject]@{ Exists = $true; Kind = 'ExpandString'; Data = $canonicalDumpFolder }
        DumpCount  = [pscustomobject]@{ Exists = $true; Kind = 'DWord'; Data = [int]1 }
        DumpType   = [pscustomobject]@{ Exists = $true; Kind = 'DWord'; Data = [int]2 }
    }

    $actions = @()
    foreach ($name in $target.ManagedNames) {
        $desired = $configured[$name]
        $actions += [pscustomobject]@{
            Operation = 'SetValue'
            Path = $target.AppKey
            Name = $name
            Kind = $desired.Kind
            Data = Copy-MarkdownSandboxCrashDumpPolicyData $desired.Data
            ExpectedCurrent = [pscustomobject]@{
                Exists = [bool]$Snapshot.Values[$name].Exists
                Kind = $Snapshot.Values[$name].Kind
                Data = Copy-MarkdownSandboxCrashDumpPolicyData $Snapshot.Values[$name].Data
            }
        }
    }

    return [pscustomobject][ordered]@{
        SchemaVersion = 1
        SnapshotId = [string]$Snapshot.SnapshotId
        Hive = $target.Hive
        RegistryView = $target.RegistryView
        BaseSubKey = $target.BaseSubKey
        AppSubKey = $target.AppSubKey
        TargetId = $target.TargetId
        ExpectedBaseKeyExists = [bool]$Snapshot.BaseKeyExists
        ExpectedAppKeyExists = [bool]$Snapshot.AppKeyExists
        ExpectedManagedValues = Copy-MarkdownSandboxCrashDumpPolicyValueMap $Snapshot.Values
        DumpFolder = $canonicalDumpFolder
        Values = $configured
        Actions = @($actions)
    }
}

function Assert-MarkdownSandboxCrashDumpSetupPlanApplicable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][object]$SetupPlan,
        [Parameter(Mandatory)][scriptblock]$ReadKey
    )

    Assert-MarkdownSandboxCrashDumpSetupPlan -Snapshot $Snapshot -SetupPlan $SetupPlan
    $target = Get-MarkdownSandboxCrashDumpTarget
    $base = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.BaseKey
    $app = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.AppKey
    if ($app.Exists -and -not $base.Exists) { throw 'Current registry state has an app key without its base key.' }
    if ($base.Exists -ne $Snapshot.BaseKeyExists -or $app.Exists -ne $Snapshot.AppKeyExists) {
        throw 'Registry key existence changed after the crash-dump snapshot; setup was not applied.'
    }
    foreach ($name in $target.ManagedNames) {
        $current = Get-MarkdownSandboxCrashDumpNamedValue -KeyState $app -Name $name
        if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $current -Right $Snapshot.Values[$name])) {
            throw "Managed value '$name' changed after the crash-dump snapshot; setup was not applied."
        }
    }
    return $true
}

function Assert-MarkdownSandboxCrashDumpSetupPlan {
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][object]$SetupPlan
    )

    Assert-MarkdownSandboxCrashDumpSnapshot -Snapshot $Snapshot
    $target = Get-MarkdownSandboxCrashDumpTarget
    foreach ($field in @('SchemaVersion', 'Hive', 'RegistryView', 'BaseSubKey', 'AppSubKey', 'TargetId')) {
        $expected = switch ($field) {
            'SchemaVersion' { 1 }
            'Hive' { $target.Hive }
            'RegistryView' { $target.RegistryView }
            'BaseSubKey' { $target.BaseSubKey }
            'AppSubKey' { $target.AppSubKey }
            'TargetId' { $target.TargetId }
        }
        if ($null -eq $SetupPlan.PSObject.Properties[$field] -or
            -not [object]::Equals($SetupPlan.$field, $expected)) {
            throw "Crash-dump setup plan has invalid target identity or schema field '$field'."
        }
    }
    if ([string]$SetupPlan.SnapshotId -ne [string]$Snapshot.SnapshotId) {
        throw 'Crash-dump setup plan does not belong to this snapshot.'
    }
    if ($SetupPlan.ExpectedBaseKeyExists -isnot [bool] -or
        $SetupPlan.ExpectedAppKeyExists -isnot [bool] -or
        $SetupPlan.ExpectedManagedValues -isnot [System.Collections.IDictionary]) {
        throw 'Crash-dump setup plan is missing its current-state guard.'
    }
    if ($SetupPlan.Values -isnot [System.Collections.IDictionary] -or
        $SetupPlan.Actions.Count -ne 3) {
        throw 'Crash-dump setup plan has an invalid managed-value/action set.'
    }
    foreach ($name in $target.ManagedNames) {
        if (-not $SetupPlan.Values.Contains($name)) { throw "Setup plan is missing '$name'." }
        $matches = @($SetupPlan.Actions | Where-Object { $_.Name -eq $name })
        if ($matches.Count -ne 1 -or $matches[0].Operation -ne 'SetValue' -or $matches[0].Path -ne $target.AppKey) {
            throw "Setup plan has an invalid action for '$name'."
        }
        $value = $SetupPlan.Values[$name]
        if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $value -Right ([pscustomobject]@{
            Exists = $true; Kind = $matches[0].Kind; Data = $matches[0].Data
        }))) {
            throw "Setup plan action does not match its managed value '$name'."
        }
        if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $matches[0].ExpectedCurrent -Right $Snapshot.Values[$name])) {
            throw "Setup plan has an invalid expected-current guard for '$name'."
        }
        if (-not $SetupPlan.ExpectedManagedValues.Contains($name) -or
            -not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $SetupPlan.ExpectedManagedValues[$name] -Right $Snapshot.Values[$name])) {
            throw "Setup plan has an invalid full-state guard for '$name'."
        }
    }
    if ($SetupPlan.ExpectedBaseKeyExists -ne $Snapshot.BaseKeyExists -or
        $SetupPlan.ExpectedAppKeyExists -ne $Snapshot.AppKeyExists) {
        throw 'Setup plan key guards do not match the snapshot.'
    }
    if ([string]$SetupPlan.Values.DumpFolder.Data -ne [string]$SetupPlan.DumpFolder -or
        $SetupPlan.Values.DumpFolder.Kind -ne 'ExpandString' -or
        $SetupPlan.Values.DumpCount.Kind -ne 'DWord' -or [int]$SetupPlan.Values.DumpCount.Data -ne 1 -or
        $SetupPlan.Values.DumpType.Kind -ne 'DWord' -or [int]$SetupPlan.Values.DumpType.Data -ne 2) {
        throw 'Crash-dump setup plan does not contain the fixed per-app WER policy.'
    }
}

function New-MarkdownSandboxCrashDumpRestorePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][object]$SetupPlan,
        [Parameter(Mandatory)][scriptblock]$ReadKey
    )

    Assert-MarkdownSandboxCrashDumpSetupPlan -Snapshot $Snapshot -SetupPlan $SetupPlan
    $target = Get-MarkdownSandboxCrashDumpTarget
    $base = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.BaseKey
    $app = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.AppKey
    if ($app.Exists -and -not $base.Exists) { throw 'Current registry state has an app key without its base key.' }
    if ($Snapshot.BaseKeyExists -and -not $base.Exists) { throw 'Concurrent modification removed the pre-existing LocalDumps base key.' }
    if ($Snapshot.AppKeyExists -and -not $app.Exists) { throw 'Concurrent modification removed the pre-existing per-app LocalDumps key.' }

    $currentValues = [ordered]@{}
    foreach ($name in $target.ManagedNames) {
        $current = Get-MarkdownSandboxCrashDumpNamedValue -KeyState $app -Name $name
        $original = $Snapshot.Values[$name]
        $configured = $SetupPlan.Values[$name]
        if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $current -Right $original) -and
            -not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $current -Right $configured)) {
            throw "Concurrent modification changed managed value '$name'; no restore actions were generated."
        }
        $currentValues[$name] = $current
    }

    $actions = @()
    foreach ($name in $target.ManagedNames) {
        $original = $Snapshot.Values[$name]
        $current = $currentValues[$name]
        if (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $current -Right $original) { continue }

        if ($original.Exists) {
            $actions += [pscustomobject]@{
                Operation = 'SetValue'
                Path = $target.AppKey
                Name = $name
                Kind = $original.Kind
                Data = Copy-MarkdownSandboxCrashDumpPolicyData $original.Data
                ExpectedCurrent = [pscustomobject]@{
                    Exists = [bool]$current.Exists
                    Kind = $current.Kind
                    Data = Copy-MarkdownSandboxCrashDumpPolicyData $current.Data
                }
            }
        } else {
            $actions += [pscustomobject]@{
                Operation = 'RemoveValue'
                Path = $target.AppKey
                Name = $name
                Kind = $null
                Data = $null
                ExpectedCurrent = [pscustomobject]@{
                    Exists = [bool]$current.Exists
                    Kind = $current.Kind
                    Data = Copy-MarkdownSandboxCrashDumpPolicyData $current.Data
                }
            }
        }
    }

    if (-not $Snapshot.AppKeyExists) {
        $actions += [pscustomobject]@{
            Operation = 'RemoveKeyIfEmpty'
            Path = $target.AppKey
            Name = $null
            Kind = $null
            Data = $null
            ExpectedCurrent = $null
        }
    }
    if (-not $Snapshot.BaseKeyExists) {
        $actions += [pscustomobject]@{
            Operation = 'RemoveKeyIfEmpty'
            Path = $target.BaseKey
            Name = $null
            Kind = $null
            Data = $null
            ExpectedCurrent = $null
        }
    }

    return [pscustomobject][ordered]@{
        SchemaVersion = 1
        SnapshotId = [string]$Snapshot.SnapshotId
        Hive = $target.Hive
        RegistryView = $target.RegistryView
        BaseSubKey = $target.BaseSubKey
        AppSubKey = $target.AppSubKey
        TargetId = $target.TargetId
        ExpectedBaseKeyExists = [bool]$base.Exists
        ExpectedAppKeyExists = [bool]$app.Exists
        ExpectedManagedValues = Copy-MarkdownSandboxCrashDumpPolicyValueMap $currentValues
        Actions = @($actions)
    }
}

function Assert-MarkdownSandboxCrashDumpRestorePlanApplicable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][object]$RestorePlan,
        [Parameter(Mandatory)][scriptblock]$ReadKey
    )

    Assert-MarkdownSandboxCrashDumpRestorePlan -Snapshot $Snapshot -RestorePlan $RestorePlan
    $target = Get-MarkdownSandboxCrashDumpTarget
    if ($RestorePlan.ExpectedBaseKeyExists -isnot [bool] -or
        $RestorePlan.ExpectedAppKeyExists -isnot [bool] -or
        $RestorePlan.ExpectedManagedValues -isnot [System.Collections.IDictionary]) {
        throw 'Crash-dump restore plan is missing its current-state guard.'
    }
    $base = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.BaseKey
    $app = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.AppKey
    if ($app.Exists -and -not $base.Exists) { throw 'Current registry state has an app key without its base key.' }
    if ($base.Exists -ne $RestorePlan.ExpectedBaseKeyExists -or
        $app.Exists -ne $RestorePlan.ExpectedAppKeyExists) {
        throw 'Registry key existence changed after the restore plan was prepared.'
    }
    foreach ($name in $target.ManagedNames) {
        if (-not $RestorePlan.ExpectedManagedValues.Contains($name)) {
            throw "Restore plan is missing expected-current value '$name'."
        }
        $current = Get-MarkdownSandboxCrashDumpNamedValue -KeyState $app -Name $name
        if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $current -Right $RestorePlan.ExpectedManagedValues[$name])) {
            throw "Managed value '$name' changed after the restore plan was prepared."
        }
    }
    return $true
}

function Assert-MarkdownSandboxCrashDumpRestoreAction {
    param([Parameter(Mandatory)][object]$Action)

    $target = Get-MarkdownSandboxCrashDumpTarget
    if ($Action.Operation -eq 'SetValue' -or $Action.Operation -eq 'RemoveValue') {
        if ($Action.Path -ne $target.AppKey -or $Action.Name -notin $target.ManagedNames) {
            throw 'Restore plan contains a value action outside the fixed per-app target.'
        }
        if ($null -eq $Action.ExpectedCurrent -or $Action.ExpectedCurrent.Exists -isnot [bool]) {
            throw 'Restore value action is missing its concurrent-modification guard.'
        }
        if ($Action.Operation -eq 'SetValue' -and
            [string]$Action.Kind -notin @('None', 'String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord')) {
            throw 'Restore value action contains an unsupported registry value kind.'
        }
        if ($Action.Operation -eq 'RemoveValue' -and ($null -ne $Action.Kind -or $null -ne $Action.Data)) {
            throw 'RemoveValue action must not contain replacement value data.'
        }
        return
    }
    if ($Action.Operation -eq 'RemoveKeyIfEmpty') {
        if ($Action.Path -ne $target.AppKey -and $Action.Path -ne $target.BaseKey) {
            throw 'Restore plan contains a key-removal action outside the fixed LocalDumps keys.'
        }
        if ($null -ne $Action.Name -or $null -ne $Action.Kind -or $null -ne $Action.Data) {
            throw 'RemoveKeyIfEmpty action must not contain value fields.'
        }
        return
    }
    throw "Restore plan contains unsupported operation '$($Action.Operation)'."
}

function Assert-MarkdownSandboxCrashDumpRestorePlan {
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][object]$RestorePlan
    )

    Assert-MarkdownSandboxCrashDumpSnapshot -Snapshot $Snapshot
    $target = Get-MarkdownSandboxCrashDumpTarget
    foreach ($field in @('SchemaVersion', 'Hive', 'RegistryView', 'BaseSubKey', 'AppSubKey', 'TargetId')) {
        $expected = switch ($field) {
            'SchemaVersion' { 1 }
            'Hive' { $target.Hive }
            'RegistryView' { $target.RegistryView }
            'BaseSubKey' { $target.BaseSubKey }
            'AppSubKey' { $target.AppSubKey }
            'TargetId' { $target.TargetId }
        }
        if ($null -eq $RestorePlan.PSObject.Properties[$field] -or
            -not [object]::Equals($RestorePlan.$field, $expected)) {
            throw "Crash-dump restore plan has invalid target identity or schema field '$field'."
        }
    }
    if ([string]$RestorePlan.SnapshotId -ne [string]$Snapshot.SnapshotId) {
        throw 'Crash-dump restore plan does not belong to this snapshot.'
    }
    if ($RestorePlan.ExpectedBaseKeyExists -isnot [bool] -or
        $RestorePlan.ExpectedAppKeyExists -isnot [bool] -or
        $RestorePlan.ExpectedManagedValues -isnot [System.Collections.IDictionary]) {
        throw 'Crash-dump restore plan is missing its current-state guard.'
    }
    foreach ($name in $target.ManagedNames) {
        if (-not $RestorePlan.ExpectedManagedValues.Contains($name)) {
            throw "Restore plan is missing expected-current value '$name'."
        }
        if ($RestorePlan.ExpectedManagedValues[$name].Exists -isnot [bool]) {
            throw "Restore plan expected-current value '$name' is malformed."
        }
    }
    foreach ($action in @($RestorePlan.Actions)) {
        Assert-MarkdownSandboxCrashDumpRestoreAction -Action $action
    }
}

function ConvertTo-MarkdownSandboxCrashDumpValueEnvelope {
    param([Parameter(Mandatory)][object]$Value)

    if ($Value.Exists -isnot [bool]) { throw 'A serialized registry value must have a Boolean Exists field.' }
    if (-not $Value.Exists) {
        if ($null -ne $Value.Kind -or $null -ne $Value.Data) {
            throw 'An absent registry value must not carry a kind or raw data.'
        }
        return [ordered]@{ exists = $false; kind = $null; encoding = $null; data = $null }
    }

    $kind = [string]$Value.Kind
    switch -Exact ($kind) {
        'None' {
            if ($Value.Data -isnot [byte[]]) { throw 'REG_NONE raw data must be a byte array.' }
            $encoding = 'base64'
            $data = [Convert]::ToBase64String($Value.Data)
        }
        'Binary' {
            if ($Value.Data -isnot [byte[]]) { throw 'REG_BINARY raw data must be a byte array.' }
            $encoding = 'base64'
            $data = [Convert]::ToBase64String($Value.Data)
        }
        'String' {
            if ($Value.Data -isnot [string]) { throw 'REG_SZ raw data must be a string.' }
            $encoding = 'string'
            $data = [string]$Value.Data
        }
        'ExpandString' {
            if ($Value.Data -isnot [string]) { throw 'REG_EXPAND_SZ raw data must remain an unexpanded string.' }
            $encoding = 'string'
            $data = [string]$Value.Data
        }
        'DWord' {
            if ($Value.Data -isnot [int]) { throw 'REG_DWORD raw data must retain its Int32 type.' }
            $encoding = 'int32-invariant'
            $data = ([int]$Value.Data).ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        'QWord' {
            if ($Value.Data -isnot [long]) { throw 'REG_QWORD raw data must retain its Int64 type.' }
            $encoding = 'int64-invariant'
            $data = ([long]$Value.Data).ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        'MultiString' {
            if ($Value.Data -isnot [string[]]) { throw 'REG_MULTI_SZ raw data must retain its String[] type.' }
            foreach ($entry in $Value.Data) {
                if ($entry -isnot [string]) { throw 'REG_MULTI_SZ contains a non-string element.' }
            }
            $encoding = 'string-array'
            $data = [string[]]$Value.Data
        }
        default { throw "Unsupported registry value kind '$kind'; refusing a lossy receipt." }
    }

    return [ordered]@{ exists = $true; kind = $kind; encoding = $encoding; data = $data }
}

function Get-MarkdownSandboxCrashDumpJsonProperty {
    param(
        [Parameter(Mandatory)][object]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "Crash-dump receipt is missing '$Name'." }
    if ($property.Value -is [System.Array]) { return ,$property.Value }
    return $property.Value
}

function Assert-MarkdownSandboxCrashDumpJsonNames {
    param(
        [Parameter(Mandatory)][object]$Object,
        [Parameter(Mandatory)][string[]]$ExpectedNames,
        [Parameter(Mandatory)][string]$Description
    )

    $actualNames = @($Object.PSObject.Properties | ForEach-Object { [string]$_.Name })
    if ($actualNames.Count -ne $ExpectedNames.Count) {
        throw "$Description has an unexpected property set."
    }
    foreach ($name in $ExpectedNames) {
        if ($actualNames -cnotcontains $name) { throw "$Description is missing or has a mis-cased '$name' property." }
    }
}

function ConvertFrom-MarkdownSandboxCrashDumpValueEnvelope {
    param([Parameter(Mandatory)][object]$Envelope)

    $exists = Get-MarkdownSandboxCrashDumpJsonProperty -Object $Envelope -Name 'exists'
    $kind = Get-MarkdownSandboxCrashDumpJsonProperty -Object $Envelope -Name 'kind'
    $encoding = Get-MarkdownSandboxCrashDumpJsonProperty -Object $Envelope -Name 'encoding'
    $data = Get-MarkdownSandboxCrashDumpJsonProperty -Object $Envelope -Name 'data'
    if ($exists -isnot [bool]) { throw 'Serialized registry value Exists must be Boolean.' }
    if (-not $exists) {
        if ($null -ne $kind -or $null -ne $encoding -or $null -ne $data) {
            throw 'Serialized absent registry value contains unexpected kind, encoding, or data.'
        }
        return [pscustomobject]@{ Exists = $false; Kind = $null; Data = $null }
    }

    switch -Exact ([string]$kind) {
        'None' {
            if ($encoding -cne 'base64' -or $data -isnot [string]) { throw 'REG_NONE receipt must use base64 encoding.' }
            $bytes = [Convert]::FromBase64String([string]$data)
            if ([Convert]::ToBase64String($bytes) -cne [string]$data) { throw 'REG_NONE receipt has noncanonical base64 data.' }
            $typedData = [byte[]]$bytes
        }
        'Binary' {
            if ($encoding -cne 'base64' -or $data -isnot [string]) { throw 'REG_BINARY receipt must use base64 encoding.' }
            $bytes = [Convert]::FromBase64String([string]$data)
            if ([Convert]::ToBase64String($bytes) -cne [string]$data) { throw 'REG_BINARY receipt has noncanonical base64 data.' }
            $typedData = [byte[]]$bytes
        }
        'String' {
            if ($encoding -cne 'string' -or $data -isnot [string]) { throw 'REG_SZ receipt must use string encoding.' }
            $typedData = [string]$data
        }
        'ExpandString' {
            if ($encoding -cne 'string' -or $data -isnot [string]) { throw 'REG_EXPAND_SZ receipt must use unexpanded string encoding.' }
            $typedData = [string]$data
        }
        'DWord' {
            if ($encoding -cne 'int32-invariant' -or $data -isnot [string]) { throw 'REG_DWORD receipt must use invariant Int32 encoding.' }
            $number = 0
            if (-not [int]::TryParse([string]$data, [Globalization.NumberStyles]::AllowLeadingSign, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
                $number.ToString([Globalization.CultureInfo]::InvariantCulture) -cne [string]$data) {
                throw 'REG_DWORD receipt contains an invalid or noncanonical Int32.'
            }
            $typedData = [int]$number
        }
        'QWord' {
            if ($encoding -cne 'int64-invariant' -or $data -isnot [string]) { throw 'REG_QWORD receipt must use invariant Int64 encoding.' }
            $number = [long]0
            if (-not [long]::TryParse([string]$data, [Globalization.NumberStyles]::AllowLeadingSign, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
                $number.ToString([Globalization.CultureInfo]::InvariantCulture) -cne [string]$data) {
                throw 'REG_QWORD receipt contains an invalid or noncanonical Int64.'
            }
            $typedData = [long]$number
        }
        'MultiString' {
            if ($encoding -cne 'string-array' -or $null -eq $data -or $data -is [string] -or $data -isnot [array]) {
                throw 'REG_MULTI_SZ receipt must use an explicit string array.'
            }
            foreach ($entry in $data) {
                if ($entry -isnot [string]) { throw 'REG_MULTI_SZ receipt contains a non-string element.' }
            }
            $typedData = [string[]]@($data)
        }
        default { throw "Serialized registry value has unsupported kind '$kind'." }
    }
    return [pscustomobject]@{ Exists = $true; Kind = [string]$kind; Data = $typedData }
}

function ConvertTo-MarkdownSandboxCrashDumpSnapshotDocument {
    param([Parameter(Mandatory)][object]$Snapshot)

    Assert-MarkdownSandboxCrashDumpSnapshot -Snapshot $Snapshot
    $values = [ordered]@{}
    foreach ($name in @('DumpFolder', 'DumpCount', 'DumpType')) {
        $values[$name] = ConvertTo-MarkdownSandboxCrashDumpValueEnvelope -Value $Snapshot.Values[$name]
    }
    return [ordered]@{
        schemaVersion = 1
        snapshotId = [string]$Snapshot.SnapshotId
        hive = [string]$Snapshot.Hive
        registryView = [string]$Snapshot.RegistryView
        baseSubKey = [string]$Snapshot.BaseSubKey
        appSubKey = [string]$Snapshot.AppSubKey
        targetId = [string]$Snapshot.TargetId
        baseKeyExists = [bool]$Snapshot.BaseKeyExists
        appKeyExists = [bool]$Snapshot.AppKeyExists
        values = $values
    }
}

function ConvertFrom-MarkdownSandboxCrashDumpSnapshotDocument {
    param([Parameter(Mandatory)][object]$Document)

    $target = Get-MarkdownSandboxCrashDumpTarget
    $schemaVersion = Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'schemaVersion'
    if (($schemaVersion -isnot [int] -and $schemaVersion -isnot [long]) -or $schemaVersion -ne 1) {
        throw 'Crash-dump snapshot JSON has an unsupported schema version.'
    }
    Assert-MarkdownSandboxCrashDumpJsonNames -Object $Document -ExpectedNames @(
        'schemaVersion', 'snapshotId', 'hive', 'registryView', 'baseSubKey', 'appSubKey', 'targetId',
        'baseKeyExists', 'appKeyExists', 'values'
    ) -Description 'Crash-dump snapshot JSON'
    $valuesDocument = Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'values'
    Assert-MarkdownSandboxCrashDumpJsonNames -Object $valuesDocument -ExpectedNames $target.ManagedNames -Description 'Crash-dump snapshot values'
    $values = [ordered]@{}
    foreach ($name in $target.ManagedNames) {
        $envelope = Get-MarkdownSandboxCrashDumpJsonProperty -Object $valuesDocument -Name $name
        $values[$name] = ConvertFrom-MarkdownSandboxCrashDumpValueEnvelope -Envelope $envelope
    }
    $snapshot = [pscustomobject][ordered]@{
        SchemaVersion = 1
        SnapshotId = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'snapshotId')
        Hive = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'hive')
        RegistryView = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'registryView')
        BaseSubKey = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'baseSubKey')
        AppSubKey = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'appSubKey')
        TargetId = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'targetId')
        BaseKeyExists = Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'baseKeyExists'
        AppKeyExists = Get-MarkdownSandboxCrashDumpJsonProperty -Object $Document -Name 'appKeyExists'
        Values = $values
    }
    Assert-MarkdownSandboxCrashDumpSnapshot -Snapshot $snapshot
    return $snapshot
}

function ConvertTo-MarkdownSandboxCrashDumpReceiptJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][object]$SetupPlan
    )

    Assert-MarkdownSandboxCrashDumpSetupPlan -Snapshot $Snapshot -SetupPlan $SetupPlan
    $target = Get-MarkdownSandboxCrashDumpTarget
    $expectedValues = [ordered]@{}
    foreach ($name in $target.ManagedNames) {
        $expectedValues[$name] = ConvertTo-MarkdownSandboxCrashDumpValueEnvelope -Value $SetupPlan.ExpectedManagedValues[$name]
    }
    $actions = @()
    foreach ($action in @($SetupPlan.Actions)) {
        $desired = [pscustomobject]@{ Exists = $true; Kind = [string]$action.Kind; Data = $action.Data }
        $actions += [ordered]@{
            operation = [string]$action.Operation
            path = [string]$action.Path
            name = [string]$action.Name
            kind = [string]$action.Kind
            data = ConvertTo-MarkdownSandboxCrashDumpValueEnvelope -Value $desired
            expectedCurrent = ConvertTo-MarkdownSandboxCrashDumpValueEnvelope -Value $action.ExpectedCurrent
        }
    }
    $setupDocument = [ordered]@{
        schemaVersion = 1
        snapshotId = [string]$SetupPlan.SnapshotId
        hive = [string]$SetupPlan.Hive
        registryView = [string]$SetupPlan.RegistryView
        baseSubKey = [string]$SetupPlan.BaseSubKey
        appSubKey = [string]$SetupPlan.AppSubKey
        targetId = [string]$SetupPlan.TargetId
        expectedBaseKeyExists = [bool]$SetupPlan.ExpectedBaseKeyExists
        expectedAppKeyExists = [bool]$SetupPlan.ExpectedAppKeyExists
        expectedManagedValues = $expectedValues
        dumpFolder = [string]$SetupPlan.DumpFolder
        actions = @($actions)
    }
    $receipt = [ordered]@{
        schemaVersion = 1
        targetId = $target.TargetId
        snapshot = ConvertTo-MarkdownSandboxCrashDumpSnapshotDocument -Snapshot $Snapshot
        setupPlan = $setupDocument
    }
    return (ConvertTo-Json -InputObject $receipt -Depth 24 -Compress)
}

function ConvertFrom-MarkdownSandboxCrashDumpReceiptJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Json)

    try { $receipt = ConvertFrom-Json -InputObject $Json -ErrorAction Stop }
    catch { throw "Crash-dump policy receipt is not valid JSON: $($_.Exception.Message)" }
    $target = Get-MarkdownSandboxCrashDumpTarget
    $schemaVersion = Get-MarkdownSandboxCrashDumpJsonProperty -Object $receipt -Name 'schemaVersion'
    if (($schemaVersion -isnot [int] -and $schemaVersion -isnot [long]) -or $schemaVersion -ne 1 -or
        [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $receipt -Name 'targetId') -cne $target.TargetId) {
        throw 'Crash-dump policy receipt has an unsupported schema or target identity.'
    }
    Assert-MarkdownSandboxCrashDumpJsonNames -Object $receipt -ExpectedNames @('schemaVersion', 'targetId', 'snapshot', 'setupPlan') -Description 'Crash-dump policy receipt'
    $snapshotDocument = Get-MarkdownSandboxCrashDumpJsonProperty -Object $receipt -Name 'snapshot'
    $snapshot = ConvertFrom-MarkdownSandboxCrashDumpSnapshotDocument -Document $snapshotDocument
    $planDocument = Get-MarkdownSandboxCrashDumpJsonProperty -Object $receipt -Name 'setupPlan'
    $planSchema = Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'schemaVersion'
    if (($planSchema -isnot [int] -and $planSchema -isnot [long]) -or $planSchema -ne 1) {
        throw 'Crash-dump setup plan JSON has an unsupported schema version.'
    }
    Assert-MarkdownSandboxCrashDumpJsonNames -Object $planDocument -ExpectedNames @(
        'schemaVersion', 'snapshotId', 'hive', 'registryView', 'baseSubKey', 'appSubKey', 'targetId',
        'expectedBaseKeyExists', 'expectedAppKeyExists', 'expectedManagedValues', 'dumpFolder', 'actions'
    ) -Description 'Crash-dump setup plan JSON'
    $planValuesDocument = Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'expectedManagedValues'
    Assert-MarkdownSandboxCrashDumpJsonNames -Object $planValuesDocument -ExpectedNames $target.ManagedNames -Description 'Crash-dump setup-plan expected values'
    $expectedValues = [ordered]@{}
    foreach ($name in $target.ManagedNames) {
        $expectedValues[$name] = ConvertFrom-MarkdownSandboxCrashDumpValueEnvelope -Envelope (Get-MarkdownSandboxCrashDumpJsonProperty -Object $planValuesDocument -Name $name)
    }
    $values = [ordered]@{
        DumpFolder = [pscustomobject]@{ Exists = $true; Kind = 'ExpandString'; Data = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'dumpFolder') }
        DumpCount = [pscustomobject]@{ Exists = $true; Kind = 'DWord'; Data = [int]1 }
        DumpType = [pscustomobject]@{ Exists = $true; Kind = 'DWord'; Data = [int]2 }
    }
    $actionsDocument = Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'actions'
    $actions = @()
    foreach ($actionDocument in @($actionsDocument)) {
        Assert-MarkdownSandboxCrashDumpJsonNames -Object $actionDocument -ExpectedNames @('operation', 'path', 'name', 'kind', 'data', 'expectedCurrent') -Description 'Crash-dump setup action JSON'
        $operation = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $actionDocument -Name 'operation')
        $actionData = ConvertFrom-MarkdownSandboxCrashDumpValueEnvelope -Envelope (Get-MarkdownSandboxCrashDumpJsonProperty -Object $actionDocument -Name 'data')
        $expectedCurrent = ConvertFrom-MarkdownSandboxCrashDumpValueEnvelope -Envelope (Get-MarkdownSandboxCrashDumpJsonProperty -Object $actionDocument -Name 'expectedCurrent')
        if ($operation -ne 'SetValue' -or -not $actionData.Exists) { throw 'Persisted setup plan contains a non-SetValue action.' }
        $actions += [pscustomobject]@{
            Operation = $operation
            Path = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $actionDocument -Name 'path')
            Name = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $actionDocument -Name 'name')
            Kind = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $actionDocument -Name 'kind')
            Data = Copy-MarkdownSandboxCrashDumpPolicyData $actionData.Data
            ExpectedCurrent = $expectedCurrent
        }
    }
    $setupPlan = [pscustomobject][ordered]@{
        SchemaVersion = 1
        SnapshotId = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'snapshotId')
        Hive = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'hive')
        RegistryView = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'registryView')
        BaseSubKey = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'baseSubKey')
        AppSubKey = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'appSubKey')
        TargetId = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'targetId')
        ExpectedBaseKeyExists = Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'expectedBaseKeyExists'
        ExpectedAppKeyExists = Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'expectedAppKeyExists'
        ExpectedManagedValues = $expectedValues
        DumpFolder = [string](Get-MarkdownSandboxCrashDumpJsonProperty -Object $planDocument -Name 'dumpFolder')
        Values = $values
        Actions = @($actions)
    }
    Assert-MarkdownSandboxCrashDumpSetupPlan -Snapshot $snapshot -SetupPlan $setupPlan
    return [pscustomobject]@{ Snapshot = $snapshot; SetupPlan = $setupPlan }
}

function Get-MarkdownSandboxCrashDumpRestoreStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][scriptblock]$ReadKey
    )

    Assert-MarkdownSandboxCrashDumpSnapshot -Snapshot $Snapshot
    $target = Get-MarkdownSandboxCrashDumpTarget
    $failures = @()
    $base = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.BaseKey
    $app = Get-MarkdownSandboxCrashDumpKeyState -ReadKey $ReadKey -Path $target.AppKey
    if ($base.Exists -ne $Snapshot.BaseKeyExists) { $failures += 'baseKeyExistenceMismatch' }
    if ($app.Exists -ne $Snapshot.AppKeyExists) { $failures += 'appKeyExistenceMismatch' }
    if ($app.Exists -and -not $base.Exists) { $failures += 'inconsistentKeyHierarchy' }
    $valueFailures = @()
    foreach ($name in $target.ManagedNames) {
        $current = Get-MarkdownSandboxCrashDumpNamedValue -KeyState $app -Name $name
        if (-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $current -Right $Snapshot.Values[$name])) {
            $valueFailures += $name
        }
    }
    if ($valueFailures.Count -gt 0) { $failures += 'managedValueMismatch:' + ($valueFailures -join ',') }
    return [pscustomobject][ordered]@{
        IsRestored = ($failures.Count -eq 0)
        ManagedValuesRestored = ($valueFailures.Count -eq 0)
        KeyExistenceRestored = ($base.Exists -eq $Snapshot.BaseKeyExists -and $app.Exists -eq $Snapshot.AppKeyExists)
        Failures = @($failures)
        RetainedUnrelatedValues = @($app.Values | Where-Object { $_.Name -notin $target.ManagedNames } | ForEach-Object { $_.Name })
        RetainedSubKeys = @($app.SubKeys)
    }
}
