[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$AppPid,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][long]$AppHwnd,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedAppExePath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedAppStartTimeUtc,
    [Parameter(Mandatory)][ValidateSet(100,150,200)][int]$ScalePercent,
    [string]$ExpectedAppTitle = 'MarkdownRenderer',
    [ValidateRange(640,1440)][int]$NarrowWidthDip = 760,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\correctness\dpi')
)

# The user sets Windows display scaling. This runner changes only the approved
# sample app's bounds, page and controls, restoring those in finally. It neither
# operates Settings nor changes Windows display/theme configuration.
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -lt 7){throw 'The host evidence runner requires PowerShell 7; guest interop compatibility is checked separately.'}

$script:RepoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:RunId='manual-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'),[guid]::NewGuid().ToString('N')
$script:RunDirectory=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $script:RunId
$script:WorkflowId="markdown-manual-dpi-$([guid]::NewGuid().ToString('N'))"
$script:PreviousWorkflowId=$env:WINAPP_UI_WORKFLOW_ID
$script:Results=[Collections.Generic.List[object]]::new()
$script:Commands=[Collections.Generic.List[object]]::new()
$script:Screenshots=[Collections.Generic.List[string]]::new()
$script:OriginalBounds=$null
$script:OriginalDpiContext=[IntPtr]::Zero
$script:SettingsMonitor=[IntPtr]::Zero
$script:UiStarted=$false
$script:OriginalAppState=$null
$script:OriginalViewport=$null
$script:InitialAppIdentity=$null
$script:Source=$null

# Reuse the reviewed checks instead of maintaining a second interaction oracle.
# Only the enumerated function definitions and native type declaration are
# loaded; the automatic runner's parameter block, execution and cleanup do not run.
$automaticPath=Join-Path $PSScriptRoot 'Invoke-MarkdownCorrectnessDpiTests.ps1'
$automaticSource=Get-Content -LiteralPath $automaticPath -Raw
$tokens=$null;$errors=$null
$automaticAst=[System.Management.Automation.Language.Parser]::ParseFile($automaticPath,[ref]$tokens,[ref]$errors)
if($errors.Count -gt 0){throw "The shared DPI runner does not parse: $($errors | Out-String)"}
$interop=[regex]::Match($automaticSource,'(?s)Add-Type\s+-TypeDefinition\s+@[''\"]\r?\n(?<code>.*?)\r?\n[''\"]@')
if(-not $interop.Success){throw 'The shared DPI native declaration was not found.'}
if(-not('MarkdownCorrectnessDpiNative' -as [type])){Add-Type -TypeDefinition $interop.Groups['code'].Value -Language CSharp}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$functionNames=@(
    'Write-JsonFile','Get-WindowInfo','Save-CommandEvidence','Invoke-WinAppChecked','Invoke-AppUi',
    'Get-HwndElement','Find-UniqueAutomationId','Get-CurrentToggle','Set-AppToggle','Set-TestWindowBounds',
    'Convert-ManagedTextRectangles','Find-TextRange','Get-TouchGeometrySnapshot','Test-TouchGeometryStable','Get-SelectionText','Get-SelectionHandles','Wait-ForExactSelection','Assert-TestSelectionCleared','Assert-SelectionGeometry',
    'Wait-RawName','Clear-TouchTestSelection','Set-ViewportMode','Set-TestAppearance','Navigate-ToTouchSample','Save-TestScreenshot',
    'Invoke-TouchSelectionCheck','Get-SourceFingerprint'
)
foreach($functionName in $functionNames){
    $matches=@($automaticAst.FindAll({param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
    },$true))
    if($matches.Count -ne 1){throw "Expected one shared function '$functionName', found $($matches.Count)."}
    Invoke-Expression $matches[0].Extent.Text
}

