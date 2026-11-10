using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Images.Raster;

/// <summary>
/// Everything one <see cref="ImageRasterLoader"/> load holds while it runs:
/// the decoded bitmap and its output reservation, the cache lease, every
/// admission slot and gate, and the geometry the WinRT decoder refines. The
/// loader releases it in one place. It is a class, not a struct, because the
/// asynchronous stages that fill it cannot take a <see langword="ref"/>.
/// </summary>
internal sealed class RasterLoadState
{
    internal RasterLoadState(RasterLoadPlan plan, IMarkdownPerformanceSessionInternal? session)
    {
        Session = session;
        IntrinsicSize = plan.IntrinsicSize;
        RasterSize = plan.RasterSize;
    }

    internal readonly IMarkdownPerformanceSessionInternal? Session;
    internal CanvasBitmap? OwnedBitmap;
    internal BitmapOutputAdmission.Reservation? BitmapReservation;
    internal SharedCanvasBitmapCache.Lease? BitmapLease;
    internal IDisposable? ProcessDecodeSlot;
    internal WicDecodeOperationOwner? WicDecodeOwner;
    internal IDisposable? PreparationSlot;
    internal IDisposable? RasterWork;
    internal SemaphoreSlim? DecodeGate;
    internal bool DecodeGateHeld;
    internal Size IntrinsicSize;
    internal (int Width, int Height) RasterSize;
    internal bool CacheRasterDecodeFailure;
    internal long RasterPreparationId;
    internal long DecodeStarted;

    /// <summary>The start timestamp of a traced preparation stage, or zero when untraced.</summary>
    internal long StartStage() => RasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Records one preparation stage with the session, when this load is traced.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordStage(MarkdownRasterPreparationStage stage, long startedAt)
    {
        if (RasterPreparationId != 0)
            Session!.RecordRasterPreparationStage(
                RasterPreparationId, stage, Stopwatch.GetTimestamp() - startedAt);
    }
}
