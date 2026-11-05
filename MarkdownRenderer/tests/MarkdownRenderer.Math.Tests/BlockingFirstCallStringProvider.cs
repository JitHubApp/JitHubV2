namespace MarkdownRenderer.Math.Tests;

/// <summary>A string provider whose first call blocks until released and records where it ran.</summary>
internal sealed class BlockingFirstCallStringProvider : IMathStringProvider, IDisposable
{
    private readonly ManualResetEventSlim _releaseFirstCall = new(initialState: false);
    private int _callCount;
    private int _firstCallThreadId;
    private int _firstCallUsedThreadPool;

    internal ManualResetEventSlim FirstCallStarted { get; } = new(initialState: false);

    internal ManualResetEventSlim FirstCallReturned { get; } = new(initialState: false);

    internal ManualResetEventSlim SecondCallStarted { get; } = new(initialState: false);

    internal int CallCount => Volatile.Read(ref _callCount);

    internal int FirstCallThreadId => Volatile.Read(ref _firstCallThreadId);

    internal bool FirstCallUsedThreadPool =>
        Volatile.Read(ref _firstCallUsedThreadPool) != 0;

    public string? GetString(string resourceKey, string? languageTag)
    {
        int call = Interlocked.Increment(ref _callCount);
        if (call == 1)
        {
            Volatile.Write(ref _firstCallThreadId, Environment.CurrentManagedThreadId);
            Volatile.Write(
                ref _firstCallUsedThreadPool,
                Thread.CurrentThread.IsThreadPoolThread ? 1 : 0);
            FirstCallStarted.Set();
            try
            {
                _releaseFirstCall.Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                FirstCallReturned.Set();
            }
        }
        else
        {
            SecondCallStarted.Set();
        }

        return null;
    }

    internal void ReleaseFirstCall() => _releaseFirstCall.Set();

    public void Dispose()
    {
        _releaseFirstCall.Set();
        _releaseFirstCall.Dispose();
        FirstCallStarted.Dispose();
        FirstCallReturned.Dispose();
        SecondCallStarted.Dispose();
    }
}
