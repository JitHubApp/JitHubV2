using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class HostedFocusableBufferSourceContractTests
{
    [Fact]
    public void HostedFocusUsesPrivateArrayStorageAndRetainsTraversalInvariants()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "MarkdownRenderer", "MarkdownRenderer",
            "Controls", "MarkdownRendererControl.cs"));
        int start = source.IndexOf("private sealed class HostedFocusableControlBuffer", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = source.IndexOf("private bool TrySetFocusedItemForHostedElement", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string hostedFocus = source[start..end];
        Assert.Contains("private Control[] _items", hostedFocus, StringComparison.Ordinal);
        Assert.DoesNotContain("List<Control>", hostedFocus, StringComparison.Ordinal);
        Assert.DoesNotContain("IReadOnlyList<Control>", hostedFocus, StringComparison.Ordinal);
        Assert.Contains("(uint)index >= (uint)Count", hostedFocus, StringComparison.Ordinal);
        Assert.Contains("throw new ArgumentOutOfRangeException(nameof(index))", hostedFocus, StringComparison.Ordinal);
        Assert.Contains("EqualityComparer<Control>.Default.Equals(_items[i], control)", hostedFocus, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(focusables[i], control)", hostedFocus, StringComparison.Ordinal);
        Assert.Contains("reverse ? focusables[^1] : focusables[0]", hostedFocus, StringComparison.Ordinal);
        Assert.Contains("focusables[next].Focus(FocusState.Keyboard)", hostedFocus, StringComparison.Ordinal);

        int addCurrentNode = hostedFocus.IndexOf("if (node is Control control && IsHostedFocusableControl(control))", StringComparison.Ordinal);
        int countChildren = hostedFocus.IndexOf("VisualTreeHelper.GetChildrenCount(node)", StringComparison.Ordinal);
        int recurse = hostedFocus.IndexOf("CollectHostedFocusableControls(child, result)", StringComparison.Ordinal);
        Assert.True(addCurrentNode >= 0 && addCurrentNode < countChildren && countChildren < recurse);
        Assert.Contains("catch { return; }", hostedFocus, StringComparison.Ordinal);
        Assert.Contains("catch { continue; }", hostedFocus, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the JitHub repository root.");
    }
}
