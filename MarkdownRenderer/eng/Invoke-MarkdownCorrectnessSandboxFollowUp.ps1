[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$PriorRunJson,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\correctness'),
    [string]$ProbePath = (Join-Path $PSScriptRoot 'MarkdownCorrectnessSandboxProbe.ps1')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$script:RunInfo = Get-Content -LiteralPath $PriorRunJson -Raw | ConvertFrom-Json
$script:OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$script:RunId = 'followup-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [guid]::NewGuid().ToString('N')
$script:RunDirectory = Join-Path $script:OutputRoot $script:RunId
$script:WorkflowId = "markdown-correctness-followup-$([guid]::NewGuid().ToString('N'))"
$script:PreviousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$script:Results = [Collections.Generic.List[object]]::new()
$script:CommandIndex = 0
$script:AppProcessId = [int]$script:RunInfo.app.processId
$script:AppWindowHandle = [long]$script:RunInfo.app.hwnd
$script:TargetEpoch = [string]$script:RunInfo.targetEpoch
$script:ExpectedExeSha256 = [string]$script:RunInfo.buildBinary.sha256
$script:ExpectedBuildPayloads = @()
$script:PayloadIdentity = $null
$script:ExpectedHostComputerName = [Environment]::MachineName
$script:ExpectedGuestComputerName = ''
$script:ExpectedStartTicks = 0L
$script:GuestProbePath = ''
$script:UiStarted = $false
$script:OriginalHighContrast = $null
$script:OriginalAppState = $null

function Write-JsonFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 32) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Get-Property {
    param([AllowNull()][object]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Required {
    param([AllowNull()][object]$Object, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Context)
    $value = Get-Property $Object $Name
    if ($null -eq $value) { throw "$Context is missing '$Name'." }
    return $value
}

$recordedPayloadIdentity = Get-Property $script:RunInfo 'payloadIdentity'
if ($null -ne $recordedPayloadIdentity) {
    $script:ExpectedBuildPayloads = @(Get-Property $recordedPayloadIdentity 'host')
}

function Save-CommandEvidence {
    param([string[]]$Arguments, [int]$ExitCode, [string]$Output)
    $script:CommandIndex++
    $label = ($Arguments -join ' ') -replace '[^A-Za-z0-9._-]+', '-'
    if ($label.Length -gt 100) { $label = $label.Substring(0, 100) }
    $path = Join-Path $script:RunDirectory ('commands\{0:D4}-{1}.txt' -f $script:CommandIndex, $label)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
    [IO.File]::WriteAllText($path, "exitCode=$ExitCode`r`n$Output", [Text.UTF8Encoding]::new($false))
    return $path
}

function Invoke-RedirectedUtf8Process {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments
    )
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $null = $startInfo.ArgumentList.Add([string]$argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start '$FileName'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

function Invoke-NativeChecked {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Json, [switch]$AllowNonZero)
    $argv = @($Arguments)
    if ($Json -and $argv -notcontains '--json' -and $argv -notcontains '--') { $argv += '--json' }
    $command = Get-Command -Name 'winapp' -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $fileName = if (-not [string]::IsNullOrWhiteSpace([string]$command.Source)) { [string]$command.Source } else { [string]$command.Path }
    if ([string]::IsNullOrWhiteSpace($fileName)) { throw 'Could not resolve the winapp executable path.' }
    $processResult = Invoke-RedirectedUtf8Process -FileName $fileName -Arguments $argv
    $exitCode = [int]$processResult.ExitCode
    $stdout = [string]$processResult.StandardOutput
    $stderr = [string]$processResult.StandardError
    $output = if ([string]::IsNullOrEmpty($stderr)) { $stdout } else { $stdout + [Environment]::NewLine + '[stderr]' + [Environment]::NewLine + $stderr }
    $evidence = Save-CommandEvidence -Arguments $argv -ExitCode $exitCode -Output $output
    if ($exitCode -ne 0 -and -not $AllowNonZero) { throw "winapp exited $exitCode (evidence $evidence): $output" }
    if (-not $Json) { return [pscustomobject]@{ exitCode = $exitCode; output = $output; evidence = $evidence } }
    $trimmed = $stdout.Trim()
    $match = [regex]::Match($trimmed, '(?s)(\{|\[)')
    if (-not $match.Success) { throw "winapp returned no JSON (evidence $evidence): $output" }
    try { $value = ConvertFrom-Json -InputObject $trimmed.Substring($match.Index) }
    catch { throw "winapp returned malformed JSON (evidence $evidence): $_`n$output" }
    return [pscustomobject]@{ exitCode = $exitCode; value = $value; output = $output; evidence = $evidence }
}

function Assert-SandboxEpoch {
    param([string]$Phase)
    $snapshot = (Invoke-NativeChecked @('target','snapshot','sandbox') -Json).value
    $running = [bool](Get-Required $snapshot 'running' "Sandbox snapshot $Phase")
    $target = Get-Required $snapshot 'executionTarget' "Sandbox snapshot $Phase"
    $epoch = [string](Get-Required $target 'epoch' "Sandbox execution target $Phase")
    $capabilities = Get-Required $snapshot 'capabilities' "Sandbox snapshot $Phase"
    $architecture = [string](Get-Required $capabilities 'architecture' "Sandbox capabilities $Phase")
    $desktop = Get-Required $snapshot 'desktop' "Sandbox desktop $Phase"
    $inputReady = [bool](Get-Required $desktop 'effectiveInputReady' "Sandbox desktop $Phase")
    $captureReady = [bool](Get-Required $desktop 'effectiveCaptureReady' "Sandbox desktop $Phase")
    if (-not $running) { throw "Approved Sandbox is not running ($Phase)." }
    if ($epoch -cne $script:TargetEpoch) { throw "Sandbox epoch changed ($Phase): expected $($script:TargetEpoch), got $epoch." }
    if ($architecture -ne 'x64') { throw "Sandbox architecture changed ($Phase): expected x64, got $architecture." }
    if (-not $inputReady -or -not $captureReady) { throw "Approved Sandbox input/capture is no longer ready ($Phase)." }
    return $snapshot
}

function Assert-AppIdentity {
    $null = Assert-SandboxEpoch 'while validating the app window'
    $windows = @((Invoke-NativeChecked @('ui','list-windows','--on','sandbox') -Json).value)
    $matches = @($windows | Where-Object {
        [int]$_.processId -eq $script:AppProcessId -and [long]$_.hwnd -eq $script:AppWindowHandle -and
        [string]$_.processName -eq [string]$script:RunInfo.app.processName -and [string]$_.title -eq [string]$script:RunInfo.app.title
    })
    if ($matches.Count -ne 1) { throw "The exact captured guest PID/HWND/process/title is no longer present; found $($matches.Count)." }
    return $matches[0]
}

function Assert-GuestPayloadIdentity {
    $recordedIdentity = Get-Property $script:RunInfo 'payloadIdentity'
    if ([string](Get-Property $recordedIdentity 'status') -cne 'MATCH' -or $script:ExpectedBuildPayloads.Count -lt 3) {
        throw 'The prior run receipt does not contain a matched host/guest identity for the managed sample and renderer payloads.'
    }
    $receipt = Invoke-GuestProbe -Mode 'PayloadHashes'
    if ([string]$receipt.mode -cne 'PayloadHashes' -or [int]$receipt.processId -ne $script:AppProcessId) {
        throw 'Fresh guest payload identity is not scoped to the captured MarkdownRenderer.Sample process.'
    }
    $guestFiles = @($receipt.files)
    $matches = [Collections.Generic.List[object]]::new()
    foreach ($expected in $script:ExpectedBuildPayloads) {
        $name = [string]$expected.name
        if ([string]::IsNullOrWhiteSpace($name) -or [string]$expected.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [int64]$expected.length -le 0) {
            throw "Prior host payload receipt contains an invalid expected identity for '$name'."
        }
        $sameName = @($guestFiles | Where-Object { [string]$_.name -ceq $name })
        if ($sameName.Count -ne 1) { throw "Fresh guest payload receipt must contain exactly one '$name'; found $($sameName.Count)." }
        $actual = $sameName[0]
        if ([int64]$actual.length -ne [int64]$expected.length -or [string]$actual.sha256 -cne [string]$expected.sha256) {
            throw "Fresh guest payload '$name' differs from the main run's host build receipt: expected $($expected.sha256)/$($expected.length), found $($actual.sha256)/$($actual.length)."
        }
        $matches.Add([pscustomobject]@{name=$name;sha256=[string]$actual.sha256;length=[int64]$actual.length;guestPath=[string]$actual.path;status='MATCH'})
    }
    return [pscustomobject]@{status='MATCH';processId=[int]$receipt.processId;executablePath=[string]$receipt.executablePath;files=@($matches.ToArray())}
}

function Stage-GuestProbe {
    $snapshot=Assert-SandboxEpoch 'before staging the guest probe'
    $workRoot=[string](Get-Required $snapshot 'workRoot' 'Sandbox snapshot')
    if([string]::IsNullOrWhiteSpace($workRoot)){throw 'Sandbox snapshot contains no workRoot; refusing to guess a guest path.'}
    $relativePath="correctness\$($script:RunId)\MarkdownCorrectnessSandboxProbe.ps1"
    $null=Invoke-NativeChecked @('target','push','sandbox',[IO.Path]::GetFullPath($ProbePath),$relativePath)
    $script:GuestProbePath=Join-Path $workRoot $relativePath
}

function Get-GuestComputerName {
    $r=Invoke-NativeChecked @('target','exec','sandbox','--json','--','powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-Command','[Environment]::MachineName') -Json
    $name=$null
    foreach($field in @('stdout','output','result')){
        $value=Get-Property $r.value $field
        if($value -is [string] -and -not [string]::IsNullOrWhiteSpace($value)){$name=$value;break}
    }
    if($null -eq $name -and $r.value -is [string]){$name=[string]$r.value}
    if([string]::IsNullOrWhiteSpace($name)) { throw "Could not capture guest computer name from Sandbox target exec: $($r.output)" }
    $lines=@($name -split "`r?`n"|ForEach-Object{$_.Trim()}|Where-Object{$_})
    if($lines.Count -ne 1){throw "Sandbox target exec returned an ambiguous computer name: '$name'."}
    $name=$lines[0]
    if([String]::Equals($name,$script:ExpectedHostComputerName,[StringComparison]::OrdinalIgnoreCase)){
        throw 'Sandbox target exec reported the host computer name; refusing any native or UI operation.'
    }
    return $name
}

function Invoke-Ui {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Json)
    $null = Assert-AppIdentity
    $argv = @('ui') + $Arguments + @('--on','sandbox','-a',[string]$script:AppProcessId)
    return Invoke-NativeChecked -Arguments $argv -Json:$Json
}

function Invoke-UiValue {
    param([Parameter(Mandatory)][string]$AutomationId)
    $r = Invoke-Ui @('get-value',$AutomationId) -Json
    foreach ($name in @('text','value','name')) {
        $p = Get-Property $r.value $name
        if ($null -ne $p) { return [string]$p }
    }
    throw "UIA get-value '$AutomationId' returned no text/value/name."
}

function Invoke-UiProperty {
    param([Parameter(Mandatory)][string]$AutomationId, [Parameter(Mandatory)][string]$Property)
    $r = Invoke-Ui @('get-property',$AutomationId,'--property',$Property) -Json
    $properties = Get-Property $r.value 'properties'
    $value = Get-Property $properties $Property
    if ($null -eq $value) { throw "UIA did not report property '$Property' for '$AutomationId'." }
    return $value
}

function Invoke-GuestProbe {
    param(
        [Parameter(Mandatory)][ValidateSet('Identity','PayloadHashes','ResizeWindow','Range','PointRange','Bounds','ScrollSemantic','State','ExpectedRange','StationaryEdge','ScrollToTop','RawName','HighContrastRead','HighContrastSet')][string]$Mode,
        [string]$Viewport = 'owned', [string]$Needle = '', [string]$AutomationId = '',
        [string]$StartNeedle = '', [string]$StartEndpoint = 'Start', [string]$EndNeedle = '', [string]$EndEndpoint = 'End',
        [uint32]$PointerId = 73, [uint32]$HighContrastFlags = 0, [AllowNull()][string]$HighContrastScheme = $null,
        [int]$WindowWidthPixels = 0, [int]$WindowHeightPixels = 0
    )
    $null = Assert-SandboxEpoch "before guest probe $Mode"
    $args = @('target','exec','sandbox','--json','--','powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-STA','-File',$script:GuestProbePath,
        '-Mode',$Mode,'-ProcessId',[string]$script:AppProcessId,'-WindowHandle',[string]$script:AppWindowHandle,'-ExpectedExeSha256',$script:ExpectedExeSha256,
        '-ExpectedGuestComputerName',$script:ExpectedGuestComputerName,'-ExpectedHostComputerName',$script:ExpectedHostComputerName)
    if ($script:ExpectedStartTicks -gt 0) { $args += @('-ExpectedStartTicks',[string]$script:ExpectedStartTicks) }
    if ($Mode -in @('Range','ScrollSemantic','StationaryEdge','ExpectedRange','ScrollToTop','State')) { $args += @('-ViewportMode',$Viewport) }
    if (-not [string]::IsNullOrEmpty($Needle)) { $args += @('-Needle',$Needle) }
    if (-not [string]::IsNullOrEmpty($AutomationId)) { $args += @('-AutomationId',$AutomationId) }
    if (-not [string]::IsNullOrEmpty($StartNeedle)) { $args += @('-StartNeedle',$StartNeedle) }
    if ($StartEndpoint -ne 'Start') { $args += @('-StartEndpoint',$StartEndpoint) }
    if (-not [string]::IsNullOrEmpty($EndNeedle)) { $args += @('-EndNeedle',$EndNeedle) }
    if ($EndEndpoint -ne 'End') { $args += @('-EndEndpoint',$EndEndpoint) }
    if ($Mode -eq 'StationaryEdge') { $args += @('-PointerId',[string]$PointerId) }
    if ($Mode -eq 'ResizeWindow') { $args += @('-WindowWidthPixels',[string]$WindowWidthPixels,'-WindowHeightPixels',[string]$WindowHeightPixels) }
    if ($Mode -eq 'HighContrastSet') {
        $args += @('-HighContrastFlags',[string]$HighContrastFlags)
        if (-not [string]::IsNullOrEmpty($HighContrastScheme)) { $args += @('-HighContrastScheme',$HighContrastScheme) }
    }
    $r = Invoke-NativeChecked -Arguments $args -Json
    $payload = $null
    foreach ($name in @('stdout','output','result')) {
        $p = Get-Property $r.value $name
        if ($p -is [string] -and -not [string]::IsNullOrWhiteSpace($p)) { $payload = $p; break }
    }
    if ($null -eq $payload -and $r.value -is [string]) { $payload = [string]$r.value }
    if ($null -eq $payload -and $null -ne (Get-Property $r.value 'mode')) { return $r.value }
    if ([string]::IsNullOrWhiteSpace($payload)) { throw "Sandbox guest probe returned no payload: $($r.output)" }
    $jsonMatch = [regex]::Match($payload.Trim(), '(?s)(\{.*\})\s*$')
    if (-not $jsonMatch.Success) { throw "Sandbox guest probe output was not JSON: $payload" }
    try { return ConvertFrom-Json -InputObject $jsonMatch.Groups[1].Value }
    catch { throw "Sandbox guest probe returned malformed JSON: $_`n$payload" }
}

function Set-ToggleState {
    param([Parameter(Mandatory)][string]$AutomationId, [Parameter(Mandatory)][bool]$Enabled)
    $state = [string](Invoke-UiProperty -AutomationId $AutomationId -Property 'ToggleState')
    $isOn = $state -in @('On','1','true','True')
    if ($isOn -ne $Enabled) {
        $action = if ($Enabled) { 'toggle-on' } else { 'toggle-off' }
        $null = Invoke-Ui @('invoke',$AutomationId,'--action',$action)
    }
    $actual = [string](Invoke-UiProperty -AutomationId $AutomationId -Property 'ToggleState')
    if (($actual -in @('On','1','true','True')) -ne $Enabled) { throw "Toggle '$AutomationId' did not reach requested state $Enabled (got '$actual')." }
}

function Navigate-TouchPage {
    $null = Invoke-Ui @('invoke','SampleNav_TouchSelection','--action','select')
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $text = Invoke-UiValue 'MarkdownRenderer'
        $page = Invoke-GuestProbe -Mode 'RawName' -AutomationId 'CurrentSamplePage'
        if ($text.Contains('Press and hold a word') -and $page.name -eq 'page:TouchSelection') { return $text }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'TouchSelection did not become the active sample page with its fixture text.'
}

function Get-RangeCenter {
    param([Parameter(Mandatory)][string]$Text)
    $r = Invoke-GuestProbe -Mode 'Range' -Needle $Text
    if ($r.text -cne $Text) { throw "UIA text search for '$Text' resolved '$($r.text)' instead." }
    $rect = $r.rect
    if ($null -eq $rect -or $rect.width -le 0 -or $rect.height -le 0) { throw "UIA returned empty geometry for '$Text'." }
    return [pscustomobject]@{x=[int][Math]::Floor($rect.x+$rect.width/2);y=[int][Math]::Floor($rect.y+$rect.height/2);rect=$rect}
}

function Invoke-PointRangeCase {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Viewport)
    $null=Navigate-TouchPage
    Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($Viewport -eq 'ancestor')
    $null=Invoke-GuestProbe -Mode 'ScrollToTop' -Viewport $Viewport
    $identity=Invoke-GuestProbe -Mode 'Identity'
    try {
        $null=Invoke-GuestProbe -Mode 'ResizeWindow' -WindowWidthPixels ([int][Math]::Ceiling(760*$identity.dpi/96.0)) -WindowHeightPixels ([int]$identity.windowBounds.height)
        $pointRange=Invoke-GuestProbe -Mode 'PointRange' -Needle 'family'
        if($pointRange.text -cne 'family' -or $pointRange.referenceText -cne 'family'){
            throw 'UIA RangeFromPoint must resolve the exact painted family word through the real COM provider.'
        }
        $bounds=Invoke-GuestProbe -Mode 'Bounds' -Needle 'Native menu'
        if($bounds.text -cne 'Native menu' -or [int]$bounds.rectangleCount -ne 0){
            throw 'The offscreen diagram semantic label must remain searchable without publishing visible bounding rectangles.'
        }
        $repeat=Invoke-GuestProbe -Mode 'PointRange' -Needle 'family'
        $shot=Save-Screenshot -Name "screenshots\uia-point-range-$Viewport.png"
        $scroll=Invoke-GuestProbe -Mode 'ScrollSemantic' -Needle 'Native menu' -Viewport $Viewport
        if($scroll.text -cne 'Native menu' -or -not [bool]$scroll.after.verticallyScrollable -or
           [double]$scroll.after.verticalPercent -le [double]$scroll.before.verticalPercent){
            throw 'Scrolling the clipped semantic label must use its logical block location and advance the viewport, not jump to the document origin.'
        }
        # Query without scrolling again: the first label in the same diagram
        # must now be visible near the viewport top, proving block positioning.
        $blockBounds=Invoke-GuestProbe -Mode 'Bounds' -Needle 'One-finger pan'
        if($blockBounds.text -cne 'One-finger pan' -or [int]$blockBounds.rectangleCount -ne 1){throw 'Semantic scrolling did not reveal the first label of the target diagram block.'}
        $label=$blockBounds.rectangles[0];$rendererViewport=$scroll.after.rendererRect
        $topError=[double]$label.y-[double]$rendererViewport.y
        $centerX=[double]$label.x+[double]$label.width/2;$centerY=[double]$label.y+[double]$label.height/2
        if($topError -lt 0 -or $topError -gt (64*[double]$scroll.after.dpi/96.0) -or
           $centerX -lt [double]$rendererViewport.x -or $centerX -gt ([double]$rendererViewport.x+[double]$rendererViewport.width) -or
           $centerY -lt [double]$rendererViewport.y -or $centerY -gt ([double]$rendererViewport.y+[double]$rendererViewport.height)){
            throw 'The first diagram label is not visible at the logical block scroll destination.'
        }
        $scrollShot=Save-Screenshot -Name "screenshots\uia-clipped-range-scroll-$Viewport.png"
        return [pscustomobject]@{
            viewport=$Viewport;pointRange=$pointRange;offscreenDiagramBounds=$bounds
            repeatedPointRange=$repeat;screenshot=$shot;semanticScroll=$scroll;scrollDestinationBounds=$blockBounds;semanticScrollScreenshot=$scrollShot
        }
    }
    finally {
        $null=Invoke-GuestProbe -Mode 'ResizeWindow' -WindowWidthPixels ([int]$identity.windowBounds.width) -WindowHeightPixels ([int]$identity.windowBounds.height)
    }
}

