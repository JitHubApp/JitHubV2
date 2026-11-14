using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance.Internal.Scheduling;

namespace MarkdownRenderer.Performance.Internal.Session.ImageSources;

/// <summary>
/// The source-byte admission and detached-work tracker handed to a host
/// resolver for one fetch. It holds one unit of session work until it is
/// closed and every tracked task has settled.
/// </summary>
internal sealed class ResolverSourceByteAdmission :
    IMarkdownImageSourceByteAdmission,
    IMarkdownImageSourceWorkTracker,
    IDisposable
{
    private readonly object _workGate = new();
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly MarkdownSessionAdmissions _admissions;
    private readonly SourceByteAdmission _admission;
    private readonly MarkdownPerformanceDocumentScope _owner;
    private readonly bool _background;
    private readonly CancellationToken _requestCancellationToken;
    private int _trackedWorkCount;
    private bool _closed;
    private bool _sessionLifetimeReleased;

    internal ResolverSourceByteAdmission(
        MarkdownPerformanceSessionLifetime lifetime,
        MarkdownSessionAdmissions admissions,
        MarkdownPerformanceDocumentScope owner,
        bool background,
        CancellationToken requestCancellationToken)
    {
        _lifetime = lifetime;
        _admissions = admissions;
        _admission = admissions.SourceBytes;
        _owner = owner;
        _background = background;
        _requestCancellationToken = requestCancellationToken;

        // Reserve one shutdown-drain unit before exposing this scope to the
        // host. Child registration then only adjusts this already-owned
        // lifetime and never calls RegisterWork after session disposal.
        lifetime.RegisterWork();
    }

    public bool IsSpeculative => _background;

    public long MaximumReservationBytes => _background
        ? _admission.BackgroundCapacity
        : _admission.Capacity;

    public async ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
    {
        if (_background && bytes > _admission.BackgroundCapacity)
            throw new MarkdownImageSourceDeferredException();
        return await _admissions.ReserveSourceBytesAsync(
                _owner,
                bytes,
                _background,
                cancellationToken,
                _requestCancellationToken)
            .ConfigureAwait(false);
    }

    public void TrackWork(Task completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_workGate)
        {
            if (_closed)
            {
                throw new InvalidOperationException(
                    "The image resolver work-tracking scope is closed.");
            }

            checked { _trackedWorkCount++; }
        }

        _ = ObserveTrackedWorkAsync(completion);
    }

    public void Dispose()
    {
        bool releaseSessionLifetime;
        lock (_workGate)
        {
            if (_closed)
                return;

            _closed = true;
            releaseSessionLifetime = TryReleaseSessionLifetimeNoLock();
        }

        if (releaseSessionLifetime)
            _lifetime.RetireWork();
    }

    private async Task ObserveTrackedWorkAsync(Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Losing image branches normally settle through cancellation.
        }
        catch (Exception exception)
        {
            // The task's actual fault is observed, but never log its message
            // or source-bearing state from the caller-owned completion task.
            try
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] detached image resolver work failed: " +
                    $"{exception.GetType().Name}; HRESULT=0x{exception.HResult:X8}");
            }
            catch
            {
            }
        }
        finally
        {
            CompleteTrackedWork();
        }
    }

    private void CompleteTrackedWork()
    {
        bool releaseSessionLifetime;
        lock (_workGate)
        {
            if (_trackedWorkCount <= 0)
            {
                throw new InvalidOperationException(
                    "The image resolver work-tracking count is already zero.");
            }

            _trackedWorkCount--;
            releaseSessionLifetime = TryReleaseSessionLifetimeNoLock();
        }

        if (releaseSessionLifetime)
            _lifetime.RetireWork();
    }

    private bool TryReleaseSessionLifetimeNoLock()
    {
        if (!_closed || _trackedWorkCount != 0 || _sessionLifetimeReleased)
            return false;

        _sessionLifetimeReleased = true;
        return true;
    }
}
