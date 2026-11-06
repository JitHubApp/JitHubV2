namespace MarkdownRenderer.Tests;

/// <summary>
/// A disposable that runs its callback on the first dispose only and
/// reports whether it has been disposed.
/// </summary>
internal sealed class TrackingDisposable(Action dispose) : IDisposable
{
    private int _disposed;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            dispose();
    }
}
