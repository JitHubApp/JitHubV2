using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Images.Internal.Svg;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using MarkdownRenderer.Layout.Internal.Inlines.SpeculativeMeasurement;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal.Residency;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// Renders a markdown image. The bitmap is loaded asynchronously and the box
/// self-invalidates the canvas once decoding completes. Until then, the alt
/// text is shown as a placeholder. When alt text is non-empty it is also
/// rendered as a caption below the image.
///
/// SVG content is admitted by the host resource preflight and then opened and
/// rasterized by the optional engine-neutral <see cref="IMarkdownSvgRenderer"/>.
/// The premultiplied raster is uploaded off the UI thread and shared through
/// device-scoped bitmap leases. SVG text remains inside the provider render so
/// transforms, clipping, bidi, and text paths are preserved.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because the composition root keeps the
/// constructor graph, the clone constructors, disposal ordering and every
/// forwarder of the box's existing internal API in one place; no behavior is
/// implemented here. The forwarders go once callers reach the owners directly.
///
/// The box is a composition root. Its state lives in the work lifetime, load
/// state, presentation, SVG state and document custody; its behavior lives
/// in the owners built over them: load activation and source loading,
/// measurement, paint, placeholder and failure publication, SVG publication
/// and tiles, residency, and private placements. Construction order matches
/// the order in which the box used to create its resources.
/// </remarks>
internal sealed class ImageBox : BlockBox, ISelectionBackgroundGeometrySource, IImageMeasureHost,
    IImageWorkLifetimeHost, IMarkdownImageResidencyTarget
{
    internal const int SvgTileSizePixels = SvgTileCache.SvgTileSizePixels;

    internal readonly MarkdownLayoutContext Context;
    internal readonly string Url;
    public readonly string Alt;

    private readonly ImageLoadActivation _activation;
    private readonly SvgTileHost _tileHost;
    private readonly ImageSourceLoadHost _loadHost;
    private readonly ImageMeasureCustody _measure;
    private readonly ImageWorkLifetime _lifetime;
    private readonly ImageLoadState _load;
    private readonly ImagePresentation _presentation;
    private readonly SvgDocumentCustody _documents;
    private readonly SvgImageState _svg;
    private readonly ImageUiPublisher _uiPublisher;
    private readonly ImagePlaceholderLayout _placeholder;
    private readonly ImageLayoutMeasurer _measurer;
    private readonly ImagePainter _painter;
    private readonly ImageCompletionReport _completion;
    private readonly ImageResidency _residency;
    private readonly ImagePlacementCloner _cloner;

    /// <summary>Raised when the asset finishes loading and a repaint is required.
    /// The event arg's <see cref="LoadCompletedEventArgs.LayoutInvalidated"/>
    /// indicates whether the host must re-run layout (intrinsic size may have
    /// changed) or merely repaint. Always raised on the UI thread.</summary>
    public event EventHandler<LoadCompletedEventArgs>? LoadCompleted;

    // Only a wake-up signal. The control reconciles on its dispatcher under
    // the snapshot read lease; this callback must never detach paint resources.
    public event EventHandler? ResidencyWorkDrained;

    public ImageBox(
        MarkdownLayoutContext context, ImageResourceScope resources,
        string url,
        string alt,
        SafeHtmlLength? requestedWidth = null,
        SafeHtmlLength? requestedHeight = null)
        : this(context, resources, url, alt, requestedWidth, requestedHeight, context.RenderResources.NativeRetirement)
    {
    }

    internal ImageBox(
        MarkdownLayoutContext context, ImageResourceScope resources,
        string url,
        string alt,
        NativeResourceRetirement nativeRetirement,
        Action<CanvasTextLayout>? nativeLayoutOptionsSetter = null)
        : this(context, resources, url, alt, null, null, nativeRetirement, nativeLayoutOptionsSetter)
    {
    }

    internal ImageBox(
        MarkdownLayoutContext context, ImageResourceScope resources,
        string url,
        string alt,
        SafeHtmlLength? requestedWidth,
        SafeHtmlLength? requestedHeight,
        NativeResourceRetirement nativeRetirement,
        Action<CanvasTextLayout>? nativeLayoutOptionsSetter = null)
        : this(
            ValidateNativeCustody(context, nativeRetirement),
            resources ?? throw new ArgumentNullException(nameof(resources)),
            url ?? string.Empty,
            alt ?? string.Empty,
            requestedWidth,
            requestedHeight,
            box => new ImageMeasureCustody(box, context, nativeRetirement, nativeLayoutOptionsSetter))
    {
        BuiltInSource = MarkdownBuiltInImageSourcePolicy.TryResolve(
            Url,
            Context.ImageBaseUri,
            out MarkdownBuiltInImageSource builtInSource)
            ? builtInSource
            : null;
        _load.IsSvg = SvgIntrinsics.LooksLikeSvg(Url) ||
                      SvgIntrinsics.LooksLikeSvg(BuiltInSource?.Uri.AbsoluteUri);
        Margin = new Thickness(0, 6, 0, 6);
        if (string.IsNullOrEmpty(Url))
        {
            _load.IsStarted = true;
            _load.IsFailed = true;
            return;
        }

        PreResolutionCacheKey = ImageLoadActivation.GetPreResolutionCacheKey(Context, Url, BuiltInSource);
        _load.ActiveCacheKey = PreResolutionCacheKey;
        _activation.AdoptCachedResult(PreResolutionCacheKey);
    }

    // A never-started placement descriptor of source for a candidate layout.
    private ImageBox(MarkdownLayoutContext context, ImageBox source)
        : this(context, source.Resources, source.Url, source.Alt, source.RequestedWidth, source.RequestedHeight,
            box => source._measure.CreateForClone(box, context))
    {
        try
        {
            // These are immutable request descriptors, not resource state.
            // Data-URI payloads are rejected by the caller so this does not
            // retain or duplicate an embedded source buffer.
            BuiltInSource = source.BuiltInSource;
            PreResolutionCacheKey = source.PreResolutionCacheKey;
            _load.ActiveCacheKey = source.PreResolutionCacheKey;
            _load.IsSvg = source._load.IsSvg; // a URI hint only; accepted SVG state is not copied
            _load.CarriedCompletion = source._load.CarriedCompletion; // immutable scalars
            _measure.IsInlineLayout = source._measure.IsInlineLayout;
            _load.IsStarted = false;
            _load.IsFailed = false;
            CopyPlacementInputsFrom(source);
        }
        catch (Exception constructionFailure)
        {
            RetireFailedClone(constructionFailure, "MarkdownRenderer.PrivateIdleImageCloneCleanupFailure");
            throw;
        }
    }

    // A completed placement of source behind the caller's independent lease.
    private ImageBox(
        MarkdownLayoutContext context,
        ImageBox source,
        SharedCanvasBitmapCache.Lease bitmapLease,
        CompletedSvgPlacementMetadataFence? svgMetadataFence)
        : this(context, source.Resources, source.Url, source.Alt, source.RequestedWidth, source.RequestedHeight,
            box => source._measure.CreateForClone(box, context))
    {
        try
        {
            BuiltInSource = source.BuiltInSource;
            PreResolutionCacheKey = svgMetadataFence is { } svgFenceForKey
                ? svgFenceForKey.PreResolutionCacheKey
                : source.PreResolutionCacheKey;
            _load.ActiveCacheKey = svgMetadataFence is { } svgFence
                ? svgFence.ActiveCacheKey
                : source._load.ActiveCacheKey;
            _measure.IsInlineLayout = source._measure.IsInlineLayout;
            _load.IsStarted = true;
            CopyPlacementInputsFrom(source);
            // Descriptor capture runs under the source snapshot gate. The
            // source already resolved these intrinsic DIPs when its bitmap
            // was published; never query Win2D Size here for an uncached or
            // legacy lease whose optional IntrinsicSize metadata is absent.
            _presentation.AdoptPlacement(
                bitmapLease,
                source._presentation.RasterIntrinsicSize,
                source._presentation.HasPresentedImage,
                svgMetadataFence?.BitmapPhysicalViewport);
            _load.CarriedCompletion = source._load.CarriedCompletion;

            if (svgMetadataFence is { } acceptedSvg)
            {
                _load.IsSvg = true;
                _svg.AdoptPlacementFence(acceptedSvg);
                _load.AdoptDemandGeneration(acceptedSvg.LoadDemandGeneration);
                _load.IsFailed = false;
            }
        }
        catch (Exception constructionFailure)
        {
            // The caller keeps the bitmap lease until construction succeeds.
            // Retire only this partially-created object's linked lifetime.
            RetireFailedClone(constructionFailure, "MarkdownRenderer.PrivateRasterImageCloneCleanupFailure");
            throw;
        }
    }

    // Composes the owners in the order the box creates its resources.
    // Host adapters hold only the box; every other owner gets its state.
    private ImageBox(
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        string url,
        string alt,
        SafeHtmlLength? requestedWidth,
        SafeHtmlLength? requestedHeight,
        Func<ImageBox, ImageMeasureCustody> createMeasure)
    {
        Context = context;
        Resources = resources;
        _activation = new ImageLoadActivation(this);
        _tileHost = new SvgTileHost(this);
        _loadHost = new ImageSourceLoadHost(this);
        _measure = createMeasure(this);
        SvgHostDeferral = new SvgHostLoadDeferral(_activation, resources);
        SvgTileCache = new SvgTileCache(_tileHost);
        Url = url;
        Alt = alt;
        RequestedWidth = requestedWidth;
        RequestedHeight = requestedHeight;
        _lifetime = new ImageWorkLifetime(
            this,
            context,
            RetirableCancellationTokenSource.CreateLinked(context.ImageCancellationToken));
        _load = new ImageLoadState();
        _presentation = new ImagePresentation(_lifetime, _load);
        _documents = new SvgDocumentCustody(_lifetime);
        _svg = new SvgImageState(_lifetime, _documents);
        (OutputDeferral, OutputAdmission) = resources.CreateBitmapOutput(
            _activation, context, _lifetime.LifetimeCancellation);
        SourceLoader = new ImageSourceLoader(
            _loadHost, context, resources, OutputAdmission, OutputDeferral, SvgHostDeferral, Url,
            RequestedWidth, RequestedHeight);
        SvgTileRenderer = new SvgTileRenderer(_tileHost, SvgTileCache, resources, OutputDeferral, OutputAdmission);
        _uiPublisher = new ImageUiPublisher(_lifetime, context);
        _placeholder = new ImagePlaceholderLayout(this, _measure, _load);
        FailurePublisher = new ImageFailurePublisher(this, _lifetime, _load, _svg, _placeholder);
        SvgRasterPlanner = new SvgRasterPlanner(this);
        SvgCachedPublisher = new SvgCachedBitmapPublisher(this, _svg);
        SvgPublication = new SvgPublicationCommit(this, _lifetime, _load, _svg, _documents);
        _measurer = new ImageLayoutMeasurer(this, _measure, _load, _presentation, _svg, _placeholder);
        _painter = new ImagePainter(this, _measure, _presentation, _svg, _placeholder);
        _completion = new ImageCompletionReport(this, _load, _presentation, _svg);
        _residency = new ImageResidency(this, _lifetime, _load, _svg, _documents);
        PlacementEligibility = new ImagePlacementEligibility(this, _lifetime, _load, _presentation, _svg);
        MeasuredPlacement = new ImageMeasuredPlacement(this, _measure);
        _cloner = new ImagePlacementCloner(this, PlacementEligibility);
    }

    private static MarkdownLayoutContext ValidateNativeCustody(
        MarkdownLayoutContext context,
        NativeResourceRetirement nativeRetirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nativeRetirement);
        return context;
    }

    private void CopyPlacementInputsFrom(ImageBox source)
    {
        ContentAlignment = source.ContentAlignment;
        MinimumParagraphLineHeight = source.MinimumParagraphLineHeight;
        BlockIndex = source.BlockIndex;
        SafeHtmlInlineFlowScopeId = source.SafeHtmlInlineFlowScopeId;
        CopyPlacementMarginsFrom(source);
    }

    private void RetireFailedClone(Exception constructionFailure, string diagnosticKey)
    {
        try
        {
            _lifetime.LifetimeCancellation.Dispose();
        }
        catch (Exception cleanupFailure)
        {
            try
            {
                constructionFailure.Data[diagnosticKey] = cleanupFailure;
            }
            catch
            {
                // Cleanup diagnostics must not mask construction failure.
            }
        }
    }

    internal static ImageBox CreateIdlePlacement(MarkdownLayoutContext context, ImageBox source) =>
        new(context, source);

    internal static ImageBox CreateCompletedPlacement(
        MarkdownLayoutContext context,
        ImageBox source,
        SharedCanvasBitmapCache.Lease bitmapLease,
        CompletedSvgPlacementMetadataFence? svgMetadataFence) =>
        new(context, source, bitmapLease, svgMetadataFence);

    // Composed state and behavior, for the box's owners.
    internal ImageResourceScope Resources { get; }
    internal ImageWorkLifetime Lifetime => _lifetime;
    internal ImageLoadState LoadState => _load;
    internal ImagePresentation Presentation => _presentation;
    internal SvgImageState Svg => _svg;
    internal SvgDocumentCustody SvgDocuments => _documents;
    internal ImageMeasureCustody MeasureCustody => _measure;
    internal SafeHtmlLength? RequestedWidth { get; }
    internal SafeHtmlLength? RequestedHeight { get; }
    internal MarkdownBuiltInImageSource? BuiltInSource { get; }
    internal string? PreResolutionCacheKey { get; }
    internal ImageBitmapOutputDeferral OutputDeferral { get; }
    internal ImageBitmapOutputAdmission OutputAdmission { get; }
    internal SvgHostLoadDeferral SvgHostDeferral { get; }
    internal SvgTileCache SvgTileCache { get; }
    internal SvgTileRenderer SvgTileRenderer { get; }
    internal ImageSourceLoader SourceLoader { get; }
    internal ImageSourceLoadHost SourceLoadHost => _loadHost;
    internal SvgTileHost TileHost => _tileHost;
    internal ImageLoadActivation Activation => _activation;
    internal ImageFailurePublisher FailurePublisher { get; }
    internal ImagePlaceholderLayout Placeholder => _placeholder;
    internal ImagePainter Painter => _painter;
    internal SvgRasterPlanner SvgRasterPlanner { get; }
    internal SvgCachedBitmapPublisher SvgCachedPublisher { get; }
    internal SvgPublicationCommit SvgPublication { get; }
    internal ImagePlacementEligibility PlacementEligibility { get; }
    internal ImageMeasuredPlacement MeasuredPlacement { get; }

    public CanvasHorizontalAlignment ContentAlignment { get; set; } = CanvasHorizontalAlignment.Left;

    // A promoted Markdown image still participates in its paragraph's text
    // line box. Kept zero for standalone/HTML inline images, whose owner lays
    // out the line itself.
    internal float MinimumParagraphLineHeight { get; set; }

    internal float ParagraphLineHeight
        => float.IsFinite(MinimumParagraphLineHeight) &&
           MinimumParagraphLineHeight is > 0f and <= 1_000_000f
            ? MinimumParagraphLineHeight
            : 0f;

    /// <summary>
    /// True when the active representation is SVG. Before host resolution completes, this may
    /// temporarily reflect a source-URI hint; resolved bytes and MIME metadata supersede it.
    /// </summary>
    public bool IsSvg => _load.IsSvg;

    /// <summary>Test-only: returns the cached bitmap, if any.</summary>
    public CanvasBitmap? Bitmap => _presentation.Bitmap;

    /// <summary>SVG &lt;title&gt; element value, or null. Used by the automation
    /// peer as the accessible name when richer than alt.</summary>
    public string? SvgTitle => _completion.SvgTitle;

    /// <summary>SVG &lt;desc&gt; element value, or null. Used by the automation
    /// peer as <c>HelpText</c> for screen-reader description.</summary>
    public string? SvgDesc => _completion.SvgDescription;

    /// <summary>Typed provider failure for diagnostics and focused tests.</summary>
    internal MarkdownSvgFailureReason? SvgFailureReason => _svg.FailureReason;

    /// <summary>Physical SVG raster dimensions currently presented by this box.</summary>
    internal (int Width, int Height) SvgRasterPixelSize => (_svg.BitmapWidthPixels, _svg.BitmapHeightPixels);

    /// <summary>True when this box is using bounded visible SVG tiles.</summary>
    internal bool UsesSvgTilesForAutomation => _svg.UsesTiles;

    internal bool HasBitmapForAutomation => _presentation.Bitmap is not null;

    /// <summary>Current state projected into the image's UIA accessible name.</summary>
    internal MarkdownImageAccessibilityState AccessibilityState => _completion.AccessibilityState;

    /// <summary>
    /// True while a visible image cannot show its pixels yet: it is still
    /// loading, or it carries an earlier layout's completed result
    /// and has not presented in this layout. Readiness checks of visible
    /// content use this, never the carried Loaded state alone.
    /// </summary>
    internal bool IsAwaitingVisiblePixelsForAutomation => _completion.IsAwaitingVisiblePixels;

    internal bool IsFailedDecorativeImageCollapsed => _placeholder.ShouldCollapseFailedDecorativeImage;

    internal Rect AutomationRenderDestination => _painter.AutomationRenderDestination;

    /// <summary>Test-only: height of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageHeight => _measure.ImageHeight;

    /// <summary>Test-only: width of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageWidth => _measure.ImageWidth;

    /// <summary>Test-only: height of the caption area at last measure (excludes margins).</summary>
    public float MeasuredCaptionHeight => _measure.CaptionHeight;

    /// <summary>One read of the admitted image work, under the image-work lock.</summary>
    internal ImageWorkState GetWorkState() => _lifetime.GetWorkState();

    /// <inheritdoc cref="ImageWorkLifetime.RetirementTask"/>
    internal Task RetirementTask => _lifetime.RetirementTask;

    internal SharedCanvasBitmapCache.Lease? AcquireBitmapLease() => _presentation.AcquireLease();

    /// <inheritdoc cref="ImageBitmapOutputDeferral.NeedsBitmapOutputRecovery"/>
    internal bool NeedsBitmapOutputRecovery => OutputDeferral.NeedsBitmapOutputRecovery;

    /// <inheritdoc cref="ImageBitmapOutputDeferral.ObserveDeferredBitmapOutput"/>
    internal bool ObserveBitmapOutputRecovery(bool isVisible) => OutputDeferral.ObserveDeferredBitmapOutput(isVisible);

    /// <inheritdoc cref="ImageCompletionReport.TryCaptureCompletedCarry"/>
    internal bool TryCaptureCompletedCarry(out CompletedImageCarry? carry) =>
        _completion.TryCaptureCompletedCarry(out carry);

    /// <summary>
    /// Triggers the network/disk load for this image if it has not already
    /// started. Called by the renderer when the box enters the viewport.
    /// </summary>
    public void EnsureLoading()
    {
        EnsureLoading(isVisible: true);
    }

    internal void EnsureLoading(bool isVisible) => _activation.EnsureLoading(isVisible);

    /// <inheritdoc cref="ImageUiPublisher.Publish"/>
    internal void PublishOnUiThread(
        Action publish,
        Action? onDropped = null,
        Func<Action, bool>? tryEnqueueOverride = null) =>
        _uiPublisher.Publish(publish, onDropped, tryEnqueueOverride);

    internal void RaiseLoadCompleted(bool layoutInvalidated)
    {
        FailurePublisher.ReportUnreportedFailure();
        LoadCompleted?.Invoke(this, LoadCompletedEventArgs.For(layoutInvalidated));
    }

    internal void RaiseBitmapOutputRecoverySignal() =>
        LoadCompleted?.Invoke(this, LoadCompletedEventArgs.ForBitmapOutputRecoverySignal());

    // Measurement.
    public override float Measure(float availableWidth) => _measurer.Measure(availableWidth);

    internal InlineMeasureCheckpoint BeginInlineCheckpoint()
    {
        lock (_measure.Sync)
        {
            if (_lifetime.IsDisposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            return new InlineMeasureCheckpoint(this, _measure.OpenCheckpointNoLock());
        }
    }

    internal void FinishInlineCheckpoint(
        InlineMeasureCheckpoint checkpoint,
        bool commit)
    {
        lock (_measure.Sync)
        {
            if (!_measure.CheckpointActive ||
                !checkpoint.BelongsTo(this, _measure.CheckpointGeneration))
            {
                return;
            }

            _measure.FinishCheckpointNoLock(checkpoint, commit);
        }
    }

    /// <summary>
    /// Measures the image as an inline atomic cell. Inline images share the
    /// same loading/cache pipeline as block images, but they do not render
    /// captions or margins and reserve a compact placeholder before loading.
    /// </summary>
    internal Size MeasureInline(float availableWidth, float lineHeight)
    {
        lock (_measure.Sync)
        {
            if (_lifetime.IsDisposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            if (_measure.CheckpointActive)
                throw new InvalidOperationException("An inline image measure requires its active checkpoint.");
            return _measurer.MeasureInlineNoLock(availableWidth, lineHeight);
        }
    }

    internal Size MeasureInline(
        float availableWidth,
        float lineHeight,
        InlineMeasureCheckpoint checkpoint)
    {
        lock (_measure.Sync)
        {
            if (_lifetime.IsDisposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            if (!_measure.CheckpointActive ||
                !checkpoint.BelongsTo(this, _measure.CheckpointGeneration))
            {
                throw new InvalidOperationException("The inline image measure checkpoint is no longer active.");
            }
            return _measurer.MeasureInlineNoLock(availableWidth, lineHeight);
        }
    }

    /// <inheritdoc cref="ImageLayoutMeasurer.SetInlineBounds"/>
    internal void SetInlineBounds(Rect rect) => _measurer.SetInlineBounds(rect);

    /// <inheritdoc cref="ImageLayoutMeasurer.HasExactReservedRasterGeometry"/>
    internal bool HasExactReservedRasterGeometry(Size intrinsicSize) =>
        _measurer.HasExactReservedRasterGeometry(intrinsicSize);

    /// <inheritdoc cref="ImageLayoutMeasurer.TryCanUsePaintOnlyCompletion"/>
    internal bool TryCanUsePaintOnlyCompletion() => _measurer.TryCanUsePaintOnlyCompletion();

    /// <summary>Commits a measured rectangle for this box's owners.</summary>
    internal void SetMeasuredBounds(Rect bounds) => Bounds = bounds;

    /// <summary>True when both boxes resolve the same raw and collapsed placement margins.</summary>
    internal bool HasSamePlacementMargins(ImageBox other) =>
        RawPlacementMargin.Equals(other.RawPlacementMargin) &&
        CollapsedTopMargin == other.CollapsedTopMargin &&
        SuppressTopMargin == other.SuppressTopMargin &&
        SuppressBottomMargin == other.SuppressBottomMargin;

    // Paint, hit testing and selection.
    public override void Paint(CanvasDrawingSession ds, Rect viewport) => _painter.Paint(ds, viewport);

    /// <inheritdoc cref="ImagePainter.PaintInline"/>
    internal void PaintInline(CanvasDrawingSession ds, Rect rect, Rect viewport) =>
        _painter.PaintInline(ds, rect, viewport);

    internal void PaintInlineSelectionForeground(CanvasDrawingSession ds, Rect rect, Rect viewport, Color selectionForeground) =>
        _painter.PaintInlineSelectionForeground(ds, rect, viewport, selectionForeground);

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    internal override bool HitTestSelectionEndpoint(Point point, out DocumentPosition position) =>
        _painter.HitTestSelectionEndpoint(point, out position);

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        if (_painter.SelectionIntersectsAtomicSlot(range))
            yield return Bounds;
    }

    public override void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Color color, Rect viewport) =>
        _painter.PaintSelectionForeground(ds, range, color, viewport);

    /// <inheritdoc cref="ImagePainter.GetSelectionBackgroundGeometry"/>
    public IEnumerable<SelectionBackgroundGeometry> GetSelectionBackgroundGeometry(DocumentRange range) =>
        _painter.GetSelectionBackgroundGeometry(range);

    // Residency.
    public void SuspendResources() => _residency.SuspendResources();

    public bool TryReleaseSuspendedResources() => _residency.TryReleaseSuspendedResources();

    public bool TryResumeResources() => _residency.TryResumeResources();

    internal object? CaptureSvgViewportObservationToken() => _svg.AcceptedPlan;

    internal bool ObserveSvgViewportVisibility(object? expectedPlanToken, bool visible) =>
        SvgTileCache.ObserveSvgViewportVisibility(expectedPlanToken, visible);

    internal bool TryGetSvgPressureParkTier(SvgTileRenderPlan plan, long demandVersion, out int tier) =>
        _residency.TryGetSvgPressureParkTier(plan, demandVersion, out tier);

    internal bool TryParkSvgDocument(SvgTileRenderPlan plan, long demandVersion, int maximumTier) =>
        _residency.TryParkSvgDocument(plan, demandVersion, maximumTier);

    // Private placements for candidate layouts.
    internal bool TryCreateCompletedRasterPlacementClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease) =>
        _cloner.TryCreateCompletedRasterClone(targetContext, out placement, out expectedSourceLease);

    /// <inheritdoc cref="ImagePlacementCloner.TryCreateCompletedClone"/>
    internal bool TryCreateCompletedRasterPlacementClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence) =>
        _cloner.TryCreateCompletedClone(targetContext, out placement, out expectedSourceLease, out svgMetadataFence);

    /// <inheritdoc cref="ImagePlacementCloner.TryCreatePrivateClone"/>
    internal bool TryCreatePrivateImagePlacementClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration) =>
        _cloner.TryCreatePrivateClone(
            targetContext, out placement, out expectedSourceLease, out svgMetadataFence,
            out isIdleDescriptor, out expectedIdleLoadDemandGeneration);

    /// <inheritdoc cref="ImagePlacementCloner.TryClassifyPrivate"/>
    internal bool TryClassifyPrivateImagePlacement(
        MarkdownLayoutContext targetContext,
        bool allowIdleImagePlacements,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration) =>
        _cloner.TryClassifyPrivate(
            targetContext, allowIdleImagePlacements, out isIdleDescriptor, out expectedIdleLoadDemandGeneration);

    /// <inheritdoc cref="ImagePlacementEligibility.IsCurrentIdlePlacement"/>
    internal bool IsCurrentIdleImagePlacement(int expectedLoadDemandGeneration) =>
        PlacementEligibility.IsCurrentIdlePlacement(expectedLoadDemandGeneration);

    /// <inheritdoc cref="ImagePlacementEligibility.IsCurrentCompletedRaster"/>
    internal bool IsCurrentCompletedRasterPlacement(SharedCanvasBitmapCache.Lease expectedSourceLease) =>
        PlacementEligibility.IsCurrentCompletedRaster(expectedSourceLease);

    /// <inheritdoc cref="ImagePlacementEligibility.IsCurrentCompletedPlacement"/>
    internal bool IsCurrentCompletedRasterPlacement(
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? svgMetadataFence,
        long currentProviderCacheGeneration) =>
        PlacementEligibility.IsCurrentCompletedPlacement(
            expectedSourceLease, svgMetadataFence, currentProviderCacheGeneration);

    // Measured placement transfer.
    internal bool TryGetCompletedInlinePlacementWidth(out float availableWidth) =>
        MeasuredPlacement.TryGetCompletedInlinePlacementState(out availableWidth, out _, out _);

    /// <inheritdoc cref="ImageMeasuredPlacement.TryGetCompletedInlinePlacementState"/>
    internal bool TryGetCompletedInlinePlacementState(
        out float availableWidth,
        out float imageWidth,
        out float imageHeight) =>
        MeasuredPlacement.TryGetCompletedInlinePlacementState(out availableWidth, out imageWidth, out imageHeight);

    internal bool HasNativePlacementLayoutAlias(CanvasTextLayout? candidate) =>
        MeasuredPlacement.HasNativePlacementLayoutAlias(candidate);

    internal bool TryAddNativePlacementOwners(
        HashSet<CanvasTextLayout> layoutOwners,
        HashSet<NativeResourceRetirementRecord> retirementOwners) =>
        MeasuredPlacement.TryAddNativePlacementOwners(layoutOwners, retirementOwners);

    internal bool TryCaptureNativeLayoutPlacementFence(out NativeLayoutPlacementFence fence) =>
        MeasuredPlacement.TryCaptureNativeLayoutPlacementFence(out fence);

    /// <summary>
    /// Adopts the privately measured placement of <paramref name="prepared"/>;
    /// a batch of one through <see cref="TryTransferMeasuredPlacementsFromCompletedClones"/>.
    /// </summary>
    internal bool TryTransferMeasuredPlacementFromCompletedClone(
        ImageBox prepared,
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? expectedSvgMetadataFence,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expectedSourceLease);
        var transfer = new ImageMeasuredPlacementTransfer(
            this, prepared, expectedSourceLease, expectedSvgMetadataFence);
        return ImageMeasuredPlacementBatch.TryTransfer(
            MemoryMarshal.CreateReadOnlySpan(ref transfer, 1),
            currentProviderCacheGeneration,
            expectedAvailableWidth);
    }

    /// <inheritdoc cref="ImageMeasuredPlacementBatch.TryTransfer"/>
    internal static bool TryTransferMeasuredPlacementsFromCompletedClones(
        ReadOnlySpan<ImageMeasuredPlacementTransfer> transfers,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth) =>
        ImageMeasuredPlacementBatch.TryTransfer(transfers, currentProviderCacheGeneration, expectedAvailableWidth);

    // Retirement.
    public override void Dispose()
    {
        if (!_lifetime.TryBeginDispose(out System.Threading.Tasks.TaskCompletionSource? retirementCompletion))
            return;

        SvgHostDeferral.ClearSvgHostDeferral();
        OutputDeferral.ClearBitmapOutputDeferral();
        _measure.InvalidateInlineCheckpointForDisposal();

        _svg.AdvanceRenderGeneration();
        Task cancellation = _lifetime.CancelLifetimeAsync();
        _ = _lifetime.RetireAsync(cancellation, retirementCompletion!);
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        Dispose();
        Task retirement = RetirementTask;
        if (!retirement.IsCompleted || retirement.IsFaulted || retirement.IsCanceled)
            pendingTasks.Add(retirement);
    }

    bool IImageMeasureHost.IsDisposed => _lifetime.IsDisposed;

    void IImageMeasureHost.RestoreBounds(Rect bounds) => Bounds = bounds;

    void IImageWorkLifetimeHost.OnImageWorkDrained()
    {
        OutputDeferral.ArmDeferredBitmapOutputRetryAfterWorkDrained();
        if (_lifetime.IsSuspended && !_lifetime.IsDisposed)
        {
            try { ResidencyWorkDrained?.Invoke(this, EventArgs.Empty); }
            catch (Exception exception) { _lifetime.RecordFailure(exception); }
        }
    }

    void IImageWorkLifetimeHost.ReleaseRetiredResources(Action<Exception> addFailure)
    {
        IMarkdownSvgDocument? terminalDocument;
        SvgHostOwnerSlot? terminalOwnerSlot;
        lock (_lifetime.Sync)
        {
            terminalDocument = _documents.Current;
            _documents.SetCurrentNoLock(null);
            terminalOwnerSlot = _svg.AcceptedPlan?.OwnerSlot;
            _svg.SetAcceptedPlanNoLock(null);
        }
        SvgHostDeferral.ClearSvgHostDeferral();
        OutputDeferral.ClearBitmapOutputDeferral();
        terminalOwnerSlot?.Dispose();
        if (terminalDocument is not null)
        {
            try { terminalDocument.Dispose(); }
            catch (Exception exception) { addFailure(exception); }
        }
        try { SvgPublication.ReleaseSvgTiles(); } catch (Exception exception) { addFailure(exception); }
        try { _presentation.Release(); } catch (Exception exception) { addFailure(exception); }
        try { _measure.RetireNativeLayoutsForDisposal(addFailure); }
        catch (Exception exception) { addFailure(exception); }
        LoadCompleted = null;
    }

    // The snapshot owner retires boxes only after its paint readers drain,
    // and every admitted/late image operation has drained before this is
    // called. Clear only box-owned source/cache identity references: never
    // mutate arrays shared with the SVG cache, and retain all UIA-facing
    // and user-facing metadata.
    void IImageWorkLifetimeHost.ClearRetiredStateNoLock()
    {
        _svg.ClearRetiredSourceStateNoLock();
        _load.ActiveCacheKey = null;
        _documents.ClearUsesNoLock();
        ResidencyWorkDrained = null;
    }
}