function Assert-ManualDpi {
    $info=Get-WindowInfo -ProcessId $AppPid -WindowHandle $AppHwnd -ExpectedExePath $ExpectedAppExePath -ExpectedStartTimeUtc $ExpectedAppStartTimeUtc -ExpectedTitle $ExpectedAppTitle
    $expectedDpi=[int]($ScalePercent*96/100)
    if($info.dpi -ne $expectedDpi){throw "The user-set $ScalePercent% test needs actual app DPI $expectedDpi; HWND reports $($info.dpi). No Windows setting was changed by this runner."}
    if([MarkdownCorrectnessDpiNative]::DisplayMonitorCount() -ne 1){throw 'This bounded physical-desktop test requires one active monitor to avoid ambiguous manual display selection.'}
    if($script:SettingsMonitor -ne [IntPtr]::Zero -and [IntPtr]$info.monitor -ne $script:SettingsMonitor){throw 'The sample app moved off the captured test monitor.'}
    return $info
}

function Invoke-ManualCase {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Mode,
        [Parameter(Mandatory)][ValidateSet('light','dark','forced-hc-preview')][string]$Appearance,
        [switch]$Emoji)
    $null=Assert-ManualDpi
    Set-ViewportMode -Mode $Mode
    Set-TestAppearance -Appearance $Appearance
    $target=if($Emoji){'👩🏽‍💻'}else{'family'}
    $gesture=if($Mode -eq 'ancestor' -and $Appearance -eq 'light'){'double-tap'}else{'long-press'}
    $suffix=if($Emoji){'-emoji-grapheme'}else{''}
    $id="dpi-$ScalePercent-$Mode-$Appearance$suffix"
    $check=Invoke-TouchSelectionCheck -TargetText $target -Gesture $gesture -Dpi ([int]($ScalePercent*96/100)) -ScreenshotName "$id.png"
    $null=Assert-ManualDpi
    $script:Results.Add([pscustomobject]@{id=$id;status='PASS';scalePercent=$ScalePercent;actualDpi=$check.actualDpi;viewport=$Mode;appearance=$Appearance;selection=$check})
}

