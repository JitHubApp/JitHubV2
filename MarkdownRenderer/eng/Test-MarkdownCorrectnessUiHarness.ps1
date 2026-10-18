[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks=[Collections.Generic.List[object]]::new()
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$harnessPath = Join-Path $PSScriptRoot 'Invoke-MarkdownCorrectnessUiTests.ps1'
$tokens = $null
$parseErrors = $null
$harnessAst = [System.Management.Automation.Language.Parser]::ParseFile($harnessPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "The UI harness does not parse: $($parseErrors | Out-String)"
}

$runArgumentsFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SampleProjectRunArguments'
}, $true)
$buildOutputFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SampleBuildOutputDirectory'
}, $true)
$initializeRunFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-Run'
}, $true)
if ($null -eq $runArgumentsFunction -or $null -eq $buildOutputFunction -or $null -eq $initializeRunFunction) {
    throw 'The UI runner must expose testable project-mode argument, payload-path, and initialization helpers.'
}
Invoke-Expression $runArgumentsFunction.Extent.Text
Invoke-Expression $buildOutputFunction.Extent.Text

$testRepoRoot = 'E:\fixture repo'
$testProjectPath = Join-Path $testRepoRoot 'MarkdownRenderer\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj'
$defaultRunArguments = @(Get-SampleProjectRunArguments -ProjectPath $testProjectPath -Configuration Release)
$expectedDefaultRunArguments = @('run', $testProjectPath, '--on', 'sandbox', '--detach', '--json', '-c', 'Release', '--arch', 'x64')
if ($defaultRunArguments.Count -ne $expectedDefaultRunArguments.Count -or
    [string]::Join([char]0, $defaultRunArguments) -cne [string]::Join([char]0, $expectedDefaultRunArguments)) {
    throw 'The default sample launch must preserve the existing Sandbox project-mode arguments without an ArtifactsPath override.'
}
$defaultBuildOutput = Get-SampleBuildOutputDirectory -RepoRoot $testRepoRoot -Configuration Release
$expectedDefaultBuildOutput = Join-Path $testRepoRoot 'MarkdownRenderer\MarkdownRenderer.Sample\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64'
if (-not [string]::Equals($defaultBuildOutput, $expectedDefaultBuildOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The default payload path must remain the existing x64 Release output directory.'
}

$testArtifactsPath = 'C:\Users\neroc\AppData\Local\Temp\MarkdownRenderer Correctness Artifacts'
$customRunArguments = @(Get-SampleProjectRunArguments -ProjectPath $testProjectPath -Configuration Release -NoBuild -BuildArtifactsPath $testArtifactsPath)
$expectedCustomRunArguments = @('run', $testProjectPath, '--on', 'sandbox', '--detach', '--json', '-c', 'Release', '--arch', 'x64', '--no-build', '-p', "ArtifactsPath=$testArtifactsPath")
if ($customRunArguments.Count -ne $expectedCustomRunArguments.Count -or
    [string]::Join([char]0, $customRunArguments) -cne [string]::Join([char]0, $expectedCustomRunArguments)) {
    throw 'A custom build root must be passed as one quoted-safe MSBuild property in Sandbox project mode, without switching to folder mode.'
}
$customBuildOutput = Get-SampleBuildOutputDirectory -RepoRoot $testRepoRoot -Configuration Release -BuildArtifactsPath $testArtifactsPath
$expectedCustomBuildOutput = Join-Path $testArtifactsPath 'bin\MarkdownRenderer.Sample\release_win-x64'
if (-not [string]::Equals($customBuildOutput, $expectedCustomBuildOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The custom payload directory must resolve under the exact requested ArtifactsPath and configuration/RID pivot.'
}

$relativeArtifactsRejected = $false
try { Get-SampleProjectRunArguments -ProjectPath $testProjectPath -Configuration Release -BuildArtifactsPath 'relative\artifacts' | Out-Null }
catch { $relativeArtifactsRejected = $true }
if (-not $relativeArtifactsRejected) { throw 'The custom build root must reject relative paths rather than risk resolving a different output.' }

$initializeRunText = $initializeRunFunction.Extent.Text
foreach ($required in @(
    'Get-SampleProjectRunArguments',
    'Get-SampleBuildOutputDirectory',
    'Get-BuildPayloadIdentity -Directory $buildOutputDirectory'
)) {
    if ($initializeRunText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Initialize-Run does not consistently route launch and payload identity through '$required'."
    }
}

# Execute the actual script parameter block and top-level initialization AST in
# an isolated script scope. This catches parameter shadowing that helper-only
# tests cannot see. Replace only the automatic PSScriptRoot value with a fixture
# root so the extracted prefix resolves the same paths without running the UI.
$initializationStatements = [Collections.Generic.List[string]]::new()
foreach ($statement in $harnessAst.EndBlock.Statements) {
    if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) { break }
    $initializationStatements.Add($statement.Extent.Text)
}
$testScriptRoot = Join-Path $testRepoRoot 'MarkdownRenderer\eng'
$testScriptRootLiteral = "'$( $testScriptRoot.Replace("'", "''") )'"
$initializationResultSource = @'
[pscustomobject]@{
    effectiveBuildArtifactsPath = $script:BuildArtifactsPath
    repoRoot = $script:RepoRoot
    projectPath = $script:ProjectPath
    runArguments = @(Get-SampleProjectRunArguments -ProjectPath $script:ProjectPath -Configuration $Configuration -NoBuild:$NoBuild -BuildArtifactsPath $script:BuildArtifactsPath)
    payloadDirectory = Get-SampleBuildOutputDirectory -RepoRoot $script:RepoRoot -Configuration $Configuration -BuildArtifactsPath $script:BuildArtifactsPath
}
'@
$initializationParts = @($harnessAst.ParamBlock.Extent.Text) +
    @($initializationStatements.ToArray()) +
    @($runArgumentsFunction.Extent.Text, $buildOutputFunction.Extent.Text, $initializationResultSource)
$initializationSource = [string]::Join([Environment]::NewLine, $initializationParts).Replace('$PSScriptRoot', $testScriptRootLiteral)
$initializationPath = Join-Path ([System.IO.Path]::GetTempPath()) ("MarkdownCorrectnessUiHarnessInit-{0}.ps1" -f [guid]::NewGuid().ToString('N'))
try {
    [System.IO.File]::WriteAllText($initializationPath, $initializationSource, [System.Text.UTF8Encoding]::new($false))
    # Invoke as a script file rather than a nested scriptblock so its param
    # variables and $script: variables share the same scope, as in production.
    $customInitialization = & $initializationPath -Configuration Release -NoBuild -OutputRoot (Join-Path $testArtifactsPath 'evidence') -BuildArtifactsPath $testArtifactsPath
    $defaultInitialization = & $initializationPath -Configuration Release -OutputRoot (Join-Path $testArtifactsPath 'default-evidence')
}
finally {
    Remove-Item -LiteralPath $initializationPath -Force -ErrorAction SilentlyContinue
}
$expectedFixtureProject = Join-Path $testRepoRoot 'MarkdownRenderer\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj'
if (-not [string]::Equals([string]$customInitialization.effectiveBuildArtifactsPath, $testArtifactsPath, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$customInitialization.repoRoot, $testRepoRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$customInitialization.projectPath, $expectedFixtureProject, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$customInitialization.payloadDirectory, $expectedCustomBuildOutput, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Join([char]0, @($customInitialization.runArguments)) -cne [string]::Join([char]0, $expectedCustomRunArguments)) {
    throw 'The actual custom-path script initialization must preserve the bound C artifacts root across project launch and host payload selection.'
}
if (-not [string]::IsNullOrEmpty([string]$defaultInitialization.effectiveBuildArtifactsPath) -or
    -not [string]::Equals([string]$defaultInitialization.payloadDirectory, $expectedDefaultBuildOutput, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Join([char]0, @($defaultInitialization.runArguments)) -cne [string]::Join([char]0, $expectedDefaultRunArguments)) {
    throw 'The actual default script initialization must keep the legacy output root and omit the custom MSBuild property.'
}
$checks.Add([pscustomobject]@{name='custom absolute C-artifacts root is passed as one Sandbox project-mode MSBuild argument and used for the exact host payload directory while the default output remains unchanged';status='PASS'})

$handleStateFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SelectionHandleState'
}, $true)
$nativeEditorSourceFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-NativeMarkdownEditorSource'
}, $true)
if ($null -eq $handleStateFunction -or $null -eq $nativeEditorSourceFunction) {
    throw 'The runner must expose strict handle-state and independently authored native-editor source helpers.'
}
Invoke-Expression $handleStateFunction.Extent.Text
Invoke-Expression $nativeEditorSourceFunction.Extent.Text
$handleAssignments = @($harnessAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -in @('$clearedHandles', '$priorHandles') -and
        $node.Right.Extent.Text.Contains('Get-SelectionHandleState')
}, $true))
if ($handleAssignments.Count -ne 3) { throw 'All three Unicode cleanup assignments must use the validated handle-state projection.' }
if ($harnessAst.Extent.Text.Contains("@(Invoke-GuestProbe -Mode 'handles').handles")) {
    throw 'The empty-array member projection that prevented touch input must not remain.'
}
& {
    function Invoke-GuestProbe { param([string]$Mode) if ($Mode -cne 'handles') { throw 'Unexpected offline probe mode.' }; return $script:HandleProbeFixture }
    foreach ($count in @(0, 1, 2)) {
        $fixtureItems = @(for ($index = 0; $index -lt $count; $index++) { [pscustomobject]@{automationId="handle-$index"} })
        $script:HandleProbeFixture = [pscustomobject]@{mode='handles';handles=$fixtureItems}
        foreach ($assignment in $handleAssignments) {
            $projected = & ([scriptblock]::Create($assignment.Extent.Text + [Environment]::NewLine + 'return ,@(' + $assignment.Left.Extent.Text + ')'))
            if ($projected -isnot [Array] -or $projected.Count -ne $count) {
                throw "The actual Unicode cleanup assignment lost the explicit $count-handle array."
            }
            for ($index = 0; $index -lt $count; $index++) {
                if ($projected[$index].automationId -cne "handle-$index") { throw 'The projection changed a handle identity or order.' }
            }
        }
    }
    foreach ($invalid in @($null, [pscustomobject]@{}, [pscustomobject]@{handles=$null}, [pscustomobject]@{handles=[pscustomobject]@{automationId='scalar'}})) {
        $script:HandleProbeFixture = $invalid
        $rejected = $false
        try { Get-SelectionHandleState | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw 'Missing, null, or scalar handle state must fail rather than mean zero handles.' }
    }
}
$nativeSourceReference = "alpha`r`rβeta`r"
foreach ($authoredSource in @("alpha`n`nβeta`n", "alpha`r`n`r`nβeta`r`n", $nativeSourceReference)) {
    if ((Get-NativeMarkdownEditorSource -Text $authoredSource) -cne $nativeSourceReference) { throw 'Native TextBox source prediction changed content or line count.' }
}
if ((Get-NativeMarkdownEditorSource -Text "alpha`n`nBETA`n") -ceq $nativeSourceReference -or
    (Get-NativeMarkdownEditorSource -Text '') -cne '') { throw 'The native source oracle must preserve every non-line-ending code unit and empty input.' }
$checks.Add([pscustomobject]@{name='actual Unicode cleanup assignments preserve zero/one/two handle arrays and reject malformed states; native TextBox source expectations are authored independently';status='PASS'})

$scrollBottomFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Wait-ForOwnedRendererScrollBottom'
}, $true)
if ($null -eq $scrollBottomFunction) { throw 'A bottom-scroll assertion must observe committed ScrollPattern state rather than a fixed sleep.' }
Invoke-Expression $scrollBottomFunction.Extent.Text
& {
    function Start-Sleep { param([int]$Milliseconds) }
    function Invoke-GuestProbe { param([string]$Mode) if ($Mode -cne 'scroll') { throw 'Unexpected scroll probe mode.' }; return $script:ScrollProbeFixture }
    $script:ScrollProbeFixture = [pscustomobject]@{scrollers=@([pscustomobject]@{automationId='MarkdownRenderer';verticalPercent=100})}
    $complete = Wait-ForOwnedRendererScrollBottom -TimeoutMilliseconds 0
    if ($complete.scrollers[0].verticalPercent -ne 100) { throw 'The scroll wait did not preserve the measured terminal receipt.' }
    foreach ($fixture in @(
        [pscustomobject]@{scrollers=@()},
        [pscustomobject]@{scrollers=@([pscustomobject]@{automationId='MarkdownEditor';verticalPercent=100})},
        [pscustomobject]@{scrollers=@([pscustomobject]@{automationId='MarkdownRenderer';verticalPercent=0})},
        [pscustomobject]@{scrollers=@([pscustomobject]@{automationId='MarkdownRenderer';verticalPercent=[double]::NaN})}
    )) {
        $script:ScrollProbeFixture = $fixture
        $rejected = $false
        try { Wait-ForOwnedRendererScrollBottom -TimeoutMilliseconds 0 | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw 'Bottom-scroll setup must reject missing/wrong targets, an uncommitted scroll and invalid metrics.' }
    }
}
$checks.Add([pscustomobject]@{name='Mermaid bottom geometry is gated on the exact owned renderer committing 100% vertical scroll; wrong peers and uncommitted scroll cannot pass';status='PASS'})

$committedLayoutFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-CommittedTouchLayoutObservation'
}, $true)
if ($null -eq $committedLayoutFunction) { throw 'Resize diagnostics must distinguish the committed snapshot width from current control width.' }
Invoke-Expression $committedLayoutFunction.Extent.Text
& {
    function Start-Sleep { param([int]$Milliseconds) }
    function Invoke-GuestProbe { param([string]$Mode, [string]$AutomationId) if ($Mode -cne 'raw-name' -or $AutomationId -cne 'TouchContactObservation') { throw 'Unexpected offline layout probe.' }; return [pscustomobject]@{name=($script:LayoutProbeFixture | ConvertTo-Json -Depth 5)} }
    $script:LayoutProbeFixture = [pscustomobject]@{observationStatus='ok';observationErrors=0;layoutCaptureErrors=0;layoutCapture=[pscustomobject]@{status='captured';rendererActualWidthDip=345;snapshotSizeDip=[pscustomobject]@{width=345;height=1000}}}
    $currentLayout = Get-CommittedTouchLayoutObservation -TimeoutMilliseconds 0
    if ($currentLayout.layoutCapture.snapshotSizeDip.width -ne 345) { throw 'The layout reader lost the measured current snapshot width.' }
    foreach ($failure in @('staleWidth', 'degraded')) {
        $savedFixture = $script:LayoutProbeFixture | ConvertTo-Json -Depth 5
        if ($failure -ceq 'staleWidth') { $script:LayoutProbeFixture.layoutCapture.snapshotSizeDip.width = 1359 }
        else { $script:LayoutProbeFixture.observationErrors = 1 }
        $rejected = $false
        try { Get-CommittedTouchLayoutObservation -TimeoutMilliseconds 0 | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Resize diagnostics accepted $failure observation state." }
        $script:LayoutProbeFixture = $savedFixture | ConvertFrom-Json
    }
}
$checks.Add([pscustomobject]@{name='resize layout evidence requires a healthy observer and current committed snapshot/control width agreement';status='PASS'})

$guestProbeFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-GuestProbe'
}, $true)
if ($null -eq $guestProbeFunction) { throw 'Could not find Invoke-GuestProbe in the UI harness.' }
Invoke-Expression $guestProbeFunction.Extent.Text
$outerModeParameter = @($guestProbeFunction.Body.FindAll({param($node)$node -is [System.Management.Automation.Language.ParameterAst] -and $node.Name.VariablePath.UserPath -ceq 'Mode'},$true)) | Select-Object -First 1
$outerModeValidateSet = @()
if($null -ne $outerModeParameter){$outerModeValidateSet = @($outerModeParameter.Attributes | Where-Object { $_.TypeName.FullName -ceq 'ValidateSet' })}
$outerModeModes = if($outerModeValidateSet.Count -gt 0){@($outerModeValidateSet[0].PositionalArguments | ForEach-Object {[string]$_.Value})}else{@()}
if($outerModeModes -cnotcontains 'process-state' -or $outerModeModes -cnotcontains 'launch-selection-disable-probe') {
    throw 'The host Invoke-GuestProbe wrapper must allow exact process-state cleanup and direct guest launch of the opt-in probe.'
}
$uiFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Ui'},$true)
$caseFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-Case'},$true)
$identityFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-ProcessIdentity'},$true)
$pointOutsideFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-PointOutsideSelectionHandles'},$true)
if($null -eq $uiFunction -or $null -eq $caseFunction -or $null -eq $identityFunction -or $null -eq $pointOutsideFunction){
    throw 'The UI harness must expose input, per-case, identity, and unrelated-contact geometry guards.'
}

$nativeProcessFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-RedirectedUtf8Process'
}, $true)
if ($null -eq $nativeProcessFunction) { throw 'Could not find the redirected UTF-8 native process helper.' }
if ($nativeProcessFunction.Extent.Text -notmatch 'RedirectStandardOutput\s*=\s*\$true' -or
    $nativeProcessFunction.Extent.Text -notmatch 'RedirectStandardError\s*=\s*\$true' -or
    $nativeProcessFunction.Extent.Text -notmatch 'StandardOutputEncoding\s*=\s*\[System\.Text\.UTF8Encoding\]::new\(\$false\)' -or
    $nativeProcessFunction.Extent.Text -notmatch 'StandardErrorEncoding\s*=\s*\[System\.Text\.UTF8Encoding\]::new\(\$false\)' -or
    $nativeProcessFunction.Extent.Text -notmatch '\$startInfo\.ArgumentList\.Add\(') {
    throw 'Native winapp execution must redirect stdout and stderr separately as UTF-8 and pass arguments without shell redirection.'
}
if ($PSVersionTable.PSEdition -eq 'Core') {
    Invoke-Expression $nativeProcessFunction.Extent.Text
    $childPath = (Get-Process -Id $PID -ErrorAction Stop).Path
    $child = Invoke-RedirectedUtf8Process -FileName $childPath -Arguments @('-NoProfile','-NonInteractive','-Command','[Console]::Out.WriteLine("stdout-sentinel"); [Console]::Error.WriteLine("stderr-sentinel")')
    if ($child.ExitCode -ne 0 -or $child.StandardOutput -notmatch 'stdout-sentinel' -or $child.StandardError -notmatch 'stderr-sentinel') {
        throw 'Redirected process smoke did not preserve separate stdout/stderr content and exit status.'
    }
}

$stageFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-GuestProbeStage'
}, $true)
$sourceAssignment = $stageFunction.Body.Find({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$source'
}, $true)
if ($null -eq $sourceAssignment -or $sourceAssignment.Right.Expression -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) {
    throw 'Could not isolate the exact PowerShell source staged for guest UIA probing.'
}
$guestSource = [string]$sourceAssignment.Right.Expression.Value
$guestTokens = $null
$guestParseErrors = $null
$guestAst = [System.Management.Automation.Language.Parser]::ParseInput($guestSource, [ref]$guestTokens, [ref]$guestParseErrors)
if ($guestParseErrors.Count -gt 0) { throw "Embedded guest probe does not parse: $($guestParseErrors | Out-String)" }
$guestModeParameter = @($guestAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'Mode' }) | Select-Object -First 1
if ($null -eq $guestModeParameter) { throw 'Embedded guest probe has no Mode parameter.' }
$guestModeValidateSet = @($guestModeParameter.Attributes | Where-Object { $_.TypeName.FullName -ceq 'ValidateSet' }) | Select-Object -First 1
if ($null -eq $guestModeValidateSet) { throw 'Embedded guest Mode parameter has no ValidateSet.' }
$guestAllowedModes = @($guestModeValidateSet.PositionalArguments | ForEach-Object { [string]$_.Value })
if ($guestAllowedModes -cnotcontains 'focus-state') { throw 'Embedded guest Mode ValidateSet omits focus-state although C09 invokes it.' }
if ($guestAllowedModes -cnotcontains 'process-state') { throw 'Embedded guest Mode ValidateSet omits process-state needed to prove exact opt-in probe exit.' }
if ($guestAllowedModes -cnotcontains 'launch-selection-disable-probe') { throw 'Embedded guest Mode ValidateSet omits the direct launch mode for the opt-in second app.' }
if ([regex]::Matches($guestSource, "(?m)^\s*'focus-state'\s*\{").Count -ne 1) {
    throw 'Embedded guest probe must have exactly one focus-state dispatcher branch.'
}
# Exercise PowerShell's real parameter binder using the exact embedded guest
# param block, but stop before any guest probe body or UI Automation executes.
$guestModeBinder = [ScriptBlock]::Create($guestAst.ParamBlock.Extent.Text + [Environment]::NewLine + 'return [pscustomobject]@{mode=$Mode;automationId=$AutomationId}')
$boundFocusState = & $guestModeBinder -Mode 'focus-state' -ProcessId 4321 -WindowHandle 197292L -AutomationId 'MarkdownSelectionEndHandle'
if ($boundFocusState.mode -cne 'focus-state' -or $boundFocusState.automationId -cne 'MarkdownSelectionEndHandle') {
    throw 'The exact embedded guest parameter binder did not accept and preserve C09 focus-state arguments.'
}
$boundProcessState = & $guestModeBinder -Mode 'process-state' -ProcessId 4321 -WindowHandle 197292L
if ($boundProcessState.mode -cne 'process-state') { throw 'The exact embedded guest parameter binder did not accept the process-state cleanup mode.' }
$boundSelectionDisableLaunch = & $guestModeBinder -Mode 'launch-selection-disable-probe' -ProcessId 4321 -WindowHandle 197292L
if ($boundSelectionDisableLaunch.mode -cne 'launch-selection-disable-probe') { throw 'The exact embedded guest parameter binder did not accept the direct selection-disable probe launch mode.' }
$processStateBranchIndex = $guestSource.IndexOf("if (`$Mode -eq 'process-state')",[StringComparison]::Ordinal)
$windowLookupIndex = $guestSource.IndexOf('$window = Get-AppWindow',[StringComparison]::Ordinal)
if ($processStateBranchIndex -lt 0 -or $windowLookupIndex -lt 0 -or $processStateBranchIndex -ge $windowLookupIndex -or
    $guestSource.IndexOf('Get-Process -Id $ProcessId -ErrorAction SilentlyContinue',$processStateBranchIndex,[StringComparison]::Ordinal) -lt 0 -or
    $guestSource.IndexOf('ConvertTo-AsciiJson -Value $result',$processStateBranchIndex,[StringComparison]::Ordinal) -lt 0) {
    throw 'The guest process-state probe must run and serialize before requiring the app HWND/UIA window, so cleanup can verify a closed app PID.'
}
$guestProcessIdentityFunction=$guestAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-GuestSampleProcessIdentity'},$true)
$guestDirectLaunchFunction=$guestAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Start-SelectionDisableProbeProcess'},$true)
if($null -eq $guestProcessIdentityFunction -or $null -eq $guestDirectLaunchFunction){throw 'The exact embedded guest source must expose its process-derived, direct selection-disable launcher for offline functional validation.'}
& {
    Set-StrictMode -Version Latest
    $script:MockLaunchProcesses=@{}
    $script:MockLaunchCalls=[Collections.Generic.List[object]]::new()
    $script:MockLaunchScenario='normal'
    function Get-Process {
        [CmdletBinding()]
        param([Parameter(Mandatory)][int]$Id)
        if($script:MockLaunchProcesses.ContainsKey($Id)){return $script:MockLaunchProcesses[$Id]}
        return $null
    }
    function Start-Process {
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$FilePath,[Parameter(Mandatory)][string]$WorkingDirectory,
            [Parameter(Mandatory)][string[]]$ArgumentList,[switch]$PassThru,[ValidateSet('Normal','Hidden')][string]$WindowStyle)
        $script:MockLaunchCalls.Add([pscustomobject]@{filePath=$FilePath;workingDirectory=$WorkingDirectory;arguments=@($ArgumentList);passThru=[bool]$PassThru;windowStyle=$WindowStyle})
        $script:MockLaunchProcesses[202]=[pscustomobject]@{Id=202;HasExited=$false;ProcessName='MarkdownRenderer.Sample';Path='C:\fixture\MarkdownRenderer.Sample.exe';StartTime=[DateTime]::SpecifyKind([DateTime]::Parse('2026-10-12T23:01:00.0000000'),[DateTimeKind]::Utc)}
        if($script:MockLaunchScenario -ceq 'original-replaced-after-launch'){
            $script:MockLaunchProcesses[101]=[pscustomobject]@{Id=101;HasExited=$false;ProcessName='MarkdownRenderer.Sample';Path='C:\fixture\MarkdownRenderer.Sample.exe';StartTime=[DateTime]::SpecifyKind([DateTime]::Parse('2026-10-12T23:00:01.0000000'),[DateTimeKind]::Utc)}
        }
        return [pscustomobject]@{Id=202}
    }
    Invoke-Expression $guestProcessIdentityFunction.Extent.Text
    Invoke-Expression $guestDirectLaunchFunction.Extent.Text
    $script:MockLaunchProcesses[101]=[pscustomobject]@{Id=101;HasExited=$false;ProcessName='MarkdownRenderer.Sample';Path='C:\fixture\MarkdownRenderer.Sample.exe';StartTime=[DateTime]::SpecifyKind([DateTime]::Parse('2026-10-12T23:00:00.0000000'),[DateTimeKind]::Utc)}
    $goodLaunch=Start-SelectionDisableProbeProcess -OriginalProcessId 101
    if($goodLaunch.mode -cne 'launch-selection-disable-probe' -or $goodLaunch.processId -ne 202 -or -not $goodLaunch.sourceRetained -or
        $script:MockLaunchCalls.Count -ne 1 -or $script:MockLaunchCalls[0].filePath -cne 'C:\fixture\MarkdownRenderer.Sample.exe' -or
        $script:MockLaunchCalls[0].workingDirectory -cne 'C:\fixture' -or $script:MockLaunchCalls[0].arguments.Count -ne 1 -or
        $script:MockLaunchCalls[0].arguments[0] -cne '--renderer-selection-disable-probe' -or -not $script:MockLaunchCalls[0].passThru -or
        $script:MockLaunchCalls[0].windowStyle -cne 'Normal'){
        throw 'The embedded guest launcher did not start a distinct visible child from the exact original process path with the one fixed probe argument.'
    }
    $script:MockLaunchCalls.Clear()
    $script:MockLaunchProcesses[101]=[pscustomobject]@{Id=101;HasExited=$false;ProcessName='powershell';Path='C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe';StartTime=[DateTime]::UtcNow}
    $wrongSourceRejected=$false
    try{Start-SelectionDisableProbeProcess -OriginalProcessId 101|Out-Null}catch{$wrongSourceRejected=$true}
    if(-not $wrongSourceRejected -or $script:MockLaunchCalls.Count -ne 0){throw 'The guest launcher accepted a non-sample PID or launched before validating the exact source process.'}
    $script:MockLaunchProcesses[101]=[pscustomobject]@{Id=101;HasExited=$false;ProcessName='MarkdownRenderer.Sample';Path='relative\MarkdownRenderer.Sample.exe';StartTime=[DateTime]::UtcNow}
    $unrootedSourceRejected=$false
    try{Start-SelectionDisableProbeProcess -OriginalProcessId 101|Out-Null}catch{$unrootedSourceRejected=$true}
    if(-not $unrootedSourceRejected -or $script:MockLaunchCalls.Count -ne 0){throw 'The guest launcher trusted a relative or caller-controlled executable path.'}
    $script:MockLaunchProcesses[101]=[pscustomobject]@{Id=101;HasExited=$false;ProcessName='MarkdownRenderer.Sample';Path='C:\fixture\MarkdownRenderer.Sample.exe';StartTime=[DateTime]::SpecifyKind([DateTime]::Parse('2026-10-12T23:00:00.0000000'),[DateTimeKind]::Utc)}
    $script:MockLaunchScenario='original-replaced-after-launch'
    $changedSourceLaunch=Start-SelectionDisableProbeProcess -OriginalProcessId 101
    if($changedSourceLaunch.processId -ne 202 -or $changedSourceLaunch.sourceRetained){throw 'The guest launcher lost the new PID recovery receipt or reported a changed original process as retained.'}
    Remove-Item Function:\Get-Process,Function:\Start-Process -ErrorAction SilentlyContinue
}
$checks.Add([pscustomobject]@{name='embedded guest launch helper functionally starts the exact original sample executable with one fixed argument, visible app/hidden helper policy, and rejects untrusted source PID/path while retaining a cleanup PID if original identity changes';status='PASS'})
& {
    $savedGuestProbePathVariable=Get-Variable -Name GuestProbePath -Scope Script -ErrorAction SilentlyContinue
    $savedAppProcessIdVariable=Get-Variable -Name AppProcessId -Scope Script -ErrorAction SilentlyContinue
    $savedAppWindowHandleVariable=Get-Variable -Name AppWindowHandle -Scope Script -ErrorAction SilentlyContinue
    $hadGuestProbePath=$null -ne $savedGuestProbePathVariable
    $hadAppProcessId=$null -ne $savedAppProcessIdVariable
    $hadAppWindowHandle=$null -ne $savedAppWindowHandleVariable
    try{
        $script:GuestProbePath='C:\offline\GuestUiProbe.ps1'
        $script:AppProcessId=111
        $script:AppWindowHandle=222L
        & {
            $script:CapturedGuestProbeCalls=[Collections.Generic.List[object]]::new()
            function Invoke-NativeChecked {
                [CmdletBinding()]
                param([Parameter(Mandatory)][string[]]$Arguments,[switch]$Json)
                $script:CapturedGuestProbeCalls.Add([pscustomobject]@{arguments=@($Arguments);json=[bool]$Json})
                $payload=if($script:CapturedGuestProbeCalls.Count -eq 1){'{"mode":"process-state","processId":333,"exists":false}'}else{'{"mode":"launch-selection-disable-probe","processId":444}'}
                return [pscustomobject]@{value=[pscustomobject]@{stdout=$payload};output=''}
            }
            Invoke-Expression $guestProbeFunction.Extent.Text
            $processResult=Invoke-GuestProbe -Mode 'process-state' -TargetProcessId 333
            if($processResult.processId -ne 333){throw 'The host guest-probe wrapper did not return its exact target process-state payload.'}
            $processArgs=@($script:CapturedGuestProbeCalls[0].arguments)
            $processIdIndex=[Array]::IndexOf($processArgs,'-ProcessId')
            if($processIdIndex -lt 0 -or $processArgs[$processIdIndex+1] -cne '333' -or [Array]::IndexOf($processArgs,'run') -ge 0 -or
                [Array]::IndexOf($processArgs,'--') -ge [Array]::IndexOf($processArgs,'powershell.exe') -or [Array]::IndexOf($processArgs,'--json') -ge [Array]::IndexOf($processArgs,'--')){
                throw 'A read-only guest process identity query was not routed once through target exec to the requested PID.'
            }
            $launchResult=Invoke-GuestProbe -Mode 'launch-selection-disable-probe'
            if($launchResult.mode -cne 'launch-selection-disable-probe'){throw 'The host guest-probe wrapper did not preserve the actual direct-launch guest receipt.'}
            $launchArgs=@($script:CapturedGuestProbeCalls[1].arguments)
            $launchPidIndex=[Array]::IndexOf($launchArgs,'-ProcessId')
            $helperWindowIndex=[Array]::IndexOf($launchArgs,'-WindowStyle')
            if($launchPidIndex -lt 0 -or $launchArgs[$launchPidIndex+1] -cne '111' -or $helperWindowIndex -lt 0 -or $launchArgs[$helperWindowIndex+1] -cne 'Hidden' -or
                [Array]::IndexOf($launchArgs,'--') -ge [Array]::IndexOf($launchArgs,'powershell.exe') -or [Array]::IndexOf($launchArgs,'--json') -ge [Array]::IndexOf($launchArgs,'--') -or
                [Array]::IndexOf($launchArgs,'run') -ge 0){throw 'The direct launch helper did not remain hidden and target-exec-routed to the captured original PID without invoking project deployment.'}
            $beforeCalls=$script:CapturedGuestProbeCalls.Count
            $overrideRejected=$false
            try{Invoke-GuestProbe -Mode 'metadata' -TargetProcessId 333|Out-Null}catch{$overrideRejected=$true}
            $negativePidRejected=$false
            try{Invoke-GuestProbe -Mode 'process-state' -TargetProcessId -1|Out-Null}catch{$negativePidRejected=$true}
            if(-not $overrideRejected -or -not $negativePidRejected -or $script:CapturedGuestProbeCalls.Count -ne $beforeCalls){throw 'An arbitrary/negative PID override escaped the read-only process-state query restriction.'}
        }
    }
    finally{
        if($hadGuestProbePath){$script:GuestProbePath=$savedGuestProbePathVariable.Value}else{Remove-Variable -Name GuestProbePath -Scope Script -ErrorAction SilentlyContinue}
        if($hadAppProcessId){$script:AppProcessId=$savedAppProcessIdVariable.Value}else{Remove-Variable -Name AppProcessId -Scope Script -ErrorAction SilentlyContinue}
        if($hadAppWindowHandle){$script:AppWindowHandle=$savedAppWindowHandleVariable.Value}else{Remove-Variable -Name AppWindowHandle -Scope Script -ErrorAction SilentlyContinue}
    }
}
$checks.Add([pscustomobject]@{name='host guest-probe wrapper functionally target-execs the exact original PID for process-state, hides the one direct-launch PowerShell helper, and rejects arbitrary process overrides without project redeployment';status='PASS'})
$unknownModeRejected = $false
try { & $guestModeBinder -Mode 'not-a-probe-mode' -ProcessId 4321 -WindowHandle 197292L -AutomationId 'MarkdownSelectionEndHandle' | Out-Null }
catch { $unknownModeRejected = $true }
if (-not $unknownModeRejected) { throw 'The embedded guest Mode ValidateSet accepted an unknown probe mode.' }
$checks.Add([pscustomobject]@{name='exact embedded guest binder accepts C09 focus-state, process-state, and direct probe-launch modes, rejects unknown modes, and process-state bypasses app HWND/UIA lookup';status='PASS'})
$rawWindowRootsFunction=$guestAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ProcessWindowRoots'},$true)
$rawProcessElementsFunction=$guestAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ProcessElements'},$true)
$rawUniqueResolverFunction=$guestAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-UniqueProcessAutomationId'},$true)
$rawUniqueFinderFunction=$guestAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Find-UniqueProcessElement'},$true)
if($null -eq $rawWindowRootsFunction -or $null -eq $rawProcessElementsFunction -or $null -eq $rawUniqueResolverFunction -or $null -eq $rawUniqueFinderFunction){
    throw 'Embedded guest probe must expose exact-PID window roots and unique raw-process AutomationId lookup.'
}
$rawWindowRootsText=$rawWindowRootsFunction.Extent.Text
$rawProcessElementsText=$rawProcessElementsFunction.Extent.Text
$rawUniqueFinderText=$rawUniqueFinderFunction.Extent.Text
foreach($required in @(
    @{Source=$rawWindowRootsText;Text='CacheRequest]::new()'},
    @{Source=$rawWindowRootsText;Text='TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition'},
    @{Source=$rawWindowRootsText;Text='$rawScope=$cache.Activate()'},
    @{Source=$rawWindowRootsText;Text='$rawScope.Dispose()'},
    @{Source=$rawProcessElementsText;Text='CacheRequest]::new()'},
    @{Source=$rawProcessElementsText;Text='TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition'},
    @{Source=$rawProcessElementsText;Text='$rawScope=$cache.Activate()'},
    @{Source=$rawProcessElementsText;Text='FindAll([System.Windows.Automation.TreeScope]::Descendants'},
    @{Source=$rawProcessElementsText;Text='Condition]::TrueCondition'},
    @{Source=$rawProcessElementsText;Text='$rootCurrent.ProcessId -ne $TargetProcessId'},
    @{Source=$rawProcessElementsText;Text='$rootHandle -le 0'},
    @{Source=$rawProcessElementsText;Text='$elementProcessId -ne $TargetProcessId -and $elementProcessId -ne 0'},
    @{Source=$rawProcessElementsText;Text='ownerProcessId=$TargetProcessId'},
    @{Source=$rawProcessElementsText;Text='ownerWindowHandle=$rootHandle'},
    @{Source=$rawProcessElementsText;Text='reportedProcessId=$elementProcessId'},
    @{Source=$rawProcessElementsText;Text='GetRuntimeId'},
    @{Source=$rawProcessElementsText;Text='$rawScope.Dispose()'},
    @{Source=$rawUniqueFinderText;Text='Get-ProcessElements'},
    @{Source=$rawUniqueFinderText;Text='Resolve-UniqueProcessAutomationId'}
)){
    if($required.Source.IndexOf($required.Text,[StringComparison]::Ordinal) -lt 0){throw "Embedded guest raw UIA lookup is missing '$($required.Text)' or no longer activates RawViewCondition around FindAll."}
}
$rawUniqueResolverText=$rawUniqueResolverFunction.Extent.Text
foreach($required in @(
    '$ownerProcessId -ne $TargetProcessId -or $ownerWindowHandle -le 0',
    '$candidate.element.Current',
    '$candidate.reportedProcessId -eq $reportedProcessId',
    '$reportedProcessId -eq 0'
)){
    if($rawUniqueResolverText.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Embedded resolver omits verified-root PID-zero provenance check '$required'."}
}
if($rawProcessElementsText -match 'TreeWalker\]::ControlViewWalker' -or
   $rawProcessElementsText -match 'TreeWalker\]::RawViewWalker' -or
   $rawProcessElementsText -notmatch 'finally\s*\{\s*\$rawScope\.Dispose\(\)\s*\}'){
    throw 'Embedded guest UIA process scan must use a disposed RawViewCondition cache request rather than an unfiltered TreeWalker.'
}
Invoke-Expression $rawUniqueResolverFunction.Extent.Text
# Keep the collector's production logic intact except for the candidate-list
# element type, which lets this offline test supply AutomationElement-shaped
# mock roots. The production provenance-envelope list remains exactly as
# authored, so the runtime Add exercised below catches an accidental
# List[AutomationElement] declaration on that outer collection.
$mockCandidateDeclaration='$candidates=[System.Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()'
$mockCandidateCount=[regex]::Matches($rawProcessElementsText,[regex]::Escape($mockCandidateDeclaration)).Count
if($mockCandidateCount -ne 1){throw "Expected exactly one production AutomationElement candidate-list declaration for mock adaptation; found $mockCandidateCount."}
$collectorTestSource=$rawProcessElementsText.Replace($mockCandidateDeclaration,'$candidates=[System.Collections.Generic.List[object]]::new()')
Invoke-Expression $collectorTestSource
$rawPageElement=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=0;AutomationId='CurrentSamplePage';Name='page:TouchSelection'}}
$foreignRawPageElement=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=4322;AutomationId='CurrentSamplePage';Name='foreign-window'}}
$rawEditorElement=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=4321;AutomationId='MarkdownEditor';Name='MarkdownEditor'}}
$rawPage=[pscustomobject]@{element=$rawPageElement;ownerProcessId=4321;ownerWindowHandle=0x30003;reportedProcessId=0}
$foreignRawPage=[pscustomobject]@{element=$foreignRawPageElement;ownerProcessId=4321;ownerWindowHandle=0x30003;reportedProcessId=4322}
$rawEditor=[pscustomobject]@{element=$rawEditorElement;ownerProcessId=4321;ownerWindowHandle=0x30003;reportedProcessId=4321}
$foreignRootZero=[pscustomobject]@{element=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=0;AutomationId='CurrentSamplePage';Name='foreign-root'}};ownerProcessId=4322;ownerWindowHandle=0x40004;reportedProcessId=0}
$resolvedRawPage=Resolve-UniqueProcessAutomationId -Elements @($foreignRawPage,$rawEditor,$rawPage) -TargetProcessId 4321 -Id 'CurrentSamplePage'
if(-not [object]::ReferenceEquals($resolvedRawPage,$rawPageElement)){throw 'Embedded guest raw resolver did not accept the PID-zero CurrentSamplePage beneath an exact verified same-process HWND.'}
foreach($rawCase in @(
    [pscustomobject]@{Name='missing';Elements=@($foreignRawPage,$rawEditor,$foreignRootZero);Expected=0},
    [pscustomobject]@{Name='duplicate same-root PID-zero';Elements=@($rawPage,[pscustomobject]@{element=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=0;AutomationId='CurrentSamplePage';Name='duplicate'}};ownerProcessId=4321;ownerWindowHandle=0x30003;reportedProcessId=0});Expected=2},
    [pscustomobject]@{Name='foreign-root PID-zero';Elements=@($foreignRootZero);Expected=0},
    [pscustomobject]@{Name='foreign nonzero descendant';Elements=@($foreignRawPage);Expected=0},
    [pscustomobject]@{Name='unverified root handle';Elements=@([pscustomobject]@{element=$rawPageElement;ownerProcessId=4321;ownerWindowHandle=0;reportedProcessId=0});Expected=0}
)){
    $rejected=$false
    try{Resolve-UniqueProcessAutomationId -Elements $rawCase.Elements -TargetProcessId 4321 -Id 'CurrentSamplePage' | Out-Null}catch{$rejected=$_.Exception.Message.IndexOf("found $($rawCase.Expected).",[StringComparison]::Ordinal) -ge 0}
    if(-not $rejected){throw "Embedded guest raw resolver did not reject $($rawCase.Name) CurrentSamplePage candidates."}
}
$checks.Add([pscustomobject]@{name='verified raw-root resolver accepts a PID-zero local descendant and rejects PID-zero foreign-root, explicit foreign-process, missing, duplicate, and unverified-root candidates';status='PASS'})

