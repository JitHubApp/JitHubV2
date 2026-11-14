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
        IsShy = isShy;
    }

    public ShyHeaderScrollAction Observe(double offset, double scrollableHeight, bool canHide)
    {
        double delta = offset - _lastOffset;
        bool movedByLayout = IsMovedByLayout(offset, scrollableHeight);
        _lastOffset = offset;
        _lastScrollableHeight = scrollableHeight;
        if (movedByLayout)
        {
            return ShyHeaderScrollAction.None;
        }

        if (IsShy)
        {
            if (offset <= _restoreOffset)
            {
                return Reveal(revealedByUpwardScroll: false);
            }

            if (delta < -_epsilon)
            {
                _upwardTravel += -delta;
                if (_upwardTravel >= _revealTravel)
                {
                    return Reveal(revealedByUpwardScroll: true);
                }
            }
            else if (delta > _epsilon)
            {
                _upwardTravel = 0;
            }

            return ShyHeaderScrollAction.None;
        }

        if (offset <= _restoreOffset)
        {
            _revealedByUpwardScroll = false;
            _downwardTravel = 0;
        }
        else if (_revealedByUpwardScroll)
        {
            if (delta > _epsilon)
            {
                _downwardTravel += delta;
                if (_downwardTravel >= _rehideTravel)
                {
                    return Hide(canHide);
                }
            }
            else if (delta < -_epsilon)
            {
                _downwardTravel = 0;
            }
        }
        else if (offset >= _startOffset)
        {
            return Hide(canHide);
        }

        return ShyHeaderScrollAction.None;
    }

    // A changed range moved the offset when the old offset no longer fits the
    // new range, or when the offset sat at the end of either range.
    private bool IsMovedByLayout(double offset, double scrollableHeight) =>
        Math.Abs(scrollableHeight - _lastScrollableHeight) > _epsilon &&
        (_lastOffset > scrollableHeight + _epsilon ||
         _lastOffset >= _lastScrollableHeight - _epsilon ||
         offset >= scrollableHeight - _epsilon);

    private ShyHeaderScrollAction Reveal(bool revealedByUpwardScroll)
    {
        IsShy = false;
        _revealedByUpwardScroll = revealedByUpwardScroll;
        _upwardTravel = 0;
        _downwardTravel = 0;
        return ShyHeaderScrollAction.Reveal;
    }

    private ShyHeaderScrollAction Hide(bool canHide)
    {
        if (!canHide)
        {
            _downwardTravel = 0;
            return ShyHeaderScrollAction.None;
        }

        IsShy = true;
        _revealedByUpwardScroll = false;
        _upwardTravel = 0;
        _downwardTravel = 0;
        return ShyHeaderScrollAction.Hide;
    }
}
