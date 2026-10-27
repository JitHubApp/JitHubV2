using System.Collections;
using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SingletonMutableListTests
{
    [Fact]
    public void RetainedReadOnlyViewTracksEmptySingletonPromotionGrowthAndRemoval()
    {
        var actual = new SingletonMutableList<object>();
        IReadOnlyList<object> retainedView = actual;
        var expected = new List<object>();
        Assert.Same(actual, retainedView);
        AssertSameItems(expected, retainedView);

        object firstListItem = new List<object>();
        object third = new();
        object fourth = new();
        object fifth = new();
        object[] additions = [null!, firstListItem, third, fourth, fifth];

        foreach (object item in additions)
        {
            actual.Add(item);
            expected.Add(item);
            Assert.Same(retainedView, actual);
            AssertSameItems(expected, retainedView);
        }

        object[] multiSnapshot = actual.ToArray();
        AssertSameArrayItems(expected, multiSnapshot);

        // The first T=object item deliberately is itself a List<object>.
        // Singleton state is identified by its count, not the runtime type of
        // the stored item, before the second Add promotes to List<T> storage.
        var listItemAsSingleton = new SingletonMutableList<object>();
        listItemAsSingleton.Add(firstListItem);
        Assert.Same(firstListItem, listItemAsSingleton[0]);
        listItemAsSingleton.Add(third);
        Assert.Same(firstListItem, listItemAsSingleton[0]);
        Assert.Same(third, listItemAsSingleton[1]);

        actual.RemoveAt(2);
        expected.RemoveAt(2);
        AssertSameItems(expected, retainedView);
        actual.RemoveAt(1);
        expected.RemoveAt(1);
        AssertSameItems(expected, retainedView);

        // Keep removing through the List-backed singleton and empty states;
        // the retained view remains live while array snapshots stay detached.
        while (actual.Count > 1)
        {
            actual.RemoveAt(actual.Count - 1);
            expected.RemoveAt(expected.Count - 1);
        }

        object[] singletonSnapshot = actual.ToArray();
        AssertSameArrayItems(expected, singletonSnapshot);
        Assert.Single(singletonSnapshot);
        actual.RemoveAt(0);
        expected.RemoveAt(0);
        AssertSameItems(expected, retainedView);
        AssertSameArrayItems(new object[] { null! }, singletonSnapshot);

        actual.Clear();
        expected.Clear();
        AssertSameItems(expected, retainedView);
        Assert.Empty(actual.ToArray());

        actual.Add(firstListItem);
        expected.Add(firstListItem);
        actual.Add(null!);
        expected.Add(null!);
        AssertSameItems(expected, retainedView);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 4)]
    public void ConcreteEnumeratorsMatchListOracleAfterAppend(int initialCount, int moveCount)
    {
        var actualCollection = new SingletonMutableList<object>();
        var expectedCollection = new List<object>();
        for (int index = 0; index < initialCount; index++)
        {
            object item = new();
            actualCollection.Add(item);
            expectedCollection.Add(item);
        }

        SingletonMutableList<object>.Enumerator actual = actualCollection.GetEnumerator();
        List<object>.Enumerator expected = expectedCollection.GetEnumerator();
        for (int index = 0; index < moveCount; index++)
            AssertConcreteMoveNextMatches(ref actual, ref expected);

        Assert.Same(expected.Current, actual.Current);
        AssertNonGenericCurrentMatches((IEnumerator)actual, (IEnumerator)expected);

        object appended = new();
        actualCollection.Add(appended);
        expectedCollection.Add(appended);

        // Current remains a read of the enumerator's saved current value; the
        // next movement/reset observes List<T>'s version invalidation.
        Assert.Same(expected.Current, actual.Current);
        AssertNonGenericCurrentMatches((IEnumerator)actual, (IEnumerator)expected);
        AssertConcreteMoveNextMatches(ref actual, ref expected);
        AssertConcreteResetMatches(ref actual, ref expected);
        AssertDisposeMatches(((IDisposable)actual).Dispose, ((IDisposable)expected).Dispose);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ConcreteEnumeratorsMatchListOracleAfterClearAndRemove(int initialCount)
    {
        var clearActual = CreateList(initialCount, out List<object> clearExpected);
        SingletonMutableList<object>.Enumerator actualClearEnumerator = clearActual.GetEnumerator();
        List<object>.Enumerator expectedClearEnumerator = clearExpected.GetEnumerator();
        clearActual.Clear();
        clearExpected.Clear();
        Assert.Same(expectedClearEnumerator.Current, actualClearEnumerator.Current);
        AssertConcreteMoveNextMatches(ref actualClearEnumerator, ref expectedClearEnumerator);
        AssertConcreteResetMatches(ref actualClearEnumerator, ref expectedClearEnumerator);

        if (initialCount == 0)
            return;

        var removeActual = CreateList(initialCount, out List<object> removeExpected);
        SingletonMutableList<object>.Enumerator actualRemoveEnumerator = removeActual.GetEnumerator();
        List<object>.Enumerator expectedRemoveEnumerator = removeExpected.GetEnumerator();
        removeActual.RemoveAt(initialCount - 1);
        removeExpected.RemoveAt(initialCount - 1);
        Assert.Same(expectedRemoveEnumerator.Current, actualRemoveEnumerator.Current);
        AssertNonGenericCurrentMatches((IEnumerator)actualRemoveEnumerator, (IEnumerator)expectedRemoveEnumerator);
        AssertConcreteMoveNextMatches(ref actualRemoveEnumerator, ref expectedRemoveEnumerator);
        AssertConcreteResetMatches(ref actualRemoveEnumerator, ref expectedRemoveEnumerator);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void InterfaceEnumeratorsMatchListAndEmptyArrayFastPaths(int initialCount)
    {
        var actualCollection = CreateList(initialCount, out List<object> expectedCollection);
        IEnumerable<object> actualSequence = actualCollection;
        IEnumerable<object> expectedSequence = expectedCollection;

        IEnumerator<object> actualGeneric = actualSequence.GetEnumerator();
        IEnumerator<object> expectedGeneric = expectedSequence.GetEnumerator();
        IEnumerator actualNonGeneric = ((IEnumerable)actualSequence).GetEnumerator();
        IEnumerator expectedNonGeneric = ((IEnumerable)expectedSequence).GetEnumerator();

        AssertGenericCurrentMatches(actualGeneric, expectedGeneric);
        AssertNonGenericCurrentMatches(actualNonGeneric, expectedNonGeneric);
        AssertMoveNextMatches(actualGeneric, expectedGeneric);
        AssertMoveNextMatches(actualNonGeneric, expectedNonGeneric);

        object appended = new();
        actualCollection.Add(appended);
        expectedCollection.Add(appended);

        AssertGenericCurrentMatches(actualGeneric, expectedGeneric);
        AssertNonGenericCurrentMatches(actualNonGeneric, expectedNonGeneric);
        AssertMoveNextMatches(actualGeneric, expectedGeneric);
        AssertMoveNextMatches(actualNonGeneric, expectedNonGeneric);
        AssertResetMatches(actualGeneric.Reset, expectedGeneric.Reset);
        AssertResetMatches(actualNonGeneric.Reset, expectedNonGeneric.Reset);
        AssertDisposeMatches(actualGeneric.Dispose, expectedGeneric.Dispose);
        AssertDisposeMatches(((IDisposable)actualNonGeneric).Dispose, ((IDisposable)expectedNonGeneric).Dispose);
    }

    [Fact]
    public void DefaultPatternEnumeratorMatchesDefaultListEnumerator()
    {
        SingletonMutableList<object>.Enumerator actual = default;
        List<object>.Enumerator expected = default;

        AssertGenericCurrentMatches(actual, expected);
        AssertNonGenericCurrentMatches((IEnumerator)actual, (IEnumerator)expected);
        AssertConcreteMoveNextMatches(ref actual, ref expected);
        AssertConcreteResetMatches(ref actual, ref expected);
        AssertDisposeMatches(((IDisposable)actual).Dispose, ((IDisposable)expected).Dispose);
    }

    [Fact]
    public void InvalidIndexesMatchListAndDoNotInvalidateExistingEnumerator()
    {
        var actual = new SingletonMutableList<object>();
        var expected = new List<object>();
        object first = new();
        actual.Add(first);
        expected.Add(first);
        object second = new();
        actual.Add(second);
        expected.Add(second);
        IReadOnlyList<object> actualView = actual;
        IReadOnlyList<object> expectedView = expected;
        AssertIndexFailureMatches(actualView, expectedView, -1);
        AssertIndexFailureMatches(actualView, expectedView, expected.Count);

        SingletonMutableList<object>.Enumerator actualEnumerator = actual.GetEnumerator();
        List<object>.Enumerator expectedEnumerator = expected.GetEnumerator();
        AssertRemoveFailureMatches(actual, expected, -1);
        AssertRemoveFailureMatches(actual, expected, actual.Count);
        AssertConcreteMoveNextMatches(ref actualEnumerator, ref expectedEnumerator);
        Assert.Same(expectedEnumerator.Current, actualEnumerator.Current);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FailedRemoveAtPreservesNeverPromotedConcreteEnumerator(int initialCount)
    {
        var actual = new SingletonMutableList<object>();
        var expected = new List<object>();
        if (initialCount != 0)
        {
            object item = new();
            actual.Add(item);
            expected.Add(item);
        }

        IReadOnlyList<object> retainedView = actual;
        SingletonMutableList<object>.Enumerator actualEnumerator = actual.GetEnumerator();
        List<object>.Enumerator expectedEnumerator = expected.GetEnumerator();

        int invalidIndex = initialCount;
        AssertRemoveFailureMatches(actual, expected, invalidIndex);
        Assert.Same(actual, retainedView);
        AssertSameItems(expected, retainedView);

        // A failed removal must not advance the collection version, even for
        // the never-promoted empty and singleton representations.
        AssertConcreteMoveNextMatches(ref actualEnumerator, ref expectedEnumerator);
        if (initialCount == 1)
            Assert.Same(expectedEnumerator.Current, actualEnumerator.Current);

        AssertConcreteResetMatches(ref actualEnumerator, ref expectedEnumerator);
        AssertConcreteMoveNextMatches(ref actualEnumerator, ref expectedEnumerator);
    }

    [Fact]
    public void RemovingSingletonReleasesLiveViewAndAllowsReAdd()
    {
        var actual = new SingletonMutableList<object>();
        var expected = new List<object>();
        object removedItem = new();
        actual.Add(removedItem);
        expected.Add(removedItem);
        IReadOnlyList<object> retainedView = actual;
        object[] detachedSnapshot = actual.ToArray();
        SingletonMutableList<object>.Enumerator actualEnumerator = actual.GetEnumerator();
        List<object>.Enumerator expectedEnumerator = expected.GetEnumerator();

        actual.RemoveAt(0);
        expected.RemoveAt(0);
        Assert.Same(actual, retainedView);
        Assert.Empty(retainedView);
        Assert.Same(removedItem, detachedSnapshot[0]);
        AssertConcreteMoveNextMatches(ref actualEnumerator, ref expectedEnumerator);

        object replacement = new();
        actual.Add(replacement);
        expected.Add(replacement);
        Assert.Same(actual, retainedView);
        AssertSameItems(expected, retainedView);
        Assert.Same(removedItem, detachedSnapshot[0]);

        // The enumerator captured before removal remains invalid after a
        // subsequent Add; a fresh enumerator observes only the replacement.
        AssertConcreteMoveNextMatches(ref actualEnumerator, ref expectedEnumerator);
        SingletonMutableList<object>.Enumerator freshActual = actual.GetEnumerator();
        List<object>.Enumerator freshExpected = expected.GetEnumerator();
        AssertConcreteMoveNextMatches(ref freshActual, ref freshExpected);
        Assert.Same(freshExpected.Current, freshActual.Current);
        Assert.Same(replacement, retainedView[0]);
    }

    [Fact]
    public void StackChildrenKeepOneLiveViewAndDetachTransfersOwnership()
    {
        var stack = new StackBox();
        IReadOnlyList<BlockBox> retainedView = stack.Children;
        Assert.Same(retainedView, stack.Children);
        Assert.Empty(retainedView);

        var first = new TrackingBlockBox();
        var second = new TrackingBlockBox();
        stack.Add(first);
        stack.Add(second);
        Assert.Same(retainedView, stack.Children);
        Assert.Equal(2, retainedView.Count);
        Assert.Same(first, retainedView[0]);
        Assert.Same(second, retainedView[1]);

        BlockBox[] detached = stack.DetachChildrenForTransfer();
        Assert.Collection(detached,
            item => Assert.Same(first, item),
            item => Assert.Same(second, item));
        Assert.Empty(retainedView);

        var replacement = new TrackingBlockBox();
        stack.Add(replacement);
        Assert.Same(replacement, retainedView[0]);
        stack.Dispose();
        Assert.Equal(0, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
        Assert.Equal(1, replacement.DisposeCount);

        var receivingStack = new StackBox();
        receivingStack.Add(detached[0]);
        receivingStack.Add(detached[1]);
        receivingStack.Dispose();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    private static SingletonMutableList<object> CreateList(int count, out List<object> expected)
    {
        var actual = new SingletonMutableList<object>();
        expected = new List<object>();
        for (int index = 0; index < count; index++)
        {
            object item = new();
            actual.Add(item);
            expected.Add(item);
        }

        return actual;
    }

    private static void AssertSameItems(IReadOnlyList<object> expected, IReadOnlyList<object> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
            Assert.Same(expected[index], actual[index]);

        AssertIndexFailureMatches(actual, expected, -1);
        AssertIndexFailureMatches(actual, expected, expected.Count);
    }

    private static void AssertSameArrayItems(IReadOnlyList<object> expected, object[] actual)
    {
        Assert.Equal(expected.Count, actual.Length);
        for (int index = 0; index < expected.Count; index++)
            Assert.Same(expected[index], actual[index]);
    }

    private static void AssertIndexFailureMatches(
        IReadOnlyList<object> actual,
        IReadOnlyList<object> expected,
        int index)
    {
        Exception? actualException = Record.Exception(() => _ = actual[index]);
        Exception? expectedException = Record.Exception(() => _ = expected[index]);
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        Assert.Equal(
            (expectedException as ArgumentOutOfRangeException)?.ParamName,
            (actualException as ArgumentOutOfRangeException)?.ParamName);
    }

    private static void AssertRemoveFailureMatches(
        SingletonMutableList<object> actual,
        List<object> expected,
        int index)
    {
        Exception? actualException = Record.Exception(() => actual.RemoveAt(index));
        Exception? expectedException = Record.Exception(() => expected.RemoveAt(index));
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        Assert.Equal(
            (expectedException as ArgumentOutOfRangeException)?.ParamName,
            (actualException as ArgumentOutOfRangeException)?.ParamName);
    }

    private static void AssertGenericCurrentMatches<T>(IEnumerator<T> actual, IEnumerator<T> expected)
        where T : class
    {
        T? actualValue = default;
        T? expectedValue = default;
        Exception? actualException = Record.Exception(() => actualValue = actual.Current);
        Exception? expectedException = Record.Exception(() => expectedValue = expected.Current);
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException is null)
            Assert.Same(expectedValue, actualValue);
    }

    private static void AssertNonGenericCurrentMatches(IEnumerator actual, IEnumerator expected)
    {
        object? actualValue = null;
        object? expectedValue = null;
        Exception? actualException = Record.Exception(() => actualValue = actual.Current);
        Exception? expectedException = Record.Exception(() => expectedValue = expected.Current);
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException is null)
            Assert.Same(expectedValue, actualValue);
    }

    private static void AssertMoveNextMatches(IEnumerator actual, IEnumerator expected)
    {
        bool actualMoved = false;
        bool expectedMoved = false;
        Exception? actualException = Record.Exception(() => actualMoved = actual.MoveNext());
        Exception? expectedException = Record.Exception(() => expectedMoved = expected.MoveNext());
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException is null)
            Assert.Equal(expectedMoved, actualMoved);
    }

    private static void AssertMoveNextMatches<T>(IEnumerator<T> actual, IEnumerator<T> expected)
    {
        bool actualMoved = false;
        bool expectedMoved = false;
        Exception? actualException = Record.Exception(() => actualMoved = actual.MoveNext());
        Exception? expectedException = Record.Exception(() => expectedMoved = expected.MoveNext());
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException is null)
            Assert.Equal(expectedMoved, actualMoved);
    }

    private static void AssertConcreteMoveNextMatches(
        ref SingletonMutableList<object>.Enumerator actual,
        ref List<object>.Enumerator expected)
    {
        bool actualMoved = false;
        bool expectedMoved = false;
        Exception? actualException = null;
        Exception? expectedException = null;
        try { actualMoved = actual.MoveNext(); }
        catch (Exception exception) { actualException = exception; }
        try { expectedMoved = expected.MoveNext(); }
        catch (Exception exception) { expectedException = exception; }
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
        if (expectedException is null)
            Assert.Equal(expectedMoved, actualMoved);
    }

    private static void AssertConcreteResetMatches(
        ref SingletonMutableList<object>.Enumerator actual,
        ref List<object>.Enumerator expected)
    {
        Exception? actualException = null;
        Exception? expectedException = null;
        IEnumerator expectedBox = expected;
        try { actual.Reset(); }
        catch (Exception exception) { actualException = exception; }
        try { expectedBox.Reset(); }
        catch (Exception exception) { expectedException = exception; }
        expected = (List<object>.Enumerator)expectedBox;
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
    }

    private static void AssertResetMatches(Action actual, Action expected)
    {
        Exception? actualException = Record.Exception(actual);
        Exception? expectedException = Record.Exception(expected);
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
    }

    private static void AssertDisposeMatches(Action actual, Action expected)
    {
        Exception? actualException = Record.Exception(actual);
        Exception? expectedException = Record.Exception(expected);
        Assert.Equal(expectedException?.GetType(), actualException?.GetType());
    }

    private sealed class TrackingBlockBox : BlockBox
    {
        public int DisposeCount { get; private set; }

        public override float Measure(float availableWidth) => 0;

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport)
        {
        }

        public override void Dispose() => DisposeCount++;
    }
}