# Exercise the actual collector, not just its source text and resolver. The
# collector emits provenance envelopes (element + verified owner HWND/PID),
# so a typed List[AutomationElement] must fail on its Add call. Mock the UIA
# shape at the verified-window boundary and keep all descendants in memory.
function New-MockAutomationElement {
    param([int]$ElementProcessId,[long]$NativeWindowHandle,[string]$AutomationId,[string]$Name,[int[]]$RuntimeId)
    $element=[pscustomobject]@{
        Current=[pscustomobject]@{ProcessId=$ElementProcessId;NativeWindowHandle=$NativeWindowHandle;AutomationId=$AutomationId;Name=$Name}
        RuntimeId=$RuntimeId
        Children=[Collections.ArrayList]::new()
    }
    $element | Add-Member -MemberType ScriptMethod -Name GetRuntimeId -Value { return ,([int[]]$this.RuntimeId) }
    $element | Add-Member -MemberType ScriptMethod -Name FindAll -Value { param($TreeScope,$Condition) return ,$this.Children }
    return $element
}
$collectorRoot=New-MockAutomationElement -ElementProcessId 4321 -NativeWindowHandle 0x30003 -AutomationId 'VerifiedWindow' -Name 'Mock approved app window' -RuntimeId @(10,1)
$collectorLocalPeer=New-MockAutomationElement -ElementProcessId 0 -NativeWindowHandle 0 -AutomationId 'CurrentSamplePage' -Name 'page:TouchSelection' -RuntimeId @(10,2)
$collectorAppPeer=New-MockAutomationElement -ElementProcessId 4321 -NativeWindowHandle 0 -AutomationId 'MarkdownEditor' -Name 'MarkdownEditor' -RuntimeId @(10,3)
$collectorForeignPeer=New-MockAutomationElement -ElementProcessId 4322 -NativeWindowHandle 0 -AutomationId 'ForeignPeer' -Name 'must be excluded' -RuntimeId @(10,4)
[void]$collectorRoot.Children.Add($collectorLocalPeer)
[void]$collectorRoot.Children.Add($collectorAppPeer)
[void]$collectorRoot.Children.Add($collectorForeignPeer)
$script:MockVerifiedProcessRoots=@($collectorRoot)
function Get-ProcessWindowRoots {
    param([int]$TargetProcessId)
    if($TargetProcessId -ne 4321){throw "Mock collector refused unapproved PID $TargetProcessId."}
    if($script:MockVerifiedProcessRoots.Count -ne 1 -or
        [int]$script:MockVerifiedProcessRoots[0].Current.ProcessId -ne 4321 -or
        [long]$script:MockVerifiedProcessRoots[0].Current.NativeWindowHandle -ne 0x30003){
        throw 'Mock collector roots are not the exact verified app PID/HWND.'
    }
    return ,$script:MockVerifiedProcessRoots
}
$collected= @(Get-ProcessElements -TargetProcessId 4321)
if($collected.Count -ne 3){throw "Actual raw-process collector should retain the verified root, PID-zero local peer, and same-process peer while excluding foreign descendants; got $($collected.Count)."}
$collectedPage=Resolve-UniqueProcessAutomationId -Elements $collected -TargetProcessId 4321 -Id 'CurrentSamplePage'
if(-not [object]::ReferenceEquals($collectedPage,$collectorLocalPeer)){throw 'Actual raw-process collector lost the PID-zero CurrentSamplePage beneath the verified root.'}
if(@($collected | Where-Object { $_.element -eq $collectorForeignPeer }).Count -ne 0){throw 'Actual raw-process collector admitted a foreign-process descendant.'}
if(@($collected | Where-Object { $_.ownerProcessId -ne 4321 -or $_.ownerWindowHandle -ne 0x30003 }).Count -ne 0){throw 'Actual raw-process collector emitted an unqualified provenance envelope.'}
$checks.Add([pscustomobject]@{name='actual raw-process collector accepts a PID-zero peer under a verified mock HWND, emits provenance envelopes, and excludes foreign descendants';status='PASS'})

$touchDeliveryAssertionAst=$guestAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-SyntheticTouchDelivery'
},$true)
if($null -eq $touchDeliveryAssertionAst){throw 'Embedded guest probe must independently validate actual routed touch delivery without assuming injected IDs.'}
Invoke-Expression $touchDeliveryAssertionAst.Extent.Text
$rootClientPointFunctionAst=$guestAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-ExpectedRootClientPoint'
},$true)
if($null -eq $rootClientPointFunctionAst){throw 'Embedded guest probe must convert physical screen points through the XamlRoot scale and HWND client origin.'}
Invoke-Expression $rootClientPointFunctionAst.Extent.Text
$asciiJsonFunction = $guestAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-AsciiJson'
}, $true)
if ($null -eq $asciiJsonFunction) { throw 'Embedded guest probe does not define ASCII-safe JSON serialization for emoji UIA receipts.' }
Invoke-Expression $asciiJsonFunction.Extent.Text
$cancelBefore=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@();contacts=@(
    [pscustomobject]@{pointerId=3701;contactSerial=6;pressCount=4;moveCount=8;cancelCount=0;releaseCount=3;captureLostCount=0;lastEventSequence=50;inContact=$false;lastEvent='Released';handle=$null;lastPositionDipX=12.5;lastPositionDipY=14.0;lastPositionRootDipX=12.5;lastPositionRootDipY=14.0;eventRasterizationScale=2.0}
)}
$cancelAfter=[pscustomobject]@{
    version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@()
    contacts=@([pscustomobject]@{pointerId=3701;contactSerial=7;pressCount=5;moveCount=9;cancelCount=1;releaseCount=3;captureLostCount=1;lastEventSequence=54;inContact=$false;lastEvent='CaptureLost';handle=$null;lastPositionDipX=12.75;lastPositionDipY=14.25;lastPositionRootDipX=12.5;lastPositionRootDipY=14.0;eventRasterizationScale=2.0})
}
$expectedTouchPoint=[pscustomobject]@{screenX=1025.0;screenY=528.0;windowDpi=192;clientOriginScreenPx=[pscustomobject]@{x=1000.0;y=500.0}}
$cancelEvidence=Assert-SyntheticTouchDelivery -Before $cancelBefore -After $cancelAfter -ExpectedPoint $expectedTouchPoint -ExpectedState 'Canceled'
if($cancelEvidence.status -cne 'PASS' -or $cancelEvidence.cancelCount -ne 1 -or $cancelEvidence.releaseCount -ne 3 -or $cancelEvidence.activePointerCount -ne 0 -or $cancelEvidence.pointerId -ne 3701 -or $cancelEvidence.pointerId -eq 0 -or $cancelEvidence.rasterizationScale -ne 2.0){throw 'The validator did not report the actual routed pointer identity and its canceled-contact evidence.'}
$nativeCanceledRelease = $cancelAfter | ConvertTo-Json -Depth 8 | ConvertFrom-Json
$nativeCanceledRelease.contacts[0].captureLostCount = 0
$nativeCanceledRelease.contacts[0].lastEvent = 'Canceled'
$nativeCanceledRelease.contacts[0] | Add-Member -NotePropertyName rawReleaseCount -NotePropertyValue 4
$nativeCanceledRelease.contacts[0] | Add-Member -NotePropertyName nativeCanceledPacketCount -NotePropertyValue 1
$nativeCanceledRelease.contacts[0] | Add-Member -NotePropertyName lastRoutedEvent -NotePropertyValue 'Released'
$nativeCanceledRelease.contacts[0] | Add-Member -NotePropertyName lastNativeCanceled -NotePropertyValue $true
$null = Assert-SyntheticTouchDelivery -Before $cancelBefore -After $nativeCanceledRelease -ExpectedPoint $expectedTouchPoint -ExpectedState 'Canceled'
foreach ($nativeProofFailure in @('lastNativeCanceled', 'nativeCanceledPacketCount')) {
    $changedProof = $nativeCanceledRelease | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $changedProof.contacts[0].$nativeProofFailure = 0
    $rejected = $false
    try { Assert-SyntheticTouchDelivery -Before $cancelBefore -After $changedProof -ExpectedPoint $expectedTouchPoint -ExpectedState 'Canceled' | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "A raw release was relabeled as cancellation without valid $nativeProofFailure evidence." }
}
$pointerInputSourcePath = Join-Path $PSScriptRoot '..\MarkdownRenderer\Controls\MarkdownRendererControl.PointerInput.cs'
$pointerInputSource = Get-Content -LiteralPath $pointerInputSourcePath -Raw
$dispatcherStart = $pointerInputSource.IndexOf('private void DispatchPointerInput(', [StringComparison]::Ordinal)
$dispatcherEnd = $pointerInputSource.IndexOf('private DispatcherQueueTimer CreatePointerRetryTimer()', $dispatcherStart, [StringComparison]::Ordinal)
$dispatcherBody = $pointerInputSource.Substring($dispatcherStart, $dispatcherEnd - $dispatcherStart)
$cancellationClassification = $dispatcherBody.IndexOf('NormalizeCanceledPointerInputKind(kind, point.Properties.IsCanceled)', [StringComparison]::Ordinal)
if ($cancellationClassification -lt 0 -or
    $dispatcherBody.IndexOf('Kind = kind', [StringComparison]::Ordinal) -lt $cancellationClassification -or
    $dispatcherBody.IndexOf('snapshot.TryBeginInteraction()', [StringComparison]::Ordinal) -lt $cancellationClassification -or
    $dispatcherBody.IndexOf('_deferredPointerInput.TryEnqueue', [StringComparison]::Ordinal) -lt $cancellationClassification) {
    throw 'Native canceled input must be classified before input capture, live dispatch and deferred enqueue.'
}
$checks.Add([pscustomobject]@{name='native IsCanceled release retains raw-event evidence and cannot masquerade as ordinary release; real dispatcher normalizes before live and deferred paths';status='PASS'})
$noCancelSnapshot = $null
foreach($invalidCancellation in @(
    [pscustomobject]@{Name='no cancel event';Snapshot=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@();contacts=@([pscustomobject]@{pointerId=3701;contactSerial=7;pressCount=5;moveCount=9;cancelCount=0;releaseCount=4;captureLostCount=0;lastEventSequence=54;inContact=$false;lastEvent='Released';handle=$null;lastPositionRootDipX=12.5;lastPositionRootDipY=14.0;eventRasterizationScale=2.0})}},
    [pscustomobject]@{Name='still active';Snapshot=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@([pscustomobject]@{pointerId=3701;contactSerial=7});contacts=@([pscustomobject]@{pointerId=3701;contactSerial=7;pressCount=5;moveCount=9;cancelCount=1;releaseCount=3;captureLostCount=0;lastEventSequence=54;inContact=$true;lastEvent='Canceled';handle=$null;lastPositionRootDipX=12.5;lastPositionRootDipY=14.0;eventRasterizationScale=2.0})}},
    [pscustomobject]@{Name='degraded observer';Snapshot=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='degraded';observationErrors=1;active=@();contacts=@([pscustomobject]@{pointerId=3701;contactSerial=7;pressCount=5;moveCount=9;cancelCount=1;releaseCount=3;captureLostCount=0;lastEventSequence=54;inContact=$false;lastEvent='Canceled';handle=$null;lastPositionRootDipX=12.5;lastPositionRootDipY=14.0;eventRasterizationScale=2.0})}},
    [pscustomobject]@{Name='changed renderer generation';Snapshot=[pscustomobject]@{version=1;rendererGeneration=13;observationStatus='ok';observationErrors=0;active=@();contacts=@([pscustomobject]@{pointerId=3701;contactSerial=7;pressCount=5;moveCount=9;cancelCount=1;releaseCount=3;captureLostCount=0;lastEventSequence=54;inContact=$false;lastEvent='Canceled';handle=$null;lastPositionRootDipX=12.5;lastPositionRootDipY=14.0;eventRasterizationScale=2.0})}},
    [pscustomobject]@{Name='wrong root-client coordinate';Snapshot=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@();contacts=@([pscustomobject]@{pointerId=3701;contactSerial=7;pressCount=5;moveCount=9;cancelCount=1;releaseCount=3;captureLostCount=0;lastEventSequence=54;inContact=$false;lastEvent='Canceled';handle=$null;lastPositionRootDipX=44.0;lastPositionRootDipY=50.0;eventRasterizationScale=2.0})}},
    [pscustomobject]@{Name='ambiguous new routed contacts';Snapshot=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@();contacts=@(
        [pscustomobject]@{pointerId=3701;contactSerial=7;pressCount=5;moveCount=9;cancelCount=1;releaseCount=3;captureLostCount=0;lastEventSequence=54;inContact=$false;lastEvent='Canceled';handle=$null;lastPositionDipX=12.5;lastPositionDipY=14.0;lastPositionRootDipX=12.5;lastPositionRootDipY=14.0;eventRasterizationScale=2.0},
        [pscustomobject]@{pointerId=3702;contactSerial=1;pressCount=1;moveCount=1;cancelCount=1;releaseCount=0;captureLostCount=0;lastEventSequence=55;inContact=$false;lastEvent='Canceled';handle=$null;lastPositionDipX=12.5;lastPositionDipY=14.0;lastPositionRootDipX=12.5;lastPositionRootDipY=14.0;eventRasterizationScale=2.0}
    )}}
)){
    if ($invalidCancellation.Name -ceq 'no cancel event') { $noCancelSnapshot = $invalidCancellation.Snapshot }
    $rejected=$false
    try{Assert-SyntheticTouchDelivery -Before $cancelBefore -After $invalidCancellation.Snapshot -ExpectedPoint $expectedTouchPoint -ExpectedState 'Canceled' | Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw "Canceled-contact observer accepted $($invalidCancellation.Name)."}
}
$cancelFailureMessage = ''
try {
    Assert-SyntheticTouchDelivery -Before $cancelBefore -After $noCancelSnapshot -ExpectedPoint $expectedTouchPoint -ExpectedState 'Canceled' | Out-Null
} catch {
    $cancelFailureMessage = $_.Exception.Message
}
if ($cancelFailureMessage -notmatch 'did not cancel without an ordinary release\. Diagnostics: ') {
    throw 'A rejected canceled contact must preserve the original cancellation failure and attach structured diagnostics.'
}
$cancelDiagnosticJson = $cancelFailureMessage.Substring($cancelFailureMessage.IndexOf('Diagnostics: ',[StringComparison]::Ordinal) + 'Diagnostics: '.Length)
$cancelDiagnostics = ConvertFrom-Json -InputObject $cancelDiagnosticJson
foreach ($requiredCancellationField in @('pointerId','contactSerial','pressDelta','moveDelta','cancelDelta','releaseDelta','captureLostDelta','activePointerCount','inContact','lastEvent','lastEventSequence','positionErrorDip','expectedRootPoint','observerBefore','observerAfter')) {
    if ($cancelDiagnostics.PSObject.Properties.Name -cnotcontains $requiredCancellationField) { throw "Cancellation failure diagnostics omitted '$requiredCancellationField'." }
}
if ([uint32]$cancelDiagnostics.pointerId -ne 3701 -or [long]$cancelDiagnostics.contactSerial -ne 7 -or
    [int]$cancelDiagnostics.pressDelta -ne 1 -or [int]$cancelDiagnostics.moveDelta -ne 1 -or
    [int]$cancelDiagnostics.cancelDelta -ne 0 -or [int]$cancelDiagnostics.releaseDelta -ne 1 -or
    [int]$cancelDiagnostics.captureLostDelta -ne 0 -or [int]$cancelDiagnostics.activePointerCount -ne 0 -or
    [bool]$cancelDiagnostics.inContact -or [string]$cancelDiagnostics.lastEvent -cne 'Released' -or
    [string]$cancelDiagnostics.observerBefore.observationStatus -cne 'ok' -or
    [string]$cancelDiagnostics.observerAfter.observationStatus -cne 'ok' -or
    [int]$cancelDiagnostics.observerBefore.observationErrors -ne 0 -or
    [int]$cancelDiagnostics.observerAfter.observationErrors -ne 0) {
    throw 'Cancellation diagnostics did not preserve the measured failing phase deltas and both healthy observer snapshots.'
}
$checks.Add([pscustomobject]@{name='cancellation validator correlates actual routed ID/contact serial/phase/XamlRoot-client coordinate across generations and rejects no-cancel, active, degraded, wrong-position, and ambiguous contacts';status='PASS'})
$mainC09=$harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Run-CorrectnessCases'
},$true)
if($null -eq $mainC09){throw 'Could not find the finite main correctness batch for keyboard endpoint coverage.'}
$mainUiText=(Get-Content -LiteralPath $harnessPath -Raw)
foreach($required in @(
    "Test-Case -Id 'C09' -Name 'mouse-selection-crosses-lines-formatting-lists-and-table-and-keyboard-adjusts-handle-endpoint'",
    "Invoke-Ui @('touch','MarkdownRenderer','--gesture','double-tap','--at'",
    "Assert-SelectionHandles",
    "Invoke-Ui @('focus','MarkdownSelectionEndHandle')",
    "Invoke-GuestProbe -Mode 'focus-state' -AutomationId 'MarkdownSelectionEndHandle'",
    "Invoke-Ui @('send-keys','right','--via','send-input')",
    "Invoke-Ui @('send-keys','left','--via','send-input')",
    "'keyboardTarget,'",
    "Test-Case -Id 'P1-003'",
    '$injection.delivery.cancelDelta',
    '$injection.delivery.releaseDelta',
    '$injection.delivery.pressDelta',
    '$injection.delivery.moveDelta',
    '$injection.delivery.activePointerCount'
)){
    if($mainUiText.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "The frozen UI batch lacks keyboard/cancel delivery assertion '$required'."}
}
$checks.Add([pscustomobject]@{name='finite C09 includes keyboard endpoint extension/contraction and P1-003 consumes per-contact cancellation-event deltas';status='PASS'})
$handleDragCase = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Test-Case' -and
    $node.Extent.Text.Contains("-Name 'touch-double-tap-handle-drag-context-copy-and-cancel'")
}, $true)
if ($null -eq $handleDragCase) { throw 'Could not find the frozen C10 handle-drag case.' }
$handleDragText = $handleDragCase.Extent.Text
foreach ($required in @(
    '$blankObserverBefore = Get-HostTouchContactObservation',
    '$blankObserverAfter = Get-HostTouchContactObservation',
    'observerBefore=$blankObserverBefore;observerAfter=$blankObserverAfter',
    'Assert-HostTouchHandleDragDelivery -Before $blankObserverBefore -After $blankObserverAfter',
    '-ExpectedPoint $blankExpectedPoint -ExpectedState Released -ExpectedHandle $startHandle',
    '[string]$blankDelivery.handle -cne $startHandle -or [int]$blankDelivery.moveDelta -lt 1',
    '$blankAfter.StartsWith(''touchtarget'',[StringComparison]::Ordinal)',
    '[int]$blankSelection.rangeCount -eq 1',
    '[string]$blankSelection.text -ceq $blankAfter',
    'if (-not $hasBlankRange)',
    '$observerBefore = Get-HostTouchContactObservation',
    '$observerAfter = Get-HostTouchContactObservation',
    'observerBefore=$observerBefore;observerAfter=$observerAfter',
    'Assert-HostTouchHandleDragDelivery -Before $observerBefore -After $observerAfter',
    "-ExpectedPoint `$expectedPoint -ExpectedState Released -ExpectedHandle 'MarkdownSelectionEndHandle'",
    "[string]`$delivery.handle -cne 'MarkdownSelectionEndHandle' -or [int]`$delivery.moveDelta -lt 1",
    '$extended -cne $expectedExtended',
    '[string]$selectionAfter.text -cne $expectedExtended',
    'Format-TouchHandleDragFailure -Reason $_.Exception.Message -Diagnostics $dragDiagnostics',
    'blankSpaceNonCollapse=',
    'exactExtensionByViewport=@($extensionRuns.ToArray())'
)) {
    if ($handleDragText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "The C10 drag must retain native delivery evidence, blank-space non-collapse and exact authored extension: '$required'."
    }
}
if ($handleDragText.IndexOf('$blankObserverBefore = Get-HostTouchContactObservation', [StringComparison]::Ordinal) -ge
    $handleDragText.IndexOf("Invoke-Ui @('touch',`$startHandle,'--gesture','swipe'", [StringComparison]::Ordinal)) {
    throw 'The C10 blank-space before snapshot must precede its actual handle swipe.'
}
if ($handleDragText.IndexOf('$observerBefore = Get-HostTouchContactObservation', [StringComparison]::Ordinal) -ge
    $handleDragText.IndexOf("Invoke-Ui @('touch','MarkdownSelectionEndHandle','--gesture','swipe'", [StringComparison]::Ordinal)) {
    throw 'The C10 exact-extension before snapshot must precede its actual handle swipe.'
}
$hostTouchFunctionAsts = @{}
foreach ($functionName in @('Get-HostTouchContactObservation','Assert-TouchContactObservationSnapshot','Get-HostExpectedScreenTouchPoint','ConvertTo-HostExpectedRootClientPoint','Assert-HostTouchHandleDragDelivery','Format-TouchHandleDragFailure')) {
    $functionAst = $harnessAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $functionName
    }, $true)
    if ($null -eq $functionAst) { throw "The main UI script does not define host-callable '$functionName'; embedded guest definitions cannot satisfy C10." }
    $hostTouchFunctionAsts[$functionName] = $functionAst
    Invoke-Expression $functionAst.Extent.Text
}
$hostDragCaseCommands = @($handleDragCase.FindAll({param($node) $node -is [System.Management.Automation.Language.CommandAst]}, $true) | ForEach-Object { $_.GetCommandName() })
foreach ($requiredHostCall in @('Get-HostTouchContactObservation','Get-HostExpectedScreenTouchPoint','Assert-HostTouchHandleDragDelivery','Format-TouchHandleDragFailure')) {
    if ($hostDragCaseCommands -cnotcontains $requiredHostCall) { throw "C10 does not invoke its host-callable '$requiredHostCall' implementation." }
}
if ($handleDragText.Contains('ConvertTo-AsciiJson')) { throw 'The C10 host failure path still references the guest-only ASCII JSON serializer.' }
if ($guestAst.Extent.Text.IndexOf('$clientOrigin=[MarkdownCorrectnessNative]::GetClientOriginOnScreen([IntPtr]$WindowHandle)',[StringComparison]::Ordinal) -lt 0 -or
    $guestAst.Extent.Text.IndexOf('clientOriginScreenPx=@{x=$clientOrigin.X;y=$clientOrigin.Y}',[StringComparison]::Ordinal) -lt 0) {
    throw 'The read-only guest metadata mode must return the native physical client origin consumed by the host point validator.'
}