function Get-CurrentState {
    param([Parameter(Mandatory)][string]$Viewport)
    return Invoke-GuestProbe -Mode 'State' -Viewport $Viewport
}

function Assert-ExactSelection {
    param([Parameter(Mandatory)][string]$Expected,[ValidateSet('owned','ancestor')][string]$Viewport='owned')
    $state = Get-CurrentState -Viewport $Viewport
    if ($state.selection -cne $Expected -or $state.rangeCount -ne 1) { throw "TextPattern selected '$($state.selection)' ($($state.rangeCount) ranges), expected one exact '$Expected' range." }
    if (@($state.handles).Count -ne 2) { throw "Expected two visible selection handles, got $(@($state.handles).Count)." }
    $minimum = 44.0 * ([double]$state.dpi / 96.0)
    foreach ($handle in $state.handles) {
        if ($handle.offscreen -or $handle.width -lt $minimum -or $handle.height -lt $minimum) { throw "Handle $($handle.automationId) fails the 44 epx target at DPI $($state.dpi)." }
    }
    return $state
}

function Get-RangeBoundaryPoint {
    param([Parameter(Mandatory)][string]$Text,[Parameter(Mandatory)][ValidateSet('Start','End')][string]$Endpoint)
    $range=Invoke-GuestProbe -Mode 'Range' -Needle $Text
    if($range.text -cne $Text){throw "UIA range for '$Text' resolved '$($range.text)'."}
    $rect=$range.rect
    if($null -eq $rect -or $rect.width -le 2 -or $rect.height -le 2){throw "UIA returned unusable bounds for '$Text'."}
    $x=if($Endpoint -eq 'Start'){[int][Math]::Ceiling($rect.x+1)}else{[int][Math]::Floor($rect.x+$rect.width-1)}
    # The renderer anchors handles at the text rectangle's bottom edge, not
    # its midpoint. Keep the target on that same caret baseline even when
    # source and target runs have different heights or wrap onto another row.
    return [pscustomobject]@{x=$x;y=[int][Math]::Floor($rect.y+$rect.height);rect=$rect;endpoint=$Endpoint}
}

