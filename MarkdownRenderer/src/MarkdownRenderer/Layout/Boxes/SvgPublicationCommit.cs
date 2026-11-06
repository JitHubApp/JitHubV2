using System;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Commits a rendered SVG to an image box: its accepted metadata, document,
/// tile plan and tile-cache transition, or its whole bitmap. Owns the
/// publication lock that serializes this two-phase local-plan and
/// tile-cache publication, and tile-failure invalidation, without holding
/// the image-work lock while the coordinator or the tile cache changes.
/// </summary>
/// <remarks>
/// Lock order: the publication lock is taken before the image-work lock.
/// Provider code and disposal or event callbacks never execute under the
/// image-work lock; every local render-generation advance shares that lock.
/// </remarks>
internal sealed class SvgPublicationCommit
{
    private readonly ImageBox _box;
    private readonly ImageWorkLifetime _lifetime;
    private readonly ImageLoadState _load;
    private readonly SvgImageState _svg;
    private readonly SvgDocumentCustody _documents;

    internal SvgPublicationCommit(
        ImageBox box,
        ImageWorkLifetime lifetime,
        ImageLoadState load,
        SvgImageState svg,
        SvgDocumentCustody documents)
    {
        _box = box;
        _lifetime = lifetime;
        _load = load;
        _svg = svg;
        _documents = documents;
    }

    /// <summary>The publication lock, taken before the image-work lock.</summary>
    internal object Sync { get; } = new();

    /// <summary>
    /// True when a publication must relayout. Known bytes are not a promise
    /// of unchanged geometry: a supported color-scheme rule or provider
    /// environment can alter the intrinsic size.
    /// </summary>
    internal static bool ShouldInvalidateSvgGeometry(
        bool metadataWasKnown,
        Size previousIntrinsic,
        Size candidateIntrinsic) =>
        !metadataWasKnown || !previousIntrinsic.Equals(candidateIntrinsic);

    /// <summary>
    /// The UI publication of a tiled SVG and its drop cleanup. The opened
    /// document's candidate use and owner slot stay with the candidate until
    /// its exact plan is accepted.
    /// </summary>
    // Transient callback pairs use a built-in value tuple: no separate carrier
    // object is allocated or retained alongside the actual publication owner.
    internal (Action Publish, Action? OnDropped) CreateTiledPublication(
        SvgPublicationMetadata metadata,
        IMarkdownSvgDocument openedDocument,
        SvgDocumentUseLease candidateUse,
        long publishRequestedAt = 0)
    {
        var plan = new SvgTileRenderPlan(
            openedDocument,
            metadata.BitmapIdentity,
            metadata.TargetWidthPixels,
            metadata.TargetHeightPixels,
            metadata.RenderGeneration,
            metadata.Demand,
            metadata.HostAdmission,
            metadata.SourceReservationBytes,
            metadata.OwnerSlot,
            metadata.PhysicalViewport);
        int candidateReleased = 0;
        void ReleaseCandidate()
        {
            if (Interlocked.Exchange(ref candidateReleased, 1) != 0)
                return;
            // Registry admission is staged before local publication. If the
            // callback fails or loses its generation fence, this candidate
            // remains unaccepted and owns the slot. Conversely, once the exact
            // plan is visible, later cleanup (including an exception from
            // cache rebinding) must not release the accepted plan's slot.
            bool acceptedPlan = ReferenceEquals(_svg.AcceptedPlan, plan);
            try
            {
                if (!acceptedPlan)
                    _documents.RetireUncommitted(openedDocument);
            }
            finally
            {
                try { candidateUse.Dispose(); }
                finally
                {
                    if (!acceptedPlan)
                        metadata.OwnerSlot?.Dispose();
                }
            }
        }

        void Publish()
        {
            using var publishTrace = MeasurePublish(metadata, publishRequestedAt);
            try
            {
                bool layoutInvalidated = ShouldInvalidateSvgGeometry(
                    metadata.MetadataWasKnown, _svg.IntrinsicSize, metadata.Intrinsic);
                if (!TryCommit(metadata, plan))
                    return;

                _box.RaiseLoadCompleted(layoutInvalidated);
            }
            finally { ReleaseCandidate(); }
        }

        return (Publish, ReleaseCandidate);
    }

