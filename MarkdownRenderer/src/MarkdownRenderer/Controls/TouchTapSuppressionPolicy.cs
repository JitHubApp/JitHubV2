using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Pure ownership and duplicate-tap suppression state for touch gestures.
/// Tap suppression belongs to the gesture that produced it, not merely to a
/// time-and-distance window shared by every later contact.
/// </summary>
internal sealed class TouchTapSuppressionPolicy
{
    private const double SuppressionRadiusDips = 48;
    private uint _activePointerId;
    private long _activeGestureEpoch;
    private bool _activeGestureTapCompleted;
    private long _completedGestureEpoch;
    private uint _completedGesturePointerId;
    private long _nextGestureEpoch;
    private long _suppressedGestureEpoch;
    private long _suppressedDeadlineTickMs;
    private Point _suppressedOrigin;
    private bool _hasNoContactFallback;
    private bool _requiresAcceptedContactForRecognizedGesture;
    private readonly Dictionary<uint, UnownedContact> _unownedContacts = new();

    /// <summary>
    /// A source/document replacement invalidates the identity of any pending
    /// ID-less XAML gesture. Until a new primary contact is accepted, an
    /// unmatched recognized event must not be applied to replacement content.
    /// </summary>
    internal bool RequiresAcceptedContactForRecognizedGesture =>
        _requiresAcceptedContactForRecognizedGesture;

    internal void RequireAcceptedContactForRecognizedGesture() =>
        _requiresAcceptedContactForRecognizedGesture = true;

    internal bool CanDispatchRecognizedGesture(long gestureEpoch, bool hasDeferredOwner)
    {
        if (!_requiresAcceptedContactForRecognizedGesture || hasDeferredOwner)
            return true;

        // A stale nonzero epoch is not provenance: document replacement has
        // already retired active and completed ownership. Only a gesture still
        // tied to a current policy owner can pass the replacement boundary.
        return gestureEpoch != 0 &&
            (gestureEpoch == _activeGestureEpoch || gestureEpoch == _completedGestureEpoch);
    }

    /// <summary>
    /// Starts or retrieves the epoch owned by this primary pointer. A different
    /// pointer cannot steal an active gesture; a later press with a reused ID
    /// receives a fresh epoch after the prior contact ends.
    /// </summary>
    internal bool TryBeginContact(uint pointerId, out long gestureEpoch)
    {
        gestureEpoch = 0;
        if (pointerId == 0)
            return false;

        if (_activePointerId != 0)
        {
            if (_activePointerId != pointerId)
                return false;

            gestureEpoch = _activeGestureEpoch;
            return true;
        }

        _activePointerId = pointerId;
        _activeGestureEpoch = NextEpoch();
        _requiresAcceptedContactForRecognizedGesture = false;
        _activeGestureTapCompleted = false;
        _completedGestureEpoch = 0;
        _completedGesturePointerId = 0;
        ClearCompletedUnownedContacts();
        _unownedContacts.Remove(pointerId);
        ClearSuppression();
        gestureEpoch = _activeGestureEpoch;
        return true;
    }

    internal long GetGestureEpochForPointer(uint pointerId) =>
        pointerId != 0 && pointerId == _activePointerId
            ? _activeGestureEpoch
            : 0;

    /// <summary>
    /// Resolves a pointer identity after FIFO replay, including a contact whose
    /// release was replayed before its ID-less Tapped/RightTapped event.
    /// </summary>
    internal long GetGestureEpochForPointerOrCompleted(uint pointerId)
    {
        if (pointerId == 0)
            return 0;
        if (pointerId == _activePointerId)
            return _activeGestureEpoch;
        return pointerId == _completedGesturePointerId ? _completedGestureEpoch : 0;
    }

    /// <summary>
    /// Gestures raised after PointerReleased still belong to the completed
    /// contact until the next accepted contact begins.
    /// </summary>
    internal long GetGestureEpochForRecognizedGesture() =>
        _activeGestureEpoch != 0 ? _activeGestureEpoch : _completedGestureEpoch;

    internal uint GetPointerIdForRecognizedGesture() =>
        _activeGestureEpoch != 0 ? _activePointerId : _completedGesturePointerId;