& {
    $savedAppProcessId = Get-Variable -Name AppProcessId -Scope Script -ErrorAction SilentlyContinue
    $savedAppWindowHandle = Get-Variable -Name AppWindowHandle -Scope Script -ErrorAction SilentlyContinue
    $hadAppProcessId = $null -ne $savedAppProcessId
    $hadAppWindowHandle = $null -ne $savedAppWindowHandle
    try {
        $script:AppProcessId = 4321
        $script:AppWindowHandle = 305419896L
        $script:HostTouchProbeCalls = [Collections.Generic.List[object]]::new()
        $script:HostTouchRawName = ''
        $script:HostTouchMetadata = $null
        function Invoke-GuestProbe {
            param([string]$Mode,[string]$AutomationId)
            $script:HostTouchProbeCalls.Add([pscustomobject]@{mode=$Mode;automationId=$AutomationId})
            if ($Mode -ceq 'raw-name' -and $AutomationId -ceq 'TouchContactObservation') {
                return [pscustomobject]@{mode='raw-name';automationId=$AutomationId;name=$script:HostTouchRawName}
            }
            if ($Mode -ceq 'metadata') { return $script:HostTouchMetadata }
            throw "Unexpected host touch probe mode '$Mode' / AutomationId '$AutomationId'."
        }

        $priorContact = [pscustomobject]@{
            pointerId=2401;contactSerial=17;pressCount=4;moveCount=9;releaseCount=3;cancelCount=0;captureLostCount=0
            rawReleaseCount=3;nativeCanceledPacketCount=0;lastNativeCanceled=$false;lastRoutedEvent='Released'
            lastEventSequence=100;inContact=$false;lastEvent='Released';handle='MarkdownSelectionEndHandle'
            lastPositionRootDipX=8.0;lastPositionRootDipY=15.0;eventRasterizationScale=1.5
        }
        $before = [pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@();contacts=@($priorContact)}
        $completedContact = [pscustomobject]@{
            pointerId=2401;contactSerial=18;pressCount=5;moveCount=11;releaseCount=4;cancelCount=0;captureLostCount=0
            rawReleaseCount=4;nativeCanceledPacketCount=0;lastNativeCanceled=$false;lastRoutedEvent='Released'
            lastEventSequence=104;inContact=$false;lastEvent='Released';handle='MarkdownSelectionEndHandle'
            lastPositionRootDipX=10.5;lastPositionRootDipY=20.5;eventRasterizationScale=1.5
        }
        $after = [pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@();contacts=@($completedContact)}
        $script:HostTouchRawName = ConvertTo-Json -InputObject $before -Depth 8 -Compress
        $script:HostTouchMetadata = [pscustomobject]@{
            mode='metadata';processId=4321;hwnd=305419896L;dpi=144
            clientOriginScreenPx=[pscustomobject]@{x=1500;y=780}
        }

        $parsedObservation = Get-HostTouchContactObservation
        if ([long]$parsedObservation.rendererGeneration -ne 12 -or $script:HostTouchProbeCalls.Count -ne 1 -or
            $script:HostTouchProbeCalls[0].mode -cne 'raw-name' -or $script:HostTouchProbeCalls[0].automationId -cne 'TouchContactObservation') {
            throw 'The host collector did not read and parse the observer through the raw-name AutomationId probe.'
        }
        $expectedPoint = Get-HostExpectedScreenTouchPoint -ScreenX 1515 -ScreenY 810
        if ($script:HostTouchProbeCalls.Count -ne 2 -or $script:HostTouchProbeCalls[1].mode -cne 'metadata' -or
            [double]$expectedPoint.windowDpi -ne 144 -or [double]$expectedPoint.clientOriginScreenPx.x -ne 1500 -or
            [double]$expectedPoint.clientOriginScreenPx.y -ne 780) {
            throw 'The expected physical point did not use metadata bound to the exact captured process/HWND.'
        }

        foreach ($invalidDpi in @([double]::NaN,[double]::PositiveInfinity)) {
            $validMetadata = $script:HostTouchMetadata
            $badMetadata = [pscustomobject]@{
                mode = $validMetadata.mode; processId = $validMetadata.processId; hwnd = $validMetadata.hwnd
                dpi = $invalidDpi; clientOriginScreenPx = $validMetadata.clientOriginScreenPx
            }
            $script:HostTouchMetadata = $badMetadata
            $rejected = $false
            try { Get-HostExpectedScreenTouchPoint -ScreenX 1515 -ScreenY 810 | Out-Null } catch { $rejected = $true }
            finally { $script:HostTouchMetadata = $validMetadata }
            if (-not $rejected) { throw "The host accepted nonfinite HWND metadata DPI '$invalidDpi'." }
        }

        $delivery = Assert-HostTouchHandleDragDelivery -Before $before -After $after -ExpectedPoint $expectedPoint -ExpectedState Released -ExpectedHandle 'MarkdownSelectionEndHandle'
        if ($delivery.status -cne 'PASS' -or $delivery.pointerId -ne 2401 -or $delivery.contactSerial -ne 18 -or
            $delivery.pressDelta -ne 1 -or $delivery.moveDelta -ne 2 -or $delivery.releaseDelta -ne 1 -or
            $delivery.rawReleaseDelta -ne 1 -or $delivery.nativeCanceledPacketDelta -ne 0 -or
            $delivery.lastRoutedEvent -cne 'Released' -or $delivery.lastNativeCanceled -or
            $delivery.cancelDelta -ne 0 -or $delivery.activePointerCount -ne 0 -or
            $delivery.handle -cne 'MarkdownSelectionEndHandle' -or $delivery.positionErrorDip -gt 12) {
            throw 'The host correlation did not accept the expected reused-ID/new-contact/move/release/handle proof.'
        }

        $releaseThenCaptureLost = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $after -Depth 8 -Compress)
        $releaseThenCaptureLost.contacts[0].captureLostCount = 1
        $releaseThenCaptureLost.contacts[0].lastEventSequence = 105
        $releaseThenCaptureLost.contacts[0].lastEvent = 'CaptureLost'
        $releaseThenCaptureLost.contacts[0].lastRoutedEvent = 'CaptureLost'
        $captureLostDelivery = Assert-HostTouchHandleDragDelivery -Before $before -After $releaseThenCaptureLost -ExpectedPoint $expectedPoint -ExpectedState Released -ExpectedHandle 'MarkdownSelectionEndHandle'
        if ($captureLostDelivery.status -cne 'PASS' -or $captureLostDelivery.releaseDelta -ne 1 -or
            $captureLostDelivery.captureLostDelta -ne 1 -or $captureLostDelivery.cancelDelta -ne 0 -or
            $captureLostDelivery.rawReleaseDelta -ne 1 -or $captureLostDelivery.nativeCanceledPacketDelta -ne 0 -or
            $captureLostDelivery.lastRoutedEvent -cne 'CaptureLost' -or $captureLostDelivery.lastNativeCanceled -or
            [string]$captureLostDelivery.lastEvent -cne 'CaptureLost') {
            throw 'The host did not accept one ordinary release followed by one routed capture-loss notification.'
        }

        $invalidCases = @(
            [pscustomobject]@{name='wrong handle';change={param($value) $value.contacts[0].handle='MarkdownSelectionStartHandle'}},
            [pscustomobject]@{name='no move';change={param($value) $value.contacts[0].moveCount=9}},
            [pscustomobject]@{name='missing release';change={param($value) $value.contacts[0].releaseCount=3;$value.contacts[0].rawReleaseCount=3;$value.contacts[0].lastEvent='Moved';$value.contacts[0].inContact=$true;$value.active=@([pscustomobject]@{pointerId=2401;contactSerial=18})}},
            [pscustomobject]@{name='wrong point';change={param($value) $value.contacts[0].lastPositionRootDipX=80.0}},
            [pscustomobject]@{name='canceled raw release';change={param($value) $value.contacts[0].nativeCanceledPacketCount=1;$value.contacts[0].lastNativeCanceled=$true}},
            [pscustomobject]@{name='capture loss without release';change={param($value) $value.contacts[0].releaseCount=3;$value.contacts[0].rawReleaseCount=3;$value.contacts[0].captureLostCount=1;$value.contacts[0].lastEventSequence=105;$value.contacts[0].lastEvent='CaptureLost';$value.contacts[0].lastRoutedEvent='CaptureLost'}},
            [pscustomobject]@{name='canceled release followed by capture loss';change={param($value) $value.contacts[0].releaseCount=3;$value.contacts[0].rawReleaseCount=4;$value.contacts[0].cancelCount=1;$value.contacts[0].captureLostCount=1;$value.contacts[0].nativeCanceledPacketCount=1;$value.contacts[0].lastNativeCanceled=$false;$value.contacts[0].lastEventSequence=105;$value.contacts[0].lastEvent='CaptureLost';$value.contacts[0].lastRoutedEvent='CaptureLost'}},
            [pscustomobject]@{name='nonfinite root x';change={param($value) $value.contacts[0].lastPositionRootDipX=[double]::NaN}},
            [pscustomobject]@{name='infinite root y';change={param($value) $value.contacts[0].lastPositionRootDipY=[double]::PositiveInfinity}},
            [pscustomobject]@{name='stale reused pointer epoch';change={param($value) $value.contacts[0].contactSerial=17}},
            [pscustomobject]@{name='degraded observer';change={param($value) $value.observationStatus='degraded';$value.observationErrors=1}},
            [pscustomobject]@{name='changed renderer generation';change={param($value) $value.rendererGeneration=13}},
            [pscustomobject]@{name='ambiguous contacts';change={param($value) $value.contacts=@($value.contacts)+@([pscustomobject]@{pointerId=2402;contactSerial=1;pressCount=1;moveCount=1;releaseCount=1;cancelCount=0;captureLostCount=0;rawReleaseCount=1;nativeCanceledPacketCount=0;lastNativeCanceled=$false;lastRoutedEvent='Released';lastEventSequence=105;inContact=$false;lastEvent='Released';handle='MarkdownSelectionEndHandle';lastPositionRootDipX=10.0;lastPositionRootDipY=20.0;eventRasterizationScale=1.5})}}
        )
        foreach ($invalidCase in $invalidCases) {
            $copy = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $after -Depth 8 -Compress)
            $change = $invalidCase.change
            & $change $copy
            $rejected = $false
            try { Assert-HostTouchHandleDragDelivery -Before $before -After $copy -ExpectedPoint $expectedPoint -ExpectedState Released -ExpectedHandle 'MarkdownSelectionEndHandle' | Out-Null }
            catch { $rejected = $true }
            if (-not $rejected) { throw "The host touch correlation accepted invalid evidence: $($invalidCase.name)." }
        }
        $malformedRawName = $script:HostTouchRawName
        $script:HostTouchRawName = '{not-json'
        $malformedRejected = $false
        try { Get-HostTouchContactObservation | Out-Null } catch { $malformedRejected = $true }
        if (-not $malformedRejected) { throw 'The host raw-name collector accepted malformed observer JSON.' }
        $script:HostTouchRawName = $malformedRawName

        $failureDiagnostics = [ordered]@{observerBefore=$before;observerAfter=$after;expectedHandle='MarkdownSelectionEndHandle'}
        $failureMessage = Format-TouchHandleDragFailure -Reason 'mock failure' -Diagnostics $failureDiagnostics
        $marker = 'Diagnostics: '
        $markerIndex = $failureMessage.IndexOf($marker,[StringComparison]::Ordinal)
        if ($markerIndex -lt 0) { throw 'The host C10 failure formatter omitted serialized diagnostics.' }
        $roundTripDiagnostics = ConvertFrom-Json -InputObject $failureMessage.Substring($markerIndex + $marker.Length)
        if ([long]$roundTripDiagnostics.observerBefore.contacts[0].contactSerial -ne 17 -or
            [long]$roundTripDiagnostics.observerAfter.contacts[0].contactSerial -ne 18 -or
            [string]$roundTripDiagnostics.expectedHandle -cne 'MarkdownSelectionEndHandle') {
            throw 'The host C10 failure formatter did not retain both exact observer snapshots and the expected handle.'
        }
    }
    finally {
        if ($hadAppProcessId) { $script:AppProcessId = $savedAppProcessId.Value } else { Remove-Variable -Name AppProcessId -Scope Script -ErrorAction SilentlyContinue }
        if ($hadAppWindowHandle) { $script:AppWindowHandle = $savedAppWindowHandle.Value } else { Remove-Variable -Name AppWindowHandle -Scope Script -ErrorAction SilentlyContinue }
        Remove-Variable -Name HostTouchProbeCalls,HostTouchRawName,HostTouchMetadata -Scope Script -ErrorAction SilentlyContinue
    }
}
$checks.Add([pscustomobject]@{name='host C10 raw-name collector, exact-HWND point anchor, reused-contact correlation, terminal/handle/move/coordinate rejection, and serialized before/after failure snapshots are functionally exercised';status='PASS'})
$emoji = [string]::new([char[]]@(0xD83D,0xDE00))
$technologist = [string]::new([char[]]@(0xD83D,0xDC69,0xD83C,0xDFFD,0x200D,0xD83D,0xDCBB))
$wireJson = ConvertTo-AsciiJson -Value @{smile=$emoji;technologist=$technologist}
if (@($wireJson.ToCharArray() | Where-Object { [int]$_ -gt 0x7f }).Count -gt 0) { throw 'Guest JSON wire representation still contains raw non-ASCII UTF-16 code units.' }
$roundTrip = ConvertFrom-Json -InputObject $wireJson
if ($roundTrip.smile -cne $emoji -or $roundTrip.technologist -cne $technologist) { throw 'ASCII-safe guest JSON failed to preserve emoji/grapheme UTF-16 code units.' }

$geometryFunction=$harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-VectorHandleDestination'
},$true)
if($null -eq $geometryFunction){throw 'Could not find Resolve-VectorHandleDestination in the UI harness.'}
Invoke-Expression $geometryFunction.Extent.Text

foreach($offset in @(
    @{Name='positive';X=18.0;Y=22.0},
    @{Name='negative';X=-18.0;Y=-22.0}
)){
    $resolved=Resolve-VectorHandleDestination -ObjectX 100 -ObjectY 200 -ObjectWidth 180 -ObjectHeight 140 -FingerToCaretOffsetX $offset.X -FingerToCaretOffsetY $offset.Y
    if($resolved.target.x -lt 100 -or $resolved.target.x -gt 280 -or $resolved.target.y -lt 200 -or $resolved.target.y -gt 340){throw "The $($offset.Name) handle-offset finger point escaped the object bounds."}
    if($resolved.effectiveCaret.y -lt 270 -or $resolved.effectiveCaret.y -gt 340){throw "The $($offset.Name) handle-offset effective caret escaped the lower-half region."}
}
$noFitRejected=$false
try{Resolve-VectorHandleDestination -ObjectX 100 -ObjectY 200 -ObjectWidth 80 -ObjectHeight 40 -FingerToCaretOffsetX 0 -FingerToCaretOffsetY 30 | Out-Null}catch{$noFitRejected=$_.Exception.Message.Contains('no lower-half caret point')}
if(-not $noFitRejected){throw 'Handle-offset geometry must reject an object with no feasible lower-half/in-bounds intersection.'}

foreach ($mode in @('metadata', 'range', 'range-between', 'table-cell', 'scroll-range', 'selection', 'raw-name', 'image-state', 'handles', 'focus-state', 'scroll', 'clipboard', 'resize', 'cancel-contact', 'payload-hashes', 'source-file-preflight', 'source-file', 'near-origin-sequence')) {
    if ($guestProbeFunction.Extent.Text -notmatch [regex]::Escape("'$mode'")) { throw "Guest probe mode '$mode' is not wired through the host orchestrator." }
}

$script:GuestProbePath = 'C:\WinApp\work\correctness\unit\GuestUiProbe.ps1'
$script:AppProcessId = 4321
$script:AppWindowHandle = 197292L
$script:LastNativeArguments = @()
$script:MockIdentityChecks = 0
function Assert-ProcessIdentity {
    $script:MockIdentityChecks++
    return [pscustomobject]@{target='sandbox';epoch='offline-epoch';processId=$script:AppProcessId;hwnd=$script:AppWindowHandle;processName='MarkdownRenderer.Sample';title='offline sample'}
}

function Invoke-NativeChecked {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Json)
    $script:LastNativeArguments = @($Arguments)
    if ($script:LastNativeArguments | Where-Object { [string]::IsNullOrEmpty($_) }) {
        throw 'Guest probe argument list contains an empty native argument.'
    }
    if (-not $Json) { throw 'Guest probe did not request JSON output.' }
    $modeIndex = [Array]::IndexOf($script:LastNativeArguments, '-Mode')
    if ($modeIndex -lt 0 -or $modeIndex + 1 -ge $script:LastNativeArguments.Count) {
        throw 'Guest probe argument list omitted its mode.'
    }
    $mode = $script:LastNativeArguments[$modeIndex + 1]
    $payloadObject = @{ mode = $mode }
    if ($mode -ceq 'source-file-preflight') {
        $pathIndex = [Array]::IndexOf($script:LastNativeArguments, '-SourcePath')
        if ($pathIndex -lt 0 -or $pathIndex + 1 -ge $script:LastNativeArguments.Count) { throw 'Preflight mock requires an explicit source path.' }
        $payloadObject.sourcePath = $script:LastNativeArguments[$pathIndex + 1]
        $payloadObject.destinationExisted = $false
    }
    $payload = ConvertTo-Json -InputObject $payloadObject -Compress
    return [pscustomobject]@{
        value = [pscustomobject]@{ stdout = $payload }
        output = $payload
        evidence = 'offline-mock'
    }
}

