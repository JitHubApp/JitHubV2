using System.Numerics;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>A DirectWrite glyph run captured by a test text renderer for replay and comparison.</summary>
internal sealed record CapturedGlyphRun(
    Vector2 Point,
    CanvasFontFace FontFace,
    float FontSize,
    CanvasGlyph[] Glyphs,
    bool IsSideways,
    uint BidiLevel,
    object? Brush,
    CanvasTextMeasuringMode MeasuringMode,
    string LocaleName,
    string Text,
    int[] ClusterMap,
    uint CharacterIndex);
