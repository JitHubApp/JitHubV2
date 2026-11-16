using System;
using System.Threading;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// Publishes an image box's load failures: the failure placeholder, the
/// remembered transport, decode and SVG failures, the host's unavailable
/// report, and SVG generation invalidations that send the box back to
/// loading.
/// </summary>
/// <remarks>
/// Every published error placeholder reaches the host once, including a
/// decoder failure after successful resolution and a cached failure. An
/// earlier policy- or SVG-specific reason is kept and never reported twice.
/// A failure while a previous good image remains visible keeps that image
/// and records only the reason.
/// </remarks>
internal sealed class ImageFailurePublisher
{
    private readonly ImageBox _box;
    private readonly ImageWorkLifetime _lifetime;
    private readonly ImageLoadState _load;
    private readonly SvgImageState _svg;
    private readonly ImagePlaceholderLayout _placeholder;

    internal ImageFailurePublisher(
        ImageBox box,
        ImageWorkLifetime lifetime,
        ImageLoadState load,
        SvgImageState svg,
        ImagePlaceholderLayout placeholder)
    {
        _box = box;
        _lifetime = lifetime;
        _load = load;
        _svg = svg;
        _placeholder = placeholder;
    }

    /// <summary>Reports the shown failure to the host unless this load already reported one.</summary>
    internal void ReportUnreportedFailure()
    {
        if (_load.IsFailed && !_lifetime.IsDisposed && !_lifetime.IsSuspended &&
            _load.TryClaimUnavailableReport())
        {
            _box.Context.ImageUnavailable?.Invoke(_box.Url, MarkdownImageUnavailableReason.Unavailable, _svg.FailureReason);
        }
    }

    /// <summary>Reports <paramref name="reason"/> to the host while the box is live.</summary>
    internal void ReportUnavailable(
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason = null)
    {
        if (!_lifetime.IsDisposed && !_lifetime.IsSuspended && reason != MarkdownImageUnavailableReason.None)
        {
            _load.MarkUnavailableReported();
            _box.Context.ImageUnavailable?.Invoke(_box.Url, reason, svgFailureReason);
        }
    }

    /// <summary>
    /// Shows the failure placeholder on the UI thread; a permanent failure is
    /// remembered under <paramref name="cacheKey"/> and may be carried.
    /// </summary>
    internal void PublishFailure(
        string cacheKey,
        bool cachePermanently = true)
    {
        _box.PublishOnUiThread(() =>
        {
            if (_lifetime.IsDisposed) return;
            _load.IsFailed = true;
            _load.IsFailureCarryable = cachePermanently;
            if (cachePermanently && !string.IsNullOrEmpty(cacheKey))
            {
                _box.Resources.Failures.RememberFailedUrl(cacheKey);
            }

            // Block images and explicitly-sized inline images preserve their geometry. A
            // compact inline image without requested dimensions expands once to show its alt
            // text; the host relayouts the committed snapshot and does not recreate or retry it.
            _placeholder.UpdateForFailure();
            _box.RaiseLoadCompleted(
                layoutInvalidated: _placeholder.ShouldExpandInlineFailure ||
                    _placeholder.ShouldCollapseFailedDecorativeImage);
        });
    }

    /// <summary>Shows a remembered SVG data-URI failure synchronously.</summary>
    internal void PresentSvgDataUriFailure()
    {
        if (_lifetime.IsDisposed) return;
        _load.IsFailed = true;
        _load.IsFailureCarryable = true;
        string cacheKey = _load.ActiveCacheKey ?? _box.Url;
        _box.Resources.Failures.RememberFailedUrl(cacheKey);
        _box.RaiseLoadCompleted(layoutInvalidated: true);
    }

    /// <inheritdoc cref="PublishSvgFailure(MarkdownSvgFailureReason, string, string, bool, int)"/>
    internal void PublishSvgFailure(
        MarkdownSvgException exception,
        string failureCacheKey,
        bool cachePermanently,
        int renderGeneration)
    {
        PublishSvgFailure(
            exception.Reason,
            exception.Message,
            failureCacheKey,
            cachePermanently,
            renderGeneration);
    }

