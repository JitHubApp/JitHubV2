using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// Leases engine-owned extension callbacks so delegates obtained through the
/// public extension registry cannot outlive their captured resources.
/// </summary>
internal sealed class MarkdownExtensionCallbackLifetime
{
    private readonly TaskCompletionSource _retired = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeCallbacks;
    private int _disposeRequested;

    internal Lease Enter()
    {
        if (Volatile.Read(ref _disposeRequested) != 0)
            throw new ObjectDisposedException(nameof(MarkdownEngine));

        Interlocked.Increment(ref _activeCallbacks);
        if (Volatile.Read(ref _disposeRequested) == 0)
            return new Lease(this);

        Exit();
        throw new ObjectDisposedException(nameof(MarkdownEngine));
    }

    internal Task BeginDispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        if (Volatile.Read(ref _activeCallbacks) == 0)
            _retired.TrySetResult();
        return _retired.Task;
    }

    private void Exit()
    {
        int remaining = Interlocked.Decrement(ref _activeCallbacks);
        if (remaining == 0 && Volatile.Read(ref _disposeRequested) != 0)
            _retired.TrySetResult();
    }

    internal readonly struct Lease : IDisposable
    {
        private readonly MarkdownExtensionCallbackLifetime _owner;

        internal Lease(MarkdownExtensionCallbackLifetime owner) => _owner = owner;

        public void Dispose() => _owner.Exit();
    }
}
