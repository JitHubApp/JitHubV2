# Phase 2 live feature-matrix cases (docs/markdown-renderer/evidence/phase2-matrix.json).
#
# Dot-sourced by Invoke-MarkdownCorrectnessUiTests.ps1 -Phase2Only after the
# C00 Sandbox/app identity case. Every case uses the runner's existing guest
# probe, UIA and screenshot helpers against the one hashed Sample payload of
# this run. A row passes only when every one of its cases passes; a check this
# batch cannot perform is recorded UNVERIFIED with its reason, never PASS.

$script:P2ViewportModes = @('owned', 'ancestor')

# The Sample shows its viewport-ownership toggle only on the TouchSelection
# page and resets it to the owned viewport on every other page. Owned mode
# uses the feature page itself; ancestor mode renders the same source (the
# page's own document, or the case fixture) through the editor on the
# TouchSelection page with the ancestor-viewport renderer.
function Enter-P2Mode {
    param(
        [Parameter(Mandatory)][ValidateSet('owned', 'ancestor')][string]$Mode,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$RequiredText,
        [string]$Markdown = '',
        [string]$MarkdownRequiredText = ''
    )
    if ($Mode -eq 'owned') {
        Navigate-Sample -Key $Key -RequiredText $RequiredText
        if ($Markdown) { Set-TemporaryMarkdown -Markdown $Markdown -RequiredText $MarkdownRequiredText }
        return
    }
    $source = $Markdown
    $required = $MarkdownRequiredText
    if (-not $source) {
        Navigate-Sample -Key $Key -RequiredText $RequiredText
        $source = [string](Invoke-UiValue 'MarkdownEditor')
        $required = $RequiredText
    }
    Navigate-Sample -Key 'TouchSelection' -RequiredText 'Touch selection'
    Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled $true
    Start-Sleep -Milliseconds 300
    Set-TemporaryMarkdown -Markdown $source -RequiredText $required
}

function Exit-P2Mode {
    # Leaving TouchSelection resets the Sample to the owned viewport.
    Navigate-Sample -Key 'Typography' -RequiredText 'The quick brown fox'
}

function Reset-P2Display {
    Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $false
    Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled $false
    Set-ToggleState -AutomationId 'TextScaleToggle' -Enabled $false
    Wait-ThemeState -Theme 'light' -Contrast 'hc:off' -TextScale '1'
}

function Clear-P2Selection {
    $menu = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy', 'MarkdownContextCopyMarkdown', 'MarkdownContextSelectAll') })
    if ($menu.Count -gt 0) {
        $null = Invoke-Ui @('send-keys', 'esc', '--via', 'send-input')
        $null = Invoke-Ui @('wait-for', 'MarkdownContextCopy', '--gone', '-t', '2000')
    }
    $null = Invoke-Ui @('focus', 'MarkdownRenderer')
    $null = Invoke-Ui @('send-keys', 'esc', '--via', 'send-input')
}

function Get-P2Text {
    return [string](Invoke-UiValue 'MarkdownRenderer')
}

function Get-P2Occurrences {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Needle)
    $count = 0
    $index = 0
    while (($index = $Text.IndexOf($Needle, $index, [System.StringComparison]::Ordinal)) -ge 0) {
        $count++
        $index += $Needle.Length
    }
    return $count
}

function Assert-P2InOrder {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string[]]$Phrases)
    $position = -1
    foreach ($phrase in $Phrases) {
        $next = $Text.IndexOf($phrase, $position + 1, [System.StringComparison]::Ordinal)
        if ($next -lt 0) { throw "Expected '$phrase' after position $position in the rendered UIA text." }
        $position = $next
    }
}

function Select-P2Range {
    param([Parameter(Mandatory)][string]$StartNeedle, [Parameter(Mandatory)][string]$EndNeedle)
    $range = Invoke-GuestProbe -Mode 'range-between' -StartNeedle $StartNeedle -EndNeedle $EndNeedle -SelectRange
    if (-not [bool]$range.selectionRequested) { throw "The TextPattern range from '$StartNeedle' to '$EndNeedle' was not selected." }
    if (@($range.rectangles).Count -eq 0) { throw "The selected range from '$StartNeedle' to '$EndNeedle' has no UIA geometry." }
    return $range
}

function Copy-P2PlainSelection {
    $null = Invoke-Ui @('send-keys', 'ctrl+c', '--via', 'send-input')
    Start-Sleep -Milliseconds 150
    return Invoke-GuestProbe -Mode 'clipboard'
}

function Copy-P2MarkdownSelection {
    param([Parameter(Mandatory)]$Range)
    $rect = $Range.rectangles[0]
    $point = '{0},{1}' -f [int][Math]::Floor($rect.x + ($rect.width / 2.0)), [int][Math]::Floor($rect.y + ($rect.height / 2.0))
    $null = Invoke-Ui @('drag', $point, $point, '--right', '--hold-ms', '40')
    $null = Invoke-Ui @('wait-for', 'MarkdownContextCopyMarkdown', '-t', '3000')
    $null = Invoke-Ui @('invoke', 'MarkdownContextCopyMarkdown')
    Start-Sleep -Milliseconds 150
    return Invoke-GuestProbe -Mode 'clipboard'
}

function Get-P2Elements {
    param([string]$ClassName = '', [string]$Type = '')
    $elements = @(Get-UiElements)
    if ($ClassName) { $elements = @($elements | Where-Object { [string]$_.className -ceq $ClassName }) }
    if ($Type) { $elements = @($elements | Where-Object { [string]$_.type -ceq $Type }) }
    return $elements
}

function Scroll-P2Text {
    param([Parameter(Mandatory)][string]$Needle)
    $range = Invoke-GuestProbe -Mode 'scroll-range' -Needle $Needle
    if ([string]$range.text -cne $Needle) { throw "TextPattern did not locate '$Needle' to scroll it into view." }
    return $range
}

function Scroll-P2Bottom {
    $null = Invoke-Ui @('scroll', 'MarkdownRenderer', '--to', 'bottom')
    Start-Sleep -Milliseconds 300
}

# Screenshot pixels. Save-Screenshot captures the app window; UIA bounds are
# physical screen pixels. Map a screen rectangle into the screenshot through
# the window rectangle reported by the guest at the same moment.
function Get-P2PixelSampler {
    param([Parameter(Mandatory)]$Shot)
    Add-Type -AssemblyName System.Drawing
    $meta = Invoke-GuestProbe -Mode 'metadata'
    $windowWidth = [double]($meta.window.right - $meta.window.left)
    $windowHeight = [double]($meta.window.bottom - $meta.window.top)
    $bitmap = [System.Drawing.Bitmap]::new([string]$Shot.path)
    if ($windowWidth -le 0 -or $windowHeight -le 0) { $bitmap.Dispose(); throw 'The guest reported an empty window rectangle for pixel mapping.' }
    return [pscustomobject]@{
        bitmap = $bitmap
        left = [double]$meta.window.left
        top = [double]$meta.window.top
        scaleX = $bitmap.Width / $windowWidth
        scaleY = $bitmap.Height / $windowHeight
    }
}

function Get-P2Pixel {
    param([Parameter(Mandatory)]$Sampler, [Parameter(Mandatory)][double]$ScreenX, [Parameter(Mandatory)][double]$ScreenY)
    $x = [int][Math]::Round(($ScreenX - $Sampler.left) * $Sampler.scaleX)
    $y = [int][Math]::Round(($ScreenY - $Sampler.top) * $Sampler.scaleY)
    if ($x -lt 0 -or $y -lt 0 -or $x -ge $Sampler.bitmap.Width -or $y -ge $Sampler.bitmap.Height) {
        throw "Screen point ($ScreenX,$ScreenY) maps outside the window screenshot."
    }
    $color = $Sampler.bitmap.GetPixel($x, $y)
    return [pscustomobject]@{ r = [int]$color.R; g = [int]$color.G; b = [int]$color.B }
}

function Get-P2RectPixel {
    param([Parameter(Mandatory)]$Sampler, [Parameter(Mandatory)]$Rect, [Parameter(Mandatory)][double]$FractionX, [Parameter(Mandatory)][double]$FractionY)
    return Get-P2Pixel -Sampler $Sampler -ScreenX ([double]$Rect.x + [double]$Rect.width * $FractionX) -ScreenY ([double]$Rect.y + [double]$Rect.height * $FractionY)
}

function Test-P2ColorNear {
    param([Parameter(Mandatory)]$Actual, [Parameter(Mandatory)][int[]]$Expected, [int]$Tolerance = 48)
    return ([Math]::Abs($Actual.r - $Expected[0]) -le $Tolerance -and
        [Math]::Abs($Actual.g - $Expected[1]) -le $Tolerance -and
        [Math]::Abs($Actual.b - $Expected[2]) -le $Tolerance)
}

function Get-P2DistinctColorCount {
    param([Parameter(Mandatory)]$Sampler, [Parameter(Mandatory)]$Rect, [int]$Columns = 40, [int]$Rows = 12, [int]$Quantize = 32)
    $colors = [System.Collections.Generic.HashSet[string]]::new()
    for ($row = 0; $row -lt $Rows; $row++) {
        for ($column = 0; $column -lt $Columns; $column++) {
            $pixel = Get-P2RectPixel -Sampler $Sampler -Rect $Rect -FractionX (($column + 0.5) / $Columns) -FractionY (($row + 0.5) / $Rows)
            $null = $colors.Add(('{0},{1},{2}' -f [int]($pixel.r / $Quantize), [int]($pixel.g / $Quantize), [int]($pixel.b / $Quantize)))
        }
    }
    return $colors.Count
}

# Highlighting shows as saturated token colors. Count every screenshot pixel
# inside a screen rectangle whose channel spread is at least MinimumSpread;
# plain code (gray text on a gray block) has none.
function Get-P2ChromaticPixelCount {
    param([Parameter(Mandatory)]$Sampler, [Parameter(Mandatory)]$Rect, [int]$MinimumSpread = 60)
    if (-not ('P2PixelCounter' -as [type])) {
        Add-Type -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, System.Drawing.Common -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class P2PixelCounter
{
    public static int CountChromatic(Bitmap bitmap, Rectangle area, int minimumSpread)
    {
        area.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (area.Width <= 0 || area.Height <= 0) return 0;
        BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[area.Width * 4];
            int count = 0;
            for (int y = 0; y < area.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < area.Width; x++)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) >= minimumSpread) count++;
                }
            }
            return count;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
'@
    }
    $left = [int][Math]::Round(([double]$Rect.x - $Sampler.left) * $Sampler.scaleX)
    $top = [int][Math]::Round(([double]$Rect.y - $Sampler.top) * $Sampler.scaleY)
    $width = [int][Math]::Round([double]$Rect.width * $Sampler.scaleX)
    $height = [int][Math]::Round([double]$Rect.height * $Sampler.scaleY)
    return [P2PixelCounter]::CountChromatic($Sampler.bitmap, [System.Drawing.Rectangle]::new($left, $top, $width, $height), $MinimumSpread)
}

# A short window (432 DIP tall at 200% in the Sandbox) can leave a realized
# element below the visible area. Wheel down over the renderer, which reaches
# the owned or ancestor scroller, until the element's center is in the window.
function Show-P2Element {
    param([Parameter(Mandatory)][scriptblock]$Find, [Parameter(Mandatory)][string]$Description, [int]$MaxWheelSteps = 12)
    $meta = Invoke-GuestProbe -Mode 'metadata'
    for ($step = 0; $step -le $MaxWheelSteps; $step++) {
        $element = & $Find
        if ($null -ne $element -and -not [bool]$element.isOffscreen -and [double]$element.width -gt 0 -and [double]$element.height -gt 0) {
            $centerY = [double]$element.y + [double]$element.height / 2.0
            if ($centerY -gt [double]$meta.window.top -and $centerY -lt [double]$meta.window.bottom) { return $element }
        }
        if ($step -lt $MaxWheelSteps) {
            $null = Invoke-Ui @('scroll', 'MarkdownRenderer', '--wheel', '-1')
            Start-Sleep -Milliseconds 250
        }
    }
    throw "$Description never became visible inside the window."
}

