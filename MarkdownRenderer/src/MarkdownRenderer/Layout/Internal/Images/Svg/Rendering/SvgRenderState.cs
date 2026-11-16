using System.Threading;
using Microsoft.Graphics.Canvas;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// What one <see cref="SvgOpenedRenderer"/> whole-image render owns while it
/// runs: the shared bitmap lease, the bitmap output reservation, the uploaded
/// bitmap before the cache adopts it, and the keyed device-bitmap gate. The
/// renderer releases whatever remains in one place. It is a class because the
/// asynchronous stages that fill it cannot take a <see langword="ref"/>.
/// </summary>
internal sealed class SvgRenderState
{
    internal SvgRenderState(SvgOpenedRenderWork work, BitmapOutputWorkContext bitmapOutputWork)
    {
        Work = work;
        BitmapOutputWork = bitmapOutputWork;
    }

    internal readonly SvgOpenedRenderWork Work;
    internal readonly BitmapOutputWorkContext BitmapOutputWork;

    internal SharedCanvasBitmapCache.Lease? BitmapLease;
    internal BitmapOutputAdmission.Reservation? BitmapReservation;
    internal CanvasBitmap? OwnedBitmap;
    internal SemaphoreSlim? RenderGate;
    internal bool RenderGateHeld;
}
