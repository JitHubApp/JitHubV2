using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using static MarkdownRenderer.Sample.Automation.Uia.AuditScreenshots;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes the audit matrix page: hostile content, diagnostics and plain horizontal overflow.
/// </summary>
internal static class AuditMatrixProbes
{
    internal static void ProbeAuditMatrixHostileContent(Window window)
    {
        var stopwatch = Stopwatch.StartNew();
        SelectSample(window, "AuditMatrix");

        var renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains(
                "Document remains responsive after hostile content",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(8),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        stopwatch.Stop();
        Assert(ready, "audit matrix did not finish rendering its final marker");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(8),
            $"audit matrix first render exceeded the interaction budget: {stopwatch.Elapsed.TotalMilliseconds:0} ms");

        string documentText = GetRendererDocumentText(renderer);
        Assert(documentText.Contains("Completed audit task", StringComparison.Ordinal),
            "audit matrix task-list content is missing from TextPattern");
        Assert(documentText.Contains("Expected behavior", StringComparison.Ordinal),
            "audit matrix table content is missing from TextPattern");
        Assert(documentText.Contains("public static string Audit", StringComparison.Ordinal),
            "audit matrix code content is missing from TextPattern");
        Assert(documentText.Contains("\\notacommand{sample}", StringComparison.Ordinal),
            "audit matrix must preserve the invalid formula as exact fallback source");
        Assert(documentText.Contains("layout: elk", StringComparison.Ordinal),
            "audit matrix must preserve the unsupported ELK request as exact fallback source");

        var descendants = renderer.FindAllDescendants();
        Assert(descendants.All(e => e.ControlType != ControlType.CheckBox),
            "read-only task state must not be exposed as an interactive checkbox");
        Assert(descendants.Any(e => e.ControlType == ControlType.Table),
            "audit matrix must expose native table semantics");
        Assert(descendants.Any(e => e.ControlType == ControlType.Image &&
                                    (e.Name ?? string.Empty).Contains("Safe blue audit square", StringComparison.Ordinal)),
            "audit matrix safe SVG must expose image semantics");

        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var finalMarker = textPattern.DocumentRange.FindText(
            "Document remains responsive after hostile content", backward: false, ignoreCase: false)
            ?? throw new InvalidOperationException("audit matrix final marker range not found");
        finalMarker.ScrollIntoView(alignToTop: false);
        Thread.Sleep(500);
        var finalRects = finalMarker.GetBoundingRectangles();
        Assert(finalRects.Length > 0 && finalRects.All(r => r.Width <= renderer.BoundingRectangle.Width + 2),
            "hostile SVG/HTML caused text geometry to escape the renderer viewport");

        CaptureAuditScreenshot(renderer, "audit-matrix-hostile-content.png");

        AutomationElement diagnostics = WaitForDiagnostics(window, "MATH100");
        string diagnosticText = GetAutomationElementText(diagnostics);
        Assert(diagnosticText.Contains("Error MATH100", StringComparison.Ordinal),
            $"Math diagnostic must expose severity and code, got '{diagnosticText}'");
        string? mermaidDiagnosticLine = diagnosticText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(static line => line.Contains("MMR0002", StringComparison.Ordinal));
        Assert(mermaidDiagnosticLine is not null &&
               mermaidDiagnosticLine.Contains("Error MMR0002", StringComparison.Ordinal) &&
               mermaidDiagnosticLine.Contains("[", StringComparison.Ordinal) &&
               mermaidDiagnosticLine.Contains("..", StringComparison.Ordinal) &&
               mermaidDiagnosticLine.Contains("): ", StringComparison.Ordinal),
            $"Mermaid diagnostic must expose severity, code, and a half-open UTF-16 source span, got '{diagnosticText}'");
        Assert(diagnosticText.Contains("[", StringComparison.Ordinal) &&
               diagnosticText.Contains("..", StringComparison.Ordinal) &&
               diagnosticText.Contains("): ", StringComparison.Ordinal),
            $"Math diagnostic must expose a half-open UTF-16 source span, got '{diagnosticText}'");
        Assert(diagnostics.Patterns.Text.IsSupported,
            "diagnostic details must expose selectable TextPattern content");

        AutomationElement diagnosticsBar = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SampleDiagnostics"))
            ?? throw new InvalidOperationException("open diagnostics InfoBar not found");
        Assert(NameOrEmpty(diagnosticsBar).Contains("MATH100", StringComparison.Ordinal),
            "diagnostics live-region summary must include the actionable diagnostic code");

        using var diagnosticsResolved = new ManualResetEventSlim(false);
        string resolutionNotification = string.Empty;
        var notificationHandler = diagnosticsBar.RegisterNotificationEvent(
            TreeScope.Element,
            (_, kind, processing, displayString, activityId) =>
            {
                if (kind == NotificationKind.ActionCompleted &&
                    processing == NotificationProcessing.MostRecent &&
                    string.Equals(activityId, "RendererDiagnostics", StringComparison.Ordinal))
                {
                    resolutionNotification = displayString;
                    diagnosticsResolved.Set();
                }
            });
        try
        {
            SelectSample(window, "Typography");
            Assert(diagnosticsResolved.Wait(TimeSpan.FromSeconds(5)),
                "clearing committed diagnostics did not raise an accessibility resolution notification");
            Assert(resolutionNotification.Contains("No renderer diagnostics", StringComparison.Ordinal),
                $"diagnostics resolution notification was not actionable, got '{resolutionNotification}'");

            bool cleared = Retry.WhileFalse(
                () =>
                {
                    AutomationElement? current = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("SampleDiagnosticsMessage"));
                    return current is null ||
                           current.IsOffscreen ||
                           string.IsNullOrEmpty(GetAutomationElementText(current));
                },
                timeout: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromMilliseconds(100)).Result;
            Assert(cleared, "audit diagnostics persisted after a valid document committed");
        }
        finally
        {
            diagnosticsBar.FrameworkAutomationElement.UnregisterNotificationEventHandler(notificationHandler);
        }
    }

    internal static void ProbePlainHorizontalOverflowKeyboard(Window window)
    {
        SelectSample(window, "AuditMatrix");
        AutomationElement renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains(
                "Document remains responsive after hostile content",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(8),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, "audit matrix did not finish rendering before the overflow keyboard probe");

        AutomationElement overflow = FindPlainHorizontalOverflow(renderer);
        var scroll = overflow.Patterns.Scroll.PatternOrDefault
                     ?? throw new InvalidOperationException("plain overflow table must expose ScrollPattern");
        scroll.SetScrollPercent(0, -1);
        bool atStart = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) <= 0.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(atStart,
            $"overflow table did not reset to the start, percent={ReadPlainHorizontalScrollPercent(window):0.##}");

        overflow.Focus();
        bool focused = Retry.WhileFalse(
            () => overflow.Properties.HasKeyboardFocus.ValueOrDefault,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(focused, "plain overflow peer did not take keyboard focus");

        Keyboard.Press(VirtualKeyShort.RIGHT);
        bool movedRight = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) > 0.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(movedRight,
            $"Right must scroll a focused plain overflow before spatial navigation, percent={ReadPlainHorizontalScrollPercent(window):0.##}");
        Assert(ReadPlainHorizontalOverflowFocus(window),
            "Right moved focus away from the plain overflow surface");

        Keyboard.Press(VirtualKeyShort.END);
        bool atEnd = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) >= 99.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(atEnd,
            $"End must move plain overflow to its trailing edge, percent={ReadPlainHorizontalScrollPercent(window):0.##}");

        Keyboard.Press(VirtualKeyShort.LEFT);
        bool movedLeft = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) < 99.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(movedLeft,
            $"Left must scroll a focused plain overflow before spatial navigation, percent={ReadPlainHorizontalScrollPercent(window):0.##}");
        Assert(ReadPlainHorizontalOverflowFocus(window),
            "Left moved focus away from the plain overflow surface");

        Keyboard.Press(VirtualKeyShort.HOME);
        bool returnedHome = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) <= 0.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(returnedHome,
            $"Home must move plain overflow to its leading edge, percent={ReadPlainHorizontalScrollPercent(window):0.##}");
    }

    private static AutomationElement FindPlainHorizontalOverflow(AutomationElement renderer) =>
        renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Table))
            .FirstOrDefault(element =>
                element.Patterns.Scroll.PatternOrDefault?.HorizontallyScrollable.ValueOrDefault == true)
        ?? throw new InvalidOperationException(
            "audit matrix must expose a link-free horizontally scrollable table");

    private static double ReadPlainHorizontalScrollPercent(Window window)
    {
        try
        {
            return FindPlainHorizontalOverflow(FindRenderer(window))
                .Patterns.Scroll.PatternOrDefault?
                .HorizontalScrollPercent.ValueOrDefault ?? double.NaN;
        }
        catch (Exception ex) when (
            ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return double.NaN;
        }
    }

    private static bool ReadPlainHorizontalOverflowFocus(Window window)
    {
        try
        {
            return FindPlainHorizontalOverflow(FindRenderer(window))
                .Properties.HasKeyboardFocus.ValueOrDefault;
        }
        catch (Exception ex) when (
            ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }
}
