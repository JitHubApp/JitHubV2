using System;
using System.Globalization;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal.Raster;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Internal.Images.Raster;

/// <summary>
/// Plans one raster load for <see cref="ImageRasterLoader"/>: validates the
/// encoded budget, checks the portable provider's format support, chooses the
/// decode path and its output geometry, and derives the bitmap cache identity.
/// A rejected plan has already published its failure through the host.
/// </summary>
internal sealed class RasterLoadPlanner
{
    private readonly IImageRasterLoadHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly SafeHtmlLength? _requestedWidth;
    private readonly SafeHtmlLength? _requestedHeight;

    internal RasterLoadPlanner(
        IImageRasterLoadHost host,
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        SafeHtmlLength? requestedWidth,
        SafeHtmlLength? requestedHeight)
    {
        _host = host;
        _context = context;
        _resources = resources;
        _requestedWidth = requestedWidth;
        _requestedHeight = requestedHeight;
    }

    internal bool TryPlan(
        byte[] bytes, string cacheKey, CancellationToken imageCancellationToken, out RasterLoadPlan plan)
    {
        plan = default;
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(bytes);
        if (!budget.Accepted && !budget.CanRenderStaticPreview)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] raster rejected before CanvasBitmap decode for {cacheKey}: {budget.Reason}");
            _host.ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
            _host.PublishFailure(cacheKey);
            return false;
        }
        string failureFormatKey = budget.Format ?? string.Empty;

        MarkdownPerformanceOptions? rasterOptions = _context.PerformanceSession?.Options;
        bool displaySized = rasterOptions?.UseDisplaySizedRasterDecode == true;
        bool sessionPixelCapApplies = _context.PerformanceSession is { } performanceSession &&
            (long)budget.Width * budget.Height > performanceSession.Options.MaxRasterOutputPixels;
        bool composeGifCanvas = budget.FirstFrame is { } gifFrame &&
            (gifFrame.Left != 0 || gifFrame.Top != 0 ||
             gifFrame.Width != budget.Width || gifFrame.Height != budget.Height);
        IMarkdownRasterDecoder? rasterDecoder = _context.RasterDecoder;
        long rasterDecoderIdentity = rasterDecoder is null ? 0 : _resources.ProviderIdentities.GetRasterDecoderIdentity(rasterDecoder);
        if (!TryCheckProviderFormat(budget, cacheKey, rasterDecoder,
                out bool rasterProviderSupportsFormat, out MarkdownRasterFormat rasterFormat))
            return false;

        plan = PlanDecodeGeometry(bytes, new RasterLoadPlan
        {
            Budget = budget,
            FailureFormatKey = failureFormatKey,
            Options = rasterOptions,
            DisplaySized = displaySized,
            SessionPixelCapApplies = sessionPixelCapApplies,
            ComposeGifCanvas = composeGifCanvas,
            RasterDecoder = rasterDecoder,
            RasterFormat = rasterFormat,
        }, rasterProviderSupportsFormat, imageCancellationToken);
        long rasterDecodeFailureIdentity = plan.PortableRasterDecode ? rasterDecoderIdentity : 0;
        if (_resources.Failures.HasFailedRasterDecode(cacheKey, failureFormatKey, rasterDecodeFailureIdentity))
        {
            _host.PublishFailure(cacheKey: string.Empty, cachePermanently: false);
            return false;
        }

        (string cacheFamily, string bitmapCacheKey) = GetBitmapCacheIdentity(cacheKey, plan);
        Size intrinsicSize = new(budget.Width, budget.Height);
        if (plan.NativeDecode || plan.PortableRasterDecode)
            intrinsicSize = new Size(plan.NativePlan.IntrinsicWidth, plan.NativePlan.IntrinsicHeight);
        plan = plan with
        {
            RasterDecodeFailureIdentity = rasterDecodeFailureIdentity,
            CacheFamily = cacheFamily,
            BitmapCacheKey = bitmapCacheKey,
            IntrinsicSize = intrinsicSize,
        };
        return true;
    }

    private bool TryCheckProviderFormat(
        RasterImageBudgetResult budget,
        string cacheKey,
        IMarkdownRasterDecoder? rasterDecoder,
        out bool rasterProviderSupportsFormat,
        out MarkdownRasterFormat rasterFormat)
    {
        rasterProviderSupportsFormat = false;
        rasterFormat = default;
        bool hasStaticProviderFormat = budget.FrameCount == 1 && budget.FirstFrame is null &&
            TryGetMarkdownRasterFormat(budget.Format, out rasterFormat);
        if (hasStaticProviderFormat && rasterDecoder is not null)
        {
            try
            {
                rasterProviderSupportsFormat = rasterDecoder.SupportsFormat(rasterFormat);
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[ImageBox] raster decoder capability check failed for {cacheKey}: {exception.GetType().Name}.");
                _host.ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
                _host.PublishFailure(cacheKey: string.Empty);
                return false;
            }
        }

        return true;
    }

    private RasterLoadPlan PlanDecodeGeometry(
        byte[] bytes,
        RasterLoadPlan plan,
        bool rasterProviderSupportsFormat,
        CancellationToken imageCancellationToken)
    {
        RasterImageBudgetResult budget = plan.Budget;
        bool boundedBitmapOutput = plan.Options?.ReleaseOffscreenImageResources == true;
        bool composeGifCanvas = plan.ComposeGifCanvas;
        // A registered portable provider receives exact physical display
        // geometry even when display-sized previews are not enabled for other
        // image formats. Formats without a valid static plan stay on the
        // existing platform decode path.
        bool legacySizingPolicyApplies = plan.DisplaySized || budget.CanRenderStaticPreview || plan.SessionPixelCapApplies;
        bool sizingPolicyApplies = legacySizingPolicyApplies ||
            rasterProviderSupportsFormat;
        // Participating consumers always use WIC's explicit bounded transform.
        // The shared budget reserves both the GIF canvas and its temporary
        // frame; legacy consumers retain their existing eager decode behavior.
        bool transformedDecode = sizingPolicyApplies || composeGifCanvas || boundedBitmapOutput;
        long maximumOutputPixels = GetMaximumOutputPixels(plan, boundedBitmapOutput, composeGifCanvas);
        var sizingRequest = new RasterImageSizingRequest(
            sizingPolicyApplies ? _host.RasterSizingAvailableWidth : budget.Width,
            sizingPolicyApplies ? _context.RasterizationScale : 1d,
            sizingPolicyApplies ? _requestedWidth : null,
            sizingPolicyApplies ? _requestedHeight : null,
            maximumOutputPixels);
        (int Width, int Height) rasterSize = transformedDecode
            ? sizingRequest.GetSize(budget.Width, budget.Height)
            : default;
        RasterImageDecodePlan nativePlan = default;
        bool nativePlanAvailable = transformedDecode &&
            RasterImageDecodePlan.TryCreate(bytes, budget, sizingRequest, out nativePlan, imageCancellationToken);
        bool portableRasterDecode = rasterProviderSupportsFormat && nativePlanAvailable;
        // Capability alone must not alter legacy behavior. If an otherwise
        // eligible format cannot produce the host's validated static plan,
        // retry planning with the caller's pre-existing transform policy and
        // leave decode selection to the established platform path.
        if (rasterProviderSupportsFormat && !portableRasterDecode && !legacySizingPolicyApplies)
        {
            transformedDecode = composeGifCanvas || boundedBitmapOutput;
            sizingRequest = new RasterImageSizingRequest(
                budget.Width,
                1d,
                null,
                null,
                maximumOutputPixels);
            rasterSize = transformedDecode
                ? sizingRequest.GetSize(budget.Width, budget.Height)
                : default;
            nativePlan = default;
            nativePlanAvailable = transformedDecode &&
                RasterImageDecodePlan.TryCreate(bytes, budget, sizingRequest, out nativePlan, imageCancellationToken);
        }
        bool nativeDecode = nativePlanAvailable && !portableRasterDecode &&
            RasterNativeWicDecoder.IsQualifiedPlan(nativePlan);
        if (nativeDecode || portableRasterDecode)
            rasterSize = (nativePlan.OutputWidth, nativePlan.OutputHeight);
        RasterGifFirstFrameDecodePlan nativeGifPlan = default;
        bool nativeGifDecode = transformedDecode && budget.Format == "GIF" &&
            RasterGifFirstFrameDecodePlan.TryCreate(bytes, budget, sizingRequest,
                out nativeGifPlan, imageCancellationToken);
        if (nativeGifDecode)
            rasterSize = (nativeGifPlan.OutputWidth, nativeGifPlan.OutputHeight);
        return plan with
        {
            SizingRequest = sizingRequest,
            TransformedDecode = transformedDecode,
            RasterSize = rasterSize,
            NativePlan = nativePlan,
            PortableRasterDecode = portableRasterDecode,
            NativeDecode = nativeDecode,
            NativeGifPlan = nativeGifPlan,
            NativeGifDecode = nativeGifDecode,
        };
    }

    // The per-frame output ceiling, narrowed by the options' raster cap and,
    // for bounded bitmap output, by the largest single output the shared
    // budget admits (a composed GIF canvas also holds its temporary frame).
    private long GetMaximumOutputPixels(RasterLoadPlan plan, bool boundedBitmapOutput, bool composeGifCanvas)
    {
        long maximumOutputPixels = Math.Min(RasterImageResourceBudget.MaxPixelsPerFrame,
            plan.Options?.MaxRasterOutputPixels ?? RasterImageResourceBudget.MaxPixelsPerFrame);
        if (boundedBitmapOutput)
            maximumOutputPixels = Math.Min(maximumOutputPixels,
                _resources.BitmapOutput.MaximumSingleOutputBytes / (composeGifCanvas ? 8 : 4));
        return maximumOutputPixels;
    }

    private (string CacheFamily, string BitmapCacheKey) GetBitmapCacheIdentity(
        string cacheKey, RasterLoadPlan plan)
    {
        // A partial GIF is the full logical canvas, not the WIC first-frame
        // bitmap. Partition it before the sizing grammar so old raw entries
        // and preview candidates cannot substitute incompatible geometry.
        string rasterSourceIdentity = plan.ComposeGifCanvas && cacheKey.Length > 0
            ? cacheKey + "\u001Fgif-canvas-v1" : cacheKey;
        // Owned WIC variants use final orientation, unlike older provisional
        // raw-axis variants. Never substitute those cache entries on promotion.
        if (plan.NativeDecode && rasterSourceIdentity.Length > 0)
            rasterSourceIdentity += RasterNativeWicDecoder.CachePartition;
        if (plan.NativeGifDecode && rasterSourceIdentity.Length > 0)
            rasterSourceIdentity += RasterNativeWicDecoder.GifCachePartition;
        if (plan.PortableRasterDecode && rasterSourceIdentity.Length > 0 && plan.RasterDecoder is { } rasterDecoder)
        {
            rasterSourceIdentity += "\u001Fraster-decoder-v1:" +
                _resources.ProviderIdentities.GetRasterDecoderIdentity(rasterDecoder).ToString(CultureInfo.InvariantCulture);
        }
        string cacheFamily = plan.TransformedDecode && cacheKey.Length > 0
            ? plan.SizingRequest.GetCacheFamily(rasterSourceIdentity, plan.DisplaySized) : cacheKey;
        string bitmapCacheKey = plan.TransformedDecode && cacheKey.Length > 0
            ? plan.SizingRequest.GetCacheKey(cacheFamily, plan.RasterSize)
            : cacheKey;
        return (cacheFamily, bitmapCacheKey);
    }

    private static bool TryGetMarkdownRasterFormat(string? format, out MarkdownRasterFormat rasterFormat)
    {
        rasterFormat = format switch
        {
            "PNG" => MarkdownRasterFormat.Png,
            "JPEG" => MarkdownRasterFormat.Jpeg,
            "BMP" => MarkdownRasterFormat.Bmp,
            "WEBP" => MarkdownRasterFormat.WebP,
            _ => default,
        };
        return format is "PNG" or "JPEG" or "BMP" or "WEBP";
    }
}
