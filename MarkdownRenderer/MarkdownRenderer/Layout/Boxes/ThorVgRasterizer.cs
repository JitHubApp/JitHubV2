// SPDX-License-Identifier: MIT
//
// ThorVG-backed SVG rasterizer. Produces a BGRA premultiplied pixel buffer
// from raw SVG bytes that the caller wraps in a CanvasBitmap.
//
// This is the *only* SVG rendering path in MarkdownRenderer — there is no
// Win2D / Skia fallback. Every SVG (data URI, remote, currentColor-themed,
// gradient, filter, mask, clipPath, &lt;use&gt;) goes through this class.
//
// Threading: each Rasterize() call allocates and destroys its own canvas
// and picture. Canvases in ThorVG are not safe to share across threads, so
// per-call construction is the safe pattern. The engine itself is initialized
// once (reference-counted by ThorVG) on first use.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using MarkdownRenderer.Diagnostics;
using static MarkdownRenderer.Layout.Boxes.ThorVgNative;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Rasterizes SVG documents to BGRA pixel buffers using ThorVG.
/// </summary>
internal static class ThorVgRasterizer
{
    /// <summary>Rasterized SVG output: BGRA premultiplied buffer plus dimensions.</summary>
    public readonly record struct Raster(byte[] Bgra, int WidthPx, int HeightPx);

    private static int _engineInitialized;
    private static bool _shutdownRequested;
    private static int _activeRasterizations;
    private static readonly ManualResetEventSlim _idle = new(initialState: true);
    private static readonly object _engineLock = new();