try{
    if(Test-Path -LiteralPath $script:RunDirectory){throw 'Fresh evidence path already exists.'}
    [IO.Directory]::CreateDirectory($script:RunDirectory)|Out-Null
    $script:OriginalDpiContext=[MarkdownCorrectnessDpiNative]::EnterPerMonitorV2()
    $script:InitialAppIdentity=Assert-ManualDpi
    $script:SettingsMonitor=[IntPtr]$script:InitialAppIdentity.monitor
    if([MarkdownCorrectnessDpiNative]::SystemHighContrast()){throw 'Actual OS High Contrast is active; this run must label sample forced colors as preview, not actual OS High Contrast.'}
    if([MarkdownCorrectnessDpiNative]::IsMaximized([IntPtr]$AppHwnd) -or [MarkdownCorrectnessDpiNative]::IsMinimized([IntPtr]$AppHwnd)){throw 'The approved test app must start in its normal window state.'}
    $script:OriginalBounds=[MarkdownCorrectnessDpiNative]::GetBounds([IntPtr]$AppHwnd)
    $page=Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId 'CurrentSamplePage'
    $script:OriginalAppState=[pscustomobject]@{
        page=[string]$page.Current.Name
        dark=Get-CurrentToggle 'ThemeToggle'
        forcedContrast=Get-CurrentToggle 'ForcedHighContrastToggle'
        rtl=Get-CurrentToggle 'RtlToggle'
        textScale=Get-CurrentToggle 'TextScaleToggle'
    }
    $script:Source=Get-SourceFingerprint
    $appDirectory=Split-Path -Parent $script:InitialAppIdentity.exePath
    $managedPayload=foreach($name in @('MarkdownRenderer.Sample.dll','MarkdownRenderer.dll','MarkdownRenderer.Sample.deps.json')) {
        $path=Join-Path $appDirectory $name
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "The expected JIT payload is missing: $path"}
        [pscustomobject]@{name=$name;path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    }
    $env:WINAPP_UI_WORKFLOW_ID=$script:WorkflowId
    $script:UiStarted=$true
    Navigate-ToTouchSample
    $script:OriginalViewport=Get-CurrentToggle 'TouchViewportOwnershipToggle'
    Set-AppToggle -AutomationId 'RtlToggle' -Enabled $false
    Set-AppToggle -AutomationId 'TextScaleToggle' -Enabled $false
    Wait-RawName -AutomationId 'TextScaleStatus' -Expected 'text-scale:1'
    $bounds=Set-TestWindowBounds -Dpi ([int]($ScalePercent*96/100))
    Write-JsonFile (Join-Path $script:RunDirectory 'run-metadata.json') ([ordered]@{
        target='local physical desktop';scaleSource='user-set Windows display scale';scalePercent=$ScalePercent
        app=$script:InitialAppIdentity;managedPayload=@($managedPayload);source=$script:Source;runnerSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        sharedRunnerSha256=(Get-FileHash -LiteralPath $automaticPath -Algorithm SHA256).Hash
        originalAppState=$script:OriginalAppState;originalViewport=$script:OriginalViewport;testBounds=$bounds
        windowsSettingsChangedByRunner=$false;visualReview='UNVERIFIED';generatedUtc=[DateTime]::UtcNow.ToString('o')
    })
    foreach($mode in @('owned','ancestor')){Invoke-ManualCase -Mode $mode -Appearance 'light'}
    if($ScalePercent -eq 150){
        foreach($mode in @('owned','ancestor')){
            Invoke-ManualCase -Mode $mode -Appearance 'dark'
            Invoke-ManualCase -Mode $mode -Appearance 'forced-hc-preview'
        }
        Invoke-ManualCase -Mode 'owned' -Appearance 'light' -Emoji
    }
}catch{
    $script:Results.Add([pscustomobject]@{id='HARNESS';status='FAIL';detail=$_.Exception.ToString()})
}finally{
    if($script:UiStarted){
        $restoreActions=@()
        if($null -ne $script:OriginalAppState){
            $restoreActions+=@(
                @{id='RESTORE-THEME';body={Set-AppToggle -AutomationId 'ThemeToggle' -Enabled ([bool]$script:OriginalAppState.dark)}},
                @{id='RESTORE-FORCED-CONTRAST';body={Set-AppToggle -AutomationId 'ForcedHighContrastToggle' -Enabled ([bool]$script:OriginalAppState.forcedContrast)}},
                @{id='RESTORE-RTL';body={Set-AppToggle -AutomationId 'RtlToggle' -Enabled ([bool]$script:OriginalAppState.rtl)}},
                @{id='RESTORE-TEXT-SCALE';body={Set-AppToggle -AutomationId 'TextScaleToggle' -Enabled ([bool]$script:OriginalAppState.textScale)}}
            )
        }
        if($null -ne $script:OriginalViewport){$restoreActions+=@{id='RESTORE-VIEWPORT';body={Set-AppToggle -AutomationId 'TouchViewportOwnershipToggle' -Enabled ([bool]$script:OriginalViewport)}}}
        if($null -ne $script:OriginalAppState){$restoreActions+=@{id='RESTORE-PAGE';body={
            if($script:OriginalAppState.page -notmatch '^page:(?<page>[A-Za-z0-9]+)$'){throw 'Captured page cannot be restored.'}
            $null=Invoke-AppUi @('invoke',"SampleNav_$($Matches.page)",'--action','select')
            Wait-RawName -AutomationId 'CurrentSamplePage' -Expected $script:OriginalAppState.page
        }}}
        $restoreActions+=@{id='CLEAR-TEST-SELECTION';body={$null=Invoke-AppUi @('send-keys','Escape','--via','send-input');Assert-TestSelectionCleared}}
        foreach($action in $restoreActions){
            try{& $action.body;$script:Results.Add([pscustomobject]@{id=$action.id;status='PASS'})}
            catch{$script:Results.Add([pscustomobject]@{id=$action.id;status='FAIL';detail=$_.Exception.Message})}
        }
        try{$null=Invoke-WinAppChecked @('ui','yield','--on','local')}
        catch{$script:Results.Add([pscustomobject]@{id='YIELD';status='FAIL';detail=$_.Exception.Message})}
    }
    if($null -ne $script:OriginalBounds){
        try{
            # Bounds restoration is safe under exact window identity even if
            # an external display change invalidated this run's test DPI.
            $null=Get-WindowInfo -ProcessId $AppPid -WindowHandle $AppHwnd -ExpectedExePath $ExpectedAppExePath -ExpectedStartTimeUtc $ExpectedAppStartTimeUtc -ExpectedTitle $ExpectedAppTitle
            $r=$script:OriginalBounds
            $actual=[MarkdownCorrectnessDpiNative]::MoveResize([IntPtr]$AppHwnd,$r.Left,$r.Top,$r.Right-$r.Left,$r.Bottom-$r.Top)
            if($actual.Left -ne $r.Left -or $actual.Top -ne $r.Top -or $actual.Right -ne $r.Right -or $actual.Bottom -ne $r.Bottom){throw 'The test app bounds did not restore exactly.'}
            $script:Results.Add([pscustomobject]@{id='RESTORE-APP-BOUNDS';status='PASS'})
        }catch{$script:Results.Add([pscustomobject]@{id='RESTORE-APP-BOUNDS';status='FAIL';detail=$_.Exception.Message})}
    }
    if($script:OriginalDpiContext -ne [IntPtr]::Zero){
        try{[MarkdownCorrectnessDpiNative]::RestoreDpiContext($script:OriginalDpiContext)}
        catch{$script:Results.Add([pscustomobject]@{id='RESTORE-THREAD-DPI-CONTEXT';status='FAIL';detail=$_.Exception.Message})}
    }
    $env:WINAPP_UI_WORKFLOW_ID=$script:PreviousWorkflowId
}

