# Phase 1 live clipboard and selection cases (P1-020, P1-021, P1-030 and
# P1-033). The final-SVG selection and the P1-030 handle drag run as the
# existing C12 and C10 cases in the same batch.
#
# Dot-sourced by Invoke-MarkdownCorrectnessUiTests.ps1 -Phase1ClipboardOnly
# after MarkdownCorrectnessPhase2Cases.ps1, whose helpers it reuses. Each
# oracle comes from the record's own regression contract.

# Dismisses an open context menu and clears the selection with Escape
# without moving keyboard focus. Refocusing the renderer can fail while a
# code-block selection holds focus inside the block.
function Clear-P1Selection {
    $menu = @(Get-AppUiElements | Where-Object { (Get-UiAutomationId $_) -in @('MarkdownContextCopy', 'MarkdownContextCopyMarkdown', 'MarkdownContextSelectAll') })
    if ($menu.Count -gt 0) {
        $null = Invoke-Ui @('send-keys', 'esc', '--via', 'send-input')
        $null = Invoke-Ui @('wait-for', 'MarkdownContextSelectAll', '--gone', '-t', '2000')
    }
    $null = Invoke-Ui @('send-keys', 'esc', '--via', 'send-input')
}

# Selects the whole document through the renderer's own context menu, opened
# on visible text, so keyboard focus inside a child (such as a code block's
# copy button) cannot redirect Ctrl+A.
function Select-P1All {
    param([Parameter(Mandatory)][string]$Needle)
    Clear-P1Selection
    $point = Get-RangeCenter $Needle
    $at = '{0},{1}' -f [int]$point.x, [int]$point.y
    $null = Invoke-Ui @('drag', $at, $at, '--right', '--hold-ms', '40')
    $null = Invoke-Ui @('wait-for', 'MarkdownContextSelectAll', '-t', '3000')
    $null = Invoke-Ui @('invoke', 'MarkdownContextSelectAll')
    Start-Sleep -Milliseconds 200
}

function ConvertTo-P1Lf {
    param([AllowEmptyString()][string]$Text)
    return ($Text -replace "`r`n", "`n") -replace "`r", "`n"
}

