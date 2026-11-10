using System;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using SvgRasterPlan = (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.Svg.MarkdownSvgPhysicalViewport PhysicalViewport);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// The last stage of a <see cref="SvgSourceOpener"/> open: it plans the raster,
/// releases this open's source ownership to the bounded source cache, reserves
/// a tile owner slot under host admission, and moves every remaining owner
/// into a source-free <see cref="SvgOpenedRenderWork"/>.
/// </summary>
internal sealed class SvgRenderHandoff
{
    private readonly ISvgLoadHost _host;
    private readonly ImageResourceScope _resources;

    internal SvgRenderHandoff(ISvgLoadHost host, ImageResourceScope resources)
    {
        _host = host;
        _resources = resources;
    }

    /// <returns>
    /// The render work, or <see langword="null"/> when host admission denied a
    /// tile owner slot and recorded a capacity deferral on <paramref name="state"/>.
    /// </returns>
    internal SvgOpenedRenderWork? TryHandOff(SvgOpenState state)
    {
        MarkdownSvgDocumentInfo info = state.Info!;
        SvgRasterPlan rasterPlan = _host.PickRasterDimensions(state.Intrinsic);
        int targetWidth = rasterPlan.Width;
        int targetHeight = rasterPlan.Height;
        long targetPixels = (long)targetWidth * targetHeight;
        string bitmapIdentity = _host.BuildSvgBitmapIdentity(
            state.Preflight.ContentHash,
            state.Preflight.SecurityPartitionHash,
            info,
            targetWidth,
            targetHeight,
            state.ColorScheme,
            state.SemanticColor,
            state.RendererIdentity,
            state.ProviderCacheGeneration,
            rasterPlan.PhysicalViewport);
        (WeakReference<byte[]> publicationSourceIdentity, long publicationSourceBytes) =
            ReleaseSourceOwnership(state);

        if (rasterPlan.UsesTiles && !TryReserveTileOwnerSlot(state))
            return null;

        SvgPublicationMetadata metadata = new(
            publicationSourceIdentity,
            publicationSourceBytes,
            state.Renderer,
            state.RendererIdentity,
            state.ProviderCacheGeneration,
            state.RenderGeneration,
            state.MetadataWasKnown,
            state.ResolvedCacheKey,
            state.SvgFailureCacheKey,
            state.Intrinsic,
            state.Preflight.Title,
            state.Description,
            state.Preflight.ContentHash,
            state.Preflight.SecurityPartitionHash,
            info,
            targetWidth,
            targetHeight,
            bitmapIdentity,
            state.SvgWorkId,
            PublishRequestedAt: 0,
            targetPixels,
            state.Demand,
            state.HostAdmission,
            state.SourceReservationBytes,
            state.OwnerSlot,
            rasterPlan.PhysicalViewport);
        return TransferToRenderWork(state, metadata, rasterPlan.UsesTiles);
    }

    // From here only the bounded source cache owns the prepared array.
    // Render waits and completed async tasks must not pin an evicted
    // entry beyond that policy. UI publication uses weak source identity;
    // later bitmap memoization rechecks the exact bounded cache key.
    private static (WeakReference<byte[]> Identity, long Length) ReleaseSourceOwnership(SvgOpenState state)
    {
        var publicationSourceIdentity = new WeakReference<byte[]>(state.RawBytes);
        long publicationSourceBytes = state.RawBytes.LongLength;
        state.SourceEntry = null;
        state.Preparation = null;
        state.OpenRequest = null;
        state.RawBytes = Array.Empty<byte>();
        state.HostPreparedSourceBytes = 0;
        state.HostPreparationLease?.Dispose();
        state.HostPreparationLease = null;
        return (publicationSourceIdentity, publicationSourceBytes);
    }

    /// <returns><see langword="false"/> when host admission denied a tile owner slot.</returns>
    private bool TryReserveTileOwnerSlot(SvgOpenState state)
    {
        if (state.HostAdmission is not { } hostAdmission)
            return true;

        MarkdownSvgRenderDemand demand = state.Demand;
        state.OwnerSlot = _resources.SvgHostAdmission.TryReserveOwnerSlot(
            state.AdmissionDomain!,
            demand,
            out long ownerVersionAtDenial);
        if (state.OwnerSlot is not null)
            return true;

        state.HostCapacityDeferral = new SvgHostCapacityDeferral(
            hostAdmission,
            demand,
            state.LoadDemandGeneration,
            hostAdmission.CapacityVersion,
            state.SourceReservationBytes,
            RequiresOwnerSlot: true,
            OwnerVersionAtDenial: ownerVersionAtDenial,
            DemandVersionAtDenial: demand.Version,
            VisualDemandEpochAtDenial: _host.SvgHostVisualDemandEpoch);
        state.RawBytes = Array.Empty<byte>();
        state.Preparation = null;
        return false;
    }

    // Allocate the entire handoff before relinquishing any owner. It
    // contains no caller bytes/preparation/cache-entry aliases. Pending
    // disposal and consumed render completion are mutually exclusive.
    private SvgOpenedRenderWork TransferToRenderWork(
        SvgOpenState state, SvgPublicationMetadata metadata, bool usesTiles)
    {
        var work = new SvgOpenedRenderWork(
            _host, _resources, metadata, state.ContentCacheKey, state.BuiltInSourceAliasKey, usesTiles,
            state.OpenedDocument!, state.OpenedDocumentUse!, state.OwnerSlot, state.RenderWork,
            state.PreparationCancellation, state.CancellationToken, state.SessionLifetimeToken,
            state.OpenAdmissionEntry, state.ProviderAdmissionHeld);
        state.RenderOwnershipTransferred = true;
        state.OpenedDocument = null;
        state.OpenedDocumentUse = null;
        state.OwnerSlot = null;
        state.PreparationCancellation = null;
        state.OpenAdmissionEntry = null;
        state.ProviderAdmissionHeld = false;
        return work;
    }
}
