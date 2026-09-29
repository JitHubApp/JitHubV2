param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-SampleControl([string]$ControlId) {
    winapp ui invoke $ControlId -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "Could not invoke $ControlId" }
}

function Set-ToggleState([string]$ControlId, [bool]$Enabled) {
    $state = winapp ui get-property $ControlId -a $AppPid --property ToggleState --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not read $ControlId" }
    $isEnabled = [string]$state.properties.ToggleState -eq 'On'
    if ($isEnabled -ne $Enabled) { Invoke-SampleControl $ControlId }
}

function Expand-Elements($Elements) {
    foreach ($element in $Elements) {
        $element
        Expand-Elements $element.children
    }
}

function Get-RendererElements {
    $treeJson = winapp ui inspect MarkdownRenderer -a $AppPid --depth 10 --interactive --json
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the renderer.' }
    $tree = $treeJson | ConvertFrom-Json
    return @(Expand-Elements $tree.windows.elements)
}

function Wait-ForTaskControls([int]$ExpectedCount) {
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
        $taskControls = @(Get-RendererElements | Where-Object automationId -Like 'MarkdownTask_*')
        if ($taskControls.Count -eq $ExpectedCount) { return $taskControls }
        Start-Sleep -Milliseconds 100
    } while ([datetime]::UtcNow -lt $deadline)
    throw "Expected $ExpectedCount interactive task controls, got $($taskControls.Count)."
}

function Test-TaskState([string]$Name, [scriptblock]$Test) {
    try {
        & $Test
        $results.Add([pscustomobject]@{ name = $Name; status = 'PASS' })
    }
    catch {
        $results.Add([pscustomobject]@{
            name = $Name
            status = 'FAIL'
            detail = $_.Exception.Message
        })
    }
}

# Reset the page source even when the script is rerun against the same process.
Invoke-SampleControl SampleNav_Typography
Invoke-SampleControl SampleNav_Lists
winapp ui wait-for MarkdownRenderer -a $AppPid --value 'Task Lists (GFM)' --contains -t 10000
if ($LASTEXITCODE -ne 0) { throw 'The Lists page did not become current.' }
Set-ToggleState TaskEditingToggle $false
Set-ToggleState ForcedHighContrastToggle $false
Set-ToggleState ThemeToggle $true

Test-TaskState 'read-only-task-semantics' {
    $elements = Get-RendererElements
    $interactiveTasks = @($elements | Where-Object automationId -Like 'MarkdownTask_*')
    if ($interactiveTasks.Count -ne 0) {
        throw 'Read-only task markers exposed an interactive automation element.'
    }
    $semanticTasks = @($elements | Where-Object {
        $_.name -Like 'Completed task*' -or $_.name -Like 'Incomplete task*'
    })
    if ($semanticTasks.Count -ne 5) {
        throw "Expected five task states in the document tree, got $($semanticTasks.Count)."
    }
    winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory 'read-only-dark.png') --json
    if ($LASTEXITCODE -ne 0) { throw 'Read-only screenshot capture failed.' }
}

Test-TaskState 'editable-native-checkboxes' {
    Invoke-SampleControl TaskEditingToggle
    winapp ui wait-for TaskEditingToggle -a $AppPid --value On -t 3000
    if ($LASTEXITCODE -ne 0) { throw 'Task editing did not turn on.' }
    $taskControls = @(Wait-ForTaskControls 5 | Sort-Object y)
    if (@($taskControls | Where-Object type -ne 'CheckBox').Count -ne 0) {
        throw 'An editable task marker is not exposed as a native CheckBox.'
    }
    $firstId = [string]$taskControls[0].automationId
    winapp ui wait-for $firstId -a $AppPid --value On -t 3000
    if ($LASTEXITCODE -ne 0) { throw 'The first task did not begin checked.' }
    Invoke-SampleControl $firstId
    winapp ui wait-for $firstId -a $AppPid --value Off -t 10000
    if ($LASTEXITCODE -ne 0) { throw 'The native checkbox did not commit its new state.' }
    winapp ui wait-for MarkdownEditor -a $AppPid --value '- [ ] Design the layout engine' --contains -t 3000
    if ($LASTEXITCODE -ne 0) { throw 'The task command did not update the markdown source.' }
    winapp ui focus $firstId -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw 'Could not focus the native checkbox.' }
    winapp ui send-keys space -a $AppPid --via post-message
    if ($LASTEXITCODE -ne 0) { throw 'Could not activate the native checkbox from the keyboard.' }
    winapp ui wait-for $firstId -a $AppPid --value On -t 10000
    if ($LASTEXITCODE -ne 0) { throw 'The native checkbox did not toggle from the keyboard.' }
    winapp ui wait-for MarkdownEditor -a $AppPid --value '- [x] Design the layout engine' --contains -t 3000
    if ($LASTEXITCODE -ne 0) { throw 'The keyboard toggle did not update the markdown source.' }
    winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory 'editable-dark.png') --json
    if ($LASTEXITCODE -ne 0) { throw 'Editable screenshot capture failed.' }
}

Test-TaskState 'high-contrast-checkboxes' {
    Invoke-SampleControl ForcedHighContrastToggle
    winapp ui wait-for ForcedHighContrastToggle -a $AppPid --value On -t 3000
    if ($LASTEXITCODE -ne 0) { throw 'Forced High Contrast did not activate.' }
    [void](Wait-ForTaskControls 5)
    winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory 'editable-high-contrast.png') --json
    if ($LASTEXITCODE -ne 0) { throw 'High Contrast screenshot capture failed.' }
}

Test-TaskState 'return-to-read-only' {
    Invoke-SampleControl TaskEditingToggle
    winapp ui wait-for TaskEditingToggle -a $AppPid --value Off -t 3000
    if ($LASTEXITCODE -ne 0) { throw 'Task editing did not turn off.' }
    [void](Wait-ForTaskControls 0)
    Set-ToggleState ForcedHighContrastToggle $false
}

$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json')
$results | Format-Table name, status, detail -AutoSize
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
