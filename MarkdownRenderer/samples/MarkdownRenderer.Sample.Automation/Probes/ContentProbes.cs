using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes that sample pages render their content natively and stay responsive.
/// </summary>
internal static class ContentProbes
{
    internal static void ProbeSafeHtmlSample(Window window)
    {
        SelectSample(window, "Html");
        var renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains(
                "Native safe HTML",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, "Safe HTML sample did not render");

        string documentText = GetRendererDocumentText(renderer);
        Assert(documentText.Contains("HTML disclosure body", StringComparison.Ordinal),
            "expanded HTML details content is missing from TextPattern");
        Assert(documentText.Contains("<custom-card>", StringComparison.Ordinal),
            "unknown HTML element was not retained as inert literal source");
        Assert(documentText.Contains(
                "<script>unsafe-script-sentinel</script>",
                StringComparison.Ordinal),
            "tag-filtered script did not remain visible as inert literal source");
        Assert(!documentText.Contains("unsafe-frame-sentinel", StringComparison.Ordinal) &&
               !documentText.Contains("unsafe-form-sentinel", StringComparison.Ordinal),
            "interactive HTML leaked into the rendered document");

        AutomationElement[] descendants = renderer.FindAllDescendants();
        AutomationElement disclosure = descendants.FirstOrDefault(element =>
            ClassNameOrEmpty(element) == "MarkdownDisclosure")
            ?? throw new InvalidOperationException("HTML details summary did not expose MarkdownDisclosure");
        Assert(disclosure.Patterns.ExpandCollapse.IsSupported,
            "HTML details summary must expose ExpandCollapsePattern");
        Assert(descendants.Any(element => ClassNameOrEmpty(element) == "MarkdownTable"),
            "HTML table did not retain native table semantics");
        Assert(descendants.Any(element =>
                ClassNameOrEmpty(element) == "MarkdownLink" &&
                NameOrEmpty(element).Contains("safe HTML example", StringComparison.Ordinal)),
            "safe HTML link did not retain native hyperlink semantics");

        disclosure.Patterns.ExpandCollapse.Pattern.Collapse();
        bool collapsed = Retry.WhileFalse(
            () => !GetRendererDocumentText(renderer).Contains(
                "HTML disclosure body",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(collapsed, "collapsing HTML details did not update the document text surface");

        disclosure = renderer.FindAllDescendants().First(element =>
            ClassNameOrEmpty(element) == "MarkdownDisclosure");
        disclosure.Patterns.ExpandCollapse.Pattern.Expand();
        bool expanded = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains(
                "HTML disclosure body",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(expanded, "expanding HTML details did not restore its document text");
    }

    internal static void ProbeMathSample(Window window)
    {
        const int ExpectedFormulaCount = 27;
        SelectSample(window, "Math");
        AutomationElement renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () =>
            {
                string documentText = GetRendererDocumentText(renderer);
                int nativeFormulaCount = renderer.FindAllDescendants()
                    .Count(element => ClassNameOrEmpty(element) == "MarkdownMath");
                return documentText.Contains("Native mathematics", StringComparison.Ordinal) &&
                       documentText.Contains("Matrix multiplication", StringComparison.Ordinal) &&
                       documentText.Contains("Aligned derivation", StringComparison.Ordinal) &&
                       nativeFormulaCount == ExpectedFormulaCount;
            },
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, $"Math page must expose {ExpectedFormulaCount} native formulas");

        string renderedText = GetRendererDocumentText(renderer);
        Assert(!renderedText.Contains("\\notacommand{sample}", StringComparison.Ordinal),
            "the primary Math page must not contain the invalid-formula diagnostics fixture");
        AutomationElement? diagnostics = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SampleDiagnosticsMessage"));
        Assert(diagnostics is null ||
               diagnostics.IsOffscreen ||
               string.IsNullOrEmpty(GetAutomationElementText(diagnostics)),
            "the primary Math page must commit without renderer diagnostics");
    }

    internal static void ProbeHostLinkSchemeAllowlist(Window window)
    {
        SelectSample(window, "Typography");
        var editor = window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownEditor"))?.AsTextBox()
            ?? throw new InvalidOperationException("Markdown source editor not found");
        editor.Text = "# Host link policy\n\n[Blocked custom URI](jithub-unsafe://example/path)";

        AutomationElement renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains("Blocked custom URI", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, "custom-scheme link did not render for the host policy probe");

        AutomationElement blockedLink = renderer.FindAllDescendants().FirstOrDefault(element =>
            ClassNameOrEmpty(element) == "MarkdownLink" &&
            NameOrEmpty(element).Contains("Blocked custom URI", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("custom-scheme hyperlink peer not found");
        blockedLink.Patterns.Invoke.Pattern.Invoke();

        bool blocked = Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains("jithub-unsafe://example/path::blocked", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(blocked, "sample host did not reject a non-allowlisted URI scheme");

        SelectSample(window, "Lists");
    }

    internal static void ProbeVirtualization(Window window)
    {
        SelectSample(window, "Virtualization");
        var renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains("Embed virtualization", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, "virtualization sample did not finish rendering");

        static string[] NativeButtonIds(AutomationElement document) =>
            document.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(AutomationIdOrEmpty)
                .Where(id => id.EndsWith("-Native", StringComparison.Ordinal))
                .ToArray();

        int realised = ReadRealizedEmbedCount(renderer);
        Assert(realised < 100, $"virtualization expected ≪100 realised embeds, found {realised}");
        Assert(realised > 0, $"virtualization expected some realised embeds, found {realised}");
        string firstButtonId = NativeButtonIds(renderer).FirstOrDefault()
            ?? throw new InvalidOperationException("the initial viewport has no native hosted button");

        var scroll = renderer.Patterns.Scroll.Pattern
            ?? throw new InvalidOperationException("virtualized renderer must expose ScrollPattern");
        Assert(scroll.VerticallyScrollable.ValueOrDefault,
            "virtualized renderer must expose vertical scrolling");
        scroll.SetScrollPercent(-1, 100);
        bool recycled = Retry.WhileFalse(
            () => !NativeButtonIds(renderer).Contains(firstButtonId, StringComparer.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(recycled, "an initial hosted button was not recycled after leaving the viewport");
        int afterScroll = ReadRealizedEmbedCount(renderer);
        Assert(afterScroll < 100, $"virtualization after scroll expected ≪100 realised embeds, found {afterScroll}");
        Assert(afterScroll > 0, "the destination viewport has no native hosted buttons");

        scroll.SetScrollPercent(-1, 0);
        bool returnedToTop = Retry.WhileFalse(
            () => scroll.VerticalScrollPercent.ValueOrDefault is >= 0 and <= 0.5,
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(returnedToTop,
            $"virtualization did not return to the top; scroll percent={scroll.VerticalScrollPercent.ValueOrDefault:0.##}");
        bool restored = Retry.WhileFalse(
            () => NativeButtonIds(renderer).Contains(firstButtonId, StringComparer.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(restored,
            $"the initial hosted button {firstButtonId} was not realized after returning to the top; " +
            $"scroll percent={scroll.VerticalScrollPercent.ValueOrDefault:0.##}");
    }

    internal static void ProbeImagesSample(Window window)
    {
        SelectSample(window, "Images");
        Thread.Sleep(1500);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Images sample renderer document text must not be empty");
    }

    internal static void ProbeLazyImagesSample(Window window)
    {
        SelectSample(window, "LazyImages");
        Thread.Sleep(1500);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Lazy Images sample renderer document text must not be empty");
        // Verify renderer has children (block peers in UIA tree)
        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Length > 0, "Lazy Images renderer must expose block peers as descendants");
    }

    internal static void ProbeScrollAnchorSample(Window window)
    {
        SelectSample(window, "ScrollAnchor");
        Thread.Sleep(1000);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Scroll Anchor sample renderer document text must not be empty");
        // Scroll down and verify content is still available
        renderer.Focus();
        Keyboard.Press(VirtualKeyShort.NEXT); // Page Down
        Thread.Sleep(300);
        Keyboard.Press(VirtualKeyShort.PRIOR); // Page Up
        Thread.Sleep(300);
        var textAfterScroll = GetRendererDocumentText(renderer);
        Assert(textAfterScroll.Length > 0, "Scroll Anchor renderer document text must remain non-empty after scroll");
    }

    internal static void ProbeFootnotesSample(Window window)
    {
        SelectSample(window, "Footnotes");
        Thread.Sleep(1200);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Footnotes sample renderer document text must not be empty");
        // Verify the rendered text contains footnote markers (superscripts / back-arrows)
        // The renderer exposes document text through TextPattern so Narrator can
        // use text ranges instead of reading the root element Name.
        bool hasFootnoteContent = text.Contains("sentence with a footnote", StringComparison.OrdinalIgnoreCase)
                                  || text.Contains("footnote", StringComparison.OrdinalIgnoreCase);
        Assert(hasFootnoteContent, $"Footnotes renderer content must mention 'footnote', got: {text[..Math.Min(120, text.Length)]}");
    }
}
