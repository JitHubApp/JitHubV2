using System.Collections.Generic;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Recovery;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Images;

namespace MarkdownRenderer.Controls.Internal.Images;

/// <summary>
/// The images the control listens to for load completion, and the retry of
/// those whose bitmap output was denied for lack of capacity. Overflow has
/// no callback when a slot opens, so the recovery clock scans the plans a
/// bounded number at a time until none is pending.
/// </summary>
internal sealed class ImageSubscriptions
{
    private readonly IImageSubscriptionsHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly ImagePlanSet _plans;
    private readonly BitmapOutputRecoveryScan _bitmapOutputRecovery = new();

    private HashSet<ImageBox> _images = new();
    private long _bitmapOutputRecoveryEpoch;

    internal ImageSubscriptions(
        IImageSubscriptionsHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        ImagePlanSet plans)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _plans = plans;
    }

    /// <summary>Raised for a load completion of any subscribed image, on the image's thread.</summary>
    internal event System.EventHandler<LoadCompletedEventArgs>? LoadCompleted;

    /// <summary>The subscribed images.</summary>
    internal IReadOnlySet<ImageBox> Images => _images;

    /// <summary>True while a bitmap output retry scan is pending.</summary>
    internal bool HasPendingBitmapOutputRecovery => _bitmapOutputRecovery.IsPending;

    internal bool Contains(ImageBox image) => _images.Contains(image);

    /// <summary>Adds the block index of every subscribed image to <paramref name="blockIndices"/>.</summary>
    internal void AddBlockIndicesTo(SortedSet<int> blockIndices)
    {
        foreach (ImageBox image in _images)
            blockIndices.Add(image.BlockIndex);
    }

    /// <summary>
    /// Subscribes to <paramref name="image"/>. Returns false when it already
    /// is subscribed.
    /// </summary>
    internal bool Subscribe(ImageBox image)
    {
        if (!_images.Add(image))
            return false;

        image.LoadCompleted += OnImageLoadCompleted;
        // A fast background denial may precede first UI subscription.
        RequestBitmapOutputRecovery(image);
        return true;
    }

    /// <summary>
    /// Subscribes to <paramref name="image"/> and adds it to the image plans,
    /// unless it is already subscribed and planned.
    /// </summary>
    internal void AddPlan(ImageBox image)
    {
        if (!Subscribe(image))
            return;

        _plans.Add(image);
    }

    internal void UnsubscribeAll()
    {
        ResetBitmapOutputRecovery();
        foreach (var image in _images)
        {
            image.LoadCompleted -= OnImageLoadCompleted;
        }

        _images.Clear();
    }

    /// <summary>
    /// Replaces the subscriptions with those a raster placement prepared off
    /// the UI thread.
    /// </summary>
    internal void Replace(HashSet<ImageBox> images)
    {
        // Compiler-generated events on sealed ImageBox do not run host code.
        // Both subscriptions and all new containers were allocated off-thread.
        ResetBitmapOutputRecovery();
        foreach (ImageBox image in _images)
            image.LoadCompleted -= OnImageLoadCompleted;
        foreach (ImageBox image in images)
            image.LoadCompleted += OnImageLoadCompleted;
        _images = images;
    }

    /// <summary>Starts a bitmap output retry scan when <paramref name="image"/> needs one.</summary>
    internal void RequestBitmapOutputRecovery(ImageBox image)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot?.GeometryRecoveryRequired == true ||
            !_images.Contains(image) ||
            !image.NeedsBitmapOutputRecovery)
            return;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_images.Contains(image))
            return;
        _bitmapOutputRecovery.Request();
        _host.StartRecoveryClock();
    }

    internal void ResetBitmapOutputRecovery()
    {
        _bitmapOutputRecoveryEpoch++;
        _bitmapOutputRecovery.Reset();
    }

    /// <summary>Visits the next bounded slice of plans for bitmap output retry.</summary>
    internal void ProcessBitmapOutputRecovery()
    {
        if (_published.Snapshot?.GeometryRecoveryRequired == true)
        {
            ResetBitmapOutputRecovery();
            return;
        }

        // Unlike governor waiters, overflow has no callback when a slot opens.
        // Reuse the control's one existing 100-ms timer, visiting at most 32
        // current plans per tick. Observation starts no fetch/decode by itself:
        // unchanged capacity/slot/visibility generations do no retry work.
        long epoch = _bitmapOutputRecoveryEpoch;
        var plans = _plans.Items;
        if (plans.Count == 0)
        {
            _bitmapOutputRecovery.Reset();
            return;
        }
        int budget = System.Math.Min(BitmapOutputRecoveryScan.MaximumProbesPerTick, plans.Count);
        for (int probe = 0; probe < budget &&
            _bitmapOutputRecovery.TryTake(plans.Count, out int ordinal); probe++)
        {
            ImageBox image = plans[ordinal];
            bool pending = _images.Contains(image) && image.NeedsBitmapOutputRecovery;
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || epoch != _bitmapOutputRecoveryEpoch ||
                !ReferenceEquals(plans, _plans.Items))
                return;
            if (pending)
            {
                // Visibility comes from the ImageBox's last viewport demand,
                // not potentially stale native geometry while reflow is busy.
                image.ObserveBitmapOutputRecovery(isVisible: false);
                if (_lifetime.IsDisposed || _lifetime.IsUnloaded || epoch != _bitmapOutputRecoveryEpoch ||
                    !ReferenceEquals(plans, _plans.Items))
                    return;
            }
            // Keep a registering request alive until the next complete cycle;
            // it can fail again without emitting a duplicate epoch signal.
            _bitmapOutputRecovery.Complete(pending);
        }
    }

    private void OnImageLoadCompleted(object? sender, LoadCompletedEventArgs e) =>
        LoadCompleted?.Invoke(sender, e);
}
