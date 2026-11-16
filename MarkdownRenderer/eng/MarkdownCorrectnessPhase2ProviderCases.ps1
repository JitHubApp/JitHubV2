# Phase 2 provider-configuration checks on the Sample "Providers" page.
#
# Dot-sourced by Invoke-MarkdownCorrectnessUiTests.ps1 -Phase2Only next to
# MarkdownCorrectnessPhase2Cases.ps1. The page swaps the engine, grammar
# provider and hosted-control factory on a live document, removes every
# optional provider, and adds a second view that shares the providers. Its
# viewport-ownership toggle covers both viewport modes. Leaving the page
# restores the standard configuration.

$script:P2ProviderFullMath = 2
$script:P2ProviderEndText = 'Provider page end.'

function Enter-P2ProvidersPage {
    param([Parameter(Mandatory)][ValidateSet('owned', 'ancestor')][string]$Mode)
    Navigate-Sample -Key 'Providers' -RequiredText 'Provider configuration'
    Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($Mode -eq 'ancestor')
    $null = Wait-ForRendererText -Text $script:P2ProviderEndText
    $null = Wait-P2ProviderSnapshot -What "[$Mode] the Providers page with every provider" -Condition { param($s) Test-P2FullProviders $s }
}

function Get-P2ProviderStatus {
    return [string](Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'ProviderStatus').name
}

function Wait-P2ProviderStatus {
    param([Parameter(Mandatory)][string]$Contains, [int]$TimeoutSeconds = 8)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $status = ''
    do {
        $status = Get-P2ProviderStatus
        if ($status.Contains($Contains)) { return $status }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The Sample provider status did not reach '$Contains'; it is '$status'."
}

function Get-P2EngineGeneration {
    $status = Get-P2ProviderStatus
    if ($status -notmatch 'engine:(\d+);') { throw "The provider status '$status' has no engine generation." }
    return [int]$Matches[1]
}

function Get-P2RootElements {
    param([Parameter(Mandatory)][string]$Root)
    $result = Invoke-Ui @('inspect', $Root, '--depth', '12')
    if ($null -eq $result.value.windows) { throw "The $Root inspection has no windows collection." }
    return @(Expand-UiElements @($result.value.windows | ForEach-Object { $_.elements }))
}

# Counts the provider-backed content one view exposes.
function Get-P2ProviderSnapshot {
    param([string]$Root = 'MarkdownRenderer')
    $elements = @(Get-P2RootElements -Root $Root)
    $count = { param($className) @($elements | Where-Object { [string]$_.className -ceq $className }).Count }
    $buttons = @($elements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -like 'Provider action*' } | ForEach-Object { [string]$_.name })
    $images = @($elements | Where-Object { [string]$_.className -ceq 'MarkdownImage' -and [string]$_.name -like '*Provider SVG probe*' } | ForEach-Object { [string]$_.name })
    return [pscustomobject]@{
        root = $Root
        text = [string](Invoke-UiValue $Root)
        math = & $count 'MarkdownMath'
        diagrams = & $count 'MarkdownDiagram'
        disclosures = & $count 'MarkdownDisclosure'
        codeBlocks = & $count 'MarkdownCodeBlock'
        hostedGroups = @($elements | Where-Object { [string]$_.className -like 'MarkdownHosted*' } | ForEach-Object { [string]$_.name })
        buttons = $buttons
        images = $images
    }
}

