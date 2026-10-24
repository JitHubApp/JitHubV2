[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$path=Join-Path $PSScriptRoot 'Invoke-MarkdownCorrectnessManualDpiTests.ps1'
$tokens=$null;$errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if($errors.Count -gt 0){throw "Manual DPI runner does not parse: $($errors | Out-String)"}
$source=$ast.Extent.Text
$sharedSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-MarkdownCorrectnessDpiTests.ps1') -Raw
foreach($required in @('TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition',
    '$rawScope=$cache.Activate()', 'finally { $rawScope.Dispose() }',
    "'Escape','--target','MarkdownRenderer'",'RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$processCondition)')){
    if($sharedSource.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Raw-view sample status lookup omits '$required'."}
}
$sharedTokens=$null;$sharedErrors=$null
$sharedAst=[System.Management.Automation.Language.Parser]::ParseInput($sharedSource,[ref]$sharedTokens,[ref]$sharedErrors)
if($sharedErrors.Count -gt 0){throw 'Shared DPI runner does not parse.'}
foreach($setupName in @('Set-ViewportMode','Set-TestAppearance','Invoke-TouchSelectionCheck')){
    $setup=$sharedAst.Find({param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $setupName
    },$true)
    if($null -eq $setup -or $setup.Body.EndBlock.Statements[0].Extent.Text -cne 'Clear-TouchTestSelection'){
        throw "'$setupName' must dismiss the old modal menu and selection before querying controls or preparing geometry."
    }
}
$touchSetup=$sharedAst.Find({param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-TouchSelectionCheck'
},$true).Extent.Text
$verifiedFocus=$touchSetup.IndexOf("Invoke-AppUi @('focus','MarkdownEditor')",[StringComparison]::Ordinal)
$targetSampling=$touchSetup.IndexOf('Find-TextRange -Text $TargetText',[StringComparison]::Ordinal)
if($verifiedFocus -lt 0 -or $targetSampling -le $verifiedFocus){
    throw 'Physical touch must verify native-control foreground focus before sampling the target.'
}
if($source.IndexOf("'Clear-TouchTestSelection'",[StringComparison]::Ordinal) -lt 0){throw 'Manual runner must import the shared popup-cleanup helper.'}
$cleanupFunction=$sharedAst.Find({param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Clear-TouchTestSelection'
},$true)
if($null -eq $cleanupFunction){throw 'Shared gesture cleanup helper is missing.'}
Invoke-Expression $cleanupFunction.Extent.Text
$script:CleanupCalls=[Collections.Generic.List[string]]::new()
function Invoke-AppUi {param([string[]]$Arguments) $script:CleanupCalls.Add(($Arguments -join '|'))}
function Assert-TestSelectionCleared {$script:CleanupCalls.Add('assert-cleared')}
Clear-TouchTestSelection
if(($script:CleanupCalls -join ';') -cne 'send-keys|Escape|--via|send-input;send-keys|Escape|--target|MarkdownRenderer|--via|send-input;assert-cleared'){
    throw 'Gesture cleanup must first close the popup, then focus/clear the document, then verify no selection residue.'
}
$fingerprintFunction=$sharedAst.Find({param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SourceFingerprint'
},$true)
if($null -eq $fingerprintFunction){throw 'Missing shared source-fingerprint function.'}
if($fingerprintFunction.Extent.Text.IndexOf('$PSScriptRoot',[StringComparison]::Ordinal) -ge 0){
    throw 'Imported source fingerprint must not depend on the automatic runner script location.'
}
$script:RepoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Invoke-Expression $fingerprintFunction.Extent.Text
$fingerprint=Get-SourceFingerprint
if($fingerprint.files.Count -lt 14){throw 'Imported source fingerprint did not include the full reviewed source list.'}
Add-Type -AssemblyName WindowsBase
$rectangleFunction=$sharedAst.Find({param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Convert-ManagedTextRectangles'
},$true)
if($null -eq $rectangleFunction){throw 'Missing managed UIA Rect[] converter.'}
Invoke-Expression $rectangleFunction.Extent.Text
$one=@(Convert-ManagedTextRectangles -Rectangles @([System.Windows.Rect]::new(12.5,23.5,45.25,16)))
if($one.Count -ne 1 -or $one[0].x -ne 12.5 -or $one[0].y -ne 23.5 -or $one[0].width -ne 45.25 -or $one[0].height -ne 16){throw 'Managed UIA rectangle coordinates changed.'}
$many=@(Convert-ManagedTextRectangles -Rectangles @([System.Windows.Rect]::Empty,[System.Windows.Rect]::new(1,2,0,5),[System.Windows.Rect]::new(5,6,7,8),[System.Windows.Rect]::new(9,10,11,12)))
if($many.Count -ne 2 -or $many[0].x -ne 5 -or $many[1].x -ne 9){throw 'Managed UIA rectangle filtering or order changed.'}
if(@(Convert-ManagedTextRectangles -Rectangles @()).Count -ne 0){throw 'Empty UIA geometry must remain empty, not fabricated.'}
$stableFunction=$sharedAst.Find({param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-TouchGeometryStable'
},$true)
if($null -eq $stableFunction){throw 'Missing stable touch geometry predicate.'}
Invoke-Expression $stableFunction.Extent.Text
$snapshot=@{visible=$true;rect=@{x=10;y=20;width=30;height=19};viewport=@{x=5;y=10;width=400;height=300};scroll=@{verticalPercent=2;verticalViewSize=20}}
if(-not(Test-TouchGeometryStable $snapshot $snapshot)){throw 'Identical visible geometry must be stable.'}
foreach($changed in @(
    @{visible=$false;rect=$snapshot.rect;viewport=$snapshot.viewport;scroll=$snapshot.scroll},
    @{visible=$true;rect=@{x=10;y=21;width=30;height=19};viewport=$snapshot.viewport;scroll=$snapshot.scroll},
    @{visible=$true;rect=$snapshot.rect;viewport=@{x=5;y=11;width=400;height=300};scroll=$snapshot.scroll},
    @{visible=$true;rect=$snapshot.rect;viewport=$snapshot.viewport;scroll=@{verticalPercent=3;verticalViewSize=20}},
    @{visible=$true;rect=$snapshot.rect;viewport=$snapshot.viewport;scroll=$null}
)){
    if(Test-TouchGeometryStable $snapshot $changed){throw 'Hidden, moving or scroll-changing geometry must not be stable.'}
}
foreach($forbidden in @('Set-DisplayScale','Invoke-SettingsUi','Restore-Scale','ChangeDisplaySettings','SetDisplayConfig','Set-ItemProperty','reg.exe')){
    if($source.IndexOf($forbidden,[StringComparison]::Ordinal) -ge 0){throw "Manual capture runner must not contain '$forbidden'."}
}
foreach($required in @('Assert-ManualDpi','ExpectedAppStartTimeUtc','DisplayMonitorCount() -ne 1',
    'MoveResize([IntPtr]$AppHwnd','RESTORE-APP-BOUNDS','RestoreDpiContext',
    "@('ui','yield','--on','local')",'managedPayload','windowsSettingsChangedByRunner=$false','complete=$false',
    "Wait-RawName -AutomationId 'CurrentSamplePage' -Expected `$script:OriginalAppState.page",'Assert-TestSelectionCleared')){
    if($source.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Manual capture runner omits '$required'."}
}
$function=$ast.Find({param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-ManualCase'
},$true)
if($null -eq $function){throw 'Cannot find the manual interaction case function.'}
Invoke-Expression $function.Extent.Text

# All interaction/identity calls are mocks. The test validates dispatch and
# receipts without loading native code, calling winapp or accessing a desktop.
$script:Results=[Collections.Generic.List[object]]::new()
$script:ObservedTarget=$null
$script:ObservedGesture=$null
$script:IdentityChecks=0
function Assert-ManualDpi {$script:IdentityChecks++;return @{dpi=[int]($ScalePercent*96/100)}}
function Set-ViewportMode {param([string]$Mode) $script:ObservedMode=$Mode}
function Set-TestAppearance {param([string]$Appearance) $script:ObservedAppearance=$Appearance}
function Invoke-TouchSelectionCheck {
    param([string]$TargetText,[string]$Gesture,[int]$Dpi,[string]$ScreenshotName)
    $script:ObservedTarget=$TargetText;$script:ObservedGesture=$Gesture
    return @{actualDpi=$Dpi;screenshot=$ScreenshotName}
}
$cases=@(
    @{scale=100;mode='owned';appearance='light';emoji=$false;gesture='long-press';dpi=96},
    @{scale=100;mode='ancestor';appearance='light';emoji=$false;gesture='double-tap';dpi=96},
    @{scale=150;mode='owned';appearance='light';emoji=$false;gesture='long-press';dpi=144},
    @{scale=150;mode='ancestor';appearance='light';emoji=$false;gesture='double-tap';dpi=144},
    @{scale=150;mode='owned';appearance='dark';emoji=$false;gesture='long-press';dpi=144},
    @{scale=150;mode='ancestor';appearance='dark';emoji=$false;gesture='long-press';dpi=144},
    @{scale=150;mode='owned';appearance='forced-hc-preview';emoji=$false;gesture='long-press';dpi=144},
    @{scale=150;mode='ancestor';appearance='forced-hc-preview';emoji=$false;gesture='long-press';dpi=144},
    @{scale=150;mode='owned';appearance='light';emoji=$true;gesture='long-press';dpi=144},
    @{scale=200;mode='owned';appearance='light';emoji=$false;gesture='long-press';dpi=192},
    @{scale=200;mode='ancestor';appearance='light';emoji=$false;gesture='double-tap';dpi=192}
)
foreach($case in $cases){
    $ScalePercent=$case.scale
    $before=$script:Results.Count
    Invoke-ManualCase -Mode $case.mode -Appearance $case.appearance -Emoji:([bool]$case.emoji)
    if($script:Results.Count -ne $before+1){throw 'Case did not append exactly one receipt.'}
    $receipt=$script:Results[$before]
    $suffix=if($case.emoji){'-emoji-grapheme'}else{''}
    $expectedId="dpi-$($case.scale)-$($case.mode)-$($case.appearance)$suffix"
    $expectedTarget=if($case.emoji){'👩🏽‍💻'}else{'family'}
    if($receipt.id -cne $expectedId -or $receipt.status -ne 'PASS' -or $receipt.actualDpi -ne $case.dpi){throw "Wrong case receipt for $expectedId."}
    if($script:ObservedMode -ne $case.mode -or $script:ObservedAppearance -ne $case.appearance){throw "Wrong viewport or appearance for $expectedId."}
    if($script:ObservedTarget -cne $expectedTarget -or $script:ObservedGesture -ne $case.gesture){throw "Wrong gesture or semantic target for $expectedId."}
}
if($script:IdentityChecks -ne 22){throw 'Every interaction must recheck app identity and real DPI before and after input.'}
'PASS: manual-DPI runner parses, all 11 mocked dispatch/receipt cases use the expected real-DPI targets and gestures, exact app identity is checked before/after each case, cleanup and payload evidence contracts are present, and no Windows display-setting mutation exists. No live UI or native code ran.'
