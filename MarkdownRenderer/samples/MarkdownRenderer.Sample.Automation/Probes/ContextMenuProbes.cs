using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using MarkdownRenderer.Sample.Automation.Native;
using MarkdownRenderer.Sample.Automation.Uia;
using static MarkdownRenderer.Sample.Automation.Native.NativeClipboard;
using static MarkdownRenderer.Sample.Automation.Native.NativeMouse;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;
using static MarkdownRenderer.Sample.Automation.Uia.TextPatternGeometry;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes the selection and target-aware context menu commands through the clipboard.
/// </summary>
internal static class ContextMenuProbes
{
    internal static void ProbeContextMenuCopy(Window window)
    {
        SelectSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var start = FindTextPatternPoint(renderer, "Click and drag", "context-copy selection start");
        var end = FindTextPatternPoint(renderer, "The selection spans", "context-copy selection end");
        DragMouseThrough(start, end);
        Thread.Sleep(400);

        string selected = GetRendererSelectionText(renderer);
        Assert(!string.IsNullOrWhiteSpace(selected),
            "context-copy probe must create a selection before opening the menu");

        const string sentinel = "markdown-renderer-context-copy-sentinel";
        SetClipboardText(sentinel);
        var selectedPoint = new System.Drawing.Point(
            (int)Math.Round(start.X + (end.X - start.X) * 0.5),
            (int)Math.Round(start.Y + (end.Y - start.Y) * 0.5));
        Mouse.MoveTo(selectedPoint);
        Thread.Sleep(100);
        Mouse.RightClick();
        Thread.Sleep(400);

        var copyItem = Retry.WhileNull(() =>
            {
                var inWindow = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(e => string.Equals(e.Name, "Copy", StringComparison.OrdinalIgnoreCase));
                if (inWindow is not null)
                    return inWindow;

                return renderer.Automation.GetDesktop()
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(e => string.Equals(e.Name, "Copy", StringComparison.OrdinalIgnoreCase) && !e.IsOffscreen);
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException("selected markdown context menu did not expose a Copy item");

        var copyMarkdownItem = Retry.WhileNull(() =>
            {
                var inWindow = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(e => string.Equals(e.Name, "Copy as Markdown", StringComparison.OrdinalIgnoreCase));
                if (inWindow is not null)
                    return inWindow;

                return renderer.Automation.GetDesktop()
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(e => string.Equals(e.Name, "Copy as Markdown", StringComparison.OrdinalIgnoreCase) && !e.IsOffscreen);
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException("selected markdown context menu did not expose a Copy as Markdown item");
        Assert(copyMarkdownItem.IsEnabled,
            "Copy as Markdown must be enabled for a non-empty selection");

        copyItem.Click();
        Thread.Sleep(600);

        string copied = GetClipboardText();
        Assert(!string.Equals(copied, sentinel, StringComparison.Ordinal),
            "context-menu Copy left the clipboard unchanged");
        Assert(copied.Contains("select any text", StringComparison.OrdinalIgnoreCase) ||
               copied.Contains("selection spans", StringComparison.OrdinalIgnoreCase),
            $"context-menu Copy should copy selected rendered text, got: {Truncate(copied, 240)}");
        Assert(GetRendererDocumentText(renderer).Length > 0,
            "renderer must remain responsive after context-menu Copy");
    }

    internal static void ProbeTargetAwareContextCommands(Window window)
    {
        SelectSample(window, "AuditMatrix");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        Assert(GetRendererDocumentText(renderer).Contains(
                "Document remains responsive after hostile content",
                StringComparison.Ordinal),
            "target-aware context probe requires the complete audit matrix");

        string copiedLink = InvokeTargetContextCommand(
            window,
            renderer,
            FindTextPatternPointAfterScroll(renderer, "keyboard link", "Copy link target"),
            "MarkdownContextCopyLink",
            "Copy link");
        Assert(string.Equals(copiedLink, "https://example.com/audit-link", StringComparison.Ordinal),
            $"Copy link returned an unexpected target: {Truncate(copiedLink, 240)}");

        string copiedTable = InvokeTargetContextCommand(
            window,
            renderer,
            FindTextPatternPointAfterScroll(renderer, "Surface", "Copy table target"),
            "MarkdownContextCopyTable",
            "Copy table");
        Assert(copiedTable.Contains("Surface\tExpected behavior", StringComparison.Ordinal) &&
               copiedTable.Contains("Table\tRemains readable and selectable", StringComparison.Ordinal),
            $"Copy table did not provide rendered TSV: {Truncate(copiedTable, 320)}");

        string copiedCode = InvokeTargetContextCommand(
            window,
            renderer,
            FindTextPatternPointAfterScroll(renderer, "public static string Audit", "Copy code target"),
            "MarkdownContextCopyCode",
            "Copy code");
        Assert(copiedCode.Contains(
                "public static string Audit() => \"selection and copy remain available\";",
                StringComparison.Ordinal) &&
               !copiedCode.Contains("```", StringComparison.Ordinal),
            $"Copy code did not provide fence-free source: {Truncate(copiedCode, 320)}");

        _ = FindTextPatternPointAfterScroll(renderer, "Safe inline image", "Copy image viewport anchor");
        var image = Retry.WhileNull(
            () => renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Image))
                .FirstOrDefault(element =>
                    (element.Name ?? string.Empty).Contains(
                        "Safe blue audit square",
                        StringComparison.Ordinal) &&
                    !element.IsOffscreen),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException("Copy image target was not exposed in the visible UIA tree");
        var imageBounds = image.BoundingRectangle;
        string copiedImage = InvokeTargetContextCommand(
            window,
            renderer,
            new System.Drawing.Point(
                imageBounds.Left + imageBounds.Width / 2,
                imageBounds.Top + imageBounds.Height / 2),
            "MarkdownContextCopyImage",
            "Copy image");
        Assert(copiedImage.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase),
            $"Copy image did not retain its source fallback: {Truncate(copiedImage, 240)}");
    }

    private static string InvokeTargetContextCommand(
        Window window,
        AutomationElement renderer,
        System.Drawing.Point point,
        string automationId,
        string localizedName)
    {
        string sentinel = $"markdown-renderer-{automationId}-sentinel";
        SetClipboardText(sentinel);
        Mouse.MoveTo(point);
        Thread.Sleep(100);
        Mouse.RightClick();

        var item = Retry.WhileNull(
            () =>
            {
                var inWindow = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(element =>
                        string.Equals(AutomationIdOrEmpty(element), automationId, StringComparison.Ordinal) &&
                        !element.IsOffscreen);
                if (inWindow is not null)
                    return inWindow;

                return renderer.Automation.GetDesktop()
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(element =>
                        string.Equals(AutomationIdOrEmpty(element), automationId, StringComparison.Ordinal) &&
                        string.Equals(NameOrEmpty(element), localizedName, StringComparison.OrdinalIgnoreCase) &&
                        !element.IsOffscreen);
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException(
                $"target context menu did not expose localized '{localizedName}' ({automationId})");
        Assert(item.IsEnabled, $"{localizedName} must be enabled for its rendered target");
        item.Click();

        bool copied = Retry.WhileFalse(
            () => !string.Equals(GetClipboardText(), sentinel, StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(copied, $"{localizedName} left the clipboard unchanged");
        return GetClipboardText();
    }
}