function Wait-P2ProviderSnapshot {
    param([Parameter(Mandatory)][string]$What, [Parameter(Mandatory)][scriptblock]$Condition, [string]$Root = 'MarkdownRenderer', [string]$EndNeedle = $script:P2ProviderEndText, [int]$TimeoutSeconds = 15)
    # Images load near the viewport; bring the end of the page into view so
    # the SVG probe near the bottom is realized in every view.
    if ($Root -eq 'MarkdownRenderer') { $null = Scroll-P2Text -Needle $EndNeedle }
    else { $null = Invoke-Ui @('scroll', $Root, '--to', 'bottom') }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $snapshot = $null
    do {
        $snapshot = Get-P2ProviderSnapshot -Root $Root
        if (& $Condition $snapshot) { return $snapshot }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$What did not settle: math=$($snapshot.math) diagrams=$($snapshot.diagrams) disclosures=$($snapshot.disclosures) codeBlocks=$($snapshot.codeBlocks) hosted='$($snapshot.hostedGroups -join '|')' buttons='$($snapshot.buttons -join '|')' images='$($snapshot.images -join '|')'."
}

# Every provider present: native HTML disclosure, both formulas, the native
# diagram, two highlighted code blocks, the loaded SVG and the hosted button.
function Test-P2FullProviders {
    param([Parameter(Mandatory)]$Snapshot)
    return ($Snapshot.math -eq $script:P2ProviderFullMath -and
        $Snapshot.diagrams -eq 1 -and
        $Snapshot.disclosures -eq 1 -and
        $Snapshot.codeBlocks -eq 2 -and
        @($Snapshot.images) -contains 'Provider SVG probe' -and
        (Get-P2Occurrences -Text $Snapshot.text -Needle 'Provider HTML summary') -eq 1 -and
        $Snapshot.text.Contains($script:P2ProviderEndText))
}

function Assert-P2FullProviders {
    param([Parameter(Mandatory)]$Snapshot, [Parameter(Mandatory)][string]$Where)
    if (-not (Test-P2FullProviders $Snapshot)) {
        throw "$Where does not show every provider: math=$($Snapshot.math) diagrams=$($Snapshot.diagrams) disclosures=$($Snapshot.disclosures) codeBlocks=$($Snapshot.codeBlocks) images='$($Snapshot.images -join '|')'."
    }
}

# Highlighting is visible only as color. Count the token-colored pixels of
# the code block that contains the needle.
function Measure-P2CodeColors {
    param([Parameter(Mandatory)][string]$Needle, [Parameter(Mandatory)][string]$ShotName)
    $range = Scroll-P2Text -Needle $Needle
    $line = $range.rectangles[0]
    $block = @(Get-P2Elements -ClassName 'MarkdownCodeBlock' | Where-Object {
            -not [bool]$_.isOffscreen -and [double]$_.y -le [double]$line.y -and ([double]$_.y + [double]$_.height) -ge ([double]$line.y + [double]$line.height) }) | Select-Object -First 1
    if ($null -eq $block) { throw "No visible code block contains '$Needle'." }
    $shot = Save-Screenshot $ShotName
    $sampler = Get-P2PixelSampler -Shot $shot
    try { $colors = Get-P2ChromaticPixelCount -Sampler $sampler -Rect $block }
    finally { $sampler.bitmap.Dispose() }
    return [pscustomobject]@{ colors = $colors; block = $block; screenshot = $shot }
}

function Invoke-P2ReplaceEngine {
    $generation = Get-P2EngineGeneration
    $null = Invoke-Ui @('invoke', 'ProviderReplaceEngine')
    $null = Wait-P2ProviderStatus -Contains "engine:$($generation + 1);"
    return $generation + 1
}

# P2-HTML-04, P2-MATH-04 and P2-MERMAID-04: replacing the engine of a live
# view re-renders the current document once, and a replacement document
# followed by another engine replacement shows only that document.
function Invoke-P2EngineReplacement {
    param([Parameter(Mandatory)][ValidateSet('html', 'math', 'mermaid')][string]$Feature)
    $replacement = switch ($Feature) {
        'html' { "# Engine replacement`n`n<details>`n<summary>Replacement summary only</summary>`n`nReplacement body.`n`n</details>`n`nReplacement end.`n" }
        'math' { "# Engine replacement`n`nOnly `$r_{engine}`$ remains.`n`nReplacement end.`n" }
        'mermaid' { "# Engine replacement`n`n``````mermaid`nflowchart LR`n  replacementOnly[Replacement node only]`n```````n`nReplacement end.`n" }
    }
    $isCurrent = switch ($Feature) {
        'html' { { param($s) $s.disclosures -eq 1 -and (Get-P2Occurrences -Text $s.text -Needle 'Replacement summary only') -eq 1 -and -not $s.text.Contains('Provider HTML summary') } }
        'math' { { param($s) $s.math -eq 1 -and -not $s.text.Contains('Inline provider math') } }
        'mermaid' { { param($s) $s.diagrams -eq 1 -and -not $s.text.Contains('ProviderStart') -and -not $s.text.Contains('Highlighted code') } }
    }
    $observed = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($mode in $script:P2ViewportModes) {
            Enter-P2ProvidersPage -Mode $mode
            $first = Invoke-P2ReplaceEngine
            $after = Wait-P2ProviderSnapshot -What "[$mode] the Providers page after engine replacement $first" -Condition { param($s) Test-P2FullProviders $s }
            Set-TemporaryMarkdown -Markdown $replacement -RequiredText 'Replacement end.'
            $null = Wait-P2ProviderSnapshot -What "[$mode] the $Feature replacement document" -Condition $isCurrent -EndNeedle 'Replacement end.'
            $second = Invoke-P2ReplaceEngine
            Start-Sleep -Milliseconds 800
            $current = Wait-P2ProviderSnapshot -What "[$mode] the $Feature replacement document after engine replacement $second" -Condition $isCurrent -EndNeedle 'Replacement end.'
            if ($Feature -eq 'mermaid') {
                $node = @(Get-P2Elements -ClassName 'MarkdownDiagramNode' | Where-Object { [string]$_.name -ceq 'Replacement node only' })
                if ($node.Count -ne 1) { throw "[$mode] After engine replacement the diagram does not show the current document's node." }
            }
            $observed.Add([pscustomobject]@{ viewport = $mode; engineGenerations = @($first, $second); fullAfterReplacement = $true; currentOnly = $true; math = $current.math; diagrams = $current.diagrams; disclosures = $current.disclosures; screenshot = (Save-Screenshot "screenshots\p2-$Feature-04-engine-$mode.png") })
        }
    }
    finally { Exit-P2Mode }
    return @($observed.ToArray())
}

