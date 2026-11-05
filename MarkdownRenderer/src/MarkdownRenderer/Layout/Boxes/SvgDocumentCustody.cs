using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The SVG document an image box has adopted, the native-use accounting of
/// every document it opened, and their disposal. A document is disposed on
/// the pool exactly once, after its last native use ends, under an
/// image-work unit that keeps the box's retirement barrier open.
/// </summary>
/// <remarks>
/// Use accounting runs under the image-work lock owned by
/// <see cref="ImageWorkLifetime"/>: a use counts as admitted image work, and
/// the disposal unit is reserved in the same critical section that releases
/// the last use, so terminal retirement never observes a false zero-work
/// gap. Provider code and disposal never run under that lock. Members ending
/// in NoLock expect the caller to hold it.
/// </remarks>
internal sealed class SvgDocumentCustody
{
    private readonly ImageWorkLifetime _lifetime;
    private IMarkdownSvgDocument? _current;
    private ConditionalWeakTable<IMarkdownSvgDocument, SvgDocumentUseState>? _uses;

    internal SvgDocumentCustody(ImageWorkLifetime lifetime) => _lifetime = lifetime;

    /// <summary>The adopted document, if any.</summary>
    internal IMarkdownSvgDocument? Current => Volatile.Read(ref _current);

    /// <summary>Adopts or detaches <paramref name="document"/> as the current document.</summary>
    internal void SetCurrentNoLock(IMarkdownSvgDocument? document) => Volatile.Write(ref _current, document);

    /// <summary>Forgets the use table of a retired box.</summary>
    internal void ClearUsesNoLock() => _uses = null;

    /// <summary>The use state of <paramref name="document"/>, created on first use.</summary>
    internal SvgDocumentUseState GetUseStateNoLock(IMarkdownSvgDocument document)
    {
        ConditionalWeakTable<IMarkdownSvgDocument, SvgDocumentUseState> uses =
            _uses ??= new ConditionalWeakTable<IMarkdownSvgDocument, SvgDocumentUseState>();
        if (!uses.TryGetValue(document, out SvgDocumentUseState? state))
        {
            state = new SvgDocumentUseState(document);
            uses.Add(document, state);
        }

        return state;
    }

    /// <summary>
    /// Retires <paramref name="document"/>. Returns the disposal work unit
    /// when no use remains; otherwise the last use schedules disposal.
    /// </summary>
    internal ImageWorkLease? RetireNoLock(IMarkdownSvgDocument document)
    {
        SvgDocumentUseState state = GetUseStateNoLock(document);
        if (state.DisposalScheduled)
            return null;

        if (state.ActiveUses != 0)
        {
            state.Retired = true;
            return null;
        }

        ImageWorkLease disposalWork = state.ReservedDisposalWork ??
            _lifetime.BeginWorkNoLock(CancellationToken.None);
        state.ReservedDisposalWork = null;
        state.Retired = true;
        state.DisposalScheduled = true;
        return disposalWork;
    }

    /// <summary>
    /// Retires <paramref name="document"/> with a disposal unit reserved
    /// before the caller changed any other state, so retirement cannot fail
    /// for lack of an allocation after that point.
    /// </summary>
    internal ImageWorkLease? RetireNoLock(
        IMarkdownSvgDocument document,
        SvgDocumentUseState preparedState,
        ref ImageWorkLease? preparedDisposalWork)
    {
        if (!ReferenceEquals(preparedState.Document, document))
            throw new InvalidOperationException("Prepared SVG document retirement does not match its document.");
        if (preparedState.DisposalScheduled)
            return null;

        if (preparedState.ActiveUses != 0)
        {
            preparedState.Retired = true;
            // Transfer the already-accounted cleanup lease to the state so the
            // final outstanding document use cannot need a new allocation.
            if (preparedState.ReservedDisposalWork is null && preparedDisposalWork is not null)
            {
                preparedState.ReservedDisposalWork = preparedDisposalWork;
                preparedDisposalWork = null;
            }
            return null;
        }

        ImageWorkLease? disposalWork = preparedState.ReservedDisposalWork;
        if (disposalWork is not null)
        {
            preparedState.ReservedDisposalWork = null;
        }
        else
        {
            disposalWork = preparedDisposalWork ??
                throw new InvalidOperationException("SVG document retirement has no prepared cleanup lease.");
            preparedDisposalWork = null;
        }

        preparedState.Retired = true;
        preparedState.DisposalScheduled = true;
        return disposalWork;
    }

