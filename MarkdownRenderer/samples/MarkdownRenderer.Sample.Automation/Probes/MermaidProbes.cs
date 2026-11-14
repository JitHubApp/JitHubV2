using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using MarkdownRenderer.Sample.Automation.Native;
using MarkdownRenderer.Sample.Automation.Uia;
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
    private const string LinkName = "Invokable Mermaid node";

    internal static void ProbeMermaidVectorScene(Window window)
    {
        SelectSample(window, "Mermaid");
        var renderer = FindRenderer(window);
        WaitForMermaidLink(renderer);
        AutomationElement[] pageDescendants = AssertMermaidPageCommitted(window, renderer);

        AutomationElement link = pageDescendants
            .First(element => element.ControlType == ControlType.Hyperlink &&
                (element.Name ?? string.Empty).Contains(LinkName, StringComparison.Ordinal));
        Assert(link.Patterns.Invoke.IsSupported, "Mermaid hyperlink must expose InvokePattern");
        (double normalDiagramFontSize, System.Drawing.Rectangle linkBounds) = AssertLinkTextSemantics(renderer, link);
        AssertTextScaleResizesVectorScene(window, renderer, normalDiagramFontSize, linkBounds);

        link = FindMermaidLink(renderer)
               ?? throw new InvalidOperationException("Mermaid hyperlink disappeared after text-scale rebuild");
        AssertLinkActivationPaths(window, link);

        string documentText = GetRendererDocumentText(renderer);
        Assert(documentText.Contains(LinkName, StringComparison.Ordinal),
            "Mermaid semantic labels must contribute to TextPattern");
        Assert(!documentText.Contains("layout: elk", StringComparison.Ordinal),
            "the primary Mermaid page must not contain the unsupported-layout diagnostics fixture");
        Assert(renderer.FindAllDescendants().Any(element => element.ControlType == ControlType.Image),
            "Mermaid diagram root must expose an image/diagram UIA peer");

        ProbeMermaidHighContrastPixelsAndTextAttributes(window);
    }

    private static void WaitForMermaidLink(AutomationElement renderer)
    {
        bool ready = Retry.WhileFalse(
            () => renderer.FindAllDescendants().Any(element =>
                element.ControlType == ControlType.Hyperlink &&
                (element.Name ?? string.Empty).Contains(LinkName, StringComparison.Ordinal)),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(
            ready,
            "native Mermaid hyperlink did not appear in the UIA tree; document=" +
            GetRendererDocumentText(renderer) +
            "; descendants=" +
            string.Join(" | ", renderer.FindAllDescendants().Select(static element =>
                $"{element.ControlType}/{NameOrEmpty(element)}/id={AutomationIdOrEmpty(element)}/class={ClassNameOrEmpty(element)}")));
    }

    /// <summary>
    /// Checks that the page committed without diagnostics and with every
    /// diagram, and returns the renderer's descendants for the link lookup.
    /// </summary>
    private static AutomationElement[] AssertMermaidPageCommitted(Window window, AutomationElement renderer)
    {
        const int ExpectedDiagramCount = 17;
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
        return pageDescendants;
    }

    /// <summary>
    /// Checks the link's child-granular TextPattern range, its Diagram style
    /// attributes and point hit testing, and returns its unscaled font size
    /// and bounds.
    /// </summary>
    private static (double FontSize, System.Drawing.Rectangle Bounds) AssertLinkTextSemantics(AutomationElement renderer, AutomationElement link)
    {
        var linkBounds = link.BoundingRectangle;
        Assert(linkBounds.Width > 1 && linkBounds.Height > 1,
            $"Mermaid hyperlink must expose non-empty hit-test bounds, got {FormatRect(linkBounds)}");
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var attributes = renderer.Automation.TextAttributeLibrary;
        var linkRange = textPattern.RangeFromChild(link);
        Assert(
            string.Equals(linkRange.GetText(-1).Trim(), LinkName, StringComparison.Ordinal),
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
            formatRange.GetText(-1).Contains(LinkName, StringComparison.Ordinal) &&
            !formatRange.GetText(-1).Contains("Native scene", StringComparison.Ordinal),
            "TextUnit.Format must stop at the semantic vector-child boundary");
        var pointRange = textPattern.RangeFromPoint(new System.Drawing.Point(
            linkBounds.Left + linkBounds.Width / 3,
            linkBounds.Top + linkBounds.Height / 2));
        pointRange.ExpandToEnclosingUnit(TextUnit.Format);
        Assert(
            pointRange.GetText(-1).Contains(LinkName, StringComparison.Ordinal),
            "TextPattern.RangeFromPoint must resolve linked-only Mermaid semantics");
        return (normalDiagramFontSize, linkBounds);
    }

    /// <summary>
    /// Turns on 200% text scale and waits for the link's font size and bounds
    /// to grow, then turns it off and waits for the theme commit that ends the
    /// rebuild, so later pointer probes read settled bounds.
    /// </summary>
    private static void AssertTextScaleResizesVectorScene(
        Window window,
        AutomationElement renderer,
        double normalDiagramFontSize,
        System.Drawing.Rectangle linkBounds)
    {
        var attributes = renderer.Automation.TextAttributeLibrary;
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
    }

    /// <summary>
    /// Activates the link by pointer, by UIA Invoke and by keyboard, and checks
    /// that each reaches the host LinkClick event with its own input kind.
    /// </summary>
    private static void AssertLinkActivationPaths(Window window, AutomationElement link)
    {
        var linkBounds = link.BoundingRectangle;
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
        bool pointerActivated = WaitForLinkActivation(window, "Mouse");
        Assert(
            pointerActivated,
            "pointer hit testing did not raise the renderer's host LinkClick event; " +
            $"bounds={linkBounds}, point={linkPoint}, status='{FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name}'");

        link.Patterns.Invoke.Pattern.Invoke();

        bool activated = WaitForLinkActivation(window, "Automation");
        Assert(activated, "UIA Invoke did not raise the renderer's host LinkClick event");

        link.Focus();
        bool focused = Retry.WhileFalse(
            () => link.Properties.HasKeyboardFocus.ValueOrDefault,
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(focused, "UIA SetFocus did not report keyboard focus on the Mermaid semantic hyperlink");
        Keyboard.Press(VirtualKeyShort.RETURN);
        bool keyboardActivated = WaitForLinkActivation(window, "Keyboard");
        Assert(keyboardActivated, "keyboard activation did not use the same Mermaid link path as pointer/UIA");
    }

    private static bool WaitForLinkActivation(Window window, string inputKind) =>
        Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains($"link:{inputKind}:https://example.invalid/mermaid-node", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(50)).Result;

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
            AssertHighContrastTextAttributes(renderer, linked, plain);

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

    private static void AssertHighContrastTextAttributes(
        AutomationElement renderer,
        AutomationElement linked,
        AutomationElement plain)
    {
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

        var interior = HighContrastVectorPixels.Measure(bitmap, expectHotlight);
        Assert(interior.Width > 0 && interior.Height > 0,
            $"High Contrast vector capture has no testable interior: {bitmap.Width}x{bitmap.Height}");

        int interiorPixels = interior.PixelCount;
        Assert(interior.SurfacePixels >= Math.Max(16, interiorPixels / 5),
            $"High Contrast vector surface is not visibly present in the capture interior; " +
            $"black-like={interior.SurfacePixels}/{interiorPixels}. " +
            "A solid foreground rectangle must fail this rendered-pixel gate.");
        Assert(interior.SemanticPixels >= Math.Max(8, (interior.Width + interior.Height) / 8),
            $"High Contrast vector {(expectHotlight ? "Hotlight" : "WindowText")} glyph paint is not visibly present " +
            $"inside the focus-border mask; semantic={interior.SemanticPixels}/{interiorPixels}");

        System.Drawing.Color surface = interior.AverageSurface;
        System.Drawing.Color foreground = interior.AverageSemantic;
        double contrast = ColorContrast.Ratio(surface, foreground);
        Assert(contrast >= 4.5,
            $"Rendered High Contrast vector contrast {contrast:F2}:1 is below 4.5:1; " +
            $"surface={surface}, foreground={foreground}");
        Console.WriteLine(
            $"[automation] rendered HC vector interior pixels: {fileName}; inset={interior.Inset}; " +
            $"surface={interior.SurfacePixels}/{interiorPixels}; semantic={interior.SemanticPixels}/{interiorPixels}; " +
            $"contrast={contrast:F2}:1; screenshot={path}");
    }

    private static AutomationElement? FindMermaidLink(AutomationElement renderer) =>
        renderer.FindAllDescendants().FirstOrDefault(element =>
            element.ControlType == ControlType.Hyperlink &&
            (element.Name ?? string.Empty).Contains(LinkName, StringComparison.Ordinal));
}
