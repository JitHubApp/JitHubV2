using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using WinRT;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Text;

/// <summary>
/// Independent same-era oracle for the documented pre-Context7 color-font
/// path. It deliberately owns its ABI declarations and vtable calls instead
/// of calling the production renderer, so this test can catch production
/// translation, palette, and color-layer drawing defects.
/// </summary>
internal sealed unsafe partial class LegacyDirectWriteColorReference : IDisposable
{
    private static readonly Guid IidCanvasResourceWrapperNative = new("5f10688d-ea55-4d55-a3b0-4ddb55c0c20a");
    private static readonly Guid IidD2D1DeviceContext4 = new("8c427831-3d90-4476-b647-c4fae349e4db");
    private static readonly Guid IidD2D1Brush = new("2cd906a8-12e2-11dc-9fed-001143a055f9");
    private static readonly Guid IidDWriteFontFaceReference = new("5e7fa7ca-dde3-424c-89f0-9fcd6fed58cd");
    private static readonly Guid IidDWriteFactory4 = new("4b0b5bd3-0797-4549-8ac5-fe915cc53856");

    // Header-derived ID2D1DeviceContext4/ID2D1RenderTarget and DirectWrite
    // slots. These are intentionally local to this test oracle.
    private const int Factory4TranslateColorGlyphRun = 40;
    private const int EnumeratorMoveNext = 3;
    private const int Enumerator1GetCurrentRun = 5;
    private const int ContextDrawGlyphRun = 82;
    private const int ContextCreateSolidColorBrush = 8;
    private const int SolidColorBrushSetColor = 8;
    private const int Context4DrawColorBitmapGlyphRun = 111;
    private const int Context4DrawSvgGlyphRun = 112;
    private const int DWriteNoColor = unchecked((int)0x8898500C);
    private const uint LegacyImageFormats = 0xFF;
    private const ushort NoPalette = 0xFFFF;

    private nint _context4;
    private nint _factory4;
    private nint _paletteBrush;

    public LegacyDirectWriteColorReference(CanvasDrawingSession drawing)
    {
        _context4 = GetNativeResource(drawing, IidD2D1DeviceContext4);
        try
        {
            Guid iid = IidDWriteFactory4;
            int hr = DWriteCreateFactory(0, ref iid, out _factory4);
            ThrowIfFailed(hr);
            if (_factory4 == 0)
                throw new InvalidOperationException("DWriteCreateFactory returned a null IDWriteFactory4.");
        }
        catch
        {
            Release(ref _factory4);
            Release(ref _context4);
            throw;
        }
    }

    public bool TryDraw(
        Vector2 baseline,
        CapturedGlyphRun source,
        CanvasGlyph[] glyphs,
        ICanvasBrush foreground,
        Matrix3x2 transform,
        float dpi)
    {
        if (glyphs.Length == 0 || IsTransparent(foreground))
            return true;

        nint fontFaceReference = 0;
        nint fontFace = 0;
        nint foregroundBrush = 0;
        nint enumerator = 0;
        ushort[] indices = new ushort[glyphs.Length];
        float[] advances = new float[glyphs.Length];
        ReferenceGlyphOffset[] offsets = new ReferenceGlyphOffset[glyphs.Length];
        try
        {
            fontFaceReference = GetNativeResource(source.FontFace, IidDWriteFontFaceReference);
            CreateFontFace(fontFaceReference, &fontFace);

            foregroundBrush = GetNativeResource(foreground, IidD2D1Brush);
            CopyGlyphs(glyphs, indices, advances, offsets);

            var nativeRun = new ReferenceGlyphRun
            {
                FontFace = fontFace,
                FontEmSize = source.FontSize,
                GlyphCount = checked((uint)glyphs.Length),
                IsSideways = source.IsSideways ? 1 : 0,
                BidiLevel = source.BidiLevel,
            };

            fixed (ushort* indicesPointer = indices)
            fixed (float* advancesPointer = advances)
            fixed (ReferenceGlyphOffset* offsetsPointer = offsets)
            {
                nativeRun.GlyphIndices = indicesPointer;
                nativeRun.GlyphAdvances = advancesPointer;
                nativeRun.GlyphOffsets = offsetsPointer;
                int measuringMode = source.MeasuringMode switch
                {
                    CanvasTextMeasuringMode.Natural => 0,
                    CanvasTextMeasuringMode.GdiClassic => 1,
                    CanvasTextMeasuringMode.GdiNatural => 2,
                    _ => throw new ArgumentOutOfRangeException(nameof(source)),
                };
                ReferenceMatrix worldAndDpi = ToWorldAndDpi(transform, dpi);
                int translateHr = TranslateColorGlyphRun(
                    baseline, &nativeRun, measuringMode, &worldAndDpi, &enumerator);
                if (translateHr == DWriteNoColor)
                    return false;
                ThrowIfFailed(translateHr);
                if (enumerator == 0)
                    throw new InvalidOperationException("TranslateColorGlyphRun returned no enumerator.");

                DrawTranslatedRuns(enumerator, foregroundBrush);
                return true;
            }
        }
        finally
        {
            Release(ref enumerator);
            Release(ref foregroundBrush);
            Release(ref fontFace);
            Release(ref fontFaceReference);
        }
    }

