using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;

namespace MarkdownRenderer.Sample.Automation.Uia;

/// <summary>
/// Finds the sample window's renderer, status mirrors and navigation items, and selects sample pages.
/// </summary>
internal static class SampleWindowQueries
{
    internal static AutomationElement FindRenderer(Window window)
        => window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownRenderer"))
           ?? throw new InvalidOperationException("MarkdownRenderer not found in automation tree");

    internal static AutomationElement? FindFirstRawDescendantByAutomationId(
        AutomationElement root,
        string automationId)
    {
        var walker = root.Automation.TreeWalkerFactory.GetRawViewWalker();
        var pending = new Queue<AutomationElement>();
        EnqueueChildren(root);
        while (pending.Count > 0)
        {
            AutomationElement current = pending.Dequeue();
            if (string.Equals(
                    AutomationIdOrEmpty(current),
                    automationId,
                    StringComparison.Ordinal))
            {
                return current;
            }

            EnqueueChildren(current);
        }

        return null;

        void EnqueueChildren(AutomationElement parent)
        {
            AutomationElement? child;
            try { child = walker.GetFirstChild(parent); }
            catch { return; }

            while (child is not null)
            {
                pending.Enqueue(child);
                try { child = walker.GetNextSibling(child); }
                catch { return; }
            }
        }
    }

    internal static AutomationElement WaitForDiagnostics(Window window, string expectedCode)
    {
        AutomationElement? diagnostics = null;
        bool ready = Retry.WhileFalse(
            () =>
            {
                diagnostics = window.FindFirstDescendant(
                    cf => cf.ByAutomationId("SampleDiagnosticsMessage"));
                return diagnostics is not null &&
                       GetAutomationElementText(diagnostics).Contains(expectedCode, StringComparison.Ordinal);
            },
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, $"renderer diagnostics did not expose {expectedCode}");
        return diagnostics!;
    }

    internal static void SelectSample(Window window, string key)
    {
        AutomationElement item = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SampleNav_" + key))
            ?? throw new InvalidOperationException($"{key} sample navigation item not found");

        if (item.Patterns.ScrollItem.IsSupported)
        {
            try { item.Patterns.ScrollItem.Pattern.ScrollIntoView(); }
            catch { }
        }

        if (item.Patterns.SelectionItem.IsSupported)
        {
            item.Patterns.SelectionItem.Pattern.Select();
        }
        else if (item.Patterns.Invoke.IsSupported)
        {
            item.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            item.Click();
        }

        string expectedStatus = "page:" + key;
        bool selected = Retry.WhileFalse(
            () => string.Equals(
                FindFirstRawDescendantByAutomationId(window, "CurrentSamplePage")?.Name,
                expectedStatus,
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(selected, $"{key} sample page did not become current");
    }

    /// <summary>
    /// Reads the realised embed count published by the sample app's hidden
    /// status TextBlock ("realized:N"), which mirrors
    /// MarkdownRendererControl.RealizedEmbedCount via the
    /// EmbedsRealizationChanged event. We intentionally do not read this
    /// from the renderer's own UIA properties to avoid Narrator announcing
    /// it on every scroll.
    /// </summary>
    internal static int ReadRealizedEmbedCount(AutomationElement renderer)
    {
        try
        {
            // Find the status TextBlock within the application window tree only.
            // Stop at ControlType.Window so we never search the entire desktop and
            // cannot accidentally read a count from a different application.
            var root = renderer;
            while (root.Parent is not null
                   && root.ControlType != FlaUI.Core.Definitions.ControlType.Window)
                root = root.Parent;
            var status = FindFirstRawDescendantByAutomationId(root, "RealizedEmbedCount");
            string? text = status?.Name ?? status?.Properties.Name.ValueOrDefault;
            if (string.IsNullOrEmpty(text)) return 0;
            const string prefix = "realized:";
            int idx = text.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0) return 0;
            return int.TryParse(text.AsSpan(idx + prefix.Length), out var n) ? n : 0;
        }
        catch { return 0; }
    }

    internal static void TryFocus(AutomationElement element, string description)
    {
        try { element.Focus(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[automation] warn: could not focus {description}: {ex.Message}");
        }
    }
}
