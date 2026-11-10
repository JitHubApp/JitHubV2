using System.Collections;
using Xunit;
using MarkdownRenderer.Accessibility.Internal.Semantics;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownSemanticNodeChildrenTests
{
    [Fact]
    public void ChildrenView_RemainsStableAndLiveAcrossFirstAndLaterAdditions()
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        MarkdownSemanticChildrenView retainedView = parent.Children;

        Assert.Empty(retainedView);
        Assert.Same(parent, retainedView.Owner);
        Assert.Same(retainedView.Owner, parent.Children.Owner);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = retainedView[0]);

        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        parent.Add(first);

        Assert.Same(retainedView.Owner, parent.Children.Owner);
        Assert.Single(retainedView);
        Assert.Same(first, retainedView[0]);
        Assert.Same(parent, first.Parent);

        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        parent.Add(second);

        Assert.Same(retainedView.Owner, parent.Children.Owner);
        Assert.Equal([first, second], retainedView);
        Assert.Same(first, retainedView[0]);
        Assert.Same(second, retainedView[1]);
        Assert.Same(parent, second.Parent);
    }

    [Fact]
    public void SingletonChildUsesDirectStorageAndSecondChildPromotesToCapacityTwoList()
    {
        // Warm both Add paths and the reference allocation so only storage is measured.
        var warmup = new MarkdownSemanticNode(MarkdownSemanticRole.Image);
        warmup.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph));
        warmup.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Link));
        var leaf = new MarkdownSemanticNode(MarkdownSemanticRole.Image);
        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        _ = MeasureCapacityTwoList(first, second);
        long capacityTwoListBytes = MeasureCapacityTwoList(first, second);
        Assert.Equal(0, leaf.ChildCount);

        MarkdownSemanticChildrenView retainedView = leaf.Children;
        long before = GC.GetAllocatedBytesForCurrentThread();
        leaf.Add(first);
        long firstChildBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // A unary node keeps its child in the existing reference slot.
        Assert.Equal(0, firstChildBytes);
        Assert.Same(leaf, first.Parent);
        Assert.Single(retainedView);
        Assert.Same(first, retainedView[0]);

        before = GC.GetAllocatedBytesForCurrentThread();
        leaf.Add(second);
        long secondChildBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // The second child promotes storage to exactly one capacity-two list.
        Assert.Equal(capacityTwoListBytes, secondChildBytes);
        Assert.Equal(2, retainedView.Count);
        Assert.Same(first, retainedView[0]);
        Assert.Same(second, retainedView[1]);
        Assert.Same(leaf, second.Parent);
    }

    private static long MeasureCapacityTwoList(MarkdownSemanticNode first, MarkdownSemanticNode second)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        List<MarkdownSemanticNode> list = new(2) { first, second };
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(list);
        return allocated;
    }

    [Fact]
    public void GenericEnumerator_CurrentMoveNextAndResetMatchListEnumerator()
    {
        var (node, referenceChildren) = CreateTwoChildren();
        IEnumerator<MarkdownSemanticNode> actual = node.Children.GetEnumerator();
        IEnumerator<MarkdownSemanticNode> expected = referenceChildren.GetEnumerator();

        Assert.Null((object?)actual.Current);
        Assert.Null((object?)expected.Current);
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Same(expected.Current, actual.Current);
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Same(expected.Current, actual.Current);
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Null((object?)actual.Current);
        Assert.Null((object?)expected.Current);

        expected.Reset();
        actual.Reset();
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Same(expected.Current, actual.Current);
    }

    [Fact]
    public void NonGenericEnumerator_CurrentAndResetMatchListEnumerator()
    {
        var (node, referenceChildren) = CreateTwoChildren();
        IEnumerator actual = ((IEnumerable)node.Children).GetEnumerator();
        IEnumerator expected = ((IEnumerable)referenceChildren).GetEnumerator();

        Assert.Throws<InvalidOperationException>(() => _ = actual.Current);
        Assert.Throws<InvalidOperationException>(() => _ = expected.Current);
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Same(expected.Current, actual.Current);
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Same(expected.Current, actual.Current);
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Throws<InvalidOperationException>(() => _ = actual.Current);
        Assert.Throws<InvalidOperationException>(() => _ = expected.Current);

        expected.Reset();
        actual.Reset();
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Same(expected.Current, actual.Current);
    }

    [Fact]
    public void GenericEnumerator_CurrentRemainsReadableButMoveNextAndResetRejectMutationLikeList()
    {
        var (node, referenceChildren) = CreateTwoChildren();
        IEnumerator<MarkdownSemanticNode> actual = node.Children.GetEnumerator();
        IEnumerator<MarkdownSemanticNode> expected = referenceChildren.GetEnumerator();
        Assert.Equal(expected.MoveNext(), actual.MoveNext());
        Assert.Same(expected.Current, actual.Current);

        var third = new MarkdownSemanticNode(MarkdownSemanticRole.Image);
        node.Add(third);
        referenceChildren.Add(third);

        // List<T>.Enumerator.Current does not check its captured version.
        Assert.Same(expected.Current, actual.Current);
        Assert.Throws<InvalidOperationException>(() => expected.MoveNext());
        Assert.Throws<InvalidOperationException>(() => actual.MoveNext());
        Assert.Throws<InvalidOperationException>(() => expected.Reset());
        Assert.Throws<InvalidOperationException>(() => actual.Reset());
    }

    private static (MarkdownSemanticNode Node, List<MarkdownSemanticNode> Children) CreateTwoChildren()
    {
        var node = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        node.Add(first);
        node.Add(second);
        return (node, [first, second]);
    }
}