function Get-HandleDragDestination {
    param(
        [Parameter(Mandatory)]$InitialState,
        [Parameter(Mandatory)][ValidateSet('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle')][string]$HandleAutomationId,
        [Parameter(Mandatory)][ValidateSet('Start','End')][string]$SourceEndpoint,
        [Parameter(Mandatory)]$TargetCaret
    )
    if($InitialState.rangeCount -ne 1 -or @($InitialState.selectionRects).Count -ne 1){throw 'Handle-offset calibration requires one selected text range with one visible line rectangle.'}
    $handles=@($InitialState.handles|Where-Object automationId -eq $HandleAutomationId)
    if($handles.Count -ne 1 -or $handles[0].offscreen){throw "Cannot calibrate the touch offset for visible handle '$HandleAutomationId'."}
    $selectionRect=$InitialState.selectionRects[0]
    $handle=$handles[0]
    $caretX=if($SourceEndpoint -eq 'Start'){[double]$selectionRect.x}else{[double]($selectionRect.x+$selectionRect.width)}
    $caretY=[double]($selectionRect.y+$selectionRect.height)
    $fingerX=[double]($handle.x+$handle.width/2)
    $fingerY=[double]($handle.y+$handle.height/2)
    $offsetX=$fingerX-$caretX;$offsetY=$fingerY-$caretY
    $scale=[double]$InitialState.dpi/96.0
    if($scale -le 0){throw 'Handle-offset calibration received an invalid window DPI.'}
    return [pscustomobject]@{
        x=[int][Math]::Round([double]$TargetCaret.x+$offsetX)
        y=[int][Math]::Round([double]$TargetCaret.y+$offsetY)
        sourceCaret=@{x=$caretX;y=$caretY};fingerCenter=@{x=$fingerX;y=$fingerY}
        fingerToCaretOffsetDip=@{x=($offsetX/$scale);y=($offsetY/$scale)}
        targetCaret=@{x=$TargetCaret.x;y=$TargetCaret.y};endpoint=$TargetCaret.endpoint
    }
}

