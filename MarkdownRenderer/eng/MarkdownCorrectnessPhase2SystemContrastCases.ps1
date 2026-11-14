# Phase 2 checks under the actual Windows High Contrast setting of the Sandbox
# guest (P2-MERMAID-03, P2-HIGHLIGHT-02).
#
# Dot-sourced by Invoke-MarkdownCorrectnessUiTests.ps1 -Phase2Only
# -Phase2SystemHighContrast. Each case turns the guest's real contrast theme on
# with SystemParametersInfo, keeps the current scheme, and restores the exact
# original flags and scheme in finally. The guest helper refuses to run on the
# host; no host display setting is read or changed.
#
# Oracle: the documented contract maps every renderer color onto the system
# roles WindowText, Window, Hotlight, Highlight and HighlightText
# (theming-and-customization.md, MarkdownHighContrastDefaults). The guest's
# GetSysColor values give those roles independently of the app, and every
# rendered pixel of a diagram or code block must be one of them or an
# anti-aliasing blend of two of them.

$script:P2ContrastGuestPath = ''
$script:P2ContrastOriginal = $null

$script:P2ContrastGuestSource = @'
param(
    [Parameter(Mandatory)][ValidateSet('read', 'set', 'colors')][string]$Mode,
    [Parameter(Mandatory)][string]$HostComputerName,
    [uint32]$Flags = 0,
    [string]$Scheme = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($HostComputerName) -or [string]::Equals([Environment]::MachineName, $HostComputerName, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to read or change contrast settings outside the Sandbox guest.'
}
if (-not ('P2GuestContrast' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct P2HighContrast { public uint Size; public uint Flags; public IntPtr DefaultScheme; }
public static class P2GuestContrast {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint parameter, ref P2HighContrast value, uint flags);
    [DllImport("user32.dll")] static extern uint GetSysColor(int index);
    public static P2HighContrast Read() {
        var v = new P2HighContrast(); v.Size = (uint)Marshal.SizeOf<P2HighContrast>();
        if (!SystemParametersInfo(0x0042, v.Size, ref v, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return v;
    }
    public static string Scheme(P2HighContrast v) { return v.DefaultScheme == IntPtr.Zero ? null : Marshal.PtrToStringUni(v.DefaultScheme); }
    public static void Set(uint flags, string scheme) {
        IntPtr text = IntPtr.Zero;
        try {
            if (!String.IsNullOrEmpty(scheme)) text = Marshal.StringToHGlobalUni(scheme);
            var v = new P2HighContrast { Size = (uint)Marshal.SizeOf<P2HighContrast>(), Flags = flags, DefaultScheme = text };
            if (!SystemParametersInfo(0x0043, v.Size, ref v, 0x0002)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { if (text != IntPtr.Zero) Marshal.FreeHGlobal(text); }
    }
    public static string Color(int index) { uint c = GetSysColor(index); return String.Format("{0},{1},{2}", c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); }
}
"@
}
switch ($Mode) {
    'read' {
        $hc = [P2GuestContrast]::Read()
        $result = @{ mode = $Mode; flags = $hc.Flags; enabled = (($hc.Flags -band 1) -ne 0); scheme = [P2GuestContrast]::Scheme($hc) }
    }
    'set' {
        $target = if ([string]::IsNullOrEmpty($Scheme)) { $null } else { $Scheme }
        [P2GuestContrast]::Set($Flags, $target)
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 250
            $hc = [P2GuestContrast]::Read()
            $actual = [P2GuestContrast]::Scheme($hc)
            # Windows keeps its default scheme name when none is supplied, so
            # the scheme is compared only when one was requested.
            $committed = ($hc.Flags -eq $Flags -and ($null -eq $target -or [string]::Equals([string]$actual, [string]$target, [StringComparison]::Ordinal)))
        } while (-not $committed -and [DateTime]::UtcNow -lt $deadline)
        if (-not $committed) { throw "The guest did not commit contrast flags=$Flags scheme='$target'; actual flags=$($hc.Flags) scheme='$actual'." }
        $result = @{ mode = $Mode; flags = $hc.Flags; enabled = (($hc.Flags -band 1) -ne 0); scheme = $actual }
    }
    'colors' {
        # COLOR_WINDOWTEXT 8, COLOR_WINDOW 5, COLOR_HOTLIGHT 26, COLOR_HIGHLIGHT 13, COLOR_HIGHLIGHTTEXT 14
        $result = @{ mode = $Mode; windowText = [P2GuestContrast]::Color(8); window = [P2GuestContrast]::Color(5); hotlight = [P2GuestContrast]::Color(26); highlight = [P2GuestContrast]::Color(13); highlightText = [P2GuestContrast]::Color(14) }
    }
}
$result | ConvertTo-Json -Compress
'@

function Stage-P2ContrastGuest {
    $local = Join-Path $script:RunDirectory 'GuestSystemContrast.ps1'
    [System.IO.File]::WriteAllText($local, $script:P2ContrastGuestSource, [System.Text.UTF8Encoding]::new($false))
    $relative = "correctness\$($script:RunId)\GuestSystemContrast.ps1"
    $null = Invoke-NativeChecked @('target', 'push', 'sandbox', $local, $relative)
    $script:P2ContrastGuestPath = Join-Path 'C:\WinApp\work' $relative
}

function Invoke-P2ContrastGuest {
    param([Parameter(Mandatory)][ValidateSet('read', 'set', 'colors')][string]$Mode, [uint32]$Flags = 0, [AllowNull()][string]$Scheme = $null)
    if (-not $script:P2ContrastGuestPath) { Stage-P2ContrastGuest }
    $arguments = @('target', 'exec', 'sandbox', '--json', '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $script:P2ContrastGuestPath, '-Mode', $Mode, '-HostComputerName', [Environment]::MachineName)
    if ($Mode -eq 'set') {
        $arguments += @('-Flags', [string]$Flags)
        if (-not [string]::IsNullOrEmpty($Scheme)) { $arguments += @('-Scheme', $Scheme) }
    }
    $result = Invoke-NativeChecked -Arguments $arguments -Json
    $value = $result.value
    $text = $null
    foreach ($name in @('stdout', 'output', 'result')) {
        $property = $value.PSObject.Properties[$name]
        if ($null -ne $property -and $property.Value -is [string] -and $property.Value.Trim()) { $text = [string]$property.Value; break }
    }
    if ($null -eq $text) {
        if ($value -is [string]) { $text = $value }
        elseif ($value.PSObject.Properties['mode']) { return $value }
        else { throw "The guest contrast helper returned no JSON: $($result.output)" }
    }
    $match = [regex]::Match($text.Trim(), '(?s)(\{.*\})\s*$')
    if (-not $match.Success) { throw "The guest contrast helper wrote no JSON: $text" }
    return ConvertFrom-Json -InputObject $match.Groups[1].Value
}

function Enable-P2SystemHighContrast {
    $script:P2ContrastOriginal = Invoke-P2ContrastGuest -Mode 'read'
    $flags = [uint32]([uint32]$script:P2ContrastOriginal.flags -bor 1)
    if ([uint32]$script:P2ContrastOriginal.flags -ne $flags) {
        $null = Invoke-P2ContrastGuest -Mode 'set' -Flags $flags -Scheme ([string]$script:P2ContrastOriginal.scheme)
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $theme = [string](Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'ThemeStatus').name
        if ($theme -ceq 'theme:high-contrast') { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($theme -cne 'theme:high-contrast') { throw "The sample did not report the system contrast theme; ThemeStatus is '$theme'." }
    $native = Invoke-P2ContrastGuest -Mode 'read'
    if (-not [bool]$native.enabled) { throw 'The guest does not report High Contrast as enabled.' }
    $colors = Invoke-P2ContrastGuest -Mode 'colors'
    $palette = foreach ($role in @('windowText', 'window', 'hotlight', 'highlight', 'highlightText')) {
        $parts = ([string]$colors.$role).Split(',') | ForEach-Object { [int]$_ }
        [pscustomobject]@{ role = $role; r = $parts[0]; g = $parts[1]; b = $parts[2] }
    }
    return [pscustomobject]@{ flags = [uint32]$native.flags; scheme = [string]$native.scheme; palette = @($palette) }
}

function Restore-P2SystemHighContrast {
    if ($null -eq $script:P2ContrastOriginal) { return }
    $original = $script:P2ContrastOriginal
    $null = Invoke-P2ContrastGuest -Mode 'set' -Flags ([uint32]$original.flags) -Scheme ([string]$original.scheme)
    $restored = Invoke-P2ContrastGuest -Mode 'read'
    # An unset original scheme cannot be written back: Windows keeps the
    # scheme name it last used. Flags must match exactly, and a named
    # original scheme must match exactly.
    $schemeMatches = [string]::IsNullOrEmpty([string]$original.scheme) -or ([string]$restored.scheme -ceq [string]$original.scheme)
    if ([uint32]$restored.flags -ne [uint32]$original.flags -or -not $schemeMatches) {
        throw "Guest High Contrast restore mismatch: flags=$($restored.flags) scheme='$($restored.scheme)'."
    }
    $script:P2ContrastOriginal = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $theme = [string](Invoke-GuestProbe -Mode 'raw-name' -AutomationId 'ThemeStatus').name
        if ($theme -cne 'theme:high-contrast') { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The sample stayed in the contrast theme after the guest setting was restored.'
}

# Counts pixels in a screen rectangle that are neither a palette color nor an
# anti-aliasing blend of two palette colors (distance to every palette segment
# above the tolerance), and how many pixels lie near each palette color.
function Measure-P2PaletteConformance {
    param([Parameter(Mandatory)]$Sampler, [Parameter(Mandatory)]$Rect, [Parameter(Mandatory)][object[]]$Palette, [double]$Tolerance = 20)
    if (-not ('P2PaletteCounter' -as [type])) {
        Add-Type -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, System.Drawing.Common -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class P2PaletteCounter
{
    public static int[] Count(Bitmap bitmap, Rectangle area, int[] palette, double tolerance)
    {
        area.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        int colors = palette.Length / 3;
        int[] result = new int[3 + colors];
        if (area.Width <= 0 || area.Height <= 0) return result;
        BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[area.Width * 4];
            double limit = tolerance * tolerance;
            double pure = 12.0 * 12.0;
            for (int y = 0; y < area.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < area.Width; x++)
                {
                    double b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    result[0]++;
                    double best = double.MaxValue;
                    double bestPoint = double.MaxValue;
                    for (int i = 0; i < colors; i++)
                    {
                        double dr = r - palette[i * 3], dg = g - palette[i * 3 + 1], db = b - palette[i * 3 + 2];
                        double point = dr * dr + dg * dg + db * db;
                        if (point <= limit) result[3 + i]++;
                        if (point < best) best = point;
                        if (point < bestPoint) bestPoint = point;
                        for (int j = i + 1; j < colors; j++)
                        {
                            double sr = palette[j * 3] - palette[i * 3], sg = palette[j * 3 + 1] - palette[i * 3 + 1], sb = palette[j * 3 + 2] - palette[i * 3 + 2];
                            double length = sr * sr + sg * sg + sb * sb;
                            if (length <= 0) continue;
                            double t = Math.Max(0, Math.Min(1, (dr * sr + dg * sg + db * sb) / length));
                            double er = dr - t * sr, eg = dg - t * sg, eb = db - t * sb;
                            double segment = er * er + eg * eg + eb * eb;
                            if (segment < best) best = segment;
                        }
                    }
                    if (best > limit) result[1]++;
                    if (bestPoint > pure) result[2]++;
                }
            }
            return result;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
'@
    }
    $values = [int[]]@($Palette | ForEach-Object { [int]$_.r; [int]$_.g; [int]$_.b })
    $left = [int][Math]::Round(([double]$Rect.x - $Sampler.left) * $Sampler.scaleX)
    $top = [int][Math]::Round(([double]$Rect.y - $Sampler.top) * $Sampler.scaleY)
    $width = [int][Math]::Round([double]$Rect.width * $Sampler.scaleX)
    $height = [int][Math]::Round([double]$Rect.height * $Sampler.scaleY)
    $counts = [P2PaletteCounter]::Count($Sampler.bitmap, [System.Drawing.Rectangle]::new($left, $top, $width, $height), $values, $Tolerance)
    $near = [ordered]@{}
    for ($i = 0; $i -lt $Palette.Count; $i++) { $near[[string]$Palette[$i].role] = $counts[3 + $i] }
    $total = [Math]::Max(1, $counts[0])
    return [pscustomobject]@{ pixels = $counts[0]; offPalette = $counts[1]; offPaletteFraction = $counts[1] / [double]$total; blended = $counts[2]; blendedFraction = $counts[2] / [double]$total; nearRole = [pscustomobject]$near }
}

function Assert-P2PaletteConformance {
    param([Parameter(Mandatory)]$Measure, [Parameter(Mandatory)][string]$What, [double]$MaximumFraction = 0.005, [double]$MaximumBlendedFraction = 0.25)
    if ($Measure.pixels -le 0) { throw "$What has no measurable pixels." }
    # Anti-aliasing occupies only glyph and shape edges. A large share of
    # non-pure pixels means a filled area painted in a non-system color.
    if ($Measure.blendedFraction -gt $MaximumBlendedFraction) { throw "$What has $($Measure.blended) of $($Measure.pixels) pixels that are not a pure system contrast role ($([Math]::Round($Measure.blendedFraction * 100, 1))%); a filled area is not using the contrast palette." }
    if ($Measure.offPaletteFraction -gt $MaximumFraction) { throw "$What has $($Measure.offPalette) of $($Measure.pixels) pixels outside the system contrast roles and their blends ($([Math]::Round($Measure.offPaletteFraction * 100, 2))%)." }
    if ([int]$Measure.nearRole.window -le 0 -or [int]$Measure.nearRole.windowText -le 0) { throw "$What does not show both Window and WindowText; it is not readable content on the contrast surface." }
}

function Get-P2VisibleElement {
    param([Parameter(Mandatory)][string]$ClassName)
    $renderer = Get-UiBounds 'MarkdownRenderer'
    return @(Get-P2Elements -ClassName $ClassName | Where-Object {
            -not [bool]$_.isOffscreen -and [double]$_.width -gt 0 -and [double]$_.height -gt 0 -and
            [double]$_.y -ge $renderer[1] -and ([double]$_.y + [double]$_.height) -le ($renderer[1] + $renderer[3]) }) |
        Sort-Object { [double]$_.y } | Select-Object -First 1
}

function Run-Phase2SystemContrastCases {
    $mermaidDiagramCount = 17
    Test-Case -Id 'P2-MERMAID-03-SYSHC' -Name 'mermaid-actual-windows-high-contrast' -Requirement 'Under the Sandbox guest''s actual Windows High Contrast setting, the Mermaid sample keeps every diagram native with labelled nodes and its link, paints only system contrast roles, and in both viewport modes the final diagram selects with Select All and copies its exact fenced source.' -Body {
        $selectionSource = "# Mermaid selection`n`nBefore diagram.`n`n``````mermaid`nflowchart LR`n  selectA[Selectable start] --> selectB[Selectable end]`n```````n"
        try {
            Reset-P2Display
            $contrast = Enable-P2SystemHighContrast
            Navigate-Sample -Key 'Mermaid' -RequiredText 'Native Mermaid'
            Scroll-P2Bottom
            $diagrams = @(Get-P2Elements -ClassName 'MarkdownDiagram')
            if ($diagrams.Count -ne $mermaidDiagramCount) { throw "Under system High Contrast the Mermaid sample exposed $($diagrams.Count) native diagrams; it documents $mermaidDiagramCount." }
            if (@(Get-P2Elements -ClassName 'MarkdownCodeBlock').Count -gt 0) { throw 'A documented diagram fell back to code under system High Contrast.' }
            $pageNodes = @(Get-P2Elements -ClassName 'MarkdownDiagramNode')
            if ($pageNodes.Count -lt $mermaidDiagramCount) { throw "Only $($pageNodes.Count) diagram nodes expose labels under system High Contrast." }
            if (@(Get-P2Elements -ClassName 'MarkdownDiagramLink' | Where-Object { [string]$_.name -ceq 'Invokable Mermaid node' }).Count -ne 1) { throw 'The invokable Mermaid node lost its link under system High Contrast.' }
            $null = Invoke-Ui @('scroll', 'MarkdownRenderer', '--to', 'top')
            Start-Sleep -Milliseconds 600
            $diagram = Get-P2VisibleElement -ClassName 'MarkdownDiagram'
            if ($null -eq $diagram) { throw 'No fully visible diagram to measure under system High Contrast.' }
            $shot = Save-Screenshot 'screenshots\p2-mermaid-03-system-high-contrast.png'
            $sampler = Get-P2PixelSampler -Shot $shot
            try { $pagePalette = Measure-P2PaletteConformance -Sampler $sampler -Rect $diagram -Palette $contrast.palette }
            finally { $sampler.bitmap.Dispose() }
            Assert-P2PaletteConformance -Measure $pagePalette -What "The diagram '$($diagram.name)'"

            $modes = [System.Collections.Generic.List[object]]::new()
            foreach ($mode in $script:P2ViewportModes) {
                Enter-P2Mode -Mode $mode -Key 'Mermaid' -RequiredText 'Native Mermaid' -Markdown $selectionSource -MarkdownRequiredText 'Mermaid selection'
                Start-Sleep -Milliseconds 1200
                $diagram = @(Get-P2Elements -ClassName 'MarkdownDiagram')
                if ($diagram.Count -ne 1) { throw "[$mode] The selection fixture exposed $($diagram.Count) diagrams under system High Contrast." }
                $nodes = @(Get-P2Elements -ClassName 'MarkdownDiagramNode' | Where-Object { [string]$_.name -in @('Selectable start', 'Selectable end') })
                if ($nodes.Count -ne 2) { throw "[$mode] The final diagram's nodes do not expose their labels under system High Contrast." }
                $shot = Save-Screenshot "screenshots\p2-mermaid-03-system-high-contrast-$mode.png"
                $sampler = Get-P2PixelSampler -Shot $shot
                try { $finalPalette = Measure-P2PaletteConformance -Sampler $sampler -Rect $diagram[0] -Palette $contrast.palette }
                finally { $sampler.bitmap.Dispose() }
                Assert-P2PaletteConformance -Measure $finalPalette -What "[$mode] The final diagram"
                Clear-P2Selection
                $null = Invoke-Ui @('focus', 'MarkdownRenderer')
                $null = Invoke-Ui @('send-keys', 'ctrl+a', '--via', 'send-input')
                $selection = Invoke-GuestProbe -Mode 'selection'
                $rects = @($selection.ranges | ForEach-Object { $_.rectangles } | Where-Object { $_.width -gt 0 -and $_.height -gt 0 })
                $d = $diagram[0]
                if (@($rects | Where-Object { ($_.y + $_.height) -gt $d.y -and $_.y -lt ($d.y + $d.height) }).Count -eq 0) { throw "[$mode] Select All did not select the final diagram under system High Contrast." }
                $copied = Copy-P2MarkdownSelection -Range ([pscustomobject]@{ rectangles = @($rects[0]) })
                $expected = (Get-NativeMarkdownEditorSource -Text $selectionSource) -replace "`r", "`n"
                if ((([string]$copied.plain) -replace "`r`n|`r", "`n").TrimEnd("`n") -cne $expected.TrimEnd("`n")) { throw "[$mode] Copy as Markdown under system High Contrast did not return the exact source including the final fenced diagram." }
                Clear-P2Selection
                $modes.Add([pscustomobject]@{ viewport = $mode; finalDiagramPalette = $finalPalette; finalDiagramSelected = $true; exactCopy = $true; screenshot = $shot })
            }
            return [pscustomobject]@{ contrast = $contrast; diagrams = $diagrams.Count; labelledNodes = $pageNodes.Count; pageDiagramPalette = $pagePalette; viewports = @($modes.ToArray()) }
        }
        finally {
            try { Exit-P2Mode }
            finally { Restore-P2SystemHighContrast }
        }
    }

    Test-Case -Id 'P2-HIGHLIGHT-02-SYSHC' -Name 'highlight-actual-windows-high-contrast' -Requirement 'Under the Sandbox guest''s actual Windows High Contrast setting, unknown-language and highlighted code keep their exact text and both paint only system contrast roles, with readable WindowText on Window.' -Body {
        $source = "# Unknown language`n`n``````unknownlang`nplainCodeMarker = call(1, ""two"") // not a token`n```````n`n``````csharp`npublic sealed class KnownMarker { private int value = 1; }`n```````n"
        try {
            Reset-P2Display
            $contrast = Enable-P2SystemHighContrast
            Navigate-Sample -Key 'Code' -RequiredText 'Fenced Code Blocks'
            Set-TemporaryMarkdown -Markdown $source -RequiredText 'Unknown language'
            Start-Sleep -Milliseconds 800
            $text = Get-P2Text
            foreach ($code in @('plainCodeMarker = call(1, "two") // not a token', 'public sealed class KnownMarker { private int value = 1; }')) {
                if ((Get-P2Occurrences -Text $text -Needle $code) -ne 1) { throw "Under system High Contrast the code '$code' is not exposed exactly once." }
            }
            $blocks = @(Get-P2Elements -ClassName 'MarkdownCodeBlock' | Sort-Object { [double]$_.y })
            if ($blocks.Count -ne 2) { throw "Expected 2 code blocks under system High Contrast, found $($blocks.Count)." }
            $shot = Save-Screenshot 'screenshots\p2-highlight-02-system-high-contrast.png'
            $sampler = Get-P2PixelSampler -Shot $shot
            try {
                $plain = Measure-P2PaletteConformance -Sampler $sampler -Rect $blocks[0] -Palette $contrast.palette
                $known = Measure-P2PaletteConformance -Sampler $sampler -Rect $blocks[1] -Palette $contrast.palette
            }
            finally { $sampler.bitmap.Dispose() }
            Assert-P2PaletteConformance -Measure $plain -What 'The unknown-language block'
            Assert-P2PaletteConformance -Measure $known -What 'The highlighted C# block'
            return [pscustomobject]@{ contrast = $contrast; unknownLanguage = $plain; highlighted = $known; screenshot = $shot }
        }
        finally {
            try { Restore-P2SystemHighContrast }
            finally { Reset-P2Display }
        }
    }
}
