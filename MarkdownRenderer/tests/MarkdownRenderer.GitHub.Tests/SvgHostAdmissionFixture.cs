using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Creates deferred SVG host owners, records their real host-capacity
/// denials, and creates bare image boxes for the SVG host admission tests. It
/// also finds the scope's admission domain and its pending rows, and reads
/// cancellation state.
/// </summary>
internal static class SvgHostAdmissionFixture
{
    /// <summary>
    /// Creates a deferral whose host is loading <paramref name="demand"/> at
    /// <paramref name="generation"/> and whose waiters are linked to
    /// <paramref name="lifetime"/>.
    /// </summary>
    internal static SvgHostLoadDeferral CreateLoadingOwner(
        ImageResourceScope scope,
        MarkdownSvgRenderDemand demand,
        CancellationToken lifetime,
        Func<Action, bool>? retryQueue = null,
        int generation = 1)
    {
        SvgHostLoadDeferral owner = TestSvgHostDeferralHost.CreateDeferral(scope, retryQueue);
        TestSvgHostDeferralHost host = TestSvgHostDeferralHost.Of(owner);
        host.LoadDemand = demand;
        host.LoadDemandGeneration = generation;
        host.Cancellation = lifetime;
        return owner;
    }

    /// <summary>
    /// Records a real source-byte denial of <paramref name="demand"/>, which
    /// registers its waiter through the scope's admission coordinator.
    /// </summary>
    internal static void Deny(
        SvgHostLoadDeferral owner,
        IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand,
        long requiredBytes,
        int generation = 1) =>
        owner.DeferSvgHostLoad(new SvgHostCapacityDeferral(
            admission, demand, generation, admission.CapacityVersion, requiredBytes,
            RequiresOwnerSlot: false, OwnerVersionAtDenial: 0,
            DemandVersionAtDenial: demand.Version, VisualDemandEpochAtDenial: 0));

    /// <summary>The scope's host admission domain for a provider domain key.</summary>
    internal static SvgHostAdmissionDomain DomainOf(ImageResourceScope scope, object admissionDomainKey) =>
        scope.SvgHostAdmission.GetDomain(admissionDomainKey);

    /// <summary>The pending row whose identity is <paramref name="identity"/>.</summary>
    internal static SvgHostPendingEntry PendingEntry(SvgHostAdmissionDomain domain, object? identity) =>
        Assert.Single(domain.GetPendingEntries(), entry => ReferenceEquals(entry.Identity, identity));

    /// <summary>Whether a pending row has started telling its owner that its waiter finished.</summary>
    internal static bool IsFinished(SvgHostPendingEntry entry) =>
        Volatile.Read(ref entry.WaiterCompletionStarted) != 0;

    internal static bool IsCancellationDisposed(CancellationTokenSource cancellation)
    {
        try
        {
            _ = cancellation.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    // The box never loads here, so it needs no device, theme or dispatcher.
    internal static ImageBox NewBox(MarkdownRenderResources renderResources, string url, string alt) => new(
        new MarkdownLayoutContext(null!, null!, null!, null!, FlowDirection.LeftToRight) { RenderResources = renderResources },
        renderResources.Images, url, alt);
}