# Clip a screen rectangle to the renderer's visible area so pixel sampling
# never reads outside the captured window.
function Get-P2VisibleRect {
    param([Parameter(Mandatory)]$Rect, [Parameter(Mandatory)]$Sampler, [double]$MinimumHeight = 40)
    $renderer = Get-UiBounds 'MarkdownRenderer'
    $windowRight = $Sampler.left + $Sampler.bitmap.Width / $Sampler.scaleX
    $windowBottom = $Sampler.top + $Sampler.bitmap.Height / $Sampler.scaleY
    $left = [Math]::Max([Math]::Max([double]$Rect.x, $renderer[0]), $Sampler.left)
    $top = [Math]::Max([Math]::Max([double]$Rect.y, $renderer[1]), $Sampler.top)
    $right = [Math]::Min([Math]::Min([double]$Rect.x + [double]$Rect.width, $renderer[0] + $renderer[2]), $windowRight - 1)
    $bottom = [Math]::Min([Math]::Min([double]$Rect.y + [double]$Rect.height, $renderer[1] + $renderer[3]), $windowBottom - 1)
    if ($right - $left -le 0 -or $bottom - $top -lt $MinimumHeight) {
        throw "Only $([Math]::Max(0, $bottom - $top)) px of the rectangle is visible; at least $MinimumHeight px are needed to sample it."
    }
    return [pscustomobject]@{ x = $left; y = $top; width = $right - $left; height = $bottom - $top }
}

function Get-P2ImagePeer {
    param([Parameter(Mandatory)][string]$AltText)
    $null = Scroll-P2Text -Needle $AltText
    $state = Wait-ForImagePeerState -AltText $AltText -TimeoutMilliseconds 8000
    $images = @($state.images)
    if ($images.Count -ne 1) { throw "Expected exactly one image peer for '$AltText', found $($images.Count)." }
    return $images[0]
}

function Assert-P2LoadedImage {
    param([Parameter(Mandatory)]$Peer, [Parameter(Mandatory)][string]$AltText)
    if ([string]$Peer.name -cne $AltText) { throw "Image '$AltText' did not finish loaded; name='$($Peer.name)' helpText='$($Peer.helpText)'." }
    if ([bool]$Peer.offscreen -or [double]$Peer.width -le 0 -or [double]$Peer.height -le 0) { throw "Image '$AltText' has no visible geometry." }
}