    /// <summary>Disposes <paramref name="document"/> on the pool, then ends <paramref name="cleanupWork"/>.</summary>
    internal void ScheduleDisposal(IMarkdownSvgDocument document, ImageWorkLease cleanupWork)
    {
        void ReleaseDocument()
        {
            using (cleanupWork)
                Dispose(document);
        }

        try { _ = Task.Run(ReleaseDocument); }
        catch (Exception exception)
        {
            try { _lifetime.RecordFailure(exception); }
            finally { ReleaseDocument(); }
        }
    }

    private void Dispose(IMarkdownSvgDocument document)
    {
        try
        {
            document.Dispose();
        }
        catch (Exception exception)
        {
            // Resource release is best-effort for the renderer pipeline, but
            // teardown must not report success when the provider failed to
            // release an adopted native document. The current image-work lease
            // keeps this failure inside the snapshot retirement barrier.
            _lifetime.RecordFailure(exception);
        }
    }

    /// <summary>
    /// Adopts <paramref name="document"/> as the current document and retires
    /// the one it replaces; false when the box is retiring or the document
    /// was already retired, in which case it is retired here.
    /// </summary>
    internal bool TryAdopt(IMarkdownSvgDocument document)
    {
        IMarkdownSvgDocument? previous = null;
        ImageWorkLease? rejectedDocumentCleanup = null;
        ImageWorkLease? previousDocumentCleanup = null;
        bool adopted = false;
        lock (_lifetime.Sync)
        {
            if (_lifetime.IsDisposed || _lifetime.IsFinalizing)
            {
                // The terminal drain owns the currently published document.
                // A distinct late-opened handle was never adopted and needs
                // its own asynchronous retirement.
                if (!ReferenceEquals(_current, document))
                    rejectedDocumentCleanup = RetireNoLock(document);
            }
            else
            {
                SvgDocumentUseState state = GetUseStateNoLock(document);
                if (!state.Retired && !state.DisposalScheduled)
                {
                    previous = _current;
                    if (previous is not null && !ReferenceEquals(previous, document))
                        previousDocumentCleanup = RetireNoLock(previous);
                    _current = document;
                    adopted = true;
                }
                else
                {
                    rejectedDocumentCleanup = RetireNoLock(document);
                }
            }
        }

        if (rejectedDocumentCleanup is not null)
            ScheduleDisposal(document, rejectedDocumentCleanup);
        if (!adopted)
            return false;

        if (previous is not null && previousDocumentCleanup is not null)
            ScheduleDisposal(previous, previousDocumentCleanup);
        return true;
    }

    /// <summary>A native use of the current document, or null when it is not current or the box is retiring.</summary>
    internal SvgDocumentUseLease? TryAcquireUse(IMarkdownSvgDocument document)
    {
        lock (_lifetime.Sync)
        {
            if (_lifetime.IsDisposed || _lifetime.IsFinalizing || _lifetime.IsSuspended ||
                !ReferenceEquals(_current, document))
            {
                return null;
            }

            SvgDocumentUseState state = GetUseStateNoLock(document);
            if (state.Retired || state.DisposalScheduled)
                return null;

            var lease = new SvgDocumentUseLease(this, state);
            _lifetime.AddWorkNoLock();
            state.ActiveUses++;
            return lease;
        }
    }

    /// <summary>
    /// The candidate use of a newly opened document. Each document has at
    /// most one candidate; <paramref name="ownershipConflict"/> reports a
    /// document already reserved or retired.
    /// </summary>
    internal SvgDocumentUseLease? TryAcquireCandidateUse(
        IMarkdownSvgDocument document,
        out bool ownershipConflict)
    {
        lock (_lifetime.Sync)
        {
            SvgDocumentUseState state = GetUseStateNoLock(document);
            ownershipConflict = state.CandidateReserved || state.Retired || state.DisposalScheduled;
            if (ownershipConflict || _lifetime.IsDisposed || _lifetime.IsSuspended || _lifetime.IsFinalizing)
                return null;

            var lease = new SvgDocumentUseLease(this, state, candidate: true);
            _lifetime.AddWorkNoLock();
            state.ActiveUses++;
            state.CandidateReserved = true;
            return lease;
        }
    }

    /// <summary>Retires <paramref name="document"/> unless it was adopted.</summary>
    internal void RetireUncommitted(IMarkdownSvgDocument document)
    {
        ImageWorkLease? cleanup = null;
        lock (_lifetime.Sync)
        {
            if (!ReferenceEquals(_current, document))
                cleanup = RetireNoLock(document);
        }
        if (cleanup is not null)
            ScheduleDisposal(document, cleanup);
    }