    /// <summary>
    /// A recognized XAML gesture has no pointer ID. If it lands at a recorded
    /// secondary contact, it cannot safely be attributed to the primary owner.
    /// Completed secondary origins are retained until their trailing gesture
    /// arrives or a later accepted primary contact supersedes them.
    /// </summary>
    internal bool TryGetGestureEpochForRecognizedGesture(
        Point point,
        out long gestureEpoch,
        out uint pointerId,
        out bool isAmbiguous)
    {
        gestureEpoch = 0;
        pointerId = 0;
        isAmbiguous = HasUnownedContactNear(point);
        if (isAmbiguous)
            return false;

        gestureEpoch = GetGestureEpochForRecognizedGesture();
        pointerId = GetPointerIdForRecognizedGesture();
        return gestureEpoch != 0 && pointerId != 0;
    }

    internal bool HasUnownedContactNear(Point point)
    {
        foreach (UnownedContact contact in _unownedContacts.Values)
        {
            if (IsWithinSuppressionRadius(contact.Origin, point))
                return true;
        }
        return false;
    }

    internal void TrackUnownedContact(uint pointerId, Point origin)
    {
        if (pointerId == 0 || pointerId == _activePointerId ||
            !double.IsFinite(origin.X) || !double.IsFinite(origin.Y))
        {
            return;
        }

        _unownedContacts[pointerId] = new UnownedContact(origin, IsActive: true);
    }

    internal void EndContact(uint pointerId)
    {
        if (pointerId == 0)
            return;
        if (_activePointerId != pointerId)
        {
            if (_unownedContacts.TryGetValue(pointerId, out UnownedContact contact))
                _unownedContacts[pointerId] = contact with { IsActive = false };
            return;
        }

        _completedGestureEpoch = _activeGestureTapCompleted ? 0 : _activeGestureEpoch;
        _completedGesturePointerId = _activeGestureTapCompleted ? 0 : _activePointerId;
        _activeGestureEpoch = 0;
        _activeGestureTapCompleted = false;
        _activePointerId = 0;
    }

    /// <summary>
    /// Ends an active contact only when a deferred terminal event still belongs
    /// to its current epoch. Pointer IDs may be reused, so matching the ID
    /// alone could let a stale queued release retire a newer contact.
    /// </summary>
    internal bool TryEndContact(uint pointerId, long gestureEpoch)
    {
        if (pointerId == 0 || gestureEpoch == 0 ||
            _activePointerId != pointerId || _activeGestureEpoch != gestureEpoch)
        {
            return false;
        }

        EndContact(pointerId);
        return true;
    }

    internal void CancelContact(uint pointerId)
    {
        if (pointerId == 0)
            return;
        if (_activePointerId != pointerId)
        {
            _unownedContacts.Remove(pointerId);
            return;
        }

        _activePointerId = 0;
        _activeGestureEpoch = 0;
        _activeGestureTapCompleted = false;
        _completedGestureEpoch = 0;
        _completedGesturePointerId = 0;
        ClearSuppression();
    }

    /// <summary>
    /// Cancels an active deferred contact only when its queued terminal still
    /// belongs to the current pointer epoch.
    /// </summary>
    internal bool TryCancelContact(uint pointerId, long gestureEpoch)
    {
        if (pointerId == 0 || gestureEpoch == 0 ||
            _activePointerId != pointerId || _activeGestureEpoch != gestureEpoch)
        {
            return false;
        }

        CancelContact(pointerId);
        return true;
    }

    internal void SuppressTouchTap(
        long gestureEpoch,
        Point origin,
        long nowTickMs,
        long durationMs = 1_500)
    {
        _suppressedGestureEpoch = gestureEpoch;
        durationMs = Math.Max(0, durationMs);
        _suppressedDeadlineTickMs = nowTickMs > long.MaxValue - durationMs
            ? long.MaxValue
            : nowTickMs + durationMs;
        _suppressedOrigin = origin;
        // Some deferred/synthetic gesture routes do not have a captured
        // pointer press. Preserve their duplicate-Tap suppression as a single
        // spatially bounded fallback, which a real accepted contact retires.
        _hasNoContactFallback = gestureEpoch == 0;
    }

