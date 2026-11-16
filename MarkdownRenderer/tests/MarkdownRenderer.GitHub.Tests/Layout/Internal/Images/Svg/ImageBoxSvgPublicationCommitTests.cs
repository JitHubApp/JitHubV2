using System.Collections;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SvgPublicationCommitTestOperations;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg;

/// <summary>How an SVG image box commits, rejects and retires tiled and bitmap publications.</summary>
/// <remarks>
/// This file is longer than 500 lines because each commit rule (queued UI publication, dropped and
/// stale candidates, failed plans, reused and duplicate document handles, provider and metadata
/// matching, suspension, geometry invalidation and the fallback cache miss) is its own ordered
/// publication scenario over the same image box. The shared document, plan and tile-owner
/// operations live in <see cref="SvgPublicationCommitTestOperations"/>.
/// </remarks>
public sealed class ImageBoxSvgPublicationCommitTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task TiledFailureTransfersPreparedCleanupLeaseUntilTheLastDocumentUseDrains()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var document = new PublicationCommitSvgDocument();
        IDisposable? documentUse = null;
        RetirableCancellationTokenSource? tileCancellation = null;
        try
        {
            TrackDisposalsUnderWorkLock(image, document);
            (int X, int Y, int Width, int Height) key = (0, 0, 128, 128);
            CommitTiled(image, renderer, document, "previous-good-svg");
            SvgTileCacheTestOperations.PublishTile(
                image.SvgTileCache,
                (SvgTileRenderPlan)Plan(image)!,
                key,
                new SharedCanvasBitmapCache.Lease(null!));
            CommitTiled(image, renderer, document, "failure-prepared-cleanup");
            object plan = Plan(image)!;
            Assert.Equal("previous-good-svg", image.SvgTileCache.CaptureSnapshot().FallbackIdentityPrefix);
            documentUse = AcquireCurrentUse(image, document);

            tileCancellation = new RetirableCancellationTokenSource();
            object workOwner = SvgTileCacheTestOperations.AdmitWork(
                image.SvgTileCache, (SvgTileRenderPlan)plan, key, tileCancellation);

            image.TileHost.PublishSvgTileFailure((SvgTileRenderPlan)plan, key, (SvgTileWorkOwner)workOwner, new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "Deterministic test failure."));

            SvgDocumentUseState useState = DocumentUseState(image, document);
            Assert.True(useState.Retired);
            Assert.False(useState.DisposalScheduled);
            Assert.NotNull(useState.ReservedDisposalWork);
            Assert.Null(CurrentDocument(image));
            Assert.Equal(0, document.DisposeCount);

            documentUse.Dispose();
            documentUse = null;
            await document.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(useState.DisposalScheduled);
            Assert.Null(useState.ReservedDisposalWork);
            Assert.Equal(1, document.DisposeCount);
            Assert.False(document.DisposeWhileWorkLockHeld);
        }
        finally
        {
            documentUse?.Dispose();
            if (tileCancellation is not null)
            {
                Task cancellation = image.SvgTileRenderer.RetireTileCancellationSource(tileCancellation);
                await cancellation.WaitAsync(TimeSpan.FromSeconds(10));
            }
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TiledCandidateDoesNotReplaceAcceptedDocumentUntilUiPublicationRuns()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();
        var candidateDocument = new PublicationCommitSvgDocument();
        IDisposable? acceptedDocumentUse = null;
        Action? queuedPublication = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted");
            object acceptedPlan = Plan(image)!;
            acceptedDocumentUse = AcquireCurrentUse(image, acceptedDocument);
            var candidate = CreateTiledPublication(image, renderer, candidateDocument, "candidate");

            image.PublishOnUiThread(
                candidate.Publish,
                candidate.OnDropped,
                callback =>
                {
                    queuedPublication = callback;
                    return true;
                });

            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            Assert.True(IsCurrentTilePlan(image, acceptedPlan));
            Assert.Equal(0, acceptedDocument.DisposeCount);
            Assert.Equal(0, candidateDocument.DisposeCount);

            Assert.NotNull(queuedPublication);
            queuedPublication!();
            queuedPublication = null;

            Assert.Same(candidateDocument, CurrentDocument(image));
            Assert.Same(candidateDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            Assert.False(IsCurrentTilePlan(image, acceptedPlan));
            Assert.Equal(
                image.Svg.RenderGeneration,
                ((SvgTileRenderPlan)Plan(image)!).RenderGeneration);
            Assert.Equal("candidate", ((SvgTileRenderPlan)Plan(image)!).IdentityPrefix);
            Assert.Equal(2048, ((SvgTileRenderPlan)Plan(image)!).FullWidthPixels);
            Assert.Equal(1024, ((SvgTileRenderPlan)Plan(image)!).FullHeightPixels);

            // The accepted document's outstanding tile use keeps it alive after
            // replacement commits, until that exact use drains.
            Assert.Equal(0, acceptedDocument.DisposeCount);
            acceptedDocumentUse.Dispose();
            acceptedDocumentUse = null;
            await acceptedDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, acceptedDocument.DisposeCount);
            Assert.False(acceptedDocument.DisposeWhileWorkLockHeld);
            Assert.Equal(0, candidateDocument.DisposeCount);
        }
        finally
        {
            // Drain a publication held by the test dispatcher even if an assertion fails.
            queuedPublication?.Invoke();
            acceptedDocumentUse?.Dispose();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DroppedCandidatePublicationRetiresOnlyTheCandidate()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();
        var candidateDocument = new PublicationCommitSvgDocument();

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-drop");
            object acceptedPlan = Plan(image)!;
            var candidate = CreateTiledPublication(image, renderer, candidateDocument, "dropped-candidate");

            image.PublishOnUiThread(
                candidate.Publish,
                candidate.OnDropped,
                _ => false);

            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            Assert.True(IsCurrentTilePlan(image, acceptedPlan));
            Assert.Equal(0, acceptedDocument.DisposeCount);
            await candidateDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, candidateDocument.DisposeCount);
            Assert.False(candidateDocument.DisposeWhileWorkLockHeld);

            // The publication cleanup owner is idempotent even if a dispatcher
            // reports rejection and a faulty host invokes its drop callback again.
            candidate.OnDropped?.Invoke();
            Assert.Equal(1, candidateDocument.DisposeCount);
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StaleCandidatePublicationCannotRetireCurrentDocument()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();
        var candidateDocument = new PublicationCommitSvgDocument();
        Action? queuedPublication = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-stale");
            object acceptedPlan = Plan(image)!;
            var candidate = CreateTiledPublication(image, renderer, candidateDocument, "stale-candidate");
            image.PublishOnUiThread(
                candidate.Publish,
                candidate.OnDropped,
                callback =>
                {
                    queuedPublication = callback;
                    return true;
                });

            _ = AdvanceGeneration(image);
            Assert.True(IsCurrentTilePlan(image, acceptedPlan));
            queuedPublication!();
            queuedPublication = null;

            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            Assert.True(IsCurrentTilePlan(image, acceptedPlan));
            Assert.Equal(0, acceptedDocument.DisposeCount);
            await candidateDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, candidateDocument.DisposeCount);
        }
        finally
        {
            queuedPublication?.Invoke();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task OlderQueuedCandidateCannotReplaceNewerCommittedDocument()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();
        var olderCandidateDocument = new PublicationCommitSvgDocument();
        var newerDocument = new PublicationCommitSvgDocument();
        Action? olderPublication = null;

        try
        {
            TrackDisposalsUnderWorkLock(
                image,
                acceptedDocument,
                olderCandidateDocument,
                newerDocument);
            CommitTiled(image, renderer, acceptedDocument, "original");
            object originalPlan = Plan(image)!;

            var older = CreateTiledPublication(image, renderer, olderCandidateDocument, "older");
            image.PublishOnUiThread(
                older.Publish,
                older.OnDropped,
                callback =>
                {
                    olderPublication = callback;
                    return true;
                });

            // Advancing the generation and committing the newer document makes
            // the already-queued candidate stale before its callback is invoked.
            CommitTiled(image, renderer, newerDocument, "newer");
            Assert.Same(newerDocument, CurrentDocument(image));
            Assert.False(IsCurrentTilePlan(image, originalPlan));
            Assert.Equal(0, newerDocument.DisposeCount);

            olderPublication!();
            olderPublication = null;

            Assert.Same(newerDocument, CurrentDocument(image));
            Assert.Same(newerDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            Assert.False(IsCurrentTilePlan(image, originalPlan));
            await olderCandidateDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, olderCandidateDocument.DisposeCount);
            Assert.False(olderCandidateDocument.DisposeWhileWorkLockHeld);
            Assert.Equal(0, newerDocument.DisposeCount);
        }
        finally
        {
            olderPublication?.Invoke();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task AcceptedPlanRemainsUsableWhileReplacementIsPendingAndOldTileFailureCannotCancelIt()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();
        var candidateDocument = new PublicationCommitSvgDocument();
        IDisposable? candidateUse = null;
        RetirableCancellationTokenSource? oldTileWork = null;
        RetirableCancellationTokenSource? lateOldTileWork = null;
        object? oldTileOwner = null;
        object? lateOldTileOwner = null;
        (int X, int Y, int Width, int Height) key = (0, 0, 128, 128);

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-plan");
            object acceptedPlan = Plan(image)!;

            // Starting a candidate advances publication ordering, but must not
            // make the still-accepted plan ineligible to refill visible tiles.
            int candidateGeneration = AdvanceGeneration(image);
            Assert.True(IsCurrentTilePlan(image, acceptedPlan));

            candidateUse = AcquireCandidate(image, candidateDocument);
            (object metadata, object candidatePlan) = CreateMetadataAndPlan(
                image, renderer, candidateDocument, candidateGeneration, "replacement-plan");

            oldTileWork = new RetirableCancellationTokenSource();
            oldTileOwner = RegisterTileFailureOwner(image, acceptedPlan, key, oldTileWork);

            // A terminal failure of the older fallback plan detaches only that
            // plan. It must not advance the newer candidate's publication epoch.
            Assert.True(image.TileHost.TryInvalidateFailedSvgTile((SvgTileRenderPlan)acceptedPlan, key, (SvgTileWorkOwner)oldTileOwner, null));
            Assert.Equal(candidateGeneration, image.Svg.RenderGeneration);
            Assert.Null(Plan(image));
            Assert.False(image.Svg.UsesTiles);
            ReleaseTileWork(image, key, oldTileOwner, oldTileWork);
            oldTileWork = null;

            Assert.True(image.SvgPublication.TryCommit((SvgPublicationMetadata)metadata, (SvgTileRenderPlan?)candidatePlan));
            candidateUse.Dispose();
            candidateUse = null;
            Assert.Same(candidateDocument, CurrentDocument(image));
            Assert.Same(candidateDocument, ((SvgTileRenderPlan)Plan(image)!).Document);

            // A delayed callback from the retired plan cannot invalidate or
            // otherwise disturb the just-committed candidate. The commit
            // detached the retired plan's work, so its owner is no longer mapped.
            lateOldTileWork = new RetirableCancellationTokenSource();
            lateOldTileOwner = CreateUnmappedTileOwner(acceptedPlan, lateOldTileWork);
            Assert.False(image.TileHost.TryInvalidateFailedSvgTile((SvgTileRenderPlan)acceptedPlan, key, (SvgTileWorkOwner)lateOldTileOwner, null));
            Assert.Equal(candidateGeneration, image.Svg.RenderGeneration);
            Assert.Same(candidateDocument, CurrentDocument(image));
            Assert.Same(candidateDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            ReleaseTileWork(image, key, lateOldTileOwner, lateOldTileWork);
            lateOldTileWork = null;

            await acceptedDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, acceptedDocument.DisposeCount);
            Assert.False(acceptedDocument.DisposeWhileWorkLockHeld);
            Assert.Equal(0, candidateDocument.DisposeCount);
        }
        finally
        {
            candidateUse?.Dispose();
            if (oldTileWork is not null)
                ReleaseTileWork(image, key, oldTileOwner, oldTileWork);
            if (lateOldTileWork is not null)
                ReleaseTileWork(image, key, lateOldTileOwner, lateOldTileWork);
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task FailedAcceptedPlanRejectsSameHandleCandidateAndRetiresItOnlyOnce()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var document = new PublicationCommitSvgDocument();
        IDisposable? candidateUse = null;
        RetirableCancellationTokenSource? tileWork = null;
        object? tileWorkOwner = null;
        (int X, int Y, int Width, int Height) key = (0, 0, 128, 128);

        try
        {
            TrackDisposalsUnderWorkLock(image, document);
            CommitTiled(image, renderer, document, "same-handle-before-failure");
            object failedPlan = Plan(image)!;
            int candidateGeneration = AdvanceGeneration(image);
            candidateUse = AcquireCandidate(image, document);
            (object metadata, object candidatePlan) = CreateMetadataAndPlan(
                image, renderer, document, candidateGeneration, "same-handle-after-failure");

            tileWork = new RetirableCancellationTokenSource();
            tileWorkOwner = RegisterTileFailureOwner(image, failedPlan, key, tileWork);
            Assert.True(image.TileHost.TryInvalidateFailedSvgTile((SvgTileRenderPlan)failedPlan, key, (SvgTileWorkOwner)tileWorkOwner, null));
            Assert.Equal(candidateGeneration, image.Svg.RenderGeneration);
            RemoveTileFailureOwner(image, key, tileWorkOwner);
            tileWork.Dispose();
            tileWork = null;

            // The real failure publication releases the failed current handle
            // after detaching its plan. A candidate reservation for this same
            // handle cannot resurrect an already-retired provider document.
            image.SvgDocuments.Release(document);
            Assert.Null(CurrentDocument(image));
            Assert.False(image.SvgPublication.TryCommit((SvgPublicationMetadata)metadata, (SvgTileRenderPlan?)candidatePlan));

            image.SvgDocuments.RetireUncommitted(document);
            candidateUse.Dispose();
            candidateUse = null;
            await document.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, document.DisposeCount);
            Assert.False(document.DisposeWhileWorkLockHeld);
        }
        finally
        {
            candidateUse?.Dispose();
            if (tileWork is not null)
            {
                RemoveTileFailureOwner(image, key, tileWorkOwner);
                tileWork.Dispose();
            }
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DroppingCandidateThatReusesCurrentDocumentDoesNotDisposeIt()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var currentDocument = new PublicationCommitSvgDocument();

        try
        {
            TrackDisposalsUnderWorkLock(image, currentDocument);
            CommitTiled(image, renderer, currentDocument, "same-current");
            var candidate = CreateTiledPublication(image, renderer, currentDocument, "same-current-candidate");

            image.PublishOnUiThread(candidate.Publish, candidate.OnDropped, _ => false);

            Assert.Same(currentDocument, CurrentDocument(image));
            Assert.Same(currentDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            Assert.Equal(0, currentDocument.DisposeCount);
            Assert.False(DocumentUseState(image, currentDocument).CandidateReserved);
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task AcceptedPublicationReusingCurrentDocumentReplacesPlanWithoutRetiringHandle()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var currentDocument = new PublicationCommitSvgDocument();

        try
        {
            TrackDisposalsUnderWorkLock(image, currentDocument);
            CommitTiled(image, renderer, currentDocument, "same-current-before-reopen");
            object previousPlan = Plan(image)!;
            var candidate = CreateTiledPublication(
                image,
                renderer,
                currentDocument,
                "same-current-after-reopen");

            image.PublishOnUiThread(
                candidate.Publish,
                candidate.OnDropped,
                callback =>
                {
                    callback();
                    return true;
                });

            Assert.Same(currentDocument, CurrentDocument(image));
            Assert.Same(currentDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            Assert.NotSame(previousPlan, Plan(image));
            Assert.False(IsCurrentTilePlan(image, previousPlan));
            Assert.Equal("same-current-after-reopen", ((SvgTileRenderPlan)Plan(image)!).IdentityPrefix);
            Assert.Equal(0, currentDocument.DisposeCount);
            Assert.False(DocumentUseState(image, currentDocument).CandidateReserved);
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DuplicatePendingDocumentHandleIsRejectedWithoutDisposingFirstCandidate()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var candidateDocument = new PublicationCommitSvgDocument();
        IDisposable? firstUse = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, candidateDocument);
            firstUse = AcquireCandidate(image, candidateDocument);
            SvgDocumentUseLease? duplicate = image.SvgDocuments.TryAcquireCandidateUse(
                candidateDocument, out bool ownershipConflict);

            Assert.Null(duplicate);
            Assert.True(ownershipConflict);
            Assert.Equal(0, candidateDocument.DisposeCount);

            image.SvgDocuments.RetireUncommitted(candidateDocument);
            firstUse.Dispose();
            firstUse = null;
            await candidateDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, candidateDocument.DisposeCount);
        }
        finally
        {
            firstUse?.Dispose();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PlanMustMatchPublicationMetadataAndCurrentProvider()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();
        var candidateDocument = new PublicationCommitSvgDocument();
        IDisposable? candidateUse = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-mismatch");
            int generation = AdvanceGeneration(image);
            candidateUse = AcquireCandidate(image, candidateDocument);
            object metadata = CreateMetadata(image, renderer, generation, "candidate-identity");
            object mismatchedPlan = CreatePlan(
                candidateDocument,
                "wrong-identity",
                Width(metadata),
                Height(metadata),
                generation);

            Assert.False(image.SvgPublication.TryCommit((SvgPublicationMetadata)metadata, (SvgTileRenderPlan?)mismatchedPlan));
            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedDocument, ((SvgTileRenderPlan)Plan(image)!).Document);

            var otherRenderer = new PublicationCommitSvgRenderer();
            object wrongProviderMetadata = CreateMetadata(
                image,
                otherRenderer,
                generation,
                "candidate-identity");
            object matchingPlan = CreatePlan(
                candidateDocument,
                "candidate-identity",
                Width(wrongProviderMetadata),
                Height(wrongProviderMetadata),
                generation);
            Assert.False(image.SvgPublication.TryCommit((SvgPublicationMetadata)wrongProviderMetadata, (SvgTileRenderPlan?)matchingPlan));
            Assert.Same(acceptedDocument, CurrentDocument(image));

            image.SvgDocuments.RetireUncommitted(candidateDocument);
            candidateUse.Dispose();
            candidateUse = null;
            await candidateDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, candidateDocument.DisposeCount);
            Assert.Equal(0, acceptedDocument.DisposeCount);
        }
        finally
        {
            candidateUse?.Dispose();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SuspendedImageRejectsCandidateAndPreservesAcceptedDocument()
    {
        var renderer = new PublicationCommitSvgRenderer();
        // Offscreen release is what lets an image's residency be suspended.
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                PrefetchDocumentImages = false,
                ReleaseOffscreenImageResources = true,
            });
        var image = CreateImage(renderer, session);
        var acceptedDocument = new PublicationCommitSvgDocument();
        var candidateDocument = new PublicationCommitSvgDocument();
        IDisposable? candidateUse = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-suspend");
            int generation = AdvanceGeneration(image);
            candidateUse = AcquireCandidate(image, candidateDocument);
            (object metadata, object plan) = CreateMetadataAndPlan(
                image, renderer, candidateDocument, generation, "candidate-during-suspend");
            // Close admission only, as the first step of SuspendResources does,
            // so the candidate's render generation stays current and the
            // suspension alone must reject it.
            Assert.True(image.Lifetime.TrySuspend(
                out RetirableCancellationTokenSource? _,
                out ImageWorkLease? suspensionWork));
            suspensionWork?.Dispose();

            Assert.False(image.SvgPublication.TryCommit((SvgPublicationMetadata)metadata, (SvgTileRenderPlan?)plan));
            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedDocument, ((SvgTileRenderPlan)Plan(image)!).Document);

            image.SvgDocuments.RetireUncommitted(candidateDocument);
            candidateUse.Dispose();
            candidateUse = null;
            await candidateDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, candidateDocument.DisposeCount);
            Assert.Equal(0, acceptedDocument.DisposeCount);
        }
        finally
        {
            candidateUse?.Dispose();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task AcceptedBitmapPublicationClearsAndRetiresPreviousTiledDocument()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-bitmap");
            int generation = AdvanceGeneration(image);
            object metadata = CreateMetadata(image, renderer, generation, "full-bitmap");

            Assert.True(image.SvgPublication.TryCommit((SvgPublicationMetadata)metadata, (SvgTileRenderPlan?)null));

            Assert.Null(CurrentDocument(image));
            Assert.Null(Plan(image));
            Assert.False(image.Svg.UsesTiles);
            await acceptedDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, acceptedDocument.DisposeCount);
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(true, 32, 16, 32, 16, false)]
    [InlineData(false, 32, 16, 32, 16, true)]
    [InlineData(true, 32, 16, 64, 32, true)]
    [InlineData(true, 32, 16, 32, 24, true)]
    [InlineData(true, 32, 16, 48, 16, true)]
    [InlineData(true, 32, 16, 32, 32, true)]
    public void SvgGeometryInvalidationDependsOnKnownMetadataAndExactIntrinsicSize(
        bool metadataWasKnown,
        double previousWidth,
        double previousHeight,
        double candidateWidth,
        double candidateHeight,
        bool expected)
    {
        bool actual = SvgPublicationCommit.ShouldInvalidateSvgGeometry(
            metadataWasKnown,
            new Size(previousWidth, previousHeight),
            new Size(candidateWidth, candidateHeight));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task QueuedTiledPublicationInvalidatesLayoutWhenKnownIntrinsicSizeChanges()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();
        var candidateDocument = new PublicationCommitSvgDocument();
        Action? queuedPublication = null;
        bool? layoutInvalidated = null;
        EventHandler<LoadCompletedEventArgs> loadCompleted = (_, args) =>
            layoutInvalidated = args.LayoutInvalidated;
        image.LoadCompleted += loadCompleted;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-known-geometry-change");
            var candidate = CreateTiledPublication(
                image,
                renderer,
                candidateDocument,
                "known-geometry-change",
                metadataWasKnown: true,
                intrinsic: new Size(64, 32));

            image.PublishOnUiThread(
                candidate.Publish,
                candidate.OnDropped,
                callback =>
                {
                    queuedPublication = callback;
                    return true;
                });

            Assert.Null(layoutInvalidated);
            Assert.Same(acceptedDocument, CurrentDocument(image));

            Assert.NotNull(queuedPublication);
            queuedPublication!();
            queuedPublication = null;

            Assert.True(layoutInvalidated.HasValue);
            Assert.True(layoutInvalidated.Value);
            Assert.Same(candidateDocument, CurrentDocument(image));
            Assert.Same(candidateDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
        }
        finally
        {
            queuedPublication?.Invoke();
            image.LoadCompleted -= loadCompleted;
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task FallbackCacheTileVariantMissLeavesAcceptedSvgMetadataAndDocumentUntouched()
    {
        var renderer = new PublicationCommitSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new PublicationCommitSvgDocument();
        string cacheKey = "svg-fallback-staging-test:" + Guid.NewGuid().ToString("N");
        object? cacheEntry = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-fallback-miss");

            object acceptedPlan = Plan(image)!;
            AcceptedSvgMetadataSnapshot accepted = AcceptedSvgMetadataSnapshot.Capture(image);

            SvgCacheEntry entry = CreateUnacceptedTiledCacheEntry(renderer);
            cacheEntry = entry;

            _renderResources.Images.SvgSources.Set(cacheKey, entry);

            var sourceLoader = new ImageSourceLoader(
                image.SourceLoadHost, image.Context, _renderResources.Images, image.OutputAdmission, image.OutputDeferral,
                image.SvgHostDeferral, image.Url, requestedWidth: null, requestedHeight: null);
            Assert.False(sourceLoader.TryLoadFallbackFromProcessCache(cacheKey, expectedWork: null));

            Assert.True(image.LoadState.IsSvg);
            accepted.AssertUnchanged(image);
            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedPlan, Plan(image));
            Assert.Same(acceptedDocument, ((SvgTileRenderPlan)Plan(image)!).Document);
            Assert.True(image.Svg.UsesTiles);
            Assert.Equal(0, acceptedDocument.DisposeCount);
        }
        finally
        {
            if (cacheEntry is not null)
                _ = _renderResources.Images.SvgSources.Remove(cacheKey);
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private ImageBox CreateImage(
        PublicationCommitSvgRenderer renderer,
        MarkdownPerformanceSession? session = null) => new(
        CreateContext(renderer, session), _renderResources.Images,
        "data:image/svg+xml,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%3E%3C%2Fsvg%3E",
        "Publication commit test");

    private MarkdownLayoutContext CreateContext(
        PublicationCommitSvgRenderer renderer,
        MarkdownPerformanceSession? session)
    {
        var body = new ElementStyle { Foreground = Color.FromArgb(255, 12, 34, 56) };
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.ImageCaption] = body,
                [MarkdownElementKeys.ListMarker] = body,
            },
            new Dictionary<string, ElementStyleOverride>(),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            isDark: true,
            isHighContrast: false,
            textScaleFactor: 1);

        return new MarkdownLayoutContext(
            resourceCreator: null!,
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = _renderResources,
            SvgRenderer = renderer,
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = 1,
            PerformanceSession = session,
            PerformanceDocumentOwner = session is null ? null : new object(),
        };
    }

    private void CommitTiled(
        ImageBox image,
        PublicationCommitSvgRenderer renderer,
        PublicationCommitSvgDocument document,
        string identity)
    {
        int generation = AdvanceGeneration(image);
        using IDisposable candidateUse = AcquireCandidate(image, document);
        (object metadata, object plan) = CreateMetadataAndPlan(image, renderer, document, generation, identity);

        Assert.True(image.SvgPublication.TryCommit((SvgPublicationMetadata)metadata, (SvgTileRenderPlan?)plan));
        Assert.Same(document, CurrentDocument(image));
    }

    private (object Metadata, object Plan) CreateMetadataAndPlan(
        ImageBox image,
        PublicationCommitSvgRenderer renderer,
        PublicationCommitSvgDocument document,
        int generation,
        string identity)
    {
        object metadata = CreateMetadata(image, renderer, generation, identity);
        object plan = CreatePlan(
            document,
            identity,
            Width(metadata),
            Height(metadata),
            generation);
        return (metadata, plan);
    }

    /// <summary>
    /// A very large intrinsic variant is deterministically classified as tiled
    /// before any CanvasDevice access. It cannot use an exact whole-bitmap cache
    /// entry and therefore exercises the miss/fallback path.
    /// </summary>
    private SvgCacheEntry CreateUnacceptedTiledCacheEntry(PublicationCommitSvgRenderer renderer)
    {
        long rendererIdentity =
            _renderResources.Images.ProviderIdentities.GetSvgRendererIdentity(renderer);
        var candidateInfo = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 20_000,
            IntrinsicHeightDips: 20_000,
            IntrinsicAspectRatio: 1,
            Description: "unaccepted cached metadata");
        return new SvgCacheEntry(
            new byte[] { 1, 2, 3 },
            new Size(20_000, 20_000),
            "unaccepted cached title",
            "unaccepted cached description",
            new string('C', 64),
            new string('D', 64),
            candidateInfo,
            rendererIdentity,
            renderer.CacheGeneration,
            null,
            0,
            0,
            null);
    }

    private (Action Publish, Action? OnDropped) CreateTiledPublication(
        ImageBox image,
        PublicationCommitSvgRenderer renderer,
        PublicationCommitSvgDocument document,
        string identity,
        bool metadataWasKnown = false,
        Size? intrinsic = null)
    {
        int generation = AdvanceGeneration(image);
        IDisposable candidateUse = AcquireCandidate(image, document);
        object metadata = CreateMetadata(
            image,
            renderer,
            generation,
            identity,
            metadataWasKnown,
            intrinsic);
        object publication = image.SvgPublication.CreateTiledPublication((SvgPublicationMetadata)metadata, document, (SvgDocumentUseLease)candidateUse, 0L)!;
        return Assert.IsType<ValueTuple<Action, Action?>>(publication);
    }

    private object CreateMetadata(
        ImageBox image,
        PublicationCommitSvgRenderer renderer,
        int generation,
        string identity,
        bool metadataWasKnown = false,
        Size? intrinsic = null)
    {
        byte[] source = [1, 2, 3, 4];
        Size intrinsicSize = intrinsic ?? new Size(32, 16);
        long rendererIdentity =
            _renderResources.Images.ProviderIdentities.GetSvgRendererIdentity(renderer);
        var info = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: intrinsicSize.Width,
            IntrinsicHeightDips: intrinsicSize.Height,
            IntrinsicAspectRatio: intrinsicSize.Width / intrinsicSize.Height,
            Description: "SVG publication test");
        return new SvgPublicationMetadata(
            new WeakReference<byte[]>(source),
            source.LongLength,
            renderer,
            rendererIdentity,
            renderer.CacheGeneration,
            generation,
            metadataWasKnown,
            "resolved:" + identity,
            "failure:" + identity,
            intrinsicSize,
            "title",
            "description",
            new string('A', 64),
            new string('B', 64),
            info,
            2048,
            1024,
            identity,
            0L,
            0L,
            2048L * 1024L,
            null, // No provider-admission demand on this legacy-provider fixture.
            null, // No host admission capability.
            0L,
            null,
            null);
    }
}