foreach ($mode in @('metadata', 'handles', 'scroll', 'scroll-range', 'selection', 'clipboard', 'range-between', 'table-cell', 'source-file', 'near-origin-sequence', 'cancel-contact', 'resize')) {
    $result = Invoke-GuestProbe -Mode $mode
    if ($result.mode -ne $mode) { throw "Default invocation for '$mode' did not return the matching payload." }
    if ($script:LastNativeArguments -contains '-Needle' -or $script:LastNativeArguments -contains '-StartNeedle' -or $script:LastNativeArguments -contains '-EndNeedle' -or $script:LastNativeArguments -contains '-SelectRange' -or $script:LastNativeArguments -contains '-AutomationId') {
        throw "Default invocation for '$mode' forwarded an empty optional text argument."
    }
}
if($script:MockIdentityChecks -ne 6){throw "Identity-sensitive guest modes must use exactly bounded pre/post checks; expected 6 total checks, observed $script:MockIdentityChecks."}
$checks.Add([pscustomobject]@{name='native cancellation/suppression/resize guest probes perform pre/post identity checks without inserting checks inside the measured gesture';status='PASS'})

$range = Invoke-GuestProbe -Mode 'range' -Needle 'independent text'
if ($range.mode -ne 'range') { throw 'Explicit range invocation failed.' }
$needleIndex = [Array]::IndexOf($script:LastNativeArguments, '-Needle')
if ($needleIndex -lt 0 -or $script:LastNativeArguments[$needleIndex + 1] -ne 'independent text') {
    throw 'Explicit Needle was not preserved in the guest command.'
}

$rawName = Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'ThemeStatus'
if ($rawName.mode -ne 'raw-name') { throw 'Explicit raw-name invocation failed.' }
$automationIdIndex = [Array]::IndexOf($script:LastNativeArguments, '-AutomationId')
if ($automationIdIndex -lt 0 -or $script:LastNativeArguments[$automationIdIndex + 1] -ne 'ThemeStatus') {
    throw 'Explicit AutomationId was not preserved in the guest command.'
}

$focusState = Invoke-GuestProbe -Mode 'focus-state' -AutomationId 'MarkdownSelectionEndHandle'
if ($focusState.mode -ne 'focus-state') { throw 'Explicit focus-state invocation failed.' }
$focusModeIndex = [Array]::IndexOf($script:LastNativeArguments, '-Mode')
$focusIdIndex = [Array]::IndexOf($script:LastNativeArguments, '-AutomationId')
if ($focusModeIndex -lt 0 -or $script:LastNativeArguments[$focusModeIndex + 1] -ne 'focus-state' -or
    $focusIdIndex -lt 0 -or $script:LastNativeArguments[$focusIdIndex + 1] -ne 'MarkdownSelectionEndHandle') {
    throw 'Explicit focus-state probe did not preserve its mode and required handle AutomationId.'
}

$rangeBetween = Invoke-GuestProbe -Mode 'range-between' -StartNeedle 'start anchor' -EndNeedle 'end anchor' -SelectRange
if ($rangeBetween.mode -ne 'range-between') { throw 'Explicit range-between invocation failed.' }
$startNeedleIndex = [Array]::IndexOf($script:LastNativeArguments, '-StartNeedle')
$endNeedleIndex = [Array]::IndexOf($script:LastNativeArguments, '-EndNeedle')
if ($startNeedleIndex -lt 0 -or $script:LastNativeArguments[$startNeedleIndex + 1] -ne 'start anchor' -or $endNeedleIndex -lt 0 -or $script:LastNativeArguments[$endNeedleIndex + 1] -ne 'end anchor' -or $script:LastNativeArguments -notcontains '-SelectRange') {
    throw 'Explicit range endpoints or SelectRange were not preserved in the guest command.'
}

$tableCell = Invoke-GuestProbe -Mode 'table-cell' -Needle 'image alt' -StartNeedle 'adjacent text'
if ($tableCell.mode -ne 'table-cell') { throw 'Explicit table-cell invocation failed.' }
if ($script:LastNativeArguments -notcontains '-Needle' -or $script:LastNativeArguments -notcontains '-StartNeedle' -or $script:LastNativeArguments -notcontains 'image alt' -or $script:LastNativeArguments -notcontains 'adjacent text') {
    throw 'Explicit table-cell image/text anchors were not preserved in the guest command.'
}

$runId='20261012T223954Z-a947b31b93da4033ac14547a6677eec2'
$sourcePath="C:\WinApp\work\correctness\$runId\source-0123456789abcdef0123456789abcdef.md"
$preflight=Invoke-GuestProbe -Mode 'source-file-preflight' -SourcePath $sourcePath -RunId $runId
if($preflight.mode -cne 'source-file-preflight' -or [bool]$preflight.destinationExisted -or $preflight.sourcePath -cne $sourcePath){throw 'Staged-source preflight did not prove a new destination at the exact path.'}
foreach($pair in @(@('-SourcePath',$sourcePath),@('-RunId',$runId))){
    $index=[Array]::IndexOf($script:LastNativeArguments,$pair[0])
    if($index -lt 0 -or $index+1 -ge $script:LastNativeArguments.Count -or $script:LastNativeArguments[$index+1] -cne $pair[1]){throw "Staged-source preflight did not preserve $($pair[0])."}
}
$staged=Invoke-GuestProbe -Mode 'source-file' -SourcePath $sourcePath -RequiredText 'unique staged fixture' -RunId $runId
if($staged.mode -ne 'source-file'){throw 'Staged source-file invocation did not return its mode.'}
foreach($pair in @(@('-SourcePath',$sourcePath),@('-RequiredText','unique staged fixture'),@('-RunId',$runId))){
    $index=[Array]::IndexOf($script:LastNativeArguments,$pair[0])
    if($index -lt 0 -or $index+1 -ge $script:LastNativeArguments.Count -or $script:LastNativeArguments[$index+1] -cne $pair[1]){throw "Staged source-file invocation did not preserve $($pair[0])."}
}
$nearOrigin=Invoke-GuestProbe -Mode 'near-origin-sequence' -X 401 -Y 520 -Width 438 -Height 520 -RequiredText 'https://example.invalid/test' -PointerId 0
if($nearOrigin.mode -ne 'near-origin-sequence'){throw 'Same-process near-origin invocation did not return its mode.'}
foreach($pair in @(@('-X','401'),@('-Y','520'),@('-Width','438'),@('-Height','520'),@('-RequiredText','https://example.invalid/test'),@('-PointerId','0'))){
    $index=[Array]::IndexOf($script:LastNativeArguments,$pair[0])
    if($index -lt 0 -or $index+1 -ge $script:LastNativeArguments.Count -or $script:LastNativeArguments[$index+1] -cne $pair[1]){throw "Near-origin guest sequence did not preserve $($pair[0])."}
}
if($script:LastNativeArguments|Where-Object{[string]::IsNullOrEmpty($_)}){throw 'Staged-source or same-process guest test added an empty native argument.'}

$setTemporary=$harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-TemporaryMarkdown'},$true)
if($null -eq $setTemporary){throw 'Could not find Set-TemporaryMarkdown.'}
$setTemporaryText=$setTemporary.Extent.Text
$preflightIndex=$setTemporaryText.IndexOf("-Mode 'source-file-preflight'",[StringComparison]::Ordinal)
$pushIndex=$setTemporaryText.IndexOf("'target', 'push', 'sandbox'",[StringComparison]::Ordinal)
if($preflightIndex -lt 0 -or $pushIndex -lt 0 -or $preflightIndex -ge $pushIndex -or
    $setTemporaryText.IndexOf('destinationExisted',[StringComparison]::Ordinal) -lt 0 -or
    $setTemporaryText.IndexOf('run-scoped Markdown destination was not proven new',[StringComparison]::Ordinal) -lt 0){
    throw 'Long-source staging must verify a fresh run-scoped destination before pushing and refuse an existing Sandbox file.'
}

$setTemporaryParameters = @($setTemporary.Body.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
if ($setTemporaryParameters -cnotcontains 'SkipRendererWait' -or
    $setTemporaryText -notmatch 'if\s*\(\s*-not\s+\$SkipRendererWait\s*\)\s*\{\s*\$null\s*=\s*Wait-ForRendererText') {
    throw 'Set-TemporaryMarkdown must support staging replacement input without waiting for the superseded source to render.'
}
$uiRunnerText = Get-Content -LiteralPath $harnessPath -Raw
$replacementMarker = "Test-Case -Id 'C14' -Name 'current-source-replacement-cancels-stale-document'"
$replacementStart = $uiRunnerText.IndexOf($replacementMarker,[StringComparison]::Ordinal)
$replacementEnd = if ($replacementStart -ge 0) { $uiRunnerText.IndexOf("Test-Case -Id",$replacementStart + $replacementMarker.Length,[StringComparison]::Ordinal) } else { -1 }
if ($replacementStart -lt 0) { throw 'Could not find the rapid source-replacement UI case.' }
if ($replacementEnd -lt 0) { $replacementEnd = $uiRunnerText.Length }
$replacementCaseText = $uiRunnerText.Substring($replacementStart,$replacementEnd - $replacementStart)
if ($replacementCaseText.IndexOf("Set-TemporaryMarkdown -Markdown `$old -RequiredText 'OldContentToken' -SkipRendererWait",[StringComparison]::Ordinal) -lt 0 -or
    $replacementCaseText.IndexOf("Set-TemporaryMarkdown -Markdown `$new -RequiredText 'LatestContentToken'",[StringComparison]::Ordinal) -lt 0 -or
    $replacementCaseText -match "Invoke-Ui\s+@\('set-value','MarkdownEditor',`$(old|new)\)") {
    throw 'Rapid source replacement must stage the large old input, skip waiting on it, and publish the small replacement through the common bounded input path.'
}
$checks.Add([pscustomobject]@{name='large rapid-replacement input uses run-scoped staged source and skips only the superseded renderer wait';status='PASS'})

$automationIdFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-UiAutomationId'
},$true)
if ($null -eq $automationIdFunction) { throw 'Could not find Get-UiAutomationId.' }
Invoke-Expression $automationIdFunction.Extent.Text
foreach ($elementWithoutAutomationId in @(
    [pscustomobject]@{name='presentation-only UIA element'},
    [ordered]@{name='dictionary-shaped presentation-only UIA element'}
)) {
    if ((Get-UiAutomationId $elementWithoutAutomationId) -cne '') {
        throw 'A UIA element without an AutomationId did not resolve safely to an empty identifier.'
    }
}
$diagnosticFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SampleDiagnosticMessage'
},$true)
if ($null -eq $diagnosticFunction) { throw 'Could not find Get-SampleDiagnosticMessage.' }
$diagnosticText = $diagnosticFunction.Extent.Text
if ($diagnosticText -notmatch 'Get-AppUiElements' -or $diagnosticText -notmatch "return ''" -or
    $diagnosticText -match 'Invoke-UiValue') {
    throw 'The optional Math diagnostic reader must treat an omitted empty UIA message as no diagnostic and must not query it as a required element.'
}
if ($uiRunnerText -match "Invoke-UiValue\s+'SampleDiagnosticsMessage'") {
    throw 'Math correctness checks must not query the intentionally omitted empty diagnostics TextBlock by required selector.'
}
Invoke-Expression $diagnosticFunction.Extent.Text
$script:MockSampleDiagnosticElements = @()
function Get-AppUiElements { return @($script:MockSampleDiagnosticElements) }
if ((Get-SampleDiagnosticMessage) -cne '') { throw 'An omitted closed/empty diagnostics message was not treated as empty.' }
$script:MockSampleDiagnosticElements = @([pscustomobject]@{automationId='SampleDiagnosticsMessage';name='MATH100: fixture diagnostic';value='MATH100: fixture diagnostic'})
if ((Get-SampleDiagnosticMessage) -cnotmatch '^MATH100') { throw 'An exposed renderer diagnostic was not preserved for the Math assertion.' }
$script:MockSampleDiagnosticElements = @(
    [pscustomobject]@{automationId='SampleDiagnosticsMessage';name='first';value='first'},
    [pscustomobject]@{automationId='SampleDiagnosticsMessage';name='second';value='second'}
)
$duplicateDiagnosticRejected = $false
try { Get-SampleDiagnosticMessage | Out-Null } catch { $duplicateDiagnosticRejected = $true }
if (-not $duplicateDiagnosticRejected) { throw 'Duplicate diagnostic message elements were silently accepted.' }
$checks.Add([pscustomobject]@{name='optional UIA diagnostic reader distinguishes omitted empty state, visible errors, and duplicate elements';status='PASS'})

$selectionHandlesFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-SelectionHandles'
},$true)
if ($null -eq $selectionHandlesFunction) { throw 'Could not find Assert-SelectionHandles.' }
$selectionHandlesText = $selectionHandlesFunction.Extent.Text
if ($selectionHandlesText -match 'Where-Object\s+automationId' -or
    $selectionHandlesText -notmatch 'Where-Object\s*\{\s*\(Get-UiAutomationId\s+\$_\)\s+-ceq\s+''MarkdownSelection(Start|End)Handle''') {
    throw 'Selection handle lookup must use the safe AutomationId accessor rather than strict-mode property binding.'
}
$handleJson = '{"dpiScale":1,"handles":[{"automationId":"MarkdownSelectionStartHandle","width":48,"height":48,"offscreen":false},{"automationId":"MarkdownSelectionEndHandle","width":48,"height":48,"offscreen":false},{"name":"decorative element without AutomationId"}]}'
foreach ($payload in @(
    (ConvertFrom-Json -InputObject $handleJson),
    @{
        dpiScale = 1
        handles = @(
            @{ automationId = 'MarkdownSelectionStartHandle'; width = 48; height = 48; offscreen = $false },
            @{ automationId = 'MarkdownSelectionEndHandle'; width = 48; height = 48; offscreen = $false },
            @{ name = 'decorative element without AutomationId' }
        )
    }
)) {
    $startHandle = @($payload.handles | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownSelectionStartHandle' })
    $endHandle = @($payload.handles | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownSelectionEndHandle' })
    if ($startHandle.Count -ne 1 -or $endHandle.Count -ne 1) { throw 'Explicit handle ID lookup did not support both native PSCustomObject and dictionary JSON shapes.' }
}
$checks.Add([pscustomobject]@{name='safe AutomationId filtering tolerates missing IDs and both object/dictionary UIA JSON payloads';status='PASS'})

$guestProbeStage = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-GuestProbeStage'
}, $true)
if ($null -eq $guestProbeStage) { throw 'Could not find Invoke-GuestProbeStage in the UI harness.' }
foreach($required in @("'source-file-preflight' {",'Refusing to overwrite an existing Sandbox Markdown destination.','The staged Markdown destination must be a fresh source file directly inside this run-specific Sandbox work directory.')){
    if($guestProbeStage.Extent.Text.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Guest preflight implementation is missing '$required'."}
}
$nearOriginDown=$guestProbeStage.Extent.Text.IndexOf('$touchDevice.SendFrame($PointerId,[int][Math]::Round($X),[int][Math]::Round($Y),$true,$true)',[StringComparison]::Ordinal)
$nearOriginHold=$guestProbeStage.Extent.Text.IndexOf('$longPressDeadline=',[StringComparison]::Ordinal)
$nearOriginUpdates=$guestProbeStage.Extent.Text.IndexOf('$touchDevice.SendFrame($PointerId,[int][Math]::Round($X),[int][Math]::Round($Y),$true,$false)',[StringComparison]::Ordinal)
$nearOriginRelease=$guestProbeStage.Extent.Text.IndexOf('$touchDevice.SendFrame($PointerId,[int][Math]::Round($X),[int][Math]::Round($Y),$false,$false)',[StringComparison]::Ordinal)
$tapDown=$guestProbeStage.Extent.Text.IndexOf('$touchDevice.SendFrame($tapPointerId,[int][Math]::Round($Width),[int][Math]::Round($Height),$true,$true)',[StringComparison]::Ordinal)
$tapDelay=if($tapDown -ge 0){$guestProbeStage.Extent.Text.IndexOf('Start-Sleep -Milliseconds 10',$tapDown,[StringComparison]::Ordinal)}else{-1}
$tapUp=if($tapDown -ge 0){$guestProbeStage.Extent.Text.IndexOf('$touchDevice.SendFrame($tapPointerId,[int][Math]::Round($Width),[int][Math]::Round($Height),$false,$false)',$tapDown,[StringComparison]::Ordinal)}else{-1}
if($nearOriginDown -lt 0 -or $nearOriginHold -le $nearOriginDown -or $nearOriginUpdates -le $nearOriginHold -or $nearOriginRelease -le $nearOriginUpdates -or $tapDown -le $nearOriginRelease -or $tapDelay -le $tapDown -or $tapUp -le $tapDelay -or
    $guestProbeStage.Extent.Text.IndexOf('Start-Sleep -Milliseconds 25',$nearOriginHold,[StringComparison]::Ordinal) -lt 0 -or
    $guestProbeStage.Extent.Text.IndexOf('Frequency*0.65',$nearOriginHold,[StringComparison]::Ordinal) -lt 0){
    throw 'V1-004 input must sustain the original long-press contact with stationary updates and give the independent tap a legal down/up interval.'
}
$csharpMatch = [regex]::Match($guestProbeStage.Extent.Text, '(?s)Add-Type\s+-TypeDefinition\s+@"\r?\n(?<code>.*?)\r?\n"@')
if (-not $csharpMatch.Success) { throw 'Could not extract the guest-native interop source for offline ABI validation.' }
if ([IntPtr]::Size -ne 8) { throw 'The offline POINTER_TOUCH_INFO ABI check requires the x64 host used by the approved Sandbox.' }
$measurementSource = @'
public static class MarkdownTouchAbiMeasure
{
    private static Type GetType(string name) { if(name == "MarkdownPointerInfo") return typeof(MarkdownPointerInfo); if(name == "MarkdownPointerTouchInfo") return typeof(MarkdownPointerTouchInfo); if(name == "MarkdownPointerTypeInfo") return typeof(MarkdownPointerTypeInfo); if(name == "MarkdownPointerTypeInfoUnion") return typeof(MarkdownPointerTypeInfoUnion); throw new ArgumentException(name); }
    public static int Size(string name) { return Marshal.SizeOf(GetType(name)); }
    public static int Offset(string name, string field) { return Marshal.OffsetOf(GetType(name), field).ToInt32(); }
}
'@
Add-Type -TypeDefinition ($csharpMatch.Groups['code'].Value + [Environment]::NewLine + $measurementSource) -Language CSharp

$expectedLayouts = @(
    @{ Name = 'MarkdownPointerInfo'; Size = 96; Fields = @{ PointerFlags = 12; SourceDevice = 16; HwndTarget = 24; Pixel = 32; Time = 64; PerformanceCount = 80; ButtonChangeType = 88 } },
    @{ Name = 'MarkdownPointerTouchInfo'; Size = 144; Fields = @{ PointerInfo = 0; TouchFlags = 96; TouchMask = 100; Contact = 104; ContactRaw = 120; Orientation = 136; Pressure = 140 } },
    @{ Name = 'MarkdownPointerTypeInfo'; Size = 152; Fields = @{ Type = 0; Anonymous = 8 } },
    @{ Name = 'MarkdownPointerTypeInfoUnion'; Size = 144; Fields = @{ TouchInfo = 0 } }
)
foreach ($layout in $expectedLayouts) {
    $actualSize = [MarkdownTouchAbiMeasure]::Size($layout.Name)
    if ($actualSize -ne $layout.Size) { throw "$($layout.Name) ABI size is $actualSize; expected $($layout.Size)." }
    foreach ($field in $layout.Fields.GetEnumerator()) {
        $actualOffset = [MarkdownTouchAbiMeasure]::Offset($layout.Name, $field.Key)
        if ($actualOffset -ne $field.Value) { throw "$($layout.Name).$($field.Key) ABI offset is $actualOffset; expected $($field.Value)." }
    }
}
$touchImport = [regex]::Match($guestProbeStage.Extent.Text, 'InjectSyntheticPointerInput\(IntPtr device,\s*\[In\]\s*MarkdownPointerTypeInfo\[\]\s*contacts,\s*uint count\)')
if (-not $touchImport.Success) { throw 'InjectSyntheticPointerInput must use the SDK POINTER_TYPE_INFO array ABI.' }
if ($guestProbeStage.Extent.Text -match 'InitializeTouchInjection|InjectTouchInput\(') { throw 'The guest interop still contains the legacy touch-injection backend.' }
if ($guestProbeStage.Extent.Text -notmatch 'private const uint PointerFeedbackNone = 3' -or
    $guestProbeStage.Extent.Text -notmatch 'CreateSyntheticPointerDevice\(MarkdownPointerType\.Touch,\s*MaximumContacts,\s*PointerFeedbackNone\)' -or
    $guestProbeStage.Extent.Text -notmatch 'private const uint MaximumContacts = 1' -or
    $guestProbeStage.Extent.Text -notmatch 'private static MarkdownPointerTypeInfo Create\(uint pointerId, int x, int y, MarkdownPointerFlags flags\)' -or
    $guestProbeStage.Extent.Text -notmatch 'if \(pointerId != 0\) throw new ArgumentOutOfRangeException\("pointerId"') {
    throw 'The synthetic touch device must be bounded to one contact and use local contact index 0 rather than pretending it controls the OS-routed pointer ID.'
}
if ($guestProbeStage.Extent.Text -notmatch 'if \(error != 21 \|\| attempt == 10\) throw LastError\(phase,error\)' -or
    $guestProbeStage.Extent.Text -notmatch 'Thread\.Sleep\(1\)' -or
    $guestProbeStage.Extent.Text -notmatch 'MarkdownPointerTypeInfo\[\] frame = new\[\] \{ Create\(pointerId,x,y,flags\) \}' -or
    $guestProbeStage.Extent.Text -notmatch 'InjectSyntheticPointerInput\(_device,frame,1\)') {
    throw 'Transient ERROR_NOT_READY handling must replay the identical synthetic pointer frame with a strict bounded retry.'
}
if ($guestProbeStage.Extent.Text -notmatch 'MarkdownPointerTypeInfoUnion\s*\{\s*TouchInfo\s*=\s*touch\s*\}' -or
    $guestProbeStage.Extent.Text -notmatch 'PointerType\s*=\s*MarkdownPointerType\.Touch' -or
    $guestProbeStage.Extent.Text -notmatch 'Pixel\s*=\s*new MarkdownPoint\(x,y\)' -or
    $guestProbeStage.Extent.Text -notmatch 'Contact=rect') {
    throw 'Synthetic pointer input must populate the SDK union with a touch record at the supplied physical screen-pixel coordinate.'
}
if ($guestProbeStage.Extent.Text -notmatch 'MarkdownPointerFlags\.Primary' -or
    $guestProbeStage.Extent.Text -notmatch 'Down\|MarkdownPointerFlags\.InRange\|MarkdownPointerFlags\.InContact' -or
    $guestProbeStage.Extent.Text -notmatch 'Update\|MarkdownPointerFlags\.InRange\|MarkdownPointerFlags\.InContact' -or
    $guestProbeStage.Extent.Text -notmatch 'flags=MarkdownPointerFlags\.Up;') {
    throw 'Synthetic contact phases must use primary touch down/update and a canonical terminal up.'
}
if ($guestProbeStage.Extent.Text -notmatch '\$range\.ScrollIntoView\(\$true\)') { throw 'Semantic scrolling does not use the exposed TextPatternRange.ScrollIntoView method.' }
$unreliableScrollSelectors = [regex]::Matches($harnessAst.Extent.Text, "Invoke-Ui\s+@\('scroll-into-view'")
if ($unreliableScrollSelectors.Count -gt 0) { throw 'The batch still calls ScrollItemPattern on synthetic Markdown peers instead of scrolling semantic text ranges.' }

$dpiHarnessPath=Join-Path $PSScriptRoot 'Invoke-MarkdownCorrectnessDpiTests.ps1'
$dpiTokens=$null
$dpiParseErrors=$null
$dpiAst=[System.Management.Automation.Language.Parser]::ParseFile($dpiHarnessPath,[ref]$dpiTokens,[ref]$dpiParseErrors)
if ($dpiParseErrors.Count -gt 0) { throw "The DPI harness does not parse: $($dpiParseErrors | Out-String)" }
$dpiSource=Get-Content -LiteralPath $dpiHarnessPath -Raw
foreach($required in @('ExpectedAppStartTimeUtc','ExpectedSettingsStartTimeUtc','MonitorFromWindow','SystemHighContrast','GetDpiForWindow','SelectionItemPattern','Set-DisplayScale','Restore-Scale','-Restoring','RESTORE-APP-BOUNDS','100,150,200','forced-hc-preview')) {
    if ($dpiSource.IndexOf($required,[StringComparison]::Ordinal) -lt 0) { throw "DPI harness is missing required safety/evidence contract '$required'." }
}
if ($dpiSource -match 'ChangeDisplaySettings|SetDisplayConfig|reg\.exe|Set-ItemProperty|SPI_SETHIGHCONTRAST|SystemParametersInfo\(0x0043') { throw 'DPI harness contains a forbidden display/High Contrast registry or SPI mutation.' }
if ($dpiSource -notmatch '(?s)if\s*\(-not\s+\$Restoring\).*?Get-WindowInfo\s+-ProcessId\s+\$AppPid' -or
    $dpiSource -notmatch '(?s)function\s+Restore-Scale.*?Set-DisplayScale\s+-Percent\s+\$percent\s+-Restoring') {
    throw 'DPI cleanup cannot restore Windows display scale through the verified Settings window after the app window is lost.'
}
if ($dpiSource -notmatch 'Monitor\s*-ne\s*\$script:SettingsMonitor') { throw 'DPI harness does not enforce same-monitor ownership before mutations.' }
if ($dpiSource -notmatch '(?s)function\s+Set-DisplayScale.*?DisplayMonitorCount\(\)\s*-ne\s*1') { throw 'DPI harness can mutate an ambiguously selected display on a multi-monitor desktop.' }
if ($dpiSource -notmatch '\[MarkdownCorrectnessDpiNative\]::MoveResize\(\[IntPtr\]\$AppHwnd') { throw 'DPI harness does not scope SetWindowPos to the approved app HWND.' }

$mainSource=Get-Content -LiteralPath $harnessPath -Raw
$lineEndingNormalizerAst=$guestAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Normalize-MarkdownEditorLineEndings'
},$true)
if($null -eq $lineEndingNormalizerAst){throw 'The embedded guest probe must define its narrow native line-ending normalizer.'}
Invoke-Expression $lineEndingNormalizerAst.Extent.Text
$lineEndingReference="alpha`nβeta`n"
$lineEndingCases=@(
    [pscustomobject]@{name='LF';text=$lineEndingReference},
    [pscustomobject]@{name='CRLF';text=$lineEndingReference.Replace("`n","`r`n")},
    [pscustomobject]@{name='CR';text=$lineEndingReference.Replace("`n","`r")}
)
$normalizedLineEndingReference=Normalize-MarkdownEditorLineEndings -Text $lineEndingReference
foreach($lineEndingCase in $lineEndingCases){
    if((Normalize-MarkdownEditorLineEndings -Text $lineEndingCase.text) -cne $normalizedLineEndingReference){
        throw "The exact staged-source comparator did not accept native $($lineEndingCase.name) line endings as equivalent."
    }
}
$changedLineEndingText=Normalize-MarkdownEditorLineEndings -Text "alpha`nBETA`n"
if($changedLineEndingText -ceq $normalizedLineEndingReference){throw 'Line-ending canonicalization concealed a changed Markdown text code unit.'}
$sourceFileModeStart=$guestSource.IndexOf("    'source-file' {",[StringComparison]::Ordinal)
$nearOriginModeStart=$guestSource.IndexOf("    'near-origin-sequence' {",[StringComparison]::Ordinal)
if($sourceFileModeStart -lt 0 -or $nearOriginModeStart -le $sourceFileModeStart){throw 'Could not isolate the guest source-file mode from the next probe branch.'}
$sourceFileModeText=$guestSource.Substring($sourceFileModeStart,$nearOriginModeStart-$sourceFileModeStart)
foreach($required in @(
    'Normalize-MarkdownEditorLineEndings -Text $markdown',
    'Normalize-MarkdownEditorLineEndings -Text $updated',
    '$actualText -cne $expectedText',
    'exactValuePatternRoundTrip',
    'exactApartFromNativeLineEndings=$true'
)){
    if($sourceFileModeText.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Staged source-file verification is missing exact-preservation contract '$required'."}
}
$checks.Add([pscustomobject]@{name='the exact guest source normalizer accepts LF/CRLF/CR while preserving non-line-ending changes, and source-file mode verifies both exact ValuePattern round-trip and only that narrow normalization';status='PASS'})

$nearOriginSuccessAssignments=@($guestAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -ceq '$result' -and $null -ne $node.Right -and
        $node.Right.Extent.Text.Contains('independentTapActivated')
},$true))
if($nearOriginSuccessAssignments.Count -ne 1){throw 'The near-origin guest mode must have one successful payload containing the independent link activation result.'}
$nearOriginPayloadEvaluator=[ScriptBlock]::Create(@'
param([string]$AssignmentText)
$Mode='near-origin-sequence';$X=10;$Y=20;$Width=30;$Height=40;$PointerId=[uint32]0;$tapPointerId=[uint32]0
$originDelivery=[pscustomobject]@{status='PASS';pointerId=1};$tapDelivery=[pscustomobject]@{status='PASS';pointerId=2}
$distancePixels=10.0;$distanceDips=5.0
$releaseUtc=[DateTime]::SpecifyKind([DateTime]::new(2026,10,12,0,0,0),[DateTimeKind]::Utc)
$elapsedMilliseconds=10.0;$tapDownTicks=[long]2;$releaseTicks=[long]1
$statusBefore='before';$statusAfterRelease='before';$statusAfterEscape='before';$RequiredText='https://example.invalid/near-origin';$statusAfterTap=$RequiredText
$selected='selected';$selectedAfterEscape='';$menuIds=@('copy','copy-markdown');$stillOpen=@();$handleIds=@('start','end')
Invoke-Expression $AssignmentText
return $result
'@)
$nearOriginSuccessPayload=& $nearOriginPayloadEvaluator $nearOriginSuccessAssignments[0].Extent.Text
if([string]$nearOriginSuccessPayload.mode -cne 'near-origin-sequence' -or
   [string]$nearOriginSuccessPayload.status -cne 'PASS' -or -not [bool]$nearOriginSuccessPayload.independentTapActivated){
    throw 'The actual successful near-origin guest payload did not explicitly report PASS with affirmative independent-tap activation.'
}
$nearOriginBranchText=$guestSource.Substring($nearOriginModeStart)
if($nearOriginBranchText.IndexOf("status='UNVERIFIED'",[StringComparison]::Ordinal) -lt 0 -or
   $nearOriginBranchText.IndexOf("status='PASS'",[StringComparison]::Ordinal) -lt 0){
    throw 'The near-origin guest sequence must distinguish out-of-envelope UNVERIFIED cases from a successful PASS receipt.'
}
$checks.Add([pscustomobject]@{name='the exact successful near-origin guest payload serializes status PASS only with positive independent-tap activation while out-of-envelope results remain UNVERIFIED';status='PASS'})

$c11Start=$mainSource.IndexOf("Test-Case -Id 'C11'",[StringComparison]::Ordinal)
$c11End=if($c11Start -ge 0){$mainSource.IndexOf('Test-Case -Id',$c11Start+1,[StringComparison]::Ordinal)}else{-1}
if($c11End -lt 0){throw 'Could not isolate the C11 Unicode selection case for cleanup verification.'}
$c11Source=$mainSource.Substring($c11Start,$c11End-$c11Start)
$escapeCount=[regex]::Matches($c11Source,"Invoke-Ui\s+@\('send-keys','esc','--via','send-input'\)").Count
$firstEscapeIndex=$c11Source.IndexOf("Invoke-Ui @('send-keys','esc','--via','send-input')",[StringComparison]::Ordinal)
$secondEscapeIndex=if($firstEscapeIndex -ge 0){$c11Source.IndexOf("Invoke-Ui @('send-keys','esc','--via','send-input')",$firstEscapeIndex+1,[StringComparison]::Ordinal)}else{-1}
$menuDismissIndex=$c11Source.IndexOf("Invoke-Ui @('wait-for','MarkdownContextCopy','--gone','-t','2000')",[StringComparison]::Ordinal)
$focusRendererIndex=$c11Source.IndexOf("Invoke-Ui @('focus','MarkdownRenderer')",[StringComparison]::Ordinal)
if($escapeCount -ne 2 -or $firstEscapeIndex -lt 0 -or $secondEscapeIndex -le $firstEscapeIndex -or
   $menuDismissIndex -le $firstEscapeIndex -or $focusRendererIndex -le $menuDismissIndex -or $secondEscapeIndex -le $focusRendererIndex -or
   $c11Source.IndexOf('$clearedSelection = Get-SelectedText',$secondEscapeIndex,[StringComparison]::Ordinal) -lt 0 -or
   $c11Source.IndexOf('$clearedHandles = Invoke-GuestProbe -Mode ''handles''',$secondEscapeIndex,[StringComparison]::Ordinal) -lt 0 -or
   $c11Source.IndexOf('$clearedMenu = @(',$secondEscapeIndex,[StringComparison]::Ordinal) -lt 0){
    throw 'C11 must dismiss the open selection menu, send a second Escape to clear the retained text selection, and verify text, handles, and menu state before the next Unicode fixture.'
}
$checks.Add([pscustomobject]@{name='C11 performs a second Escape after dismissing the selection menu and verifies all selection UI state is cleared before the next Unicode fixture';status='PASS'})

foreach($required in @(
    "'payload-hashes'",
    'MarkdownRenderer.Sample.dll',
    'MarkdownRenderer.dll',
    'Assert-GuestPayloadIdentity -BuildPayloads',
    'payloadIdentity = $payloadIdentity',
    "'range-between'",
    "'table-cell'",
    "GetMethod('CompareEndpoints').GetParameters()[0].ParameterType",
    'GridItemPattern',
    'GridPattern',
    'neighborTextRectangles',
    '1600.0 / 220.0',
    'copy-as-markdown-preserves-only-partially-selected-source-syntax',
    'copy-markdown-partial.png',
    'touch-selection-preserves-rtl-logical-text-and-combining-graphemes',
    'Combining-mark target overlaps a pre-existing selection handle',
    'layout-resize-and-selection-state-survive-relayout',
    'Restoring the original width changed or cleared selection',
    'four-local-data-svg-image-fixtures-render-with-exact-alt-and-bounds',
    'Wide teal icon-strip SVG fixture',
    'Portrait purple-and-gold SVG fixture',
    'touch-selected-link-exposes-exact-copy-link-command',
    'Copy Link clipboard was',
    "Invoke-GuestProbe -Mode 'near-origin-sequence'",
    "'image-state'",
    'svg-script-and-animation-use-accessible-failure-placeholders',
    "Save-Screenshot 'screenshots\svg-unsupported-content-failure-states.png'",
    "'^Image unavailable:\s*'",
    'ConvertTo-AsciiJson -Value $result',
    'Invoke-RedirectedUtf8Process -FileName $fileName -Arguments $argv',
    '$caseScreenshots = @($script:Screenshots.ToArray() | Select-Object -Skip $initialScreenshotCount)',
    'evidence = $caseScreenshots',
    'StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)',
    'StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)',
    '$expectedPixelHeight = [double]$imageRect.width / $expectedAspectRatio',
    '$heightRoundingError -gt 1.0',
    'Get-RangeEdgePoint ''alpha'' -Endpoint Start',
    'Get-RangeEdgePoint ''epsilon'' -Endpoint End',
    "Get-RangeCenter 'Unrelated'",
    "x [y](`$url)",
    'beforeSup <sup>raisedMarker</sup> afterSup',
    'beforeSub <sub>loweredMarker</sub> afterSub',
    'selection-list-table-crossing.png',
    'Tapping inside the committed selection did not reopen the native selection menu.',
    'function Get-ProcessWindowRoots([int]$TargetProcessId)',
    'TreeWalker]::RawViewWalker',
    'Resolve-UniqueProcessAutomationId',
    'Get-ProcessElements([int]$TargetProcessId)',
    'CurrentSamplePage',
    'Resolve-VectorHandleDestination',
    "final-math-object-selection-owned-viewport",
    "final-math-object-selection-ancestor-viewport",
    "final-mermaid-object-selection-owned-viewport",
    "final-mermaid-object-selection-ancestor-viewport",
    "final-svg-atomic-selection-owned-viewport",
    "final-svg-atomic-selection-ancestor-viewport",
    '\frac{\frac{1+1}{2}}{\frac{2+2}{4}}=1',
    'Test-FinalVectorObjectSelection -Kind Math -Viewport owned',
    'Test-FinalVectorObjectSelection -Kind Math -Viewport ancestor',
    'Test-FinalVectorObjectSelection -Kind Mermaid -Viewport owned',
    'Test-FinalVectorObjectSelection -Kind Mermaid -Viewport ancestor',
    'Test-FinalVectorObjectSelection -Kind Svg -Viewport owned',
    'Test-FinalVectorObjectSelection -Kind Svg -Viewport ancestor',
    'FinalSvgAtomicAltUnique',
    'FinalSvgBoundaryUnique',
    'MarkdownImage-',
    '$expectedMarkdown = (Get-NativeMarkdownEditorSource -Text $source).TrimEnd([char]13,[char]10)',
    '$clipboard.plain -cne $expectedMarkdown',
    '$clipboard.selectedText -cne $expectedSelection',
    'expectedSelection = "$boundary`n$semanticText"',
    'Copy as Markdown',
    'effectiveCaretDestination',
    'selectionScreenshot'
)) {
    if($mainSource.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Main correctness batch lacks required payload/vector-selection assertion '$required'."}
}

$vectorFunction = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-FinalVectorObjectSelection'
},$true)
if($null -eq $vectorFunction){throw 'Could not isolate the final embedded-object selection oracle.'}
$vectorFunctionText = $vectorFunction.Extent.Text
foreach($required in @(
    "ValidateSet('Math','Mermaid','Svg')",
    "`$semanticText = 'FinalSvgAtomicAltUnique'",
    "`$boundary = 'FinalSvgBoundaryUnique'",
    '$imageMarkdown = "![$semanticText]($imageUri)"',
    '$expectedMarkdown = (Get-NativeMarkdownEditorSource -Text $source).TrimEnd([char]13,[char]10)',
    '$selection.rangeCount -ne 1',
    '$clipboard.plain -cne $expectedMarkdown',
    '$clipboard.selectedText -cne $expectedSelection',
    'Wait-ForImagePeerState -AltText $semanticText',
    'MarkdownImage',
    'destinationInsideObject=$true',
    '$_.controlType -ceq ''ControlType.Image''',
    '$_.name -ceq $semanticText',
    '$loadedImages.Count -ne 1'
)) {
    if($vectorFunctionText.IndexOf($required,[StringComparison]::Ordinal) -lt 0){
        throw "The final SVG oracle is missing required exact semantic/source evidence '$required'."
    }
}
if($vectorFunctionText.IndexOf('$actualSource',[StringComparison]::Ordinal) -ge 0){
    throw 'The final SVG expected source must not be read back from the UI editor.'
}
$svgOwnedIndex = $mainSource.IndexOf("Test-Case -Id 'C12' -Name 'final-svg-atomic-selection-owned-viewport'",[StringComparison]::Ordinal)
$svgAncestorIndex = $mainSource.IndexOf("Test-Case -Id 'C12' -Name 'final-svg-atomic-selection-ancestor-viewport'",[StringComparison]::Ordinal)
if($svgOwnedIndex -lt 0 -or $svgAncestorIndex -le $svgOwnedIndex -or
   $mainSource.IndexOf('selected.Length -le $selectedBeforeObject.Length',[StringComparison]::Ordinal) -ge 0){
    throw 'C12 final-SVG coverage must run the exact atomic-object selection oracle in both viewport modes, not rely only on range growth.'
}
$checks.Add([pscustomobject]@{name='C12 final SVG uses exact authored alt/source and one UIA range in both viewport modes';status='PASS'})

