using System.Collections.ObjectModel;
using System.Collections.Specialized;
using JitHub.WinUI.ViewModels.Common;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class KeyedObservableReconcilerTests
{
    [Fact]
    public void ApplySnapshot_PreservesUnchangedRowsAndNeverRaisesReset()
    {
        Row first = new("1", "first");
        Row second = new("2", "second");
        ObservableCollection<Row> rows = [first, second];
        List<NotifyCollectionChangedAction> actions = [];
        rows.CollectionChanged += (_, args) => actions.Add(args.Action);

        KeyedCollectionDiffResult result = KeyedObservableReconciler.ApplySnapshot(
            rows,
            [new Row("2", "second"), new Row("1", "updated"), new Row("3", "third")],
            static row => row.Id,
            static (current, incoming) => current == incoming);

        Assert.Same(second, rows[0]);
        Assert.Equal("updated", rows[1].Value);
        Assert.Equal("3", rows[2].Id);
        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Added);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void ApplySnapshot_RemovesOnlyMissingRows()
    {
        Row first = new("1", "first");
        Row second = new("2", "second");
        ObservableCollection<Row> rows = [first, second];

        KeyedCollectionDiffResult result = KeyedObservableReconciler.ApplySnapshot(
            rows,
            [new Row("1", "first")],
            static row => row.Id,
            static (current, incoming) => current == incoming);

        Assert.Same(first, Assert.Single(rows));
        Assert.Equal(1, result.Removed);
    }

    private sealed record Row(string Id, string Value);
}