function Run-Phase2Cases {
    Reset-P2Display

    # ── HTML ─────────────────────────────────────────────────────────────────
    Test-Case -Id 'P2-HTML-01' -Name 'html-sample-ordered-text-styles-and-source-positions' -Requirement 'The Html sample exposes its supported inline/block text in authored order, styled inline text is visible in the screenshot, and a selection inside supported markup copies the exact source slice from its UTF-16 source position.' -Body {
        Navigate-Sample -Key 'Html' -RequiredText 'Native safe HTML'
        $text = Get-P2Text
        Assert-P2InOrder -Text $text -Phrases @('Semantic block and inline markup', 'strong text', 'emphasis', 'marked text', 'inline code', 'A native blockquote remains selectable', 'Lists retain document semantics', 'safe HTML example', 'Native disclosure with ExpandCollapse semantics', 'HTML disclosure body', 'Native HTML table', 'Capability', 'Inert literal source', 'A data-URI image still goes through the host image pipeline', 'Security boundary')
        $source = Invoke-UiValue 'MarkdownEditor'
        Clear-P2Selection
        $range = Select-P2Range -StartNeedle 'strong text' -EndNeedle 'marked text'
        $markdown = Copy-P2MarkdownSelection -Range $range
        $copied = [string]$markdown.plain
        if (-not $copied.Contains('strong text') -or -not $copied.Contains('marked text')) { throw "Copy as Markdown of the HTML selection returned '$copied'." }
        $sourceIndex = $source.IndexOf($copied, [System.StringComparison]::Ordinal)
        $paragraphIndex = $source.IndexOf('<p>HTML supports', [System.StringComparison]::Ordinal)
        if ($sourceIndex -lt 0) { throw "The copied Markdown '$copied' is not an exact slice of the Html sample source." }
        if ($sourceIndex -lt $paragraphIndex -or $sourceIndex -gt ($paragraphIndex + 400)) { throw 'The copied Markdown slice does not come from the selected paragraph source position.' }
        Clear-P2Selection
        return [pscustomobject]@{ orderedPhrases = 15; copiedMarkdown = $copied; sourceIndex = $sourceIndex; screenshot = (Save-Screenshot 'screenshots\p2-html-01.png') }
    }

    Test-Case -Id 'P2-HTML-02' -Name 'html-script-sentinel-inert-once-and-frames-forms-absent' -Requirement 'The standalone script sentinel appears exactly once as inert literal text; the frame and form sentinels are absent from the accessibility text.' -Body {
        Navigate-Sample -Key 'Html' -RequiredText 'Native safe HTML'
        $null = Scroll-P2Text -Needle 'Security boundary'
        $text = Get-P2Text
        $script = Get-P2Occurrences -Text $text -Needle 'unsafe-script-sentinel'
        if ($script -ne 1) { throw "The script sentinel appeared $script times; expected exactly once." }
        foreach ($absent in @('unsafe-frame-sentinel', 'unsafe-form-sentinel')) {
            if ($text.Contains($absent)) { throw "The suppressed '$absent' is present in the accessibility text." }
        }
        if (-not $text.Contains('Unknown element content remains visible as literal source.')) { throw 'The unknown element content is not visible as literal text.' }
        return [pscustomobject]@{ scriptSentinelCount = $script; framesAndFormsAbsent = $true; screenshot = (Save-Screenshot 'screenshots\p2-html-02.png') }
    }

    Test-Case -Id 'P2-HTML-03' -Name 'html-focus-roles-selection-and-copy-in-both-viewports' -Requirement 'In both viewport modes the Html sample exposes its link, disclosure and table roles, keyboard focus reaches the link, and a selection copies rendered plain text with CF_HTML and exact Markdown source.' -Body {
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'Html' -RequiredText 'Native safe HTML'
                $elements = Get-P2Elements
                $links = @($elements | Where-Object { [string]$_.type -ceq 'Hyperlink' -and [string]$_.name -ceq 'safe HTML example' })
                if ($links.Count -ne 1) { throw "[$mode] Expected one 'safe HTML example' Hyperlink, found $($links.Count)." }
                $disclosure = @($elements | Where-Object { [string]$_.className -ceq 'MarkdownDisclosure' })
                if ($disclosure.Count -lt 1) { throw "[$mode] The HTML disclosure has no MarkdownDisclosure peer." }
                $tables = @($elements | Where-Object { [string]$_.type -ceq 'Table' })
                if ($tables.Count -lt 1) { throw "[$mode] The native HTML table has no Table peer." }

                Clear-P2Selection
                $linkId = Get-UiAutomationId $links[0]
                if ([string]::IsNullOrWhiteSpace($linkId)) { throw "[$mode] The HTML link has no AutomationId for a focus probe." }
                $focusedLink = $false
                for ($tab = 0; $tab -lt 12 -and -not $focusedLink; $tab++) {
                    $null = Invoke-Ui @('send-keys', 'tab', '--via', 'send-input')
                    Start-Sleep -Milliseconds 80
                    $focus = Invoke-GuestProbe -Mode 'focus-state' -AutomationId $linkId
                    $focusedLink = [bool]$focus.hasKeyboardFocus
                }
                if (-not $focusedLink) { throw "[$mode] Keyboard Tab never focused the 'safe HTML example' link." }

                Clear-P2Selection
                $range = Select-P2Range -StartNeedle 'strong text' -EndNeedle 'marked text'
                $selected = Get-SelectedText
                $plain = Copy-P2PlainSelection
                if ([string]$plain.plain -cne $selected) { throw "[$mode] Ctrl+C plain text '$($plain.plain)' differs from the selected rendered text '$selected'." }
                if (-not [bool]$plain.hasHtml -or -not ([string]$plain.html).Contains('strong text')) { throw "[$mode] Ctrl+C did not place a CF_HTML fragment with the selected content." }
                $markdown = Copy-P2MarkdownSelection -Range $range
                $source = Invoke-UiValue 'MarkdownEditor'
                if (-not $source.Contains([string]$markdown.plain) -or -not ([string]$markdown.plain).Contains('strong text')) { throw "[$mode] Copy as Markdown returned '$($markdown.plain)', not an exact source slice." }
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ viewport = $mode; link = $links[0].name; disclosures = $disclosure.Count; tables = $tables.Count; selected = $selected; markdown = [string]$markdown.plain; screenshot = (Save-Screenshot "screenshots\p2-html-03-$mode.png") })
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P2-HTML-04' -Name 'html-document-replacement-publishes-no-stale-content' -Requirement 'In both viewport modes, replacing the Html sample with another HTML document and then returning shows only the current document content, and replacing the engine of a live HTML document re-renders only the current document.' -Body {
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'Html' -RequiredText 'Native safe HTML'
                $htmlSource = [string](Invoke-UiValue 'MarkdownEditor')
                Set-TemporaryMarkdown -Markdown "# HTML replacement`n`n<p><strong>replacementStrongMarker</strong> and <em>replacementEmphasisMarker</em></p>`n" -RequiredText 'replacementStrongMarker'
                $replaced = Get-P2Text
                foreach ($stale in @('Native safe HTML', 'A native blockquote remains selectable', 'unsafe-script-sentinel')) {
                    if ($replaced.Contains($stale)) { throw "[$mode] The replacement still exposes stale Html sample content '$stale'." }
                }
                Set-TemporaryMarkdown -Markdown $htmlSource -RequiredText 'Native safe HTML'
                $restored = Get-P2Text
                if ($restored.Contains('replacementStrongMarker')) { throw "[$mode] Returning to the Html sample still exposes the replacement document." }
                $observed.Add([pscustomobject]@{ viewport = $mode; replacementExact = $true; staleAbsent = $true })
            }
        }
        finally { Exit-P2Mode }
        $engine = Invoke-P2EngineReplacement -Feature 'html'
        return [pscustomobject]@{ documentReplacement = @($observed.ToArray()); engineReplacement = $engine }
    }

    # ── Math ─────────────────────────────────────────────────────────────────
    $mathInlineCount = 20
    $mathBlockCount = 7
    Test-Case -Id 'P2-MATH-01' -Name 'math-sample-renders-every-valid-formula-natively' -Requirement 'Every valid inline and block formula on the Math sample is a visible native MarkdownMath scene with no code or source fallback; escaped dollars and backslash delimiters stay literal.' -Body {
        Navigate-Sample -Key 'Math' -RequiredText 'Native mathematics'
        Scroll-P2Bottom
        $math = @(Get-P2Elements -ClassName 'MarkdownMath')
        $expected = $mathInlineCount + $mathBlockCount
        if ($math.Count -ne $expected) { throw "The Math sample exposed $($math.Count) MarkdownMath scenes; the fixture has $expected valid formulas." }
        if (@(Get-P2Elements -ClassName 'MarkdownCodeBlock').Count -gt 0) { throw 'A valid Math fixture fell back to a code block.' }
        $text = Get-P2Text
        # CommonMark backslash escapes render the escaped punctuation, so the
        # authored \(x+y\) and \[z\] read "(x+y)" and "[z]" as plain text; the
        # exact MarkdownMath count above proves they did not become formulas.
        foreach ($literal in @('$20 remain text', 'remain literal: (x+y) and [z].')) {
            if (-not $text.Contains($literal)) { throw "The literal '$literal' is missing; escaped dollars and backslash delimiters must stay literal text." }
        }
        $diagnostic = Get-SampleDiagnosticMessage
        if ($diagnostic -match 'MATH') { throw "The valid Math fixture emitted a diagnostic: $diagnostic" }
        return [pscustomobject]@{ nativeMathScenes = $math.Count; literalsKept = 3; screenshot = (Save-Screenshot 'screenshots\p2-math-01.png') }
    }

    Test-Case -Id 'P2-MATH-02' -Name 'math-invalid-and-unsupported-keep-exact-source-and-diagnostic' -Requirement 'An invalid formula and an unsupported command keep their exact authored source visible and report the mapped Math diagnostic.' -Body {
        Navigate-Sample -Key 'Math' -RequiredText 'Native mathematics'
        $source = "# Math fallback`n`nInvalid: `$\frac{1}{`$ end.`n`nUnsupported: `$\underbrace{x+y}_{z}`$ end.`n`nValid: `$a+b`$.`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'Math fallback'
        Start-Sleep -Milliseconds 800
        $text = Get-P2Text
        foreach ($authored in @('\frac{1}{', '\underbrace{x+y}_{z}')) {
            if (-not $text.Contains($authored)) { throw "The authored source '$authored' is not visible after its fallback." }
        }
        $diagnostic = Get-SampleDiagnosticMessage
        if ($diagnostic -notmatch 'MATH\d+') { throw "The invalid formulas did not report a mapped Math diagnostic; diagnostics='$diagnostic'." }
        $valid = @(Get-P2Elements -ClassName 'MarkdownMath')
        if ($valid.Count -lt 1) { throw 'The valid formula next to the fallbacks did not render.' }
        return [pscustomobject]@{ authoredSourceKept = $true; diagnostic = $diagnostic; validScenes = $valid.Count; screenshot = (Save-Screenshot 'screenshots\p2-math-02.png') }
    }

    Test-Case -Id 'P2-MATH-03' -Name 'math-inline-block-and-final-formula-selection-and-copy' -Requirement 'In both viewport modes, inline, block and final-document formulas select as atomic readable ranges, and Copy as Markdown returns their exact authored source.' -Body {
        $source = "# Math selection`n`nInline `$E=mc^2`$ formula.`n`n`$`$`n\sum_{i=1}^{n} i`n`$`$`n`nBefore final.`n`n`$`$`nx^2+y^2`n`$`$`n"
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'Math' -RequiredText 'Native mathematics' -Markdown $source -MarkdownRequiredText 'Math selection'
                Start-Sleep -Milliseconds 600
                $math = @(Get-P2Elements -ClassName 'MarkdownMath')
                if ($math.Count -ne 3) { throw "[$mode] Expected 3 native formulas in the selection fixture, found $($math.Count)." }
                Clear-P2Selection
                $inline = Select-P2Range -StartNeedle 'Inline' -EndNeedle 'formula.'
                $inlineMarkdown = Copy-P2MarkdownSelection -Range $inline
                if ([string]$inlineMarkdown.plain -cne 'Inline $E=mc^2$ formula.') { throw "[$mode] Inline formula Copy as Markdown returned '$($inlineMarkdown.plain)'." }
                Clear-P2Selection
                $null = Invoke-Ui @('focus', 'MarkdownRenderer')
                $null = Invoke-Ui @('send-keys', 'ctrl+a', '--via', 'send-input')
                $selection = Invoke-GuestProbe -Mode 'selection'
                $final = $math | Sort-Object { [double]$_.y } | Select-Object -Last 1
                $selectedRects = @($selection.ranges | ForEach-Object { $_.rectangles } | Where-Object { $_.width -gt 0 -and $_.height -gt 0 })
                $finalCovered = @($selectedRects | Where-Object {
                    ($_.x + $_.width) -gt $final.x -and $_.x -lt ($final.x + $final.width) -and ($_.y + $_.height) -gt $final.y -and $_.y -lt ($final.y + $final.height) })
                if ($finalCovered.Count -eq 0) { throw "[$mode] Select All left the final formula without selection geometry." }
                $all = Copy-P2MarkdownSelection -Range ([pscustomobject]@{ rectangles = @($selectedRects[0]) })
                $expectedSource = Get-NativeMarkdownEditorSource -Text $source
                if ((([string]$all.plain) -replace "`r`n|`r", "`n").TrimEnd("`n") -cne ($expectedSource -replace "`r", "`n").TrimEnd("`n")) { throw "[$mode] Copy as Markdown of the whole document did not return the exact source including the final formula." }
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ viewport = $mode; inline = [string]$inlineMarkdown.plain; finalSelected = $true; screenshot = (Save-Screenshot "screenshots\p2-math-03-$mode.png") })
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P2-MATH-04' -Name 'math-theme-text-size-and-document-replacement-discard-stale-scenes' -Requirement 'Theme and text-size changes re-render the Math scenes at the new size, replacing the document leaves only the current document scenes, and replacing the engine of a live document in both viewport modes re-renders only the current scenes.' -Body {
        try {
            Navigate-Sample -Key 'Math' -RequiredText 'Native mathematics'
            $before = @(Get-P2Elements -ClassName 'MarkdownMath')
            $first = $before | Sort-Object { [double]$_.y }, { [double]$_.x } | Select-Object -First 1
            Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $true
            Wait-ThemeState -Theme 'dark' -Contrast 'hc:off' -TextScale '1'
            $dark = Save-Screenshot 'screenshots\p2-math-04-dark.png'
            Set-ToggleState -AutomationId 'TextScaleToggle' -Enabled $true
            Wait-ThemeState -Theme 'dark' -Contrast 'hc:off' -TextScale '2'
            Start-Sleep -Milliseconds 800
            $null = Invoke-Ui @('scroll', 'MarkdownRenderer', '--to', 'top')
            $scaled = @(Get-P2Elements -ClassName 'MarkdownMath')
            $scaledFirst = $scaled | Sort-Object { [double]$_.y }, { [double]$_.x } | Select-Object -First 1
            if ([double]$scaledFirst.height -lt ([double]$first.height * 1.5)) { throw "The first formula did not re-render for 200% text (height $($first.height) -> $($scaledFirst.height))." }
            $scaledShot = Save-Screenshot 'screenshots\p2-math-04-dark-200.png'
            Reset-P2Display
            Set-TemporaryMarkdown -Markdown "# Math replacement`n`nOnly `$q_{replacement}`$ remains.`n" -RequiredText 'Math replacement'
            Start-Sleep -Milliseconds 600
            $replaced = @(Get-P2Elements -ClassName 'MarkdownMath')
            if ($replaced.Count -ne 1) { throw "After replacement $($replaced.Count) formulas are exposed; only the current document's one formula may remain." }
            $display = [pscustomobject]@{ firstHeight = $first.height; scaledHeight = $scaledFirst.height; replacementScenes = $replaced.Count; dark = $dark; scaled = $scaledShot }
        }
        finally { Reset-P2Display }
        $engine = Invoke-P2EngineReplacement -Feature 'math'
        return [pscustomobject]@{ display = $display; engineReplacement = $engine }
    }

    # ── Mermaid ──────────────────────────────────────────────────────────────
    $mermaidDiagramCount = 17
    Test-Case -Id 'P2-MERMAID-01' -Name 'mermaid-sample-renders-every-documented-diagram-natively' -Requirement 'Every documented diagram on the Mermaid sample renders as a native MarkdownDiagram with labels, and the invokable node exposes its link, with no code fallback.' -Body {
        Navigate-Sample -Key 'Mermaid' -RequiredText 'Native Mermaid'
        Scroll-P2Bottom
        $diagrams = @(Get-P2Elements -ClassName 'MarkdownDiagram')
        if ($diagrams.Count -ne $mermaidDiagramCount) { throw "The Mermaid sample exposed $($diagrams.Count) native diagrams; it documents $mermaidDiagramCount." }
        if (@(Get-P2Elements -ClassName 'MarkdownCodeBlock').Count -gt 0) { throw 'A documented Mermaid diagram fell back to code.' }
        $nodes = @(Get-P2Elements -ClassName 'MarkdownDiagramNode')
        if ($nodes.Count -lt $mermaidDiagramCount) { throw "Only $($nodes.Count) diagram nodes expose labels." }
        $links = @(Get-P2Elements -ClassName 'MarkdownDiagramLink' | Where-Object { [string]$_.name -ceq 'Invokable Mermaid node' })
        if ($links.Count -ne 1) { throw 'The invokable Mermaid node did not expose its link.' }
        return [pscustomobject]@{ diagrams = $diagrams.Count; labelledNodes = $nodes.Count; link = $links[0].name; screenshot = (Save-Screenshot 'screenshots\p2-mermaid-01.png') }
    }

    Test-Case -Id 'P2-MERMAID-02' -Name 'mermaid-invalid-and-unsupported-keep-exact-code-and-diagnostic' -Requirement 'Invalid Mermaid syntax and an unsupported ELK layout keep their exact code visible and report the mapped Mermaid diagnostic.' -Body {
        Navigate-Sample -Key 'Mermaid' -RequiredText 'Native Mermaid'
        $source = "# Mermaid fallback`n`n``````mermaid`ngraph TD`n  A[invalidStart] -->`n```````n`n``````mermaid`n%%{init: {""flowchart"": {""defaultRenderer"": ""elk""}}}%%`nflowchart LR`n  elkStart --> elkEnd`n```````n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'Mermaid fallback'
        Start-Sleep -Milliseconds 1500
        $text = Get-P2Text
        foreach ($authored in @('A[invalidStart] -->', 'elkStart --> elkEnd')) {
            if (-not $text.Contains($authored)) { throw "The authored Mermaid code '$authored' is not visible after its fallback." }
        }
        if (@(Get-P2Elements -ClassName 'MarkdownDiagram').Count -gt 0) { throw 'An invalid or unsupported diagram rendered a native scene.' }
        $diagnostic = Get-SampleDiagnosticMessage
        if ($diagnostic -notmatch 'MMR\d+') { throw "The Mermaid fallbacks did not report a mapped Mermaid diagnostic; diagnostics='$diagnostic'." }
        return [pscustomobject]@{ exactCodeKept = $true; diagnostic = $diagnostic; screenshot = (Save-Screenshot 'screenshots\p2-mermaid-02.png') }
    }

    Test-Case -Id 'P2-MERMAID-03' -Name 'mermaid-contrast-semantics-selection-copy-and-final-diagram' -Requirement 'The Mermaid sample stays legible in Light, Dark and forced High Contrast; in both viewport modes nodes and links keep UIA semantics, a diagram selects atomically, Copy as Markdown returns its exact fenced source, and the final diagram is selectable.' -Body {
        $observed = [System.Collections.Generic.List[object]]::new()
        $source = "# Mermaid selection`n`nBefore diagram.`n`n``````mermaid`nflowchart LR`n  selectA[Selectable start] --> selectB[Selectable end]`n```````n"
        try {
            Navigate-Sample -Key 'Mermaid' -RequiredText 'Native Mermaid'
            $themes = @()
            $themes += Save-Screenshot 'screenshots\p2-mermaid-03-light.png'
            Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $true
            Wait-ThemeState -Theme 'dark' -Contrast 'hc:off' -TextScale '1'
            $themes += Save-Screenshot 'screenshots\p2-mermaid-03-dark.png'
            Set-ToggleState -AutomationId 'ForcedHighContrastToggle' -Enabled $true
            Wait-ThemeState -Theme 'high-contrast' -Contrast 'hc:on' -TextScale '1'
            $themes += Save-Screenshot 'screenshots\p2-mermaid-03-forced-high-contrast.png'
            if (@(Get-P2Elements -ClassName 'MarkdownDiagram').Count -lt 1) { throw 'Forced High Contrast removed the native diagrams.' }
            Reset-P2Display
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'Mermaid' -RequiredText 'Native Mermaid' -Markdown $source -MarkdownRequiredText 'Mermaid selection'
                Start-Sleep -Milliseconds 1200
                $diagram = @(Get-P2Elements -ClassName 'MarkdownDiagram')
                if ($diagram.Count -ne 1) { throw "[$mode] The selection fixture exposed $($diagram.Count) diagrams." }
                $nodes = @(Get-P2Elements -ClassName 'MarkdownDiagramNode' | Where-Object { [string]$_.name -in @('Selectable start', 'Selectable end') })
                if ($nodes.Count -ne 2) { throw "[$mode] The final diagram's nodes do not expose their labels." }
                Clear-P2Selection
                $null = Invoke-Ui @('focus', 'MarkdownRenderer')
                $null = Invoke-Ui @('send-keys', 'ctrl+a', '--via', 'send-input')
                $selection = Invoke-GuestProbe -Mode 'selection'
                $rects = @($selection.ranges | ForEach-Object { $_.rectangles } | Where-Object { $_.width -gt 0 -and $_.height -gt 0 })
                $d = $diagram[0]
                if (@($rects | Where-Object { ($_.y + $_.height) -gt $d.y -and $_.y -lt ($d.y + $d.height) }).Count -eq 0) { throw "[$mode] Select All did not select the final diagram." }
                $copied = Copy-P2MarkdownSelection -Range ([pscustomobject]@{ rectangles = @($rects[0]) })
                $expected = (Get-NativeMarkdownEditorSource -Text $source) -replace "`r", "`n"
                if ((([string]$copied.plain) -replace "`r`n|`r", "`n").TrimEnd("`n") -cne $expected.TrimEnd("`n")) { throw "[$mode] Copy as Markdown did not return the exact source including the final fenced diagram." }
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ viewport = $mode; nodes = $nodes.Count; finalDiagramSelected = $true; screenshot = (Save-Screenshot "screenshots\p2-mermaid-03-$mode.png") })
            }
        }
        finally {
            Reset-P2Display
            Exit-P2Mode
        }
        throw 'UNVERIFIED: Light, Dark and forced High Contrast, node/link semantics, atomic selection and exact copy passed in both viewport modes; actual Windows system High Contrast runs in the follow-up runner, and screenshots need review.'
    }

    # Rendered plain-text copy of Mermaid diagrams (P2-010, P2-011, P2-015).
    # A selected diagram copies one line per exposed semantic item name; the
    # oracle is each record's stated contract, not the copy implementation.
    $mermaidCopyArabic = 'مرحبا API 👩‍💻'
    $mermaidCopyCaption = 'uses stable text'
    $mermaidCopyPlaceholder = 'ﬂ°quot¶ß'
    $mermaidCopySource = "# Mermaid copy`n`nBefore equal labels.`n`n``````mermaid`nflowchart LR`n  a1[Same label] --> a2[Same label]`n  b1[""$mermaidCopyArabic""] --> b2[""$mermaidCopyArabic""]`n```````n`nBetween diagrams one.`n`n``````mermaid`nclassDiagram`n  class A`n  class B`n  A --> B : $mermaidCopyCaption`n```````n`nBetween diagrams two.`n`n``````mermaid`nmindmap`n  root[$mermaidCopyPlaceholder #quot; literal #amp;quot;]`n    child`n```````n`nAfter diagrams.`n"
    Test-Case -Id 'P2-MERMAID-03-COPY' -Name 'mermaid-rendered-plain-copy-keeps-logical-labels' -Requirement 'In both viewport modes, Ctrl+C of a selection across Mermaid diagrams copies each distinct node''s equal label as its own whole line (P2-011), a class relationship caption once as one logical line (P2-015), and authored placeholder-shaped text and entities as their final literal text (P2-010); the copy matches the selected UIA text.' -Body {
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'Mermaid' -RequiredText 'Native Mermaid' -Markdown $mermaidCopySource -MarkdownRequiredText 'Mermaid copy'
                Start-Sleep -Milliseconds 1500
                $diagrams = @(Get-P2Elements -ClassName 'MarkdownDiagram')
                if ($diagrams.Count -ne 3) { throw "[$mode] The copy fixture exposed $($diagrams.Count) native diagrams; it has 3." }
                Clear-P2Selection
                $range = Select-P2Range -StartNeedle 'Before equal labels.' -EndNeedle 'After diagrams.'
                $selected = Get-SelectedText
                $copied = Copy-P2PlainSelection
                $plain = [string]$copied.plain
                if (($plain -replace "`r`n|`r", "`n") -cne ($selected -replace "`r`n|`r", "`n")) { throw "[$mode] Ctrl+C plain text differs from the selected UIA text." }
                $lines = @(($plain -split "`r`n|`r|`n") | Where-Object { $_ -ne '' })
                $section = {
                    param([string]$from, [string]$to)
                    $a = [Array]::IndexOf($lines, $from)
                    $b = [Array]::IndexOf($lines, $to)
                    if ($a -lt 0 -or $b -le $a) { throw "[$mode] The copied text does not keep '$from' before '$to': $($lines -join ' | ')" }
                    return @($lines[($a + 1)..($b - 1)])
                }

                # P2-011: four distinct nodes, two equal labels each.
                $flow = & $section 'Before equal labels.' 'Between diagrams one.'
                $expectedFlow = @('Same label', 'Same label', $mermaidCopyArabic, $mermaidCopyArabic) | Sort-Object
                if ((@($flow | Sort-Object) -join "`n") -cne ($expectedFlow -join "`n")) { throw "[$mode] The flowchart copied '$($flow -join ' | ')'; each of the four nodes must copy its whole label once." }

                # P2-015: the relationship caption is one logical line.
                $class = & $section 'Between diagrams one.' 'Between diagrams two.'
                if (@($class | Where-Object { $_ -ceq $mermaidCopyCaption }).Count -ne 1) { throw "[$mode] The class diagram copied '$($class -join ' | ')'; the caption must appear once as a whole line." }
                $fragments = @($class | Where-Object { $_ -cne $mermaidCopyCaption -and $mermaidCopyCaption.Contains($_) })
                if ($fragments.Count -gt 0) { throw "[$mode] The class diagram copied caption fragments: '$($fragments -join ' | ')'." }
                foreach ($name in @('A', 'B')) {
                    if (@($class | Where-Object { $_ -ceq $name }).Count -ne 1) { throw "[$mode] The class diagram did not copy class '$name' once: '$($class -join ' | ')'." }
                }

                # P2-010: placeholder-shaped authored text stays literal; Mermaid
                # entities decode once (#quot; -> ", #amp;quot; -> &quot;).
                $mind = & $section 'Between diagrams two.' 'After diagrams.'
                $expectedRoot = "$mermaidCopyPlaceholder `" literal &quot;"
                if (@($mind | Where-Object { $_ -ceq $expectedRoot }).Count -ne 1 -or @($mind | Where-Object { $_ -ceq 'child' }).Count -ne 1) { throw "[$mode] The mindmap copied '$($mind -join ' | ')'; expected '$expectedRoot' and 'child' once each." }

                if (-not [bool]$copied.hasHtml) { throw "[$mode] Ctrl+C did not place a CF_HTML fragment." }
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ viewport = $mode; flowchartLines = $flow; classLines = $class; mindmapLines = $mind; screenshot = (Save-Screenshot "screenshots\p2-mermaid-03-copy-$mode.png") })
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P2-MERMAID-04' -Name 'mermaid-navigation-and-replacement-publish-no-obsolete-scene' -Requirement 'Navigating away and back, replacing the Mermaid document, and replacing the engine of a live document in both viewport modes leave only the current document diagrams.' -Body {
        Navigate-Sample -Key 'Mermaid' -RequiredText 'Native Mermaid'
        Navigate-Sample -Key 'Math' -RequiredText 'Native mathematics'
        if (@(Get-P2Elements -ClassName 'MarkdownDiagram').Count -gt 0) { throw 'A Mermaid diagram survived navigation to the Math page.' }
        Navigate-Sample -Key 'Mermaid' -RequiredText 'Native Mermaid'
        Scroll-P2Bottom
        $back = @(Get-P2Elements -ClassName 'MarkdownDiagram')
        if ($back.Count -ne $mermaidDiagramCount) { throw "Returning to Mermaid exposed $($back.Count) diagrams instead of $mermaidDiagramCount." }
        Set-TemporaryMarkdown -Markdown "# Mermaid replacement`n`n``````mermaid`nflowchart LR`n  onlyNode[Only replacement node]`n```````n" -RequiredText 'Mermaid replacement'
        Start-Sleep -Milliseconds 1200
        $replaced = @(Get-P2Elements -ClassName 'MarkdownDiagram')
        if ($replaced.Count -ne 1) { throw "After replacement $($replaced.Count) diagrams are exposed; only the current one may remain." }
        $engine = Invoke-P2EngineReplacement -Feature 'mermaid'
        return [pscustomobject]@{ navigationDiagrams = $back.Count; replacementDiagrams = $replaced.Count; engineReplacement = $engine }
    }

    Run-Phase2MediaCases
}

