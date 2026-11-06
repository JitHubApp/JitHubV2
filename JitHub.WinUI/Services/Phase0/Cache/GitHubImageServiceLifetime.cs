using System.Threading;

namespace JitHub.Services;

/// <summary>
/// The disposal state one image service shares with the parts it composes, so
/// a part never starts a cache write after its owner began disposing.
/// </summary>
internal sealed class GitHubImageServiceLifetime
{
    private int _disposed;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Marks the owner disposed. Returns <see langword="false"/> if it already was.</summary>
    internal bool TryRetire() => Interlocked.Exchange(ref _disposed, 1) == 0;
}
