using System.Runtime.InteropServices;
using Xunit;
using MarkdownRenderer.Layout.Internal.Text;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Text;

/// <summary>
/// Checks the DirectWrite and Direct2D vtable slots and native structure
/// layouts that the script baseline color glyph renderer declares, for the
/// running pointer size and for explicit 32-bit layouts.
/// </summary>
internal static class DirectWriteAbiLayoutAssertions
{
    internal static void AssertVtableSlotsAndGlyphOffsetLayout()
    {
        Assert.Equal(
            122,
            ScriptBaselineNativeColorGlyphRenderer.DrawGlyphRunWithColorSupportVtableSlot);
        Assert.Equal(40, ScriptBaselineNativeColorGlyphRenderer.Factory4TranslateColorGlyphRunVtableSlot);
        Assert.Equal(3, ScriptBaselineNativeColorGlyphRenderer.ColorGlyphRunEnumeratorMoveNextVtableSlot);
        Assert.Equal(5, ScriptBaselineNativeColorGlyphRenderer.ColorGlyphRunEnumerator1GetCurrentRunVtableSlot);
        Assert.Equal(82, ScriptBaselineNativeColorGlyphRenderer.DeviceContextDrawGlyphRunVtableSlot);
        Assert.Equal(8, ScriptBaselineNativeColorGlyphRenderer.RenderTargetCreateSolidColorBrushVtableSlot);
        Assert.Equal(8, ScriptBaselineNativeColorGlyphRenderer.SolidColorBrushSetColorVtableSlot);
        Assert.Equal(111, ScriptBaselineNativeColorGlyphRenderer.DeviceContext4DrawColorBitmapGlyphRunVtableSlot);
        Assert.Equal(112, ScriptBaselineNativeColorGlyphRenderer.DeviceContext4DrawSvgGlyphRunVtableSlot);
        Assert.Equal(8, Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset>());
        Assert.Equal(0, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset.AdvanceOffset)));
        Assert.Equal(4, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset.AscenderOffset)));
    }

    /// <summary>Checks <c>DWRITE_GLYPH_RUN</c> for <paramref name="pointerSize"/> and for x86.</summary>
    internal static void AssertGlyphRunLayout(int pointerSize)
    {
        Assert.Equal(pointerSize == 8 ? 48 : 32,
            Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>());
        Assert.Equal(0, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.FontFace)));
        Assert.Equal(pointerSize, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.FontEmSize)));
        Assert.Equal(pointerSize + 4, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.GlyphCount)));
        Assert.Equal(pointerSize + 8, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.GlyphIndices)));
        Assert.Equal(pointerSize + 8 + pointerSize, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.GlyphAdvances)));
        Assert.Equal(pointerSize + 8 + (pointerSize * 2), OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.GlyphOffsets)));
        Assert.Equal(pointerSize + 8 + (pointerSize * 3), OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.IsSideways)));
        Assert.Equal(pointerSize + 12 + (pointerSize * 3), OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.BidiLevel)));

        // The test target is x64 today, but preserve explicit x86 ABI coverage
        // even when this suite is not executable as a 32-bit WinUI process.
        Assert.Equal(32, Marshal.SizeOf<NativeGlyphRun32>());
        Assert.Equal(0, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.FontFace)));
        Assert.Equal(4, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.FontEmSize)));
        Assert.Equal(8, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.GlyphCount)));
        Assert.Equal(12, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.GlyphIndices)));
        Assert.Equal(16, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.GlyphAdvances)));
        Assert.Equal(20, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.GlyphOffsets)));
        Assert.Equal(24, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.IsSideways)));
        Assert.Equal(28, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.BidiLevel)));
    }

    /// <summary>Checks <c>DWRITE_COLOR_GLYPH_RUN</c> and <c>DWRITE_COLOR_GLYPH_RUN1</c> for <paramref name="pointerSize"/> and for x86.</summary>
    internal static void AssertColorGlyphRunLayout(int pointerSize)
    {
        Assert.Equal(16, Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeColor>());
        Assert.Equal(24, Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeDWriteMatrix>());
        Assert.Equal(pointerSize == 8 ? 88 : 64,
            Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>());
        Assert.Equal(0, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.GlyphRun)));
        Assert.Equal(pointerSize == 8 ? 48 : 32,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.GlyphRunDescription)));
        Assert.Equal(pointerSize == 8 ? 56 : 36,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.BaselineOriginX)));
        Assert.Equal(pointerSize == 8 ? 64 : 44,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.RunColor)));
        Assert.Equal(pointerSize == 8 ? 80 : 60,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.PaletteIndex)));
        Assert.Equal(pointerSize == 8 ? 96 : 72,
            Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1>());
        Assert.Equal(0, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1.Base)));
        Assert.Equal(pointerSize == 8 ? 88 : 64,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1.GlyphImageFormat)));
        Assert.Equal(pointerSize == 8 ? 92 : 68,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1.MeasuringMode)));

        Assert.Equal(72, Marshal.SizeOf<NativeColorGlyphRun32>());
        Assert.Equal(32, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.GlyphRunDescription)));
        Assert.Equal(36, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.BaselineOriginX)));
        Assert.Equal(44, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.RunColor)));
        Assert.Equal(60, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.PaletteIndex)));
        Assert.Equal(64, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.GlyphImageFormat)));
        Assert.Equal(68, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.MeasuringMode)));
    }

    private static int OffsetOf<T>(string fieldName) where T : struct
        => Marshal.OffsetOf<T>(fieldName).ToInt32();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGlyphRun32
    {
        public int FontFace;
        public float FontEmSize;
        public uint GlyphCount;
        public int GlyphIndices;
        public int GlyphAdvances;
        public int GlyphOffsets;
        public int IsSideways;
        public uint BidiLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeColorGlyphRun32
    {
        public NativeGlyphRun32 GlyphRun;
        public int GlyphRunDescription;
        public float BaselineOriginX;
        public float BaselineOriginY;
        public ScriptBaselineNativeColorGlyphRenderer.NativeColor RunColor;
        public ushort PaletteIndex;
        public ushort Reserved;
        public int GlyphImageFormat;
        public int MeasuringMode;
    }
}
