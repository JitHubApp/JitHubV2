using System;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    private readonly BitmapOutputRecoveryScan _bitmapOutputRecovery = new();
    private long _bitmapOutputRecoveryEpoch;

    private void RequestBitmapOutputRecovery(ImageBox image)
    {
        if (_isDisposed || _isUnloaded || _snapshot?.GeometryRecoveryRequired == true ||
            !_subscribedImages.Contains(image) ||
            !image.NeedsBitmapOutputRecovery)
            return;
        if (_isDisposed || _isUnloaded || !_subscribedImages.Contains(image))
            return;
        _bitmapOutputRecovery.Request();
        StartImageRelayoutRecovery();
    }

    private void ResetBitmapOutputRecovery()
    {
        _bitmapOutputRecoveryEpoch++;
        _bitmapOutputRecovery.Reset();
    }

    private void ProcessBitmapOutputRecovery()
    {
        if (_snapshot?.GeometryRecoveryRequired == true)
        {
            ResetBitmapOutputRecovery();
            return;
        }

        // Unlike governor waiters, overflow has no callback when a slot opens.
        // Reuse the control's one existing 100-ms timer, visiting at most 32
        // current plans per tick. Observation starts no fetch/decode by itself:
        // unchanged capacity/slot/visibility generations do no retry work.
        long epoch = _bitmapOutputRecoveryEpoch;
        var plans = _imagePlans;
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
            bool pending = _subscribedImages.Contains(image) && image.NeedsBitmapOutputRecovery;
            if (_isDisposed || _isUnloaded || epoch != _bitmapOutputRecoveryEpoch ||
                !ReferenceEquals(plans, _imagePlans))
                return;
            if (pending)
            {
                // Visibility comes from the ImageBox's last viewport demand,
                // not potentially stale native geometry while reflow is busy.
                image.ObserveBitmapOutputRecovery(isVisible: false);
                if (_isDisposed || _isUnloaded || epoch != _bitmapOutputRecoveryEpoch ||
                    !ReferenceEquals(plans, _imagePlans))
                    return;
            }
            // Keep a registering request alive until the next complete cycle;
            // it can fail again without emitting a duplicate epoch signal.
            _bitmapOutputRecovery.Complete(pending);
        }
    }
}
