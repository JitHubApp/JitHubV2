using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Common.Collections;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace CommunityToolkit.WinUI;

public interface IIncrementalLoadingSourceState
{
    bool HasMoreItems { get; }
}

public partial class IncrementalLoadingCollection<TSource, TItem> : ObservableCollection<TItem>, ISupportIncrementalLoading
    where TSource : IIncrementalSource<TItem>
{
    private readonly int _itemsPerPage;
    private readonly TSource _source;
    private bool _isLoading;
    private int _pageIndex;

    public IncrementalLoadingCollection(TSource source, int itemsPerPage = 20)
    {
        _source = source;
        _itemsPerPage = itemsPerPage;
        HasMoreItems = true;
    }

    public bool HasMoreItems { get; private set; }

    public bool IsLoading => _isLoading;

    public event Action? OnStartLoading;

    public event Action? OnEndLoading;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Clear();
        _pageIndex = 0;
        HasMoreItems = true;
        await LoadNextPageAsync(_itemsPerPage, cancellationToken);
    }

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        => LoadMoreItemsAsyncInternal(count).AsAsyncOperation();

    private async Task<LoadMoreItemsResult> LoadMoreItemsAsyncInternal(uint count)
    {
        if (_isLoading || !HasMoreItems)
        {
            return new LoadMoreItemsResult { Count = 0 };
        }

        int requestedPageSize = Math.Max((int)count, _itemsPerPage);
        return await LoadNextPageAsync(requestedPageSize, CancellationToken.None);
    }

    private async Task<LoadMoreItemsResult> LoadNextPageAsync(int pageSize, CancellationToken cancellationToken)
    {
        if (_isLoading || !HasMoreItems)
        {
            return new LoadMoreItemsResult { Count = 0 };
        }

        _isLoading = true;
        OnStartLoading?.Invoke();
        try
        {
            IReadOnlyList<TItem> pageItems = (await _source.GetPagedItemsAsync(_pageIndex, pageSize, cancellationToken))
                .ToList();

            foreach (TItem item in pageItems)
            {
                Add(item);
            }

            _pageIndex++;
            if (_source is IIncrementalLoadingSourceState sourceState)
            {
                HasMoreItems = sourceState.HasMoreItems;
            }
            else if (pageItems.Count < pageSize)
            {
                HasMoreItems = false;
            }

            return new LoadMoreItemsResult { Count = (uint)pageItems.Count };
        }
        finally
        {
            _isLoading = false;
            OnEndLoading?.Invoke();
        }
    }
}