    /// <summary>
    /// The UI publication of a whole SVG bitmap and its drop cleanup. The
    /// lease transfers to the box only after the commit succeeds.
    /// </summary>
    internal (Action Publish, Action? OnDropped) CreateBitmapPublication(
        SvgPublicationMetadata metadata,
        SharedCanvasBitmapCache.Lease publishedLease,
        long publishRequestedAt = 0)
    {
        SvgTilePublicationOwnership ownership = default;
        void ReleaseUntransferredLease()
        {
            if (ownership.TryReleaseUntransferred())
                _lifetime.TryCleanup(publishedLease.Dispose);
        }

        void Publish()
        {
            using var publishTrace = MeasurePublish(metadata, publishRequestedAt);
            bool transferred = false;
            try
            {
                bool layoutInvalidated = ShouldInvalidateSvgGeometry(
                    metadata.MetadataWasKnown, _svg.IntrinsicSize, metadata.Intrinsic);
                if (!TryCommit(metadata, plan: null))
                    return;

                if (!string.IsNullOrEmpty(metadata.ResolvedCacheKey))
                {
                    _box.Resources.Failures.ForgetSvgFailure(metadata.SvgFailureCacheKey);
                    _box.Resources.Failures.ForgetFailedUrl(metadata.ResolvedCacheKey);
                }
                transferred = ownership.TryTransfer();
                if (!transferred)
                    return;
                _box.Presentation.Replace(publishedLease.Bitmap, publishedLease, metadata.PhysicalViewport);
                _box.RaiseLoadCompleted(layoutInvalidated);
            }
            finally
            {
                if (!transferred)
                    ReleaseUntransferredLease();
            }
        }

        return (Publish, ReleaseUntransferredLease);
    }

    // Records the UI dispatch delay, then measures the publication itself.
    private static IDisposable MeasurePublish(SvgPublicationMetadata metadata, long publishRequestedAt)
    {
        using (MarkdownSvgStageTrace.MeasureFrom(
            metadata.WorkId,
            MarkdownSvgWorkStage.UiDispatchDelay,
            publishRequestedAt != 0 ? publishRequestedAt : metadata.PublishRequestedAt,
            metadata.SourceByteLength,
            metadata.TargetPixels))
        {
        }
        return MarkdownSvgStageTrace.Measure(
            metadata.WorkId,
            MarkdownSvgWorkStage.UiPublish,
            metadata.SourceByteLength,
            metadata.TargetPixels);
    }

    /// <summary>Detaches and retires every tile and in-flight tile render.</summary>
    internal void ReleaseSvgTiles()
    {
        (SharedCanvasBitmapCache.Lease[] Tiles, RetirableCancellationTokenSource[] Work) detached;
        lock (Sync)
            detached = _box.SvgTileCache.DetachAll();
        _box.SvgTileCache.RetireDetached(detached.Tiles, detached.Work);
    }

    /// <summary>
    /// Accepts <paramref name="metadata"/> and <paramref name="plan"/> (null
    /// for a whole bitmap) while the provider, render generation and accepted
    /// plan are unchanged, then applies the tile-cache transition. The
    /// replaced document and owner slot retire after every lock is released.
    /// </summary>
    internal bool TryCommit(SvgPublicationMetadata metadata, SvgTileRenderPlan? plan)
    {
        if (!_box.SvgCachedPublisher.IsProviderCurrent(
                metadata.Renderer,
                metadata.ProviderCacheGeneration,
                metadata.Info.HasText) ||
            !TryObserveAdmissionDomain(plan, out object? planAdmissionDomain))
        {
            return false;
        }

        var commit = new SvgPublicationCommitState();
        SvgTileCacheTransition? tileTransition;
        lock (Sync)
        {
            tileTransition = _box.SvgTileCache.PrepareSvgTileCacheTransition(plan);
            if (tileTransition is null)
                return false;

            try
            {
                // Registry allocation/locking happens outside the image-work
                // lock. Until the exact candidate plan is accepted below, its
                // weak registry row cannot be selected for parking because the
                // owner rechecks accepted plan identity.
                if (plan?.OwnerSlot is { } stagedOwnerSlot)
                    stagedOwnerSlot.Commit(_box, plan, plan.Demand, plan.SourceReservationBytes);

                lock (_lifetime.Sync)
                {
                    if (!TryApplyLocalCommitNoLock(metadata, plan, planAdmissionDomain, tileTransition, commit))
                        return false;
                }

                commit.TileTransitionApplied = _box.SvgTileCache.ApplySvgTileCacheTransition(tileTransition);
                if (!commit.TileTransitionApplied && plan is not null)
                {
                    lock (_lifetime.Sync)
                        AbandonUnappliedPlanNoLock(plan, commit);
                }
            }
            finally
            {
                if (!commit.TileTransitionApplied)
                    _box.SvgTileCache.AbortSvgTileCacheTransition(tileTransition);
            }
        }

        commit.RetireReplacedAndAbandoned(plan, _documents);
        if (!commit.TileTransitionApplied)
            return false;

        _box.SvgTileCache.RetireSvgTileCacheTransition(tileTransition!);
        return true;
    }

