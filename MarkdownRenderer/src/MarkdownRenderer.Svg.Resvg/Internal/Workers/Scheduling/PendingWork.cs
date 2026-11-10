using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

/// <summary>
/// One queued or active request for a worker slot. Scheduling members are
/// read and written under the pool's state lock.
/// </summary>
internal sealed class PendingWork(
    WorkerOperation operation,
    MarkdownSvgRenderPriority priority,
    CancellationToken cancellationToken,
    bool requiresFontCatalog = false,
    long preferredWorkerInstanceId = 0,
    bool isSecondaryFontProbe = false,
    MarkdownSvgRenderDemand? demandTicket = null)
{
    public WorkerOperation Operation { get; } = operation;
    private MarkdownSvgRenderPriority _priority = priority;
    private IDisposable? _promotionRegistration;
    public MarkdownSvgRenderPriority Priority => _priority;
    public CancellationToken CancellationToken { get; set; } = cancellationToken;
    public bool RequiresFontCatalog { get; } = requiresFontCatalog;
    public long PreferredWorkerInstanceId { get; } = preferredWorkerInstanceId;
    public bool IsSecondaryFontProbe { get; } = isSecondaryFontProbe;
    public MarkdownSvgRenderDemand? DemandTicket { get; } = demandTicket;
    public TaskCompletionSource<WorkerLease> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenRegistration CancellationRegistration { get; set; }
    public WorkerSlot? ActiveSlot { get; set; }
    public bool HasDetachedFontWaitReservation { get; set; }
    public bool IsCanceled { get; set; }
    public DateTimeOffset? BlockedSince { get; set; }
    public bool CancellationCheckScheduled { get; set; }

    public void SetPromotionRegistration(IDisposable registration)
    {
        if (Interlocked.CompareExchange(ref _promotionRegistration, registration, null) is not null)
            throw new InvalidOperationException("A queued worker item already has a promotion registration.");
    }

    public void DisposePromotionRegistration() =>
        Interlocked.Exchange(ref _promotionRegistration, null)?.Dispose();

    /// <summary>
    /// Registers <paramref name="cancellationToken"/> to cancel this work
    /// through <paramref name="scheduler"/>, replacing no earlier registration.
    /// </summary>
    /// <param name="scheduler">The scheduler that owns this work.</param>
    /// <param name="cancellationToken">The token that cancels this work.</param>
    public void RegisterCancellation(WorkerScheduler scheduler, CancellationToken cancellationToken) =>
        CancellationRegistration = cancellationToken.UnsafeRegister(
            static state =>
            {
                var cancellation = (PendingWorkCancellation)state!;
                cancellation.Pool.Cancel(cancellation.Pending);
            },
            new PendingWorkCancellation(scheduler, this));

    /// <summary>
    /// Moves an admitted lease's cancellation from its admission token to the
    /// operation's own token.
    /// </summary>
    /// <param name="scheduler">The scheduler that owns this work.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    /// <param name="admissionCancellationToken">The admission token being replaced.</param>
    public void RebindCancellation(
        WorkerScheduler scheduler,
        CancellationToken cancellationToken,
        CancellationToken admissionCancellationToken)
    {
        CancellationTokenRegistration previous = CancellationRegistration;
        CancellationRegistration = default;
        previous.Dispose();
        // Disposing the old registration drains a racing deadline callback.
        // Never transfer an already-expired admission as uncanceled content
        // work, leaving its stale IsCanceled bit eligible for worker preemption.
        admissionCancellationToken.ThrowIfCancellationRequested();
        CancellationToken = cancellationToken;
        if (cancellationToken.CanBeCanceled)
            RegisterCancellation(scheduler, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void SynchronizePriorityFromTicket() => PromoteVisibleFromTicket();

    public bool PromoteVisibleFromTicket()
    {
        if (_priority == MarkdownSvgRenderPriority.Visible ||
            DemandTicket?.Priority != MarkdownSvgRenderPriority.Visible)
        {
            return false;
        }
        _priority = MarkdownSvgRenderPriority.Visible;
        return true;
    }
}
