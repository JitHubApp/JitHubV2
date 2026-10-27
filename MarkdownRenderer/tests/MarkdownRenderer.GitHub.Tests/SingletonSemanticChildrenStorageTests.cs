using System.Collections;
using MarkdownRenderer.Accessibility;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SingletonSemanticChildrenStorageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(128)]
    public void ChildrenViewMatchesIndependentListOracle(int childCount)
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var expected = new List<MarkdownSemanticNode>(childCount);
        MarkdownSemanticNode.ChildrenView retainedView = parent.Children;

        AssertViewMatchesList(retainedView, expected);
        for (int index = 0; index < childCount; index++)
        {
            var child = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
            parent.Add(child);
            expected.Add(child);

            Assert.Same(parent, child.Parent);
            AssertViewMatchesList(retainedView, expected);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(128, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(128, true)]
    public void CapturedEnumeratorsMatchListAfterAnAppend(int initialCount, bool exhaustBeforeAppend)
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var expected = new List<MarkdownSemanticNode>(initialCount + 1);
        for (int index = 0; index < initialCount; index++)
        {
            var child = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
            parent.Add(child);
            expected.Add(child);
        }

        // Compare the concrete pattern enumerators here. Empty interface
        // enumeration has a distinct BCL fast path and is covered below.
        IEnumerator<MarkdownSemanticNode> actualGeneric = parent.Children.GetEnumerator();
        IEnumerator<MarkdownSemanticNode> expectedGeneric = expected.GetEnumerator();
        PositionBeforeAppend(actualGeneric, expectedGeneric, initialCount, exhaustBeforeAppend);
        AssertGenericCurrentMatches(actualGeneric, expectedGeneric);

        IEnumerator actualNonGeneric = (IEnumerator)parent.Children.GetEnumerator();
        IEnumerator expectedNonGeneric = (IEnumerator)expected.GetEnumerator();
        PositionBeforeAppend(actualNonGeneric, expectedNonGeneric, initialCount, exhaustBeforeAppend);
        AssertNonGenericCurrentMatches(actualNonGeneric, expectedNonGeneric);

        var appended = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        parent.Add(appended);
        expected.Add(appended);

        // List<T>.Enumerator.Current remains readable after mutation, including
        // its default value when the enumerator had already reached the end.
        AssertGenericCurrentMatches(actualGeneric, expectedGeneric);
        AssertNonGenericCurrentMatches(actualNonGeneric, expectedNonGeneric);
        AssertInvalidated(actualGeneric, expectedGeneric);
        AssertInvalidated(actualNonGeneric, expectedNonGeneric);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void InterfaceEnumeratorsMatchListEmptyFastPathAndMutationBehavior(int initialCount)
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var expected = new List<MarkdownSemanticNode>(initialCount + 1);
        for (int index = 0; index < initialCount; index++)
        {
            var child = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
            parent.Add(child);
            expected.Add(child);
        }

        IEnumerable<MarkdownSemanticNode> actualSequence = parent.Children;
        IEnumerable<MarkdownSemanticNode> expectedSequence = expected;
        IEnumerator<MarkdownSemanticNode> actualGeneric = actualSequence.GetEnumerator();
        IEnumerator<MarkdownSemanticNode> expectedGeneric = expectedSequence.GetEnumerator();
        IEnumerator actualNonGeneric = ((IEnumerable)actualSequence).GetEnumerator();
        IEnumerator expectedNonGeneric = ((IEnumerable)expectedSequence).GetEnumerator();

        AssertGenericEnumeratorPrefixMatches(actualGeneric, expectedGeneric);
        AssertNonGenericEnumeratorPrefixMatches(actualNonGeneric, expectedNonGeneric);

        var appended = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        parent.Add(appended);
        expected.Add(appended);

        AssertGenericEnumeratorAfterAppendMatches(actualGeneric, expectedGeneric);
        AssertNonGenericEnumeratorAfterAppendMatches(actualNonGeneric, expectedNonGeneric);
    }

    [Fact]
    public void DefaultChildrenViewMatchesEmptyListInterfaceEnumerators()
    {
        MarkdownSemanticNode.ChildrenView defaultView = default;
        List<MarkdownSemanticNode> expected = [];
        IEnumerable<MarkdownSemanticNode> actualSequence = defaultView;
        IEnumerable<MarkdownSemanticNode> expectedSequence = expected;

        AssertEmptyGenericInterfaceEnumeratorMatches(
            actualSequence.GetEnumerator(),
            expectedSequence.GetEnumerator());
        AssertEmptyNonGenericInterfaceEnumeratorMatches(
            ((IEnumerable)actualSequence).GetEnumerator(),
            ((IEnumerable)expectedSequence).GetEnumerator());
    }

    [Fact]
    public void DisposeDoesNotDisableEnumerationAndRetainedViewTracksPromotion()
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        MarkdownSemanticNode.ChildrenView retainedView = parent.Children;
        var actual = retainedView.GetEnumerator();

        actual.Dispose();
        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        parent.Add(first);
        Assert.Single(retainedView);
        Assert.Same(first, retainedView[0]);

        // The prior enumerator is invalidated by the successful addition even
        // though Dispose itself is a no-op, matching List<T>.Enumerator.
        Assert.Throws<InvalidOperationException>(() => actual.MoveNext());

        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Link);
        parent.Add(second);
        Assert.Equal(2, retainedView.Count);
        Assert.Same(first, retainedView[0]);
        Assert.Same(second, retainedView[1]);
    }

    [Fact]
    public void ReparentingDuplicatesAndCyclesKeepTheirExistingListSemantics()
    {
        var originalParent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var newParent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var child = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);

        originalParent.Add(child);
        newParent.Add(child);
        newParent.Add(child);

        Assert.Single(originalParent.Children);
        Assert.Same(child, originalParent.Children[0]);
        Assert.Equal(2, newParent.Children.Count);
        Assert.Same(child, newParent.Children[0]);
        Assert.Same(child, newParent.Children[1]);
        Assert.Same(newParent, child.Parent);

        var first = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var second = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        first.Add(second);
        second.Add(first);

        Assert.Same(second, first.Parent);
        Assert.Same(first, second.Parent);
        Assert.Same(second, first.Children[0]);
        Assert.Same(first, second.Children[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NullAddThrowsBeforeChangingStorageOrEnumeratorVersion(int initialCount)
    {
        var parent = new MarkdownSemanticNode(MarkdownSemanticRole.Group);
        var expected = new List<MarkdownSemanticNode>(initialCount);
        for (int index = 0; index < initialCount; index++)
        {
            var child = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
            parent.Add(child);
            expected.Add(child);
        }

        var enumerator = parent.Children.GetEnumerator();
        bool movedBeforeFailure = enumerator.MoveNext();
        Assert.Equal(initialCount > 0, movedBeforeFailure);
        if (movedBeforeFailure)
            Assert.Same(expected[0], enumerator.Current);

        Assert.Throws<NullReferenceException>(() => parent.Add(null!));

        Assert.Equal(expected.Count, parent.Children.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index], parent.Children[index]);
            Assert.Same(parent, expected[index].Parent);
        }

        if (initialCount > 0)
            Assert.Same(expected[0], enumerator.Current);
        Assert.Equal(initialCount > 1, enumerator.MoveNext());

        enumerator.Reset();
        Assert.Equal(initialCount > 0, enumerator.MoveNext());
        if (initialCount > 0)
            Assert.Same(expected[0], enumerator.Current);
    }

    private static void AssertViewMatchesList(
        MarkdownSemanticNode.ChildrenView actual,
        List<MarkdownSemanticNode> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
            Assert.Same(expected[index], actual[index]);

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = actual[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = actual[expected.Count]);

        var concreteItems = new List<MarkdownSemanticNode>(actual.Count);
        foreach (MarkdownSemanticNode child in actual)
            concreteItems.Add(child);
        AssertSameItems(expected, concreteItems);

        using (IEnumerator<MarkdownSemanticNode> actualEnumerator = actual.GetEnumerator())
        using (IEnumerator<MarkdownSemanticNode> expectedEnumerator = expected.GetEnumerator())
        {
            AssertGenericCurrentMatches(actualEnumerator, expectedEnumerator);
            for (int index = 0; index <= expected.Count; index++)
            {
                Assert.Equal(expectedEnumerator.MoveNext(), actualEnumerator.MoveNext());
                AssertGenericCurrentMatches(actualEnumerator, expectedEnumerator);
            }

            actualEnumerator.Reset();
            expectedEnumerator.Reset();
            for (int index = 0; index < expected.Count; index++)
            {
                Assert.Equal(expectedEnumerator.MoveNext(), actualEnumerator.MoveNext());
                AssertGenericCurrentMatches(actualEnumerator, expectedEnumerator);
            }
        }

        IEnumerator actualNonGeneric = ((IEnumerable)actual).GetEnumerator();
        IEnumerator expectedNonGeneric = ((IEnumerable)expected).GetEnumerator();
        AssertNonGenericCurrentMatches(actualNonGeneric, expectedNonGeneric);
        for (int index = 0; index <= expected.Count; index++)
        {
            Assert.Equal(expectedNonGeneric.MoveNext(), actualNonGeneric.MoveNext());
            AssertNonGenericCurrentMatches(actualNonGeneric, expectedNonGeneric);
        }

        actualNonGeneric.Reset();
        expectedNonGeneric.Reset();
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expectedNonGeneric.MoveNext(), actualNonGeneric.MoveNext());
            AssertNonGenericCurrentMatches(actualNonGeneric, expectedNonGeneric);
        }

        ((IDisposable)actualNonGeneric).Dispose();
        ((IDisposable)expectedNonGeneric).Dispose();
        Assert.Equal(expectedNonGeneric.MoveNext(), actualNonGeneric.MoveNext());
        AssertNonGenericCurrentMatches(actualNonGeneric, expectedNonGeneric);
    }

    private static void AssertGenericCurrentMatches(
        IEnumerator<MarkdownSemanticNode> actual,
        IEnumerator<MarkdownSemanticNode> expected)
        => Assert.Same(expected.Current, actual.Current);

    private static void PositionBeforeAppend(
        IEnumerator<MarkdownSemanticNode> actual,
        IEnumerator<MarkdownSemanticNode> expected,
        int childCount,
        bool exhaust)
    {
        int moveCount = exhaust ? childCount + 1 : 1;
        for (int index = 0; index < moveCount; index++)
            Assert.Equal(expected.MoveNext(), actual.MoveNext());
    }

    private static void PositionBeforeAppend(
        IEnumerator actual,
        IEnumerator expected,
        int childCount,
        bool exhaust)
    {
        int moveCount = exhaust ? childCount + 1 : 1;
        for (int index = 0; index < moveCount; index++)
            Assert.Equal(expected.MoveNext(), actual.MoveNext());
    }

    private static void AssertNonGenericCurrentMatches(IEnumerator actual, IEnumerator expected)
    {
        object? expectedValue = null;
        object? actualValue = null;
        Exception? expectedException = Record.Exception(() => expectedValue = expected.Current);
        Exception? actualException = Record.Exception(() => actualValue = actual.Current);

        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException is null)
            Assert.Same(expectedValue, actualValue);
    }

    private static void AssertGenericEnumeratorPrefixMatches(
        IEnumerator<MarkdownSemanticNode> actual,
        IEnumerator<MarkdownSemanticNode> expected)
    {
        AssertGenericCurrentOutcomeMatches(actual, expected);
        AssertMoveNextOutcomeMatches(actual, expected);
        AssertGenericCurrentOutcomeMatches(actual, expected);
        AssertResetOutcomeMatches(actual.Reset, expected.Reset);
        AssertGenericCurrentOutcomeMatches(actual, expected);
        AssertMoveNextOutcomeMatches(actual, expected);
        AssertGenericCurrentOutcomeMatches(actual, expected);
    }

    private static void AssertNonGenericEnumeratorPrefixMatches(IEnumerator actual, IEnumerator expected)
    {
        AssertNonGenericCurrentMatches(actual, expected);
        AssertMoveNextOutcomeMatches(actual, expected);
        AssertNonGenericCurrentMatches(actual, expected);
        AssertResetOutcomeMatches(actual.Reset, expected.Reset);
        AssertNonGenericCurrentMatches(actual, expected);
        AssertMoveNextOutcomeMatches(actual, expected);
        AssertNonGenericCurrentMatches(actual, expected);
    }

    private static void AssertGenericEnumeratorAfterAppendMatches(
        IEnumerator<MarkdownSemanticNode> actual,
        IEnumerator<MarkdownSemanticNode> expected)
    {
        AssertGenericCurrentOutcomeMatches(actual, expected);
        AssertMoveNextOutcomeMatches(actual, expected);
        AssertGenericCurrentOutcomeMatches(actual, expected);
        AssertResetOutcomeMatches(actual.Reset, expected.Reset);
        AssertDisposeOutcomeMatches(actual.Dispose, expected.Dispose);
    }

    private static void AssertNonGenericEnumeratorAfterAppendMatches(IEnumerator actual, IEnumerator expected)
    {
        AssertNonGenericCurrentMatches(actual, expected);
        AssertMoveNextOutcomeMatches(actual, expected);
        AssertNonGenericCurrentMatches(actual, expected);
        AssertResetOutcomeMatches(actual.Reset, expected.Reset);
        AssertDisposeOutcomeMatches(((IDisposable)actual).Dispose, ((IDisposable)expected).Dispose);
    }

    private static void AssertEmptyGenericInterfaceEnumeratorMatches(
        IEnumerator<MarkdownSemanticNode> actual,
        IEnumerator<MarkdownSemanticNode> expected)
    {
        AssertGenericCurrentOutcomeMatches(actual, expected);
        AssertMoveNextOutcomeMatches(actual, expected);
        AssertGenericCurrentOutcomeMatches(actual, expected);
        AssertResetOutcomeMatches(actual.Reset, expected.Reset);
        AssertGenericCurrentOutcomeMatches(actual, expected);
        AssertDisposeOutcomeMatches(actual.Dispose, expected.Dispose);
        AssertMoveNextOutcomeMatches(actual, expected);
    }

    private static void AssertEmptyNonGenericInterfaceEnumeratorMatches(IEnumerator actual, IEnumerator expected)
    {
        AssertNonGenericCurrentMatches(actual, expected);
        AssertMoveNextOutcomeMatches(actual, expected);
        AssertNonGenericCurrentMatches(actual, expected);
        AssertResetOutcomeMatches(actual.Reset, expected.Reset);
        AssertNonGenericCurrentMatches(actual, expected);
        AssertDisposeOutcomeMatches(((IDisposable)actual).Dispose, ((IDisposable)expected).Dispose);
        AssertMoveNextOutcomeMatches(actual, expected);
    }

    private static void AssertGenericCurrentOutcomeMatches(
        IEnumerator<MarkdownSemanticNode> actual,
        IEnumerator<MarkdownSemanticNode> expected)
    {
        object? actualValue = null;
        object? expectedValue = null;
        Exception? actualException = Record.Exception(() => actualValue = actual.Current);
        Exception? expectedException = Record.Exception(() => expectedValue = expected.Current);

        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException is null)
            Assert.Same(expectedValue, actualValue);
    }

    private static void AssertMoveNextOutcomeMatches(IEnumerator actual, IEnumerator expected)
    {
        bool actualMoved = false;
        bool expectedMoved = false;
        Exception? actualException = Record.Exception(() => actualMoved = actual.MoveNext());
        Exception? expectedException = Record.Exception(() => expectedMoved = expected.MoveNext());

        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException is null)
            Assert.Equal(expectedMoved, actualMoved);
    }

    private static void AssertResetOutcomeMatches(Action actual, Action expected)
    {
        Exception? actualException = Record.Exception(actual);
        Exception? expectedException = Record.Exception(expected);
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
    }

    private static void AssertDisposeOutcomeMatches(Action actual, Action expected)
    {
        Exception? actualException = Record.Exception(actual);
        Exception? expectedException = Record.Exception(expected);
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
    }

    private static void AssertInvalidated(
        IEnumerator<MarkdownSemanticNode> actual,
        IEnumerator<MarkdownSemanticNode> expected)
    {
        Assert.IsType<InvalidOperationException>(Record.Exception(() => actual.MoveNext()));
        Assert.IsType<InvalidOperationException>(Record.Exception(() => expected.MoveNext()));
        Assert.IsType<InvalidOperationException>(Record.Exception(() => actual.Reset()));
        Assert.IsType<InvalidOperationException>(Record.Exception(() => expected.Reset()));
    }

    private static void AssertInvalidated(IEnumerator actual, IEnumerator expected)
    {
        Assert.IsType<InvalidOperationException>(Record.Exception(() => actual.MoveNext()));
        Assert.IsType<InvalidOperationException>(Record.Exception(() => expected.MoveNext()));
        Assert.IsType<InvalidOperationException>(Record.Exception(() => actual.Reset()));
        Assert.IsType<InvalidOperationException>(Record.Exception(() => expected.Reset()));
    }

    private static void AssertSameItems(
        IReadOnlyList<MarkdownSemanticNode> expected,
        IReadOnlyList<MarkdownSemanticNode> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
            Assert.Same(expected[index], actual[index]);
    }
}
