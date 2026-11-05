using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using static MarkdownRenderer.Sample.Automation.Native.NativeMouse;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;
using static MarkdownRenderer.Sample.Automation.Uia.TextAttributeValues;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes native Mermaid vector scenes: semantics, text scale, link activation and High Contrast paint.
/// </summary>
internal static class MermaidProbes
{
    internal static void ProbeMermaidVectorScene(Window window)
    {
        const int ExpectedDiagramCount = 17;
        SelectSample(window, "Mermaid");
        var renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => renderer.FindAllDescendants().Any(element =>
                element.ControlType == ControlType.Hyperlink &&
                (element.Name ?? string.Empty).Contains("Invokable Mermaid node", StringComparison.Ordinal)),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(
            ready,
            "native Mermaid hyperlink did not appear in the UIA tree; document=" +
            GetRendererDocumentText(renderer) +
            "; descendants=" +
            string.Join(" | ", renderer.FindAllDescendants().Select(static element =>
                $"{element.ControlType}/{NameOrEmpty(element)}/id={AutomationIdOrEmpty(element)}/class={ClassNameOrEmpty(element)}")));

        AutomationElement? diagnostics = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SampleDiagnosticsMessage"));
        Assert(diagnostics is null ||
               diagnostics.IsOffscreen ||
               string.IsNullOrEmpty(GetAutomationElementText(diagnostics)),
            "the primary Mermaid page must commit without renderer diagnostics");

        AutomationElement[] pageDescendants = renderer.FindAllDescendants();
        int diagramCount = pageDescendants.Count(element =>
            ClassNameOrEmpty(element) == "MarkdownDiagram");
        Assert(
            diagramCount == ExpectedDiagramCount,
            $"Mermaid page must expose {ExpectedDiagramCount} native diagrams, got {diagramCount}");
        string initialDocumentText = GetRendererDocumentText(renderer);
        Assert(
            initialDocumentText.Contains("Class diagram", StringComparison.Ordinal) &&
            initialDocumentText.Contains("XY chart", StringComparison.Ordinal) &&
            initialDocumentText.Contains("Kanban", StringComparison.Ordinal),
            "Mermaid page is missing representative standard or beta grammar families");

        AutomationElement link = pageDescendants
            .First(element => element.ControlType == ControlType.Hyperlink &&
                (element.Name ?? string.Empty).Contains("Invokable Mermaid node", StringComparison.Ordinal));
        Assert(link.Patterns.Invoke.IsSupported, "Mermaid hyperlink must expose InvokePattern");

        var linkBounds = link.BoundingRectangle;
        Assert(linkBounds.Width > 1 && linkBounds.Height > 1,
            $"Mermaid hyperlink must expose non-empty hit-test bounds, got {FormatRect(linkBounds)}");
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var attributes = renderer.Automation.TextAttributeLibrary;
        var linkRange = textPattern.RangeFromChild(link);
        Assert(
            string.Equals(linkRange.GetText(-1).Trim(), "Invokable Mermaid node", StringComparison.Ordinal),
            $"Mermaid RangeFromChild must be child-granular, got '{linkRange.GetText(-1)}'");
        Assert(
            (linkRange.GetAttributeValue(attributes.StyleName)?.ToString() ?? string.Empty)
                .Contains("Diagram", StringComparison.Ordinal),
            "Mermaid child text must expose the block-vector Diagram style");
        Assert(
            TryAttributeNumber(linkRange.GetAttributeValue(attributes.FontSize), out double normalDiagramFontSize),
            "Mermaid child text must expose a numeric FontSize");
        Assert(
            (linkRange.GetAttributeValue(attributes.FontName)?.ToString() ?? string.Empty)
                .Contains("Cascadia Mono", StringComparison.OrdinalIgnoreCase),
            "Diagram.FontFamily resource customization must reach Mermaid vector text");
        var formatRange = linkRange.Clone();
        formatRange.ExpandToEnclosingUnit(TextUnit.Format);
        Assert(
            formatRange.GetText(-1).Contains("Invokable Mermaid node", StringComparison.Ordinal) &&
            !formatRange.GetText(-1).Contains("Native scene", StringComparison.Ordinal),
            "TextUnit.Format must stop at the semantic vector-child boundary");
        var pointRange = textPattern.RangeFromPoint(new System.Drawing.Point(
            linkBounds.Left + linkBounds.Width / 3,
            linkBounds.Top + linkBounds.Height / 2));
        pointRange.ExpandToEnclosingUnit(TextUnit.Format);
        Assert(
            pointRange.GetText(-1).Contains("Invokable Mermaid node", StringComparison.Ordinal),
            "TextPattern.RangeFromPoint must resolve linked-only Mermaid semantics");

        var textScaleToggle = window.FindFirstDescendant(cf => cf.ByAutomationId("TextScaleToggle"))?.AsToggleButton()
                              ?? throw new InvalidOperationException("TextScaleToggle not found");
        string committedThemeStatus =
            FindFirstRawDescendantByAutomationId(window, "ThemeStatus")?.Name ?? string.Empty;
        Assert(
            committedThemeStatus.StartsWith("theme:", StringComparison.Ordinal) &&
            !string.Equals(committedThemeStatus, "theme:pending", StringComparison.Ordinal),
            $"Mermaid theme was not committed before the scale probe: '{committedThemeStatus}'");
        try
        {
            if (textScaleToggle.ToggleState != ToggleState.On)
                textScaleToggle.Toggle();

            bool vectorScaled = Retry.WhileFalse(() =>
                {
                    AutomationElement? currentLink = FindMermaidLink(renderer);
                    if (currentLink is null)
                        return false;
                    var currentPattern = renderer.Patterns.Text.PatternOrDefault;
                    var currentRange = currentPattern?.RangeFromChild(currentLink);
                    return currentRange is not null &&
                           TryAttributeNumber(currentRange.GetAttributeValue(attributes.FontSize), out double scaledFontSize) &&
                           scaledFontSize >= normalDiagramFontSize * 1.8d &&
                           currentLink.BoundingRectangle.Height >= linkBounds.Height * 1.8;
                },
                timeout: TimeSpan.FromSeconds(8),
                interval: TimeSpan.FromMilliseconds(150)).Result;
            Assert(vectorScaled, "200% text scale did not resize the Mermaid block and semantic link bounds");
        }
        finally
        {
            if (textScaleToggle.ToggleState == ToggleState.On)
            {
                textScaleToggle.Toggle();
                bool scaleResetCommitted = Retry.WhileFalse(
                    () => string.Equals(
                        FindFirstRawDescendantByAutomationId(window, "ThemeStatus")?.Name,
                        committedThemeStatus,
                        StringComparison.Ordinal),
                    timeout: TimeSpan.FromSeconds(8),
                    interval: TimeSpan.FromMilliseconds(100)).Result;
                Assert(
                    scaleResetCommitted,
                    "text-scale reset did not commit before pointer bounds were reacquired");
            }
        }

        link = FindMermaidLink(renderer)
               ?? throw new InvalidOperationException("Mermaid hyperlink disappeared after text-scale rebuild");
        linkBounds = link.BoundingRectangle;
        var linkPoint = new System.Drawing.Point(
            // Stay inside the first text half. The vector text hit-test changes
            // its insertion position at the exact horizontal midpoint, so a
            // one-pixel press/release rounding difference there can look like a
            // drag selection and correctly suppress link activation.
            linkBounds.Left + linkBounds.Width / 3,
            linkBounds.Top + linkBounds.Height / 2);
        window.SetForeground();
        window.FocusNative();
        PositionPointerOnWritableInputDesktop(linkPoint);
        Mouse.Click(linkPoint, FlaUI.Core.Input.MouseButton.Left);
        bool pointerActivated = Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains("link:Mouse:https://example.invalid/mermaid-node", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(
            pointerActivated,
            "pointer hit testing did not raise the renderer's host LinkClick event; " +
            $"bounds={linkBounds}, point={linkPoint}, status='{FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name}'");

        link.Patterns.Invoke.Pattern.Invoke();

        bool activated = Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains("link:Automation:https://example.invalid/mermaid-node", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(activated, "UIA Invoke did not raise the renderer's host LinkClick event");

        link.Focus();
        bool focused = Retry.WhileFalse(
            () => link.Properties.HasKeyboardFocus.ValueOrDefault,
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(focused, "UIA SetFocus did not report keyboard focus on the Mermaid semantic hyperlink");
        Keyboard.Press(VirtualKeyShort.RETURN);
        bool keyboardActivated = Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains("link:Keyboard:https://example.invalid/mermaid-node", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(keyboardActivated, "keyboard activation did not use the same Mermaid link path as pointer/UIA");

        string documentText = GetRendererDocumentText(renderer);
        Assert(documentText.Contains("Invokable Mermaid node", StringComparison.Ordinal),
            "Mermaid semantic labels must contribute to TextPattern");
        Assert(!documentText.Contains("layout: elk", StringComparison.Ordinal),
            "the primary Mermaid page must not contain the unsupported-layout diagnostics fixture");
        Assert(renderer.FindAllDescendants().Any(element => element.ControlType == ControlType.Image),
            "Mermaid diagram root must expose an image/diagram UIA peer");

        ProbeMermaidHighContrastPixelsAndTextAttributes(window);
    }

    private static void ProbeMermaidHighContrastPixelsAndTextAttributes(Window window)
    {
        var toggle = window.FindFirstDescendant(cf => cf.ByAutomationId("ForcedHighContrastToggle"))?.AsToggleButton()
                     ?? throw new InvalidOperationException("ForcedHighContrastToggle not found");

        try
        {
            if (toggle.ToggleState != ToggleState.On)
            {
                toggle.Toggle();
                Thread.Sleep(1200);
            }

            AutomationElement renderer = FindRenderer(window);
            bool ready = Retry.WhileFalse(
                () => FindMermaidLink(renderer) is not null &&
                      FindMermaidSemantic(renderer, "Native scene") is not null,
                timeout: TimeSpan.FromSeconds(8),
                interval: TimeSpan.FromMilliseconds(100)).Result;
            Assert(ready, "High Contrast Mermaid semantic nodes did not become available");

            // The keyboard-invoke portion of this probe leaves the linked
            // semantic focused. Move real focus outside the renderer so its
            // LosingFocus path clears the logical vector focus before pixel
            // capture; focusing an already-focused renderer can be a UIA no-op.
            // This keeps the Hotlight assertion on glyph paint rather than a
            // cyan focus rectangle.
            toggle.Focus();
            bool vectorFocusCleared = Retry.WhileFalse(
                () => FindMermaidLink(renderer)?.Properties.HasKeyboardFocus.ValueOrDefault != true,
                timeout: TimeSpan.FromSeconds(3),
                interval: TimeSpan.FromMilliseconds(50)).Result;
            Assert(vectorFocusCleared, "Mermaid vector focus remained active before High Contrast capture");

            AutomationElement linked = FindMermaidLink(renderer)
                                       ?? throw new InvalidOperationException("High Contrast Mermaid link not found");
            AutomationElement plain = FindMermaidSemantic(renderer, "Native scene")
                                      ?? throw new InvalidOperationException("High Contrast Mermaid non-link node not found");
            var textPattern = renderer.Patterns.Text.PatternOrDefault
                              ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
            var attributes = renderer.Automation.TextAttributeLibrary;
            var linkedRange = textPattern.RangeFromChild(linked);
            var plainRange = textPattern.RangeFromChild(plain);

            Assert(
                (linkedRange.GetAttributeValue(attributes.StyleName)?.ToString() ?? string.Empty)
                    .Contains("Diagram", StringComparison.Ordinal),
                "linked vector text must retain its Diagram semantic style context");
            Assert(
                (plainRange.GetAttributeValue(attributes.StyleName)?.ToString() ?? string.Empty)
                    .Contains("Diagram", StringComparison.Ordinal),
                "non-linked vector text must retain its Diagram semantic style context");
            Assert(
                ToColorRefValue(linkedRange.GetAttributeValue(attributes.ForegroundColor)) ==
                ColorRef(0x00, 0xFF, 0xFF),
                "linked High Contrast vector text must expose the draw-time Hotlight foreground");
            Assert(
                ToColorRefValue(plainRange.GetAttributeValue(attributes.ForegroundColor)) ==
                ColorRef(0xFF, 0xFF, 0xFF),
                "non-linked High Contrast vector text must expose the draw-time WindowText foreground");
            Assert(
                ToColorRefValue(linkedRange.GetAttributeValue(attributes.BackgroundColor)) ==
                ColorRef(0x00, 0x00, 0x00) &&
                ToColorRefValue(plainRange.GetAttributeValue(attributes.BackgroundColor)) ==
                ColorRef(0x00, 0x00, 0x00),
                "High Contrast vector TextPattern backgrounds must expose the Window surface");

            AssertHighContrastVectorPixels(
                linked,
                expectHotlight: true,
                "mermaid-high-contrast-linked-node.png");
            AssertHighContrastVectorPixels(
                plain,
                expectHotlight: false,
                "mermaid-high-contrast-plain-node.png");
        }
        finally
        {
            if (toggle.ToggleState == ToggleState.On)
            {
                toggle.Toggle();
                Thread.Sleep(800);
            }
        }
    }

    private static AutomationElement? FindMermaidSemantic(
        AutomationElement renderer,
        string accessibleName) =>
        renderer.FindAllDescendants().FirstOrDefault(element =>
            string.Equals(NameOrEmpty(element).Trim(), accessibleName, StringComparison.Ordinal));

    private static void AssertHighContrastVectorPixels(
        AutomationElement semantic,
        bool expectHotlight,
        string fileName)
    {
        string artifactDir = Path.Combine(
            Path.GetFullPath("."), "artifacts", "screenshots", "markdown-audit");
        Directory.CreateDirectory(artifactDir);
        string path = Path.Combine(artifactDir, fileName);
        using var capture = FlaUI.Core.Capturing.Capture.Element(
            semantic,
            new FlaUI.Core.Capturing.CaptureSettings());
        capture.ToFile(path);

        System.Drawing.Bitmap bitmap = capture.Bitmap;
        Assert(bitmap.Width > 2 && bitmap.Height > 2,
            $"High Contrast vector capture is empty: {bitmap.Width}x{bitmap.Height}");

        // Ignore the outer focus-border band even after focus has been cleared.
        // Requiring Hotlight/WindowText pixels in this interior makes a cyan
        // perimeter alone insufficient to pass the rendered-paint gate.
        int interiorInset = Math.Min(
            4,
            Math.Max(1, (Math.Min(bitmap.Width, bitmap.Height) - 1) / 4));
        int interiorWidth = bitmap.Width - (interiorInset * 2);
        int interiorHeight = bitmap.Height - (interiorInset * 2);
        Assert(interiorWidth > 0 && interiorHeight > 0,
            $"High Contrast vector capture has no testable interior: {bitmap.Width}x{bitmap.Height}");

        int surfacePixels = 0;
        int semanticPixels = 0;
        long surfaceRed = 0;
        long surfaceGreen = 0;
        long surfaceBlue = 0;
        long semanticRed = 0;
        long semanticGreen = 0;
        long semanticBlue = 0;
        for (int y = interiorInset; y < bitmap.Height - interiorInset; y++)
        {
            for (int x = interiorInset; x < bitmap.Width - interiorInset; x++)
            {
                System.Drawing.Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R <= 48 && pixel.G <= 48 && pixel.B <= 48)
                {
                    surfacePixels++;
                    surfaceRed += pixel.R;
                    surfaceGreen += pixel.G;
                    surfaceBlue += pixel.B;
                    continue;
                }

                bool isSemantic = expectHotlight
                    ? pixel.R <= 80 && pixel.G >= 160 && pixel.B >= 160
                    : pixel.R >= 160 && pixel.G >= 160 && pixel.B >= 160;
                if (!isSemantic)
                    continue;
                semanticPixels++;
                semanticRed += pixel.R;
                semanticGreen += pixel.G;
                semanticBlue += pixel.B;
            }
        }

        int interiorPixels = interiorWidth * interiorHeight;
        Assert(surfacePixels >= Math.Max(16, interiorPixels / 5),
            $"High Contrast vector surface is not visibly present in the capture interior; " +
            $"black-like={surfacePixels}/{interiorPixels}. " +
            "A solid foreground rectangle must fail this rendered-pixel gate.");
        Assert(semanticPixels >= Math.Max(8, (interiorWidth + interiorHeight) / 8),
            $"High Contrast vector {(expectHotlight ? "Hotlight" : "WindowText")} glyph paint is not visibly present " +
            $"inside the focus-border mask; semantic={semanticPixels}/{interiorPixels}");

        System.Drawing.Color surface = System.Drawing.Color.FromArgb(
            (int)(surfaceRed / surfacePixels),
            (int)(surfaceGreen / surfacePixels),
            (int)(surfaceBlue / surfacePixels));
        System.Drawing.Color foreground = System.Drawing.Color.FromArgb(
            (int)(semanticRed / semanticPixels),
            (int)(semanticGreen / semanticPixels),
            (int)(semanticBlue / semanticPixels));
        double contrast = ContrastRatio(surface, foreground);
        Assert(contrast >= 4.5,
            $"Rendered High Contrast vector contrast {contrast:F2}:1 is below 4.5:1; " +
            $"surface={surface}, foreground={foreground}");
        Console.WriteLine(
            $"[automation] rendered HC vector interior pixels: {fileName}; inset={interiorInset}; " +
            $"surface={surfacePixels}/{interiorPixels}; semantic={semanticPixels}/{interiorPixels}; " +
            $"contrast={contrast:F2}:1; screenshot={path}");
    }

    private static double ContrastRatio(System.Drawing.Color first, System.Drawing.Color second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(System.Drawing.Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) +
               0.7152 * Linearize(color.G) +
               0.0722 * Linearize(color.B);
    }

    private static AutomationElement? FindMermaidLink(AutomationElement renderer) =>
        renderer.FindAllDescendants().FirstOrDefault(element =>
            element.ControlType == ControlType.Hyperlink &&
            (element.Name ?? string.Empty).Contains("Invokable Mermaid node", StringComparison.Ordinal));
}