function Invoke-HandleCrossingCase {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Viewport)
    $null=Navigate-TouchPage
    Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($Viewport -eq 'ancestor')
    Start-Sleep -Milliseconds 180
    $null=Invoke-GuestProbe -Mode 'ScrollToTop' -Viewport $Viewport

    # Move the logical Start endpoint beyond the selected word's End. The
    # expected range is calculated independently from UIA TextPattern endpoints.
    $thenPoint=Get-RangeCenter 'then'
    $null=Invoke-Ui @('touch','MarkdownRenderer','--gesture','long-press','--hold-ms','700','--at',"$($thenPoint.x),$($thenPoint.y)")
    $selected=Get-CurrentState -Viewport $Viewport
    if($selected.selection -cne 'then' -or $selected.rangeCount -ne 1){throw "Long press selected '$($selected.selection)' in $Viewport mode; expected 'then'."}
    $expectedForward=Invoke-GuestProbe -Mode 'ExpectedRange' -StartNeedle 'then' -StartEndpoint 'End' -EndNeedle 'drag' -EndEndpoint 'End'
    $forwardCaret=Get-RangeBoundaryPoint 'drag' 'End'
    $forwardTarget=Get-HandleDragDestination -InitialState $selected -HandleAutomationId 'MarkdownSelectionStartHandle' -SourceEndpoint 'Start' -TargetCaret $forwardCaret
    $null=Invoke-Ui @('touch','MarkdownSelectionStartHandle','--gesture','swipe','--to-point',"$($forwardTarget.x),$($forwardTarget.y)",'--duration-ms','350')
    $forward=Assert-ExactSelection -Expected ([string]$expectedForward.text) -Viewport $Viewport

    # Start a separate selection then move its logical End before its Start.
    # This is the opposite crossing direction; compare exact text, not length.
    $null=Invoke-GuestProbe -Mode 'ScrollToTop' -Viewport $Viewport
    $usePoint=Get-RangeCenter 'Use'
    $null=Invoke-Ui @('touch','MarkdownRenderer','--gesture','long-press','--hold-ms','700','--at',"$($usePoint.x),$($usePoint.y)")
    $selectedUse=Get-CurrentState -Viewport $Viewport
    if($selectedUse.selection -cne 'Use' -or $selectedUse.rangeCount -ne 1){throw "Long press selected '$($selectedUse.selection)' in $Viewport mode; expected 'Use'."}
    $expectedBackward=Invoke-GuestProbe -Mode 'ExpectedRange' -StartNeedle 'Use' -StartEndpoint 'Start' -EndNeedle 'Press' -EndEndpoint 'Start'
    $pressCaret=Get-RangeBoundaryPoint 'Press' 'Start'
    $pressStart=Get-HandleDragDestination -InitialState $selectedUse -HandleAutomationId 'MarkdownSelectionEndHandle' -SourceEndpoint 'End' -TargetCaret $pressCaret
    $null=Invoke-Ui @('touch','MarkdownSelectionEndHandle','--gesture','swipe','--to-point',"$($pressStart.x),$($pressStart.y)",'--duration-ms','350')
    $backward=Assert-ExactSelection -Expected ([string]$expectedBackward.text) -Viewport $Viewport
    $shot=Save-Screenshot -Name "screenshots\handle-crossing-$Viewport.png"
    return [pscustomobject]@{
        viewport=$Viewport
        forwardCrossing=@{initial='then';movedHandle='MarkdownSelectionStartHandle';target='drag/end';fingerToCaretOffsetDip=$forwardTarget.fingerToCaretOffsetDip;expected=$expectedForward.text;actual=$forward.selection;match=($forward.selection -ceq $expectedForward.text)}
        backwardCrossing=@{initial='Use';movedHandle='MarkdownSelectionEndHandle';target='Press/start';fingerToCaretOffsetDip=$pressStart.fingerToCaretOffsetDip;expected=$expectedBackward.text;actual=$backward.selection;match=($backward.selection -ceq $expectedBackward.text)}
        forwardHandles=@($forward.handles);backwardHandles=@($backward.handles);screenshot=$shot
    }
}