$p1016Start = $mainSource.IndexOf("Test-Case -Id 'P1-016' -Name 'touch-word-selection-does-not-merge-adjacent-keycap-emoji'",[StringComparison]::Ordinal)
$p1016End = if($p1016Start -ge 0){$mainSource.IndexOf('Test-Case -Id',$p1016Start + 1,[StringComparison]::Ordinal)}else{-1}
if($p1016Start -lt 0){throw 'Could not find the P1-016 adjacent-keycap UI regression case.'}
if($p1016End -lt 0){$p1016End=$mainSource.Length}
$p1016Text=$mainSource.Substring($p1016Start,$p1016End-$p1016Start)
$keycap = [string][char]0x0031 + [char]0xFE0F + [char]0x20E3
foreach($required in @(
    "wordleft${keycap}wordright",
    "needle='wordleft';expected='wordleft'",
    "needle='$keycap';expected='$keycap'",
    "needle='wordright';expected='wordright'",
    "@('owned','ancestor')",
    "@('double-tap','long-press')",
    '$selection.rangeCount -ne 1',
    '$selection.ranges[0].text -cne [string]$targetCase.expected',
    '$clipboard.plain -cne [string]$targetCase.expected',
    'targetIntersections.Count -eq 0',
    'screenshots\p1-016-',
    "Invoke-Ui @('focus','MarkdownRenderer')",
    "Invoke-Ui @('send-keys','esc','--via','send-input')",
    '$clearedHandles.Count -gt 0 -or $clearedMenu.Count -gt 0'
)) {
    if($p1016Text.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "P1-016 omits required exact gesture, selection, or cleanup assertion '$required'."}
}
if($p1016Text.IndexOf('$targetCase.expected = $selected',[StringComparison]::Ordinal) -ge 0 -or
   $p1016Text.IndexOf('$expected = $selection.text',[StringComparison]::Ordinal) -ge 0){
    throw 'P1-016 expected selected text must be literal and independent of observed UIA selection.'
}
$checks.Add([pscustomobject]@{name='P1-016 exercises exact adjacent word/keycap ranges and clipboard with both gestures in both viewports';status='PASS'})

$uiGuardText=$uiFunction.Extent.Text
$inputGuardIndex=$uiGuardText.IndexOf('$null = Assert-ProcessIdentity',[StringComparison]::Ordinal)
$nativeUiCallIndex=$uiGuardText.IndexOf('Invoke-NativeChecked -Arguments $argv',[StringComparison]::Ordinal)
if($inputGuardIndex -lt 0 -or $nativeUiCallIndex -le $inputGuardIndex){throw 'State-changing UIA/input commands must verify Sandbox epoch and exact sample PID/HWND immediately before dispatch.'}
$caseGuardText=$caseFunction.Extent.Text
$caseIdentityBeforeIndex=$caseGuardText.IndexOf('$identityBefore = Assert-ProcessIdentity',[StringComparison]::Ordinal)
$caseBodyIndex=$caseGuardText.IndexOf('$actual = & $Body',[StringComparison]::Ordinal)
$caseIdentityAfterIndex=$caseGuardText.IndexOf('$identityAfter = Assert-ProcessIdentity',[StringComparison]::Ordinal)
if($caseIdentityBeforeIndex -lt 0 -or $caseBodyIndex -le $caseIdentityBeforeIndex -or $caseIdentityAfterIndex -le $caseBodyIndex -or
   $caseGuardText.IndexOf('Add-Unverified -Id $Id -Name $Name -Reason "Not run because Sandbox identity was invalidated:',[StringComparison]::Ordinal) -lt 0){
    throw 'Each test case must pin identity before and after execution and skip later cases after target invalidation.'
}
$identityGuardText=$identityFunction.Extent.Text
if($identityGuardText.IndexOf('$script:TargetIdentityInvalid = $true',[StringComparison]::Ordinal) -lt 0 -or
   $identityGuardText.IndexOf('$script:TargetIdentityFailure = $_.Exception.Message',[StringComparison]::Ordinal) -lt 0 -or
   $identityGuardText.IndexOf('$matches[0].processName -cne ''MarkdownRenderer.Sample''',[StringComparison]::Ordinal) -lt 0){
    throw 'Identity failures must permanently invalidate the batch and preserve the failure reason rather than allowing later UI actions.'
}
$guestCallText=$guestProbeFunction.Extent.Text
$sensitiveModeIndex=$guestCallText.IndexOf('$identitySensitiveMode = $Mode -in @(''cancel-contact'',''near-origin-sequence'',''resize'')',[StringComparison]::Ordinal)
$sensitivePreIndex=$guestCallText.IndexOf('$null = Assert-ProcessIdentity',[StringComparison]::Ordinal)
$guestInvokeIndex=$guestCallText.IndexOf('$result = Invoke-NativeChecked -Arguments $arguments -Json',[StringComparison]::Ordinal)
$sensitivePostIndex=$guestCallText.IndexOf('if ($identitySensitiveMode) { $null = Assert-ProcessIdentity }',[StringComparison]::Ordinal)
if($sensitiveModeIndex -lt 0 -or $sensitivePreIndex -lt 0 -or $guestInvokeIndex -le $sensitivePreIndex -or $sensitivePostIndex -le $guestInvokeIndex){
    throw 'Native touch/cancellation, suppression, and app-resize probes must use bounded pre/post identity checks around one guest call.'
}

Invoke-Expression $pointOutsideFunction.Extent.Text
$pointTestHandles=@(
    @{automationId='MarkdownSelectionStartHandle';x=100.0;y=100.0;width=48.0;height=48.0;offscreen=$false},
    @{automationId='MarkdownSelectionEndHandle';x=220.0;y=100.0;width=48.0;height=48.0;offscreen=$false}
)
$outsideEvidence=Assert-PointOutsideSelectionHandles -X 180.0 -Y 124.0 -Handles $pointTestHandles -InjectedContactRadiusPixels 2.0
if(-not [bool]$outsideEvidence.outsideBothSelectionHandleBounds -or @($outsideEvidence.handles).Count -ne 2){throw 'The unrelated-contact geometry guard failed to report both verified handle bounds.'}
foreach($contactCase in @(
    @{Name='start-handle center';X=124.0;Y=124.0},
    @{Name='end-handle center';X=244.0;Y=124.0},
    @{Name='contact footprint touches handle edge';X=98.0;Y=124.0},
    @{Name='non-finite point';X=[double]::NaN;Y=124.0}
)){
    $rejected=$false
    try{Assert-PointOutsideSelectionHandles -X $contactCase.X -Y $contactCase.Y -Handles $pointTestHandles -InjectedContactRadiusPixels 2.0 | Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw "The unrelated-contact geometry guard accepted $($contactCase.Name)."}
}
foreach($invalidHandles in @(
    @{Name='missing end';Handles=@($pointTestHandles[0])},
    @{Name='duplicate start';Handles=@($pointTestHandles[0],$pointTestHandles[0],$pointTestHandles[1])},
    @{Name='offscreen end';Handles=@($pointTestHandles[0],@{automationId='MarkdownSelectionEndHandle';x=220;y=100;width=48;height=48;offscreen=$true})}
)){
    $rejected=$false
    try{Assert-PointOutsideSelectionHandles -X 180.0 -Y 124.0 -Handles $invalidHandles.Handles -InjectedContactRadiusPixels 2.0 | Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw "The unrelated-contact geometry guard accepted $($invalidHandles.Name)."}
}
$checks.Add([pscustomobject]@{name='input dispatch and every case pin the Sandbox epoch/PID/HWND; identity loss blocks later actions; P1-003 rejects contact footprints overlapping either 44-DIP selection handle';status='PASS'})

if($mainSource.IndexOf('$targetAutomationId = [string]$target.automationId',[StringComparison]::Ordinal) -lt 0 -or
   $mainSource.IndexOf('$updatedTarget = @(Get-UiElements | Where-Object { $_.type -eq ''CheckBox'' -and (Get-UiAutomationId $_) -ceq $targetAutomationId })',[StringComparison]::Ordinal) -lt 0 -or
   $mainSource.IndexOf('$after = [string](Invoke-UiProperty -Selector $targetAutomationId -Property ''ToggleState'')',[StringComparison]::Ordinal) -lt 0 -or
   $mainSource.IndexOf('$cancelGeometry = Assert-PointOutsideSelectionHandles',[StringComparison]::Ordinal) -lt 0 -or
   $mainSource.IndexOf('$cancelGeometry;cancelInjection=$injection',[StringComparison]::Ordinal) -lt 0){
    throw 'C03 must reread the exact checkbox it touched, and P1-003 must report the validated non-handle contact geometry.'
}
$checks.Add([pscustomobject]@{name='C03 verifies ToggleState on the exact touched checkbox AutomationId; P1-003 records an input footprint disjoint from both selection handles';status='PASS'})

if ($mainSource.IndexOf("-Name 'navigation-replacement-clears-selection-and-reloads-clean-source'", [StringComparison]::Ordinal) -lt 0 -or
    $mainSource.IndexOf("-Name 'navigation-replacement-disables-selection-and-reloads-clean-source'", [StringComparison]::Ordinal) -ge 0) {
    throw 'C14 must describe the behavior it actually tests; navigation/replacement cleanup does not exercise IsSelectionEnabled=false.'
}
$checks.Add([pscustomobject]@{name='C14 is accurately labeled as navigation/replacement selection cleanup, not an IsSelectionEnabled=false test';status='PASS'})

$c14LiveRetirementMarker = "Add-Unverified -Id 'C14' -Name 'live-capture-loss-unload-and-disposal-during-touch'"
$c14LiveRetirementReason = 'Existing offline retirement coverage is not live app qualification.'
if ($mainSource.IndexOf($c14LiveRetirementMarker, [StringComparison]::Ordinal) -lt 0 -or
    $mainSource.IndexOf($c14LiveRetirementReason, [StringComparison]::Ordinal) -lt 0) {
    throw 'C14 must explicitly remain UNVERIFIED for capture loss, unload, and disposal during active touch; offline retirement coverage is not live qualification.'
}
$checks.Add([pscustomobject]@{name='C14 explicitly reports live capture-loss/unload/disposal during touch as UNVERIFIED despite offline retirement coverage';status='PASS'})

$selectionDisableParameter = @($harnessAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'IncludeSelectionDisableInvestigation' }) | Select-Object -First 1
if ($null -eq $selectionDisableParameter -or $selectionDisableParameter.StaticType -ne [System.Management.Automation.SwitchParameter] -or $null -ne $selectionDisableParameter.DefaultValue) {
    throw 'The IsSelectionEnabled UI investigation must be a default-off PowerShell switch.'
}
$runnerText = [System.IO.File]::ReadAllText($harnessPath)
$probeRunnerFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Run-SelectionDisableInvestigation'},$true)
$viewportProbeFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-SelectionDisableViewportAssertions'},$true)
$clearedProbeFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-SelectionProbeCleared'},$true)
$findProbeWindowFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Find-NewSelectionDisableProbeWindow'},$true)
$getProbeProcessIdentityFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SelectionDisableProcessIdentity'},$true)
$assertProbeLaunchReceiptFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-SelectionDisableLaunchReceipt'},$true)
$assertProbeDiscoveredIdentityFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-SelectionDisableProbeProcessIdentity'},$true)
$testProbeProcessIdentityFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-SelectionDisableProcessIdentityAlive'},$true)
$jsonPropertyFunction = $harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-JsonPropertyValue'},$true)
if ($null -eq $probeRunnerFunction -or $null -eq $viewportProbeFunction -or $null -eq $clearedProbeFunction -or $null -eq $findProbeWindowFunction -or $null -eq $getProbeProcessIdentityFunction -or $null -eq $assertProbeLaunchReceiptFunction -or $null -eq $assertProbeDiscoveredIdentityFunction -or $null -eq $testProbeProcessIdentityFunction -or $null -eq $jsonPropertyFunction) { throw 'The opt-in IsSelectionEnabled runner does not contain its isolated launch, exact cleanup identity, and viewport assertion functions.' }
foreach ($probeFunction in @($viewportProbeFunction,$clearedProbeFunction)) {
    if ($probeFunction.Extent.Text -match '\$_\.automationId' -or $probeFunction.Extent.Text.IndexOf('Get-UiAutomationId $_',[StringComparison]::Ordinal) -lt 0) {
        throw "Selection-disable assertions must use the StrictMode-safe AutomationId accessor for sparse UIA elements ($($probeFunction.Name))."
    }
}

