namespace MarkdownRenderer.Tests;

/// <summary>A resolver result that counts how often it was disposed.</summary>
internal sealed class TrackedResult : IDisposable
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}
