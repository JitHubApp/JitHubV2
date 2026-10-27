using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Dispatching;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    private IMarkdownImageResidencyPolicy? _imageResidencyPolicy;
    private LayoutSnapshot? _imageResidencySnapshot;
    private readonly List<ImageBox> _imageResidencyBandScratch = new();
    private bool _imageResidencyPlansChanged;
    private int _imageResidencyReconcileQueued;

    private void PrepareImageResidency(LayoutSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, _imageResidencySnapshot))
        {
            ResetImageResidency();
            _imageResidencySnapshot = snapshot;
            // DependencyObject.DispatcherQueue must be captured on its owner
            // thread; a resource-drain notification can arrive on the pool.
            DispatcherQueue? dispatcher = DispatcherQueue;
            _imageResidencyPolicy = (PerformanceSession as IMarkdownPerformanceSessionInternal)?
                .CreateImageResidencyPolicy(() => RequestImageResidencyReconcile(dispatcher));
        }
        _imageResidencyPlansChanged = true;
    }

    private void ReconcileImageResidency(LazyLayoutBand band, ViewportRange range)
    {
        if (_imageResidencyPolicy is not { } policy ||
            _snapshot is not { } snapshot ||
            !ReferenceEquals(snapshot, _imageResidencySnapshot) ||
            !snapshot.TryBeginInteraction())
            return;

        try
        {
            // A worker can finish mutating geometry before the corresponding
            // UI plan publication. Retain resources until its index is current.
            if (snapshot.LayoutRevision != _appliedLazyLayoutRevision)
                return;
            // Query only indexed band members on scroll. Newly published plans
            // are visited once to release constructor-acquired warm leases.
            for (int index = range.Start; index < range.End; index++)
            {
                ImageBox image = _imagePlans[_imagePlanIndex!.GetBlockOrdinal(index)];
                (double top, double bottom) = _imagePlanIndex.GetBounds(index);
                if (bottom >= band.Top && top <= band.Bottom)
                    _imageResidencyBandScratch.Add(image);
            }
            policy.Reconcile(_imageResidencyBandScratch,
                _imageResidencyPlansChanged ? _imagePlans : null);
            _imageResidencyPlansChanged = false;
        }
        finally
        {
            _imageResidencyBandScratch.Clear();
            snapshot.EndInteraction();
        }
    }

    private void RequestImageResidencyReconcile(DispatcherQueue? dispatcher)
    {
        if (Interlocked.Exchange(ref _imageResidencyReconcileQueued, 1) != 0)
            return;

        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _imageResidencyReconcileQueued, 0);
            if (_isDisposed || _isUnloaded)
                return;
            RealizeVisibleEmbeds();
            InvalidateCanvas();
        }))
            Interlocked.Exchange(ref _imageResidencyReconcileQueued, 0);
    }

    private void ResetImageResidency()
    {
        _imageResidencyPolicy?.Dispose();
        _imageResidencyPolicy = null;
        _imageResidencySnapshot = null;
        _imageResidencyBandScratch.Clear();
        _imageResidencyPlansChanged = false;
    }
}
