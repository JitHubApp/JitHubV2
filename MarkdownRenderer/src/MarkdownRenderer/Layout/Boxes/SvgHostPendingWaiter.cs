using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A box's handle on its pending SVG admission row. Disposing the last
/// handle releases the row's waiter.
/// </summary>
internal sealed class SvgHostPendingWaiter(SvgHostAdmissionDomain domain, SvgHostPendingEntry entry) : IDisposable
{
    private SvgHostAdmissionDomain? _domain = domain;
    private SvgHostPendingEntry? _entry = entry;
    private readonly object _identity = entry.Identity;

    internal object Identity => _identity;

    /// <summary>The pending row this handle holds, or null once the handle is disposed.</summary>
    internal SvgHostPendingEntry? Entry => Volatile.Read(ref _entry);
    internal bool IsFinished => Volatile.Read(ref _entry) is not { } currentEntry ||
        Volatile.Read(ref currentEntry.WaiterCompletionStarted) != 0;

    public void Dispose()
    {
        SvgHostAdmissionDomain? currentDomain = Interlocked.Exchange(ref _domain, null);
        SvgHostPendingEntry? currentEntry = Interlocked.Exchange(ref _entry, null);
        if (currentDomain is not null && currentEntry is not null)
            currentDomain.ReleaseWaiterHandle(currentEntry);
    }
}