# P2-HIGHLIGHT-04: replacing the grammar provider on a live document repaints
# only the affected language and keeps every code character.
function Invoke-P2GrammarReplacement {
    $csharp = 'public sealed class ProviderProbe : IDisposable { }'
    $python = 'provider_probe = 42'
    $observed = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($mode in $script:P2ViewportModes) {
            Enter-P2ProvidersPage -Mode $mode
            $before = Measure-P2CodeColors -Needle 'public sealed class ProviderProbe' -ShotName "screenshots\p2-highlight-04-grammar-$mode-before.png"
            $pythonBefore = Measure-P2CodeColors -Needle $python -ShotName "screenshots\p2-highlight-04-grammar-$mode-python-before.png"
            Set-ToggleState -AutomationId 'ProviderWithoutCSharpGrammar' -Enabled $true
            $null = Wait-P2ProviderStatus -Contains 'grammar:no-csharp'
            Start-Sleep -Milliseconds 1200
            $plain = Measure-P2CodeColors -Needle 'public sealed class ProviderProbe' -ShotName "screenshots\p2-highlight-04-grammar-$mode-plain.png"
            $pythonPlain = Measure-P2CodeColors -Needle $python -ShotName "screenshots\p2-highlight-04-grammar-$mode-python-plain.png"
            $text = Get-P2Text
            if ((Get-P2Occurrences -Text $text -Needle $csharp) -ne 1 -or (Get-P2Occurrences -Text $text -Needle $python) -ne 1) { throw "[$mode] Replacing the grammar provider changed the code text." }
            if ($before.colors -lt 50 -or $pythonBefore.colors -lt 20) { throw "[$mode] The code is not highlighted before the grammar replacement (C# $($before.colors), Python $($pythonBefore.colors) token-colored pixels)." }
            if ($plain.colors * 10 -ge $before.colors) { throw "[$mode] The C# block kept $($plain.colors) token-colored pixels (was $($before.colors)) after its grammar was removed; stale highlight spans remain." }
            if ($pythonPlain.colors * 2 -lt $pythonBefore.colors) { throw "[$mode] The Python block lost its highlighting ($($pythonBefore.colors) -> $($pythonPlain.colors)) when only the C# grammar was removed." }
            Set-ToggleState -AutomationId 'ProviderWithoutCSharpGrammar' -Enabled $false
            $null = Wait-P2ProviderStatus -Contains 'grammar:common'
            Start-Sleep -Milliseconds 1200
            $restored = Measure-P2CodeColors -Needle 'public sealed class ProviderProbe' -ShotName "screenshots\p2-highlight-04-grammar-$mode-restored.png"
            if ($restored.colors * 2 -lt $before.colors) { throw "[$mode] Restoring the C# grammar did not re-highlight the block ($($before.colors) -> $($plain.colors) -> $($restored.colors))." }
            $observed.Add([pscustomobject]@{ viewport = $mode; csharpColors = @($before.colors, $plain.colors, $restored.colors); pythonColors = @($pythonBefore.colors, $pythonPlain.colors) })
        }
    }
    finally { Exit-P2Mode }
    return @($observed.ToArray())
}

