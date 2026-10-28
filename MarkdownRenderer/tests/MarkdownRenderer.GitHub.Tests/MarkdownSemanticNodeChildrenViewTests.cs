using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MarkdownRenderer.Accessibility;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownSemanticNodeChildrenViewTests
{
    [Fact]
    public void NodeIsNotAWinRtCollectionAndChildrenUsesAConcreteValueView()
    {
        Type nodeType = typeof(MarkdownSemanticNode);
        Type viewType = typeof(MarkdownSemanticNode.ChildrenView);

        Assert.False(typeof(IReadOnlyList<MarkdownSemanticNode>).IsAssignableFrom(nodeType));
        Assert.False(typeof(IEnumerable<MarkdownSemanticNode>).IsAssignableFrom(nodeType));
        Assert.False(typeof(IEnumerable).IsAssignableFrom(nodeType));
        Assert.True(typeof(IReadOnlyList<MarkdownSemanticNode>).IsAssignableFrom(viewType));

        PropertyInfo childrenProperty = nodeType.GetProperty(nameof(MarkdownSemanticNode.Children))!;
        Assert.Equal(viewType, childrenProperty.PropertyType);
        Assert.True(viewType.IsValueType);

        MethodInfo patternEnumerator = viewType.GetMethod(
            nameof(MarkdownSemanticNode.ChildrenView.GetEnumerator),
            BindingFlags.Instance | BindingFlags.Public)!;
        Assert.Equal(typeof(MarkdownSemanticNode.ChildrenView.ChildrenEnumerator), patternEnumerator.ReturnType);
        Assert.True(patternEnumerator.ReturnType.IsValueType);
    }

    [Fact]
    public void ConcreteForeachPatternPreservesOrderedChildReferences()
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        parent.Add(first);
        parent.Add(second);

        MarkdownSemanticNode[] actual = EnumerateWithConcretePattern(parent.Children);

        Assert.Equal(2, actual.Length);
        Assert.Same(first, actual[0]);
        Assert.Same(second, actual[1]);
    }

    [Fact]
    public void DefaultAndEmptyViewsKeepBoundsAndEnumeratorDefaults()
    {
        MarkdownSemanticNode.ChildrenView defaultView = default;
        Assert.Null(defaultView.Owner);
        Assert.Empty(defaultView);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = defaultView[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = defaultView[0]);

        var defaultGeneric = defaultView.GetEnumerator();
        Assert.Null((object?)defaultGeneric.Current);
        Assert.False(defaultGeneric.MoveNext());
        Assert.Null((object?)defaultGeneric.Current);
        defaultGeneric.Reset();
        defaultGeneric.Dispose();

        IEnumerator defaultNonGeneric = ((IEnumerable)defaultView).GetEnumerator();
        Assert.Throws<InvalidOperationException>(() => _ = defaultNonGeneric.Current);
        Assert.False(defaultNonGeneric.MoveNext());
        Assert.Throws<InvalidOperationException>(() => _ = defaultNonGeneric.Current);
        defaultNonGeneric.Reset();
        ((IDisposable)defaultNonGeneric).Dispose();

        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        MarkdownSemanticNode.ChildrenView emptyView = parent.Children;
        Assert.Same(parent, emptyView.Owner);
        Assert.Empty(emptyView);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = emptyView[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = emptyView[0]);

        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        parent.Add(first);
        parent.Add(second);

        Assert.Equal(2, emptyView.Count);
        Assert.Same(first, emptyView[0]);
        Assert.Same(second, emptyView[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = emptyView[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = emptyView[2]);
    }

    [Fact]
    public void DuplicateChildAdmissionAndParentIdentityRemainUnchanged()
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var child = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);

        parent.Add(child);
        parent.Add(child);

        MarkdownSemanticNode.ChildrenView children = parent.Children;
        Assert.Equal(2, children.Count);
        Assert.Same(child, children[0]);
        Assert.Same(child, children[1]);
        Assert.Same(parent, child.Parent);
        Assert.Equal([child, child], EnumerateWithConcretePattern(children));
    }

    [Fact]
    public void AddStillAdmitsCyclesWithoutTraversingThem()
    {
        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Group);

        first.Add(second);
        second.Add(first);

        Assert.Same(second, first.Parent);
        Assert.Same(first, second.Parent);
        Assert.Same(second, first.Children[0]);
        Assert.Same(first, second.Children[0]);
        // Do not invoke a tree traversal here: Add historically admits cycles,
        // while document builders are responsible for constructing a tree.
    }

    [Fact]
    public void StructEnumeratorDisposeRemainsANoopLikeListEnumerator()
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        parent.Add(first);

        var expectedChildren = new List<MarkdownSemanticNode> { first };
        MarkdownSemanticNode.ChildrenView.ChildrenEnumerator actual = parent.Children.GetEnumerator();
        IEnumerator<MarkdownSemanticNode> expected = expectedChildren.GetEnumerator();
        actual.Dispose();
        expected.Dispose();

        Assert.True(actual.MoveNext());
        Assert.True(expected.MoveNext());
        Assert.Same(expected.Current, actual.Current);
        actual.Reset();
        expected.Reset();
        Assert.True(actual.MoveNext());
        Assert.True(expected.MoveNext());
        Assert.Same(expected.Current, actual.Current);
    }

    private static MarkdownSemanticNode[] EnumerateWithConcretePattern(
        MarkdownSemanticNode.ChildrenView children)
    {
        var result = new List<MarkdownSemanticNode>(children.Count);
        foreach (MarkdownSemanticNode child in children)
            result.Add(child);
        return result.ToArray();
    }
}
