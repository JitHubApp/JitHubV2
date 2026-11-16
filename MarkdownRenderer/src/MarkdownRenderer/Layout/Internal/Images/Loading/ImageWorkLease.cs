using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Internal.Images.Loading;

/// <summary>
/// One unit of an image box's counted image work, admitted by its
/// <see cref="ImageWorkLifetime"/>, and its cancellation token.
/// Disposing it completes the work exactly once.
/// </summary>
internal sealed class ImageWorkLease : IDisposable
{
    private ImageWorkLifetime? _owner;

    internal ImageWorkLease(ImageWorkLifetime owner, CancellationToken token)
    {
        _owner = owner;
        Token = token;
    }

    internal readonly CancellationToken Token;

    public void Dispose() =>
        Interlocked.Exchange(ref _owner, null)?.CompleteWork();
}
