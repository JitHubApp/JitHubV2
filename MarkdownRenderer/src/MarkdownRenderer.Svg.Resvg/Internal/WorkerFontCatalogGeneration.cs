using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Monotonic font-catalog generation bookkeeping shared by the worker process
/// and its scheduling readiness state. A value of -1 means no catalog has
/// been confirmed by the host yet; generation zero is a valid initial catalog.
/// </summary>
internal sealed class WorkerFontCatalogGeneration
{
    public const long MaximumGeneration = uint.MaxValue;
    private long _readyGeneration = -1;

    public long ReadyGeneration => Interlocked.Read(ref _readyGeneration);

    public bool IsReadyFor(long requestedGeneration) =>
        IsReadyFor(ReadyGeneration, requestedGeneration);

    public static bool IsReadyFor(long readyGeneration, long requestedGeneration) =>
        requestedGeneration >= 0 &&
        requestedGeneration <= MaximumGeneration &&
        readyGeneration >= requestedGeneration;

    public static void ValidateRequest(long requestedGeneration)
    {
        if (requestedGeneration is < 0 or > MaximumGeneration)
            throw new WorkerProtocolException("The SVG font generation exceeds the protocol limit.");
    }

    public void MarkReady(long readyGeneration)
    {
        if (readyGeneration is < 0 or > MaximumGeneration)
            throw new WorkerProtocolException("The SVG worker returned an invalid font generation.");

        long observed = Interlocked.Read(ref _readyGeneration);
        while (readyGeneration > observed)
        {
            long prior = Interlocked.CompareExchange(ref _readyGeneration, readyGeneration, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }

    public static bool IsSupportedGeneration(long generation) =>
        generation is >= 0 and <= MaximumGeneration;
}
