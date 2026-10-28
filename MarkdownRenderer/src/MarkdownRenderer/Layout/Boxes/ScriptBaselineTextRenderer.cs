using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Forwards DirectWrite's already-shaped glyph runs to Win2D while applying
/// the script offset at the run origin. Because the source layout remains the
/// shaper, wrapping, bidi ordering, clusters, and source character indexes do
/// not change.
/// </summary>
internal sealed partial class ScriptBaselineTextRenderer : ICanvasTextRenderer, IDisposable
{
    private readonly CanvasDrawingSession _drawingSession;
    private readonly ScriptBaselineGeometry _geometry;
    private readonly ScriptBaselineNativeColorGlyphRenderer _nativeColorGlyphRenderer;
    private readonly ICanvasBrush _defaultForeground;
    private readonly ICanvasBrush? _foregroundOverride;
    private readonly int _foregroundOverrideStart;
    private readonly int _foregroundOverrideEnd;

    internal ScriptBaselineTextRenderer(
        CanvasDrawingSession drawingSession,
        ScriptBaselineGeometry geometry,
        ICanvasBrush defaultForeground,
        ICanvasBrush? foregroundOverride = null,
        int foregroundOverrideStart = -1,
        int foregroundOverrideLength = 0,
        bool forceLegacyColorSupport = false)
    {
        _drawingSession = drawingSession ?? throw new ArgumentNullException(nameof(drawingSession));
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _defaultForeground = defaultForeground ?? throw new ArgumentNullException(nameof(defaultForeground));
        _nativeColorGlyphRenderer = new ScriptBaselineNativeColorGlyphRenderer(
            drawingSession,
            forceLegacyColorSupport);
        _foregroundOverride = foregroundOverride;
        _foregroundOverrideStart = foregroundOverrideStart;
        _foregroundOverrideEnd = foregroundOverrideLength > 0 && foregroundOverrideStart >= 0 &&
            foregroundOverrideStart <= int.MaxValue - foregroundOverrideLength
                ? foregroundOverrideStart + foregroundOverrideLength
                : foregroundOverrideStart;
    }

    public float Dpi => _drawingSession.Dpi;
    // DirectWrite would snap the callback origin before the per-script offset
    // is applied. Ask for its fractional origin, then snap the final baseline
    // ourselves in DrawOriented so the offset participates in snapping.
    public bool PixelSnappingDisabled => true;
    public Matrix3x2 Transform => _drawingSession.Transform;

    internal static Vector2 SnapBaselineToDevicePixel(
        Vector2 baselineOrigin,
        Matrix3x2 transform,
        float dpi)
    {
        if (!IsFinite(baselineOrigin) || !IsFinite(transform) || !float.IsFinite(dpi) || dpi <= 0f)
            return baselineOrigin;

        float pixelsPerDip = dpi / 96f;
        Vector2 transformedOrigin = Vector2.Transform(baselineOrigin, transform);
        Vector2 deviceOrigin = transformedOrigin * pixelsPerDip;
        if (!IsFinite(transformedOrigin) || !IsFinite(deviceOrigin) ||
            !Matrix3x2.Invert(transform, out Matrix3x2 inverseTransform) || !IsFinite(inverseTransform))
        {
            return baselineOrigin;
        }

        // DirectWrite pixel-snaps the line-normal coordinate, not the advance
        // axis. Rounding X would perturb fractional glyph advances and bidi
        // placement, so preserve transformed X and snap only transformed Y.
        Vector2 snappedTransformedOrigin = new(
            transformedOrigin.X,
            MathF.Round(deviceOrigin.Y, MidpointRounding.AwayFromZero) / pixelsPerDip);
        Vector2 snappedBaselineOrigin = Vector2.Transform(snappedTransformedOrigin, inverseTransform);
        return IsFinite(snappedBaselineOrigin) ? snappedBaselineOrigin : baselineOrigin;
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Matrix3x2 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32);

