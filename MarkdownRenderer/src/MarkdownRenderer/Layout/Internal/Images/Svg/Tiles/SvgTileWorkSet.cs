using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;

/// <summary>
/// The in-flight SVG tile renders of one tile cache, keyed by tile: each
/// entry's cancellation source and its <see cref="SvgTileWorkOwner"/>. Like
/// <see cref="Dictionary{TKey, TValue}"/> it is not thread-safe;
/// <see cref="SvgTileCache"/> calls every member under its tile lock.
/// </summary>
internal sealed class SvgTileWorkSet
{
    /// <summary>The most tile renders one image keeps in flight.</summary>
    internal const int MaximumInFlight = 32;

    // Keep the original CTS map stable for lifecycle cleanup. The parallel
    // work-owner map carries only immutable plan/ticket identity for parking
    // and exact stale-completion rejection.
    private readonly Dictionary<SvgTileKey, RetirableCancellationTokenSource> _svgTilesInFlight = new();
    private readonly Dictionary<SvgTileKey, SvgTileWorkOwner> _svgTileWorkOwners = new();

    /// <summary>The number of tiles with work in flight.</summary>
    internal int Count => _svgTilesInFlight.Count;

    /// <summary>Whether neither a cancellation source nor a work owner is held.</summary>
    internal bool IsEmpty => _svgTilesInFlight.Count == 0 && _svgTileWorkOwners.Count == 0;

    /// <summary>Whether <paramref name="key"/> has work in flight.</summary>
    internal bool Contains(SvgTileKey key) => _svgTilesInFlight.ContainsKey(key);

    /// <summary>The cancellation sources of every in-flight render.</summary>
    internal Dictionary<SvgTileKey, RetirableCancellationTokenSource>.ValueCollection Cancellations => _svgTilesInFlight.Values;

    /// <summary>Copies the cancellation sources of every in-flight render.</summary>
    /// <summary>A copy of the in-flight cancellation map.</summary>
    internal Dictionary<SvgTileKey, RetirableCancellationTokenSource> CopyCancellations() => new(_svgTilesInFlight);

    internal RetirableCancellationTokenSource[] ToCancellationArray() => _svgTilesInFlight.Values.ToArray();

    /// <summary>Forgets every in-flight render; its owner still drains its own leases.</summary>
    internal void Clear()
    {
        _svgTilesInFlight.Clear();
        _svgTileWorkOwners.Clear();
    }

    /// <summary>
    /// Whether a tile not yet resident may be queued for <paramref name="plan"/>:
    /// true for free capacity, or with <paramref name="existingVisibleWork"/>
    /// set when visible demand should promote the plan's queued render.
    /// </summary>
    internal bool CanQueue(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        MarkdownSvgRenderPriority priority,
        out SvgTileWorkOwner? existingVisibleWork)
    {
        existingVisibleWork = null;
        if (_svgTilesInFlight.ContainsKey(key))
        {
            if (priority == MarkdownSvgRenderPriority.Visible &&
                _svgTileWorkOwners.TryGetValue(key, out SvgTileWorkOwner? existing) &&
                ReferenceEquals(existing.Plan, plan))
            {
                existingVisibleWork = existing;
            }
            else
            {
                return false;
            }
        }
        else if (_svgTilesInFlight.Count >= MaximumInFlight)
        {
            return false;
        }
        return true;
    }