$mockWindow = [pscustomobject]@{processName='MarkdownRenderer.Sample';processId=7890;hwnd=991122L;title='MarkdownRenderer selection-disable probe'}
Invoke-Expression $jsonPropertyFunction.Extent.Text
Invoke-Expression $findProbeWindowFunction.Extent.Text
$validWindow = Find-NewSelectionDisableProbeWindow -Windows @($mockWindow) -BeforeSamplePids @(1234,5678) -ExpectedProcessId 7890
$staleWindow = Find-NewSelectionDisableProbeWindow -Windows @($mockWindow) -BeforeSamplePids @(1234,5678) -ExpectedProcessId 7891
$oldWindow = Find-NewSelectionDisableProbeWindow -Windows @($mockWindow) -BeforeSamplePids @(1234,7890) -ExpectedProcessId 7890
if ($null -eq $validWindow -or $null -ne $staleWindow -or $null -ne $oldWindow) { throw 'Selection-disable cleanup discovery did not require one exact new process/HWND/title and, when supplied, the captured launcher PID.' }
Invoke-Expression $getProbeProcessIdentityFunction.Extent.Text
$canonicalProcessStartFunction=$harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-CanonicalProcessStartUtc'},$true)
if($null -eq $canonicalProcessStartFunction){throw 'The selection-disable process identity must define its exact UTC timestamp canonicalizer.'}
Invoke-Expression $canonicalProcessStartFunction.Extent.Text
Invoke-Expression $testProbeProcessIdentityFunction.Extent.Text
function Invoke-GuestProbe { return $script:MockSelectionDisableProcessState }
$script:MockSelectionDisableProcessState = [pscustomobject]@{mode='process-state';processId=7890;exists=$true;processName='MarkdownRenderer.Sample';executablePath='C:\sample\MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-12T23:00:00.0000000Z'}
$capturedProbeProcessIdentity = Get-SelectionDisableProcessIdentity -ProcessId 7890
if ($capturedProbeProcessIdentity.processId -ne 7890 -or $capturedProbeProcessIdentity.processName -cne 'MarkdownRenderer.Sample' -or
    -not (Test-SelectionDisableProcessIdentityAlive -State $script:MockSelectionDisableProcessState -ExpectedIdentity $capturedProbeProcessIdentity)) {
    throw 'The opt-in cleanup process identity helper did not accept a complete exact PID/path/start-time identity.'
}
$script:MockSelectionDisableProcessState = [pscustomobject]@{mode='process-state';processId=7890;exists=$true;processName='MarkdownRenderer.Sample';executablePath='C:\sample\MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-12T23:00:01.0000000Z'}
if (Test-SelectionDisableProcessIdentityAlive -State $script:MockSelectionDisableProcessState -ExpectedIdentity $capturedProbeProcessIdentity) { throw 'A reused PID with a different process start time was mistaken for the captured second-app process.' }
$script:MockSelectionDisableProcessState = [pscustomobject]@{mode='process-state';processId=7890;exists=$false;processName=$null;executablePath=$null;startTimeUtc=$null}
if (Test-SelectionDisableProcessIdentityAlive -State $script:MockSelectionDisableProcessState -ExpectedIdentity $capturedProbeProcessIdentity) { throw 'The exact second-app process identity was reported alive after an absent-process response.' }
$wrongProbePidRejected = $false
try { Test-SelectionDisableProcessIdentityAlive -State ([pscustomobject]@{mode='process-state';processId=7891;exists=$false}) -ExpectedIdentity $capturedProbeProcessIdentity | Out-Null } catch { $wrongProbePidRejected = $true }
if (-not $wrongProbePidRejected) { throw 'The exact second-app exit checker accepted a process-state result for a different PID.' }
$preciseStartUtc='2026-10-13T04:04:55.5833526Z'
$jsonStartValue=(ConvertFrom-Json -InputObject ('{{"startTimeUtc":"{0}"}}' -f $preciseStartUtc)).startTimeUtc
$dateTimeStartValue=[DateTime]::ParseExact($preciseStartUtc,'o',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
$offsetStartValue=[DateTimeOffset]::ParseExact('2026-10-13T06:04:55.5833526+02:00','o',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
foreach($startValue in @($preciseStartUtc,$jsonStartValue,$dateTimeStartValue,$offsetStartValue)){
    if((Get-CanonicalProcessStartUtc -Value $startValue) -cne $preciseStartUtc){throw "Process-start canonicalization lost UTC kind, offset meaning, or 100-nanosecond precision for '$($startValue.GetType().FullName)' ($startValue)."}
}
$unspecifiedStartRejected=$false
try{Get-CanonicalProcessStartUtc -Value ([DateTime]::SpecifyKind($dateTimeStartValue,[DateTimeKind]::Unspecified))|Out-Null}catch{$unspecifiedStartRejected=$true}
if(-not $unspecifiedStartRejected){throw 'An unspecified DateTime process-start value was accepted without a timezone identity.'}
$fractionIdentity=[pscustomobject]@{processId=7892;processName='MarkdownRenderer.Sample';executablePath='C:\sample\MarkdownRenderer.Sample.exe';startTimeUtc=$preciseStartUtc}
$fractionTickState=[pscustomobject]@{mode='process-state';processId=7892;exists=$true;processName='MarkdownRenderer.Sample';executablePath='C:\sample\MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-13T04:04:55.5833527Z'}
if(Test-SelectionDisableProcessIdentityAlive -State $fractionTickState -ExpectedIdentity $fractionIdentity){throw 'A process instance with a start time differing by one fractional tick was treated as the captured process.'}
$checks.Add([pscustomobject]@{name='process identity canonicalization preserves UTC/offset meaning and exact fractional start time across JSON DateTime or string representations, rejects unspecified time, and detects a one-tick PID-instance difference';status='PASS'})
Invoke-Expression $assertProbeLaunchReceiptFunction.Extent.Text
Invoke-Expression $assertProbeDiscoveredIdentityFunction.Extent.Text
$launchSourcePath='C:\fixture\MarkdownRenderer.Sample.exe'
$launchOriginalIdentity=[pscustomobject]@{processId=7893;processName='MarkdownRenderer.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:00:00.0000000Z'}
function New-TestSelectionDisableLaunchReceipt {
    param([string]$Scenario='valid')
    $sourceBefore=[pscustomobject]@{processId=7893;processName='MarkdownRenderer.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:00:00.0000000Z'}
    $sourceAfter=[pscustomobject]@{processId=7893;processName='MarkdownRenderer.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:00:00.0000000Z'}
    $receipt=[pscustomobject]@{mode='launch-selection-disable-probe';launchMethod='guest-Start-Process';sourceBefore=$sourceBefore;sourceAfter=$sourceAfter;sourceRetained=$true;processId=7894;processName='MarkdownRenderer.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:02:00.0000000Z';arguments=@('--renderer-selection-disable-probe')}
    switch($Scenario){
        'source-start-time-changed' { $receipt.sourceAfter.startTimeUtc='2026-10-12T23:00:00.0000001Z' }
        'new-executable-path-changed' { $receipt.executablePath='C:\other\MarkdownRenderer.Sample.exe' }
        'same-pid' { $receipt.processId=7893 }
        'argument-expanded' { $receipt.arguments=@('--renderer-selection-disable-probe','--arbitrary') }
        'wrong-launch-mode' { $receipt.mode='project-run' }
    }
    return $receipt
}
$validLaunchReceipt=New-TestSelectionDisableLaunchReceipt
$validLaunchIdentity=Assert-SelectionDisableLaunchReceipt -Receipt $validLaunchReceipt -OriginalBefore $launchOriginalIdentity -OriginalAfter $launchOriginalIdentity
if($validLaunchIdentity.processId -ne 7894 -or $validLaunchIdentity.executablePath -cne $launchSourcePath -or $validLaunchIdentity.startTimeUtc -cne '2026-10-12T23:02:00.0000000Z'){
    throw 'The host direct-launch receipt validator did not preserve the exact new guest process identity.'
}
$matchingDiscoveredIdentity=[pscustomobject]@{processId=7894;processName='MarkdownRenderer.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:02:00.0000000Z'}
$verifiedDiscoveredIdentity=Assert-SelectionDisableProbeProcessIdentity -Actual $matchingDiscoveredIdentity -Expected $validLaunchIdentity
if($verifiedDiscoveredIdentity.processId -ne 7894 -or $verifiedDiscoveredIdentity.startTimeUtc -cne $validLaunchIdentity.startTimeUtc){
    throw 'The discovered process comparator did not preserve the exact launch-receipt identity.'
}
$discoveredIdentityMutations=@(
    [pscustomobject]@{name='different PID';identity=[pscustomobject]@{processId=7895;processName='MarkdownRenderer.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:02:00.0000000Z'}},
    [pscustomobject]@{name='different process name';identity=[pscustomobject]@{processId=7894;processName='Other.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:02:00.0000000Z'}},
    [pscustomobject]@{name='different executable path';identity=[pscustomobject]@{processId=7894;processName='MarkdownRenderer.Sample';executablePath='C:\other\MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-12T23:02:00.0000000Z'}},
    [pscustomobject]@{name='one-tick changed start time';identity=[pscustomobject]@{processId=7894;processName='MarkdownRenderer.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:02:00.0000001Z'}},
    [pscustomobject]@{name='unrooted executable path';identity=[pscustomobject]@{processId=7894;processName='MarkdownRenderer.Sample';executablePath='MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-12T23:02:00.0000000Z'}}
)
foreach($mutation in $discoveredIdentityMutations){
    $rejected=$false
    try{Assert-SelectionDisableProbeProcessIdentity -Actual $mutation.identity -Expected $validLaunchIdentity|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw "Discovered-window process identity accepted $($mutation.name) instead of requiring the exact launch PID/path/start time."}
}
$checks.Add([pscustomobject]@{name='discovered probe window must resolve to the exact direct-launched PID, process name, executable path and canonical UTC start time, including one-tick PID-reuse detection';status='PASS'})
foreach($badLaunchScenario in @('source-start-time-changed','new-executable-path-changed','same-pid','argument-expanded','wrong-launch-mode')){
    $badReceipt=New-TestSelectionDisableLaunchReceipt -Scenario $badLaunchScenario
    $rejected=$false
    try{Assert-SelectionDisableLaunchReceipt -Receipt $badReceipt -OriginalBefore $launchOriginalIdentity -OriginalAfter $launchOriginalIdentity|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw "The direct guest launcher receipt validator accepted unsafe scenario '$badLaunchScenario'."}
}
$changedOriginalAfter=[pscustomobject]@{processId=7893;processName='MarkdownRenderer.Sample';executablePath=$launchSourcePath;startTimeUtc='2026-10-12T23:00:00.0000001Z'}
$originalChangeRejected=$false
try{Assert-SelectionDisableLaunchReceipt -Receipt $validLaunchReceipt -OriginalBefore $launchOriginalIdentity -OriginalAfter $changedOriginalAfter|Out-Null}catch{$originalChangeRejected=$true}
if(-not $originalChangeRejected){throw 'The direct guest launcher receipt validator failed to reject a changed host-observed original process identity.'}
$checks.Add([pscustomobject]@{name='direct guest launch receipt validator accepts only a distinct visible sample from the same original path and exact pre/post start-time identity, fixed argument and hidden helper';status='PASS'})
$emptySelectionFunction=$harnessAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-SelectionProbeHasNoSelectedContent'},$true)
if($null -eq $emptySelectionFunction){throw 'The selection probe must define a semantic empty-selection predicate.'}
Invoke-Expression $emptySelectionFunction.Extent.Text
$emptySelectionCases=@(
    [pscustomobject]@{name='None with no ranges';selection=[pscustomobject]@{supportedSelection='None';rangeCount=0;text='';ranges=@()};expected=$true},
    [pscustomobject]@{name='Single with no ranges';selection=[pscustomobject]@{supportedSelection='Single';rangeCount=0;text='';ranges=@()};expected=$true},
    [pscustomobject]@{name='Single with one rectangle-free collapsed caret';selection=[pscustomobject]@{supportedSelection='Single';rangeCount=1;text='';ranges=@([pscustomobject]@{text='';rectangles=@()})};expected=$true},
    [pscustomobject]@{name='Single with one caret rectangle';selection=[pscustomobject]@{supportedSelection='Single';rangeCount=1;text='';ranges=@([pscustomobject]@{text='';rectangles=@([pscustomobject]@{x=4;y=5;width=0;height=18})})};expected=$false},
    [pscustomobject]@{name='Single with visible selected range';selection=[pscustomobject]@{supportedSelection='Single';rangeCount=1;text='selected';ranges=@([pscustomobject]@{text='selected';rectangles=@()})};expected=$false},
    [pscustomobject]@{name='Single with two empty ranges';selection=[pscustomobject]@{supportedSelection='Single';rangeCount=2;text='';ranges=@([pscustomobject]@{text='';rectangles=@()},[pscustomobject]@{text='';rectangles=@()})};expected=$false},
    [pscustomobject]@{name='None with selected text';selection=[pscustomobject]@{supportedSelection='None';rangeCount=0;text='selected';ranges=@()};expected=$false},
    [pscustomobject]@{name='Single with aggregate selected text despite collapsed range';selection=[pscustomobject]@{supportedSelection='Single';rangeCount=1;text='selected';ranges=@([pscustomobject]@{text='';rectangles=@()})};expected=$false}
)
foreach($emptySelectionCase in $emptySelectionCases){
    $actual=[bool](Test-SelectionProbeHasNoSelectedContent -Selection $emptySelectionCase.selection)
    if($actual -ne [bool]$emptySelectionCase.expected){throw "Semantic empty-selection predicate misclassified $($emptySelectionCase.name)."}
}
$checks.Add([pscustomobject]@{name='semantic cleared-selection predicate accepts None/zero ranges and a single rectangle-free empty caret, but rejects visible geometry, selected text, and ambiguous ranges';status='PASS'})
$checks.Add([pscustomobject]@{name='selection-disable cleanup helpers functionally require an exact new app window and verify stable PID/path/start-time exit including PID reuse and absent-process states';status='PASS'})

$probeRunnerText=$probeRunnerFunction.Extent.Text
$probeIdentityCaptureIndex=$probeRunnerText.IndexOf('$probeProcessIdentity = Get-SelectionDisableProcessIdentity -ProcessId $probePid',[StringComparison]::Ordinal)
$probeIdentityCompareIndex=$probeRunnerText.IndexOf('$probeProcessIdentity = Assert-SelectionDisableProbeProcessIdentity -Actual $probeProcessIdentity -Expected $launchProcessIdentity',[StringComparison]::Ordinal)
$probeDiscoveredIndex=$probeRunnerText.IndexOf('$probeDiscovered = $true',[StringComparison]::Ordinal)
$probePayloadHashIndex=$probeRunnerText.IndexOf('$payloadReceipt = Invoke-GuestProbe -Mode ''payload-hashes''',[StringComparison]::Ordinal)
if($probeIdentityCaptureIndex -lt 0 -or $probeIdentityCompareIndex -lt $probeIdentityCaptureIndex -or
   $probeDiscoveredIndex -lt $probeIdentityCompareIndex -or $probePayloadHashIndex -lt $probeIdentityCompareIndex -or
   $probeIdentityCompareIndex -gt $probePayloadHashIndex){
    throw 'The discovered probe process instance must be compared with the launch receipt before marking it discovered or reading payload hashes/UI assertions.'
}
$recoveryIdentityGuard='$recoveredProbeProcessIdentity = Assert-SelectionDisableProbeProcessIdentity -Actual $recoveredProbeProcessIdentity -Expected $launchProcessIdentity'
$recoveryIdentityIndex=$probeRunnerText.IndexOf($recoveryIdentityGuard,[StringComparison]::Ordinal)
$recoveryDiscoveredIndex=$probeRunnerText.IndexOf('$probeDiscovered = $true',$probeDiscoveredIndex+1,[StringComparison]::Ordinal)
if($recoveryIdentityIndex -lt 0 -or $recoveryDiscoveredIndex -lt $recoveryIdentityIndex){
    throw 'Recovered cleanup identity must also be compared with the launch receipt before normal-close input is permitted.'
}
$launchAttemptIndex = $probeRunnerText.IndexOf('$launchAttempted = $true',[StringComparison]::Ordinal)
$launchCommandIndex = $probeRunnerText.IndexOf('$launch = Invoke-GuestProbe -Mode ''launch-selection-disable-probe''',[StringComparison]::Ordinal)
$launchPidCaptureIndex=$probeRunnerText.IndexOf('$reportedPid = Get-JsonPropertyValue -Object $launch -Name ''processId''',[StringComparison]::Ordinal)
$launchEpochAfterIndex=$probeRunnerText.IndexOf('Assert-SandboxSnapshot -Snapshot $afterLaunchSnapshot -Phase ''after direct selection-disable probe launch''',[StringComparison]::Ordinal)
$launchOriginalAfterIndex=$probeRunnerText.IndexOf('$originalProcessIdentityAfter = Get-SelectionDisableProcessIdentity -ProcessId $originalApp.processId',[StringComparison]::Ordinal)
$launchReceiptValidationIndex=$probeRunnerText.IndexOf('Assert-SelectionDisableLaunchReceipt -Receipt $launch -OriginalBefore $originalProcessIdentityBefore -OriginalAfter $originalProcessIdentityAfter',[StringComparison]::Ordinal)
if ($launchAttemptIndex -lt 0 -or $launchCommandIndex -lt $launchAttemptIndex -or $launchPidCaptureIndex -lt $launchCommandIndex -or
    $launchEpochAfterIndex -lt $launchPidCaptureIndex -or $launchOriginalAfterIndex -lt $launchEpochAfterIndex -or $launchReceiptValidationIndex -lt $launchOriginalAfterIndex -or
    $probeRunnerFunction.Extent.Text.IndexOf('Find-NewSelectionDisableProbeWindow -Windows $windows -BeforeSamplePids $beforeSamplePids -ExpectedProcessId $launchPid',[StringComparison]::Ordinal) -lt 0 -or
    $probeRunnerFunction.Extent.Text.IndexOf('if ($launchAttempted -and -not $probeDiscovered)',[StringComparison]::Ordinal) -lt 0 -or
    $probeRunnerFunction.Extent.Text.IndexOf('$cleanupUnverified = "A second-app launch was attempted',[StringComparison]::Ordinal) -lt 0 -or
    $probeRunnerFunction.Extent.Text.IndexOf('Invoke-Ui @(''invoke'',''LifecycleCloseProbe'',''--action'',''invoke'')',[StringComparison]::Ordinal) -lt 0 -or
    $probeRunnerFunction.Extent.Text.IndexOf('Invoke-GuestProbe -Mode ''process-state''',[StringComparison]::Ordinal) -lt 0 -or
    $probeRunnerFunction.Extent.Text.IndexOf('$processDeadline = [DateTime]::UtcNow.AddSeconds(10)',[StringComparison]::Ordinal) -lt 0 -or
    $probeRunnerFunction.Extent.Text.IndexOf('exactProcessIdentityExited=$processExited',[StringComparison]::Ordinal) -lt 0 -or
    $probeRunnerFunction.Extent.Text -match '(?i)\b(?:Stop-Process|taskkill)\b') {
    throw 'The opt-in direct-launch cleanup must capture the guest PID before post-launch guards, verify same-epoch/original PID-path-start-time identity, recover only an exact new window, report unsafe recovery as UNVERIFIED, normal-close by UIA, and verify exact process-instance exit without kill commands.'
}
$guestLaunchFunctionText=$guestDirectLaunchFunction.Extent.Text
$guestIdentityFunctionText=$guestProcessIdentityFunction.Extent.Text
$hostGuestProbeText=$guestProbeFunction.Extent.Text
if($probeRunnerText -match '@\(''run''\s*,\s*\$script:ProjectPath' -or
    $probeRunnerText -match '(?i)ProcessScope|launchEpoch|project-mode selection-disable' -or
    $guestLaunchFunctionText.IndexOf('Get-GuestSampleProcessIdentity -ExpectedProcessId $OriginalProcessId',[StringComparison]::Ordinal) -lt 0 -or
    $guestLaunchFunctionText.IndexOf('Start-Process -FilePath $sourceBefore.executablePath',[StringComparison]::Ordinal) -lt 0 -or
    $guestLaunchFunctionText.IndexOf('-ArgumentList @(''--renderer-selection-disable-probe'')',[StringComparison]::Ordinal) -lt 0 -or
    $guestLaunchFunctionText.IndexOf('-PassThru -WindowStyle Normal',[StringComparison]::Ordinal) -lt 0 -or
    $guestLaunchFunctionText.IndexOf('$sourceAfter = Get-GuestSampleProcessIdentity -ExpectedProcessId $OriginalProcessId',[StringComparison]::Ordinal) -lt 0 -or
    $guestIdentityFunctionText.IndexOf('$processName -cne ''MarkdownRenderer.Sample''',[StringComparison]::Ordinal) -lt 0 -or
    $guestIdentityFunctionText.IndexOf('[IO.Path]::IsPathRooted($rawPath)',[StringComparison]::Ordinal) -lt 0 -or
    $guestIdentityFunctionText.IndexOf('[IO.Path]::GetFileName($rawPath) -cne ''MarkdownRenderer.Sample.exe''',[StringComparison]::Ordinal) -lt 0){
    throw 'The optional second app must be started directly from the exact validated live guest process path with a fixed argument and without project-mode redeployment or invented CLI launch metadata.'
}
if($hostGuestProbeText.IndexOf('$TargetProcessId -lt 0 -or ($TargetProcessId -ne 0 -and $Mode -cne ''process-state'')',[StringComparison]::Ordinal) -lt 0 -or
    $hostGuestProbeText.IndexOf('$guestProcessId = if ($Mode -ceq ''process-state'' -and $TargetProcessId -gt 0)',[StringComparison]::Ordinal) -lt 0 -or
    $hostGuestProbeText.IndexOf('if ($Mode -ceq ''launch-selection-disable-probe'') { $arguments += ''-WindowStyle''; $arguments += ''Hidden'' }',[StringComparison]::Ordinal) -lt 0){
    throw 'Guest process ID overrides must remain read-only process-state queries, and the direct launch helper PowerShell must be hidden.'
}
foreach ($required in @(
    'if ($IncludeSelectionDisableInvestigation) { Run-SelectionDisableInvestigation }',
    'if (-not $IncludeSelectionDisableInvestigation)',
    'Assert-SandboxSnapshot -Snapshot $beforeSnapshot -Phase ''before selection-disable probe launch''',
    'Invoke-GuestProbe -Mode ''launch-selection-disable-probe''',
    'Assert-SelectionDisableLaunchReceipt -Receipt $launch -OriginalBefore $originalProcessIdentityBefore -OriginalAfter $originalProcessIdentityAfter',
    'targetEpochBefore=$script:TargetEpoch;targetEpochAfter=[string]$afterLaunchSnapshot.executionTarget.epoch',
    'launchProcessIdentity=$launchProcessIdentity;guestProcessIdentity=$probeProcessIdentity',
    "`$title -ceq 'MarkdownRenderer selection-disable probe'",
    "`$processName -ceq 'MarkdownRenderer.Sample'",
    "`$candidatePid -notin `$BeforeSamplePids",
    "`$candidatePid -eq `$ExpectedProcessId",
    'Assert-GuestPayloadIdentity -BuildPayloads $script:BuildPayloads -GuestPayloadReceipt $payloadReceipt',
    'Invoke-Ui @(''invoke'',''LifecycleCloseProbe'',''--action'',''invoke'')',
    'The exact second-app PID/start-time/path identity remained live after its HWND closed.',
    'Add-SelectionDisableProbeResult -Status ''UNVERIFIED''',
    '-Name ''is-selection-enabled-disables-and-reenables-owned-viewport''',
    '-Name ''is-selection-enabled-disables-and-reenables-ancestor-viewport''',
    'Invoke-Ui @(''send-keys'',''ctrl+a'',''--via'',''send-input'')',
    'Invoke-Ui @(''touch'',''MarkdownRenderer'',''--gesture'',''double-tap'',''--at''',
    'Test-SelectionProbeHasNoSelectedContent -Selection $selection',
    'ExpectedSupport=''None''',
    'ExpectedSupport ''Single''',
    'Assert-SelectionProbeCleared -Context "Initial $Viewport viewport state"',
    'Assert-SelectionProbeCleared -Context "$Viewport selection-disable transition"',
    'Assert-SelectionProbeCleared -Context "$Viewport Ctrl+A while disabled"',
    'Assert-SelectionProbeCleared -Context "$Viewport touch double-tap while disabled"',
    '$result=@{mode=$Mode;supportedSelection=[string]$pattern.SupportedTextSelection;',
    "'MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll'",
    'selection-disable-$Viewport-cleared.png'
)) {
    if ($runnerText.IndexOf($required,[StringComparison]::Ordinal) -lt 0) { throw "Opt-in selection-disable investigation is missing required identity, disabled-input, re-enable, menu, or evidence assertion '$required'." }
}
if ($runnerText.IndexOf('if ($IncludeSelectionDisableInvestigation) { Run-SelectionDisableInvestigation }',[StringComparison]::Ordinal) -lt 0 -or
    [regex]::Matches($runnerText,"(?m)^\s*Run-SelectionDisableInvestigation\s*$").Count -gt 0) {
    throw 'The second app must not be launched from the normal default path.'
}

$appSource = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\MarkdownRenderer.Sample\App.xaml.cs'))
$probeWindowSource = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\MarkdownRenderer.Sample\RendererLifecycleProbeWindow.cs'))
foreach ($required in @(
    'Contains("--renderer-selection-disable-probe")',
    'new RendererLifecycleProbeWindow(selectionDisableProbe: true)',
    'new RendererLifecycleProbeWindow()',
    'internal sealed class RendererLifecycleProbeWindow',
    'private void BuildSelectionDisableProbe()',
    'AutomationProperties.SetAutomationId(_selectionProbeEnabled, "LifecycleSelectionEnabled")',
    'AutomationProperties.SetAutomationId(_selectionProbeAncestor, "LifecycleAncestorViewport")',
    'AutomationProperties.SetAutomationId(_status, "LifecycleSelectionStatus")',
    'AutomationProperties.SetAutomationId(close, "LifecycleCloseProbe")',
    'ActiveSelectionProbeRenderer.IsSelectionEnabled = enabled',
    'ActiveSelectionProbeRenderer.IsSelectionEnabled = _selectionProbeEnabled.IsChecked == true',
    'Selection disable probe',
    'keyboardenabledword',
    'touchenabledword'
)) {
    $source = if ($required.StartsWith('Contains(') -or $required.StartsWith('new RendererLifecycleProbeWindow')) { $appSource } else { $probeWindowSource }
    if ($source.IndexOf($required,[StringComparison]::Ordinal) -lt 0) { throw "Internal selection-disable probe source is missing '$required'." }
}
if ($appSource -match 'public\s+(?:class|record|interface)\s+.*SelectionDisable' -or $probeWindowSource -match 'public\s+(?:class|record|interface)\s+.*SelectionDisable') {
    throw 'Selection-disable UI testing must remain an internal sample-only probe, not a new public library feature.'
}
$selectionProbeMethodStart = $probeWindowSource.IndexOf('private void BuildSelectionDisableProbe()',[StringComparison]::Ordinal)
$selectionProbeMethodEnd = $probeWindowSource.IndexOf('private MarkdownRendererControl ActiveSelectionProbeRenderer',[StringComparison]::Ordinal)
if ($selectionProbeMethodStart -lt 0 -or $selectionProbeMethodEnd -le $selectionProbeMethodStart) { throw 'Could not isolate the internal selection-disable probe window setup.' }
$selectionProbeMethodText = $probeWindowSource.Substring($selectionProbeMethodStart,$selectionProbeMethodEnd-$selectionProbeMethodStart)
foreach ($required in @('Closed += (_, _) =>','_ancestor.Content = null;','_selectionProbeHost.Children.Clear();','_owned.Dispose();','_document.Dispose();')) {
    if ($selectionProbeMethodText.IndexOf($required,[StringComparison]::Ordinal) -lt 0) { throw "The opt-in probe window must detach and dispose both renderer instances on normal close; missing '$required'." }
}
if ($mainSource.IndexOf("Add-Unverified -Id 'C14' -Name 'is-selection-enabled-disable-and-reenable-owned-and-ancestor-viewports'",[StringComparison]::Ordinal) -lt 0) {
    throw 'The IsSelectionEnabled gate must remain explicitly UNVERIFIED in the default run.'
}
$checks.Add([pscustomobject]@{name='default-off second-app C14 probe pins same Sandbox epoch/process/HWND/title and managed payloads, tests disabled UIA/menu/keyboard/touch plus re-enable in both viewports, and normal-closes its isolated internal sample window';status='PASS'})

'PASS: strict-mode probe defaults omit empty native arguments; optional arguments and ASCII-safe emoji JSON round-trip; redirected UTF-8 stdout/stderr preserve process status; SDK POINTER_TYPE_INFO synthetic-touch ABI, cancellation sequence, and routed XamlRoot-client contact correlation are verified; failure screenshots attach to pass/fail results; SVG typed placeholder and exact atomic-selection assertions are present; semantic range scrolling, payload identity, final Math/Mermaid/SVG selection, adjacent keycap/word touch boundaries, touch-offset geometry, DPI cleanup, per-action target pinning, exact checkbox state, and canceled-contact handle exclusion contracts are present. Native input, guest state, and display settings were not changed.'
