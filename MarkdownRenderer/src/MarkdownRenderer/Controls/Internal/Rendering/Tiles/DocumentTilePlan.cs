using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Controls.Internal.Rendering.Tiles;

/// <summary>
/// Which document tiles to keep painted around the viewport, and in what
/// order: the visible tiles top to bottom, then tiles outward from them,
/// favouring the scroll direction once the view moves quickly. The planned
/// tiles always form one contiguous run.
/// </summary>
/// <remarks>
/// A document inside an ancestor's viewport can be wholly offscreen. It then
/// plans only the tiles within reach of the viewport, nearest first, and
/// none when it is farther away, so a long list of documents holds tiles
/// only near what is shown.
/// </remarks>
/// <remarks>
/// The plan is reused every frame, so updating it does not allocate once its
/// order list has grown to the tile budget.
/// </remarks>
internal sealed class DocumentTilePlan
{
    /// <summary>Speed, in DIPs per second, above which look-ahead favours the scroll direction.</summary>
    internal const double DirectionalSpeed = 500;

    /// <summary>Tiles planned ahead for every tile behind while scrolling quickly.</summary>
    internal const int AheadPerBehind = 3;

    private readonly List<int> _order = [];

    /// <summary>Tile indices in painting order.</summary>
    internal IReadOnlyList<int> Order => _order;

    internal int FirstVisible { get; private set; }

    internal int LastVisible { get; private set; } = -1;

    /// <summary>The first tile of the planned run.</summary>
    internal int First { get; private set; }

    /// <summary>The last tile of the planned run, or -1 when nothing is planned.</summary>
    internal int Last { get; private set; } = -1;

    internal bool IsVisible(int index) => index >= FirstVisible && index <= LastVisible;

    internal bool Contains(int index) => index >= First && index <= Last;

    /// <summary>
    /// Plans at most <paramref name="maximumTiles"/> tiles, never fewer than
    /// the visible ones.
    /// </summary>
    /// <param name="grid">The tile grid of the document.</param>
    /// <param name="viewportTop">The viewport's top in document DIPs; may lie outside the document.</param>
    /// <param name="viewportHeight">The viewport's height in DIPs.</param>
    /// <param name="documentHeight">The document's height in DIPs.</param>
    /// <param name="velocity">The scroll velocity in DIPs per second, positive downward.</param>
    /// <param name="maximumTiles">The most tiles to plan beyond the visible ones' minimum.</param>
    /// <param name="offscreenReach">
    /// How far, in DIPs, beyond a viewport that does not intersect the
    /// document its tiles are still planned.
    /// </param>
    internal void Update(
        DocumentTileGrid grid,
        double viewportTop,
        double viewportHeight,
        double documentHeight,
        double velocity,
        int maximumTiles,
        double offscreenReach = double.PositiveInfinity)
    {
        _order.Clear();
        FirstVisible = First = 0;
        LastVisible = Last = -1;
        int count = grid.CountFor(documentHeight);
        if (count == 0 || !double.IsFinite(viewportTop) || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
            return;

        double viewportBottom = viewportTop + viewportHeight;
        if (viewportBottom <= 0 || viewportTop >= documentHeight)
        {
            PlanOffscreen(grid, viewportTop, viewportBottom, documentHeight, count, maximumTiles, offscreenReach);
            return;
        }

        int firstVisible = Math.Min(count - 1, grid.IndexAt(viewportTop));
        int lastVisible = Math.Clamp(grid.IndexAt(viewportTop + viewportHeight - 1e-6), firstVisible, count - 1);
        FirstVisible = firstVisible;
        LastVisible = lastVisible;
        for (int index = firstVisible; index <= lastVisible; index++)
            _order.Add(index);

        bool directional = double.IsFinite(velocity) && Math.Abs(velocity) >= DirectionalSpeed;
        bool downward = !double.IsFinite(velocity) || velocity >= 0;
        int aheadPerBehind = directional ? AheadPerBehind : 1;
        int below = lastVisible + 1;
        int above = firstVisible - 1;
        int sinceBehind = 0;
        while (_order.Count < maximumTiles && (below < count || above >= 0))
        {
            bool takeAhead = sinceBehind < aheadPerBehind;
            bool takeBelow = downward == takeAhead;
            if (takeBelow ? below >= count : above < 0)
                takeBelow = !takeBelow;

            if (takeBelow)
                _order.Add(below++);
            else
                _order.Add(above--);

            sinceBehind = takeBelow == downward ? sinceBehind + 1 : 0;
        }

        First = above + 1;
        Last = below - 1;
    }

    /// <summary>
    /// Plans the tiles of a document the viewport does not intersect: those
    /// within <paramref name="reach"/> of it, nearest first. None is visible.
    /// </summary>
    private void PlanOffscreen(
        DocumentTileGrid grid,
        double viewportTop,
        double viewportBottom,
        double documentHeight,
        int count,
        int maximumTiles,
        double reach)
    {
        if (!(reach > 0))
            return;

        if (viewportBottom <= 0)
        {
            // The document starts below the viewport.
            double reachedBottom = viewportBottom + reach;
            if (reachedBottom <= 0)
                return;

            int last = Math.Min(count - 1, grid.IndexAt(reachedBottom));
            for (int index = 0; index <= last && _order.Count < maximumTiles; index++)
                _order.Add(index);
            First = 0;
            Last = _order.Count - 1;
            return;
        }

        // The document ends above the viewport.
        double reachedTop = viewportTop - reach;
        if (reachedTop >= documentHeight)
            return;

        int first = grid.IndexAt(reachedTop);
        for (int index = count - 1; index >= first && _order.Count < maximumTiles; index--)
            _order.Add(index);
        Last = count - 1;
        First = count - _order.Count;
    }
}