    /// <summary>Adds the render owned by <paramref name="workOwner"/> if the tile is free and capacity remains.</summary>
    internal bool TryAdd(SvgTileKey key, SvgTileWorkOwner workOwner)
    {
        if (_svgTilesInFlight.ContainsKey(key) ||
            _svgTilesInFlight.Count >= MaximumInFlight)
        {
            return false;
        }

        _svgTilesInFlight.Add(key, workOwner.Cancellation);
        _svgTileWorkOwners.Add(key, workOwner);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="workCancellation"/> still owns <paramref name="key"/>
    /// for <paramref name="plan"/> and has not been claimed, retired or reassigned.
    /// </summary>
    internal bool IsCurrentOwner(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        RetirableCancellationTokenSource workCancellation)
    {
        if (!_svgTilesInFlight.TryGetValue(key, out RetirableCancellationTokenSource? current) ||
            !ReferenceEquals(current, workCancellation) ||
            !_svgTileWorkOwners.TryGetValue(key, out SvgTileWorkOwner? owner))
        {
            return false;
        }

        return ReferenceEquals(owner.Plan, plan) &&
            ReferenceEquals(owner.Cancellation, workCancellation) &&
            Volatile.Read(ref owner.State) == 0;
    }

    /// <summary>
    /// Claims terminal invalidation (state 3) of a failed render that still
    /// owns its map entry, so its completion cannot remove or reassign it.
    /// </summary>
    internal bool TryBeginInvalidation(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner)
    {
        return ReferenceEquals(workOwner.Plan, plan) &&
            _svgTilesInFlight.TryGetValue(key, out RetirableCancellationTokenSource? current) &&
            ReferenceEquals(current, workOwner.Cancellation) &&
            _svgTileWorkOwners.TryGetValue(key, out SvgTileWorkOwner? mappedOwner) &&
            ReferenceEquals(mappedOwner, workOwner) &&
            Interlocked.CompareExchange(ref workOwner.State, 3, 1) == 1;
    }

    /// <summary>Removes a finished render's entry unless a terminal failure transition owns it.</summary>
    internal void Complete(SvgTileKey key, RetirableCancellationTokenSource workCancellation)
    {
        if (_svgTilesInFlight.TryGetValue(key, out RetirableCancellationTokenSource? current) &&
            ReferenceEquals(current, workCancellation))
        {
            if (_svgTileWorkOwners.TryGetValue(key, out SvgTileWorkOwner? owner) &&
                ReferenceEquals(owner.Cancellation, workCancellation))
            {
                // The terminal failure transition owns this row until
                // it detaches the complete tile-map generation.
                if (Volatile.Read(ref owner.State) == 3)
                    return;
                _svgTileWorkOwners.Remove(key);
            }
            _svgTilesInFlight.Remove(key);
        }
    }

    /// <summary>Removes queued work whose render task could not be scheduled.</summary>
    internal void Abandon(SvgTileKey key, SvgTileWorkOwner workOwner)
    {
        if (_svgTilesInFlight.TryGetValue(key, out RetirableCancellationTokenSource? current) &&
            ReferenceEquals(current, workOwner.Cancellation))
        {
            Interlocked.CompareExchange(ref workOwner.State, 2, 0);
            _svgTilesInFlight.Remove(key);
            _svgTileWorkOwners.Remove(key);
        }
    }

    /// <summary>
    /// Retires queued or already-retired work outside the tile range and adds
    /// its cancellation source to <paramref name="staleWork"/>. Busy work
    /// (claimed by a terminal failure) stays.
    /// </summary>
    internal void DetachOutside(
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY,
        ref List<RetirableCancellationTokenSource>? staleWork)
    {
        foreach (KeyValuePair<SvgTileKey, RetirableCancellationTokenSource> work in _svgTilesInFlight)
        {
            int tileX = work.Key.X / SvgTileCache.SvgTileSizePixels;
            int tileY = work.Key.Y / SvgTileCache.SvgTileSizePixels;
            if (tileX < firstTileX || tileX > lastTileX ||
                tileY < firstTileY || tileY > lastTileY)
            {
                SvgTileWorkOwner? owner = null;
                int state = 2;
                if (_svgTileWorkOwners.TryGetValue(work.Key, out owner))
                {
                    state = Volatile.Read(ref owner.State);
                }
                if (state is not (0 or 2))
                    continue;

                // Do not claim queued work before its cancellation owner
                // can be retained, and do not allocate for busy work.
                staleWork ??= new List<RetirableCancellationTokenSource>(_svgTilesInFlight.Count);
                if (owner is not null && state == 0 &&
                    Interlocked.CompareExchange(ref owner.State, 2, 0) != 0)
                {
                    continue;
                }
                if (_svgTilesInFlight.Remove(work.Key))
                {
                    _svgTileWorkOwners.Remove(work.Key);
                    staleWork.Add(work.Value);
                }
            }
        }
    }

    /// <summary>Whether <paramref name="plan"/> has work in flight inside the tile range.</summary>
    internal bool HasPlanWorkWithin(
        SvgTileRenderPlan plan,
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY)
    {
        foreach (KeyValuePair<(int X, int Y, int Width, int Height), SvgTileWorkOwner> active in _svgTileWorkOwners)
        {
            if (!ReferenceEquals(active.Value.Plan, plan))
                continue;
            int tileX = active.Key.X / SvgTileCache.SvgTileSizePixels;
            int tileY = active.Key.Y / SvgTileCache.SvgTileSizePixels;
            if (tileX >= firstTileX && tileX <= lastTileX &&
                tileY >= firstTileY && tileY <= lastTileY)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The number of <paramref name="plan"/>'s renders that pressure parking
    /// may detach, or -1 when one is claimed by a terminal failure.
    /// </summary>
    internal int CountDetachablePlanWork(SvgTileRenderPlan plan)
    {
        int ownedWorkCount = 0;
        foreach (KeyValuePair<(int X, int Y, int Width, int Height), RetirableCancellationTokenSource> work in _svgTilesInFlight)
        {
            if (!_svgTileWorkOwners.TryGetValue(work.Key, out SvgTileWorkOwner? owner) ||
                !ReferenceEquals(owner.Plan, plan))
            {
                continue;
            }

            int state = Volatile.Read(ref owner.State);
            if (state is 1 or 3)
                return -1;
            ownedWorkCount++;
        }
        return ownedWorkCount;
    }

    /// <summary>
    /// Retires and removes every render of <paramref name="plan"/>, writing
    /// each cancellation source to <paramref name="canceledWork"/> as it is
    /// removed. Stops and returns false at work that can no longer be claimed.
    /// </summary>
    internal bool TryDetachPlanWork(
        SvgTileRenderPlan plan,
        RetirableCancellationTokenSource[] canceledWork,
        ref int canceledWorkCount)
    {
        foreach (KeyValuePair<(int X, int Y, int Width, int Height), RetirableCancellationTokenSource> work in _svgTilesInFlight)
        {
            if (!_svgTileWorkOwners.TryGetValue(work.Key, out SvgTileWorkOwner? owner) ||
                !ReferenceEquals(owner.Plan, plan))
            {
                continue;
            }

            int state = Volatile.Read(ref owner.State);
            if (state == 0 && Interlocked.CompareExchange(ref owner.State, 2, 0) != 0)
                return false;
            if (state is not (0 or 2))
                return false;

            _svgTilesInFlight.Remove(work.Key);
            _svgTileWorkOwners.Remove(work.Key);
            canceledWork[canceledWorkCount++] = work.Value;
        }
        return true;
    }
}
