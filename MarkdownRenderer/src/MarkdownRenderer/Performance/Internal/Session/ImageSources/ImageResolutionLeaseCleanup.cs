using System;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;

namespace MarkdownRenderer.Performance.Internal.Session.ImageSources;

/// <summary>Releases image result leases without letting provider cleanup escape.</summary>
internal static class ImageResolutionLeaseCleanup
{
    internal static void DisposeSafely(IMarkdownImageResolutionLease lease)
    {
        try
        {
            lease.Dispose();
        }
        catch (Exception exception)
        {
            // Never run provider cleanup under a cache/flight lock or let it
            // replace a resolver failure. Diagnostics omit source identity.
            try
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] image result lease cleanup failed: {exception.GetType().Name}");
            }
            catch
            {
            }
        }
    }
}
