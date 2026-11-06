namespace MarkdownRenderer.Extensions;

/// <summary>Identifies one painter-neutral scene command.</summary>
public enum MarkdownVectorCommandKind
{
    /// <summary>Fills a vector path.</summary>
    FillPath,
    /// <summary>Strokes a vector path.</summary>
    StrokePath,
    /// <summary>Fills an axis-aligned rectangle.</summary>
    FillRectangle,
    /// <summary>Strokes an axis-aligned rectangle.</summary>
    StrokeRectangle,
    /// <summary>Strokes a line.</summary>
    StrokeLine,
    /// <summary>Draws a rectangle using a complete paint style.</summary>
    DrawRectangle,
    /// <summary>Draws a path using a complete paint style.</summary>
    DrawPath,
    /// <summary>Draws a line using a complete paint style.</summary>
    DrawLine,
    /// <summary>Draws an ellipse using a complete paint style.</summary>
    DrawEllipse,
    /// <summary>Draws one already-positioned line of text at its baseline.</summary>
    DrawText,
    /// <summary>Begins a transformed/opacity group.</summary>
    BeginGroup,
    /// <summary>Ends the current group.</summary>
    EndGroup,
    /// <summary>Begins a path clip.</summary>
    BeginClip,
    /// <summary>Ends the current path clip.</summary>
    EndClip,
}
