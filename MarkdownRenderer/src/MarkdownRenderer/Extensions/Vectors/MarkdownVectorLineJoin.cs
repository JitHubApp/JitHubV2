namespace MarkdownRenderer.Extensions.Vectors;

/// <summary>Controls joins between stroked segments.</summary>
public enum MarkdownVectorLineJoin
{
    /// <summary>Uses a mitered join.</summary>
    Miter,
    /// <summary>Uses a round join.</summary>
    Round,
    /// <summary>Uses a beveled join.</summary>
    Bevel,
}