# P2-HOSTED-04: replacing the hosted-control factory of a live document
# replaces each control exactly once, and keyboard focus reaches and
# activates the replacement rather than a retired control.
function Invoke-P2FactoryReplacement {
    $observed = [System.Collections.Generic.List[object]]::new()
    $buttons = { param($name) @(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -ceq $name }) }
    try {
        foreach ($mode in $script:P2ViewportModes) {
            Enter-P2ProvidersPage -Mode $mode
            $null = Scroll-P2Text -Needle $script:P2ProviderEndText
            $original = @(& $buttons 'Provider action')
            if ($original.Count -ne 1) { throw "[$mode] The default factory did not realize one 'Provider action' button." }
            $null = Invoke-Ui @('focus', (Get-UiAutomationId $original[0]))
            Set-ToggleState -AutomationId 'ProviderAlternateFactory' -Enabled $true
            $null = Wait-P2ProviderStatus -Contains 'factory:alternate'
            $deadline = [DateTime]::UtcNow.AddSeconds(8)
            do {
                $alternate = @(& $buttons 'Provider action (alternate)')
                $stale = @(& $buttons 'Provider action')
                if ($alternate.Count -eq 1 -and $stale.Count -eq 0) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
            if ($alternate.Count -ne 1 -or $stale.Count -ne 0) { throw "[$mode] Factory replacement left $($stale.Count) default and $($alternate.Count) alternate controls; expected only one alternate." }
            $alternateId = Get-UiAutomationId $alternate[0]
            Clear-P2Selection
            $focused = $false
            for ($tab = 0; $tab -lt 20 -and -not $focused; $tab++) {
                $null = Invoke-Ui @('send-keys', 'tab', '--via', 'send-input')
                Start-Sleep -Milliseconds 80
                $focused = [bool](Invoke-GuestProbe -Mode 'focus-state' -AutomationId $alternateId).hasKeyboardFocus
            }
            if (-not $focused) { throw "[$mode] Keyboard Tab never reached the replacement hosted control." }
            $null = Invoke-Ui @('send-keys', 'space', '--via', 'send-input')
            Wait-ForRawName -AutomationId $alternateId -Expected 'Provider action (alternate) activated'
            Set-ToggleState -AutomationId 'ProviderAlternateFactory' -Enabled $false
            $null = Wait-P2ProviderStatus -Contains 'factory:default'
            $deadline = [DateTime]::UtcNow.AddSeconds(8)
            do {
                $restored = @(& $buttons 'Provider action')
                $leftover = @(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -like 'Provider action (alternate)*' })
                if ($restored.Count -eq 1 -and $leftover.Count -eq 0) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
            if ($restored.Count -ne 1 -or $leftover.Count -ne 0) { throw "[$mode] Restoring the default factory left $($leftover.Count) alternate and $($restored.Count) default controls." }
            $observed.Add([pscustomobject]@{ viewport = $mode; replacedOnce = $true; replacementFocusedAndActivated = $true; restored = $true; screenshot = (Save-Screenshot "screenshots\p2-hosted-04-factory-$mode.png") })
        }
    }
    finally { Exit-P2Mode }
    return @($observed.ToArray())
}

