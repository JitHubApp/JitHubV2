using System.Collections.Specialized;
using System.Linq;
using JitHub.WinUI.ViewModels.Common;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class KeyedObservableCollectionTests
{
    [Fact]
    public void ResetSnapshot_PopulatesWithOneStableInstanceReset()
    {
        KeyedObservableCollection<TestItem, TestSnapshot> collection = [];
        collection.Add(new TestItem("old", "Old"));
        int notificationCount = 0;
        NotifyCollectionChangedAction? action = null;
        collection.CollectionChanged += (_, args) =>
        {
            notificationCount++;
            action = args.Action;
        };

        KeyedCollectionDiffResult result = collection.ResetSnapshot(
            [
                new TestSnapshot("a", "Alpha"),
                new TestSnapshot("b", "Beta"),
                new TestSnapshot("a", "Duplicate")
            ],
            static snapshot => snapshot.Key,
            static snapshot => new TestItem(snapshot.Key, snapshot.Title));

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(["a", "b"], collection.Select(static item => item.Key));
        Assert.Equal(1, notificationCount);
        Assert.Equal(NotifyCollectionChangedAction.Reset, action);
    }

    [Fact]
    public void ApplySnapshot_AddsMovesUpdatesAndRemovesWithoutClearing()
    {
        KeyedObservableCollection<TestItem, TestSnapshot> collection = [];
        TestItem first = new("a", "Alpha");
        TestItem second = new("b", "Beta");
        TestItem removed = new("c", "Gamma");
        collection.Add(first);
        collection.Add(second);
        collection.Add(removed);

        KeyedCollectionDiffResult result = collection.ApplySnapshot(
            [
                new TestSnapshot("b", "Beta 2"),
                new TestSnapshot("a", "Alpha"),
                new TestSnapshot("d", "Delta")
            ],
            static snapshot => snapshot.Key,
            static item => item.Key,
            static snapshot => new TestItem(snapshot.Key, snapshot.Title),
            static (item, snapshot) => item.Apply(snapshot));

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(["b", "a", "d"], collection.Select(static item => item.Key));
        Assert.Same(second, collection[0]);
        Assert.Same(first, collection[1]);
        Assert.Equal("Beta 2", second.Title);
    }

    [Fact]
    public void ApplySnapshot_AppendingStableRowsUsesNearLinearKeyLookups()
    {
        KeyedObservableCollection<TestItem, TestSnapshot> collection = [];
        TestSnapshot[] initial = Enumerable.Range(0, 1000)
            .Select(static index => new TestSnapshot(index.ToString(), index.ToString()))
            .ToArray();
        collection.ApplySnapshot(
            initial,
            static snapshot => snapshot.Key,
            static item => item.Key,
            static snapshot => new TestItem(snapshot.Key, snapshot.Title));

        int itemKeyCalls = 0;
        TestSnapshot[] expanded = Enumerable.Range(0, 1100)
            .Select(static index => new TestSnapshot(index.ToString(), index.ToString()))
            .ToArray();

        collection.ApplySnapshot(
            expanded,
            static snapshot => snapshot.Key,
            item =>
            {
                itemKeyCalls++;
                return item.Key;
            },
            static snapshot => new TestItem(snapshot.Key, snapshot.Title));

        Assert.Equal(1100, collection.Count);
        Assert.True(itemKeyCalls < 4000, $"Expected near-linear key lookup work, observed {itemKeyCalls} calls.");
    }

    [Fact]
    public void ApplySnapshot_CanPreserveMissingRowsForPartialRefreshes()
    {
        KeyedObservableCollection<TestItem, TestSnapshot> collection = [];
        collection.Add(new TestItem("a", "Alpha"));
        collection.Add(new TestItem("b", "Beta"));

        KeyedCollectionDiffResult result = collection.ApplySnapshot(
            [new TestSnapshot("b", "Beta 2")],
            static snapshot => snapshot.Key,
            static item => item.Key,
            static snapshot => new TestItem(snapshot.Key, snapshot.Title),
            static (item, snapshot) => item.Apply(snapshot),
            KeyedCollectionDiffOptions.PreserveMissing);

        Assert.Equal(0, result.Removed);
        Assert.Equal(["b", "a"], collection.Select(static item => item.Key));
        Assert.Equal("Beta 2", collection[0].Title);
    }

    private sealed class TestItem(string key, string title)
    {
        public string Key { get; } = key;

        public string Title { get; private set; } = title;

        public bool Apply(TestSnapshot snapshot)
        {
            if (Title == snapshot.Title)
            {
                return false;
            }

            Title = snapshot.Title;
            return true;
        }
    }

    private readonly record struct TestSnapshot(string Key, string Title);
}
