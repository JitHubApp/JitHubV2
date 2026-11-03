using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Decodes an image box's admitted raster bytes into a device bitmap: budget
/// validation, the decode plan, cache lookups, preparation admission, the
/// portable-provider, owned-WIC, GIF and WinRT decode paths, and the
/// UI-thread handoff of the completed lease. Presentation state stays with the
/// box, which applies the completion through <see cref="IImageRasterLoadHost"/>.
/// </summary>
/// <remarks>
/// One load runs as named stages. The immutable <see cref="RasterLoadPlan"/>
/// and the mutable <see cref="RasterLoadState"/> (every resource the load
/// holds, released in one place) are passed explicitly between them. A stage
/// that returns <see langword="false"/> ends the load without a publication,
/// exactly where the single method used to return.
/// </remarks>
internal sealed class ImageRasterLoader
{
    private readonly IImageRasterLoadHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly ImageBitmapOutputAdmission _outputAdmission;
    private readonly ImageBitmapOutputDeferral _outputDeferral;
    private readonly RasterLoadPlanner _planner;
    private readonly RasterBitmapDecoder _decoder;

    internal ImageRasterLoader(
        IImageRasterLoadHost host,
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        ImageBitmapOutputAdmission outputAdmission,
        ImageBitmapOutputDeferral outputDeferral,
        SafeHtmlLength? requestedWidth,
        SafeHtmlLength? requestedHeight)
    {
        _host = host;
        _context = context;
        _resources = resources;
        _outputAdmission = outputAdmission;
        _outputDeferral = outputDeferral;
        _planner = new RasterLoadPlanner(host, context, resources, requestedWidth, requestedHeight);
        _decoder = new RasterBitmapDecoder(context, resources, outputAdmission);
    }

    internal Task LoadAsync(
        byte[] bytes, string cacheKey, BitmapOutputWorkContext bitmapOutputWork)
    {
        // A session source-cache hit can complete synchronously inside the
        // viewport's EnsureLoading call. Keep header validation, preview-cache
        // search, WIC decode, and GPU upload off that UI callback even then.
        return _context.Dispatcher?.HasThreadAccess == true
            ? Task.Run(() => LoadCoreAsync(bytes, cacheKey, bitmapOutputWork), CancellationToken.None)
            : LoadCoreAsync(bytes, cacheKey, bitmapOutputWork);
    }

    private async Task LoadCoreAsync(
        byte[] bytes, string cacheKey, BitmapOutputWorkContext bitmapOutputWork)
    {
        if (!_host.TryBeginImageWork(out ImageWorkLease? admittedWork) || admittedWork is null)
            return;
        using ImageWorkLease imageWork = admittedWork;
        CancellationToken imageCancellationToken = imageWork.Token;
        if (!_planner.TryPlan(bytes, cacheKey, imageCancellationToken, out RasterLoadPlan plan))
            return;

        var state = new RasterLoadState(plan, _context.PerformanceSession);
        bool failed = false;
        bool deviceLost = false;
        try
        {
            imageCancellationToken.ThrowIfCancellationRequested();
            CanvasDevice device = _context.ResourceCreator.Device;
            if (!string.IsNullOrEmpty(plan.BitmapCacheKey) &&
                _outputAdmission.TryAcquireRasterBitmap(device, plan.BitmapCacheKey, plan.SizingRequest,
                    plan.TransformedDecode, out state.BitmapLease, bitmapOutputWork))
            {
                // Warm cache hit. No stream, decoder, or GPU allocation.
            }
            else if (!await LoadCacheMissAsync(
                    bytes, cacheKey, plan, state, device, imageCancellationToken, bitmapOutputWork)
                .ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref state.OwnedBitmap, ref state.BitmapReservation);
            _outputAdmission.ReleasePendingRasterOutputs(ref state.BitmapLease, ref state.OwnedBitmap);
            _host.ResetLoadAfterCancellation();
            return;
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] resolved bitmap load deferred after graphics device loss for {cacheKey}: {ex.Message}");
            _outputAdmission.ReleaseAdmittedBitmap(ref state.OwnedBitmap, ref state.BitmapReservation);
            _outputAdmission.ReleasePendingRasterOutputs(ref state.BitmapLease, ref state.OwnedBitmap);
            deviceLost = true;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[ImageBox] resolved bitmap load failed for {cacheKey}: {ex.Message}");
            _outputAdmission.ReleaseAdmittedBitmap(ref state.OwnedBitmap, ref state.BitmapReservation);
            _outputAdmission.ReleasePendingRasterOutputs(ref state.BitmapLease, ref state.OwnedBitmap);
            failed = true;
        }
        finally
        {
            bytes = Array.Empty<byte>();
            ReleaseLoadResources(state);
        }

