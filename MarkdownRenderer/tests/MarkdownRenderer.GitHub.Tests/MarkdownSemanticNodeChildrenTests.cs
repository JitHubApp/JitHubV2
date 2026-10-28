using System.Collections;
using System.Reflection;
using MarkdownRenderer.Accessibility;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownSemanticNodeChildrenTests
{
    private static readonly FieldInfo ChildStorageField = typeof(MarkdownSemanticNode).GetField(
        "_children",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Semantic child backing field was not found.");

    [Fact]
    public void ChildrenView_RemainsStableAndLiveAcrossFirstAndLaterAdditions()
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        MarkdownSemanticNode.ChildrenView retainedView = parent.Children;

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
        var leaf = new MarkdownSemanticNode(MarkdownSemanticRole.Image);
        Assert.Null(ChildStorageField.GetValue(leaf));

        MarkdownSemanticNode.ChildrenView retainedView = leaf.Children;
        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        leaf.Add(first);

        Assert.Same(first, ChildStorageField.GetValue(leaf));
        Assert.Same(leaf, first.Parent);
        Assert.Single(retainedView);
        Assert.Same(first, retainedView[0]);

        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        leaf.Add(second);

        List<MarkdownSemanticNode> promoted = Assert.IsType<List<MarkdownSemanticNode>>(
            ChildStorageField.GetValue(leaf));
        Assert.Equal(2, promoted.Capacity);
        Assert.Equal(2, retainedView.Count);
        Assert.Same(first, promoted[0]);
        Assert.Same(second, promoted[1]);
        Assert.Same(leaf, second.Parent);
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