function Test-Case {
    param([Parameter(Mandatory)][string]$Id,[Parameter(Mandatory)][string]$Name,[Parameter(Mandatory)][string]$Requirement,[Parameter(Mandatory)][scriptblock]$Body)
    $start=[DateTime]::UtcNow
    try {
        $actual=& $Body
        $script:Results.Add([pscustomobject]@{id=$Id;name=$Name;requirement=$Requirement;status='PASS';actual=$actual;detail=$null;startedUtc=$start.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})
    } catch {
        $message=$_.Exception.Message
        $unverified=$message.StartsWith('UNVERIFIED: ',[StringComparison]::Ordinal)
        $status=if($unverified){'UNVERIFIED'}else{'FAIL'}
        $detail=if($unverified){$message.Substring('UNVERIFIED: '.Length)}else{$message}
        $script:Results.Add([pscustomobject]@{id=$Id;name=$Name;requirement=$Requirement;status=$status;actual=$null;detail=$detail;startedUtc=$start.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})
    }
}

function Add-Unverified {
    param([string]$Id,[string]$Name,[string]$Reason)
    $script:Results.Add([pscustomobject]@{id=$Id;name=$Name;requirement='Not proven by this bounded Sandbox follow-up';status='UNVERIFIED';actual=$null;detail=$Reason;startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})
}