    /// <summary>
    /// Reference-counted engine initialization. ThorVG's
    /// <c>tvg_engine_init</c> is itself ref-counted so repeated calls are
    /// cheap, but we keep a managed gate so the first failure is observable
    /// and we don't pay the P/Invoke overhead on every rasterize.
    /// </summary>
    private static bool EnsureEngineLocked()
    {
        if (_shutdownRequested) return false;
        if (_engineInitialized == 1) return true;

        try
        {
            // Zero keeps ThorVG on the calling thread. Even in this mode the
            // C API requires every draw to be followed by canvas_sync before
            // the target buffer is unpinned or the canvas is destroyed.
            // Shutdown is coordinated explicitly by ShutdownForProcessExit(),
            // not by finalizer/process-exit races.
            var r = tvg_engine_init(0);
            if (r != Tvg_Result.Success)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[ThorVgRasterizer] tvg_engine_init failed: {r}");
                return false;
            }
            _engineInitialized = 1;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ThorVgRasterizer] thorvg.dll not found: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ThorVgRasterizer] engine init threw: {ex}");
            return false;
        }
    }

    private static bool TryEnterRasterizer()
    {
        lock (_engineLock)
        {
            if (!EnsureEngineLocked())
                return false;

            if (_activeRasterizations == 0)
                _idle.Reset();

            _activeRasterizations++;
            return true;
        }
    }

    private static void LeaveRasterizer()
    {
        if (Interlocked.Decrement(ref _activeRasterizations) == 0)
            _idle.Set();
    }

    internal static void BeginShutdown()
    {
        lock (_engineLock)
        {
            _shutdownRequested = true;
        }
    }

    /// <summary>
    /// Stops new native SVG rasterization work, waits briefly for active
    /// raster jobs, then terminates ThorVG before the CRT unload path runs.
    /// </summary>
    internal static void ShutdownForProcessExit(TimeSpan timeout)
    {
        bool shouldTerminate = false;

        lock (_engineLock)
        {
            _shutdownRequested = true;
            if (_engineInitialized == 0)
                return;

            shouldTerminate = _activeRasterizations == 0;
        }

        if (!shouldTerminate)
        {
            try
            {
                shouldTerminate = _idle.Wait(timeout);
            }
            catch (ObjectDisposedException)
            {
                shouldTerminate = false;
            }
        }

        if (!shouldTerminate)
        {
            MarkdownDiagnostics.WriteLine(
                "[ThorVgRasterizer] skipped tvg_engine_term because rasterization did not become idle before shutdown.");
            return;
        }

        lock (_engineLock)
        {
            if (_engineInitialized == 0 || _activeRasterizations != 0)
                return;

            try
            {
                var r = tvg_engine_term();
                if (r != Tvg_Result.Success)
                {
                    MarkdownDiagnostics.WriteLine(
                        $"[ThorVgRasterizer] tvg_engine_term returned: {r}");
                }
            }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[ThorVgRasterizer] tvg_engine_term threw: {ex}");
            }
            finally
            {
                _engineInitialized = 0;
            }
        }
    }

    /// <summary>
    /// Parses <paramref name="svgBytes"/> and rasterizes it to a bitmap sized
    /// <paramref name="targetWidthPx"/> by <paramref name="targetHeightPx"/>.
    /// ThorVG handles the viewBox + preserveAspectRatio rules itself when
    /// <c>tvg_picture_set_size</c> is given the target size; the picture
    /// stays uniformly scaled to fit, matching how browsers render
    /// <c>&lt;img src=…&gt;</c>.
    /// </summary>
    /// <returns>
    /// The rasterized BGRA buffer, or <c>null</c> if the SVG could not be
    /// parsed (caller falls back to the alt-text placeholder).
    /// </returns>
    public static unsafe Raster? Rasterize(
        byte[] svgBytes,
        int targetWidthPx,
        int targetHeightPx,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (svgBytes is null || svgBytes.Length == 0) return null;
        if (targetWidthPx <= 0 || targetHeightPx <= 0) return null;
        // Guard against int32 overflow in the allocation below. A single
        // raster larger than ~64 MB (4096x4096 BGRA) is well beyond any
        // sensible markdown image and almost certainly a corrupt or
        // adversarial intrinsic-size; reject before we wrap.
        long byteCount = (long)targetWidthPx * targetHeightPx * 4L;
        if (byteCount <= 0 || byteCount > 64L * 1024L * 1024L) return null;
        if (!TryEnterRasterizer()) return null;

        // Output buffer is uint32 per pixel — width*height 32-bit cells.
        var bgra = new byte[(int)byteCount];

        try
        {
            using SafeTvgCanvasHandle canvas = SafeTvgCanvasHandle.FromNative(
                tvg_swcanvas_create(Tvg_Engine_Option.Default));
            cancellationToken.ThrowIfCancellationRequested();
            if (canvas.IsInvalid)
            {
                MarkdownDiagnostics.WriteLine("[ThorVgRasterizer] swcanvas_create returned null");
                return null;
            }

            fixed (byte* outPtr = bgra)
            {
                // Target the caller-owned buffer with BGRA premultiplied,
                // which is what CanvasBitmap.CreateFromBytes expects when
                // told B8G8R8A8UIntNormalized. Stride is in *pixels* per
                // the ThorVG C-API (not bytes).
                var st = tvg_swcanvas_set_target(
                    canvas.DangerousGetHandle(),
                    (uint*)outPtr,
                    (uint)targetWidthPx,
                    (uint)targetWidthPx,
                    (uint)targetHeightPx,
                    Tvg_Colorspace.Argb8888);
                if (st != Tvg_Result.Success)
                {
                    MarkdownDiagnostics.WriteLine($"[ThorVgRasterizer] set_target failed: {st}");
                    return null;
                }

                using SafeTvgPaintHandle picture = SafeTvgPaintHandle.FromNative(tvg_picture_new());
                cancellationToken.ThrowIfCancellationRequested();
                if (picture.IsInvalid)
                {
                    MarkdownDiagnostics.WriteLine("[ThorVgRasterizer] picture_new returned null");
                    return null;
                }

                fixed (byte* svgPtr = svgBytes)
                {
                    // copy=true — ThorVG copies the SVG data into its own
                    // storage so we can unpin the buffer immediately.
                    var lr = tvg_picture_load_data(
                        picture.DangerousGetHandle(), svgPtr, (uint)svgBytes.Length,
                        mimetype: "svg", rpath: null, copy: true);
                    if (lr != Tvg_Result.Success)
                    {
                        MarkdownDiagnostics.WriteLine($"[ThorVgRasterizer] picture_load_data failed: {lr}");
                        return null;
                    }
                }

                var sizeResult = tvg_picture_set_size(
                    picture.DangerousGetHandle(), targetWidthPx, targetHeightPx);
                if (sizeResult != Tvg_Result.Success)
                {
                    MarkdownDiagnostics.WriteLine($"[ThorVgRasterizer] picture_set_size failed: {sizeResult}");
                    return null;
                }
                cancellationToken.ThrowIfCancellationRequested();

                // tvg_canvas_add transfers ownership of `picture` to the
                // canvas — the canvas will destroy the picture when the
                // canvas itself is destroyed, so we must NOT call
                // tvg_paint_rel after this.
                var ar = tvg_canvas_add(canvas.DangerousGetHandle(), picture.DangerousGetHandle());
                if (ar != Tvg_Result.Success)
                {
                    MarkdownDiagnostics.WriteLine($"[ThorVgRasterizer] canvas_add failed: {ar}");
                    return null;
                }
                picture.RelinquishOwnership();

                // draw performs an implicit update. Keep draw and sync in one
                // non-cancelable native lifetime: ThorVG requires sync after
                // every draw regardless of its configured worker count. A
                // cancellation observed between these calls could otherwise
                // unpin bgra and destroy the canvas while native code still
                // references them, which is a process-fatal CFG violation.
                Tvg_Result dr = Tvg_Result.Unknown;
                Tvg_Result sr = Tvg_Result.Unknown;
                bool drawEntered = false;
                try
                {
                    drawEntered = true;
                    dr = tvg_canvas_draw(canvas.DangerousGetHandle(), clear: true);
                }
                finally
                {
                    if (drawEntered)
                        sr = tvg_canvas_sync(canvas.DangerousGetHandle());
                }

                if (dr != Tvg_Result.Success)
                {
                    MarkdownDiagnostics.WriteLine($"[ThorVgRasterizer] canvas_draw failed: {dr}");
                    return null;
                }
                if (sr != Tvg_Result.Success)
                {
                    MarkdownDiagnostics.WriteLine($"[ThorVgRasterizer] canvas_sync failed: {sr}");
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            // ThorVG writes ARGB8888 premultiplied = native-endian uint32.
            // On little-endian Windows that lays out in memory as B, G, R, A
            // which is exactly what CanvasBitmap waits for
            // B8G8R8A8UIntNormalized. No swizzle needed.
            return new Raster(bgra, targetWidthPx, targetHeightPx);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[ThorVgRasterizer] rasterize threw: {ex}");
            return null;
        }
        finally
        {
            LeaveRasterizer();
        }
    }

    internal static unsafe Version? TryGetNativeVersion()
    {
        try
        {
            uint major = 0;
            uint minor = 0;
            uint micro = 0;
            IntPtr value = IntPtr.Zero;
            Tvg_Result result = tvg_engine_version(&major, &minor, &micro, &value);
            return result == Tvg_Result.Success && value != IntPtr.Zero
                ? new Version((int)major, (int)minor, (int)micro)
                : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[ThorVgRasterizer] native version probe threw: {ex}");
            return null;
        }
    }
}
