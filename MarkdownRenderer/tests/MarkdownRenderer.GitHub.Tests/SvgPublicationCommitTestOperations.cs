using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Image-box SVG publication operations shared by the publication commit tests: document
/// use leases, tile plans, tile-failure owners and the accepted document and plan readers.
/// </summary>
internal static class SvgPublicationCommitTestOperations
{
    internal static void TrackDisposalsUnderWorkLock(ImageBox image, params PublicationCommitSvgDocument[] documents)
    {
        object workLock = image.Lifetime.Sync;
        foreach (PublicationCommitSvgDocument document in documents)
            document.WorkLock = workLock;
    }

    /// <summary>
    /// A delayed callback owner for a retired plan whose work the commit already
    /// detached, so the tile cache no longer maps it.
    /// </summary>
    internal static SvgTileWorkOwner CreateUnmappedTileOwner(
        object retiredPlan,
        RetirableCancellationTokenSource cancellation) =>
        new((SvgTileRenderPlan)retiredPlan, cancellation, ((SvgTileRenderPlan)retiredPlan).Demand)
        {
            State = 1,
        };

    internal static void ReleaseTileWork(
        ImageBox image,
        (int X, int Y, int Width, int Height) key,
        object? owner,
        RetirableCancellationTokenSource work)
    {
        RemoveTileFailureOwner(image, key, owner);
        work.Dispose();
    }

    internal static object RegisterTileFailureOwner(
        ImageBox image,
        object plan,
        (int X, int Y, int Width, int Height) key,
        RetirableCancellationTokenSource cancellation) =>
        SvgTileCacheTestOperations.AdmitWork(
            image.SvgTileCache, (SvgTileRenderPlan)plan, key, cancellation, claimFailure: true);

    internal static void RemoveTileFailureOwner(
        ImageBox image,
        (int X, int Y, int Width, int Height) key,
        object? owner)
    {
        if (owner is SvgTileWorkOwner workOwner)
            image.SvgTileCache.AbandonUnscheduledTileWork(key, workOwner);
    }

    internal static IDisposable AcquireCandidate(ImageBox image, PublicationCommitSvgDocument document)
    {
        SvgDocumentUseLease? lease = image.SvgDocuments.TryAcquireCandidateUse(
            document, out bool ownershipConflict);
        Assert.False(ownershipConflict);
        return Assert.IsAssignableFrom<IDisposable>(lease);
    }

    internal static IDisposable AcquireCurrentUse(ImageBox image, PublicationCommitSvgDocument document) =>
        Assert.IsAssignableFrom<IDisposable>(image.SvgDocuments.TryAcquireUse(document));

    internal static object CreatePlan(
        PublicationCommitSvgDocument document,
        string identity,
        int width,
        int height,
        int generation)
    {
        return new SvgTileRenderPlan(document, identity, width, height, generation);
    }

    internal static int AdvanceGeneration(ImageBox image) => image.Svg.AdvanceRenderGeneration();

    internal static bool IsCurrentTilePlan(ImageBox image, object plan) =>
        image.SvgTileCache.IsCurrentSvgTilePlan((SvgTileRenderPlan)plan);

    internal static object? CurrentDocument(ImageBox image) => image.SvgDocuments.Current;

    internal static object? Plan(ImageBox image) => image.Svg.AcceptedPlan;

    internal static SvgDocumentUseState DocumentUseState(ImageBox image, PublicationCommitSvgDocument document)
    {
        lock (image.Lifetime.Sync)
            return image.SvgDocuments.GetUseStateNoLock(document);
    }

    internal static int Width(object metadata) => ((SvgPublicationMetadata)metadata).TargetWidthPixels;

    internal static int Height(object metadata) => ((SvgPublicationMetadata)metadata).TargetHeightPixels;
}