    /// <summary>Detaches and retires <paramref name="document"/> while it is current.</summary>
    internal void Release(IMarkdownSvgDocument document)
    {
        ImageWorkLease? cleanupWork = null;
        lock (_lifetime.Sync)
        {
            if (ReferenceEquals(_current, document))
            {
                cleanupWork = RetireNoLock(document);
                _current = null;
            }
        }

        if (cleanupWork is not null)
            ScheduleDisposal(document, cleanupWork);
    }

    /// <summary>
    /// A native use nested in <paramref name="parent"/>, an already-admitted
    /// use of this custody's document, or null once that use drained or its
    /// document's disposal was scheduled.
    /// </summary>
    internal SvgDocumentUseLease? TryAcquireNestedUse(SvgDocumentUseLease parent)
    {
        if (!parent.IsOwnedBy(this))
            return null;

        lock (_lifetime.Sync)
        {
            SvgDocumentUseState state = parent.State;
            if (!parent.IsOwnedBy(this) || state.ActiveUses <= 0 || state.DisposalScheduled)
                return null;

            // Parent leases represent already-admitted use, such as an SVG
            // tile queued before provider admission. That in-flight work may
            // continue after the document is retired, but cannot outlive it.
            var lease = new SvgDocumentUseLease(this, state);
            _lifetime.AddWorkNoLock();
            state.ActiveUses++;
            return lease;
        }
    }

    /// <summary>
    /// Ends one use. The last use of a retired document schedules its
    /// disposal before the work it counted is released.
    /// </summary>
    internal void CompleteUse(SvgDocumentUseState state, bool candidate)
    {
        ImageWorkLease? disposalWork = null;
        TaskCompletionSource? workDrained;
        lock (_lifetime.Sync)
        {
            if (state.ActiveUses <= 0 || _lifetime.WorkCountNoLock <= 0)
                return;

            if (candidate)
                state.CandidateReserved = false;

            if (state.Retired && state.ActiveUses == 1 && !state.DisposalScheduled)
            {
                // Reserve disposal before releasing this use so terminal
                // retirement never observes a false zero-work gap.
                disposalWork = state.ReservedDisposalWork;
                state.ReservedDisposalWork = null;
                disposalWork ??= _lifetime.BeginWorkNoLock(CancellationToken.None);
                state.DisposalScheduled = true;
            }

            state.ActiveUses--;
            workDrained = _lifetime.ReleaseWorkNoLock();
        }

        if (disposalWork is not null)
            ScheduleDisposal(state.Document, disposalWork);

        _lifetime.SignalWorkDrained(workDrained);
    }

    /// <summary>
    /// Renders <paramref name="document"/> under a native use nested in
    /// <paramref name="parentUse"/>. The use stays with an abandoned provider
    /// task until that task finishes and its result is disposed.
    /// </summary>
    internal async Task<MarkdownSvgRaster> RenderAsync(
        IMarkdownSvgDocument document,
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken,
        SvgDocumentUseLease parentUse,
        MarkdownSvgRenderDemand? demand = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SvgDocumentUseLease nativeUse = TryAcquireNestedUse(parentUse) ??
            throw new OperationCanceledException(cancellationToken);
        ValueTask<MarkdownSvgRaster> operation;
        try
        {
            operation = demand is not null && document is IMarkdownSvgPrioritizedDocument hostDocument
                ? hostDocument.RenderAsync(request, demand, cancellationToken)
                : document.RenderAsync(request, cancellationToken);
        }
        catch
        {
            nativeUse.Dispose();
            throw;
        }

        return await AwaitTrackedResultAsync(
                operation,
                cancellationToken,
                nativeUse)
            .ConfigureAwait(false);
    }

    private async Task<T> AwaitTrackedResultAsync<T>(
        ValueTask<T> operation,
        CancellationToken cancellationToken,
        SvgDocumentUseLease? documentUse = null)
        where T : IDisposable
    {
        Task<T>? task = null;
        bool useTransferred = false;
        try
        {
            task = operation.AsTask();
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (task is not null)
            {
                // This task includes disposal of a result which races cancellation.
                // Keep the document's native-use lease with that task too: the
                // provider may ignore cancellation and still be reading its tree.
                Task retirement = SvgResultRetirement.RetireUnclaimed(task, documentUse);
                useTransferred = documentUse is not null;
                _lifetime.TrackDetachedWork(retirement, failRetirementOnFault: true);
            }
            throw;
        }
        finally
        {
            if (!useTransferred)
                documentUse?.Dispose();
        }
    }
}
