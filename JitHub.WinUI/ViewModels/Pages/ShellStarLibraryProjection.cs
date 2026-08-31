using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed record ShellStarLibrarySnapshot(
    string UserId,
    int IndexedCount,
    bool IsComplete,
    StarLibraryDegradedState DegradedState);

public sealed partial class ShellStarLibraryProjection : IDisposable
{
    private readonly IGitHubStarLibraryService _starLibraryService;
    private readonly object _gate = new();
    private string _userId = string.Empty;
    private long _refreshVersion;
    private int _lastIndexedCount;
    private bool _disposed;

    public ShellStarLibraryProjection(IGitHubStarLibraryService starLibraryService)
    {
        _starLibraryService = starLibraryService ?? throw new ArgumentNullException(nameof(starLibraryService));
        _starLibraryService.Changed += StarLibraryService_Changed;
    }

    public event EventHandler<ShellStarLibrarySnapshot>? Changed;

    public async Task SetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _userId = userId?.Trim() ?? string.Empty;
            _lastIndexedCount = 0;
            _refreshVersion++;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        string userId;
        long refreshVersion;
        lock (_gate)
        {
            if (_disposed || string.IsNullOrWhiteSpace(_userId))
            {
                return;
            }

            userId = _userId;
            refreshVersion = ++_refreshVersion;
        }

        try
        {
            StarLibraryPage page = await _starLibraryService.QueryAsync(
                new StarLibraryQuery(
                    userId,
                    StarSmartList.All,
                    CategoryId: null,
                    SearchText: string.Empty,
                    StarLibraryFilter.Empty,
                    StarLibrarySort.RecentlyStarred,
                    Offset: 0,
                    Limit: 1),
                cancellationToken).ConfigureAwait(false);
            PublishIfCurrent(
                userId,
                refreshVersion,
                Math.Max(page.TotalCount, page.SyncState.IndexedCount),
                page.SyncState.IsComplete,
                _starLibraryService.GetDegradedState(userId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            PublishIfCurrent(
                userId,
                refreshVersion,
                _lastIndexedCount,
                isComplete: false,
                _starLibraryService.GetDegradedState(userId));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _refreshVersion++;
        }

        _starLibraryService.Changed -= StarLibraryService_Changed;
    }

    private void StarLibraryService_Changed(object? sender, StarLibraryChangedEventArgs e)
    {
        bool shouldRefresh;
        lock (_gate)
        {
            shouldRefresh = !_disposed &&
                string.Equals(e.UserId, _userId, StringComparison.Ordinal) &&
                e.Kind is StarLibraryChangeKind.Items or StarLibraryChangeKind.Sync or StarLibraryChangeKind.Degraded;
        }

        if (shouldRefresh)
        {
            _ = RefreshAfterNotificationAsync();
        }
    }

    private async Task RefreshAfterNotificationAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // RefreshAsync already preserves and publishes the last local count on failure.
        }
    }

    private void PublishIfCurrent(
        string userId,
        long refreshVersion,
        int indexedCount,
        bool isComplete,
        StarLibraryDegradedState degradedState)
    {
        ShellStarLibrarySnapshot snapshot;
        lock (_gate)
        {
            if (_disposed ||
                refreshVersion != _refreshVersion ||
                !string.Equals(userId, _userId, StringComparison.Ordinal))
            {
                return;
            }

            _lastIndexedCount = Math.Max(0, indexedCount);
            snapshot = new ShellStarLibrarySnapshot(
                userId,
                _lastIndexedCount,
                isComplete,
                degradedState);
        }

        Changed?.Invoke(this, snapshot);
    }
}
