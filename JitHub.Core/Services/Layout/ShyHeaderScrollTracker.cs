using System;

namespace JitHub.Core.Services.Layout;

public enum ShyHeaderScrollAction
{
    None,
    Hide,
    Reveal
}

/// <summary>
/// Decides when a scroll-driven shy header hides or comes back, from the
/// scroll viewer's offset and scroll range.
/// </summary>
/// <remarks>
/// Hiding or showing the header resizes the viewport, which changes the scroll
/// range. Near the end of the content the scroll viewer then moves the offset to
/// fit the new range. That move is layout, not scrolling: counting it as travel
/// reveals the header again, which restores the range and hides it again, until
/// WinUI stops the loop with a layout cycle.
/// <para>
/// A held touchpad or touch gesture keeps scrolling while that layout settles, so
/// the header changes state at most once per continuous scroll. Travel keeps
/// counting meanwhile, so a real reversal applies when the scroll settles, and
/// reaching the top of the content always restores the header.
/// </para>
/// </remarks>
public sealed class ShyHeaderScrollTracker
{
    private readonly double _startOffset;
    private readonly double _restoreOffset;
    private readonly double _revealTravel;
    private readonly double _rehideTravel;
    private readonly double _epsilon;
    private double _lastOffset;
    private double _lastScrollableHeight;
    private double _upwardTravel;
    private double _downwardTravel;
    private bool _revealedByUpwardScroll;
    private bool _isManipulating;
    private bool _changedDuringScroll;

    public ShyHeaderScrollTracker(
        double startOffset,
        double restoreOffset,
        double revealTravel,
        double rehideTravel,
        double epsilon)
    {
        _startOffset = startOffset;
        _restoreOffset = restoreOffset;
        _revealTravel = revealTravel;
        _rehideTravel = rehideTravel;
        _epsilon = epsilon;
    }

    public bool IsShy { get; private set; }

    public void Reset(double offset, double scrollableHeight, bool isShy)
    {
        _lastOffset = offset;
        _lastScrollableHeight = scrollableHeight;
        _upwardTravel = 0;
        _downwardTravel = 0;
        _revealedByUpwardScroll = false;
        _changedDuringScroll = false;
        IsShy = isShy;
    }

    public void BeginManipulation() => _isManipulating = true;

    public void EndManipulation()
    {
        _isManipulating = false;
        _changedDuringScroll = false;
    }

    /// <summary>
    /// Records a position produced by the header's own layout change, without
    /// counting it as scroll travel.
    /// </summary>
    public void SyncLayout(double offset, double scrollableHeight)
    {
        _lastOffset = offset;
        _lastScrollableHeight = scrollableHeight;
    }

    public ShyHeaderScrollAction Observe(
        double offset,
        double scrollableHeight,
        bool canHide,
        bool isIntermediate = false)
    {
        double delta = offset - _lastOffset;
        bool movedByLayout = IsMovedByLayout(offset, scrollableHeight);
        _lastOffset = offset;
        _lastScrollableHeight = scrollableHeight;
        bool isScrolling = isIntermediate || _isManipulating;
        if (!isScrolling)
        {
            _changedDuringScroll = false;
        }

        if (movedByLayout)
        {
            return ShyHeaderScrollAction.None;
        }

        if (offset <= _restoreOffset)
        {
            _changedDuringScroll = false;
            if (IsShy)
            {
                return Reveal(revealedByUpwardScroll: false, isScrolling: false);
            }

            _revealedByUpwardScroll = false;
            _downwardTravel = 0;
            return ShyHeaderScrollAction.None;
        }

        if (IsShy)
        {
            if (delta < -_epsilon)
            {
                _upwardTravel += -delta;
            }
            else if (delta > _epsilon)
            {
                _upwardTravel = Math.Max(0, _upwardTravel - delta);
            }

            return !_changedDuringScroll && _upwardTravel >= _revealTravel
                ? Reveal(revealedByUpwardScroll: true, isScrolling)
                : ShyHeaderScrollAction.None;
        }

        if (_revealedByUpwardScroll)
        {
            if (delta > _epsilon)
            {
                _downwardTravel += delta;
            }
            else if (delta < -_epsilon)
            {
                _downwardTravel = 0;
            }

            return !_changedDuringScroll && _downwardTravel >= _rehideTravel
                ? Hide(canHide, isScrolling)
                : ShyHeaderScrollAction.None;
        }

        return !_changedDuringScroll && offset >= _startOffset
            ? Hide(canHide, isScrolling)
            : ShyHeaderScrollAction.None;
    }

    // A changed range moved the offset when the old offset no longer fits the
    // new range, or when the offset sat at the end of either range.
    private bool IsMovedByLayout(double offset, double scrollableHeight) =>
        Math.Abs(scrollableHeight - _lastScrollableHeight) > _epsilon &&
        (_lastOffset > scrollableHeight + _epsilon ||
         _lastOffset >= _lastScrollableHeight - _epsilon ||
         offset >= scrollableHeight - _epsilon);

    private ShyHeaderScrollAction Reveal(bool revealedByUpwardScroll, bool isScrolling)
    {
        IsShy = false;
        _changedDuringScroll = isScrolling;
        _revealedByUpwardScroll = revealedByUpwardScroll;
        _upwardTravel = 0;
        _downwardTravel = 0;
        return ShyHeaderScrollAction.Reveal;
    }

    private ShyHeaderScrollAction Hide(bool canHide, bool isScrolling)
    {
        if (!canHide)
        {
            _downwardTravel = 0;
            return ShyHeaderScrollAction.None;
        }

        IsShy = true;
        _changedDuringScroll = isScrolling;
        _revealedByUpwardScroll = false;
        _upwardTravel = 0;
        _downwardTravel = 0;
        return ShyHeaderScrollAction.Hide;
    }
}