        PublishLoadCompletion(plan, state, cacheKey, failed, deviceLost);
    }

    private async Task<bool> LoadCacheMissAsync(
        byte[] bytes,
        string cacheKey,
        RasterLoadPlan plan,
        RasterLoadState state,
        CanvasDevice device,
        CancellationToken imageCancellationToken,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        IMarkdownPerformanceSessionInternal? rasterSession = state.Session;
        state.RasterWork = rasterSession?.TryBeginRasterWork();
        if (rasterSession is not null && state.RasterWork is null)
            throw new OperationCanceledException(rasterSession.LifetimeToken);
        using CancellationTokenSource? decodeCancellation = rasterSession is null ? null :
            CancellationTokenSource.CreateLinkedTokenSource(imageCancellationToken, rasterSession.LifetimeToken);
        CancellationToken decodeToken = decodeCancellation?.Token ?? imageCancellationToken;
        decodeToken.ThrowIfCancellationRequested();
        if (!TryPresentCachedPreview(cacheKey, plan, device, bitmapOutputWork))
            return false;

        if (!string.IsNullOrEmpty(plan.BitmapCacheKey))
        {
            state.DecodeGate = _resources.BitmapCache.GetDecodeGate(device, plan.BitmapCacheKey);
            await state.DecodeGate.WaitAsync(decodeToken).ConfigureAwait(false);
            state.DecodeGateHeld = true;

            // Another request may have populated the cache while this
            // request waited on the fixed-size keyed decode gate.
            _ = _outputAdmission.TryAcquireRasterBitmap(device, plan.BitmapCacheKey, plan.SizingRequest,
                plan.TransformedDecode, out state.BitmapLease, bitmapOutputWork);
        }

        if (state.BitmapLease is not null)
            return true;
        if (_outputDeferral.IsBitmapOutputDeferred)
            return false;
        state.DecodeStarted = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
            ? Stopwatch.GetTimestamp()
            : 0;
        long stageStarted = await EnterPreparationAsync(bytes.Length, plan, state, decodeToken)
            .ConfigureAwait(false);
        if (!await _decoder.DecodeAsync(bytes, cacheKey, plan, state, stageStarted, decodeToken, bitmapOutputWork)
                .ConfigureAwait(false))
            return false;

        StoreDecodedBitmap(plan, state, device, decodeToken);
        return true;
    }

    /// <returns><see langword="false"/> when bitmap output is deferred.</returns>
    private bool TryPresentCachedPreview(
        string cacheKey, RasterLoadPlan plan, CanvasDevice device, BitmapOutputWorkContext bitmapOutputWork)
    {
        // A resize or DPI transition may need a larger raster than an
        // image already uploaded for this source. Keep that cached
        // bitmap visible while the exact replacement is decoded. An
        // exact cache hit above never incurs this variant search.
        if (plan.DisplaySized &&
            plan.Options?.UseCachedRasterPreview == true &&
            cacheKey.Length > 0 &&
            (long)plan.RasterSize.Width * plan.RasterSize.Height >= 65_536 &&
            _resources.BitmapCache.TryAcquireBestRasterPreview(
                device,
                plan.CacheFamily,
                plan.SizingRequest,
                out SharedCanvasBitmapCache.Lease? previewLease) &&
            previewLease is not null)
        {
            SharedCanvasBitmapCache.Lease cachedPreview = previewLease;
            if (!_outputAdmission.TryAdmitCachedBitmapOutput(cachedPreview, expectedWork: bitmapOutputWork))
            {
                _host.TryImageCleanup(cachedPreview.Dispose);
                if (_outputDeferral.IsBitmapOutputDeferred)
                    return false;
            }
            else
            {
                _host.PublishOnUiThread(
                    () => _host.PresentRasterPreview(cachedPreview), cachedPreview.Dispose);
            }
        }

        return true;
    }

    /// <returns>The timestamp the first decode stage measures from.</returns>
    private async Task<long> EnterPreparationAsync(
        int sourceLength, RasterLoadPlan plan, RasterLoadState state, CancellationToken decodeToken)
    {
        IMarkdownPerformanceSessionInternal? rasterSession = state.Session;
        RasterImageBudgetResult budget = plan.Budget;
        state.RasterPreparationId = rasterSession is { IsDisposed: false }
            ? rasterSession.BeginRasterPreparation(sourceLength, budget.Width, budget.Height)
            : 0;
        long admissionStarted = state.StartStage();
        state.PreparationSlot = rasterSession is { IsDisposed: false } &&
            _context.EffectiveImagePreparationOwner is { } documentOwner
            ? await rasterSession.EnterCpuPreparationAsync(
                    documentOwner,
                    exclusive: plan.SessionPixelCapApplies || budget.CanRenderStaticPreview,
                    decodeToken)
                .ConfigureAwait(false)
            : null;
        // Keyed deduplication and optional session limits are not
        // aggregate codec limits. All ordinary-raster cache misses
        // share this admission, including empty keys and controls
        // with no performance session. Native decode cannot be
        // canceled by this token: hold the lease through its await,
        // bitmap upload, and output cleanup, never in a callback.
        long processAdmissionWeight = RasterBitmapDecoder.GetProcessAdmissionWeight(sourceLength, plan);
        state.ProcessDecodeSlot = await _resources.Preparation.EnterAsync(
            _context.EffectiveImagePreparationOwner ?? _context,
            processAdmissionWeight,
            exclusive: plan.SessionPixelCapApplies || budget.CanRenderStaticPreview,
            decodeToken).ConfigureAwait(false);
        state.RecordStage(MarkdownRasterPreparationStage.PreparationAdmission, admissionStarted);
        return state.StartStage();
    }

    private void StoreDecodedBitmap(
        RasterLoadPlan plan, RasterLoadState state, CanvasDevice device, CancellationToken decodeToken)
    {
        decodeToken.ThrowIfCancellationRequested();
        CanvasBitmap ownedBitmap = state.OwnedBitmap!;
        var outputSize = ownedBitmap.SizeInPixels;
        if (outputSize.Width == 0 || outputSize.Height == 0 ||
            outputSize.Width > RasterImageResourceBudget.MaxDimension ||
            outputSize.Height > RasterImageResourceBudget.MaxDimension ||
            checked((long)outputSize.Width * outputSize.Height) > plan.SizingRequest.MaximumPixels)
        {
            throw new InvalidDataException("The decoded raster output exceeds the consumer's pixel budget.");
        }
        bool transformedDecode = plan.TransformedDecode;
        if (!string.IsNullOrEmpty(plan.BitmapCacheKey))
        {
            long stageStarted = state.StartStage();
            state.BitmapLease = _resources.BitmapCache.StoreAndAcquire(
                device, plan.BitmapCacheKey, ownedBitmap,
                transformedDecode ? state.IntrinsicSize : ownedBitmap.Size,
                bitmapReservation: state.BitmapReservation);
            state.OwnedBitmap = null; // lease now owns the decoded handle
            state.BitmapReservation = null;
            state.RecordStage(MarkdownRasterPreparationStage.CachePublication, stageStarted);
        }
        else
        {
            // Unkeyed payloads acquire lifetime accounting on the
            // preparation worker too. Publication only swaps the
            // completed lease, without querying native dimensions.
            state.BitmapLease = _resources.BitmapCache.CreateUncachedLease(
                ownedBitmap, checked((int)outputSize.Width), checked((int)outputSize.Height),
                transformedDecode ? state.IntrinsicSize : ownedBitmap.Size,
                bitmapReservation: state.BitmapReservation);
            state.OwnedBitmap = null;
            state.BitmapReservation = null;
        }
        if (state.DecodeStarted != 0)
        {
            MarkdownPerformanceEventSource.Log.ResourceWork(
                2,
                Stopwatch.GetTimestamp() - state.DecodeStarted,
                (long)(transformedDecode ? state.RasterSize.Width : plan.Budget.Width) *
                    (transformedDecode ? state.RasterSize.Height : plan.Budget.Height));
        }
    }

    private void ReleaseLoadResources(RasterLoadState state)
    {
        _outputAdmission.ReleaseAdmittedBitmap(ref state.OwnedBitmap, ref state.BitmapReservation);
        if (state.WicDecodeOwner is not null)
            _host.TryImageCleanup(state.WicDecodeOwner.Retire);
        if (state.ProcessDecodeSlot is not null)
            _host.TryImageCleanup(state.ProcessDecodeSlot.Dispose);
        if (state.DecodeGateHeld)
        {
            SemaphoreSlim decodeGate = state.DecodeGate!;
            _host.TryImageCleanup(() => decodeGate.Release());
        }
        if (state.PreparationSlot is not null)
            _host.TryImageCleanup(state.PreparationSlot.Dispose);
        if (state.RasterWork is not null)
            _host.TryImageCleanup(state.RasterWork.Dispose);
    }

    private void PublishLoadCompletion(
        RasterLoadPlan plan, RasterLoadState state, string cacheKey, bool failed, bool deviceLost)
    {
        SharedCanvasBitmapCache.Lease? publishedLease = state.BitmapLease;
        CanvasBitmap? publishedOwnedBitmap = state.OwnedBitmap;
        Size intrinsicSize = state.IntrinsicSize;
        if (plan.TransformedDecode && publishedLease?.IntrinsicSize is { } cachedIntrinsicSize)
            intrinsicSize = cachedIntrinsicSize;

        var completion = new RasterLoadCompletion(
            publishedLease, publishedOwnedBitmap, intrinsicSize, plan.TransformedDecode, failed, deviceLost,
            state.CacheRasterDecodeFailure, cacheKey, plan.FailureFormatKey, plan.RasterDecodeFailureIdentity);
        _host.PublishOnUiThread(() => _host.PresentRasterLoad(completion),
        onDropped: () =>
        {
            if (publishedLease is { } droppedLease)
                _host.TryImageCleanup(droppedLease.Dispose);
            if (publishedOwnedBitmap is { } droppedBitmap)
                _host.TryImageCleanup(droppedBitmap.Dispose);
        });
    }
}