function Save-Screenshot {
    param([Parameter(Mandatory)][string]$Name)
    $path=Join-Path $script:RunDirectory $Name
    if(Test-Path -LiteralPath $path){throw "Refusing to overwrite screenshot $path."}
    $null=Invoke-Ui @('screenshot','--output',$path,'--capture-screen')
    if(-not(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0){throw "Screenshot wasn't delivered: $path"}
    return $path
}

function Assert-StationaryEdgeContactRelease {
    param(
        [Parameter(Mandatory)][uint32]$PointerId,
        [Parameter(Mandatory)]$EdgeObservation,
        [Parameter(Mandatory)]$ExpectedContact,
        [Parameter(Mandatory)]$ReleaseSnapshot
    )

    if ([int]$EdgeObservation.version -ne 1 -or [string]$EdgeObservation.observationStatus -cne 'ok' -or
        [int]$EdgeObservation.observationErrors -ne 0 -or [int]$ReleaseSnapshot.version -ne 1 -or
        [string]$ReleaseSnapshot.observationStatus -cne 'ok' -or [int]$ReleaseSnapshot.observationErrors -ne 0) {
        throw 'The sample observer did not provide healthy version-1 edge and post-release snapshots.'
    }
    if ([long]$ReleaseSnapshot.rendererGeneration -ne [long]$EdgeObservation.rendererGeneration) {
        throw 'The renderer generation changed between stationary hold and contact release.'
    }
    $releasedRecords = @($ReleaseSnapshot.contacts | Where-Object { [uint32]$_.pointerId -eq $PointerId })
    $releasedActive = @($ReleaseSnapshot.active | Where-Object { [uint32]$_.pointerId -eq $PointerId })
    if ($releasedRecords.Count -ne 1) { throw 'The sample observer did not retain exactly one pointer record after the up frame.' }
    $releasedRecord = $releasedRecords[0]
    $captureLostDelta = [int]$releasedRecord.captureLostCount - [int]$ExpectedContact.captureLostCount

    # PointerCaptureLost may accompany intentional ReleasePointerCapture cleanup.
    # It is permitted only after the stationary hold and at most once here.
    if ($releasedActive.Count -ne 0 -or [bool]$releasedRecord.inContact -or
        [long]$releasedRecord.contactSerial -ne [long]$ExpectedContact.contactSerial -or
        [int]$releasedRecord.releaseCount -ne ([int]$ExpectedContact.releaseCount + 1) -or
        [int]$releasedRecord.cancelCount -ne [int]$ExpectedContact.cancelCount -or
        $captureLostDelta -lt 0 -or $captureLostDelta -gt 1 -or
        [string]$releasedRecord.lastEvent -cnotin @('Released','CaptureLost')) {
        throw 'The sample observer did not confirm one ordinary up transition for the same stationary contact (allowing its one normal capture-loss cleanup event).'
    }

    return [pscustomobject]@{ record=$releasedRecord; captureLostDelta=$captureLostDelta; terminalEvent=[string]$releasedRecord.lastEvent }
}

function Invoke-StationaryEdgeCase {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Viewport)
    $null = Navigate-TouchPage
    Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($Viewport -eq 'ancestor')
    Start-Sleep -Milliseconds 180
    $null=Invoke-GuestProbe -Mode 'ScrollToTop' -Viewport $Viewport
    $target=Get-RangeCenter 'Use'
    $null=Invoke-Ui @('touch','MarkdownRenderer','--gesture','long-press','--hold-ms','700','--at',"$($target.x),$($target.y)")
    $initial=Get-CurrentState -Viewport $Viewport
    if($initial.selection -cne 'Use' -or $initial.rangeCount -ne 1){throw "Hold selected '$($initial.selection)' in $Viewport mode, expected exactly 'Use'."}
    if(@($initial.handles).Count -ne 2){throw "Hold did not expose exactly two handles in $Viewport mode."}
    $minimum=44.0*([double]$initial.dpi/96.0)
    foreach($handle in $initial.handles){if($handle.offscreen -or $handle.width -lt $minimum -or $handle.height -lt $minimum){throw "Handle $($handle.automationId) fails 44 epx at $($initial.dpi) DPI."}}
    # Synthetic device-local ID zero is not assumed to equal WinUI's routed ID.
    $injectedPointerId=[uint32]0
    $probe=Invoke-GuestProbe -Mode 'StationaryEdge' -Viewport $Viewport -PointerId $injectedPointerId
    if(-not [bool]$probe.contactReleased -or [int]$probe.stationaryFrameCountDuringHold -ne 0){throw 'The guest helper did not report one released contact with no pointer updates during the stationary hold.'}
    if(-not [bool]$probe.observerVerified -or [string]$probe.observationStatus -cne 'ok' -or [int]$probe.observationErrors -ne 0){throw 'UNVERIFIED: The sample Pointer-event observer did not provide a healthy contact trace; stationary scrolling cannot be qualified.'}
    $pointerId=[uint32](Get-RequiredJsonPropertyValue -Object $probe -Name 'routedPointerId' -Context 'Stationary touch receipt')
    $contactSerial=[long](Get-RequiredJsonPropertyValue -Object $probe -Name 'contactSerial' -Context 'Stationary touch receipt')
    if($contactSerial -le 0){throw 'The stationary receipt did not identify an actual routed contact serial.'}
    $contactSnapshots=@($probe.contactAtEdge,$probe.contactMidHold,$probe.contactEndHold)
    $contactRecords=[Collections.Generic.List[object]]::new()
    foreach($sample in $contactSnapshots){
        if([int]$sample.version -ne 1 -or [string]$sample.observationStatus -cne 'ok' -or [int]$sample.observationErrors -ne 0){throw 'UNVERIFIED: The sample Pointer-event observer degraded during the stationary contact trace.'}
        $records=@($sample.contacts|Where-Object{[uint32]$_.pointerId -eq $pointerId})
        if($records.Count -ne 1){throw 'UNVERIFIED: The sample observer did not retain exactly one injected contact record at every stationary sample.'}
        $record=$records[0]
        $active=@($sample.active|Where-Object{[uint32]$_.pointerId -eq $pointerId -and [long]$_.contactSerial -eq $contactSerial})
        if($active.Count -ne 1 -or [long]$record.contactSerial -ne $contactSerial -or -not [bool]$record.inContact -or [string]$record.handle -cne 'MarkdownSelectionEndHandle'){
            throw 'UNVERIFIED: The injected contact did not remain active on the selection end handle for the full stationary interval.'
        }
        $contactRecords.Add($record)
    }
    $edgeContact=$contactRecords[0];$midContact=$contactRecords[1];$endContact=$contactRecords[2]
    if([long]$edgeContact.contactSerial -ne [long]$midContact.contactSerial -or [long]$edgeContact.contactSerial -ne [long]$endContact.contactSerial -or
       [int]$edgeContact.moveCount -ne [int]$midContact.moveCount -or [int]$edgeContact.moveCount -ne [int]$endContact.moveCount -or
       [int]$edgeContact.pressCount -ne [int]$midContact.pressCount -or [int]$edgeContact.pressCount -ne [int]$endContact.pressCount -or
       [int]$edgeContact.releaseCount -ne [int]$midContact.releaseCount -or [int]$edgeContact.releaseCount -ne [int]$endContact.releaseCount -or
       [int]$edgeContact.cancelCount -ne [int]$midContact.cancelCount -or [int]$edgeContact.cancelCount -ne [int]$endContact.cancelCount -or
       [int]$edgeContact.captureLostCount -ne [int]$midContact.captureLostCount -or [int]$edgeContact.captureLostCount -ne [int]$endContact.captureLostCount){
        throw 'UNVERIFIED: Pointer/contact identity or event counters changed while the injected finger was stationary.'
    }
     $releaseEvidence=Assert-StationaryEdgeContactRelease -PointerId $pointerId -EdgeObservation $probe.contactAtEdge -ExpectedContact $endContact -ReleaseSnapshot $probe.contactAfterRelease
     $releasedRecord=$releaseEvidence.record
     $releaseCaptureLostDelta=[int]$releaseEvidence.captureLostDelta
    $edge=[double]$probe.atEdge.verticalPercent;$mid=[double]$probe.midHold.verticalPercent;$end=[double]$probe.endHold.verticalPercent
    if(-not [bool]$probe.atEdge.verticallyScrollable -or $edge -lt 0 -or $mid -lt 0 -or $end -lt 0){throw "UIA ScrollPattern did not report a vertically scrollable $Viewport viewport during the edge hold."}
    if($mid -le ($edge+0.05) -or $end -le ($mid+0.05)){throw "ScrollPercent did not continue increasing while the pointer was stationary (${Viewport}: edge=$edge, mid=$mid, end=$end)."}
    $edgeLength=([string]$probe.atEdge.selection).Length;$midLength=([string]$probe.midHold.selection).Length;$endLength=([string]$probe.endHold.selection).Length
    if($edgeLength -le 'Use'.Length -or $midLength -le $edgeLength -or $endLength -le $midLength){throw "TextPattern selected range did not continue extending during the stationary hold (${Viewport}: $edgeLength/$midLength/$endLength)."}
    if($probe.afterRelease.rangeCount -ne 1 -or [string]::IsNullOrWhiteSpace([string]$probe.afterRelease.selection)){throw 'Releasing the stationary edge contact lost the committed selection.'}
    $shot=Save-Screenshot -Name "screenshots\stationary-edge-$Viewport.png"
     return [pscustomobject]@{viewport=$Viewport;initialSelection=$initial.selection;scrollPercent=@{atEdge=$edge;midHold=$mid;endHold=$end};selectionLengths=@{atEdge=$edgeLength;midHold=$midLength;endHold=$endLength};stationaryUpdateFrames=0;contactVerifiedActiveThroughout=$true;contactReleased=$true;releaseCaptureLostDelta=$releaseCaptureLostDelta;releasedLastEvent=[string]$releaseEvidence.terminalEvent;pointerId=$pointerId;injectedPointerId=$injectedPointerId;contactSerial=[long]$edgeContact.contactSerial;moveCount=[int]$edgeContact.moveCount;observerStatus='ok';observerErrors=0;handles=@($initial.handles);screenshot=$shot;probe=$probe}
}

function Get-ReportCounts {
    $counts=[ordered]@{pass=0;fail=0;unverified=0}
    foreach($result in $script:Results){switch($result.status){'PASS'{$counts.pass++}'FAIL'{$counts.fail++}'UNVERIFIED'{$counts.unverified++}default{throw "Unknown status $($result.status)"}}}
    return $counts
}

