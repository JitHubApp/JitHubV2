using System.Threading;

namespace MarkdownRenderer.Performance.Internal.Session;

/// <summary>Releases one scheduled layout item's linked cancellation and session work exactly once.</summary>
internal sealed class LayoutWorkCompletion(
    MarkdownPerformanceSessionLifetime lifetime,
    CancellationTokenSource linked)
{
    private int _completed;

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        try
        {
            linked.Dispose();
        }
        finally
        {
            lifetime.RetireWork();
        }
    }
}