    // Admission providers are external code. Read their domain identity
    // before taking the image-work lock.
    private static bool TryObserveAdmissionDomain(SvgTileRenderPlan? plan, out object? planAdmissionDomain)
    {
        planAdmissionDomain = null;
        if (plan?.HostAdmission is not { } hostAdmission)
            return true;

        try
        {
            planAdmissionDomain = hostAdmission.AdmissionDomain;
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG admission domain could not be observed: {exception.GetType().Name}");
            return false;
        }

        return plan.AdmissionDomain is not null &&
               ReferenceEquals(plan.AdmissionDomain, planAdmissionDomain);
    }

    private bool TryApplyLocalCommitNoLock(
        SvgPublicationMetadata metadata,
        SvgTileRenderPlan? plan,
        object? planAdmissionDomain,
        SvgTileCacheTransition tileTransition,
        SvgPublicationCommitState commit)
    {
        if (_lifetime.IsDisposed || _lifetime.IsSuspended || _lifetime.IsFinalizing ||
            metadata.RenderGeneration != _svg.RenderGeneration ||
            !ReferenceEquals(_svg.AcceptedPlan, tileTransition.PreviousPlan) ||
            _svg.ParkingPlan is not null)
        {
            return false;
        }
        if (plan is not null && !IsCandidatePlanAcceptableNoLock(metadata, plan, planAdmissionDomain))
            return false;

        commit.Previous = _documents.Current;
        commit.PreviousPlan = _svg.AcceptedPlan;
        IMarkdownSvgDocument? next = plan?.Document;
        if (commit.Previous is not null && !ReferenceEquals(commit.Previous, next))
            commit.PreviousCleanup = _documents.RetireNoLock(commit.Previous);

        _load.IsSvg = true;
        _svg.ApplyPublicationNoLock(metadata);
        _load.IsFailed = false;
        _documents.SetCurrentNoLock(next);
        _svg.SetAcceptedPlanNoLock(plan);
        _svg.SetParkingPlanNoLock(null);
        _svg.SetUsesTilesNoLock(plan is not null);
        _box.SvgTileCache.ResetRenderedTileState();
        _svg.SetPressureParkedNoLock(false);
        commit.LocalCommitApplied = true;
        return true;
    }

    private bool IsCandidatePlanAcceptableNoLock(
        SvgPublicationMetadata metadata,
        SvgTileRenderPlan plan,
        object? planAdmissionDomain)
    {
        SvgDocumentUseState candidate = _documents.GetUseStateNoLock(plan.Document);
        if (candidate.Retired || candidate.DisposalScheduled ||
            !candidate.CandidateReserved || candidate.ActiveUses <= 0 ||
            plan.RenderGeneration != metadata.RenderGeneration ||
            plan.FullWidthPixels != metadata.TargetWidthPixels ||
            plan.FullHeightPixels != metadata.TargetHeightPixels ||
            !plan.PhysicalViewport.Equals(metadata.PhysicalViewport ??
                new MarkdownSvgPhysicalViewport(metadata.TargetWidthPixels, metadata.TargetHeightPixels)) ||
            !string.Equals(plan.IdentityPrefix, metadata.BitmapIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        return plan.HostAdmission is null ||
               (plan.OwnerSlot is not null && plan.AdmissionDomain is not null &&
                ReferenceEquals(plan.AdmissionDomain, planAdmissionDomain));
    }

    // The local commit was applied but the tile cache refused the transition:
    // detach the exact plan again so the box reloads it.
    private void AbandonUnappliedPlanNoLock(SvgTileRenderPlan plan, SvgPublicationCommitState commit)
    {
        if (!ReferenceEquals(_svg.AcceptedPlan, plan))
            return;

        commit.AbandonedOwnerSlot = plan.OwnerSlot;
        if (ReferenceEquals(_documents.Current, plan.Document))
        {
            commit.AbandonedDocument = plan.Document;
            commit.AbandonedDocumentCleanup = _documents.RetireNoLock(plan.Document);
            _documents.SetCurrentNoLock(null);
        }
        _svg.SetAcceptedPlanNoLock(null);
        _svg.SetParkingPlanNoLock(null);
        _svg.SetUsesTilesNoLock(false);
        _box.SvgTileCache.ResetRenderedTileState();
        _load.IsStarted = false;
    }
}
