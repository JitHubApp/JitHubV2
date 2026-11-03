using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// What a raster placement worker needs from the UI thread, captured there.
/// It reaches the publisher only weakly and never forwards callbacks that
/// belong to an older layout generation.
/// </summary>
internal sealed class RasterPlacementInputs(
    WeakReference<RasterPlacementPublisher> receiver,
    long generation,
    object imagePreparationOwner,
    CancellationToken imageLifetime,
    SnapshotRetirementTracker retirements,
    double? scrollOffset)
{
    private WeakReference<LayoutSnapshot>? _candidateSnapshot;
    internal readonly CancellationToken ImageLifetime = imageLifetime;
    internal readonly object ImagePreparationOwner = imagePreparationOwner;
    internal readonly Action<Task> RetirementSink = retirements.Track;
    internal readonly double? ScrollOffset = scrollOffset;

    internal void Bind(LayoutSnapshot snapshot) =>
        Volatile.Write(ref _candidateSnapshot, new WeakReference<LayoutSnapshot>(snapshot));

    internal void ImageUnavailable(string url, MarkdownImageUnavailableReason reason, MarkdownSvgFailureReason? svgReason)
    {
        if (receiver.TryGetTarget(out RasterPlacementPublisher? publisher) &&
            Volatile.Read(ref _candidateSnapshot) is { } identity && identity.TryGetTarget(out LayoutSnapshot? snapshot))
            publisher.RaiseImageUnavailableIfCurrent(snapshot, generation, url, reason, svgReason);
    }
}
