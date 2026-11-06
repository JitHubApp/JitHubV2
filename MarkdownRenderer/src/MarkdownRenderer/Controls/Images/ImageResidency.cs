using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// Applies the performance session's image residency policy to the
/// published plans: images near the viewport keep their decoded resources,
/// the rest may release them. A policy belongs to one snapshot; when the
/// policy drains resources it asks for a reconcile on the UI thread.
/// </summary>
internal sealed class ImageResidency
{
    private readonly IImageResidencyHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly ImagePlanSet _plans;
    private readonly EmbedRealization _embeds;
    private readonly List<ImageBox> _bandScratch = new();

    private IMarkdownImageResidencyPolicy? _policy;
    private LayoutSnapshot? _snapshot;
    private bool _plansChanged;
    private int _reconcileQueued;

    internal ImageResidency(
        IImageResidencyHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        ImagePlanSet plans,
        EmbedRealization embeds)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _plans = plans;
        _embeds = embeds;
    }

    /// <summary>
    /// Readies the policy for <paramref name="snapshot"/> after its plans
    /// were published; the next reconcile visits every plan once.
    /// </summary>
    internal void Prepare(LayoutSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, _snapshot))
        {
            Reset();
            _snapshot = snapshot;
            // DependencyObject.DispatcherQueue must be captured on its owner
            // thread; a resource-drain notification can arrive on the pool.
            DispatcherQueue? dispatcher = _host.DispatcherQueue;
            _policy = (_host.PerformanceSession as IMarkdownPerformanceSessionInternal)?
                .CreateImageResidencyPolicy(() => RequestReconcile(dispatcher));
        }
        _plansChanged = true;
    }

    /// <summary>
    /// Tells the policy which plans in <paramref name="range"/> of the image
    /// index lie in <paramref name="band"/>.
    /// </summary>
    internal void Reconcile(LazyLayoutBand band, ViewportRange range)
    {
        if (_policy is not { } policy ||
            _published.Snapshot is not { } snapshot ||
            !ReferenceEquals(snapshot, _snapshot) ||
            !snapshot.TryBeginInteraction())
            return;

        try
        {
            // A worker can finish mutating geometry before the corresponding
            // UI plan publication. Retain resources until its index is current.
            if (snapshot.LayoutRevision != _host.AppliedLazyLayoutRevision)
                return;
            // Query only indexed band members on scroll. Newly published plans
            // are visited once to release constructor-acquired warm leases.
            for (int index = range.Start; index < range.End; index++)
            {
                ImageBox image = _plans[_plans.Index!.GetBlockOrdinal(index)];
                (double top, double bottom) = _plans.Index.GetBounds(index);
                if (bottom >= band.Top && top <= band.Bottom)
                    _bandScratch.Add(image);
            }
            policy.Reconcile(_bandScratch,
                _plansChanged ? _plans.Items : null);
            _plansChanged = false;
        }
        finally
        {
            _bandScratch.Clear();
            snapshot.EndInteraction();
        }
    }

    internal void Reset()
    {
        _policy?.Dispose();
        _policy = null;
        _snapshot = null;
        _bandScratch.Clear();
        _plansChanged = false;
    }

    private void RequestReconcile(DispatcherQueue? dispatcher)
    {
        if (Interlocked.Exchange(ref _reconcileQueued, 1) != 0)
            return;

        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _reconcileQueued, 0);
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
                return;
            _embeds.RealizeVisible();
            _host.InvalidateCanvas();
        }))
            Interlocked.Exchange(ref _reconcileQueued, 0);
    }
}
