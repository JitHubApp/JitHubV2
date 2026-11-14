using System;
using MarkdownRenderer.Images;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace MarkdownRenderer.Performance.Internal.Session.ImageSources;

/// <summary>
/// Decides when background prefetch of remote images waits: remote sources
/// pause while the device saves energy or the connection costs data.
/// </summary>
internal static class RemoteImagePrefetchPolicy
{
    /// <summary>
    /// True while Energy Saver is on or the internet connection is metered,
    /// roaming or over its data limit. Hosts that expose neither state never
    /// pause.
    /// </summary>
    internal static bool ShouldPause()
    {
        try
        {
            if (PowerManager.EnergySaverStatus == EnergySaverStatus.On)
                return true;
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            // Unpackaged and headless hosts do not always expose power state.
        }

        try
        {
            ConnectionCost? cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return cost is not null &&
                (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable ||
                 cost.Roaming || cost.OverDataLimit);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="source"/>, resolved against the context's base
    /// URI when relative, is an http or https URI.
    /// </summary>
    internal static bool IsRemoteSource(string source, MarkdownImageResolveContext context)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            (context.BaseUri is null || !Uri.TryCreate(context.BaseUri, source, out uri)))
            return false;
        return uri.Scheme is "https" or "http";
    }
}
