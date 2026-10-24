using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P3-020 (Phase 3 rank 484: UIA exposed 4 of 6 read-only task states). When a
/// read-only task marker was realized as a XAML CheckBox, the automation tree
/// exposed that CheckBox's peer in place of the Markdown task-state peer. The
/// CheckBox is deliberately in the Raw view, so the task state disappeared from
/// the Control and Content views whenever its marker had been scrolled into
/// view. A Raw-view realized element must never replace the semantic peer.
/// These source contracts pin the rule; the live Sandbox run of rank 484
/// verifies the behavior.
/// </summary>
public sealed class RealizedEmbedPeerContractTests
{
    [Fact]
    public void ReadOnlyTaskMarkerStaysDecorativeInTheRawView()
    {
        string factory = ReadSource("Layout", "TaskMarkerControlFactory.cs");
        string readOnly = Body(factory, "private sealed partial class ReadOnlyTaskCheckBox : CheckBox");
        Assert.Contains("AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);", readOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void RawViewRealizedEmbedKeepsTheSemanticNodePeer()
    {
        string peer = ReadSource("Accessibility", "MarkdownAutomationPeer.cs");
        string method = Body(peer, "internal bool TryGetPeerForSemanticNode(MarkdownSemanticNode node, out AutomationPeer peer)");
        int embed = method.IndexOf("if (node.Role == MarkdownSemanticRole.Embed)", StringComparison.Ordinal);
        Assert.True(embed >= 0);
        int rawGuard = method.IndexOf("AutomationProperties.GetAccessibilityView(element) != AccessibilityView.Raw", embed, StringComparison.Ordinal);
        int elementPeer = method.IndexOf("FrameworkElementAutomationPeer.FromElement(element)", embed, StringComparison.Ordinal);
        int fallback = method.IndexOf("peer = GetOrCreateNodePeer(node);", embed, StringComparison.Ordinal);
        Assert.True(rawGuard > embed, "The embed branch must check the realized element's accessibility view.");
        Assert.True(elementPeer > rawGuard, "The view check must come before the element peer is taken.");
        Assert.True(fallback > elementPeer, "Raw-view elements must fall back to the semantic node peer.");
    }

    private static string ReadSource(string folder, string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", folder, fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Could not locate {fileName}.");
    }

    private static string Body(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find {signature}");
        int open = source.IndexOf('{', member + signature.Length);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[open..(index + 1)];
        }
        throw new InvalidOperationException("Unbalanced source block.");
    }
}
