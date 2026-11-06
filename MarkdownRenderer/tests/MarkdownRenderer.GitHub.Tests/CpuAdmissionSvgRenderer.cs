using System.Collections.Concurrent;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// An SVG renderer double for CPU preparation admission: it can block,
/// hold, reject or throw from its first source preparation, block its
/// first open, and signals each stage to the test.
/// </summary>
internal sealed class CpuAdmissionSvgRenderer : IMarkdownSvgRenderer, IDisposable
{
    internal enum FirstPreparationFailure
    {
        None,
        Reject,
        Throw,
    }

    private readonly MarkdownSvgDocumentInfo _info;
    private readonly bool _blockFirstPreparation;
    private readonly bool _holdFirstPreparationAfterCancellation;
    private readonly bool _blockFirstOpen;
    private readonly FirstPreparationFailure _firstPreparationFailure;
    private int _prepareSourceCount;
    private int _openCount;

    internal CpuAdmissionSvgRenderer(
        MarkdownSvgDocumentInfo info,
        bool blockFirstPreparation = false,
        bool holdFirstPreparationAfterCancellation = false,
        bool blockFirstOpen = false,
        FirstPreparationFailure firstPreparationFailure = FirstPreparationFailure.None)
    {
        _info = info;
        _blockFirstPreparation = blockFirstPreparation;
        _holdFirstPreparationAfterCancellation = holdFirstPreparationAfterCancellation;
        _blockFirstOpen = blockFirstOpen;
        _firstPreparationFailure = firstPreparationFailure;
    }

    internal ManualResetEventSlim ReleaseFirstPreparation { get; } = new(initialState: false);

    internal ManualResetEventSlim ReleaseFirstPreparationAfterCancellation { get; } = new(initialState: false);

    internal TaskCompletionSource FirstPreparationStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource SecondPreparationStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource FirstPreparationCancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource FirstOpenStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseFirstOpen { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);

    internal int OpenCount => Volatile.Read(ref _openCount);

    public long CacheGeneration => 0;

    public MarkdownSvgSourcePreparation PrepareSource(
        byte[] source,
        CancellationToken cancellationToken)
    {
        int ordinal = Interlocked.Increment(ref _prepareSourceCount);
        if (ordinal == 1)
        {
            FirstPreparationStarted.TrySetResult();
            if (_blockFirstPreparation)
            {
                try
                {
                    if (!ReleaseFirstPreparation.Wait(TimeSpan.FromSeconds(15), cancellationToken))
                        throw new TimeoutException("The test did not release the first SVG preparation gate.");
                }
                catch (OperationCanceledException)
                {
                    FirstPreparationCancellationObserved.TrySetResult();
                    if (_holdFirstPreparationAfterCancellation)
                        ReleaseFirstPreparationAfterCancellation.Wait();
                    throw;
                }
            }

            if (_firstPreparationFailure == FirstPreparationFailure.Reject)
            {
                return MarkdownSvgSourcePreparation.Reject(
                    MarkdownSvgFailureReason.UnsupportedContent,
                    "Rejected by the CPU admission test renderer.");
            }
            if (_firstPreparationFailure == FirstPreparationFailure.Throw)
                throw new InvalidOperationException("Thrown by the CPU admission test renderer.");
        }
        else if (ordinal == 2)
        {
            SecondPreparationStarted.TrySetResult();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return MarkdownSvgSourcePreparation.Admit(source);
    }

    public async ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int ordinal = Interlocked.Increment(ref _openCount);
        if (ordinal == 1 && _blockFirstOpen)
        {
            FirstOpenStarted.TrySetResult();
            await ReleaseFirstOpen.Task.WaitAsync(cancellationToken);
        }

        return new RecordingSvgDocument(
            _info,
            new ConcurrentQueue<MarkdownSvgRenderRequest>());
    }

    public void Dispose()
    {
        ReleaseFirstPreparation.Set();
        ReleaseFirstPreparationAfterCancellation.Set();
        ReleaseFirstOpen.TrySetResult();
        ReleaseFirstPreparation.Dispose();
        ReleaseFirstPreparationAfterCancellation.Dispose();
    }
}
