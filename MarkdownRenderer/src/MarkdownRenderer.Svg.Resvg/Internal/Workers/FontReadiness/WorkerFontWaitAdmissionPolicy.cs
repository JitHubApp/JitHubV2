using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.FontReadiness;

internal static class WorkerFontWaitAdmissionPolicy
{
    public static int CountVisibleDemand(int queuedCount, int detachedCount)
    {
        if (queuedCount < 0 || detachedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(queuedCount));
        return checked(queuedCount + detachedCount);
    }

    public static bool CanAdmitNew(int queuedCount, int detachedWaitCount, int queueCapacity) =>
        queuedCount >= 0 && detachedWaitCount >= 0 && queueCapacity > 0 &&
        queuedCount < queueCapacity && queuedCount + detachedWaitCount < queueCapacity;

    public static bool CanReenter(int queuedCount, int queueCapacity) =>
        queuedCount >= 0 && queueCapacity > 0 && queuedCount < queueCapacity;

    public static bool CanReserveFromActive(
        int queuedCount,
        int detachedWaitCount,
        int queueCapacity,
        int activeSlotCount,
        bool leaseIsActive = true,
        bool alreadyReserved = false) =>
        leaseIsActive && !alreadyReserved &&
        queuedCount >= 0 && detachedWaitCount >= 0 && queueCapacity > 0 && activeSlotCount > 0 &&
        queuedCount + detachedWaitCount < queueCapacity + activeSlotCount;
}