function Run-Phase2ProviderCases {
    Test-Case -Id 'P2-CONFIG-01' -Name 'config-registered-providers-render-and-missing-providers-fall-back' -Requirement 'In both viewport modes the Providers page renders native HTML, Math, Mermaid, highlighted code, SVG and a hosted control with every provider registered; with strict GFM and no optional providers each one keeps its documented safe-text or original-source fallback; restoring the providers renders them again.' -Body {
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2ProvidersPage -Mode $mode
                $full = Get-P2ProviderSnapshot
                Assert-P2FullProviders -Snapshot $full -Where "[$mode] The full configuration"
                if (@($full.buttons) -notcontains 'Provider action') { throw "[$mode] The hosted factory did not realize the 'Provider action' button." }
                $fullCode = Measure-P2CodeColors -Needle 'public sealed class ProviderProbe' -ShotName "screenshots\p2-config-01-$mode-full.png"
                Set-ToggleState -AutomationId 'ProviderMinimal' -Enabled $true
                $null = Wait-P2ProviderStatus -Contains 'minimal:on'
                $null = Wait-ForRendererText -Text $script:P2ProviderEndText
                $minimal = Wait-P2ProviderSnapshot -What "[$mode] The minimal configuration" -Condition { param($s) $s.math -eq 0 -and $s.diagrams -eq 0 -and $s.disclosures -eq 0 -and $s.text.Contains('ProviderStart --> ProviderEnd') }
                foreach ($source in @('$a^2 + b^2 = c^2$', 'ProviderStart --> ProviderEnd', 'Provider HTML summary', 'public sealed class ProviderProbe : IDisposable { }')) {
                    if (-not $minimal.text.Contains($source)) { throw "[$mode] Without its provider, '$source' is not kept as safe text or original source." }
                }
                if (@($minimal.hostedGroups).Count -gt 0 -or @($minimal.buttons).Count -gt 0) { throw "[$mode] Without a hosted factory or extension the page still exposes hosted content: '$($minimal.hostedGroups -join '|')' '$($minimal.buttons -join '|')'." }
                if (@(Get-AppUiElements | Where-Object { [string]$_.type -ceq 'Button' -and [string]$_.name -like 'Provider action*' }).Count -gt 0) { throw "[$mode] A hosted control survived the minimal configuration." }
                $svg = Get-P2ImagePeer -AltText 'Provider SVG probe'
                if ([string]$svg.name -notlike '*Provider SVG probe*' -or [string]$svg.name -ceq 'Provider SVG probe') { throw "[$mode] Without an SVG provider the image is '$($svg.name)', not its accessible fallback." }
                if ([double]$svg.width -le 0 -or [double]$svg.height -le 0) { throw "[$mode] The SVG fallback has no visible geometry." }
                $minimalCode = Measure-P2CodeColors -Needle 'public sealed class ProviderProbe' -ShotName "screenshots\p2-config-01-$mode-minimal.png"
                if ($fullCode.colors -lt 50 -or $minimalCode.colors * 10 -ge $fullCode.colors) { throw "[$mode] Code without a highlighter shows $($minimalCode.colors) token-colored pixels against the highlighted $($fullCode.colors)." }
                Set-ToggleState -AutomationId 'ProviderMinimal' -Enabled $false
                $null = Wait-P2ProviderStatus -Contains 'minimal:off'
                $restored = Wait-P2ProviderSnapshot -What "[$mode] The restored configuration" -Condition { param($s) Test-P2FullProviders $s }
                $observed.Add([pscustomobject]@{ viewport = $mode; full = $full | Select-Object math, diagrams, disclosures, codeBlocks, images, buttons; minimal = $minimal | Select-Object math, diagrams, disclosures, codeBlocks, images, hostedGroups; svgFallback = [string]$svg.name; codeColors = @($fullCode.colors, $minimalCode.colors); restored = $true })
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P2-CONFIG-02' -Name 'config-two-views-share-providers-and-survive-replacement-and-disposal' -Requirement 'Starting in each viewport mode, two live views share one engine and set of providers; replacing and disposing the first view, replacing the shared engine, and disposing the second view each leave the remaining view complete and interactive.' -Body {
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2ProvidersPage -Mode $mode
                Set-ToggleState -AutomationId 'ProviderSecondView' -Enabled $true
                $null = Wait-P2ProviderStatus -Contains 'second:on'
                $second = Wait-P2ProviderSnapshot -Root 'SecondMarkdownRenderer' -What "[$mode] The second view" -Condition { param($s) Test-P2FullProviders $s }
                Assert-P2FullProviders -Snapshot (Get-P2ProviderSnapshot) -Where "[$mode] The first view beside the second"

                # The viewport toggle replaces the first view with a new
                # control of the other ownership mode and disposes the old one.
                $other = if ($mode -eq 'owned') { 'ancestor' } else { 'owned' }
                Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($other -eq 'ancestor')
                $null = Wait-ForRendererText -Text $script:P2ProviderEndText
                $null = Wait-P2ProviderSnapshot -What "[$mode] The replacement first view" -Condition { param($s) Test-P2FullProviders $s }
                $secondAfter = Get-P2ProviderSnapshot -Root 'SecondMarkdownRenderer'
                Assert-P2FullProviders -Snapshot $secondAfter -Where "[$mode] The second view after the first was replaced and disposed"
                if ($secondAfter.text -cne $second.text) { throw "[$mode] Replacing the first view changed the second view's document." }

                $generation = Invoke-P2ReplaceEngine
                $null = Wait-P2ProviderSnapshot -What "[$mode] The first view after shared engine replacement $generation" -Condition { param($s) Test-P2FullProviders $s }
                $null = Wait-P2ProviderSnapshot -Root 'SecondMarkdownRenderer' -What "[$mode] The second view after shared engine replacement $generation" -Condition { param($s) Test-P2FullProviders $s }
                $shot = Save-Screenshot "screenshots\p2-config-02-$mode-two-views.png"

                Set-ToggleState -AutomationId 'ProviderSecondView' -Enabled $false
                $null = Wait-P2ProviderStatus -Contains 'second:off'
                if (@(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -ceq 'SecondMarkdownRenderer' }).Count -gt 0) { throw "[$mode] The disposed second view is still in the UIA tree." }
                $remaining = Get-P2ProviderSnapshot
                Assert-P2FullProviders -Snapshot $remaining -Where "[$mode] The first view after the second was disposed"
                Clear-P2Selection
                $null = Scroll-P2Text -Needle $script:P2ProviderEndText
                $null = Select-P2Range -StartNeedle $script:P2ProviderEndText -EndNeedle $script:P2ProviderEndText
                $copied = Copy-P2PlainSelection
                if ([string]$copied.plain -cne $script:P2ProviderEndText) { throw "[$mode] The remaining view copied '$($copied.plain)' after the second view was disposed." }
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ startViewport = $mode; replacedFirstViewport = $other; engineGeneration = $generation; secondViewKeptDocument = $true; remainingViewInteractive = $true; screenshot = $shot })
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }
}
