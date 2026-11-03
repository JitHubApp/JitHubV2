using System;
using System.Diagnostics.CodeAnalysis;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Holds only the unclaimed root handle. Its TaskCompletionSource publishes
/// a bool, never the raw image result, so a completed fan-out task cannot
/// keep a released byte[] alive.
/// </summary>
internal sealed class ResolutionLeaseSlot
{
    private readonly object _gate = new();
    private IMarkdownImageResolutionLease? _root;

    internal void SetRoot(IMarkdownImageResolutionLease root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (_gate)
        {
            if (_root is not null)
                throw new InvalidOperationException("An image result lease was already published.");
            _root = root;
        }
    }

    internal bool TryRetain([NotNullWhen(true)] out IMarkdownImageResolutionLease? retained)
    {
        lock (_gate)
        {
            if (_root is null)
            {
                retained = null;
                return false;
            }

            retained = _root.Retain();
            return true;
        }
    }

    internal void ReleaseRootSafely()
    {
        IMarkdownImageResolutionLease? root;
        lock (_gate)
        {
            root = _root;
            _root = null;
        }

        if (root is not null)
            ImageResolutionLeaseCleanup.DisposeSafely(root);
    }
}
