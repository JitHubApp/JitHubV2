[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "Sandbox UI configuration contract failed: $Message"
    }
}

$scriptPath = Join-Path $PSScriptRoot 'Test-SandboxSampleUi.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) 'the sandbox UI smoke script must parse'

$parameterNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
Assert-True (($parameterNames -join ',') -ceq 'NoBuild,OutputDirectory,Configuration') 'Configuration must be appended so existing positional arguments remain compatible'

$configurationParameters = @($ast.ParamBlock.Parameters | Where-Object {
    $_.Name.VariablePath.UserPath -ceq 'Configuration'
})
Assert-True ($configurationParameters.Count -eq 1) 'Configuration must be declared exactly once'
$configurationParameter = $configurationParameters[0]
Assert-True ($configurationParameter.DefaultValue.Extent.Text -ceq "'Debug'") 'Debug must remain the backward-compatible default'

$configurationConstraints = @($configurationParameter.Attributes | Where-Object {
    $_.TypeName.FullName -ceq 'ValidateSet'
})
Assert-True ($configurationConstraints.Count -eq 1) 'Configuration must be constrained to supported build configurations'
$allowedConfigurations = @($configurationConstraints[0].PositionalArguments | ForEach-Object { $_.Value })
Assert-True (($allowedConfigurations -join ',') -ceq 'Debug,Release') 'only Debug and Release may be selected'

$runArgumentAssignments = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -ceq 'runArguments' -and
        $node.Operator.ToString() -ceq 'Equals'
}, $true))
Assert-True ($runArgumentAssignments.Count -eq 1) 'the smoke must construct one project-mode WinApp invocation'
$runArgumentText = $runArgumentAssignments[0].Right.Extent.Text
Assert-True ($runArgumentText.Contains('''-c'', $Configuration')) 'the selected configuration must be passed to winapp run'
Assert-True (-not $runArgumentText.Contains('''-c'', ''Debug''')) 'the launch must not hardcode Debug'

$noBuildBranch = $ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text -match 'if\s*\(\s*\$NoBuild\s*\)[\s\S]*?\$runArguments\s*\+=\s*''--no-build'''
}, $true)
Assert-True (@($noBuildBranch).Count -eq 1) '-NoBuild must continue to pass --no-build independently of configuration'

Write-Host 'Sandbox sample UI configuration contract passed.'
