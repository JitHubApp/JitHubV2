using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using WinRT;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Uses the active Win2D Direct2D context to render an already-shaped glyph
/// run through Direct2D's color-font path. Every native resource is borrowed
/// only for this synchronous draw callback; no device-bound pointer survives
/// a drawing session or device-loss transition.
/// </summary>
internal sealed unsafe partial class ScriptBaselineNativeColorGlyphRenderer : IDisposable
{
    private static readonly Guid IidCanvasResourceWrapperNative = new("5f10688d-ea55-4d55-a3b0-4ddb55c0c20a");
    private static readonly Guid IidD2D1DeviceContext7 = new("ec891cf7-9b69-4851-9def-4e0915771e62");
    private static readonly Guid IidD2D1DeviceContext4 = new("8c427831-3d90-4476-b647-c4fae349e4db");
    private static readonly Guid IidD2D1Brush = new("2cd906a8-12e2-11dc-9fed-001143a055f9");
    private static readonly Guid IidDWriteFontFaceReference = new("5e7fa7ca-dde3-424c-89f0-9fcd6fed58cd");
    private static readonly Guid IidDWriteFactory4 = new("4b0b5bd3-0797-4549-8ac5-fe915cc53856");

    // Verified against d2d1.h, d2d1_1.h, d2d1_2.h, and d2d1_3.h
    // (Windows SDK 10.0.26100.0): IUnknown contributes slots 0–2;
    // ID2D1Resource, ID2D1RenderTarget, ID2D1DeviceContext, and Context1–6
    // add 1, 53, 35, 3, 11, 2, 7, 4, and 1 methods respectively. Context7
    // then adds GetPaintFeatureLevel at 120, DrawPaintGlyphRun at 121, and
    // DrawGlyphRunWithColorSupport at 122.
    internal const int DrawGlyphRunWithColorSupportVtableSlot = 122;
    internal const int Factory4TranslateColorGlyphRunVtableSlot = 40;
    internal const int ColorGlyphRunEnumeratorMoveNextVtableSlot = 3;
    internal const int ColorGlyphRunEnumerator1GetCurrentRunVtableSlot = 5;
    internal const int DeviceContextDrawGlyphRunVtableSlot = 82;
    internal const int RenderTargetCreateSolidColorBrushVtableSlot = 8;
    internal const int SolidColorBrushSetColorVtableSlot = 8;
    internal const int DeviceContext4DrawColorBitmapGlyphRunVtableSlot = 111;
    internal const int DeviceContext4DrawSvgGlyphRunVtableSlot = 112;
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int DWriteENoColor = unchecked((int)0x8898500C);
    private const int DWriteGlyphImageFormatTrueType = 0x1;
    private const int DWriteGlyphImageFormatCff = 0x2;
    private const int DWriteGlyphImageFormatColr = 0x4;
    private const int DWriteGlyphImageFormatSvg = 0x8;
    private const int DWriteGlyphImageFormatPng = 0x10;
    private const int DWriteGlyphImageFormatJpeg = 0x20;
    private const int DWriteGlyphImageFormatTiff = 0x40;
    private const int DWriteGlyphImageFormatPremultipliedBgra = 0x80;
    private const int DWriteSupportedLegacyGlyphImageFormats =
        DWriteGlyphImageFormatTrueType |
        DWriteGlyphImageFormatCff |
        DWriteGlyphImageFormatColr |
        DWriteGlyphImageFormatSvg |
        DWriteGlyphImageFormatPng |
        DWriteGlyphImageFormatJpeg |
        DWriteGlyphImageFormatTiff |
        DWriteGlyphImageFormatPremultipliedBgra;
    private const ushort DWriteNoPaletteIndex = 0xFFFF;
    private const uint DWriteFactoryTypeShared = 0;

    private nint _deviceContext7;
    private nint _deviceContext4;
    private nint _dwriteFactory4;

    internal ScriptBaselineNativeColorGlyphRenderer(
        CanvasDrawingSession drawingSession,
        bool forceLegacyColorSupport = false)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        if (!forceLegacyColorSupport &&
            TryGetNativeResource(drawingSession, IidD2D1DeviceContext7, out nint context7))
        {
            _deviceContext7 = context7;
            return;
        }

