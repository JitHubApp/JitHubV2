using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P3-021 (Phase 3 rank 164): a semantic node without layout geometry fell
/// back to the owning control's rectangle, so an image far above the viewport
/// reported the whole renderer's bounds and IsOffscreen=False. A node without
/// geometry must report an empty rectangle instead.
/// </summary>
public sealed class NodePeerEmptyGeometryContractTests
{
    [Fact]
    public void NodeWithoutGeometryNeverReportsTheOwningControlRectangle()
    {
        string peer = ReadSource("Accessibility", "MarkdownNodePeer.cs");
        string method = Body(peer, "protected override Windows.Foundation.Rect GetBoundingRectangleCore()");
        Assert.DoesNotContain("base.GetBoundingRectangleCore()", method, StringComparison.Ordinal);
        int empty = method.IndexOf("if (rect.Width <= 0 || rect.Height <= 0)", StringComparison.Ordinal);
        int screen = method.IndexOf("_root.GetScreenRectForDocumentRect(rect)", StringComparison.Ordinal);
        Assert.True(empty >= 0 && screen > empty, "Empty geometry must return before screen mapping.");
        Assert.Contains("return default;", method[empty..screen], StringComparison.Ordinal);

        string offscreen = Body(peer, "protected override bool IsOffscreenCore()");
        Assert.Contains("IsScreenRectOffscreen(GetBoundingRectangleCore())", offscreen, StringComparison.Ordinal);
        string root = ReadSource("Accessibility", "MarkdownAutomationPeer.cs");
        Assert.Contains("return screenRect.Width <= 0 ||", Body(root, "internal bool IsScreenRectOffscreen(Windows.Foundation.Rect screenRect)"), StringComparison.Ordinal);
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
