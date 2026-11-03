namespace MarkdownRenderer.Extensions;

/// <summary>Controls the shape at the ends of stroked segments.</summary>
public enum MarkdownVectorLineCap
{
    /// <summary>Ends at the segment endpoint.</summary>
    Flat,
    /// <summary>Uses a round cap.</summary>
    Round,
    /// <summary>Uses a square cap extending past the endpoint.</summary>
    Square,
}
