using System;
using System.Threading;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;

namespace MarkdownRenderer.Layout.Internal.Images.Loading;

/// <summary>
/// One queued image publication and its delivery state: pending, callback
/// running, delivered, or dropped. Exactly one of the accepted callback and
/// the rejection path releases its work unit and, when dropped, runs its
/// cleanup.
/// </summary>
internal sealed class ImageUiPublication
{
    private const int Pending = 0;
    private const int Running = 1;
    private const int Delivered = 2;
    private const int Dropped = 3;

    private readonly ImageWorkLifetime _lifetime;
    private readonly MarkdownLayoutContext _context;
    private readonly Action _publish;
    private readonly Action? _onDropped;
    private readonly ImageWorkLease? _work;
    private int _deliveryState;

    internal ImageUiPublication(
        ImageWorkLifetime lifetime,
        MarkdownLayoutContext context,
        Action publish,
        Action? onDropped,
        ImageWorkLease? publicationWork)
    {
        _lifetime = lifetime;
        _context = context;
        _publish = publish;
        _onDropped = onDropped;
        _work = publicationWork;
    }

    /// <summary>
    /// The dispatcher callback: publishes unless the box retired or the
    /// session ended, and always releases the work unit.
    /// </summary>
    internal void Run()
    {
        if (Interlocked.CompareExchange(ref _deliveryState, Running, Pending) != Pending)
            return;

        try
        {
            if (_lifetime.IsDisposed || _lifetime.IsSuspended || _work!.Token.IsCancellationRequested ||
                _context.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true })
            {
                Drop(expectedState: Running);
                return;
            }

            _publish();
            Interlocked.CompareExchange(ref _deliveryState, Delivered, Running);
        }
        catch (Exception ex) when (_lifetime.IsDisposed || GraphicsDeviceErrors.IsShutdownOrDisposed(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] ignored image publish during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            Drop(expectedState: Running);
        }
        catch
        {
            Interlocked.CompareExchange(ref _deliveryState, Delivered, Running);
            throw;
        }
        finally
        {
            _work!.Dispose();
        }
    }

    /// <summary>The dispatcher refused or failed to queue the callback.</summary>
    internal void Reject()
    {
        try { Drop(expectedState: Pending); }
        finally { _work!.Dispose(); }
    }

    /// <summary>Runs the dropped cleanup; its failure is recorded and rethrown.</summary>
    internal void InvokeDroppedCleanup()
    {
        try
        {
            _onDropped?.Invoke();
        }
        catch (Exception exception)
        {
            _lifetime.RecordFailure(exception);
            throw;
        }
    }

    private void Drop(int expectedState)
    {
        if (Interlocked.CompareExchange(ref _deliveryState, Dropped, expectedState) != expectedState)
            return;

        InvokeDroppedCleanup();
    }
}