    private static void CreateFontFace(nint fontFaceReference, nint* fontFace)
    {
        var referenceVtable = *(nint**)fontFaceReference;
        var createFontFace = (delegate* unmanaged[Stdcall]<nint, nint*, int>)referenceVtable[3];
        int createHr = createFontFace(fontFaceReference, fontFace);
        ThrowIfFailed(createHr);
        if (*fontFace == 0)
            throw new InvalidOperationException("IDWriteFontFaceReference returned a null face.");
    }

    private static void CopyGlyphs(
        CanvasGlyph[] glyphs,
        ushort[] indices,
        float[] advances,
        ReferenceGlyphOffset[] offsets)
    {
        for (int index = 0; index < glyphs.Length; index++)
        {
            if ((uint)glyphs[index].Index > ushort.MaxValue)
                throw new InvalidOperationException("DirectWrite returned an invalid glyph index.");
            indices[index] = (ushort)glyphs[index].Index;
            advances[index] = glyphs[index].Advance;
            offsets[index] = new ReferenceGlyphOffset(
                glyphs[index].AdvanceOffset,
                glyphs[index].AscenderOffset);
        }
    }

    private int TranslateColorGlyphRun(
        Vector2 baseline,
        ReferenceGlyphRun* nativeRun,
        int measuringMode,
        ReferenceMatrix* worldAndDpi,
        nint* enumerator)
    {
        var factoryVtable = *(nint**)_factory4;
        var translate = (delegate* unmanaged[Stdcall]<
            nint,
            Vector2,
            ReferenceGlyphRun*,
            nint,
            uint,
            int,
            ReferenceMatrix*,
            uint,
            nint*,
            int>)factoryVtable[Factory4TranslateColorGlyphRun];
        return translate(
            _factory4,
            baseline,
            nativeRun,
            0,
            LegacyImageFormats,
            measuringMode,
            worldAndDpi,
            0,
            enumerator);
    }

    public void Dispose()
    {
        Release(ref _paletteBrush);
        Release(ref _factory4);
        Release(ref _context4);
    }

    private void DrawTranslatedRuns(nint enumerator, nint foregroundBrush)
    {
        var enumeratorVtable = *(nint**)enumerator;
        var moveNext = (delegate* unmanaged[Stdcall]<nint, int*, int>)enumeratorVtable[EnumeratorMoveNext];
        var getCurrentRun = (delegate* unmanaged[Stdcall]<nint, ReferenceColorGlyphRun1**, int>)enumeratorVtable[Enumerator1GetCurrentRun];
        while (true)
        {
            int hasRun = 0;
            ThrowIfFailed(moveNext(enumerator, &hasRun));
            if (hasRun == 0)
                return;

            ReferenceColorGlyphRun1* current = null;
            ThrowIfFailed(getCurrentRun(enumerator, &current));
            if (current is null)
                throw new InvalidOperationException("The DirectWrite enumerator returned a null run.");

            nint brush = foregroundBrush;
            if (current->Base.PaletteIndex != NoPalette)
            {
                if (_paletteBrush == 0)
                    _paletteBrush = CreatePaletteBrush(current->Base.RunColor);
                else
                    SetPaletteBrushColor(_paletteBrush, current->Base.RunColor);
                brush = _paletteBrush;
            }

            DrawTranslatedRun(current, brush);
        }
    }

    private void DrawTranslatedRun(ReferenceColorGlyphRun1* colorRun, nint brush)
    {
        int format = colorRun->GlyphImageFormat;
        Vector2 baseline = new(colorRun->Base.BaselineOriginX, colorRun->Base.BaselineOriginY);
        var vtable = *(nint**)_context4;
        if (format == 0 || (format & ~0x7) == 0)
        {
            var drawGlyphRun = (delegate* unmanaged[Stdcall]<
                nint,
                Vector2,
                ReferenceGlyphRun*,
                nint,
                nint,
                int,
                void>)vtable[ContextDrawGlyphRun];
            drawGlyphRun(
                _context4,
                baseline,
                &colorRun->Base.GlyphRun,
                colorRun->Base.GlyphRunDescription,
                brush,
                colorRun->MeasuringMode);
            return;
        }

        if (format is 0x10 or 0x20 or 0x40 or 0x80)
        {
            var drawBitmap = (delegate* unmanaged[Stdcall]<
                nint,
                int,
                Vector2,
                ReferenceGlyphRun*,
                int,
                int,
                void>)vtable[Context4DrawColorBitmapGlyphRun];
            drawBitmap(
                _context4,
                format,
                baseline,
                &colorRun->Base.GlyphRun,
                colorRun->MeasuringMode,
                0);
            return;
        }

        if (format == 0x8)
        {
            var drawSvg = (delegate* unmanaged[Stdcall]<
                nint,
                Vector2,
                ReferenceGlyphRun*,
                nint,
                nint,
                uint,
                int,
                void>)vtable[Context4DrawSvgGlyphRun];
            drawSvg(
                _context4,
                baseline,
                &colorRun->Base.GlyphRun,
                brush,
                0,
                0,
                colorRun->MeasuringMode);
            return;
        }

        throw new NotSupportedException($"The test oracle does not cover legacy glyph format 0x{format:X}.");
    }

