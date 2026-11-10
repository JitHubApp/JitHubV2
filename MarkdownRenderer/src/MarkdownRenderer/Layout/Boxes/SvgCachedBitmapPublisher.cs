using System;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using SvgRasterPlan = (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.MarkdownSvgPhysicalViewport PhysicalViewport);
using CachedSvgBitmap = (MarkdownRenderer.Images.IMarkdownSvgRenderer Renderer, long RendererIdentity, long ProviderCacheGeneration, Windows.Foundation.Size Intrinsic, (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.MarkdownSvgPhysicalViewport PhysicalViewport) Plan, string Identity);

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Publishes an SVG an image box can show without rendering: an exact
/// device-bitmap cache hit for the accepted SVG or a current source-cache
/// entry, and decides whether a cache entry or the accepted SVG still match
/// the configured renderer.
/// </summary>
/// <remarks>
/// A whole-image cache hit is a publication too: it must retire a prior
/// tiled document and its plan after a resize rather than display new
/// dimensions over the old tile grid. Admitted metadata stays staged until
/// the bitmap publication commits, so a cache miss or a dropped callback
/// leaves the previous image and its intrinsic dimensions and accessibility
/// metadata coherent.
/// </remarks>
internal sealed class SvgCachedBitmapPublisher
{
    private readonly ImageBox _box;
    private readonly SvgImageState _svg;

    internal SvgCachedBitmapPublisher(ImageBox box, SvgImageState svg)
    {
        _box = box;
        _svg = svg;
    }

