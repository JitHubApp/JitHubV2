param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$OutputDirectory
)

# Attach to a sample launched using project-mode winapp run.
$ErrorActionPreference = 'Stop'
$ExpectedPrimaryPageDiagramCount = 17
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
Add-Type -AssemblyName UIAutomationClient

function Invoke-SampleControl([string]$ControlId) {
    winapp ui invoke $ControlId -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "Could not invoke $ControlId" }
}

function Expand-Elements($Elements) {
    foreach ($element in $Elements) {
        $element
        Expand-Elements $element.children
    }
}

function Get-RawAutomationName([string]$AutomationId) {
    $automationElement = [System.Windows.Automation.AutomationElement]
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        $automationElement::ProcessIdProperty,
        $AppPid)
    $appRoot = $automationElement::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    if ($null -eq $appRoot) { return $null }

    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $pending = [System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new()
    $pending.Push($appRoot)
    while ($pending.Count -gt 0) {
        $element = $pending.Pop()
        try {
            if ($element.Current.AutomationId -eq $AutomationId) {
                return $element.Current.Name
            }

            $child = $walker.GetFirstChild($element)
            while ($null -ne $child) {
                $pending.Push($child)
                $child = $walker.GetNextSibling($child)
            }
        }
        catch {
            # A XAML rebuild can retire an element while UIA is walking it.
            # Retry from the application root on the next polling interval.
        }
    }

    return $null
}

function Wait-ForCommittedTheme([string]$ExpectedTheme) {
    $expectedStatus = "theme:$ExpectedTheme"
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if ((Get-RawAutomationName 'ThemeStatus') -eq $expectedStatus) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The renderer did not commit $expectedStatus before the timeout."
}

function Test-DisplayToggleState([string]$ExpectedTheme) {
    $theme = winapp ui get-property ThemeToggle -a $AppPid --property ToggleState --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read ThemeToggle.' }
    $contrast = winapp ui get-property ForcedHighContrastToggle -a $AppPid --property ToggleState --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read ForcedHighContrastToggle.' }
    $themeOn = [string]$theme.properties.ToggleState -eq 'On'
    $contrastOn = [string]$contrast.properties.ToggleState -eq 'On'
    $expectedThemeOn = $ExpectedTheme -ne 'light'
    $expectedContrastOn = $ExpectedTheme -eq 'high-contrast'
    if ($themeOn -ne $expectedThemeOn -or $contrastOn -ne $expectedContrastOn) {
        throw "Display toggles do not represent $ExpectedTheme."
    }
}

function Test-Diagram(
    [string]$Name,
    [string]$Title,
    [int]$ExpectedDiagramCount,
    [int]$FallbackCount,
    [string]$ExpectedTheme,
    [string]$Source) {
    try {
        if ($Source) {
            winapp ui set-value MarkdownEditor $Source -a $AppPid
            if ($LASTEXITCODE -ne 0) { throw 'Could not change the sample source.' }
        }
        winapp ui wait-for MarkdownRenderer -a $AppPid --value $Title --contains -t 10000
        if ($LASTEXITCODE -ne 0) { throw 'The diagram document did not render.' }
        # The toggle state changes before the asynchronous renderer snapshot is
        # committed. Synchronize on the sample's raw-tree RenderCompleted probe;
        # a fixed delay can capture CanvasVirtualControl between tile updates.
        Wait-ForCommittedTheme $ExpectedTheme
        Start-Sleep -Milliseconds 100
        Test-DisplayToggleState $ExpectedTheme
        $treeJson = winapp ui inspect MarkdownRenderer -a $AppPid --depth 8 --json
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the diagram.' }
        $elements = @(Expand-Elements (($treeJson | ConvertFrom-Json).windows.elements))
        $diagrams = @($elements | Where-Object className -eq 'MarkdownDiagram')
        if ($diagrams.Count -ne $ExpectedDiagramCount) {
            throw "Expected $ExpectedDiagramCount native diagram(s), got $($diagrams.Count)."
        }
        if (@($diagrams | Where-Object { $_.width -le 0 -or $_.height -le 0 }).Count) {
            throw 'A diagram has no bounds.'
        }
        if (@($elements | Where-Object className -eq 'MarkdownCodeBlock').Count -ne $FallbackCount) {
            throw 'Unexpected source fallback.'
        }
        if ($Name.StartsWith('linked-flowchart', [System.StringComparison]::Ordinal)) {
            $link = @($elements | Where-Object className -eq 'MarkdownDiagramLink')
            if ($link.Count -ne 1 -or $link[0].name -ne 'Invokable Mermaid node') { throw 'Linked node semantics were lost.' }
            winapp ui invoke $link[0].automationId -a $AppPid
            if ($LASTEXITCODE -ne 0) { throw 'The native node link could not be invoked.' }
            # Host dispatch is asserted through the raw-tree probe in the main
            # FlaUI suite; this visual gate verifies the public InvokePattern.
        }
        winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory "$Name.png") --json
        if ($LASTEXITCODE -ne 0) { throw 'Screenshot failed.' }
        $results.Add([pscustomobject]@{ name = $Name; status = 'PASS' })
    }
    catch { $results.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; detail = $_.Exception.Message }) }
}

Invoke-SampleControl SampleNav_Mermaid
winapp ui wait-for MarkdownRenderer -a $AppPid --value 'Native Mermaid' --contains -t 10000
if ($LASTEXITCODE -ne 0) { throw 'The Mermaid page did not become current.' }
# A fresh sample has an unchecked theme toggle. On -> Dark, off -> Light.
Invoke-SampleControl ThemeToggle
Invoke-SampleControl ThemeToggle
Test-Diagram 'linked-flowchart-light-100' 'Native Mermaid' $ExpectedPrimaryPageDiagramCount 0 'light'
Invoke-SampleControl ThemeToggle
Test-Diagram 'linked-flowchart-dark-100' 'Native Mermaid' $ExpectedPrimaryPageDiagramCount 0 'dark'
Invoke-SampleControl TextScaleToggle
Test-Diagram 'linked-flowchart-dark-200-text' 'Native Mermaid' $ExpectedPrimaryPageDiagramCount 0 'dark'
Invoke-SampleControl ForcedHighContrastToggle
Test-Diagram 'linked-flowchart-forced-high-contrast-200-text' 'Native Mermaid' $ExpectedPrimaryPageDiagramCount 0 'high-contrast'

# Restore the normal sample state before exercising the broader grammar set.
Invoke-SampleControl ForcedHighContrastToggle
Invoke-SampleControl TextScaleToggle
Invoke-SampleControl ThemeToggle
Invoke-SampleControl SampleNav_Diagrams
winapp ui wait-for MarkdownRenderer -a $AppPid --value 'Diagram extension pipeline' --contains -t 10000
if ($LASTEXITCODE -ne 0) { throw 'The Diagram pipeline page did not become current.' }
Test-Diagram 'pipeline-flowchart' 'Diagram extension pipeline' 1 1 'light'
Test-Diagram 'sequence' 'Sequence regression' 1 0 'light' @'
# Sequence regression

```mermaid
sequenceDiagram
    participant Alice
    participant Bob
    Alice->>Bob: Hello Bob
    Bob-->>Alice: Hello Alice
```
'@
Test-Diagram 'pie' 'Pie regression' 1 0 'light' @'
# Pie regression

```mermaid
pie title Delivery
    "Complete" : 60
    "In progress" : 30
    "Planned" : 10
```
'@
Invoke-SampleControl SampleNav_Mermaid
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json')
$results | Format-Table name, status, detail -AutoSize
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
