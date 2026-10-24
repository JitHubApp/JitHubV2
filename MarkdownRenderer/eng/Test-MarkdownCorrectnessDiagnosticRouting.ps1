[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runnerPath = Join-Path $PSScriptRoot 'Invoke-MarkdownCorrectnessUiTests.ps1'
$tokens = $null
$parseErrors = $null
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Runner parse failed: $($parseErrors[0].Message)" }

$script:CheckCount = 0
function Assert-Check {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Condition)
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:CheckCount++
    Write-Host "PASS: $Name"
}

function Get-FunctionAstText {
    param([Parameter(Mandatory)][string]$Name)
    $functionAst = $runnerAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
    }, $true)
    if ($null -eq $functionAst) { throw "Runner is missing function '$Name'." }
    return $functionAst.Extent.Text
}

$functionNames = @(
    'Should-RunCorrectnessCase',
    'Assert-CorrectnessRunOptions',
    'Add-Unverified',
    'Get-CaseCounts',
    'Test-Case',
    'New-RunResultsReport'
)
$functionText = @($functionNames | ForEach-Object { Get-FunctionAstText -Name $_ }) -join [Environment]::NewLine
. ([scriptblock]::Create($functionText))

$validationStatement = @($runnerAst.EndBlock.Statements | Where-Object {
    $_.Extent.Text -match '^Assert-CorrectnessRunOptions\b'
}) | Select-Object -First 1
$outerTry = @($runnerAst.EndBlock.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.TryStatementAst]
}) | Select-Object -Last 1
if ($null -eq $validationStatement -or $null -eq $outerTry) {
    throw 'Could not locate the actual top-level option guard and run try/finally in the runner.'
}
$initializeCall = $outerTry.Find({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Initialize-Run'
}, $true)
if ($null -eq $initializeCall) { throw 'Could not locate Initialize-Run inside the runner try/finally.' }
Assert-Check -Name 'invalid-option guard precedes the UI try/finally and Initialize-Run' -Condition ($validationStatement.Extent.StartOffset -lt $outerTry.Extent.StartOffset -and $initializeCall.Extent.StartOffset -gt $validationStatement.Extent.StartOffset)

$paramBlockText = $runnerAst.ParamBlock.Extent.Text
$validationFunctionText = Get-FunctionAstText -Name 'Assert-CorrectnessRunOptions'
$validationStatementText = $validationStatement.Extent.Text
function Invoke-BoundInitializationMock {
    param([string[]]$Arguments)

    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ('MarkdownCorrectnessDiagnosticBinding-{0}.ps1' -f [guid]::NewGuid().ToString('N'))
    $source = @'
__PARAM_BLOCK__
$script:InitializeCalls = 0
function Initialize-Run { $script:InitializeCalls++; return 'mock-initialized' }
__VALIDATION_FUNCTION__
try {
    __VALIDATION_STATEMENT__
    $null = Initialize-Run
    [pscustomobject]@{ accepted=$true; initializeCalls=$script:InitializeCalls; diagnosticOnly=[bool]$ResizeCrashDiagnosticOnly } | ConvertTo-Json -Compress
}
catch {
    [pscustomobject]@{ accepted=$false; initializeCalls=$script:InitializeCalls; diagnosticOnly=[bool]$ResizeCrashDiagnosticOnly; error=$_.Exception.Message } | ConvertTo-Json -Compress
}
'@
    $source = $source.Replace('__PARAM_BLOCK__', $paramBlockText).
        Replace('__VALIDATION_FUNCTION__', $validationFunctionText).
        Replace('__VALIDATION_STATEMENT__', $validationStatementText)
    [System.IO.File]::WriteAllText($tempPath, $source, [System.Text.UTF8Encoding]::new($false))
    try {
        $argumentKey = $Arguments -join '|'
        switch ($argumentKey) {
            '' { $output = @(& $tempPath) }
            '-ResizeCrashDiagnosticOnly' { $output = @(& $tempPath -ResizeCrashDiagnosticOnly) }
            '-ResizeCrashDiagnosticOnly|-IncludeSuppressionInvestigation' { $output = @(& $tempPath -ResizeCrashDiagnosticOnly -IncludeSuppressionInvestigation) }
            '-ResizeCrashDiagnosticOnly|-IncludeSelectionDisableInvestigation' { $output = @(& $tempPath -ResizeCrashDiagnosticOnly -IncludeSelectionDisableInvestigation) }
            default { throw "Unexpected mock parameter combination '$argumentKey'." }
        }
        if ($output.Count -ne 1) { throw "Mock binding script returned $($output.Count) output records instead of one." }
        return ConvertFrom-Json -InputObject ([string]$output[0])
    }
    finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}

$defaultBinding = Invoke-BoundInitializationMock -Arguments @()
Assert-Check -Name 'actual parameter block defaults to full mode and reaches mocked initialization' -Condition ($defaultBinding.accepted -and -not $defaultBinding.diagnosticOnly -and $defaultBinding.initializeCalls -eq 1)
$focusedBinding = Invoke-BoundInitializationMock -Arguments @('-ResizeCrashDiagnosticOnly')
Assert-Check -Name 'actual parameter block binds focused diagnostic switch' -Condition ($focusedBinding.accepted -and $focusedBinding.diagnosticOnly -and $focusedBinding.initializeCalls -eq 1)
$suppressionConflict = Invoke-BoundInitializationMock -Arguments @('-ResizeCrashDiagnosticOnly', '-IncludeSuppressionInvestigation')
Assert-Check -Name 'suppression conflict is rejected before mocked initialization' -Condition ((-not $suppressionConflict.accepted) -and $suppressionConflict.initializeCalls -eq 0 -and $suppressionConflict.error -match 'cannot be combined')
$selectionConflict = Invoke-BoundInitializationMock -Arguments @('-ResizeCrashDiagnosticOnly', '-IncludeSelectionDisableInvestigation')
Assert-Check -Name 'selection-disable conflict is rejected before mocked initialization' -Condition ((-not $selectionConflict.accepted) -and $selectionConflict.initializeCalls -eq 0 -and $selectionConflict.error -match 'cannot be combined')