    public void DrawGlyphRun(
        Vector2 point,
        CanvasFontFace fontFace,
        float fontSize,
        CanvasGlyph[] glyphs,
        bool isSideways,
        uint bidiLevel,
        object brush,
        CanvasTextMeasuringMode measuringMode,
        string localeName,
        string textString,
        int[] clusterMapIndices,
        uint characterIndex,
        CanvasGlyphOrientation glyphOrientation)
    {
        int index = characterIndex <= int.MaxValue ? (int)characterIndex : -1;
        CanvasGlyph[] glyphsToDraw = glyphs;
        if (index >= 0 && TryCreateMixedBaselineGlyphRun(
                index,
                textString,
                clusterMapIndices,
                glyphs,
                out CanvasGlyph[]? adjustedGlyphs))
        {
            // A callback can coalesce adjacent styles when their font and
            // brush properties happen to match. Preserve DirectWrite's
            // shaping and cluster map, but apply the baseline delta to each
            // glyph rather than incorrectly moving the whole run together.
            glyphsToDraw = adjustedGlyphs!;
        }
        else
        {
            point.Y += _geometry.GetOffsetAtCharacter(index);
        }

        ICanvasBrush drawBrush = ResolveBrush(brush, textString, index);
        DrawOriented(glyphOrientation, isSideways, point, snappedPoint =>
        {
            if (!_nativeColorGlyphRenderer.TryDrawGlyphRun(
                    snappedPoint,
                    fontFace,
                    fontSize,
                    glyphsToDraw,
                    isSideways,
                    bidiLevel,
                    drawBrush,
                    measuringMode,
                    _drawingSession.Transform,
                    _drawingSession.Dpi))
            {
                _drawingSession.DrawGlyphRun(
                    snappedPoint,
                    fontFace,
                    fontSize,
                    glyphsToDraw,
                    isSideways,
                    bidiLevel,
                    drawBrush,
                    measuringMode,
                    localeName,
                    textString,
                    clusterMapIndices,
                    characterIndex);
            }
        });
    }

    private bool TryCreateMixedBaselineGlyphRun(
        int characterIndex,
        string? textString,
        int[]? clusterMapIndices,
        CanvasGlyph[] glyphs,
        out CanvasGlyph[]? adjustedGlyphs)
    {
        adjustedGlyphs = null;
        if (glyphs.Length < 2 || string.IsNullOrEmpty(textString) ||
            clusterMapIndices is not { Length: > 1 })
        {
            return false;
        }

        int characterCount = Math.Min(textString.Length, clusterMapIndices.Length);
        if (characterCount < 2 || characterIndex > int.MaxValue - characterCount)
            return false;

        int rangeEnd = characterIndex + characterCount;
        float firstOffset = _geometry.GetOffsetAtCharacter(characterIndex);
        int cursor = characterIndex;
        bool hasDifferentCharacterOffset = false;
        while (cursor < rangeEnd)
        {
            int segmentEnd = _geometry.GetSegmentEnd(cursor, rangeEnd, out float segmentOffset);
            if (segmentEnd <= cursor)
                return false;
            if (segmentOffset != firstOffset)
            {
                hasDifferentCharacterOffset = true;
                break;
            }

            cursor = segmentEnd;
        }

        if (!hasDifferentCharacterOffset)
            return false;

        // DirectWrite's cluster map is UTF-16-character to glyph-index. Use
        // the first character in each cluster so combining marks and emoji
        // sequences stay with their shaped base glyph. A glyph with no direct
        // cluster entry inherits the preceding visual cluster's baseline.
        int[] firstCharacterByGlyph = new int[glyphs.Length];
        Array.Fill(firstCharacterByGlyph, -1);
        for (int characterOffset = 0; characterOffset < characterCount; characterOffset++)
        {
            int glyphIndex = clusterMapIndices[characterOffset];
            if ((uint)glyphIndex >= (uint)glyphs.Length ||
                firstCharacterByGlyph[glyphIndex] >= 0)
            {
                continue;
            }

            firstCharacterByGlyph[glyphIndex] = characterOffset;
        }

        int precedingCharacterOffset = 0;
        bool hasDifferentGlyphOffset = false;
        float firstGlyphOffset = 0f;
        for (int glyphIndex = 0; glyphIndex < glyphs.Length; glyphIndex++)
        {
            if (firstCharacterByGlyph[glyphIndex] >= 0)
                precedingCharacterOffset = firstCharacterByGlyph[glyphIndex];
            else
                firstCharacterByGlyph[glyphIndex] = precedingCharacterOffset;

            float glyphOffset = _geometry.GetOffsetAtCharacter(
                characterIndex + firstCharacterByGlyph[glyphIndex]);
            if (glyphIndex == 0)
                firstGlyphOffset = glyphOffset;
            else if (glyphOffset != firstGlyphOffset)
                hasDifferentGlyphOffset = true;
        }

        if (!hasDifferentGlyphOffset)
            return false;

        var copy = (CanvasGlyph[])glyphs.Clone();
        for (int glyphIndex = 0; glyphIndex < copy.Length; glyphIndex++)
        {
            float glyphOffset = _geometry.GetOffsetAtCharacter(
                characterIndex + firstCharacterByGlyph[glyphIndex]);
            // DirectWrite's ascender offset is positive toward the top of the
            // baseline. Canvas coordinates grow downward, so invert the
            // control's Y delta while preserving any existing glyph offset.
            copy[glyphIndex].AscenderOffset -= glyphOffset;
        }

        adjustedGlyphs = copy;
        return true;
    }