try {
    if(-not(Test-Path -LiteralPath $ProbePath -PathType Leaf)){throw "Guest probe is missing: $ProbePath"}
    if($script:RunInfo.target -ne 'sandbox' -or [string]::IsNullOrWhiteSpace($script:TargetEpoch)){throw 'Prior run is not a qualified Sandbox run.'}
    if($script:RunInfo.architecture -ne 'x64' -or [string]::IsNullOrWhiteSpace($script:ExpectedExeSha256)){throw 'Prior run lacks a qualified x64 binary hash.'}
    if($script:AppProcessId -le 0 -or $script:AppWindowHandle -le 0){throw 'Prior run lacks a valid captured sample PID/HWND.'}
    if(Test-Path -LiteralPath $script:RunDirectory){throw "Fresh output directory already exists: $script:RunDirectory"}
    [IO.Directory]::CreateDirectory($script:RunDirectory) | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID=$script:WorkflowId
    $script:UiStarted=$true
    $snapshot=Assert-SandboxEpoch 'before follow-up'
    $app=Assert-AppIdentity
    Stage-GuestProbe
    $script:ExpectedGuestComputerName=Get-GuestComputerName
    $script:OriginalHighContrast=Invoke-GuestProbe -Mode 'HighContrastRead'
    $firstIdentity=Invoke-GuestProbe -Mode 'Identity'
    if($firstIdentity.exeSha256 -cne $script:ExpectedExeSha256.ToLowerInvariant()){throw 'The live Sandbox app binary does not match the approved build receipt.'}
    $script:ExpectedStartTicks=[long]$firstIdentity.startTicks
    $script:PayloadIdentity=Assert-GuestPayloadIdentity

    $page=Invoke-GuestProbe -Mode 'RawName' -AutomationId 'CurrentSamplePage'
    $themeToggle=[string](Invoke-UiProperty -AutomationId 'ThemeToggle' -Property 'ToggleState')
    $forcedToggle=[string](Invoke-UiProperty -AutomationId 'ForcedHighContrastToggle' -Property 'ToggleState')
    $null=Navigate-TouchPage
    $viewportToggle=[string](Invoke-UiProperty -AutomationId 'TouchViewportOwnershipToggle' -Property 'ToggleState')
    $script:OriginalAppState=[pscustomobject]@{page=[string]$page.name;themeDark=($themeToggle -eq 'On');forcedHighContrast=($forcedToggle -eq 'On');ancestorViewport=($viewportToggle -eq 'On');windowBounds=$firstIdentity.windowBounds}
    Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled $false

    Test-Case -Id 'C11-uia-point-range-owned' -Name 'visible-point-query-preserves-absent-diagram-bounds-owned' -Requirement 'Real COM TextPattern.RangeFromPoint resolves the painted word with offscreen diagram spans present, and those spans expose no visible geometry.' -Body { Invoke-PointRangeCase -Viewport 'owned' }
    Test-Case -Id 'C11-uia-point-range-ancestor' -Name 'visible-point-query-preserves-absent-diagram-bounds-ancestor' -Requirement 'The same COM point query and absent-bounds contract hold in the ancestor-owned viewport.' -Body { Invoke-PointRangeCase -Viewport 'ancestor' }

    Test-Case -Id 'C10-stationary-edge-owned' -Name 'stationary-bottom-edge-autoscroll-owned-viewport' -Requirement 'Once the handle reaches the edge and the injected contact remains motionless, UIA ScrollPercent and the selected TextPattern range continue advancing.' -Body { Invoke-StationaryEdgeCase -Viewport 'owned' }
    Test-Case -Id 'C10-stationary-edge-ancestor' -Name 'stationary-bottom-edge-autoscroll-ancestor-viewport' -Requirement 'The same stationary contact causes continued scrolling and endpoint extension with an ancestor-owned ScrollViewer.' -Body { Invoke-StationaryEdgeCase -Viewport 'ancestor' }
    Test-Case -Id 'C10-handle-crossing-owned' -Name 'logical-endpoint-roles-cross-in-owned-viewport' -Requirement 'Dragging the Start handle beyond End and the End handle before Start preserves the independently computed UIA ranges in the owned viewport.' -Body { Invoke-HandleCrossingCase -Viewport 'owned' }
    Test-Case -Id 'C10-handle-crossing-ancestor' -Name 'logical-endpoint-roles-cross-in-ancestor-viewport' -Requirement 'Both directions of logical endpoint crossing preserve independently computed UIA ranges in an ancestor-owned viewport.' -Body { Invoke-HandleCrossingCase -Viewport 'ancestor' }

    Test-Case -Id 'C13-actual-system-high-contrast' -Name 'windows-system-high-contrast-and-selection' -Requirement 'The Sandbox Windows High Contrast mode, not the sample preview toggle, changes the renderer to theme:high-contrast while a touch-selected range and both targets remain available.' -Body {
        $original=$script:OriginalHighContrast
        if($null -eq $original){throw 'UNVERIFIED: Sandbox did not return original SPI_GETHIGHCONTRAST state.'}
        $wantedFlags=[uint32]([uint32]$original.flags -bor 1)
        if([uint32]$original.flags -ne $wantedFlags){
            try { $null=Invoke-GuestProbe -Mode 'HighContrastSet' -HighContrastFlags $wantedFlags -HighContrastScheme $original.scheme }
            catch { throw "UNVERIFIED: Sandbox rejected the actual Windows High Contrast mode change: $($_.Exception.Message)" }
        }
        $deadline=[DateTime]::UtcNow.AddSeconds(12);$native=$null;$theme=$null
        do{
            $native=Invoke-GuestProbe -Mode 'HighContrastRead'
            $theme=Invoke-GuestProbe -Mode 'RawName' -AutomationId 'ThemeStatus'
            if([bool]$native.enabled -and $theme.name -eq 'theme:high-contrast'){break}
            Start-Sleep -Milliseconds 180
        }while([DateTime]::UtcNow -lt $deadline)
        if(-not [bool]$native.enabled -or $theme.name -ne 'theme:high-contrast'){throw 'UNVERIFIED: The guest system flag and sample theme provider did not both report High Contrast.'}
        $null=Navigate-TouchPage
        Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled $false
        # Same-page navigation preserves the prior ownership toggle. The
        # preceding ancestor case must not leave this owned-mode probe using
        # an inactive scroller.
        Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled $false
        if([string](Invoke-UiProperty -AutomationId 'TouchViewportOwnershipToggle' -Property 'ToggleState') -cne 'Off'){throw 'High Contrast setup did not activate the owned viewport.'}
        $null=Invoke-GuestProbe -Mode 'ScrollToTop' -Viewport 'owned'
        $point=Get-RangeCenter 'Press'
        $null=Invoke-Ui @('touch','MarkdownRenderer','--gesture','long-press','--hold-ms','700','--at',"$($point.x),$($point.y)")
        $state=Assert-ExactSelection -Expected 'Press'
        $shot=Save-Screenshot -Name 'screenshots\actual-windows-high-contrast-selection.png'
        return [pscustomobject]@{systemHighContrastEnabled=[bool]$native.enabled;systemFlags=[uint32]$native.flags;scheme=$native.scheme;themeStatus=$theme.name;selection=$state.selection;dpi=$state.dpi;handles=@($state.handles);screenshot=$shot;forcedPreviewToggle='Off'}
    }

    Add-Unverified -Id 'C13-high-contrast-visual-review' -Name 'actual-system-high-contrast-screenshot-review' -Reason 'The guest screenshot and actual Windows theme status are captured; visual contrast/content review remains a separate human inspection.'
}
catch {
    $script:Results.Add([pscustomobject]@{id='HARNESS';name='follow-up-setup-or-fatal-error';requirement='Only use the exact app and Sandbox epoch in the approved prior run receipt.';status='FAIL';actual=$null;detail=$_.Exception.ToString();startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})
}
finally {
    if($null -ne $script:OriginalHighContrast){
        try{
            $null=Invoke-GuestProbe -Mode 'HighContrastSet' -HighContrastFlags ([uint32]$script:OriginalHighContrast.flags) -HighContrastScheme $script:OriginalHighContrast.scheme
            $restored=Invoke-GuestProbe -Mode 'HighContrastRead'
            $sameFlags=([uint32]$restored.flags -eq [uint32]$script:OriginalHighContrast.flags)
            $sameScheme=([string]$restored.scheme -ceq [string]$script:OriginalHighContrast.scheme)
            if(-not $sameFlags -or -not $sameScheme){throw "Guest High Contrast restore mismatch: flags=$($restored.flags), scheme='$($restored.scheme)'."}
            $script:Results.Add([pscustomobject]@{id='RESTORE-HC';name='restore-sandbox-system-high-contrast';requirement='Restore exact prior guest flags/scheme even if the sample app exited.';status='PASS';actual=$restored;detail=$null;startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})
        }catch{$script:Results.Add([pscustomobject]@{id='RESTORE-HC';name='restore-sandbox-system-high-contrast';requirement='Restore exact prior guest flags/scheme even if the sample app exited.';status='FAIL';actual=$null;detail=$_.Exception.Message;startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})}
    }else{Add-Unverified -Id 'RESTORE-HC' -Name 'restore-sandbox-system-high-contrast' -Reason 'No original SPI_GETHIGHCONTRAST state was captured; script made no HC mutation.'}
    if($null -ne $script:OriginalAppState){
        try{
            $null=Assert-AppIdentity
            $null=Invoke-GuestProbe -Mode 'ResizeWindow' -WindowWidthPixels ([int]$script:OriginalAppState.windowBounds.width) -WindowHeightPixels ([int]$script:OriginalAppState.windowBounds.height)
            Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled ([bool]$script:OriginalAppState.forcedHighContrast)
            Set-ToggleState -AutomationId 'ThemeToggle' -Enabled ([bool]$script:OriginalAppState.themeDark)
            Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ([bool]$script:OriginalAppState.ancestorViewport)
            if([string]$script:OriginalAppState.page -match '^page:([A-Za-z0-9]+)$'){
                $key=$Matches[1]
                $null=Invoke-Ui @('invoke',"SampleNav_$key",'--action','select')
            }else{throw 'The original sample page has no exact restorable navigation key.'}
            $restoreDeadline=[DateTime]::UtcNow.AddSeconds(5)
            do{
                $restoredPage=Invoke-GuestProbe -Mode 'RawName' -AutomationId 'CurrentSamplePage'
                if([string]$restoredPage.name -ceq [string]$script:OriginalAppState.page){break}
                Start-Sleep -Milliseconds 100
            }while([DateTime]::UtcNow -lt $restoreDeadline)
            if([string]$restoredPage.name -cne [string]$script:OriginalAppState.page){throw 'The restored sample page does not match its original UIA state.'}
            $script:Results.Add([pscustomobject]@{id='RESTORE-APP';name='restore-sample-page-and-preview-controls';requirement='Restore the previously observed sample page, theme, forced-preview, and viewport mode.';status='PASS';actual=$script:OriginalAppState;detail=$null;startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})
        }catch{$script:Results.Add([pscustomobject]@{id='RESTORE-APP';name='restore-sample-page-and-preview-controls';requirement='Restore the previously observed sample page, theme, forced-preview, and viewport mode.';status='UNVERIFIED';actual=$null;detail=$_.Exception.Message;startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})}
    }
    if($script:UiStarted){
        try{$null=Invoke-NativeChecked @('ui','yield','--on','sandbox')}
        catch{$script:Results.Add([pscustomobject]@{id='WORKFLOW';name='release-sandbox-ui-workflow';requirement='Yield the coordinated Sandbox UI workflow in finally.';status='FAIL';actual=$null;detail=$_.Exception.Message;startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o')})}
    }
    $env:WINAPP_UI_WORKFLOW_ID=$script:PreviousWorkflowId
}

if(-not(Test-Path -LiteralPath $script:RunDirectory -PathType Container)){[IO.Directory]::CreateDirectory($script:RunDirectory)|Out-Null}
$counts=Get-ReportCounts
$report=[ordered]@{
    schemaVersion=1;objective='MarkdownRenderer Phase 1 correctness-only Sandbox follow-up';runId=$script:RunId
    priorRunJson=[IO.Path]::GetFullPath($PriorRunJson);target='sandbox';targetEpoch=$script:TargetEpoch;appProcessId=$script:AppProcessId;appWindowHandle=$script:AppWindowHandle
    buildBinarySha256=$script:ExpectedExeSha256;payloadIdentity=$script:PayloadIdentity;expectedStartTicks=$script:ExpectedStartTicks;guestComputerName=$script:ExpectedGuestComputerName;hostComputerName=$script:ExpectedHostComputerName;resultCounts=$counts;results=@($script:Results.ToArray())
    complete=($counts.fail -eq 0 -and $counts.unverified -eq 0);generatedUtc=[DateTime]::UtcNow.ToString('o')
}
Write-JsonFile (Join-Path $script:RunDirectory 'results.json') $report
Write-JsonFile (Join-Path $script:RunDirectory 'run.json') ([ordered]@{runId=$script:RunId;priorRunJson=$report.priorRunJson;target='sandbox';targetEpoch=$script:TargetEpoch;appProcessId=$script:AppProcessId;appWindowHandle=$script:AppWindowHandle;buildBinarySha256=$script:ExpectedExeSha256;payloadIdentity=$script:PayloadIdentity;expectedStartTicks=$script:ExpectedStartTicks;guestComputerName=$script:ExpectedGuestComputerName;hostComputerName=$script:ExpectedHostComputerName;originalHighContrast=$script:OriginalHighContrast;originalAppState=$script:OriginalAppState;generatedUtc=[DateTime]::UtcNow.ToString('o')})
$counts | Format-List
Write-Host "Correctness Sandbox follow-up evidence: $script:RunDirectory"
if($counts.fail -gt 0){exit 1}
if($counts.unverified -gt 0){exit 2}
exit 0
