[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RunnerPath = Join-Path $PSScriptRoot 'Invoke-MarkdownCorrectnessUiTests.ps1'
$script:ImageBoxPath = Join-Path $PSScriptRoot '..\MarkdownRenderer\Layout\Boxes\ImageBox.cs'
$script:LayoutBuilderPath = Join-Path $PSScriptRoot '..\MarkdownRenderer\Layout\LayoutBuilder.cs'
$script:ThemeResolverPath = Join-Path $PSScriptRoot '..\MarkdownRenderer\Theming\ThemeResolver.cs'
$script:ElementStylePath = Join-Path $PSScriptRoot '..\MarkdownRenderer\Theming\ElementStyle.cs'
$script:ThemeSnapshotPath = Join-Path $PSScriptRoot '..\MarkdownRenderer\Theming\ThemeSnapshot.cs'
$script:SampleWindowPath = Join-Path $PSScriptRoot '..\MarkdownRenderer.Sample\MainWindow.xaml.cs'
$script:SampleAppPath = Join-Path $PSScriptRoot '..\MarkdownRenderer.Sample\App.xaml'
$tokens = $null
$parseErrors = $null
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile($script:RunnerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Runner parse failed: $($parseErrors[0].Message)" }

$script:CheckCount = 0
function Assert-Check {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Condition)
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:CheckCount++
    Write-Host "PASS: $Name"
}

function Assert-Throws {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Action)
    $thrown = $false
    try { & $Action | Out-Null } catch { $thrown = $true }
    Assert-Check -Name $Name -Condition $thrown
}

function Get-RunnerFunctionAstText {
    param([Parameter(Mandatory)][string]$Name)
    $functionAst = $runnerAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
    }, $true)
    if ($null -eq $functionAst) { throw "Runner is missing helper '$Name'." }
    return $functionAst.Extent.Text
}

$helperNames = @(
    'Resolve-VectorHandleDestination',
    'Resolve-MarkdownImageArtworkBounds',
    'Resolve-TextHandleDestination',
    'Assert-PointOutsideTextRanges',
    'Get-RangeRectangles'
)
$helperText = @($helperNames | ForEach-Object { Get-RunnerFunctionAstText -Name $_ }) -join [Environment]::NewLine
. ([scriptblock]::Create($helperText))

# Exercise the runner's actual UIA range-array adapter with a deterministic
# probe stub. The returned rectangles must enumerate as individual geometry
# records (including the multi-line case used by C10), not a nested Object[].
function Invoke-GuestProbe {
    param([string]$Mode,[string]$Needle)
    if ($Mode -cne 'range') { throw "Unexpected mocked probe mode '$Mode'." }
    $items = if ($Needle -ceq 'two-line-range') {
        @(
            [pscustomobject]@{x=11;y=17;width=70;height=18},
            [pscustomobject]@{x=13;y=40;width=85;height=18}
        )
    } else {
        @([pscustomobject]@{x=21;y=27;width=35;height=18})
    }
    return [pscustomobject]@{mode='range';text=$Needle;rectangles=$items}
}

function New-Rect {
    param([double]$X,[double]$Y,[double]$Width,[double]$Height)
    return [pscustomobject]@{x=$X;y=$Y;width=$Width;height=$Height}
}

