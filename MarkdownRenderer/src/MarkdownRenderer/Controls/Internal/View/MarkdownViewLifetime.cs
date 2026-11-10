using System.Threading;

namespace MarkdownRenderer.Controls.Internal.View;

/// <summary>
/// Tracks a renderer's load state. A renderer starts unloaded, may load and
/// unload any number of times, and is disposed once.
/// </summary>
/// <remarks>
/// Written on the UI thread. Provider callbacks such as an SVG renderer's
/// cache invalidation read it from other threads, so every access is
/// volatile.
/// </remarks>
internal sealed class MarkdownViewLifetime
{
    private bool _isUnloaded = true;
    private bool _isDisposed;
    private bool _isDisposalComplete;

    /// <summary>True until the first Loaded and again after each Unloaded.</summary>
    public bool IsUnloaded => Volatile.Read(ref _isUnloaded);

    /// <summary>True from the start of disposal.</summary>
    public bool IsDisposed => Volatile.Read(ref _isDisposed);

    /// <summary>True once disposal has released every resource.</summary>
    public bool IsDisposalComplete => Volatile.Read(ref _isDisposalComplete);

    public void MarkLoaded() => Volatile.Write(ref _isUnloaded, false);

    public void MarkUnloaded() => Volatile.Write(ref _isUnloaded, true);

    public void MarkDisposing() => Volatile.Write(ref _isDisposed, true);

    public void MarkDisposalComplete() => Volatile.Write(ref _isDisposalComplete, true);
}
