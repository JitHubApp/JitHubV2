using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Decides once whether an opened SVG render is consumed or disposed while
/// still pending.
/// </summary>
internal struct SvgOpenedRenderOwnership
{
    private int _state; // 0 pending, 1 consumed, 2 retired

    internal bool TryConsume() =>
        Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal bool TryDisposePending() =>
        Interlocked.CompareExchange(ref _state, 2, 0) == 0;

    internal bool TryCompleteConsumed() =>
        Interlocked.CompareExchange(ref _state, 2, 1) == 1;
}
