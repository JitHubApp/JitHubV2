
namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

/// <summary>
/// Value-only coalescing state for a frame-paced retry. A rendering callback
/// consumes one pending attempt; Busy calls Request again for the next frame.
/// It deliberately owns no control, snapshot, box, or native resource.
/// </summary>
internal sealed class SelectionAdornerRetryState
{
    private bool _pending;

    internal bool IsPending => _pending;

    internal void Request() => _pending = true;

    internal bool TryBeginFrame()
    {
        if (!_pending)
            return false;

        _pending = false;
        return true;
    }

    internal void Retry() => _pending = true;

    internal void Complete() => _pending = false;

    internal void Cancel() => _pending = false;
}
