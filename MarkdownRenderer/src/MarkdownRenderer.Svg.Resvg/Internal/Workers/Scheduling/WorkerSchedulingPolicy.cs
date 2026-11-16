using System.Runtime.InteropServices;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

internal static class WorkerSchedulingPolicy
{
    // Process launch and the one-time Windows font catalog are initialization,
    // not untrusted SVG work. Keep their deadline independent from the immutable
    // three-second per-content ceiling so a cold machine cannot poison valid SVGs.
    public static readonly TimeSpan InitializationDeadline = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SecondaryIdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CanceledVisibleBlockingGrace = TimeSpan.FromMilliseconds(100);

    public static bool CanUseSecondary(
        Architecture architecture,
        int logicalProcessorCount,
        bool energySaverOn,
        bool energySaverQueryFailed = false) =>
        architecture is Architecture.X64 or Architecture.Arm64 &&
        logicalProcessorCount >= 8 &&
        !energySaverOn &&
        !energySaverQueryFailed;

    public static bool CanDispatchSecondary(
        bool machineEligible,
        bool workerReady,
        bool workerBusy) =>
        machineEligible && workerReady && !workerBusy;

    public static bool ShouldStartSecondary(
        bool canUseSecondary,
        bool primaryBusy,
        bool secondaryExists,
        int queuedVisibleUncachedRenders) =>
        canUseSecondary &&
        primaryBusy &&
        !secondaryExists &&
        queuedVisibleUncachedRenders >= 2;

    public static bool ShouldWarmSecondaryFontCatalog(
        long primaryReadyGeneration,
        long secondaryReadyGeneration,
        long requestedGeneration,
        int queuedVisibleTextRenders) =>
        WorkerFontCatalogGeneration.IsReadyFor(primaryReadyGeneration, requestedGeneration) &&
        !WorkerFontCatalogGeneration.IsReadyFor(secondaryReadyGeneration, requestedGeneration) &&
        queuedVisibleTextRenders >= 2;

    // Kept for the scheduling tests and call sites that only need to describe
    // initial generation-zero readiness.
    public static bool ShouldWarmSecondaryFontCatalog(
        bool primaryReady,
        bool secondaryReady,
        int queuedVisibleTextRenders) =>
        ShouldWarmSecondaryFontCatalog(
            primaryReady ? 0 : -1,
            secondaryReady ? 0 : -1,
            requestedGeneration: 0,
            queuedVisibleTextRenders);

    // A failed optional worker is a batch-scoped sentinel. Keep it registered
    // while queued work drains so Release cannot immediately trigger another
    // startup attempt through PumpLocked.
    public static bool ShouldRetainFailedSecondaryAfterRelease(
        bool hasFailed,
        int queuedWorkCount) =>
        hasFailed && queuedWorkCount > 0;

    public static bool ShouldRetireSecondary(
        DateTimeOffset idleSince,
        DateTimeOffset now) =>
        now - idleSince >= SecondaryIdleTimeout;

    public static bool ShouldTerminateCanceledActive(
        bool activeRequestCanceled,
        int queuedVisibleRequests,
        DateTimeOffset blockedSince,
        DateTimeOffset now) =>
        activeRequestCanceled &&
        queuedVisibleRequests > 0 &&
        now - blockedSince >= CanceledVisibleBlockingGrace;
}