$runFunction = $runnerAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Run-CorrectnessCases'
}, $true)
if ($null -eq $runFunction) { throw 'Runner is missing Run-CorrectnessCases.' }
$caseCalls = @($runFunction.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Test-Case'
}, $true))
function Get-NamedStringArgument {
    param([Parameter(Mandatory)]$Command, [Parameter(Mandatory)][string]$ParameterName)
    for ($index = 1; $index -lt $Command.CommandElements.Count - 1; $index++) {
        $element = $Command.CommandElements[$index]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -ceq $ParameterName) {
            $value = $Command.CommandElements[$index + 1]
            if ($value -is [System.Management.Automation.Language.StringConstantExpressionAst]) { return [string]$value.Value }
        }
    }
    return $null
}
$c00Calls = @($caseCalls | Where-Object { (Get-NamedStringArgument -Command $_ -ParameterName 'Id') -ceq 'C00' -and (Get-NamedStringArgument -Command $_ -ParameterName 'Name') -ceq 'sandbox-and-app-identity' })
$c14Calls = @($caseCalls | Where-Object { (Get-NamedStringArgument -Command $_ -ParameterName 'Id') -ceq 'C14' -and (Get-NamedStringArgument -Command $_ -ParameterName 'Name') -ceq 'layout-resize-and-selection-state-survive-relayout' })
Assert-Check -Name 'runner retains exactly one existing C00 identity case' -Condition ($c00Calls.Count -eq 1)
Assert-Check -Name 'runner retains exactly one exact existing C14 resize case' -Condition ($c14Calls.Count -eq 1)

$script:Results = [System.Collections.Generic.List[object]]::new()
$script:Screenshots = [System.Collections.Generic.List[string]]::new()
$script:TargetIdentityInvalid = $false
$script:TargetIdentityFailure = ''
$script:ResizeCrashDiagnosticOnly = $false
$script:BodyCalls = [System.Collections.Generic.List[string]]::new()
function Assert-ProcessIdentity { return [pscustomobject]@{ processName='MarkdownRenderer.Sample'; title='mock-window' } }

Test-Case -Id 'C02' -Name 'ordinary-full-suite-case' -Requirement 'default mode remains unfiltered' -Body {
    $script:BodyCalls.Add('default')
    return 'default body ran'
}
Assert-Check -Name 'default Test-Case behavior still runs arbitrary suite bodies' -Condition ($script:BodyCalls.Count -eq 1 -and $script:Results[0].status -ceq 'PASS')

$script:Results.Clear()
$script:BodyCalls.Clear()
$script:ResizeCrashDiagnosticOnly = $true
Test-Case -Id 'C00' -Name 'sandbox-and-app-identity' -Requirement 'identity guard case' -Body {
    $script:BodyCalls.Add('C00')
    return 'identity body ran'
}
Test-Case -Id 'C14' -Name 'layout-resize-and-selection-state-survive-relayout' -Requirement 'existing resize case' -Body {
    $script:BodyCalls.Add('C14')
    return 'resize body ran'
}
Test-Case -Id 'C14' -Name 'layout-resize-and-selection-state-survive-relayout-near-match' -Requirement 'near-name must be omitted' -Body {
    $script:BodyCalls.Add('near')
    return 'near body must not run'
}
Test-Case -Id 'C00' -Name 'sandbox-and-app-identity-near-match' -Requirement 'near-name identity must be omitted' -Body {
    $script:BodyCalls.Add('near-C00')
    return 'near identity body must not run'
}
$focusedCounts = Get-CaseCounts
Assert-Check -Name 'focused Test-Case executes only C00 plus the exact C14 name' -Condition ($script:BodyCalls.Count -eq 2 -and $script:BodyCalls[0] -ceq 'C00' -and $script:BodyCalls[1] -ceq 'C14')
Assert-Check -Name 'near-name cases are recorded UNVERIFIED and their bodies never run' -Condition ($focusedCounts.pass -eq 2 -and $focusedCounts.unverified -eq 2 -and @($script:Results | Where-Object { $_.status -ceq 'UNVERIFIED' -and $_.detail -like 'Not run: resize crash diagnostic mode*' }).Count -eq 2)

$script:RunId = 'mock-run'
$script:TargetEpoch = 'mock-epoch'
$script:AppProcessId = 123
$script:AppWindowHandle = 456L
$script:ResizeCrashDiagnosticOnly = $true
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:Results.Add([pscustomobject]@{ status='PASS'; id='C00'; name='mock-pass' })
$script:Screenshots = [System.Collections.Generic.List[string]]::new()
$focusedReport = New-RunResultsReport
Assert-Check -Name 'focused report stays incomplete and states no-crash heap-conditioning caveat' -Condition ($focusedReport.diagnosticOnly -and -not $focusedReport.complete -and $focusedReport.resultCounts.pass -eq 1 -and $focusedReport.diagnosticOnlyNote -like '*inconclusive about heap conditioning*' -and $focusedReport.diagnosticOnlyNote -like '*not full Phase 1 qualification*')
$script:ResizeCrashDiagnosticOnly = $false
$fullReport = New-RunResultsReport
Assert-Check -Name 'default report retains prior completeness rule' -Condition ((-not $fullReport.diagnosticOnly) -and $fullReport.complete)

Write-Host "Diagnostic routing checks passed: $script:CheckCount"
