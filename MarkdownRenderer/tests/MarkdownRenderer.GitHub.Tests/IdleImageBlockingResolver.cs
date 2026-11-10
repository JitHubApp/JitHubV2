using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>Image resolver that blocks until released and reports its first two calls and returns.</summary>
internal sealed class IdleImageBlockingResolver : IMarkdownImageResolver, IDisposable
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _bothReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _resolveCount;
    private int _returnedCount;

    internal int ResolveCount => Volatile.Read(ref _resolveCount);
    internal Task WaitForCallAsync(int count) => count switch
    {
        1 => _firstCall.Task,
        2 => _secondCall.Task,
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };
    internal Task WaitForAllReturnedAsync(int count) => count switch
    {
        1 => _firstReturned.Task,
        2 => _bothReturned.Task,
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };
    internal void Release() => _release.TrySetResult();

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _resolveCount);
        if (call == 1)
            _firstCall.TrySetResult();
        else if (call == 2)
            _secondCall.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline);
        }
        finally
        {
            int returned = Interlocked.Increment(ref _returnedCount);
            if (returned == 1)
                _firstReturned.TrySetResult();
            else if (returned == 2)
                _bothReturned.TrySetResult();
        }
    }

    public void Dispose() => Release();
}
