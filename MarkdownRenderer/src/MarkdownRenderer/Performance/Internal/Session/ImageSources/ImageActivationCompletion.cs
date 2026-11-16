using System;
using System.Threading;
using MarkdownRenderer.Diagnostics.Internal;

namespace MarkdownRenderer.Performance.Internal.Session.ImageSources;

/// <summary>Settles one scheduled image activation exactly once and releases its session work.</summary>
internal sealed class ImageActivationCompletion(
    MarkdownPerformanceSessionLifetime lifetime,
    CancellationTokenSource linked,
    Action onNotStarted)
{
    private int _completed;

    internal void Complete(bool started)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        try
        {
            if (!started)
                onNotStarted();
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] Image activation retirement failed: {exception.Message}");
        }
        finally
        {
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
}
