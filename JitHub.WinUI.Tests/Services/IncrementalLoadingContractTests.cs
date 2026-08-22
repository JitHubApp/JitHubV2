using CommunityToolkit.Common.Collections;
using CommunityToolkit.WinUI;
using JitHub.WinUI.Behaviors;
using Microsoft.UI.Xaml.Data;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class IncrementalLoadingContractTests
{
    [Fact]
    public void Resolve_UsesExplicitThenItemsThenHostWithoutReflection()
    {
        StubIncrementalSource explicitSource = new();
        StubIncrementalSource itemsSource = new();
        StubIncrementalSource hostSource = new();
        StubHost host = new(hostSource);

        Assert.Same(explicitSource, IncrementalLoadingSourceAdapter.Resolve(explicitSource, itemsSource, host));
        Assert.Same(itemsSource, IncrementalLoadingSourceAdapter.Resolve(null, itemsSource, host));
        Assert.Same(hostSource, IncrementalLoadingSourceAdapter.Resolve(null, new object(), host));
        Assert.Null(IncrementalLoadingSourceAdapter.Resolve(null, new object(), new object()));
    }

    [Fact]
    public void IsLoading_UsesTypedActivityContract()
    {
        Assert.True(IncrementalLoadingSourceAdapter.IsLoading(new StubIncrementalSource(isLoading: true)));
        Assert.False(IncrementalLoadingSourceAdapter.IsLoading(new StubIncrementalSource(isLoading: false)));
    }

    [Fact]
    public async Task Collection_PagesAndStopsUsingTypedSourceState()
    {
        PagedSource source = new([[1, 2], [3]]);
        IncrementalLoadingCollection<PagedSource, int> collection = new(source, itemsPerPage: 2);
        source.IsConsumerLoading = () => collection.IsLoading;
        List<string> events = [];
        collection.OnStartLoading += () => events.Add("start");
        collection.OnEndLoading += () => events.Add("end");

        LoadMoreItemsResult first = await collection.LoadMoreItemsAsync(1);
        LoadMoreItemsResult second = await collection.LoadMoreItemsAsync(2);
        LoadMoreItemsResult afterEnd = await collection.LoadMoreItemsAsync(2);

        Assert.Equal((uint)2, first.Count);
        Assert.Equal((uint)1, second.Count);
        Assert.Equal((uint)0, afterEnd.Count);
        Assert.Equal([1, 2, 3], collection);
        Assert.Equal([(0, 2), (1, 2)], source.Requests);
        Assert.Equal(["start", "end", "start", "end"], events);
        Assert.False(collection.HasMoreItems);
        Assert.False(collection.IsLoading);
    }

    private sealed class StubHost(ISupportIncrementalLoading source) : IIncrementalLoadingHost
    {
        public ISupportIncrementalLoading IncrementalLoadingSource { get; } = source;
    }

    private sealed class StubIncrementalSource(bool isLoading = false) : ISupportIncrementalLoading, IIncrementalLoadingActivity
    {
        public bool HasMoreItems => false;

        public bool IsLoading { get; } = isLoading;

        public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
            => Task.FromResult(new LoadMoreItemsResult()).AsAsyncOperation();
    }

    private sealed class PagedSource(IReadOnlyList<IReadOnlyList<int>> pages) : IIncrementalSource<int>, IIncrementalLoadingSourceState
    {
        public List<(int PageIndex, int PageSize)> Requests { get; } = [];

        public Func<bool>? IsConsumerLoading { get; set; }

        public bool HasMoreItems { get; private set; } = true;

        public Task<IEnumerable<int>> GetPagedItemsAsync(
            int pageIndex,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(IsConsumerLoading?.Invoke());
            Requests.Add((pageIndex, pageSize));
            IReadOnlyList<int> page = pages[pageIndex];
            HasMoreItems = pageIndex + 1 < pages.Count;
            return Task.FromResult<IEnumerable<int>>(page);
        }
    }
}
