using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;
using static MarkdownRenderer.Sample.Automation.Uia.TextAttributeValues;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes the Accessibility Lab's TextPattern, semantic roles, text attributes and forced High Contrast colors.
/// </summary>
internal static class AccessibilityLabTextProbes
{
    internal static void ProbeAccessibilityLabTextPattern(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        string text = textPattern.DocumentRange.GetText(-1);
        Assert(text.Contains("Accessibility Lab", StringComparison.Ordinal),
            "TextPattern document text must include the lab heading");
        Assert(text.Contains("quick brown fox", StringComparison.OrdinalIgnoreCase),
            "TextPattern document text must include paragraph content");
        Assert(text.Contains("Console.WriteLine", StringComparison.Ordinal),
            "TextPattern document text must include fenced code content");
        Assert(text.Contains("native hosted fallback marker", StringComparison.Ordinal),
            "a declined hosted factory key must restore the original native markdown block");

        AssertWordRangeGeometryAndEndpoints(renderer, textPattern);
        AssertParagraphPeerTextPattern(renderer, textPattern);
        AssertWordMovementAndVisibleRanges(renderer, textPattern);

        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Any(e => e.ControlType == ControlType.Hyperlink),
            "Accessibility lab must expose hyperlink descendants for TextPattern clients");
    }

    private static void AssertWordRangeGeometryAndEndpoints(
        AutomationElement renderer,
        FlaUI.Core.Patterns.ITextPattern textPattern)
    {
        var word = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                   ?? throw new InvalidOperationException("TextPattern FindText('quick') returned null");
        word.ExpandToEnclosingUnit(TextUnit.Word);
        word.ScrollIntoView(alignToTop: true);
        Thread.Sleep(250);
        var rects = word.GetBoundingRectangles();
        Assert(rects.Length > 0, "TextPattern word range must expose at least one bounding rectangle");
        Assert(rects.All(r => r.Width < renderer.BoundingRectangle.Width / 3 &&
                              r.Height < renderer.BoundingRectangle.Height / 3),
            "TextPattern word bounding rectangles must be glyph/line-sized, not renderer-sized");

        var endpointRange = textPattern.DocumentRange.Clone();
        endpointRange.MoveEndpointByRange(TextPatternRangeEndpoint.Start, word, TextPatternRangeEndpoint.Start);
        endpointRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, word, TextPatternRangeEndpoint.Start);
        int endpointMoved = endpointRange.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Word, 1);
        Assert(endpointMoved == 1, $"MoveEndpointByUnit(Start, Word, 1) from a collapsed range should move one word, moved={endpointMoved}");
        Assert(endpointRange.CompareEndpoints(TextPatternRangeEndpoint.Start, endpointRange, TextPatternRangeEndpoint.End) == 0,
            "MoveEndpointByUnit must keep the range valid by collapsing the opposite endpoint when Start crosses End");
    }

    private static void AssertParagraphPeerTextPattern(
        AutomationElement renderer,
        FlaUI.Core.Patterns.ITextPattern textPattern)
    {
        var blockTextPeer = renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(e => (e.Name ?? string.Empty).Contains("quick brown fox", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Accessibility lab paragraph text peer not found");
        var blockTextPattern = blockTextPeer.Patterns.Text.PatternOrDefault
                               ?? throw new InvalidOperationException("Paragraph text peer must expose TextPattern for Narrator word highlighting");
        var blockWord = blockTextPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                        ?? throw new InvalidOperationException("Paragraph TextPattern FindText('quick') returned null");
        blockWord.ExpandToEnclosingUnit(TextUnit.Word);
        var blockWordRects = blockWord.GetBoundingRectangles();
        Assert(blockWordRects.Length > 0, "Paragraph TextPattern word range must expose bounding rectangles");
        Assert(blockWordRects.All(r => r.Width < blockTextPeer.BoundingRectangle.Width / 2 &&
                                       r.Height < blockTextPeer.BoundingRectangle.Height),
            "Paragraph TextPattern word rectangles must be smaller than the paragraph peer rectangle");
        AutomationElement wordEnclosing = blockWord.GetEnclosingElement();
        Assert(
            string.Equals(
                AutomationIdOrEmpty(wordEnclosing),
                AutomationIdOrEmpty(blockTextPeer),
                StringComparison.Ordinal),
            "TextPattern ranges must return their innermost semantic block as GetEnclosingElement");

        var page = blockWord.Clone();
        page.ExpandToEnclosingUnit(TextUnit.Page);
        Assert(
            page.Compare(textPattern.DocumentRange),
            "unsupported TextUnit.Page must promote to the supported Document unit");
    }

    private static void AssertWordMovementAndVisibleRanges(
        AutomationElement renderer,
        FlaUI.Core.Patterns.ITextPattern textPattern)
    {
        var movedWord = textPattern.DocumentRange.Clone();
        int moved = movedWord.Move(TextUnit.Word, 1);
        Assert(moved == 1, $"TextPattern Move(Word, 1) should move by one word, moved={moved}");
        string movedWordText = movedWord.GetText(-1).Trim();
        Assert(!string.IsNullOrWhiteSpace(movedWordText) &&
               movedWordText.Length <= 32 &&
               !movedWordText.Contains('\n'),
            $"TextPattern Move(Word, 1) must produce a word-sized range, got '{movedWordText}'");
        movedWord.ScrollIntoView(alignToTop: true);
        Thread.Sleep(250);
        var movedWordRects = movedWord.GetBoundingRectangles();
        Assert(movedWordRects.Length > 0, "Moved word range must expose bounding rectangles");
        Assert(movedWordRects.Sum(r => r.Width) < renderer.BoundingRectangle.Width / 2,
            "Moved word range bounding rectangles must be word-sized, not whole-document-sized");

        var visible = textPattern.GetVisibleRanges();
        Assert(visible.Length > 0, "TextPattern must expose visible ranges");
        Assert(visible[0].GetText(200).Length > 0, "Visible TextPattern range must contain text");

        var offscreen = textPattern.DocumentRange.FindText("Paragraph 5", backward: false, ignoreCase: false)
                        ?? throw new InvalidOperationException("offscreen paragraph range not found");
        offscreen.ScrollIntoView(alignToTop: false);
        Thread.Sleep(500);
        var offscreenRects = offscreen.GetBoundingRectangles();
        Assert(offscreenRects.Any(r => r.Width > 0 && r.Height > 0 && renderer.BoundingRectangle.IntersectsWith(r)),
            "TextPattern.ScrollIntoView must bring the requested offscreen range into the renderer viewport");
    }

    internal static void ProbeAccessibilityLabSemanticRoles(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Any(e => e.ControlType == ControlType.Header), "Accessibility lab must expose heading/header peers");
        var hyperlinks = descendants.Where(e => e.ControlType == ControlType.Hyperlink).ToArray();
        Assert(hyperlinks.Length > 0, "Accessibility lab must expose hyperlink peers");
        Assert(hyperlinks.All(link => link.FindAllChildren().Length == 0),
            "Synthetic hyperlink peers must remain semantic leaves and must not cycle into the renderer visual tree");
        Assert(descendants.Any(e => e.ControlType == ControlType.List), "Accessibility lab must expose list peer");
        Assert(descendants.Any(e => e.ControlType == ControlType.ListItem), "Accessibility lab must expose list item peers");
        Assert(descendants.Any(e => e.ControlType == ControlType.Table), "Accessibility lab must expose table peer");
        Assert(descendants.Any(e => e.ControlType == ControlType.DataItem), "Accessibility lab must expose table cell peers");
        Assert(descendants.Count(e => e.ControlType == ControlType.Image) >= 2,
            "Accessibility lab must expose both block and inline image peers");
        Assert(descendants.Any(e => e.ControlType == ControlType.Image &&
                                    (e.Name ?? string.Empty).Contains("Inline accessibility icon", StringComparison.Ordinal)),
            "Inline markdown image must expose an image peer instead of flattening to plain paragraph text");
        Assert(descendants.Any(e => e.ControlType == ControlType.Button && (e.Name ?? string.Empty).Contains("Native action", StringComparison.Ordinal)),
            "Accessibility lab must expose hosted native button");
        Assert(descendants.All(e => e.ControlType != ControlType.CheckBox),
            "Accessibility lab read-only task state must not expose checkbox semantics");

        AutomationElement ordinaryList = descendants.First(e => e.ControlType == ControlType.List);
        Assert(!ordinaryList.Properties.IsKeyboardFocusable.ValueOrDefault,
            "ordinary synthetic semantic nodes must not inherit renderer focusability");
        renderer.Focus();
        ordinaryList.Focus();
        Assert(!ordinaryList.Properties.HasKeyboardFocus.ValueOrDefault,
            "SetFocus on an ordinary synthetic semantic node must not focus its shared renderer owner");

        var table = descendants.First(e => e.ControlType == ControlType.Table);
        var grid = table.Patterns.Grid.PatternOrDefault
                   ?? throw new InvalidOperationException("Table peer must expose GridPattern");
        Assert(grid.RowCount.Value >= 4, "GridPattern RowCount must include header and body rows");
        Assert(grid.ColumnCount.Value == 3, "GridPattern ColumnCount must be 3 for the lab table");
        Assert(grid.GetItem(0, 0) is not null, "GridPattern.GetItem(0, 0) must return a cell");
    }

    internal static void ProbeAccessibilityLabTextAttributes(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var attributes = renderer.Automation.TextAttributeLibrary;
        var descendants = renderer.FindAllDescendants();

        var hyperlink = descendants.First(e => e.ControlType == ControlType.Hyperlink);
        AssertChildRanges(textPattern, descendants, hyperlink);
        AssertCustomStyleAttributes(textPattern, attributes);
        object underline = AssertLinkCodeAndReadOnlyAttributes(textPattern, attributes);

        var findUnderline = textPattern.DocumentRange.FindAttribute(attributes.UnderlineStyle, underline, backward: false)
                            ?? throw new InvalidOperationException("FindAttribute(UnderlineStyle) returned null");
        Assert(findUnderline.GetText(-1).Contains("painted link", StringComparison.Ordinal) ||
               findUnderline.GetText(-1).Contains("second painted link", StringComparison.Ordinal),
            "FindAttribute(UnderlineStyle) must return an underlined markdown link range");

        AssertCaretActivityFollowsChildFocus(renderer, hyperlink, attributes);
    }

    /// <summary>
    /// Checks RangeFromChild for a hyperlink, an image and a hosted button, and
    /// that GetChildren returns only immediate inline children.
    /// </summary>
    private static void AssertChildRanges(
        FlaUI.Core.Patterns.ITextPattern textPattern,
        AutomationElement[] descendants,
        AutomationElement hyperlink)
    {
        var hyperlinkRange = textPattern.RangeFromChild(hyperlink);
        Assert(hyperlinkRange.GetText(-1).Contains(hyperlink.Name ?? string.Empty, StringComparison.Ordinal),
            "TextPattern.RangeFromChild(hyperlink) must return the hyperlink text range");
        Assert(
            string.Equals(
                AutomationIdOrEmpty(hyperlinkRange.GetEnclosingElement()),
                AutomationIdOrEmpty(hyperlink),
                StringComparison.Ordinal),
            "an exact hyperlink range must return the hyperlink as its innermost enclosing element");
        AutomationElement hyperlinkParent = hyperlink.Parent
            ?? throw new InvalidOperationException("hyperlink semantic parent not found");
        var parentTextPattern = hyperlinkParent.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException("hyperlink parent must expose TextPattern");
        AutomationElement[] immediateChildren = parentTextPattern.DocumentRange.GetChildren();
        Assert(
            immediateChildren.Any(child => string.Equals(
                AutomationIdOrEmpty(child),
                AutomationIdOrEmpty(hyperlink),
                StringComparison.Ordinal)),
            "TextPattern.GetChildren must return immediate inline semantic children");
        Assert(
            immediateChildren.All(child =>
                child.Parent is { } parent
                && string.Equals(
                    AutomationIdOrEmpty(parent),
                    AutomationIdOrEmpty(hyperlinkParent),
                    StringComparison.Ordinal)),
            "TextPattern.GetChildren must not flatten deeper semantic descendants");

        var image = descendants.First(e => e.ControlType == ControlType.Image &&
                                           (e.Name ?? string.Empty).Contains("Accessibility lab blue square", StringComparison.Ordinal));
        var imageRange = textPattern.RangeFromChild(image);
        Assert(imageRange.GetText(-1).Contains("Accessibility lab blue square", StringComparison.Ordinal),
            "TextPattern.RangeFromChild(image) must return image alt text");

        var hostedButton = descendants.First(e => e.ControlType == ControlType.Button &&
            (e.Name ?? string.Empty).Contains("Native action", StringComparison.Ordinal));
        var hostedButtonRange = textPattern.RangeFromChild(hostedButton);
        Assert(hostedButtonRange.GetText(-1).Length > 0,
            "TextPattern.RangeFromChild(hosted native button) must return its embedded-object placeholder range");
    }

    private static void AssertCustomStyleAttributes(
        FlaUI.Core.Patterns.ITextPattern textPattern,
        FlaUI.Core.ITextAttributeLibrary attributes)
    {
        var customStyleRange = textPattern.DocumentRange.FindText(
            "Ancestor-scoped extension style",
            backward: false,
            ignoreCase: false)
            ?? throw new InvalidOperationException("custom extension style marker range not found");
        Assert(
            string.Equals(
                customStyleRange.GetAttributeValue(attributes.StyleName)?.ToString(),
                "SampleExtensionCallout",
                StringComparison.Ordinal),
            "a non-built-in extension role must remain visible through UIA StyleName");
        Assert(
            TryAttributeNumber(customStyleRange.GetAttributeValue(attributes.FontSize), out double customFontSize) &&
            Math.Abs(customFontSize - 19d) < 0.1d,
            $"ancestor-scoped custom role FontSize must be 19, got {customFontSize}");
        Assert(
            ToColorRefValue(customStyleRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0x8A, 0x2B, 0xE2),
            "ancestor-scoped custom role foreground was not applied");
    }

    /// <summary>
    /// Checks link underline and style, code font and read-only attributes,
    /// and returns the painted link's UnderlineStyle value.
    /// </summary>
    private static object AssertLinkCodeAndReadOnlyAttributes(
        FlaUI.Core.Patterns.ITextPattern textPattern,
        FlaUI.Core.ITextAttributeLibrary attributes)
    {
        var paintedLinkRange = textPattern.DocumentRange.FindText("painted link", backward: false, ignoreCase: false)
                               ?? throw new InvalidOperationException("painted link range not found");
        var underline = paintedLinkRange.GetAttributeValue(attributes.UnderlineStyle);
        Assert(IsNonNoneTextDecoration(underline),
            $"painted link must expose a non-None UnderlineStyle text attribute, got {DescribeAttributeValue(underline)}");
        Assert((paintedLinkRange.GetAttributeValue(attributes.StyleName)?.ToString() ?? string.Empty)
               .Contains("Link", StringComparison.Ordinal),
            "painted link must expose StyleName=Link");
        var collapsedLinkStart = paintedLinkRange.Clone();
        collapsedLinkStart.MoveEndpointByRange(
            TextPatternRangeEndpoint.End,
            collapsedLinkStart,
            TextPatternRangeEndpoint.Start);
        Assert(
            IsNonNoneTextDecoration(collapsedLinkStart.GetAttributeValue(attributes.UnderlineStyle)),
            "a collapsed caret at a half-open format boundary must choose the following run, not Mixed");

        var codeRange = textPattern.DocumentRange.FindText("Console.WriteLine", backward: false, ignoreCase: false)
                        ?? throw new InvalidOperationException("code range not found");
        string fontName = codeRange.GetAttributeValue(attributes.FontName)?.ToString() ?? string.Empty;
        Assert(fontName.Contains("Consolas", StringComparison.OrdinalIgnoreCase),
            $"code range must expose monospace FontName, got {fontName}");

        var quickRange = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                         ?? throw new InvalidOperationException("quick range not found");
        Assert(IsTrueAttribute(quickRange.GetAttributeValue(attributes.IsReadOnly)),
            "TextPattern ranges must expose IsReadOnly=true");
        return underline;
    }

    private static void AssertCaretActivityFollowsChildFocus(
        AutomationElement renderer,
        AutomationElement hyperlink,
        FlaUI.Core.ITextAttributeLibrary attributes)
    {
        var text2 = renderer.Patterns.Text2.PatternOrDefault
                    ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern2");
        renderer.Focus();
        _ = text2.GetCaretRange(out bool documentCaretActive);
        Assert(documentCaretActive, "focused selectable document must expose an active caret range");
        hyperlink.Focus();
        Retry.WhileFalse(
            () => hyperlink.Properties.HasKeyboardFocus.ValueOrDefault,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(50));
        Assert(hyperlink.Properties.HasKeyboardFocus.ValueOrDefault,
            "the virtual hyperlink must own UIA keyboard focus before caret activity is evaluated");
        Thread.Sleep(500);
        Assert(hyperlink.Properties.HasKeyboardFocus.ValueOrDefault,
            "the virtual hyperlink must retain UIA keyboard focus through deferred layout/focus notifications");
        var childCaretRange = text2.GetCaretRange(out _);
        Assert(
            !IsTrueAttribute(childCaretRange.GetAttributeValue(attributes.IsActive)),
            "the caret range returned by TextPattern2 must expose IsActive=false while a virtual hyperlink child owns keyboard focus");
    }

    internal static void ProbeAccessibilityLabForcedHighContrast(Window window)
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

        SelectSample(window, "AccessibilityLab");
            Thread.Sleep(1200);

            var status = FindFirstRawDescendantByAutomationId(window, "HighContrastStatus");
            var statusText = status?.Name ?? status?.Properties.Name.ValueOrDefault ?? string.Empty;
            Assert(statusText.Contains("hc:on", StringComparison.Ordinal),
                $"forced high contrast status must be on, got '{statusText}'");

            var renderer = FindRenderer(window);
            var textPattern = renderer.Patterns.Text.PatternOrDefault
                              ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
            var attributes = renderer.Automation.TextAttributeLibrary;

            var bodyRange = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                            ?? throw new InvalidOperationException("quick range not found");
            Assert(ToColorRefValue(bodyRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0xFF, 0xFF, 0xFF),
                "forced high contrast body foreground must resolve to WindowText");
            Assert(ToColorRefValue(bodyRange.GetAttributeValue(attributes.BackgroundColor)) == ColorRef(0x00, 0x00, 0x00),
                "forced high contrast body background must resolve to Window");

            var linkRange = textPattern.DocumentRange.FindText("painted link", backward: false, ignoreCase: false)
                            ?? throw new InvalidOperationException("painted link range not found");
            Assert(ToColorRefValue(linkRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0x00, 0xFF, 0xFF),
                "forced high contrast link foreground must resolve to Hotlight");

            var inlineCodeRange = textPattern.DocumentRange.FindText("inline-code-token", backward: false, ignoreCase: false)
                                  ?? throw new InvalidOperationException("inline code range not found");
            Assert(ToColorRefValue(inlineCodeRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0xFF, 0xFF, 0xFF),
                "forced high contrast inline code foreground must resolve to WindowText");
            Assert(ToColorRefValue(inlineCodeRange.GetAttributeValue(attributes.BackgroundColor)) == ColorRef(0x00, 0x00, 0x00),
                "forced high contrast inline code background must resolve to Window");

            var customStyleRange = textPattern.DocumentRange.FindText(
                "Ancestor-scoped extension style",
                backward: false,
                ignoreCase: false)
                ?? throw new InvalidOperationException("custom extension style marker range not found in high contrast");
            Assert(
                TryAttributeNumber(customStyleRange.GetAttributeValue(attributes.FontSize), out double customFontSize) &&
                Math.Abs(customFontSize - 23d) < 0.1d,
                $"High Contrast theme-dictionary FontSize must be 23, got {customFontSize}");
            Assert(
                ToColorRefValue(customStyleRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0xFF, 0xFF, 0xFF),
                "High Contrast must map the custom authored foreground to WindowText");

            var tableHeaderRange = textPattern.DocumentRange.FindText("Feature", backward: false, ignoreCase: false)
                                   ?? throw new InvalidOperationException("table header range not found");
            Assert(ToColorRefValue(tableHeaderRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0x00, 0x00, 0x00),
                "forced high contrast table header foreground must resolve to HighlightText");
            Assert(ToColorRefValue(tableHeaderRange.GetAttributeValue(attributes.BackgroundColor)) == ColorRef(0xFF, 0xFF, 0x00),
                "forced high contrast table header background must resolve to Highlight");
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
}
