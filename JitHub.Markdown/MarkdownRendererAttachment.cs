using System;

namespace JitHub.Markdown;

/// <summary>
/// Decides when a Markdown host creates, queues or reattaches the renderer in
/// its <see cref="MarkdownRendererSlot{TRenderer}"/>. Never reattaches a renderer
/// whose teardown failed or that is still draining, and never creates one after
/// shutdown was requested or while the host is unloaded.
/// </summary>
/// <typeparam name="TRenderer">The renderer control type.</typeparam>
public sealed class MarkdownRendererAttachment<TRenderer>
    where TRenderer : class, IAsyncDisposable
{
    private readonly MarkdownRendererSlot<TRenderer> _slot;
    private readonly IMarkdownRendererAttachmentHost<TRenderer> _host;
    private bool _creationQueued;

    /// <param name="slot">The slot that owns the host's renderer.</param>
    /// <param name="host">The host that shows the renderer.</param>
    public MarkdownRendererAttachment(
        MarkdownRendererSlot<TRenderer> slot,
        IMarkdownRendererAttachmentHost<TRenderer> host)
    {
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Forgets a queued creation; its callback still runs but finds nothing to do.</summary>
    public void CancelQueuedCreation() => _creationQueued = false;

    /// <summary>
    /// Queues one low-priority renderer creation, or creates the renderer now
    /// when the host cannot queue work.
    /// </summary>
    public void QueueCreation()
    {
        if (_host.IsShutdownRequested ||
            _slot.Current is not null ||
            _slot.IsRetirementPending ||
            _creationQueued ||
            !_host.IsLoaded)
        {
            return;
        }

        _creationQueued = true;
        if (!_host.TryEnqueueLowPriority(() =>
            {
                _creationQueued = false;
                if (_host.IsShutdownRequested || !_host.IsLoaded)
                {
                    return;
                }

                EnsureRenderer();
            }))
        {
            _creationQueued = false;
            EnsureRenderer();
        }
    }

    /// <summary>Reattaches the current renderer, or creates and attaches a new one.</summary>
    public void EnsureRenderer()
    {
        if (_host.IsShutdownRequested)
            return;

        // A failed teardown can latch the underlying control's disposed state
        // before it has released every resource. Never reattach that instance.
        if (_slot.IsDisposalFaulted)
        {
            _host.ShowRendererDisposalFailure();
            return;
        }

        // The old control is already disposed, but its published snapshot may
        // still be releasing device resources on the pool. Do not reattach it
        // if this viewer is loaded again before that retirement completes.
        if (_slot.IsRetirementPending)
            return;

        if (_slot.Current is { } current)
        {
            if (!_host.IsRendererInTree(current))
            {
                _host.AddRendererToTree(current);
                _host.ApplyRendererSettings();
            }

            return;
        }

        TRenderer renderer = _host.CreateRenderer();
        _slot.Attach(renderer);
        _host.ConfigureNewRenderer(renderer);
        _host.AddRendererToTree(renderer);
        _host.ApplyRendererSettings();
    }

    /// <summary>Queues a replacement once a retired renderer has fully released, if the host still wants one.</summary>
    public void OnRendererReleased()
    {
        if (_host.IsLoaded && !_host.IsShutdownRequested && !_slot.IsDisposalFaulted)
            QueueCreation();
    }
}
