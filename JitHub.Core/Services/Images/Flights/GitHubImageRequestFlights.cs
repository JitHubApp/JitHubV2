using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;

namespace JitHub.Core.Services.Images.Flights;

/// <summary>
/// Deduplicates concurrent image fetches by flight key. Callers join a shared
/// <see cref="SharedImageRequest"/>; the last caller to leave retires it.
/// </summary>
internal sealed class GitHubImageRequestFlights
{
    private readonly ConcurrentDictionary<string, SharedImageRequest> _requests =
        new(StringComparer.Ordinal);

    internal Task<GitHubCachedImage?> JoinAsync(
        string flightKey,
        Func<CancellationToken, Task<GitHubCachedImage?>> taskFactory,
        CancellationToken callerCancellationToken)
    {
        while (true)
        {
            SharedImageRequest request = _requests.GetOrAdd(flightKey, _ => new SharedImageRequest(taskFactory));
            if (request.TryAddWaiter())
            {
                return AwaitSharedAsync(flightKey, request, callerCancellationToken);
            }

            _requests.TryRemove(new KeyValuePair<string, SharedImageRequest>(flightKey, request));
        }
    }

    internal void CancelAll()
    {
        foreach (SharedImageRequest request in _requests.Values)
        {
            request.Cancel();
        }
    }

    private async Task<GitHubCachedImage?> AwaitSharedAsync(
        string flightKey,
        SharedImageRequest request,
        CancellationToken callerCancellationToken)
    {
        Task<GitHubCachedImage?> task = request.Task;
        try
        {
            return await task.WaitAsync(callerCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SharedImageRequestRelease release = request.ReleaseWaiter(task.IsCompleted);
            if (release.ShouldCancel)
            {
                request.Cancel();
            }

            if (release.ShouldRetire)
            {
                _requests.TryRemove(new KeyValuePair<string, SharedImageRequest>(flightKey, request));
                request.DisposeWhenComplete();
            }
        }
    }
}
