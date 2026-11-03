namespace MarkdownRenderer.Math;

/// <summary>Identifies a native-painter-neutral drawing operation.</summary>
internal enum MathSceneCommandKind
{
    /// <summary>Fills a vector path.</summary>
    FillPath,
    /// <summary>Strokes a vector path.</summary>
    StrokePath,
    /// <summary>Fills an axis-aligned rectangle.</summary>
    FillRectangle,
    /// <summary>Strokes an axis-aligned rectangle.</summary>
    StrokeRectangle,
    /// <summary>Strokes a line, normally a fraction or radical rule.</summary>
    StrokeLine,
}
