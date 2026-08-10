using System;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

internal readonly record struct StarSyncRequestBatch(long Version, bool ForceFull);

internal sealed class StarSyncRequestCoordinator : IDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private long _requestedVersion;
    private long _completedVersion;
    private bool _forceFullPending;
    private bool _disposed;

    public long Request(bool forceFull)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requestedVersion++;
            _forceFullPending |= forceFull;
            return _requestedVersion;
        }
    }

    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await _executionGate.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    public void Exit() => _executionGate.Release();

    public bool TryTake(long requestedVersion, out StarSyncRequestBatch batch)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completedVersion >= requestedVersion)
            {
                batch = default;
                return false;
            }

            // A waiter owns every request queued before it entered the execution gate.
            // Advancing to the latest version coalesces bursts into one follow-up sync.
            batch = new StarSyncRequestBatch(_requestedVersion, _forceFullPending);
            _forceFullPending = false;
            return true;
        }
    }

    public void Complete(StarSyncRequestBatch batch)
    {
        lock (_stateGate)
        {
            _completedVersion = Math.Max(_completedVersion, batch.Version);
        }
    }

    public void Abandon(StarSyncRequestBatch batch)
    {
        lock (_stateGate)
        {
            _forceFullPending |= batch.ForceFull;
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _forceFullPending = false;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();

        // SemaphoreSlim.Dispose is not safe while another caller owns or waits on
        // the semaphore. Cancellation releases waiters and the current owner can
        // still call Exit without racing a disposed synchronization primitive.
    }
}
