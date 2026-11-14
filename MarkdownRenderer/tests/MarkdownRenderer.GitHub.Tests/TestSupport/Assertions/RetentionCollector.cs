using System.Runtime.CompilerServices;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;

/// <summary>
/// Forces full blocking collections until every probed object is gone, for
/// retention tests that prove a queued callback or retained task holds its
/// owner only weakly. Allocate the probed objects in a
/// <see cref="MethodImplOptions.NoInlining"/> helper so no test local roots them.
/// </summary>
internal static class RetentionCollector
{
    private const int MaxAttempts = 10;

    /// <summary>Collects until every target in <paramref name="references"/> is dead, or gives up.</summary>
    internal static void CollectUntilDead(params WeakReference[] references)
    {
        for (int attempt = 0; attempt < MaxAttempts && AnyAlive(references); attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AnyAlive(WeakReference[] references)
    {
        foreach (WeakReference reference in references)
        {
            if (reference.IsAlive)
                return true;
        }
        return false;
    }
}