[IO.Directory]::CreateDirectory($script:RunDirectory)|Out-Null
$expectedCases=if($ScalePercent -eq 150){7}else{2}
$passedCases=@($script:Results|Where-Object{$_.status -eq 'PASS' -and $_.id -like 'dpi-*'}).Count
$failed=@($script:Results|Where-Object status -eq 'FAIL').Count
$report=[ordered]@{
    schemaVersion=1;objective='Phase 1 actual user-set Windows display-DPI correctness';runId=$script:RunId;target='local physical desktop'
    scalePercent=$ScalePercent;expectedActualDpi=[int]($ScalePercent*96/100);app=$script:InitialAppIdentity;source=$script:Source
    windowsSettingsChangedByRunner=$false;expectedCases=$expectedCases;passedCases=$passedCases;failed=$failed
    automatedChecksPassed=($failed -eq 0 -and $passedCases -eq $expectedCases -and $script:Screenshots.Count -eq (2*$expectedCases))
    screenshots=@($script:Screenshots.ToArray());results=@($script:Results.ToArray());commands=@($script:Commands.ToArray())
    unverified=@('Visual inspection of every PNG.','User restoration of Windows scaling to the original 100% after all three scale passes.','Actual OS High Contrast is tested separately in Sandbox.')
    complete=$false;generatedUtc=[DateTime]::UtcNow.ToString('o')
}
Write-JsonFile (Join-Path $script:RunDirectory 'results.json') $report
Write-Host "Manual DPI evidence: $script:RunDirectory"
Write-Host "Scale: $ScalePercent% | Interaction cases: $passedCases/$expectedCases | Failures: $failed"
if(-not $report.automatedChecksPassed){exit 1}
exit 0
