using System.Numerics;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Captures each glyph run a native text layout draws, with copies of its
/// glyph and cluster arrays, at 96 DPI with pixel snapping disabled.
/// Strikethroughs, underlines and inline objects are ignored.
/// </summary>
internal sealed class RasterGlyphRunRecorder : ICanvasTextRenderer
{
    public List<CapturedGlyphRun> Runs { get; } = [];
    public float Dpi => 96f;
    public bool PixelSnappingDisabled => true;
    public Matrix3x2 Transform => Matrix3x2.Identity;

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
        Runs.Add(new CapturedGlyphRun(
            point,
            fontFace,
            fontSize,
            (CanvasGlyph[])glyphs.Clone(),
            isSideways,
            bidiLevel,
            brush,
            measuringMode,
            localeName,
            textString,
            clusterMapIndices is null ? [] : (int[])clusterMapIndices.Clone(),
            characterIndex));
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
    }

    public void DrawInlineObject(
        Vector2 point,
        ICanvasTextInlineObject inlineObject,
        bool isSideways,
        bool isRightToLeft,
        object brush,
        CanvasGlyphOrientation glyphOrientation)
    {
    }
}