    /// <summary>
    /// Records an SVG failure of <paramref name="renderGeneration"/> on the
    /// UI thread. Without a previous good image the box fails, and a
    /// permanent failure is remembered under <paramref name="failureCacheKey"/>.
    /// </summary>
    internal void PublishSvgFailure(
        MarkdownSvgFailureReason reason,
        string description,
        string failureCacheKey,
        bool cachePermanently,
        int renderGeneration)
    {
        MarkdownDiagnostics.WriteLine(
            $"[ImageBox] SVG failure {reason}: {description}");
        _box.PublishOnUiThread(() =>
        {
            if (_lifetime.IsDisposed || renderGeneration != _svg.RenderGeneration)
                return;

            bool hasPreviousGoodBitmap = _box.Presentation.Bitmap is not null || _box.SvgTileCache.HasRenderedTile;
            _svg.SetFailure(reason, hasPreviousGoodBitmap ? null : description);
            _load.IsFailed = !hasPreviousGoodBitmap;
            _load.IsFailureCarryable = _load.IsFailed && cachePermanently;
            if (_load.IsFailed && cachePermanently &&
                failureCacheKey.Length is > 0 and <= ImageFailureCache.MaxKeyLength)
            {
                _box.Resources.Failures.RememberSvgFailure(
                    failureCacheKey, reason, SvgSourcePreflight.TrimMetadata(description));
            }

            if (_load.IsFailed)
            {
                ReportUnavailable(MarkdownImageUnavailableReason.Unavailable, reason);
                _placeholder.UpdateForFailure();
            }

            _box.RaiseLoadCompleted(layoutInvalidated: _load.IsFailed);
        });
    }

    /// <summary>
    /// Returns the box to not-started on the UI thread while
    /// <paramref name="renderGeneration"/> is current, so the next paint
    /// reloads it.
    /// </summary>
    internal void PublishSvgGenerationInvalidated(int renderGeneration)
    {
        _box.PublishOnUiThread(() =>
        {
            if (_lifetime.IsDisposed || renderGeneration != _svg.RenderGeneration)
                return;

            _load.IsStarted = false;
            _box.RaiseLoadCompleted(layoutInvalidated: false);
        });
    }

    /// <summary>
    /// Classifies a failed SVG operation: cancellation resets or invalidates
    /// the load, a typed provider failure is published (and remembered when
    /// deterministic), device loss defers the upload, and anything else is a
    /// transient worker failure.
    /// </summary>
    internal void HandleSvgWorkFailure(
        Exception exception, CancellationToken cancellationToken,
        CancellationToken sessionLifetimeToken, string resolvedCacheKey,
        string svgFailureCacheKey, int renderGeneration)
    {
        if (exception is OperationCanceledException ||
            exception is MarkdownSvgException { Reason: MarkdownSvgFailureReason.Canceled })
        {
            // A parent cancellation may precede the linked-token callback.
            // Retirement cannot become a retryable provider-generation change.
            if (cancellationToken.IsCancellationRequested || sessionLifetimeToken.IsCancellationRequested)
            {
                if (!_lifetime.IsDisposed)
                    _load.IsStarted = false;
            }
            else
            {
                PublishSvgGenerationInvalidated(renderGeneration);
            }
            return;
        }
        if (exception is MarkdownSvgException svgException)
        {
            // Target/queue-dependent resource limits must not poison content.
            // Deterministic preflight denials are cached in the Open stage.
            bool deterministic = svgException.Reason == MarkdownSvgFailureReason.UnsupportedContent;
            PublishSvgFailure(svgException, svgFailureCacheKey,
                deterministic && !string.IsNullOrEmpty(svgFailureCacheKey), renderGeneration);
            return;
        }
        if (GraphicsDeviceErrors.IsDeviceLost(exception))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG upload deferred after graphics device loss: {exception.Message}");
            _box.PublishOnUiThread(() =>
            {
                if (_lifetime.IsDisposed || renderGeneration != _svg.RenderGeneration)
                    return;
                _load.IsStarted = false;
                _box.RaiseLoadCompleted(layoutInvalidated: false);
            });
            return;
        }
        PublishSvgFailure(
            new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer failed safely.", exception),
            resolvedCacheKey, cachePermanently: false, renderGeneration: renderGeneration);
    }
}
