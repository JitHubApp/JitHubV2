using System;
using Microsoft.UI.Dispatching;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Diagnostics.Internal;

namespace MarkdownRenderer.Controls.Internal.Embeds;

/// <summary>
/// Queues at most one low-priority dispatcher callback that resumes visible
/// realization after <see cref="EmbedRealization.RealizeVisible"/> ran out of
/// passes with a nested request still pending. A ticket retires any callback
/// queued before <see cref="Cancel"/>.
/// </summary>
internal sealed class VisibleRealizationContinuation
{
    private readonly EmbedRealization _realization;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly DispatcherQueue _dispatcher;

    private bool _realizeVisibleEmbedsContinuationQueued;
    private long _realizeVisibleEmbedsContinuationTicket;

    internal VisibleRealizationContinuation(
        EmbedRealization realization,
        MarkdownViewLifetime lifetime,
        DispatcherQueue dispatcher)
    {
        _realization = realization;
        _lifetime = lifetime;
        _dispatcher = dispatcher;
    }

    /// <summary>Retires the queued callback, if any, so it does nothing when it runs.</summary>
    internal void Cancel()
    {
        _realizeVisibleEmbedsContinuationQueued = false;
        _realizeVisibleEmbedsContinuationTicket++;
    }

    /// <summary>Queues the continuation unless one is queued or the control is shutting down.</summary>
    internal void Queue()
    {
        if (_realizeVisibleEmbedsContinuationQueued ||
            _lifetime.IsDisposed || _lifetime.IsUnloaded || _realization.IsDerealizingAll)
            return;

        var dispatcher = _dispatcher;
        if (dispatcher is null)
            return;

        long ticket = ++_realizeVisibleEmbedsContinuationTicket;
        if (ticket == 0)
            ticket = ++_realizeVisibleEmbedsContinuationTicket;
        var receiver = new WeakReference<VisibleRealizationContinuation>(this);
        try
        {
            if (dispatcher.TryEnqueue(
                    DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (receiver.TryGetTarget(out var continuation))
                            continuation.ProcessQueued(ticket);
                    }))
            {
                _realizeVisibleEmbedsContinuationQueued = true;
            }
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] visible realization continuation dispatch failed: {exception.GetType().Name} " +
                $"(0x{exception.HResult:X8}).");
        }
    }

    private void ProcessQueued(long ticket)
    {
        if (ticket != _realizeVisibleEmbedsContinuationTicket)
            return;

        _realizeVisibleEmbedsContinuationQueued = false;
        _realization.ContinueQueuedRealization();
    }
}