    internal bool ShouldSuppressTap(
        long gestureEpoch,
        long nowTickMs,
        Point point)
    {
        bool sameGesture = gestureEpoch != 0 && gestureEpoch == _suppressedGestureEpoch;
        if (!sameGesture && !(gestureEpoch == 0 && _hasNoContactFallback))
            return false;

        if (nowTickMs > _suppressedDeadlineTickMs)
            return false;

        return IsWithinSuppressionRadius(_suppressedOrigin, point);
    }

    /// <summary>
    /// A touch contact yields at most one Tapped event. Retire its completed
    /// association after routing that event, even when it was outside the
    /// suppression region or its deadline elapsed.
    /// </summary>
    internal void CompleteTap(long gestureEpoch)
    {
        if (gestureEpoch == 0)
        {
            _hasNoContactFallback = false;
            if (_suppressedGestureEpoch == 0)
                ClearSuppression();
            return;
        }

        if (_suppressedGestureEpoch == gestureEpoch)
            ClearSuppression();

        if (_activePointerId != 0 && _activeGestureEpoch == gestureEpoch)
            _activeGestureTapCompleted = true;
        else if (_completedGestureEpoch == gestureEpoch)
        {
            _completedGestureEpoch = 0;
            _completedGesturePointerId = 0;
        }
    }

    /// <summary>
    /// Preserves the late-recognizer barrier without discarding ownership.
    /// A caller retiring deferred terminals can stage this before cancellation
    /// erases the contact identity, then reset after its disposition is applied.
    /// </summary>
    internal void PrepareForReset()
    {
        // A caller may reset input state while a native recognizer still has
        // a delayed ID-less event to deliver. Preserve a provenance barrier
        // whenever this reset retires an observed touch sequence, whether the
        // primary contact is still down, has just completed, or was an
        // unowned secondary/synthetic gesture. A later accepted primary press
        // is the only event that can safely establish new provenance.
        if (_activePointerId != 0 ||
            _activeGestureEpoch != 0 ||
            _completedGestureEpoch != 0 ||
            _unownedContacts.Count != 0 ||
            _hasNoContactFallback ||
            _suppressedGestureEpoch != 0)
        {
            RequireAcceptedContactForRecognizedGesture();
        }
    }

    /// <summary>
    /// Clears pointer ownership and duplicate state. The replacement provenance
    /// latch deliberately survives generic resets and is retired only by a new
    /// accepted primary contact, so rebuild/reset paths cannot reopen a window
    /// for stale ID-less gestures.
    /// </summary>
    internal void Reset()
    {
        PrepareForReset();

        _activePointerId = 0;
        _activeGestureEpoch = 0;
        _activeGestureTapCompleted = false;
        _completedGestureEpoch = 0;
        _completedGesturePointerId = 0;
        _unownedContacts.Clear();
        ClearSuppression();
    }

    private long NextEpoch()
    {
        _nextGestureEpoch = _nextGestureEpoch == long.MaxValue ? 1 : _nextGestureEpoch + 1;
        return _nextGestureEpoch;
    }

    private void ClearSuppression()
    {
        _suppressedGestureEpoch = 0;
        _suppressedDeadlineTickMs = 0;
        _suppressedOrigin = default;
        _hasNoContactFallback = false;
    }

    private void ClearCompletedUnownedContacts()
    {
        if (_unownedContacts.Count == 0)
            return;

        List<uint>? completed = null;
        foreach ((uint pointerId, UnownedContact contact) in _unownedContacts)
        {
            if (!contact.IsActive)
                (completed ??= new List<uint>()).Add(pointerId);
        }

        if (completed is null)
            return;
        foreach (uint pointerId in completed)
            _unownedContacts.Remove(pointerId);
    }

    internal static bool IsWithinSuppressionRadius(Point origin, Point point)
    {
        double dx = point.X - origin.X;
        double dy = point.Y - origin.Y;
        return dx * dx + dy * dy <= SuppressionRadiusDips * SuppressionRadiusDips;
    }

    private readonly record struct UnownedContact(Point Origin, bool IsActive);
}
