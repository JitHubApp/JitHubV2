using Windows.Foundation;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Identifies a single deferred touch contact that can own an ID-less XAML
/// gesture. A gesture near multiple pending contacts is deliberately left
/// unattributed rather than borrowing whichever pointer happens to be active.
/// </summary>
internal struct DeferredTouchGestureOwnerResolver
{
    private readonly Point _gesturePosition;
    private uint _candidatePointerId;
    private bool _ambiguous;
    private bool _candidateCanceled;

    internal DeferredTouchGestureOwnerResolver(Point gesturePosition)
    {
        _gesturePosition = gesturePosition;
        _candidatePointerId = 0;
        _ambiguous = false;
        _candidateCanceled = false;
    }

    internal void ObserveTouchPress(uint pointerId, Point pressPosition, bool isInContact)
    {
        if (pointerId == 0 || !isInContact ||
            !TouchTapSuppressionPolicy.IsWithinSuppressionRadius(_gesturePosition, pressPosition))
        {
            return;
        }

        if (_candidatePointerId == 0)
        {
            _candidatePointerId = pointerId;
            return;
        }

        // Even a repeated ID can represent a newly reused pointer/contact.
        // Without the ID on the recognized event, two nearby press epochs are
        // not safely distinguishable.
        _ambiguous = true;
    }

    internal void ObserveTouchCancel(uint pointerId)
    {
        if (pointerId != 0 && pointerId == _candidatePointerId)
            _candidateCanceled = true;
    }

    internal bool TryGetOwnerPointerId(out uint pointerId)
    {
        pointerId = _candidatePointerId;
        return !_ambiguous && !_candidateCanceled && pointerId != 0;
    }

    internal bool IsAmbiguous => _ambiguous || _candidateCanceled;
}
