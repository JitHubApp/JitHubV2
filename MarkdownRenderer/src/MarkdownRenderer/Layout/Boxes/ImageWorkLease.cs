using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One unit of an image box's counted image work and its cancellation token.
/// Disposing it completes the work exactly once.
/// </summary>
internal sealed class ImageWorkLease : IDisposable
{
    private ImageBox? _owner;

    internal ImageWorkLease(ImageBox owner, CancellationToken token)
    {
        _owner = owner;
        Token = token;
    }

    internal readonly CancellationToken Token;

    public void Dispose() =>
        Interlocked.Exchange(ref _owner, null)?.CompleteImageWork();
}
