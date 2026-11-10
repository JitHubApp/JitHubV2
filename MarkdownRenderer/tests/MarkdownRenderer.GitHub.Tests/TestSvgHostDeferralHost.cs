using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A managed stand-in for the image box behind an <see cref="SvgHostLoadDeferral"/>,
/// so deferral tests construct the real owner instead of an uninitialized box.
/// </summary>
internal sealed class TestSvgHostDeferralHost : ISvgHostDeferralHost
{
    private static readonly ConditionalWeakTable<SvgHostLoadDeferral, TestSvgHostDeferralHost> Hosts = new();

    public MarkdownSvgRenderDemand? LoadDemand { get; set; }
    public int LoadDemandGeneration { get; set; }
    public long SvgHostVisualDemandEpoch { get; set; }
    public CancellationToken Cancellation { get; set; }
    public int StartLoadCount { get; private set; }

    /// <summary>Creates a deferral over a fresh host in <paramref name="scope"/>.</summary>
    /// <param name="scope">The test's image scope.</param>
    /// <param name="retryQueue">The deferral's retry scheduler, or null for the built-in queues.</param>
    internal static SvgHostLoadDeferral CreateDeferral(
        ImageResourceScope scope, System.Func<System.Action, bool>? retryQueue = null)
    {
        var host = new TestSvgHostDeferralHost();
        var deferral = new SvgHostLoadDeferral(host, scope, retryQueue);
        Hosts.Add(deferral, host);
        return deferral;
    }

    /// <summary>The host behind a deferral made by <see cref="CreateDeferral"/>.</summary>
    internal static TestSvgHostDeferralHost Of(SvgHostLoadDeferral deferral) =>
        Hosts.TryGetValue(deferral, out TestSvgHostDeferralHost? host)
            ? host
            : throw new System.InvalidOperationException("The deferral was not made by this host.");

    public bool TryCaptureSvgHostDeferralCancellation(
        SvgHostCapacityDeferral deferral, out CancellationToken cancellationToken)
    {
        cancellationToken = Cancellation;
        return ReferenceEquals(LoadDemand, deferral.Demand) &&
               deferral.LoadGeneration == LoadDemandGeneration;
    }

    public void MarkLoadNotStarted()
    {
    }

    public void StartLoad(bool isVisible, MarkdownSvgRenderDemand? demandOverride, int loadDemandGeneration) =>
        StartLoadCount++;
}