# Scans screenshot pixels from a start point while they differ from the
# background and returns the contiguous run (physical pixels).
function Measure-P2Run {
    param(
        [Parameter(Mandatory)]$Sampler,
        [Parameter(Mandatory)][double]$StartX,
        [Parameter(Mandatory)][double]$StartY,
        [Parameter(Mandatory)][ValidateSet('x', 'y')][string]$Axis,
        [Parameter(Mandatory)][double]$Limit,
        [Parameter(Mandatory)]$Background
    )
    $first = $null
    $last = $null
    $gap = 0
    $position = if ($Axis -eq 'x') { $StartX } else { $StartY }
    while ($position -le $Limit) {
        $pixel = if ($Axis -eq 'x') { Get-P2Pixel -Sampler $Sampler -ScreenX $position -ScreenY $StartY } else { Get-P2Pixel -Sampler $Sampler -ScreenX $StartX -ScreenY $position }
        $isBackground = Test-P2ColorNear -Actual $pixel -Expected @($Background.r, $Background.g, $Background.b) -Tolerance 24
        if (-not $isBackground) {
            if ($null -eq $first) { $first = $position }
            $last = $position
            $gap = 0
        }
        elseif ($null -ne $first) {
            $gap++
            if ($gap -gt 3) { break }
        }
        $position += 1
    }
    if ($null -eq $first) { return $null }
    return [pscustomobject]@{ start = $first; end = $last; length = ($last - $first + 1) }
}

function Assert-P2Near {
    param([Parameter(Mandatory)][double]$Actual, [Parameter(Mandatory)][double]$Expected, [Parameter(Mandatory)][string]$What, [double]$Tolerance = 3)
    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) { throw "$What is $Actual physical pixels; expected $Expected +/- $Tolerance." }
}

