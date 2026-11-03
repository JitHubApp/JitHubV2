using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Readiness state for the optional worker. A secondary slot is not eligible
/// for content dispatch while process startup is in progress. A live worker
/// remains eligible for geometry while its font catalog refreshes.
/// </summary>
internal sealed class SecondaryWorkerReadiness
{
    private int _isWarming;
    private int _workerReady;
    private long _fontCatalogReadyGeneration = -1;
    private long _fontCatalogFailedGeneration = -1;
    private int _hasFailed;

    public bool IsWarming => Volatile.Read(ref _isWarming) != 0;
    public bool IsWorkerReady => Volatile.Read(ref _workerReady) != 0;
    public long FontCatalogReadyGeneration => Interlocked.Read(ref _fontCatalogReadyGeneration);
    public long FontCatalogFailedGeneration => Interlocked.Read(ref _fontCatalogFailedGeneration);
    public bool IsFontCatalogReady => FontCatalogReadyGeneration >= 0;
    public bool HasFailed => Volatile.Read(ref _hasFailed) != 0;

    public bool CanDispatch(bool requiresFontCatalog, long requestedGeneration = 0) =>
        IsWorkerReady &&
        (!requiresFontCatalog || WorkerFontCatalogGeneration.IsReadyFor(
            FontCatalogReadyGeneration,
            requestedGeneration));

    public bool TryBeginWarmup(bool workerAlreadyReady, bool fontCatalogAlreadyReady)
        => TryBeginWarmup(workerAlreadyReady, fontCatalogAlreadyReady ? 0 : -1);

    public bool TryBeginWarmup(bool workerAlreadyReady, long fontCatalogReadyGeneration)
    {
        if (HasFailed)
            return false;
        if (Interlocked.CompareExchange(ref _isWarming, 1, 0) != 0)
            return false;

        Volatile.Write(ref _workerReady, workerAlreadyReady ? 1 : 0);
        Interlocked.Exchange(
            ref _fontCatalogReadyGeneration,
            workerAlreadyReady ? fontCatalogReadyGeneration : -1);
        return true;
    }

    public void MarkWorkerReady() => Volatile.Write(ref _workerReady, 1);

    public void MarkFontCatalogReady(long generation)
    {
        if (generation is < 0 or > WorkerFontCatalogGeneration.MaximumGeneration)
            throw new WorkerProtocolException("The SVG worker returned an invalid font generation.");

        long observed = Interlocked.Read(ref _fontCatalogReadyGeneration);
        long publishedGeneration = observed;
        while (generation > observed)
        {
            long prior = Interlocked.CompareExchange(ref _fontCatalogReadyGeneration, generation, observed);
            if (prior == observed)
            {
                publishedGeneration = generation;
                break;
            }
            observed = prior;
            publishedGeneration = prior;
        }

        // Clear only failures covered by the catalog actually published. An
        // older in-flight completion must not erase a newer failure marker.
        long failedGeneration = Interlocked.Read(ref _fontCatalogFailedGeneration);
        while (failedGeneration >= 0 && publishedGeneration >= failedGeneration)
        {
            long prior = Interlocked.CompareExchange(ref _fontCatalogFailedGeneration, -1, failedGeneration);
            if (prior == failedGeneration)
                break;
            failedGeneration = prior;
        }
    }

    public void MarkFontCatalogFailed(long generation)
    {
        if (generation is < 0 or > WorkerFontCatalogGeneration.MaximumGeneration)
            throw new WorkerProtocolException("The SVG worker returned an invalid failed font generation.");
        if (generation <= FontCatalogReadyGeneration)
            return;

        long observed = Interlocked.Read(ref _fontCatalogFailedGeneration);
        while (generation > observed)
        {
            long prior = Interlocked.CompareExchange(ref _fontCatalogFailedGeneration, generation, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }

    public void MarkFontCatalogReady() => MarkFontCatalogReady(0);

    public void CompleteWarmup() => Volatile.Write(ref _isWarming, 0);

    public void FailWarmup()
    {
        Volatile.Write(ref _hasFailed, 1);
        Volatile.Write(ref _workerReady, 0);
        Interlocked.Exchange(ref _fontCatalogReadyGeneration, -1);
        Interlocked.Exchange(ref _fontCatalogFailedGeneration, -1);
        Volatile.Write(ref _isWarming, 0);
    }

    public async Task WarmAsync(
        Func<CancellationToken, Task> startWorkerAsync,
        Func<bool> shouldWarmFontCatalog,
        Func<CancellationToken, Task<long>> warmFontCatalogAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startWorkerAsync);
        ArgumentNullException.ThrowIfNull(shouldWarmFontCatalog);
        ArgumentNullException.ThrowIfNull(warmFontCatalogAsync);

        try
        {
            await startWorkerAsync(cancellationToken).ConfigureAwait(false);
            MarkWorkerReady();
            if (shouldWarmFontCatalog())
            {
                long readyGeneration = await warmFontCatalogAsync(cancellationToken).ConfigureAwait(false);
                if (readyGeneration >= 0)
                    MarkFontCatalogReady(readyGeneration);
            }

            CompleteWarmup();
        }
        catch
        {
            FailWarmup();
            throw;
        }
    }

    public async Task WarmAsync(
        Func<CancellationToken, Task> startWorkerAsync,
        Func<bool> shouldWarmFontCatalog,
        Func<CancellationToken, Task> warmFontCatalogAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(warmFontCatalogAsync);
        await WarmAsync(
            startWorkerAsync,
            shouldWarmFontCatalog,
            async token =>
            {
                await warmFontCatalogAsync(token).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
