namespace MarkdownRenderer.Controls;

/// <summary>UI-owned, scalar-only round-robin cursor. No image, snapshot or
/// source is retained when recovery is idle or a publication is replaced.</summary>
internal sealed class BitmapOutputRecoveryScan
{
    internal const int MaximumProbesPerTick = 32;
    private int _next;
    private int _remaining;
    private bool _cycleSawPending;
    internal bool IsPending { get; private set; }

    internal void Request()
    {
        if (IsPending)
        {
            // A signal for an ordinal already visited must survive the end of
            // this cycle. Repeated signals do not restart/starve its tail.
            _cycleSawPending = true;
            return;
        }
        IsPending = true;
        _remaining = 0;
    }

    internal bool TryTake(int count, out int ordinal)
    {
        ordinal = 0;
        if (!IsPending)
            return false;
        if (count <= 0)
        {
            Reset();
            return false;
        }
        if (_remaining == 0)
        {
            _remaining = count;
            _next %= count;
            _cycleSawPending = false;
        }
        ordinal = _next;
        _next = (_next + 1) % count;
        return true;
    }

    internal void Complete(bool needsRecovery)
    {
        _cycleSawPending |= needsRecovery;
        if (--_remaining == 0)
            IsPending = _cycleSawPending;
    }

    internal void Reset()
    {
        IsPending = false;
        _next = 0;
        _remaining = 0;
        _cycleSawPending = false;
    }
}