# Finds the screen-space bounding box of pixels near one color inside a
# screen rectangle of a window screenshot.
function Find-P1ColorBounds {
    param([Parameter(Mandatory)]$Sampler, [Parameter(Mandatory)]$Rect, [Parameter(Mandatory)][int[]]$Color, [int]$Tolerance = 12)
    if (-not ('P1ColorBounds' -as [type])) {
        Add-Type -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, System.Drawing.Common -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class P1ColorBounds
{
    public static int[] Find(Bitmap bitmap, Rectangle area, int r, int g, int b, int tolerance)
    {
        area.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        if (area.Width <= 0 || area.Height <= 0) return new[] { -1, -1, -1, -1, 0 };
        BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[area.Width * 4];
            for (int y = 0; y < area.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < area.Width; x++)
                {
                    if (Math.Abs(row[x * 4 + 2] - r) <= tolerance && Math.Abs(row[x * 4 + 1] - g) <= tolerance && Math.Abs(row[x * 4] - b) <= tolerance)
                    {
                        count++;
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        if (count == 0) return new[] { -1, -1, -1, -1, 0 };
        return new[] { area.X + minX, area.Y + minY, area.X + maxX, area.Y + maxY, count };
    }
}
'@
    }
    $left = [int][Math]::Round(([double]$Rect.x - $Sampler.left) * $Sampler.scaleX)
    $top = [int][Math]::Round(([double]$Rect.y - $Sampler.top) * $Sampler.scaleY)
    $width = [int][Math]::Round([double]$Rect.width * $Sampler.scaleX)
    $height = [int][Math]::Round([double]$Rect.height * $Sampler.scaleY)
    $found = [P1ColorBounds]::Find($Sampler.bitmap, [System.Drawing.Rectangle]::new($left, $top, $width, $height), $Color[0], $Color[1], $Color[2], $Tolerance)
    if ($found[4] -le 0) { return $null }
    return [pscustomobject]@{
        x = $Sampler.left + $found[0] / $Sampler.scaleX
        y = $Sampler.top + $found[1] / $Sampler.scaleY
        width = ($found[2] - $found[0] + 1) / $Sampler.scaleX
        height = ($found[3] - $found[1] + 1) / $Sampler.scaleY
        pixels = $found[4]
    }
}

$script:P1PasteGuestScript = @'
param([Parameter(Mandatory)][string]$HostComputerName)
$ErrorActionPreference = 'Stop'
if ([string]::Equals([Environment]::MachineName, $HostComputerName, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to paste into a target outside the Sandbox guest.' }
Add-Type -AssemblyName System.Windows.Forms
$report = @{ status = 'error'; target = 'MSHTML editable document (WinForms WebBrowser)' }
$form = $null
try {
    $form = [System.Windows.Forms.Form]::new()
    $form.Text = 'P1-020 rich paste target'
    $form.Width = 900
    $form.Height = 500
    $browser = [System.Windows.Forms.WebBrowser]::new()
    $browser.Dock = 'Fill'
    $browser.ScriptErrorsSuppressed = $true
    $form.Controls.Add($browser)
    $form.Show()
    $browser.DocumentText = '<!doctype html><html><head><meta http-equiv="X-UA-Compatible" content="IE=edge"></head><body><div id="target" contenteditable="true" style="min-height:200px;border:1px solid #444"></div></body></html>'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (($browser.ReadyState -ne [System.Windows.Forms.WebBrowserReadyState]::Complete -or $null -eq $browser.Document -or $null -eq $browser.Document.GetElementById('target')) -and [DateTime]::UtcNow -lt $deadline) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 50
    }
    $target = $browser.Document.GetElementById('target')
    if ($null -eq $target) { $report.status = 'no-target'; return }
    $report.clipboardHasHtml = [System.Windows.Forms.Clipboard]::ContainsText([System.Windows.Forms.TextDataFormat]::Html)
    $target.Focus()
    [System.Windows.Forms.Application]::DoEvents()
    $null = $browser.Document.ExecCommand('Paste', $false, $null)
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $deadline) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
    $html = [string]$target.InnerHtml
    if ([string]::IsNullOrWhiteSpace($html)) { $report.status = 'no-paste'; return }
    $codeInLink = $false
    foreach ($anchor in $target.GetElementsByTagName('a')) {
        $inner = [string]$anchor.InnerHtml
        if ($inner -match '<code' -or $inner -match 'font-family:[^;"]*(monospace|consolas|cascadia)') { $codeInLink = $true }
    }
    $report.status = 'pasted'
    $report.result = (@{
        pastedText = [string]$target.InnerText
        pastedHtml = $html
        preCount = $target.GetElementsByTagName('pre').Count
        codeInLink = $codeInLink
    } | ConvertTo-Json -Compress)
}
catch { $report.status = 'error'; $report.detail = $_.Exception.Message }
finally {
    if ($null -ne $form) { $form.Close(); $form.Dispose() }
    $report | ConvertTo-Json -Compress -Depth 4
}
'@

function Run-Phase1ClipboardCases {
    Reset-P2Display

    Test-Case -Id 'P1-021' -Name 'authored-final-code-line-and-terminal-html-break-survive-copy' -Requirement 'In both viewport modes, Select All and Ctrl+C keep an authored final blank code line ("alpha\n") and an authored terminal HTML hard break ("Tail\n"), as both the selected UIA text and the copied plain text.' -Body {
        $fixtures = @(
            @{ name = 'terminal-code-line'; source = "``````text`nalpha`n`n```````n"; required = 'alpha'; expected = "alpha`n" },
            @{ name = 'terminal-html-break'; source = "<div>Tail<br><hr></div>`n"; required = 'Tail'; expected = "Tail`n" }
        )
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                foreach ($fixture in $fixtures) {
                    Enter-P2Mode -Mode $mode -Key 'Code' -RequiredText 'Fenced Code Blocks' -Markdown $fixture.source -MarkdownRequiredText $fixture.required
                    Start-Sleep -Milliseconds 400
                    Select-P1All -Needle $fixture.required
                    $selected = ConvertTo-P1Lf (Get-SelectedText)
                    if ($selected -cne $fixture.expected) { throw "[$mode/$($fixture.name)] Select All selected '$selected' ($($selected.Length) chars); expected '$($fixture.expected)'." }
                    $copied = Copy-P2PlainSelection
                    $plain = ConvertTo-P1Lf ([string]$copied.plain)
                    if ($plain -cne $fixture.expected) { throw "[$mode/$($fixture.name)] Ctrl+C copied '$plain' ($($plain.Length) chars); expected '$($fixture.expected)'." }
                    Clear-P1Selection
                    $observed.Add([pscustomobject]@{ viewport = $mode; fixture = $fixture.name; selected = $selected; copied = $plain })
                }
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P1-020' -Name 'rich-code-copy-pastes-whitespace-and-linked-code-into-a-rich-target' -Requirement 'Ctrl+C of code places CF_HTML with escaped pre/code blocks and pre-wrap inline code; pasting it with the target''s own Paste command into a real rich editing surface in the Sandbox (an MSHTML editable document) keeps tabs, indentation, repeated inline spaces and code styling nested in a link.' -Body {
        $source = "# Rich code copy`n`n``````text`n`ttabbed line`n    four-space line`n```````n`nInline ``left  right`` code.`n`n[``linked code``](https://example.invalid/linked-code)`n"
        try {
            Enter-P2Mode -Mode 'owned' -Key 'Code' -RequiredText 'Fenced Code Blocks' -Markdown $source -MarkdownRequiredText 'Rich code copy'
            Start-Sleep -Milliseconds 400
            Select-P1All -Needle 'Rich code copy'
            $copied = Copy-P2PlainSelection
            if (-not [bool]$copied.hasHtml) { throw 'Ctrl+C placed no CF_HTML fragment.' }
            $html = [string]$copied.html
            if ($html -notmatch '<pre[^>]*>\s*<code') { throw 'The CF_HTML fragment has no escaped <pre><code> block for the fenced code.' }
            if (-not $html.Contains("`ttabbed line") -or -not $html.Contains('    four-space line')) { throw 'The CF_HTML code block lost its tab or indentation.' }
            if ($html -notmatch 'white-space:\s*pre-wrap') { throw 'The CF_HTML inline code has no white-space: pre-wrap declaration.' }

            # The paste target lives inside one guest command: the Sandbox ends a
            # command's child processes when it returns. The guest script hosts
            # an MSHTML editable document, pastes with its own Paste command and
            # reports what it received. The script is written through exec as
            # base64 so a stalled file transfer cannot block the case.
            $guestScript = 'C:\WinApp\work\correctness\' + $script:RunId + '\p1-020-paste.ps1'
            [System.IO.File]::WriteAllText((Join-Path $script:RunDirectory 'p1-020-paste.ps1'), $script:P1PasteGuestScript, [System.Text.UTF8Encoding]::new($false))
            $payload = [Convert]::ToBase64String([System.Text.UTF8Encoding]::new($false).GetBytes($script:P1PasteGuestScript))
            $write = "New-Item -ItemType Directory -Force (Split-Path '$guestScript') | Out-Null; [IO.File]::WriteAllBytes('$guestScript', [Convert]::FromBase64String('$payload'))"
            $null = Invoke-NativeChecked @('target', 'exec', 'sandbox', '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-Command', $write)
            $result = Invoke-NativeChecked @('target', 'exec', 'sandbox', '--json', '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-STA',
                '-File', $guestScript, '-HostComputerName', [Environment]::MachineName) -Json
            $value = $result.value
            $text = $null
            # WinApp forwards the guest's JSON object directly; that object has
            # its own 'result' field, so recognize the report before looking
            # for an output envelope.
            if ($value.PSObject.Properties['status']) { $report = $value }
            else {
            foreach ($name in @('stdout', 'output', 'result')) {
                $property = $value.PSObject.Properties[$name]
                if ($null -ne $property -and $property.Value -is [string] -and $property.Value.Trim()) { $text = [string]$property.Value; break }
            }
            if ($null -eq $text) { if ($value.PSObject.Properties['status']) { $report = $value } else { throw "The guest paste script returned no JSON: $($result.output)" } }
            else {
                $match = [regex]::Match($text.Trim(), '(?s)(\{.*\})\s*$')
                if (-not $match.Success) { throw "The guest paste script wrote no JSON: $text" }
                $report = ConvertFrom-Json $match.Groups[1].Value
            }
            }
            if ([string]$report.status -cne 'pasted') { throw "The rich-target paste did not complete: $($report | ConvertTo-Json -Compress)" }
            $paste = ConvertFrom-Json ([string]$report.result)
            # Rich editors keep repeated spaces as non-breaking spaces; both
            # spaces must still be present.
            $pastedText = (ConvertTo-P1Lf ([string]$paste.pastedText)).Replace([char]0x00A0, ' ')
            if ([int]$paste.preCount -lt 1) { throw 'The pasted content in the rich target has no preformatted block.' }
            foreach ($needle in @("`ttabbed line", '    four-space line', 'left  right', 'linked code')) {
                if (-not $pastedText.Contains($needle)) { throw "The pasted text in the rich target lost '$($needle -replace "`t", '\t')'." }
            }
            if (-not [bool]$paste.codeInLink) { throw 'The pasted link in the rich target lost its nested code styling.' }
            return [pscustomobject]@{ clipboardHasPre = $true; clipboardPreWrap = $true; pasteTarget = $report.target; targetPreCount = [int]$paste.preCount; targetCodeInLink = $true; pastedText = $pastedText; pastedHtml = [string]$paste.pastedHtml }
        }
        finally { Exit-P2Mode }
    }

    $png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAAAoCAYAAABOzvzpAAAAVElEQVR42u3QAQ0AIAzAsB9VaEEgWnB1ZDyBzsCSZqxT0dme2bkf8XkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAODlLnVKA1BQMA6iAAAAAElFTkSuQmCC'
    Test-Case -Id 'P1-030' -Name 'final-image-plain-copy-is-its-alt-text' -Requirement 'In both viewport modes, a final-document image selects as its alt text and Ctrl+C copies exactly that alt text.' -Body {
        $source = "# Image copy`n`nBefore the final image.`n`n![Final raster alt]($png)`n"
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'Images' -RequiredText 'Images, captions, and SVG' -Markdown $source -MarkdownRequiredText 'Image copy'
                $peer = Get-P2ImagePeer -AltText 'Final raster alt'
                Assert-P2LoadedImage -Peer $peer -AltText 'Final raster alt'
                Clear-P2Selection
                $range = Select-P2Range -StartNeedle 'Final raster alt' -EndNeedle 'Final raster alt'
                if ([string]$range.actualSelection -cne 'Final raster alt') { throw "[$mode] The final image selected '$($range.actualSelection)'." }
                $plain = Copy-P2PlainSelection
                if ([string]$plain.plain -cne 'Final raster alt') { throw "[$mode] Ctrl+C on the final image copied '$($plain.plain)' instead of its alt text." }
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ viewport = $mode; copied = [string]$plain.plain })
            }
        }
        finally { Exit-P2Mode }
        return @($observed.ToArray())
    }

    Test-Case -Id 'P1-033' -Name 'wrapped-caption-image-lower-artwork-reaches-after-boundary' -Requirement 'In both viewport modes, for a 96x96 image whose wrapped caption makes its block much taller than the artwork, dragging the selection end handle into the lower half of the visible artwork (located from screenshot pixels) selects through the image, and Ctrl+C copies the boundary word and the alt text.' -Body {
        $boundary = 'WrappedCaptionBoundaryUnique'
        $alt = (@(1..400 | ForEach-Object { 'caption' }) -join ' ')
        $svg = 'data:image/svg+xml;base64,' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes('<svg xmlns="http://www.w3.org/2000/svg" width="96" height="96"><rect width="96" height="96" fill="#1570ef"/></svg>'))
        $source = "$boundary`n`n![$alt]($svg)`n`nAfterCaptionParagraphUnique`n"
        $expected = "$boundary`n$alt"
        $observed = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'TouchSelection' -RequiredText 'Touch selection' -Markdown $source -MarkdownRequiredText $boundary
                Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled ($mode -eq 'ancestor')
                $state = Wait-ForImagePeerState -AltText $alt -TimeoutMilliseconds 8000
                $peer = @($state.images | Where-Object { [string]$_.name -ceq $alt })
                if ($peer.Count -ne 1 -or [bool]$peer[0].offscreen) { throw "[$mode] The captioned image is not one loaded, visible peer." }
                $peer = $peer[0]
                $shot = Save-Screenshot "screenshots\p1-033-before-$mode.png"
                $sampler = Get-P2PixelSampler -Shot $shot
                try { $artwork = Find-P1ColorBounds -Sampler $sampler -Rect $peer -Color @(21, 112, 239) }
                finally { $sampler.bitmap.Dispose() }
                if ($null -eq $artwork) { throw "[$mode] The SVG artwork is not visible inside its image peer." }
                $scale = [double](Invoke-GuestProbe -Mode 'metadata').dpiScale
                if ([Math]::Abs($artwork.width - 96 * $scale) -gt 3 -or [Math]::Abs($artwork.height - 96 * $scale) -gt 3) { throw "[$mode] The visible artwork is $($artwork.width)x$($artwork.height) px, not 96x96 DIPs." }
                if (($artwork.y + $artwork.height) -ge ([double]$peer.y + [double]$peer.height / 2.0)) { throw "[$mode] Fixture invalid: the caption does not push the image block midpoint below the artwork." }

                $boundaryPoint = Get-RangeCenter $boundary
                $null = Invoke-Ui @('touch', 'MarkdownRenderer', '--gesture', 'double-tap', '--at', "$($boundaryPoint.x),$($boundaryPoint.y)")
                $deadline = [DateTime]::UtcNow.AddSeconds(4)
                do { Start-Sleep -Milliseconds 80; $initial = Get-SelectedText } while ($initial -cne $boundary -and [DateTime]::UtcNow -lt $deadline)
                if ($initial -cne $boundary) { throw "[$mode] Could not select the boundary word; selected '$initial'." }
                $handles = Assert-SelectionHandles
                $endHandle = @($handles | Where-Object { (Get-UiAutomationId $_) -ceq 'MarkdownSelectionEndHandle' })
                $selection = Invoke-GuestProbe -Mode 'selection'
                $wordRect = $selection.ranges[0].rectangles[0]
                $finger = [pscustomobject]@{ x = [double]$endHandle[0].x + [double]$endHandle[0].width / 2.0; y = [double]$endHandle[0].y + [double]$endHandle[0].height / 2.0 }
                $caret = [pscustomobject]@{ x = [double]$wordRect.x + [double]$wordRect.width; y = [double]$wordRect.y + [double]$wordRect.height }
                $destination = Resolve-VectorHandleDestination -ObjectX $artwork.x -ObjectY $artwork.y -ObjectWidth $artwork.width -ObjectHeight $artwork.height `
                    -FingerToCaretOffsetX ($finger.x - $caret.x) -FingerToCaretOffsetY ($finger.y - $caret.y)
                if ($destination.effectiveCaret.y -lt ($artwork.y + $artwork.height / 2.0)) { throw "[$mode] The computed effective caret is not in the artwork's lower half." }
                $null = Invoke-Ui @('touch', 'MarkdownSelectionEndHandle', '--gesture', 'swipe', '--to-point', "$($destination.target.x),$($destination.target.y)", '--duration-ms', '350')
                $deadline = [DateTime]::UtcNow.AddSeconds(3)
                do { Start-Sleep -Milliseconds 80; $selected = Get-SelectedText } while ($selected -cne $expected -and [DateTime]::UtcNow -lt $deadline)
                if ($selected -cne $expected) { throw "[$mode] Dragging into the lower half of the artwork selected '$selected'; expected the boundary word and the image alt text." }
                $null = Assert-SelectionHandles
                $copied = Copy-P2PlainSelection
                if ((ConvertTo-P1Lf ([string]$copied.plain)) -cne $expected) { throw "[$mode] Ctrl+C copied '$($copied.plain)' after selecting through the image." }
                $after = Save-Screenshot "screenshots\p1-033-selected-$mode.png"
                Clear-P2Selection
                $observed.Add([pscustomobject]@{ viewport = $mode; artwork = $artwork; peer = @{ x = $peer.x; y = $peer.y; width = $peer.width; height = $peer.height }; target = $destination.target; effectiveCaret = $destination.effectiveCaret; selectedThroughImage = $true; screenshots = @($shot, $after) })
            }
        }
        finally {
            Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled $false
            Exit-P2Mode
        }
        return @($observed.ToArray())
    }
}