        // Context4 + Factory4 is the documented DirectWrite/Direct2D color-font
        // fallback for Windows 10 1809 through versions before Context7. The
        // package's minimum OS is 19041, where both interfaces are available.
        // Do not silently route a supported color glyph through Win2D's legacy
        // monochrome overload if either interface is missing.
        _deviceContext4 = GetNativeResource(drawingSession, IidD2D1DeviceContext4);
        try
        {
            _dwriteFactory4 = CreateDWriteFactory4();
        }
        catch
        {
            Release(ref _deviceContext4);
            throw;
        }
    }

    internal bool IsSupported => _deviceContext7 != 0 || _deviceContext4 != 0;
    internal bool IsLegacyPath => _deviceContext7 == 0 && _deviceContext4 != 0;

    internal static bool IsContext7Available(CanvasDrawingSession drawingSession)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        if (!TryGetNativeResource(drawingSession, IidD2D1DeviceContext7, out nint context7))
            return false;

        Release(ref context7);
        return true;
    }

    /// <summary>
    /// Returns false only when DirectWrite reports that this glyph run has no
    /// color representation. The caller then uses Win2D's normal monochrome
    /// path for that run; any other translation failure is surfaced.
    /// </summary>
    internal bool TryDrawGlyphRun(
        Vector2 baselineOrigin,
        CanvasFontFace fontFace,
        float fontSize,
        CanvasGlyph[] glyphs,
        bool isSideways,
        uint bidiLevel,
        ICanvasBrush foregroundBrush,
        CanvasTextMeasuringMode measuringMode,
        Matrix3x2 transform,
        float dpi)
    {
        nint deviceContext7 = _deviceContext7;
        nint deviceContext4 = _deviceContext4;
        nint dwriteFactory4 = _dwriteFactory4;
        if (deviceContext7 == 0 && (deviceContext4 == 0 || dwriteFactory4 == 0))
            throw new InvalidOperationException("The renderer has no supported native color-glyph path.");

        ArgumentNullException.ThrowIfNull(fontFace);
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(foregroundBrush);
        if (glyphs.Length == 0 || IsFullyTransparent(foregroundBrush))
            return true;

        nint fontFaceReference = 0;
        nint nativeFontFace = 0;
        nint nativeBrush = 0;
        nint colorLayerBrush = 0;
        nint colorRunEnumerator = 0;
        ushort[]? glyphIndices = null;
        float[]? glyphAdvances = null;
        NativeGlyphOffset[]? glyphOffsets = null;
        try
        {
            fontFaceReference = GetNativeResource(fontFace, IidDWriteFontFaceReference);
            nativeFontFace = CreateFontFace(fontFaceReference);
            nativeBrush = GetNativeResource(foregroundBrush, IidD2D1Brush);

            glyphIndices = new ushort[glyphs.Length];
            glyphAdvances = new float[glyphs.Length];
            glyphOffsets = new NativeGlyphOffset[glyphs.Length];
            for (int index = 0; index < glyphs.Length; index++)
            {
                CanvasGlyph glyph = glyphs[index];
                if ((uint)glyph.Index > ushort.MaxValue)
                    throw new InvalidOperationException("DirectWrite returned a glyph index outside its native UINT16 range.");

                glyphIndices[index] = (ushort)glyph.Index;
                glyphAdvances[index] = glyph.Advance;
                glyphOffsets[index] = new NativeGlyphOffset(glyph.AdvanceOffset, glyph.AscenderOffset);
            }

            var nativeRun = new NativeGlyphRun
            {
                FontFace = nativeFontFace,
                FontEmSize = fontSize,
                GlyphCount = checked((uint)glyphs.Length),
                IsSideways = isSideways ? 1 : 0,
                BidiLevel = bidiLevel,
            };

            fixed (ushort* indices = glyphIndices)
            fixed (float* advances = glyphAdvances)
            fixed (NativeGlyphOffset* offsets = glyphOffsets)
            {
                nativeRun.GlyphIndices = indices;
                nativeRun.GlyphAdvances = advances;
                nativeRun.GlyphOffsets = offsets;
                int nativeMeasuringMode = MapMeasuringMode(measuringMode);
                if (deviceContext7 != 0)
                {
                    DrawColorGlyphRun(
                        deviceContext7,
                        baselineOrigin,
                        &nativeRun,
                        nativeBrush,
                        nativeMeasuringMode);
                    return true;
                }

                int translationResult = TranslateColorGlyphRun(
                    dwriteFactory4,
                    baselineOrigin,
                    &nativeRun,
                    DWriteSupportedLegacyGlyphImageFormats,
                    nativeMeasuringMode,
                    ToWorldAndDpiTransform(transform, dpi),
                    &colorRunEnumerator);
                if (translationResult == DWriteENoColor)
                {
                    Release(ref colorRunEnumerator);
                    return false;
                }
                if (translationResult < 0)
                {
                    Release(ref colorRunEnumerator);
                    Marshal.ThrowExceptionForHR(translationResult);
                }
                if (colorRunEnumerator == 0)
                    throw new InvalidOperationException("TranslateColorGlyphRun succeeded without an enumerator.");

                DrawTranslatedColorGlyphRuns(
                    deviceContext4,
                    colorRunEnumerator,
                    nativeBrush,
                    ref colorLayerBrush);
                return true;
            }
        }
        finally
        {
            Release(ref colorRunEnumerator);
            Release(ref colorLayerBrush);
            if (glyphIndices is not null)
                Array.Clear(glyphIndices);
            if (glyphAdvances is not null)
                Array.Clear(glyphAdvances);
            if (glyphOffsets is not null)
                Array.Clear(glyphOffsets);
            Release(ref nativeBrush);
            Release(ref nativeFontFace);
            Release(ref fontFaceReference);
        }
    }

    public void Dispose()
    {
        Release(ref _dwriteFactory4);
        Release(ref _deviceContext4);
        Release(ref _deviceContext7);
    }

    private static bool IsFullyTransparent(ICanvasBrush brush)
    {
        if (brush.Opacity <= 0f)
            return true;

        // Selection clones use a solid color brush with alpha zero for ranges
        // that must not paint. Color glyph palette layers ignore that brush in
        // their artwork, so elide such runs explicitly to preserve selection.
        return brush is CanvasSolidColorBrush solidBrush && solidBrush.Color.A == 0;
    }

    private static int MapMeasuringMode(CanvasTextMeasuringMode measuringMode) => measuringMode switch
    {
        CanvasTextMeasuringMode.Natural => 0,
        CanvasTextMeasuringMode.GdiClassic => 1,
        CanvasTextMeasuringMode.GdiNatural => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(measuringMode)),
    };

    private static nint CreateFontFace(nint fontFaceReference)
    {
        nint fontFace = 0;
        var vtable = *(nint**)fontFaceReference;
        var createFontFace = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[3];
        int result = createFontFace(fontFaceReference, &fontFace);
        if (result < 0)
        {
            Release(ref fontFace);
            Marshal.ThrowExceptionForHR(result);
        }
        if (fontFace == 0)
            throw new InvalidOperationException("IDWriteFontFaceReference::CreateFontFace returned null.");
        return fontFace;
    }

    private static void DrawColorGlyphRun(
        nint deviceContext7,
        Vector2 baselineOrigin,
        NativeGlyphRun* glyphRun,
        nint foregroundBrush,
        int measuringMode)
    {
        var vtable = *(nint**)deviceContext7;
        var draw = (delegate* unmanaged[Stdcall]<
            nint,
            Vector2,
            NativeGlyphRun*,
            nint,
            nint,
            nint,
            uint,
            int,
            int,
            void>)vtable[DrawGlyphRunWithColorSupportVtableSlot];

        // The DirectWrite description and SVG glyph style are optional. The
        // original shaped glyph indices, advances, offsets, font face, measuring
        // mode, bidi level, active transform, and caller brush are all retained.
        draw(
            deviceContext7,
            baselineOrigin,
            glyphRun,
            0,
            foregroundBrush,
            0,
            0,
            measuringMode,
            0);
    }

    private static void DrawTranslatedColorGlyphRuns(
        nint deviceContext4,
        nint colorRunEnumerator,
        nint foregroundBrush,
        ref nint colorLayerBrush)
    {
        var enumeratorVtable = *(nint**)colorRunEnumerator;
        var moveNext = (delegate* unmanaged[Stdcall]<nint, int*, int>)enumeratorVtable[ColorGlyphRunEnumeratorMoveNextVtableSlot];
        var getCurrentRun = (delegate* unmanaged[Stdcall]<nint, NativeColorGlyphRun1**, int>)enumeratorVtable[ColorGlyphRunEnumerator1GetCurrentRunVtableSlot];

        while (true)
        {
            int hasRun = 0;
            int hr = moveNext(colorRunEnumerator, &hasRun);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
            if (hasRun == 0)
                return;

            NativeColorGlyphRun1* colorRun = null;
            hr = getCurrentRun(colorRunEnumerator, &colorRun);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
            if (colorRun is null)
                throw new InvalidOperationException("The color-glyph enumerator returned a null run.");

            nint runBrush = foregroundBrush;
            if (colorRun->Base.PaletteIndex != DWriteNoPaletteIndex)
            {
                if (colorLayerBrush == 0)
                {
                    colorLayerBrush = CreateSolidColorBrush(deviceContext4, colorRun->Base.RunColor);
                }
                else
                {
                    SetSolidColorBrushColor(colorLayerBrush, colorRun->Base.RunColor);
                }
                runBrush = colorLayerBrush;
            }

            DrawTranslatedColorGlyphRun(
                deviceContext4,
                colorRun,
                runBrush);
        }
    }

    private static void DrawTranslatedColorGlyphRun(
        nint deviceContext4,
        NativeColorGlyphRun1* colorRun,
        nint runBrush)
    {
        int imageFormat = colorRun->GlyphImageFormat;
        var translatedBaseline = new Vector2(colorRun->Base.BaselineOriginX, colorRun->Base.BaselineOriginY);
        var contextVtable = *(nint**)deviceContext4;
        switch (imageFormat)
        {
            case 0: // DWRITE_GLYPH_IMAGE_FORMATS_NONE
            {
                // A nonempty translated run must never disappear merely because
                // DirectWrite reports no specialized image format. Draw its
                // shaped outline as ordinary text using the current layer color.
                var drawGlyphRun = (delegate* unmanaged[Stdcall]<nint, Vector2, NativeGlyphRun*, nint, nint, int, void>)
                    contextVtable[DeviceContextDrawGlyphRunVtableSlot];
                drawGlyphRun(
                    deviceContext4,
                    translatedBaseline,
                    &colorRun->Base.GlyphRun,
                    colorRun->Base.GlyphRunDescription,
                    runBrush,
                    colorRun->MeasuringMode);
                return;
            }
            case DWriteGlyphImageFormatPng:
            case DWriteGlyphImageFormatJpeg:
            case DWriteGlyphImageFormatTiff:
            case DWriteGlyphImageFormatPremultipliedBgra:
            {
                var drawBitmap = (delegate* unmanaged[Stdcall]<nint, int, Vector2, NativeGlyphRun*, int, int, void>)
                    contextVtable[DeviceContext4DrawColorBitmapGlyphRunVtableSlot];
                drawBitmap(
                    deviceContext4,
                    imageFormat,
                    translatedBaseline,
                    &colorRun->Base.GlyphRun,
                    colorRun->MeasuringMode,
                    0);
                return;
            }
            case DWriteGlyphImageFormatSvg:
            {
                var drawSvg = (delegate* unmanaged[Stdcall]<nint, Vector2, NativeGlyphRun*, nint, nint, uint, int, void>)
                    contextVtable[DeviceContext4DrawSvgGlyphRunVtableSlot];
                drawSvg(
                    deviceContext4,
                    translatedBaseline,
                    &colorRun->Base.GlyphRun,
                    runBrush,
                    0,
                    0,
                    colorRun->MeasuringMode);
                return;
            }
            case int _ when IsSupportedOutlineImageFormat(imageFormat):
            {
                var drawGlyphRun = (delegate* unmanaged[Stdcall]<nint, Vector2, NativeGlyphRun*, nint, nint, int, void>)
                    contextVtable[DeviceContextDrawGlyphRunVtableSlot];
                drawGlyphRun(
                    deviceContext4,
                    translatedBaseline,
                    &colorRun->Base.GlyphRun,
                    colorRun->Base.GlyphRunDescription,
                    runBrush,
                    colorRun->MeasuringMode);
                return;
            }
            default:
                // Do not silently paint an unsupported color representation
                // with a monochrome fallback. The legacy request intentionally
                // omits COLR_PAINT_TREE, so this indicates a new/unexpected format.
                throw new NotSupportedException($"Direct2D 4 cannot draw color glyph image format 0x{imageFormat:X}.");
        }
    }

    internal static bool IsSupportedOutlineImageFormat(int imageFormat)
    {
        // DWRITE_GLYPH_IMAGE_FORMATS is a flags enum. DirectWrite can return
        // combined outline capabilities (for example TRUETYPE | COLR = 0x5),
        // not just one enum member. Treat only the bounded legacy outline bits
        // as DrawGlyphRun-compatible; raster, SVG, paint-tree, and unknown bits
        // must use their dedicated renderer or fail visibly.
        const int outlineFormats =
            DWriteGlyphImageFormatTrueType |
            DWriteGlyphImageFormatCff |
            DWriteGlyphImageFormatColr;

        return imageFormat != 0 &&
            (imageFormat & ~outlineFormats) == 0 &&
            (imageFormat & outlineFormats) != 0;
    }

    private static int TranslateColorGlyphRun(
        nint dwriteFactory4,
        Vector2 baselineOrigin,
        NativeGlyphRun* glyphRun,
        int desiredGlyphImageFormats,
        int measuringMode,
        NativeDWriteMatrix transform,
        nint* enumerator)
    {
        var vtable = *(nint**)dwriteFactory4;
        var translate = (delegate* unmanaged[Stdcall]<
            nint,
            Vector2,
            NativeGlyphRun*,
            nint,
            int,
            int,
            NativeDWriteMatrix*,
            uint,
            nint*,
            int>)vtable[Factory4TranslateColorGlyphRunVtableSlot];
        return translate(
            dwriteFactory4,
            baselineOrigin,
            glyphRun,
            0,
            desiredGlyphImageFormats,
            measuringMode,
            &transform,
            0,
            enumerator);
    }

    private static NativeDWriteMatrix ToWorldAndDpiTransform(Matrix3x2 transform, float dpi)
    {
        if (!float.IsFinite(dpi) || dpi <= 0f)
            throw new ArgumentOutOfRangeException(nameof(dpi));

        Matrix3x2 worldAndDpi = Matrix3x2.Multiply(
            transform,
            Matrix3x2.CreateScale(dpi / 96f));
        return new NativeDWriteMatrix(
            worldAndDpi.M11,
            worldAndDpi.M12,
            worldAndDpi.M21,
            worldAndDpi.M22,
            worldAndDpi.M31,
            worldAndDpi.M32);
    }

    private static nint CreateSolidColorBrush(nint deviceContext4, NativeColor color)
    {
        nint brush = 0;
        var vtable = *(nint**)deviceContext4;
        var create = (delegate* unmanaged[Stdcall]<nint, NativeColor*, nint, nint*, int>)
            vtable[RenderTargetCreateSolidColorBrushVtableSlot];
        int hr = create(deviceContext4, &color, 0, &brush);
        if (hr < 0)
        {
            Release(ref brush);
            Marshal.ThrowExceptionForHR(hr);
        }
        if (brush == 0)
            throw new InvalidOperationException("CreateSolidColorBrush succeeded without returning a brush.");
        return brush;
    }

    private static void SetSolidColorBrushColor(nint solidColorBrush, NativeColor color)
    {
        var vtable = *(nint**)solidColorBrush;
        var setColor = (delegate* unmanaged[Stdcall]<nint, NativeColor*, void>)
            vtable[SolidColorBrushSetColorVtableSlot];
        setColor(solidColorBrush, &color);
    }

    private static nint CreateDWriteFactory4()
    {
        nint factory = 0;
        Guid iid = IidDWriteFactory4;
        int hr = DWriteCreateFactory(DWriteFactoryTypeShared, &iid, &factory);
        if (hr < 0)
        {
            Release(ref factory);
            Marshal.ThrowExceptionForHR(hr);
        }
        if (factory == 0)
            throw new InvalidOperationException("DWriteCreateFactory succeeded without returning IDWriteFactory4.");
        return factory;
    }

    private static nint GetNativeResource(object wrapper, Guid resourceIid)
    {
        if (!TryGetNativeResource(wrapper, resourceIid, out nint resource))
            throw new COMException($"Win2D did not expose the required native resource {resourceIid:B}.", ENoInterface);
        return resource;
    }

    private static bool TryGetNativeResource(object wrapper, Guid resourceIid, out nint resource)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        if (wrapper is not IWinRTObject winrtObject)
            throw new InvalidOperationException("The Win2D object does not expose its projected WinRT reference.");

        nint inspectable = winrtObject.NativeObject.GetRef();
        nint resourceWrapper = 0;
        try
        {
            if (!TryQueryInterface(inspectable, IidCanvasResourceWrapperNative, out resourceWrapper))
                throw new InvalidOperationException("The Win2D object does not implement ICanvasResourceWrapperNative.");

            nint nativeResource = 0;
            Guid iid = resourceIid;
            var vtable = *(nint**)resourceWrapper;
            var getNativeResource = (delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)vtable[3];
            int result = getNativeResource(resourceWrapper, 0, 0f, &iid, &nativeResource);
            if (result == ENoInterface)
            {
                Release(ref nativeResource);
                resource = 0;
                return false;
            }
            if (result < 0)
            {
                Release(ref nativeResource);
                Marshal.ThrowExceptionForHR(result);
            }
            if (nativeResource == 0)
                throw new InvalidOperationException("ICanvasResourceWrapperNative::GetNativeResource returned null.");

            resource = nativeResource;
            return true;
        }
        finally
        {
            Release(ref resourceWrapper);
            Release(ref inspectable);
        }
    }

    private static bool TryQueryInterface(nint source, Guid iid, out nint result)
    {
        nint queried = 0;
        Guid requestedIid = iid;
        var vtable = *(nint**)source;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
        int hr = queryInterface(source, &requestedIid, &queried);
        if (hr == ENoInterface)
        {
            Release(ref queried);
            result = 0;
            return false;
        }
        if (hr < 0)
        {
            Release(ref queried);
            result = 0;
            Marshal.ThrowExceptionForHR(hr);
        }
        if (queried == 0)
            throw new InvalidOperationException("QueryInterface succeeded but returned null.");
        result = queried;
        return true;
    }

    private static void Release(ref nint pointer)
    {
        nint value = pointer;
        pointer = 0;
        if (value == 0)
            return;

        var vtable = *(nint**)value;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        _ = release(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeGlyphOffset(float advanceOffset, float ascenderOffset)
    {
        public float AdvanceOffset = advanceOffset;
        public float AscenderOffset = ascenderOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeGlyphRun
    {
        public nint FontFace;
        public float FontEmSize;
        public uint GlyphCount;
        public ushort* GlyphIndices;
        public float* GlyphAdvances;
        public NativeGlyphOffset* GlyphOffsets;
        public int IsSideways;
        public uint BidiLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeColor
    {
        public float Red;
        public float Green;
        public float Blue;
        public float Alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDWriteMatrix
    {
        public float M11;
        public float M12;
        public float M21;
        public float M22;
        public float Dx;
        public float Dy;

        internal NativeDWriteMatrix(float m11, float m12, float m21, float m22, float dx, float dy)
            => (M11, M12, M21, M22, Dx, Dy) = (m11, m12, m21, m22, dx, dy);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeColorGlyphRun
    {
        public NativeGlyphRun GlyphRun;
        public nint GlyphRunDescription;
        public float BaselineOriginX;
        public float BaselineOriginY;
        public NativeColor RunColor;
        public ushort PaletteIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeColorGlyphRun1
    {
        // The derived DWRITE_COLOR_GLYPH_RUN1 tail starts at sizeof its base
        // structure. On x64 the base's 6 bytes of tail padding are part of its
        // ABI size; flattening fields here would place GlyphImageFormat at 84
        // instead of 88 and misread DirectWrite's returned layers.
        public NativeColorGlyphRun Base;
        public int GlyphImageFormat;
        public int MeasuringMode;
    }

    [LibraryImport("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
    private static partial int DWriteCreateFactory(uint factoryType, Guid* factoryIid, nint* factory);
}