# Measures a left-aligned block image from its peer rectangle: the top edge
# and height along one column, then the width along the row at mid height.
function Measure-P2BlockImage {
    param([Parameter(Mandatory)]$Sampler, [Parameter(Mandatory)]$Peer, [Parameter(Mandatory)][double]$ColumnOffset)
    $background = Get-P2Pixel -Sampler $Sampler -ScreenX ([double]$Peer.x + [double]$Peer.width - 6) -ScreenY ([double]$Peer.y + 2)
    $column = [double]$Peer.x + $ColumnOffset
    $vertical = Measure-P2Run -Sampler $Sampler -StartX $column -StartY ([double]$Peer.y) -Axis 'y' -Limit ([double]$Peer.y + [double]$Peer.height - 1) -Background $background
    if ($null -eq $vertical) { throw "No image pixels were found in the column at offset $ColumnOffset." }
    $row = $vertical.start + ($vertical.length / 2.0)
    $horizontal = Measure-P2Run -Sampler $Sampler -StartX ([double]$Peer.x) -StartY $row -Axis 'x' -Limit ([double]$Peer.x + [double]$Peer.width - 1) -Background $background
    return [pscustomobject]@{ background = $background; top = $vertical.start; height = $vertical.length; left = $horizontal.start; width = $horizontal.length; row = $row }
}

