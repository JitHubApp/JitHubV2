using System.Collections.Concurrent;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// An SVG renderer double that counts opens, records every render request
/// and the source preparation thread, can delay opens, and lets the test
/// advance its cache generation or override the source preparation.
/// </summary>
internal sealed class RecordingSvgRenderer(
    MarkdownSvgDocumentInfo info,
    TimeSpan? openDelay = null,
    Action<Memory<byte>, int, int>? fillPixels = null)
    : IMarkdownSvgRenderer
{
    internal MarkdownSvgSourcePreparation? SourcePreparationOverride { get; init; }
    private int _prepareSourceThreadId;
    private int _openCount;
    private long _cacheGeneration;

    internal int PrepareSourceThreadId => Volatile.Read(ref _prepareSourceThreadId);

    public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _prepareSourceThreadId, Environment.CurrentManagedThreadId);
        return SourcePreparationOverride ?? MarkdownSvgSourcePreparation.Admit(source);
    }

    internal int OpenCount => Volatile.Read(ref _openCount);

    public long CacheGeneration => Volatile.Read(ref _cacheGeneration);

    internal ConcurrentQueue<MarkdownSvgRenderRequest> RenderRequests { get; } = new();

    internal void AdvanceGeneration() => Interlocked.Increment(ref _cacheGeneration);

    public async ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _openCount);
        if (openDelay is { } delay && delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
        return new RecordingSvgDocument(info, RenderRequests, fillPixels);
    }
}