function Test-TextEndpointProjection {
    param([double]$Scale,[string]$Direction,[string]$Endpoint)
    $viewport = New-Rect -X 0 -Y 0 -Width (1200*$Scale) -Height (900*$Scale)
    # Deliberately return UIA fragments out of visual order. The word wraps
    # across two visual lines, with one simple rectangle per line.
    $firstLine = New-Rect -X (120*$Scale) -Y (80*$Scale) -Width (140*$Scale) -Height (20*$Scale)
    $lastLine = New-Rect -X (75*$Scale) -Y (108*$Scale) -Width (220*$Scale) -Height (20*$Scale)
    $offsetX = 24*$Scale
    $offsetY = -18*$Scale
    $resolved = Resolve-TextHandleDestination -TargetRectangles @($lastLine,$firstLine) `
        -Endpoint $Endpoint -FlowDirection $Direction -DpiScale $Scale `
        -FingerToCaretOffsetX $offsetX -FingerToCaretOffsetY $offsetY -ViewportBounds $viewport

    $expectedLine = if ($Endpoint -eq 'Start') { $firstLine } else { $lastLine }
    $expectedRight = if ($Direction -eq 'Ltr') { $Endpoint -eq 'End' } else { $Endpoint -eq 'Start' }
    $expectedCaretX = if ($expectedRight) { $expectedLine.x + $expectedLine.width - $Scale } else { $expectedLine.x + $Scale }
    $expectedCaretY = $expectedLine.y + ($expectedLine.height/2.0)
    $expectedFingerX = [int][Math]::Round($expectedCaretX + $offsetX)
    $expectedFingerY = [int][Math]::Round($expectedCaretY + $offsetY)
    $caretError = [Math]::Max(
        [Math]::Abs([double]$resolved.effectiveCaret.x - $expectedCaretX),
        [Math]::Abs([double]$resolved.effectiveCaret.y - $expectedCaretY)
    )
    return [pscustomobject]@{
        scale=$Scale;direction=$Direction;endpoint=$Endpoint;resolved=$resolved
        expectedCaret=[pscustomobject]@{x=$expectedCaretX;y=$expectedCaretY}
        expectedFinger=[pscustomobject]@{x=$expectedFingerX;y=$expectedFingerY}
        caretError=$caretError;expectedLine=$expectedLine
    }
}

