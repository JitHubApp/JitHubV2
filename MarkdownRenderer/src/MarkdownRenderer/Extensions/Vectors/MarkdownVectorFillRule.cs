namespace MarkdownRenderer.Extensions.Vectors;

/// <summary>Controls how a vector contour is filled.</summary>
public enum MarkdownVectorFillRule
{
    /// <summary>Uses nonzero winding.</summary>
    Nonzero,
    /// <summary>Alternates filled regions at each contour crossing.</summary>
    EvenOdd,
}
