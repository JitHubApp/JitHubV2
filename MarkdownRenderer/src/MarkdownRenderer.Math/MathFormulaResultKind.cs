namespace MarkdownRenderer.Math;

/// <summary>Identifies the outcome of bounded native formula processing.</summary>
internal enum MathFormulaResultKind
{
    /// <summary>The formula produced a usable vector scene.</summary>
    Accepted,
    /// <summary>The source is not valid supported TeX.</summary>
    InvalidSource,
    /// <summary>The source or generated scene exceeded a configured limit.</summary>
    Unsupported,
    /// <summary>An unexpected processing failure occurred.</summary>
    Failed,
}