$scales = @(1.0,1.25,1.5,2.0,4.0)
foreach ($scale in $scales) {
    $peer = New-Rect -X (100*$scale) -Y (40*$scale) -Width (500*$scale) -Height (140*$scale)
    $viewport = New-Rect -X 0 -Y 0 -Width (900*$scale) -Height (600*$scale)
    foreach ($direction in @('Ltr','Rtl')) {
        $art = Resolve-MarkdownImageArtworkBounds -PeerBounds $peer -DpiScale $scale `
            -IntrinsicWidthDip 96 -IntrinsicHeightDip 96 `
            -MarginLeftDip 0 -MarginTopDip 0 -MarginRightDip 0 -MarginBottomDip 8 `
            -MinimumParagraphLineHeightDip (14 * 1.4) -ContentAlignment Left -FlowDirection $direction `
            -ViewportBounds $viewport
        $bounds = $art.bounds
        Assert-Check -Name "96×96 source-derived artwork bounds at ${scale}x/$direction" -Condition `
            ([Math]::Abs(([double]$bounds.x/$scale)-100) -lt 0.001 -and
             [Math]::Abs(([double]$bounds.y/$scale)-40) -lt 0.001 -and
             [Math]::Abs(([double]$bounds.width/$scale)-96) -lt 0.001 -and
             [Math]::Abs(([double]$bounds.height/$scale)-96) -lt 0.001)
        $offsetX = 24*$scale
        $offsetY = 22*$scale
        $destination = Resolve-VectorHandleDestination -ObjectX $bounds.x -ObjectY $bounds.y `
            -ObjectWidth $bounds.width -ObjectHeight $bounds.height `
            -FingerToCaretOffsetX $offsetX -FingerToCaretOffsetY $offsetY
        Assert-Check -Name "SVG caret and contact stay inside actual artwork at ${scale}x/$direction" -Condition `
            ($destination.target.x -ge $bounds.x -and $destination.target.x -le ($bounds.x+$bounds.width) -and
             $destination.target.y -ge $bounds.y -and $destination.target.y -le ($bounds.y+$bounds.height) -and
             $destination.effectiveCaret.x -ge $bounds.x -and $destination.effectiveCaret.x -le ($bounds.x+$bounds.width) -and
             $destination.effectiveCaret.y -ge ($bounds.y+$bounds.height/2.0) -and $destination.effectiveCaret.y -le ($bounds.y+$bounds.height))
    }

    foreach ($direction in @('Ltr','Rtl')) {
        foreach ($endpoint in @('Start','End')) {
            $case = Test-TextEndpointProjection -Scale $scale -Direction $direction -Endpoint $endpoint
            Assert-Check -Name "wrapped text endpoint + fingertip offset at ${scale}x/$direction/$endpoint" -Condition `
                ($case.resolved.wrappedLineCount -eq 2 -and
                 [Math]::Abs([double]$case.resolved.target.x - [double]$case.expectedFinger.x) -lt 0.001 -and
                 [Math]::Abs([double]$case.resolved.target.y - [double]$case.expectedFinger.y) -lt 0.001 -and
                 $case.caretError -le 0.5)
        }
    }
}

$centerPeer = New-Rect -X 10 -Y 20 -Width 500 -Height 140
$centerViewport = New-Rect -X 0 -Y 0 -Width 600 -Height 200
$centered = Resolve-MarkdownImageArtworkBounds -PeerBounds $centerPeer -DpiScale 1.25 `
    -IntrinsicWidthDip 96 -IntrinsicHeightDip 96 -MarginLeftDip 0 -MarginTopDip 6 `
    -MarginRightDip 0 -MarginBottomDip 6 -ContentAlignment Center -FlowDirection Rtl `
    -ViewportBounds $centerViewport
Assert-Check -Name 'center alignment remains contained and uses physical peer geometry' -Condition `
    ([Math]::Abs([double]$centered.bounds.x - (10 + ((500/1.25-96)*1.25/2))) -lt 0.001 -and
     [double]$centered.bounds.y -ge [double]$centerPeer.y -and
     ([double]$centered.bounds.x + [double]$centered.bounds.width) -le ([double]$centerPeer.x + [double]$centerPeer.width))

$narrowPeer = New-Rect -X 0 -Y 0 -Width 48 -Height 60
$narrowArt = Resolve-MarkdownImageArtworkBounds -PeerBounds $narrowPeer -DpiScale 1 `
    -IntrinsicWidthDip 96 -IntrinsicHeightDip 96 -MarginLeftDip 4 -MarginTopDip 6 `
    -MarginRightDip 4 -MarginBottomDip 6 -ContentAlignment Left -FlowDirection Ltr
Assert-Check -Name 'narrow layout scales artwork to fit the measured content width without leaving peer bounds' -Condition `
    ([double]$narrowArt.bounds.width -eq 40 -and [double]$narrowArt.bounds.height -eq 40 -and
     [double]$narrowArt.bounds.x -ge [double]$narrowPeer.x -and
     ([double]$narrowArt.bounds.x+[double]$narrowArt.bounds.width) -le ([double]$narrowPeer.x+[double]$narrowPeer.width) -and
     ([double]$narrowArt.bounds.y+[double]$narrowArt.bounds.height) -le ([double]$narrowPeer.y+[double]$narrowPeer.height))

$blankViewport = New-Rect -X 0 -Y 0 -Width 400 -Height 300
$blankRange = New-Rect -X 20 -Y 40 -Width 100 -Height 18
$blankProof = Assert-PointOutsideTextRanges -X 200 -Y 150 -Rectangles @($blankRange) -ViewportBounds $blankViewport
Assert-Check -Name 'blank contact is inside viewport and outside text footprint' -Condition ($blankProof.insideRenderer -and $blankProof.outsideAuthoredText)
Assert-Throws -Name 'blank-area helper rejects contact overlapping a text rectangle' -Action {
    Assert-PointOutsideTextRanges -X 45 -Y 48 -Rectangles @($blankRange) -ViewportBounds $blankViewport
}
Assert-Throws -Name 'blank-area helper rejects a destination outside its renderer' -Action {
    Assert-PointOutsideTextRanges -X 401 -Y 150 -Rectangles @($blankRange) -ViewportBounds $blankViewport
}
foreach ($testCase in @(
    [pscustomobject]@{needle='two-line-range';expected=2},
    [pscustomobject]@{needle='one-line-range';expected=1}
)) {
    $actual = @(Get-RangeRectangles -Text $testCase.needle)
    Assert-Check -Name "UIA range adapter enumerates $($testCase.expected) rectangle(s) independently" -Condition `
        ($actual.Count -eq $testCase.expected -and
         @($actual | Where-Object { $_ -is [System.Array] }).Count -eq 0 -and
         ($testCase.expected -ne 2 -or ([double]$actual[1].x -eq 13 -and [double]$actual[1].y -eq 40)))
}

$targetRect = New-Rect -X 100 -Y 80 -Width 100 -Height 20
$smallViewport = New-Rect -X 0 -Y 0 -Width 400 -Height 300
Assert-Throws -Name 'text-target helper rejects an empty wrapped range' -Action {
    Resolve-TextHandleDestination -TargetRectangles @() -Endpoint End -FlowDirection Ltr -DpiScale 1 `
        -FingerToCaretOffsetX 0 -FingerToCaretOffsetY 0 -ViewportBounds $smallViewport
}
Assert-Throws -Name 'text-target helper rejects nonpositive scale' -Action {
    Resolve-TextHandleDestination -TargetRectangles @($targetRect) -Endpoint End -FlowDirection Ltr -DpiScale 0 `
        -FingerToCaretOffsetX 0 -FingerToCaretOffsetY 0 -ViewportBounds $smallViewport
}
Assert-Throws -Name 'text-target helper rejects a contact displaced beyond viewport' -Action {
    Resolve-TextHandleDestination -TargetRectangles @($targetRect) -Endpoint End -FlowDirection Ltr -DpiScale 1 `
        -FingerToCaretOffsetX 500 -FingerToCaretOffsetY 0 -ViewportBounds $smallViewport
}
Assert-Throws -Name 'text-target helper rejects a line narrower than its DPI-scaled caret inset' -Action {
    Resolve-TextHandleDestination -TargetRectangles @(New-Rect -X 10 -Y 10 -Width 1 -Height 10) `
        -Endpoint End -FlowDirection Ltr -DpiScale 2 -FingerToCaretOffsetX 0 -FingerToCaretOffsetY 0 -ViewportBounds $smallViewport
}
Assert-Throws -Name 'image helper rejects invalid intrinsic geometry' -Action {
    Resolve-MarkdownImageArtworkBounds -PeerBounds $centerPeer -DpiScale 1 -IntrinsicWidthDip 0 -IntrinsicHeightDip 96
}
Assert-Throws -Name 'image helper rejects artwork not wholly visible in renderer viewport' -Action {
    Resolve-MarkdownImageArtworkBounds -PeerBounds (New-Rect -X 0 -Y 0 -Width 200 -Height 120) -DpiScale 1 `
        -IntrinsicWidthDip 96 -IntrinsicHeightDip 96 -MarginTopDip 6 -MarginBottomDip 6 `
        -ViewportBounds (New-Rect -X 0 -Y 0 -Width 90 -Height 120)
}

# Source guards are explicitly static wiring checks: they prove the harness's
# placement constants still match ImageBox's current source contract, not that
# a live WinUI visual was rendered or touch-tested.
$imageBoxSource = [System.IO.File]::ReadAllText($script:ImageBoxPath)
$layoutBuilderSource = [System.IO.File]::ReadAllText($script:LayoutBuilderPath)
$themeResolverSource = [System.IO.File]::ReadAllText($script:ThemeResolverPath)
$elementStyleSource = [System.IO.File]::ReadAllText($script:ElementStylePath)
$themeSnapshotSource = [System.IO.File]::ReadAllText($script:ThemeSnapshotPath)
$sampleWindowSource = [System.IO.File]::ReadAllText($script:SampleWindowPath)
$sampleAppSource = [System.IO.File]::ReadAllText($script:SampleAppPath)
$promotionStart = $layoutBuilderSource.IndexOf('private BlockBox BuildParagraphOrImage(')
$promotionEnd = if ($promotionStart -ge 0) {
    $layoutBuilderSource.IndexOf('private InlineContainerBox BuildParagraph(', $promotionStart + 1)
} else { -1 }
if ($promotionStart -lt 0 -or $promotionEnd -le $promotionStart) {
    throw 'Could not isolate LayoutBuilder.BuildParagraphOrImage source guard.'
}
$promotionSource = $layoutBuilderSource.Substring($promotionStart, $promotionEnd - $promotionStart)
$sampleCreateStart = $sampleWindowSource.IndexOf('private MarkdownRendererControl CreateRenderer(')
$sampleCreateEnd = if ($sampleCreateStart -ge 0) {
    $sampleWindowSource.IndexOf('private void AttachRendererToHost(', $sampleCreateStart + 1)
} else { -1 }
if ($sampleCreateStart -lt 0 -or $sampleCreateEnd -le $sampleCreateStart) {
    throw 'Could not isolate the sample CreateRenderer source guard.'
}
$sampleCreateSource = $sampleWindowSource.Substring($sampleCreateStart, $sampleCreateEnd - $sampleCreateStart)
$textScaleStart = $sampleWindowSource.IndexOf('private void ApplyTextScale(float scale)')
$textScaleEnd = if ($textScaleStart -ge 0) {
    $sampleWindowSource.IndexOf('private void SetTheme(', $textScaleStart + 1)
} else { -1 }
if ($textScaleStart -lt 0 -or $textScaleEnd -le $textScaleStart) {
    throw 'Could not isolate the sample ApplyTextScale source guard.'
}
$textScaleSource = $sampleWindowSource.Substring($textScaleStart, $textScaleEnd - $textScaleStart)
$bodyDefaultPattern = 'MarkdownElementKeys\.Body\s*=>\s*new ElementStyle\s*\{[^}]*FontSize\s*=\s*14[^}]*Margin\s*=\s*new Thickness\(0,\s*0,\s*0,\s*8\)'
Assert-Check -Name 'source guard: promoted image replaces ImageBox defaults with effective Body paragraph margin and line height' -Condition `
    ($promotionSource.Contains('MarkdownElementKeys.Body') -and
     $promotionSource.Contains('Margin = paragraphStyle.Margin') -and
     $promotionSource.Contains('MinimumParagraphLineHeight = paragraphStyle.FontSize * paragraphStyle.LineHeightMultiplier') -and
     $imageBoxSource.Contains('ContentAlignment { get; set; } = CanvasHorizontalAlignment.Left;'))
Assert-Check -Name 'source guard: controlled sample Body defaults match the artwork placement inputs' -Condition `
    ([regex]::IsMatch($themeResolverSource, $bodyDefaultPattern) -and
     $elementStyleSource.Contains('public float LineHeightMultiplier { get; init; } = 1.4f;') -and
     $themeSnapshotSource.Contains('Margin = ov.Margin ?? defaults.Margin') -and
     $themeSnapshotSource.Contains('LineHeightMultiplier = ov.LineHeightMultiplier ?? defaults.LineHeightMultiplier') -and
     $sampleCreateSource.Contains('renderer.Theme = new MarkdownTheme();') -and
     $textScaleSource.Contains('[MarkdownElementKeys.Body] = 14,') -and
     $textScaleSource.Contains('new ElementStyleOverride { FontSize = baseSize * scale }') -and
     -not $textScaleSource.Contains('Margin =') -and
     -not $textScaleSource.Contains('LineHeightMultiplier =') -and
     -not $sampleCreateSource.Contains('StyleSheet') -and
     -not $sampleAppSource.Contains('MarkdownRenderer.Body.Margin') -and
     -not $sampleAppSource.Contains('MarkdownRenderer.Body.LineHeightMultiplier'))
Assert-Check -Name 'source guard: automation destination and paint share ImageBox content placement' -Condition `
    ($imageBoxSource.Contains('new Rect(GetContentX(), GetContentY(), _imageWidth, _imageHeight)') -and
     $imageBoxSource.Contains('var imageRect = new Rect(x, y, _imageWidth, _imageHeight);') -and
     $imageBoxSource.Contains('float x = GetContentX();') -and $imageBoxSource.Contains('float y = GetContentY();'))

$runCases = $runnerAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Run-CorrectnessCases'
}, $true)
if ($null -eq $runCases) { throw 'Runner is missing Run-CorrectnessCases.' }
$c10 = $runCases.Find({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Test-Case' -and
    $node.Extent.Text.Contains("-Name 'touch-double-tap-handle-drag-context-copy-and-cancel'")
}, $true)
if ($null -eq $c10) { throw 'Runner is missing the exact C10 touch-handle case.' }
$c10Text = $c10.Extent.Text
Assert-Check -Name 'C10 preserves its original short fixture and blank destination, with a separately asserted no-collapse stage' -Condition `
    ($c10Text.Contains("Hold or double-tap touchtarget here.") -and
     $c10Text.Contains("A second line continues the selection across formatting.") -and
     $c10Text.Contains('[Math]::Min($renderer[2]-12,500)') -and
     $c10Text.Contains('[Math]::Min($renderer[3]-12,220)') -and
     $c10Text.Contains("Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled `$false") -and
     $c10Text.Contains('Assert-PointOutsideTextRanges') -and
     $c10Text.Contains("`$blankAfter.StartsWith('touchtarget',[StringComparison]::Ordinal)") -and
     $c10Text.Contains('$blankSelection.rangeCount') -and $c10Text.Contains('$blankSelection.text -ceq $blankAfter') -and
     $c10Text.Contains('$hasBlankRange'))
Assert-Check -Name 'C10 uses an authored hard break, measured real endpoint, exact expected selection, and both viewport modes' -Condition `
    ($c10Text.Contains('touchstarttarget here.  `nA hard break starts **this bold line continues to measuredextension**') -and
     $c10Text.Contains('Resolve-TextHandleDestination') -and
     $c10Text.Contains("foreach (`$viewportMode in @('owned','ancestor'))") -and
     $c10Text.Contains('$extended -cne $expectedExtended'))
