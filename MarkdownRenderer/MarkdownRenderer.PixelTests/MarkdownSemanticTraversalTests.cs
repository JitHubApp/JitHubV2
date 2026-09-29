using MarkdownRenderer.Accessibility;
using Xunit;

namespace MarkdownRenderer.PixelTests;

public sealed class MarkdownSemanticTraversalTests
{
    [Fact]
    public void EnumerateDepthFirst_PreservesDocumentOrder()
    {
        var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document);
        var first = new MarkdownSemanticNode(MarkdownSemanticRole.List);
        var firstChild = new MarkdownSemanticNode(MarkdownSemanticRole.ListItem);
        var secondChild = new MarkdownSemanticNode(MarkdownSemanticRole.ListItem);
        var last = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        root.Add(first);
        first.Add(firstChild);
        first.Add(secondChild);
        root.Add(last);

        Assert.Equal(
            [root, first, firstChild, secondChild, last],
            MarkdownSemanticDocument.EnumerateDepthFirst(root));
    }

    [Fact]
    public void EnumerateDepthFirst_HandlesDeepNestingWithoutRecursiveIterators()
    {
        const int depth = 20_000;
        var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document);
        MarkdownSemanticNode parent = root;
        for (int index = 0; index < depth; index++)
        {
            var child = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
            parent.Add(child);
            parent = child;
        }

        int count = 0;
        foreach (MarkdownSemanticNode _ in MarkdownSemanticDocument.EnumerateDepthFirst(root))
        {
            count++;
        }

        Assert.Equal(depth + 1, count);
    }

    [Fact]
    public void EnumerateDepthFirst_WideDocumentDoesNotAllocatePerNode()
    {
        const int childCount = 20_000;
        var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document);
        for (int index = 0; index < childCount; index++)
            root.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph));

        // Warm the iterator/JIT so the measured pass isolates traversal churn.
        foreach (MarkdownSemanticNode _ in MarkdownSemanticDocument.EnumerateDepthFirst(root))
        {
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int count = 0;
        foreach (MarkdownSemanticNode _ in MarkdownSemanticDocument.EnumerateDepthFirst(root))
            count++;
        long traversalAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(childCount + 1, count);
        Assert.InRange(traversalAllocatedBytes, 0, 32 * 1024);
    }
}
