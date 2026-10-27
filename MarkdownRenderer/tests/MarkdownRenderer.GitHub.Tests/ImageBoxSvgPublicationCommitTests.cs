using System.Collections;
using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageBoxSvgPublicationCommitTests
{
    [Fact]
    public async Task TiledFailureTransfersPreparedCleanupLeaseUntilTheLastDocumentUseDrains()
    {
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var document = new TestSvgDocument();
        IDisposable? documentUse = null;
        CancellationTokenSource? tileCancellation = null;
        try
        {
            TrackDisposalsUnderWorkLock(image, document);
            CommitTiled(image, renderer, document, "failure-prepared-cleanup");
            object plan = Plan(image)!;
            documentUse = AcquireCurrentUse(image, document);

            (int X, int Y, int Width, int Height) key = (0, 0, 128, 128);
            IDictionary inFlight = (IDictionary)Field<object>(image, "_svgTilesInFlight");
            tileCancellation = new CancellationTokenSource();
            inFlight.Add(key, tileCancellation);
            object workOwner = RegisterTileFailureOwner(image, plan, key, tileCancellation);
            SetField(workOwner, "State", 0);

            SetField(
                image,
                "_svgFallbackTiles",
                new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>
                {
                    [key] = new SharedCanvasBitmapCache.Lease(null!),
                });
            SetField(image, "_svgFallbackIdentityPrefix", "previous-good-svg");
            SetField(image, "_svgFallbackWidthPixels", 128);
            SetField(image, "_svgFallbackHeightPixels", 128);

            InvokePrivate(
                image,
                "PublishSvgTileFailure",
                plan,
                key,
                workOwner,
                new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "Deterministic test failure."));

            object useState = DocumentUseState(image, document);
            Assert.True(Field<bool>(useState, "Retired"));
            Assert.False(Field<bool>(useState, "DisposalScheduled"));
            Assert.NotNull(Field<object?>(useState, "ReservedDisposalWork"));
            Assert.Null(CurrentDocument(image));
            Assert.Equal(0, document.DisposeCount);

            documentUse.Dispose();
            documentUse = null;
            await document.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(Field<bool>(useState, "DisposalScheduled"));
            Assert.Null(Field<object?>(useState, "ReservedDisposalWork"));
            Assert.Equal(1, document.DisposeCount);
            Assert.False(document.DisposeWhileWorkLockHeld);
        }
        finally
        {
            documentUse?.Dispose();
            if (tileCancellation is not null)
            {
                Task cancellation = Assert.IsAssignableFrom<Task>(InvokePrivate(
                    image,
                    "RetireTileCancellationSource",
                    tileCancellation));
                await cancellation.WaitAsync(TimeSpan.FromSeconds(10));
            }
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TiledCandidateDoesNotReplaceAcceptedDocumentUntilUiPublicationRuns()
    {
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        var candidateDocument = new TestSvgDocument();
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
            Assert.Same(acceptedDocument, Field<object>(Plan(image)!, "Document"));
            Assert.True(IsCurrentTilePlan(image, acceptedPlan));
            Assert.Equal(0, acceptedDocument.DisposeCount);
            Assert.Equal(0, candidateDocument.DisposeCount);

            Assert.NotNull(queuedPublication);
            queuedPublication!();
            queuedPublication = null;

            Assert.Same(candidateDocument, CurrentDocument(image));
            Assert.Same(candidateDocument, Field<object>(Plan(image)!, "Document"));
            Assert.False(IsCurrentTilePlan(image, acceptedPlan));
            Assert.Equal(
                Field<int>(image, "_svgRenderGeneration"),
                Field<int>(Plan(image)!, "RenderGeneration"));
            Assert.Equal("candidate", Field<string>(Plan(image)!, "IdentityPrefix"));
            Assert.Equal(2048, Field<int>(Plan(image)!, "FullWidthPixels"));
            Assert.Equal(1024, Field<int>(Plan(image)!, "FullHeightPixels"));

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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        var candidateDocument = new TestSvgDocument();

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
            Assert.Same(acceptedDocument, Field<object>(Plan(image)!, "Document"));
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        var candidateDocument = new TestSvgDocument();
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
            Assert.Same(acceptedDocument, Field<object>(Plan(image)!, "Document"));
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        var olderCandidateDocument = new TestSvgDocument();
        var newerDocument = new TestSvgDocument();
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
            Assert.Same(newerDocument, Field<object>(Plan(image)!, "Document"));
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        var candidateDocument = new TestSvgDocument();
        IDisposable? candidateUse = null;
        CancellationTokenSource? oldTileWork = null;
        CancellationTokenSource? lateOldTileWork = null;
        object? oldTileOwner = null;
        object? lateOldTileOwner = null;
        (int X, int Y, int Width, int Height) key = (0, 0, 128, 128);
        IDictionary inFlight = null!;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-plan");
            object acceptedPlan = Plan(image)!;

            // Starting a candidate advances publication ordering, but must not
            // make the still-accepted plan ineligible to refill visible tiles.
            int candidateGeneration = AdvanceGeneration(image);
            Assert.True(Assert.IsType<bool>(InvokePrivate(
                image,
                "IsCurrentSvgTilePlan",
                acceptedPlan)));

            candidateUse = AcquireCandidate(image, candidateDocument);
            object metadata = CreateMetadata(
                image,
                renderer,
                candidateGeneration,
                "replacement-plan");
            object candidatePlan = CreatePlan(
                candidateDocument,
                "replacement-plan",
                Width(metadata),
                Height(metadata),
                candidateGeneration);

            inFlight = (IDictionary)Field<object>(image, "_svgTilesInFlight");
            oldTileWork = new CancellationTokenSource();
            inFlight.Add(key, oldTileWork);
            oldTileOwner = RegisterTileFailureOwner(image, acceptedPlan, key, oldTileWork);

            // A terminal failure of the older fallback plan detaches only that
            // plan. It must not advance the newer candidate's publication epoch.
            Assert.True(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryInvalidateFailedSvgTile",
                acceptedPlan,
                key,
                oldTileOwner,
                null)));
            Assert.Equal(candidateGeneration, Field<int>(image, "_svgRenderGeneration"));
            Assert.Null(Plan(image));
            Assert.False(Field<bool>(image, "_svgUsesTiles"));
            RemoveTileFailureOwner(image, key);
            oldTileWork.Dispose();
            oldTileWork = null;

            Assert.True(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryCommitSvgPublication",
                metadata,
                candidatePlan)));
            candidateUse.Dispose();
            candidateUse = null;
            Assert.Same(candidateDocument, CurrentDocument(image));
            Assert.Same(candidateDocument, Field<object>(Plan(image)!, "Document"));

            // A delayed callback from the retired plan cannot invalidate or
            // otherwise disturb the just-committed candidate.
            lateOldTileWork = new CancellationTokenSource();
            inFlight.Add(key, lateOldTileWork);
            lateOldTileOwner = RegisterTileFailureOwner(image, acceptedPlan, key, lateOldTileWork);
            Assert.False(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryInvalidateFailedSvgTile",
                acceptedPlan,
                key,
                lateOldTileOwner,
                null)));
            Assert.Equal(candidateGeneration, Field<int>(image, "_svgRenderGeneration"));
            Assert.Same(candidateDocument, CurrentDocument(image));
            Assert.Same(candidateDocument, Field<object>(Plan(image)!, "Document"));
            RemoveTileFailureOwner(image, key);
            lateOldTileWork.Dispose();
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
            {
                RemoveTileFailureOwner(image, key);
                oldTileWork.Dispose();
            }
            if (lateOldTileWork is not null)
            {
                RemoveTileFailureOwner(image, key);
                lateOldTileWork.Dispose();
            }
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task FailedAcceptedPlanRejectsSameHandleCandidateAndRetiresItOnlyOnce()
    {
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var document = new TestSvgDocument();
        IDisposable? candidateUse = null;
        CancellationTokenSource? tileWork = null;
        object? tileWorkOwner = null;
        (int X, int Y, int Width, int Height) key = (0, 0, 128, 128);
        IDictionary inFlight = null!;

        try
        {
            TrackDisposalsUnderWorkLock(image, document);
            CommitTiled(image, renderer, document, "same-handle-before-failure");
            object failedPlan = Plan(image)!;
            int candidateGeneration = AdvanceGeneration(image);
            candidateUse = AcquireCandidate(image, document);
            object metadata = CreateMetadata(
                image,
                renderer,
                candidateGeneration,
                "same-handle-after-failure");
            object candidatePlan = CreatePlan(
                document,
                "same-handle-after-failure",
                Width(metadata),
                Height(metadata),
                candidateGeneration);

            inFlight = (IDictionary)Field<object>(image, "_svgTilesInFlight");
            tileWork = new CancellationTokenSource();
            inFlight.Add(key, tileWork);
            tileWorkOwner = RegisterTileFailureOwner(image, failedPlan, key, tileWork);
            Assert.True(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryInvalidateFailedSvgTile",
                failedPlan,
                key,
                tileWorkOwner,
                null)));
            Assert.Equal(candidateGeneration, Field<int>(image, "_svgRenderGeneration"));
            RemoveTileFailureOwner(image, key);
            tileWork.Dispose();
            tileWork = null;

            // The real failure publication releases the failed current handle
            // after detaching its plan. A candidate reservation for this same
            // handle cannot resurrect an already-retired provider document.
            InvokePrivate(image, "ReleaseSvgDocument", document);
            Assert.Null(CurrentDocument(image));
            Assert.False(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryCommitSvgPublication",
                metadata,
                candidatePlan)));

            InvokePrivate(image, "RetireUncommittedSvgDocument", document);
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
                RemoveTileFailureOwner(image, key);
                tileWork.Dispose();
            }
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DroppingCandidateThatReusesCurrentDocumentDoesNotDisposeIt()
    {
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var currentDocument = new TestSvgDocument();

        try
        {
            TrackDisposalsUnderWorkLock(image, currentDocument);
            CommitTiled(image, renderer, currentDocument, "same-current");
            var candidate = CreateTiledPublication(image, renderer, currentDocument, "same-current-candidate");

            image.PublishOnUiThread(candidate.Publish, candidate.OnDropped, _ => false);

            Assert.Same(currentDocument, CurrentDocument(image));
            Assert.Same(currentDocument, Field<object>(Plan(image)!, "Document"));
            Assert.Equal(0, currentDocument.DisposeCount);
            Assert.False(Field<bool>(DocumentUseState(image, currentDocument), "CandidateReserved"));
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var currentDocument = new TestSvgDocument();

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
            Assert.Same(currentDocument, Field<object>(Plan(image)!, "Document"));
            Assert.NotSame(previousPlan, Plan(image));
            Assert.False(IsCurrentTilePlan(image, previousPlan));
            Assert.Equal("same-current-after-reopen", Field<string>(Plan(image)!, "IdentityPrefix"));
            Assert.Equal(0, currentDocument.DisposeCount);
            Assert.False(Field<bool>(DocumentUseState(image, currentDocument), "CandidateReserved"));
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var candidateDocument = new TestSvgDocument();
        IDisposable? firstUse = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, candidateDocument);
            firstUse = AcquireCandidate(image, candidateDocument);
            object?[] duplicateArgs = [candidateDocument, null];
            object? duplicate = InvokePrivate(image, "TryAcquireCandidateSvgDocumentUse", duplicateArgs);

            Assert.Null(duplicate);
            Assert.True(Assert.IsType<bool>(duplicateArgs[1]));
            Assert.Equal(0, candidateDocument.DisposeCount);

            InvokePrivate(image, "RetireUncommittedSvgDocument", candidateDocument);
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        var candidateDocument = new TestSvgDocument();
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

            Assert.False(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryCommitSvgPublication",
                metadata,
                mismatchedPlan)));
            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedDocument, Field<object>(Plan(image)!, "Document"));

            var otherRenderer = new TestSvgRenderer();
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
            Assert.False(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryCommitSvgPublication",
                wrongProviderMetadata,
                matchingPlan)));
            Assert.Same(acceptedDocument, CurrentDocument(image));

            InvokePrivate(image, "RetireUncommittedSvgDocument", candidateDocument);
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        var candidateDocument = new TestSvgDocument();
        IDisposable? candidateUse = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument, candidateDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-suspend");
            int generation = AdvanceGeneration(image);
            candidateUse = AcquireCandidate(image, candidateDocument);
            object metadata = CreateMetadata(image, renderer, generation, "candidate-during-suspend");
            object plan = CreatePlan(
                candidateDocument,
                "candidate-during-suspend",
                Width(metadata),
                Height(metadata),
                generation);
            SetField(image, "_resourcesSuspended", true);

            Assert.False(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryCommitSvgPublication",
                metadata,
                plan)));
            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedDocument, Field<object>(Plan(image)!, "Document"));

            InvokePrivate(image, "RetireUncommittedSvgDocument", candidateDocument);
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-bitmap");
            int generation = AdvanceGeneration(image);
            object metadata = CreateMetadata(image, renderer, generation, "full-bitmap");

            Assert.True(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryCommitSvgPublication",
                metadata,
                null)));

            Assert.Null(CurrentDocument(image));
            Assert.Null(Plan(image));
            Assert.False(Field<bool>(image, "_svgUsesTiles"));
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
        bool actual = Assert.IsType<bool>(InvokePrivateStatic(
            "ShouldInvalidateSvgGeometry",
            metadataWasKnown,
            new Size(previousWidth, previousHeight),
            new Size(candidateWidth, candidateHeight)));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task QueuedTiledPublicationInvalidatesLayoutWhenKnownIntrinsicSizeChanges()
    {
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        var candidateDocument = new TestSvgDocument();
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
            Assert.Same(candidateDocument, Field<object>(Plan(image)!, "Document"));
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
        var renderer = new TestSvgRenderer();
        var image = CreateImage(renderer);
        var acceptedDocument = new TestSvgDocument();
        string cacheKey = "svg-fallback-staging-test:" + Guid.NewGuid().ToString("N");
        object? cacheEntry = null;

        try
        {
            TrackDisposalsUnderWorkLock(image, acceptedDocument);
            CommitTiled(image, renderer, acceptedDocument, "accepted-before-fallback-miss");

            object acceptedPlan = Plan(image)!;
            Size acceptedIntrinsic = Field<Size>(image, "_svgIntrinsicSize");
            string? acceptedTitle = Field<string?>(image, "_svgTitle");
            string? acceptedDescription = Field<string?>(image, "_svgDesc");
            string? acceptedContentHash = Field<string?>(image, "_svgContentHash");
            string? acceptedPartitionHash = Field<string?>(image, "_svgSecurityPartitionHash");
            object? acceptedInfo = Field<object?>(image, "_svgInfo");
            object? acceptedSourceIdentity = Field<object?>(image, "_svgSourceIdentity");
            long acceptedSourceByteLength = Field<long>(image, "_svgSourceByteLength");
            long acceptedRendererIdentity = Field<long>(image, "_svgRendererIdentity");
            long acceptedProviderGeneration = Field<long>(image, "_svgProviderCacheGeneration");
            int acceptedWidth = Field<int>(image, "_svgBitmapWidthPixels");
            int acceptedHeight = Field<int>(image, "_svgBitmapHeightPixels");
            int acceptedGeneration = Field<int>(image, "_svgRenderGeneration");

            // A very large intrinsic variant is deterministically classified as
            // tiled before any CanvasDevice access. It cannot use an exact whole-
            // bitmap cache entry and therefore exercises the miss/fallback path.
            Type entryType = typeof(ImageBox).GetNestedType(
                "SvgCacheEntry",
                BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(typeof(ImageBox).FullName, "SvgCacheEntry");
            ConstructorInfo constructor = Assert.Single(entryType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            long rendererIdentity = Assert.IsType<long>(InvokePrivateStatic(
                "GetSvgRendererIdentity",
                renderer));
            var candidateInfo = new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 20_000,
                IntrinsicHeightDips: 20_000,
                IntrinsicAspectRatio: 1,
                Description: "unaccepted cached metadata");
            cacheEntry = constructor.Invoke(
            [
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
                null,
            ]);

            object cache = FieldStatic<object>(typeof(ImageBox), "_svgCache");
            _ = InvokePrivate(cache, "Set", cacheKey, cacheEntry);

            Assert.False(Assert.IsType<bool>(InvokePrivate(
                image,
                "TryLoadFallbackFromProcessCache",
                cacheKey,
                null)));

            Assert.True(Field<bool>(image, "_isSvg"));
            Assert.Equal(acceptedIntrinsic, Field<Size>(image, "_svgIntrinsicSize"));
            Assert.Equal(acceptedTitle, Field<string?>(image, "_svgTitle"));
            Assert.Equal(acceptedDescription, Field<string?>(image, "_svgDesc"));
            Assert.Equal(acceptedContentHash, Field<string?>(image, "_svgContentHash"));
            Assert.Equal(acceptedPartitionHash, Field<string?>(image, "_svgSecurityPartitionHash"));
            Assert.Equal(acceptedInfo, Field<object?>(image, "_svgInfo"));
            Assert.Same(acceptedSourceIdentity, Field<object?>(image, "_svgSourceIdentity"));
            Assert.Equal(acceptedSourceByteLength, Field<long>(image, "_svgSourceByteLength"));
            Assert.Equal(acceptedRendererIdentity, Field<long>(image, "_svgRendererIdentity"));
            Assert.Equal(acceptedProviderGeneration, Field<long>(image, "_svgProviderCacheGeneration"));
            Assert.Equal(acceptedWidth, Field<int>(image, "_svgBitmapWidthPixels"));
            Assert.Equal(acceptedHeight, Field<int>(image, "_svgBitmapHeightPixels"));
            Assert.Equal(acceptedGeneration, Field<int>(image, "_svgRenderGeneration"));
            Assert.Same(acceptedDocument, CurrentDocument(image));
            Assert.Same(acceptedPlan, Plan(image));
            Assert.Same(acceptedDocument, Field<object>(Plan(image)!, "Document"));
            Assert.True(Field<bool>(image, "_svgUsesTiles"));
            Assert.Equal(0, acceptedDocument.DisposeCount);
        }
        finally
        {
            if (cacheEntry is not null)
                _ = InvokePrivate(FieldStatic<object>(typeof(ImageBox), "_svgCache"), "Remove", cacheKey);
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static ImageBox CreateImage(TestSvgRenderer renderer) => new(
        CreateContext(renderer),
        "data:image/svg+xml,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%3E%3C%2Fsvg%3E",
        "Publication commit test");

    private static void TrackDisposalsUnderWorkLock(ImageBox image, params TestSvgDocument[] documents)
    {
        object workLock = Field<object>(image, "_svgWorkSync");
        foreach (TestSvgDocument document in documents)
            document.WorkLock = workLock;
    }

    private static MarkdownLayoutContext CreateContext(TestSvgRenderer renderer)
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
            SvgRenderer = renderer,
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = 1,
        };
    }

    private static void CommitTiled(
        ImageBox image,
        TestSvgRenderer renderer,
        TestSvgDocument document,
        string identity)
    {
        int generation = AdvanceGeneration(image);
        using IDisposable candidateUse = AcquireCandidate(image, document);
        object metadata = CreateMetadata(image, renderer, generation, identity);
        object plan = CreatePlan(
            document,
            identity,
            Width(metadata),
            Height(metadata),
            generation);

        Assert.True(Assert.IsType<bool>(InvokePrivate(
            image,
            "TryCommitSvgPublication",
            metadata,
            plan)));
        Assert.Same(document, CurrentDocument(image));
    }

    private static object RegisterTileFailureOwner(
        ImageBox image,
        object plan,
        (int X, int Y, int Width, int Height) key,
        CancellationTokenSource cancellation)
    {
        Type ownerType = typeof(ImageBox).GetNestedType("SvgTileWorkOwner", BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("Missing ImageBox SVG tile work-owner type.");
        object owner = Activator.CreateInstance(
                ownerType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [plan, cancellation, Field<MarkdownSvgDemandTicket>(plan, "Demand")],
                culture: null)
            ?? throw new Xunit.Sdk.XunitException("Could not create SVG tile work owner.");
        SetField(owner, "State", 1);
        ((IDictionary)Field<object>(image, "_svgTileWorkOwners")).Add(key, owner);
        return owner;
    }

    private static void RemoveTileFailureOwner(
        ImageBox image,
        (int X, int Y, int Width, int Height) key)
    {
        ((IDictionary)Field<object>(image, "_svgTilesInFlight")).Remove(key);
        ((IDictionary)Field<object>(image, "_svgTileWorkOwners")).Remove(key);
    }

    private static (Action Publish, Action? OnDropped) CreateTiledPublication(
        ImageBox image,
        TestSvgRenderer renderer,
        TestSvgDocument document,
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
        object publication = InvokePrivate(
            image,
            "CreateTiledSvgPublication",
            metadata,
            document,
            candidateUse,
            0L)!;
        return Assert.IsType<ValueTuple<Action, Action?>>(publication);
    }

    private static IDisposable AcquireCandidate(ImageBox image, TestSvgDocument document)
    {
        object?[] args = [document, null];
        object? lease = InvokePrivate(image, "TryAcquireCandidateSvgDocumentUse", args);
        Assert.False(Assert.IsType<bool>(args[1]));
        return Assert.IsAssignableFrom<IDisposable>(lease);
    }

    private static IDisposable AcquireCurrentUse(ImageBox image, TestSvgDocument document) =>
        Assert.IsAssignableFrom<IDisposable>(InvokePrivate(
            image,
            "TryAcquireSvgDocumentUse",
            document));

    private static object CreateMetadata(
        ImageBox image,
        TestSvgRenderer renderer,
        int generation,
        string identity,
        bool metadataWasKnown = false,
        Size? intrinsic = null)
    {
        byte[] source = [1, 2, 3, 4];
        Size intrinsicSize = intrinsic ?? new Size(32, 16);
        long rendererIdentity = Assert.IsType<long>(InvokePrivateStatic(
            "GetSvgRendererIdentity",
            renderer));
        var info = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: intrinsicSize.Width,
            IntrinsicHeightDips: intrinsicSize.Height,
            IntrinsicAspectRatio: intrinsicSize.Width / intrinsicSize.Height,
            Description: "SVG publication test");
        return InvokePrivateStatic(
            "CreateSvgPublicationMetadata",
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
            null)!;
    }

    private static object CreatePlan(
        TestSvgDocument document,
        string identity,
        int width,
        int height,
        int generation)
    {
        Type type = typeof(ImageBox).GetNestedType(
            "SvgTileRenderPlan",
            BindingFlags.Public | BindingFlags.NonPublic)!;
        ConstructorInfo constructor = type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        return constructor.Invoke([document, identity, width, height, generation, null, null, 0L, null, null]);
    }

    private static int AdvanceGeneration(ImageBox image) =>
        Assert.IsType<int>(InvokePrivate(image, "AdvanceSvgRenderGeneration"));

    private static bool IsCurrentTilePlan(ImageBox image, object plan) =>
        Assert.IsType<bool>(InvokePrivate(image, "IsCurrentSvgTilePlan", plan));

    private static object? CurrentDocument(ImageBox image) => Field<object?>(image, "_svgDocument");

    private static object? Plan(ImageBox image) => Field<object?>(image, "_svgTileRenderPlan");

    private static object DocumentUseState(ImageBox image, TestSvgDocument document)
    {
        object table = Field<object>(image, "_svgDocumentUses")!;
        MethodInfo tryGetValue = table.GetType().GetMethod("TryGetValue")!;
        object?[] args = [document, null];
        Assert.True(Assert.IsType<bool>(tryGetValue.Invoke(table, args)));
        return Assert.IsAssignableFrom<object>(args[1]);
    }

    private static int Width(object metadata) => Field<int>(metadata, "TargetWidthPixels");

    private static int Height(object metadata) => Field<int>(metadata, "TargetHeightPixels");

    private static T Field<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        return (T)field.GetValue(instance)!;
    }

    private static T FieldStatic<T>(Type declaringType, string name)
    {
        FieldInfo field = declaringType.GetField(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(declaringType.FullName, name);
        return (T)field.GetValue(null)!;
    }

    private static void SetField(object instance, string name, object value)
    {
        FieldInfo field = instance.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        field.SetValue(instance, value);
    }

    private static object? InvokePrivate(object target, string name, params object?[] args)
    {
        MethodInfo method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, args);
    }

    private static object? InvokePrivateStatic(string name, params object?[] args)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageBox).FullName, name);
        return method.Invoke(null, args);
    }

    private sealed class TestSvgRenderer : IMarkdownSvgRenderer
    {
        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Publication commit tests do not open provider documents.");
    }

    private sealed class TestSvgDocument : IMarkdownSvgDocument
    {
        private int _disposeCount;
        private int _disposeWhileWorkLockHeld;

        public MarkdownSvgDocumentInfo Info { get; } = new(
            IntrinsicWidthDips: 32,
            IntrinsicHeightDips: 16,
            IntrinsicAspectRatio: 2);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal object? WorkLock { get; set; }

        internal bool DisposeWhileWorkLockHeld => Volatile.Read(ref _disposeWhileWorkLockHeld) != 0;

        internal TaskCompletionSource DisposeCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Publication commit tests do not render SVGs.");

        public void Dispose()
        {
            if (WorkLock is { } workLock && Monitor.IsEntered(workLock))
                Interlocked.Exchange(ref _disposeWhileWorkLockHeld, 1);
            Interlocked.Increment(ref _disposeCount);
            DisposeCompleted.TrySetResult();
        }
    }
}