function Run-Phase2MediaCases {
    $dpi = [double](Invoke-GuestProbe -Mode 'metadata').dpiScale
    if ($dpi -le 0) { $dpi = 1.0 }

    # ── SVG ──────────────────────────────────────────────────────────────────
    Test-Case -Id 'P2-SVG-01' -Name 'svg-stress-renders-every-supported-fixture' -Requirement 'Every supported SvgStress fixture finishes loaded with visible geometry, and the feature matrix shows its full multi-color artwork.' -Body {
        Navigate-Sample -Key 'SvgStress' -RequiredText 'Browser-class static SVG'
        $supported = @('Theme-aware SVG feature matrix', 'Unicode and bidirectional SVG text', 'Embedded-resource deduplication', 'Wide graph constrained to its table cell', 'Final selectable SVG with a check mark')
        $loaded = [System.Collections.Generic.List[object]]::new()
        foreach ($alt in $supported) {
            $peer = Get-P2ImagePeer -AltText $alt
            Assert-P2LoadedImage -Peer $peer -AltText $alt
            $loaded.Add(@{ alt = $alt; width = $peer.width; height = $peer.height })
        }
        # The twelve storm icons share a name prefix ("icon 1" also matches 10-12),
        # so they are read from the tree by exact name, not the unique-match probe.
        $null = Scroll-P2Text -Needle 'Repeated cache-test icon 1'
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        do {
            $storm = @(Get-P2Elements -ClassName 'MarkdownImage' | Where-Object { [string]$_.name -match '^(Loading |Image unavailable: )?Repeated cache-test icon \d+$' })
            $ready = @($storm | Where-Object { [string]$_.name -match '^Repeated cache-test icon \d+$' -and [double]$_.width -gt 0 -and [double]$_.height -gt 0 })
            if ($ready.Count -eq 12) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($storm.Count -ne 12 -or $ready.Count -ne 12) { throw "The twelve storm icons are not all loaded with geometry: $(@($storm | ForEach-Object { $_.name }) -join '; ')." }
        $null = Scroll-P2Text -Needle 'Theme-aware SVG feature matrix'
        $matrix = Get-P2ImagePeer -AltText 'Theme-aware SVG feature matrix'
        $shot = Save-Screenshot 'screenshots\p2-svg-01-feature-matrix.png'
        $sampler = Get-P2PixelSampler -Shot $shot
        try { $colors = Get-P2DistinctColorCount -Sampler $sampler -Rect $matrix }
        finally { $sampler.bitmap.Dispose() }
        if ($colors -lt 6) { throw "The feature matrix shows only $colors distinct colors; its artwork has missing regions." }
        return [pscustomobject]@{ loadedFixtures = $loaded.Count; featureMatrixColors = $colors; screenshot = $shot }
    }

    Test-Case -Id 'P2-SVG-02' -Name 'svg-unsafe-fixtures-show-typed-accessible-placeholders' -Requirement 'Script-bearing, external-resource and animated SVG fixtures each show one typed accessible unavailable-image placeholder with visible geometry; no fixture renders partial artwork.' -Body {
        Navigate-Sample -Key 'SvgStress' -RequiredText 'Browser-class static SVG'
        $null = Scroll-P2Text -Needle 'Each unsafe input must render one atomic diagnostic placeholder'
        $observed = [System.Collections.Generic.List[object]]::new()
        foreach ($alt in @('Script-bearing SVG', 'Nested external image SVG', 'Animated SVG')) {
            $state = Wait-ForImagePeerState -AltText $alt -TimeoutMilliseconds 8000
            $peer = @($state.images)[0]
            if ([string]$peer.name -notmatch ('^Image unavailable:\s*' + [regex]::Escape($alt) + '$')) { throw "The $alt peer is not a typed unavailable-image placeholder; Name='$($peer.name)'." }
            if ([bool]$peer.offscreen -or [double]$peer.width -le 0 -or [double]$peer.height -le 0) { throw "The $alt placeholder has no visible geometry." }
            $observed.Add(@{ alt = $alt; name = $peer.name; helpText = $peer.helpText })
        }
        return [pscustomobject]@{ placeholders = @($observed.ToArray()); screenshot = (Save-Screenshot 'screenshots\p2-svg-02.png') }
    }

    Test-Case -Id 'P2-SVG-03' -Name 'svg-theme-change-repaints-currentcolor-and-resize-keeps-current-generation' -Requirement 'Switching Light to Dark repaints the currentColor-dependent feature matrix, and a narrow resize keeps every fixture loaded at its new size.' -Body {
        $meta = Invoke-GuestProbe -Mode 'metadata'
        try {
            Navigate-Sample -Key 'SvgStress' -RequiredText 'Browser-class static SVG'
            $lightPeer = Get-P2ImagePeer -AltText 'Theme-aware SVG feature matrix'
            $lightShot = Save-Screenshot 'screenshots\p2-svg-03-light.png'
            Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $true
            Wait-ThemeState -Theme 'dark' -Contrast 'hc:off' -TextScale '1'
            Start-Sleep -Milliseconds 800
            $darkPeer = Get-P2ImagePeer -AltText 'Theme-aware SVG feature matrix'
            Assert-P2LoadedImage -Peer $darkPeer -AltText 'Theme-aware SVG feature matrix'
            $darkShot = Save-Screenshot 'screenshots\p2-svg-03-dark.png'
            $light = Get-P2PixelSampler -Shot $lightShot
            $dark = Get-P2PixelSampler -Shot $darkShot
            $changed = 0
            $total = 0
            try {
                for ($i = 1; $i -le 9; $i++) {
                    for ($j = 1; $j -le 9; $j++) {
                        $a = Get-P2RectPixel -Sampler $light -Rect $lightPeer -FractionX ($i / 10.0) -FractionY ($j / 10.0)
                        $b = Get-P2RectPixel -Sampler $dark -Rect $darkPeer -FractionX ($i / 10.0) -FractionY ($j / 10.0)
                        $total++
                        if (-not (Test-P2ColorNear -Actual $a -Expected @($b.r, $b.g, $b.b) -Tolerance 24)) { $changed++ }
                    }
                }
            }
            finally { $light.bitmap.Dispose(); $dark.bitmap.Dispose() }
            if ($changed -lt ($total / 10)) { throw "Only $changed of $total sampled feature-matrix pixels changed between Light and Dark; currentColor artwork did not repaint." }
            Reset-P2Display
            Navigate-Sample -Key 'SvgStress' -RequiredText 'Browser-class static SVG'
            $null = Invoke-GuestProbe -Mode 'resize' -Left ([int]$meta.window.left) -Top ([int]$meta.window.top) -Width ([int][Math]::Round(760 * $meta.dpiScale)) -Height ([int][Math]::Round(860 * $meta.dpiScale))
            Start-Sleep -Milliseconds 600
            foreach ($alt in @('Theme-aware SVG feature matrix', 'Wide graph constrained to its table cell', 'Final selectable SVG with a check mark')) {
                Assert-P2LoadedImage -Peer (Get-P2ImagePeer -AltText $alt) -AltText $alt
            }
            return [pscustomobject]@{ changedPixels = $changed; sampledPixels = $total; light = $lightShot; dark = $darkShot; narrowLoaded = $true }
        }
        finally {
            Reset-P2Display
            $null = Invoke-GuestProbe -Mode 'resize' -Left ([int]$meta.window.left) -Top ([int]$meta.window.top) -Width ([int]($meta.window.right - $meta.window.left)) -Height ([int]($meta.window.bottom - $meta.window.top))
        }
    }

    $finalSvg = 'data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI4MCIgaGVpZ2h0PSI0OCI+PHJlY3Qgd2lkdGg9IjgwIiBoZWlnaHQ9IjQ4IiBmaWxsPSIjMGI4YTNjIi8+PC9zdmc+'
    Test-Case -Id 'P2-SVG-04' -Name 'svg-final-document-image-alt-range-copy-and-artwork' -Requirement 'In both viewport modes a final-document SVG keeps its exact alt text, selects as one semantic range, copies its exact Markdown source and shows its artwork.' -Body {
        $source = "# Final SVG`n`nBefore the final image.`n`n![Final temp SVG check]($finalSvg)`n"
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'SvgStress' -RequiredText 'Browser-class static SVG' -Markdown $source -MarkdownRequiredText 'Final SVG'
                $peer = Get-P2ImagePeer -AltText 'Final temp SVG check'
                Assert-P2LoadedImage -Peer $peer -AltText 'Final temp SVG check'
                Clear-P2Selection
                $range = Select-P2Range -StartNeedle 'Final temp SVG check' -EndNeedle 'Final temp SVG check'
                if ([string]$range.actualSelection -cne 'Final temp SVG check') { throw "[$mode] The image selected '$($range.actualSelection)' instead of its exact alt text." }
                if (@($range.rectangles).Count -ne 1) { throw "[$mode] The image alt range has $(@($range.rectangles).Count) rectangles; expected one semantic range." }
                $copied = Copy-P2MarkdownSelection -Range $range
                if ([string]$copied.plain -cne "![Final temp SVG check]($finalSvg)") { throw "[$mode] Copy as Markdown returned '$($copied.plain)'." }
                Clear-P2Selection
                $shot = Save-Screenshot "screenshots\p2-svg-04-$mode.png"
                $sampler = Get-P2PixelSampler -Shot $shot
                try { $measure = Measure-P2BlockImage -Sampler $sampler -Peer $peer -ColumnOffset (10 * $dpi) }
                finally { $sampler.bitmap.Dispose() }
                Assert-P2Near -Actual $measure.width -Expected (80 * $dpi) -What "[$mode] Final SVG artwork width"
                Assert-P2Near -Actual $measure.height -Expected (48 * $dpi) -What "[$mode] Final SVG artwork height"
                $observed.Add([pscustomobject]@{ viewport = $mode; alt = $peer.name; rectangles = 1; copied = $true; artwork = $measure; screenshot = $shot })
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }

    # ── Raster ───────────────────────────────────────────────────────────────
    Test-Case -Id 'P2-RASTER-01' -Name 'raster-png-jpeg-gif-webp-size-orientation-and-alpha' -Requirement 'The Images sample PNG, JPEG, GIF and static WebP fixtures render at their admitted size, the JPEG follows its EXIF orientation, and transparent regions show the page background.' -Body {
        Navigate-Sample -Key 'Images' -RequiredText 'Images, captions, and SVG'
        $fixtures = @(
            @{ alt = 'Local PNG with alpha, 64×40'; width = 32; height = 40; color = @(0, 90, 200); transparentAt = 48 },
            @{ alt = 'Local JPEG with EXIF orientation 6, displayed 40×64'; width = 40; height = 64; color = @(220, 30, 30); bottomColor = @(20, 160, 60) },
            @{ alt = 'Local static GIF, 48×48'; width = 48; height = 48; color = @(120, 40, 160) },
            @{ alt = 'Local static WebP with alpha, 64×40'; width = 32; height = 40; color = @(0, 140, 140); transparentAt = 48 }
        )
        $observed = [System.Collections.Generic.List[object]]::new()
        foreach ($fixture in $fixtures) {
            $peer = Get-P2ImagePeer -AltText $fixture.alt
            Assert-P2LoadedImage -Peer $peer -AltText $fixture.alt
            $shot = Save-Screenshot ('screenshots\p2-raster-01-{0}.png' -f ($observed.Count + 1))
            $sampler = Get-P2PixelSampler -Shot $shot
            try {
                $m = Measure-P2BlockImage -Sampler $sampler -Peer $peer -ColumnOffset (4 * $dpi)
                Assert-P2Near -Actual $m.width -Expected ($fixture.width * $dpi) -What "$($fixture.alt) opaque width"
                Assert-P2Near -Actual $m.height -Expected ($fixture.height * $dpi) -What "$($fixture.alt) height"
                $top = Get-P2Pixel -Sampler $sampler -ScreenX ($m.left + $m.width / 2.0) -ScreenY ($m.top + $m.height * 0.25)
                if (-not (Test-P2ColorNear -Actual $top -Expected $fixture.color)) { throw "$($fixture.alt) shows ($($top.r),$($top.g),$($top.b)) instead of its authored color." }
                if ($fixture.ContainsKey('bottomColor')) {
                    $bottom = Get-P2Pixel -Sampler $sampler -ScreenX ($m.left + $m.width / 2.0) -ScreenY ($m.top + $m.height * 0.75)
                    if (-not (Test-P2ColorNear -Actual $bottom -Expected $fixture.bottomColor)) { throw "$($fixture.alt) is not oriented: its bottom shows ($($bottom.r),$($bottom.g),$($bottom.b))." }
                }
                if ($fixture.ContainsKey('transparentAt')) {
                    $clear = Get-P2Pixel -Sampler $sampler -ScreenX ($m.left + $fixture.transparentAt * $dpi) -ScreenY $m.row
                    if (-not (Test-P2ColorNear -Actual $clear -Expected @($m.background.r, $m.background.g, $m.background.b) -Tolerance 24)) { throw "$($fixture.alt) transparent half shows ($($clear.r),$($clear.g),$($clear.b)), not the page background." }
                }
            }
            finally { $sampler.bitmap.Dispose() }
            $observed.Add([pscustomobject]@{ alt = $fixture.alt; measured = $m; screenshot = $shot })
        }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P2-RASTER-02' -Name 'raster-animated-gif-and-webp-show-composed-first-canvas-without-playback' -Requirement 'Each animated fixture shows its composed first canvas (a 20×20 blue square centered on a transparent 60×60 canvas), never the raw subframe or the red later frame, and does not play.' -Body {
        Navigate-Sample -Key 'Images' -RequiredText 'Images, captions, and SVG'
        $observed = [System.Collections.Generic.List[object]]::new()
        $webpUnavailable = $false
        foreach ($alt in @('Animated GIF first canvas, 60×60 with a centered blue square', 'Animated WebP first canvas, 60×60 with a centered blue square')) {
            $peer = Get-P2ImagePeer -AltText $alt
            if ($alt -like 'Animated WebP*' -and [string]$peer.name -match '^Image unavailable:') {
                # Animated WebP decodes through the Windows WebP Image Extension,
                # which Windows Sandbox does not have (T3-008).
                $webpUnavailable = $true
                $observed.Add([pscustomobject]@{ alt = $alt; unavailableInSandbox = $true; screenshot = (Save-Screenshot 'screenshots\p2-raster-02-webp-unavailable.png') })
                continue
            }
            Assert-P2LoadedImage -Peer $peer -AltText $alt
            $samples = @()
            foreach ($delay in @(0, 1500)) {
                Start-Sleep -Milliseconds $delay
                $shot = Save-Screenshot ('screenshots\p2-raster-02-{0}-{1}.png' -f ($observed.Count + 1), $delay)
                $sampler = Get-P2PixelSampler -Shot $shot
                try {
                    $imageTop = [double]$peer.y + 6 * $dpi
                    $center = Get-P2Pixel -Sampler $sampler -ScreenX ([double]$peer.x + 30 * $dpi) -ScreenY ($imageTop + 30 * $dpi)
                    $corner = Get-P2Pixel -Sampler $sampler -ScreenX ([double]$peer.x + 5 * $dpi) -ScreenY ($imageTop + 5 * $dpi)
                    $background = Get-P2Pixel -Sampler $sampler -ScreenX ([double]$peer.x + [double]$peer.width - 6) -ScreenY ([double]$peer.y + 2)
                    if (-not (Test-P2ColorNear -Actual $center -Expected @(0, 90, 200))) { throw "$alt center shows ($($center.r),$($center.g),$($center.b)) after $delay ms; the composed first canvas is blue there." }
                    if (-not (Test-P2ColorNear -Actual $corner -Expected @($background.r, $background.g, $background.b) -Tolerance 24)) { throw "$alt corner shows ($($corner.r),$($corner.g),$($corner.b)) after $delay ms; the first canvas is transparent there (raw subframe or later frame shown)." }
                }
                finally { $sampler.bitmap.Dispose() }
                $samples += @{ delayMs = $delay; center = $center; corner = $corner; screenshot = $shot }
            }
            $observed.Add([pscustomobject]@{ alt = $alt; samples = $samples })
        }
        if ($webpUnavailable) { throw 'UNVERIFIED: The animated GIF shows its composed first canvas without playback; animated WebP needs the Windows WebP Image Extension, which Windows Sandbox lacks (T3-008), so it is verified only where that codec is installed.' }
        return @($observed.ToArray())
    }

    $widePng = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAABkAAAADICAIAAAA/Xbp/AAAFJklEQVR42u3YMQHAIADAsIKQKULBFKMEHTPBvkRCz47eHQAA95xniQAAcNGUAAAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAAAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAAAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAAAADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAIAMLAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAAAgAwsAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAACADCwAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAgAwsAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAACADCwAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAAIAMLAAAAAAysAAAAAAgAwsAAACADCwAAAAAyMACAAAAgAwsAAAAADKwAAAAACADCwAAAIAMLAAAAADIwAIAAACADCwAAAAAMrAAAAAAIAMLAAAAgAwsAAAAAMjAAgAAAIAMLAAAAAAysAAAAADgJx86awQCWKDaYwAAAABJRU5ErkJggg=='
    Test-Case -Id 'P2-RASTER-03' -Name 'raster-narrow-width-preserves-aspect-and-shrinks-in-tables' -Requirement 'At a narrow window width the Images and LazyImages fixtures stay loaded inside the renderer, and a wide raster in a table cell shrinks inside its cell with its aspect ratio preserved.' -Body {
        $meta = Invoke-GuestProbe -Mode 'metadata'
        try {
            $narrow = { $null = Invoke-GuestProbe -Mode 'resize' -Left ([int]$meta.window.left) -Top ([int]$meta.window.top) -Width ([int][Math]::Round(760 * $meta.dpiScale)) -Height ([int][Math]::Round(860 * $meta.dpiScale)); Start-Sleep -Milliseconds 600 }
            $wide = { $null = Invoke-GuestProbe -Mode 'resize' -Left ([int]$meta.window.left) -Top ([int]$meta.window.top) -Width ([int]($meta.window.right - $meta.window.left)) -Height ([int]($meta.window.bottom - $meta.window.top)); Start-Sleep -Milliseconds 400 }
            # The navigation pane collapses at narrow widths, so each page is
            # opened at the original width before the window narrows.
            $pages = @(
                @{ key = 'Images'; text = 'Images, captions, and SVG'; alts = @('Local PNG with alpha, 64×40', 'Local JPEG with EXIF orientation 6, displayed 40×64') },
                @{ key = 'LazyImages'; text = 'Lazy Image Loading'; alts = @('Wide blue landscape SVG fixture', 'Wide teal icon-strip SVG fixture') }
            )
            $renderer = $null
            foreach ($page in $pages) {
                Navigate-Sample -Key $page.key -RequiredText $page.text
                & $narrow
                $renderer = Get-UiBounds 'MarkdownRenderer'
                foreach ($alt in $page.alts) {
                    $peer = Get-P2ImagePeer -AltText $alt
                    Assert-P2LoadedImage -Peer $peer -AltText $alt
                    if (([double]$peer.x + [double]$peer.width) -gt ($renderer[0] + $renderer[2] + 2)) { throw "$alt extends outside the narrow renderer." }
                }
                & $wide
            }
            Navigate-Sample -Key 'Images' -RequiredText 'Images, captions, and SVG'
            & $narrow
            $source = "# Wide raster table`n`n| Image | Neighbor |`n| --- | --- |`n| ![Wide raster in a table cell]($widePng) | Adjacent raster cell text stays visible. |`n"
            Set-TemporaryMarkdown -Markdown $source -RequiredText 'Wide raster table'
            $null = Wait-ForImagePeerState -AltText 'Wide raster in a table cell' -TimeoutMilliseconds 8000
            $cell = Invoke-GuestProbe -Mode 'table-cell' -Needle 'Wide raster in a table cell' -StartNeedle 'Adjacent raster cell text stays visible.'
            $image = $cell.imageRect
            if (([double]$image.x + [double]$image.width) -gt ([double]$cell.neighborCell.x + 2)) { throw 'The wide raster crosses into the adjacent table cell.' }
            $ratio = [double]$image.width / [double]$image.height
            if ([Math]::Abs($ratio - 8.0) -gt 0.25) { throw "The wide raster aspect ratio is $ratio; the 1600×200 source is 8.0." }
            return [pscustomobject]@{ narrowRendererWidth = $renderer[2]; wideRaster = $image; aspectRatio = $ratio; screenshot = (Save-Screenshot 'screenshots\p2-raster-03.png') }
        }
        finally {
            $null = Invoke-GuestProbe -Mode 'resize' -Left ([int]$meta.window.left) -Top ([int]$meta.window.top) -Width ([int]($meta.window.right - $meta.window.left)) -Height ([int]($meta.window.bottom - $meta.window.top))
        }
    }

    $png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAAAoCAYAAABOzvzpAAAAVElEQVR42u3QAQ0AIAzAsB9VaEEgWnB1ZDyBzsCSZqxT0dme2bkf8XkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAODlLnVKA1BQMA6iAAAAAElFTkSuQmCC'
    Test-Case -Id 'P2-RASTER-04' -Name 'raster-linked-and-final-images-semantics-selection-and-copy' -Requirement 'In both viewport modes a linked image is a hyperlink named by its alt text, and a final-document image selects atomically as its alt text and copies its exact Markdown source.' -Body {
        $source = "# Raster semantics`n`n[![Linked raster alt]($png)](https://example.invalid/linked-raster)`n`nMiddle paragraph.`n`n![Final raster alt]($png)`n"
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'Images' -RequiredText 'Images, captions, and SVG' -Markdown $source -MarkdownRequiredText 'Raster semantics'
                $peer = Get-P2ImagePeer -AltText 'Final raster alt'
                Assert-P2LoadedImage -Peer $peer -AltText 'Final raster alt'
                $linked = @(Get-P2Elements -ClassName 'MarkdownLinkedImage' | Where-Object { [string]$_.name -like '*Linked raster alt*' })
                if ($linked.Count -ne 1 -or [string]$linked[0].type -cne 'Hyperlink') { throw "[$mode] The linked image is not one Hyperlink named by its alt text." }
                Clear-P2Selection
                $range = Select-P2Range -StartNeedle 'Final raster alt' -EndNeedle 'Final raster alt'
                if ([string]$range.actualSelection -cne 'Final raster alt') { throw "[$mode] The final image selected '$($range.actualSelection)'." }
                $plain = Copy-P2PlainSelection
                if ([string]$plain.plain -cne 'Final raster alt') { throw "[$mode] Ctrl+C on the final image copied '$($plain.plain)' instead of its alt text." }
                $markdown = Copy-P2MarkdownSelection -Range $range
                if ([string]$markdown.plain -cne "![Final raster alt]($png)") { throw "[$mode] Copy as Markdown of the final image returned '$($markdown.plain)'." }
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ viewport = $mode; linked = $linked[0].name; finalAlt = $peer.name; screenshot = (Save-Screenshot "screenshots\p2-raster-04-$mode.png") })
            }
        }
        finally { Exit-P2Mode }
        throw 'UNVERIFIED: Linked and final-image semantics, atomic selection and exact copy passed in both viewport modes; the P1-030 live image alt copy item stays open until its own qualification closes it.'
    }

    $gif = 'data:image/gif;base64,R0lGODdhMAAwAIEAAHgooAAAAAAAAAAAACwAAAAAMAAwAEAITwABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh2qMSAAOw=='
    Test-Case -Id 'P2-RASTER-05' -Name 'raster-navigation-replacement-and-decode-failure' -Requirement 'LazyImages fixtures reload after navigating away and back, replacing an image document shows only the new pixels, and an undecodable image shows the unavailable-image fallback.' -Body {
        $lazy = @('Square blue concentric-circle SVG fixture', 'Portrait purple-and-gold SVG fixture', 'Wide blue landscape SVG fixture', 'Wide teal icon-strip SVG fixture')
        Navigate-Sample -Key 'LazyImages' -RequiredText 'Lazy Image Loading'
        foreach ($alt in $lazy) { Assert-P2LoadedImage -Peer (Get-P2ImagePeer -AltText $alt) -AltText $alt }
        Navigate-Sample -Key 'Typography' -RequiredText 'The quick brown fox'
        Navigate-Sample -Key 'LazyImages' -RequiredText 'Lazy Image Loading'
        foreach ($alt in $lazy) { Assert-P2LoadedImage -Peer (Get-P2ImagePeer -AltText $alt) -AltText $alt }
        Set-TemporaryMarkdown -Markdown "# Raster replacement`n`n![Replacement image]($png)`n" -RequiredText 'Raster replacement'
        $first = Get-P2ImagePeer -AltText 'Replacement image'
        Assert-P2LoadedImage -Peer $first -AltText 'Replacement image'
        Set-TemporaryMarkdown -Markdown "# Raster replacement two`n`n![Replacement image]($gif)`n" -RequiredText 'Raster replacement two'
        $second = Get-P2ImagePeer -AltText 'Replacement image'
        Assert-P2LoadedImage -Peer $second -AltText 'Replacement image'
        $shot = Save-Screenshot 'screenshots\p2-raster-05-replacement.png'
        $sampler = Get-P2PixelSampler -Shot $shot
        try {
            $m = Measure-P2BlockImage -Sampler $sampler -Peer $second -ColumnOffset (4 * $dpi)
            $pixel = Get-P2Pixel -Sampler $sampler -ScreenX ($m.left + $m.width / 2.0) -ScreenY $m.row
        }
        finally { $sampler.bitmap.Dispose() }
        if (-not (Test-P2ColorNear -Actual $pixel -Expected @(120, 40, 160))) { throw "The replaced image shows ($($pixel.r),$($pixel.g),$($pixel.b)); the new GIF is purple." }
        Assert-P2Near -Actual $m.width -Expected (48 * $dpi) -What 'The replacement GIF width'
        Set-TemporaryMarkdown -Markdown "# Raster failure`n`n![Undecodable raster](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAA)`n" -RequiredText 'Raster failure'
        $failed = Wait-ForImagePeerState -AltText 'Undecodable raster' -TimeoutMilliseconds 8000
        if ([string]@($failed.images)[0].name -notmatch '^Image unavailable:\s*Undecodable raster$') { throw "The undecodable image did not show the unavailable fallback; Name='$(@($failed.images)[0].name)'." }
        return [pscustomobject]@{ lazyReloaded = $lazy.Count; replacementColor = $pixel; failure = @($failed.images)[0].name; screenshot = $shot }
    }

    # ── Syntax highlighting ──────────────────────────────────────────────────
    Test-Case -Id 'P2-HIGHLIGHT-01' -Name 'highlight-built-in-and-aliased-languages-color-tokens-without-changing-text' -Requirement 'The Code sample exposes every fenced block with its exact code text, and the C# and aliased TypeScript blocks show multiple token colors.' -Body {
        Navigate-Sample -Key 'Code' -RequiredText 'Fenced Code Blocks'
        $text = Get-P2Text
        foreach ($line in @('public sealed class MarkdownRendererControl : UserControl', 'type RenderState', 'This deliberately long line')) {
            if (-not $text.Contains($line)) { throw "The Code sample text lost '$line'." }
        }
        $blocks = @(Get-P2Elements -ClassName 'MarkdownCodeBlock')
        if ($blocks.Count -lt 7) { throw "The Code sample exposed $($blocks.Count) code blocks; it has 7 fenced blocks." }
        $observed = [System.Collections.Generic.List[object]]::new()
        foreach ($needle in @('public sealed class MarkdownRendererControl', 'type RenderState')) {
            $range = Scroll-P2Text -Needle $needle
            $block = Get-P2Elements -ClassName 'MarkdownCodeBlock' | Where-Object { -not [bool]$_.isOffscreen } | Sort-Object { [Math]::Abs([double]$_.y - [double]$range.rectangles[0].y) } | Select-Object -First 1
            $shot = Save-Screenshot ('screenshots\p2-highlight-01-{0}.png' -f ($observed.Count + 1))
            $sampler = Get-P2PixelSampler -Shot $shot
            try { $colors = Get-P2DistinctColorCount -Sampler $sampler -Rect (Get-P2VisibleRect -Rect $block -Sampler $sampler) -Columns 80 -Rows 20 }
            finally { $sampler.bitmap.Dispose() }
            if ($colors -lt 5) { throw "The '$needle' block shows only $colors distinct colors; its tokens are not highlighted." }
            $observed.Add([pscustomobject]@{ block = $needle; colors = $colors; screenshot = $shot })
        }
        return [pscustomobject]@{ codeBlocks = $blocks.Count; highlighted = @($observed.ToArray()) }
    }

    Test-Case -Id 'P2-HIGHLIGHT-02' -Name 'highlight-unknown-language-stays-plain-and-readable' -Requirement 'An unknown-language block keeps its exact readable code with no token colors while a highlighted block shows them; system High Contrast colors are checked by the follow-up runner.' -Body {
        Navigate-Sample -Key 'Code' -RequiredText 'Fenced Code Blocks'
        $source = "# Unknown language`n`n``````unknownlang`nplainCodeMarker = call(1, ""two"") // not a token`n```````n`n``````csharp`npublic sealed class KnownMarker { private int value = 1; }`n```````n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'Unknown language'
        Start-Sleep -Milliseconds 600
        $text = Get-P2Text
        if (-not $text.Contains('plainCodeMarker = call(1, "two") // not a token')) { throw 'The unknown-language code text changed.' }
        $blocks = @(Get-P2Elements -ClassName 'MarkdownCodeBlock' | Sort-Object { [double]$_.y })
        if ($blocks.Count -ne 2) { throw "Expected 2 code blocks, found $($blocks.Count)." }
        $shot = Save-Screenshot 'screenshots\p2-highlight-02.png'
        $sampler = Get-P2PixelSampler -Shot $shot
        try {
            $plainColors = Get-P2ChromaticPixelCount -Sampler $sampler -Rect $blocks[0]
            $knownColors = Get-P2ChromaticPixelCount -Sampler $sampler -Rect $blocks[1]
        }
        finally { $sampler.bitmap.Dispose() }
        if ($knownColors -lt 50) { throw "The highlighted C# block shows only $knownColors token-colored pixels." }
        if ($plainColors * 10 -ge $knownColors) { throw "The unknown-language block shows $plainColors token-colored pixels against the highlighted block's $knownColors; it is not plain." }
        throw "UNVERIFIED: The unknown-language block stays plain and exact ($plainColors vs $knownColors token-colored pixels); actual Windows system High Contrast colors are qualified by the follow-up runner."
    }

    Test-Case -Id 'P2-HIGHLIGHT-03' -Name 'highlight-selection-uia-range-and-copy-keep-exact-code' -Requirement 'Selecting highlighted code yields its exact text in the UIA range, Ctrl+C and Copy as Markdown.' -Body {
        Navigate-Sample -Key 'Code' -RequiredText 'Fenced Code Blocks'
        $null = Scroll-P2Text -Needle 'public sealed class MarkdownRendererControl'
        Clear-P2Selection
        $range = Select-P2Range -StartNeedle 'public sealed class' -EndNeedle 'UserControl'
        $expected = 'public sealed class MarkdownRendererControl : UserControl'
        if ([string]$range.actualSelection -cne $expected) { throw "The highlighted code selection is '$($range.actualSelection)'." }
        $plain = Copy-P2PlainSelection
        if ([string]$plain.plain -cne $expected) { throw "Ctrl+C copied '$($plain.plain)'." }
        $markdown = Copy-P2MarkdownSelection -Range $range
        # A selection that starts at a block's first rendered character projects
        # to the block's source start, so it includes the opening fence line, as
        # an unhighlighted block does. Highlighting must not change that slice.
        $source = [string](Invoke-UiValue 'MarkdownEditor')
        $fenceStart = $source.IndexOf('```csharp filename=', [System.StringComparison]::Ordinal)
        if ($fenceStart -lt 0) { throw 'The Code sample source has no csharp filename fence.' }
        $codeStart = $source.IndexOf($expected, $fenceStart, [System.StringComparison]::Ordinal)
        $expectedMarkdown = $source.Substring($fenceStart, $codeStart + $expected.Length - $fenceStart)
        if ([string]$markdown.plain -cne $expectedMarkdown) { throw "Copy as Markdown copied '$($markdown.plain)', not the projected source slice '$expectedMarkdown'." }
        Clear-P2Selection
        throw 'UNVERIFIED: Highlighted code selection, UIA range and copy are exact; the P1-020/P1-021 live formatted-paste items stay open until their own qualification closes them.'
    }

    Test-Case -Id 'P2-HIGHLIGHT-04' -Name 'highlight-theme-and-source-replacement-apply-no-stale-spans' -Requirement 'A theme change repaints the highlighted code with the same text, replacing the source shows only the new code, and replacing the grammar provider of a live document in both viewport modes repaints only the affected language.' -Body {
        try {
            Navigate-Sample -Key 'Code' -RequiredText 'Fenced Code Blocks'
            $range = Scroll-P2Text -Needle 'public sealed class MarkdownRendererControl'
            $block = Get-P2Elements -ClassName 'MarkdownCodeBlock' | Where-Object { -not [bool]$_.isOffscreen } | Sort-Object { [Math]::Abs([double]$_.y - [double]$range.rectangles[0].y) } | Select-Object -First 1
            $light = Save-Screenshot 'screenshots\p2-highlight-04-light.png'
            Set-ToggleState -AutomationId 'ThemeToggle' -Enabled $true
            Wait-ThemeState -Theme 'dark' -Contrast 'hc:off' -TextScale '1'
            Start-Sleep -Milliseconds 600
            if (-not (Get-P2Text).Contains('public sealed class MarkdownRendererControl : UserControl')) { throw 'The theme change altered the code text.' }
            $dark = Save-Screenshot 'screenshots\p2-highlight-04-dark.png'
            $a = Get-P2PixelSampler -Shot $light
            $b = Get-P2PixelSampler -Shot $dark
            try {
                $before = Get-P2RectPixel -Sampler $a -Rect $block -FractionX 0.98 -FractionY 0.5
                $after = Get-P2RectPixel -Sampler $b -Rect $block -FractionX 0.98 -FractionY 0.5
            }
            finally { $a.bitmap.Dispose(); $b.bitmap.Dispose() }
            if (Test-P2ColorNear -Actual $before -Expected @($after.r, $after.g, $after.b) -Tolerance 24) { throw 'The code block background did not change with the theme.' }
            Reset-P2Display
            Set-TemporaryMarkdown -Markdown "# Code replacement`n`n``````python`nreplacementOnly = 42`n```````n" -RequiredText 'Code replacement'
            $text = Get-P2Text
            if ($text.Contains('MarkdownRendererControl')) { throw 'Stale code from the previous document is still exposed.' }
            if (-not $text.Contains('replacementOnly = 42')) { throw 'The replacement code is missing.' }
        }
        finally { Reset-P2Display }
        $grammar = Invoke-P2GrammarReplacement
        return [pscustomobject]@{ themeAndSourceReplacement = $true; grammarReplacement = $grammar }
    }

    # ── Hosted elements ──────────────────────────────────────────────────────
    Test-Case -Id 'P2-HOSTED-01' -Name 'hosted-block-factory-creates-native-controls-and-unsupported-falls-back' -Requirement 'The Embeds sample block factory realizes native buttons, and an unsupported hosted request (null factory result) rebuilds its source block with the native Markdown renderer without a control.' -Body {
        Navigate-Sample -Key 'Embeds' -RequiredText 'Declarative Hosted Elements'
        $null = Scroll-P2Text -Needle 'extension-shaped fences below'
        $buttons = @(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -in @('Click me', 'Another action') })
        if ($buttons.Count -ne 2) { throw "Expected the two hosted native buttons, found $($buttons.Count)." }
        Set-TemporaryMarkdown -Markdown "# Hosted fallback`n`nBefore fallback.`n`n``````unsupported:Fallback_probe`nfallbackProbeSource`n```````n`nAfter fallback.`n" -RequiredText 'Hosted fallback'
        Start-Sleep -Milliseconds 600
        # Documented contract: a null factory result rebuilds that source block
        # with the native Markdown renderer, here a fenced code block.
        Assert-P2InOrder -Text (Get-P2Text) -Phrases @('Before fallback.', 'fallbackProbeSource', 'After fallback.')
        $codeBlocks = @(Get-P2Elements -ClassName 'MarkdownCodeBlock')
        if ($codeBlocks.Count -ne 1) { throw "The unsupported hosted request was not rebuilt as one native code block; found $($codeBlocks.Count)." }
        if (@(Get-P2Elements | Where-Object { [string]$_.className -like 'MarkdownHosted*' }).Count -gt 0) { throw 'The unsupported hosted request left a hosted peer.' }
        if (@(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -ceq 'Fallback probe' }).Count -gt 0) { throw 'The unsupported hosted request produced a control.' }
        return [pscustomobject]@{ nativeButtons = @($buttons | ForEach-Object { $_.name }); fallbackText = $true; screenshot = (Save-Screenshot 'screenshots\p2-hosted-01.png') }
    }

    Test-Case -Id 'P2-HOSTED-02' -Name 'hosted-null-throwing-and-attach-failed-factories-fall-back' -Requirement 'Null, throwing and attach-failed factory requests each rebuild their source block with the native Markdown renderer, the surrounding document keeps its order, and the app stays alive.' -Body {
        Navigate-Sample -Key 'Embeds' -RequiredText 'Declarative Hosted Elements'
        $source = "# Hosted failures`n`nBefore hosted.`n`n``````throw:Throwing_probe`nthrowingProbeSource`n```````n`nBetween one.`n`n``````attach-failed:Attach_probe`nattachProbeSource`n```````n`nBetween two.`n`n``````unsupported:Null_probe`nnullProbeSource`n```````n`nAfter hosted.`n"
        Set-TemporaryMarkdown -Markdown $source -RequiredText 'Hosted failures'
        Start-Sleep -Milliseconds 800
        # Documented contract: a null result, factory exception or attachment
        # failure rebuilds that source block with the native Markdown renderer.
        Assert-P2InOrder -Text (Get-P2Text) -Phrases @('Before hosted.', 'throwingProbeSource', 'Between one.', 'attachProbeSource', 'Between two.', 'nullProbeSource', 'After hosted.')
        $codeBlocks = @(Get-P2Elements -ClassName 'MarkdownCodeBlock')
        if ($codeBlocks.Count -ne 3) { throw "The three failed hosted requests were not each rebuilt as a native code block; found $($codeBlocks.Count)." }
        if (@(Get-P2Elements | Where-Object { [string]$_.className -like 'MarkdownHosted*' }).Count -gt 0) { throw 'A failed hosted request left a hosted peer.' }
        # A failed request is not retried on later viewport passes: the
        # rebuilt blocks stay stable while the document stays loaded.
        Start-Sleep -Milliseconds 1000
        if (@(Get-P2Elements -ClassName 'MarkdownCodeBlock').Count -ne 3) { throw 'The rebuilt fallback blocks changed while the document stayed loaded.' }
        $controls = @(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -in @('Throwing probe', 'Attach probe', 'Null probe') })
        if ($controls.Count -gt 0) { throw "A failed factory request left $($controls.Count) control(s) in the document." }
        $null = Assert-ProcessIdentity
        return [pscustomobject]@{ orderPreserved = $true; noFailedControls = $true; screenshot = (Save-Screenshot 'screenshots\p2-hosted-02.png') }
    }

    Test-Case -Id 'P2-HOSTED-03' -Name 'hosted-focus-touch-and-surrounding-copy-without-duplicate-text' -Requirement 'In both viewport modes a hosted button takes keyboard focus and activates, another activates by touch, and copying a selection around them includes each label exactly once.' -Body {
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Navigate-Sample -Key 'Typography' -RequiredText 'The quick brown fox'
                Enter-P2Mode -Mode $mode -Key 'Embeds' -RequiredText 'Declarative Hosted Elements'
                $null = Scroll-P2Text -Needle 'extension-shaped fences below'
                $click = @(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -ceq 'Click me' })
                if ($click.Count -ne 1) { throw "[$mode] The 'Click me' hosted button is not realized." }
                $clickId = Get-UiAutomationId $click[0]
                Clear-P2Selection
                $focused = $false
                for ($tab = 0; $tab -lt 16 -and -not $focused; $tab++) {
                    $null = Invoke-Ui @('send-keys', 'tab', '--via', 'send-input')
                    Start-Sleep -Milliseconds 80
                    $focused = [bool](Invoke-GuestProbe -Mode 'focus-state' -AutomationId $clickId).hasKeyboardFocus
                }
                if (-not $focused) { throw "[$mode] Keyboard Tab never focused the hosted 'Click me' button." }
                $null = Invoke-Ui @('send-keys', 'space', '--via', 'send-input')
                Wait-ForRawName -AutomationId $clickId -Expected 'Click me activated'
                $another = @(Show-P2Element -Description "[$mode] The 'Another action' hosted button" -Find {
                    $found = @(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -ceq 'Another action' })
                    if ($found.Count -gt 1) { throw "[$mode] Found $($found.Count) 'Another action' hosted buttons." }
                    if ($found.Count -eq 1) { $found[0] } else { $null }
                })
                $anotherId = Get-UiAutomationId $another[0]
                $center = '{0},{1}' -f [int]([double]$another[0].x + [double]$another[0].width / 2.0), [int]([double]$another[0].y + [double]$another[0].height / 2.0)
                $null = Invoke-Ui @('touch', 'MarkdownRenderer', '--gesture', 'tap', '--at', $center)
                Wait-ForRawName -AutomationId $anotherId -Expected 'Another action activated'
                Clear-P2Selection
                $range = Select-P2Range -StartNeedle 'extension-shaped fences below' -EndNeedle 'Task lists use'
                $plain = Copy-P2PlainSelection
                $copied = [string]$plain.plain
                foreach ($label in @('Click me', 'Another action')) {
                    $count = Get-P2Occurrences -Text $copied -Needle $label
                    if ($count -gt 1) { throw "[$mode] The copied selection contains '$label' $count times; hosted text must not duplicate." }
                }
                $markdown = [string](Copy-P2MarkdownSelection -Range $range).plain
                foreach ($fence in @('```button:Click_me', '```button:Another_action')) {
                    $count = Get-P2Occurrences -Text $markdown -Needle $fence
                    if ($count -ne 1) { throw "[$mode] Copy as Markdown contains '$fence' $count times; expected its source exactly once." }
                }
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ viewport = $mode; keyboardActivated = $true; touchActivated = $true; copiedOnce = $true; screenshot = (Save-Screenshot "screenshots\p2-hosted-03-$mode.png") })
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P2-HOSTED-04' -Name 'hosted-recycling-replacement-and-unload-leave-no-stale-focus' -Requirement 'Scrolling a long hosted document recycles controls within a bounded realized count; replacing or leaving the document, and replacing the hosted-control factory of a live document in both viewport modes, leave no stale hosted control or focus.' -Body {
        Navigate-Sample -Key 'Embeds' -RequiredText 'Declarative Hosted Elements'
        $builder = [System.Text.StringBuilder]::new()
        $null = $builder.Append("# Hosted recycling`n`n")
        for ($i = 1; $i -le 40; $i++) {
            $null = $builder.Append("Paragraph $i before its control.`n`n``````button:Recycled_$i`n```````n`n")
        }
        Set-TemporaryMarkdown -Markdown $builder.ToString() -RequiredText 'Hosted recycling'
        Start-Sleep -Milliseconds 600
        $counts = @()
        foreach ($position in @('top', 'bottom', 'top')) {
            $null = Invoke-Ui @('scroll', 'MarkdownRenderer', '--to', $position)
            Start-Sleep -Milliseconds 600
            $status = [string](Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'RealizedEmbedCount').name
            $realized = [int]($status -replace '^realized:', '')
            if ($realized -le 0 -or $realized -ge 40) { throw "At the $position the realized hosted-control count is $realized; recycling must keep it bounded and nonzero." }
            $counts += $realized
        }
        $first = @(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -ceq 'Recycled 1' })
        if ($first.Count -ne 1) { throw 'The first hosted control did not re-realize after scrolling back.' }
        $firstId = Get-UiAutomationId $first[0]
        $null = Invoke-Ui @('focus', $firstId)
        Set-TemporaryMarkdown -Markdown "# Hosted replaced`n`nNo hosted controls remain.`n" -RequiredText 'Hosted replaced'
        Start-Sleep -Milliseconds 400
        $stale = @(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -like 'Recycled*' })
        if ($stale.Count -gt 0) { throw "Replacing the document left $($stale.Count) stale hosted control(s)." }
        $afterStatus = [string](Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'RealizedEmbedCount').name
        if ($afterStatus -cne 'realized:0') { throw "After replacement the realized count is '$afterStatus'." }
        Navigate-Sample -Key 'Typography' -RequiredText 'The quick brown fox'
        Navigate-Sample -Key 'Embeds' -RequiredText 'Declarative Hosted Elements'
        $null = Scroll-P2Text -Needle 'extension-shaped fences below'
        if (@(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -ceq 'Click me' }).Count -ne 1) { throw 'The Embeds controls did not realize again after unload and return.' }
        $factory = Invoke-P2FactoryReplacement
        return [pscustomobject]@{ realizedCounts = $counts; documentReplacementClean = $true; factoryReplacement = $factory }
    }
}
