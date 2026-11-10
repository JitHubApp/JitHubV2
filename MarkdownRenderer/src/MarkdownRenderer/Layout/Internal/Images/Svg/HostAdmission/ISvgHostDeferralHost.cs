using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;

/// <summary>The image box as seen by its <see cref="SvgHostLoadDeferral"/>.</summary>
internal interface ISvgHostDeferralHost
{
    /// <summary>The box's current load demand ticket.</summary>
    MarkdownSvgRenderDemand? LoadDemand { get; }

    /// <summary>The generation of <see cref="LoadDemand"/>.</summary>
    int LoadDemandGeneration { get; }

    /// <summary>The box's latest visual-demand epoch.</summary>
    long SvgHostVisualDemandEpoch { get; }

    /// <summary>
    /// Under the image-work lock, checks that the box is live and the denial is
    /// for its current demand, and returns the token the waiter cancels with.
    /// </summary>
    bool TryCaptureSvgHostDeferralCancellation(
        SvgHostCapacityDeferral deferral, out CancellationToken cancellationToken);

    /// <summary>Marks the load as not started; the caller holds the deferral lock.</summary>
    void MarkLoadNotStarted();

    /// <summary>Starts or resumes the box's load.</summary>
    void StartLoad(bool isVisible, MarkdownSvgRenderDemand? demandOverride, int loadDemandGeneration);
}
