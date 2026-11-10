using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;

/// <summary>
/// The image box as seen by its <see cref="SvgTileRenderer"/>: the renderer
/// and source state a tile render is fenced on, the box's image-work and
/// document-use leases, its native raster and upload paths, and its tile
/// success and failure publication.
/// </summary>
internal interface ISvgTileRendererHost : ISvgTileCacheHost
{
    /// <summary>The source byte length reported with tile render diagnostics.</summary>
    long SvgSourceByteLength { get; }

    /// <summary>The device tile bitmaps are uploaded to.</summary>
    CanvasDevice SvgTileDevice { get; }

    /// <summary>Whether the provider identity and cache generation still match the accepted document.</summary>
    bool IsCurrentSvgRendererState();

    /// <summary>The raster plan for the box's current intrinsic size.</summary>
    (int Width, int Height, bool UsesTiles, MarkdownSvgPhysicalViewport PhysicalViewport) PickCurrentSvgRasterDimensions();

    /// <summary>Reopens a pressure-parked document for visible demand at <paramref name="visualDemandEpoch"/>.</summary>
    void ReopenPressureParkedSvgForVisibleDemand(long visualDemandEpoch);

    /// <summary>Begins image work that the box's retirement waits for.</summary>
    bool TryBeginImageWork(out ImageWorkLease? work);

    /// <summary>Acquires a use of <paramref name="document"/> if it is still the box's document.</summary>
    SvgDocumentUseLease? TryAcquireSvgDocumentUse(IMarkdownSvgDocument document);

    /// <summary>Renders <paramref name="request"/> under a nested use of the document.</summary>
    Task<MarkdownSvgRaster> RenderSvgDocumentAsync(
        IMarkdownSvgDocument document,
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken,
        SvgDocumentUseLease parentUse,
        MarkdownSvgRenderDemand? demand);

    /// <summary>Uploads a validated raster into a bitmap charged to <paramref name="bitmapReservation"/>.</summary>
    CanvasBitmap CreateBitmapFromSvgRaster(
        MarkdownSvgRaster raster,
        long diagnosticWorkId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels,
        BitmapOutputAdmission.Reservation? bitmapReservation);

    /// <summary>Tracks detached work so the box's retirement waits for it.</summary>
    void TrackDetachedImageWork(Task detachedOperation, bool failRetirementOnFault);

    /// <summary>Records a published tile as the box's presented image.</summary>
    void OnSvgTilePublished();

    /// <summary>Publishes a terminal tile failure for <paramref name="workOwner"/>.</summary>
    void PublishSvgTileFailure(
        SvgTileRenderPlan plan,
        (int X, int Y, int Width, int Height) key,
        SvgTileWorkOwner workOwner,
        MarkdownSvgException exception);
}
