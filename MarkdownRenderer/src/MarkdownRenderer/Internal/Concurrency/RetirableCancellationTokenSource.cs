using System;
using System.Threading;
using System.Threading.Tasks;
using static MarkdownRenderer.Internal.Concurrency.TaskFaultObservation;

namespace MarkdownRenderer.Internal.Concurrency;

/// <summary>
/// A cancellation source that remembers its first asynchronous cancellation
/// request, so every later retirement waits for the same token callbacks.
/// </summary>
/// <remarks>
/// The request state lives on the source itself rather than in a
/// process-wide table keyed by source: whoever creates the source owns it,
/// and the state is collected with it. Linked sources forward cancellation
/// from their parent tokens exactly as
/// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/>
/// does and drop those parent registrations when they are disposed.
/// </remarks>
internal sealed class RetirableCancellationTokenSource : CancellationTokenSource
{
    private readonly object _gate = new();
    private CancellationTokenRegistration _firstLink;
    private CancellationTokenRegistration _secondLink;
    private Task? _callbacks;

    /// <summary>Creates an unlinked source.</summary>
    internal RetirableCancellationTokenSource()
    {
    }

    /// <summary>
    /// Creates a source that is canceled when <paramref name="token"/> is
    /// canceled.
    /// </summary>
    /// <param name="token">The parent token.</param>
    internal static RetirableCancellationTokenSource CreateLinked(CancellationToken token)
    {
        var source = new RetirableCancellationTokenSource();
        source._firstLink = Link(token, source);
        return source;
    }

    /// <summary>
    /// Creates a source that is canceled when either parent token is
    /// canceled.
    /// </summary>
    /// <param name="first">The first parent token.</param>
    /// <param name="second">The second parent token.</param>
    internal static RetirableCancellationTokenSource CreateLinked(
        CancellationToken first,
        CancellationToken second)
    {
        var source = new RetirableCancellationTokenSource();
        source._firstLink = Link(first, source);
        source._secondLink = Link(second, source);
        return source;
    }

    /// <summary>
    /// Requests cancellation without running token callbacks on the caller.
    /// Every request after the first returns the first request's callback
    /// task, so a retirement never mistakes an already-canceling source for
    /// one whose callbacks have completed.
    /// </summary>
    internal Task RequestCancellation()
    {
        lock (_gate)
        {
            if (_callbacks is not null)
                return _callbacks;

            try
            {
                // CancelAsync transitions IsCancellationRequested before
                // returning, but schedules registrations away from the
                // initiating thread.
                _callbacks = CancelAsync();
                ObserveFault(_callbacks);
            }
            catch (ObjectDisposedException)
            {
                // The owning operation completed while a concurrent
                // cancellation request was being coordinated.
                _callbacks = Task.CompletedTask;
            }

            return _callbacks;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Unregister before disposal so a parent cancellation can never
            // reach this source after it has been disposed.
            _firstLink.Dispose();
            _secondLink.Dispose();
        }

        base.Dispose(disposing);
    }

    private static CancellationTokenRegistration Link(
        CancellationToken parent,
        RetirableCancellationTokenSource source)
        => parent.UnsafeRegister(
            static state => ((RetirableCancellationTokenSource)state!).CancelFromParent(),
            source);

    private void CancelFromParent()
    {
        try
        {
            Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal raced the parent's cancellation.
        }
    }
}
