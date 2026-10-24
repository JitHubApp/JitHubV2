using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Validates source wiring only. Compiled layout and activation-target behavior
/// is covered by VectorAccessibilityContractTests; this is not a WinUI/UIA
/// control integration test.
/// </summary>
public sealed class LinkedInlineMathAutomationSourceContractTests
{
    [Fact]
    public void LinkedMathKeepsItsMathPeerWhileExposingHyperlinkKeyboardAndInvokePatterns()
    {
        string nodePeer = ReadRepositorySource(
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Accessibility",
            "MarkdownNodePeer.cs");
        string automationPeer = ReadRepositorySource(
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Accessibility",
            "MarkdownAutomationPeer.cs");

        Assert.Contains("if (LinkedInlineVectorScene is not null)", nodePeer, StringComparison.Ordinal);
        Assert.Contains("return AutomationControlType.Hyperlink;", nodePeer, StringComparison.Ordinal);
        Assert.Contains("_owner.FocusInlineVectorSceneFromAutomation(inlineScene);", nodePeer, StringComparison.Ordinal);
        Assert.Contains("IsVectorSemanticInvokable || LinkedInlineVectorScene is not null", nodePeer, StringComparison.Ordinal);
        Assert.Contains("_owner.RaiseInlineVectorSceneClickFromAutomation(inlineScene);", nodePeer, StringComparison.Ordinal);

        string focusRoute = SliceBetween(
            automationPeer,
            "internal void RaiseFocusForInlineVectorScene(",
            "internal void RaiseFocusForVectorSemantic(");
        Assert.Contains("node.Role == MarkdownSemanticRole.Math", focusRoute, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(node.InlineRun, scene)", focusRoute, StringComparison.Ordinal);
        Assert.Contains("GetOrCreateNodePeer(node).RaiseAutomationFocusChanged();", focusRoute, StringComparison.Ordinal);
    }

    private static string SliceBetween(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find source marker '{start}'.");
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find source marker '{end}' after '{start}'.");
        return source[startIndex..endIndex];
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = Path.Combine(path, part);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate repository source '{string.Join(Path.DirectorySeparatorChar, pathParts)}'.");
    }
}
