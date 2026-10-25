[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$ResizeCrashDiagnosticOnly,
    [switch]$IncludeSuppressionInvestigation,
    [switch]$IncludeSelectionDisableInvestigation,
    # Phase 2: run only C00 and the P2-* live feature-matrix cases.
    [switch]$Phase2Only,
    # With -Phase2Only: run only the cases that turn on the Sandbox guest's
    # actual Windows High Contrast setting (restored in finally).
    [switch]$Phase2SystemHighContrast,
    # With -Phase2Only: run only the P2 cases whose ID matches this regex.
    [string]$Phase2CasePattern = '',
    # Phase 1 live clipboard/selection closure: C00, the C10 handle-drag case,
    # the C12 final-SVG cases and MarkdownCorrectnessPhase1ClipboardCases.ps1.
    [switch]$Phase1ClipboardOnly,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\correctness'),
    [string]$BuildArtifactsPath = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:ProjectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj'))
$script:OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$script:BuildArtifactsPath = $BuildArtifactsPath
if (-not [string]::IsNullOrWhiteSpace($script:BuildArtifactsPath)) {
    if (-not [System.IO.Path]::IsPathRooted($script:BuildArtifactsPath)) {
        throw 'BuildArtifactsPath must be an absolute path so the launched and hashed payload use one unambiguous MSBuild output root.'
    }
    $script:BuildArtifactsPath = [System.IO.Path]::GetFullPath($script:BuildArtifactsPath)
}
$script:Phase2Only = [bool]$Phase2Only
$script:Phase2SystemHighContrast = [bool]$Phase2SystemHighContrast
if ($script:Phase2SystemHighContrast -and -not $script:Phase2Only) { throw '-Phase2SystemHighContrast requires -Phase2Only.' }
$script:Phase2CasePattern = $Phase2CasePattern
$script:Phase1ClipboardOnly = [bool]$Phase1ClipboardOnly
if ($script:Phase1ClipboardOnly -and $script:Phase2Only) { throw '-Phase1ClipboardOnly cannot be combined with -Phase2Only.' }
if ($script:Phase2CasePattern -and -not $script:Phase2Only) { throw '-Phase2CasePattern requires -Phase2Only.' }
$script:RunId = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [guid]::NewGuid().ToString('N')
$script:RunDirectory = Join-Path $script:OutputRoot $script:RunId
$script:WorkflowId = "markdown-correctness-$([guid]::NewGuid().ToString('N'))"
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:Screenshots = [System.Collections.Generic.List[string]]::new()
$script:CommandIndex = 0
$script:AppProcessId = 0
$script:AppWindowHandle = 0L
$script:ExpectedWindowTitle = ''
$script:BuildPayloads = @()
$script:TargetEpoch = ''
$script:TargetIdentityInvalid = $false
$script:TargetIdentityFailure = ''
$script:ResizeCrashDiagnosticOnly = [bool]$ResizeCrashDiagnosticOnly
$script:UiStarted = $false
$script:GuestProbePath = ''
$script:PreviousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID

function Write-JsonFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 12
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Get-JsonPropertyValue {
    param([AllowNull()][object]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-RequiredJsonPropertyValue {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Context
    )
    $property = if ($null -eq $Object) { $null } else { $Object.PSObject.Properties[$Name] }
    if ($null -eq $property) { throw "$Context is missing required JSON property '$Name'." }
    return $property.Value
}

function Assert-SandboxSnapshot {
    param([Parameter(Mandatory)][object]$Snapshot, [Parameter(Mandatory)][string]$Phase)
    $running = Get-RequiredJsonPropertyValue -Object $Snapshot -Name 'running' -Context "Sandbox snapshot ($Phase)"
    $desktop = Get-RequiredJsonPropertyValue -Object $Snapshot -Name 'desktop' -Context "Sandbox snapshot ($Phase)"
    $inputReady = Get-RequiredJsonPropertyValue -Object $desktop -Name 'effectiveInputReady' -Context "Sandbox desktop snapshot ($Phase)"
    $captureReady = Get-RequiredJsonPropertyValue -Object $desktop -Name 'effectiveCaptureReady' -Context "Sandbox desktop snapshot ($Phase)"
    $capabilities = Get-RequiredJsonPropertyValue -Object $Snapshot -Name 'capabilities' -Context "Sandbox snapshot ($Phase)"
    $architecture = [string](Get-RequiredJsonPropertyValue -Object $capabilities -Name 'architecture' -Context "Sandbox capabilities ($Phase)")
    $executionTarget = Get-RequiredJsonPropertyValue -Object $Snapshot -Name 'executionTarget' -Context "Sandbox snapshot ($Phase)"
    $epoch = [string](Get-RequiredJsonPropertyValue -Object $executionTarget -Name 'epoch' -Context "Sandbox execution target ($Phase)")
    if (-not [bool]$running -or -not [bool]$inputReady -or -not [bool]$captureReady) {
        throw "The approved Sandbox is not running with interactive input and capture ready ($Phase)."
    }
    if ($architecture -ne 'x64') { throw "The active Sandbox architecture is '$architecture' ($Phase), expected x64." }
    if ([string]::IsNullOrWhiteSpace($epoch)) { throw "The Sandbox execution target has no epoch ($Phase)." }
    return $epoch
}

function Save-CommandEvidence {
    param([string[]]$Arguments, [int]$ExitCode, [string]$Output)
    $script:CommandIndex++
    $name = ($Arguments -join ' ') -replace '[^A-Za-z0-9._-]+', '-'
    if ($name.Length -gt 80) { $name = $name.Substring(0, 80) }
    $path = Join-Path $script:RunDirectory ('commands\{0:D4}-{1}.txt' -f $script:CommandIndex, $name)
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
    [System.IO.File]::WriteAllText($path, "exitCode=$ExitCode`r`n$Output", [System.Text.UTF8Encoding]::new($false))
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
    finally {
        $process.Dispose()
    }
}

function Get-SampleProjectRunArguments {
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][ValidateSet('Debug', 'Release')][string]$Configuration,
        [switch]$NoBuild,
        [AllowEmptyString()][string]$BuildArtifactsPath = ''
    )

    $arguments = @('run', $ProjectPath, '--on', 'sandbox', '--detach', '--json', '-c', $Configuration, '--arch', 'x64')
    if ($NoBuild) { $arguments += '--no-build' }
    if (-not [string]::IsNullOrWhiteSpace($BuildArtifactsPath)) {
        if (-not [System.IO.Path]::IsPathRooted($BuildArtifactsPath)) {
            throw 'BuildArtifactsPath must be an absolute path.'
        }
        $fullArtifactsPath = [System.IO.Path]::GetFullPath($BuildArtifactsPath)
        $arguments += @('-p', "ArtifactsPath=$fullArtifactsPath")
    }

    return $arguments
}

function Get-SampleBuildOutputDirectory {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][ValidateSet('Debug', 'Release')][string]$Configuration,
        [AllowEmptyString()][string]$BuildArtifactsPath = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($BuildArtifactsPath)) {
        if (-not [System.IO.Path]::IsPathRooted($BuildArtifactsPath)) {
            throw 'BuildArtifactsPath must be an absolute path.'
        }

        $fullArtifactsPath = [System.IO.Path]::GetFullPath($BuildArtifactsPath)
        $configurationPivot = $Configuration.ToLowerInvariant()
        return Join-Path $fullArtifactsPath "bin\MarkdownRenderer.Sample\${configurationPivot}_win-x64"
    }

    return Join-Path $RepoRoot "MarkdownRenderer\MarkdownRenderer.Sample\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64"
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$Json,
        [switch]$AllowNonZero,
        [string]$Label = ''
    )

    $argv = @($Arguments)
    if ($Json -and $argv -notcontains '--json') { $argv += '--json' }
    $command = Get-Command -Name 'winapp' -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $fileName = if (-not [string]::IsNullOrWhiteSpace([string]$command.Source)) { [string]$command.Source } else { [string]$command.Path }
    if ([string]::IsNullOrWhiteSpace($fileName)) { throw 'Could not resolve the winapp executable path.' }
    $processResult = Invoke-RedirectedUtf8Process -FileName $fileName -Arguments $argv
    $exitCode = [int]$processResult.ExitCode
    $stdout = [string]$processResult.StandardOutput
    $stderr = [string]$processResult.StandardError
    $output = if ([string]::IsNullOrEmpty($stderr)) { $stdout } else { $stdout + [Environment]::NewLine + '[stderr]' + [Environment]::NewLine + $stderr }
    $evidence = Save-CommandEvidence -Arguments $argv -ExitCode $exitCode -Output $output
    if ($exitCode -ne 0 -and -not $AllowNonZero) {
        throw "winapp command failed with exit $exitCode (evidence: $evidence): $output"
    }

    if (-not $Json) {
        return [pscustomobject]@{ exitCode = $exitCode; output = $output; evidence = $evidence }
    }

    $trimmed = $stdout.Trim()
    $jsonStart = [regex]::Match($trimmed, '(?s)(\{|\[)')
    if (-not $jsonStart.Success) { throw "winapp returned no JSON (evidence: $evidence): $output" }
    $jsonText = $trimmed.Substring($jsonStart.Index)
try { $parsed = ConvertFrom-Json -InputObject $jsonText }
    catch { throw "winapp returned malformed JSON (evidence: $evidence): $_`n$output" }
    return [pscustomobject]@{ exitCode = $exitCode; value = $parsed; output = $output; evidence = $evidence }
}

function Invoke-Ui {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowNonZero)
    if ($script:AppProcessId -le 0) { throw 'No verified Sandbox guest application PID is active.' }
    if ($Arguments.Count -gt 0 -and $Arguments[0] -in @('invoke','click','set-value','focus','send-keys','drag','touch','pen','scroll','scroll-into-view','hover')) {
        # Check the selected guest epoch and exact sample HWND immediately
        # before any command that can mutate the UI or synthesize input.
        $null = Assert-ProcessIdentity
    }
    $argv = @('ui') + $Arguments + @('--on', 'sandbox', '-a', [string]$script:AppProcessId)
    return Invoke-NativeChecked -Arguments $argv -Json -AllowNonZero:$AllowNonZero
}

function Invoke-UiValue {
    param([Parameter(Mandatory)][string]$AutomationId)
    $result = Invoke-Ui @('get-value', $AutomationId)
    $value = $result.value
    foreach ($propertyName in @('text', 'value', 'name')) {
        $property = $value.PSObject.Properties[$propertyName]
        if ($null -ne $property -and $null -ne $property.Value) { return [string]$property.Value }
    }
    throw "UIA get-value for $AutomationId did not contain text/value/name. Evidence: $($result.evidence)"
}

function Invoke-UiProperty {
    param([Parameter(Mandatory)][string]$Selector, [Parameter(Mandatory)][string]$Property)
    $result = Invoke-Ui @('get-property', $Selector, '--property', $Property)
    $properties = $result.value.properties
    if ($null -eq $properties) { throw "UIA get-property $Property on $Selector returned no properties." }
    $entry = $properties.PSObject.Properties[$Property]
    if ($null -eq $entry) { throw "UIA did not report property $Property on $Selector." }
    return $entry.Value
}

function Get-UiInspection {
    $result = Invoke-Ui @('inspect', 'MarkdownRenderer', '--depth', '12')
    if ($null -eq $result.value.windows) { throw 'The renderer inspection has no windows collection.' }
    return $result.value
}

function Expand-UiElements {
    param([object[]]$Elements)
    foreach ($element in $Elements) {
        if ($null -eq $element) { continue }
        $element
        $children = $null
        if ($element -is [System.Collections.IDictionary]) {
            if ($element.Contains('children')) { $children = $element['children'] }
        }
        else {
            $childrenProperty = $element.PSObject.Properties['children']
            if ($null -ne $childrenProperty) { $children = $childrenProperty.Value }
        }
        if ($null -ne $children) { Expand-UiElements @($children) }
    }
}

function Get-UiAutomationId {
    param([AllowNull()][object]$Element)
    if ($null -eq $Element) { return '' }
    if ($Element -is [System.Collections.IDictionary]) {
        if ($Element.Contains('automationId')) { return [string]$Element['automationId'] }
        return ''
    }
    $property = $Element.PSObject.Properties['automationId']
    if ($null -eq $property -or $null -eq $property.Value) { return '' }
    return [string]$property.Value
}

function Get-UiElements {
    $tree = Get-UiInspection
    $top = @($tree.windows | ForEach-Object { $_.elements })
    return @(Expand-UiElements $top)
}

function Get-AppUiElements {
    # PopupHost/context menus and shell navigation can be outside the
    # MarkdownRenderer subtree. The -a PID scope remains the same verified app.
    $result = Invoke-Ui @('inspect', '--depth', '12')
    if ($null -eq $result.value.windows) { throw 'The full app inspection has no windows collection.' }
    $top = @($result.value.windows | ForEach-Object { $_.elements })
    return @(Expand-UiElements $top)
}

function Get-SampleDiagnosticMessage {
    # The sample keeps its diagnostics InfoBar closed and its message empty
    # when rendering succeeds. UIA then omits the empty TextBlock entirely,
    # so querying it directly with `get-value` incorrectly turns the normal
    # no-diagnostic state into an element_not_found test failure.
    $messages = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -ceq 'SampleDiagnosticsMessage' })
    if ($messages.Count -gt 1) { throw "Expected at most one SampleDiagnosticsMessage, found $($messages.Count)." }
    if ($messages.Count -eq 0) { return '' }

    foreach ($propertyName in @('name', 'value', 'text')) {
        $property = $messages[0].PSObject.Properties[$propertyName]
        if ($null -ne $property -and $null -ne $property.Value -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return [string]$property.Value
        }
    }
    return ''
}

function Wait-ForRendererText {
    param([Parameter(Mandatory)][string]$Text, [int]$TimeoutMilliseconds = 12000)
    $wait = Invoke-Ui @('wait-for', 'MarkdownRenderer', '--type', 'Document', '--value', $Text, '--contains', '-t', [string]$TimeoutMilliseconds)
    if ($wait.value.PSObject.Properties['found'] -and -not [bool]$wait.value.found) {
        throw "MarkdownRenderer did not expose required text '$Text' before timeout."
    }
    $actual = Invoke-UiValue 'MarkdownRenderer'
    if ($actual.IndexOf($Text, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "MarkdownRenderer UIA text did not contain '$Text'."
    }
    return $actual
}

function Wait-ForRawName {
    param([Parameter(Mandatory)][string]$AutomationId, [Parameter(Mandatory)][string]$Expected, [int]$TimeoutMilliseconds = 8000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $state = Invoke-GuestProbe -Mode 'raw-name' -AutomationId $AutomationId
        if ($state.name -eq $Expected) { return }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Raw UIA name for $AutomationId did not become '$Expected'."
}

function Wait-ForImagePeerState {
    param([Parameter(Mandatory)][string]$AltText, [int]$TimeoutMilliseconds = 5000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $last = $null
    do {
        $last = Invoke-GuestProbe -Mode 'image-state' -Needle $AltText
        if (@($last.images).Count -gt 1) { throw "Multiple image peers contain the exact expected alternative text '$AltText'." }
        if (@($last.images).Count -eq 1 -and [string]$last.images[0].name -notmatch '^Loading\s') { return $last }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    if (@($last.images).Count -eq 0) { throw "No MarkdownImage UIA peer for '$AltText' appeared within the bounded wait." }
    return $last
}

function Navigate-Sample {
    param([Parameter(Mandatory)][string]$Key, [Parameter(Mandatory)][string]$RequiredText)
    $navigationId = "SampleNav_$Key"
    $currentPage = Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'CurrentSamplePage'
    if ([string]$currentPage.name -ceq "page:$Key") {
        $detourKey = if ($Key -eq 'Typography') { 'Lists' } else { 'Typography' }
        $detourText = if ($detourKey -eq 'Lists') { 'Nested level 3' } else { 'The quick brown fox' }
        $null = Invoke-Ui @('invoke', "SampleNav_$detourKey", '--action', 'select')
        $null = Wait-ForRendererText -Text $detourText
        Wait-ForRawName -AutomationId 'CurrentSamplePage' -Expected "page:$detourKey"
    }
    $null = Invoke-Ui @('invoke', $navigationId, '--action', 'select')
    $null = Wait-ForRendererText -Text $RequiredText
    Wait-ForRawName -AutomationId 'CurrentSamplePage' -Expected "page:$Key"
}

function Get-UiBounds {
    param([Parameter(Mandatory)][string]$Selector)
    $raw = [string](Invoke-UiProperty -Selector $Selector -Property 'BoundingRectangle')
    $parts = @($raw -split ',' | ForEach-Object { [double]::Parse($_, [System.Globalization.CultureInfo]::InvariantCulture) })
    if ($parts.Count -ne 4 -or $parts[2] -le 0 -or $parts[3] -le 0) {
        throw "UIA element '$Selector' has an invalid BoundingRectangle '$raw'."
    }
    return ,$parts
}

function Find-UiElement {
    param([Parameter(Mandatory)][string]$Text, [string]$Type = '', [string]$Root = '')
    $args = @('search', $Text)
    if ($Type) { $args += @('--type', $Type) }
    if ($Root) { $args += @('--root', $Root) }
    $result = Invoke-Ui $args
    if ([int]$result.value.matchCount -ne 1) {
        throw "Expected exactly one UIA match for '$Text' (type='$Type', root='$Root'); found $($result.value.matchCount)."
    }
    return $result.value.matches[0]
}

function Save-Screenshot {
    param([Parameter(Mandatory)][string]$Name, [switch]$CaptureScreen)
    $path = Join-Path $script:RunDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite evidence: $path" }
    $args = @('screenshot', '--output', $path)
    if ($CaptureScreen) { $args += '--capture-screen' }
    $result = Invoke-Ui $args
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) {
        throw "Sandbox screenshot was not delivered: $path"
    }
    $script:Screenshots.Add($path)
    return [pscustomobject]@{ path = $path; width = $result.value.width; height = $result.value.height }
}

function Invoke-GuestProbe {
    param(
        [Parameter(Mandatory)][ValidateSet('metadata', 'range', 'range-between', 'table-cell', 'scroll-range', 'selection', 'raw-name', 'image-state', 'handles', 'focus-state', 'scroll', 'clipboard', 'resize', 'cancel-contact', 'payload-hashes', 'source-file-preflight', 'source-file', 'near-origin-sequence', 'process-state', 'launch-selection-disable-probe')][string]$Mode,
        [int]$TargetProcessId = 0,
        [string]$Needle = '',
        [string]$StartNeedle = '',
        [string]$EndNeedle = '',
        [switch]$SelectRange,
        [string]$AutomationId = '',
        [int]$Width = 0,
        [int]$Height = 0,
        [int]$Left = 0,
        [int]$Top = 0,
        [double]$X = 0,
        [double]$Y = 0,
        [uint32]$PointerId = 0,
        [string]$SourcePath = '',
        [string]$RequiredText = '',
        [string]$RunId = ''
    )
    if (-not $script:GuestProbePath) { throw 'The guest UIA probe was not staged.' }
    $identitySensitiveMode = $Mode -in @('cancel-contact','near-origin-sequence','resize')
    if ($identitySensitiveMode) {
        # These modes inject native touch or move the app window. Keep the
        # time-critical gesture sequence inside the guest helper, with bounded
        # host identity checks immediately before and after that one call.
        $null = Assert-ProcessIdentity
    }
    if ($TargetProcessId -lt 0 -or ($TargetProcessId -ne 0 -and $Mode -cne 'process-state')) {
        throw 'A target process ID override is accepted only for the read-only process-state guest probe.'
    }
    $guestProcessId = if ($Mode -ceq 'process-state' -and $TargetProcessId -gt 0) { $TargetProcessId } else { $script:AppProcessId }
    $arguments = @(
        'target', 'exec', 'sandbox', '--json', '--', 'powershell.exe', '-NoProfile',
        '-NonInteractive'
    )
    # The guest PowerShell helper must stay invisible while its explicitly
    # visible Start-Process child creates the app window under test.
    if ($Mode -ceq 'launch-selection-disable-probe') { $arguments += '-WindowStyle'; $arguments += 'Hidden' }
    $arguments += @(
        '-ExecutionPolicy', 'Bypass', '-STA', '-File',
        $script:GuestProbePath, '-Mode', $Mode, '-ProcessId', [string]$guestProcessId,
        '-WindowHandle', [string]$script:AppWindowHandle, '-Width', [string]$Width,
        '-Height', [string]$Height, '-Left', [string]$Left, '-Top', [string]$Top, '-X', [string]$X, '-Y', [string]$Y,
        '-PointerId', [string]$PointerId
    )
    # The guest probe itself provides empty defaults for optional text
    # parameters. Omit them here instead of passing native empty-string
    # arguments, which WinApp's structured command binder rejects.
    if (-not [string]::IsNullOrEmpty($Needle)) { $arguments += @('-Needle', $Needle) }
    if (-not [string]::IsNullOrEmpty($StartNeedle)) { $arguments += @('-StartNeedle', $StartNeedle) }
    if (-not [string]::IsNullOrEmpty($EndNeedle)) { $arguments += @('-EndNeedle', $EndNeedle) }
    if ($SelectRange) { $arguments += '-SelectRange' }
    if (-not [string]::IsNullOrEmpty($AutomationId)) { $arguments += @('-AutomationId', $AutomationId) }
    if (-not [string]::IsNullOrEmpty($SourcePath)) { $arguments += @('-SourcePath', $SourcePath) }
    if (-not [string]::IsNullOrEmpty($RequiredText)) { $arguments += @('-RequiredText', $RequiredText) }
    if (-not [string]::IsNullOrEmpty($RunId)) { $arguments += @('-RunId', $RunId) }
    # target exec consumes the command after `--`, so --json must be before
    # that delimiter rather than appended by Invoke-NativeChecked.
    $result = Invoke-NativeChecked -Arguments $arguments -Json
    $value = $result.value
    $guestOutput = $null
    foreach ($propertyName in @('stdout', 'output', 'result')) {
        $property = $value.PSObject.Properties[$propertyName]
        if ($null -ne $property -and $property.Value -is [string] -and $property.Value.Trim()) {
            $guestOutput = [string]$property.Value
            break
        }
    }
    if ($null -eq $guestOutput) {
        if ($value -is [string]) { $guestOutput = $value }
        elseif ($value.PSObject.Properties['mode']) { return $value }
        else { throw "Guest probe returned no JSON payload: $($result.output)" }
    }
    $payloadMatch = [regex]::Match($guestOutput.Trim(), '(?s)(\{.*\})\s*$')
    if (-not $payloadMatch.Success) { throw "Guest probe did not write a JSON payload: $guestOutput" }
    try { $probeResult = ConvertFrom-Json -InputObject $payloadMatch.Groups[1].Value }
    catch { throw "Guest probe JSON was invalid: $_`n$guestOutput" }
    if ($identitySensitiveMode) { $null = Assert-ProcessIdentity }
    return $probeResult
}

function Get-HostTouchContactObservation {
    $raw = Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'TouchContactObservation'
    if ($null -eq $raw -or [string]$raw.mode -cne 'raw-name' -or
        [string]$raw.automationId -cne 'TouchContactObservation' -or
        [string]::IsNullOrWhiteSpace([string]$raw.name)) {
        throw 'The exact sample TouchContactObservation raw-name peer returned no JSON snapshot.'
    }
    try { $observation = ConvertFrom-Json -InputObject ([string]$raw.name) }
    catch { throw "The sample TouchContactObservation raw-name peer returned malformed JSON: $($_.Exception.Message)" }
    Assert-TouchContactObservationSnapshot -Snapshot $observation -Context 'raw-name probe' | Out-Null
    return $observation
}

function Assert-TouchContactObservationSnapshot {
    param([Parameter(Mandatory)]$Snapshot,[Parameter(Mandatory)][string]$Context)
    if ([int]$Snapshot.version -ne 1 -or [long]$Snapshot.rendererGeneration -le 0 -or
        [string]$Snapshot.observationStatus -cne 'ok' -or [int]$Snapshot.observationErrors -ne 0 -or
        $null -eq $Snapshot.PSObject.Properties['contacts'] -or $null -eq $Snapshot.contacts -or
        $null -eq $Snapshot.PSObject.Properties['active'] -or $null -eq $Snapshot.active) {
        throw "TouchContactObservation $Context is missing required healthy version-1 contact state."
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($record in @($Snapshot.contacts)) {
        if ($null -eq $record -or $null -eq $record.PSObject.Properties['pointerId'] -or
            $null -eq $record.PSObject.Properties['contactSerial'] -or
            $null -eq $record.PSObject.Properties['pressCount'] -or
            $null -eq $record.PSObject.Properties['moveCount'] -or
            $null -eq $record.PSObject.Properties['releaseCount'] -or
            $null -eq $record.PSObject.Properties['cancelCount'] -or
            $null -eq $record.PSObject.Properties['captureLostCount'] -or
            $null -eq $record.PSObject.Properties['rawReleaseCount'] -or
            $null -eq $record.PSObject.Properties['nativeCanceledPacketCount'] -or
            $null -eq $record.PSObject.Properties['lastNativeCanceled'] -or
            $null -eq $record.PSObject.Properties['lastRoutedEvent'] -or
            $null -eq $record.PSObject.Properties['lastEventSequence'] -or
            $null -eq $record.PSObject.Properties['inContact'] -or
            $null -eq $record.PSObject.Properties['lastEvent'] -or
            $null -eq $record.PSObject.Properties['handle'] -or
            $null -eq $record.PSObject.Properties['lastPositionRootDipX'] -or
            $null -eq $record.PSObject.Properties['lastPositionRootDipY'] -or
            $null -eq $record.PSObject.Properties['eventRasterizationScale']) {
            throw "TouchContactObservation $Context contains a contact record with missing correlation fields."
        }
        foreach ($coordinateName in @('lastPositionRootDipX','lastPositionRootDipY','eventRasterizationScale')) {
            $coordinateValue = $record.$coordinateName
            if ($null -eq $coordinateValue) {
                throw "TouchContactObservation $Context contains a null $coordinateName value."
            }
            try { $coordinate = [double]$coordinateValue }
            catch { throw "TouchContactObservation $Context contains a nonnumeric $coordinateName value." }
            if ([double]::IsNaN($coordinate) -or [double]::IsInfinity($coordinate)) {
                throw "TouchContactObservation $Context contains a nonfinite $coordinateName value."
            }
        }
        $pointerId = [uint32]$record.pointerId
        if ($pointerId -eq 0 -or -not $seen.Add([string]$pointerId)) {
            throw "TouchContactObservation $Context contains a zero or duplicate routed pointer ID."
        }
    }
    return $Snapshot
}

function Get-HostExpectedScreenTouchPoint {
    param([Parameter(Mandatory)][double]$ScreenX,[Parameter(Mandatory)][double]$ScreenY)
    if ([double]::IsNaN($ScreenX) -or [double]::IsInfinity($ScreenX) -or
        [double]::IsNaN($ScreenY) -or [double]::IsInfinity($ScreenY)) {
        throw 'The expected touch screen point must contain finite physical-pixel coordinates.'
    }
    $metadata = Invoke-GuestProbe -Mode 'metadata'
    if ($null -eq $metadata -or [string]$metadata.mode -cne 'metadata' -or
        [int]$metadata.processId -ne [int]$script:AppProcessId -or
        [long]$metadata.hwnd -ne [long]$script:AppWindowHandle) {
        throw 'The touch-point metadata did not identify the exact captured sample process and HWND.'
    }
    $dpi = [double]$metadata.dpi
    $origin = $metadata.clientOriginScreenPx
    if ([double]::IsNaN($dpi) -or [double]::IsInfinity($dpi) -or $dpi -le 0 -or
        $null -eq $origin -or $null -eq $origin.x -or $null -eq $origin.y -or
        [double]::IsNaN([double]$origin.x) -or [double]::IsNaN([double]$origin.y) -or
        [double]::IsInfinity([double]$origin.x) -or [double]::IsInfinity([double]$origin.y)) {
        throw 'The exact sample HWND metadata omitted a valid physical client origin or DPI.'
    }
    return [pscustomobject]@{
        screenX = $ScreenX
        screenY = $ScreenY
        windowDpi = $dpi
        clientOriginScreenPx = [pscustomobject]@{ x = [double]$origin.x; y = [double]$origin.y }
    }
}

function ConvertTo-HostExpectedRootClientPoint {
    param([Parameter(Mandatory)]$ExpectedScreenPoint,[Parameter(Mandatory)]$ObservedContact)
    $scale = [double]$ObservedContact.eventRasterizationScale
    $observedX = [double]$ObservedContact.lastPositionRootDipX
    $observedY = [double]$ObservedContact.lastPositionRootDipY
    $screenX = [double]$ExpectedScreenPoint.screenX
    $screenY = [double]$ExpectedScreenPoint.screenY
    if ([double]::IsNaN($scale) -or [double]::IsInfinity($scale) -or $scale -le 0 -or
        [double]::IsNaN($observedX) -or [double]::IsInfinity($observedX) -or
        [double]::IsNaN($observedY) -or [double]::IsInfinity($observedY) -or
        [double]::IsNaN($screenX) -or [double]::IsInfinity($screenX) -or
        [double]::IsNaN($screenY) -or [double]::IsInfinity($screenY)) {
        throw 'The routed contact omitted a finite XamlRoot scale or root-client point.'
    }
    $origin = $ExpectedScreenPoint.clientOriginScreenPx
    $windowDpi = [double]$ExpectedScreenPoint.windowDpi
    if ($null -eq $origin -or $null -eq $origin.x -or $null -eq $origin.y -or
        [double]::IsNaN($windowDpi) -or [double]::IsInfinity($windowDpi) -or $windowDpi -le 0 -or
        [double]::IsNaN([double]$origin.x) -or [double]::IsInfinity([double]$origin.x) -or
        [double]::IsNaN([double]$origin.y) -or [double]::IsInfinity([double]$origin.y) -or
        [Math]::Abs(($windowDpi / 96.0) - $scale) -gt 0.01) {
        throw 'The physical client origin or the XamlRoot/GetDpiForWindow scale agreement is invalid.'
    }
    return [pscustomobject]@{
        x = (($screenX - [double]$origin.x) / $scale)
        y = (($screenY - [double]$origin.y) / $scale)
        scale = $scale
        clientOriginScreenPx = [pscustomobject]@{ x = [double]$origin.x; y = [double]$origin.y }
    }
}

function Assert-HostTouchHandleDragDelivery {
    param(
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After,
        [Parameter(Mandatory)]$ExpectedPoint,
        [Parameter(Mandatory)][ValidateSet('Released')][string]$ExpectedState,
        [Parameter(Mandatory)][string]$ExpectedHandle
    )
    Assert-TouchContactObservationSnapshot -Snapshot $Before -Context 'before gesture' | Out-Null
    Assert-TouchContactObservationSnapshot -Snapshot $After -Context 'after gesture' | Out-Null
    if ([long]$Before.rendererGeneration -ne [long]$After.rendererGeneration) {
        throw 'The renderer was replaced between the before/after touch-contact snapshots.'
    }

    $beforeById = @{}
    $lastPriorSequence = 0L
    foreach ($prior in @($Before.contacts)) {
        $key = [string][uint32]$prior.pointerId
        $beforeById[$key] = $prior
        if ([long]$prior.lastEventSequence -gt $lastPriorSequence) { $lastPriorSequence = [long]$prior.lastEventSequence }
    }
    $newContacts = [Collections.Generic.List[object]]::new()
    foreach ($record in @($After.contacts)) {
        $key = [string][uint32]$record.pointerId
        $prior = $null
        if ($beforeById.ContainsKey($key)) { $prior = $beforeById[$key] }
        $isNewContact = $false
        if ($null -eq $prior) {
            $isNewContact = ([int]$record.pressCount -eq 1 -and [long]$record.contactSerial -gt 0)
        }
        else {
            $isNewContact = ([int]$record.pressCount -eq ([int]$prior.pressCount + 1) -and
                [long]$record.contactSerial -gt [long]$prior.contactSerial -and
                [long]$record.lastEventSequence -gt [long]$prior.lastEventSequence)
        }
        if ($isNewContact -and [long]$record.lastEventSequence -gt $lastPriorSequence) { $newContacts.Add($record) }
    }
    if ($newContacts.Count -ne 1) {
        throw "Expected exactly one newly routed contact serial between snapshots; observed $($newContacts.Count)."
    }

    $record = $newContacts[0]
    $prior = $null
    $pointerKey = [string][uint32]$record.pointerId
    if ($beforeById.ContainsKey($pointerKey)) { $prior = $beforeById[$pointerKey] }
    $countDelta = {
        param([string]$Name)
        $previous = if ($null -eq $prior) { 0 } else { [int]$prior.$Name }
        return ([int]$record.$Name - $previous)
    }
    $pressDelta = & $countDelta 'pressCount'
    $moveDelta = & $countDelta 'moveCount'
    $releaseDelta = & $countDelta 'releaseCount'
    $cancelDelta = & $countDelta 'cancelCount'
    $captureLostDelta = & $countDelta 'captureLostCount'
    $priorRawRelease = if ($null -eq $prior) { 0 } else { [int]$prior.rawReleaseCount }
    $priorNativeCancel = if ($null -eq $prior) { 0 } else { [int]$prior.nativeCanceledPacketCount }
    $rawReleaseDelta = [int]$record.rawReleaseCount - $priorRawRelease
    $nativeCanceledDelta = [int]$record.nativeCanceledPacketCount - $priorNativeCancel
    $lastRoutedEvent = [string]$record.lastRoutedEvent
    $lastEvent = [string]$record.lastEvent
    $captureLostTerminal = $lastEvent -ceq 'CaptureLost'
    if ($pressDelta -ne 1 -or $moveDelta -lt 1 -or $releaseDelta -ne 1 -or
        $cancelDelta -ne 0 -or $captureLostDelta -lt 0 -or $captureLostDelta -gt 1 -or
        ($captureLostTerminal -and $captureLostDelta -ne 1) -or
        [bool]$record.inContact -or $lastEvent -notin @('Released', 'CaptureLost') -or
        [long]$record.lastEventSequence -le $lastPriorSequence -or [string]$record.handle -cne $ExpectedHandle) {
        throw "The correlated touch did not complete one moved release on '$ExpectedHandle' (press=$pressDelta, move=$moveDelta, release=$releaseDelta, cancel=$cancelDelta, captureLost=$captureLostDelta, contact=$([bool]$record.inContact), last=$($record.lastEvent), handle='$($record.handle)')."
    }
    if ($rawReleaseDelta -ne $releaseDelta -or $nativeCanceledDelta -ne 0 -or [bool]$record.lastNativeCanceled -or
        ($captureLostTerminal -and $lastRoutedEvent -cne 'CaptureLost') -or
        (-not $captureLostTerminal -and $lastRoutedEvent -cne 'Released')) {
        throw 'The handle gesture ended with a canceled/native-mismatched release, not an ordinary lift.'
    }

    $activeMatches = @($After.active | Where-Object {
        [uint32]$_.pointerId -eq [uint32]$record.pointerId -and [long]$_.contactSerial -eq [long]$record.contactSerial
    })
    if ($activeMatches.Count -ne 0) { throw 'The supposedly released handle contact is still active.' }
    $expectedRootPoint = ConvertTo-HostExpectedRootClientPoint -ExpectedScreenPoint $ExpectedPoint -ObservedContact $record
    $positionError = [Math]::Sqrt(
        [Math]::Pow(([double]$record.lastPositionRootDipX - [double]$expectedRootPoint.x), 2) +
        [Math]::Pow(([double]$record.lastPositionRootDipY - [double]$expectedRootPoint.y), 2))
    if ([double]::IsNaN($positionError) -or [double]::IsInfinity($positionError) -or $positionError -gt 12.0) {
        throw "The routed handle contact ended $positionError DIPs from the expected HWND-root point."
    }

    return [pscustomobject]@{
        status = 'PASS'; rendererGeneration = [long]$After.rendererGeneration
        pointerId = [uint32]$record.pointerId; contactSerial = [long]$record.contactSerial
        lastEventSequence = [long]$record.lastEventSequence; pressDelta = $pressDelta; moveDelta = $moveDelta
        releaseDelta = $releaseDelta; cancelDelta = $cancelDelta; captureLostDelta = $captureLostDelta
        inContact = [bool]$record.inContact; activePointerCount = $activeMatches.Count
        lastEvent = [string]$record.lastEvent; handle = [string]$record.handle
        lastRoutedEvent = $lastRoutedEvent; rawReleaseDelta = $rawReleaseDelta
        nativeCanceledPacketDelta = $nativeCanceledDelta; lastNativeCanceled = [bool]$record.lastNativeCanceled
        positionRootDip = @{ x = [double]$record.lastPositionRootDipX; y = [double]$record.lastPositionRootDipY }
        expectedRootDip = @{ x = [double]$expectedRootPoint.x; y = [double]$expectedRootPoint.y }
        rasterizationScale = [double]$expectedRootPoint.scale; positionErrorDip = $positionError
    }
}

function Format-TouchHandleDragFailure {
    param([Parameter(Mandatory)][string]$Reason,[Parameter(Mandatory)]$Diagnostics)
    $serialized = ConvertTo-Json -InputObject $Diagnostics -Depth 12 -Compress
    return "Touch handle drag failed: $Reason Diagnostics: $serialized"
}

function Should-RunCorrectnessCase {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Name
    )

    if ($script:Phase1ClipboardOnly) {
        return (($Id -ceq 'C00' -and $Name -ceq 'sandbox-and-app-identity') -or
            ($Id -ceq 'C10' -and $Name -ceq 'touch-double-tap-handle-drag-context-copy-and-cancel') -or
            ($Id -ceq 'C12' -and $Name -in @('final-svg-atomic-selection-owned-viewport', 'final-svg-atomic-selection-ancestor-viewport')) -or
            $Id -in @('P1-020', 'P1-021', 'P1-030', 'P1-033'))
    }
    if ($script:Phase2Only) {
        if ($Id -ceq 'C00' -and $Name -ceq 'sandbox-and-app-identity') { return $true }
        if (-not $Id.StartsWith('P2-', [System.StringComparison]::Ordinal)) { return $false }
        if ($script:Phase2CasePattern -and $Id -cnotmatch $script:Phase2CasePattern) { return $false }
        # System High Contrast cases change a guest system setting and run
        # only when explicitly requested.
        return ($Id.EndsWith('-SYSHC', [System.StringComparison]::Ordinal) -eq $script:Phase2SystemHighContrast)
    }
    if (-not $script:ResizeCrashDiagnosticOnly) { return $true }
    return (($Id -ceq 'C00' -and $Name -ceq 'sandbox-and-app-identity') -or
        ($Id -ceq 'C14' -and $Name -ceq 'layout-resize-and-selection-state-survive-relayout'))
}

function Assert-CorrectnessRunOptions {
    param(
        [switch]$ResizeCrashDiagnosticOnly,
        [switch]$IncludeSuppressionInvestigation,
        [switch]$IncludeSelectionDisableInvestigation,
        [switch]$Phase2Only
    )

    if ($Phase2Only -and ($ResizeCrashDiagnosticOnly -or $IncludeSuppressionInvestigation -or $IncludeSelectionDisableInvestigation)) {
        throw 'Phase2Only runs only C00 and the P2-* cases; it cannot be combined with a Phase 1 diagnostic or investigation switch.'
    }

    if ($ResizeCrashDiagnosticOnly -and $IncludeSuppressionInvestigation) {
        throw 'ResizeCrashDiagnosticOnly cannot be combined with IncludeSuppressionInvestigation; the diagnostic run is limited to the existing C00 and C14 cases.'
    }
    if ($ResizeCrashDiagnosticOnly -and $IncludeSelectionDisableInvestigation) {
        throw 'ResizeCrashDiagnosticOnly cannot be combined with IncludeSelectionDisableInvestigation; the diagnostic run is limited to the existing C00 and C14 cases.'
    }
}

function Test-Case {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Requirement, [Parameter(Mandatory)][scriptblock]$Body)
    $started = [DateTime]::UtcNow
    $initialScreenshotCount = $script:Screenshots.Count
    if (-not (Should-RunCorrectnessCase -Id $Id -Name $Name)) {
        # Phase 2 and Phase 1 clipboard modes do not record the skipped batch.
        if ($script:Phase2Only -or $script:Phase1ClipboardOnly) { return }
        Add-Unverified -Id $Id -Name $Name -Reason 'Not run: resize crash diagnostic mode executes only C00 sandbox-and-app-identity and the exact C14 layout-resize-and-selection-state-survive-relayout case.'
        return
    }
    if ($script:TargetIdentityInvalid) {
        Add-Unverified -Id $Id -Name $Name -Reason "Not run because Sandbox identity was invalidated: $script:TargetIdentityFailure"
        return
    }
    try {
        $identityBefore = Assert-ProcessIdentity
        $actual = & $Body
        $identityAfter = Assert-ProcessIdentity
        $caseScreenshots = @($script:Screenshots.ToArray() | Select-Object -Skip $initialScreenshotCount)
        $script:Results.Add([pscustomobject]@{
            id = $Id; name = $Name; requirement = $Requirement; status = 'PASS'
            actual = $actual; targetIdentityBefore = $identityBefore; targetIdentityAfter = $identityAfter
            evidence = $caseScreenshots; startedUtc = $started.ToString('o'); finishedUtc = [DateTime]::UtcNow.ToString('o')
        })
    }
    catch {
        $isUnverified = $_.Exception.Message.StartsWith('UNVERIFIED: ', [System.StringComparison]::Ordinal)
        $caseScreenshots = @($script:Screenshots.ToArray() | Select-Object -Skip $initialScreenshotCount)
        $script:Results.Add([pscustomobject]@{
            id = $Id; name = $Name; requirement = $Requirement; status = $(if ($isUnverified) { 'UNVERIFIED' } else { 'FAIL' })
            actual = $null; detail = $(if ($isUnverified) { $_.Exception.Message.Substring('UNVERIFIED: '.Length) } else { $_.Exception.Message }); evidence = $caseScreenshots; startedUtc = $started.ToString('o'); finishedUtc = [DateTime]::UtcNow.ToString('o')
        })
    }
}

function Add-Unverified {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Reason)
    $script:Results.Add([pscustomobject]@{
        id = $Id; name = $Name; requirement = 'Not proven by this UI batch'; status = 'UNVERIFIED'
        actual = $null; detail = $Reason; evidence = @(); startedUtc = [DateTime]::UtcNow.ToString('o'); finishedUtc = [DateTime]::UtcNow.ToString('o')
    })
}

function Get-CaseCounts {
    $counts = [ordered]@{ pass = 0; fail = 0; unverified = 0 }
    foreach ($result in $script:Results) {
        switch ($result.status) {
            'PASS' { $counts.pass++ }
            'FAIL' { $counts.fail++ }
            'UNVERIFIED' { $counts.unverified++ }
            default { throw "Unknown test status '$($result.status)'" }
        }
    }
    return $counts
}

function Set-ToggleState {
    param([Parameter(Mandatory)][string]$AutomationId, [Parameter(Mandatory)][bool]$Enabled)
    $state = [string](Invoke-UiProperty -Selector $AutomationId -Property 'ToggleState')
    $isOn = $state -eq 'On'
    if ($isOn -ne $Enabled) { $null = Invoke-Ui @('invoke', $AutomationId, '--action', $(if ($Enabled) { 'toggle-on' } else { 'toggle-off' })) }
}

function Wait-ThemeState {
    param([Parameter(Mandatory)][string]$Theme, [Parameter(Mandatory)][string]$Contrast, [Parameter(Mandatory)][string]$TextScale)
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $state = Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'ThemeStatus'
        $contrastState = Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'HighContrastStatus'
        $scaleState = Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'TextScaleStatus'
        if ($state.name -eq "theme:$Theme" -and $contrastState.name -like "$Contrast*" -and $scaleState.name -eq "text-scale:$TextScale") { return }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The sample did not commit theme=$Theme, contrast=$Contrast, textScale=$TextScale."
}

function Get-SelectedText {
    $selection = Invoke-GuestProbe -Mode 'selection'
    return [string]$selection.text
}

function Get-SelectionHandleState {
    $state = Invoke-GuestProbe -Mode 'handles'
    $property = if ($null -eq $state) { $null } else { $state.PSObject.Properties['handles'] }
    if ($null -eq $property -or $null -eq $property.Value -or $property.Value -isnot [Array]) {
        throw 'The handles probe must return an explicit array, including an empty array when no handles are visible.'
    }
    # Return the containing object, not an enumerated handle collection. Under
    # strict mode, @(...).handles projects onto zero objects for an empty list.
    return $state
}

function Get-NativeMarkdownEditorSource {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    # The sample's native multiline TextBox stores CR line separators. Predict
    # its input normalization from the independently authored fixture; never
    # derive the expected clipboard payload from selected or displayed text.
    return $Text.Replace("`r`n", "`r").Replace("`n", "`r")
}

function Wait-ForOwnedRendererScrollBottom {
    param([int]$TimeoutMilliseconds = 8000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $state = Invoke-GuestProbe -Mode 'scroll'
        $scrollers = @($state.scrollers | Where-Object { $_.automationId -ceq 'MarkdownRenderer' })
        if ($scrollers.Count -ne 1) { throw 'Expected exactly one vertically scrollable owned MarkdownRenderer peer.' }
        $percent = [double]$scrollers[0].verticalPercent
        if ([double]::IsNaN($percent) -or [double]::IsInfinity($percent) -or $percent -lt 0 -or $percent -gt 100) {
            throw 'The owned renderer returned an invalid vertical scroll percentage.'
        }
        if ($percent -ge 99.99) { return $state }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The requested bottom scroll did not commit; last owned renderer vertical percentage=$percent."
}

function Get-CommittedTouchLayoutObservation {
    param([int]$TimeoutMilliseconds = 8000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $raw = Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'TouchContactObservation'
        $observation = ConvertFrom-Json -InputObject ([string]$raw.name)
        if ($observation.observationStatus -cne 'ok' -or [int]$observation.observationErrors -ne 0 -or
            [int]$observation.layoutCaptureErrors -ne 0) { throw 'The passive touch/layout observer is degraded.' }
        $layout = $observation.layoutCapture
        if ($layout.status -ceq 'captured' -and $null -ne $layout.snapshotSizeDip -and
            [double]$layout.rendererActualWidthDip -gt 0 -and
            [Math]::Abs([double]$layout.snapshotSizeDip.width - [double]$layout.rendererActualWidthDip) -le 1) {
            return $observation
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The passive touch observer did not capture a committed snapshot matching the current renderer width.'
}

function Assert-SelectionHandles {
    param([double]$ExpectedMinimumDip = 44)
    $handles = Get-SelectionHandleState
    $start = @($handles.handles | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownSelectionStartHandle' })
    $end = @($handles.handles | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownSelectionEndHandle' })
    if ($start.Count -ne 1 -or $end.Count -ne 1) { throw 'The UIA tree does not expose both selection handles.' }
    $minimumPx = $ExpectedMinimumDip * [double]$handles.dpiScale
    foreach ($handle in @($start[0], $end[0])) {
        if ($handle.offscreen -or $handle.width -lt $minimumPx -or $handle.height -lt $minimumPx) {
            throw "Selection handle $($handle.automationId) is hidden or below $ExpectedMinimumDip epx at scale $($handles.dpiScale)."
        }
    }
    return $handles.handles
}

function Get-RangeCenter {
    param([Parameter(Mandatory)][string]$Text)
    $range = Invoke-GuestProbe -Mode 'range' -Needle $Text
    if (@($range.rectangles).Count -ne 1) { throw "Expected one UIA text rectangle for '$Text', found $(@($range.rectangles).Count)." }
    $rect = $range.rectangles[0]
    if ($rect.width -le 0 -or $rect.height -le 0 -or $rect.x -lt 0 -or $rect.y -lt 0) { throw "UIA range '$Text' has unusable geometry." }
    return [pscustomobject]@{ x = [int][Math]::Floor($rect.x + ($rect.width / 2)); y = [int][Math]::Floor($rect.y + ($rect.height / 2)); rectangle = $rect }
}

function Get-RangeEdgePoint {
    param([Parameter(Mandatory)][string]$Text, [ValidateSet('Start','End')][string]$Endpoint)
    $range = Invoke-GuestProbe -Mode 'range' -Needle $Text
    if (@($range.rectangles).Count -ne 1) { throw "Expected one UIA text rectangle for '$Text', found $(@($range.rectangles).Count)." }
    $rect = $range.rectangles[0]
    if ($rect.width -le 2 -or $rect.height -le 0) { throw "UIA range '$Text' is too small for an unambiguous $Endpoint edge point." }
    $x = if ($Endpoint -eq 'Start') { [Math]::Ceiling([double]$rect.x + 1.0) } else { [Math]::Floor([double]$rect.x + [double]$rect.width - 1.0) }
    return [pscustomobject]@{ x=[int]$x; y=[int][Math]::Floor([double]$rect.y + ([double]$rect.height / 2.0)); rectangle=$rect; endpoint=$Endpoint }
}

function Set-TemporaryMarkdown {
    param(
        [Parameter(Mandatory)][string]$Markdown,
        [Parameter(Mandatory)][string]$RequiredText,
        [switch]$SkipRendererWait
    )
    if ($Markdown.Length -gt 8000) {
        $localPath = Join-Path $script:RunDirectory ('source-{0}.md' -f [guid]::NewGuid().ToString('N'))
        $guestFileName = 'source-{0}.md' -f [guid]::NewGuid().ToString('N')
        $guestRelativePath = "correctness\$($script:RunId)\$guestFileName"
        $guestPath = Join-Path 'C:\WinApp\work' $guestRelativePath
        [System.IO.File]::WriteAllText($localPath, $Markdown, [System.Text.UTF8Encoding]::new($false))
        $preflight = Invoke-GuestProbe -Mode 'source-file-preflight' -SourcePath $guestPath -RunId $script:RunId
        if ($preflight.mode -cne 'source-file-preflight' -or [bool]$preflight.destinationExisted -or [string]$preflight.sourcePath -cne $guestPath) {
            throw 'The run-scoped Markdown destination was not proven new and confined before staging.'
        }
        $null = Invoke-NativeChecked @('target', 'push', 'sandbox', $localPath, $guestRelativePath)
        $staged = Invoke-GuestProbe -Mode 'source-file' -SourcePath $guestPath -RequiredText $RequiredText -RunId $script:RunId
        $localHash = (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([string]$staged.sha256 -cne $localHash) { throw 'The staged Markdown source file did not match the host content hash.' }
    }
    else {
        $null = Invoke-Ui @('set-value', 'MarkdownEditor', $Markdown)
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        $current = Invoke-UiValue 'MarkdownEditor'
        if ($current.IndexOf($RequiredText, [System.StringComparison]::Ordinal) -ge 0) { break }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($current.IndexOf($RequiredText, [System.StringComparison]::Ordinal) -lt 0) { throw 'The temporary source did not reach MarkdownEditor.' }
    $expectedEditorSource = Get-NativeMarkdownEditorSource -Text $Markdown
    if ($current -cne $expectedEditorSource) {
        throw 'MarkdownEditor did not commit the independently authored fixture exactly after its native CR line-ending normalization.'
    }
    if (-not $SkipRendererWait) { $null = Wait-ForRendererText -Text $RequiredText }
}

function Assert-ProcessIdentity {
    if ($script:TargetIdentityInvalid) {
        throw "The captured Sandbox target was invalidated earlier: $script:TargetIdentityFailure"
    }
    try {
        $snapshot = (Invoke-NativeChecked @('target', 'snapshot', 'sandbox') -Json).value
        if (-not $snapshot.running -or $snapshot.executionTarget.epoch -ne $script:TargetEpoch) {
            throw 'The Sandbox epoch changed during the test; guest PID/window identity is no longer valid.'
        }
        $windows = (Invoke-NativeChecked @('ui', 'list-windows', '--on', 'sandbox') -Json).value
        $matches = @($windows | Where-Object { [int]$_.processId -eq $script:AppProcessId -and [long]$_.hwnd -eq $script:AppWindowHandle })
        if ($matches.Count -ne 1) { throw 'The captured app PID/HWND is no longer present in the selected Sandbox.' }
        if ([string]$matches[0].processName -cne 'MarkdownRenderer.Sample' -or [string]::IsNullOrWhiteSpace([string]$matches[0].title)) {
            throw 'The captured PID/HWND no longer identifies the MarkdownRenderer sample window.'
        }
        if (-not [string]::IsNullOrWhiteSpace($script:ExpectedWindowTitle) -and [string]$matches[0].title -cne $script:ExpectedWindowTitle) {
            throw "The captured app window title changed from the pinned identity '$script:ExpectedWindowTitle' to '$($matches[0].title)'."
        }
        return [pscustomobject]@{ target = 'sandbox'; epoch = $script:TargetEpoch; processId = $script:AppProcessId; hwnd = $script:AppWindowHandle; processName = $matches[0].processName; title = $matches[0].title }
    }
    catch {
        $script:TargetIdentityInvalid = $true
        $script:TargetIdentityFailure = $_.Exception.Message
        throw
    }
}

function Assert-PointOutsideSelectionHandles {
    param(
        [Parameter(Mandatory)][double]$X,
        [Parameter(Mandatory)][double]$Y,
        [Parameter(Mandatory)][object[]]$Handles,
        [double]$InjectedContactRadiusPixels = 2.0
    )
    if ([double]::IsNaN($X) -or [double]::IsInfinity($X) -or [double]::IsNaN($Y) -or [double]::IsInfinity($Y)) {
        throw 'The canceled-contact point must contain finite screen coordinates.'
    }
    if ([double]::IsNaN($InjectedContactRadiusPixels) -or [double]::IsInfinity($InjectedContactRadiusPixels) -or $InjectedContactRadiusPixels -lt 0) {
        throw 'Injected contact radius must be finite and nonnegative.'
    }
    $rectangles = [System.Collections.Generic.List[object]]::new()
    foreach ($automationId in @('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle')) {
        $matches = @($Handles | Where-Object { (Get-UiAutomationId $_) -ceq $automationId })
        if ($matches.Count -ne 1) { throw "Expected exactly one $automationId rectangle before injecting an unrelated contact; found $($matches.Count)." }
        $handle = $matches[0]
        $left = [double]$handle.x; $top = [double]$handle.y
        $width = [double]$handle.width; $height = [double]$handle.height
        if ($width -le 0 -or $height -le 0 -or [bool]$handle.offscreen) { throw "$automationId has invalid/offscreen geometry before the unrelated contact." }
        $right = $left + $width; $bottom = $top + $height
        $contactLeft = $X - $InjectedContactRadiusPixels; $contactRight = $X + $InjectedContactRadiusPixels
        $contactTop = $Y - $InjectedContactRadiusPixels; $contactBottom = $Y + $InjectedContactRadiusPixels
        if ($contactRight -ge $left -and $contactLeft -le $right -and $contactBottom -ge $top -and $contactTop -le $bottom) {
            throw "The canceled-contact injection footprint intersects $automationId; refusing to misclassify a handle gesture as ordinary text contact."
        }
        $rectangles.Add([pscustomobject]@{automationId=$automationId;x=$left;y=$top;width=$width;height=$height})
    }
    return [pscustomobject]@{
        x = $X; y = $Y; injectedContactRadiusPixels = $InjectedContactRadiusPixels
        outsideBothSelectionHandleBounds = $true; handles = @($rectangles.ToArray())
    }
}

function Navigate-And-Smoke {
    param([string]$Key, [string]$Text, [string[]]$AdditionalText = @(), [switch]$Capture)
    Navigate-Sample -Key $Key -RequiredText $Text
    $actual = Invoke-UiValue 'MarkdownRenderer'
    foreach ($phrase in $AdditionalText) {
    if ($actual.IndexOf($phrase, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Sample $Key omitted required content '$phrase'."
        }
    }
    $shot = $null
    if ($Capture) { $shot = Save-Screenshot -Name "screenshots\$Key.png" }
    return [pscustomobject]@{ page = $Key; requiredText = $Text; additionalText = @($AdditionalText); characterCount = $actual.Length; screenshot = $shot }
}

function Resolve-VectorHandleDestination {
    param(
        [Parameter(Mandatory)][double]$ObjectX,
        [Parameter(Mandatory)][double]$ObjectY,
        [Parameter(Mandatory)][double]$ObjectWidth,
        [Parameter(Mandatory)][double]$ObjectHeight,
        [Parameter(Mandatory)][double]$FingerToCaretOffsetX,
        [Parameter(Mandatory)][double]$FingerToCaretOffsetY
    )

    if ($ObjectWidth -le 0 -or $ObjectHeight -le 0) { throw 'The selectable object must have positive dimensions.' }
    $left = $ObjectX
    $right = $ObjectX + $ObjectWidth
    $top = $ObjectY
    $bottom = $ObjectY + $ObjectHeight

    # Intersect the lower-half caret range with the range that places the
    # actual fingertip inside the object after applying the measured offset.
    $caretLeft = [Math]::Max($left, $left - $FingerToCaretOffsetX)
    $caretRight = [Math]::Min($right, $right - $FingerToCaretOffsetX)
    $caretTop = [Math]::Max(($top + $ObjectHeight * 0.5), ($top - $FingerToCaretOffsetY))
    $caretBottom = [Math]::Min($bottom, ($bottom - $FingerToCaretOffsetY))
    if ($caretRight -lt $caretLeft -or $caretBottom -lt $caretTop) {
        throw 'The object has no lower-half caret point whose offset-adjusted fingertip remains inside its bounds.'
    }

    $caretX = ($caretLeft + $caretRight) / 2.0
    $caretY = ($caretTop + $caretBottom) / 2.0
    $fingerX = [int][Math]::Round($caretX + $FingerToCaretOffsetX)
    $fingerY = [int][Math]::Round($caretY + $FingerToCaretOffsetY)
    $effectiveCaretX = [double]$fingerX - $FingerToCaretOffsetX
    $effectiveCaretY = [double]$fingerY - $FingerToCaretOffsetY
    if ($fingerX -lt $left -or $fingerX -gt $right -or $fingerY -lt $top -or $fingerY -gt $bottom -or
        $effectiveCaretX -lt $caretLeft -or $effectiveCaretX -gt $caretRight -or
        $effectiveCaretY -lt ($top + $ObjectHeight * 0.5) -or $effectiveCaretY -gt $bottom) {
        throw 'Rounding the fingertip point moved it or its effective caret outside the validated object region.'
    }

    return [pscustomobject]@{
        target = [pscustomobject]@{ x = $fingerX; y = $fingerY }
        effectiveCaret = [pscustomobject]@{ x = $effectiveCaretX; y = $effectiveCaretY }
        feasibleCaretBounds = [pscustomobject]@{ left = $caretLeft; right = $caretRight; top = $caretTop; bottom = $caretBottom }
    }
}

function Resolve-MarkdownImageArtworkBounds {
    param(
        [Parameter(Mandatory)][object]$PeerBounds,
        [Parameter(Mandatory)][double]$DpiScale,
        [Parameter(Mandatory)][double]$IntrinsicWidthDip,
        [Parameter(Mandatory)][double]$IntrinsicHeightDip,
        [double]$MarginLeftDip = 0,
        [double]$MarginTopDip = 0,
        [double]$MarginRightDip = 0,
        [double]$MarginBottomDip = 0,
        [double]$MinimumParagraphLineHeightDip = 0,
        [ValidateSet('Left','Center','Right')][string]$ContentAlignment = 'Left',
        [ValidateSet('Ltr','Rtl')][string]$FlowDirection = 'Ltr',
        [AllowNull()][object]$ViewportBounds = $null
    )

    $peerX = [double]$PeerBounds.x
    $peerY = [double]$PeerBounds.y
    $peerWidth = [double]$PeerBounds.width
    $peerHeight = [double]$PeerBounds.height
    $numbers = @(
        $peerX, $peerY, $peerWidth, $peerHeight, $DpiScale,
        $IntrinsicWidthDip, $IntrinsicHeightDip,
        $MarginLeftDip, $MarginTopDip, $MarginRightDip, $MarginBottomDip,
        $MinimumParagraphLineHeightDip
    )
    foreach ($number in $numbers) {
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) {
            throw 'Image peer placement requires finite geometry, scale, intrinsic dimensions, margins, and line height.'
        }
    }
    if ($DpiScale -le 0 -or $IntrinsicWidthDip -le 0 -or $IntrinsicHeightDip -le 0 -or
        $peerWidth -le 0 -or $peerHeight -le 0 -or
        $MarginLeftDip -lt 0 -or $MarginTopDip -lt 0 -or $MarginRightDip -lt 0 -or $MarginBottomDip -lt 0 -or
        $MinimumParagraphLineHeightDip -lt 0) {
        throw 'Image peer placement contains nonpositive dimensions, scale, or negative layout insets.'
    }

    # Match ImageBox's block destination: content starts after its margins,
    # respects the explicit CanvasHorizontalAlignment, and is vertically
    # centered only when its owning paragraph line is taller than the image.
    $peerWidthDip = $peerWidth / $DpiScale
    $peerHeightDip = $peerHeight / $DpiScale
    $availableWidthDip = $peerWidthDip - $MarginLeftDip - $MarginRightDip
    $availableHeightDip = $peerHeightDip - $MarginTopDip - $MarginBottomDip
    if ($availableWidthDip -le 0 -or $availableHeightDip -le 0) {
        throw 'Image peer bounds do not contain a positive content area after margins.'
    }

    $sizeScale = [Math]::Min(1.0, $availableWidthDip / $IntrinsicWidthDip)
    $imageWidthDip = $IntrinsicWidthDip * $sizeScale
    $imageHeightDip = $IntrinsicHeightDip * $sizeScale
    $left = $peerX + ($MarginLeftDip * $DpiScale)
    $availableWidthPx = $availableWidthDip * $DpiScale
    switch ($ContentAlignment) {
        'Left' { $imageX = $left }
        'Center' { $imageX = $left + [Math]::Max(0.0, ($availableWidthPx - ($imageWidthDip * $DpiScale)) / 2.0) }
        'Right' { $imageX = $left + [Math]::Max(0.0, $availableWidthPx - ($imageWidthDip * $DpiScale)) }
        default { throw "Unsupported explicit image content alignment '$ContentAlignment'." }
    }
    $lineOffsetDip = [Math]::Max(0.0, ($MinimumParagraphLineHeightDip - $imageHeightDip) / 2.0)
    $imageY = $peerY + (($MarginTopDip + $lineOffsetDip) * $DpiScale)
    $imageWidth = $imageWidthDip * $DpiScale
    $imageHeight = $imageHeightDip * $DpiScale
    $tolerance = 0.5
    if ($imageX -lt ($peerX - $tolerance) -or $imageY -lt ($peerY - $tolerance) -or
        ($imageX + $imageWidth) -gt ($peerX + $peerWidth + $tolerance) -or
        ($imageY + $imageHeight) -gt ($peerY + $peerHeight + $tolerance)) {
        throw 'Derived image artwork lies outside the loaded MarkdownImage peer bounds.'
    }
    if ($null -ne $ViewportBounds) {
        $viewportX = [double]$ViewportBounds.x
        $viewportY = [double]$ViewportBounds.y
        $viewportWidth = [double]$ViewportBounds.width
        $viewportHeight = [double]$ViewportBounds.height
        foreach ($number in @($viewportX,$viewportY,$viewportWidth,$viewportHeight)) {
            if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) {
                throw 'Image target viewport must have finite screen geometry.'
            }
        }
        if ($viewportWidth -le 0 -or $viewportHeight -le 0 -or
            $imageX -lt ($viewportX - $tolerance) -or $imageY -lt ($viewportY - $tolerance) -or
            ($imageX + $imageWidth) -gt ($viewportX + $viewportWidth + $tolerance) -or
            ($imageY + $imageHeight) -gt ($viewportY + $viewportHeight + $tolerance)) {
            throw 'The complete image artwork is not contained by the active renderer viewport.'
        }
    }

    return [pscustomobject]@{
        bounds = [pscustomobject]@{ x=$imageX; y=$imageY; width=$imageWidth; height=$imageHeight }
        dpiScale = $DpiScale
        flowDirection = $FlowDirection
        contentAlignment = $ContentAlignment
        intrinsicWidthDip = $IntrinsicWidthDip
        intrinsicHeightDip = $IntrinsicHeightDip
        sizeScale = $sizeScale
    }
}

function Resolve-TextHandleDestination {
    param(
        [Parameter(Mandatory)][object[]]$TargetRectangles,
        [Parameter(Mandatory)][ValidateSet('Start','End')][string]$Endpoint,
        [Parameter(Mandatory)][ValidateSet('Ltr','Rtl')][string]$FlowDirection,
        [Parameter(Mandatory)][double]$DpiScale,
        [Parameter(Mandatory)][double]$FingerToCaretOffsetX,
        [Parameter(Mandatory)][double]$FingerToCaretOffsetY,
        [Parameter(Mandatory)][object]$ViewportBounds,
        [double]$CaretInsetDip = 1.0
    )

    if ($TargetRectangles.Count -lt 1) { throw 'A measured text endpoint requires at least one UIA range rectangle.' }
    $numbers = @($DpiScale,$FingerToCaretOffsetX,$FingerToCaretOffsetY,$CaretInsetDip)
    foreach ($number in $numbers) {
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) {
            throw 'Text endpoint placement requires finite DPI, offsets, and caret inset.'
        }
    }
    if ($DpiScale -le 0 -or $CaretInsetDip -le 0) {
        throw 'Text endpoint placement requires a positive DPI scale and caret inset.'
    }

    $rectangles = [System.Collections.Generic.List[object]]::new()
    foreach ($rect in $TargetRectangles) {
        if ($null -eq $rect) { throw 'A measured text range contained a null rectangle.' }
        $x = [double]$rect.x; $y = [double]$rect.y
        $width = [double]$rect.width; $height = [double]$rect.height
        foreach ($number in @($x,$y,$width,$height)) {
            if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) {
                throw 'A measured text range rectangle contains nonfinite screen geometry.'
            }
        }
        if ($width -le 0 -or $height -le 0) { throw 'A measured text range rectangle must have positive width and height.' }
        $rectangles.Add([pscustomobject]@{x=$x;y=$y;width=$width;height=$height;centerY=($y + ($height / 2.0))})
    }

    # UIA may return one rectangle per wrapped line, and can return fragments
    # in provider order rather than visual order. Resolve the endpoint line by
    # vertical position, then its visual edge according to text direction.
    $orderedByLine = @($rectangles | Sort-Object -Property centerY,x)
    $endpointLine = if ($Endpoint -eq 'Start') { $orderedByLine[0] } else { $orderedByLine[$orderedByLine.Count - 1] }
    $sameLineTolerance = [Math]::Max(1.0, $DpiScale * 0.5)
    $lineRects = @($orderedByLine | Where-Object { [Math]::Abs([double]$_.centerY - [double]$endpointLine.centerY) -le $sameLineTolerance })
    $isRightEdge = if ($FlowDirection -eq 'Ltr') { $Endpoint -eq 'End' } else { $Endpoint -eq 'Start' }
    if ($lineRects.Count -gt 1) {
        $lineRects = if ($isRightEdge) {
            @($lineRects | Sort-Object -Property @{Expression={ [double]$_.x + [double]$_.width };Descending=$true})
        } else {
            @($lineRects | Sort-Object -Property x)
        }
    }
    $rect = $lineRects[0]
    $insetPx = $CaretInsetDip * $DpiScale
    if ([double]$rect.width -le (2.0 * $insetPx)) {
        throw 'The measured endpoint line is too narrow for a DPI-scaled interior caret point.'
    }
    $caretX = if ($isRightEdge) { [double]$rect.x + [double]$rect.width - $insetPx } else { [double]$rect.x + $insetPx }
    $caretY = [double]$rect.y + ([double]$rect.height / 2.0)
    # At handle press the control stores endpoint-minus-finger. The helper
    # names the inverse (finger-minus-caret), so preserving the measured offset
    # means adding it to the desired caret position.
    $fingerX = [int][Math]::Round($caretX + $FingerToCaretOffsetX)
    $fingerY = [int][Math]::Round($caretY + $FingerToCaretOffsetY)
    $effectiveCaretX = [double]$fingerX - $FingerToCaretOffsetX
    $effectiveCaretY = [double]$fingerY - $FingerToCaretOffsetY
    $viewportX = [double]$ViewportBounds.x; $viewportY = [double]$ViewportBounds.y
    $viewportWidth = [double]$ViewportBounds.width; $viewportHeight = [double]$ViewportBounds.height
    foreach ($number in @($viewportX,$viewportY,$viewportWidth,$viewportHeight)) {
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) {
            throw 'Text endpoint viewport contains nonfinite screen geometry.'
        }
    }
    if ($viewportWidth -le 0 -or $viewportHeight -le 0 -or
        $fingerX -lt $viewportX -or $fingerX -gt ($viewportX + $viewportWidth) -or
        $fingerY -lt $viewportY -or $fingerY -gt ($viewportY + $viewportHeight)) {
        throw 'The offset-adjusted touch target is outside the active renderer viewport.'
    }
    if ($effectiveCaretX -lt ([double]$rect.x - 0.5) -or
        $effectiveCaretX -gt ([double]$rect.x + [double]$rect.width + 0.5) -or
        $effectiveCaretY -lt ([double]$rect.y - 0.5) -or
        $effectiveCaretY -gt ([double]$rect.y + [double]$rect.height + 0.5)) {
        throw 'Rounding the touch point moved its effective caret outside the measured text endpoint line.'
    }

    return [pscustomobject]@{
        target = [pscustomobject]@{x=$fingerX;y=$fingerY}
        effectiveCaret = [pscustomobject]@{x=$effectiveCaretX;y=$effectiveCaretY}
        targetCaret = [pscustomobject]@{x=$caretX;y=$caretY}
        targetRectangle = [pscustomobject]@{x=$rect.x;y=$rect.y;width=$rect.width;height=$rect.height}
        endpoint = $Endpoint
        flowDirection = $FlowDirection
        dpiScale = $DpiScale
        wrappedLineCount = $orderedByLine.Count
    }
}

function Get-RangeRectangles {
    param([Parameter(Mandatory)][string]$Text)
    $range = Invoke-GuestProbe -Mode 'range' -Needle $Text
    $rectangles = @($range.rectangles)
    if ($range.mode -cne 'range' -or [string]$range.text -cne $Text -or $rectangles.Count -lt 1) {
        throw "Expected a measured UIA range with visible rectangles for exact source text '$Text'."
    }
    # Let PowerShell enumerate the UIA rectangles into the caller's array.
    # Returning with unary comma nests the entire Object[] as one rectangle,
    # which makes multi-line source-footprint validation inspect array-valued
    # x/y fields instead of the individual measured rectangles.
    return $rectangles
}

function Assert-PointOutsideTextRanges {
    param(
        [Parameter(Mandatory)][double]$X,
        [Parameter(Mandatory)][double]$Y,
        [Parameter(Mandatory)][object[]]$Rectangles,
        [Parameter(Mandatory)][object]$ViewportBounds,
        [double]$ContactRadiusPixels = 2.0
    )
    foreach ($number in @($X,$Y,$ContactRadiusPixels,[double]$ViewportBounds.x,[double]$ViewportBounds.y,[double]$ViewportBounds.width,[double]$ViewportBounds.height)) {
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { throw 'Blank-area contact validation requires finite screen geometry.' }
    }
    if ($ContactRadiusPixels -lt 0 -or [double]$ViewportBounds.width -le 0 -or [double]$ViewportBounds.height -le 0) {
        throw 'Blank-area contact validation contains an invalid contact radius or viewport.'
    }
    if ($X -lt [double]$ViewportBounds.x -or $X -gt ([double]$ViewportBounds.x + [double]$ViewportBounds.width) -or
        $Y -lt [double]$ViewportBounds.y -or $Y -gt ([double]$ViewportBounds.y + [double]$ViewportBounds.height)) {
        throw 'The legacy blank-space touch point is outside the active renderer viewport.'
    }
    $contactLeft = $X - $ContactRadiusPixels; $contactRight = $X + $ContactRadiusPixels
    $contactTop = $Y - $ContactRadiusPixels; $contactBottom = $Y + $ContactRadiusPixels
    foreach ($rect in $Rectangles) {
        if ($null -eq $rect) { throw 'Blank-area validation received a null text-range rectangle.' }
        $left = [double]$rect.x; $top = [double]$rect.y
        $width = [double]$rect.width; $height = [double]$rect.height
        foreach ($number in @($left,$top,$width,$height)) {
            if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { throw 'A text-range rectangle has nonfinite geometry.' }
        }
        if ($width -le 0 -or $height -le 0) { throw 'A text-range rectangle must have positive dimensions.' }
        if ($contactRight -ge $left -and $contactLeft -le ($left + $width) -and
            $contactBottom -ge $top -and $contactTop -le ($top + $height)) {
            throw 'The legacy blank-space touch footprint intersects rendered authored text.'
        }
    }
    return [pscustomobject]@{x=$X;y=$Y;contactRadiusPixels=$ContactRadiusPixels;rectangleCount=$Rectangles.Count;insideRenderer=$true;outsideAuthoredText=$true}
}

function Test-FinalVectorObjectSelection {
    param(
        [Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Viewport,
        [Parameter(Mandatory)][ValidateSet('Math','Mermaid','Svg')][string]$Kind
    )

    $null = Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
    Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($Viewport -eq 'ancestor')
    # Keep the independently derived image target tied to the fixture's
    # physical-left, 96-DIP placement rather than inheriting a prior sample
    # case's presentation toggles.
    Set-ToggleState -AutomationId 'RtlToggle' -Enabled $false
    Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $false
    Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled $false
    Set-ToggleState -AutomationId 'TextScaleToggle' -Enabled $false
    Wait-ThemeState -Theme 'light' -Contrast 'hc:off' -TextScale '1'
    if ($Kind -eq 'Math') {
        $boundary = 'FinalMathBoundaryUnique'
        # Nested fractions provide enough lower-half height for the measured
        # 44-DIP handle offset in the supported DPI scenarios. Tiny vector
        # scene touchability is intentionally not inferred from this fixture.
        $semanticText = '\frac{\frac{1+1}{2}}{\frac{2+2}{4}}=1'
        $source = "$boundary`n`n`$`$`n$semanticText`n`$`$`n"
        $expectedPeerClass = 'MarkdownMath'
        $expectedPeerType = 'Image'
        $expectedAutomationPrefix = 'MarkdownMath_'
        $expectedSemanticClass = ''
        $expectedSemanticName = ''
    }
    elseif ($Kind -eq 'Mermaid') {
        $boundary = 'FinalMermaidBoundaryUnique'
        $semanticText = 'MermaidLastUnique'
        $source = @'
FinalMermaidBoundaryUnique

```mermaid
flowchart LR
  A[MermaidLastUnique]
```
'@
        $expectedPeerClass = 'MarkdownDiagram'
        $expectedPeerType = 'Image'
        $expectedAutomationPrefix = 'MarkdownDiagram_'
        $expectedSemanticClass = 'MarkdownDiagramNode'
        $expectedSemanticName = $semanticText
    }
    else {
        # Keep the image fixture small, deterministic, and independently
        # authored here so its semantic alt text and source Markdown are
        # explicit oracles rather than values read back from the selection.
        $boundary = 'FinalSvgBoundaryUnique'
        $semanticText = 'FinalSvgAtomicAltUnique'
        $svgMarkup = '<svg xmlns="http://www.w3.org/2000/svg" width="96" height="96"><rect width="96" height="96" fill="#1570ef"/></svg>'
        $svgBytes = [System.Text.Encoding]::UTF8.GetBytes($svgMarkup)
        $imageUri = 'data:image/svg+xml;base64,' + [Convert]::ToBase64String($svgBytes)
        $imageMarkdown = "![$semanticText]($imageUri)"
        $source = "$boundary`n`n$imageMarkdown`n"
        $expectedPeerClass = 'MarkdownImage'
        $expectedPeerType = 'Image'
        $expectedAutomationPrefix = 'MarkdownImage-'
        $expectedSemanticClass = ''
        $expectedSemanticName = ''
    }

    Set-TemporaryMarkdown -Markdown $source -RequiredText $semanticText
    $expectedMarkdown = (Get-NativeMarkdownEditorSource -Text $source).TrimEnd([char]13,[char]10)
    if ($expectedMarkdown.IndexOf($boundary,[StringComparison]::Ordinal) -ne 0) {
        throw "The independently authored $Kind fixture source did not start with its unique boundary token."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    $elements = @()
    $peers = @()
    do {
        $elements = @(Get-UiElements)
        $peers = @($elements | Where-Object { $_.className -eq $expectedPeerClass })
        if ($peers.Count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($peers.Count -ne 1) { throw "Expected one final $Kind UIA peer with class '$expectedPeerClass'; found $($peers.Count)." }
    $peer = $peers[0]
    if ($Kind -eq 'Math') {
        $diagnostic = Get-SampleDiagnosticMessage
        if ($diagnostic -match 'MATH100|MMR0002') { throw "Valid final Math expression emitted a fallback diagnostic: $diagnostic" }
    }
    if ($peer.type -ne $expectedPeerType -or [string]::IsNullOrWhiteSpace([string]$peer.name) -or
        -not ([string]$peer.automationId).StartsWith($expectedAutomationPrefix,[StringComparison]::Ordinal)) {
        throw "Final $Kind peer identity was unexpected: type='$($peer.type)', name='$($peer.name)', automationId='$($peer.automationId)'."
    }
    if ($expectedSemanticClass) {
        $semanticPeers = @($elements | Where-Object { $_.className -eq $expectedSemanticClass -and $_.name -ceq $expectedSemanticName })
        if ($semanticPeers.Count -ne 1 -or $semanticPeers[0].type -ne 'Group' -or
            $semanticPeers[0].isOffscreen -or $semanticPeers[0].width -le 0 -or $semanticPeers[0].height -le 0) {
            throw "Final Mermaid node did not expose exactly one visible semantic UIA group named '$expectedSemanticName'."
        }
    }
    if ($Kind -eq 'Svg') {
        $imageState = Wait-ForImagePeerState -AltText $semanticText
        $loadedImages = @($imageState.images | Where-Object {
            $_.className -ceq 'MarkdownImage' -and $_.controlType -ceq 'ControlType.Image' -and
            $_.name -ceq $semanticText -and -not [bool]$_.offscreen -and $_.width -gt 0 -and $_.height -gt 0
        })
        if ($loadedImages.Count -ne 1) {
            throw "The final SVG was not exactly one loaded, visible MarkdownImage named with its authored alt text '$semanticText'."
        }
        if ($peer.name -cne $semanticText) {
            throw "Final SVG UIA peer name '$($peer.name)' does not equal independently authored alt text '$semanticText'."
        }
    }
    if ($peer.isOffscreen -or $peer.width -le 0 -or $peer.height -le 0) { throw "Final $Kind peer is offscreen or has empty geometry." }
    $renderer = Get-UiBounds 'MarkdownRenderer'
    $peerCenterX = [double]$peer.x + ([double]$peer.width / 2.0)
    $peerCenterY = [double]$peer.y + ([double]$peer.height / 2.0)
    if ($peerCenterX -lt $renderer[0] -or $peerCenterX -gt ($renderer[0]+$renderer[2]) -or
        $peerCenterY -lt $renderer[1] -or $peerCenterY -gt ($renderer[1]+$renderer[3])) {
        throw "Final $Kind peer bounds do not intersect the active renderer viewport."
    }

    $boundaryPoint = Get-RangeCenter $boundary
    $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($boundaryPoint.x),$($boundaryPoint.y)")
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    $selectedBoundary = ''
    do { Start-Sleep -Milliseconds 80; $selectedBoundary = Get-SelectedText } while ($selectedBoundary -cne $boundary -and [DateTime]::UtcNow -lt $deadline)
    if ($selectedBoundary -cne $boundary) { throw "Could not select the unique preceding word before final $Kind; UIA selected '$selectedBoundary'." }
    $handlesBefore = Assert-SelectionHandles
    $scale = [double](Invoke-GuestProbe -Mode 'handles').dpiScale
    $initialEndHandle = @($handlesBefore | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownSelectionEndHandle' })
    if ($initialEndHandle.Count -ne 1) { throw 'The initial selected word does not have exactly one end handle.' }
    $initialSelection = Invoke-GuestProbe -Mode 'selection'
    if ($initialSelection.rangeCount -ne 1 -or $initialSelection.ranges[0].text -cne $boundary -or @($initialSelection.ranges[0].rectangles).Count -ne 1) {
        throw 'The initial boundary-word UIA selection lacks one exact range and one visible text rectangle for touch-offset calibration.'
    }
    $boundaryRect = $initialSelection.ranges[0].rectangles[0]
    $sourceCaret = [pscustomobject]@{
        x = [double]$boundaryRect.x + [double]$boundaryRect.width
        # InlineContainerBox anchors a trailing endpoint at LayoutBounds.Bottom.
        y = [double]$boundaryRect.y + [double]$boundaryRect.height
    }
    $fingerCenter = [pscustomobject]@{
        x = [double]$initialEndHandle[0].x + ([double]$initialEndHandle[0].width / 2.0)
        y = [double]$initialEndHandle[0].y + ([double]$initialEndHandle[0].height / 2.0)
    }
    $fingerToCaretOffset = [pscustomobject]@{ x = $fingerCenter.x - $sourceCaret.x; y = $fingerCenter.y - $sourceCaret.y }
    $targetBounds = [pscustomobject]@{x=[double]$peer.x;y=[double]$peer.y;width=[double]$peer.width;height=[double]$peer.height}
    $artworkBounds = $null
    $imagePlacement = $null
    if ($Kind -eq 'Svg') {
        $rendererViewport = [pscustomobject]@{x=[double]$renderer[0];y=[double]$renderer[1];width=[double]$renderer[2];height=[double]$renderer[3]}
        # This fixture is an independently-authored 96×96 SVG promoted from an
        # image-only Markdown paragraph. BuildParagraphOrImage replaces the
        # ImageBox constructor margin with the effective Body paragraph style.
        # With the sample's normalized default theme/text scale, Body is
        # 14 DIPs × 1.4 line-height with margin (0,0,0,8). Its line is shorter
        # than the image, so it adds no vertical centering. Paint and
        # AutomationRenderDestination both use GetContentX/GetContentY. Target
        # that content rectangle, not the wider peer that owns block bounds.
        $imagePlacement = Resolve-MarkdownImageArtworkBounds `
            -PeerBounds $targetBounds -DpiScale $scale `
            -IntrinsicWidthDip 96 -IntrinsicHeightDip 96 `
            -MarginLeftDip 0 -MarginTopDip 0 -MarginRightDip 0 -MarginBottomDip 8 `
            -MinimumParagraphLineHeightDip (14 * 1.4) -ContentAlignment Left -FlowDirection Ltr `
            -ViewportBounds $rendererViewport
        $artworkBounds = $imagePlacement.bounds
        $targetBounds = $artworkBounds
    }
    $destination = Resolve-VectorHandleDestination `
        -ObjectX ([double]$targetBounds.x) -ObjectY ([double]$targetBounds.y) `
        -ObjectWidth ([double]$targetBounds.width) -ObjectHeight ([double]$targetBounds.height) `
        -FingerToCaretOffsetX ([double]$fingerToCaretOffset.x) `
        -FingerToCaretOffsetY ([double]$fingerToCaretOffset.y)
    $target = $destination.target
    $effectiveCaret = $destination.effectiveCaret
    if ($Kind -eq 'Svg' -and
        ($target.x -lt $artworkBounds.x -or $target.x -gt ($artworkBounds.x + $artworkBounds.width) -or
         $target.y -lt $artworkBounds.y -or $target.y -gt ($artworkBounds.y + $artworkBounds.height) -or
         $effectiveCaret.x -lt $artworkBounds.x -or $effectiveCaret.x -gt ($artworkBounds.x + $artworkBounds.width) -or
         $effectiveCaret.y -lt $artworkBounds.y -or $effectiveCaret.y -gt ($artworkBounds.y + $artworkBounds.height))) {
        throw 'The SVG touch target or measured effective caret escaped the independently derived 96×96 artwork bounds.'
    }
    $null = Invoke-Ui @('touch','MarkdownSelectionEndHandle','--gesture','swipe','--to-point',"$($target.x),$($target.y)",'--duration-ms','350')
    $expectedSelection = "$boundary`n$semanticText"
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    $selected = ''
    do { Start-Sleep -Milliseconds 80; $selected = Get-SelectedText } while ($selected -cne $expectedSelection -and [DateTime]::UtcNow -lt $deadline)
    if ($selected -cne $expectedSelection) {
        throw "Extending the existing selection into final $Kind selected '$selected'; expected exact semantic text '$expectedSelection'."
    }
    $handlesAfter = Assert-SelectionHandles
    $selection = Invoke-GuestProbe -Mode 'selection'
    if ($selection.rangeCount -ne 1) { throw "Final $Kind selection exposed $($selection.rangeCount) UIA ranges instead of one." }
    $selectionRects = @($selection.ranges | ForEach-Object { $_.rectangles } | Where-Object { $_.width -gt 0 -and $_.height -gt 0 })
    $intersections = @($selectionRects | Where-Object {
        $_.x -lt ($peer.x+$peer.width) -and ($_.x+$_.width) -gt $peer.x -and
        $_.y -lt ($peer.y+$peer.height) -and ($_.y+$_.height) -gt $peer.y
    })
    if ($intersections.Count -eq 0) { throw "UIA selected-range geometry does not intersect final $Kind object bounds." }

    $visiblePeer = @((Get-UiElements) | Where-Object { $_.className -eq $expectedPeerClass -and (Get-UiAutomationId $_) -ceq $peer.automationId })
    if ($visiblePeer.Count -ne 1 -or $visiblePeer[0].isOffscreen -or
        $visiblePeer[0].width -le 0 -or $visiblePeer[0].height -le 0 -or $visiblePeer[0].name -cne $peer.name) {
        throw "Final $Kind visual/accessibility peer changed or disappeared after selection."
    }
    if ($Kind -eq 'Svg') {
        $loadedAfter = Wait-ForImagePeerState -AltText $semanticText
        $stillLoaded = @($loadedAfter.images | Where-Object {
            $_.className -ceq 'MarkdownImage' -and $_.name -ceq $semanticText -and
            -not [bool]$_.offscreen -and $_.width -gt 0 -and $_.height -gt 0
        })
        if ($stillLoaded.Count -ne 1) { throw 'The selected SVG no longer exposes its exact loaded semantic image peer.' }
    }
    $shot = Save-Screenshot "screenshots\$($Kind.ToLowerInvariant())-last-object-selected-$Viewport.png"

    $boundaryMenuPoint = "{0},{1}" -f $boundaryPoint.x,$boundaryPoint.y
    $null = Invoke-Ui @('drag',$boundaryMenuPoint,$boundaryMenuPoint,'--right','--hold-ms','40')
    $null = Invoke-Ui @('wait-for','MarkdownContextCopyMarkdown','-t','3000')
    $commands = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') })
    if ($commands.Count -ne 3) { throw "Final $Kind selection context menu did not expose all three native selection commands." }
    $null = Invoke-Ui @('invoke','MarkdownContextCopyMarkdown')
    Start-Sleep -Milliseconds 150
    $clipboard = Invoke-GuestProbe -Mode 'clipboard'
    if ($clipboard.selectedText -cne $expectedSelection) { throw "Copy as Markdown changed final $Kind UIA selection to '$($clipboard.selectedText)'." }
    if ($clipboard.plain -cne $expectedMarkdown) {
        throw "Copy as Markdown returned '$($clipboard.plain)' for final $Kind selection; expected the exact source '$expectedMarkdown'."
    }

    return [pscustomobject]@{
        kind=$Kind;viewport=$Viewport;peer=@{type=$peer.type;className=$peer.className;name=$peer.name;automationId=$peer.automationId;x=$peer.x;y=$peer.y;width=$peer.width;height=$peer.height}
        selected=$selected;expectedSelection=$expectedSelection;copyAsMarkdown=$clipboard.plain;expectedMarkdown=$expectedMarkdown
        geometryIntersections=@($intersections);handleDestination=$target;feasibleCaretBounds=$destination.feasibleCaretBounds;destinationInsideObject=$true
        artworkBounds=$artworkBounds;imagePlacement=$imagePlacement;destinationInsideArtwork=($Kind -ne 'Svg' -or $null -ne $artworkBounds)
        initialEndHandle=@{x=$initialEndHandle[0].x;y=$initialEndHandle[0].y;width=$initialEndHandle[0].width;height=$initialEndHandle[0].height;dpiScale=$scale}
        fingertipToCaretOffsetDip=@{x=($fingerToCaretOffset.x/$scale);y=($fingerToCaretOffset.y/$scale)};effectiveCaretDestination=$effectiveCaret
        handlesBefore=@($handlesBefore);handlesAfter=@($handlesAfter);selectionScreenshot=$shot;copyCommands=$commands.Count
    }
}

function Invoke-GuestProbeStage {
    $script:GuestProbePath = Join-Path $script:RunDirectory 'GuestUiProbe.ps1'
$source = @'
param(
    [Parameter(Mandatory)][ValidateSet('metadata','range','range-between','table-cell','scroll-range','selection','raw-name','image-state','handles','focus-state','scroll','clipboard','resize','cancel-contact','payload-hashes','source-file-preflight','source-file','near-origin-sequence','process-state','launch-selection-disable-probe')][string]$Mode,
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][long]$WindowHandle,
    [string]$Needle = '',
    [string]$StartNeedle = '',
    [string]$EndNeedle = '',
    [switch]$SelectRange,
    [string]$AutomationId = '',
    [int]$Width = 0,
    [int]$Height = 0,
    [int]$Left = 0,
    [int]$Top = 0,
    [double]$X = 0,
    [double]$Y = 0,
    [uint32]$PointerId = 0,
    [string]$SourcePath = '',
    [string]$RequiredText = '',
    [string]$RunId = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('MarkdownCorrectnessNative' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class MarkdownCorrectnessNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast { public uint Size; public uint Flags; public IntPtr DefaultScheme; }
    [DllImport("user32.dll", SetLastError=true)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool ClientToScreen(IntPtr hwnd, ref MarkdownPoint point);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
    public static Rect GetBounds(IntPtr hwnd) { Rect r; if (!GetWindowRect(hwnd, out r)) throw new Win32Exception(Marshal.GetLastWin32Error()); return r; }
    public static Rect Resize(IntPtr hwnd, int left, int top, int width, int height) { if (!SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, 0x0004 | 0x0010)) throw new Win32Exception(Marshal.GetLastWin32Error()); return GetBounds(hwnd); }
    public static uint GetDpi(IntPtr hwnd) { var v = GetDpiForWindow(hwnd); if (v == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); return v; }
    public static MarkdownPoint GetClientOriginOnScreen(IntPtr hwnd) {
        IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        if(previous==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not switch this call to per-monitor-v2 awareness for physical ClientToScreen coordinates.");
        try { var point=new MarkdownPoint(0,0); if(!ClientToScreen(hwnd,ref point)) throw new Win32Exception(Marshal.GetLastWin32Error()); return point; }
        finally { if(SetThreadDpiAwarenessContext(previous)==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not restore the caller's DPI-awareness context after ClientToScreen."); }
    }
    public static bool SystemHighContrast() { var v = new HighContrast(); v.Size = (uint)Marshal.SizeOf(typeof(HighContrast)); if (!SystemParametersInfo(0x0042, v.Size, ref v, 0)) throw new Win32Exception(Marshal.GetLastWin32Error()); return (v.Flags & 1) != 0; }
}

[Flags] public enum MarkdownPointerFlags : uint { InRange=0x00000002, InContact=0x00000004, Canceled=0x00008000, Primary=0x00002000, Down=0x00010000, Update=0x00020000, Up=0x00040000 }
public enum MarkdownPointerType : uint { Touch=2 }
public enum MarkdownButtonChangeType : int { None=0 }
[Flags] public enum MarkdownTouchMask : uint { ContactArea=1, Orientation=2, Pressure=4 }
[StructLayout(LayoutKind.Sequential)] public struct MarkdownPoint { public int X; public int Y; public MarkdownPoint(int x,int y) { X=x; Y=y; } }
[StructLayout(LayoutKind.Sequential)] public struct MarkdownRect { public int Left; public int Top; public int Right; public int Bottom; public MarkdownRect(int l,int t,int r,int b) { Left=l; Top=t; Right=r; Bottom=b; } }
[StructLayout(LayoutKind.Sequential)] public struct MarkdownPointerInfo
{
    public MarkdownPointerType PointerType; public uint PointerId; public uint FrameId; public MarkdownPointerFlags PointerFlags;
    public IntPtr SourceDevice; public IntPtr HwndTarget; public MarkdownPoint Pixel; public MarkdownPoint Himetric;
    public MarkdownPoint PixelRaw; public MarkdownPoint HimetricRaw; public uint Time; public uint HistoryCount;
    public int InputData; public uint KeyStates; public ulong PerformanceCount; public MarkdownButtonChangeType ButtonChangeType;
}
[StructLayout(LayoutKind.Sequential)] public struct MarkdownPointerTouchInfo
{
    public MarkdownPointerInfo PointerInfo; public uint TouchFlags; public MarkdownTouchMask TouchMask;
    public MarkdownRect Contact; public MarkdownRect ContactRaw; public uint Orientation; public uint Pressure;
}
 [StructLayout(LayoutKind.Sequential)] public struct MarkdownPointerTypeInfo
{
    public MarkdownPointerType Type; public MarkdownPointerTypeInfoUnion Anonymous;
}
[StructLayout(LayoutKind.Explicit)] public struct MarkdownPointerTypeInfoUnion
{
    [FieldOffset(0)] public MarkdownPointerTouchInfo TouchInfo;
}
public sealed class MarkdownTouchCancellation : IDisposable
{
    private const uint MaximumContacts = 1;
    private const uint PointerFeedbackNone = 3;
    [DllImport("user32.dll", EntryPoint="CreateSyntheticPointerDevice", SetLastError=true)] private static extern IntPtr CreateSyntheticPointerDevice(MarkdownPointerType pointerType, uint maxCount, uint feedbackMode);
    [DllImport("user32.dll", EntryPoint="InjectSyntheticPointerInput", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InjectSyntheticPointerInput(IntPtr device, [In] MarkdownPointerTypeInfo[] contacts, uint count);
    [DllImport("user32.dll", EntryPoint="DestroySyntheticPointerDevice")] private static extern void DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    private IntPtr _device;
    private bool _disposed;
    private bool _active;
    private uint _activePointerId;
    private int _lastX;
    private int _lastY;

    public MarkdownTouchCancellation()
    {
        _device = CreateSyntheticPointerDevice(MarkdownPointerType.Touch, MaximumContacts, PointerFeedbackNone);
        if (_device == IntPtr.Zero)
            throw LastError("CreateSyntheticPointerDevice(PT_TOUCH)");
    }

    private static InvalidOperationException LastError(string operation)
    {
        return LastError(operation, Marshal.GetLastWin32Error());
    }

    private static InvalidOperationException LastError(string operation, int error)
    {
        return new InvalidOperationException(operation + " failed (Win32 error " + error + " / 0x" + error.ToString("X8") + "): " + new Win32Exception(error).Message);
    }

    private static MarkdownPointerTypeInfo Create(uint pointerId, int x, int y, MarkdownPointerFlags flags)
    {
        flags |= MarkdownPointerFlags.Primary;
        var rect = new MarkdownRect(x-2,y-2,x+2,y+2);
        var touch = new MarkdownPointerTouchInfo {
            PointerInfo=new MarkdownPointerInfo { PointerType=MarkdownPointerType.Touch, PointerId=pointerId, PointerFlags=flags, Pixel=new MarkdownPoint(x,y) },
            TouchFlags=0, TouchMask=MarkdownTouchMask.ContactArea, Contact=rect
        };
        return new MarkdownPointerTypeInfo { Type=MarkdownPointerType.Touch, Anonymous=new MarkdownPointerTypeInfoUnion { TouchInfo=touch } };
    }

    private void Inject(uint pointerId, int x, int y, MarkdownPointerFlags flags, string phase)
    {
        if (_disposed || _device == IntPtr.Zero) throw new ObjectDisposedException("MarkdownTouchCancellation");
        // ERROR_NOT_READY can be transient while Windows is still processing
        // the previous frame. Replay this exact frame only; never mutate or
        // synthesize a replacement gesture during the bounded retry.
        MarkdownPointerTypeInfo[] frame = new[] { Create(pointerId,x,y,flags) };
        for (int attempt = 0; attempt <= 10; attempt++)
        {
            if (InjectSyntheticPointerInput(_device,frame,1)) return;
            int error = Marshal.GetLastWin32Error();
            if (error != 21 || attempt == 10) throw LastError(phase,error);
            System.Threading.Thread.Sleep(1);
        }
    }

    public void SendFrame(uint pointerId, int x, int y, bool down, bool fresh)
    {
        MarkdownPointerFlags flags;
        if (pointerId != 0) throw new ArgumentOutOfRangeException("pointerId", "A one-contact synthetic device uses local contact index 0.");
        if (down)
        {
            if (fresh)
            {
                if (_active) throw new InvalidOperationException("A synthetic touch contact is already active on this device.");
                flags=MarkdownPointerFlags.Down|MarkdownPointerFlags.InRange|MarkdownPointerFlags.InContact;
            }
            else
            {
                if (!_active || _activePointerId != pointerId) throw new InvalidOperationException("A stationary/move frame must belong to the active synthetic touch contact.");
                flags=MarkdownPointerFlags.Update|MarkdownPointerFlags.InRange|MarkdownPointerFlags.InContact;
            }
        }
        else
        {
            if (!_active || _activePointerId != pointerId) throw new InvalidOperationException("An up frame must belong to the active synthetic touch contact.");
            flags=MarkdownPointerFlags.Up;
        }

        Inject(pointerId,x,y,flags,down ? (fresh ? "Synthetic touch DOWN" : "Synthetic touch UPDATE") : "Synthetic touch UP");
        if (down)
        {
            _active=true;
            _activePointerId=pointerId;
            _lastX=x;
            _lastY=y;
        }
        else
        {
            _active=false;
        }
    }

    public void InjectCanceledContact(uint pointerId, int x, int y)
    {
        if (pointerId != 0) throw new ArgumentOutOfRangeException("pointerId", "A one-contact synthetic device uses local contact index 0.");
        if (_active) throw new InvalidOperationException("A synthetic touch contact is already active on this device.");
        SendFrame(pointerId,x,y,true,true);
        System.Threading.Thread.Sleep(120);
        SendFrame(pointerId,x,y,true,false);
        System.Threading.Thread.Sleep(30);
        Inject(pointerId,x,y,MarkdownPointerFlags.Canceled|MarkdownPointerFlags.Up,"Synthetic canceled touch UP");
        _active=false;
    }

    public static void SendEscape(IntPtr expectedForegroundHwnd)
    {
        if (expectedForegroundHwnd == IntPtr.Zero || GetForegroundWindow() != expectedForegroundHwnd)
            throw new InvalidOperationException("Refusing Escape because the approved sample HWND is not the foreground window.");
        keybd_event(0x1B,0,0,UIntPtr.Zero);
        keybd_event(0x1B,0,0x0002,UIntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Exception releaseFailure=null;
        try
        {
            if (_active)
            {
                try { SendFrame(_activePointerId,_lastX,_lastY,false,false); }
                catch (Exception ex) { releaseFailure=ex; }
            }
        }
        finally
        {
            IntPtr device=_device;
            _device=IntPtr.Zero;
            _disposed=true;
            if (device != IntPtr.Zero) DestroySyntheticPointerDevice(device);
        }
        if (releaseFailure != null) throw new InvalidOperationException("Could not release the outstanding synthetic touch contact before destroying its device.",releaseFailure);
    }
}
"@
}

function Get-AppWindow {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$WindowHandle)
    if ($null -eq $window) { throw "UIA app window for captured HWND $WindowHandle was not found." }
    if ($window.Current.NativeWindowHandle -ne [int]$WindowHandle -or $window.Current.ProcessId -ne $ProcessId) {
        throw 'The UIA app HWND/PID differs from the captured window identity.'
    }
    return $window
}
function Get-Renderer($Window) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MarkdownRenderer')
    $renderer = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $renderer) { throw 'UIA renderer AutomationId MarkdownRenderer was not found.' }
    return $renderer
}
function Get-Rectangles($Range) {
    $rectangles = [System.Collections.Generic.List[object]]::new()
    $values = @($Range.GetBoundingRectangles())
    foreach ($value in $values) {
        if ($value -is [System.Windows.Rect]) {
            if ($value.Width -gt 0 -and $value.Height -gt 0) { $rectangles.Add(@{x=$value.X;y=$value.Y;width=$value.Width;height=$value.Height}) }
        }
    }
    if ($rectangles.Count -eq 0 -and $values.Count -ge 4) {
        for ($i=0; $i+3 -lt $values.Count; $i+=4) {
            $w=[double]$values[$i+2]; $h=[double]$values[$i+3]
            if ($w -gt 0 -and $h -gt 0) { $rectangles.Add(@{x=[double]$values[$i];y=[double]$values[$i+1];width=$w;height=$h}) }
        }
    }
    return @($rectangles.ToArray())
}
function Find-RawElement($Window, [string]$Id) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $pending = [System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new()
    $pending.Push($Window)
    while ($pending.Count -gt 0) {
        $element=$pending.Pop()
        try {
            if ($element.Current.AutomationId -eq $Id) { return $element }
            $child=$walker.GetFirstChild($element)
            while ($null -ne $child) { $pending.Push($child); $child=$walker.GetNextSibling($child) }
        } catch { }
    }
    return $null
}
function Get-ProcessWindowRoots([int]$TargetProcessId) {
    if($TargetProcessId -ne $ProcessId){throw "Only the captured sample PID $ProcessId may be traversed; refusing PID $TargetProcessId."}
    # Always include the exact captured HWND. Activate Raw view for both
    # same-process top-level-window discovery and descendant lookup; otherwise
    # FindAll silently falls back to Control view and omits shell-only peers.
    $captured=Get-AppWindow
    $roots=[Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()
    $seenHandles=[Collections.Generic.HashSet[long]]::new()
    $capturedHandle=[long]$captured.Current.NativeWindowHandle
    if($capturedHandle -ne [long]$WindowHandle -or [int]$captured.Current.ProcessId -ne $TargetProcessId){throw 'The captured UIA root no longer identifies the approved sample HWND and PID.'}
    $roots.Add($captured);[void]$seenHandles.Add($capturedHandle)
    $desktop=[System.Windows.Automation.AutomationElement]::RootElement
    $pidCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$TargetProcessId)
    $cache=[System.Windows.Automation.CacheRequest]::new()
    $cache.TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition
    $rawScope=$cache.Activate()
    try{$topLevelWindows=$desktop.FindAll([System.Windows.Automation.TreeScope]::Children,$pidCondition)}
    finally{$rawScope.Dispose()}
    for($i=0;$i -lt $topLevelWindows.Count;$i++){
        $candidate=$topLevelWindows.Item($i);$current=$candidate.Current
        if([int]$current.ProcessId -ne $TargetProcessId){continue}
        $handle=[long]$current.NativeWindowHandle
        if($handle -gt 0 -and $seenHandles.Add($handle)){$roots.Add($candidate)}
    }
    return $roots.ToArray()
}
function Get-ProcessElements([int]$TargetProcessId) {
    # Entries are provenance envelopes containing both the UIA element and
    # the verified HWND/PID that authorized traversing it. They are not raw
    # AutomationElement instances, so the collection must accept objects.
    $elements=[System.Collections.Generic.List[object]]::new()
    $seenRuntimeIds=[System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $cache=[System.Windows.Automation.CacheRequest]::new()
    $cache.TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition
    $rawScope=$cache.Activate()
    try{
        foreach($root in @(Get-ProcessWindowRoots $TargetProcessId)){
            $rootCurrent=$root.Current
            $rootHandle=[long]$rootCurrent.NativeWindowHandle
            if([int]$rootCurrent.ProcessId -ne $TargetProcessId -or $rootHandle -le 0){throw 'A raw UIA traversal root is not a verified same-process HWND.'}
            $candidates=[System.Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()
            if([int]$rootCurrent.ProcessId -eq $TargetProcessId){$candidates.Add($root)}
            # Raw-view CacheRequest is essential here. A plain FindAll search
            # can return zero for shell peers while real UIA providers expose them.
            $descendants=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            for($i=0;$i -lt $descendants.Count;$i++){$candidates.Add($descendants.Item($i))}
            foreach($element in $candidates){
                $current=$element.Current
                $elementProcessId=[int]$current.ProcessId
                # WinUI raw XAML peers report ProcessId=0/HWND=0. Accept such a
                # peer only as a descendant of this verified same-process HWND.
                # Explicit nonzero foreign processes are never admitted.
                if($elementProcessId -ne $TargetProcessId -and $elementProcessId -ne 0){continue}
                $runtimeId=@($element.GetRuntimeId())
                if($runtimeId.Count -eq 0){throw 'A same-process raw UIA element returned an empty runtime ID.'}
                $runtimeKey=($runtimeId -join ',')
                if($seenRuntimeIds.Add($runtimeKey)){
                    $elements.Add([pscustomobject]@{element=$element;ownerProcessId=$TargetProcessId;ownerWindowHandle=$rootHandle;reportedProcessId=$elementProcessId})
                }
            }
        }
    }finally{$rawScope.Dispose()}
    return $elements.ToArray()
}
function Resolve-UniqueProcessAutomationId([object[]]$Elements,[int]$TargetProcessId,[string]$Id) {
    $matchedElements=[System.Collections.Generic.List[object]]::new()
    foreach($candidate in $Elements){
        if($null -eq $candidate -or $null -eq $candidate.element){continue}
        $ownerProcessId=[int]$candidate.ownerProcessId
        $ownerWindowHandle=[long]$candidate.ownerWindowHandle
        if($ownerProcessId -ne $TargetProcessId -or $ownerWindowHandle -le 0){continue}
        $current=$candidate.element.Current
        $reportedProcessId=[int]$current.ProcessId
        if(($reportedProcessId -eq $TargetProcessId -or $reportedProcessId -eq 0) -and
            [int]$candidate.reportedProcessId -eq $reportedProcessId -and
            [string]$current.AutomationId -ceq $Id){$matchedElements.Add($candidate.element)}
    }
    if($matchedElements.Count -ne 1){throw "Expected exactly one raw AutomationId '$Id' rooted in verified PID $TargetProcessId HWNDs; found $($matchedElements.Count)."}
    return $matchedElements[0]
}
function Find-UniqueProcessElement([int]$TargetProcessId,[string]$Id) {
    $elements=@(Get-ProcessElements $TargetProcessId)
    return Resolve-UniqueProcessAutomationId -Elements $elements -TargetProcessId $TargetProcessId -Id $Id
}
function Get-TouchContactObservation {
    $element=Find-UniqueProcessElement $ProcessId 'TouchContactObservation'
    $json=[string]$element.Current.Name
    if([string]::IsNullOrWhiteSpace($json)){throw 'The sample touch-contact observer exposed no UIA snapshot.'}
    try{$observation=ConvertFrom-Json -InputObject $json}
    catch{throw "The sample touch-contact observer returned malformed JSON: $($_.Exception.Message)"}
    if([int]$observation.version -ne 1){throw "Unsupported sample touch-contact observation version '$($observation.version)'."}
    if([string]$observation.observationStatus -cne 'ok' -or [int]$observation.observationErrors -ne 0){
        throw "The sample touch-contact observer is degraded (status='$($observation.observationStatus)', errors=$($observation.observationErrors))."
    }
    if($null -eq $observation.contacts -or $null -eq $observation.active){throw 'The sample touch-contact snapshot omitted contacts/active arrays.'}
    return $observation
}
function Get-ExpectedScreenTouchPoint {
    param([Parameter(Mandatory)][double]$ScreenX,[Parameter(Mandatory)][double]$ScreenY)
    # Keep the native screen-pixel target. The observer converts it to root
    # client DIPs using the event's XamlRoot scale and an HWND client origin;
    # renderer UIA bounds are clipped after scrolling and cannot anchor it.
    $origin=[MarkdownCorrectnessNative]::GetClientOriginOnScreen([IntPtr]$WindowHandle)
    $dpi=[MarkdownCorrectnessNative]::GetDpi([IntPtr]$WindowHandle)
    return [pscustomobject]@{screenX=$ScreenX;screenY=$ScreenY;windowDpi=$dpi;clientOriginScreenPx=@{x=$origin.X;y=$origin.Y}}
}
function ConvertTo-ExpectedRootClientPoint {
    param([Parameter(Mandatory)]$ExpectedScreenPoint,[Parameter(Mandatory)]$ObservedContact)
    $scale=[double]$ObservedContact.eventRasterizationScale
    if($null -eq $ObservedContact.eventRasterizationScale -or [double]::IsNaN($scale) -or [double]::IsInfinity($scale) -or $scale -le 0){
        throw 'The routed contact did not report a valid event XamlRoot rasterization scale.'
    }
    if($null -eq $ObservedContact.lastPositionRootDipX -or $null -eq $ObservedContact.lastPositionRootDipY){
        throw 'The routed contact observer omitted the XamlRoot-content point.'
    }
    $origin=$ExpectedScreenPoint.clientOriginScreenPx
    if($null -eq $origin -or $null -eq $origin.x -or $null -eq $origin.y){throw 'The expected input point omitted the captured native HWND client origin.'}
    $windowDpi=[double]$ExpectedScreenPoint.windowDpi
    if($windowDpi -le 0 -or [Math]::Abs(($windowDpi/96.0)-$scale) -gt 0.01){throw "The routed XamlRoot rasterization scale ($scale) disagrees with GetDpiForWindow ($windowDpi)."}
    return [pscustomobject]@{x=(([double]$ExpectedScreenPoint.screenX-[double]$origin.x)/$scale);y=(([double]$ExpectedScreenPoint.screenY-[double]$origin.y)/$scale);scale=$scale;clientOriginScreenPx=@{x=$origin.x;y=$origin.y}}
}
function Assert-SyntheticTouchDelivery {
    param([Parameter(Mandatory)]$Before,[Parameter(Mandatory)]$After,[Parameter(Mandatory)]$ExpectedPoint,[Parameter(Mandatory)][ValidateSet('Active','Released','Canceled')][string]$ExpectedState,[string]$ExpectedHandle='')
    if([int]$Before.version -ne 1 -or [int]$After.version -ne 1 -or
       [string]$Before.observationStatus -cne 'ok' -or [string]$After.observationStatus -cne 'ok' -or
       [int]$Before.observationErrors -ne 0 -or [int]$After.observationErrors -ne 0 -or
       [long]$Before.rendererGeneration -le 0 -or [long]$Before.rendererGeneration -ne [long]$After.rendererGeneration){
        throw 'The sample observer was degraded or changed renderer generation during synthetic touch delivery.'
    }
    $beforeById=@{}
    foreach($prior in $Before.contacts){$beforeById[[string][uint32]$prior.pointerId]=$prior}
    $newContacts=[Collections.Generic.List[object]]::new()
    foreach($record in $After.contacts){
        $key=[string][uint32]$record.pointerId
        if($beforeById.ContainsKey($key)){
            $prior=$beforeById[$key]
            if([int]$record.pressCount -eq ([int]$prior.pressCount+1) -and
               [long]$record.contactSerial -gt [long]$prior.contactSerial -and
               [long]$record.lastEventSequence -gt [long]$prior.lastEventSequence){$newContacts.Add($record)}
        }elseif([int]$record.pressCount -eq 1 -and [long]$record.contactSerial -gt 0 -and [long]$record.lastEventSequence -gt 0){
            $newContacts.Add($record)
        }
    }
    if($newContacts.Count -ne 1){throw "Expected one newly routed touch contact based on contactSerial/press/event phase; found $($newContacts.Count). Injected pointer IDs are not used as routed identity."}
    $record=$newContacts[0]
    $prior=$null
    if($beforeById.ContainsKey([string][uint32]$record.pointerId)){$prior=$beforeById[[string][uint32]$record.pointerId]}
    $pressDelta=[int]$record.pressCount - $(if($null -eq $prior){0}else{[int]$prior.pressCount})
    $moveDelta=[int]$record.moveCount - $(if($null -eq $prior){0}else{[int]$prior.moveCount})
    $cancelDelta=[int]$record.cancelCount - $(if($null -eq $prior){0}else{[int]$prior.cancelCount})
    $releaseDelta=[int]$record.releaseCount - $(if($null -eq $prior){0}else{[int]$prior.releaseCount})
    $captureLostDelta=[int]$record.captureLostCount - $(if($null -eq $prior){0}else{[int]$prior.captureLostCount})
    $rawReleaseDelta = $releaseDelta
    $nativeCanceledPacketDelta = 0
    if ($null -ne $record.PSObject.Properties['rawReleaseCount']) {
        $priorRawReleases = if ($null -eq $prior) { 0 } elseif ($null -ne $prior.PSObject.Properties['rawReleaseCount']) { [int]$prior.rawReleaseCount } else { [int]$prior.releaseCount }
        $priorNativeCancellations = if ($null -eq $prior -or $null -eq $prior.PSObject.Properties['nativeCanceledPacketCount']) { 0 } else { [int]$prior.nativeCanceledPacketCount }
        $rawReleaseDelta = [int]$record.rawReleaseCount - $priorRawReleases
        $nativeCanceledPacketDelta = [int]$record.nativeCanceledPacketCount - $priorNativeCancellations
        if ($rawReleaseDelta -ne $releaseDelta -and
            ($ExpectedState -cne 'Canceled' -or $rawReleaseDelta -ne ($releaseDelta + 1) -or
             $nativeCanceledPacketDelta -ne 1 -or $cancelDelta -ne 1 -or
             ([string]$record.lastRoutedEvent -ceq 'Released' -and -not [bool]$record.lastNativeCanceled))) {
            throw 'A routed release cannot count as cancellation without the independently observed native IsCanceled flag.'
        }
    }
    $expectedRootPoint=ConvertTo-ExpectedRootClientPoint -ExpectedScreenPoint $ExpectedPoint -ObservedContact $record
    $positionError=[Math]::Sqrt([Math]::Pow(([double]$record.lastPositionRootDipX-[double]$expectedRootPoint.x),2)+[Math]::Pow(([double]$record.lastPositionRootDipY-[double]$expectedRootPoint.y),2))
    if($pressDelta -ne 1 -or $positionError -gt 12.0){throw "The new routed contact does not match the injected gesture phase/root-client coordinate (pressDelta=$pressDelta, errorDip=$positionError)."}
    $sameActive=@($After.active|Where-Object{[uint32]$_.pointerId -eq [uint32]$record.pointerId -and [long]$_.contactSerial -eq [long]$record.contactSerial})
    switch($ExpectedState){
        'Active' {
            if($sameActive.Count -ne 1 -or -not [bool]$record.inContact -or $moveDelta -lt 1 -or $cancelDelta -ne 0 -or $releaseDelta -ne 0 -or $captureLostDelta -ne 0 -or [string]$record.lastEvent -cne 'Moved' -or ([string]::IsNullOrEmpty($ExpectedHandle) -eq $false -and [string]$record.handle -cne $ExpectedHandle)){
                throw "New routed contact $($record.pointerId)/$($record.contactSerial) is not the expected active $ExpectedHandle move (move=$moveDelta, cancel=$cancelDelta, release=$releaseDelta, captureLost=$captureLostDelta, active=$($record.inContact), last=$($record.lastEvent))."
            }
        }
        'Released' {
            if($sameActive.Count -ne 0 -or [bool]$record.inContact -or $cancelDelta -ne 0 -or $releaseDelta -ne 1 -or $captureLostDelta -lt 0 -or $captureLostDelta -gt 1 -or [string]$record.lastEvent -notin @('Released','CaptureLost')){
                throw "New routed contact $($record.pointerId)/$($record.contactSerial) did not release once as expected."
            }
        }
        'Canceled' {
            if($sameActive.Count -ne 0 -or [bool]$record.inContact -or $moveDelta -lt 1 -or $cancelDelta -ne 1 -or $releaseDelta -ne 0 -or $captureLostDelta -lt 0 -or $captureLostDelta -gt 1 -or [string]$record.lastEvent -notin @('Canceled','CaptureLost')){
                $diagnostics=[ordered]@{
                    pointerId=[uint32]$record.pointerId;contactSerial=[long]$record.contactSerial
                    pressDelta=$pressDelta;moveDelta=$moveDelta;cancelDelta=$cancelDelta;releaseDelta=$releaseDelta;captureLostDelta=$captureLostDelta
                    activePointerCount=$sameActive.Count;inContact=[bool]$record.inContact;lastEvent=[string]$record.lastEvent;lastEventSequence=[long]$record.lastEventSequence
                    positionErrorDip=$positionError;expectedRootPoint=$expectedRootPoint;observerBefore=$Before;observerAfter=$After
                }
                # P1-031: the injected POINTER_FLAG_CANCELED|UP reached WinUI as
                # one ordinary routed release with no native IsCanceled packet.
                # The stimulus never reached the product as a cancellation, so
                # this is a delivery limit, distinct from a mishandled cancel.
                if ($null -ne $record.PSObject.Properties['nativeCanceledPacketCount'] -and
                    $null -ne $record.PSObject.Properties['lastNativeCanceled'] -and
                    $null -ne $record.PSObject.Properties['lastRoutedEvent'] -and
                    $sameActive.Count -eq 0 -and -not [bool]$record.inContact -and $moveDelta -ge 1 -and
                    $cancelDelta -eq 0 -and $releaseDelta -eq 1 -and $rawReleaseDelta -eq 1 -and $captureLostDelta -eq 0 -and
                    $nativeCanceledPacketDelta -eq 0 -and -not [bool]$record.lastNativeCanceled -and
                    [string]$record.lastRoutedEvent -ceq 'Released' -and [string]$record.lastEvent -ceq 'Released') {
                    throw ("INJECTED-CANCEL-NOT-NATIVE: New routed contact $($record.pointerId)/$($record.contactSerial) arrived as one ordinary release with no native IsCanceled packet. Diagnostics: " + (ConvertTo-AsciiJson -Value $diagnostics))
                }
                throw ("New routed contact $($record.pointerId)/$($record.contactSerial) did not cancel without an ordinary release. Diagnostics: " + (ConvertTo-AsciiJson -Value $diagnostics))
            }
        }
    }
    return [pscustomobject]@{status='PASS';rendererGeneration=[long]$After.rendererGeneration;pointerId=[uint32]$record.pointerId;contactSerial=[long]$record.contactSerial;lastEventSequence=[long]$record.lastEventSequence;pressCount=[int]$record.pressCount;moveCount=[int]$record.moveCount;cancelCount=[int]$record.cancelCount;releaseCount=[int]$record.releaseCount;captureLostCount=[int]$record.captureLostCount;pressDelta=$pressDelta;moveDelta=$moveDelta;cancelDelta=$cancelDelta;releaseDelta=$releaseDelta;captureLostDelta=$captureLostDelta;inContact=[bool]$record.inContact;lastEvent=[string]$record.lastEvent;activePointerCount=$sameActive.Count;positionRootDip=@{x=[double]$record.lastPositionRootDipX;y=[double]$record.lastPositionRootDipY};expectedRootDip=@{x=[double]$expectedRootPoint.x;y=[double]$expectedRootPoint.y};clientOriginScreenPx=$expectedRootPoint.clientOriginScreenPx;rasterizationScale=$expectedRootPoint.scale;positionErrorDip=$positionError;handle=[string]$record.handle}
}
function Get-Pattern($Element, $Pattern) {
    $object=$null
    if (-not $Element.TryGetCurrentPattern($Pattern,[ref]$object)) { return $null }
    return $object
}

function ConvertTo-AsciiJson {
    param([Parameter(Mandatory)]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 12 -Compress
    $builder = [System.Text.StringBuilder]::new($json.Length)
    foreach ($character in $json.ToCharArray()) {
        $codeUnit = [int]$character
        if ($codeUnit -gt 0x7f) {
            $null = $builder.AppendFormat([System.Globalization.CultureInfo]::InvariantCulture, '\u{0:X4}', $codeUnit)
        }
        else {
            $null = $builder.Append($character)
        }
    }
    return $builder.ToString()
}

function Normalize-MarkdownEditorLineEndings {
    param([AllowEmptyString()][Parameter(Mandatory)][string]$Text)
    # WinUI TextBox normalizes line separators. Preserve every other code unit:
    # this is not trimming, whitespace folding, or Markdown normalization.
    return $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Get-GuestSampleProcessIdentity {
    param([Parameter(Mandatory)][int]$ExpectedProcessId)
    if ($ExpectedProcessId -le 0) { throw 'A positive captured sample PID is required.' }
    $process = Get-Process -Id $ExpectedProcessId -ErrorAction Stop
    if ($null -eq $process -or $process.HasExited -or [int]$process.Id -ne $ExpectedProcessId) {
        throw "The exact captured sample PID $ExpectedProcessId is not live."
    }
    $processName = [string]$process.ProcessName
    $rawPath = [string]$process.Path
    if ($processName -cne 'MarkdownRenderer.Sample' -or [string]::IsNullOrWhiteSpace($rawPath) -or
        -not [IO.Path]::IsPathRooted($rawPath) -or [IO.Path]::GetFileName($rawPath) -cne 'MarkdownRenderer.Sample.exe') {
        throw "PID $ExpectedProcessId is not the captured MarkdownRenderer.Sample executable."
    }
    try { $fullPath = [IO.Path]::GetFullPath($rawPath); $startTimeUtc = $process.StartTime.ToUniversalTime().ToString('o') }
    catch { throw "Could not capture exact sample process path/start time for PID ${ExpectedProcessId}: $($_.Exception.Message)" }
    return [pscustomobject]@{processId=[int]$process.Id;processName=$processName;executablePath=$fullPath;startTimeUtc=$startTimeUtc}
}

function New-SelectionDisableLaunchDiagnostics {
    param(
        [Parameter(Mandatory)][string]$RunId,
        [string]$GuestWorkRoot = 'C:\WinApp\work'
    )
    if ($RunId -cnotmatch '^\d{8}T\d{6}Z-[0-9a-f]{32}$') {
        throw 'Selection-disable launch diagnostics require the current run ID in its canonical timestamp/GUID form.'
    }
    if ([string]::IsNullOrWhiteSpace($GuestWorkRoot) -or -not [IO.Path]::IsPathRooted($GuestWorkRoot)) {
        throw 'Selection-disable launch diagnostics require a rooted guest work directory.'
    }

    $runRoot = Join-Path (Join-Path $GuestWorkRoot 'correctness') $RunId
    if (-not (Test-Path -LiteralPath $runRoot -PathType Container)) {
        throw 'The run-scoped guest correctness directory is missing; refusing to create diagnostics outside the staged run.'
    }
    $directory = Join-Path $runRoot 'selection-disable-launch'
    if (Test-Path -LiteralPath $directory) {
        throw 'The run-scoped selection-disable diagnostic directory already exists; refusing to overwrite evidence.'
    }
    $null = New-Item -ItemType Directory -Path $directory -ErrorAction Stop
    $stdout = Join-Path $directory 'stdout.txt'
    $stderr = Join-Path $directory 'stderr.txt'
    if ((Test-Path -LiteralPath $stdout) -or (Test-Path -LiteralPath $stderr)) {
        throw 'A selection-disable diagnostic output already exists; refusing to overwrite evidence.'
    }
    $relativeDirectory = "correctness\$RunId\selection-disable-launch"
    return [pscustomobject]@{
        guestDirectory = $directory
        guestRelativeDirectory = $relativeDirectory
        guestStdoutPath = $stdout
        guestStderrPath = $stderr
        guestStdoutRelativePath = "$relativeDirectory\stdout.txt"
        guestStderrRelativePath = "$relativeDirectory\stderr.txt"
    }
}

function Get-SelectionDisableLaunchObservation {
    param([Parameter(Mandatory)][object]$Process)
    $Process.Refresh()
    if ([bool]$Process.HasExited) {
        return [pscustomobject]@{
            status = 'process-exited-before-window-observation'
            processExited = $true
            exitCode = [int]$Process.ExitCode
            mainWindowHandle = 0L
            mainWindowTitle = ''
        }
    }
    $handle = 0L
    try { $handle = [int64]$Process.MainWindowHandle } catch { }
    $title = ''
    try { $title = [string]$Process.MainWindowTitle } catch { }
    return [pscustomobject]@{
        status = $(if ($handle -gt 0 -and $title -ceq 'MarkdownRenderer selection-disable probe') { 'window-candidate-observed' } else { 'process-live-no-window-observed' })
        processExited = $false
        exitCode = $null
        mainWindowHandle = $handle
        mainWindowTitle = $title
    }
}

function Get-SelectionDisableLaunchIdentityFromHandle {
    param(
        [Parameter(Mandatory)][object]$Process,
        [Parameter(Mandatory)]$SourceIdentity,
        [Parameter(Mandatory)][int]$OriginalProcessId
    )
    $Process.Refresh()
    $processId = [int]$Process.Id
    if ($processId -le 0 -or $processId -eq $OriginalProcessId) {
        throw 'The retained launch process handle does not identify a distinct positive child PID.'
    }
    try { $startTimeUtc = $Process.StartTime.ToUniversalTime().ToString('o') }
    catch { throw "Could not read the launched child start time from its retained process handle: $($_.Exception.Message)" }
    return [pscustomobject]@{
        processId = $processId
        processName = 'MarkdownRenderer.Sample'
        executablePath = [IO.Path]::GetFullPath([string]$SourceIdentity.executablePath)
        startTimeUtc = $startTimeUtc
    }
}

function Get-SelectionDisableChildRuntimeRoot {
    param([Parameter(Mandatory)][int]$OriginalProcessId)
    $hostfxr = @((Get-Process -Id $OriginalProcessId -ErrorAction Stop).Modules |
        Where-Object { [string]::Equals($_.ModuleName, 'hostfxr.dll', [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { [string]$_.FileName })
    if ($hostfxr.Count -ne 1) {
        throw "The original sample process must load exactly one hostfxr.dll; found $($hostfxr.Count)."
    }
    # <root>\host\fxr\<version>\hostfxr.dll
    $root = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $hostfxr[0]) '..\..\..'))
    if (-not (Test-Path -LiteralPath (Join-Path $root 'shared') -PathType Container)) {
        throw "The original sample's runtime root '$root' has no shared frameworks."
    }
    return $root
}

function Start-DetachedGuestProcess {
    param(
        [Parameter(Mandatory)][string]$CommandLine,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [ValidateSet(0,1)][int]$ShowWindow = 1
    )
    # T1-017: `winapp target exec` closes its job object when this guest script
    # returns, terminating every Start-Process descendant. Win32_Process.Create
    # starts the process outside that job and does not inherit this script's
    # stdout/stderr pipes (T1-016). Its environment block replaces the inherited
    # one, so pass this process's full block, including DOTNET_ROOT.
    $environmentBlock = [string[]]@(Get-ChildItem Env: | ForEach-Object { '{0}={1}' -f $_.Name, $_.Value })
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]$ShowWindow; EnvironmentVariables = $environmentBlock }
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = $CommandLine; CurrentDirectory = $WorkingDirectory; ProcessStartupInformation = $startup
    }
    if ($null -eq $created -or [int]$created.ReturnValue -ne 0 -or [int]$created.ProcessId -le 0) {
        $returnValue = if ($null -ne $created) { [string]$created.ReturnValue } else { 'none' }
        throw "Win32_Process.Create failed with return value $returnValue."
    }
    return [int]$created.ProcessId
}

function Wait-DetachedGuestChildProcess {
    param(
        [Parameter(Mandatory)][int]$ParentProcessId,
        [Parameter(Mandatory)][string]$ImageName,
        [Parameter(Mandatory)][DateTime]$NotBeforeUtc,
        [ValidateRange(1,30000)][int]$TimeoutMilliseconds = 10000
    )
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $children = @(Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId = $ParentProcessId" |
            Where-Object { [string]::Equals([string]$_.Name, $ImageName, [StringComparison]::OrdinalIgnoreCase) -and
                $null -ne $_.CreationDate -and ([DateTime]$_.CreationDate).ToUniversalTime() -ge $NotBeforeUtc })
        if ($children.Count -gt 1) { throw "Detached launcher $ParentProcessId started more than one $ImageName." }
        if ($children.Count -eq 1) {
            # Read HasExited immediately so this Process object opens and keeps
            # the child handle, preserving its exit code for later observation.
            $child = Get-Process -Id ([int]$children[0].ProcessId) -ErrorAction Stop
            [void]$child.HasExited
            return $child
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Detached launcher $ParentProcessId did not start $ImageName within $TimeoutMilliseconds ms."
}

function Start-SelectionDisableProbeProcess {
    param(
        [Parameter(Mandatory)][int]$OriginalProcessId,
        [string]$RunId = '',
        [string]$GuestWorkRoot = 'C:\WinApp\work',
        [ValidateRange(0,30000)][int]$StartupObservationMilliseconds = 10000
    )
    $sourceBefore = Get-GuestSampleProcessIdentity -ExpectedProcessId $OriginalProcessId
    $executableDirectory = Split-Path -Parent $sourceBefore.executablePath
    $diagnostics = $null
    if (-not [string]::IsNullOrWhiteSpace($RunId)) {
        $diagnostics = New-SelectionDisableLaunchDiagnostics -RunId $RunId -GuestWorkRoot $GuestWorkRoot
    }
    # This is the already-deployed, live executable identified by the original
    # guest PID; no project-mode run/build/deploy is involved. The argument is
    # fixed by the test protocol, not supplied by the host or a document.
    if ($null -eq $diagnostics) {
        # Kept solely for the existing offline function-contract tests, which
        # invoke this embedded helper directly. The actual guest dispatcher
        # rejects a missing RunId before this function can be called.
        $launcherProcessId = Start-DetachedGuestProcess -CommandLine ('"' + $sourceBefore.executablePath + '" --renderer-selection-disable-probe') -WorkingDirectory $executableDirectory
        $started = Get-Process -Id $launcherProcessId -ErrorAction Stop
    }
    else {
        # T1-012: the framework-dependent sample runs on winapp's private .NET
        # runtime, which a direct launch does not inherit; without it the host
        # shows a "You must install .NET Desktop Runtime" dialog instead of the
        # app. Use the exact runtime root the original process loaded.
        $runtimeRoot = Get-SelectionDisableChildRuntimeRoot -OriginalProcessId $OriginalProcessId
        $env:DOTNET_ROOT = $runtimeRoot
        $env:DOTNET_ROOT_X64 = $runtimeRoot
        # T1-016/T1-017: a detached cmd.exe owns the run-scoped redirects, so the
        # probe survives this script's return and never holds the exec pipes.
        $launchNotBeforeUtc = [DateTime]::UtcNow.AddSeconds(-1)
        $commandLine = 'cmd.exe /d /s /c ""' + $sourceBefore.executablePath + '" --renderer-selection-disable-probe 1>"' +
            $diagnostics.guestStdoutPath + '" 2>"' + $diagnostics.guestStderrPath + '""'
        # SW_HIDE applies to the wrapper's console only; the sample window opens normally.
        $launcherProcessId = Start-DetachedGuestProcess -CommandLine $commandLine -WorkingDirectory $executableDirectory -ShowWindow 0
        $started = Wait-DetachedGuestChildProcess -ParentProcessId $launcherProcessId -ImageName 'MarkdownRenderer.Sample.exe' -NotBeforeUtc $launchNotBeforeUtc
    }
    if ($null -eq $started -or [int]$started.Id -le 0 -or [int]$started.Id -eq $OriginalProcessId) {
        throw 'The launcher did not return a distinct positive guest PID for the selection-disable probe.'
    }

    $probe = $null
    $probeIdentitySource = 'guest-process-query'
    $startupObservation = $null
    if ($null -eq $diagnostics) {
        $probe = Get-GuestSampleProcessIdentity -ExpectedProcessId ([int]$started.Id)
        $startupObservation = [pscustomobject]@{
            status = 'offline-contract-only'
            processExited = $false
            exitCode = $null
            mainWindowHandle = 0L
            mainWindowTitle = ''
        }
    }
    else {
        $deadline = [DateTime]::UtcNow.AddMilliseconds($StartupObservationMilliseconds)
        do {
            $startupObservation = Get-SelectionDisableLaunchObservation -Process $started
            if ($startupObservation.processExited) { break }
            if ($null -eq $probe) {
                try { $probe = Get-GuestSampleProcessIdentity -ExpectedProcessId ([int]$started.Id) }
                catch {
                    $started.Refresh()
                    if ($started.HasExited) {
                        $startupObservation = Get-SelectionDisableLaunchObservation -Process $started
                        break
                    }
                    # The Process handle was bound to the launched child with
                    # the exact captured executable path. It remains bound to
                    # that process object even if a separate PID lookup races
                    # startup; do not turn a transient Get-Process miss into a
                    # second launch or an unscoped retry.
                    $probe = Get-SelectionDisableLaunchIdentityFromHandle -Process $started -SourceIdentity $sourceBefore -OriginalProcessId $OriginalProcessId
                    $probeIdentitySource = 'retained-launch-process-handle'
                }
            }
            if ($null -ne $probe -and $startupObservation.status -ceq 'window-candidate-observed') { break }
            if ([DateTime]::UtcNow -ge $deadline) { break }
            Start-Sleep -Milliseconds 100
        } while ($true)
        $started.Refresh()
        if ($started.HasExited -and -not $startupObservation.processExited) {
            $startupObservation = Get-SelectionDisableLaunchObservation -Process $started
        }
    }
    if ($null -eq $probe) {
        # The retained handle was bound to the launched child with the exact
        # captured source path. It preserves this launch identity even when
        # PID-based Get-Process/path lookup loses a short-lived startup race.
        try { $probe = Get-SelectionDisableLaunchIdentityFromHandle -Process $started -SourceIdentity $sourceBefore -OriginalProcessId $OriginalProcessId }
        catch {
            throw "Could not capture the launched process identity from its retained process handle; run-scoped guest stdout/stderr paths are '$($diagnostics.guestStdoutRelativePath)' and '$($diagnostics.guestStderrRelativePath)': $($_.Exception.Message)"
        }
        $probeIdentitySource = 'retained-launch-process-handle'
    }
    if (-not [string]::Equals($probe.executablePath,$sourceBefore.executablePath,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'The newly launched probe PID does not use the exact executable path captured from the original sample process.'
    }

    # Return the launcher PID even if the original is concurrently replaced;
    # the host can then recover and normally close only this exact new app.
    # The host rejects a changed source identity and marks the batch unsafe.
    $sourceAfter = $null
    try { $sourceAfter = Get-GuestSampleProcessIdentity -ExpectedProcessId $OriginalProcessId } catch { }
    $sourceRetained = $null -ne $sourceAfter -and
        [string]::Equals($sourceAfter.processName,$sourceBefore.processName,[StringComparison]::Ordinal) -and
        [string]::Equals($sourceAfter.executablePath,$sourceBefore.executablePath,[StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($sourceAfter.startTimeUtc,$sourceBefore.startTimeUtc,[StringComparison]::Ordinal)
    return [pscustomobject]@{
        mode='launch-selection-disable-probe';launchMethod='guest-Win32_Process-Create'
        sourceBefore=$sourceBefore;sourceAfter=$sourceAfter;sourceRetained=[bool]$sourceRetained
        processId=[int]$probe.processId;processName=$probe.processName;executablePath=$probe.executablePath;startTimeUtc=$probe.startTimeUtc
        processIdentitySource=$probeIdentitySource
        arguments=@('--renderer-selection-disable-probe')
        startupObservation=$startupObservation
        diagnostics=if ($null -ne $diagnostics) { [pscustomobject]@{
            guestRelativeDirectory=$diagnostics.guestRelativeDirectory
            guestStdoutRelativePath=$diagnostics.guestStdoutRelativePath
            guestStderrRelativePath=$diagnostics.guestStderrRelativePath
        } } else { $null }
    }
}

if ($Mode -eq 'launch-selection-disable-probe') {
    if ([string]::IsNullOrWhiteSpace($RunId)) { throw 'The selection-disable child launch requires the active host run ID.' }
    $result = Start-SelectionDisableProbeProcess -OriginalProcessId $ProcessId -RunId $RunId
    ConvertTo-AsciiJson -Value $result
    return
}

if ($Mode -eq 'process-state') {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.HasExited) {
        $result = @{ mode=$Mode; processId=$ProcessId; exists=$false; processName=$null; executablePath=$null; startTimeUtc=$null }
    }
    else {
        try {
            $result = @{
                mode=$Mode
                processId=[int]$process.Id
                exists=$true
                processName=[string]$process.ProcessName
                executablePath=[string]$process.Path
                startTimeUtc=$process.StartTime.ToUniversalTime().ToString('o')
            }
        }
        catch {
            throw "Could not capture stable process identity for PID ${ProcessId}: $($_.Exception.Message)"
        }
    }
    ConvertTo-AsciiJson -Value $result
    return
}

$window = Get-AppWindow
$renderer = Get-Renderer $window
$result = $null
switch ($Mode) {
    'payload-hashes' {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        if ($process.ProcessName -cne 'MarkdownRenderer.Sample' -or [string]::IsNullOrWhiteSpace([string]$process.Path)) {
            throw 'Cannot resolve the approved MarkdownRenderer.Sample process image path for payload identity.'
        }
        $directory = Split-Path -Parent ([string]$process.Path)
        $requiredNames = @('MarkdownRenderer.Sample.exe','MarkdownRenderer.Sample.dll','MarkdownRenderer.dll')
        $optionalNames = @('MarkdownRenderer.Sample.deps.json')
        $files = [System.Collections.Generic.List[object]]::new()
        foreach ($name in $requiredNames) {
            $path = Join-Path $directory $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required deployed payload '$name' is missing beside the approved guest executable." }
            $item = Get-Item -LiteralPath $path
            $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
            $files.Add(@{name=$name;path=$path;length=$item.Length;sha256=$hash.Hash.ToLowerInvariant()})
        }
        foreach ($name in $optionalNames) {
            $path = Join-Path $directory $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $item = Get-Item -LiteralPath $path
                $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
                $files.Add(@{name=$name;path=$path;length=$item.Length;sha256=$hash.Hash.ToLowerInvariant()})
            }
        }
        $result = @{mode=$Mode;processId=$ProcessId;executablePath=[string]$process.Path;files=@($files.ToArray())}
    }
    'metadata' {
        $rect=[MarkdownCorrectnessNative]::GetBounds([IntPtr]$WindowHandle)
        $dpi=[MarkdownCorrectnessNative]::GetDpi([IntPtr]$WindowHandle)
        $clientOrigin=[MarkdownCorrectnessNative]::GetClientOriginOnScreen([IntPtr]$WindowHandle)
        $result=@{mode=$Mode;processId=$ProcessId;hwnd=$WindowHandle;windowName=$window.Current.Name;dpi=$dpi;dpiScale=($dpi/96.0);systemHighContrast=[MarkdownCorrectnessNative]::SystemHighContrast();window=@{left=$rect.Left;top=$rect.Top;right=$rect.Right;bottom=$rect.Bottom};clientOriginScreenPx=@{x=$clientOrigin.X;y=$clientOrigin.Y};rendererName=$renderer.Current.Name}
    }
    'range' {
        $pattern=Get-Pattern $renderer ([System.Windows.Automation.TextPattern]::Pattern)
        if ($null -eq $pattern) { throw 'MarkdownRenderer does not expose TextPattern.' }
        $range=$pattern.DocumentRange.FindText($Needle,$false,$false)
        if ($null -eq $range) { throw "TextPattern.FindText did not find '$Needle'." }
        $result=@{mode=$Mode;needle=$Needle;text=$range.GetText(-1);rectangles=@(Get-Rectangles $range)}
    }
    'range-between' {
        $pattern=Get-Pattern $renderer ([System.Windows.Automation.TextPattern]::Pattern)
        if ($null -eq $pattern) { throw 'MarkdownRenderer does not expose TextPattern for a range between anchors.' }
        if ([string]::IsNullOrEmpty($StartNeedle) -or [string]::IsNullOrEmpty($EndNeedle)) { throw 'Both range anchors are required.' }
        $startRange=$pattern.DocumentRange.FindText($StartNeedle,$false,$false)
        $endRange=$pattern.DocumentRange.FindText($EndNeedle,$false,$false)
        if ($null -eq $startRange -or $null -eq $endRange) { throw "TextPattern could not resolve both range anchors '$StartNeedle' and '$EndNeedle'." }
        $endpointType=$startRange.GetType().GetMethod('CompareEndpoints').GetParameters()[0].ParameterType
        if (-not $endpointType.IsEnum) { throw "UIA CompareEndpoints parameter is not an enum: $($endpointType.FullName)." }
        $startEndpoint=[System.Enum]::Parse($endpointType,'Start')
        $endEndpoint=[System.Enum]::Parse($endpointType,'End')
        $comparison=$startRange.CompareEndpoints($startEndpoint,$endRange,$startEndpoint)
        if ($comparison -le 0) {
            $range=$pattern.DocumentRange.Clone()
            $range.MoveEndpointByRange($startEndpoint,$startRange,$startEndpoint)
            $range.MoveEndpointByRange($endEndpoint,$endRange,$endEndpoint)
        } else {
            $range=$pattern.DocumentRange.Clone()
            $range.MoveEndpointByRange($startEndpoint,$endRange,$startEndpoint)
            $range.MoveEndpointByRange($endEndpoint,$startRange,$endEndpoint)
        }
        $expectedText=$range.GetText(-1)
        if ($SelectRange) { $range.Select() }
        $actualSelection=@($pattern.GetSelection() | ForEach-Object { $_.GetText(-1) }) -join ''
        $result=@{mode=$Mode;startNeedle=$StartNeedle;endNeedle=$EndNeedle;text=$expectedText;rectangles=@(Get-Rectangles $range);selectionRequested=[bool]$SelectRange;actualSelection=$actualSelection}
    }
    'table-cell' {
        if ([string]::IsNullOrEmpty($Needle) -or [string]::IsNullOrEmpty($StartNeedle)) { throw 'The image alternative text and adjacent-cell text anchors are required.' }
        $walker=[System.Windows.Automation.TreeWalker]::RawViewWalker
        $pending=[System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new()
        $pending.Push($window)
        $image=$null
        while($pending.Count -gt 0){
            $candidate=$pending.Pop()
            try {
                if($candidate.Current.ClassName -eq 'MarkdownImage' -and $candidate.Current.Name -ceq $Needle){$image=$candidate;break}
                $child=$walker.GetFirstChild($candidate)
                while($null -ne $child){$pending.Push($child);$child=$walker.GetNextSibling($child)}
            } catch { }
        }
        if($null -eq $image){throw "The semantic image peer '$Needle' was not found in the raw UIA tree."}
        $cell=$null
        $ancestor=$walker.GetParent($image)
        $depth=0
        while($null -ne $ancestor -and $depth -lt 32){
            if($ancestor.Current.ClassName -eq 'MarkdownTableCell'){$cell=$ancestor;break}
            $ancestor=$walker.GetParent($ancestor);$depth++
        }
        if($null -eq $cell){throw "Image '$Needle' has no MarkdownTableCell ancestor in the UIA tree."}
        $gridItem=Get-Pattern $cell ([System.Windows.Automation.GridItemPattern]::Pattern)
        if($null -eq $gridItem){throw 'The containing cell did not expose GridItemPattern.'}
        $row=[int]$gridItem.Current.Row;$column=[int]$gridItem.Current.Column
        $gridElement=[System.Windows.Automation.AutomationElement]$gridItem.Current.ContainingGrid
        if($null -eq $gridElement){throw 'The image cell did not expose its containing grid.'}
        $grid=Get-Pattern $gridElement ([System.Windows.Automation.GridPattern]::Pattern)
        if($null -eq $grid){throw 'The containing Markdown table did not expose GridPattern.'}
        if([int]$grid.Current.ColumnCount -le ($column+1)){throw 'The image cell has no adjacent cell to validate.'}
        $neighbor=$grid.GetItem($row,$column+1)
        if($null -eq $neighbor){throw 'GridPattern could not resolve the cell adjacent to the image.'}
        $neighborItem=Get-Pattern $neighbor ([System.Windows.Automation.GridItemPattern]::Pattern)
        if($null -eq $neighborItem -or [int]$neighborItem.Current.Row -ne $row -or [int]$neighborItem.Current.Column -ne ($column+1)){
            throw 'The adjacent GridItemPattern does not identify the expected same-row next column.'
        }
        $textPattern=Get-Pattern $renderer ([System.Windows.Automation.TextPattern]::Pattern)
        if($null -eq $textPattern){throw 'MarkdownRenderer does not expose TextPattern for adjacent-cell content.'}
        $neighborText=$textPattern.DocumentRange.FindText($StartNeedle,$false,$false)
        if($null -eq $neighborText){throw "The adjacent-cell text anchor '$StartNeedle' is absent from TextPattern."}
        $imageRect=$image.Current.BoundingRectangle;$cellRect=$cell.Current.BoundingRectangle
        $neighborRect=$neighbor.Current.BoundingRectangle
        if($imageRect.Width -le 0 -or $imageRect.Height -le 0 -or $cellRect.Width -le 0 -or $cellRect.Height -le 0 -or $neighborRect.Width -le 0 -or $neighborRect.Height -le 0){
            throw 'The image or its grid cells have empty UIA geometry.'
        }
        $result=@{mode=$Mode;imageName=$image.Current.Name;row=$row;column=$column;imageRect=@{x=$imageRect.X;y=$imageRect.Y;width=$imageRect.Width;height=$imageRect.Height};cellRect=@{x=$cellRect.X;y=$cellRect.Y;width=$cellRect.Width;height=$cellRect.Height};neighborCell=@{name=$neighbor.Current.Name;row=[int]$neighborItem.Current.Row;column=[int]$neighborItem.Current.Column;x=$neighborRect.X;y=$neighborRect.Y;width=$neighborRect.Width;height=$neighborRect.Height};neighborText=$StartNeedle;neighborTextRectangles=@(Get-Rectangles $neighborText)}
    }
    'scroll-range' {
        $pattern=Get-Pattern $renderer ([System.Windows.Automation.TextPattern]::Pattern)
        if ($null -eq $pattern) { throw 'MarkdownRenderer does not expose TextPattern for semantic scroll.' }
        $range=$pattern.DocumentRange.FindText($Needle,$false,$false)
        if ($null -eq $range) { throw "TextPattern.FindText did not expose semantic scroll text '$Needle'." }
        $range.ScrollIntoView($true)
        $rectangles=@(Get-Rectangles $range)
        if ($rectangles.Count -eq 0) { throw "Semantic scroll text '$Needle' has no UIA geometry after ScrollIntoView." }
        $result=@{mode=$Mode;needle=$Needle;text=$range.GetText(-1);rectangles=$rectangles}
    }
    'selection' {
        $pattern=Get-Pattern $renderer ([System.Windows.Automation.TextPattern]::Pattern)
        if ($null -eq $pattern) { throw 'MarkdownRenderer does not expose TextPattern.' }
        $ranges=@($pattern.GetSelection())
        $texts=@($ranges | ForEach-Object { $_.GetText(-1) })
        $result=@{mode=$Mode;supportedSelection=[string]$pattern.SupportedTextSelection;rangeCount=$ranges.Count;text=($texts -join '');ranges=@($ranges | ForEach-Object { @{text=$_.GetText(-1);rectangles=@(Get-Rectangles $_)} })}
    }
    'raw-name' {
        # Process-wide lookup includes PopupHost/menu HWNDs owned by this exact
        # app PID; ordinary renderer semantics remain separately scoped.
        $element=Find-UniqueProcessElement $ProcessId $AutomationId
        $name=$null
        if($null -ne $element){$name=[string]$element.Current.Name}
        $result=@{mode=$Mode;automationId=$AutomationId;name=$name}
    }
    'image-state' {
        if ([string]::IsNullOrWhiteSpace($Needle)) { throw 'The expected image alternative-text fragment is required.' }
        $walker=[System.Windows.Automation.TreeWalker]::RawViewWalker
        $pending=[System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new(); $pending.Push($window)
        $images=[System.Collections.Generic.List[object]]::new()
        while($pending.Count -gt 0){
            $element=$pending.Pop()
            try {
                $current=$element.Current
                if($current.ClassName -eq 'MarkdownImage' -and $current.Name.IndexOf($Needle,[System.StringComparison]::OrdinalIgnoreCase) -ge 0){
                    $r=$current.BoundingRectangle
                    $images.Add(@{name=[string]$current.Name;helpText=[string]$current.HelpText;automationId=[string]$current.AutomationId;className=[string]$current.ClassName;controlType=[string]$current.ControlType.ProgrammaticName;offscreen=[bool]$current.IsOffscreen;x=$r.X;y=$r.Y;width=$r.Width;height=$r.Height})
                }
                $child=$walker.GetFirstChild($element); while($null -ne $child){$pending.Push($child);$child=$walker.GetNextSibling($child)}
            } catch { }
        }
        if($images.Count -gt 1){throw "Expected at most one MarkdownImage peer containing '$Needle'; found $($images.Count)."}
        $result=@{mode=$Mode;needle=$Needle;images=@($images.ToArray())}
    }
    'handles' {
        $walker=[System.Windows.Automation.TreeWalker]::RawViewWalker
        $pending=[System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new(); $pending.Push($window)
        $handles=[System.Collections.Generic.List[object]]::new()
        while($pending.Count -gt 0){
            $element=$pending.Pop()
            try {
                $id=$element.Current.AutomationId
                if($id -in @('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle')){
                    $r=$element.Current.BoundingRectangle
                    $handles.Add(@{automationId=$id;name=$element.Current.Name;x=$r.X;y=$r.Y;width=$r.Width;height=$r.Height;offscreen=$element.Current.IsOffscreen})
                }
                $child=$walker.GetFirstChild($element); while($null -ne $child){$pending.Push($child);$child=$walker.GetNextSibling($child)}
            } catch { }
        }
        $dpi=[MarkdownCorrectnessNative]::GetDpi([IntPtr]$WindowHandle)
        $result=@{mode=$Mode;dpi=$dpi;dpiScale=($dpi/96.0);handles=@($handles.ToArray())}
    }
    'focus-state' {
        if([string]::IsNullOrWhiteSpace($AutomationId)){throw 'AutomationId is required for a keyboard focus-state probe.'}
        $element=Find-UniqueProcessElement $ProcessId $AutomationId
        $current=$element.Current
        $result=@{mode=$Mode;automationId=$AutomationId;name=[string]$current.Name;hasKeyboardFocus=[bool]$current.HasKeyboardFocus;isKeyboardFocusable=[bool]$current.IsKeyboardFocusable;isEnabled=[bool]$current.IsEnabled;isOffscreen=[bool]$current.IsOffscreen}
    }
    'scroll' {
        $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
        $all=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
        $scrollers=[System.Collections.Generic.List[object]]::new()
        for($i=0;$i -lt $all.Count;$i++){
            $element=$all.Item($i); $pattern=Get-Pattern $element ([System.Windows.Automation.ScrollPattern]::Pattern)
            if($null -ne $pattern -and $pattern.Current.VerticallyScrollable){
                $scrollers.Add(@{automationId=$element.Current.AutomationId;name=$element.Current.Name;className=$element.Current.ClassName;verticalPercent=$pattern.Current.VerticalScrollPercent;horizontalPercent=$pattern.Current.HorizontalScrollPercent})
            }
        }
        $result=@{mode=$Mode;scrollers=@($scrollers.ToArray())}
    }
    'clipboard' {
        Add-Type -AssemblyName PresentationCore
        $plain=[System.Windows.Clipboard]::GetText()
        $hasHtml=[System.Windows.Clipboard]::ContainsData('HTML Format')
        $html=if($hasHtml){[string][System.Windows.Clipboard]::GetData('HTML Format')}else{''}
        $selection=Get-Pattern $renderer ([System.Windows.Automation.TextPattern]::Pattern)
        $selectedText=(@($selection.GetSelection() | ForEach-Object {$_.GetText(-1)}) -join '')
        $result=@{mode=$Mode;plain=$plain;hasHtml=$hasHtml;html=$html;selectedText=$selectedText}
    }
    'resize' {
        if($Width -lt 320 -or $Height -lt 320){throw 'Refusing a window size below the safe test minimum.'}
        $rect=[MarkdownCorrectnessNative]::Resize([IntPtr]$WindowHandle,$Left,$Top,$Width,$Height)
        $result=@{mode=$Mode;window=@{left=$rect.Left;top=$rect.Top;right=$rect.Right;bottom=$rect.Bottom;width=($rect.Right-$rect.Left);height=($rect.Bottom-$rect.Top)}}
    }
    'source-file' {
        if ([string]::IsNullOrWhiteSpace($SourcePath) -or [string]::IsNullOrWhiteSpace($RequiredText) -or [string]::IsNullOrWhiteSpace($RunId)) { throw 'SourcePath, RequiredText, and RunId are required for staged Markdown input.' }
        if ($RunId -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{32}$') { throw 'The staged Markdown RunId is malformed.' }
        $runRoot=[System.IO.Path]::GetFullPath((Join-Path 'C:\WinApp\work' ("correctness\{0}" -f $RunId))).TrimEnd([char]92) + [char]92
        $fullPath=[System.IO.Path]::GetFullPath($SourcePath)
        if (-not $fullPath.StartsWith($runRoot,[System.StringComparison]::OrdinalIgnoreCase)) { throw 'Staged source path is outside this run-specific Sandbox work directory.' }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw 'The staged Markdown input file is missing in the guest.' }
        $bytes=[System.IO.File]::ReadAllBytes($fullPath)
        $markdown=[System.Text.UTF8Encoding]::new($false,$true).GetString($bytes)
        if ($markdown.IndexOf($RequiredText,[System.StringComparison]::Ordinal) -lt 0) { throw 'The required fixture marker is absent from staged Markdown input.' }
        $editor=Find-RawElement $window 'MarkdownEditor'
        if ($null -eq $editor) { throw 'MarkdownEditor is not present in the verified app UIA tree.' }
        $valuePattern=Get-Pattern $editor ([System.Windows.Automation.ValuePattern]::Pattern)
        if ($null -eq $valuePattern -or $valuePattern.Current.IsReadOnly) { throw 'MarkdownEditor has no writable ValuePattern for staged Markdown input.' }
        $valuePattern.SetValue($markdown)
        $updated=$valuePattern.Current.Value
        $expectedText=Normalize-MarkdownEditorLineEndings -Text $markdown
        $actualText=Normalize-MarkdownEditorLineEndings -Text $updated
        if ($actualText -cne $expectedText) {
            $firstDifference=0
            $sharedLength=[Math]::Min($expectedText.Length,$actualText.Length)
            while($firstDifference -lt $sharedLength -and $expectedText[$firstDifference] -ceq $actualText[$firstDifference]){$firstDifference++}
            throw "MarkdownEditor did not commit the exact staged Markdown source apart from native line-ending normalization (expectedLength=$($expectedText.Length), actualLength=$($actualText.Length), firstDifferentUtf16Offset=$firstDifference)."
        }
        $sha=[System.Security.Cryptography.SHA256]::Create()
        try { $hash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
        $result=@{mode=$Mode;processId=$ProcessId;sourcePath=$fullPath;length=$bytes.Length;sha256=$hash;requiredText=$RequiredText;exactValuePatternRoundTrip=($updated -ceq $markdown);exactApartFromNativeLineEndings=$true;valuePatternComparison='ordinal UTF-16; CRLF/CR/LF canonicalized only'}
    }
    'source-file-preflight' {
        if ([string]::IsNullOrWhiteSpace($SourcePath) -or [string]::IsNullOrWhiteSpace($RunId)) { throw 'SourcePath and RunId are required for staged Markdown preflight.' }
        if ($RunId -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{32}$') { throw 'The staged Markdown RunId is malformed.' }
        $runDirectory=[System.IO.Path]::GetFullPath((Join-Path 'C:\WinApp\work' ("correctness\{0}" -f $RunId))).TrimEnd([char]92)
        $fullPath=[System.IO.Path]::GetFullPath($SourcePath)
        $parent=[System.IO.Path]::GetDirectoryName($fullPath).TrimEnd([char]92)
        $leaf=[System.IO.Path]::GetFileName($fullPath)
        if (-not [string]::Equals($parent,$runDirectory,[System.StringComparison]::OrdinalIgnoreCase) -or $leaf -notmatch '^source-[0-9a-f]{32}\.md$') {
            throw 'The staged Markdown destination must be a fresh source file directly inside this run-specific Sandbox work directory.'
        }
        if (Test-Path -LiteralPath $fullPath) { throw 'Refusing to overwrite an existing Sandbox Markdown destination.' }
        $result=@{mode=$Mode;processId=$ProcessId;sourcePath=$fullPath;destinationExisted=$false;runId=$RunId}
    }
    'near-origin-sequence' {
        if ([string]::IsNullOrWhiteSpace($RequiredText) -or $X -le 0 -or $Y -le 0 -or $Width -le 0 -or $Height -le 0) { throw 'Near-origin sequence requires the expected link URL and positive origin/target coordinates.' }
        if ($PointerId -ne 0) { throw 'A one-contact synthetic pointer device uses local contact index 0.' }
        $linkStatus=Find-RawElement $window 'LinkActivationStatus'
        if ($null -eq $linkStatus -or [string]::IsNullOrWhiteSpace([string]$linkStatus.Current.Name)) { throw 'LinkActivationStatus is unavailable; the sequence cannot distinguish link activation.' }
        $statusBefore=[string]$linkStatus.Current.Name
        if ($statusBefore.IndexOf($RequiredText,[System.StringComparison]::Ordinal) -ge 0) { throw 'The unique link URL already appears in LinkActivationStatus before the sequence.' }
        $originExpectedPoint=Get-ExpectedScreenTouchPoint -ScreenX $X -ScreenY $Y
        $tapExpectedPoint=Get-ExpectedScreenTouchPoint -ScreenX $Width -ScreenY $Height
        $observerBeforeOrigin=Get-TouchContactObservation
        $clock=[System.Diagnostics.Stopwatch]::StartNew()
        $touchDevice=$null
        try {
        $touchDevice=[MarkdownTouchCancellation]::new()
        $touchDevice.SendFrame($PointerId,[int][Math]::Round($X),[int][Math]::Round($Y),$true,$true)
        $longPressDeadline=[System.Diagnostics.Stopwatch]::GetTimestamp()+[long]([System.Diagnostics.Stopwatch]::Frequency*0.65)
        while([System.Diagnostics.Stopwatch]::GetTimestamp() -lt $longPressDeadline){
            Start-Sleep -Milliseconds 25
            $touchDevice.SendFrame($PointerId,[int][Math]::Round($X),[int][Math]::Round($Y),$true,$false)
        }
        $touchDevice.SendFrame($PointerId,[int][Math]::Round($X),[int][Math]::Round($Y),$false,$false)
        $releaseTicks=[System.Diagnostics.Stopwatch]::GetTimestamp()
        $releaseUtc=[DateTime]::UtcNow
        $observerAfterOrigin=Get-TouchContactObservation
        $originDelivery=Assert-SyntheticTouchDelivery -Before $observerBeforeOrigin -After $observerAfterOrigin -ExpectedPoint $originExpectedPoint -ExpectedState 'Released'

        $selectionPattern=Get-Pattern $renderer ([System.Windows.Automation.TextPattern]::Pattern)
        if ($null -eq $selectionPattern) { throw 'MarkdownRenderer does not expose TextPattern after the long press.' }
        $selected=[string](@($selectionPattern.GetSelection() | ForEach-Object { $_.GetText(-1) }) -join '')
        if ($selected -cne 'x') { throw "The original plain-text long press selected '$selected', not exactly 'x'." }
        $processElements=@(Get-ProcessElements $ProcessId)
        $menuIds=@{}
        $handleIds=@{}
        for($i=0;$i -lt $processElements.Count;$i++){
            $item=$processElements[$i].element
            $id=[string]$item.Current.AutomationId
            if($id -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')){$menuIds[$id]=$true}
            if($id -in @('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle')){$handleIds[$id]=$true}
        }
        if($menuIds.Count -ne 3 -or $handleIds.Count -ne 2){throw 'The long-press release did not expose all three selection-menu commands and both selection handles.'}
        $statusAfterRelease=[string]$linkStatus.Current.Name
        if($statusAfterRelease -cne $statusBefore){throw 'The plain-text long-press release unexpectedly activated the adjacent link.'}

        [MarkdownTouchCancellation]::SendEscape([IntPtr]$WindowHandle)
        $escapeDeadline=[System.Diagnostics.Stopwatch]::GetTimestamp()+[long]([System.Diagnostics.Stopwatch]::Frequency*0.25)
        do {
            $stillOpen=0
            $processElements=@(Get-ProcessElements $ProcessId)
            for($i=0;$i -lt $processElements.Count;$i++){
                if([string]$processElements[$i].element.Current.AutomationId -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')){$stillOpen++}
            }
            $selectedAfterEscape=[string](@($selectionPattern.GetSelection() | ForEach-Object { $_.GetText(-1) }) -join '')
            if($stillOpen -eq 0 -and $selectedAfterEscape -ceq 'x'){break}
            Start-Sleep -Milliseconds 15
        } while([System.Diagnostics.Stopwatch]::GetTimestamp() -lt $escapeDeadline)
        if($stillOpen -ne 0 -or $selectedAfterEscape -cne 'x'){throw 'Escape did not dismiss only the selection menu while preserving the committed text selection.'}
        $statusAfterEscape=[string]$linkStatus.Current.Name
        if($statusAfterEscape -cne $statusBefore){throw 'Escape changed the link activation state before the independent tap.'}

        $observerBeforeTap=Get-TouchContactObservation
        $elapsedBeforeTap=([System.Diagnostics.Stopwatch]::GetTimestamp()-$releaseTicks)*1000.0/[System.Diagnostics.Stopwatch]::Frequency
        $dpi=[MarkdownCorrectnessNative]::GetDpi([IntPtr]$WindowHandle)
        $distancePixels=[Math]::Sqrt([Math]::Pow(($X-$Width),2)+[Math]::Pow(($Y-$Height),2))
        $distanceDips=$distancePixels/([double]$dpi/96.0)
        if($distanceDips -ge 48.0){$result=@{mode=$Mode;status='UNVERIFIED';reason=("New-contact tap is {0:F2} DIPs from the origin, outside the 48-DIP envelope." -f $distanceDips);distanceDips=$distanceDips;releaseToTapMilliseconds=$elapsedBeforeTap};break}
        if($elapsedBeforeTap -ge 1500.0){$result=@{mode=$Mode;status='UNVERIFIED';reason=("Same-process setup took {0:F1} ms before the new-contact tap, outside the 1.5-second envelope." -f $elapsedBeforeTap);distanceDips=$distanceDips;releaseToTapMilliseconds=$elapsedBeforeTap};break}

        # The first contact ended with UP, so the one-contact device reuses its
        # canonical local index. The observer's contact serial distinguishes
        # this intentional new gesture from the prior routed contact.
        $tapPointerId=[uint32]0
        $touchDevice.SendFrame($tapPointerId,[int][Math]::Round($Width),[int][Math]::Round($Height),$true,$true)
        $tapDownTicks=[System.Diagnostics.Stopwatch]::GetTimestamp()
        Start-Sleep -Milliseconds 10
        $touchDevice.SendFrame($tapPointerId,[int][Math]::Round($Width),[int][Math]::Round($Height),$false,$false)
        $tapCompleteTicks=[System.Diagnostics.Stopwatch]::GetTimestamp()
        $elapsedMilliseconds=($tapCompleteTicks-$releaseTicks)*1000.0/[System.Diagnostics.Stopwatch]::Frequency
        if($elapsedMilliseconds -ge 1500.0){throw ("UNVERIFIED: the independent tap completed {0:F1} ms after long-press release, outside the 1.5-second envelope." -f $elapsedMilliseconds)}
        $observerAfterTap=Get-TouchContactObservation
        $tapDelivery=Assert-SyntheticTouchDelivery -Before $observerBeforeTap -After $observerAfterTap -ExpectedPoint $tapExpectedPoint -ExpectedState 'Released'

        $activationDeadline=[DateTime]::UtcNow.AddSeconds(3)
        $statusAfterTap=$statusAfterEscape
        do {
            $linkStatus=Find-RawElement $window 'LinkActivationStatus'
            $statusAfterTap=if($null -ne $linkStatus){[string]$linkStatus.Current.Name}else{''}
            if($statusAfterTap.IndexOf($RequiredText,[System.StringComparison]::Ordinal) -ge 0){break}
            Start-Sleep -Milliseconds 30
        } while([DateTime]::UtcNow -lt $activationDeadline)
        $result=@{mode=$Mode;status='PASS';origin=@{x=$X;y=$Y};tap=@{x=$Width;y=$Height};injectedOriginPointerId=$PointerId;injectedTapPointerId=$tapPointerId;originDelivery=$originDelivery;tapDelivery=$tapDelivery;distancePixels=$distancePixels;distanceDips=$distanceDips;longPressReleasedUtc=$releaseUtc.ToString('o');releaseToTapMilliseconds=$elapsedMilliseconds;tapDownOffsetMilliseconds=(($tapDownTicks-$releaseTicks)*1000.0/[System.Diagnostics.Stopwatch]::Frequency);statusBefore=$statusBefore;statusAfterPlainRelease=$statusAfterRelease;statusAfterEscape=$statusAfterEscape;statusAfterIndependentTap=$statusAfterTap;selectedAfterRelease=$selected;selectedAfterEscape=$selectedAfterEscape;menuCommandsBeforeEscape=$menuIds.Count;menuCommandsAfterEscape=$stillOpen;handlesBeforeEscape=$handleIds.Count;independentTapActivated=($statusAfterTap.IndexOf($RequiredText,[System.StringComparison]::Ordinal) -ge 0)}
        if(-not [bool]$result.independentTapActivated){throw ("The independent nearby link tap was not activated within the measured envelope; distanceDip={0:F2}, elapsedMs={1:F1}, before='{2}', after='{3}'." -f $distanceDips,$elapsedMilliseconds,$statusBefore,$statusAfterTap)}
        } finally {
            if($null -ne $touchDevice){$touchDevice.Dispose()}
        }
    }
    'cancel-contact' {
        if($X -le 0 -or $Y -le 0){throw 'Contact coordinates must be positive screen points.'}
        if($PointerId -ne 0){throw 'A one-contact synthetic pointer device uses local contact index 0.'}
        $observerBefore=Get-TouchContactObservation
        $expectedPoint=Get-ExpectedScreenTouchPoint -ScreenX $X -ScreenY $Y
        $touchDevice=$null
        try {
        $touchDevice=[MarkdownTouchCancellation]::new()
        $touchDevice.InjectCanceledContact($PointerId,[int][Math]::Round($X),[int][Math]::Round($Y))
        $deadline=[DateTime]::UtcNow.AddSeconds(2)
        $observerAfter=$observerBefore
        do{
            Start-Sleep -Milliseconds 25
            $observerAfter=Get-TouchContactObservation
            try{$delivery=Assert-SyntheticTouchDelivery -Before $observerBefore -After $observerAfter -ExpectedPoint $expectedPoint -ExpectedState 'Canceled';break}catch{if([DateTime]::UtcNow -ge $deadline){throw}}
        }while([DateTime]::UtcNow -lt $deadline)
        if($null -eq $delivery){throw 'No uniquely correlated routed cancellation event was observed.'}
        $result=@{mode=$Mode;injectedPointerId=$PointerId;routedContact=@{pointerId=$delivery.pointerId;contactSerial=$delivery.contactSerial;rendererGeneration=$delivery.rendererGeneration};x=$X;y=$Y;sent='synthetic PT_TOUCH DOWN, stationary UPDATE, then POINTER_FLAG_CANCELED|POINTER_FLAG_UP';windowHandle=$WindowHandle;cancelDelivered=$true;delivery=$delivery;observerBefore=$observerBefore;observerAfter=$observerAfter}
        } finally {
            if($null -ne $touchDevice){$touchDevice.Dispose()}
        }
    }
}
ConvertTo-AsciiJson -Value $result
'@
    [System.IO.File]::WriteAllText($script:GuestProbePath, $source, [System.Text.UTF8Encoding]::new($false))
    $relativePath = "correctness\$($script:RunId)\GuestUiProbe.ps1"
    $null = Invoke-NativeChecked @('target', 'push', 'sandbox', $script:GuestProbePath, $relativePath)
    $script:GuestProbePath = Join-Path 'C:\WinApp\work' $relativePath
}

function Get-SourceEvidence {
    $head = (& git -C $script:RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw "Could not record source revision: $head" }
    $statusText = & git -C $script:RepoRoot status --porcelain=v1 --untracked-files=all 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Could not record git status.' }
    $diffPath = Join-Path $script:RunDirectory 'source-tracked.diff'
    $diffArgument = "--output=$diffPath"
    $null = & git -C $script:RepoRoot diff --binary --no-ext-diff $diffArgument HEAD -- 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Could not record working-tree diff.' }
    if (-not (Test-Path -LiteralPath $diffPath -PathType Leaf)) { throw 'Git did not write the tracked source diff.' }
    $diffFile = Get-FileHash -LiteralPath $diffPath -Algorithm SHA256
    $untracked = @(& git -C $script:RepoRoot ls-files --others --exclude-standard 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate untracked source files.' }
    $untrackedHashes = foreach ($relative in $untracked) {
        $path = Join-Path $script:RepoRoot $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $fileHash = Get-FileHash -LiteralPath $path -Algorithm SHA256
            [pscustomobject]@{ path = $relative; sha256 = $fileHash.Hash.ToLowerInvariant() }
        }
    }
    return [pscustomobject]@{ head = $head; status = @($statusText); trackedDiffPath = $diffPath; trackedDiffLength = (Get-Item -LiteralPath $diffPath).Length; trackedDiffSha256 = $diffFile.Hash.ToLowerInvariant(); untrackedFiles = @($untrackedHashes) }
}

function Get-BuildPayloadIdentity {
    param([Parameter(Mandatory)][string]$Directory)
    $fullDirectory = [System.IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath $fullDirectory -PathType Container)) { throw "Build payload directory is missing: $fullDirectory" }
    $requiredNames = @('MarkdownRenderer.Sample.exe','MarkdownRenderer.Sample.dll','MarkdownRenderer.dll')
    $optionalNames = @('MarkdownRenderer.Sample.deps.json')
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($name in $requiredNames) {
        $path = Join-Path $fullDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required build payload '$name' is missing: $path" }
        $item = Get-Item -LiteralPath $path
        $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
        $files.Add([pscustomobject]@{name=$name;path=$path;length=$item.Length;sha256=$hash.Hash.ToLowerInvariant();required=$true})
    }
    foreach ($name in $optionalNames) {
        $path = Join-Path $fullDirectory $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path
            $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
            $files.Add([pscustomobject]@{name=$name;path=$path;length=$item.Length;sha256=$hash.Hash.ToLowerInvariant();required=$false})
        }
    }
    return @($files.ToArray())
}

function Assert-GuestPayloadIdentity {
    param([Parameter(Mandatory)][object[]]$BuildPayloads,[Parameter(Mandatory)][object]$GuestPayloadReceipt)
    if ([string]$GuestPayloadReceipt.mode -cne 'payload-hashes' -or [int]$GuestPayloadReceipt.processId -ne $script:AppProcessId) {
        throw 'Guest payload receipt is not scoped to the captured MarkdownRenderer.Sample process.'
    }
    $guestFiles = @($GuestPayloadReceipt.files)
    $matches = [System.Collections.Generic.List[object]]::new()
    foreach ($buildFile in $BuildPayloads) {
        $sameName = @($guestFiles | Where-Object { [string]$_.name -ceq [string]$buildFile.name })
        if ($sameName.Count -ne 1) { throw "Guest payload receipt does not contain exactly one '$($buildFile.name)' file." }
        $guestFile = $sameName[0]
        if ([int64]$guestFile.length -ne [int64]$buildFile.length -or
            [string]$guestFile.sha256 -cne [string]$buildFile.sha256) {
            throw "Guest payload '$($buildFile.name)' does not match the approved build: host=$($buildFile.sha256)/$($buildFile.length), guest=$($guestFile.sha256)/$($guestFile.length)."
        }
        $matches.Add([pscustomobject]@{name=$buildFile.name;length=[int64]$buildFile.length;sha256=[string]$buildFile.sha256;guestPath=[string]$guestFile.path;status='MATCH'})
    }
    return [pscustomobject]@{status='MATCH';files=@($matches.ToArray());guestExecutablePath=[string]$GuestPayloadReceipt.executablePath;guestProcessId=[int]$GuestPayloadReceipt.processId}
}

function Initialize-Run {
    if (Test-Path -LiteralPath $script:RunDirectory) { throw "Fresh evidence directory already exists: $script:RunDirectory" }
    [System.IO.Directory]::CreateDirectory($script:RunDirectory) | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $script:WorkflowId

    $cli = Invoke-NativeChecked @('--version')
    if ($cli.output -notmatch '(\d+)\.(\d+)\.(\d+)') { throw "Cannot parse WinApp CLI version: $($cli.output)" }
    $version = [version]::new([int]$Matches[1],[int]$Matches[2],[int]$Matches[3])
    if ($version -lt [version]'0.7.0') { throw "WinApp CLI 0.7+ required, found $version." }
    $before = (Invoke-NativeChecked @('target','snapshot','sandbox') -Json).value
    $script:TargetEpoch = Assert-SandboxSnapshot -Snapshot $before -Phase 'before launch'
    # From this point onward the Sandbox UI workflow exists, so finally must
    # yield it even if launch or guest discovery fails.
    $script:UiStarted = $true
    $preWindows = @((Invoke-NativeChecked @('ui','list-windows','--on','sandbox') -Json).value)
    $preSamplePids = @($preWindows | Where-Object { $_.processName -eq 'MarkdownRenderer.Sample' } | ForEach-Object { [int]$_.processId } | Sort-Object -Unique)
    if ($preSamplePids.Count -gt 0) {
        throw "A MarkdownRenderer.Sample is already running in this Sandbox ($($preSamplePids -join ',')). Refusing to reuse an unqualified/stale binary; close that guest app and rerun."
    }

    $runArgs = @(Get-SampleProjectRunArguments `
        -ProjectPath $script:ProjectPath `
        -Configuration $Configuration `
        -NoBuild:$NoBuild `
        -BuildArtifactsPath $script:BuildArtifactsPath)
    $launch = (Invoke-NativeChecked $runArgs -Json).value
    $launchScope = [string](Get-RequiredJsonPropertyValue -Object $launch -Name 'ProcessScope' -Context 'Project-mode launch response')
    $launchTarget = Get-RequiredJsonPropertyValue -Object $launch -Name 'ExecutionTarget' -Context 'Project-mode launch response'
    $launchTargetKind = [string](Get-RequiredJsonPropertyValue -Object $launchTarget -Name 'Kind' -Context 'Project-mode execution target')
    $launchEpoch = [string](Get-RequiredJsonPropertyValue -Object $launchTarget -Name 'Epoch' -Context 'Project-mode execution target')
    if ($launchScope -ne 'sandbox' -or $launchTargetKind -ne 'sandbox' -or
        [string]::IsNullOrWhiteSpace($launchEpoch) -or $launchEpoch -ne $script:TargetEpoch) {
        throw "Project-mode launch did not preserve the same Sandbox scope and execution epoch (scope='$launchScope', kind='$launchTargetKind', epoch='$launchEpoch')."
    }
    # These fields are absent for a normal unpackaged project launch. When the
    # CLI supplies either field, use it as additional evidence, never as the
    # source of the UI target; the guest window discovery below is authoritative.
    $launchUiTargetArgs = Get-JsonPropertyValue -Object $launch -Name 'UiTargetArgs'
    if ($null -ne $launchUiTargetArgs -and [string](@($launchUiTargetArgs) -join ' ') -notmatch '--on\s+sandbox') {
        throw "Optional launch UI target arguments contradict Sandbox execution: '$launchUiTargetArgs'."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $appWindow = $null
    do {
        $after = (Invoke-NativeChecked @('target','snapshot','sandbox') -Json).value
        $afterEpoch = Assert-SandboxSnapshot -Snapshot $after -Phase 'while launching the sample'
        if ($afterEpoch -ne $script:TargetEpoch) { throw 'Sandbox readiness or epoch changed while launching the sample.' }
        $windows = @((Invoke-NativeChecked @('ui','list-windows','--on','sandbox') -Json).value)
        $newSampleWindows = @($windows | Where-Object {
            $_.processName -eq 'MarkdownRenderer.Sample' -and [int]$_.processId -notin $preSamplePids -and $_.title -match 'MarkdownRenderer'
        })
        if ($newSampleWindows.Count -eq 1) { $appWindow = $newSampleWindows[0]; break }
        if ($newSampleWindows.Count -gt 1) { throw "Expected one new sample window, discovered $($newSampleWindows.Count)." }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $appWindow) { throw 'Could not discover a new unpackaged sample window/PID in the same Sandbox target.' }

    $script:AppProcessId = [int]$appWindow.processId
    $script:AppWindowHandle = [long]$appWindow.hwnd
    $script:ExpectedWindowTitle = [string]$appWindow.title
    $launchProcessId = Get-JsonPropertyValue -Object $launch -Name 'ProcessId'
    if ($null -ne $launchProcessId -and -not [string]::IsNullOrWhiteSpace([string]$launchProcessId)) {
        $reportedProcessId = 0
        if (-not [int]::TryParse([string]$launchProcessId, [ref]$reportedProcessId) -or $reportedProcessId -le 0) {
            throw "Optional launch ProcessId is invalid: '$launchProcessId'."
        }
        if ($reportedProcessId -ne $script:AppProcessId) {
            throw "Launch response PID $reportedProcessId does not match discovered guest PID $($script:AppProcessId)."
        }
    }
    $script:UiStarted = $true

    Invoke-GuestProbeStage
    $identity = Assert-ProcessIdentity
    $source = Get-SourceEvidence
    $buildOutputDirectory = Get-SampleBuildOutputDirectory `
        -RepoRoot $script:RepoRoot `
        -Configuration $Configuration `
        -BuildArtifactsPath $script:BuildArtifactsPath
    $binaryPath = Join-Path $buildOutputDirectory 'MarkdownRenderer.Sample.exe'
    $binary = if (Test-Path -LiteralPath $binaryPath -PathType Leaf) {
        $file = Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256
        @{ path = $binaryPath; length = (Get-Item -LiteralPath $binaryPath).Length; sha256 = $file.Hash.ToLowerInvariant(); lastWriteUtc = (Get-Item -LiteralPath $binaryPath).LastWriteTimeUtc.ToString('o') }
    } else { @{ path = $binaryPath; status = 'UNVERIFIED_NOT_FOUND' } }
    $buildPayloads = @(Get-BuildPayloadIdentity -Directory $buildOutputDirectory)
    $script:BuildPayloads = @($buildPayloads)
    $guestPayloadReceipt = Invoke-GuestProbe -Mode 'payload-hashes'
    $payloadComparison = Assert-GuestPayloadIdentity -BuildPayloads $buildPayloads -GuestPayloadReceipt $guestPayloadReceipt
    $payloadIdentity = [pscustomobject]@{
        status = $payloadComparison.status
        host = @($buildPayloads)
        guest = @($guestPayloadReceipt.files)
        comparisons = @($payloadComparison.files)
        guestExecutablePath = $payloadComparison.guestExecutablePath
        guestProcessId = $payloadComparison.guestProcessId
    }
    $metadata = Invoke-GuestProbe -Mode 'metadata'
    $runInfo = [ordered]@{
        runId = $script:RunId; createdUtc = [DateTime]::UtcNow.ToString('o'); target = 'sandbox'; targetEpoch = $script:TargetEpoch
        workflowId = $script:WorkflowId; winAppCli = $cli.output.Trim(); configuration = $Configuration; architecture = 'x64'
        buildArtifactsPath = if ([string]::IsNullOrWhiteSpace($script:BuildArtifactsPath)) { $null } else { $script:BuildArtifactsPath }
        noBuild = [bool]$NoBuild; includeSuppressionInvestigation = [bool]$IncludeSuppressionInvestigation
        includeSelectionDisableInvestigation = [bool]$IncludeSelectionDisableInvestigation
        diagnosticOnly = [bool]$ResizeCrashDiagnosticOnly
        project = $script:ProjectPath; launch = $launch; source = $source; buildBinary = $binary; payloadIdentity = $payloadIdentity
        app = $identity; environment = $metadata; fixtureCheckpoint = '17f24924d2bf2f239a86ec91caa141a22e319646'
        fixtures = @('SampleDocuments.Markdown.cs','SampleDocuments.Profiles.cs','SampleDocuments.FeaturePacks.cs','SampleDocuments.Interaction.cs','SampleDocuments.Svg.cs')
    }
    Write-JsonFile (Join-Path $script:RunDirectory 'run.json') $runInfo
    return $runInfo
}

function Wait-SelectionProbeStatus {
    param(
        [Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Viewport,
        [Parameter(Mandatory)][ValidateSet('enabled','disabled')][string]$Selection,
        [int]$TimeoutMilliseconds = 5000
    )
    $expected = "viewport=$Viewport;selection=$Selection"
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $observed = Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'LifecycleSelectionStatus'
        if ([string]$observed.name -ceq $expected) { return $observed }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The opt-in probe did not commit '$expected'; UIA reported '$($observed.name)'."
}

function Wait-TextPatternSelectionSupport {
    param(
        [Parameter(Mandatory)][ValidateSet('Single','None')][string]$Expected,
        [int]$TimeoutMilliseconds = 5000
    )
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $observed = Invoke-GuestProbe -Mode 'selection'
        if ([string]$observed.supportedSelection -ceq $Expected) { return $observed }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "TextPattern.SupportedTextSelection did not become '$Expected'; UIA reported '$($observed.supportedSelection)'."
}

function Test-SelectionProbeHasNoSelectedContent {
    param([Parameter(Mandatory)]$Selection)
    $ranges=@($Selection.ranges)
    if ([int]$Selection.rangeCount -ne $ranges.Count -or -not [string]::IsNullOrEmpty([string]$Selection.text)) { return $false }
    if ([string]$Selection.supportedSelection -ceq 'None') { return $ranges.Count -eq 0 }
    if ([string]$Selection.supportedSelection -cne 'Single' -or $ranges.Count -gt 1) { return $false }
    # TextPattern may return one collapsed, rectangle-free caret range when
    # selection is supported. Range count alone does not mean selected content.
    foreach ($range in $ranges) {
        if (-not [string]::IsNullOrEmpty([string]$range.text) -or @($range.rectangles).Count -ne 0) { return $false }
    }
    return $true
}

function Assert-SelectionProbeCleared {
    param([Parameter(Mandatory)][string]$Context,[ValidateSet('Single','None')][string]$ExpectedSupport='None')
    $selection = Wait-TextPatternSelectionSupport -Expected $ExpectedSupport
    if (-not (Test-SelectionProbeHasNoSelectedContent -Selection $selection)) {
        throw "$Context retained selected UIA content: support=$($selection.supportedSelection), rangeCount=$($selection.rangeCount), text='$($selection.text)'."
    }
    $handles = Invoke-GuestProbe -Mode 'handles'
    if (@($handles.handles).Count -ne 0) { throw "$Context retained selection handles despite a cleared selection." }
    $menu = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll','MarkdownContextCopyLink') })
    if ($menu.Count -ne 0) { throw "$Context retained selection flyout commands despite a cleared selection: $(@($menu | ForEach-Object { Get-UiAutomationId $_ }) -join ', ')." }
    return [pscustomobject]@{supportedTextSelection=$selection.supportedSelection;rangeCount=$selection.rangeCount;selectedText=$selection.text;handleCount=@($handles.handles).Count;selectionMenuCommandCount=$menu.Count}
}

function Invoke-SelectionDisableViewportAssertions {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Viewport)

    Set-ToggleState -AutomationId 'LifecycleSelectionEnabled' -Enabled $false
    Set-ToggleState -AutomationId 'LifecycleAncestorViewport' -Enabled ($Viewport -eq 'ancestor')
    $status = Wait-SelectionProbeStatus -Viewport $Viewport -Selection 'disabled'
    $rendererCount = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownRenderer' }).Count
    if ($rendererCount -ne 1) { throw "Expected exactly one active MarkdownRenderer in $Viewport mode, found $rendererCount." }
    $null = Wait-ForRendererText -Text 'touchenabledword' -TimeoutMilliseconds 12000
    $disabledBefore = Assert-SelectionProbeCleared -Context "Initial $Viewport viewport state"

    Set-ToggleState -AutomationId 'LifecycleSelectionEnabled' -Enabled $true
    $status = Wait-SelectionProbeStatus -Viewport $Viewport -Selection 'enabled'
    $initialSupport = Assert-SelectionProbeCleared -Context "Fresh $Viewport probe" -ExpectedSupport 'Single'

    $target = Get-RangeCenter -Text 'touchenabledword'
    $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($target.x),$($target.y)")
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    $touchSelection = $null
    do {
        Start-Sleep -Milliseconds 80
        $touchSelection = Invoke-GuestProbe -Mode 'selection'
    } while ([string]$touchSelection.text -cne 'touchenabledword' -and [DateTime]::UtcNow -lt $deadline)
    if ([string]$touchSelection.supportedSelection -cne 'Single' -or [int]$touchSelection.rangeCount -ne 1 -or [string]$touchSelection.text -cne 'touchenabledword') {
        throw "Enabled touch selection in $Viewport mode did not select exactly the target word; UIA returned '$($touchSelection.text)' ($($touchSelection.rangeCount) ranges)."
    }
    $touchHandlesBeforeDisable = Assert-SelectionHandles
    # T1-018: a touch double-tap selects with handles; as in C10, a tap inside
    # the committed selection is what opens the selection flyout.
    $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','tap','--at',"$($target.x),$($target.y)")
    $null = Invoke-Ui @('wait-for','MarkdownContextCopyMarkdown','-t','3000')
    $menuBeforeDisable = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') })
    if ($menuBeforeDisable.Count -ne 3) { throw "Enabled touch selection in $Viewport mode did not expose all three selection flyout commands." }
    $selectionScreenshot = Save-Screenshot "screenshots\selection-disable-$Viewport-selected.png"

    Set-ToggleState -AutomationId 'LifecycleSelectionEnabled' -Enabled $false
    $status = Wait-SelectionProbeStatus -Viewport $Viewport -Selection 'disabled'
    $null = Invoke-Ui @('wait-for','MarkdownContextCopyMarkdown','--gone','-t','3000')
    $disabledAfterSelection = Assert-SelectionProbeCleared -Context "$Viewport selection-disable transition"
    $disabledScreenshot = Save-Screenshot "screenshots\selection-disable-$Viewport-cleared.png"

    $null = Invoke-Ui @('focus','MarkdownRenderer')
    $null = Invoke-Ui @('send-keys','ctrl+a','--via','send-input')
    $keyboardBlocked = Assert-SelectionProbeCleared -Context "$Viewport Ctrl+A while disabled"

    $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($target.x),$($target.y)")
    $touchBlocked = Assert-SelectionProbeCleared -Context "$Viewport touch double-tap while disabled"

    Set-ToggleState -AutomationId 'LifecycleSelectionEnabled' -Enabled $true
    $status = Wait-SelectionProbeStatus -Viewport $Viewport -Selection 'enabled'
    $keyboardSupport = Assert-SelectionProbeCleared -Context "Re-enabled $Viewport keyboard selection" -ExpectedSupport 'Single'
    $null = Invoke-Ui @('focus','MarkdownRenderer')
    $null = Invoke-Ui @('send-keys','ctrl+a','--via','send-input')
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    $keyboardSelection = $null
    do {
        Start-Sleep -Milliseconds 80
        $keyboardSelection = Invoke-GuestProbe -Mode 'selection'
    } while ((-not ([string]$keyboardSelection.text).Contains('keyboardenabledword') -or -not ([string]$keyboardSelection.text).Contains('touchenabledword')) -and [DateTime]::UtcNow -lt $deadline)
    if ([string]$keyboardSelection.supportedSelection -cne 'Single' -or [int]$keyboardSelection.rangeCount -lt 1 -or
        -not ([string]$keyboardSelection.text).Contains('keyboardenabledword') -or -not ([string]$keyboardSelection.text).Contains('touchenabledword')) {
        throw "Re-enabled keyboard Ctrl+A in $Viewport mode did not select the document text."
    }
    $keyboardScreenshot = Save-Screenshot "screenshots\selection-disable-$Viewport-keyboard-reenabled.png"

    Set-ToggleState -AutomationId 'LifecycleSelectionEnabled' -Enabled $false
    $status = Wait-SelectionProbeStatus -Viewport $Viewport -Selection 'disabled'
    $null = Invoke-Ui @('wait-for','MarkdownContextCopyMarkdown','--gone','-t','3000')
    $keyboardCleared = Assert-SelectionProbeCleared -Context "$Viewport keyboard-selection disable transition"

    Set-ToggleState -AutomationId 'LifecycleSelectionEnabled' -Enabled $true
    $status = Wait-SelectionProbeStatus -Viewport $Viewport -Selection 'enabled'
    $touchSupport = Assert-SelectionProbeCleared -Context "Re-enabled $Viewport touch selection" -ExpectedSupport 'Single'
    $target = Get-RangeCenter -Text 'touchenabledword'
    $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($target.x),$($target.y)")
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    $reenabledTouchSelection = $null
    do {
        Start-Sleep -Milliseconds 80
        $reenabledTouchSelection = Invoke-GuestProbe -Mode 'selection'
    } while ([string]$reenabledTouchSelection.text -cne 'touchenabledword' -and [DateTime]::UtcNow -lt $deadline)
    if ([string]$reenabledTouchSelection.supportedSelection -cne 'Single' -or [int]$reenabledTouchSelection.rangeCount -ne 1 -or [string]$reenabledTouchSelection.text -cne 'touchenabledword') {
        throw "Re-enabled touch input in $Viewport mode did not select exactly the target word."
    }
    $touchHandlesAfterReenable = Assert-SelectionHandles
    $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','tap','--at',"$($target.x),$($target.y)")
    $null = Invoke-Ui @('wait-for','MarkdownContextCopyMarkdown','-t','3000')
    $touchMenuAfterReenable = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') })
    if ($touchMenuAfterReenable.Count -ne 3) { throw "Re-enabled touch selection in $Viewport mode did not restore the selection flyout." }
    $reenabledTouchScreenshot = Save-Screenshot "screenshots\selection-disable-$Viewport-touch-reenabled.png"

    Set-ToggleState -AutomationId 'LifecycleSelectionEnabled' -Enabled $false
    $status = Wait-SelectionProbeStatus -Viewport $Viewport -Selection 'disabled'
    $null = Invoke-Ui @('wait-for','MarkdownContextCopyMarkdown','--gone','-t','3000')
    $finalDisabled = Assert-SelectionProbeCleared -Context "$Viewport final cleanup"

    return [pscustomobject]@{
        viewport=$Viewport; rendererCount=$rendererCount; status=$status.name
        disabledBefore=$disabledBefore; selectedTouchText=$touchSelection.text; touchHandlesBeforeDisable=@($touchHandlesBeforeDisable).Count; menuCommandsBeforeDisable=@($menuBeforeDisable | ForEach-Object { Get-UiAutomationId $_ })
        disabledAfterSelection=$disabledAfterSelection; keyboardBlocked=$keyboardBlocked; touchBlocked=$touchBlocked
        reenabledKeyboardText=$keyboardSelection.text; keyboardCleared=$keyboardCleared
        reenabledTouchText=$reenabledTouchSelection.text; touchHandlesAfterReenable=@($touchHandlesAfterReenable).Count; menuCommandsAfterReenable=@($touchMenuAfterReenable | ForEach-Object { Get-UiAutomationId $_ }); finalDisabled=$finalDisabled
        screenshots=@($selectionScreenshot,$disabledScreenshot,$keyboardScreenshot,$reenabledTouchScreenshot)
        launchProcessId=$script:SelectionDisableProbeIdentity.processId; launchHwnd=$script:SelectionDisableProbeIdentity.hwnd; targetEpoch=$script:TargetEpoch; managedPayloadStatus=$script:SelectionDisableProbeIdentity.payloadStatus
    }
}

function Add-SelectionDisableProbeResult {
    param([Parameter(Mandatory)][ValidateSet('PASS','FAIL','UNVERIFIED')][string]$Status,[Parameter(Mandatory)][string]$Name,[Parameter(Mandatory)][string]$Requirement,[AllowNull()][object]$Actual,[string]$Detail='')
    $script:Results.Add([pscustomobject]@{
        id='C14'; name=$Name; requirement=$Requirement; status=$Status; actual=$Actual; detail=$(if($Detail){$Detail}else{$null})
        targetIdentityBefore=$null; targetIdentityAfter=$null; evidence=@(); startedUtc=[DateTime]::UtcNow.ToString('o'); finishedUtc=[DateTime]::UtcNow.ToString('o')
    })
}

function Find-NewSelectionDisableProbeWindow {
    param(
        [Parameter(Mandatory)][object[]]$Windows,
        [Parameter(Mandatory)][int[]]$BeforeSamplePids,
        [int]$ExpectedProcessId = 0
    )
    $candidates = @($Windows | Where-Object {
        $candidatePid = 0
        $candidateHwnd = 0L
        $processName = [string](Get-JsonPropertyValue -Object $_ -Name 'processName')
        $title = [string](Get-JsonPropertyValue -Object $_ -Name 'title')
        $pidValue = Get-JsonPropertyValue -Object $_ -Name 'processId'
        $hwndValue = Get-JsonPropertyValue -Object $_ -Name 'hwnd'
        [int]::TryParse([string]$pidValue,[ref]$candidatePid) -and
        [long]::TryParse([string]$hwndValue,[ref]$candidateHwnd) -and
        $candidatePid -gt 0 -and $candidateHwnd -gt 0 -and
        $processName -ceq 'MarkdownRenderer.Sample' -and
        $title -ceq 'MarkdownRenderer selection-disable probe' -and
        $candidatePid -notin $BeforeSamplePids -and
        ($ExpectedProcessId -le 0 -or $candidatePid -eq $ExpectedProcessId)
    })
    if ($candidates.Count -gt 1) { throw "Expected at most one exact new selection-disable process/window; found $($candidates.Count)." }
    if ($candidates.Count -eq 0) { return $null }
    return $candidates[0]
}

function Get-CanonicalProcessStartUtc {
    param([Parameter(Mandatory)]$Value)
    # PS7 ConvertFrom-Json materializes ISO dates; PS5.1 leaves strings. Casting
    # a DateTime to string first loses its UTC kind and fractional precision.
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime.ToString('o') }
    if ($Value -is [DateTime]) {
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { throw 'Process start time has no UTC/time-zone identity.' }
        return $Value.ToUniversalTime().ToString('o')
    }
    $text=[string]$Value
    $parsed=[DateTime]::MinValue
    if (-not $text.EndsWith('Z',[StringComparison]::Ordinal) -or
        -not [DateTime]::TryParseExact($text,'o',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$parsed) -or
        $parsed.Kind -ne [DateTimeKind]::Utc) { throw 'Process start time is not an exact UTC round-trip timestamp.' }
    return $parsed.ToString('o')
}

function Get-SelectionDisableProcessIdentity {
    param([Parameter(Mandatory)][int]$ProcessId)
    $state = Invoke-GuestProbe -Mode 'process-state' -TargetProcessId $ProcessId
    if ([string]$state.mode -cne 'process-state' -or [int]$state.processId -ne $ProcessId -or -not [bool]$state.exists) {
        throw "Could not verify that the exact selection-disable PID $ProcessId is still running."
    }
    $processName = [string]$state.processName
    $path = [string]$state.executablePath
    if ($processName -cne 'MarkdownRenderer.Sample' -or
        [string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathRooted($path) -or
        [IO.Path]::GetFileName($path) -cne 'MarkdownRenderer.Sample.exe') {
        throw "Guest PID $ProcessId did not return a complete MarkdownRenderer.Sample process identity."
    }
    $startUtc=Get-CanonicalProcessStartUtc -Value $state.startTimeUtc
    return [pscustomobject]@{processId=$ProcessId;processName=$processName;executablePath=[IO.Path]::GetFullPath($path);startTimeUtc=$startUtc}
}

function Assert-SelectionDisableLaunchReceipt {
    param(
        [Parameter(Mandatory)]$Receipt,
        [Parameter(Mandatory)]$OriginalBefore,
        [Parameter(Mandatory)]$OriginalAfter
    )
    if ([string]$Receipt.mode -cne 'launch-selection-disable-probe' -or [string]$Receipt.launchMethod -cne 'guest-Win32_Process-Create') {
        throw 'The opt-in probe launch did not return the expected direct guest-process launch receipt.'
    }
    if (-not [bool]$Receipt.sourceRetained) { throw 'The original sample PID/path/start-time was not retained across the second-app launch.' }
    $receiptSourceBefore=$Receipt.sourceBefore
    $receiptSourceAfter=$Receipt.sourceAfter
    foreach($source in @($receiptSourceBefore,$receiptSourceAfter,$OriginalAfter)){
        if($null -eq $source -or [int]$source.processId -ne [int]$OriginalBefore.processId -or
            [string]$source.processName -cne 'MarkdownRenderer.Sample' -or
            [string]::IsNullOrWhiteSpace([string]$source.executablePath) -or
            -not [IO.Path]::IsPathRooted([string]$source.executablePath) -or
            [IO.Path]::GetFileName([string]$source.executablePath) -cne 'MarkdownRenderer.Sample.exe'){
            throw 'The launch receipt did not preserve a complete exact original guest process identity before and after launch.'
        }
        $sourcePath=[IO.Path]::GetFullPath([string]$source.executablePath)
        if(-not [string]::Equals($sourcePath,[string]$OriginalBefore.executablePath,[StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals((Get-CanonicalProcessStartUtc -Value $source.startTimeUtc),[string]$OriginalBefore.startTimeUtc,[StringComparison]::Ordinal)){
            throw 'The original guest process path or exact start time changed during direct probe launch.'
        }
    }
    $probePid=0
    if(-not [int]::TryParse([string]$Receipt.processId,[ref]$probePid) -or $probePid -le 0 -or $probePid -eq [int]$OriginalBefore.processId){
        throw 'The direct guest launcher did not return a distinct positive new sample PID.'
    }
    $probePath=[string]$Receipt.executablePath
    if([string]$Receipt.processName -cne 'MarkdownRenderer.Sample' -or [string]::IsNullOrWhiteSpace($probePath) -or
        -not [IO.Path]::IsPathRooted($probePath) -or [IO.Path]::GetFileName($probePath) -cne 'MarkdownRenderer.Sample.exe' -or
        -not [string]::Equals([IO.Path]::GetFullPath($probePath),[string]$OriginalBefore.executablePath,[StringComparison]::OrdinalIgnoreCase)){
        throw 'The new probe process does not use the exact executable path captured from the original guest process.'
    }
    $probeStart=Get-CanonicalProcessStartUtc -Value $Receipt.startTimeUtc
    $arguments=@($Receipt.arguments)
    if($arguments.Count -ne 1 -or [string]$arguments[0] -cne '--renderer-selection-disable-probe'){
        throw 'The guest launcher receipt did not preserve the fixed selection-disable argument.'
    }
    return [pscustomobject]@{processId=$probePid;processName='MarkdownRenderer.Sample';executablePath=[IO.Path]::GetFullPath($probePath);startTimeUtc=$probeStart}
}

function Assert-SelectionDisableProbeProcessIdentity {
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)]$Expected
    )
    $actualPid = 0
    $expectedPid = 0
    if (-not [int]::TryParse([string]$Actual.processId,[ref]$actualPid) -or $actualPid -le 0 -or
        -not [int]::TryParse([string]$Expected.processId,[ref]$expectedPid) -or $expectedPid -le 0) {
        throw 'The discovered or launched probe process identity did not contain a positive PID.'
    }
    $actualName = [string]$Actual.processName
    $expectedName = [string]$Expected.processName
    $actualPath = [string]$Actual.executablePath
    $expectedPath = [string]$Expected.executablePath
    if ($actualName -cne 'MarkdownRenderer.Sample' -or $expectedName -cne 'MarkdownRenderer.Sample' -or
        [string]::IsNullOrWhiteSpace($actualPath) -or [string]::IsNullOrWhiteSpace($expectedPath) -or
        -not [IO.Path]::IsPathRooted($actualPath) -or -not [IO.Path]::IsPathRooted($expectedPath) -or
        [IO.Path]::GetFileName($actualPath) -cne 'MarkdownRenderer.Sample.exe' -or
        [IO.Path]::GetFileName($expectedPath) -cne 'MarkdownRenderer.Sample.exe') {
        throw 'The discovered or launched probe process identity was incomplete or was not the expected sample executable.'
    }
    $actualFullPath = [IO.Path]::GetFullPath($actualPath)
    $expectedFullPath = [IO.Path]::GetFullPath($expectedPath)
    $actualStart = Get-CanonicalProcessStartUtc -Value $Actual.startTimeUtc
    $expectedStart = Get-CanonicalProcessStartUtc -Value $Expected.startTimeUtc
    if ($actualPid -ne $expectedPid -or
        -not [string]::Equals($actualName,$expectedName,[StringComparison]::Ordinal) -or
        -not [string]::Equals($actualFullPath,$expectedFullPath,[StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($actualStart,$expectedStart,[StringComparison]::Ordinal)) {
        throw "The process discovered for the probe window does not match the exact process instance captured by the launch receipt (launch PID $expectedPid, discovered PID $actualPid)."
    }
    return [pscustomobject]@{processId=$actualPid;processName=$actualName;executablePath=$actualFullPath;startTimeUtc=$actualStart}
}

function Test-SelectionDisableProcessIdentityAlive {
    param([Parameter(Mandatory)]$State,[Parameter(Mandatory)]$ExpectedIdentity)
    if ([string]$State.mode -cne 'process-state' -or [int]$State.processId -ne [int]$ExpectedIdentity.processId) {
        throw 'Guest process-state response did not describe the captured second-app PID.'
    }
    if (-not [bool]$State.exists) { return $false }
    $actualPath = [string]$State.executablePath
    $actualStart = Get-CanonicalProcessStartUtc -Value $State.startTimeUtc
    if ([string]::IsNullOrWhiteSpace($actualPath) -or [string]::IsNullOrWhiteSpace($actualStart) -or [string]::IsNullOrWhiteSpace([string]$State.processName)) {
        throw 'A live guest PID returned incomplete process identity during close verification.'
    }
    $pathMatches = [string]::Equals([IO.Path]::GetFullPath($actualPath),[string]$ExpectedIdentity.executablePath,[StringComparison]::OrdinalIgnoreCase)
    $expectedStart = Get-CanonicalProcessStartUtc -Value $ExpectedIdentity.startTimeUtc
    $startMatches = [string]::Equals($actualStart,$expectedStart,[StringComparison]::Ordinal)
    $nameMatches = [string]::Equals([string]$State.processName,[string]$ExpectedIdentity.processName,[StringComparison]::Ordinal)
    return ($pathMatches -and $startMatches -and $nameMatches)
}

function Save-SelectionDisableLaunchDiagnostics {
    param([Parameter(Mandatory)]$Launch,[Parameter(Mandatory)][ValidateSet('process-exit','window-discovery-timeout')][string]$CaptureReason)
    $runId = [string]$script:RunId
    if ($runId -cnotmatch '^\d{8}T\d{6}Z-[0-9a-f]{32}$') {
        return [pscustomobject]@{status='UNAVAILABLE';captureReason=$CaptureReason;detail='The active run ID is not in its canonical form.';files=@()}
    }
    $diagnostics = Get-JsonPropertyValue -Object $Launch -Name 'diagnostics'
    if ($null -eq $diagnostics) {
        return [pscustomobject]@{status='UNAVAILABLE';captureReason=$CaptureReason;detail='The guest launch receipt contained no scoped diagnostic paths.';files=@()}
    }

    $relativeDirectory = "correctness\$runId\selection-disable-launch"
    $expectedPaths = @{
        stdout = "$relativeDirectory\stdout.txt"
        stderr = "$relativeDirectory\stderr.txt"
    }
    $hostDirectory = Join-Path $script:RunDirectory 'selection-disable-launch-diagnostics'
    if (Test-Path -LiteralPath $hostDirectory) {
        return [pscustomobject]@{status='UNAVAILABLE';captureReason=$CaptureReason;detail='The host diagnostic destination already exists; refusing to overwrite evidence.';files=@()}
    }
    try { $null = [System.IO.Directory]::CreateDirectory($hostDirectory) }
    catch { return [pscustomobject]@{status='UNAVAILABLE';captureReason=$CaptureReason;detail=$_.Exception.Message;files=@()} }

    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($name in @('stdout','stderr')) {
        $propertyName = "guest$($name.Substring(0,1).ToUpperInvariant())$($name.Substring(1))RelativePath"
        $guestPath = [string](Get-JsonPropertyValue -Object $diagnostics -Name $propertyName)
        if (-not [string]::Equals($guestPath,$expectedPaths[$name],[StringComparison]::OrdinalIgnoreCase)) {
            $files.Add([pscustomobject]@{name=$name;status='REJECTED';detail='Guest path does not match the fixed diagnostic location for this run.'})
            continue
        }
        $hostPath = Join-Path $hostDirectory "$name.txt"
        if (Test-Path -LiteralPath $hostPath) {
            $files.Add([pscustomobject]@{name=$name;status='REJECTED';detail='Host diagnostic file already exists; refusing to overwrite evidence.'})
            continue
        }
        try {
            $null = Invoke-NativeChecked @('target','pull','sandbox',$guestPath,$hostPath)
            if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw 'target pull returned without creating the expected host file.' }
            $item = Get-Item -LiteralPath $hostPath
            $hash = Get-FileHash -LiteralPath $hostPath -Algorithm SHA256
            $files.Add([pscustomobject]@{
                name=$name;status='CAPTURED';hostPath=$hostPath;length=[long]$item.Length;sha256=$hash.Hash.ToLowerInvariant()
            })
        }
        catch {
            $files.Add([pscustomobject]@{name=$name;status='FAILED';detail=$_.Exception.Message})
        }
    }
    $captured = @($files | Where-Object status -eq 'CAPTURED').Count
    return [pscustomobject]@{
        status=$(if ($captured -eq 2) { 'CAPTURED' } elseif ($captured -gt 0) { 'PARTIAL' } else { 'FAILED' })
        captureReason=$CaptureReason;guestRelativeDirectory=$relativeDirectory;capturedUtc=[DateTime]::UtcNow.ToString('o');files=@($files.ToArray())
    }
}

function Run-SelectionDisableInvestigation {
    $requirement = 'On both ownership modes, disabling IsSelectionEnabled clears TextPattern selection, handles and selection flyout; keyboard and touch cannot select while disabled, and re-enabling restores both.'
    $originalIdentity = Assert-ProcessIdentity
    $originalApp = [pscustomobject]@{processId=$script:AppProcessId;hwnd=$script:AppWindowHandle;title=$originalIdentity.title}
    $probePid = 0
    $probeHwnd = 0L
    $probeDiscovered = $false
    $probeRecoveredForCleanup = $false
    $launchAttempted = $false
    $launchPid = 0
    $launch = $null
    $launchProcessIdentity = $null
    $originalProcessIdentityBefore = $null
    $originalProcessIdentityAfter = $null
    $beforeSamplePids = @()
    $probeProcessIdentity = $null
    $cleanupError = ''
    $cleanupUnverified = ''
    $script:SelectionDisableLaunchDiagnostics = $null
    $script:SelectionDisableProbeExitObserved = $false
    $script:SelectionDisableProbeCleanup = $null
    try {
        $beforeSnapshot = (Invoke-NativeChecked @('target','snapshot','sandbox') -Json).value
        if ((Assert-SandboxSnapshot -Snapshot $beforeSnapshot -Phase 'before selection-disable probe launch') -cne $script:TargetEpoch) { throw 'Sandbox epoch changed before the opt-in selection-disable probe launch.' }
        $beforeWindows = @((Invoke-NativeChecked @('ui','list-windows','--on','sandbox') -Json).value)
        $beforeSamplePids = @($beforeWindows | Where-Object { [string]$_.processName -ceq 'MarkdownRenderer.Sample' } | ForEach-Object { [int]$_.processId } | Sort-Object -Unique)
        if ($originalApp.processId -notin $beforeSamplePids) { throw 'The original verified sample is missing before the opt-in second-app launch.' }
        $originalProcessIdentityBefore = Get-SelectionDisableProcessIdentity -ProcessId $originalApp.processId

        $launchAttempted = $true
        $launch = Invoke-GuestProbe -Mode 'launch-selection-disable-probe' -RunId $script:RunId
        $reportedPid = Get-JsonPropertyValue -Object $launch -Name 'processId'
        if ($null -ne $reportedPid -and -not [string]::IsNullOrWhiteSpace([string]$reportedPid)) {
            if (-not [int]::TryParse([string]$reportedPid,[ref]$launchPid) -or $launchPid -le 0) {
                $launchPid = 0
                throw 'The direct guest launcher returned an invalid new-process PID.'
            }
        }
        $afterLaunchSnapshot = (Invoke-NativeChecked @('target','snapshot','sandbox') -Json).value
        if ((Assert-SandboxSnapshot -Snapshot $afterLaunchSnapshot -Phase 'after direct selection-disable probe launch') -cne $script:TargetEpoch) { throw 'Sandbox epoch changed during direct selection-disable probe launch.' }
        $null = Assert-ProcessIdentity
        $originalProcessIdentityAfter = Get-SelectionDisableProcessIdentity -ProcessId $originalApp.processId
        $launchProcessIdentity = Assert-SelectionDisableLaunchReceipt -Receipt $launch -OriginalBefore $originalProcessIdentityBefore -OriginalAfter $originalProcessIdentityAfter

        $startupObservation = Get-JsonPropertyValue -Object $launch -Name 'startupObservation'
        $startupStatus = [string](Get-JsonPropertyValue -Object $startupObservation -Name 'status')
        if ($startupStatus -ceq 'process-exited-before-window-observation') {
            $script:SelectionDisableProbeExitObserved = $true
            $script:SelectionDisableLaunchDiagnostics = Save-SelectionDisableLaunchDiagnostics -Launch $launch -CaptureReason 'process-exit'
            $exitCode = Get-JsonPropertyValue -Object $startupObservation -Name 'exitCode'
            throw "The exact selection-disable child PID $launchPid exited before its probe window was observed (exit code $exitCode); scoped stdout/stderr capture is $($script:SelectionDisableLaunchDiagnostics.status)."
        }
        if ($startupStatus -cnotin @('window-candidate-observed','process-live-no-window-observed')) {
            throw "The guest launcher returned an unknown bounded startup observation '$startupStatus'."
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        $probeWindow = $null
        $nextProcessCheckUtc = [DateTime]::MinValue
        do {
            $snapshot = (Invoke-NativeChecked @('target','snapshot','sandbox') -Json).value
            if ((Assert-SandboxSnapshot -Snapshot $snapshot -Phase 'discovering selection-disable probe') -cne $script:TargetEpoch) { throw 'Sandbox epoch changed during opt-in probe discovery.' }
            $windows = @((Invoke-NativeChecked @('ui','list-windows','--on','sandbox') -Json).value)
            $probeWindow = Find-NewSelectionDisableProbeWindow -Windows $windows -BeforeSamplePids $beforeSamplePids -ExpectedProcessId $launchPid
            if ($null -ne $probeWindow) { break }
            if ([DateTime]::UtcNow -ge $nextProcessCheckUtc) {
                $state = Invoke-GuestProbe -Mode 'process-state' -TargetProcessId $launchPid
                if ([string]$state.mode -cne 'process-state' -or [int]$state.processId -ne $launchPid) {
                    throw 'The bounded startup process-state check did not describe the exact launched PID.'
                }
                if (-not [bool]$state.exists) {
                    $script:SelectionDisableProbeExitObserved = $true
                    $script:SelectionDisableLaunchDiagnostics = Save-SelectionDisableLaunchDiagnostics -Launch $launch -CaptureReason 'process-exit'
                    throw "The exact launched selection-disable child PID $launchPid exited before its exact-title window was discoverable; scoped stdout/stderr capture is $($script:SelectionDisableLaunchDiagnostics.status)."
                }
                if (-not (Test-SelectionDisableProcessIdentityAlive -State $state -ExpectedIdentity $launchProcessIdentity)) {
                    throw 'The launched PID is still present but its exact process path/start-time identity changed; refusing discovery or cleanup against a reused PID.'
                }
                $nextProcessCheckUtc = [DateTime]::UtcNow.AddSeconds(2)
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $probeWindow) {
            $script:SelectionDisableLaunchDiagnostics = Save-SelectionDisableLaunchDiagnostics -Launch $launch -CaptureReason 'window-discovery-timeout'
            throw "Could not discover a new process/window with the exact selection-disable probe title within 45 seconds; scoped stdout/stderr capture is $($script:SelectionDisableLaunchDiagnostics.status)."
        }
        $probePid = [int]$probeWindow.processId
        $probeHwnd = [long]$probeWindow.hwnd
        if ($probePid -le 0 -or $probeHwnd -le 0 -or $probePid -eq $originalApp.processId -or $probeHwnd -eq $originalApp.hwnd) { throw 'The opt-in probe did not produce a distinct valid process and HWND.' }
        if ($launchPid -gt 0 -and $launchPid -ne $probePid) { throw 'The exact-title discovered probe PID does not match the early-captured launcher PID.' }
        $script:AppProcessId = $probePid
        $script:AppWindowHandle = $probeHwnd
        $script:ExpectedWindowTitle = 'MarkdownRenderer selection-disable probe'
        $identity = Assert-ProcessIdentity
        if ($identity.title -cne $script:ExpectedWindowTitle) { throw 'Probe window title differs from the exact approved title.' }
        $probeProcessIdentity = Get-SelectionDisableProcessIdentity -ProcessId $probePid
        $probeProcessIdentity = Assert-SelectionDisableProbeProcessIdentity -Actual $probeProcessIdentity -Expected $launchProcessIdentity
        $probeDiscovered = $true
        $payloadReceipt = Invoke-GuestProbe -Mode 'payload-hashes'
        $payloadComparison = Assert-GuestPayloadIdentity -BuildPayloads $script:BuildPayloads -GuestPayloadReceipt $payloadReceipt
        if ([string]$payloadComparison.status -cne 'MATCH') { throw 'The second app managed payload differs from the captured local build.' }
        if ([string]::IsNullOrWhiteSpace([string]$payloadComparison.guestExecutablePath) -or
            -not [string]::Equals([IO.Path]::GetFullPath([string]$payloadComparison.guestExecutablePath),[string]$launchProcessIdentity.executablePath,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'The second app payload-hash receipt was not read beside the exact direct-launched guest executable.'
        }
        $script:SelectionDisableProbeIdentity = [pscustomobject]@{
            processId=$probePid; hwnd=$probeHwnd; title=$identity.title; targetEpoch=$script:TargetEpoch
            launchMethod=[string]$launch.launchMethod; launch=$launch
            originalProcessIdentityBefore=$originalProcessIdentityBefore;originalProcessIdentityAfter=$originalProcessIdentityAfter
            targetEpochBefore=$script:TargetEpoch;targetEpochAfter=[string]$afterLaunchSnapshot.executionTarget.epoch
            launchProcessIdentity=$launchProcessIdentity;guestProcessIdentity=$probeProcessIdentity
            payloadStatus=$payloadComparison.status; hostPayloads=@($script:BuildPayloads); guestPayloads=@($payloadReceipt.files); payloadComparisons=@($payloadComparison.files)
        }
        Add-SelectionDisableProbeResult -Status 'PASS' -Name 'selection-disable-probe-second-app-identity-and-payload' -Requirement $requirement -Actual $script:SelectionDisableProbeIdentity

        Test-Case -Id 'C14' -Name 'is-selection-enabled-disables-and-reenables-owned-viewport' -Requirement $requirement -Body {
            Invoke-SelectionDisableViewportAssertions -Viewport 'owned'
        }
        Test-Case -Id 'C14' -Name 'is-selection-enabled-disables-and-reenables-ancestor-viewport' -Requirement $requirement -Body {
            Invoke-SelectionDisableViewportAssertions -Viewport 'ancestor'
        }
    }
    catch {
        Add-SelectionDisableProbeResult -Status 'FAIL' -Name 'selection-disable-probe-launch-setup-or-assertion' -Requirement $requirement -Actual $null -Detail $_.Exception.ToString()
    }
    finally {
        if ($launchAttempted -and -not $probeDiscovered) {
            try {
                if ($script:TargetIdentityInvalid) {
                    $cleanupUnverified = "A second-app launch was attempted, but cleanup is unsafe after Sandbox identity invalidation: $script:TargetIdentityFailure"
                }
                elseif ($script:SelectionDisableProbeExitObserved) {
                    $cleanupUnverified = 'The exact launched child exited before an exact-title probe window was discovered; no cleanup input was sent.'
                }
                else {
                    $recoveryDeadline = [DateTime]::UtcNow.AddSeconds(8)
                    $recoveredWindow = $null
                    do {
                        $snapshot = (Invoke-NativeChecked @('target','snapshot','sandbox') -Json).value
                        if ((Assert-SandboxSnapshot -Snapshot $snapshot -Phase 'recovering selection-disable probe identity for cleanup') -cne $script:TargetEpoch) {
                            throw 'Sandbox epoch changed before cleanup identity recovery.'
                        }
                        $windows = @((Invoke-NativeChecked @('ui','list-windows','--on','sandbox') -Json).value)
                        $recoveredWindow = Find-NewSelectionDisableProbeWindow -Windows $windows -BeforeSamplePids $beforeSamplePids -ExpectedProcessId $launchPid
                        if ($null -ne $recoveredWindow) { break }
                        Start-Sleep -Milliseconds 250
                    } while ([DateTime]::UtcNow -lt $recoveryDeadline)
                    if ($null -eq $recoveredWindow) {
                        $cleanupUnverified = "A second-app launch was attempted (launcher PID $launchPid), but no unique new process/HWND with the exact probe title can be proven; no cleanup input was sent."
                    }
                    else {
                        $probePid = [int]$recoveredWindow.processId
                        $probeHwnd = [long]$recoveredWindow.hwnd
                        if ($probePid -le 0 -or $probeHwnd -le 0 -or $probePid -eq $originalApp.processId -or $probeHwnd -eq $originalApp.hwnd) {
                            throw 'Recovered selection-disable cleanup identity is not distinct and positive.'
                        }
                        $script:AppProcessId = $probePid
                        $script:AppWindowHandle = $probeHwnd
                        $script:ExpectedWindowTitle = 'MarkdownRenderer selection-disable probe'
                        $recoveredIdentity = Assert-ProcessIdentity
                        if ($recoveredIdentity.title -cne 'MarkdownRenderer selection-disable probe') {
                            throw 'Recovered selection-disable cleanup title did not match exactly.'
                        }
                        $recoveredProbeProcessIdentity = Get-SelectionDisableProcessIdentity -ProcessId $probePid
                        if ($null -ne $launchProcessIdentity) {
                            $recoveredProbeProcessIdentity = Assert-SelectionDisableProbeProcessIdentity -Actual $recoveredProbeProcessIdentity -Expected $launchProcessIdentity
                        }
                        $probeProcessIdentity = $recoveredProbeProcessIdentity
                        $probeDiscovered = $true
                        $probeRecoveredForCleanup = $true
                        $cleanupUnverified = ''
                    }
                }
            }
            catch {
                $cleanupUnverified = 'A second-app launch was attempted, but no safe exact process/window identity was proven for normal-close cleanup: ' + $_.Exception.ToString()
            }
        }

        if ($probeDiscovered) {
            try {
                if ($script:TargetIdentityInvalid) { throw "Refusing cleanup input after Sandbox target identity was invalidated: $script:TargetIdentityFailure" }
                $currentIdentity = Assert-ProcessIdentity
                if ($currentIdentity.title -cne 'MarkdownRenderer selection-disable probe') { throw 'Refusing cleanup because the exact probe window title changed.' }

                if ($null -eq $probeProcessIdentity) {
                    try {
                        $probeProcessIdentity = Get-SelectionDisableProcessIdentity -ProcessId $probePid
                        if ($cleanupUnverified -like 'A unique exact window was recovered*' -or $cleanupUnverified -like 'The exact window can be closed normally*') { $cleanupUnverified = '' }
                    }
                    catch { $cleanupUnverified = 'The exact window can be closed normally, but stable guest process identity is unavailable: ' + $_.Exception.ToString() }
                }
                if ($null -ne $probeProcessIdentity) {
                    $stateBeforeClose = Invoke-GuestProbe -Mode 'process-state'
                    if (-not (Test-SelectionDisableProcessIdentityAlive -State $stateBeforeClose -ExpectedIdentity $probeProcessIdentity)) {
                        throw 'The captured second-app process identity was already absent/replaced before its normal close action.'
                    }
                }

                $null = Invoke-Ui @('invoke','LifecycleCloseProbe','--action','invoke')
                $closeDeadline = [DateTime]::UtcNow.AddSeconds(15)
                $probeWindowGone = $false
                do {
                    $snapshot = (Invoke-NativeChecked @('target','snapshot','sandbox') -Json).value
                    if ((Assert-SandboxSnapshot -Snapshot $snapshot -Phase 'closing selection-disable probe') -cne $script:TargetEpoch) { throw 'Sandbox epoch changed while closing the opt-in probe.' }
                    $windows = @((Invoke-NativeChecked @('ui','list-windows','--on','sandbox') -Json).value)
                    $remaining = @($windows | Where-Object { [int]$_.processId -eq $probePid -and [long]$_.hwnd -eq $probeHwnd })
                    if ($remaining.Count -eq 0) { $probeWindowGone = $true; break }
                    if ($remaining.Count -ne 1 -or [string]$remaining[0].processName -cne 'MarkdownRenderer.Sample' -or [string]$remaining[0].title -cne 'MarkdownRenderer selection-disable probe') { throw 'Probe cleanup identity no longer matches the captured process/HWND/title.' }
                    Start-Sleep -Milliseconds 150
                } while ([DateTime]::UtcNow -lt $closeDeadline)
                if (-not $probeWindowGone) { throw 'The exact opt-in probe HWND remained after its normal Close button was invoked.' }

                $processExited = $false
                $pidReused = $false
                $exitState = $null
                if ($null -ne $probeProcessIdentity) {
                    $processDeadline = [DateTime]::UtcNow.AddSeconds(10)
                    do {
                        $exitState = Invoke-GuestProbe -Mode 'process-state'
                        if (-not [bool]$exitState.exists) { $processExited = $true; break }
                        if (-not (Test-SelectionDisableProcessIdentityAlive -State $exitState -ExpectedIdentity $probeProcessIdentity)) {
                            # A different start-time/path/name at the same PID
                            # proves the captured process instance has exited.
                            $processExited = $true
                            $pidReused = $true
                            break
                        }
                        Start-Sleep -Milliseconds 200
                    } while ([DateTime]::UtcNow -lt $processDeadline)
                    if (-not $processExited) { throw 'The exact second-app PID/start-time/path identity remained live after its HWND closed.' }
                }
                else {
                    $cleanupUnverified = 'The probe window closed normally, but exact second-app process exit could not be verified because its stable pre-close process identity was unavailable.'
                }
                $script:SelectionDisableProbeCleanup = [pscustomobject]@{
                    processId=$probePid; hwnd=$probeHwnd; processIdentity=$probeProcessIdentity
                    windowGone=$probeWindowGone; exactProcessIdentityExited=$processExited; pidReused=$pidReused; finalProcessState=$exitState
                }
            }
            catch { $cleanupError = $_.Exception.ToString() }
        }
        $script:AppProcessId = $originalApp.processId
        $script:AppWindowHandle = $originalApp.hwnd
        $script:ExpectedWindowTitle = $originalApp.title
        try {
            $originalAfter = Assert-ProcessIdentity
            if ($originalAfter.title -cne $originalApp.title) { throw 'Original sample window title did not restore exactly after the probe closed.' }
        }
        catch {
            if ($cleanupError) { $cleanupError += [Environment]::NewLine }
            $cleanupError += 'Could not restore and revalidate the original app identity: ' + $_.Exception.ToString()
        }
        if ($cleanupError) {
            Add-SelectionDisableProbeResult -Status 'FAIL' -Name 'selection-disable-probe-normal-close-and-original-identity-restore' -Requirement 'The second probe app must close normally by its exact captured identity; the original app PID/HWND/title must remain live in the same Sandbox epoch.' -Actual @{launchAttempted=$launchAttempted;launchProcessId=$launchPid;launchMetadata=$launch;launchDiagnostics=$script:SelectionDisableLaunchDiagnostics;recoveredForCleanup=$probeRecoveredForCleanup;probeProcessId=$probePid;probeHwnd=$probeHwnd;cleanup=$script:SelectionDisableProbeCleanup;originalProcessId=$originalApp.processId;originalHwnd=$originalApp.hwnd;epoch=$script:TargetEpoch} -Detail $cleanupError
        }
        elseif ($cleanupUnverified) {
            Add-SelectionDisableProbeResult -Status 'UNVERIFIED' -Name 'selection-disable-probe-normal-close-and-original-identity-restore' -Requirement 'The second probe app must close normally by its exact captured identity, and the exact second-app PID/start-time/path identity must exit; the original app PID/HWND/title must remain live in the same Sandbox epoch.' -Actual @{launchAttempted=$launchAttempted;launchProcessId=$launchPid;launchMetadata=$launch;launchDiagnostics=$script:SelectionDisableLaunchDiagnostics;recoveredForCleanup=$probeRecoveredForCleanup;probeProcessId=$probePid;probeHwnd=$probeHwnd;cleanup=$script:SelectionDisableProbeCleanup;originalProcessId=$originalApp.processId;originalHwnd=$originalApp.hwnd;epoch=$script:TargetEpoch} -Detail $cleanupUnverified
        }
        elseif ($probeDiscovered) {
            Add-SelectionDisableProbeResult -Status 'PASS' -Name 'selection-disable-probe-normal-close-and-original-identity-restore' -Requirement 'The second probe app must close normally by its exact captured identity, and the exact second-app PID/start-time/path identity must exit; the original app PID/HWND/title must remain live in the same Sandbox epoch.' -Actual @{launchAttempted=$launchAttempted;launchProcessId=$launchPid;launchMetadata=$launch;launchDiagnostics=$script:SelectionDisableLaunchDiagnostics;recoveredForCleanup=$probeRecoveredForCleanup;probeProcessId=$probePid;probeHwnd=$probeHwnd;cleanup=$script:SelectionDisableProbeCleanup;originalProcessId=$originalApp.processId;originalHwnd=$originalApp.hwnd;originalTitle=$originalApp.title;epoch=$script:TargetEpoch}
        }
    }
}

function Run-CorrectnessCases {
    Test-Case -Id 'C00' -Name 'sandbox-and-app-identity' -Requirement 'Fresh project-mode app is pinned to one live Sandbox epoch, guest PID and HWND.' -Body {
        $identity = Assert-ProcessIdentity
        if ($identity.processName -ne 'MarkdownRenderer.Sample' -or -not $identity.title) { throw 'The captured window is not the sample app.' }
        if ([string]$runInfo.payloadIdentity.status -cne 'MATCH' -or @($runInfo.payloadIdentity.comparisons).Count -lt 3) { throw 'The live guest managed payloads do not match this run build receipt.' }
        $inspection = Get-UiInspection
        $elements = @(Expand-UiElements @($inspection.windows | ForEach-Object { $_.elements }))
    if (@($elements | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownRenderer' }).Count -eq 0) { throw 'Renderer-scoped inspection omitted MarkdownRenderer.' }
        $shellIds = @('MarkdownEditor','SamplePageTitle','SampleNav_TouchSelection')
        $shellNames = @{}
        foreach ($requiredId in $shellIds) {
            $raw = Invoke-GuestProbe -Mode 'raw-name' -AutomationId $requiredId
            if ($null -eq $raw.name) { throw "Required shell AutomationId $requiredId is absent from the same verified app window raw tree." }
            $shellNames[$requiredId] = [string]$raw.name
        }
        $shot = Save-Screenshot 'screenshots\initial.png'
        return [pscustomobject]@{ identity=$identity; payloadIdentity=$runInfo.payloadIdentity; rendererTreeCount=$elements.Count; shellRawNames=$shellNames; screenshot=$shot }
    }

    $pageCases = @(
        @{ Id='C02'; Key='Typography'; Text='The quick brown fox'; Additional=@('Bold text','A blockquote with an accent bar') ; Capture=$true },
        @{ Id='C03'; Key='Lists'; Text='Nested level 3'; Additional=@('Task Lists (GFM)','Performance profiling on real documents'); Capture=$true },
        @{ Id='C02'; Key='Tables'; Text='GFM Tables'; Additional=@('Feature','Status','Notes','Selection + Copy'); Capture=$true },
        @{ Id='C04'; Key='Code'; Text='Fenced Code Blocks'; Additional=@('type RenderState','This deliberately long line'); Capture=$true },
        @{ Id='C06'; Key='Images'; Text='Images, captions, and SVG'; Additional=@('A blue 64×64 circle drawn entirely in SVG','A 1×1 placeholder showing a friendly error icon'); Capture=$true },
        @{ Id='C15'; Key='GitHubAlerts'; Text='GitHub README Alerts'; Additional=@('Useful information','Urgent info'); Capture=$false },
        @{ Id='C15'; Key='Footnotes'; Text='Footnote back-links'; Additional=@('first footnote','Click ↩ to return'); Capture=$false },
        @{ Id='C15'; Key='MarkdownExtra'; Text='Markdown Extra'; Additional=@('Definition Lists','Blue circle'); Capture=$false },
        @{ Id='C05'; Key='Html'; Text='Native safe HTML'; Additional=@('Native disclosure','Unknown element content remains visible'); Capture=$true },
        @{ Id='C15'; Key='Math'; Text='Native mathematics'; Additional=@('E = mc^2','Binomial theorem'); Capture=$true },
        @{ Id='C15'; Key='Mermaid'; Text='Native Mermaid'; Additional=@('Invokable Mermaid node','Kanban'); Capture=$true },
        @{ Id='C15'; Key='SvgStress'; Text='Browser-class static SVG'; Additional=@('Last-item selection boundary','Theme-aware SVG feature matrix'); Capture=$true },
        @{ Id='C15'; Key='Diagrams'; Text='Diagram extension pipeline'; Additional=@('DiagramBox','exact code'); Capture=$false },
        @{ Id='C15'; Key='Embeds'; Text='Declarative Hosted Elements'; Additional=@('This marker announces task state'); Capture=$false },
        @{ Id='C08'; Key='Selection'; Text='DOM-Style Text Selection'; Additional=@('CF_HTML','Copy as Markdown'); Capture=$true },
        @{ Id='C10'; Key='TouchSelection'; Text='Touch selection'; Additional=@('this link remains touchable','stationary edge dragging continues here'); Capture=$true },
        @{ Id='C09'; Key='KeyboardNav'; Text='Keyboard Navigation'; Additional=@('Ctrl+A','Ctrl+C','CommonMark'); Capture=$false },
        @{ Id='C11'; Key='Rtl'; Text='دعم الكتابة'; Additional=@('العنصر الأول','hyperlink to github.com'); Capture=$true },
        @{ Id='C07'; Key='LazyImages'; Text='Lazy Image Loading'; Additional=@('Images below fold','Square blue concentric-circle SVG fixture'); Capture=$false },
        @{ Id='C13'; Key='ScrollAnchor'; Text='Scroll Anchoring'; Additional=@('This guide explains','do not make network requests'); Capture=$false },
        @{ Id='C15'; Key='ProgressiveScenes'; Text='Progressive scene preparation'; Additional=@('Section 36','Only this sample opts into deferred Math and Mermaid scenes'); Capture=$false }
    )
    foreach ($page in $pageCases) {
        $id = $page.Id
        $key = $page.Key
        $text = $page.Text
        $additional = $page.Additional
        $capture = [bool]$page.Capture
        $body = {
            return Navigate-And-Smoke -Key $key -Text $text -AdditionalText $additional -Capture:$capture
        }.GetNewClosure()
        Test-Case -Id $id -Name "sample-$key-content-and-navigation" -Requirement "The checked-in $key fixture is reached by stable nav ID and its required content appears in UIA text." -Body $body
    }

    Test-Case -Id 'C07' -Name 'four-local-data-svg-image-fixtures-render-with-exact-alt-and-bounds' -Requirement 'Every deterministic data-URI SVG in LazyImages resolves to one successful, visible MarkdownImage peer with its exact authored alt text and nonempty geometry contained by the renderer.' -Body {
        Navigate-Sample -Key 'LazyImages' -RequiredText 'Lazy Image Loading'
        $renderer=Get-UiBounds 'MarkdownRenderer'
        $fixtures=@(
            @{alt='Square blue concentric-circle SVG fixture';shot='lazy-svg-square-blue.png'},
            @{alt='Portrait purple-and-gold SVG fixture';shot='lazy-svg-portrait-purple-gold.png'},
            @{alt='Wide blue landscape SVG fixture';shot='lazy-svg-wide-blue-landscape.png'},
            @{alt='Wide teal icon-strip SVG fixture';shot='lazy-svg-wide-teal-strip.png'}
        )
        $observed=[System.Collections.Generic.List[object]]::new()
        foreach($fixture in $fixtures){
            $range=Invoke-GuestProbe -Mode 'scroll-range' -Needle $fixture.alt
            if([string]$range.text -cne [string]$fixture.alt){throw "TextPattern did not locate the exact authored image alternative text '$($fixture.alt)'."}
            $state=Wait-ForImagePeerState -AltText $fixture.alt
            $images=@($state.images)
            if($images.Count -ne 1){throw "Expected exactly one MarkdownImage peer for '$($fixture.alt)', found $($images.Count)."}
            $image=$images[0]
            if([string]$image.className -cne 'MarkdownImage'){throw "Fixture '$($fixture.alt)' is not exposed as the MarkdownImage provider (class='$($image.className)')."}
            if([string]$image.name -cne [string]$fixture.alt){throw "Fixture '$($fixture.alt)' did not finish in its successful authored-alt state; name='$($image.name)', helpText='$($image.helpText)'."}
            if([bool]$image.offscreen -or [double]$image.width -le 0 -or [double]$image.height -le 0){throw "Fixture '$($fixture.alt)' has no visible nonempty UIA geometry."}
            if([double]$image.x -lt ($renderer[0]-1) -or [double]$image.y -lt ($renderer[1]-1) -or ([double]$image.x+[double]$image.width) -gt ($renderer[0]+$renderer[2]+1) -or ([double]$image.y+[double]$image.height) -gt ($renderer[1]+$renderer[3]+1)){
                throw "Fixture '$($fixture.alt)' image bounds escape the renderer: x=$($image.x), y=$($image.y), width=$($image.width), height=$($image.height)."
            }
            $shot=Save-Screenshot ("screenshots\{0}" -f $fixture.shot)
            $observed.Add(@{alt=$fixture.alt;className=$image.className;automationId=$image.automationId;isLoaded=$true;isUnavailable=$false;bounds=@{x=$image.x;y=$image.y;width=$image.width;height=$image.height};screenshot=$shot})
        }
        return @($observed.ToArray())
    }

    Test-Case -Id 'C15' -Name 'math-native-scenes-have-geometry' -Requirement 'The documented Math page exposes visible native Math elements and no syntax fallback for valid formulas.' -Body {
        Navigate-Sample -Key 'Math' -RequiredText 'Native mathematics'
        $elements = @(Get-UiElements)
        $math = @($elements | Where-Object className -eq 'MarkdownMath')
        if ($math.Count -lt 1) { throw 'No native MarkdownMath UIA elements were exposed.' }
        if (@($math | Where-Object { $_.width -le 0 -or $_.height -le 0 }).Count -gt 0) { throw 'One or more Math elements have empty geometry.' }
        if (@($elements | Where-Object className -eq 'MarkdownCodeBlock').Count -gt 0) { throw 'The valid Math fixture unexpectedly contains a code-block fallback.' }
        $diagnostic = Get-SampleDiagnosticMessage
        if ($diagnostic -match 'MATH100|MMR0002') { throw "Math fixture emitted a syntax/layout diagnostic: $diagnostic" }
        return [pscustomobject]@{ nativeMathCount=$math.Count; diagnostics=$diagnostic; screenshot=(Save-Screenshot 'screenshots\math-native.png') }
    }

    Test-Case -Id 'C15' -Name 'svg-script-and-animation-use-accessible-failure-placeholders' -Requirement 'Script-bearing and animated SVG rows show typed unavailable-image placeholders rather than partially rendered active artwork.' -Body {
        Navigate-Sample -Key 'SvgStress' -RequiredText 'Browser-class static SVG'
        $null = Invoke-GuestProbe -Mode 'scroll-range' -Needle 'Each unsafe input must render one atomic diagnostic placeholder'
        $scriptPeer = Wait-ForImagePeerState -AltText 'Script-bearing SVG'
        $animationPeer = Wait-ForImagePeerState -AltText 'Animated SVG'
        $shot = Save-Screenshot 'screenshots\svg-unsupported-content-failure-states.png'
        $expectedPeers = @(
            @{ alt='Script-bearing SVG'; peer=$scriptPeer.images[0] },
            @{ alt='Animated SVG'; peer=$animationPeer.images[0] }
        )
        foreach ($entry in $expectedPeers) {
            $peer = $entry.peer
            if ([string]$peer.className -cne 'MarkdownImage') { throw "The $($entry.alt) failure is not exposed as a MarkdownImage semantic peer (class=$($peer.className))." }
            if ([string]$peer.name -notmatch ('^Image unavailable:\s*' + [regex]::Escape($entry.alt) + '$')) {
                throw "The $($entry.alt) peer did not expose the typed unavailable-image state; actual accessible Name='$($peer.name)' HelpText='$($peer.helpText)'."
            }
            if ([bool]$peer.offscreen -or [double]$peer.width -le 0 -or [double]$peer.height -le 0) {
                throw "The $($entry.alt) diagnostic placeholder has no visible, non-empty UIA geometry."
            }
        }
        return [pscustomobject]@{script=$scriptPeer.images[0];animation=$animationPeer.images[0];screenshot=$shot;typedErrorNamesVerified=$true}
    }

    Test-Case -Id 'C15' -Name 'mermaid-native-scenes-and-final-diagram-have-geometry' -Requirement 'The documented Mermaid page exposes native diagram/link semantics and its final diagram remains inside the viewport at the document end.' -Body {
        Navigate-Sample -Key 'Mermaid' -RequiredText 'Native Mermaid'
        $beforeScroll = Invoke-GuestProbe -Mode 'scroll'
        $null = Invoke-Ui @('scroll','MarkdownRenderer','--to','bottom')
        $afterScroll = Wait-ForOwnedRendererScrollBottom
        $elements = @(Get-UiElements)
        $diagrams = @($elements | Where-Object className -eq 'MarkdownDiagram')
        if ($diagrams.Count -lt 1) { throw 'No native MarkdownDiagram elements were exposed.' }
        if (@($diagrams | Where-Object { $_.width -le 0 -or $_.height -le 0 }).Count -gt 0) { throw 'One or more native diagrams have empty geometry.' }
        $link = @($elements | Where-Object { $_.className -eq 'MarkdownDiagramLink' -and $_.name -eq 'Invokable Mermaid node' })
        if ($link.Count -ne 1) { throw 'The first diagram node did not expose its expected accessible link.' }
        $last = $diagrams | Sort-Object y | Select-Object -Last 1
        if ($last.isOffscreen -or $last.y -le 0) {
            $bottomDiagnostics = [ordered]@{beforeScroll=$beforeScroll;afterScroll=$afterScroll;diagrams=$diagrams;rendererBounds=(Get-UiBounds 'MarkdownRenderer')}
            throw ('The final diagram is not visible at the bottom of the document. Diagnostics: ' + (ConvertTo-Json -InputObject $bottomDiagnostics -Depth 8 -Compress))
        }
        $renderer = Get-UiBounds 'MarkdownRenderer'
        if ($last.x -lt $renderer[0] -or ($last.x+$last.width) -gt ($renderer[0]+$renderer[2]+2)) { throw 'The final diagram extends outside the renderer bounds.' }
        return [pscustomobject]@{ nativeDiagramCount=$diagrams.Count; finalDiagram=@{name=$last.name;x=$last.x;y=$last.y;width=$last.width;height=$last.height}; accessibleNode=$link[0].name; beforeScroll=$beforeScroll; afterScroll=$afterScroll; screenshot=(Save-Screenshot 'screenshots\mermaid-bottom.png') }
    }

    Test-Case -Id 'C06' -Name 'local-data-svg-and-wide-table-image-are-contained' -Requirement 'A valid data-URI SVG remains available and the wide SVG table image and adjacent cell stay within the renderer at a narrow window size.' -Body {
        Navigate-Sample -Key 'SvgStress' -RequiredText 'Browser-class static SVG'
        $null = Invoke-Ui @('scroll','MarkdownRenderer','--to','bottom')
        $null = Invoke-GuestProbe -Mode 'scroll-range' -Needle 'Theme-aware SVG feature matrix'
        $image = Find-UiElement -Text 'Theme-aware SVG feature matrix' -Type 'Image'
        if ($image.width -le 0 -or $image.height -le 0) { throw 'The valid local SVG has no UIA geometry.' }
        $meta = Invoke-GuestProbe -Mode 'metadata'
        $before = @{width=($meta.window.right-$meta.window.left);height=($meta.window.bottom-$meta.window.top)}
        $null = Invoke-GuestProbe -Mode 'resize' -Left ([int]$meta.window.left) -Top ([int]$meta.window.top) -Width ([int][Math]::Round(760 * $meta.dpiScale)) -Height ([int][Math]::Round(860 * $meta.dpiScale))
        Start-Sleep -Milliseconds 250
        Navigate-Sample -Key 'SvgStress' -RequiredText 'Browser-class static SVG'
        $null = Invoke-Ui @('scroll','MarkdownRenderer','--to','bottom')
        $null = Invoke-GuestProbe -Mode 'scroll-range' -Needle 'Wide graph constrained to its table cell'
        $wide = Find-UiElement -Text 'Wide graph constrained to its table cell' -Type 'Image'
        $neighbor = Find-UiElement -Text 'intrinsic 1600-pixel width must not become an unshrinkable column minimum' -Type 'Text'
        $cellEvidence = Invoke-GuestProbe -Mode 'table-cell' -Needle 'Wide graph constrained to its table cell' -StartNeedle 'intrinsic 1600-pixel width must not become an unshrinkable column minimum'
        $renderer = Get-UiBounds 'MarkdownRenderer'
        if ($wide.width -le 0 -or $wide.width -gt $renderer[2]) { throw "Wide table image width $($wide.width) is not contained by renderer width $($renderer[2])." }
        if ($neighbor.isOffscreen -or $neighbor.width -le 0 -or $neighbor.x -lt $renderer[0] -or ($neighbor.x+$neighbor.width) -gt ($renderer[0]+$renderer[2]+2)) {
            throw 'The adjacent table cell content is clipped or outside the narrow renderer.'
        }
        $imageRect = $cellEvidence.imageRect
        $imageCell = $cellEvidence.cellRect
        $adjacentCell = $cellEvidence.neighborCell
        $geometryTolerance = 2.0
        if ([int]$cellEvidence.row -ne [int]$adjacentCell.row -or [int]$adjacentCell.column -ne ([int]$cellEvidence.column + 1)) {
            throw 'The semantic image and adjacent content did not resolve to neighboring cells in the same table row.'
        }
        if ($imageRect.x -lt ($imageCell.x-$geometryTolerance) -or ($imageRect.x+$imageRect.width) -gt ($imageCell.x+$imageCell.width+$geometryTolerance) -or
            $imageRect.y -lt ($imageCell.y-$geometryTolerance) -or ($imageRect.y+$imageRect.height) -gt ($imageCell.y+$imageCell.height+$geometryTolerance)) {
            throw 'The wide SVG image peer extends outside its own UIA GridItem cell bounds.'
        }
        if (($imageCell.x+$imageCell.width) -gt ($adjacentCell.x+$geometryTolerance) -or ($imageRect.x+$imageRect.width) -gt ($adjacentCell.x+$geometryTolerance)) {
            throw 'The image cell or SVG crosses into the adjacent table cell bounds.'
        }
        if (@($cellEvidence.neighborTextRectangles).Count -eq 0 -or @($cellEvidence.neighborTextRectangles | Where-Object {
            $_.x -lt ($adjacentCell.x-$geometryTolerance) -or ($_.x+$_.width) -gt ($adjacentCell.x+$adjacentCell.width+$geometryTolerance) -or
            $_.y -lt ($adjacentCell.y-$geometryTolerance) -or ($_.y+$_.height) -gt ($adjacentCell.y+$adjacentCell.height+$geometryTolerance)
        }).Count -gt 0) { throw 'Adjacent-cell TextPattern geometry is missing or escapes its UIA GridItem bounds.' }
        $expectedAspectRatio = 1600.0 / 220.0
        $actualAspectRatio = [double]$imageRect.width / [double]$imageRect.height
        $expectedPixelHeight = [double]$imageRect.width / $expectedAspectRatio
        $heightRoundingError = [Math]::Abs([double]$imageRect.height-$expectedPixelHeight)
        if ($heightRoundingError -gt 1.0) { throw "The wide SVG pixel height $($imageRect.height) differs from aspect-preserving height $expectedPixelHeight by more than one physical pixel." }
        $shot = Save-Screenshot 'screenshots\svg-table-narrow.png'
        $null = Invoke-GuestProbe -Mode 'resize' -Left ([int]$meta.window.left) -Top ([int]$meta.window.top) -Width ([int][Math]::Round($before.width)) -Height ([int][Math]::Round($before.height))
        return [pscustomobject]@{localSvg=$image.name;wideImage=@{name=$wide.name;bounds=$imageRect;aspectRatio=$actualAspectRatio};imageCell=$imageCell;adjacentCell=$adjacentCell;neighbor=@{name=$neighbor.name;x=$neighbor.x;width=$neighbor.width};narrowRendererWidth=$renderer[2];screenshot=$shot}
    }

    Test-Case -Id 'C05' -Name 'html-inert-script-and-formatting-content' -Requirement 'Supported safe HTML content stays ordered/selectable and script-bearing content is inert literal text.' -Body {
        Navigate-Sample -Key 'Html' -RequiredText 'Native safe HTML'
        $actual = Invoke-UiValue 'MarkdownRenderer'
        foreach ($phrase in @('strong text','emphasis','marked text','Native blockquote','Native HTML table','unsafe-script-sentinel','Frames and forms')) {
            if ($actual.IndexOf($phrase,[System.StringComparison]::OrdinalIgnoreCase) -lt 0) { throw "HTML content omitted '$phrase'." }
        }
        if ($actual.IndexOf('strong text',[System.StringComparison]::OrdinalIgnoreCase) -gt $actual.IndexOf('Native blockquote',[System.StringComparison]::OrdinalIgnoreCase)) { throw 'HTML block and inline text order changed.' }
        return [pscustomobject]@{orderedText=$true;scriptSentinelVisible=$true;screenshot=(Save-Screenshot 'screenshots\html-inert-script-and-formatting.png')}
    }

    Test-Case -Id 'P1-001' -Name 'superscript-and-subscript-have-correct-independent-baselines' -Requirement 'Ordinary safe HTML superscript and subscript phrases are positioned above/below their neighboring regular text, measured from TextPattern geometry.' -Body {
        Navigate-Sample -Key 'Html' -RequiredText 'Native safe HTML'
        $source = "# Script geometry regression`n`nbeforeSup <sup>raisedMarker</sup> afterSup`n`nbeforeSub <sub>loweredMarker</sub> afterSub`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'raisedMarker'
        $before = Get-RangeCenter 'beforeSup'
        $raised = Get-RangeCenter 'raisedMarker'
        $after = Get-RangeCenter 'afterSup'
        $beforeSub = Get-RangeCenter 'beforeSub'
        $subscript = Get-RangeCenter 'loweredMarker'
        $afterSubscript = Get-RangeCenter 'afterSub'
        if ([Math]::Abs($before.y-$after.y) -gt 1.0) { throw "Superscript baseline peers are not on one visual row (before=$($before.y), after=$($after.y))." }
        if ([Math]::Abs($beforeSub.y-$afterSubscript.y) -gt 1.0) { throw "Subscript baseline peers are not on one visual row (before=$($beforeSub.y), after=$($afterSubscript.y))." }
        $regularBaseline = ($before.y + $after.y) / 2.0
        $subscriptBaseline = ($beforeSub.y + $afterSubscript.y) / 2.0
        if ($raised.y -ge ($regularBaseline - 1.0)) { throw "Superscript center y=$($raised.y) is not visibly above neighboring regular baseline y=$regularBaseline." }
        if ($subscript.y -le ($subscriptBaseline + 1.0)) { throw "Subscript center y=$($subscript.y) is not visibly below neighboring regular baseline y=$subscriptBaseline." }
        return [pscustomobject]@{before=$before;raised=$raised;after=$after;raisedBaselineDipDelta=($regularBaseline-$raised.y);beforeSubscript=$beforeSub;lowered=$subscript;afterSubscript=$afterSubscript;subscriptBaselineDipDelta=($subscript.y-$subscriptBaseline);screenshot=(Save-Screenshot 'screenshots\html-script-geometry.png')}
    }

    Test-Case -Id 'C13' -Name 'owned-and-ancestor-viewports-scroll-without-selecting' -Requirement 'One-finger swipe changes the owning ScrollPattern and does not create selection in both viewport ownership modes.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled $false
        foreach ($mode in @('owned','ancestor')) {
            if ($mode -eq 'ancestor') { Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled $true; Start-Sleep -Milliseconds 250 }
            $null = Invoke-Ui @('scroll','MarkdownRenderer','--to','top')
            $before = Invoke-GuestProbe -Mode 'scroll'
            if (@($before.scrollers).Count -eq 0) { throw "No UIA ScrollPattern was available in $mode mode." }
            $beforePercent = [double](@($before.scrollers | Sort-Object verticalPercent -Descending | Select-Object -First 1)[0].verticalPercent)
            $renderer = Get-UiBounds 'MarkdownRenderer'
            $point = "{0},{1}" -f [int]($renderer[0]+$renderer[2]*.65), [int]($renderer[1]+$renderer[3]*.65)
            $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','swipe','--direction','up','--distance','320','--duration-ms','300','--at',$point)
            Start-Sleep -Milliseconds 250
            $after = Invoke-GuestProbe -Mode 'scroll'
            if (@($after.scrollers).Count -eq 0) { throw "No UIA ScrollPattern remained after swipe in $mode mode." }
            $afterPercent = [double](@($after.scrollers | Sort-Object verticalPercent -Descending | Select-Object -First 1)[0].verticalPercent)
            if ($afterPercent -le $beforePercent) { throw "The $mode viewport did not scroll (before=$beforePercent, after=$afterPercent)." }
            if ((Get-SelectedText).Length -gt 0) { throw "A pan created text selection in $mode mode." }
            $shot = Save-Screenshot "screenshots\touch-pan-$mode.png"
            if ($mode -eq 'owned') { $owned = @{before=$beforePercent;after=$afterPercent;screenshot=$shot} }
            else { $ancestor = @{before=$beforePercent;after=$afterPercent;screenshot=$shot} }
        }
        return [pscustomobject]@{owned=$owned;ancestor=$ancestor}
    }

    Test-Case -Id 'C09' -Name 'mouse-selection-crosses-lines-formatting-lists-and-table-and-keyboard-adjusts-handle-endpoint' -Requirement 'Mouse selection crosses lines and formatting, UIA exposes exact text through list items and table cells, keyboard Copy matches it as plain and rich HTML, Ctrl+A selects all, Escape clears it, and Left/Right keys routed to the focused selection-end handle move its exact text endpoint forward and back.' -Body {
        Navigate-Sample -Key 'Selection' -RequiredText 'DOM-Style Text Selection'
        $markdown = "# Selection regression`n`nalpha **bravo** charlie`ndelta epsilon foxtrot`n"
        Set-TemporaryMarkdown -Markdown $markdown -RequiredText 'alpha'
        $start = Get-RangeEdgePoint 'alpha' -Endpoint Start
        $end = Get-RangeEdgePoint 'epsilon' -Endpoint End
        $null = Invoke-Ui @('drag',"$($start.x),$($start.y)","$($end.x),$($end.y)",'--hold-ms','80')
        Start-Sleep -Milliseconds 120
        $selected = Get-SelectedText
        if (-not $selected.Contains('alpha') -or -not $selected.Contains('epsilon') -or -not $selected.Contains('bravo')) { throw "Mouse drag did not select across the intended lines/formatting: '$selected'." }
        $null = Invoke-Ui @('send-keys','ctrl+c','--via','send-input')
        Start-Sleep -Milliseconds 120
        $clipboard = Invoke-GuestProbe -Mode 'clipboard'
        if ($clipboard.plain -ne $clipboard.selectedText -or $clipboard.plain -ne $selected) { throw 'Clipboard plain text differs from UIA TextPattern selection.' }
        if (-not $clipboard.hasHtml -or $clipboard.html -notmatch 'StartFragment:|<html') { throw 'Copy did not publish a rich CF_HTML payload.' }
        $null = Invoke-Ui @('send-keys','ctrl+a','--via','send-input')
        $allText = Get-SelectedText
        if (-not $allText.Contains('Selection regression') -or -not $allText.Contains('foxtrot')) { throw 'Ctrl+A did not select the full rendered document.' }
        $null = Invoke-Ui @('send-keys','esc','--via','send-input')
        if ((Get-SelectedText).Length -ne 0) { throw 'Escape did not clear the active selection.' }
        $blocks = "# List and table selection regression`n`n- ListStart alpha **listBold** omega`n- ListEnd item`n`n| TableStart | Adjacent |`n|---|---|`n| TableBold **cellBold** | TableEnd |`n"
        Set-TemporaryMarkdown -Markdown $blocks -RequiredText 'ListStart'
        $expectedBlocks = Invoke-GuestProbe -Mode 'range-between' -StartNeedle 'ListStart' -EndNeedle 'TableEnd'
        foreach ($anchor in @('ListStart','listBold','ListEnd','TableStart','TableBold','cellBold','TableEnd')) {
            if ($expectedBlocks.text.IndexOf($anchor,[System.StringComparison]::Ordinal) -lt 0) { throw "The independently resolved cross-block range omitted '$anchor'." }
        }
        $blockStart = Get-RangeCenter 'ListStart'
        $blockEnd = Get-RangeCenter 'TableEnd'
        $from = "{0},{1}" -f [int][Math]::Floor($blockStart.rectangle.x+1.0),[int][Math]::Floor($blockStart.rectangle.y+($blockStart.rectangle.height/2.0))
        $to = "{0},{1}" -f [int][Math]::Ceiling($blockEnd.rectangle.x+$blockEnd.rectangle.width-1.0),[int][Math]::Floor($blockEnd.rectangle.y+($blockEnd.rectangle.height/2.0))
        $null = Invoke-Ui @('drag',$from,$to,'--hold-ms','80')
        $selectedBlocks = Get-SelectedText
        if ($selectedBlocks -cne $expectedBlocks.text) { throw "Mouse selection across list/table blocks was '$selectedBlocks'; expected exact UIA range '$($expectedBlocks.text)'." }
        $blockShot = Save-Screenshot 'screenshots\selection-list-table-crossing.png'
        $null = Invoke-Ui @('send-keys','esc','--via','send-input')
        if ((Get-SelectedText).Length -ne 0) { throw 'Escape did not clear the cross-block selection before the keyboard endpoint case.' }
        $keyboardMarkdown = [string]::Join([Environment]::NewLine,@('# Keyboard endpoint regression','','Prefix keyboardTarget, suffix.')) + [Environment]::NewLine
        Set-TemporaryMarkdown -Markdown $keyboardMarkdown -RequiredText 'keyboardTarget'
        $keyboardTarget = Get-RangeCenter 'keyboardTarget'
        $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($keyboardTarget.x),$($keyboardTarget.y)")
        $keyboardDeadline = [DateTime]::UtcNow.AddSeconds(4)
        $initialKeyboardSelection = ''
        do {
            Start-Sleep -Milliseconds 60
            $initialKeyboardSelection = Get-SelectedText
        } while ($initialKeyboardSelection -cne 'keyboardTarget' -and [DateTime]::UtcNow -lt $keyboardDeadline)
        if ($initialKeyboardSelection -cne 'keyboardTarget') { throw "Touch double-tap did not establish the exact keyboard target selection: '$initialKeyboardSelection'." }
        $handleBefore = Assert-SelectionHandles
        $null = Invoke-Ui @('focus','MarkdownSelectionEndHandle')
        $focusDeadline = [DateTime]::UtcNow.AddSeconds(2)
        $handleFocus = Invoke-GuestProbe -Mode 'focus-state' -AutomationId 'MarkdownSelectionEndHandle'
        while (-not [bool]$handleFocus.hasKeyboardFocus -and [DateTime]::UtcNow -lt $focusDeadline) {
            Start-Sleep -Milliseconds 50
            $handleFocus = Invoke-GuestProbe -Mode 'focus-state' -AutomationId 'MarkdownSelectionEndHandle'
        }
        if (-not [bool]$handleFocus.hasKeyboardFocus -or -not [bool]$handleFocus.isKeyboardFocusable -or
            -not [bool]$handleFocus.isEnabled -or [bool]$handleFocus.isOffscreen) {
            throw 'The end-selection handle did not acquire verified keyboard focus before endpoint adjustment.'
        }
        $null = Invoke-Ui @('send-keys','right','--via','send-input')
        $extendedSelection = ''
        $keyboardDeadline = [DateTime]::UtcNow.AddSeconds(2)
        do {
            Start-Sleep -Milliseconds 50
            $extendedSelection = Get-SelectedText
        } while ($extendedSelection -cne 'keyboardTarget,' -and [DateTime]::UtcNow -lt $keyboardDeadline)
        if ($extendedSelection -cne 'keyboardTarget,') { throw "Right on the focused selection-end handle did not extend the exact range through its next punctuation boundary: '$extendedSelection'." }
        $handleAfterRight = Assert-SelectionHandles
        $null = Invoke-Ui @('send-keys','left','--via','send-input')
        $contractedSelection = ''
        $keyboardDeadline = [DateTime]::UtcNow.AddSeconds(2)
        do {
            Start-Sleep -Milliseconds 50
            $contractedSelection = Get-SelectedText
        } while ($contractedSelection -cne 'keyboardTarget' -and [DateTime]::UtcNow -lt $keyboardDeadline)
        if ($contractedSelection -cne 'keyboardTarget') { throw "Left on the focused selection-end handle did not restore the original exact selection endpoint: '$contractedSelection'." }
        $handleAfterLeft = Assert-SelectionHandles
        $keyboardShot = Save-Screenshot 'screenshots\selection-keyboard-endpoint-adjustment.png'
        return [pscustomobject]@{mouseSelection=$selected;clipboardMatches=$true;cfHtml=$clipboard.hasHtml;selectAllLength=$allText.Length;escapeCleared=$true;blockSelection=$selectedBlocks;expectedBlockSelection=$expectedBlocks.text;blockStart=$from;blockEnd=$to;blockScreenshot=$blockShot;keyboardEndpoint=@{inputPath='keyboard-focused MarkdownSelectionEndHandle';verifiedFocus=$handleFocus;before=$handleBefore;afterRight=$handleAfterRight;afterLeft=$handleAfterLeft;initial=$initialKeyboardSelection;afterRightSelection=$extendedSelection;afterLeftSelection=$contractedSelection;screenshot=$keyboardShot}}
    }

    Test-Case -Id 'C08' -Name 'copy-as-markdown-preserves-source-for-entire-document' -Requirement 'The target-aware Copy as Markdown command copies the original checked-in selection fixture source, not rendered-only text.' -Body {
        Navigate-Sample -Key 'Selection' -RequiredText 'DOM-Style Text Selection'
        $source = Invoke-UiValue 'MarkdownEditor'
        $null = Invoke-Ui @('focus','MarkdownRenderer')
        $null = Invoke-Ui @('send-keys','ctrl+a','--via','send-input')
        $selected = Get-SelectedText
        if ($selected.Length -lt 200 -or -not $selected.Contains('DOM-Style Text Selection')) { throw 'Ctrl+A did not establish the full sample selection.' }
        $bounds = Get-UiBounds 'MarkdownRenderer'
        $selection = Invoke-GuestProbe -Mode 'selection'
        $selectedRects = @($selection.ranges | ForEach-Object { $_.rectangles } | Where-Object { $_.width -gt 0 -and $_.height -gt 0 })
        if ($selectedRects.Count -eq 0) { throw 'Full-document selection has no range geometry for the target-aware context menu.' }
        $targetRect = $selectedRects[0]
        $contextPoint = "{0},{1}" -f [int][Math]::Floor($targetRect.x+$targetRect.width/2.0),[int][Math]::Floor($targetRect.y+$targetRect.height/2.0)
        $null = Invoke-Ui @('drag',$contextPoint,$contextPoint,'--right','--hold-ms','40')
        $null = Invoke-Ui @('wait-for','MarkdownContextCopyMarkdown','-t','3000')
        $null = Invoke-Ui @('invoke','MarkdownContextCopyMarkdown')
        Start-Sleep -Milliseconds 150
        $clipboard = Invoke-GuestProbe -Mode 'clipboard'
        if ($clipboard.plain -ne $source) { throw 'Copy as Markdown did not reproduce the exact original source for full-document selection.' }
        return [pscustomobject]@{sourceCharacters=$source.Length;clipboardCharacters=$clipboard.plain.Length;exactSource=$true}
    }

    Test-Case -Id 'C08' -Name 'copy-as-markdown-preserves-only-partially-selected-source-syntax' -Requirement 'Copy as Markdown for a partial semantic range returns exactly the selected source slice, including inline emphasis and code delimiters but excluding unselected prefix and suffix.' -Body {
        Navigate-Sample -Key 'Selection' -RequiredText 'DOM-Style Text Selection'
        $source = "# Partial source copy regression`n`nPrefix **boldTarget** gap ``codeTarget`` suffix.`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'boldTarget'
        $expectedSource = '**boldTarget** gap `codeTarget`'
        $expectedRendered = 'boldTarget gap codeTarget'
        $range = Invoke-GuestProbe -Mode 'range-between' -StartNeedle 'boldTarget' -EndNeedle 'codeTarget' -SelectRange
        if ($range.text -cne $expectedRendered) { throw "The independent TextPattern range resolved to '$($range.text)', expected '$expectedRendered'." }
        if (-not [bool]$range.selectionRequested -or [string]$range.actualSelection -cne $expectedRendered -or (Get-SelectedText) -cne $expectedRendered) {
            throw 'The exact partial semantic selection was not committed through TextPattern.'
        }
        if (@($range.rectangles).Count -eq 0) { throw 'The partial selection has no independent UIA geometry for its target-aware menu.' }
        $firstRect = $range.rectangles[0]
        $menuPoint = "{0},{1}" -f [int][Math]::Floor($firstRect.x+($firstRect.width/2.0)),[int][Math]::Floor($firstRect.y+($firstRect.height/2.0))
        $null = Invoke-Ui @('drag',$menuPoint,$menuPoint,'--right','--hold-ms','40')
        $null = Invoke-Ui @('wait-for','MarkdownContextCopyMarkdown','-t','3000')
        if ((Get-SelectedText) -cne $expectedRendered) { throw 'Opening the partial-selection context menu changed the selected range.' }
        $null = Invoke-Ui @('invoke','MarkdownContextCopyMarkdown')
        Start-Sleep -Milliseconds 120
        $clipboard = Invoke-GuestProbe -Mode 'clipboard'
        if ([string]$clipboard.plain -cne $expectedSource -or [string]$clipboard.selectedText -cne $expectedRendered) {
            throw "Partial Copy as Markdown returned '$($clipboard.plain)' instead of exactly '$expectedSource'."
        }
        $shot = Save-Screenshot 'screenshots\copy-markdown-partial.png'
        return [pscustomobject]@{selectedRendered=$range.text;expectedMarkdown=$expectedSource;clipboard=$clipboard.plain;unselectedPrefixAbsent=($clipboard.plain -notmatch '^Prefix');unselectedSuffixAbsent=($clipboard.plain -notmatch 'suffix\.$');selectionRectangles=@($range.rectangles);screenshot=$shot}
    }

    Test-Case -Id 'C04' -Name 'code-copy-button-copies-only-the-fenced-source' -Requirement 'The accessible native Copy code button copies the corresponding code block exactly without creating or changing document text selection.' -Body {
        Navigate-Sample -Key 'Code' -RequiredText 'Fenced Code Blocks'
        $null = Invoke-GuestProbe -Mode 'scroll-range' -Needle 'Fenced Code Blocks'
        $null = Invoke-Ui @('send-keys','esc','--via','send-input')
        $beforeSelection = Get-SelectedText
        if ($beforeSelection.Length -ne 0) { throw 'The code-copy case did not begin with a cleared document selection.' }
        $buttons = @(Get-UiElements | Where-Object { $_.className -eq 'MarkdownCodeCopyButton' -and $_.type -eq 'Button' })
        if ($buttons.Count -lt 1) { throw 'The Code page exposed no accessible native Copy code button.' }
        $button = $buttons[0]
        if ($button.isOffscreen -or $button.width -le 0 -or $button.height -le 0) { throw 'The first code-copy button is not visible after scrolling its semantic heading into view.' }
        $null = Invoke-Ui @('invoke',$button.automationId)
        Start-Sleep -Milliseconds 100
        $clipboard = Invoke-GuestProbe -Mode 'clipboard'
        $expectedCode = @'
public sealed class MarkdownRendererControl : UserControl
{
    private volatile LayoutSnapshot? _snapshot;

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        var device = CanvasDevice.GetSharedDevice();
        var ctx = new MarkdownLayoutContext(device, themeSnapshot, sourceMap, registry, FlowDirection);
        _snapshot = await Task.Run(() => builder.Build(doc, width), ct);
        _canvas.Invalidate();
    }
}
'@
        $actualCode = ([string]$clipboard.plain) -replace "`r`n", "`n" -replace "`r", "`n"
        $expected = ([string]$expectedCode) -replace "`r`n", "`n" -replace "`r", "`n"
        if ($actualCode.TrimEnd("`n") -cne $expected.TrimEnd("`n")) { throw 'The code-copy clipboard differs from the exact first fenced code block.' }
        if ((Get-SelectedText).Length -ne 0) { throw 'Copy code created document text selection.' }
        return [pscustomobject]@{buttonAutomationId=$button.automationId;copiedCharacters=$actualCode.Length;exactCode=$true;selectionUnaffected=$true}
    }

    Test-Case -Id 'P1-002' -Name 'touch-long-press-selects-whole-emoji-and-symbol-graphemes' -Requirement 'Touching emoji/symbols selects exactly the targeted grapheme, never a neighboring word, with complete UTF-16 cluster and handles.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        $source = "# Emoji touch regression`n`na 😀 b`nstandalone 😇`nZWJ: 👩🏽‍💻`nsymbol ∑`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'a 😀 b'
        $observed = [System.Collections.Generic.List[object]]::new()
        foreach ($grapheme in @('😀','😇','👩🏽‍💻','∑')) {
            $center = Get-RangeCenter $grapheme
            $point = "$($center.x),$($center.y)"
            $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','long-press','--hold-ms','650','--at',$point)
            $deadline = [DateTime]::UtcNow.AddSeconds(4)
            $selected = ''
            do { Start-Sleep -Milliseconds 100; $selected = Get-SelectedText } while ($selected -ne $grapheme -and [DateTime]::UtcNow -lt $deadline)
            if ($selected -cne $grapheme) { throw "Long-press '$grapheme' selected '$selected' instead of the exact grapheme." }
            $handles = Assert-SelectionHandles
        $commands = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') })
            if ($commands.Count -ne 3) { throw "Long-press '$grapheme' did not expose the native selection menu." }
            $observed.Add(@{grapheme=$grapheme;selected=$selected;utf16Length=$selected.Length;handles=$handles.Count;menuCommands=$commands.Count})
            $null = Invoke-Ui @('send-keys','esc','--via','send-input')
        }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P1-016' -Name 'touch-word-selection-does-not-merge-adjacent-keycap-emoji' -Requirement 'Real double-tap and long-press gestures on each adjacent word and the keycap grapheme in `wordleft1️⃣wordright` select exactly one independently authored UIA range, copy only that literal text, and behave the same in both viewport ownership modes.' -Body {
        $source = "# Emoji word-boundary regression`n`nwordleft1️⃣wordright`n"
        $targets = @(
            [pscustomobject]@{name='left-word';needle='wordleft';expected='wordleft'},
            [pscustomobject]@{name='keycap';needle='1️⃣';expected='1️⃣'},
            [pscustomobject]@{name='right-word';needle='wordright';expected='wordright'}
        )
        $gestures = @('double-tap','long-press')
        $evidence = [System.Collections.Generic.List[object]]::new()
        try {
        foreach ($viewport in @('owned','ancestor')) {
            Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($viewport -eq 'ancestor')
            Set-TemporaryMarkdown -Markdown $source -RequiredText 'wordleft1️⃣wordright'

            foreach ($targetCase in $targets) {
                foreach ($gesture in $gestures) {
                    # A prior long-press can leave its native flyout open; one
                    # Escape dismisses that flyout, a second after renderer
                    # focus clears the committed selection and handle geometry.
                    $priorMenu = @(Get-AppUiElements | Where-Object {
                        (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')
                    })
                    if ($priorMenu.Count -gt 0) {
                        $null = Invoke-Ui @('send-keys','esc','--via','send-input')
                        $null = Invoke-Ui @('wait-for','MarkdownContextCopy','--gone','-t','2000')
                    }
                    if ((Get-SelectedText).Length -gt 0) {
                        $null = Invoke-Ui @('focus','MarkdownRenderer')
                        $null = Invoke-Ui @('send-keys','esc','--via','send-input')
                    }
                    $clearDeadline = [DateTime]::UtcNow.AddSeconds(2)
                    do {
                        Start-Sleep -Milliseconds 60
                        $clearedText = Get-SelectedText
                    } while ($clearedText.Length -gt 0 -and [DateTime]::UtcNow -lt $clearDeadline)
                    $clearedHandles = @((Get-SelectionHandleState).handles)
                    $clearedMenu = @(Get-AppUiElements | Where-Object {
                        (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')
                    })
                    if ($clearedText.Length -gt 0 -or $clearedHandles.Count -gt 0 -or $clearedMenu.Count -gt 0) {
                        throw "Could not clear the previous touch selection/menu before $viewport/$($targetCase.name)/$gesture."
                    }

                    $target = Get-RangeCenter ([string]$targetCase.needle)
                    $targetRange = Invoke-GuestProbe -Mode 'range' -Needle ([string]$targetCase.needle)
                    if ([string]$targetRange.text -cne [string]$targetCase.expected -or @($targetRange.rectangles).Count -ne 1) {
                        throw "The independently located $($targetCase.name) target is not exactly one UIA range for its authored literal."
                    }
                    $gestureArguments = @('touch','MarkdownRenderer','--gesture',$gesture)
                    if ($gesture -eq 'long-press') { $gestureArguments += @('--hold-ms','650') }
                    $gestureArguments += @('--at',"$($target.x),$($target.y)")
                    try {
                    $null = Invoke-Ui $gestureArguments

                    $deadline = [DateTime]::UtcNow.AddSeconds(4)
                    $selected = ''
                    do {
                        Start-Sleep -Milliseconds 80
                        $selected = Get-SelectedText
                    } while ($selected -cne [string]$targetCase.expected -and [DateTime]::UtcNow -lt $deadline)
                    if ($selected -cne [string]$targetCase.expected) {
                        throw "$viewport/$($targetCase.name)/$gesture selected '$selected', expected only '$($targetCase.expected)'."
                    }

                    $selection = Invoke-GuestProbe -Mode 'selection'
                    if ([int]$selection.rangeCount -ne 1 -or [string]$selection.text -cne [string]$targetCase.expected -or
                        [string]$selection.ranges[0].text -cne [string]$targetCase.expected) {
                        throw "$viewport/$($targetCase.name)/$gesture did not expose exactly one UIA text range equal to its independently authored expected literal."
                    }
                    $selectedRects = @($selection.ranges[0].rectangles | Where-Object { $_.width -gt 0 -and $_.height -gt 0 })
                    $targetRect = $target.rectangle
                    $targetIntersections = @($selectedRects | Where-Object {
                        $_.x -lt ($targetRect.x+$targetRect.width) -and ($_.x+$_.width) -gt $targetRect.x -and
                        $_.y -lt ($targetRect.y+$targetRect.height) -and ($_.y+$_.height) -gt $targetRect.y
                    })
                    if ($targetIntersections.Count -eq 0) { throw "$viewport/$($targetCase.name)/$gesture selected-range geometry does not intersect the exact UIA target range." }
                    $handles = @(Assert-SelectionHandles)
                    if ($handles.Count -ne 2) { throw "$viewport/$($targetCase.name)/$gesture did not expose exactly two selection handles." }
                    $shot = Save-Screenshot "screenshots\p1-016-$viewport-$($targetCase.name)-$gesture.png"

                    $menu = @(Get-AppUiElements | Where-Object {
                        (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')
                    })
                    if ($menu.Count -eq 0) {
                        $point = "{0},{1}" -f $target.x,$target.y
                        $null = Invoke-Ui @('drag',$point,$point,'--right','--hold-ms','40')
                        $null = Invoke-Ui @('wait-for','MarkdownContextCopy','-t','2500')
                        $menu = @(Get-AppUiElements | Where-Object {
                            (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')
                        })
                    }
                    if ($menu.Count -ne 3) { throw "$viewport/$($targetCase.name)/$gesture did not expose all native selection Copy commands." }
                    $selectionBeforeCopy = Invoke-GuestProbe -Mode 'selection'
                    if ([int]$selectionBeforeCopy.rangeCount -ne 1 -or [string]$selectionBeforeCopy.text -cne [string]$targetCase.expected) {
                        throw "$viewport/$($targetCase.name)/$gesture changed the exact range while opening the Copy menu."
                    }
                    $null = Invoke-Ui @('invoke','MarkdownContextCopy')
                    $null = Invoke-Ui @('wait-for','MarkdownContextCopy','--gone','-t','2000')
                    $clipboard = Invoke-GuestProbe -Mode 'clipboard'
                    if ([string]$clipboard.plain -cne [string]$targetCase.expected -or
                        [string]$clipboard.selectedText -cne [string]$targetCase.expected) {
                        throw "$viewport/$($targetCase.name)/$gesture plain clipboard was '$($clipboard.plain)', expected only '$($targetCase.expected)'."
                    }

                    $evidence.Add([pscustomobject]@{
                        viewport=$viewport;target=$targetCase.name;gesture=$gesture;expected=$targetCase.expected
                        selected=$selection.text;rangeCount=$selection.rangeCount;targetRectangle=$targetRect
                        selectedRectangles=$selectedRects;handleCount=$handles.Count;clipboardPlain=$clipboard.plain
                        screenshot=$shot
                    })
                    }
                    finally {
                    $remainingMenu = @(Get-AppUiElements | Where-Object {
                        (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')
                    })
                    if ($remainingMenu.Count -gt 0) {
                        $null = Invoke-Ui @('send-keys','esc','--via','send-input')
                        $null = Invoke-Ui @('wait-for','MarkdownContextCopy','--gone','-t','2000')
                    }
                    $null = Invoke-Ui @('focus','MarkdownRenderer')
                    $null = Invoke-Ui @('send-keys','esc','--via','send-input')
                    $clearDeadline = [DateTime]::UtcNow.AddSeconds(2)
                    do {
                        Start-Sleep -Milliseconds 60
                        $clearedText = Get-SelectedText
                    } while ($clearedText.Length -gt 0 -and [DateTime]::UtcNow -lt $clearDeadline)
                    $clearedHandles = @((Get-SelectionHandleState).handles)
                    $clearedMenu = @(Get-AppUiElements | Where-Object {
                        (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')
                    })
                    if ($clearedText.Length -gt 0 -or $clearedHandles.Count -gt 0 -or $clearedMenu.Count -gt 0) {
                        throw "Could not clear $viewport/$($targetCase.name)/$gesture selection/menu before the next touch contact."
                    }
                    }
                }
            }
        }
        }
        finally {
            Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled $false
        }
        return @($evidence.ToArray())
    }

    Test-Case -Id 'C11' -Name 'touch-selection-preserves-rtl-logical-text-and-combining-graphemes' -Requirement 'A touch-selected Arabic word keeps logical Unicode order in mixed-direction context, and a decomposed combining-mark word is selected without splitting or normalizing its grapheme.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        $combining = 'cafe' + [char]0x0301
        $rtlMarkdown = "# RTL selection regression`n`nEnglish before العربية שלום English after.`n"
        $combiningMarkdown = "# Combining selection regression`n`nDecomposed target $combining appears alone on this line.`n"
        Set-TemporaryMarkdown -Markdown $rtlMarkdown -RequiredText 'العربية'
        $cases = @(
            @{ name='rtl';needle='العربية';expected='العربية' },
            @{ name='combining';needle=$combining;expected=$combining }
        )
        $renderer = Get-UiBounds 'MarkdownRenderer'
        $evidence = [System.Collections.Generic.List[object]]::new()
        foreach ($case in $cases) {
            if ($case.name -eq 'combining') {
                $beforeReplace = Get-SelectedText
                if ($beforeReplace.Length -gt 0) { throw 'The previous RTL selection was not cleared before replacing the document for the combining-mark case.' }
                $oldHandles = Invoke-GuestProbe -Mode 'handles'
                if (@($oldHandles.handles).Count -ne 0) { throw 'RTL selection handles remain after Escape; refusing to place the combining fixture under a live handle.' }
                $oldMenu = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') })
                if ($oldMenu.Count -gt 0) { throw 'RTL selection menu remains after Escape; refusing to overlap the combining fixture with it.' }
                Set-TemporaryMarkdown -Markdown $combiningMarkdown -RequiredText $combining
            }
            $target = Get-RangeCenter ([string]$case.needle)
            if ($case.name -eq 'combining') {
                $priorHandles = @((Get-SelectionHandleState).handles)
                if ($priorHandles.Count -gt 0) {
                    foreach ($handle in $priorHandles) {
                        $r = $target.rectangle
                        if ($handle.x -lt ($r.x+$r.width) -and ($handle.x+$handle.width) -gt $r.x -and $handle.y -lt ($r.y+$r.height) -and ($handle.y+$handle.height) -gt $r.y) {
                            throw 'Combining-mark target overlaps a pre-existing selection handle.'
                        }
                    }
                }
            }
            $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','long-press','--hold-ms','650','--at',"$($target.x),$($target.y)")
            $deadline = [DateTime]::UtcNow.AddSeconds(4)
            $selectedText = ''
            do { Start-Sleep -Milliseconds 100; $selectedText = Get-SelectedText } while ($selectedText -cne [string]$case.expected -and [DateTime]::UtcNow -lt $deadline)
            if ($selectedText -cne [string]$case.expected) { throw "$($case.name) touch selection returned '$selectedText', expected exact logical text '$($case.expected)'." }
            $null = Assert-SelectionHandles
            $selection = Invoke-GuestProbe -Mode 'selection'
            if ([int]$selection.rangeCount -ne 1 -or [string]$selection.text -cne [string]$case.expected) { throw "$($case.name) UIA TextPattern selection does not expose the exact single logical range." }
            $selectedRects = @($selection.ranges[0].rectangles)
            if ($selectedRects.Count -eq 0) { throw "$($case.name) selected range has no UIA geometry." }
            $targetRect = $target.rectangle
            $overlapsTarget = @($selectedRects | Where-Object { $_.x -lt ($targetRect.x+$targetRect.width) -and ($_.x+$_.width) -gt $targetRect.x -and $_.y -lt ($targetRect.y+$targetRect.height) -and ($_.y+$_.height) -gt $targetRect.y })
            if ($overlapsTarget.Count -eq 0) { throw "$($case.name) selected TextPattern geometry does not overlap the independently found target range." }
            foreach ($rect in $selectedRects) {
                if ($rect.x -lt ($renderer[0]-1) -or $rect.y -lt ($renderer[1]-1) -or ($rect.x+$rect.width) -gt ($renderer[0]+$renderer[2]+1) -or ($rect.y+$rect.height) -gt ($renderer[1]+$renderer[3]+1)) {
                    throw "$($case.name) selected range geometry escapes the renderer bounds."
                }
            }
            $shot = Save-Screenshot "screenshots\c11-$($case.name)-selection.png"
            $evidence.Add(@{case=$case.name;expected=$case.expected;selected=$selection.text;utf16Length=([string]$selection.text).Length;targetRect=$targetRect;selectedRectangles=$selectedRects;screenshot=$shot})
            # The first Escape is consumed by an open native flyout and must
            # preserve the committed range; close it before clearing selection.
            $openMenu = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') })
            if ($openMenu.Count -gt 0) {
                $null = Invoke-Ui @('send-keys','esc','--via','send-input')
                $null = Invoke-Ui @('wait-for','MarkdownContextCopy','--gone','-t','2000')
            }
            $null = Invoke-Ui @('focus','MarkdownRenderer')
            $null = Invoke-Ui @('send-keys','esc','--via','send-input')
            $clearDeadline = [DateTime]::UtcNow.AddSeconds(2)
            do {
                Start-Sleep -Milliseconds 60
                $clearedSelection = Get-SelectedText
            } while ($clearedSelection.Length -gt 0 -and [DateTime]::UtcNow -lt $clearDeadline)
            $clearedHandles = Invoke-GuestProbe -Mode 'handles'
            $clearedMenu = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') })
            if ($clearedSelection.Length -gt 0 -or @($clearedHandles.handles).Count -gt 0 -or $clearedMenu.Count -gt 0) {
                throw "$($case.name) selection/menu state was not cleared before the next Unicode fixture."
            }
        }
        return @($evidence.ToArray())
    }

    Test-Case -Id 'P1-003' -Name 'canceled-unrelated-contact-preserves-committed-selection' -Requirement 'A prior word selection and handles survive a later ordinary contact canceled with POINTER_FLAG_CANCELED; only transient contact state is released.' -Body {
        $source = "# Contact cancellation regression`n`nCommitted selectiontarget remains selected.`nUnrelated ordinary contact target.`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'selectiontarget'
        $selectionPoint = Get-RangeCenter 'selectiontarget'
        $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($selectionPoint.x),$($selectionPoint.y)")
        $deadline = [DateTime]::UtcNow.AddSeconds(4)
        $selected = ''
        do { Start-Sleep -Milliseconds 100; $selected = Get-SelectedText } while ($selected -ne 'selectiontarget' -and [DateTime]::UtcNow -lt $deadline)
        if ($selected -cne 'selectiontarget') { throw "Could not establish the original selection; selected '$selected'." }
        $handlesBeforeCancel = Assert-SelectionHandles
        $cancelPoint = Get-RangeCenter 'Unrelated'
        $cancelGeometry = Assert-PointOutsideSelectionHandles -X $cancelPoint.x -Y $cancelPoint.y -Handles $handlesBeforeCancel -InjectedContactRadiusPixels 2.0
        try { $injection = Invoke-GuestProbe -Mode 'cancel-contact' -X $cancelPoint.x -Y $cancelPoint.y -PointerId 0 }
        catch {
            # P1-031: only the guest's exact no-native-cancel delivery pattern
            # is a tooling limit; every other delivery mismatch stays a FAIL.
            # Guest stderr is console-wrapped; compare without line breaks.
            if (($_.Exception.Message -replace '\r?\n', '').Contains('INJECTED-CANCEL-NOT-NATIVE: ')) {
                throw 'UNVERIFIED: Tooling limit (P1-031): the synthetic POINTER_FLAG_CANCELED|UP packet reached WinUI as one ordinary routed Release with PointerPoint.Properties.IsCanceled=false and no native-canceled packet, so this injection path cannot exercise native cancellation. Offline cancellation-classifier coverage applies; the guest receipt is retained under this run''s commands.'
            }
            throw
        }
        if (-not [bool]$injection.cancelDelivered -or $injection.delivery.status -cne 'PASS' -or
            [int]$injection.delivery.cancelDelta -ne 1 -or [int]$injection.delivery.releaseDelta -ne 0 -or
            [int]$injection.delivery.pressDelta -ne 1 -or [int]$injection.delivery.moveDelta -lt 1 -or
            [bool]$injection.delivery.inContact -or [int]$injection.delivery.activePointerCount -ne 0) {
            throw 'The canceled contact did not produce the required independent routed-event observer receipt.'
        }
        Start-Sleep -Milliseconds 250
        $after = Get-SelectedText
        if ($after -cne $selected) { throw "Canceling unrelated contact changed selection from '$selected' to '$after'." }
        $handles = Assert-SelectionHandles
        $menu = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') })
        if ($menu.Count -gt 0) { throw 'Canceled ordinary contact left the selection context menu open.' }
        return [pscustomobject]@{before=$selected;after=$after;cancelPoint=@{x=$cancelPoint.x;y=$cancelPoint.y;textRectangle=$cancelPoint.rectangle};cancelGeometry=$cancelGeometry;cancelInjection=$injection;handleCount=$handles.Count;flyoutClosed=($menu.Count -eq 0)}
    }

    if ($IncludeSuppressionInvestigation) {
      Test-Case -Id 'V1-004' -Name 'investigate-near-origin-independent-touch-tap' -Requirement 'Investigate whether a long-press selection followed by Escape and a separate tap on an adjacent link within the measured 48-DIP/1.5-second envelope is incorrectly suppressed; this is a suspected issue, not a confirmed failure.' -Body {
        $url = 'https://example.invalid/suppression/' + [guid]::NewGuid().ToString('N')
        $source = "# Near-origin touch regression`n`nx [y]($url)`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'Near-origin touch regression'
        $origin = Get-RangeCenter 'x'
        $target = Get-RangeCenter 'y'
        $metadata = Invoke-GuestProbe -Mode 'metadata'
        $distancePixels = [Math]::Sqrt([Math]::Pow(($origin.x - $target.x), 2) + [Math]::Pow(($origin.y - $target.y), 2))
        $distanceDips = $distancePixels / [double]$metadata.dpiScale
        if ($distanceDips -ge 48) { throw "UNVERIFIED: adjacent fixture points are $distanceDips DIPs apart, outside the 48-DIP repro envelope." }
        # One guest PowerShell process performs release, checks Escape-only
        # dismissal/selection preservation, then injects a distinct gesture
        # on the one-contact device's reused local index 0.
        # Do not insert host CLI calls between release and the measured tap.
        $sequence=Invoke-GuestProbe -Mode 'near-origin-sequence' -X $origin.x -Y $origin.y -Width $target.x -Height $target.y -RequiredText $url -PointerId 0
        if([string]$sequence.status -eq 'UNVERIFIED'){throw ("UNVERIFIED: {0}" -f $sequence.reason)}
        if([string]$sequence.mode -cne 'near-origin-sequence' -or -not [bool]$sequence.independentTapActivated){throw 'Guest sequence returned no affirmative evidence that the nearby new-contact tap activated its unique link.'}
        if([double]$sequence.distanceDips -ge 48.0 -or [double]$sequence.releaseToTapMilliseconds -ge 1500.0){throw 'Guest sequence exceeded the exact 48-DIP or 1.5-second reproduction bounds.'}
        return [pscustomobject]@{origin=$origin;target=$target;fixtureDistancePixels=$distancePixels;fixtureDistanceDips=$distanceDips;guestSequence=$sequence;classification='investigation only; no defect is claimed unless the measured in-envelope tap fails'}
      }
    }

    Test-Case -Id 'C10' -Name 'touch-double-tap-handle-drag-context-copy-and-cancel' -Requirement 'Touch double-tap exposes a selectable word and handles, dragging changes the UIA range, and releasing a long-press opens native Copy commands.' -Body {
        # The full batch reaches this case on the TouchSelection page; the
        # focused clipboard batch opens it here.
        if ($script:Phase1ClipboardOnly) { Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection' }
        Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled $false
        $source = "# Touch interaction regression`n`nHold or double-tap touchtarget here.`nA second line continues the selection across formatting.`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'touchtarget'
        $target = Get-RangeCenter 'touchtarget'
        $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($target.x),$($target.y)")
        $deadline = [DateTime]::UtcNow.AddSeconds(4)
        $selected = ''
        do { Start-Sleep -Milliseconds 100; $selected = Get-SelectedText } while ($selected -ne 'touchtarget' -and [DateTime]::UtcNow -lt $deadline)
        if ($selected -cne 'touchtarget') { throw "Double-tap selected '$selected', expected touchtarget." }
        $null = Assert-SelectionHandles
        $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','tap','--at',"$($target.x),$($target.y)")
        $deadline = [DateTime]::UtcNow.AddSeconds(3)
        $selectionMenu = @()
        do { Start-Sleep -Milliseconds 80; $selectionMenu = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') }) } while ($selectionMenu.Count -lt 3 -and [DateTime]::UtcNow -lt $deadline)
        if ($selectionMenu.Count -ne 3) { throw 'Tapping inside the committed selection did not reopen the native selection menu.' }
        if ((Get-SelectedText) -cne $selected) { throw 'Tapping inside the selected text changed or cleared the committed range.' }
        $null = Invoke-Ui @('send-keys','esc','--via','send-input')
        $null = Invoke-Ui @('wait-for','MarkdownContextCopy','--gone','-t','1500')
        if ((Get-SelectedText) -cne $selected) { throw 'Dismissing the reopened selection menu changed the selected range.' }
        $null = Assert-SelectionHandles
        $startHandle = 'MarkdownSelectionEndHandle'
        $renderer = Get-UiBounds 'MarkdownRenderer'
        $rendererViewport = [pscustomobject]@{x=[double]$renderer[0];y=[double]$renderer[1];width=[double]$renderer[2];height=[double]$renderer[3]}

        # Preserve the original short-document/blank-canvas destination as a
        # separate non-collapse check. Prove this old +500,+220 destination is
        # still inside the renderer and outside every authored text rectangle;
        # it is not the target used to claim that a drag extends a range.
        $legacyBlankX = [int]($renderer[0]+[Math]::Min($renderer[2]-12,500))
        $legacyBlankY = [int]($renderer[1]+[Math]::Min($renderer[3]-12,220))
        $shortFixtureText = @(
            'Touch interaction regression',
            'Hold or double-tap touchtarget here.',
            'A second line continues the selection across formatting.'
        )
        $shortTextRectangles = [System.Collections.Generic.List[object]]::new()
        foreach ($text in $shortFixtureText) {
            foreach ($rect in @(Get-RangeRectangles -Text $text)) { $shortTextRectangles.Add($rect) }
        }
        $blankGeometry = Assert-PointOutsideTextRanges -X $legacyBlankX -Y $legacyBlankY `
            -Rectangles @($shortTextRectangles.ToArray()) -ViewportBounds $rendererViewport -ContactRadiusPixels 2
        $blankExpectedPoint = Get-HostExpectedScreenTouchPoint -ScreenX $legacyBlankX -ScreenY $legacyBlankY
        $blankObserverBefore = Get-HostTouchContactObservation
        $null = Invoke-Ui @('touch',$startHandle,'--gesture','swipe','--to-point',"$legacyBlankX,$legacyBlankY",'--duration-ms','350')
        $blankAfter = ''
        $blankSelection = $null
        $blankSelectionDeadline = [DateTime]::UtcNow.AddSeconds(3)
        do {
            Start-Sleep -Milliseconds 80
            $blankSelection = Invoke-GuestProbe -Mode 'selection'
            $blankAfter = [string]$blankSelection.text
            $hasBlankRange = ([int]$blankSelection.rangeCount -eq 1 -and
                [string]$blankSelection.text -ceq $blankAfter -and
                @($blankSelection.ranges[0].rectangles).Count -ge 1)
        } while (-not $hasBlankRange -and [DateTime]::UtcNow -lt $blankSelectionDeadline)
        $blankObserverAfter = Get-HostTouchContactObservation
        $blankDiagnostics = [ordered]@{
            selectionBefore=$selected;selectionAfter=$blankAfter;expectedHandle=$startHandle
            expectedScreenPoint=$blankExpectedPoint;rendererBounds=$renderer;blankGeometry=$blankGeometry
            observerBefore=$blankObserverBefore;observerAfter=$blankObserverAfter
        }
        try {
            $blankDelivery = Assert-HostTouchHandleDragDelivery -Before $blankObserverBefore -After $blankObserverAfter `
                -ExpectedPoint $blankExpectedPoint -ExpectedState Released -ExpectedHandle $startHandle
            $blankDiagnostics.delivery = $blankDelivery
            if ([string]$blankDelivery.handle -cne $startHandle -or [int]$blankDelivery.moveDelta -lt 1) {
                throw 'The blank-space gesture did not begin on the exact end handle and move.'
            }
            if ([string]::IsNullOrEmpty($blankAfter) -or -not $blankAfter.StartsWith('touchtarget',[StringComparison]::Ordinal)) {
                throw 'Dragging the owned end handle into the measured blank region collapsed or moved the original selection anchor.'
            }
            if (-not $hasBlankRange) {
                throw 'The blank-space gesture did not leave one exact, geometry-backed UIA range matching the committed text.'
            }
            $blankHandlesAfter = Assert-SelectionHandles
        }
        catch { throw (Format-TouchHandleDragFailure -Reason $_.Exception.Message -Diagnostics $blankDiagnostics) }

        # A separate hard-break + strong-text fixture makes the real extension
        # target a measured final word on the next rendered line. Its exact
        # expected selection is authored here, never read back from UIA.
        $extensionSource = "# Touch handle extension`n`ntouchstarttarget here.  `nA hard break starts **this bold line continues to measuredextension**`n"
        $expectedExtended = "touchstarttarget here.`nA hard break starts this bold line continues to measuredextension"
        if (-not $extensionSource.Contains("  `n")) { throw 'The independent extension fixture lost its authored hard break.' }
        $extensionRuns = [System.Collections.Generic.List[object]]::new()
        foreach ($viewportMode in @('owned','ancestor')) {
            Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($viewportMode -eq 'ancestor')
            Set-ToggleState -AutomationId 'RtlToggle' -Enabled $false
            Set-TemporaryMarkdown -Markdown $extensionSource -RequiredText 'measuredextension'
            if ((Get-SelectedText).Length -ne 0) { throw "Replacing the source for $viewportMode viewport retained the prior selection." }

            $extensionStart = Get-RangeCenter 'touchstarttarget'
            $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($extensionStart.x),$($extensionStart.y)")
            $deadline = [DateTime]::UtcNow.AddSeconds(4)
            $extensionSelectionText = ''
            do { Start-Sleep -Milliseconds 80; $extensionSelectionText = Get-SelectedText } while ($extensionSelectionText -cne 'touchstarttarget' -and [DateTime]::UtcNow -lt $deadline)
            if ($extensionSelectionText -cne 'touchstarttarget') { throw "The $viewportMode viewport fixture selected '$extensionSelectionText' before the measured drag." }

            $handlesBeforeExtension = Assert-SelectionHandles
            $selectionBeforeExtension = Invoke-GuestProbe -Mode 'selection'
            if ([int]$selectionBeforeExtension.rangeCount -ne 1 -or
                [string]$selectionBeforeExtension.text -cne 'touchstarttarget' -or
                @($selectionBeforeExtension.ranges[0].rectangles).Count -ne 1) {
                throw "The $viewportMode viewport start selection lacks one exact range rectangle for offset calibration."
            }
            $endHandles = @($handlesBeforeExtension | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownSelectionEndHandle' })
            if ($endHandles.Count -ne 1) { throw "The $viewportMode viewport did not expose exactly one end handle." }
            $startRect = $selectionBeforeExtension.ranges[0].rectangles[0]
            $sourceCaret = [pscustomobject]@{
                # The source selection is LTR, so its logical trailing caret is
                # on the right edge. InlineContainerBox uses the layout bottom.
                x = [double]$startRect.x + [double]$startRect.width
                y = [double]$startRect.y + [double]$startRect.height
            }
            $fingerCenter = [pscustomobject]@{
                x = [double]$endHandles[0].x + ([double]$endHandles[0].width / 2.0)
                y = [double]$endHandles[0].y + ([double]$endHandles[0].height / 2.0)
            }
            # On press, the control records endpoint - finger and later adds
            # that vector to contact. Therefore the desired contact is the
            # measured caret target plus (finger - caret), not the text point.
            $fingerToCaretOffset = [pscustomobject]@{
                x = $fingerCenter.x - $sourceCaret.x
                y = $fingerCenter.y - $sourceCaret.y
            }
            $targetRectangles = Get-RangeRectangles -Text 'measuredextension'
            $extensionViewportRaw = Get-UiBounds 'MarkdownRenderer'
            $extensionViewport = [pscustomobject]@{
                x=[double]$extensionViewportRaw[0];y=[double]$extensionViewportRaw[1]
                width=[double]$extensionViewportRaw[2];height=[double]$extensionViewportRaw[3]
            }
            $destination = Resolve-TextHandleDestination -TargetRectangles $targetRectangles `
                -Endpoint End -FlowDirection Ltr -DpiScale ([double](Get-SelectionHandleState).dpiScale) `
                -FingerToCaretOffsetX $fingerToCaretOffset.x -FingerToCaretOffsetY $fingerToCaretOffset.y `
                -ViewportBounds $extensionViewport
            $expectedPoint = Get-HostExpectedScreenTouchPoint -ScreenX $destination.target.x -ScreenY $destination.target.y
            $observerBefore = Get-HostTouchContactObservation
            $null = Invoke-Ui @('touch','MarkdownSelectionEndHandle','--gesture','swipe',
                '--to-point',"$($destination.target.x),$($destination.target.y)",'--duration-ms','350')
            Start-Sleep -Milliseconds 120
            $extended = Get-SelectedText
            $observerAfter = Get-HostTouchContactObservation
            $dragDiagnostics = [ordered]@{
                viewport=$viewportMode;selectionBefore=$extensionSelectionText;selectionAfter=$extended
                expectedSelection=$expectedExtended;targetRange=$targetRectangles;destination=$destination
                fingerToCaretOffset=$fingerToCaretOffset;expectedScreenPoint=$expectedPoint
                observerBefore=$observerBefore;observerAfter=$observerAfter
            }
            try {
                $delivery = Assert-HostTouchHandleDragDelivery -Before $observerBefore -After $observerAfter `
                    -ExpectedPoint $expectedPoint -ExpectedState Released -ExpectedHandle 'MarkdownSelectionEndHandle'
                $dragDiagnostics.delivery = $delivery
                if ([string]$delivery.handle -cne 'MarkdownSelectionEndHandle' -or [int]$delivery.moveDelta -lt 1) {
                    throw "The $viewportMode viewport gesture did not begin on the owned end handle and move."
                }
                if ($extended -cne $expectedExtended) {
                    throw "The $viewportMode viewport handle drag selected '$extended'; expected the independently authored exact range '$expectedExtended'."
                }
                $selectionAfter = Invoke-GuestProbe -Mode 'selection'
                if ([int]$selectionAfter.rangeCount -ne 1 -or [string]$selectionAfter.text -cne $expectedExtended) {
                    throw "The $viewportMode viewport UIA TextPattern did not expose the one exact independently authored extended range."
                }
                $handlesAfter = Assert-SelectionHandles
                $extensionRuns.Add([pscustomobject]@{
                    viewport=$viewportMode;selection=$extended;expectedSelection=$expectedExtended
                    targetRange=$targetRectangles;destination=$destination;delivery=$delivery
                    rangeCount=$selectionAfter.rangeCount;handleCount=$handlesAfter.Count
                })
            }
            catch { throw (Format-TouchHandleDragFailure -Reason $_.Exception.Message -Diagnostics $dragDiagnostics) }
        }

        $longPressTarget = Get-RangeCenter 'touchstarttarget'
        $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','long-press','--hold-ms','650','--at',"$($longPressTarget.x),$($longPressTarget.y)")
        $deadline = [DateTime]::UtcNow.AddSeconds(4)
        $commands = @()
        do { Start-Sleep -Milliseconds 100; $commands = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll') }) } while ($commands.Count -lt 3 -and [DateTime]::UtcNow -lt $deadline)
        if ($commands.Count -ne 3) { throw 'Long-press release did not expose Copy, Copy as Markdown, and Select All.' }
        if ((Get-SelectedText) -cne $expectedExtended) { throw 'Long-pressing inside the extended range changed its exact committed selection.' }
        return [pscustomobject]@{
            doubleTapSelection=$selected;tapInsideReopenedMenu=$selectionMenu.Count
            selectionPreservedAfterTap=$true;selectionPreservedAfterMenuDismissal=$true
            blankSpaceNonCollapse=[pscustomobject]@{selectionBefore=$selected;selectionAfter=$blankAfter;originalAnchorRetained=$true;geometry=$blankGeometry;delivery=$blankDelivery;handleCount=$blankHandlesAfter.Count}
            exactExtensionByViewport=@($extensionRuns.ToArray());menuCommands=$commands.Count
            finalSelection=$expectedExtended;longPressTarget=$longPressTarget
        }
    }

    Test-Case -Id 'C10' -Name 'touch-selected-link-exposes-exact-copy-link-command' -Requirement 'A touch-selected hyperlink label retains one exact text range while its enabled Copy Link command places the authored URL on the clipboard without activating the link.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        Set-ToggleState -AutomationId 'RtlToggle' -Enabled $false
        Set-ToggleState -AutomationId 'TaskEditingToggle' -Enabled $false
        $url='https://example.invalid/copy-link/'+[guid]::NewGuid().ToString('N')
        $source="# Copy selected link regression`n`nBefore [linkedlabel]($url) after.`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'linkedlabel'
        $baseline=Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'LinkActivationStatus'
        if([string]::IsNullOrWhiteSpace([string]$baseline.name)){throw 'LinkActivationStatus is unavailable; the test cannot distinguish Copy Link from link activation.'}
        $target=Get-RangeCenter 'linkedlabel'
        $null=Invoke-Ui @('touch','MarkdownRenderer','--gesture','long-press','--hold-ms','650','--at',"$($target.x),$($target.y)")
        $deadline=[DateTime]::UtcNow.AddSeconds(4);$selected=''
        do{Start-Sleep -Milliseconds 80;$selected=Get-SelectedText}while($selected -cne 'linkedlabel' -and [DateTime]::UtcNow -lt $deadline)
        if($selected -cne 'linkedlabel'){throw "Long-press on the link label selected '$selected', not the exact label."}
        $handles=Assert-SelectionHandles
        $deadline=[DateTime]::UtcNow.AddSeconds(3);$commands=@()
        do{Start-Sleep -Milliseconds 80;$commands=@(Get-AppUiElements|Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll','MarkdownContextCopyLink') })}while($commands.Count -lt 4 -and [DateTime]::UtcNow -lt $deadline)
        if($commands.Count -ne 4){throw "The selected hyperlink context menu omitted one or more commands; observed IDs: $(@($commands|ForEach-Object automationId)-join ', ')."}
        $copyLink=Invoke-GuestProbe -Mode 'focus-state' -AutomationId 'MarkdownContextCopyLink'
        if(-not [bool]$copyLink.isEnabled){throw 'MarkdownContextCopyLink is present but disabled for the authored hyperlink.'}
        $selection=Invoke-GuestProbe -Mode 'selection'
        if([int]$selection.rangeCount -ne 1 -or [string]$selection.text -cne 'linkedlabel'){throw 'Before copying the URL, UIA no longer exposes exactly the selected hyperlink label.'}
        $shot=Save-Screenshot 'screenshots\touch-selected-link-copy-link-menu.png'
        $null=Invoke-Ui @('invoke','MarkdownContextCopyLink')
        $null=Invoke-Ui @('wait-for','MarkdownContextCopyLink','--gone','-t','2500')
        $clipboard=Invoke-GuestProbe -Mode 'clipboard'
        if([string]$clipboard.plain -cne $url){throw "Copy Link clipboard was '$($clipboard.plain)', expected the exact authored URL '$url'."}
        if([string]$clipboard.selectedText -cne 'linkedlabel' -or (Get-SelectedText) -cne 'linkedlabel'){throw 'Copy Link changed or cleared the existing text selection.'}
        $afterHandles=Assert-SelectionHandles
        $afterStatus=Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'LinkActivationStatus'
        if([string]$afterStatus.name -cne [string]$baseline.name){throw "Copy Link activated the hyperlink instead of copying it. Before='$($baseline.name)'; after='$($afterStatus.name)'."}
        return [pscustomobject]@{selectedLabel=$selection.text;rangeCount=$selection.rangeCount;copyLinkEnabled=$copyLink.isEnabled;clipboardUrl=$clipboard.plain;expectedUrl=$url;selectionPreserved=$true;handleCountBefore=$handles.Count;handleCountAfter=$afterHandles.Count;linkActivationUnchanged=$true;screenshot=$shot}
    }

    Test-Case -Id 'C12' -Name 'final-svg-atomic-selection-owned-viewport' -Requirement 'A deterministic final inline SVG exposes its independently authored alt text as one visible image peer; extending an existing text selection into that visible object yields one exact UIA range and exact Copy as Markdown source in owned-viewport mode.' -Body {
        Test-FinalVectorObjectSelection -Kind Svg -Viewport owned
    }
    Test-Case -Id 'C12' -Name 'final-svg-atomic-selection-ancestor-viewport' -Requirement 'A deterministic final inline SVG exposes its independently authored alt text as one visible image peer; extending an existing text selection into that visible object yields one exact UIA range and exact Copy as Markdown source in ancestor-viewport mode.' -Body {
        Test-FinalVectorObjectSelection -Kind Svg -Viewport ancestor
    }

    Test-Case -Id 'C12' -Name 'final-math-object-selection-owned-viewport' -Requirement 'A tall valid final Math scene has an accessible native peer and remains visible while an existing text selection extends through it inside its object bounds in owned viewport mode; UIA text and Copy as Markdown are exact.' -Body {
        Test-FinalVectorObjectSelection -Kind Math -Viewport owned
    }
    Test-Case -Id 'C12' -Name 'final-math-object-selection-ancestor-viewport' -Requirement 'A tall valid final Math scene has an accessible native peer and remains visible while an existing text selection extends through it inside its object bounds in ancestor viewport mode; UIA text and Copy as Markdown are exact.' -Body {
        Test-FinalVectorObjectSelection -Kind Math -Viewport ancestor
    }
    Test-Case -Id 'C12' -Name 'final-mermaid-object-selection-owned-viewport' -Requirement 'A final Mermaid scene has an accessible native peer and remains visible while an existing text selection extends through it inside its object bounds in owned viewport mode; UIA text and Copy as Markdown are exact.' -Body {
        Test-FinalVectorObjectSelection -Kind Mermaid -Viewport owned
    }
    Test-Case -Id 'C12' -Name 'final-mermaid-object-selection-ancestor-viewport' -Requirement 'A final Mermaid scene has an accessible native peer and remains visible while an existing text selection extends through it inside its object bounds in ancestor viewport mode; UIA text and Copy as Markdown are exact.' -Body {
        Test-FinalVectorObjectSelection -Kind Mermaid -Viewport ancestor
    }

    Test-Case -Id 'C14' -Name 'navigation-replacement-clears-selection-and-reloads-clean-source' -Requirement 'Replacing current source and navigating away cancels stale selection; returning to the sample restores its checked-in source without old text.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        Set-TemporaryMarkdown -Markdown "# Stale document marker`n`nStaleTokenUnique`n" -RequiredText 'StaleTokenUnique'
        $point = Get-RangeCenter 'StaleTokenUnique'
        $null = Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($point.x),$($point.y)")
        $deadline=[DateTime]::UtcNow.AddSeconds(3); do { Start-Sleep -Milliseconds 100; $selected=Get-SelectedText } while (-not $selected.Contains('StaleTokenUnique') -and [DateTime]::UtcNow -lt $deadline)
        if (-not $selected.Contains('StaleTokenUnique')) { throw 'Could not establish stale-document selection before replacement.' }
        Set-TemporaryMarkdown -Markdown "# Replacement document`n`nReplacementTokenUnique`n" -RequiredText 'ReplacementTokenUnique'
        if ((Get-SelectedText).Length -gt 0) { throw 'Document replacement retained old selection.' }
        Navigate-Sample -Key 'Selection' -RequiredText 'DOM-Style Text Selection'
        if ((Get-SelectedText).Length -gt 0) { throw 'Navigation retained a stale selection.' }
        $actual = Invoke-UiValue 'MarkdownRenderer'
        if ($actual.Contains('StaleTokenUnique') -or -not $actual.Contains('Copy as Markdown')) { throw 'Navigation did not restore the checked-in Selection sample.' }
        $handles = Invoke-GuestProbe -Mode 'handles'
        if (@($handles.handles).Count -gt 0) { throw 'Navigation left a selection handle in the UIA tree.' }
        return [pscustomobject]@{replacementVisible=$true;staleTextAbsent=$true;handlesAfterNavigation=0}
    }

    Test-Case -Id 'C13' -Name 'light-dark-forced-contrast-and-text-scale-are-committed' -Requirement 'Selection-capable document content survives app Light, Dark, forced High Contrast, and the sample 200% text-size profile; screenshots are retained.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $false
        Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled $false
        Set-ToggleState -AutomationId 'TextScaleToggle' -Enabled $false
        Wait-ThemeState -Theme 'light' -Contrast 'hc:off' -TextScale '1'
        $light=Save-Screenshot 'screenshots\touch-light.png'
        Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $true
        Wait-ThemeState -Theme 'dark' -Contrast 'hc:off' -TextScale '1'
        $dark=Save-Screenshot 'screenshots\touch-dark.png'
        Set-ToggleState -AutomationId 'TextScaleToggle' -Enabled $true
        Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled $true
        Wait-ThemeState -Theme 'high-contrast' -Contrast 'hc:on' -TextScale '2'
        $highContrast=Save-Screenshot 'screenshots\touch-forced-high-contrast-text-200.png'
        $native = Invoke-GuestProbe -Mode 'metadata'
        Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled $false
        Set-ToggleState -AutomationId 'TextScaleToggle' -Enabled $false
        Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $false
        Wait-ThemeState -Theme 'light' -Contrast 'hc:off' -TextScale '1'
        return [pscustomobject]@{light=$light;dark=$dark;forcedHighContrast=$highContrast;systemHighContrast=$native.systemHighContrast;windowDpiScale=$native.dpiScale}
    }

    Test-Case -Id 'C03' -Name 'editable-task-control-toggles-native-state' -Requirement 'On the task-editing page, a real task checkbox is independently touchable and its ToggleState changes.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        Set-ToggleState -AutomationId 'TaskEditingToggle' -Enabled $true
        $null = Invoke-Ui @('scroll','MarkdownRenderer','--to','bottom')
        $checkboxes = @(Get-UiElements | Where-Object { $_.type -eq 'CheckBox' -and (Get-UiAutomationId $_) -like 'MarkdownTask_*' })
        if ($checkboxes.Count -lt 1) { throw 'Task editing did not expose a native CheckBox with TogglePattern semantics.' }
        $target = $checkboxes[0]
        $targetAutomationId = [string]$target.automationId
        if ([string]::IsNullOrWhiteSpace($targetAutomationId)) { throw 'The selected native task checkbox has no stable AutomationId.' }
        $before = [string](Invoke-UiProperty -Selector $target.automationId -Property 'ToggleState')
        if ([string]::IsNullOrWhiteSpace([string]$target.name)) { throw 'The native task checkbox has no semantic label to scroll into view.' }
        $null = Invoke-GuestProbe -Mode 'scroll-range' -Needle ([string]$target.name)
        $targetMatches = @(Get-UiElements | Where-Object { $_.type -eq 'CheckBox' -and (Get-UiAutomationId $_) -ceq $targetAutomationId })
        if ($targetMatches.Count -ne 1) { throw "The exact native task checkbox '$targetAutomationId' was not realized after semantic TextPattern scrolling." }
        $target = $targetMatches[0]
        $null = Invoke-Ui @('touch',$targetAutomationId,'--gesture','tap')
        Start-Sleep -Milliseconds 100
        $updatedTarget = @(Get-UiElements | Where-Object { $_.type -eq 'CheckBox' -and (Get-UiAutomationId $_) -ceq $targetAutomationId })
        if ($updatedTarget.Count -ne 1) { throw "The exact touched task checkbox '$targetAutomationId' was not realized after activation." }
        $after = [string](Invoke-UiProperty -Selector $targetAutomationId -Property 'ToggleState')
        if ($before -ceq $after) { throw "The exact touched native task checkbox '$targetAutomationId' did not change state (still $after)." }
        Set-ToggleState -AutomationId 'TaskEditingToggle' -Enabled $false
        return [pscustomobject]@{checkboxAutomationId=$targetAutomationId;before=$before;after=$after;exactTouchedCheckboxVerified=$true}
    }

    Test-Case -Id 'C04' -Name 'touch-link-and-hosted-control-remain-direct-interaction-targets' -Requirement 'A tap on the checked-in link is routed as Touch rather than creating selection; the hosted action remains invokable and updates its status.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        Set-ToggleState -AutomationId 'RtlToggle' -Enabled $false
        Set-ToggleState -AutomationId 'TaskEditingToggle' -Enabled $false
        $null = Invoke-Ui @('touch','this link remains touchable','--gesture','tap')
        $deadline=[DateTime]::UtcNow.AddSeconds(5); $linkStatus=$null
        do { $linkStatus=Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'LinkActivationStatus'; if ($linkStatus.name -like 'link:Touch:https://example.invalid/touch-link*') { break }; Start-Sleep -Milliseconds 120 } while ([DateTime]::UtcNow -lt $deadline)
        if ($linkStatus.name -notlike 'link:Touch:https://example.invalid/touch-link*') { throw "Link tap was not routed to the expected host action: $($linkStatus.name)" }
        if ((Get-SelectedText).Length -gt 0) { throw 'Link activation created text selection.' }
        $null = Invoke-GuestProbe -Mode 'scroll-range' -Needle 'Touch hosted action'
        $hostedAction = Find-UiElement -Text 'Touch hosted action' -Type 'Button'
        if ($hostedAction.isOffscreen -or $hostedAction.width -le 0 -or $hostedAction.height -le 0) { throw 'The hosted action did not become visible after semantic TextPattern scrolling.' }
        $null = Invoke-Ui @('touch','Touch hosted action','--gesture','tap')
        $null = Invoke-Ui @('wait-for','Touch hosted action activated','-t','4000')
        $status = (Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'LinkActivationStatus').name
        if ((Get-SelectedText).Length -gt 0) { throw 'Hosted control activation created text selection.' }
        return [pscustomobject]@{linkStatus=$linkStatus.name;hostedActionActivated=$true;status=$status}
    }

    Test-Case -Id 'C14' -Name 'layout-resize-and-selection-state-survive-relayout' -Requirement 'A narrow then restored-width relayout keeps the exact selected word and recomputes current selection geometry/handles without leaving stale bounds.' -Body {
        Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
        $metadata=Invoke-GuestProbe -Mode 'metadata'
        $originalLeft=[int]$metadata.window.left; $originalTop=[int]$metadata.window.top
        $originalWidth=[int]($metadata.window.right-$metadata.window.left); $originalHeight=[int]($metadata.window.bottom-$metadata.window.top)
        $isNarrow=$false
        try {
            $narrowed=Invoke-GuestProbe -Mode 'resize' -Left $originalLeft -Top $originalTop -Width ([int][Math]::Round(760*$metadata.dpiScale)) -Height ([int][Math]::Round(860*$metadata.dpiScale))
            $isNarrow=$true
            Start-Sleep -Milliseconds 200
            $renderer=Get-UiBounds 'MarkdownRenderer'; $title=Get-UiBounds 'SamplePageTitle'
            if ($renderer[2] -le 0 -or $renderer[2] -gt (760*$metadata.dpiScale+10)) { throw 'The renderer does not fit the resized narrow window.' }
            if ($title[0] -lt 0 -or $title[2] -le 0) { throw 'The sample title has invalid bounds after narrow resize.' }
            $point=Get-RangeCenter 'hold'
            $null=Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at',"$($point.x),$($point.y)")
            $deadline=[DateTime]::UtcNow.AddSeconds(3); $selected=''
            do { Start-Sleep -Milliseconds 80; $selected=Get-SelectedText } while ($selected -cne 'hold' -and [DateTime]::UtcNow -lt $deadline)
            if ($selected -cne 'hold') { throw "Could not establish the exact hold selection for resize relayout; UIA returned '$selected'." }
            $narrowSelection=Invoke-GuestProbe -Mode 'selection'
            if ([int]$narrowSelection.rangeCount -ne 1 -or [string]$narrowSelection.text -cne 'hold' -or @($narrowSelection.ranges[0].rectangles).Count -eq 0) { throw 'Narrow-width selection does not expose exact text and current TextPattern geometry.' }
            $narrowHandles=Assert-SelectionHandles
            $narrowLayout=Get-CommittedTouchLayoutObservation
            $narrowShot=Save-Screenshot 'screenshots\selection-resize-narrow.png'

            $wide=Invoke-GuestProbe -Mode 'resize' -Left $originalLeft -Top $originalTop -Width $originalWidth -Height $originalHeight
            $isNarrow=$false
            Start-Sleep -Milliseconds 250
            if ((Get-SelectedText) -cne 'hold') { throw "Restoring the original width changed or cleared selection; current text is '$(Get-SelectedText)'." }
            $wideSelection=Invoke-GuestProbe -Mode 'selection'
            if ([int]$wideSelection.rangeCount -ne 1 -or [string]$wideSelection.text -cne 'hold') { throw 'Restored-width UIA TextPattern did not preserve exactly the selected word.' }
            $wideRects=@($wideSelection.ranges[0].rectangles | Where-Object { $_.width -gt 0 -and $_.height -gt 0 })
            if ($wideRects.Count -eq 0) { throw 'Restored-width selection has no current geometry; it may be stale.' }
            $currentTarget=Get-RangeCenter 'hold'
            # Refresh after the wide relayout; the earlier bounds describe the
            # narrow viewport and cannot qualify the restored geometry.
            $renderer=Get-UiBounds 'MarkdownRenderer'
            $matchingRects=@($wideRects | Where-Object { $_.x -lt ($currentTarget.rectangle.x+$currentTarget.rectangle.width) -and ($_.x+$_.width) -gt $currentTarget.rectangle.x -and $_.y -lt ($currentTarget.rectangle.y+$currentTarget.rectangle.height) -and ($_.y+$_.height) -gt $currentTarget.rectangle.y })
            if ($matchingRects.Count -eq 0) { throw 'Restored-width selected TextPattern rectangles do not overlap the current exact target geometry.' }
            foreach ($rect in $wideRects) {
                if ($rect.x -lt ($renderer[0]-1) -or $rect.y -lt ($renderer[1]-1) -or ($rect.x+$rect.width) -gt ($renderer[0]+$renderer[2]+1) -or ($rect.y+$rect.height) -gt ($renderer[1]+$renderer[3]+1)) { throw 'Restored-width selection geometry escapes the current renderer bounds.' }
            }
            $wideHandles=Assert-SelectionHandles
            $wideLayout=Get-CommittedTouchLayoutObservation
            $wideShot=Save-Screenshot 'screenshots\selection-resize-restored.png'
            return [pscustomobject]@{originalBounds=@{left=$originalLeft;top=$originalTop;width=$originalWidth;height=$originalHeight};narrowBounds=$narrowed.window;restoredBounds=$wide.window;selected='hold';narrowSelection=$narrowSelection.ranges[0].rectangles;restoredSelection=$wideRects;restoredTarget=$currentTarget.rectangle;handlesAfterNarrow=@($narrowHandles);handlesAfterRestore=@($wideHandles);narrowLayout=$narrowLayout;restoredLayout=$wideLayout;narrowScreenshot=$narrowShot;restoredScreenshot=$wideShot}
        }
        finally {
            if ($isNarrow) { $null=Invoke-GuestProbe -Mode 'resize' -Left $originalLeft -Top $originalTop -Width $originalWidth -Height $originalHeight }
        }
    }

    Test-Case -Id 'C14' -Name 'current-source-replacement-cancels-stale-document' -Requirement 'Rapid source replacement publishes the latest document, not an old snapshot, and leaves no stale diagnostic or selection.' -Body {
        Navigate-Sample -Key 'Selection' -RequiredText 'DOM-Style Text Selection'
        $old = '# Slow source candidate' + "`n`n" + (('OldContentToken ' * 120) + "`n`n") * 80
        $new = "# Latest replacement`n`nLatestContentToken remains.`n"
        # Keep the two source replacements back-to-back. The old fixture is
        # intentionally large, so stage it through the run-scoped guest file
        # path rather than putting ~95 KB on winapp's process command line.
        Set-TemporaryMarkdown -Markdown $old -RequiredText 'OldContentToken' -SkipRendererWait
        Set-TemporaryMarkdown -Markdown $new -RequiredText 'LatestContentToken'
        $actual=Wait-ForRendererText -Text 'LatestContentToken' -TimeoutMilliseconds 15000
        if ($actual.Contains('OldContentToken')) { throw 'A stale previous source was published after replacement.' }
        if ((Get-SelectedText).Length -gt 0) { throw 'Replacement retained prior selection.' }
        return [pscustomobject]@{latestPresent=$true;staleAbsent=$true;characters=$actual.Length}
    }

    # These are deliberately non-pass entries: they require external display
    # configurations, a fresh offline conformance/test receipt, or human review.
    if ($script:Phase2Only -or $script:Phase1ClipboardOnly) { return }
    Add-Unverified -Id 'C01' -Name 'commonmark-gfm-conformance-receipt' -Reason 'This UI batch does not run the pinned offline conformance corpus or ConformanceHarnessTests; attach the separate current-source test receipt.'
    Add-Unverified -Id 'C07' -Name 'valid-image-network-policy-and-invalid-failure-lifecycle' -Reason 'Only deterministic data-URI SVG is exercised here; remote image admission, cancellation races, and fallback classification require the separate current-source image test receipt.'
    Add-Unverified -Id 'C10' -Name 'stationary-edge-autoscroll-and-endpoint-crossing' -Reason 'The UI batch verifies swipe, handle extension and cancellation, but does not prove stationary frame-paced edge scrolling or handle-role crossing.'
    Add-Unverified -Id 'C13' -Name 'actual-dpi-100-150-200' -Reason 'The active Sandbox reports one effective window DPI. UI text-scale preview is not a substitute for 100%, 150%, or 200% Windows display scaling; record each physical setting in separate qualified runs.'
    Add-Unverified -Id 'C13' -Name 'actual-windows-system-high-contrast' -Reason 'The script exercises the sample forced High Contrast semantic provider. It records but does not change the Windows system High Contrast setting.'
    Add-Unverified -Id 'C13' -Name 'screenshot-review' -Reason 'Screenshots are captured; root must inspect each listed PNG for clipping, overlap, missing visual content, and theme correctness before this criterion can pass.'
    Add-Unverified -Id 'C14' -Name 'live-capture-loss-unload-and-disposal-during-touch' -Reason 'This UI batch does not induce and observe capture loss, renderer unload, or disposal while a touch/selection-handle contact is active. Existing offline retirement coverage is not live app qualification.'
    Add-Unverified -Id 'C15' -Name 'current-source-feature-test-receipts' -Reason 'The UI batch runs sample smoke assertions, not every current-source focused HTML/math/Mermaid/SVG/highlighting/hosted-control test project.'
    if (-not $IncludeSuppressionInvestigation) {
        Add-Unverified -Id 'V1-004' -Name 'suspected-near-origin-touch-suppression-investigation' -Reason 'Not run: use -IncludeSuppressionInvestigation to perform the bounded long-press/Escape/nearby-link reproduction. This is suspected only until measured live evidence exists.'
    }
    if (-not $IncludeSelectionDisableInvestigation) {
        Add-Unverified -Id 'C14' -Name 'is-selection-enabled-disable-and-reenable-owned-and-ancestor-viewports' -Reason 'Not run: selection-disable behavior requires the opt-in second app instance; use -IncludeSelectionDisableInvestigation to exercise the actual IsSelectionEnabled property in both viewport ownership modes.'
    }
}

Assert-CorrectnessRunOptions `
    -ResizeCrashDiagnosticOnly:$ResizeCrashDiagnosticOnly `
    -IncludeSuppressionInvestigation:$IncludeSuppressionInvestigation `
    -IncludeSelectionDisableInvestigation:$IncludeSelectionDisableInvestigation `
    -Phase2Only:$Phase2Only

try {
    $runInfo = Initialize-Run
    $script:UiStarted = $true
    Run-CorrectnessCases
    if ($script:Phase1ClipboardOnly) {
        . (Join-Path $PSScriptRoot 'MarkdownCorrectnessPhase2Cases.ps1')
        . (Join-Path $PSScriptRoot 'MarkdownCorrectnessPhase1ClipboardCases.ps1')
        Run-Phase1ClipboardCases
    }
    if ($script:Phase2Only) {
        . (Join-Path $PSScriptRoot 'MarkdownCorrectnessPhase2Cases.ps1')
        . (Join-Path $PSScriptRoot 'MarkdownCorrectnessPhase2ProviderCases.ps1')
        if ($script:Phase2SystemHighContrast) {
            . (Join-Path $PSScriptRoot 'MarkdownCorrectnessPhase2SystemContrastCases.ps1')
            Run-Phase2SystemContrastCases
        }
        else {
            Run-Phase2Cases
            Run-Phase2ProviderCases
        }
    }
    if ($IncludeSelectionDisableInvestigation) { Run-SelectionDisableInvestigation }
}
catch {
    $script:Results.Add([pscustomobject]@{
        id = 'HARNESS'; name = 'harness-setup-or-fatal-error'; requirement = 'The batch must stay on the approved Sandbox and preserve target/PID identity.'
        status = 'FAIL'; actual = $null; detail = $_.Exception.ToString(); evidence = @(); startedUtc = [DateTime]::UtcNow.ToString('o'); finishedUtc = [DateTime]::UtcNow.ToString('o')
    })
}
finally {
    try {
        $null = Invoke-NativeChecked @('ui','yield','--on','sandbox')
    }
    catch {
        $script:Results.Add([pscustomobject]@{ id='WORKFLOW';name='release-ui-workflow';requirement='Yield the same Sandbox UI workflow in finally.';status='FAIL';detail=$_.Exception.Message;actual=$null;evidence=@();startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=[DateTime]::UtcNow.ToString('o') })
    }
    $env:WINAPP_UI_WORKFLOW_ID = $script:PreviousWorkflowId
}

if (-not (Test-Path -LiteralPath $script:RunDirectory -PathType Container)) {
    [System.IO.Directory]::CreateDirectory($script:RunDirectory) | Out-Null
}
function New-RunResultsReport {
    $counts = Get-CaseCounts
    $diagnosticOnly = [bool]$script:ResizeCrashDiagnosticOnly
    $diagnosticNote = if ($diagnosticOnly) {
        'A focused run with no crash is inconclusive about heap conditioning and is not full Phase 1 qualification.'
    } else { $null }
    return [ordered]@{
        schemaVersion = 1; objective = $(if ($script:Phase2Only) { 'MarkdownRenderer Phase 2 feature matrix (live)' } else { 'MarkdownRenderer Phase 1 correctness-only' }); runId = $script:RunId
        target = 'sandbox'; targetEpoch = $script:TargetEpoch; appProcessId = $script:AppProcessId; appWindowHandle = $script:AppWindowHandle
        diagnosticOnly = $diagnosticOnly; diagnosticOnlyNote = $diagnosticNote
        resultCounts = $counts; screenshots = @($script:Screenshots.ToArray()); results = @($script:Results.ToArray())
        complete = (-not $diagnosticOnly -and $counts.fail -eq 0 -and $counts.unverified -eq 0)
        exitCodeMeaning = '0=all recorded assertions pass; 1=one or more assertions/harness failures; 2=batch completed but one or more required checks remain unverified.'
        generatedUtc = [DateTime]::UtcNow.ToString('o')
    }
}
$report = New-RunResultsReport
$counts = $report.resultCounts
Write-JsonFile (Join-Path $script:RunDirectory 'results.json') $report
$counts | Format-List
Write-Host "Correctness UI evidence: $script:RunDirectory"
if ($counts.fail -gt 0) { exit 1 }
if ($counts.unverified -gt 0) { exit 2 }
exit 0
