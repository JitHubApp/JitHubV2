using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using Windows.Foundation;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Puts an <see cref="ImageBox"/> into an SVG or load state through the
/// owners the box composes: its SVG state, document custody and load state.
/// Each operation is one the box's own publication or load path performs.
/// </summary>
internal static class ImageBoxTestState
{
    /// <summary>
    /// Installs <paramref name="plan"/> as the accepted tiled SVG: its document,
    /// its load demand, its render generation and tile mode, as a committed
    /// tiled publication leaves them.
    /// </summary>
    internal static void AcceptTiledPlan(ImageBox box, SvgTileRenderPlan plan)
    {
        lock (box.Lifetime.Sync)
        {
            box.Svg.SetAcceptedPlanNoLock(plan);
            box.SvgDocuments.SetCurrentNoLock(plan.Document);
        }
        box.LoadState.BeginDemand(plan.Demand);
        AdvanceRenderGenerationTo(box, plan.RenderGeneration);
        lock (box.Lifetime.Sync)
            box.Svg.SetUsesTilesNoLock(true);
    }

    /// <summary>Starts render generations until <paramref name="generation"/> is current.</summary>
    internal static void AdvanceRenderGenerationTo(ImageBox box, int generation)
    {
        if (box.Svg.RenderGeneration > generation)
            throw new InvalidOperationException("Render generations never move backwards.");
        while (box.Svg.RenderGeneration < generation)
            box.Svg.AdvanceRenderGeneration();
    }

    /// <summary>
    /// Applies accepted SVG metadata as a committed publication does. Values
    /// left out stay empty, as on a box that never published.
    /// </summary>
    internal static void AdoptSvgMetadata(
        ImageBox box,
        IMarkdownSvgRenderer renderer,
        long rendererIdentity = 0,
        long providerCacheGeneration = 0,
        MarkdownSvgDocumentInfo? info = null,
        WeakReference<byte[]>? sourceIdentity = null,
        long sourceByteLength = 0,
        Size intrinsic = default,
        string? title = null,
        string? description = null,
        string? contentHash = null,
        string? securityPartitionHash = null,
        int targetWidthPixels = 0,
        int targetHeightPixels = 0)
    {
        var metadata = new SvgPublicationMetadata(
            sourceIdentity!,
            sourceByteLength,
            renderer,
            rendererIdentity,
            providerCacheGeneration,
            box.Svg.RenderGeneration,
            MetadataWasKnown: true,
            ResolvedCacheKey: string.Empty,
            SvgFailureCacheKey: string.Empty,
            intrinsic,
            title,
            description,
            contentHash!,
            securityPartitionHash!,
            info!,
            targetWidthPixels,
            targetHeightPixels,
            BitmapIdentity: string.Empty,
            WorkId: 0,
            PublishRequestedAt: 0,
            TargetPixels: 0);
        lock (box.Lifetime.Sync)
            box.Svg.ApplyPublicationNoLock(metadata);
    }

    /// <summary>
    /// Re-applies the box's accepted SVG metadata with a new title and
    /// description, as a publication that changed only the SVG text would.
    /// Every other accepted value, and the presented bitmap lease, stay.
    /// </summary>
    internal static void ReplaceSvgText(ImageBox box, string? title, string? description)
    {
        SvgImageState svg = box.Svg;
        var metadata = new SvgPublicationMetadata(
            svg.SourceIdentity!,
            svg.SourceByteLength,
            box.Context.SvgRenderer!,
            svg.RendererIdentity,
            svg.ProviderCacheGeneration,
            svg.RenderGeneration,
            MetadataWasKnown: true,
            ResolvedCacheKey: string.Empty,
            SvgFailureCacheKey: string.Empty,
            svg.IntrinsicSize,
            title,
            description,
            svg.ContentHash!,
            svg.SecurityPartitionHash!,
            svg.Info!,
            svg.BitmapWidthPixels,
            svg.BitmapHeightPixels,
            BitmapIdentity: string.Empty,
            WorkId: 0,
            PublishRequestedAt: 0,
            TargetPixels: 0,
            PhysicalViewport: svg.PhysicalViewport);
        lock (box.Lifetime.Sync)
            svg.ApplyPublicationNoLock(metadata);
    }

    /// <summary>Waits until no admitted image work remains.</summary>
    internal static async Task WaitForImageWorkDrainAsync(ImageBox box, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (box.GetWorkState().ActiveWorkCount != 0)
        {
            if (elapsed.Elapsed > timeout)
                throw new TimeoutException("Admitted image work did not drain.");
            await Task.Delay(10);
        }
    }
}
