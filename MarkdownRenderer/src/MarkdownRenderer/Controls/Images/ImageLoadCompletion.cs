using System;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Threading;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// Routes an image load completion to the work it calls for: a bitmap
/// output retry, a relayout of the image's root, a full rebuild for a
/// sender that is not an image, or a repaint when layout is unaffected.
/// </summary>
internal sealed class ImageLoadCompletion
{
    private readonly IImageLoadCompletionHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly LayoutCommitState _layoutCommit;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImageRelayout _imageRelayout;
    private readonly IMarkdownUiScheduler? _scheduler;

    internal ImageLoadCompletion(
        IImageLoadCompletionHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        LayoutCommitState layoutCommit,
        ImageSubscriptions imageSubscriptions,
        ImageRelayout imageRelayout,
        IMarkdownUiScheduler? scheduler)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _layoutCommit = layoutCommit;
        _imageSubscriptions = imageSubscriptions;
        _imageRelayout = imageRelayout;
        _scheduler = scheduler;
    }

    /// <summary>
    /// Handles a load completion of any subscribed image. An off-thread
    /// sender is handed to the UI thread through weak references only.
    /// </summary>
    internal void OnLoadCompleted(object? sender, LoadCompletedEventArgs e)
    {
        // ImageBox publishes on UI already. Keep that path inline; only custom
        // off-thread senders need a second weak dispatcher handoff.
        var scheduler = _scheduler;
        if (scheduler is null) return;
        bool layoutInvalidated = e?.LayoutInvalidated ?? true;
        bool bitmapOutputRecoverySignal = e?.IsBitmapOutputRecoverySignal ?? false;
        if (scheduler.HasThreadAccess)
        {
            Apply(sender as ImageBox, layoutInvalidated, bitmapOutputRecoverySignal);
            return;
        }
        var receiver = new WeakReference<ImageLoadCompletion>(this);
        var image = sender is ImageBox imageSender
            ? new WeakReference<ImageBox>(imageSender)
            : null;
        bool enqueued = false;
        try
        {
            enqueued = scheduler.TryEnqueue(MarkdownUiPriority.Normal, () =>
            {
                if (!receiver.TryGetTarget(out var completion))
                    return;
                ImageBox? completedImage = null;
                if (image is not null && !image.TryGetTarget(out completedImage))
                    return;
                completion.Apply(completedImage, layoutInvalidated, bitmapOutputRecoverySignal);
            });
        }
        catch (Exception exception)
        {
            RelayoutDiagnostics.ReportFailure("completion dispatch", exception.Message);
        }
        if (!enqueued && !_lifetime.IsDisposed && !_lifetime.IsUnloaded)
            _host.RecordRejectedCrossThreadFullRebuild();
    }

    private void Apply(ImageBox? completedImage, bool layoutInvalidated,
        bool bitmapOutputRecoverySignal = false)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;
        if (_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
            return;
        if (bitmapOutputRecoverySignal)
        {
            if (completedImage is not null)
                _imageSubscriptions.RequestBitmapOutputRecovery(completedImage);
            return;
        }
        if (completedImage is not null)
        {
            if (!_imageSubscriptions.Contains(completedImage))
                return;
            _host.NotifyImageStatusChanged(completedImage);
            // UIA notification can run host code. Recheck lifetime and membership
            // before using a snapshot-owned placement proof or queuing work.
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_imageSubscriptions.Contains(completedImage))
                return;
            if (layoutInvalidated && !_layoutCommit.IsPublishing &&
                _host.PerformanceSession is IMarkdownPerformanceSessionInternal session &&
                !session.IsDisposed && ReferenceEquals(session, completedImage.Context.PerformanceSession) &&
                _published.Snapshot is { } snapshot &&
                snapshot.TryCanUsePaintOnlyImageCompletion(
                    completedImage, _host.LayoutWidth))
            {
                // Authored dimensions can make intrinsic publication irrelevant
                // to native layout. Keep the same UIA status and paint/selection
                // invalidation, without scheduling a no-op text reflow.
                layoutInvalidated = false;
            }
        }
        if (layoutInvalidated)
        {
            if (completedImage is not null)
                _imageRelayout.Queue(completedImage.BlockIndex);
            else
                _host.RequestFullRebuild();
        }
        else
        {
            // Device/theme reparses are paint-only: rebuilding here would
            // dispose the newly parsed SVG and start the reparse loop again.
            _host.InvalidatePaint();
        }
    }
}
