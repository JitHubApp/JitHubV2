using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

internal sealed class PublicationCommitSvgDocument : IMarkdownSvgDocument
{
    private int _disposeCount;
    private int _disposeWhileWorkLockHeld;

    public MarkdownSvgDocumentInfo Info { get; } = new(
        IntrinsicWidthDips: 32,
        IntrinsicHeightDips: 16,
        IntrinsicAspectRatio: 2);

    internal int DisposeCount => Volatile.Read(ref _disposeCount);

    internal object? WorkLock { get; set; }

    internal bool DisposeWhileWorkLockHeld => Volatile.Read(ref _disposeWhileWorkLockHeld) != 0;

    internal TaskCompletionSource DisposeCompleted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Publication commit tests do not render SVGs.");

    public void Dispose()
    {
        if (WorkLock is { } workLock && Monitor.IsEntered(workLock))
            Interlocked.Exchange(ref _disposeWhileWorkLockHeld, 1);
        Interlocked.Increment(ref _disposeCount);
        DisposeCompleted.TrySetResult();
    }
}