    public void DrawStrikethrough(
        Vector2 point,
        float strikethroughWidth,
        float strikethroughThickness,
        float strikethroughOffset,
        CanvasTextDirection textDirection,
        object brush,
        CanvasTextMeasuringMode textMeasuringMode,
        string localeName,
        CanvasGlyphOrientation glyphOrientation)
    {
        // InlineContainerBox paints authored decorations from the same
        // translated character-region geometry used by selection and hit
        // testing. Drawing DirectWrite's callback here would duplicate those
        // decorations and depend on callback grouping across adjacent runs.
    }

    public void DrawUnderline(
        Vector2 point,
        float underlineWidth,
        float underlineThickness,
        float underlineOffset,
        float runHeight,
        CanvasTextDirection textDirection,
        object brush,
        CanvasTextMeasuringMode textMeasuringMode,
        string localeName,
        CanvasGlyphOrientation glyphOrientation)
    {
        // See DrawStrikethrough: the box's explicit decoration pass owns this
        // geometry and keeps it aligned with the same translated regions.
    }

    public void DrawInlineObject(
        Vector2 point,
        ICanvasTextInlineObject inlineObject,
        bool isSideways,
        bool isRightToLeft,
        object brush,
        CanvasGlyphOrientation glyphOrientation)
    {
        // Renderer-owned inline images and vector objects are painted through
        // their atomic box paths; their DirectWrite placeholder callbacks are
        // intentionally empty. Forward for any other inline object so the
        // wrapper remains a complete ICanvasTextRenderer.
        inlineObject.Draw(this, point, isSideways, isRightToLeft, brush);
    }

    private ICanvasBrush ResolveBrush(object? brush, string? textString, int characterIndex)
    {
        // Selection layout ranges already carry their own foreground brushes;
        // a link-state override is confined to that link's source range. The
        // native color-font API uses this brush for foreground layers while
        // retaining the font's own palette for authored color glyph artwork.
        bool inOverrideRange = characterIndex >= _foregroundOverrideStart &&
            characterIndex < _foregroundOverrideEnd;
        if (_foregroundOverride is not null && inOverrideRange)
            return _foregroundOverride;

        // CanvasTextLayout may provide null for glyph runs that inherit the
        // layout's default foreground. This is common when a script's color
        // equals the containing paragraph color, because no SetColor range was
        // applied. Use the explicit draw fallback instead of treating null as
        // a renderer failure.
        return brush as ICanvasBrush ?? _defaultForeground;
    }

    public void Dispose() => _nativeColorGlyphRenderer.Dispose();

    private void DrawOriented(
        CanvasGlyphOrientation orientation,
        bool isSideways,
        Vector2 point,
        Action<Vector2> draw)
    {
        if (orientation == CanvasGlyphOrientation.Upright && !isSideways)
        {
            draw(SnapBaselineToDevicePixel(point, _drawingSession.Transform, _drawingSession.Dpi));
            return;
        }

        Matrix3x2 originalTransform = _drawingSession.Transform;
        Matrix3x2 glyphTransform = CanvasTextLayout.GetGlyphOrientationTransform(
            orientation,
            isSideways,
            point);
        Exception? drawFailure = null;
        try
        {
            _drawingSession.Transform = Matrix3x2.Multiply(glyphTransform, originalTransform);
            draw(SnapBaselineToDevicePixel(point, _drawingSession.Transform, _drawingSession.Dpi));
        }
        catch (Exception exception)
        {
            drawFailure = exception;
        }

        Exception? restoreFailure = null;
        try
        {
            _drawingSession.Transform = originalTransform;
        }
        catch (Exception exception)
        {
            restoreFailure = exception;
        }

        if (drawFailure is not null)
        {
            if (restoreFailure is not null)
                throw new AggregateException("Glyph drawing and transform restoration both failed.", drawFailure, restoreFailure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(drawFailure).Throw();
        }

        if (restoreFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(restoreFailure).Throw();
    }
}