    private nint CreatePaletteBrush(ReferenceColor color)
    {
        nint brush = 0;
        var vtable = *(nint**)_context4;
        var create = (delegate* unmanaged[Stdcall]<nint, ReferenceColor*, nint, nint*, int>)vtable[ContextCreateSolidColorBrush];
        int hr = create(_context4, &color, 0, &brush);
        if (hr < 0)
        {
            Release(ref brush);
            Marshal.ThrowExceptionForHR(hr);
        }
        if (brush == 0)
            throw new InvalidOperationException("Direct2D returned a null palette brush.");
        return brush;
    }

    private static void SetPaletteBrushColor(nint brush, ReferenceColor color)
    {
        var vtable = *(nint**)brush;
        var setColor = (delegate* unmanaged[Stdcall]<nint, ReferenceColor*, void>)vtable[SolidColorBrushSetColor];
        setColor(brush, &color);
    }

    private static bool IsTransparent(ICanvasBrush brush)
        => brush.Opacity <= 0f || brush is CanvasSolidColorBrush solid && solid.Color.A == 0;

    private static ReferenceMatrix ToWorldAndDpi(Matrix3x2 transform, float dpi)
    {
        if (!float.IsFinite(dpi) || dpi <= 0f)
            throw new ArgumentOutOfRangeException(nameof(dpi));
        Matrix3x2 transformed = Matrix3x2.Multiply(transform, Matrix3x2.CreateScale(dpi / 96f));
        return new ReferenceMatrix(
            transformed.M11,
            transformed.M12,
            transformed.M21,
            transformed.M22,
            transformed.M31,
            transformed.M32);
    }

    private static nint GetNativeResource(object wrapper, Guid requestedIid)
    {
        if (wrapper is not IWinRTObject winrtObject)
            throw new InvalidOperationException("The Win2D wrapper did not expose its projected WinRT object.");

        nint inspectable = winrtObject.NativeObject.GetRef();
        nint nativeWrapper = 0;
        try
        {
            nativeWrapper = QueryInterface(inspectable, IidCanvasResourceWrapperNative);
            var vtable = *(nint**)nativeWrapper;
            var getResource = (delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)vtable[3];
            nint resource = 0;
        Guid iid = requestedIid;
        int hr = getResource(nativeWrapper, 0, 0f, &iid, &resource);
            try
            {
                ThrowIfFailed(hr);
                if (resource == 0)
                    throw new InvalidOperationException("ICanvasResourceWrapperNative returned a null resource.");
                return resource;
            }
            catch
            {
                Release(ref resource);
                throw;
            }
        }
        finally
        {
            Release(ref nativeWrapper);
            Release(ref inspectable);
        }
    }

    private static nint QueryInterface(nint source, Guid iid)
    {
        nint result = 0;
        var vtable = *(nint**)source;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
        Guid requestedIid = iid;
        int hr = queryInterface(source, &requestedIid, &result);
        try
        {
            ThrowIfFailed(hr);
            if (result == 0)
                throw new InvalidOperationException("QueryInterface succeeded with a null result.");
            return result;
        }
        catch
        {
            Release(ref result);
            throw;
        }
    }

    private static void ThrowIfFailed(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    private static void Release(ref nint value)
    {
        nint pointer = value;
        value = 0;
        if (pointer == 0)
            return;
        var vtable = *(nint**)pointer;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        _ = release(pointer);
    }

    [LibraryImport("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
    private static partial int DWriteCreateFactory(uint factoryType, ref Guid iid, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct ReferenceGlyphOffset(float advanceOffset, float ascenderOffset)
    {
        public float AdvanceOffset = advanceOffset;
        public float AscenderOffset = ascenderOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReferenceGlyphRun
    {
        public nint FontFace;
        public float FontEmSize;
        public uint GlyphCount;
        public ushort* GlyphIndices;
        public float* GlyphAdvances;
        public ReferenceGlyphOffset* GlyphOffsets;
        public int IsSideways;
        public uint BidiLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReferenceColorGlyphRun
    {
        public ReferenceGlyphRun GlyphRun;
        public nint GlyphRunDescription;
        public float BaselineOriginX;
        public float BaselineOriginY;
        public ReferenceColor RunColor;
        public ushort PaletteIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReferenceColorGlyphRun1
    {
        // This test-only declaration follows the SDK's derived-structure
        // inheritance padding and is not shared with the production ABI.
        public ReferenceColorGlyphRun Base;
        public int GlyphImageFormat;
        public int MeasuringMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReferenceColor
    {
        public float Red;
        public float Green;
        public float Blue;
        public float Alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReferenceMatrix(float m11, float m12, float m21, float m22, float dx, float dy)
    {
        public float M11 = m11;
        public float M12 = m12;
        public float M21 = m21;
        public float M22 = m22;
        public float Dx = dx;
        public float Dy = dy;
    }
}
