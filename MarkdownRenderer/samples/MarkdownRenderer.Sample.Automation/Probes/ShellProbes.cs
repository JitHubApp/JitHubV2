using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using static MarkdownRenderer.Sample.Automation.Uia.AuditScreenshots;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;
using static MarkdownRenderer.Sample.Automation.Uia.TextAttributeValues;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes the sample shell: the automation tree, RTL, side navigation, navigation reset and text scale.
/// </summary>
internal static class ShellProbes
{
    internal static void ProbeAutomationTreeShape(Window window)
    {
        var renderer = FindRenderer(window);
        var name = renderer.Name ?? string.Empty;
        Assert(!string.IsNullOrWhiteSpace(name), "renderer.Name must expose a short document label");
        Assert(name.Length <= 120, $"renderer.Name must stay short so Narrator reads content through TextPattern, got length {name.Length}");
        try
        {
            var landmarkType = renderer.Properties.LandmarkType.ValueOrDefault;
            Assert(!string.Equals(landmarkType.ToString(), "Custom", StringComparison.OrdinalIgnoreCase),
                $"renderer must not expose itself as a custom landmark, got LandmarkType={landmarkType}");
        }
        catch (NotSupportedException)
        {
            // UIA providers may omit LandmarkType entirely; that is acceptable
            // and preferable to exposing this document as a custom landmark.
        }
        Assert(renderer.Properties.IsControlElement.ValueOrDefault,
            "renderer must be present in the UIA control view");
        Assert(renderer.Properties.IsContentElement.ValueOrDefault,
            "renderer must be present in the UIA content view");
        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Length > 0, "renderer must expose block peers as descendants");
    }

    internal static void ProbeRtlToggle(Window window)
    {
        var rtl = window.FindFirstDescendant(cf => cf.ByAutomationId("RtlToggle"))?.AsToggleButton()
                  ?? throw new InvalidOperationException("RtlToggle not found");
        Assert(rtl.ToggleState == ToggleState.Off, "RTL must start OFF");
        Assert(ReadFlowDirection(window) == "ltr", "renderer must start LTR");

        rtl.Toggle();
        Thread.Sleep(350);
        Assert(rtl.ToggleState == ToggleState.On, "RTL must report ON after toggling");
        Assert(ReadFlowDirection(window) == "rtl", "renderer FlowDirection mirror must be 'rtl' after toggle");

        rtl.Toggle();
        Thread.Sleep(350);
        Assert(rtl.ToggleState == ToggleState.Off, "RTL must return to OFF after re-toggling");
        Assert(ReadFlowDirection(window) == "ltr", "renderer FlowDirection mirror must be 'ltr' after re-toggle");
    }

    private static string ReadFlowDirection(Window window)
    {
        var el = FindFirstRawDescendantByAutomationId(window, "FlowDirectionStatus");
        string? text = el?.Name ?? el?.Properties.Name.ValueOrDefault;
        if (string.IsNullOrEmpty(text)) return string.Empty;
        const string prefix = "flow:";
        int idx = text.IndexOf(prefix, StringComparison.Ordinal);
        return idx < 0 ? string.Empty : text.Substring(idx + prefix.Length).Trim();
    }

    internal static void ProbeSampleNavigation(Window window)
    {
        Assert(
            window.FindFirstDescendant(cf => cf.ByAutomationId("SampleNavigation")) is not null,
            "SampleNavigation not found in automation tree");

        AutomationElement pageTitle = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SamplePageTitle"))
            ?? throw new InvalidOperationException("SamplePageTitle not found in automation tree");
        Assert(pageTitle.Properties.HeadingLevel.ValueOrDefault == HeadingLevel.Level1,
            "SamplePageTitle must expose heading level 1");

        foreach (string statusId in new[]
                 {
                     "RealizedEmbedCount", "FlowDirectionStatus", "HighContrastStatus",
                     "TextScaleStatus", "LinkActivationStatus", "ThemeStatus", "CurrentSamplePage",
                 })
        {
            AutomationElement status = FindFirstRawDescendantByAutomationId(window, statusId)
                ?? throw new InvalidOperationException($"{statusId} raw automation status not found");
            Assert(!status.Properties.IsControlElement.ValueOrDefault &&
                   !status.Properties.IsContentElement.ValueOrDefault,
                $"{statusId} must remain outside the assistive-technology control/content views");
        }

        string[] expected =
        {
            "FullDemo", "Typography", "Lists", "Tables", "Code", "Images",
            "GitHubAlerts", "Footnotes", "MarkdownExtra", "Html", "Math",
            "Mermaid", "Diagrams", "Embeds", "Selection", "KeyboardNav",
            "Rtl", "LazyImages", "ScrollAnchor", "Virtualization", "Stress",
            "AccessibilityLab", "AuditMatrix",
        };
        foreach (string key in expected)
        {
            var item = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleNav_" + key))
                ?? throw new InvalidOperationException($"SampleNav_{key} not found in automation tree");
            Assert(item.Patterns.SelectionItem.IsSupported,
                $"SampleNav_{key} must expose SelectionItem semantics");
        }
    }

    internal static void ProbeNavigationResetsDocumentPosition(Window window)
    {
        SelectSample(window, "FullDemo");
        AutomationElement renderer = FindRenderer(window);
        var scroll = renderer.Patterns.Scroll.PatternOrDefault
                     ?? throw new InvalidOperationException("Markdown renderer must expose ScrollPattern");
        Assert(scroll.VerticallyScrollable.ValueOrDefault,
            "Full demo must be vertically scrollable for the navigation reset probe");

        scroll.SetScrollPercent(-1, 100);
        bool reachedBottom = Retry.WhileFalse(
            () => scroll.VerticalScrollPercent.ValueOrDefault >= 95,
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(reachedBottom, "Could not move the Full demo preview to the bottom");

        SelectSample(window, "Mermaid");
        bool rendered = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains("Native Mermaid", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(rendered, "Mermaid page did not render after navigation");

        bool reset = Retry.WhileFalse(
            () =>
            {
                double percent = scroll.VerticalScrollPercent.ValueOrDefault;
                return percent < 0 || percent <= 0.5;
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(reset, "Navigation carried the previous document's scroll position into Mermaid");
    }

    internal static void ProbeTextScale(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1000);

        var renderer = FindRenderer(window);
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var attributes = renderer.Automation.TextAttributeLibrary;
        var range = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                    ?? throw new InvalidOperationException("text-scale marker range not found");
        Assert(TryAttributeNumber(range.GetAttributeValue(attributes.FontSize), out double normalSize),
            "normal text FontSize attribute is unavailable");

        var toggle = window.FindFirstDescendant(cf => cf.ByAutomationId("TextScaleToggle"))?.AsToggleButton()
                     ?? throw new InvalidOperationException("TextScaleToggle not found");
        try
        {
            if (toggle.ToggleState != ToggleState.On)
                toggle.Toggle();

            bool scaled = Retry.WhileFalse(() =>
                {
                    var currentRenderer = FindRenderer(window);
                    var currentPattern = currentRenderer.Patterns.Text.PatternOrDefault;
                    var currentRange = currentPattern?.DocumentRange.FindText("quick", backward: false, ignoreCase: true);
                    return currentRange is not null &&
                           TryAttributeNumber(currentRange.GetAttributeValue(currentRenderer.Automation.TextAttributeLibrary.FontSize), out double size) &&
                           size >= normalSize * 1.8;
                },
                timeout: TimeSpan.FromSeconds(8),
                interval: TimeSpan.FromMilliseconds(150)).Result;
            Assert(scaled, "200% text scale did not reflow the rendered document");

            var status = FindFirstRawDescendantByAutomationId(window, "TextScaleStatus");
            Assert((status?.Name ?? string.Empty).Contains("text-scale:2", StringComparison.Ordinal),
                "text scale status did not report the active 200% scale");
            CaptureAuditScreenshot(FindRenderer(window), "accessibility-text-scale-200.png");
        }
        finally
        {
            if (toggle.ToggleState == ToggleState.On)
            {
                toggle.Toggle();
                Thread.Sleep(600);
            }
        }
    }
}