$c14 = $runCases.Find({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Test-Case' -and
    $node.Extent.Text.Contains("-Name 'layout-resize-and-selection-state-survive-relayout'")
}, $true)
if ($null -eq $c14) { throw 'The existing C14 resize case is missing.' }
Assert-Check -Name 'existing C14 resize case identity and exact required word remain present' -Condition `
    ($c14.Extent.Text.Contains("-Id 'C14'") -and $c14.Extent.Text.Contains('Get-SelectedText') -and $c14.Extent.Text.Contains("'hold'"))

$vectorFunction = $runnerAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-FinalVectorObjectSelection'
}, $true)
if ($null -eq $vectorFunction) { throw 'Runner is missing Test-FinalVectorObjectSelection.' }
$vectorText = $vectorFunction.Extent.Text
Assert-Check -Name 'vector targeting normalizes direction, theme, contrast, and text scale before deriving fixture geometry' -Condition `
    ($vectorText.Contains("Set-ToggleState -AutomationId 'RtlToggle' -Enabled `$false") -and
     $vectorText.Contains("Set-ToggleState -AutomationId 'ThemeToggle' -Enabled `$false") -and
     $vectorText.Contains("Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled `$false") -and
     $vectorText.Contains("Set-ToggleState -AutomationId 'TextScaleToggle' -Enabled `$false") -and
     $vectorText.Contains("Wait-ThemeState -Theme 'light' -Contrast 'hc:off' -TextScale '1'"))

Write-Host "Selection target helper checks passed: $script:CheckCount (geometry behavior plus explicitly labeled static source guards)."