    /// <summary>
    /// True while the renderer of a cache entry is the configured renderer
    /// and, for text-bearing SVGs, at the entry's cache generation.
    /// </summary>
    internal bool IsCacheEntryCurrent(SvgCacheEntry entry)
    {
        try
        {
            return _box.Context.SvgRenderer is { } renderer &&
                   entry.RendererIdentity == _box.Resources.ProviderIdentities.GetSvgRendererIdentity(renderer) &&
                   (!entry.Info.HasText ||
                    entry.ProviderCacheGeneration == renderer.CacheGeneration);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True while the accepted SVG's renderer identity and generation are current.</summary>
    internal bool IsCurrentRendererState() =>
        _svg.IsRendererCurrent(_box.Context.SvgRenderer, _box.Resources.ProviderIdentities);

    /// <summary>
    /// True while <paramref name="renderer"/> is the configured renderer and,
    /// when the SVG depends on fonts, at <paramref name="expectedGeneration"/>.
    /// </summary>
    internal bool IsProviderCurrent(
        IMarkdownSvgRenderer renderer,
        long expectedGeneration,
        bool dependsOnFontGeneration)
    {
        try
        {
            return ReferenceEquals(_box.Context.SvgRenderer, renderer) &&
                   (!dependsOnFontGeneration || renderer.CacheGeneration == expectedGeneration);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Publishes the content-cache entry <paramref name="contentCacheKey"/>
    /// through an exact bitmap cache hit, and aliases a built-in source to it.
    /// </summary>
    internal bool TryPublishContentCached(
        string contentCacheKey,
        string resolvedCacheKey,
        string? builtInSourceAliasKey,
        bool layoutInvalidated,
        int renderGeneration,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        if (!_box.Resources.SvgSources.TryGetValue(contentCacheKey, out SvgCacheEntry? entry) ||
            !IsCacheEntryCurrent(entry))
        {
            return false;
        }

        if (!TryPublish(layoutInvalidated, entry, renderGeneration, resolvedCacheKey, bitmapOutputWork))
            return false;

        if (!string.IsNullOrEmpty(builtInSourceAliasKey))
            _box.Resources.SvgSources.Set(builtInSourceAliasKey, entry);
        return true;
    }

    /// <summary>
    /// Publishes the device bitmap cached for <paramref name="sourceEntry"/>,
    /// or for the accepted SVG when it is null, at the current raster plan.
    /// False on a miss or when the plan needs tiles. Content-cache followers
    /// pass <paramref name="expectedRenderGeneration"/>: they belong to an
    /// existing request and must not advance its epoch past a newer resize or
    /// theme request admitted while they awaited work.
    /// </summary>
    internal bool TryPublish(
        bool layoutInvalidated,
        SvgCacheEntry? sourceEntry = null,
        int? expectedRenderGeneration = null,
        string? resolvedCacheKey = null,
        BitmapOutputWorkContext? expectedWork = null)
    {
        if ((sourceEntry?.Info ?? _svg.Info) is not { } info ||
            (sourceEntry?.ContentHash ?? _svg.ContentHash) is not { } contentHash ||
            (sourceEntry?.SecurityPartitionHash ?? _svg.SecurityPartitionHash) is not { } partitionHash)
        {
            return false;
        }

        SharedCanvasBitmapCache.Lease? unclaimedLease = null;
        string publicationCacheKey = resolvedCacheKey ?? _box.LoadState.ActiveCacheKey ?? _box.Url;
        try
        {
            if (!TryAcquireCachedBitmap(
                    sourceEntry, info, contentHash, partitionHash, expectedWork,
                    out CachedSvgBitmap cached, out unclaimedLease) ||
                unclaimedLease is null)
            {
                return false;
            }

            int generation = expectedRenderGeneration ?? _svg.AdvanceRenderGeneration();
            SvgPublicationMetadata metadata = CreateMetadata(
                sourceEntry, cached, generation, layoutInvalidated, publicationCacheKey,
                info, contentHash, partitionHash);
            var publication = _box.SvgPublication.CreateBitmapPublication(metadata, unclaimedLease);
            unclaimedLease = null;
            try
            {
                _box.PublishOnUiThread(publication.Publish, publication.OnDropped);
            }
            catch
            {
                publication.OnDropped?.Invoke();
                throw;
            }
            return true;
        }
        catch (MarkdownSvgException ex)
        {
            _box.FailurePublisher.PublishSvgFailure(
                ex,
                publicationCacheKey,
                cachePermanently: false,
                renderGeneration: expectedRenderGeneration ?? _svg.RenderGeneration);
            return true;
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] ignored an SVG cache hit after device loss: {ex.Message}");
            return false;
        }
        finally
        {
            if (unclaimedLease is not null)
                _box.Lifetime.TryCleanup(unclaimedLease.Dispose);
        }
    }

    // Describes a cache-hit publication: the source and text come from
    // sourceEntry when given, else from the accepted SVG.
    private SvgPublicationMetadata CreateMetadata(
        SvgCacheEntry? sourceEntry,
        CachedSvgBitmap cached,
        int generation,
        bool layoutInvalidated,
        string publicationCacheKey,
        MarkdownSvgDocumentInfo info,
        string contentHash,
        string partitionHash) => new(
            sourceEntry is null
                ? _svg.SourceIdentity ?? new WeakReference<byte[]>(Array.Empty<byte>())
                : new WeakReference<byte[]>(sourceEntry.RawBytes),
            sourceEntry?.RawBytes.LongLength ?? _svg.SourceByteLength,
            cached.Renderer,
            cached.RendererIdentity,
            cached.ProviderCacheGeneration,
            generation,
            !layoutInvalidated,
            publicationCacheKey,
            publicationCacheKey,
            cached.Intrinsic,
            sourceEntry is null ? _svg.Title : sourceEntry.Title,
            sourceEntry is null ? _svg.Description : sourceEntry.Desc,
            contentHash,
            partitionHash,
            info,
            cached.Plan.Width,
            cached.Plan.Height,
            cached.Identity,
            0,
            0,
            (long)cached.Plan.Width * cached.Plan.Height,
            PhysicalViewport: cached.Plan.PhysicalViewport);

    // Acquires the admitted device bitmap of the current whole-bitmap plan.
    // The caller describes the publication once the lease is held.
    private bool TryAcquireCachedBitmap(
        SvgCacheEntry? sourceEntry,
        MarkdownSvgDocumentInfo info,
        string contentHash,
        string partitionHash,
        BitmapOutputWorkContext? expectedWork,
        out CachedSvgBitmap cached,
        out SharedCanvasBitmapCache.Lease? lease)
    {
        cached = default;
        lease = null;
        SvgRasterPlanner planner = _box.SvgRasterPlanner;
        MarkdownSvgColor semanticColor = planner.GetSemanticColor();
        MarkdownSvgColorScheme colorScheme = planner.GetColorScheme();
        IMarkdownSvgRenderer? renderer = _box.Context.SvgRenderer;
        if (renderer is null)
            return false;
        long rendererIdentity = _box.Resources.ProviderIdentities.GetSvgRendererIdentity(renderer);
        long providerCacheGeneration = renderer.CacheGeneration;
        if ((sourceEntry?.RendererIdentity ?? _svg.RendererIdentity) != rendererIdentity ||
            (info.HasText &&
             (sourceEntry?.ProviderCacheGeneration ?? _svg.ProviderCacheGeneration) != providerCacheGeneration))
        {
            return false;
        }
        Size intrinsic = sourceEntry?.Intrinsic ?? _svg.IntrinsicSize;
        SvgRasterPlan plan = planner.PickRasterDimensions(intrinsic);
        if (plan.UsesTiles)
            return false;

        string identity = planner.BuildBitmapIdentity(
            contentHash, partitionHash, info, plan.Width, plan.Height, colorScheme, semanticColor,
            rendererIdentity, providerCacheGeneration, plan.PhysicalViewport);
        if (!_box.OutputAdmission.TryAcquireAdmittedSvgBitmap(
                _box.Context.ResourceCreator.Device,
                identity,
                out lease,
                expectedWork: expectedWork) ||
            lease is null)
        {
            return false;
        }

        cached = (renderer, rendererIdentity, providerCacheGeneration, intrinsic, plan, identity);
        return true;
    }
}
