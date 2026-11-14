using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.Telemetry;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Performance;

namespace JitHub.Core.Services.Images.Flights;

/// <summary>
/// Deduplicates concurrent leased image fetches. Each joined caller receives
/// its own retained handle to the shared result; the last caller retires it.
/// </summary>
internal sealed class GitHubLeasedImageFlights
{
    private readonly ConcurrentDictionary<GitHubLeasedImageFlightKey, SharedLeasedImageRequest> _requests = new();

    internal Task<GitHubImageSourceLease?> JoinAsync(
        GitHubLeasedImageFlightKey key,
        Func<CancellationToken, Task<GitHubImageSourceLease?>> taskFactory,
        IMarkdownImageSourceWorkTracker? workTracker,
        CancellationToken callerCancellationToken)
    {
        while (true)
        {
            SharedLeasedImageRequest request = _requests.GetOrAdd(
                key,
                _ => new SharedLeasedImageRequest(taskFactory));
            if (request.TryAddWaiter())
            {
                request.Start(workTracker);
                return AwaitSharedAsync(key, request, callerCancellationToken);
            }

            _requests.TryRemove(new KeyValuePair<GitHubLeasedImageFlightKey, SharedLeasedImageRequest>(key, request));
        }
    }

    internal void CancelAll()
    {
        foreach (SharedLeasedImageRequest request in _requests.Values)
        {
            try
            {
                request.Cancel();
            }
            catch (Exception exception)
            {
                // Service disposal must proceed even if a cancellation callback or
                // cancellation infrastructure fails unexpectedly.
                HandledFailureReporter.Report(exception, "github-image-flight-cancel");
            }
        }
    }

    private async Task<GitHubImageSourceLease?> AwaitSharedAsync(
        GitHubLeasedImageFlightKey key,
        SharedLeasedImageRequest request,
        CancellationToken callerCancellationToken)
    {
        Task<bool> task = request.Task;
        try
        {
            bool hasImage = await task.WaitAsync(callerCancellationToken)
                .ConfigureAwait(false);
            return hasImage ? request.RetainResult() : null;
        }
        finally
        {
            SharedImageRequestRelease release = request.ReleaseWaiter(task.IsCompleted);
            try
            {
                if (release.ShouldCancel)
                    request.Cancel();
            }
            catch (Exception exception)
            {
                // Cancellation is best-effort cleanup. A token callback must never
                // strand the flight in the dictionary or bypass root retirement.
                HandledFailureReporter.Report(exception, "github-image-flight-cancel");
            }
            finally
            {
                if (release.ShouldRetire)
                {
                    try
                    {
                        _requests.TryRemove(
                            new KeyValuePair<GitHubLeasedImageFlightKey, SharedLeasedImageRequest>(key, request));
                    }
                    finally
                    {
                        request.DisposeWhenComplete();
                    }
                }
            }
        }
    }
}
