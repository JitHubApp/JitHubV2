[CmdletBinding()]
param()

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$adapterPath=Join-Path $PSScriptRoot 'MarkdownSandboxCrashDumpSetup.ps1'
$tokens=$null;$errors=$null
$adapterAst=[Management.Automation.Language.Parser]::ParseFile($adapterPath,[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
. (Join-Path $PSScriptRoot 'MarkdownSandboxCrashDumpPolicy.ps1')
# Import function definitions only. Never execute the adapter's registry,
# filesystem, identity or process-inspection entry point on the host.
foreach($node in $adapterAst.EndBlock.Statements){
    if($node -is [Management.Automation.Language.FunctionDefinitionAst]){Invoke-Expression $node.Extent.Text}
}
$checks=[Collections.Generic.List[string]]::new()
function Check([string]$Name,[scriptblock]$Body){try{& $Body;$checks.Add($Name)}catch{throw "Offline check '$Name' failed: $($_.Exception.Message)`n$($_.ScriptStackTrace)"}}
function Must-Throw([scriptblock]$Body){$thrown=$false;try{& $Body|Out-Null}catch{$thrown=$true};if(-not $thrown){throw 'Expected rejection did not occur.'}}

class MarkdownDumpFakeRoot {
    [hashtable]$State=@{}
    [int]$Writes=0
    [int]$SetAttempts=0
    [int]$FailSetAt=0
    [object]OpenSubKey([string]$Path,[bool]$Writable){
        if(-not $this.State.ContainsKey($Path)){return $null}
        return [MarkdownDumpFakeKey]::new($this,$Path)
    }
    [object]CreateSubKey([string]$Path,[bool]$Writable){
        $parent=[IO.Path]::GetDirectoryName($Path)
        if(-not $this.State.ContainsKey($parent)){$this.State[$parent]=@{Values=@{};SubKeys=@()}}
        if(-not $this.State.ContainsKey($Path)){$this.State[$Path]=@{Values=@{};SubKeys=@()}}
        $leaf=[IO.Path]::GetFileName($Path)
        if($this.State[$parent].SubKeys -notcontains $leaf){$this.State[$parent].SubKeys+=,$leaf}
        return [MarkdownDumpFakeKey]::new($this,$Path)
    }
    [void]DeleteSubKey([string]$Path,[bool]$ThrowIfMissing){
        if(-not $this.State.ContainsKey($Path)){if($ThrowIfMissing){throw 'Missing fake key'};return}
        if($this.State[$Path].Values.Count -or $this.State[$Path].SubKeys.Count){throw 'Fake nonrecursive deletion refuses nonempty keys.'}
        $this.State.Remove($Path);$this.Writes++
        $parent=[IO.Path]::GetDirectoryName($Path);$leaf=[IO.Path]::GetFileName($Path)
        if($this.State.ContainsKey($parent)){$this.State[$parent].SubKeys=@($this.State[$parent].SubKeys|Where-Object{$_ -ne $leaf})}
    }
}
class MarkdownDumpFakeKey {
    [MarkdownDumpFakeRoot]$Root
    [string]$Path
    [int]$ValueCount
    [int]$SubKeyCount
    MarkdownDumpFakeKey([MarkdownDumpFakeRoot]$Root,[string]$Path){$this.Root=$Root;$this.Path=$Path;$this.ValueCount=$Root.State[$Path].Values.Count;$this.SubKeyCount=$Root.State[$Path].SubKeys.Count}
    [string[]]GetValueNames(){return [string[]]@($this.Root.State[$this.Path].Values.Keys)}
    [object]GetValueKind([string]$Name){return [Microsoft.Win32.RegistryValueKind]([Enum]::Parse([Microsoft.Win32.RegistryValueKind],[string]$this.Root.State[$this.Path].Values[$Name].Kind))}
    [object]GetValue([string]$Name,[object]$Default,[object]$Options){return $this.Root.State[$this.Path].Values[$Name].Data}
    [string[]]GetSubKeyNames(){return [string[]]@($this.Root.State[$this.Path].SubKeys)}
    [void]SetValue([string]$Name,[object]$Data,[object]$Kind){
        $this.Root.SetAttempts++
        if($this.Root.FailSetAt -gt 0 -and $this.Root.SetAttempts -eq $this.Root.FailSetAt){$this.Root.FailSetAt=0;throw 'Injected fake SetValue failure'}
        $this.Root.State[$this.Path].Values[$Name]=@{Kind=$Kind.ToString();Data=$Data};$this.Root.Writes++
    }
    [void]DeleteValue([string]$Name,[bool]$ThrowIfMissing){$this.Root.State[$this.Path].Values.Remove($Name);$this.Root.Writes++}
    [void]Flush(){}
    [void]Dispose(){}
}

$context=@{ExpectedGuestComputerName='GUEST';ExpectedHostComputerName='HOST';ComputerName='GUEST';UserName='WDAGUtilityAccount';IsAdministrator=$true;Is64BitProcess=$true;Is64BitOperatingSystem=$true;GuestWorkRoot='C:\WinApp\work'}
Check 'approved guest facts accepted without side effects' {Assert-MarkdownCrashDumpGuestContext @context}
foreach($bad in @(
    @{ComputerName='HOST'},@{ExpectedGuestComputerName='HOST'},@{ComputerName='REPLACEMENT'},
    @{UserName='tester'},@{IsAdministrator=$false},@{Is64BitProcess=$false},@{Is64BitOperatingSystem=$false},@{GuestWorkRoot='C:\Other'}
)){
    $copy=$context.Clone();foreach($key in $bad.Keys){$copy[$key]=$bad[$key]}
    Check ('reject unsafe context '+($bad|ConvertTo-Json -Compress)) {Must-Throw {Assert-MarkdownCrashDumpGuestContext @copy}}
}
foreach($path in @('C:\WinApp\work','C:\WinApp\work-other\fresh','C:\WinApp\work\..\escape','C:\WinApp\work\fresh:stream','\\server\share\fresh','relative\fresh','C:\WinApp\work\%TEMP%\fresh')){
    Check ('reject unsafe run path '+$path) {Must-Throw {Assert-MarkdownCrashDumpFileLocation -GuestWorkRoot 'C:\WinApp\work' -RunDirectory $path}}
}

$values=@(
    @{Kind='String';Data='plain'},@{Kind='ExpandString';Data='%TEMP%\literal'},
    @{Kind='DWord';Data=[int]-1},@{Kind='QWord';Data=[long]::MinValue},
    @{Kind='MultiString';Data=[string[]]@('one','two')},@{Kind='MultiString';Data=[string[]]@()},
    @{Kind='Binary';Data=[byte[]]@(0,128,255)},@{Kind='None';Data=[byte[]]@()}
)
foreach($value in $values){
    Check ('typed raw conversion '+$value.Kind+' '+@($value.Data).Count) {
        $actual=ConvertTo-MarkdownCrashDumpRegistryData -Kind $value.Kind -Data $value.Data
        $expected=[pscustomobject]@{Exists=$true;Kind=$value.Kind;Data=$value.Data}
        if(-not (Test-MarkdownSandboxCrashDumpPolicyValueEqual -Left $expected -Right ([pscustomobject]@{Exists=$true;Kind=$value.Kind;Data=$actual}))){throw 'Raw conversion changed kind/data/type.'}
    }
}
foreach($value in @(@{Kind='DWord';Data=1.5},@{Kind='DWord';Data=[long]4294967295},@{Kind='QWord';Data='1'},@{Kind='Binary';Data=@(-1)},@{Kind='MultiString';Data=@('okay',1)},@{Kind='Unknown';Data=0})){
    Check ('reject lossy raw conversion '+$value.Kind) {Must-Throw {ConvertTo-MarkdownCrashDumpRegistryData -Kind $value.Kind -Data $value.Data}}
}

$target=Get-MarkdownSandboxCrashDumpTarget
$readKey={param($path) Read-MarkdownCrashDumpRegistryKey -Path $path}
foreach($value in $values){
    Check ('actual adapter fake-registry JSON setup/restore '+$value.Kind+' '+@($value.Data).Count) {
        $script:CrashDumpRegistryRoot=[MarkdownDumpFakeRoot]::new()
        $script:CrashDumpRegistryRoot.State[$target.BaseSubKey]=@{Values=@{ForeignGlobal=@{Kind='String';Data='untouched'}};SubKeys=@('MarkdownRenderer.Sample.exe')}
        $script:CrashDumpRegistryRoot.State[$target.AppSubKey]=@{Values=@{DumpFolder=@{Kind=$value.Kind;Data=$value.Data};Foreign=@{Kind='String';Data='keep'}};SubKeys=@('ForeignChild')}
        $before=Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $readKey
        $setup=New-MarkdownSandboxCrashDumpSetupPlan -Snapshot $before -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\unique\Dumps' -PathExists {$false}
        # Use the actual serialization boundary that a second guest process
        # will read, not the original in-memory snapshot or inferred JSON types.
        $json=ConvertTo-MarkdownSandboxCrashDumpReceiptJson -Snapshot $before -SetupPlan $setup
        $roundTrip=ConvertFrom-MarkdownSandboxCrashDumpReceiptJson -Json $json
        Invoke-MarkdownCrashDumpRegistryPlan -Plan $setup -Snapshot $before -SetupPlan $setup -ReadKey $readKey
        $null=Assert-MarkdownCrashDumpManagedValues -Expected $setup -ReadKey $readKey
        $restore=New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $roundTrip.Snapshot -SetupPlan $roundTrip.SetupPlan -ReadKey $readKey
        Invoke-MarkdownCrashDumpRegistryPlan -Plan $restore -Snapshot $roundTrip.Snapshot -SetupPlan $roundTrip.SetupPlan -ReadKey $readKey
        $verified=Get-MarkdownCrashDumpRestoreVerification -Snapshot $roundTrip.Snapshot -ReadKey $readKey
        if(-not $verified.managedValuesRestored -or -not $verified.exactKeyExistenceRestored){throw 'Restore not verified.'}
        if($script:CrashDumpRegistryRoot.State[$target.AppSubKey].Values.Foreign.Data -cne 'keep' -or $script:CrashDumpRegistryRoot.State[$target.BaseSubKey].Values.ForeignGlobal.Data -cne 'untouched' -or $script:CrashDumpRegistryRoot.State[$target.AppSubKey].SubKeys -notcontains 'ForeignChild'){throw 'Unrelated registry state changed.'}
    }
}
Check 'fresh absent keys retained inertly without racy key deletion' {
    $script:CrashDumpRegistryRoot=[MarkdownDumpFakeRoot]::new()
    $before=Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $readKey
    $setup=New-MarkdownSandboxCrashDumpSetupPlan -Snapshot $before -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\fresh\Dumps' -PathExists {$false}
    Invoke-MarkdownCrashDumpRegistryPlan -Plan $setup -Snapshot $before -SetupPlan $setup -ReadKey $readKey
    $restore=New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $before -SetupPlan $setup -ReadKey $readKey
    Invoke-MarkdownCrashDumpRegistryPlan -Plan $restore -Snapshot $before -SetupPlan $setup -ReadKey $readKey
    $verified=Get-MarkdownCrashDumpRestoreVerification -Snapshot $before -ReadKey $readKey
    if($verified.exactKeyExistenceRestored -or @($verified.inertKeysRetained).Count -ne 2 -or @($verified.foreignStateKeysRetained).Count -ne 0){throw 'Inert retained keys were hidden or mislabeled as exact restoration.'}
    if(-not $script:CrashDumpRegistryRoot.State.ContainsKey($target.AppSubKey) -or -not $script:CrashDumpRegistryRoot.State.ContainsKey($target.BaseSubKey)){throw 'Concrete adapter attempted key deletion.'}
}
Check 'concurrent managed-value change prevents all preplanned restore writes' {
    $script:CrashDumpRegistryRoot=[MarkdownDumpFakeRoot]::new()
    $before=Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $readKey
    $setup=New-MarkdownSandboxCrashDumpSetupPlan -Snapshot $before -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\concurrent\Dumps' -PathExists {$false}
    Invoke-MarkdownCrashDumpRegistryPlan -Plan $setup -Snapshot $before -SetupPlan $setup -ReadKey $readKey
    $restore=New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $before -SetupPlan $setup -ReadKey $readKey
    $script:CrashDumpRegistryRoot.State[$target.AppSubKey].Values.DumpType.Data=[int]99
    $writes=$script:CrashDumpRegistryRoot.Writes
    Must-Throw {Invoke-MarkdownCrashDumpRegistryPlan -Plan $restore -Snapshot $before -SetupPlan $setup -ReadKey $readKey}
    if($script:CrashDumpRegistryRoot.Writes -ne $writes -or $script:CrashDumpRegistryRoot.State[$target.AppSubKey].Values.DumpType.Data -ne 99){throw 'Concurrent state was overwritten.'}
}

Check 'UTF8 receipt file preserves Unicode and refuses existing output' {
    $receiptFolder=Join-Path $env:TEMP ('MarkdownDumpReceiptOffline-'+[guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($receiptFolder)|Out-Null
    $receiptFile=Join-Path $receiptFolder 'typed-receipt.json'
    # ASCII source is intentional: PS5.1 reads BOM-less .ps1 files as ANSI.
    # Both expected strings are built from independent Unicode escapes, not
    # from literals that could be corrupted identically before write/read.
    $text='"C:\\WinApp\\work\\\u65e5\u672c\u8a9e-\u0639\u0631\u0628\u064a-\ud83d\ude00\\Dumps"'|ConvertFrom-Json
    $raw='"%TEMP%\\\u65e5\u672c\u8a9e"'|ConvertFrom-Json
    if(-not $text.Contains([char]0x65e5) -or -not $text.Contains([char]0x0639) -or -not $text.Contains([char]0xd83d)){throw 'The Unicode fixture was not constructed with the intended UTF-16 units.'}
    $script:CrashDumpRegistryRoot=[MarkdownDumpFakeRoot]::new()
    $script:CrashDumpRegistryRoot.State[$target.BaseSubKey]=@{Values=@{};SubKeys=@('MarkdownRenderer.Sample.exe')}
    $script:CrashDumpRegistryRoot.State[$target.AppSubKey]=@{Values=@{DumpFolder=@{Kind='ExpandString';Data=$text}};SubKeys=@()}
    $before=Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $readKey
    $setup=New-MarkdownSandboxCrashDumpSetupPlan -Snapshot $before -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\unicode\Dumps' -PathExists {$false}
    $policyJson=ConvertTo-MarkdownSandboxCrashDumpReceiptJson -Snapshot $before -SetupPlan $setup
    Write-MarkdownCrashDumpReceipt -Path $receiptFile -Value ([ordered]@{unicode=$text;rawExpandString=$raw;policyJson=$policyJson})
    $actual=Read-MarkdownCrashDumpReceipt -Path $receiptFile
    if($actual.unicode -cne $text -or $actual.rawExpandString -cne $raw){throw 'Receipt file changed Unicode or expanded a string.'}
    $decoded=ConvertFrom-MarkdownSandboxCrashDumpReceiptJson -Json $actual.policyJson
    if($decoded.Snapshot.Values.DumpFolder.Kind -cne 'ExpandString' -or $decoded.Snapshot.Values.DumpFolder.Data -cne $text){throw 'Durable nested policy receipt changed the original Unicode managed value.'}
    $originalBytes=[IO.File]::ReadAllBytes($receiptFile)
    Must-Throw {Write-MarkdownCrashDumpReceipt -Path $receiptFile -Value @{replacement='forbidden'}}
    if([Convert]::ToBase64String([IO.File]::ReadAllBytes($receiptFile)) -cne [Convert]::ToBase64String($originalBytes)){throw 'Existing receipt bytes were overwritten.'}
    # Leave this tiny uniquely named test artifact for evidence; no recursive cleanup.
}
Check 'actual Prepare catch rolls partial writes back using fake registry only' {
    $prepareTry=@($adapterAst.FindAll({param($node)
        $node -is [Management.Automation.Language.TryStatementAst] -and
        $node.Body.Statements.Count -gt 0 -and
        $node.Body.Statements[0].Extent.Text.StartsWith('Invoke-MarkdownCrashDumpRegistryPlan -Plan $setup')
    },$true))
    if($prepareTry.Count -ne 1){throw 'The actual Prepare setup/catch block was not uniquely found.'}
    $script:CrashDumpRegistryRoot=[MarkdownDumpFakeRoot]::new()
    $script:CrashDumpRegistryRoot.State[$target.BaseSubKey]=@{Values=@{ForeignGlobal=@{Kind='DWord';Data=[int]37}};SubKeys=@('MarkdownRenderer.Sample.exe')}
    $script:CrashDumpRegistryRoot.State[$target.AppSubKey]=@{Values=@{DumpFolder=@{Kind='ExpandString';Data='%TEMP%\original'};Foreign=@{Kind='String';Data='preserved'}};SubKeys=@('Child')}
    $snapshot=Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $readKey
    $setup=New-MarkdownSandboxCrashDumpSetupPlan -Snapshot $snapshot -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\failure\Dumps' -PathExists {$false}
    $script:CrashDumpRegistryRoot.FailSetAt=2
    $run='C:\WinApp\work\failure';$dumpFolder=Join-Path $run 'Dumps';$snapshotPath=Join-Path $run 'registry-before.json';$preparedPath=Join-Path $run 'registry-prepared.json'
    $script:rollbackReceipt=$null
    function Write-MarkdownCrashDumpReceipt {param($Path,$Value);$script:rollbackReceipt=$Value}
    $failed=$false
    try{Invoke-Expression $prepareTry[0].Extent.Text}catch{
        $failed=$true
        if($_.Exception.Message -notlike '*Dump setup failed and rollback was applied*'){throw}
    }
    if(-not $failed -or $null -eq $script:rollbackReceipt -or -not $script:rollbackReceipt.rollbackApplied -or -not $script:rollbackReceipt.verification.managedValuesRestored){throw 'Partial setup failure was not rolled back by the actual catch.'}
    $null=Get-MarkdownCrashDumpRestoreVerification -Snapshot $snapshot -ReadKey $readKey
    if($script:CrashDumpRegistryRoot.State[$target.AppSubKey].Values.Foreign.Data -cne 'preserved' -or $script:CrashDumpRegistryRoot.State[$target.AppSubKey].SubKeys -notcontains 'Child' -or $script:CrashDumpRegistryRoot.State[$target.BaseSubKey].Values.ForeignGlobal.Data -ne 37){throw 'Partial rollback damaged foreign state.'}
}
Check 'foreign value added at key-removal seam is never deleted' {
    $script:CrashDumpRegistryRoot=[MarkdownDumpFakeRoot]::new()
    $before=Get-MarkdownSandboxCrashDumpSnapshot -ReadKey $readKey
    $setup=New-MarkdownSandboxCrashDumpSetupPlan -Snapshot $before -GuestWorkRoot 'C:\WinApp\work' -DumpFolder 'C:\WinApp\work\seam\Dumps' -PathExists {$false}
    Invoke-MarkdownCrashDumpRegistryPlan -Plan $setup -Snapshot $before -SetupPlan $setup -ReadKey $readKey
    $restore=New-MarkdownSandboxCrashDumpRestorePlan -Snapshot $before -SetupPlan $setup -ReadKey $readKey
    foreach($action in $restore.Actions){
        if($action.Operation -ceq 'RemoveKeyIfEmpty'){
            $script:CrashDumpRegistryRoot.State[$target.AppSubKey].Values.ForeignRace=@{Kind='String';Data='must survive'}
        }
        Invoke-MarkdownCrashDumpRegistryAction -Action $action
    }
    if($script:CrashDumpRegistryRoot.State[$target.AppSubKey].Values.ForeignRace.Data -cne 'must survive'){throw 'The key-removal seam destroyed foreign state.'}
    $verified=Get-MarkdownCrashDumpRestoreVerification -Snapshot $before -ReadKey $readKey
    if(-not $verified.managedValuesRestored -or $verified.exactKeyExistenceRestored -or @($verified.foreignStateKeysRetained).Count -ne 2){throw 'Foreign key retention was not reported honestly.'}
}

# Verify the real early approval guard in isolation, before even identity reads.
$prefix=[Collections.Generic.List[string]]::new()
foreach($statement in $adapterAst.EndBlock.Statements){
    if($statement.Extent.Text.StartsWith('$identity = ')){break}
    $prefix.Add($statement.Extent.Text)
}
$guard=[scriptblock]::Create(($prefix -join [Environment]::NewLine))
foreach($modeName in @('Prepare','Restore')){
    Check ('unapproved '+$modeName+' stops before registry/identity') {
        $Mode=$modeName;$RunApprovedSandboxDiagnostic=$false
        Must-Throw {& $guard}
    }
}
[ordered]@{schemaVersion=1;status='PASS_OFFLINE_FAKE_REGISTRY_ONLY';checks=$checks.Count;results=@($checks);realRegistryOpened=$false;guestCommandsRun=$false;uiLaunched=$false}|ConvertTo-Json -Depth 5
