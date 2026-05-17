// SPDX-License-Identifier: MIT
//
// P/Invoke surface for the ThorVG (Thor Vector Graphics) C-API.
// We only bind the subset required to:
//   1. Initialize / terminate the engine,
//   2. Create a software canvas that targets a caller-owned pixel buffer,
//   3. Load an SVG from in-memory bytes via a Picture,
//   4. Drive a single draw+sync cycle,
//   5. Tear everything down deterministically.
//
// The native binary ships as `thorvg.dll` next to the managed assembly
// (see MarkdownRenderer.csproj — Content item under native\win-x64\).
//
// Marshaling notes:
//  * Tvg_Canvas / Tvg_Paint are opaque `void*` handles → IntPtr.
//  * Pixel buffer is passed by pinned pointer (uint32_t*) to avoid copies.
//  * SVG payload bytes are passed by pinned pointer (`const char*`) with the
//    `copy=true` flag so ThorVG owns the data after `tvg_picture_load_data`
//    returns; the caller can unpin immediately.
//  * `LibraryImport` (source-generated) is preferred over `DllImport` for
//    NativeAOT compatibility.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Layout.Boxes;

internal static partial class ThorVgNative
{
    private const string DllName = "thorvg";

    static ThorVgNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(ThorVgNative).Assembly, ResolveThorVg);
    }

    private static IntPtr ResolveThorVg(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var candidate in EnumerateNativeCandidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> EnumerateNativeCandidates()
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => string.Empty,
        };

        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "thorvg.dll");
        yield return Path.Combine(baseDir, "MarkdownRenderer", "thorvg.dll");

        if (!string.IsNullOrEmpty(rid))
        {
            yield return Path.Combine(baseDir, "runtimes", rid, "native", "thorvg.dll");
            yield return Path.Combine(baseDir, "native", rid, "thorvg.dll");
        }
    }

    public enum Tvg_Result : int
    {
        Success = 0,
        InvalidArgument,
        InsufficientCondition,
        FailedAllocation,
        MemoryCorruption,
        NotSupported,
        Unknown = 255,
    }

    [Flags]
    public enum Tvg_Engine_Option : int
    {
        None = 0,
        Default = 1 << 0,
        SmartRender = 1 << 1,
    }

    public enum Tvg_Colorspace : int
    {
        Abgr8888 = 0,
        Argb8888 = 1,
        Abgr8888S = 2,
        Argb8888S = 3,
        Unknown = 255,
    }

    // -- Engine -----------------------------------------------------------

    [LibraryImport(DllName)]
    public static partial Tvg_Result tvg_engine_init(uint threads);

    [LibraryImport(DllName)]
    public static partial Tvg_Result tvg_engine_term();

    // -- Canvas (software backend) ----------------------------------------

    [LibraryImport(DllName)]
    public static partial IntPtr tvg_swcanvas_create(Tvg_Engine_Option op);

    [LibraryImport(DllName)]
    public static unsafe partial Tvg_Result tvg_swcanvas_set_target(
        IntPtr canvas, uint* buffer, uint stride, uint w, uint h, Tvg_Colorspace cs);

    [LibraryImport(DllName)]
    public static partial Tvg_Result tvg_canvas_destroy(IntPtr canvas);

    [LibraryImport(DllName)]
    public static partial Tvg_Result tvg_canvas_add(IntPtr canvas, IntPtr paint);

    [LibraryImport(DllName)]
    public static partial Tvg_Result tvg_canvas_update(IntPtr canvas);

    [LibraryImport(DllName)]
    public static partial Tvg_Result tvg_canvas_draw(IntPtr canvas, [MarshalAs(UnmanagedType.U1)] bool clear);

    [LibraryImport(DllName)]
    public static partial Tvg_Result tvg_canvas_sync(IntPtr canvas);

    // -- Picture (SVG payload owner) --------------------------------------

    [LibraryImport(DllName)]
    public static partial IntPtr tvg_picture_new();

    /// <summary>
    /// Loads an SVG from raw bytes. The <paramref name="mimetype"/> argument
    /// is a hint ("svg"); when null/empty ThorVG auto-detects.
    /// <paramref name="rpath"/> is the path used to resolve relative refs in
    /// the SVG (e.g. xlink:href to external sources); we pass null because
    /// we only handle self-contained SVGs.
    /// When <paramref name="copy"/> is true, ThorVG copies the buffer into
    /// internal storage so the caller can free/unpin immediately on return.
    /// </summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial Tvg_Result tvg_picture_load_data(
        IntPtr picture, byte* data, uint size, string? mimetype, string? rpath,
        [MarshalAs(UnmanagedType.U1)] bool copy);

    [LibraryImport(DllName)]
    public static partial Tvg_Result tvg_picture_set_size(IntPtr picture, float w, float h);

    [LibraryImport(DllName)]
    public static unsafe partial Tvg_Result tvg_picture_get_size(IntPtr picture, float* w, float* h);
}
