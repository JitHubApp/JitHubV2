param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

# Launch the sample with project-mode winapp run first, setting
# MARKDOWN_RENDERER_MATH_EVIDENCE to EvidencePath. This script attaches only.
$ErrorActionPreference = 'Stop'
$ExpectedFormulaCount = 27
$ExpectedDisplayFormulaCount = 7
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$lastEvidenceWriteUtc = if (Test-Path -LiteralPath $EvidencePath) {
    (Get-Item -LiteralPath $EvidencePath).LastWriteTimeUtc
} else {
    [datetime]::MinValue
}

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

function Wait-MathRenderEvidence {
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
        if (Test-Path -LiteralPath $EvidencePath) {
            $writeUtc = (Get-Item -LiteralPath $EvidencePath).LastWriteTimeUtc
            if ($writeUtc -gt $script:lastEvidenceWriteUtc) {
                $script:lastEvidenceWriteUtc = $writeUtc
                return
            }
        }
        Start-Sleep -Milliseconds 50
    } while ([datetime]::UtcNow -lt $deadline)
    throw 'The renderer did not publish fresh math render evidence.'
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

function Test-MathState([string]$State, [string]$ExpectedTheme) {
    try {
        winapp ui wait-for MarkdownRenderer -a $AppPid --value 'Native mathematics' --contains -t 10000
        if ($LASTEXITCODE -ne 0) { throw 'The math sample did not render.' }
        Wait-MathRenderEvidence
        Test-DisplayToggleState $ExpectedTheme
        $treeJson = winapp ui inspect MarkdownRenderer -a $AppPid --depth 8 --json
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the renderer.' }
        $tree = $treeJson | ConvertFrom-Json
        $elements = @(Expand-Elements $tree.windows.elements)
        $math = @($elements | Where-Object className -eq 'MarkdownMath')
        if ($math.Count -ne $ExpectedFormulaCount) {
            throw "Expected $ExpectedFormulaCount native formulas, got $($math.Count)."
        }
        if (@($math | Where-Object { $_.width -le 0 -or $_.height -le 0 }).Count) {
            throw 'A formula has no rendered bounds.'
        }
        if (@($elements | Where-Object className -eq 'MarkdownCodeBlock').Count) {
            throw 'The valid display sum fell back to a code block.'
        }
        $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
        if ($evidence.processId -ne $AppPid) { throw 'Stale evidence from another process.' }
        $nativeDisplays = @($evidence.displayFormulas | Where-Object {
            @($_.kinds).Count -eq 1 -and $_.kinds[0] -eq 'VectorScene'
        })
        if ($evidence.displayFormulas.Count -ne $ExpectedDisplayFormulaCount -or
            $nativeDisplays.Count -ne $ExpectedDisplayFormulaCount) {
            throw "Expected $ExpectedDisplayFormulaCount native display formulas."
        }
        if ($evidence.diagnostics.Count -ne 0) {
            throw 'The primary Math page must render without diagnostics.'
        }
        winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory "$State.png") --json
        if ($LASTEXITCODE -ne 0) { throw 'Screenshot capture failed.' }
        $results.Add([pscustomobject]@{ name = $State; status = 'PASS' })
    }
    catch { $results.Add([pscustomobject]@{ name = $State; status = 'FAIL'; detail = $_.Exception.Message }) }
}

Invoke-SampleControl SampleNav_Math
winapp ui wait-for MarkdownRenderer -a $AppPid --value 'Native mathematics' --contains -t 10000
if ($LASTEXITCODE -ne 0) { throw 'The Math page did not become current.' }
# A fresh sample has an unchecked theme toggle. On -> Dark, off -> Light.
Invoke-SampleControl ThemeToggle
Invoke-SampleControl ThemeToggle
Test-MathState 'light-100' 'light'
Invoke-SampleControl ThemeToggle
Test-MathState 'dark-100' 'dark'
Invoke-SampleControl TextScaleToggle
Test-MathState 'dark-200-text' 'dark'
Invoke-SampleControl ForcedHighContrastToggle
Test-MathState 'forced-high-contrast-200-text' 'high-contrast'

$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json')
$results | Format-Table name, status, detail -AutoSize
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
