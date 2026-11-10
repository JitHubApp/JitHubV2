using System;
using System.Threading;
using MarkdownRenderer.Performance.Internal.Residency;

namespace MarkdownRenderer.Performance.Internal.Session;

/// <summary>
/// Holds one scene-preparation admission, its linked cancellation and one
/// unit of session work.
/// </summary>
internal sealed class ScenePreparationLease(
    IDisposable admissionLease,
    CancellationTokenSource cancellation,
    MarkdownSessionAdmissions admissions,
    MarkdownPerformanceSessionLifetime lifetime,
    long started) : IMarkdownScenePreparationLease
{
    private IDisposable? _admissionLease = admissionLease;
    private int _timingCompleted;

    public CancellationToken CancellationToken => cancellation.Token;

    internal void CompletePreparationTiming()
    {
        if (Interlocked.Exchange(ref _timingCompleted, 1) == 0)
            admissions.RecordScenePreparation(started);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _admissionLease, null) is not { } held)
            return;
        try
        {
            held.Dispose();
        }
        finally
        {
            try
            {
                CompletePreparationTiming();
            }
            finally
            {
                try
                {
                    cancellation.Dispose();
                }
                finally
                {
                    lifetime.RetireWork();
                }
            }
        }
    }
}
